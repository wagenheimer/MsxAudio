using System;
using System.Collections.Generic;

namespace Wagenheimer.MsxAudio.Mixing;

/// <summary>
/// Integrated loudness in LUFS, after ITU-R BS.1770: the signal is K-weighted (a high shelf that
/// models the head and a low cut), its power is measured in 400 ms blocks overlapping by 75 %, and
/// the quiet blocks are left out (absolute gate -70 LUFS, then 10 LU under the average of the rest),
/// so silence at the start or end does not make a song look quieter than it is.
/// </summary>
public static class LoudnessMeter
{
    /// <summary>Measures stereo or mono interleaved 16-bit samples. Returns -70 for silence.</summary>
    public static double IntegratedLufs(short[] samples, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));

        int frames = samples.Length / channels;
        int block = (int)(0.4 * sampleRate);
        int hop = (int)(0.1 * sampleRate);
        if (frames < block)
            return -70;

        // K-weighting, per channel.
        var weighted = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            var shelf = KWeightingStage.HeadShelf(sampleRate);
            var lowCut = KWeightingStage.LowCut(sampleRate);
            var data = new float[frames];

            for (int i = 0; i < frames; i++)
                data[i] = (float)lowCut.Process(shelf.Process(samples[i * channels + c] / 32768.0));

            weighted[c] = data;
        }

        // Power of every block (the channels add up with weight 1).
        var powers = new List<double>();
        for (int start = 0; start + block <= frames; start += hop)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                double square = 0;
                float[] data = weighted[c];
                for (int i = start; i < start + block; i++)
                    square += (double)data[i] * data[i];

                sum += square / block;
            }

            powers.Add(sum);
        }

        static double ToLufs(double power) => power <= 0 ? -200 : -0.691 + 10 * Math.Log10(power);

        // Absolute gate, then the relative gate.
        double absoluteSum = 0;
        int absoluteCount = 0;
        foreach (double power in powers)
        {
            if (ToLufs(power) > -70)
            {
                absoluteSum += power;
                absoluteCount++;
            }
        }

        if (absoluteCount == 0)
            return -70;

        double relativeGate = ToLufs(absoluteSum / absoluteCount) - 10;
        double keptSum = 0;
        int keptCount = 0;
        foreach (double power in powers)
        {
            if (ToLufs(power) > -70 && ToLufs(power) > relativeGate)
            {
                keptSum += power;
                keptCount++;
            }
        }

        return keptCount == 0 ? -70 : ToLufs(keptSum / keptCount);
    }

    /// <summary>Measures a rendered song.</summary>
    public static double IntegratedLufs(RenderedAudio audio) =>
        IntegratedLufs(audio.Samples, audio.Channels, audio.SampleRate);
}

/// <summary>
/// One of the two filters of the BS.1770 K-weighting, with the coefficients computed for any sample
/// rate from the analogue prototype of the standard (the same maths libebur128 uses).
/// </summary>
internal sealed class KWeightingStage
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    private KWeightingStage(double b0, double b1, double b2, double a1, double a2)
    {
        _b0 = b0; _b1 = b1; _b2 = b2; _a1 = a1; _a2 = a2;
    }

    /// <summary>Stage 1: a +4 dB shelf above ~1.7 kHz (the effect of the head).</summary>
    public static KWeightingStage HeadShelf(double sampleRate)
    {
        const double f0 = 1681.974450955533;
        const double gain = 3.999843853973347;
        const double q = 0.7071752369554196;

        double k = Math.Tan(Math.PI * f0 / sampleRate);
        double vh = Math.Pow(10.0, gain / 20.0);
        double vb = Math.Pow(vh, 0.4996667741545416);
        double a0 = 1.0 + k / q + k * k;

        return new KWeightingStage(
            (vh + vb * k / q + k * k) / a0,
            2.0 * (k * k - vh) / a0,
            (vh - vb * k / q + k * k) / a0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / q + k * k) / a0);
    }

    /// <summary>Stage 2: a second-order high-pass at ~38 Hz.</summary>
    public static KWeightingStage LowCut(double sampleRate)
    {
        const double f0 = 38.13547087602444;
        const double q = 0.5003270373238773;

        double k = Math.Tan(Math.PI * f0 / sampleRate);
        double a0 = 1.0 + k / q + k * k;

        return new KWeightingStage(1.0, -2.0, 1.0, 2.0 * (k * k - 1.0) / a0, (1.0 - k / q + k * k) / a0);
    }

    /// <summary>Processes one sample.</summary>
    public double Process(double x)
    {
        double y = _b0 * x + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = x;
        _y2 = _y1;
        _y1 = y;
        return y;
    }
}

/// <summary>Brings a rendered song to a target loudness, so every export plays at the same level.</summary>
public static class LoudnessNormalizer
{
    /// <summary>
    /// Scales the audio to <paramref name="targetLufs"/>. The gain is capped so the loudest sample
    /// stays under <paramref name="peakCeiling"/> (0.89 is -1 dBFS): a song with a very high peak
    /// ends up a little under the target instead of clipping.
    /// </summary>
    public static RenderedAudio Normalize(RenderedAudio audio, double targetLufs = -14.0, double peakCeiling = 0.89)
    {
        ArgumentNullException.ThrowIfNull(audio);

        double loudness = LoudnessMeter.IntegratedLufs(audio);
        if (loudness <= -69.9)
            return audio;                                          // silence: nothing to scale

        int peak = 0;
        foreach (short sample in audio.Samples)
            peak = Math.Max(peak, Math.Abs((int)sample));

        double gain = Math.Pow(10, (targetLufs - loudness) / 20.0);
        if (peak > 0)
            gain = Math.Min(gain, peakCeiling * 32768.0 / peak);

        var scaled = new short[audio.Samples.Length];
        float newPeak = 0;
        for (int i = 0; i < scaled.Length; i++)
        {
            int value = (int)Math.Round(audio.Samples[i] * gain);
            scaled[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
            newPeak = Math.Max(newPeak, Math.Abs(scaled[i]) / 32768f);
        }

        return new RenderedAudio(scaled, audio.SampleRate, audio.Channels, newPeak);
    }
}
