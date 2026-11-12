using System;
using System.Collections.Generic;
using System.Linq;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;
using Xunit;
using Xunit.Abstractions;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>Tempo-synced echo and loudness: objective checks on the final polish of the mix.</summary>
public class RemasterTests
{
    private readonly ITestOutputHelper _output;

    public RemasterTests(ITestOutputHelper output) => _output = output;

    private static RenderedAudio Sine(double hertz, double dbfs, double seconds, int rate = 48000)
    {
        int frames = (int)(seconds * rate);
        double amplitude = Math.Pow(10, dbfs / 20.0) * 32767;
        var samples = new short[frames * 2];

        for (int i = 0; i < frames; i++)
        {
            short value = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * hertz * i / rate));
            samples[i * 2] = value;
            samples[i * 2 + 1] = value;
        }

        return new RenderedAudio(samples, rate, 2, 0);
    }

    [Fact]
    public void Loudness_AStereoSineReadsItsOwnLevelInLufs()
    {
        // BS.1770: a 1 kHz sine with the same level in both channels reads its own dBFS in LUFS
        // (the EBU test signal: -23 dBFS stereo = -23 LUFS), within a tenth of a unit.
        double lufs = LoudnessMeter.IntegratedLufs(Sine(1000, -20, 5));

        _output.WriteLine($"-20 dBFS sine: {lufs:0.00} LUFS");
        Assert.InRange(lufs, -20.15, -19.85);
    }

    [Fact]
    public void Loudness_SilenceAroundTheMusicDoesNotMakeItQuieter()
    {
        RenderedAudio tone = Sine(440, -18, 4);
        var padded = new short[tone.Samples.Length + 2 * 48000 * 2 * 3];       // 3 s of silence before and after
        Array.Copy(tone.Samples, 0, padded, 48000 * 2 * 3, tone.Samples.Length);

        double alone = LoudnessMeter.IntegratedLufs(tone);
        double withSilence = LoudnessMeter.IntegratedLufs(new RenderedAudio(padded, 48000, 2, 0));

        Assert.InRange(Math.Abs(alone - withSilence), 0, 0.5);       // only the blocks that straddle the edges differ
    }

    [Fact]
    public void Normalize_ReachesTheTargetWhenThePeakAllows()
    {
        RenderedAudio quiet = Sine(440, -30, 5);

        RenderedAudio loud = LoudnessNormalizer.Normalize(quiet, -14.0, 0.89);
        double lufs = LoudnessMeter.IntegratedLufs(loud);

        _output.WriteLine($"normalised: {lufs:0.00} LUFS, peak {loud.Peak:0.00}");
        Assert.InRange(lufs, -14.4, -13.6);
        Assert.True(loud.Peak <= 0.89 + 0.001);
    }

    [Fact]
    public void Normalize_NeverClipsEvenIfItCannotReachTheTarget()
    {
        // A signal that is mostly silence with one loud click: the gain that would reach -14 LUFS
        // would push the click far over full scale, so the peak ceiling wins.
        var samples = new short[48000 * 2 * 5];
        for (int i = 0; i < 40; i++)
            samples[48000 * 2 * 2 + i * 2] = samples[48000 * 2 * 2 + i * 2 + 1] = 30000;

        RenderedAudio result = LoudnessNormalizer.Normalize(new RenderedAudio(samples, 48000, 2, 0.9f), -14.0, 0.89);

        Assert.True(result.Samples.Max(s => Math.Abs((int)s)) <= 0.89 * 32768 + 2);
    }

    [Fact]
    public void Normalize_LeavesSilenceAlone()
    {
        var silent = new RenderedAudio(new short[48000 * 2 * 3], 48000, 2, 0);

        Assert.Same(silent, LoudnessNormalizer.Normalize(silent));
    }

    [Fact]
    public void Plan_FindsTheTempoOfASteadySong()
    {
        var script = new AutoMixTests.Script();
        script.Psg(7, 0x38);

        double eighth = 60.0 / 132 / 2;
        double[] melody = { 523.25, 587.33, 659.25, 783.99, 659.25, 587.33, 523.25, 392.0 };

        for (int i = 0; i < 64; i++)
        {
            script.Note(0, melody[i % melody.Length], 12);
            script.Wait(eighth * 0.8);
            script.Psg(8, 0);
            script.Wait(eighth * 0.2);
        }

        AutoMixPlan plan = AutoMixAnalyzer.Analyze(script.Build());

        _output.WriteLine($"bpm {plan.Bpm}, echo {plan.EchoLeftSeconds:0.000}/{plan.EchoRightSeconds:0.000} s");

        Assert.True(plan.TempoConfident);
        Assert.InRange(plan.Bpm, 131, 133);
        Assert.Equal(0.75 * 60 / plan.Bpm, plan.EchoLeftSeconds, 3);            // a dotted eighth
        Assert.Equal(60 / plan.Bpm, plan.EchoRightSeconds, 3);                  // a quarter note
    }

    [Fact]
    public void Plan_UsesFixedEchoTimesWhenThereIsNoTempo()
    {
        AutoMixPlan plan = AutoMixPlan.Neutral();

        Assert.False(plan.TempoConfident);
        Assert.Equal(0.19, plan.EchoLeftSeconds, 3);
        Assert.Equal(0.255, plan.EchoRightSeconds, 3);
    }

    [Fact]
    public void Echo_ArrivesAtTheTempoDelay()
    {
        // One click on the lead channel; the left echo must come back after the plan's left delay.
        AutoMixPlan plan = AutoMixPlan.Neutral();
        ChannelMix lead = plan.Channels[1];
        lead.Role = ChannelRole.Lead;
        lead.DelaySend = 0.5f;
        lead.Pan = -1f;                                         // all in the left channel

        const int rate = 48000;
        var processor = new AutoMixProcessor(
            new AutoMixPlan { Channels = plan.Channels, Bpm = 120, TempoConfident = true }, 1, rate);

        int frames = rate;                                       // one second
        var channels = Enumerable.Range(0, 8).Select(_ => new float[frames]).ToList();
        channels[1][100] = 0.5f;

        var left = new float[frames];
        var right = new float[frames];
        float[] gains = Enumerable.Repeat(1f, 8).ToArray();
        processor.Process(channels, frames, frames, 1f, gains, left, right);

        // 120 BPM: the left echo is a dotted eighth = 0.375 s.
        int expected = 100 + (int)(0.375 * rate);
        float atEcho = left.Skip(expected - 3).Take(7).Max(Math.Abs);
        float elsewhere = left.Skip(expected + 2000).Take(2000).Max(Math.Abs);

        _output.WriteLine($"echo {atEcho:0.0000} vs elsewhere {elsewhere:0.0000}");
        Assert.True(atEcho > elsewhere * 3, "a distinct repeat arrives at the tempo delay");
    }
}
