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
    /// <summary>Records a note event whenever a channel changes key or pitch (for the piano roll).</summary>
    private void UpdateNotes()
    {
        if (_player is null)
            return;

        double now = _player.Time;
        for (int ch = 0; ch < 8; ch++)
        {
            var info = _player.Channels[ch];
            int midi = info.Key && info.Frequency >= 20
                ? (int)Math.Round(69 + 12 * Math.Log2(info.Frequency / 440.0))
                : -1;

            var list = _notes[ch];

            if (midi == _lastMidi[ch])
            {
                if (list.Count > 0)
                    list[^1].Volume = Math.Max(list[^1].Volume, info.Volume);
                continue;
            }

            _lastMidi[ch] = midi;
            list.Add(new NoteEvent
            {
                Time = now,
                Frequency = info.Frequency,
                Volume = info.Volume,
                Key = midi >= 0,
            });

            while (list.Count > 1 && list[0].Time < now - RollSeconds * 2)
                list.RemoveAt(0);
        }
    }

    private void ClearNotes()
    {
        for (int ch = 0; ch < 8; ch++)
        {
            _notes[ch].Clear();
            _lastMidi[ch] = -1;
        }
    }

    private void UpdateMeters(float dt)
    {
        if (_player is null)
            return;

        for (int i = 0; i < 8; i++)
        {
            float level = _player.Channels[i].Level;
            _peakHold[i] = Math.Max(level, _peakHold[i] - dt * 0.35f);
        }
    }

    /// <summary>Solo on/off; when soloing, the piano follows the channel that is being heard.</summary>
    private void ToggleSolo(int channel)
    {
        if (_player is null)
            return;

        _player.SoloChannel = _player.SoloChannel == channel ? null : channel;

        if (_player.SoloChannel is int solo)
            _pianoChannel = solo;

        _status = _player.SoloChannel is int s ? $"SOLO {ChannelName(s)}" : "SOLO OFF";
        _statusTimer = 2f;
    }

    private static string ChannelName(int ch) => ch < 3 ? $"PSG{ch + 1}" : $"SCC{ch - 2}";

    /// <summary>
    /// Whether a channel is actually audible right now: not muted, its chip is on and it passes the
    /// solo filter. The piano, the roll and the channel strips all use this so the screen matches
    /// what is heard.
    /// </summary>
    private bool IsAudible(int channel)
    {
        if (_player is null)
            return false;

        if (_player.ChannelMuted[channel])
            return false;

        bool chipOn = channel < 3 ? _player.PsgEnabled : _player.SccEnabled;
        if (!chipOn)
            return false;

        return _player.SoloChannel is not int solo || solo == channel;
    }

    private void DrawChannels()
    {
        const int x = 336, y = 436, w = VW - 348, h = 222;
        Panel(x, y, w, h);
        Text("CHANNELS", x + 10, y + 6, Accent);
        Text("COLOUR = CHANNEL   CLICK LABEL = SOLO   S = SOLO   M = MUTE", x + 96, y + 6, Dim);

        int stripW = (w - 20) / 8;
        int stripY = y + 24;
        int stripH = h - 34;

        for (int ch = 0; ch < 8; ch++)
        {
            var info = _player!.Channels[ch];
            bool isPsg = ch < 3;
            bool muted = _player.ChannelMuted[ch];
            bool soloed = _player.SoloChannel == ch;
            bool audible = IsAudible(ch);

            // The strip is painted with the same colour the channel uses in the roll and the piano.
            var color = ChannelColors[ch];
            if (!audible) color = Dim;

            int sx = x + 10 + ch * stripW;
            _stripHit[ch] = new Rectangle(sx + 2, stripY, stripW - 4, 20);

            Panel(sx + 2, stripY, stripW - 4, stripH, PanelHi);
            Rect(sx + 4, stripY + 19, stripW - 8, 2, color);

            // Colour key: filled square + name in the channel colour.
            Rect(sx + 8, stripY + 6, 6, 6, color);
            Text(isPsg ? $"PSG{info.Number}" : $"SCC{info.Number}", sx + 18, stripY + 6, color);

            // The automatic mix says what it made of the channel (TUNE, BASS, ARP, PAD, DRUM).
            if (_player.AutoMixPlan is { } plan)
            {
                string tag = AutoMixPlan.RoleTag(plan.Channels[ch].Role);
                if (tag.Length > 0)
                    Text(tag, sx + 18 + 28, stripY + 6, audible ? TextColor : Dim);
            }

            var soloRect = new Rectangle(sx + stripW - 38, stripY + 4, 14, 12);
            var muteRect = new Rectangle(sx + stripW - 20, stripY + 4, 14, 12);
            _soloHit[ch] = soloRect;
            _muteHit[ch] = muteRect;

            DrawButton(soloRect, "S", soloed, Accent);
            DrawButton(muteRect, "M", muted, Warn);

            // Live mini scope (min/max envelope per column).
            int scopeX = sx + 8, scopeY = stripY + 24, scopeW = stripW - 16, scopeH = 46;
            Rect(scopeX, scopeY + scopeH / 2, scopeW, 1, Grid);

            var ring = _player.ChannelScope(ch);
            int ringPos = _player.ChannelScopePos;
            int ringLen = ring.Length;

            int usable = Math.Min(ringLen, scopeW * 8);
            for (int col = 0; col < scopeW; col++)
            {
                int i0 = col * usable / scopeW;
                int i1 = (col + 1) * usable / scopeW;
                if (i1 <= i0) i1 = i0 + 1;

                float min = 1f, max = -1f;
                for (int i = i0; i < i1 && i < ringLen; i++)
                {
                    float v = ring[(ringPos + ringLen - usable + i) % ringLen];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }

                int y0 = scopeY + scopeH / 2 - (int)(max * scopeH * 1.2f);
                int y1 = scopeY + scopeH / 2 - (int)(min * scopeH * 1.2f);
                y0 = Math.Clamp(y0, scopeY, scopeY + scopeH - 1);
                y1 = Math.Clamp(y1, scopeY, scopeY + scopeH - 1);

                Rect(scopeX + col, Math.Min(y0, y1), 1, Math.Max(1, Math.Abs(y1 - y0)), color);
            }

            // SCC wave RAM (32 samples).
            if (!isPsg)
            {
                int waveY = stripY + 74, waveH = 20;
                Rect(scopeX, waveY + waveH / 2, scopeW, 1, Grid);
                _player.CopySccWave(ch - 3, _wave);

                var waveColor = Color.Lerp(color, Color.White, 0.3f);
                int stepX = Math.Max(1, scopeW / 32);
                for (int i = 0; i < 32; i++)
                {
                    int px = scopeX + i * stepX;
                    int py = waveY + waveH / 2 - (int)(_wave[i] * waveH / 2);
                    py = Math.Clamp(py, waveY, waveY + waveH - 1);
                    Rect(px, Math.Min(py, waveY + waveH / 2), Math.Max(1, stepX - 1),
                        Math.Max(1, Math.Abs(py - (waveY + waveH / 2))), waveColor);
                }
            }

            // Volume bar.
            int volX = sx + stripW - 16, volY = stripY + 24, volH = 70;
            Rect(volX, volY, 6, volH, Grid);
            int vol = (int)(info.Volume * volH);
            Rect(volX, volY + volH - vol, 6, vol, color);

            // Level bar + peak hold.
            int levelY = stripY + 104, levelW = stripW - 16;
            Rect(scopeX, levelY, levelW, 6, Grid);
            Rect(scopeX, levelY, (int)(Math.Clamp(info.Level * 3f, 0, 1) * levelW), 6, color);

            int peakX = scopeX + (int)(Math.Clamp(_peakHold[ch] * 3f, 0, 1) * levelW);
            Rect(Math.Min(peakX, scopeX + levelW - 2), levelY - 2, 2, 10, Accent);

            string freq = info.Frequency >= 1000
                ? $"{info.Frequency / 1000:0.0}K"
                : $"{info.Frequency:0}HZ";
            Text(freq, sx + 8, stripY + 120, info.Key ? TextColor : Dim);
            Text(NoteName(info.Frequency), sx + 8, stripY + 134, info.Key ? Accent : Dim);
            Text(info.Key ? "KEY" : "---", sx + 8, stripY + 148, info.Key ? color : Dim);
        }
    }
}
