using System;

namespace Wagenheimer.MsxAudio.Vgm;

/// <summary>
/// Finds a VGM's exact loop point by comparing the sequence of register writes.
/// </summary>
/// <remarks>
/// A VGM recorded by openMSX carries no loop information (the game's sound driver decides to
/// repeat). The repetition shows up in the log as a sequence of writes <b>identical</b> to an
/// earlier section. Finding that repetition gives the loop with sample accuracy — which is what
/// removes the "click" at the seam that cutting a separate audio recording used to produce.
/// </remarks>
public static class VgmLoopFinder
{
    /// <summary>
    /// Finds the VGM loop.
    /// </summary>
    /// <param name="vgm">Parsed file.</param>
    /// <param name="minSpanSeconds">
    /// How long the repetition must hold to be accepted (25 s by default). Avoids mistaking a
    /// repeated bar for the song loop.
    /// </param>
    /// <param name="log">Optional diagnostics (evaluated candidates).</param>
    /// <returns>Loop start and duration in seconds, or <c>null</c> when there is none.</returns>
    public static (double Start, double Duration)? Find(
        VgmFile vgm,
        double minSpanSeconds = 25.0,
        Action<string>? log = null)
    {
        var events = vgm.Events;
        int n = events.Count;
        if (n < 200) return null;

        // Compact signature of each write (for fast comparison).
        int[] signature = new int[n];
        long[] times = new long[n];
        for (int i = 0; i < n; i++)
        {
            VgmEvent e = events[i];
            signature[i] = (int)e.Kind | (e.P0 << 8) | (e.P1 << 16) | (e.P2 << 24);
            times[i] = e.Sample;
        }

        long minSpan = (long)(minSpanSeconds * 44100.0);
        long minPeriod = (long)(4 * 44100.0);
        int maxStart = n / 3;

        for (int start = 0; start < maxStart; start += 40)
        {
            for (int k = 50; start + k < n; k++)
            {
                if (signature[start] != signature[start + k])
                    continue;

                int run = 0;
                while (start + run + k < n && signature[start + run] == signature[start + run + k])
                    run++;

                if (run < 2) continue;

                long span = times[start + run - 1] - times[start];
                long period = times[start + k] - times[start];
                if (period <= 0) continue;

                // A real loop: the sequence repeats for at least one whole period. If the capture
                // ended first, accept when the repeated section covers ~the period (long songs
                // recorded only a little beyond one loop).
                bool fullPeriod = run >= k;
                bool reachedEnd = start + run + k >= n;
                bool accepted = fullPeriod
                    ? span >= minSpan
                    : reachedEnd && span >= period * 0.9 && period > minPeriod;
                if (!accepted) continue;

                log?.Invoke(
                    $"candidate start={times[start] / 44100.0:F3}s period={period / 44100.0:F3}s run={run} span={span / 44100.0:F2}s idx={start},{k}");
                return (times[start] / 44100.0, period / 44100.0);
            }
        }

        return null;
    }
}
