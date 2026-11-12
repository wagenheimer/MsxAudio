using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wagenheimer.MsxAudio.Export;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

public class ExportTests
{
    /// <summary>Just enough of a Standard MIDI File reader to check what was written.</summary>
    private sealed record Midi(int Format, int Division, List<List<(int Tick, byte Status, byte A, byte B)>> Tracks);

    private static int ReadVlq(byte[] data, ref int at)
    {
        int value = 0;
        byte b;
        do
        {
            b = data[at++];
            value = (value << 7) | (b & 0x7F);
        }
        while ((b & 0x80) != 0);

        return value;
    }

    private static Midi ParseMidi(byte[] data)
    {
        Assert.Equal("MThd", Encoding.ASCII.GetString(data, 0, 4));
        int format = (data[8] << 8) | data[9];
        int count = (data[10] << 8) | data[11];
        int division = (data[12] << 8) | data[13];

        var tracks = new List<List<(int, byte, byte, byte)>>();
        int at = 14;

        for (int t = 0; t < count; t++)
        {
            Assert.Equal("MTrk", Encoding.ASCII.GetString(data, at, 4));
            int length = (data[at + 4] << 24) | (data[at + 5] << 16) | (data[at + 6] << 8) | data[at + 7];
            at += 8;
            int end = at + length;
            int tick = 0;
            var events = new List<(int, byte, byte, byte)>();

            while (at < end)
            {
                tick += ReadVlq(data, ref at);
                byte status = data[at++];

                if (status == 0xFF)
                {
                    byte type = data[at++];
                    int metaLength = ReadVlq(data, ref at);
                    at += metaLength;
                    events.Add((tick, status, type, 0));
                }
                else if ((status & 0xF0) is 0xC0 or 0xD0)
                {
                    events.Add((tick, status, data[at++], 0));
                }
                else
                {
                    events.Add((tick, status, data[at++], data[at++]));
                }
            }

            tracks.Add(events);
        }

        return new Midi(format, division, tracks);
    }

    private static List<int> NotesOn(List<(int Tick, byte Status, byte A, byte B)> track) =>
        track.Where(e => (e.Status & 0xF0) == 0x90 && e.B > 0).Select(e => (int)e.A).ToList();

    [Fact]
    public void Midi_TranscribesTheBassTheTuneAndTheArpeggio()
    {
        byte[] bytes = MidiExporter.Export(AutoMixTests.BuildSong(), "test song");
        Midi midi = ParseMidi(bytes);

        Assert.Equal(1, midi.Format);
        Assert.Equal(480, midi.Division);
        Assert.Equal(4, midi.Tracks.Count);                    // tempo + PSG 1 (bass) + PSG 2 (tune) + PSG 3 (arpeggio)

        List<int> bass = NotesOn(midi.Tracks[1]);
        List<int> tune = NotesOn(midi.Tracks[2]);
        List<int> arpeggio = NotesOn(midi.Tracks[3]);

        Assert.Contains(36, bass);                             // 65.41 Hz = C2
        Assert.Contains(72, tune);                             // 523.25 Hz = C5
        Assert.Contains(67, tune);                             // 783.99 Hz = G5 is 79; 392 Hz = G4 is 67

        // The arpeggio (C4 E4 G4 cycling every 1/48 s) is the chord, held: three notes, not 288.
        Assert.Equal(new[] { 60, 64, 67 }, arpeggio.OrderBy(n => n));
    }

    [Fact]
    public void Midi_CanKeepTheArpeggioNoteByNote()
    {
        byte[] bytes = MidiExporter.Export(AutoMixTests.BuildSong(), "raw", new MidiOptions { ArpeggiosAsChords = false });
        List<int> arpeggio = NotesOn(ParseMidi(bytes).Tracks[3]);

        Assert.True(arpeggio.Count > 100);
        Assert.Equal(new[] { 60, 64, 67 }, arpeggio.Take(3));
    }

    [Fact]
    public void Transcriber_KeepsVibratoAsOneNoteWithPitchBend()
    {
        var script = new AutoMixTests.Script();
        script.Psg(7, 0x38);

        // A held A4 wobbling by a semitone every 1/50 s for 2 s: one note, not a hundred.
        for (int i = 0; i < 100; i++)
        {
            script.Note(0, i % 2 == 0 ? 440.0 : 466.16, 12);
            script.Wait(0.02);
        }

        List<TranscribedNote> notes = VgmTranscriber.Transcribe(script.Build());

        Assert.Single(notes);
        Assert.Equal(69, notes[0].Midi);
        Assert.True(notes[0].Bend is { Count: > 20 }, "the wobble is kept as pitch bend");
    }

    [Fact]
    public void Transcriber_MergesAFlickerAndDropsAGlitch()
    {
        var script = new AutoMixTests.Script();
        script.Psg(7, 0x38);

        script.Note(0, 440.0, 12);
        script.Wait(0.3);
        script.Psg(8, 0);                                       // off for 20 ms ...
        script.Wait(0.02);
        script.Note(0, 440.0, 12);                              // ... and on again: the same note
        script.Wait(0.3);
        script.Psg(8, 0);
        script.Wait(0.2);
        script.Note(0, 880.0, 12);                              // a 15 ms blip: a glitch
        script.Wait(0.015);
        script.Psg(8, 0);
        script.Wait(0.5);

        List<TranscribedNote> notes = VgmTranscriber.Transcribe(script.Build());

        Assert.Single(notes);
        Assert.InRange(notes[0].End - notes[0].Start, 0.6, 0.7);
    }

    [Fact]
    public void Tempo_IsFoundAndTheNotesLandOnTheGrid()
    {
        var script = new AutoMixTests.Script();
        script.Psg(7, 0x38);

        // Eighth notes at 132 BPM (0.2273 s each), a little off the beat like a human or a driver.
        double eighth = 60.0 / 132 / 2;
        var random = new Random(7);
        double[] melody = { 523.25, 587.33, 659.25, 783.99, 659.25, 587.33, 523.25, 392.0 };

        double clock = 0;
        for (int i = 0; i < 64; i++)
        {
            double wobble = (random.NextDouble() - 0.5) * 0.012;
            script.Note(0, melody[i % melody.Length], 12);
            script.Wait(eighth * 0.8 + wobble);
            script.Psg(8, 0);
            script.Wait(eighth * 0.2 - wobble);
            clock += eighth;
        }

        VgmFile song = script.Build();
        List<TranscribedNote> notes = VgmTranscriber.Transcribe(song);
        MidiExporter.Tempo tempo = MidiExporter.EstimateTempo(notes);

        Assert.True(tempo.Confident);
        Assert.InRange(tempo.Bpm, 131.0, 133.0);

        // In the file, every note starts on a 16th-note grid line (120 ticks).
        Midi midi = ParseMidi(MidiExporter.Export(song, "steady"));
        var starts = midi.Tracks[1].Where(e => (e.Status & 0xF0) == 0x90 && e.B > 0).Select(e => e.Tick).ToList();

        Assert.NotEmpty(starts);
        Assert.All(starts, tick => Assert.Equal(0, tick % 120));
    }

    [Fact]
    public void Midi_FollowsTheChipVolumeWithExpression()
    {
        var script = new AutoMixTests.Script();
        script.Psg(7, 0x38);

        script.Note(0, 440.0, 15);
        for (int volume = 14; volume >= 3; volume--)             // a decaying note
        {
            script.Wait(0.06);
            script.Psg(8, (byte)volume);
        }

        script.Wait(0.1);
        script.Psg(8, 0);
        script.Wait(0.3);

        Midi midi = ParseMidi(MidiExporter.Export(script.Build(), "decay"));
        var expression = midi.Tracks[1].Where(e => (e.Status & 0xF0) == 0xB0 && e.A == 11).Select(e => (int)e.B).ToList();

        Assert.True(expression.Count >= 5, "the decay is written as expression (CC11)");
        Assert.True(expression[0] > expression[^1], "and it goes down");
    }

    [Fact]
    public void Midi_PutsNoiseOnTheDrumChannel()
    {
        var script = new AutoMixTests.Script();
        script.Psg(7, 0x37);                                   // channel A: noise only
        script.Psg(6, 4);

        for (int i = 0; i < 10; i++)
        {
            script.Psg(8, 13);
            script.Wait(0.1);
            script.Psg(8, 0);
            script.Wait(0.1);
        }

        Midi midi = ParseMidi(MidiExporter.Export(script.Build(), "drums"));

        var drums = midi.Tracks.Last();
        Assert.All(drums.Where(e => (e.Status & 0xF0) is 0x90 or 0x80), e => Assert.Equal(9, e.Status & 0x0F));
        Assert.Equal(10, NotesOn(drums).Count);
        Assert.All(NotesOn(drums), n => Assert.Equal(42, n));  // a short noise period is a hi-hat
    }

    [Fact]
    public void Midi_MarksTheLoop()
    {
        VgmFile song = AutoMixTests.BuildSong();
        song.SetLoop(1.0, 2.0);

        byte[] bytes = MidiExporter.Export(song, "looped");

        Assert.Contains("loopStart", Encoding.ASCII.GetString(bytes));
        Assert.Contains("loopEnd", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Wav_HasAValidHeaderAndTheRightSize()
    {
        RenderedAudio audio = MsxSoundRenderer.Render(AutoMixTests.BuildSong(), AudioProfile.Stereo, 44100, 0, 1);
        byte[] wav = AudioExporter.ToWav(audio);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(2, BitConverter.ToInt16(wav, 22));                        // stereo
        Assert.Equal(44100, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal(audio.Samples.Length * 2, BitConverter.ToInt32(wav, 40));
        Assert.Equal(44 + audio.Samples.Length * 2, wav.Length);
    }

    [Fact]
    public void Render_RepeatsTheLoopAndFadesOut()
    {
        VgmFile song = AutoMixTests.BuildSong();                               // 6 s
        song.SetLoop(2.0, 3.0);

        RenderedAudio once = AudioExporter.Render(song, AudioProfile.Stereo, 44100, loops: 1, fadeSeconds: 0);
        RenderedAudio twice = AudioExporter.Render(song, AudioProfile.Stereo, 44100, loops: 3, fadeSeconds: 1);

        Assert.Equal(5.0, once.Seconds, 1);                                    // intro 2 s + one loop of 3 s
        Assert.Equal(11.0, twice.Seconds, 1);                                  // intro 2 s + three loops of 3 s

        // The last tenth of a second is far quieter than the middle of the song.
        int channels = twice.Channels;
        double Rms(int from, int to)
        {
            double sum = 0;
            for (int i = from * channels; i < to * channels; i++)
                sum += Math.Pow(twice.Samples[i] / 32768.0, 2);

            return Math.Sqrt(sum / Math.Max(1, (to - from) * channels));
        }

        int frames = twice.Frames;
        Assert.True(Rms(frames - 4410, frames) < Rms(frames / 2, frames / 2 + 4410) * 0.2);
    }

    [Fact]
    public void Render_ASongWithoutALoopStopsAfterItsLastNote()
    {
        RenderedAudio audio = AudioExporter.Render(AutoMixTests.BuildSong(), AudioProfile.Stereo, 44100);

        Assert.InRange(audio.Seconds, 5.5, 7.5);
    }
}
