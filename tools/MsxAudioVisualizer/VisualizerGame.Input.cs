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
    private bool Pressed(Keys key) => _keys.IsKeyDown(key) && !_prevKeys.IsKeyDown(key);

    private Keys _repeatKey;
    private long _repeatAt;

    /// <summary>True on the press and then again, repeating, while the key is held (list navigation).</summary>
    private bool PressedRepeat(Keys key)
    {
        if (!_keys.IsKeyDown(key))
        {
            if (_repeatKey == key)
                _repeatKey = Keys.None;

            return false;
        }

        long now = Environment.TickCount64;

        if (!_prevKeys.IsKeyDown(key))
        {
            _repeatKey = key;
            _repeatAt = now + 350;
            return true;
        }

        if (_repeatKey == key && now >= _repeatAt)
        {
            _repeatAt = now + 45;
            return true;
        }

        return false;
    }


    private Point MouseDesign()
    {
        if (_scale <= 0) return Point.Zero;
        return new Point((int)((_mouse.X - _offX) / _scale), (int)((_mouse.Y - _offY) / _scale));
    }

    private void HandleInput()
    {
        // A dialog owns the input until it closes.
        if (_dialog != DialogKind.None)
        {
            HandleDialogInput();
            return;
        }

        bool ctrl = _keys.IsKeyDown(Keys.LeftControl) || _keys.IsKeyDown(Keys.RightControl);
        bool shift = _keys.IsKeyDown(Keys.LeftShift) || _keys.IsKeyDown(Keys.RightShift);

        if (Pressed(Keys.Escape))
            Exit();

        if (Pressed(Keys.F2) || (ctrl && Pressed(Keys.O)))
            OpenFolderDialog();

        if (Pressed(Keys.F3))
            OpenExtractDialog();

        if (Pressed(Keys.E))
            OpenExportDialog();

        if (Pressed(Keys.H))
            _showHelp = !_showHelp;

        if (Pressed(Keys.Tab))
            CycleView();

        if (Pressed(Keys.O))
        {
            // O opens the profile dialog; Shift+O keeps the quick cycle.
            if (shift)
                CycleProfile();
            else
                OpenProfileDialog();
        }

        // The keyboard drives the track list: a cursor moves through its rows (held keys repeat),
        // ENTER plays the track under it (or folds/unfolds a pack header), and on a header LEFT folds
        // and RIGHT unfolds. The playing track and the song position are not touched while browsing,
        // unless "play on move" (T) is on.
        EnsureRows();

        if (_rows.Count > 0)
        {
            int page = Math.Max(1, _playlistRows - 2);
            int move = 0;

            if (PressedRepeat(Keys.Up)) move = -1;
            else if (PressedRepeat(Keys.Down)) move = 1;
            else if (PressedRepeat(Keys.PageUp)) move = -page;
            else if (PressedRepeat(Keys.PageDown)) move = page;

            if (move != 0)
            {
                bool single = Math.Abs(move) == 1;
                _cursor = single
                    ? (ClampRow(_cursor) + move + _rows.Count) % _rows.Count
                    : Math.Clamp(ClampRow(_cursor) + move, 0, _rows.Count - 1);
                EnsureVisible(_cursor);

                if (_browsePlays && !_rows[_cursor].IsHeader)
                    LoadTrack(_rows[_cursor].Track);
            }

            if (Pressed(Keys.Home))
            {
                _cursor = 0;
                EnsureVisible(_cursor);
                if (_browsePlays && !_rows[_cursor].IsHeader) LoadTrack(_rows[_cursor].Track);
            }

            if (Pressed(Keys.End))
            {
                _cursor = _rows.Count - 1;
                EnsureVisible(_cursor);
                if (_browsePlays && !_rows[_cursor].IsHeader) LoadTrack(_rows[_cursor].Track);
            }

            if (Pressed(Keys.Enter))
                ActivateRow(ClampRow(_cursor));
        }

        if (_rows.Count > 0)
        {
            if (Pressed(Keys.F4))
                BeginEdit(ClampRow(_cursor), EditMode.Rename);
            else if (Pressed(Keys.Delete))
                BeginEdit(ClampRow(_cursor), EditMode.Delete);
        }

        if (Pressed(Keys.T))
        {
            _browsePlays = !_browsePlays;
            _status = _browsePlays ? "PLAY ON MOVE: ON" : "PLAY ON MOVE: OFF (ENTER PLAYS)";
            _statusTimer = 2f;
        }

        if (Pressed(Keys.Space))
            TogglePause();

        if (Pressed(Keys.R) && _player is not null)
        {
            _player.Restart();
            ClearNotes();
            FlushAudio();
        }

        PlaylistRow cursorRow = _rows.Count > 0 ? _rows[ClampRow(_cursor)] : default;
        bool onHeader = _rows.Count > 0 && cursorRow.IsHeader;

        if (onHeader && Pressed(Keys.Left))
        {
            if (!cursorRow.Collapsed)
                TogglePack(cursorRow.Pack!);
        }
        else if (onHeader && Pressed(Keys.Right))
        {
            if (cursorRow.Collapsed)
                TogglePack(cursorRow.Pack!);
        }
        else if (Pressed(Keys.Left) && _player is not null)
        {
            _player.Seek(Math.Max(0, _player.Time - 5));
            ClearNotes();
            FlushAudio();
        }

        if (!onHeader && Pressed(Keys.Right) && _player is not null)
        {
            _player.Seek(Math.Min(_player.TotalSeconds, _player.Time + 5));
            ClearNotes();
            FlushAudio();
        }

        if (Pressed(Keys.P))
            ToggleChip(true);

        if (Pressed(Keys.S))
            ToggleChip(false);

        for (int i = 0; i < 8; i++)
        {
            var key = i < 3 ? Keys.D1 + i : Keys.D4 + (i - 3);
            if (Pressed(key) && _player is not null)
                _player.ChannelMuted[i] = !_player.ChannelMuted[i];
        }

        if (Pressed(Keys.L))
            ToggleLoop();

        // Media-player keys: X stops, "," and "." are previous and next song.
        if (Pressed(Keys.X))
            StopPlayback();

        if (Pressed(Keys.OemComma))
            PrevTrack();

        if (Pressed(Keys.OemPeriod))
            NextTrack();

        HandleMouse();
    }

    private void HandleMouse()
    {
        bool down = _mouse.LeftButton == ButtonState.Pressed;
        bool pressed = down && _prevMouse.LeftButton == ButtonState.Released;
        var p = MouseDesign();

        // Right-click a row: rename / delete menu.
        if (_mouse.RightButton == ButtonState.Pressed && _prevMouse.RightButton == ButtonState.Released)
        {
            foreach (var (rect, index) in _trackHit)
            {
                if (!rect.Contains(p))
                    continue;

                OpenRowMenu(index, p);
                return;
            }
        }

        // Volume: click or drag anywhere on the slider (with some vertical slack).
        if (!down && _volumeDragging)
        {
            _volumeDragging = false;
            _settings.Save();
        }

        var volumeArea = new Rectangle(_volHit.X, _volHit.Y - 6, _volHit.Width, _volHit.Height + 12);
        if (down && (_volumeDragging || (pressed && volumeArea.Contains(p))))
        {
            _volumeDragging = true;
            SetVolumeFromX(p.X);
            return;
        }

        // Transport bar: previous / play-pause / next / loop / A-B.
        if (pressed)
        {
            if (_prevHit.Contains(p)) { PrevTrack(); return; }
            if (_playHit.Contains(p)) { TogglePause(); return; }
            if (_nextHit.Contains(p)) { NextTrack(); return; }
            if (_stopHit.Contains(p)) { StopPlayback(); return; }
            if (_endLoopHit.Contains(p)) { SetEndMode(EndMode.Loop); return; }
            if (_endNextHit.Contains(p)) { SetEndMode(EndMode.Next); return; }
            if (_endStopHit.Contains(p)) { SetEndMode(EndMode.Stop); return; }
            if (_aHit.Contains(p)) { MarkLoopA(); return; }
            if (_bHit.Contains(p)) { MarkLoopB(); return; }
            if (_psgHit.Contains(p)) { ToggleChip(true); return; }
            if (_sccHit.Contains(p)) { ToggleChip(false); return; }
        }

        // Playlist: the wheel scrolls without changing the track; the scrollbar is draggable.
        int wheel = _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
        if (wheel != 0 && _playlistRect.Contains(p) && _rows.Count > _playlistRows)
        {
            ScrollPlaylist(wheel > 0 ? -3 : 3);
            return;
        }

        if (_playlistScrollDragging)
        {
            if (!down)
                _playlistScrollDragging = false;
            else
            {
                ScrollPlaylistTo(p.Y);
                return;
            }
        }

        if (down && _playlistScrollThumb != Rectangle.Empty &&
            (new Rectangle(_playlistScrollTrack.X - 2, _playlistScrollTrack.Y, 10,
                _playlistScrollTrack.Height)).Contains(p))
        {
            _playlistScrollDragging = true;
            ScrollPlaylistTo(p.Y);
            return;
        }

        // Piano note held with the mouse: it follows the pointer and stops on release, like a key.
        if (_heldKeyMidi >= 0)
        {
            if (!down)
                StopPianoHold();
            else if (_view == PanelView.Piano && _player is not null)
                DragPianoHold(p);
        }

        // Header/playlist buttons work even before a song is loaded.
        if (pressed)
        {
            if (_profileButtonHit.Contains(p))
            {
                OpenProfileDialog();
                return;
            }

            if (_extractButtonHit.Contains(p))
            {
                OpenExtractDialog();
                return;
            }

            if (_exportButtonHit.Contains(p))
            {
                OpenExportDialog();
                return;
            }

            if (_openFolderHit.Contains(p))
            {
                OpenFolderDialog();
                return;
            }

            if (_collapseAllHit.Contains(p))
            {
                SetAllPacks(true);
                return;
            }

            if (_expandAllHit.Contains(p))
            {
                SetAllPacks(false);
                return;
            }
        }

        // Playlist: click a track to select and play it (works even before a song is loaded).
        if (pressed)
        {
            foreach (var (rect, index) in _trackHit)
            {
                if (!rect.Contains(p))
                    continue;

                ActivateRow(index);
                return;
            }
        }

        if (_player is null)
            return;

        // Seek bar: click or drag.
        if (down && _seekBar.Width > 0 && _seekBar.Contains(p))
        {
            double t = (p.X - _seekBar.Left) / (double)_seekBar.Width * _player.TotalSeconds;
            _player.Seek(Math.Clamp(t, 0, _player.TotalSeconds));
            if (pressed) ClearNotes();
            if (pressed) FlushAudio();
            return;
        }

        // Song map (piano-roll footer): click or drag to jump anywhere in the track.
        if (down && _view == PanelView.Roll && _songMapHit.Width > 0 && _songMapHit.Contains(p))
        {
            double t = (p.X - _songMapHit.Left) / (double)_songMapHit.Width * _player.TotalSeconds;
            _player.Seek(Math.Clamp(t, 0, _player.TotalSeconds));
            if (pressed) ClearNotes();
            if (pressed) FlushAudio();
            return;
        }

        if (!pressed)
            return;

        // View tabs: spectrum / piano roll / piano.
        for (int i = 0; i < 3; i++)
        {
            if (!_viewTabHit[i].Contains(p))
                continue;

            SetView((PanelView)i);
            return;
        }

        // Piano view: channel selectors, "play all", octave and the keys themselves.
        if (_view == PanelView.Piano)
        {
            for (int ch = 0; ch < 8; ch++)
            {
                if (!_pianoChannelHit[ch].Contains(p))
                    continue;

                _pianoChannel = ch;
                _status = "AUDITION " + ChannelName(ch);
                _statusTimer = 2f;
                return;
            }

            if (_pianoAllHit.Contains(p))
            {
                ToggleInstrumentDemo();
                return;
            }

            if (_pianoOctDownHit.Contains(p))
            {
                ShiftPianoOctave(-1);
                return;
            }

            if (_pianoOctUpHit.Contains(p))
            {
                ShiftPianoOctave(1);
                return;
            }

            // Black keys overlap the white ones, so they are tested first.
            int key = KeyAt(p);
            if (key >= 0)
            {
                StartPianoHold(key);
                return;
            }
        }

        // Per-channel S (solo) and M (mute) buttons, then the label (solo too).
        for (int ch = 0; ch < 8; ch++)
        {
            if (_soloHit[ch].Contains(p))
            {
                ToggleSolo(ch);
                return;
            }

            if (_muteHit[ch].Contains(p))
            {
                _player.ChannelMuted[ch] = !_player.ChannelMuted[ch];
                _status = $"{ChannelName(ch)} {(_player.ChannelMuted[ch] ? "MUTED" : "ON")}";
                _statusTimer = 2f;
                return;
            }

            if (_stripHit[ch].Contains(p))
            {
                ToggleSolo(ch);
                return;
            }
        }
    }
}
