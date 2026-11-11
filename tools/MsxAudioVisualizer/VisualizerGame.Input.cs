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
        if (Pressed(Keys.Escape))
            Exit();

        if (Pressed(Keys.H))
            _showHelp = !_showHelp;

        if (Pressed(Keys.Tab))
            CycleView();

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

        if (Pressed(Keys.Space) && _sound is not null)
        {
            _paused = !_paused;
            if (_paused)
                _sound.Pause();
            else
                FlushAudio();
        }

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

        if (Pressed(Keys.O))
            CycleProfile();

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

        // Piano note held with the mouse: it follows the pointer and stops on release, like a key.
        if (_heldKeyMidi >= 0)
        {
            if (!down)
                StopPianoHold();
            else if (_view == PanelView.Piano && _player is not null)
                DragPianoHold(p);
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

        // Header toggle: cycle spectrum -> piano roll -> piano.
        if (_viewToggleHit.Contains(p))
        {
            CycleView();
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
