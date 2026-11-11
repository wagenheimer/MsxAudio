using System;
using System.IO;
using System.Text;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Playback;
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Vgm;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>Round-trip and API tests for the package surface.</summary>
public class PackageApiTests
{
    [Fact]
    public void VgmWriter_RoundTripsEventsAndClock()
    {
        var events = new[]
        {
            new VgmEvent(0, VgmEventKind.Psg, 7, 0x3E, 0),
            new VgmEvent(735, VgmEventKind.Scc, 1, 2, 100),
            new VgmEvent(1470, VgmEventKind.Scc, 2, 1, 15),
        };
        var original = VgmFile.FromEvents(events, 4410, 1789772.5, "roundtrip");

        byte[] bytes = VgmWriter.Write(original);
        var parsed = VgmFile.Parse(bytes, "roundtrip");

        Assert.Equal(3, parsed.Events.Count);
        Assert.Equal(1789772, parsed.AyClock, 0);
        Assert.Equal(4410, parsed.TotalSamples);
        Assert.Equal(VgmEventKind.Scc, parsed.Events[1].Kind);
        Assert.Equal(735, parsed.Events[1].Sample);
        Assert.Equal(100, parsed.Events[1].P2);
    }

    [Fact]
    public void AudioProfile_ByNameAndOversample()
    {
        Assert.Equal("stereo", AudioProfile.ByName("STEREO").Name);
        Assert.Equal("remaster", AudioProfile.ByName("remaster").Name);
        Assert.Equal("psg", AudioProfile.ByName("psg").Name);
        Assert.Equal("original", AudioProfile.ByName("nonsense").Name);

        // "PSG only" mutes the SCC and keeps the PSG untouched.
        Assert.Equal(0f, AudioProfile.PsgOnly.SccGain);
        Assert.Equal(1f, AudioProfile.PsgOnly.PsgGain);

        var quiet = AudioProfile.Remaster.WithOversample(2);
        Assert.Equal(2, quiet.Oversample);
        Assert.Equal(AudioProfile.Remaster.PsgPan, quiet.PsgPan);
        Assert.Equal(AudioProfile.Remaster.MasterGain, quiet.MasterGain);
    }

    [Fact]
    public void FindAudibleEnd_TrimsTrailingSilence()
    {
        // tone on for 0.5 s, then everything silent for another 3 s
        var events = new[]
        {
            new VgmEvent(0, VgmEventKind.Psg, 7, 0x3E, 0),
            new VgmEvent(0, VgmEventKind.Psg, 8, 15, 0),
            new VgmEvent(22050, VgmEventKind.Psg, 7, 0x3F, 0),
            new VgmEvent(22050, VgmEventKind.Psg, 8, 0, 0),
            new VgmEvent(150000, VgmEventKind.Psg, 8, 0, 0),
        };
        var vgm = VgmFile.FromEvents(events, 154350);

        double end = MsxSoundRenderer.FindAudibleEnd(vgm, 0.5);
        Assert.InRange(end, 0.4, 1.2);
    }

    [Fact]
    public void WavDecoder_ReadsMonoAndStereo16Bit()
    {
        var mono = BuildWav(new short[] { 0, 16384, -16384 }, channels: 1);
        var decodedMono = WavDecoder.Decode(mono);
        Assert.Equal(1, decodedMono.Channels);
        Assert.Equal(3, decodedMono.Samples.Length);
        Assert.Equal(0.5f, decodedMono.Samples[1], 3);

        var stereo = BuildWav(new short[] { 1000, -1000, 2000, -2000 }, channels: 2);
        var decodedStereo = WavDecoder.Decode(stereo);
        Assert.Equal(2, decodedStereo.Channels);
        var monoMix = decodedStereo.ToMono();
        Assert.Equal(2, monoMix.Length);
        Assert.Equal(0f, monoMix[0], 3);
        Assert.Equal(0f, monoMix[1], 3);
    }

    [Fact]
    public void Library_FindAndEntries()
    {
        using var library = new MsxAudioLibrary("test");
        var vgm = VgmFile.FromEvents(new[] { new VgmEvent(0, VgmEventKind.Psg, 8, 15, 0) }, 735);
        library.AddSong("Song_A", "Song A", vgm);
        library.AddClip("Jump", "Jump", vgm);
        library.AddEntry("readme.txt", Encoding.ASCII.GetBytes("hello"));

        Assert.NotNull(library.Find("song_a"));
        Assert.NotNull(library.Find("JUMP"));
        Assert.Null(library.Find("missing"));
        Assert.Equal("hello", Encoding.ASCII.GetString(library.ReadEntry("readme.txt")!));
    }

    private static byte[] BuildWav(short[] samples, int channels)
    {
        const int sampleRate = 44100;
        int dataLength = samples.Length * 2;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        foreach (short sample in samples) writer.Write(sample);
        return stream.ToArray();
    }
}
