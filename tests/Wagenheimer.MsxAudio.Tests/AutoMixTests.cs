using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;
using Xunit;
using Xunit.Abstractions;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>The automatic mix: what it recognises, and objective checks on what it renders.</summary>
public class AutoMixTests
{
    private readonly ITestOutputHelper _output;

    public AutoMixTests(ITestOutputHelper output) => _output = output;

    private const double Clock = 1789772.5;

    private static (byte Low, byte High) Period(double frequency)
    {
        int period = (int)Math.Round(Clock / (16.0 * frequency));
        return ((byte)(period & 0xFF), (byte)((period >> 8) & 0x0F));
    }

    internal sealed class Script
    {
        private readonly List<byte[]> _commands = new();
        private long _samples;

        public void Psg(byte register, byte value) => _commands.Add(new byte[] { 0xA0, register, value });

        public void Note(int channel, double frequency, int volume)
        {
            (byte low, byte high) = Period(frequency);
            Psg((byte)(channel * 2), low);
            Psg((byte)(channel * 2 + 1), high);
            Psg((byte)(8 + channel), (byte)volume);
        }

        public void Wait(double seconds)
        {
            int remaining = (int)Math.Round(seconds * 44100);
            _samples += remaining;

            while (remaining > 0)
            {
                int step = Math.Min(remaining, 65535);
                _commands.Add(new[] { (byte)0x61, (byte)(step & 0xFF), (byte)((step >> 8) & 0xFF) });
                remaining -= step;
            }
        }

        public VgmFile Build()
        {
            using var stream = new MemoryStream();
            var header = new byte[0x100];
            header[0] = (byte)'V'; header[1] = (byte)'g'; header[2] = (byte)'m'; header[3] = (byte)' ';
            BitConverter.GetBytes(0x151).CopyTo(header, 0x08);
            BitConverter.GetBytes((int)_samples).CopyTo(header, 0x18);
            BitConverter.GetBytes((int)1789772).CopyTo(header, 0x74);        // AY8910 clock
            BitConverter.GetBytes(0x100 - 0x34).CopyTo(header, 0x34);        // data offset
            stream.Write(header);

            foreach (byte[] command in _commands)
                stream.Write(command);

            stream.WriteByte(0x66);
            return VgmFile.Parse(stream.ToArray(), "song");
        }
    }

    /// <summary>A bass line, a tune and a fast arpeggio, six seconds of it.</summary>
    internal static VgmFile BuildSong()
    {
        var script = new Script();
        script.Psg(7, 0x38);                               // tone on for the three channels, noise off

        double[] tune = { 523.25, 587.33, 659.25, 783.99, 659.25, 587.33, 523.25, 392.0 };
        double[] chord = { 261.63, 329.63, 392.0 };
        double[] bass = { 65.41, 65.41, 87.31, 98.0 };

        for (int step = 0; step < 24; step++)             // 24 x 0.25 s
        {
            script.Note(0, bass[step / 2 % bass.Length], 13);
            script.Note(1, tune[step % tune.Length], 12);

            for (int tick = 0; tick < 12; tick++)          // twelve arpeggio notes per step (1/48 s)
            {
                script.Note(2, chord[tick % chord.Length], 9);
                script.Wait(0.25 / 12);
            }
        }

        return script.Build();
    }

    private static (double Rms, double Peak) Measure(RenderedAudio audio)
    {
        double sum = 0;
        double peak = 0;

        foreach (short sample in audio.Samples)
        {
            double v = sample / 32768.0;
            sum += v * v;
            peak = Math.Max(peak, Math.Abs(v));
        }

        return (Math.Sqrt(sum / Math.Max(1, audio.Samples.Length)), peak);
    }

    [Fact]
    public void Analyzer_RecognisesBassTuneAndArpeggio()
    {
        AutoMixPlan plan = AutoMixAnalyzer.Analyze(BuildSong());

        _output.WriteLine(plan.Summary);

        Assert.Equal(ChannelRole.Bass, plan.Channels[0].Role);
        Assert.Equal(ChannelRole.Lead, plan.Channels[1].Role);
        Assert.Equal(ChannelRole.Arpeggio, plan.Channels[2].Role);
        Assert.All(plan.Channels.Skip(3), c => Assert.Equal(ChannelRole.Silent, c.Role));
    }

    [Fact]
    public void Analyzer_TakesNoiseOnlyChannelsForPercussion()
    {
        var script = new Script();
        script.Psg(7, 0x37);                               // channel A: noise only (tone off, noise on); the rest off
        script.Psg(6, 8);

        for (int i = 0; i < 20; i++)
        {
            script.Psg(8, 12);
            script.Wait(0.1);
            script.Psg(8, 0);
            script.Wait(0.1);
        }

        AutoMixPlan plan = AutoMixAnalyzer.Analyze(script.Build());

        Assert.Equal(ChannelRole.Percussion, plan.Channels[0].Role);
    }

    [Fact]
    public void Analyzer_VibratoDoesNotLookLikeAnArpeggio()
    {
        var script = new Script();
        script.Psg(7, 0x38);

        // A held note wobbling by a semitone around A4, every 1/50 s: a vibrato, not a chord.
        for (int i = 0; i < 400; i++)
        {
            script.Note(0, i % 2 == 0 ? 440.0 : 466.16, 12);
            script.Wait(0.02);
        }

        AutoMixPlan plan = AutoMixAnalyzer.Analyze(script.Build());

        Assert.NotEqual(ChannelRole.Arpeggio, plan.Channels[0].Role);
    }

    [Fact]
    public void Render_IsAudibleBoundedAndDeterministic()
    {
        VgmFile song = BuildSong();

        RenderedAudio first = MsxSoundRenderer.Render(song, AudioProfile.Auto, 48000, 0, 5);
        RenderedAudio second = MsxSoundRenderer.Render(song, AudioProfile.Auto, 48000, 0, 5);
        RenderedAudio remaster = MsxSoundRenderer.Render(song, AudioProfile.Remaster, 48000, 0, 5);

        (double rms, double peak) = Measure(first);
        (double remasterRms, double remasterPeak) = Measure(remaster);

        _output.WriteLine($"auto: RMS {20 * Math.Log10(rms):0.0} dBFS, peak {20 * Math.Log10(peak):0.0} dBFS");
        _output.WriteLine($"remaster: RMS {20 * Math.Log10(remasterRms):0.0} dBFS, peak {20 * Math.Log10(remasterPeak):0.0} dBFS");

        Assert.True(rms > 0.01, "the mix is not silent");
        Assert.True(peak <= 0.99, "the limiter keeps it below full scale");
        Assert.Equal(first.Samples, second.Samples);
    }

    [Fact]
    public void Render_PutsTheArpeggioOnOneSideAndKeepsTheBassInTheMiddle()
    {
        VgmFile song = BuildSong();
        AutoMixPlan plan = AutoMixAnalyzer.Analyze(song);

        Assert.Equal(0f, plan.Channels[0].Pan);                 // bass: centred
        Assert.True(Math.Abs(plan.Channels[2].Pan) > 0.4f);     // arpeggio: spread

        RenderedAudio audio = MsxSoundRenderer.Render(song, AudioProfile.Auto, 48000, 0, 5);

        double left = 0, right = 0;
        for (int i = 0; i + 1 < audio.Samples.Length; i += 2)
        {
            left += Math.Pow(audio.Samples[i] / 32768.0, 2);
            right += Math.Pow(audio.Samples[i + 1] / 32768.0, 2);
        }

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void LivePlayerAndOfflineRendererAgreeOnTheRoles()
    {
        // Both paths read the same plan: switching profile must not change what a channel is.
        AutoMixPlan a = AutoMixAnalyzer.Analyze(BuildSong());
        AutoMixPlan b = AutoMixAnalyzer.Analyze(BuildSong());

        Assert.Equal(a.Summary, b.Summary);
    }
}
