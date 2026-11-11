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
    private void ScanTracks(string dir)
    {
        _tracks.Clear();
        if (!Directory.Exists(dir))
            return;

        _tracks.AddRange(Directory
            .EnumerateFiles(dir, "*.vgm", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Freezes a mute/solo state at startup, for docs and screenshots:
    /// <c>MSXAUDIO_MUTE=0,3</c> and <c>MSXAUDIO_SOLO=4</c> (channel indexes 0..7).
    /// </summary>
    private void ApplyStartupMix(VgmPlayer player)
    {
        var mute = Environment.GetEnvironmentVariable("MSXAUDIO_MUTE");
        if (!string.IsNullOrWhiteSpace(mute))
        {
            foreach (var part in mute.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(part, out int ch) && ch is >= 0 and < 8)
                    player.ChannelMuted[ch] = true;
        }

        var solo = Environment.GetEnvironmentVariable("MSXAUDIO_SOLO");
        if (!string.IsNullOrWhiteSpace(solo) &&
            int.TryParse(solo.Trim(), out int soloChannel) &&
            soloChannel is >= 0 and < 8)
        {
            player.SoloChannel = soloChannel;
            _pianoChannel = soloChannel;
        }
    }

    private void LoadTrack(int index)
    {
        if (index < 0 || index >= _tracks.Count)
            return;

        _selected = index;
        LoadFile(_tracks[index]);
    }

    private void LoadFile(string path)
    {
        try
        {
            var vgm = VgmFile.Load(path);
            _player?.Dispose();
            _sound?.Dispose();

            _player = new VgmPlayer(vgm, loop: true, sampleRate: 48000, oversample: _profile.Oversample, profile: _profile);
            ApplyStartupMix(_player);
            _songMap = SongMap.Build(vgm, SongMapBuckets);
            _heldKeyMidi = -1;
            _auditionTimer = 0f;

            if (!_recording)
            {
                _sound = new DynamicSoundEffectInstance(48000, AudioChannels.Stereo);
                _sound.Play();
            }

            _paused = false;

            var folder = Path.GetDirectoryName(path) ?? ".";
            if (!string.Equals(folder, _startDir, StringComparison.OrdinalIgnoreCase))
            {
                ScanTracks(folder);
                int found = _tracks.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (found >= 0) _selected = found;
            }

            Console.WriteLine($"[Visualizer] loaded {path} events={vgm.Events.Count} len={vgm.TotalSeconds:0.0}s");
            _status = "LOADED " + Path.GetFileName(path);
            ClearNotes();
            _statusTimer = 3f;
            Array.Clear(_bars);
            Array.Clear(_barPeak);
            Array.Clear(_peakHold);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Visualizer] erro: {ex}");
            _status = "ERROR: " + ex.Message;
            _statusTimer = 6f;
        }
    }

    private void DrawPlaylist()
    {
        const int x = 12, y = 52, w = 316, h = 614;
        Panel(x, y, w, h);

        Text("TRACKS", x + 10, y + 8, Accent);
        Rect(x + 10, y + 24, w - 20, 1, Grid);

        int rows = (h - 90) / 18;
        int first = Math.Max(0, Math.Min(_selected - rows / 2, Math.Max(0, _tracks.Count - rows)));

        _trackHit.Clear();

        for (int i = 0; i < rows && first + i < _tracks.Count; i++)
        {
            int index = first + i;
            int rowY = y + 30 + i * 18;
            bool active = index == _selected;

            if (active)
                Rect(x + 6, rowY - 4, w - 12, 17, PanelHi);

            _trackHit.Add((new Rectangle(x + 4, rowY - 5, w - 8, 18), index));

            string name = Path.GetFileNameWithoutExtension(_tracks[index]).ToUpperInvariant();
            if (name.Length > 30) name = name[..30];
            Text(name, x + 12, rowY, active ? Accent : Dim);
        }

        if (_tracks.Count == 0)
        {
            Text("NO .VGM FILES", x + 10, y + 40, Warn);
            Text("DROP A .VGM HERE", x + 10, y + 56, Dim);
        }

        int listY = y + h - 46;
        Rect(x + 10, listY, w - 20, 1, Grid);
        string length = _player is null ? "--:--" : FormatTime(_player.TotalSeconds);
        Text($"LENGTH {length}", x + 10, listY + 6, Dim);
        Text($"{_tracks.Count} FILE(S)", x + 10, listY + 20, Dim);
    }

    private void DrawEmpty()
    {
        Panel(336, 52, VW - 348, 616);
        Text("DROP A .VGM FILE OR PASS A FOLDER AS THE FIRST ARGUMENT", 368, 330, Dim);
        Text("MSXAUDIOVISUALIZER <FILE.VGM | FOLDER> [--RECORD OUT.GIF --SECONDS 10]", 368, 350, Dim);
    }
}
