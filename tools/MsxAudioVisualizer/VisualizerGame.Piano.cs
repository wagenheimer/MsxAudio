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
    /// <summary>MIDI note under a design-space point, or -1 (black keys are tested first).</summary>
    private int KeyAt(Point p)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool black = pass == 0;
            for (int midi = 0; midi < 128; midi++)
            {
                var rect = _pianoKeyRect[midi];
                if (rect.IsEmpty || Notes[midi % 12].Contains('#') != black)
                    continue;

                if (rect.Contains(p))
                    return midi;
            }
        }

        return -1;
    }

    /// <summary>
    /// Note-on: the note keeps sounding while the mouse button is held (drag retunes it), so the
    /// duration is exactly the duration of the click.
    /// </summary>
    private void StartPianoHold(int midi)
    {
        if (_player is null)
            return;

        if (_demoRunning)
        {
            _demoRunning = false;
            _demoChannel = -1;
        }

        _heldKeyMidi = midi;
        _player.AuditionHold(_pianoChannel, midi);
        _auditionTimer = 600f;   // keep the paused feeder alive while the key is held

        _status = $"{ChannelName(_pianoChannel)} {NoteName(FrequencyOf(midi))}";
        _statusTimer = 2f;

        if (_paused && _sound is not null)
        {
            // The song is paused: drop its queued buffers so the note is heard immediately.
            _sound.Stop();
            _sound.Play();
        }
    }

    /// <summary>While dragging with the button down, move the sounding note to the key under the cursor.</summary>
    private void DragPianoHold(Point p)
    {
        int midi = KeyAt(p);
        if (midi < 0 || midi == _heldKeyMidi || _player is null)
            return;

        _heldKeyMidi = midi;
        _player.AuditionHold(_pianoChannel, midi);

        _status = $"{ChannelName(_pianoChannel)} {NoteName(FrequencyOf(midi))}";
        _statusTimer = 2f;
    }

    /// <summary>Note-off on mouse release (the player applies a short fade to avoid a click).</summary>
    private void StopPianoHold()
    {
        if (_heldKeyMidi < 0)
            return;

        _heldKeyMidi = -1;
        _player?.StopAudition();
        _auditionTimer = 0.1f;   // feed the release tail while paused
    }

    /// <summary>
    /// Plays a fixed-length note with <paramref name="channel"/>'s instrument (used by the "play all"
    /// demo). Works while the song is paused too: the audition is rendered alone.
    /// </summary>
    private void PlayPianoFor(int channel, int midi)
    {
        if (_player is null)
            return;

        _player.Audition(channel, midi, 0.85);
        _auditionTimer = 1.05f;

        _status = $"{ChannelName(channel)} {NoteName(FrequencyOf(midi))}";
        _statusTimer = 2f;

        if (_paused && _sound is not null)
        {
            // The song is paused: drop its queued buffers so the audition is heard immediately.
            _sound.Stop();
            _sound.Play();
        }
    }

    private void ShiftPianoOctave(int delta)
    {
        _pianoBaseMidi = Math.Clamp(_pianoBaseMidi + delta * 12, 24, 72);
        _status = "OCTAVE " + (_pianoBaseMidi / 12 - 1);
        _statusTimer = 2f;
    }

    private void ToggleInstrumentDemo()
    {
        if (_demoRunning)
        {
            _demoRunning = false;
            _demoChannel = -1;
            _player?.StopAudition();
            _status = "DEMO STOPPED";
            _statusTimer = 2f;
            return;
        }

        bool any = false;
        for (int ch = 0; ch < 8; ch++)
            any |= IsAudible(ch);

        if (!any)
        {
            _status = "ALL CHANNELS MUTED";
            _statusTimer = 2f;
            return;
        }

        StopPianoHold();

        _demoRunning = true;
        _demoChannel = -1;
        _demoTimer = 0f;
        _status = "PLAYING ALL INSTRUMENTS";
        _statusTimer = 2f;
    }

    /// <summary>
    /// Walks the audible channels, playing each instrument at the note the song is using. Muted or
    /// soloed-out channels are skipped, so the demo matches the mix that is being heard.
    /// </summary>
    private void UpdateInstrumentDemo(float dt)
    {
        if (!_demoRunning || _player is null)
            return;

        _demoTimer -= dt;
        if (_demoTimer > 0)
            return;

        do
        {
            _demoChannel++;
        }
        while (_demoChannel < 8 && !IsAudible(_demoChannel));

        if (_demoChannel >= 8)
        {
            _demoRunning = false;
            _demoChannel = -1;
            return;
        }

        _pianoChannel = _demoChannel;
        PlayPianoFor(_demoChannel, DemoNote(_demoChannel));
        _demoTimer = 0.95f;
    }

    private int DemoNote(int channel)
    {
        if (_player is null)
            return 60;

        var info = _player.Channels[channel];
        int midi = info.Key && info.Frequency >= 20 ? MidiOf(info.Frequency) : 60;
        return Math.Clamp(midi, _pianoBaseMidi, _pianoBaseMidi + 35);
    }

    /// <summary>
    /// Playable three-octave keyboard. Clicking a key auditions the selected channel with the very
    /// instrument the song is using (PSG envelope/noise or SCC wave RAM); the bars at the bottom of
    /// each key show which notes each channel of the song is playing right now.
    /// </summary>
    private void DrawPiano()
    {
        const int x = 336, y = 104, w = VW - 348, h = 326;
        Panel(x, y, w, h);

        Text("PIANO", x + 10, y + 6, Accent);
        Text("CLICK AND HOLD A KEY TO PLAY - DRAG TO SLIDE", x + 74, y + 7, Dim);

        string next = NextViewLabel();
        DrawViewToggle(x + w - PixelFont.Measure(next) - 22, y + 2, next);

        // Channel selector: the key plays the instrument of the selected channel.
        int rowY = y + 24;
        for (int ch = 0; ch < 8; ch++)
        {
            var rect = new Rectangle(x + 10 + ch * 46, rowY, 42, 16);
            _pianoChannelHit[ch] = rect;

            // Audible channels keep their colour; muted / soloed-out ones fade, like the strips.
            var color = IsAudible(ch)
                ? ChannelColors[ch]
                : Color.Lerp(ChannelColors[ch], PanelColor, 0.7f);

            DrawButton(rect, ChannelName(ch), _pianoChannel == ch, color);

            // Always show the channel colour as a swatch, even when the button is not selected.
            Rect(rect.X + 3, rect.Y + 3, 3, rect.Height - 6, color);
        }

        _pianoAllHit = new Rectangle(x + 10 + 8 * 46 + 8, rowY, 80, 16);
        DrawButton(_pianoAllHit, _demoRunning ? "STOP" : "PLAY ALL", _demoRunning, Accent);

        int navX = _pianoAllHit.Right + 16;
        _pianoOctDownHit = new Rectangle(navX, rowY, 20, 16);
        _pianoOctUpHit = new Rectangle(navX + 24, rowY, 20, 16);
        DrawButton(_pianoOctDownHit, "<", false, Accent);
        DrawButton(_pianoOctUpHit, ">", false, Accent);
        Text($"OCT {_pianoBaseMidi / 12 - 1}", navX + 52, rowY + 5, Dim);

        int kx = x + 12, ky = y + 46, kw = w - 24, kh = h - 46 - 40;
        const int whiteCount = 21;                 // 3 octaves
        int whiteW = kw / whiteCount;

        Array.Clear(_pianoKeyRect);

        // Which notes the song is playing right now (bit per channel on the MIDI note). Muted/soloed
        // out channels are skipped, so the keyboard only lights what is actually audible.
        Array.Clear(_keyLit);
        var player = _player!;
        for (int ch = 0; ch < 8; ch++)
        {
            if (!IsAudible(ch))
                continue;

            var info = player.Channels[ch];
            if (!info.Key || info.Frequency < 20)
                continue;

            int midi = MidiOf(info.Frequency);
            if (midi is >= 0 and < 128)
                _keyLit[midi] |= 1 << ch;
        }

        int auditionNote = player.AuditionNote;

        // White keys first, then the black ones on top (drawing order = z order).
        int wi = 0;
        for (int midi = _pianoBaseMidi; midi < _pianoBaseMidi + 36; midi++)
        {
            if (Notes[midi % 12].Contains('#'))
                continue;

            var rect = new Rectangle(kx + wi * whiteW, ky, whiteW - 1, kh);
            _pianoKeyRect[midi] = rect;

            var fill = midi == auditionNote ? new Color(255, 224, 150) : new Color(215, 223, 232);
            Rect(rect, fill);
            Rect(rect.X, rect.Y, 1, rect.Height, new Color(150, 162, 176));
            wi++;
        }

        for (int midi = _pianoBaseMidi; midi < _pianoBaseMidi + 36; midi++)
        {
            if (!Notes[midi % 12].Contains('#'))
                continue;

            int bw = Math.Max(10, (int)(whiteW * 0.62f));

            // Centre the black key on the boundary of the white keys around it.
            int before = WhiteIndexBefore(_pianoBaseMidi, midi);
            var rect = new Rectangle(kx + before * whiteW - bw / 2, ky, bw, (int)(kh * 0.62f));

            _pianoKeyRect[midi] = rect;

            var fill = midi == auditionNote ? new Color(120, 92, 26) : new Color(24, 30, 40);
            Rect(rect, fill);
        }

        // Octave labels on the white C keys, near the top (the bottom is used by the note bands).
        for (int midi = _pianoBaseMidi; midi < _pianoBaseMidi + 36; midi += 12)
        {
            var rect = _pianoKeyRect[midi];
            if (rect.IsEmpty)
                continue;

            Text("C" + (midi / 12 - 1), rect.X + (rect.Width - 12) / 2, rect.Y + 8, new Color(70, 82, 96));
        }

        // Song notes: one solid band per channel, stacked from the bottom of the key. Nothing is
        // mixed: when two channels play the same note, both bands appear (one above the other).
        for (int midi = _pianoBaseMidi; midi < _pianoBaseMidi + 36; midi++)
        {
            int mask = _keyLit[midi];
            if (mask == 0)
                continue;

            var rect = _pianoKeyRect[midi];
            if (rect.IsEmpty)
                continue;

            int bands = 0;
            for (int ch = 0; ch < 8; ch++)
                if ((mask & (1 << ch)) != 0)
                    bands++;

            int bandH = bands > 1 ? 9 : 16;
            int band = 0;

            for (int ch = 0; ch < 8; ch++)
            {
                if ((mask & (1 << ch)) == 0)
                    continue;

                int by = rect.Bottom - 3 - bandH - band * (bandH + 1);
                var color = ChannelColors[ch];

                Rect(rect.X + 2, by, rect.Width - 4, bandH, color);

                // Dark line on top of each band: keeps the stack readable on a white key.
                Rect(rect.X + 2, by, rect.Width - 4, 1, Background);
                band++;
            }
        }

        // Audition note: outline the whole key so the note being played stands out.
        if (auditionNote is >= 0 and < 128)
        {
            var rect = _pianoKeyRect[auditionNote];
            if (!rect.IsEmpty)
            {
                Rect(rect.X, rect.Y, 2, rect.Height, Accent);
                Rect(rect.Right - 2, rect.Y, 2, rect.Height, Accent);
                Rect(rect.X, rect.Y, rect.Width, 4, Accent);
            }
        }

        // Info strip: labelled fields inside a panel of its own, so nothing touches the border.
        var target = player.Channels[_pianoChannel];
        int targetNote = target.Key && target.Frequency >= 20 ? MidiOf(target.Frequency) : -1;
        string note = targetNote >= 0 ? NoteName(target.Frequency) : "--";
        bool audibleTarget = IsAudible(_pianoChannel);
        var channelColor = audibleTarget ? ChannelColors[_pianoChannel] : Dim;

        int infoY = ky + kh + 12;
        Rect(x + 12, infoY - 6, w - 24, 24, PanelHi);
        Rect(x + 12, infoY - 6, 3, 24, channelColor);

        int fx = x + 24;
        fx = DrawField(fx, infoY, "CHANNEL", ChannelName(_pianoChannel), channelColor);
        fx = DrawField(fx, infoY, "NOTE", note, target.Key ? TextColor : Dim);
        fx = DrawField(fx, infoY, "VOL", $"{target.Volume * 100:0}%", TextColor);
        fx = DrawField(fx, infoY, "OCT", (_pianoBaseMidi / 12 - 1).ToString(), TextColor);
        DrawField(fx, infoY, "INSTRUMENT",
            _pianoChannel < 3 ? "PSG ENVELOPE + NOISE" : "SCC WAVE RAM", TextColor);

        // Whatever is stopping the channel from being heard, and where its note is, on the right.
        string status = "";
        if (_player.ChannelMuted[_pianoChannel])
            status = "MUTED";
        else if (_pianoChannel < 3 ? !_player.PsgEnabled : !_player.SccEnabled)
            status = "CHIP OFF";
        else if (_player.SoloChannel is int solo && solo != _pianoChannel)
            status = "SOLOED OUT";

        if (targetNote >= 0 && targetNote < _pianoBaseMidi)
            status = Append(status, "NOTE BELOW - CLICK '<'");
        else if (targetNote > _pianoBaseMidi + 35)
            status = Append(status, "NOTE ABOVE - CLICK '>'");

        if (status.Length > 0)
        {
            int sw = PixelFont.Measure(status);
            Text(status, x + w - 26 - sw, infoY, status.Contains("MUTED") || status.Contains("OFF") || status.Contains("SOLOED") ? Warn : Accent);
        }
    }

    private static string Append(string left, string right) => left.Length == 0 ? right : left + "   " + right;

    /// <summary>Draws "<c>LABEL value</c>" and returns the x where the next field starts.</summary>
    private int DrawField(int x, int y, string label, string value, Color valueColor)
    {
        Text(label, x, y, Dim);

        int valueX = x + PixelFont.Measure(label) + 6;
        Text(value, valueX, y, valueColor);

        return valueX + PixelFont.Measure(value) + 22;
    }

    /// <summary>Index (0-based) of the white key that starts before <paramref name="midi"/>.</summary>
    private static int WhiteIndexBefore(int baseMidi, int midi)
    {
        int index = 0;
        for (int m = baseMidi; m < midi; m++)
            if (!Notes[m % 12].Contains('#'))
                index++;

        return index;
    }

    private static int MidiOf(double frequency) =>
        (int)Math.Round(69 + 12 * Math.Log2(frequency / 440.0));

    private static double FrequencyOf(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

    private static string NoteName(double frequency)
    {
        if (frequency < 20 || double.IsNaN(frequency))
            return "---";

        int midi = (int)Math.Round(69 + 12 * Math.Log2(frequency / 440.0));
        if (midi < 0 || midi > 127) return "---";
        return Notes[midi % 12] + (midi / 12 - 1);
    }
}
