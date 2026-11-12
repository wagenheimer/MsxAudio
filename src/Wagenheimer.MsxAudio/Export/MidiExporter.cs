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
public readonly record struct TranscribedNote(int Channel, int Midi, double Start, double End, int Velocity, bool Drum);

/// <summary>
/// Turns the register writes of a VGM back into notes. The chips are driven by a sound driver that
/// writes a pitch and a volume, so the notes are in the data exactly: there is nothing to guess from
/// audio.
/// </summary>
public static class VgmTranscriber
{
    private const double SccClock = 1789772.5;

    /// <summary>Notes shorter than this are chip glitches, not music.</summary>
    private const double MinNoteSeconds = 0.012;

    private sealed class Open
    {
        public int Midi;
        public double Start;
        public int MaxVolume;
        public bool Drum;
        public int DrumNote;
    }

    /// <summary>Transcribes the song.</summary>
    public static List<TranscribedNote> Transcribe(VgmFile vgm)
    {
        ArgumentNullException.ThrowIfNull(vgm);

        var psg = new byte[16];
        psg[7] = 0x38;
        var sccRegs = new byte[10];
        var sccVolume = new int[5];
        int sccEnable = 0;
        double psgClock = vgm.AyClock > 0 ? vgm.AyClock : 1789772.5;

        var notes = new List<TranscribedNote>();
        var open = new Open?[8];
        double now = 0;

        foreach (VgmEvent e in vgm.Events)
        {
            now = e.Sample / 44100.0;

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

            for (int c = 0; c < 8; c++)
            {
                AutoMixAnalyzer.State state = AutoMixAnalyzer.StateOf(c, psg, sccRegs, sccVolume, sccEnable, psgClock);
                Follow(c, state, psg[6] & 0x1F, now, open, notes);
            }
        }

        for (int c = 0; c < 8; c++)
            Close(c, now, open, notes);

        notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Midi.CompareTo(b.Midi));
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
            int midi = (int)Math.Round(69 + 12 * Math.Log2(state.Frequency / 440.0));
            midi = Math.Clamp(midi, 0, 127);

            // A vibrato stays inside a semitone; a new note is a jump of two or more.
            if (current is { Drum: false } && Math.Abs(midi - current.Midi) < 2)
            {
                current.MaxVolume = Math.Max(current.MaxVolume, state.Volume);
                return;
            }

            Close(channel, now, open, notes);
            open[channel] = new Open { Midi = midi, Start = now, MaxVolume = state.Volume };
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
        if (length < MinNoteSeconds)
            return;

        // A drum hit is short whatever the register stays at.
        double end = current.Drum ? current.Start + Math.Min(length, 0.12) : now;
        int velocity = Math.Clamp(28 + current.MaxVolume * 6, 1, 127);

        notes.Add(new TranscribedNote(channel, current.Midi, current.Start, end, velocity, current.Drum));
    }
}

/// <summary>Writes a Standard MIDI File (type 1) from a transcribed VGM.</summary>
public static class MidiExporter
{
    private const int TicksPerQuarter = 480;
    private const int MicrosecondsPerQuarter = 500_000;                // 120 BPM: 960 ticks per second
    private const double TicksPerSecond = TicksPerQuarter * 1_000_000.0 / MicrosecondsPerQuarter;

    private static readonly string[] ChannelNames = { "PSG 1", "PSG 2", "PSG 3", "SCC 1", "SCC 2", "SCC 3", "SCC 4", "SCC 5" };

    /// <summary>Builds the file: a tempo track, one track per voice and one for the drums.</summary>
    public static byte[] Export(VgmFile vgm, string title)
    {
        ArgumentNullException.ThrowIfNull(vgm);

        List<TranscribedNote> notes = VgmTranscriber.Transcribe(vgm);
        AutoMixPlan plan = AutoMixAnalyzer.Analyze(vgm);

        var tracks = new List<byte[]> { TempoTrack(vgm, title) };

        int midiChannel = 0;
        foreach (int source in notes.Where(n => !n.Drum).Select(n => n.Channel).Distinct().OrderBy(c => c))
        {
            int program = ProgramFor(plan.Channels[source].Role, source);
            tracks.Add(NoteTrack(ChannelNames[source], midiChannel, program,
                notes.Where(n => !n.Drum && n.Channel == source)));

            midiChannel++;
            if (midiChannel == 9)
                midiChannel = 10;                                          // channel 10 (index 9) is the drum kit
        }

        List<TranscribedNote> drums = notes.Where(n => n.Drum).ToList();
        if (drums.Count > 0)
            tracks.Add(NoteTrack("Drums", 9, -1, drums));

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

    /// <summary>General MIDI program for a voice: square lead, synth bass, pad.</summary>
    private static int ProgramFor(ChannelRole role, int source) => role switch
    {
        ChannelRole.Bass => 38,                    // Synth Bass 1
        ChannelRole.Harmony => 90,                 // Pad 3 (polysynth)
        ChannelRole.Arpeggio => 81,                // Lead 2 (sawtooth)
        _ => source < 3 ? 80 : 81,                 // Lead 1 (square) for the PSG, sawtooth for the SCC waves
    };

    private static int Ticks(double seconds) => (int)Math.Round(seconds * TicksPerSecond);

    private static byte[] TempoTrack(VgmFile vgm, string title)
    {
        using var stream = new MemoryStream();

        WriteVlq(stream, 0);
        WriteMeta(stream, 0x03, Encoding.UTF8.GetBytes(title));

        WriteVlq(stream, 0);
        stream.Write(new byte[] { 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20 });      // 500000 us per quarter

        WriteVlq(stream, 0);
        stream.Write(new byte[] { 0xFF, 0x58, 0x04, 0x04, 0x02, 0x18, 0x08 }); // 4/4

        // The loop, as markers a sequencer can show.
        if (vgm.HasLoop)
        {
            int start = Ticks(vgm.LoopStartSeconds);
            int end = Ticks(vgm.LoopStartSeconds + vgm.LoopSeconds);

            WriteVlq(stream, start);
            WriteMeta(stream, 0x06, Encoding.ASCII.GetBytes("loopStart"));

            WriteVlq(stream, Math.Max(0, end - start));
            WriteMeta(stream, 0x06, Encoding.ASCII.GetBytes("loopEnd"));
        }

        WriteVlq(stream, 0);
        stream.Write(new byte[] { 0xFF, 0x2F, 0x00 });
        return stream.ToArray();
    }

    private static byte[] NoteTrack(string name, int channel, int program, IEnumerable<TranscribedNote> notes)
    {
        var events = new List<(int Tick, int Order, byte[] Data)>();

        foreach (TranscribedNote note in notes)
        {
            int on = Ticks(note.Start);
            int off = Math.Max(on + 1, Ticks(note.End));

            events.Add((on, 1, new byte[] { (byte)(0x90 | channel), (byte)note.Midi, (byte)note.Velocity }));
            events.Add((off, 0, new byte[] { (byte)(0x80 | channel), (byte)note.Midi, 0 }));
        }

        events.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));

        using var stream = new MemoryStream();

        WriteVlq(stream, 0);
        WriteMeta(stream, 0x03, Encoding.UTF8.GetBytes(name));

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
