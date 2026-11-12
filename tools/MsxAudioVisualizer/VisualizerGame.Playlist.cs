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
using Wagenheimer.MsxAudio.Songs;
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
    // Playlist viewport: the first visible row and the scrollbar geometry (design coordinates).
    private int _playlistFirst;
    private int _playlistRows;
    private int _playlistLastSelected = -1;
    private Rectangle _playlistRect;
    private Rectangle _playlistScrollTrack;
    private Rectangle _playlistScrollThumb;
    private bool _playlistScrollDragging;
    private Rectangle _openFolderHit;

    // Open .msxaudio packages (one per file), so switching songs inside a package is instant.
    private readonly Dictionary<string, PackageCache> _packages = new(StringComparer.OrdinalIgnoreCase);

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
        _playlistFirst = 0;
        _playlistLastSelected = -1;

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
        VgmEntry entry = _tracks[index];
        LoadFile(entry.Path, songId: entry.SongId, displayName: entry.Name);
    }

    /// <summary>
    /// Loads a VGM from disk, or one song out of a <c>.msxaudio</c> package. Packages are cached, so
    /// switching songs inside the same package does not re-read the file.
    /// </summary>
    private VgmFile LoadVgm(string path, string? songId)
    {
        if (!Path.GetExtension(path).Equals(".msxaudio", StringComparison.OrdinalIgnoreCase))
            return VgmFile.Load(path);

        DateTime stamp = File.GetLastWriteTimeUtc(path);

        if (!_packages.TryGetValue(path, out PackageCache? cache) || cache.Stamp != stamp)
        {
            if (_packages.Remove(path, out PackageCache? previous))
                previous.Library.Dispose();

            cache = new PackageCache(MsxAudioLibrary.Load(path), stamp);
            _packages[path] = cache;
        }

        MsxAudioSong song = cache.Library.Find(songId ?? string.Empty)
                            ?? cache.Library.Songs.Concat(cache.Library.Clips).FirstOrDefault()
                            ?? throw new InvalidDataException("the package has no songs");

        return cache.Library.LoadVgm(song);
    }

    /// <summary>An open package, kept for the playlist (disposed on exit).</summary>
    private sealed record PackageCache(MsxAudioLibrary Library, DateTime Stamp);

    private void LoadFile(string path, bool rescan = true, string? songId = null, string? displayName = null)
    {
        try
        {
            VgmFile vgm = LoadVgm(path, songId);
            _player?.Dispose();
            _sound?.Dispose();

            _player = new VgmPlayer(vgm, loop: true, sampleRate: 48000, oversample: _profile.Oversample, profile: _profile);
            _player.Volume = _volume;
            _player.LoopEnabled = _loopEnabled;
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

            Console.WriteLine($"[MSXScope] loaded {path}{(songId is null ? "" : "#" + songId)} events={vgm.Events.Count} len={vgm.TotalSeconds:0.0}s");
            _status = "LOADED " + (displayName ?? Path.GetFileName(path));
            ClearNotes();
            _statusTimer = 3f;
            Array.Clear(_bars);
            Array.Clear(_barPeak);
            Array.Clear(_peakHold);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MSXScope] erro: {ex}");
            _status = "ERROR: " + ex.Message;
            _statusTimer = 6f;
        }
    }

    /// <summary>Moves the viewport so the selected row is visible.</summary>
    private void EnsureSelectedVisible()
    {
        if (_playlistRows <= 0)
            return;

        if (_selected < _playlistFirst)
            _playlistFirst = _selected;
        else if (_selected >= _playlistFirst + _playlistRows)
            _playlistFirst = _selected - _playlistRows + 1;

        _playlistFirst = Math.Clamp(_playlistFirst, 0, Math.Max(0, _tracks.Count - _playlistRows));
    }

    /// <summary>Scrolls the playlist by <paramref name="delta"/> rows (wheel uses ±3).</summary>
    private void ScrollPlaylist(int delta)
    {
        if (_tracks.Count <= _playlistRows)
            return;

        _playlistFirst = Math.Clamp(_playlistFirst + delta, 0, _tracks.Count - _playlistRows);
    }

    /// <summary>Centres the scrollbar thumb on the mouse (click or drag).</summary>
    private void ScrollPlaylistTo(int mouseY)
    {
        if (_tracks.Count <= _playlistRows || _playlistScrollTrack.Height <= 0)
            return;

        int listH = _playlistScrollTrack.Height;
        int thumbH = Math.Max(18, listH * _playlistRows / _tracks.Count);
        int span = Math.Max(1, listH - thumbH);

        float t = Math.Clamp((mouseY - _playlistScrollTrack.Y - thumbH / 2f) / span, 0f, 1f);
        _playlistFirst = (int)Math.Round(t * (_tracks.Count - _playlistRows));
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
        int listTop = y + 28;
        int listH = rows * 18;

        _playlistRows = rows;
        _playlistRect = new Rectangle(x, y, w, h);

        // Keep the viewport valid; only snap to the selection when it actually changed (otherwise
        // scrolling away from the playing track would be impossible).
        int maxFirst = Math.Max(0, _tracks.Count - rows);
        _playlistFirst = Math.Clamp(_playlistFirst, 0, maxFirst);

        if (_selected != _playlistLastSelected)
        {
            _playlistLastSelected = _selected;
            EnsureSelectedVisible();
        }

        int first = _playlistFirst;

        _trackHit.Clear();

        for (int i = 0; i < rows && first + i < _tracks.Count; i++)
        {
            int index = first + i;
            int rowY = listTop + i * 18;
            bool active = index == _selected;
            var entry = _tracks[index];

            if (active)
                Rect(x + 6, rowY - 4, w - 18, 17, PanelHi);

            _trackHit.Add((new Rectangle(x + 4, rowY - 5, w - 22, 18), index));

            string name = entry.Name.ToUpperInvariant();
            int maxName = _settings.Recursive ? 18 : 24;
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
            else if (entry.SongId is not null)
            {
                // Song from a .msxaudio package: show which package it came from.
                string source = Path.GetFileNameWithoutExtension(entry.Source ?? string.Empty).ToUpperInvariant();
                int room = Math.Max(0, (w - 60) / 6 - maxName - 2);
                if (source.Length > room)
                    source = room > 1 ? source[..(room - 1)] + "…" : string.Empty;

                if (source.Length > 0)
                    Text(source, x + 12 + (maxName + 2) * 6, rowY, Dim);
            }

            if (entry.Seconds is double seconds)
            {
                string length = FormatTime(seconds);
                Text(length, x + w - 20 - PixelFont.Measure(length), rowY, active ? Accent : Dim);
            }
        }

        // Scrollbar (only when the list does not fit).
        _playlistScrollTrack = new Rectangle(x + w - 10, listTop, 6, listH);
        _playlistScrollThumb = Rectangle.Empty;

        if (_tracks.Count > rows)
        {
            Rect(_playlistScrollTrack, Grid);

            int thumbH = Math.Max(18, listH * rows / _tracks.Count);
            int span = Math.Max(1, listH - thumbH);
            int thumbY = listTop + (int)Math.Round(span * (_playlistFirst / (float)Math.Max(1, maxFirst)));

            _playlistScrollThumb = new Rectangle(x + w - 10, thumbY, 6, thumbH);
            Rect(_playlistScrollThumb, new Color(120, 136, 156));
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
