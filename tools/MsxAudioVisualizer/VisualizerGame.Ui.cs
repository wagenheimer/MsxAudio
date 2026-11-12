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

        Text("MSX SCOPE", 16, 12, Accent, 2);
        Text("PLAYER & CHIP ANALYSER - PSG AY-3-8910 + KONAMI SCC", 16, 30, Dim);

        var p = _player;

        // Profile chip on the right (opens the profile dialog); "extract" sits just left of it.
        string profileLabel = "PROFILE: " + (p?.Profile.Name ?? _profile.Name).ToUpperInvariant();
        int labelWidth = PixelFont.Measure(profileLabel) + 14;
        int chipX = VW - 16 - labelWidth;
        _profileButtonHit = new Rectangle(chipX, 28, labelWidth, 15);

        const string extractLabel = "EXTRACT FROM ROM";
        int extractWidth = PixelFont.Measure(extractLabel) + 14;
        _extractButtonHit = new Rectangle(chipX - 8 - extractWidth, 28, extractWidth, 15);

        const string exportLabel = "EXPORT AUDIO";
        int exportWidth = PixelFont.Measure(exportLabel) + 14;
        _exportButtonHit = new Rectangle(_extractButtonHit.X - 8 - exportWidth, 28, exportWidth, 15);

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
        DrawButton(_extractButtonHit, extractLabel, false, Accent);
        DrawButton(_exportButtonHit, exportLabel, false, CanExport ? Accent : Dim);
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
    private Rectangle _prevHit, _playHit, _stopHit, _nextHit, _volHit, _aHit, _bHit, _psgHit, _sccHit;
    private Rectangle _endLoopHit, _endNextHit, _endStopHit, _exportButtonHit;

    /// <summary>Stopped (not just paused): the song is back at 0 and waits for play.</summary>
    private bool _stopped;

    private enum EndMode { Loop, Next, Stop }

    /// <summary>What happens when a song ends: repeat it, go to the next one, or stop.</summary>
    private EndMode CurrentEndMode => _loopEnabled ? EndMode.Loop : _autoNext ? EndMode.Next : EndMode.Stop;
    private bool _volumeDragging;

    private void DrawFooter()
    {
        // Kept away from the very bottom edge: a maximised window can have the taskbar over the
        // last pixels.
        Rect(0, VH - 56, VW, 44, PanelHi);
        Rect(0, VH - 56, VW, 1, Grid);

        int y = VH - 46;
        int x = 16;

        // Transport: previous, play/pause, stop, next. The icons are drawn, not typed, so they stay
        // the same size and are readable at any window scale.
        bool loaded = _player is not null;
        bool playing = loaded && !_paused;

        _prevHit = new Rectangle(x, y, 28, 18);
        DrawIconButton(_prevHit, TransportIcon.Previous, false, loaded || _tracks.Count > 0);
        x += 32;

        _playHit = new Rectangle(x, y, 36, 18);
        DrawIconButton(_playHit, playing ? TransportIcon.Pause : TransportIcon.Play, playing, loaded);
        x += 40;

        _stopHit = new Rectangle(x, y, 28, 18);
        DrawIconButton(_stopHit, TransportIcon.Stop, _stopped, loaded);
        x += 32;

        _nextHit = new Rectangle(x, y, 28, 18);
        DrawIconButton(_nextHit, TransportIcon.Next, false, loaded || _tracks.Count > 0);
        x += 44;

        // What happens when a song ends: one choice, always visible (LOOP, NEXT or STOP).
        Text("AT END", x, y + 6, Dim);
        x += 42;

        EndMode mode = CurrentEndMode;
        _endLoopHit = new Rectangle(x, y, 38, 18);
        DrawButton(_endLoopHit, "LOOP", mode == EndMode.Loop, Accent);
        _endNextHit = new Rectangle(x + 40, y, 38, 18);
        DrawButton(_endNextHit, "NEXT", mode == EndMode.Next, Accent);
        _endStopHit = new Rectangle(x + 80, y, 38, 18);
        DrawButton(_endStopHit, "STOP", mode == EndMode.Stop, Accent);
        x += 118 + 22;

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

        // Per-chip toggles: exactly the same as the P / S keys, but visible and clickable.
        _psgHit = new Rectangle(x, y, 34, 18);
        DrawButton(_psgHit, "PSG", _player?.PsgEnabled ?? true, PsgColor);
        _sccHit = new Rectangle(x + 38, y, 34, 18);
        DrawButton(_sccHit, "SCC", _player?.SccEnabled ?? true, SccColor);
        x += 38 + 34 + 22;

        var p = _player;
        if (p is not null)
        {
            string state = p.SoloChannel is int solo ? "SOLO " + ChannelName(solo) : string.Empty;

            if (p.LoopA is double a && p.LoopB is double b)
                state += (state.Length > 0 ? "   " : "") + $"A-B {FormatTime(a)}-{FormatTime(b)}";

            if (state.Length > 0)
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

        _stopped = false;
        _paused = !_paused;
        if (_paused)
            _sound.Pause();
        else
            FlushAudio();
    }

    /// <summary>Stops: back to the start, silent, waiting for play (pause keeps the position).</summary>
    private void StopPlayback()
    {
        if (_player is null)
            return;

        _player.Restart();
        ClearNotes();
        _paused = true;
        _stopped = true;
        _sound?.Pause();
        _status = "STOPPED";
        _statusTimer = 2f;
    }

    /// <summary>Previous: restarts the song when it has played for a few seconds, otherwise goes back one.</summary>
    private void PrevTrack()
    {
        if (_player is not null && _player.Time > 3.0)
        {
            _player.Restart();
            ClearNotes();
            FlushAudio();
            _stopped = false;
            return;
        }

        if (_tracks.Count > 0)
            LoadTrack((_selected - 1 + _tracks.Count) % _tracks.Count);
    }

    private void NextTrack()
    {
        if (_tracks.Count > 0)
            LoadTrack((_selected + 1) % _tracks.Count);
    }

    /// <summary>Sets what happens at the end of a song and remembers it.</summary>
    private void SetEndMode(EndMode mode)
    {
        _loopEnabled = mode == EndMode.Loop;

        if (mode != EndMode.Loop)
            _autoNext = mode == EndMode.Next;

        if (_player is not null)
            _player.LoopEnabled = _loopEnabled;

        _settings.Loop = _loopEnabled;
        _settings.AutoNext = _autoNext;
        _settings.Save();

        _status = mode switch
        {
            EndMode.Loop => "AT END: REPEAT THE SONG",
            EndMode.Next => "AT END: GO TO THE NEXT SONG",
            _ => "AT END: STOP",
        };
        _statusTimer = 2f;
    }

    /// <summary>L key: LOOP, then NEXT, then STOP, then LOOP again.</summary>
    private void ToggleLoop() => SetEndMode(CurrentEndMode switch
    {
        EndMode.Loop => EndMode.Next,
        EndMode.Next => EndMode.Stop,
        _ => EndMode.Loop,
    });

    private enum TransportIcon { Previous, Play, Pause, Stop, Next }

    /// <summary>A button with a drawn icon (a bar and a triangle, two bars, a square...).</summary>
    private void DrawIconButton(Rectangle rect, TransportIcon icon, bool active, bool enabled)
    {
        Color color = !enabled ? Grid : active ? Accent : TextColor;

        Rect(rect, active ? Accent : Grid);
        Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2, active ? PanelHi : PanelColor);

        const int size = 9;
        int x = rect.X + (rect.Width - size) / 2;
        int y = rect.Y + (rect.Height - size) / 2;

        switch (icon)
        {
            case TransportIcon.Play:
                DrawTriangle(x + 1, y, size, pointRight: true, color);
                break;

            case TransportIcon.Pause:
                Rect(x + 1, y, 3, size, color);
                Rect(x + 6, y, 3, size, color);
                break;

            case TransportIcon.Stop:
                Rect(x + 1, y + 1, size - 2, size - 2, color);
                break;

            case TransportIcon.Previous:
                Rect(x, y, 2, size, color);
                DrawTriangle(x + 3, y, size, pointRight: false, color);
                break;

            case TransportIcon.Next:
                DrawTriangle(x, y, size, pointRight: true, color);
                Rect(x + size - 2, y, 2, size, color);
                break;
        }
    }

    /// <summary>A filled triangle <paramref name="height"/> pixels tall, one pixel row at a time.</summary>
    private void DrawTriangle(int x, int y, int height, bool pointRight, Color color)
    {
        int half = height / 2;

        for (int row = 0; row < height; row++)
        {
            int width = half + 1 - Math.Abs(row - half);          // 1 at the ends, half + 1 in the middle
            width = Math.Max(1, width);

            if (pointRight)
                Rect(x, y + row, width, 1, color);
            else
                Rect(x + half + 1 - width, y + row, width, 1, color);
        }
    }

    private void ToggleChip(bool psg)
    {
        if (_player is null)
            return;

        if (psg)
            _player.PsgEnabled = !_player.PsgEnabled;
        else
            _player.SccEnabled = !_player.SccEnabled;

        _status = psg
            ? (_player.PsgEnabled ? "PSG ON" : "PSG OFF")
            : (_player.SccEnabled ? "SCC ON" : "SCC OFF");
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
        const int w = 660, h = 602;
        int x = (VW - w) / 2, y = (VH - h) / 2;
        Panel(x, y, w, h, PanelHi);
        Rect(x, y, w, 24, PanelColor);
        Text("HELP", x + 10, y + 8, Accent);

        string version = "V" + AppVersion.Value;
        Text(version, x + w - 10 - PixelFont.Measure(version), y + 8, Dim);

        string[] lines =
        {
            "TAB / HEADER   SPECTRUM / PIANO ROLL / PIANO",
            "F2 / CTRL+O    OPEN FOLDER (RECURSIVE OPTION)",
            "F3             EXTRACT MUSIC FROM A CARTRIDGE",
            "E              EXPORT AUDIO: WAV / MP3 / MIDI",            "F5             RESCAN THE CURRENT FOLDER",
            "UP / DOWN      MOVE THE TRACK CURSOR (HOLD TO REPEAT)",
            "PAGE UP/DOWN   MOVE THE CURSOR A PAGE · HOME / END",
            "ENTER / CLICK  PLAY THE TRACK / FOLD A PACK (LEFT, RIGHT TOO)",
            "F4 / RIGHT-CLK RENAME A SONG, A FILE OR A PACK",
            "DEL            DELETE (PACK REWRITTEN / RECYCLE BIN)",
            "T              PLAY EVERY ROW THE CURSOR LANDS ON",
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
            "L              AT END: LOOP / NEXT / STOP",
            "X              STOP (BACK TO 0:00, WAITS FOR PLAY)",
            ", AND .         PREVIOUS / NEXT SONG",
            "R              RESTART",
            "H              THIS HELP",
            "ESC            CLOSE DIALOG / QUIT",
            "",
            "FOOTER: PREV / PLAY-PAUSE / NEXT / LOOP / VOLUME / PSG-SCC",
            "",
            "PIANO: CLICK AND HOLD A KEY TO PLAY THE SELECTED",
            "CHANNEL'S INSTRUMENT (WORKS WHILE PAUSED); RELEASE",
            "TO STOP. DRAG ACROSS THE KEYS TO SLIDE. 'PLAY ALL'",
            "WALKS THE AUDIBLE CHANNELS; '<' / '>' CHANGE OCTAVE.",
            "",
            "DROP A .VGM OR A FOLDER INTO THE WINDOW TO LOAD IT",
            "MSXSCOPE SONG.VGM --RECORD OUT.GIF --SECONDS 10",
        };

        for (int i = 0; i < lines.Length; i++)
            Text(lines[i], x + 16, y + 36 + i * 14, i >= lines.Length - 2 ? Dim : TextColor);
    }
}
