using System;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>Small radix-2 FFT used by the spectrum view.</summary>
public static class Fft
{
    /// <summary>
    /// Transforms <paramref name="samples"/> (length must be a power of two) in place and writes the
    /// magnitude (0..1) into <paramref name="magnitude"/>.
    /// </summary>
    public static void Magnitude(ReadOnlySpan<float> samples, Span<float> magnitude, Span<float> window)
    {
        int n = samples.Length;
        if (n != window.Length || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT needs power-of-two samples/window of the same size.");
        if (magnitude.Length < n / 2)
            throw new ArgumentException("magnitude buffer must hold at least n/2 bins.");

        var re = new double[n];
        var im = new double[n];

        for (int i = 0; i < n; i++)
            re[i] = samples[i] * window[i];

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            double wRe = Math.Cos(angle);
            double wIm = Math.Sin(angle);

            for (int i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k;
                    int b = a + len / 2;
                    double uRe = re[a], uIm = im[a];
                    double vRe = re[b] * curRe - im[b] * curIm;
                    double vIm = re[b] * curIm + im[b] * curRe;
                    re[a] = uRe + vRe;
                    im[a] = uIm + vIm;
                    re[b] = uRe - vRe;
                    im[b] = uIm - vIm;

                    double nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }

        for (int i = 0; i < n / 2; i++)
        {
            double mag = Math.Sqrt(re[i] * re[i] + im[i] * im[i]) / n;
            magnitude[i] = (float)Math.Min(1.0, Math.Sqrt(mag * 40.0));
        }
    }

    public static float[] HannWindow(int size)
    {
        var w = new float[size];
        for (int i = 0; i < size; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (size - 1)));
        return w;
    }
}
