using System;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Whole-song density map used by the piano-roll footer: for every channel and time bucket it counts
/// the pitch changes, i.e. how busy that channel is there.
/// </summary>
/// <remarks>
/// Built from the VGM event log when the file is loaded, so the map is complete from the first frame
/// (no need to play the song once) and does not depend on the current playback position.
/// </remarks>
public static class SongMap
{
    /// <summary>Returns <c>[8][buckets]</c>, each channel normalised to 0..1.</summary>
    public static float[][] Build(VgmFile vgm, int buckets)
    {
        if (buckets <= 0)
            throw new ArgumentOutOfRangeException(nameof(buckets));

        var map = new float[8][];
        for (int ch = 0; ch < 8; ch++)
            map[ch] = new float[buckets];

        double total = Math.Max(0.001, vgm.TotalSeconds);

        foreach (var e in vgm.Events)
        {
            int channel = ChannelOf(e);
            if (channel < 0)
                continue;

            int bucket = (int)(e.Sample / 44100.0 / total * buckets);
            bucket = Math.Clamp(bucket, 0, buckets - 1);
            map[channel][bucket] += 1f;
        }

        // Writes are discrete events: dilate by one bucket so the map reads as blocks of activity
        // instead of a dot matrix at the pixel scale.
        for (int ch = 0; ch < 8; ch++)
        {
            var source = (float[])map[ch].Clone();
            for (int b = 0; b < buckets; b++)
            {
                float v = source[b];
                if (b > 0) v = Math.Max(v, source[b - 1]);
                if (b + 1 < buckets) v = Math.Max(v, source[b + 1]);
                map[ch][b] = v;
            }
        }

        // Normalise per channel so every row uses its full range (a quiet channel still shows its
        // own shape). No gamma: the brightness should drop where the channel is calm.
        for (int ch = 0; ch < 8; ch++)
        {
            float max = 0f;
            foreach (float value in map[ch])
                max = Math.Max(max, value);

            if (max <= 0f)
                continue;

            for (int b = 0; b < buckets; b++)
                map[ch][b] /= max;
        }

        return map;
    }

    /// <summary>Channel index of a register write that starts a new note, or -1 otherwise.</summary>
    private static int ChannelOf(VgmEvent e)
    {
        // Only the low byte of each period is counted: one event per pitch change. Volume writes and
        // the high period byte (sent right after) would only add a constant floor to every bucket.
        if (e.Kind == VgmEventKind.Psg)
            return e.P0 <= 5 && (e.P0 & 1) == 0 ? e.P0 / 2 : -1;

        // SCC: port 1 = frequency, even register = low byte.
        return e.P0 == 1 && e.P1 < 10 && (e.P1 & 1) == 0 ? 3 + e.P1 / 2 : -1;
    }
}
