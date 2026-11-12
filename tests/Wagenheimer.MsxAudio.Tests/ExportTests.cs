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
        Assert.True(arpeggio.Count > 100, "the arpeggio notes are kept one by one");
        Assert.Equal(new[] { 60, 64, 67 }, arpeggio.Take(3));  // C4 E4 G4
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
