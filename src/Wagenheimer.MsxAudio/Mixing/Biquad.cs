using System;

namespace Wagenheimer.MsxAudio.Mixing;

/// <summary>RBJ biquad used by the post-processing filters.</summary>
public sealed class Biquad
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    /// <summary>Creates a high-pass biquad.</summary>
    public static Biquad CreateHighPass(double sampleRate, double frequency, double q)
    {
        var b = new Biquad();
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * q);
        double a0 = 1 + alpha;
        b._b0 = (1 + cos) / 2 / a0;
        b._b1 = -(1 + cos) / a0;
        b._b2 = (1 + cos) / 2 / a0;
        b._a1 = -2 * cos / a0;
        b._a2 = (1 - alpha) / a0;
        return b;
    }

    /// <summary>Creates a low-pass biquad.</summary>
    public static Biquad CreateLowPass(double sampleRate, double frequency, double q)
    {
        var b = new Biquad();
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cos = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2 * q);
        double a0 = 1 + alpha;
        b._b0 = (1 - cos) / 2 / a0;
        b._b1 = (1 - cos) / a0;
        b._b2 = (1 - cos) / 2 / a0;
        b._a1 = -2 * cos / a0;
        b._a2 = (1 - alpha) / a0;
        return b;
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
