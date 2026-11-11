using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Visualizer;

public sealed partial class VisualizerGame : Game
{
    // Folder currently scanned, the running scan task and its progress (worker thread writes it).
    private string _scanRoot = string.Empty;
    private Task<ScanResult>? _listScan;
    private CancellationTokenSource? _listCts;
    private bool _listScanning;
    private string? _listSelectPath;
    private string? _scanError;
    private volatile int _listScanProgress;
    private Rectangle _openFolderHit;

    /// <summary>
    /// Scans <paramref name="dir"/> in the background (recursive or not) and replaces the playlist
    /// when it finishes. The audio of the current track keeps playing meanwhile.
    /// </summary>
    private void ScanTracks(string dir, bool recursive, string? selectPath = null)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return;

        _listCts?.Cancel();
        _listCts?.Dispose();
        _listCts = new CancellationTokenSource();

        _scanRoot = dir;
        _listSelectPath = selectPath;
        _listScanning = true;
        _listScanProgress = 0;
        _scanError = null;

        CancellationToken token = _listCts.Token;
        _listScan = Task.Run(() =>
        {
            try
            {
                return Library.Scan(dir, recursive, token, count => _listScanProgress = count);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }, token);
    }

    /// <summary>Called from Update: consumes a finished background scan.</summary>
    private void PollScan()
    {
        if (_listScan is null || !_listScan.IsCompleted)
            return;

        Task<ScanResult> task = _listScan;
        _listScan = null;
        _listScanning = false;

        ScanResult? result = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
        if (result is null)
            return;

        _tracks.Clear();
        _tracks.AddRange(result.Files);
        _scanError = result.Error;

        if (_listSelectPath is not null)
        {
            int found = _tracks.FindIndex(t => string.Equals(t.Path, _listSelectPath, StringComparison.OrdinalIgnoreCase));
            if (found >= 0)
                _selected = found;

            _listSelectPath = null;
        }
        else
        {
            _selected = Math.Clamp(_selected, 0, Math.Max(0, _tracks.Count - 1));
        }

        if (_player is null && _tracks.Count > 0)
            LoadTrack(_selected);

        _status = result.Error is null
            ? $"FOUND {_tracks.Count} FILE(S)"
            : "SCAN ERROR: " + result.Error;
        _statusTimer = 3f;
    }

    /// <summary>Opens a folder: remembers it, scans it and (if nothing is loaded) plays the first file.</summary>
    private void SetFolder(string dir)
    {
        if (!Directory.Exists(dir))
            return;

        _settings.LastFolder = dir;
        _settings.AddRecent(dir);
        _settings.Save();

        ScanTracks(dir, _settings.Recursive);
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
        LoadFile(_tracks[index].Path);
    }

    private void LoadFile(string path, bool rescan = true)
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
            if (!string.Equals(_settings.LastFolder, folder, StringComparison.OrdinalIgnoreCase))
            {
                _settings.LastFolder = folder;
                _settings.AddRecent(folder);
                _settings.Save();
            }

            if (!rescan)
            {
                int found = _tracks.FindIndex(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
                if (found >= 0)
                    _selected = found;
                else if (_listScanning)
                    _listSelectPath = path;
            }
            else if (!string.Equals(folder, _scanRoot, StringComparison.OrdinalIgnoreCase))
            {
                _settings.LastFolder = folder;
                _settings.AddRecent(folder);
                _settings.Save();
                ScanTracks(folder, _settings.Recursive, path);
            }
            else
            {
                // Same folder: just move the selection (the scan may still be running).
                int found = _tracks.FindIndex(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
                if (found >= 0)
                    _selected = found;
                else if (_listScanning)
                    _listSelectPath = path;
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

        // Open-folder button in the panel header.
        _openFolderHit = new Rectangle(x + w - 54, y + 3, 44, 15);
        DrawButton(_openFolderHit, "OPEN", false, Accent);

        Rect(x + 10, y + 24, w - 20, 1, Grid);

        int rows = (h - 90) / 18;
        int first = Math.Max(0, Math.Min(_selected - rows / 2, Math.Max(0, _tracks.Count - rows)));

        _trackHit.Clear();

        for (int i = 0; i < rows && first + i < _tracks.Count; i++)
        {
            int index = first + i;
            int rowY = y + 30 + i * 18;
            bool active = index == _selected;
            var entry = _tracks[index];

            if (active)
                Rect(x + 6, rowY - 4, w - 12, 17, PanelHi);

            _trackHit.Add((new Rectangle(x + 4, rowY - 5, w - 8, 18), index));

            string name = entry.Name.ToUpperInvariant();
            int maxName = _settings.Recursive ? 18 : 26;
            if (name.Length > maxName)
                name = name[..maxName];

            Text(name, x + 12, rowY, active ? Accent : TextColor);

            if (_settings.Recursive)
            {
                string folder = Path.GetFileName(entry.Folder).ToUpperInvariant();
                if (folder.Length > 9)
                    folder = folder[..9];
                Text(folder, x + 12 + (maxName + 2) * 6, rowY, Dim);
            }

            if (entry.Seconds is double seconds)
            {
                string length = FormatTime(seconds);
                Text(length, x + w - 12 - PixelFont.Measure(length), rowY, active ? Accent : Dim);
            }
        }

        if (_tracks.Count == 0)
        {
            if (_listScanning)
            {
                Text("SCANNING…", x + 10, y + 40, Accent);
                Text($"{_listScanProgress} FILE(S)", x + 10, y + 56, Dim);
            }
            else if (_scanError is not null)
            {
                Text("SCAN ERROR", x + 10, y + 40, Warn);
                Text(_scanError.ToUpperInvariant(), x + 10, y + 56, Dim);
            }
            else
            {
                Text("NO .VGM FILES", x + 10, y + 40, Warn);
                Text("PRESS F2 OR CLICK OPEN", x + 10, y + 56, Dim);
                Text("OR DROP A FOLDER HERE", x + 10, y + 72, Dim);
            }
        }

        int listY = y + h - 64;
        Rect(x + 10, listY, w - 20, 1, Grid);

        if (_listScanning)
        {
            Text($"SCANNING… {_listScanProgress}", x + 10, listY + 6, Accent);
        }
        else
        {
            string count = $"{_tracks.Count} FILE(S)" + (_settings.Recursive ? "  RECURSIVE" : "");
            Text(count, x + 10, listY + 6, Dim);
        }

        string length2 = _player is null ? "--:--" : FormatTime(_player.TotalSeconds);
        Text($"LENGTH {length2}", x + 10, listY + 20, Dim);

        // Current folder, truncated to fit the panel.
        string folderPath = _scanRoot.ToUpperInvariant();
        if (folderPath.Length > 29)
            folderPath = "…" + folderPath[^28..];
        Text(folderPath, x + 10, listY + 34, Dim);
    }

    private void DrawEmpty()
    {
        Panel(336, 52, VW - 348, 616);
        Text("NO SONG LOADED", 368, 300, Accent, 2);
        Text("PRESS F2 TO OPEN A FOLDER, OR DROP A .VGM / FOLDER INTO THE WINDOW", 368, 330, Dim);
        Text("CLI: MSXAUDIOVISUALIZER <FILE.VGM | FOLDER> [--RECORD OUT.GIF --SECONDS 10]", 368, 350, Dim);

        var open = new Rectangle(368, 380, 96, 18);
        _openFolderHit = open;
        DrawButton(open, "OPEN FOLDER", false, Accent);
    }
}
