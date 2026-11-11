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

        if (Pressed(Keys.Up) && _tracks.Count > 0)
        {
            _selected = (_selected - 1 + _tracks.Count) % _tracks.Count;
            LoadTrack(_selected);
        }

        if (Pressed(Keys.Down) && _tracks.Count > 0)
        {
            _selected = (_selected + 1) % _tracks.Count;
            LoadTrack(_selected);
        }

        if (Pressed(Keys.Enter) && _tracks.Count > 0)
            LoadTrack(_selected);

        // Page keys move the playlist viewport without changing the current track.
        if (Pressed(Keys.PageUp))
            ScrollPlaylist(-Math.Max(1, _playlistRows - 2));

        if (Pressed(Keys.PageDown))
            ScrollPlaylist(Math.Max(1, _playlistRows - 2));

        if (Pressed(Keys.Space))
            TogglePause();

        if (Pressed(Keys.R) && _player is not null)
        {
            _player.Restart();
            ClearNotes();
            FlushAudio();
        }

        if (Pressed(Keys.Left) && _player is not null)
        {
            _player.Seek(Math.Max(0, _player.Time - 5));
            ClearNotes();
            FlushAudio();
        }

        if (Pressed(Keys.Right) && _player is not null)
        {
            _player.Seek(Math.Min(_player.TotalSeconds, _player.Time + 5));
            ClearNotes();
            FlushAudio();
        }

        if (Pressed(Keys.P) && _player is not null)
            _player.PsgEnabled = !_player.PsgEnabled;

        if (Pressed(Keys.S) && _player is not null)
            _player.SccEnabled = !_player.SccEnabled;

        for (int i = 0; i < 8; i++)
        {
            var key = i < 3 ? Keys.D1 + i : Keys.D4 + (i - 3);
            if (Pressed(key) && _player is not null)
                _player.ChannelMuted[i] = !_player.ChannelMuted[i];
        }

        if (Pressed(Keys.L))
        {
            _autoNext = !_autoNext;
            _status = _autoNext ? "AUTO NEXT ON" : "AUTO NEXT OFF";
            _statusTimer = 2f;
        }

        HandleMouse();
    }

    private void HandleMouse()
    {
        bool down = _mouse.LeftButton == ButtonState.Pressed;
        bool pressed = down && _prevMouse.LeftButton == ButtonState.Released;
        var p = MouseDesign();

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
            if (_loopHit.Contains(p)) { ToggleLoop(); return; }
            if (_aHit.Contains(p)) { MarkLoopA(); return; }
            if (_bHit.Contains(p)) { MarkLoopB(); return; }
        }

        // Playlist: the wheel scrolls without changing the track; the scrollbar is draggable.
        int wheel = _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;
        if (wheel != 0 && _playlistRect.Contains(p) && _tracks.Count > _playlistRows)
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

            if (_openFolderHit.Contains(p))
            {
                OpenFolderDialog();
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

                _selected = index;
                LoadTrack(index);
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
