using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Visualizer;

public sealed partial class VisualizerGame : Game
{
    private void UpdateSpectrum()
    {
        if (_player is null)
            return;

        var scope = _player.Scope;
        int pos = _player.ScopePos;
        for (int i = 0; i < FftSize; i++)
            _fftSamples[i] = scope[(pos + scope.Length - FftSize + i) % scope.Length];

        Fft.Magnitude(_fftSamples, _fftMagnitude, _fftWindow);

        int bins = _fftMagnitude.Length;
        for (int b = 0; b < SpectrumBars; b++)
        {
            double f0 = Math.Pow(bins, b / (double)SpectrumBars);
            double f1 = Math.Pow(bins, (b + 1) / (double)SpectrumBars);
            int i0 = Math.Clamp((int)f0, 1, bins - 1);
            int i1 = Math.Clamp((int)Math.Ceiling(f1), i0 + 1, bins);

            float value = 0;
            for (int i = i0; i < i1; i++)
                value = Math.Max(value, _fftMagnitude[i]);

            // dB scale: 0 dB = full scale, -60 dB = floor. A linear bar would make everything but
            // the fundamental invisible; this is how audio analysers show the whole range.
            float db = 20f * MathF.Log10(Math.Max(value, 1e-5f));
            float norm = Math.Clamp((db + 60f) / 60f, 0f, 1f);

            _bars[b] = Math.Max(norm, _bars[b] - 0.03f);
            _barPeak[b] = Math.Max(norm, _barPeak[b] - 0.008f);
        }
    }

    private void DrawSpectrum()
    {
        const int x = 336, y = 104, w = VW - 348, h = 182;
        Panel(x, y, w, h);

        DrawViewTabs(x + 10, y + 2);

        // Room for the dB scale on the left and the frequency labels under the plot.
        const int gutter = 40;
        const int footer = 12;
        int left = x + gutter, top = y + 22;
        int innerW = w - gutter - 12, innerH = h - 34 - footer;

        // dB grid: 0 (full scale) down to -60 dB.
        for (int db = 0; db >= -60; db -= 20)
        {
            int gy = top + (int)((1 - (db + 60) / 60f) * (innerH - 1));
            Rect(left, gy, innerW, 1, Grid);
            Text(db == 0 ? "0 DB" : $"{db}", x + 8, gy - 3, db == 0 ? TextColor : Dim);
        }

        // Frequency grid (log axis, matching the bars).
        foreach (double freq in FrequencyMarks)
        {
            int gx = BarX(freq, left, innerW);
            Rect(gx, top, 1, innerH, Grid);

            string label = freq >= 1000 ? $"{freq / 1000:0}K" : $"{freq:0}";
            Text(label, gx + 3, top + innerH + 2, Dim);
        }

        float barW = innerW / (float)SpectrumBars;
        for (int b = 0; b < SpectrumBars; b++)
        {
            float value = Math.Clamp(_bars[b], 0f, 1f);
            int barH = (int)(value * innerH);
            int bx = left + (int)(b * barW);
            int bw = Math.Max(1, (int)barW - 1);

            if (barH > 0)
            {
                var color = Color.Lerp(PsgColor, SccColor, b / (float)SpectrumBars);
                Rect(bx, top + innerH - barH, bw, barH, color);
            }

            // Peak hold: a bright cap that falls slowly, so short transients stay visible.
            int peakY = top + innerH - (int)(Math.Clamp(_barPeak[b], 0f, 1f) * innerH);
            Rect(bx, Math.Clamp(peakY, top, top + innerH - 1), bw, 2, new Color(235, 244, 252));
        }
    }

    /// <summary>Horizontal position of a frequency on the log-spaced spectrum axis.</summary>
    private static int BarX(double frequency, int left, int innerW)
    {
        double bin = frequency * FftSize / 48000.0;
        double norm = Math.Log(Math.Max(1.0, bin)) / Math.Log(FftSize / 2.0);
        return left + (int)Math.Round(Math.Clamp(norm, 0, 1) * (innerW - 1));
    }

    /// <summary>
    /// Scrolling piano roll: time to the right, pitch up, one colour per channel. Arpeggios leave a
    /// short tail, so fast SCC runs and PSG chords stay stacked (a chord is visible, not a staircase
    /// of disconnected dots).
    /// </summary>
    private void DrawRoll()
    {
        const int x = 336, y = 104, w = VW - 348, h = 326;
        Panel(x, y, w, h);

        int tabsRight = DrawViewTabs(x + 10, y + 2);

        // Channel legend (same colours as the notes), after the tabs.
        int lx = tabsRight + 16;
        for (int ch = 0; ch < 8; ch++)
        {
            Rect(lx, y + 8, 5, 5, ChannelColors[ch]);
            Text(ChannelName(ch), lx + 8, y + 7, Dim);
            lx += 42;
        }

        Text("MAP BELOW: CLICK TO SEEK", lx + 8, y + 7, Dim);

        const int gutter = 22;   // mini keyboard on the left
        const int axis = 10;     // time labels under the plot
        const int mapH = 36;     // whole-song density map
        int left = x + 10 + gutter, top = y + 24;
        int innerW = w - gutter - 22;
        int innerH = h - 34 - axis - mapH - 4;
        int mapY = top + innerH + 2;
        int axisY = mapY + mapH + 2;
        const int lowMidi = 24, highMidi = 96;   // C1..C7
        int semis = highMidi - lowMidi + 1;
        int step = Math.Max(2, innerH / semis);

        int RowY(int midi) => top + innerH - (midi - lowMidi + 1) * innerH / semis;
        int RowH() => Math.Max(2, innerH / semis - 1);

        // Mini keyboard gutter (white/black rows) with the C labels.
        for (int midi = lowMidi; midi <= highMidi; midi++)
        {
            bool black = Notes[midi % 12].Contains('#');
            Rect(x + 10, RowY(midi), gutter - 2, RowH(),
                black ? new Color(44, 52, 64) : new Color(140, 152, 166));
        }

        // Alternate octave bands, so the pitch is readable at a glance.
        for (int midi = lowMidi; midi < highMidi; midi += 12)
        {
            if (((midi / 12) & 1) != 0)
                continue;

            int y0 = RowY(midi + 12);
            int y1 = RowY(midi);
            Rect(left, y0, innerW, Math.Max(1, y1 - y0), new Color(20, 27, 38));
        }

        for (int midi = lowMidi; midi <= highMidi; midi += 12)
        {
            int py = RowY(midi);
            Rect(left, py, innerW, 1, Grid);
            if (py >= top + 4)
                Text("C" + (midi / 12 - 1), left + 3, py - 3, Dim);
        }

        // Time grid every 2 s (0 = now at the right edge).
        for (int s = 0; s <= (int)RollSeconds; s += 2)
        {
            int gx = left + (int)(innerW * (1 - s / RollSeconds));
            if (s > 0)
                Rect(gx, top, 1, innerH, Grid);

            string label = s == 0 ? "NOW" : $"-{s}S";
            int tx = s == 0 ? gx - PixelFont.Measure(label) + 1 : gx + 2;
            Text(label, tx, axisY, s == 0 ? Accent : Dim);
        }

        if (_player is null)
            return;

        double now = _player.Time;

        for (int ch = 0; ch < 8; ch++)
        {
            bool audible = IsAudible(ch);
            var color = audible ? ChannelColors[ch] : Dim;
            var list = _notes[ch];

            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.Key || e.Frequency < 20)
                    continue;

                double t0 = e.Time;
                double nextTime = i + 1 < list.Count ? list[i + 1].Time : now;
                double t1 = Math.Min(nextTime, now) + NoteTailSeconds;
                if (t1 <= t0)
                    continue;

                int midi = (int)Math.Round(69 + 12 * Math.Log2(e.Frequency / 440.0));
                int py = RowY(Math.Clamp(midi, lowMidi, highMidi));

                int px0 = left + (int)(innerW * (1 - (now - t0) / RollSeconds));
                int px1 = left + (int)(innerW * (1 - (now - t1) / RollSeconds));
                if (px1 < left || px0 > left + innerW)
                    continue;

                px0 = Math.Clamp(px0, left, left + innerW - 1);
                px1 = Math.Clamp(px1, left, left + innerW);

                int thickness = Math.Clamp(3 + (int)Math.Round(Math.Clamp(e.Volume, 0f, 1f) * 4f), 3, Math.Max(3, step));
                Rect(px0, py, Math.Max(1, px1 - px0), thickness, color);
            }
        }

        // "Now" line at the right edge.
        Rect(left + innerW - 1, top, 2, innerH, Accent);

        DrawSongMap(left, mapY, innerW, mapH);
    }

    /// <summary>
    /// Whole-song density map under the roll: one row per channel, brightness = how busy that channel
    /// is at that moment. Click or drag it to jump anywhere; the last 12 s (the roll window) and the
    /// loop window are highlighted.
    /// </summary>
    private void DrawSongMap(int left, int mapY, int innerW, int mapH)
    {
        if (_player is null || _songMap is null)
            return;

        double total = Math.Max(0.001, _player.TotalSeconds);
        int buckets = _songMap[0].Length;

        Rect(left, mapY, innerW, mapH, PanelColor);

        // Window currently shown by the roll (the right-hand 12 s).
        int windowX = left + (int)(innerW * Math.Clamp((total - RollSeconds) / total, 0, 1));
        Rect(windowX, mapY, Math.Max(1, left + innerW - windowX), mapH, new Color(26, 34, 46));

        int rowH = Math.Max(2, (mapH - 2) / 8);
        float bucketW = innerW / (float)buckets;

        for (int ch = 0; ch < 8; ch++)
        {
            var baseColor = IsAudible(ch) ? ChannelColors[ch] : Dim;
            int ry = mapY + 1 + ch * rowH;
            var row = _songMap[ch];

            for (int b = 0; b < buckets; b++)
            {
                float value = row[b];
                if (value <= 0.02f)
                    continue;

                int x0 = (int)(b * bucketW);
                int x1 = (int)((b + 1) * bucketW);
                int bx = left + x0;
                int bw = Math.Max(1, x1 - x0);

                var color = Color.Lerp(PanelColor, baseColor, 0.25f + 0.75f * Math.Min(1f, value));
                Rect(bx, ry, bw, Math.Max(1, rowH - 1), color);
            }
        }

        // Loop window edges.
        var song = _player.Song;
        if (song.HasLoop)
        {
            int lx = left + (int)(innerW * Math.Clamp(song.LoopStartSeconds / total, 0, 1));
            int lw = Math.Max(2, (int)(innerW * Math.Clamp(song.LoopSeconds / total, 0, 1)));
            Rect(lx, mapY, 1, mapH, Dim);
            Rect(Math.Min(lx + lw, left + innerW - 1), mapY, 1, mapH, Dim);
        }

        // Playhead.
        int px = left + (int)(innerW * Math.Clamp(_player.Time / total, 0, 1));
        Rect(Math.Clamp(px - 1, left, left + innerW - 2), mapY, 2, mapH, Accent);

        Rect(left, mapY, innerW, 1, Grid);
        Rect(left, mapY + mapH - 1, innerW, 1, Grid);

        _songMapHit = new Rectangle(left, mapY, innerW, mapH);
    }

    private void DrawScope()
    {
        const int x = 336, y = 292, w = VW - 348, h = 138;
        Panel(x, y, w, h);
        Text("MASTER", x + 10, y + 6, Accent);

        int left = x + 10, top = y + 22, innerW = w - 20, innerH = h - 32;
        Rect(left, top + innerH / 2, innerW, 1, Grid);

        var scope = _player!.Scope;
        int pos = _player.ScopePos;
        int n = scope.Length;

        // Trigger on an upward zero crossing so the waveform does not jitter frame to frame.
        int trig = 0;
        for (int i = 0; i < n / 2; i++)
        {
            float a = scope[(pos + i) % n];
            float b = scope[(pos + i + 1) % n];
            if (a < 0 && b >= 0)
            {
                trig = i;
                break;
            }
        }

        int available = n - trig - 1;
        if (available < innerW)
            available = n - trig;

        // Min/max envelope per column (keeps the peaks instead of averaging them away).
        for (int col = 0; col < innerW; col++)
        {
            int i0 = trig + col * available / innerW;
            int i1 = trig + (col + 1) * available / innerW;
            if (i1 <= i0) i1 = i0 + 1;

            float min = 1f, max = -1f;
            for (int i = i0; i < i1 && i < n; i++)
            {
                float v = scope[(pos + i) % n];
                if (v < min) min = v;
                if (v > max) max = v;
            }

            int y0 = top + innerH / 2 - (int)(max * innerH * 1.7f);
            int y1 = top + innerH / 2 - (int)(min * innerH * 1.7f);
            y0 = Math.Clamp(y0, top, top + innerH - 1);
            y1 = Math.Clamp(y1, top, top + innerH - 1);

            int topY = Math.Min(y0, y1);
            int height = Math.Max(1, Math.Abs(y1 - y0));
            Rect(left + col, topY, 1, height, PsgColor);
        }
    }
}
