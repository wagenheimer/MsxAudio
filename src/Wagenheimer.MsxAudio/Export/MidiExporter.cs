using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Export;

/// <summary>One note found in a song.</summary>
/// <param name="Channel">Source channel: 0..2 = PSG A-C, 3..7 = SCC 1-5.</param>
/// <param name="Midi">MIDI note number (for a drum hit: the General MIDI drum note).</param>
/// <param name="Start">Start in seconds.</param>
/// <param name="End">End in seconds.</param>
/// <param name="Velocity">1..127.</param>
/// <param name="Drum">True for a hit of the noise generator.</param>
/// <param name="Bend">Pitch moves inside the note (vibrato, slides): time and distance from <paramref name="Midi"/> in semitones.</param>
/// <param name="Volume">The chip volume (0-15) while the note lasts: time and value.</param>
public readonly record struct TranscribedNote(
    int Channel,
    int Midi,
    double Start,
    double End,
    int Velocity,
    bool Drum,
    IReadOnlyList<(double Time, double Semitones)>? Bend = null,
    IReadOnlyList<(double Time, int Volume)>? Volume = null);

/// <summary>What the transcription and the MIDI file do beyond the plain notes.</summary>
public sealed class MidiOptions
{
    /// <summary>Snap starts and ends to a 16th-note grid when a steady tempo was found.</summary>
    public bool Quantize { get; init; } = true;

    /// <summary>
    /// An arpeggio (a chord played one fast note at a time) becomes the chord, held, instead of
    /// hundreds of 20 ms notes.
    /// </summary>
    public bool ArpeggiosAsChords { get; init; } = true;
}

/// <summary>
/// Turns the register writes of a VGM back into notes. The chips are driven by a sound driver that
/// writes a pitch and a volume, so the notes are in the data exactly: there is nothing to guess from
/// audio. The raw stream is then cleaned up the way a person would: flicker merged, glitches
/// dropped, vibrato kept as pitch bend, arpeggios turned back into chords.
/// </summary>
public static class VgmTranscriber
{
    /// <summary>Notes shorter than this are chip glitches, not music.</summary>
    private const double MinNoteSeconds = 0.04;

    /// <summary>The same note switched off and on again within this is one note.</summary>
    private const double MergeGapSeconds = 0.04;

    private sealed class Open
    {
        public int Midi;
        public double Start;
        public int MaxVolume;
        public int LastVolume;
        public double LastBend;
        public bool Drum;
        public int DrumNote;
        public readonly List<(double Time, double Semitones)> Bend = new();
        public readonly List<(double Time, int Volume)> Volume = new();
    }

    /// <summary>Transcribes the song with the default clean-up.</summary>
    public static List<TranscribedNote> Transcribe(VgmFile vgm) => Transcribe(vgm, new MidiOptions());

    /// <summary>Transcribes the song.</summary>
    public static List<TranscribedNote> Transcribe(VgmFile vgm, MidiOptions options)
    {
        ArgumentNullException.ThrowIfNull(vgm);
        ArgumentNullException.ThrowIfNull(options);

        List<TranscribedNote> raw = Raw(vgm);
        AutoMixPlan plan = AutoMixAnalyzer.Analyze(vgm);

        var result = new List<TranscribedNote>();

        foreach (IGrouping<int, TranscribedNote> channel in raw.GroupBy(n => n.Channel))
        {
            List<TranscribedNote> notes = channel.OrderBy(n => n.Start).ToList();
            bool isArpeggio = plan.Channels[channel.Key].Role == ChannelRole.Arpeggio;

            notes = Merge(notes);

            if (isArpeggio && options.ArpeggiosAsChords)
                notes = ToChords(notes);
            else if (!isArpeggio)
                notes = notes.Where(n => n.Drum || n.End - n.Start >= MinNoteSeconds).ToList();

            result.AddRange(notes);
        }

        result.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Midi.CompareTo(b.Midi));
        return result;
    }

    // ── Raw notes from the registers ─────────────────────────────────────────────

    private static List<TranscribedNote> Raw(VgmFile vgm)
    {
        var psg = new byte[16];
        psg[7] = 0x38;
        var sccRegs = new byte[10];
        var sccVolume = new int[5];
        int sccEnable = 0;
        double psgClock = vgm.AyClock > 0 ? vgm.AyClock : 1789772.5;

        var notes = new List<TranscribedNote>();
        var open = new Open?[8];
        double now = 0;

        IReadOnlyList<VgmEvent> events = vgm.Events;

        for (int i = 0; i < events.Count;)
        {
            now = events[i].Sample / 44100.0;

            // A pitch is written as two bytes (low, then high) at the same instant: look at the
            // registers only once all the writes of that instant are in, or the half-written value
            // would be heard as a note of its own.
            long sample = events[i].Sample;
            for (; i < events.Count && events[i].Sample == sample; i++)
            {
                VgmEvent e = events[i];

                if (e.Kind == VgmEventKind.Psg)
                {
                    if (e.P0 < psg.Length)
                        psg[e.P0] = e.P1;
                }
                else if (e.P0 == 1 && e.P1 < sccRegs.Length)
                {
                    sccRegs[e.P1] = e.P2;
                }
                else if (e.P0 == 2 && e.P1 < sccVolume.Length)
                {
                    sccVolume[e.P1] = e.P2 & 0x0F;
                }
                else if (e.P0 == 3)
                {
                    sccEnable = e.P2;
                }
            }

            for (int c = 0; c < 8; c++)
            {
                AutoMixAnalyzer.State state = AutoMixAnalyzer.StateOf(c, psg, sccRegs, sccVolume, sccEnable, psgClock);
                Follow(c, state, psg[6] & 0x1F, now, open, notes);
            }
        }

        for (int c = 0; c < 8; c++)
            Close(c, now, open, notes);

        return notes;
    }

    private static void Follow(int channel, AutoMixAnalyzer.State state, int noisePeriod, double now, Open?[] open,
        List<TranscribedNote> notes)
    {
        Open? current = open[channel];

        if (!state.Audible)
        {
            Close(channel, now, open, notes);
            return;
        }

        if (state.Tone && state.Frequency > 0)
        {
            double pitch = 69 + 12 * Math.Log2(state.Frequency / 440.0);
            int midi = Math.Clamp((int)Math.Round(pitch), 0, 127);

            // Still the same note while the pitch stays within a semitone and a half: that is vibrato
            // or a slide, and it is kept as pitch bend.
            if (current is { Drum: false })
            {
                double deviation = pitch - current.Midi;

                if (Math.Abs(deviation) < 1.5)
                {
                    if (Math.Abs(deviation - current.LastBend) >= 0.12)
                    {
                        current.Bend.Add((now, deviation));
                        current.LastBend = deviation;
                    }

                    if (state.Volume != current.LastVolume)
                    {
                        current.Volume.Add((now, state.Volume));
                        current.LastVolume = state.Volume;
                    }

                    current.MaxVolume = Math.Max(current.MaxVolume, state.Volume);
                    return;
                }
            }

            Close(channel, now, open, notes);

            var started = new Open { Midi = midi, Start = now, MaxVolume = state.Volume, LastVolume = state.Volume };
            double initial = pitch - midi;
            if (Math.Abs(initial) >= 0.12)
            {
                started.Bend.Add((now, initial));
                started.LastBend = initial;
            }

            started.Volume.Add((now, state.Volume));
            open[channel] = started;
            return;
        }

        if (state.Noise)
        {
            // Noise only: a drum. The noise period tells which one (high = hi-hat, low = kick).
            int drum = noisePeriod <= 7 ? 42 : noisePeriod <= 15 ? 38 : 36;

            if (current is { Drum: true } && current.DrumNote == drum)
            {
                current.MaxVolume = Math.Max(current.MaxVolume, state.Volume);
                return;
            }

            Close(channel, now, open, notes);
            open[channel] = new Open { Midi = drum, Start = now, MaxVolume = state.Volume, Drum = true, DrumNote = drum };
        }
    }

    private static void Close(int channel, double now, Open?[] open, List<TranscribedNote> notes)
    {
        Open? current = open[channel];
        if (current is null)
            return;

        open[channel] = null;

        double length = now - current.Start;
        if (length <= 0.004)
            return;

        // A drum hit is short whatever the register stays at.
        double end = current.Drum ? current.Start + Math.Min(length, 0.12) : now;
        int velocity = Math.Clamp(28 + current.MaxVolume * 6, 1, 127);

        notes.Add(new TranscribedNote(channel, current.Midi, current.Start, end, velocity, current.Drum,
            current.Bend.Count > 0 ? current.Bend : null,
            current.Drum ? null : current.Volume));
    }

    // ── Clean-up ─────────────────────────────────────────────────────────────────

    /// <summary>The same pitch released and struck again within a few milliseconds: one note.</summary>
    private static List<TranscribedNote> Merge(List<TranscribedNote> notes)
    {
        var merged = new List<TranscribedNote>();

        foreach (TranscribedNote note in notes)
        {
            if (merged.Count > 0 && !note.Drum)
            {
                TranscribedNote last = merged[^1];

                if (!last.Drum && last.Midi == note.Midi && note.Start - last.End <= MergeGapSeconds)
                {
                    merged[^1] = last with
                    {
                        End = note.End,
                        Velocity = Math.Max(last.Velocity, note.Velocity),
                        Bend = Join(last.Bend, note.Bend),
                        Volume = Join(last.Volume, note.Volume),
                    };
                    continue;
                }
            }

            merged.Add(note);
        }

        return merged;
    }

    private static IReadOnlyList<T>? Join<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Concat(b).ToList();
    }

    /// <summary>
    /// A chord played one fast note at a time (C, E, G, C, E, G ...) becomes the chord, held for as
    /// long as the pattern lasts. A new pitch is part of the chord only during its first cycle; once
    /// a pitch comes back, another new pitch means the chord changed.
    /// </summary>
    private static List<TranscribedNote> ToChords(List<TranscribedNote> notes)
    {
        var output = new List<TranscribedNote>();
        var segment = new List<TranscribedNote>();
        var pitches = new List<int>();
        bool locked = false;

        void Flush()
        {
            if (segment.Count == 0)
                return;

            bool isChord = pitches.Count >= 2 && segment.Count >= 2 * pitches.Count;

            if (isChord)
            {
                double start = segment[0].Start;
                double end = segment[^1].End;
                int velocity = (int)segment.Average(n => n.Velocity);
                int channel = segment[0].Channel;

                foreach (int pitch in pitches)
                    output.Add(new TranscribedNote(channel, pitch, start, end, velocity, false));
            }
            else
            {
                output.AddRange(segment.Where(n => n.End - n.Start >= 0.012));
            }

            segment.Clear();
            pitches.Clear();
            locked = false;
        }

        foreach (TranscribedNote note in notes)
        {
            if (segment.Count > 0 && note.Start - segment[^1].End > 0.08)
                Flush();

            if (pitches.Contains(note.Midi))
            {
                locked = true;
            }
            else if (!locked && pitches.Count < 5)
            {
                pitches.Add(note.Midi);
            }
            else
            {
                Flush();
                pitches.Add(note.Midi);
            }

            segment.Add(note);
        }

        Flush();
        return output;
    }
}

/// <summary>Writes a Standard MIDI File (type 1) from a transcribed VGM.</summary>
public static class MidiExporter
{
    private const int TicksPerQuarter = 480;
    private const int GridTicks = TicksPerQuarter / 4;                       // a 16th note

    private static readonly string[] ChannelNames = { "PSG 1", "PSG 2", "PSG 3", "SCC 1", "SCC 2", "SCC 3", "SCC 4", "SCC 5" };

    /// <summary>Builds the file with the default options.</summary>
    public static byte[] Export(VgmFile vgm, string title) => Export(vgm, title, new MidiOptions());

    /// <summary>Builds the file: a tempo track, one track per voice and one for the drums.</summary>
    public static byte[] Export(VgmFile vgm, string title, MidiOptions options)
    {
        ArgumentNullException.ThrowIfNull(vgm);
        ArgumentNullException.ThrowIfNull(options);

        List<TranscribedNote> notes = VgmTranscriber.Transcribe(vgm, options);
        AutoMixPlan plan = AutoMixAnalyzer.Analyze(vgm);

        // The tempo the music actually has, and the delay that puts its beats on the grid.
        Tempo tempo = EstimateTempo(notes);
        bool quantize = options.Quantize && tempo.Confident;
        double ticksPerSecond = TicksPerQuarter * tempo.Bpm / 60.0;

        var tracks = new List<byte[]> { TempoTrack(vgm, title, tempo, ticksPerSecond) };

        int midiChannel = 0;
        foreach (int source in notes.Where(n => !n.Drum).Select(n => n.Channel).Distinct().OrderBy(c => c))
        {
            int program = ProgramFor(plan.Channels[source].Role, source);
            tracks.Add(NoteTrack(ChannelNames[source], midiChannel, program,
                notes.Where(n => !n.Drum && n.Channel == source).ToList(), tempo, ticksPerSecond, quantize));

            midiChannel++;
            if (midiChannel == 9)
                midiChannel = 10;                                          // channel 10 (index 9) is the drum kit
        }

        List<TranscribedNote> drums = notes.Where(n => n.Drum).ToList();
        if (drums.Count > 0)
            tracks.Add(NoteTrack("Drums", 9, -1, drums, tempo, ticksPerSecond, quantize));

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("MThd"));
        WriteInt32(writer, 6);
        WriteInt16(writer, 1);
        WriteInt16(writer, (short)tracks.Count);
        WriteInt16(writer, TicksPerQuarter);

        foreach (byte[] track in tracks)
        {
            writer.Write(Encoding.ASCII.GetBytes("MTrk"));
            WriteInt32(writer, track.Length);
            writer.Write(track);
        }

        writer.Flush();
        return stream.ToArray();
    }

    // ── Tempo ────────────────────────────────────────────────────────────────────

    /// <summary>The tempo, the delay that puts the first beat on the grid, and how sure we are.</summary>
    internal readonly record struct Tempo(double Bpm, double DelaySeconds, double Score)
    {
        public bool Confident => Score >= 0.5;
    }

    /// <summary>
    /// Finds the tempo that makes the note starts of the melody, bass and drums fall on a 16th-note
    /// grid. Every candidate tempo is scored by how tightly the onsets cluster on its grid (the length
    /// of their mean phasor); the arpeggios are left out because their notes follow the frame rate,
    /// not the beat.
    /// </summary>
    internal static Tempo EstimateTempo(IReadOnlyList<TranscribedNote> notes)
    {
        var onsets = notes.Where(n => n.Midi > 0 && n.End - n.Start >= 0.05)
            .Select(n => n.Start)
            .OrderBy(t => t)
            .ToList();

        // One onset per instant (chords and doubled voices would count the same beat many times).
        var unique = new List<double>();
        foreach (double t in onsets)
        {
            if (unique.Count == 0 || t - unique[^1] > 0.015)
                unique.Add(t);
        }

        if (unique.Count < 8)
            return new Tempo(120, 0, 0);

        double bestScore = -1, bestBpm = 120, bestPhase = 0;

        for (double bpm = 70; bpm <= 190; bpm += 0.25)
        {
            double grid = 60.0 / bpm / 4.0;
            double re = 0, im = 0;

            foreach (double t in unique)
            {
                double angle = 2 * Math.PI * t / grid;
                re += Math.Cos(angle);
                im += Math.Sin(angle);
            }

            double score = Math.Sqrt(re * re + im * im) / unique.Count;

            // Half and double tempos fit just as well: prefer the ones people play at.
            score *= 1.0 - 0.0006 * Math.Abs(bpm - 120);

            if (score > bestScore)
            {
                bestScore = score;
                bestBpm = bpm;
                bestPhase = Math.Atan2(im, re) / (2 * Math.PI) * grid;           // where the grid sits, in seconds
            }
        }

        // Shift the whole song so that grid line zero falls on its first beat.
        double gridSeconds = 60.0 / bestBpm / 4.0;
        double delay = (gridSeconds - ((bestPhase % gridSeconds) + gridSeconds) % gridSeconds) % gridSeconds;

        return new Tempo(Math.Round(bestBpm, 2), delay, bestScore);
    }

    // ── Programs ─────────────────────────────────────────────────────────────────

    /// <summary>General MIDI program for a voice: square lead, synth bass, pad.</summary>
    private static int ProgramFor(ChannelRole role, int source) => role switch
    {
        ChannelRole.Bass => 38,                    // Synth Bass 1
        ChannelRole.Harmony => 90,                 // Pad 3 (polysynth)
        ChannelRole.Arpeggio => 81,                // Lead 2 (sawtooth): the chord, held
        _ => source < 3 ? 80 : 81,                 // Lead 1 (square) for the PSG, sawtooth for the SCC waves
    };

    // ── Tracks ───────────────────────────────────────────────────────────────────

    private static byte[] TempoTrack(VgmFile vgm, string title, Tempo tempo, double ticksPerSecond)
    {
        using var stream = new MemoryStream();

        WriteVlq(stream, 0);
        WriteMeta(stream, 0x03, Encoding.UTF8.GetBytes(title));

        int microseconds = (int)Math.Round(60_000_000.0 / tempo.Bpm);
        WriteVlq(stream, 0);
        stream.Write(new byte[]
        {
            0xFF, 0x51, 0x03,
            (byte)(microseconds >> 16), (byte)(microseconds >> 8), (byte)microseconds,
        });

        WriteVlq(stream, 0);
        stream.Write(new byte[] { 0xFF, 0x58, 0x04, 0x04, 0x02, 0x18, 0x08 }); // 4/4

        // The loop, as markers a sequencer can show.
        if (vgm.HasLoop)
        {
            int start = Ticks(vgm.LoopStartSeconds + tempo.DelaySeconds, ticksPerSecond);
            int end = Ticks(vgm.LoopStartSeconds + vgm.LoopSeconds + tempo.DelaySeconds, ticksPerSecond);

            WriteVlq(stream, start);
            WriteMeta(stream, 0x06, Encoding.ASCII.GetBytes("loopStart"));

            WriteVlq(stream, Math.Max(0, end - start));
            WriteMeta(stream, 0x06, Encoding.ASCII.GetBytes("loopEnd"));
        }

        WriteVlq(stream, 0);
        stream.Write(new byte[] { 0xFF, 0x2F, 0x00 });
        return stream.ToArray();
    }

    private static int Ticks(double seconds, double ticksPerSecond) => (int)Math.Round(seconds * ticksPerSecond);

    private static int Snap(int ticks) => (int)Math.Round(ticks / (double)GridTicks) * GridTicks;

    private static byte[] NoteTrack(string name, int channel, int program, List<TranscribedNote> notes, Tempo tempo,
        double ticksPerSecond, bool quantize)
    {
        // Tick positions, snapped to the grid when the tempo is steady.
        var timed = new List<(TranscribedNote Note, int On, int Off)>();

        foreach (TranscribedNote note in notes)
        {
            int on = Ticks(note.Start + tempo.DelaySeconds, ticksPerSecond);
            int off = Ticks(note.End + tempo.DelaySeconds, ticksPerSecond);

            if (quantize && note.Drum)
            {
                on = Snap(on);
                off = on + 30;
            }
            else if (quantize && off - on >= GridTicks / 2)
            {
                // Notes shorter than half a grid step are arpeggio notes: they follow the frame rate,
                // not the beat, so they keep their own timing.
                on = Snap(on);
                off = Math.Max(on + GridTicks / 2, Snap(off));
            }

            timed.Add((note, on, Math.Max(on + 1, off)));
        }

        // A voice plays one pitch at a time: after snapping, a note must not run into the next one of
        // the same pitch, or its note-off would cut the wrong one.
        timed.Sort((a, b) => a.On != b.On ? a.On.CompareTo(b.On) : a.Note.Midi.CompareTo(b.Note.Midi));
        for (int i = 0; i < timed.Count; i++)
        {
            for (int j = i + 1; j < timed.Count; j++)
            {
                if (timed[j].Note.Midi != timed[i].Note.Midi)
                    continue;

                if (timed[i].Off > timed[j].On)
                    timed[i] = (timed[i].Note, timed[i].On, Math.Max(timed[i].On + 1, timed[j].On));

                break;
            }
        }

        var events = new List<(int Tick, int Order, byte[] Data)>();
        bool drums = channel == 9;
        int bendNow = 8192;

        foreach ((TranscribedNote note, int on, int off) in timed)
        {
            if (drums)
            {
                events.Add((on, 2, new byte[] { (byte)(0x90 | channel), (byte)note.Midi, (byte)note.Velocity }));
                events.Add((off, 0, new byte[] { (byte)(0x80 | channel), (byte)note.Midi, 0 }));
                continue;
            }

            // The chip volume is the expression: set at the start, then followed while the note lasts.
            int firstVolume = note.Volume is { Count: > 0 } v ? v[0].Volume : Math.Clamp((note.Velocity - 28) / 6, 1, 15);
            events.Add((on, 1, Controller(channel, 11, Expression(firstVolume))));

            // Pitch bend back to centre for a note that starts straight, then the moves inside the note.
            int startBend = note.Bend is { Count: > 0 } b && b[0].Time <= note.Start + 0.001 ? BendValue(b[0].Semitones) : 8192;
            if (startBend != bendNow)
            {
                events.Add((on, 1, PitchBend(channel, startBend)));
                bendNow = startBend;
            }

            events.Add((on, 2, new byte[] { (byte)(0x90 | channel), (byte)note.Midi, 100 }));

            if (note.Bend is { Count: > 0 } bends)
            {
                int lastTick = on;
                foreach ((double time, double semitones) in bends)
                {
                    int tick = Math.Clamp(on + Ticks(time - note.Start, ticksPerSecond), on, off - 1);
                    int value = BendValue(semitones);

                    if (value == bendNow || tick - lastTick < 8)
                        continue;

                    events.Add((tick, 1, PitchBend(channel, value)));
                    bendNow = value;
                    lastTick = tick;
                }
            }

            if (note.Volume is { Count: > 1 } volumes)
            {
                int lastTick = on;
                int lastValue = Expression(firstVolume);

                foreach ((double time, int volume) in volumes.Skip(1))
                {
                    int tick = Math.Clamp(on + Ticks(time - note.Start, ticksPerSecond), on, off - 1);
                    int value = Expression(volume);

                    if (value == lastValue || tick - lastTick < 8)
                        continue;

                    events.Add((tick, 1, Controller(channel, 11, value)));
                    lastValue = value;
                    lastTick = tick;
                }
            }

            events.Add((off, 0, new byte[] { (byte)(0x80 | channel), (byte)note.Midi, 0 }));
        }

        events.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));

        using var stream = new MemoryStream();

        WriteVlq(stream, 0);
        WriteMeta(stream, 0x03, Encoding.UTF8.GetBytes(name));

        if (!drums)
        {
            // Volume 100, and a pitch-bend range of two semitones (RPN 0), so every player bends alike.
            foreach (byte[] setup in new[]
                     {
                         Controller(channel, 7, 100),
                         Controller(channel, 101, 0),
                         Controller(channel, 100, 0),
                         Controller(channel, 6, 2),
                         Controller(channel, 38, 0),
                         Controller(channel, 101, 127),
                         Controller(channel, 100, 127),
                     })
            {
                WriteVlq(stream, 0);
                stream.Write(setup);
            }
        }

        if (program >= 0)
        {
            WriteVlq(stream, 0);
            stream.Write(new byte[] { (byte)(0xC0 | channel), (byte)program });
        }

        int last = 0;
        foreach ((int tick, _, byte[] data) in events)
        {
            WriteVlq(stream, tick - last);
            stream.Write(data);
            last = tick;
        }

        WriteVlq(stream, 0);
        stream.Write(new byte[] { 0xFF, 0x2F, 0x00 });
        return stream.ToArray();
    }

    /// <summary>The chip's 0-15 volume as MIDI expression (the chip steps are logarithmic).</summary>
    private static int Expression(int volume) =>
        volume <= 0 ? 0 : Math.Clamp((int)Math.Round(127 * Math.Pow(volume / 15.0, 0.75)), 1, 127);

    private static int BendValue(double semitones) =>
        Math.Clamp((int)Math.Round(8192 + semitones / 2.0 * 8191), 0, 16383);

    private static byte[] Controller(int channel, int controller, int value) =>
        new[] { (byte)(0xB0 | channel), (byte)controller, (byte)value };

    private static byte[] PitchBend(int channel, int value) =>
        new[] { (byte)(0xE0 | channel), (byte)(value & 0x7F), (byte)((value >> 7) & 0x7F) };

    private static void WriteMeta(Stream stream, byte type, byte[] data)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte(type);
        WriteVlq(stream, data.Length);
        stream.Write(data);
    }

    private static void WriteVlq(Stream stream, int value)
    {
        value = Math.Max(0, value);
        Span<byte> buffer = stackalloc byte[5];
        int count = 0;

        buffer[count++] = (byte)(value & 0x7F);
        while ((value >>= 7) > 0)
            buffer[count++] = (byte)((value & 0x7F) | 0x80);

        for (int i = count - 1; i >= 0; i--)
            stream.WriteByte(buffer[i]);
    }

    private static void WriteInt32(BinaryWriter writer, int value)
    {
        writer.Write((byte)(value >> 24));
        writer.Write((byte)(value >> 16));
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }

    private static void WriteInt16(BinaryWriter writer, short value)
    {
        writer.Write((byte)(value >> 8));
        writer.Write((byte)value);
    }
}
