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
    private void Rect(int x, int y, int w, int h, Color color) =>
        _batch.Draw(_pixel, new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h)), color);

    private void Rect(Rectangle r, Color color) =>
        _batch.Draw(_pixel, r, color);

    private void Panel(int x, int y, int w, int h, Color? fill = null)
    {
        Rect(x, y, w, h, fill ?? PanelColor);
        Rect(x, y, w, 1, Grid);
        Rect(x, y + h - 1, w, 1, Grid);
        Rect(x, y, 1, h, Grid);
        Rect(x + w - 1, y, 1, h, Grid);
    }

    private void Text(string text, int x, int y, Color color, int scale = 1) =>
        PixelFont.Draw(_batch, _pixel, text, x, y, color, scale);

    private void DrawHeader()
    {
        Rect(0, 0, VW, 46, PanelHi);
        Rect(0, 45, VW, 1, Grid);

        Text("MSX AUDIO VISUALIZER", 16, 12, Accent, 2);
        Text("PSG AY-3-8910 + KONAMI SCC", 16, 30, Dim);

        var p = _player;

        // Profile chip on the right (opens the profile dialog).
        string profileLabel = "PROFILE: " + (p?.Profile.Name ?? _profile.Name).ToUpperInvariant();
        int labelWidth = PixelFont.Measure(profileLabel) + 14;
        int chipX = VW - 16 - labelWidth;
        _profileButtonHit = new Rectangle(chipX, 28, labelWidth, 15);

        if (p is not null)
        {
            string name = Path.GetFileNameWithoutExtension(p.Name).ToUpperInvariant();
            if (name.Length > 26)
                name = name[..26];
            Text(name, 360, 12, TextColor, 2);

            // Current position and total, measured at the same scale it is drawn with.
            string time = $"{FormatTime(p.Time)} / {FormatTime(p.TotalSeconds)}";
            Text(time, VW - 16 - PixelFont.Measure(time, 2), 12, TextColor, 2);

            // Track counter and folder, kept clear of the profile chip.
            string where = _tracks.Count > 0
                ? $"TRACK {_selected + 1}/{_tracks.Count}   {Path.GetFileName(_scanRoot).ToUpperInvariant()}"
                : string.Empty;

            int room = Math.Max(0, (chipX - 370) / 6);
            if (where.Length > room)
                where = room > 1 ? where[..(room - 1)] + "…" : string.Empty;

            if (where.Length > 0)
                Text(where, 360, 30, Dim);
        }

        DrawButton(_profileButtonHit, profileLabel, true, Accent);
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int total = (int)seconds;
        return $"{total / 60:00}:{total % 60:00}";
    }

    private void DrawSeekBar()
    {
        const int x = 336, y = 52, w = VW - 348, h = 44;
        Panel(x, y, w, h);
        Text("POSITION", x + 10, y + 6, Accent);

        int barX = x + 10, barY = y + 24, barW = w - 20, barH = 10;
        _seekBar = new Rectangle(barX, barY - 4, barW, barH + 8);

        Rect(barX, barY, barW, barH, Grid);

        if (_player is null)
            return;

        double total = Math.Max(0.001, _player.TotalSeconds);
        double time = Math.Clamp(_player.Time, 0, total);
        int filled = (int)(barW * time / total);

        Rect(barX, barY, filled, barH, PsgColor);

        // Loop window (start..end) when the VGM exposes it.
        var song = _player.Song;
        if (song.HasLoop)
        {
            int lx = barX + (int)(barW * (song.LoopStartSeconds / total));
            int lw = Math.Max(2, (int)(barW * (song.LoopSeconds / total)));
            Rect(lx, barY - 3, lw, 3, Accent);
        }

        // Marker.
        int mx = barX + filled;
        Rect(mx - 1, barY - 4, 3, barH + 8, Accent);

        // A-B loop markers.
        if (_player.LoopA is double a)
        {
            int ax = barX + (int)(barW * Math.Clamp(a / total, 0, 1));
            Rect(ax - 1, barY - 8, 2, 6, PsgColor);
            Text("A", ax - 2, barY + barH + 2, PsgColor);
        }

        if (_player.LoopB is double b)
        {
            int bx = barX + (int)(barW * Math.Clamp(b / total, 0, 1));
            Rect(bx - 1, barY - 8, 2, 6, PsgColor);
            Text("B", bx - 2, barY + barH + 2, PsgColor);
        }

        string text = $"{FormatTime(time)} / {FormatTime(total)}";
        Text(text, x + w - 10 - PixelFont.Measure(text), y + 6, TextColor);
    }

    private void SetView(PanelView view)
    {
        if (_view == view)
            return;

        _view = view;

        if (_view != PanelView.Piano)
        {
            StopPianoHold();

            if (_demoRunning)
            {
                _demoRunning = false;
                _demoChannel = -1;
                _player?.StopAudition();
            }
        }

        _status = ViewName(_view);
        _statusTimer = 2f;

        _settings.View = _view.ToString().ToLowerInvariant();
        _settings.Save();
    }

    private void CycleView() => SetView(_view switch
    {
        PanelView.Spectrum => PanelView.Roll,
        PanelView.Roll => PanelView.Piano,
        _ => PanelView.Spectrum,
    });

    private static string ViewName(PanelView view) => view switch
    {
        PanelView.Roll => "PIANO ROLL",
        PanelView.Piano => "PIANO",
        _ => "SPECTRUM",
    };

    /// <summary>
    /// Tab strip with the three analyzer views. The active tab is filled and outlined in the accent
    /// colour, so it is obvious that these are tabs and which one is showing.
    /// </summary>
    /// <returns>The x coordinate right after the last tab.</returns>
    private int DrawViewTabs(int x, int y)
    {
        int tx = x;

        for (int i = 0; i < 3; i++)
        {
            var view = (PanelView)i;
            string label = ViewName(view);
            int width = PixelFont.Measure(label) + 14;

            _viewTabHit[i] = new Rectangle(tx, y, width, 18);
            DrawButton(_viewTabHit[i], label, _view == view, Accent);

            tx += width + 2;
        }

        return tx;
    }

    private void DrawButton(Rectangle rect, string label, bool active, Color activeColor)
    {
        Rect(rect, active ? activeColor : Grid);
        Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2, active ? PanelHi : PanelColor);

        int tw = Math.Max(0, PixelFont.Measure(label) - 1);
        Text(label, rect.X + (rect.Width - tw) / 2, rect.Y + (rect.Height - 7) / 2,
            active ? activeColor : Dim);
    }

    // Transport bar (design coordinates, set in DrawFooter).
    private Rectangle _prevHit, _playHit, _nextHit, _loopHit, _volHit, _aHit, _bHit;
    private bool _volumeDragging;

    private void DrawFooter()
    {
        // Kept away from the very bottom edge: a maximised window can have the taskbar over the
        // last pixels.
        Rect(0, VH - 56, VW, 44, PanelHi);
        Rect(0, VH - 56, VW, 1, Grid);

        int y = VH - 46;
        int x = 16;

        _prevHit = new Rectangle(x, y, 26, 18);
        DrawButton(_prevHit, "<<", false, Accent);
        x += 30;

        _playHit = new Rectangle(x, y, 48, 18);
        DrawButton(_playHit, _paused ? "PLAY" : "PAUSE", false, Accent);
        x += 52;

        _nextHit = new Rectangle(x, y, 26, 18);
        DrawButton(_nextHit, ">>", false, Accent);
        x += 38;

        _loopHit = new Rectangle(x, y, 46, 18);
        DrawButton(_loopHit, "LOOP", _loopEnabled, Accent);
        x += 54;

        Text("VOL", x, y + 6, Dim);
        _volHit = new Rectangle(x + 26, y + 6, 96, 8);
        Rect(_volHit, Grid);
        Rect(_volHit.X, _volHit.Y, (int)(_volHit.Width * _volume), _volHit.Height, Accent);
        x += 26 + 96 + 20;

        _aHit = new Rectangle(x, y, 22, 18);
        DrawButton(_aHit, "A", _player?.LoopA is not null, Accent);
        _bHit = new Rectangle(x + 26, y, 22, 18);
        DrawButton(_bHit, "B", _player?.LoopB is not null, Accent);
        x += 26 + 22 + 22;

        var p = _player;
        if (p is not null)
        {
            string state = $"PSG {(p.PsgEnabled ? "ON" : "OFF")}   SCC {(p.SccEnabled ? "ON" : "OFF")}";
            if (p.SoloChannel is int solo)
                state += "   SOLO " + ChannelName(solo);
            if (p.LoopA is double a && p.LoopB is double b)
                state += $"   A-B {FormatTime(a)}-{FormatTime(b)}";

            Text(state, x, y + 6, TextColor);
        }

        const string hint = "H HELP";
        if (_statusTimer > 0)
        {
            int statusWidth = PixelFont.Measure(_status);
            Text(_status, VW - 16 - statusWidth, y + 6, Accent);
        }
        else
        {
            Text(hint, VW - 16 - PixelFont.Measure(hint), y + 6, Dim);
        }
    }

    private void TogglePause()
    {
        if (_sound is null)
            return;

        _paused = !_paused;
        if (_paused)
            _sound.Pause();
        else
            FlushAudio();
    }

    private void PrevTrack()
    {
        if (_tracks.Count > 0)
            LoadTrack((_selected - 1 + _tracks.Count) % _tracks.Count);
    }

    private void NextTrack()
    {
        if (_tracks.Count > 0)
            LoadTrack((_selected + 1) % _tracks.Count);
    }

    private void ToggleLoop()
    {
        _loopEnabled = !_loopEnabled;

        if (_player is not null)
            _player.LoopEnabled = _loopEnabled;

        _settings.Loop = _loopEnabled;
        _settings.Save();

        _status = _loopEnabled ? "LOOP ON" : "LOOP OFF";
        _statusTimer = 2f;
    }

    private void SetVolumeFromX(int mouseX)
    {
        if (_volHit.Width <= 0)
            return;

        _volume = Math.Clamp((mouseX - _volHit.X) / (float)_volHit.Width, 0f, 1f);

        if (_player is not null)
            _player.Volume = _volume;

        _settings.Volume = _volume;
    }

    private void MarkLoopA()
    {
        if (_player is null)
            return;

        if (_player.LoopA is not null && _player.LoopB is not null)
        {
            _player.LoopA = null;
            _player.LoopB = null;
            _status = "A-B CLEARED";
        }
        else
        {
            _player.LoopA = _player.Time;
            if (_player.LoopB is double b && b <= _player.LoopA)
                _player.LoopB = null;

            _status = "A = " + FormatTime(_player.LoopA.Value);
        }

        _statusTimer = 2f;
    }

    private void MarkLoopB()
    {
        if (_player is null)
            return;

        if (_player.LoopA is double a && _player.Time > a)
        {
            _player.LoopB = _player.Time;
            _status = $"A-B {FormatTime(a)}-{FormatTime(_player.LoopB.Value)}";
        }
        else
        {
            _status = "SET A FIRST (B MUST BE AFTER A)";
        }

        _statusTimer = 2f;
    }

    private void DrawHelp()
    {
        const int w = 660, h = 462;
        int x = (VW - w) / 2, y = (VH - h) / 2;
        Panel(x, y, w, h, PanelHi);
        Rect(x, y, w, 24, PanelColor);
        Text("HELP", x + 10, y + 8, Accent);

        string[] lines =
        {
            "TAB / HEADER   SPECTRUM / PIANO ROLL / PIANO",
            "F2 / CTRL+O    OPEN FOLDER (RECURSIVE OPTION)",
            "F5             RESCAN THE CURRENT FOLDER",
            "UP / DOWN      SELECT AND PLAY A TRACK",
            "PAGE UP/DOWN   SCROLL THE TRACK LIST",
            "WHEEL          SCROLL THE TRACK LIST",
            "SPACE          PAUSE / RESUME",
            "LEFT / RIGHT   SEEK -/+ 5 S",
            "CLICK BAR/MAP  SEEK / JUMP ANYWHERE",
            "CLICK LABEL    SOLO THAT CHANNEL",
            "P / S          TOGGLE PSG / SCC",
            "1 2 3          PSG CHANNELS",
            "4 5 6 7 8      SCC CHANNELS",
            "O              MIXING PROFILE (DIALOG)",
            "SHIFT+O        NEXT PROFILE",
            "A / B          A-B LOOP (A AGAIN CLEARS)",
            "L              AUTO NEXT",
            "R              RESTART",
            "H              THIS HELP",
            "ESC            CLOSE DIALOG / QUIT",
            "",
            "FOOTER: PREV / PLAY-PAUSE / NEXT / LOOP / VOLUME",
            "",
            "PIANO: CLICK AND HOLD A KEY TO PLAY THE SELECTED",
            "CHANNEL'S INSTRUMENT (WORKS WHILE PAUSED); RELEASE",
            "TO STOP. DRAG ACROSS THE KEYS TO SLIDE. 'PLAY ALL'",
            "WALKS THE AUDIBLE CHANNELS; '<' / '>' CHANGE OCTAVE.",
            "",
            "DROP A .VGM OR A FOLDER INTO THE WINDOW TO LOAD IT",
            "MSXAUDIOVISUALIZER SONG.VGM --RECORD OUT.GIF --SECONDS 10",
        };

        for (int i = 0; i < lines.Length; i++)
            Text(lines[i], x + 16, y + 36 + i * 14, i >= lines.Length - 2 ? Dim : TextColor);
    }
}
