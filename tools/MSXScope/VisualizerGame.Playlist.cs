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

namespace Wagenheimer.MsxAudio.Scope;

public sealed partial class VisualizerGame : Game
{
    private enum TrackFilter { All, Music, Sfx }
    private TrackFilter _trackFilter;
    private readonly Rectangle[] _filterHit = new Rectangle[3];
    private Rectangle _clearFilterHit;
    private Rectangle _emptyOpenHit;
    private bool MatchesTrackFilter(VgmEntry entry) => _trackFilter switch
    {
        TrackFilter.Music => entry.Music == true,
        TrackFilter.Sfx => entry.Music == false,
        _ => true,
    };

    private void CycleTrackFilter()
    {
        SetTrackFilter((TrackFilter)(((int)_trackFilter + 1) % 3));
    }

    private void SetTrackFilter(TrackFilter filter)
    {
        _trackFilter = filter;
        _cursor = 0;
        _rowsDirty = true;
        _playlistFirst = 0;
        EnsureRows();
    }

    private void MoveTrack(int direction)
    {
        if (_tracks.Count == 0) return;
        for (int step = 1; step <= _tracks.Count; step++)
        {
            int index = ((_selected + direction * step) % _tracks.Count + _tracks.Count) % _tracks.Count;
            if (MatchesTrackFilter(_tracks[index])) { LoadTrack(index); return; }
        }
    }

    // Folder currently scanned, the running scan task and its progress (worker thread writes it).
    private string _scanRoot = string.Empty;
    private Task<ScanResult?>? _listScan;
    private CancellationTokenSource? _listCts;
    private bool _listScanning;
    private string? _listSelectPath;
    private string? _listSelectSong;          // song id inside the package (many tracks share one file)
    private string? _scanError;
    private volatile int _listScanProgress;
    // Playlist viewport: the first visible row and the scrollbar geometry (design coordinates).
    private int _playlistFirst;
    private int _playlistRows;
    private int _playlistLastSelected = -1;
    private int _cursor;                      // ROW the keyboard is on (the playing track is _selected)

    // What the list shows: pack headers (which fold) and the tracks that are not hidden by one.
    private readonly record struct PlaylistRow(int Track, string? Pack, int Count, bool Collapsed)
    {
        public bool IsHeader => Track < 0;
    }

    private readonly List<PlaylistRow> _rows = new();
    private readonly HashSet<string> _collapsedPacks = new(StringComparer.OrdinalIgnoreCase);
    private bool _rowsDirty = true;
    private Rectangle _collapseAllHit, _expandAllHit;
    private bool _editDemoDone;
    private bool _exportDemoDone;
    private bool _browsePlays;                // play every row the cursor lands on (T)
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

        WatchLibrary(dir, recursive);
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

        Task<ScanResult?> task = _listScan;
        _listScan = null;
        _listScanning = false;

        ScanResult? result = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
        if (result is null)
            return;

        string? playingPath = _selected >= 0 && _selected < _tracks.Count ? _tracks[_selected].Path : null;
        string? playingSong = _selected >= 0 && _selected < _tracks.Count ? _tracks[_selected].SongId : null;
        _tracks.Clear();
        _tracks.AddRange(result.Files);
        if (_listSelectPath is null && playingPath is not null)
        {
            _listSelectPath = playingPath;
            _listSelectSong = playingSong;
        }
        _rowsDirty = true;
        _scanError = result.Error;

        if (_listSelectPath is not null)
        {
            int found = FindTrack(_listSelectPath, _listSelectSong);
            _selected = found >= 0 ? found : Math.Clamp(_selected, 0, Math.Max(0, _tracks.Count - 1));

            _listSelectPath = null;
            _listSelectSong = null;
        }
        else
        {
            _selected = Math.Clamp(_selected, 0, Math.Max(0, _tracks.Count - 1));
        }

        if (_player is null && _tracks.Count > 0)
            LoadTrack(_selected);

        // Docs/CI: start with the packs folded, or with an edit dialog open (screenshots only).
        if (Environment.GetEnvironmentVariable("MSXAUDIO_COLLAPSE") == "1")
            SetAllPacks(true);

        // Docs/CI: open the export dialog (and run it) at startup: MSXAUDIO_EXPORT=wav,mp3,mid [MSXAUDIO_EXPORT_DIR].
        if (Environment.GetEnvironmentVariable("MSXAUDIO_EXPORT") is { Length: > 0 } formats && !_exportDemoDone &&
            _tracks.Count > 0 && _player is not null)
        {
            _exportDemoDone = true;
            OpenExportDialog();
            _expWav = formats.Contains("wav", StringComparison.OrdinalIgnoreCase);
            _expMp3 = formats.Contains("mp3", StringComparison.OrdinalIgnoreCase);
            _expMidi = formats.Contains("mid", StringComparison.OrdinalIgnoreCase);

            if (Environment.GetEnvironmentVariable("MSXAUDIO_EXPORT_DIR") is { Length: > 0 } dir)
            {
                _expFolder = dir;
                _expFolderAuto = false;
            }

            if (Environment.GetEnvironmentVariable("MSXAUDIO_EXPORT_RUN") == "1")
                StartExport();
        }

        if (Environment.GetEnvironmentVariable("MSXAUDIO_EDIT") is { Length: > 0 } edit && _tracks.Count > 0 && !_editDemoDone)
        {
            _editDemoDone = true;
            EnsureRows();
            int row = Math.Min(1, _rows.Count - 1);

            if (edit == "menu")
                OpenRowMenu(row, new Point(120, 160));
            else
                BeginEdit(row, edit == "delete" ? EditMode.Delete : EditMode.Rename);
        }

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
        LoadFile(entry.Path, rescan: false, songId: entry.SongId, displayName: entry.Name);
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
                int found = FindTrack(path, songId);
                if (found >= 0)
                    _selected = found;
                else if (_listScanning)
                {
                    _listSelectPath = path;
                    _listSelectSong = songId;
                }
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
                int found = FindTrack(path, songId);
                if (found >= 0)
                    _selected = found;
                else if (_listScanning)
                {
                    _listSelectPath = path;
                    _listSelectSong = songId;
                }
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

    /// <summary>
    /// Index of a track by file and, for a <c>.msxaudio</c> package, by song: every song of a package
    /// shares the same file, so matching the path alone always lands on the package's first song.
    /// When the song is unknown the first track of the file is used.
    /// </summary>
    private int FindTrack(string path, string? songId)
    {
        if (songId is not null)
        {
            int exact = _tracks.FindIndex(t =>
                string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.SongId, songId, StringComparison.OrdinalIgnoreCase));

            if (exact >= 0)
                return exact;
        }

        return _tracks.FindIndex(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Moves the viewport so the selected row is visible.</summary>
    /// <summary>Rebuilds the visible rows: a header per pack with two or more songs, then its songs unless folded.</summary>
    private void EnsureRows()
    {
        if (!_rowsDirty)
            return;

        _rowsDirty = false;
        _rows.Clear();

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (VgmEntry track in _tracks)
        {
            if (!MatchesTrackFilter(track)) continue;
            if (track.SongId is not null)
                counts[track.Path] = counts.TryGetValue(track.Path, out int n) ? n + 1 : 1;
        }

        string? current = null;

        for (int i = 0; i < _tracks.Count; i++)
        {
            VgmEntry track = _tracks[i];
            if (!MatchesTrackFilter(track)) continue;
            string? pack = track.SongId is not null && counts[track.Path] >= 2 ? track.Path : null;

            if (pack is not null && !string.Equals(pack, current, StringComparison.OrdinalIgnoreCase))
                _rows.Add(new PlaylistRow(-1, pack, counts[pack], _collapsedPacks.Contains(pack)));

            current = pack;

            if (pack is not null && _collapsedPacks.Contains(pack))
                continue;

            _rows.Add(new PlaylistRow(i, pack, 0, false));
        }

        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, _rows.Count - 1));
    }

    /// <summary>The row to put the cursor on for a track: its own row, or its pack's header when folded.</summary>
    private int RowOfTrack(int track)
    {
        EnsureRows();

        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Track == track)
                return i;
        }

        if (track >= 0 && track < _tracks.Count && _tracks[track].SongId is not null)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].IsHeader && string.Equals(_rows[i].Pack, _tracks[track].Path, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
        }

        return 0;
    }

    private int ClampRow(int row) => Math.Clamp(row, 0, Math.Max(0, _rows.Count - 1));

    /// <summary>Folds or unfolds a pack, keeping the cursor on the header that was clicked.</summary>
    private void TogglePack(string pack)
    {
        if (!_collapsedPacks.Remove(pack))
            _collapsedPacks.Add(pack);

        _rowsDirty = true;
        EnsureRows();

        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].IsHeader && string.Equals(_rows[i].Pack, pack, StringComparison.OrdinalIgnoreCase))
            {
                _cursor = i;
                EnsureVisible(i);
                break;
            }
        }
    }

    private void SetAllPacks(bool collapsed)
    {
        _collapsedPacks.Clear();

        if (collapsed)
        {
            foreach (VgmEntry track in _tracks)
            {
                if (track.SongId is not null)
                    _collapsedPacks.Add(track.Path);
            }
        }

        _rowsDirty = true;
        EnsureRows();
        _cursor = RowOfTrack(_selected);
        EnsureVisible(_cursor);
    }

    /// <summary>A row was chosen (click or Enter): a header folds, a track plays.</summary>
    private void ActivateRow(int row)
    {
        EnsureRows();
        if (row < 0 || row >= _rows.Count)
            return;

        _cursor = row;
        PlaylistRow item = _rows[row];

        if (item.IsHeader)
            TogglePack(item.Pack!);
        else
            LoadTrack(item.Track);
    }

    private void EnsureSelectedVisible() => EnsureVisible(RowOfTrack(_selected));

    /// <summary>Scrolls the list just enough to show row <paramref name="index"/>.</summary>
    private void EnsureVisible(int index)
    {
        if (_playlistRows <= 0)
            return;

        if (index < _playlistFirst)
            _playlistFirst = index;
        else if (index >= _playlistFirst + _playlistRows)
            _playlistFirst = index - _playlistRows + 1;

        _playlistFirst = Math.Clamp(_playlistFirst, 0, Math.Max(0, _rows.Count - _playlistRows));
    }

    /// <summary>Scrolls the playlist by <paramref name="delta"/> rows (wheel uses ±3).</summary>
    private void ScrollPlaylist(int delta)
    {
        EnsureRows();
        if (_rows.Count <= _playlistRows)
            return;

        _playlistFirst = Math.Clamp(_playlistFirst + delta, 0, _rows.Count - _playlistRows);
    }

    /// <summary>Centres the scrollbar thumb on the mouse (click or drag).</summary>
    private void ScrollPlaylistTo(int mouseY)
    {
        EnsureRows();
        if (_rows.Count <= _playlistRows || _playlistScrollTrack.Height <= 0)
            return;

        int listH = _playlistScrollTrack.Height;
        int thumbH = Math.Max(18, listH * _playlistRows / _rows.Count);
        int span = Math.Max(1, listH - thumbH);

        float t = Math.Clamp((mouseY - _playlistScrollTrack.Y - thumbH / 2f) / span, 0f, 1f);
        _playlistFirst = (int)Math.Round(t * (_rows.Count - _playlistRows));
    }

    private void DrawPlaylist()
    {
        const int x = LibraryX, y = 52, w = LibraryWidth, h = 614;
        Panel(x, y, w, h);
        Text("LIBRARY", x + 14, y + 12, TextColor, 2);
        _openFolderHit = new Rectangle(x + w - 78, y + 9, 64, 24);
        DrawButton(_openFolderHit, "OPEN", false, Accent);
        int packs = _tracks.Where(t => t.SongId is not null).Select(t => t.Path).Distinct().Count();
        Text($"{packs} PACK(S) / {_tracks.Count} TRACK(S)", x + 14, y + 34, Dim);

        // Always-visible category controls; counts are independent of the selected filter.
        int[] counts = { _tracks.Count, _tracks.Count(t => t.Music == true), _tracks.Count(t => t.Music == false) };
        string[] labels = { "ALL", "MUSIC", "SFX" };
        const int gap = 6;
        int segmentWidth = (w - 28 - gap * 2) / 3;
        Point mouse = MouseDesign();
        for (int i = 0; i < 3; i++)
        {
            var hit = new Rectangle(x + 14 + i * (segmentWidth + gap), y + 54, segmentWidth, 30);
            _filterHit[i] = hit;
            bool selected = (int)_trackFilter == i;
            bool hover = _dialog == DialogKind.None && hit.Contains(mouse);
            Rect(hit, selected ? Accent : hover ? PanelHi : Background);
            Rect(hit.X, hit.Bottom - 1, hit.Width, 1, selected ? Accent : Grid);
            string label = $"{labels[i]} {counts[i]}";
            Text(label, hit.Center.X - PixelFont.Measure(label) / 2, hit.Y + 11, selected ? Background : hover ? TextColor : Dim);
        }

        EnsureRows();
        _collapseAllHit = new Rectangle(x + w - 140, y + 94, 58, 20);
        _expandAllHit = new Rectangle(x + w - 76, y + 94, 62, 20);
        DrawButton(_collapseAllHit, "FOLD", false, Dim);
        DrawButton(_expandAllHit, "EXPAND", false, Dim);
        Text(_trackFilter == TrackFilter.All ? "ALL TRACKS" : _trackFilter == TrackFilter.Music ? "MUSIC" : "SOUND EFFECTS", x + 14, y + 101, Accent);
        Rect(x + 14, y + 122, w - 28, 1, Grid);

        const int rowHeight = 20;
        int listTop = y + 134;
        int listY = y + h - 54;
        int rows = (listY - listTop - 2) / rowHeight;
        int listH = rows * rowHeight;
        _playlistRows = rows;
        _playlistRect = new Rectangle(x, listTop - 6, w, listH + 6);
        int maxFirst = Math.Max(0, _rows.Count - rows);
        _playlistFirst = Math.Clamp(_playlistFirst, 0, maxFirst);
        if (_selected != _playlistLastSelected)
        {
            _playlistLastSelected = _selected;
            _cursor = RowOfTrack(_selected);
            EnsureSelectedVisible();
        }
        _cursor = ClampRow(_cursor);
        int first = _playlistFirst;
        _trackHit.Clear();
        _clearFilterHit = Rectangle.Empty;
        string? playingPack = _selected >= 0 && _selected < _tracks.Count ? _tracks[_selected].Path : null;

        for (int i = 0; i < rows && first + i < _rows.Count; i++)
        {
            int index = first + i;
            int rowY = listTop + i * rowHeight;
            PlaylistRow row = _rows[index];
            bool active = !row.IsHeader && row.Track == _selected;
            bool atCursor = index == _cursor;
            var rowRect = new Rectangle(x + 8, rowY - 6, w - 26, rowHeight);
            bool hover = rowRect.Contains(mouse) && _dialog == DialogKind.None;
            if (row.IsHeader) Rect(rowRect, PanelHi);
            if (active) Rect(rowRect, Color.Lerp(PanelColor, Accent, 0.15f));
            else if (hover) Rect(rowRect, Color.Lerp(PanelColor, PanelHi, 0.7f));
            if (atCursor)
            {
                Rect(rowRect.X, rowRect.Y, rowRect.Width, 1, Accent);
                Rect(rowRect.X, rowRect.Bottom - 1, rowRect.Width, 1, Accent);
            }
            _trackHit.Add((rowRect, index));
            if (row.IsHeader)
            {
                var package = _tracks.First(t => t.Path == row.Pack);
                Color packColor = PackColor(row.Pack);
                Rect(x + 8, rowY - 5, 2, 18, packColor);
                DrawFoldArrow(x + 18, rowY + 3, row.Collapsed, packColor);
                string title = package.Source ?? Path.GetFileNameWithoutExtension(package.Path);
                int suffix = title.LastIndexOf(" [", StringComparison.Ordinal);
                if (suffix >= 0) title = title[..suffix];
                string variant = package.Variant?.ToUpperInvariant() ?? "UNKNOWN";
                variant = FitText(variant, 60);
                string count = row.Count.ToString();
                int badgeWidth = PixelFont.Measure(variant) + 12;
                int badgeX = x + w - 30 - PixelFont.Measure(count) - 10 - badgeWidth;
                Text(FitText(title.ToUpperInvariant(), badgeX - x - 38), x + 30, rowY, TextColor);
                Rect(badgeX, rowY - 3, badgeWidth, 14, Background);
                Text(variant, badgeX + 6, rowY, package.Variant is null ? Dim : Accent);
                Text(count, x + w - 26 - PixelFont.Measure(count), rowY, Dim);
                continue;
            }
            VgmEntry entry = _tracks[row.Track];
            int nameX = x + (row.Pack is null ? 26 : 34);
            if (active) Text(">", x + 14, rowY, Accent);
            Text(FitText(entry.Name.ToUpperInvariant(), w - (nameX - x) - 62), nameX, rowY, active ? Accent : TextColor);
            if (entry.Seconds is double seconds)
            {
                string duration = FormatTime(seconds);
                Text(duration, x + w - 26 - PixelFont.Measure(duration), rowY, active ? Accent : Dim);
            }
        }

        _playlistScrollTrack = new Rectangle(x + w - 12, listTop - 6, 5, listH);
        _playlistScrollThumb = Rectangle.Empty;
        if (_rows.Count > rows)
        {
            Rect(_playlistScrollTrack, Grid);
            int thumbH = Math.Max(20, listH * rows / _rows.Count);
            int thumbY = _playlistScrollTrack.Y + (int)((listH - thumbH) * (_playlistFirst / (float)Math.Max(1, maxFirst)));
            _playlistScrollThumb = new Rectangle(_playlistScrollTrack.X, thumbY, 5, thumbH);
            Rect(_playlistScrollThumb, Dim);
        }
        if (_rows.Count == 0)
        {
            int emptyY = listTop + 34;
            Text(_listScanning ? "SCANNING LIBRARY..." : _scanError is not null ? "LIBRARY NEEDS ATTENTION" : _tracks.Count == 0 ? "YOUR LIBRARY IS EMPTY" : "NO MATCHING TRACKS", x + 20, emptyY, TextColor);
            string hint = _scanError ?? (_tracks.Count == 0 ? "OPEN A FOLDER OR EXTRACT A ROM" : "TRY ANOTHER FILTER TO SEE MORE");
            Text(FitText(hint.ToUpperInvariant(), w - 40), x + 20, emptyY + 22, Dim);
            if (_tracks.Count > 0)
            {
                _clearFilterHit = new Rectangle(x + 20, emptyY + 46, 132, 28);
                DrawButton(_clearFilterHit, "SHOW ALL TRACKS", true, Accent);
            }
        }
        Rect(x + 14, listY, w - 28, 1, Grid);
        Text($"{_tracks.Count(MatchesTrackFilter)} SHOWN / {_tracks.Count} TOTAL", x + 14, listY + 11, Dim);
        Text("F5 FILTER", x + w - 14 - PixelFont.Measure("F5 FILTER"), listY + 11, Dim);
        string folder = Path.GetFileName(_scanRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Text(FitText(folder.ToUpperInvariant(), w - 28), x + 14, listY + 29, Dim);
    }

    /// <summary>A small triangle: pointing right when the pack is folded, down when it is open.</summary>
    private void DrawFoldArrow(int x, int y, bool collapsed, Color color)
    {
        if (collapsed)
        {
            Rect(x, y - 3, 1, 7, color);
            Rect(x + 1, y - 2, 1, 5, color);
            Rect(x + 2, y - 1, 1, 3, color);
            Rect(x + 3, y, 1, 1, color);
        }
        else
        {
            Rect(x - 3, y - 2, 7, 1, color);
            Rect(x - 2, y - 1, 5, 1, color);
            Rect(x - 1, y, 3, 1, color);
            Rect(x, y + 1, 1, 1, color);
        }
    }

    private void DrawEmpty()
    {
        Panel(AnalysisX, 52, AnalysisWidth, 606);
        const int left = AnalysisX + 32;
        Text("READY TO LISTEN", left, 268, Accent, 2);
        Text("OPEN YOUR MUSIC LIBRARY OR EXTRACT AUDIO FROM YOUR ROM.", left, 300, TextColor);
        Text("DROP A .MSXAUDIO PACKAGE, A VGM OR A FOLDER HERE.", left, 320, Dim);
        Text("YOUR PACKAGES WILL APPEAR IN THE LIBRARY ON THE LEFT.", left, 340, Dim);
        _emptyOpenHit = new Rectangle(left, 376, 132, 30);
        DrawButton(_emptyOpenHit, "OPEN FOLDER", true, Accent);
    }
}
