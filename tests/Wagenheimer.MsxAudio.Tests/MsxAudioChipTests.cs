using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wagenheimer.MsxAudio.Chips;
using Wagenheimer.MsxAudio.Vgm;
using Wagenheimer.MsxAudio.Mixing;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>
/// Tests for the MSX audio emulator (PSG AY-3-8910 + SCC) and the VGM reader/renderer.
/// </summary>
public class MsxAudioChipTests
{
    private const double MsxClock = 1789772.5;

/// <summary>Counts zero crossings to estimate the frequency of a square wave.</summary>
    private static double EstimateFrequency(float[] samples, double sampleRate)
    {
        // chip channels are unipolar (0..8160): remove the mean level before counting
        double mean = 0;
        foreach (float sample in samples) mean += sample;
        mean /= samples.Length;

        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if ((samples[i - 1] < mean) != (samples[i] < mean)) crossings++;
        }

        double seconds = samples.Length / sampleRate;
        return crossings / 2.0 / seconds;
    }

    [Fact]
    public void Ay8910_ToneFrequencyFollowsHardwareFormula()
    {
        const double rate = 192000;
        var psg = new Ay8910(MsxClock, rate);

        const int period = 200; // f = clock / (16 * period) = 559,3 Hz
        psg.Write(7, 0x3E);     // channel 1 tone only, no noise
        psg.Write(0, period & 0xFF);
        psg.Write(1, period >> 8);
        psg.Write(8, 15);       // maximum volume

        int count = (int)rate;
        var ch0 = new float[count];
        var ch1 = new float[count];
        var ch2 = new float[count];
        psg.Render(ch0, ch1, ch2, count);

        double expected = MsxClock / (16.0 * period);
        double measured = EstimateFrequency(ch0, rate);
        Assert.InRange(measured, expected * 0.99, expected * 1.01);
        Assert.All(ch1, v => Assert.Equal(0f, v));
        Assert.All(ch2, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Ay8910_MutedChannelIsSilent()
    {
        const double rate = 192000;
        var psg = new Ay8910(MsxClock, rate);
        psg.Write(7, 0x3F); // tone and noise off on every channel
        psg.Write(0, 200);
        psg.Write(8, 15);

        var ch0 = new float[4096];
        var ch1 = new float[4096];
        var ch2 = new float[4096];
        psg.Render(ch0, ch1, ch2, 4096);

        // with tone and noise off the chip outputs a CONTINUOUS level (the DC offset the
        // renderer removes with the high-pass), not an oscillation
        Assert.All(ch0, v => Assert.Equal(ch0[0], v));
    }

    [Fact]
    public void KonamiScc_ToneFrequencyFollowsHardwareFormula()
    {
        const double rate = 192000;
        var scc = new KonamiScc(MsxClock, rate);

        // 32-point sine wave on channel 2 (index 1)
        for (int i = 0; i < 32; i++)
        {
            scc.Write(0, 0x20 + i, (int)Math.Round(127 * Math.Sin(2 * Math.PI * i / 32)));
        }

        const int period = 100; // f = clock / (32 * (periodo + 1)) = 1107,5 Hz
        scc.Write(1, 2, period & 0xFF);
        scc.Write(1, 3, period >> 8);
        scc.Write(2, 1, 15);   // volume do canal 2
        scc.Write(3, 0, 0x02); // key on do canal 2

        int count = (int)rate;
        var channels = new[] { new float[count], new float[count], new float[count], new float[count], new float[count] };
        scc.Render(channels, count);

        double expected = MsxClock / (16.0 * (period + 1));
        double measured = EstimateFrequency(channels[1], rate);
        Assert.InRange(measured, expected * 0.97, expected * 1.03);
        Assert.All(channels[0], v => Assert.Equal(0f, v));
    }

    [Fact]
    public void VgmFile_ParsesPsgSccAndTiming()
    {
        byte[] data = BuildVgm(new List<byte[]>
        {
            Psg(7, 0x3E),
            Scc(1, 2, 100),
            Wait(882), // 20 ms
            Psg(8, 15),
        }, totalSamples: 44100);

        var vgm = VgmFile.Parse(data, "teste");

        Assert.Equal(3, vgm.Events.Count);
        Assert.Equal(VgmEventKind.Psg, vgm.Events[0].Kind);
        Assert.Equal(VgmEventKind.Scc, vgm.Events[1].Kind);
        Assert.Equal(882, vgm.Events[2].Sample);
        Assert.Equal(MsxClock, vgm.AyClock, 0);
    }

    [Fact]
    public void VgmLoopFinder_FindsRepeatedSection()
    {
        // 140 s: the note pattern repeats every 40 s from the start
        var commands = new List<byte[]>();
        for (int second = 0; second < 140; second++)
        {
            int period = 100 + (second % 40);
            commands.Add(Psg(0, period & 0xFF));
            commands.Add(Psg(1, period >> 8));
            commands.Add(Wait(44100));
        }

        var vgm = VgmFile.Parse(BuildVgm(commands, 140 * 44100), "loop");
        var loop = VgmLoopFinder.Find(vgm, minSpanSeconds: 5);

        Assert.NotNull(loop);
        Assert.Equal(0.0, loop!.Value.Start, 1);
        Assert.Equal(40.0, loop.Value.Duration, 1);
    }

    [Fact]
    public void Renderer_ProducesAudibleStereoWithExpectedLength()
    {
        var vgm = VgmFile.Parse(BuildVgm(new List<byte[]>
        {
            Psg(7, 0x3E),
            Psg(0, 200 & 0xFF),
            Psg(1, 200 >> 8),
            Psg(8, 15),
            Wait(44100),
        }, 44100), "tom");

        var audio = MsxSoundRenderer.Render(vgm, AudioProfile.Original, 44100, 0, 0.5);

        Assert.Equal(2, audio.Channels);
        Assert.InRange(audio.Frames, 22000, 22100);
        Assert.True(audio.Peak > 0.01f);
        Assert.Contains(audio.Samples, s => s != 0);
    }

    [Fact]
    public void Renderer_IsDeterministic()
    {
        var vgm = VgmFile.Parse(BuildVgm(new List<byte[]>
        {
            Psg(7, 0x3E), Psg(0, 120), Psg(1, 1), Psg(8, 12), Wait(22050),
        }, 22050), "tom");

        var a = MsxSoundRenderer.Render(vgm, AudioProfile.Original, 44100, 0, 0.2);
        var b = MsxSoundRenderer.Render(vgm, AudioProfile.Original, 44100, 0, 0.2);

        Assert.Equal(a.Samples, b.Samples);
    }

    [Fact]
    public void Renderer_StereoProfileKeepsChannelsCenteredWhenPanIsZero()
    {
        var vgm = VgmFile.Parse(BuildVgm(new List<byte[]>
        {
            Psg(7, 0x3E), Psg(0, 120), Psg(1, 1), Psg(8, 12), Wait(22050),
        }, 22050), "tom");

        var audio = MsxSoundRenderer.Render(vgm, AudioProfile.Original, 44100, 0, 0.2);
        for (int i = 0; i < audio.Frames; i++)
        {
            Assert.Equal(audio.Samples[i * 2], audio.Samples[i * 2 + 1]);
        }
    }

    private static byte[] Psg(int register, int value) => new byte[] { 0xA0, (byte)register, (byte)value };

    private static byte[] Scc(int port, int register, int value) =>
        new byte[] { 0xD2, (byte)port, (byte)register, (byte)value };

    private static byte[] Wait(int samples) => new[]
    {
        (byte)0x61, (byte)(samples & 0xFF), (byte)((samples >> 8) & 0xFF),
    };

    private static byte[] BuildVgm(List<byte[]> commands, int totalSamples)
    {
        using var stream = new MemoryStream();
        var header = new byte[0x100];
        header[0] = (byte)'V';
        header[1] = (byte)'g';
        header[2] = (byte)'m';
        header[3] = (byte)' ';
        BitConverter.GetBytes(0x161).CopyTo(header, 0x08);
        BitConverter.GetBytes(totalSamples).CopyTo(header, 0x18);
        BitConverter.GetBytes(0x100 - 0x34).CopyTo(header, 0x34);
        BitConverter.GetBytes((uint)MsxClock).CopyTo(header, 0x74);
        stream.Write(header);

        foreach (var command in commands) stream.Write(command);
        stream.WriteByte(0x66);
        return stream.ToArray();
    }
}

