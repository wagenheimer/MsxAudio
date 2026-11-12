using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Wagenheimer.MsxAudio.Mixing;

namespace Wagenheimer.MsxAudio.Visualizer;

public sealed partial class VisualizerGame : Game
{
    private enum DialogKind { None, Folder, Profile, Extract }

    private DialogKind _dialog;

    // ── Folder browser ───────────────────────────────────────────────────────────
    private string _browserDir = string.Empty;
    private readonly List<(string Label, string Path, bool Parent)> _browserRows = new();
    private readonly List<VgmEntry> _browserFiles = new();
    private int _browserDirSel;
    private int _browserFileSel;
    private int _browserDirFirst;
    private int _browserFileFirst;
    private int _browserFolderRows = DialogRows;
    private bool _browserRecursive;
    private bool _browserFocusFiles;
    private bool _browserScanning;
    private volatile int _browserFound;
    private string? _browserError;
    private Task<ScanResult>? _browserScan;
    private CancellationTokenSource? _browserCts;
    private int _browserWheel;
    private string _browserPattern = "*.vgm";
    private BrowserPick _browserPick;

    /// <summary>What the folder browser is choosing, if anything (it returns to the extract dialog).</summary>
    private enum BrowserPick { None, Rom, Profile, Folder }
    private readonly List<(Rectangle Rect, int Index)> _browserDirHit = new();
    private readonly List<(Rectangle Rect, int Index)> _browserFileHit = new();
    private readonly List<(Rectangle Rect, string Path)> _browserCrumbHit = new();
    private readonly List<(Rectangle Rect, string Path)> _browserRecentHit = new();
    private Rectangle _browserUpHit, _browserRecursiveHit, _browserPlayHit, _browserCancelHit, _browserCloseHit;
    private Rectangle _profileButtonHit;

    // Scrollbars of the two browser panes (design coordinates, filled while drawing).
    private Rectangle _browserFolderTrack, _browserFileTrack;
    private int _browserScrollDrag;   // 0 = none, 1 = folders, 2 = files

    // ── Profile dialog ───────────────────────────────────────────────────────────
    private int _profileSel;
    private readonly Rectangle[] _profileCardHit = new Rectangle[3];

    private const int DialogRows = 21;   // visible rows in each browser list

    private void OpenFolderDialog()
    {
        string dir = _scanRoot.Length > 0 && Directory.Exists(_scanRoot)
            ? _scanRoot
            : _settings.LastFolder is { } last && Directory.Exists(last)
                ? last
                : Directory.GetCurrentDirectory();

        _dialog = DialogKind.Folder;
        _browserPattern = "*.vgm";
        _browserPick = BrowserPick.None;
        _browserRecursive = _settings.Recursive;
        _browserFocusFiles = false;
        _browserWheel = _mouse.ScrollWheelValue;
        LoadBrowserFolder(dir);
    }

    /// <summary>Folder browser in "pick a cartridge" mode (used by the extraction dialog).</summary>
    private void OpenRomBrowser()
    {
        string dir = _extractRom.Length > 0
            ? Path.GetDirectoryName(_extractRom) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();

        if (!Directory.Exists(dir))
            dir = _settings.LastFolder is { } last && Directory.Exists(last)
                ? last
                : Directory.GetCurrentDirectory();

        _dialog = DialogKind.Folder;
        _browserPattern = "*.rom,*.zip";
        _browserPick = BrowserPick.Rom;
        _browserRecursive = false;
        _browserFocusFiles = true;
        _browserWheel = _mouse.ScrollWheelValue;
        LoadBrowserFolder(dir);
    }

    /// <summary>Folder browser in "pick the destination folder" mode.</summary>
    private void OpenFolderPicker()
    {
        string dir = _extractOutDir.Length > 0 && Directory.Exists(_extractOutDir)
            ? _extractOutDir
            : _extractRom.Length > 0 && Directory.Exists(Path.GetDirectoryName(_extractRom))
                ? Path.GetDirectoryName(_extractRom)!
                : Directory.GetCurrentDirectory();

        _dialog = DialogKind.Folder;
        _browserPattern = "*.";
        _browserPick = BrowserPick.Folder;
        _browserRecursive = false;
        _browserFocusFiles = false;
        _browserWheel = _mouse.ScrollWheelValue;
        LoadBrowserFolder(dir);
    }

    /// <summary>Folder browser in "pick a profile JSON" mode.</summary>
    private void OpenProfileBrowser()
    {
        string dir = _extractProfile.Length > 0
            ? Path.GetDirectoryName(_extractProfile) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();

        if (!Directory.Exists(dir))
            dir = Directory.GetCurrentDirectory();

        _dialog = DialogKind.Folder;
        _browserPattern = "*.json";
        _browserPick = BrowserPick.Profile;
        _browserRecursive = false;
        _browserFocusFiles = true;
        _browserWheel = _mouse.ScrollWheelValue;
        LoadBrowserFolder(dir);
    }

    private void LoadBrowserFolder(string dir)
    {
        try
        {
            _browserDir = Path.GetFullPath(dir);
        }
        catch (Exception)
        {
            return;
        }

        _browserRows.Clear();

        DirectoryInfo? parent = Directory.GetParent(_browserDir);
        if (parent is not null)
            _browserRows.Add(("..", parent.FullName, true));

        foreach (var (name, path) in Library.Subfolders(_browserDir))
            _browserRows.Add((name, path, false));

        _browserDirSel = 0;
        _browserDirFirst = 0;
        _browserFileSel = 0;
        _browserFileFirst = 0;
        _browserError = null;
        RefreshBrowser();
    }

    private void RefreshBrowser()
    {
        _browserCts?.Cancel();
        _browserCts?.Dispose();
        _browserCts = null;
        _browserScan = null;

        if (_browserRecursive)
        {
            _browserCts = new CancellationTokenSource();
            CancellationToken token = _browserCts.Token;
            string root = _browserDir;
            string pattern = _browserPattern;
            _browserScanning = true;
            _browserFound = 0;
            _browserScan = Task.Run(() =>
            {
                try
                {
                    return Library.Scan(root, true, token, n => _browserFound = n, pattern);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }, token);

            return;
        }

        _browserScanning = false;
        ScanResult result = Library.Scan(_browserDir, false, CancellationToken.None, pattern: _browserPattern);
        _browserFiles.Clear();
        _browserFiles.AddRange(result.Files);
        _browserError = result.Error;
        _browserFileSel = 0;
        _browserFileFirst = 0;
    }

    /// <summary>Called from Update while the folder dialog is open: consumes the recursive scan.</summary>
    private void PollDialogScan()
    {
        if (_dialog == DialogKind.Folder && _browserScan is not null && _browserScan.IsCompleted)
        {
            Task<ScanResult> task = _browserScan;
            _browserScan = null;
            _browserScanning = false;

            ScanResult? result = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            if (result is null)
                return;

            _browserFiles.Clear();
            _browserFiles.AddRange(result.Files);
            _browserError = result.Error;
            _browserFileSel = Math.Clamp(_browserFileSel, 0, Math.Max(0, _browserFiles.Count - 1));
            _browserFileFirst = 0;
        }
    }

    private void CloseDialog()
    {
        _browserCts?.Cancel();
        _browserCts?.Dispose();
        _browserCts = null;
        _browserScan = null;
        _browserScanning = false;
        _dialog = DialogKind.None;
    }

    /// <summary>
    /// Leaving the cartridge picker goes back to the extraction dialog; leaving the normal folder
    /// browser closes it.
    /// </summary>
    private void CancelFolderDialog()
    {
        bool backToExtract = _browserPick != BrowserPick.None;
        CloseDialog();

        if (backToExtract)
        {
            _browserPick = BrowserPick.None;
            _dialog = DialogKind.Extract;
        }
    }

    private void BrowserGoUp()
    {
        DirectoryInfo? parent = Directory.GetParent(_browserDir);
        if (parent is not null)
            LoadBrowserFolder(parent.FullName);
    }

    private void PlayBrowserFile()
    {
        if (_browserFiles.Count == 0)
            return;

        VgmEntry entry = _browserFiles[Math.Clamp(_browserFileSel, 0, _browserFiles.Count - 1)];

        // "Pick a cartridge (or a profile)": hand the path back to the extraction dialog.
        if (_browserPick == BrowserPick.Rom)
        {
            PickRom(entry.Path);
            return;
        }

        if (_browserPick == BrowserPick.Profile)
        {
            PickProfile(entry.Path);
            return;
        }

        if (_browserPick == BrowserPick.Folder)
            return;

        _settings.Recursive = _browserRecursive;
        _settings.LastFolder = _browserDir;
        _settings.AddRecent(_browserDir);
        _settings.Save();

        CloseDialog();

        // The playlist shows the browser root; the file itself is loaded right away.
        ScanTracks(_browserDir, _browserRecursive, entry.Path);
        LoadFile(entry.Path, rescan: false);
    }

    // ── Profile dialog ───────────────────────────────────────────────────────────

    private void OpenProfileDialog()
    {
        var profiles = AudioProfile.All;
        _profileSel = Math.Max(0, Array.FindIndex(profiles, p => p.Name == _profile.Name));
        _dialog = DialogKind.Profile;
    }

    private static readonly (string Tag, string Line1, string Line2)[] ProfileDetails =
    {
        ("MONO   4X", "CHANNELS CENTRED   DC CUT 12 HZ", "EXACTLY THE HARDWARE MIX"),
        ("STEREO   4X", "PSG -0.30/+0.30   SCC -0.55/+0.55", "HP 20 HZ   LP 16 KHZ"),
        ("STEREO   8X", "PSG -0.40/+0.40   SCC -0.70/+0.70   REVERB 18%", "HP 25 HZ   LP 17 KHZ   PEAK 0.89"),
    };

    /// <summary>Recreates the player with another mix, keeping position, mute, solo and chip toggles.</summary>
    private void ApplyProfile(AudioProfile next)
    {
        if (_player is null)
        {
            _profile = next;
            _settings.Profile = next.Name;
            _settings.Save();
            return;
        }

        double time = _player.Time;
        var vgm = _player.Song;
        var muted = (bool[])_player.ChannelMuted.Clone();
        int? solo = _player.SoloChannel;
        bool psgOn = _player.PsgEnabled;
        bool sccOn = _player.SccEnabled;
        double? loopA = _player.LoopA;
        double? loopB = _player.LoopB;

        _player.Dispose();
        _player = new VgmPlayer(vgm, loop: true, sampleRate: 48000, oversample: next.Oversample, profile: next);
        _player.Seek(time);

        for (int i = 0; i < 8; i++)
            _player.ChannelMuted[i] = muted[i];

        _player.SoloChannel = solo;
        _player.PsgEnabled = psgOn;
        _player.SccEnabled = sccOn;
        _player.LoopA = loopA;
        _player.LoopB = loopB;
        _player.Volume = _volume;
        _player.LoopEnabled = _loopEnabled;

        _profile = next;
        _settings.Profile = next.Name;
        _settings.Save();

        _heldKeyMidi = -1;
        _auditionTimer = 0f;
        ClearNotes();
        FlushAudio();

        _status = "PROFILE: " + next.Name.ToUpperInvariant();
        _statusTimer = 2f;
    }

    // ── Input ────────────────────────────────────────────────────────────────────

    private void HandleDialogInput()
    {
        if (_dialog == DialogKind.Profile)
            HandleProfileDialogInput();
        else if (_dialog == DialogKind.Folder)
            HandleFolderDialogInput();
        else if (_dialog == DialogKind.Extract)
            HandleExtractDialogInput();
    }

    private void HandleFolderDialogInput()
    {
        if (Pressed(Keys.Escape))
        {
            CancelFolderDialog();
            return;
        }

        if (Pressed(Keys.F5))
        {
            RefreshBrowser();
            return;
        }

        if (Pressed(Keys.Tab))
        {
            _browserFocusFiles = !_browserFocusFiles;
            return;
        }

        if (Pressed(Keys.Back))
        {
            BrowserGoUp();
            return;
        }

        if (Pressed(Keys.Up))
        {
            MoveBrowserSelection(-1);
            return;
        }

        if (Pressed(Keys.Down))
        {
            MoveBrowserSelection(1);
            return;
        }

        if (Pressed(Keys.Enter))
        {
            if (_browserPick == BrowserPick.Folder)
                PickOutFolder(_browserDir);
            else if (_browserFocusFiles)
                PlayBrowserFile();
            else if (_browserRows.Count > 0)
                LoadBrowserFolder(_browserRows[Math.Clamp(_browserDirSel, 0, _browserRows.Count - 1)].Path);

            return;
        }

        int wheel = _mouse.ScrollWheelValue - _browserWheel;
        _browserWheel = _mouse.ScrollWheelValue;
        if (wheel != 0)
            ScrollBrowser(wheel > 0 ? -3 : 3);

        // Scrollbars: drag (or click) the thumb of either pane.
        Point m = MouseDesign();
        bool held = _mouse.LeftButton == ButtonState.Pressed;

        if (!held)
            _browserScrollDrag = 0;

        if (held && _browserScrollDrag != 0)
        {
            if (_browserScrollDrag == 1)
                ScrollBrowserTo(1, m.Y, _browserFolderTrack, _browserFolderRows, _browserRows.Count);
            else
                ScrollBrowserTo(2, m.Y, _browserFileTrack, DialogRows, _browserFiles.Count);

            _browserWheel = _mouse.ScrollWheelValue;
            return;
        }

        if (held && _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released)
        {
            if (_browserFolderTrack.Contains(m) && _browserRows.Count > _browserFolderRows)
            {
                _browserScrollDrag = 1;
                _browserFocusFiles = false;
                ScrollBrowserTo(1, m.Y, _browserFolderTrack, _browserFolderRows, _browserRows.Count);
                return;
            }

            if (_browserFileTrack.Contains(m) && _browserFiles.Count > DialogRows)
            {
                _browserScrollDrag = 2;
                _browserFocusFiles = true;
                ScrollBrowserTo(2, m.Y, _browserFileTrack, DialogRows, _browserFiles.Count);
                return;
            }
        }

        bool pressed = _mouse.LeftButton == ButtonState.Pressed &&
                       _prevMouse.LeftButton == ButtonState.Released;
        if (!pressed)
            return;

        Point p = MouseDesign();

        if (_browserCloseHit.Contains(p) || _browserCancelHit.Contains(p))
        {
            CancelFolderDialog();
            return;
        }

        if (_browserUpHit.Contains(p))
        {
            BrowserGoUp();
            return;
        }

        if (_browserRecursiveHit.Contains(p))
        {
            _browserRecursive = !_browserRecursive;
            RefreshBrowser();
            return;
        }

        if (_browserPlayHit.Contains(p))
        {
            if (_browserPick == BrowserPick.Folder)
                PickOutFolder(_browserDir);
            else
                PlayBrowserFile();

            return;
        }

        foreach (var (rect, path) in _browserCrumbHit)
        {
            if (!rect.Contains(p))
                continue;

            LoadBrowserFolder(path);
            return;
        }

        foreach (var (rect, path) in _browserRecentHit)
        {
            if (!rect.Contains(p))
                continue;

            LoadBrowserFolder(path);
            return;
        }

        foreach (var (rect, index) in _browserDirHit)
        {
            if (!rect.Contains(p))
                continue;

            _browserFocusFiles = false;
            _browserDirSel = index;
            LoadBrowserFolder(_browserRows[index].Path);
            return;
        }

        foreach (var (rect, index) in _browserFileHit)
        {
            if (!rect.Contains(p))
                continue;

            _browserFocusFiles = true;
            _browserFileSel = index;
            PlayBrowserFile();
            return;
        }
    }

    private void MoveBrowserSelection(int delta)
    {
        if (_browserFocusFiles)
        {
            int count = _browserFiles.Count;
            if (count == 0)
                return;

            _browserFileSel = Math.Clamp(_browserFileSel + delta, 0, count - 1);
            ScrollBrowserIntoView();
            return;
        }

        int dirs = _browserRows.Count;
        if (dirs == 0)
            return;

        _browserDirSel = Math.Clamp(_browserDirSel + delta, 0, dirs - 1);
        KeepFirstVisible(ref _browserDirFirst, _browserDirSel, dirs, _browserFolderRows);
    }

    private void ScrollBrowser(int delta)
    {
        if (_browserFocusFiles)
        {
            int max = Math.Max(0, _browserFiles.Count - DialogRows);
            _browserFileFirst = Math.Clamp(_browserFileFirst + delta, 0, max);
            _browserFileSel = Math.Clamp(_browserFileSel, _browserFileFirst, _browserFileFirst + DialogRows - 1);
        }
        else
        {
            int rows = _browserFolderRows;
            int max = Math.Max(0, _browserRows.Count - rows);
            _browserDirFirst = Math.Clamp(_browserDirFirst + delta, 0, max);
            _browserDirSel = Math.Clamp(_browserDirSel, _browserDirFirst, _browserDirFirst + rows - 1);
        }
    }

    private void ScrollBrowserIntoView()
    {
        if (_browserFocusFiles)
            KeepFirstVisible(ref _browserFileFirst, _browserFileSel, _browserFiles.Count, DialogRows);
        else
            KeepFirstVisible(ref _browserDirFirst, _browserDirSel, _browserRows.Count, _browserFolderRows);
    }

    private static void KeepFirstVisible(ref int first, int selected, int count, int rows)
    {
        if (selected < first)
            first = selected;
        else if (selected >= first + rows)
            first = selected - rows + 1;

        first = Math.Clamp(first, 0, Math.Max(0, count - rows));
    }

    /// <summary>Label of the file filter shown in the browser ("ROM / ZIP", "VGM", ...).</summary>
    private string PatternLabel()
    {
        if (_browserPick == BrowserPick.Folder)
            return "FOLDER";

        var parts = new List<string>();
        foreach (string pattern in _browserPattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            parts.Add(pattern.Replace("*", string.Empty).TrimStart('.').ToUpperInvariant());

        return string.Join(" / ", parts);
    }

    /// <summary>
    /// Draws a scrollbar for a list and registers it, so it can be clicked and dragged.
    /// </summary>
    /// <returns>The thumb rectangle (empty when the list fits).</returns>
    private Rectangle DrawBrowserScrollbar(int x, int y, int height, int first, int rows, int count, bool active)
    {
        Rect(x, y, 6, height, Grid);

        if (count <= rows || height <= 8)
            return Rectangle.Empty;

        int thumbHeight = Math.Max(14, height * rows / count);
        int maxFirst = count - rows;
        int thumbY = y + (int)((height - thumbHeight) * (first / (float)maxFirst));

        Rect(x, thumbY, 6, thumbHeight, active ? Accent : Dim);
        return new Rectangle(x - 3, thumbY, 12, thumbHeight);
    }

    /// <summary>Maps a click/drag on a browser scrollbar to the first visible row.</summary>
    private void ScrollBrowserTo(int pane, int mouseY, Rectangle track, int rows, int count)
    {
        if (track.Height <= 0 || count <= rows)
            return;

        float t = Math.Clamp((mouseY - track.Y) / (float)track.Height, 0f, 1f);
        int first = Math.Clamp((int)(t * count) - rows / 2, 0, count - rows);

        if (pane == 1)
            _browserDirFirst = first;
        else
            _browserFileFirst = first;
    }

    private void HandleProfileDialogInput()
    {
        if (Pressed(Keys.Escape) || Pressed(Keys.Enter))
        {
            if (Pressed(Keys.Enter))
                ApplyProfile(AudioProfile.All[Math.Clamp(_profileSel, 0, AudioProfile.All.Length - 1)]);

            CloseDialog();
            return;
        }

        if (Pressed(Keys.Up))
            _profileSel = (_profileSel + AudioProfile.All.Length - 1) % AudioProfile.All.Length;

        if (Pressed(Keys.Down))
            _profileSel = (_profileSel + 1) % AudioProfile.All.Length;

        bool pressed = _mouse.LeftButton == ButtonState.Pressed &&
                       _prevMouse.LeftButton == ButtonState.Released;
        if (!pressed)
            return;

        Point p = MouseDesign();

        for (int i = 0; i < AudioProfile.All.Length; i++)
        {
            if (!_profileCardHit[i].Contains(p))
                continue;

            _profileSel = i;
            ApplyProfile(AudioProfile.All[i]);
            return;
        }

        if (_browserCloseHit.Contains(p))
            CloseDialog();
    }

    // ── Draw ─────────────────────────────────────────────────────────────────────

    private void DrawDialog()
    {
        if (_dialog == DialogKind.None)
            return;

        Rect(0, 0, VW, VH, new Color(0, 0, 0, 165));

        switch (_dialog)
        {
            case DialogKind.Folder:
                DrawFolderDialog();
                break;
            case DialogKind.Profile:
                DrawProfileDialog();
                break;
            case DialogKind.Extract:
                DrawExtractDialog();
                break;
        }
    }

    private void DrawFolderDialog()
    {
        const int dx = 190, dy = 100, dw = 900, dh = 520;
        Panel(dx, dy, dw, dh);
        Rect(dx, dy, dw, 24, PanelColor);
        Text(_browserPick switch
        {
            BrowserPick.Rom => "SELECT A CARTRIDGE (.ROM)",
            BrowserPick.Profile => "SELECT AN EXTRACTION PROFILE (.JSON)",
            BrowserPick.Folder => "SELECT THE OUTPUT FOLDER",
            _ => "OPEN FOLDER",
        }, dx + 12, dy + 8, Accent);

        _browserCloseHit = new Rectangle(dx + dw - 24, dy + 4, 16, 16);
        DrawButton(_browserCloseHit, "X", false, Warn);

        // Path breadcrumb + up button. Every segment is clickable.
        _browserUpHit = new Rectangle(dx + 12, dy + 30, 24, 16);
        DrawButton(_browserUpHit, "<", false, Accent);
        DrawBreadcrumb(dx + 44, dy + 30, dw - 70);

        int listTop = dy + 60;
        int listH = dh - 60 - 56;
        int leftX = dx + 12, leftW = 280;
        int rightX = dx + 304, rightW = dw - 316;

        // Recent folders get a fixed block at the bottom of the left pane (current folder excluded).
        var recents = new List<string>();
        foreach (string recent in _settings.RecentFolders)
        {
            if (recents.Count >= 4)
                break;

            if (string.Equals(recent, _browserDir, StringComparison.OrdinalIgnoreCase))
                continue;

            recents.Add(recent);
        }

        int recentRows = recents.Count;
        int recentH = recentRows > 0 ? recentRows * 18 + 26 : 0;
        int folderH = listH - recentH;
        int folderRows = Math.Max(3, folderH / 18);
        _browserFolderRows = folderRows;

        int subFolders = 0;
        foreach (var row in _browserRows)
            if (!row.Parent)
                subFolders++;

        _browserDirFirst = Math.Clamp(_browserDirFirst, 0, Math.Max(0, _browserRows.Count - folderRows));

        Text("FOLDERS", leftX + 4, listTop - 12, _browserFocusFiles ? Dim : Accent);
        Text($"{PatternLabel()} ({_browserFiles.Count})", rightX + 4, listTop - 12,
            _browserFocusFiles ? Accent : Dim);

        Rect(leftX, listTop, 1, folderH, Grid);
        Rect(rightX - 12, listTop, 1, listH, Grid);

        // Folders (".." first).
        _browserDirHit.Clear();
        for (int i = 0; i < folderRows && _browserDirFirst + i < _browserRows.Count; i++)
        {
            int index = _browserDirFirst + i;
            int rowY = listTop + i * 18;
            bool active = index == _browserDirSel;
            var row = _browserRows[index];

            if (active)
                Rect(leftX + 2, rowY - 3, leftW - 8, 17, PanelHi);

            _browserDirHit.Add((new Rectangle(leftX, rowY - 4, leftW - 12, 18), index));

            if (row.Parent)
            {
                Text("..", leftX + 8, rowY, active && !_browserFocusFiles ? Accent : Dim);
                Text("UP", leftX + 32, rowY, Dim);
                continue;
            }

            string name = row.Label.ToUpperInvariant();
            if (name.Length > 20)
                name = name[..20];

            Text(name + "/", leftX + 8, rowY, active && !_browserFocusFiles ? Accent : Dim);
        }

        if (subFolders == 0)
            Text("(NO SUBFOLDERS)", leftX + 8, listTop + (_browserRows.Count > 0 ? 18 : 0), Dim);

        _browserFolderTrack = new Rectangle(leftX + leftW - 12, listTop, 6, folderH);
        DrawBrowserScrollbar(_browserFolderTrack.X, listTop, folderH, _browserDirFirst, folderRows,
            _browserRows.Count, !_browserFocusFiles);

        // Recent folders (clickable).
        _browserRecentHit.Clear();
        if (recentRows > 0)
        {
            int recentTop = listTop + folderH + 6;
            Rect(leftX, recentTop - 14, leftW - 6, 1, Grid);
            Text("RECENT", leftX + 4, recentTop - 10, Dim);

            for (int i = 0; i < recentRows; i++)
            {
                string path = recents[i];
                int rowY = recentTop + i * 18;

                _browserRecentHit.Add((new Rectangle(leftX, rowY - 4, leftW - 6, 18), path));

                string label = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(label))
                    label = path;

                label = label.ToUpperInvariant();
                if (label.Length > 22)
                    label = label[..22];

                Text(label, leftX + 8, rowY, TextColor);
            }
        }

        // Files (or scan progress).
        _browserFileHit.Clear();
        for (int i = 0; i < DialogRows && _browserFileFirst + i < _browserFiles.Count; i++)
        {
            int index = _browserFileFirst + i;
            int rowY = listTop + i * 18;
            bool active = index == _browserFileSel;
            VgmEntry entry = _browserFiles[index];

            if (active)
                Rect(rightX + 2, rowY - 3, rightW - 8, 17, PanelHi);

            _browserFileHit.Add((new Rectangle(rightX, rowY - 4, rightW - 12, 18), index));

            string name = entry.Name.ToUpperInvariant();
            int nameRoom = _browserRecursive ? 34 : 52;
            if (name.Length > nameRoom) name = name[..nameRoom];

            Text(name, rightX + 8, rowY, active && _browserFocusFiles ? Accent : TextColor);

            if (_browserRecursive)
            {
                string folder = Path.GetFileName(entry.Folder).ToUpperInvariant();
                if (folder.Length > 12) folder = folder[..12];
                Text(folder, rightX + 12 + nameRoom * 6, rowY, Dim);
            }

            if (entry.Seconds is double seconds)
            {
                string length = FormatTime(seconds);
                Text(length, rightX + rightW - 12 - PixelFont.Measure(length), rowY, active ? Accent : Dim);
            }
        }

        if (_browserScanning)
        {
            Text($"SCANNING… {_browserFound} FILE(S)", rightX + 8, listTop, Accent);
        }
        else if (_browserFiles.Count == 0)
        {
            string empty = $"NO {PatternLabel()} FILES IN THIS FOLDER";
            Text(_browserError is null ? empty : "ERROR: " + _browserError.ToUpperInvariant(),
                rightX + 8, listTop, _browserError is null ? Dim : Warn);
        }

        _browserFileTrack = new Rectangle(rightX + rightW - 12, listTop, 6, listH);
        DrawBrowserScrollbar(_browserFileTrack.X, listTop, listH, _browserFileFirst, DialogRows,
            _browserFiles.Count, _browserFocusFiles);

        // Bottom bar.
        int barY = dy + dh - 48;
        Rect(dx + 12, barY, dw - 24, 1, Grid);

        _browserRecursiveHit = new Rectangle(dx + 12, barY + 14, 13, 13);
        Rect(_browserRecursiveHit, Grid);
        if (_browserRecursive)
            Rect(_browserRecursiveHit.X + 3, _browserRecursiveHit.Y + 3, 7, 7, Accent);

        Text("RECURSIVE (SUBFOLDERS)", dx + 34, barY + 18, TextColor);

        string status = _browserScanning
            ? $"SCANNING… {_browserFound}"
            : $"{_browserFiles.Count} FILE(S)   {subFolders} FOLDER(S)";
        Text(status, dx + 260, barY + 18, Dim);

        _browserCancelHit = new Rectangle(dx + dw - 92, barY + 12, 76, 18);
        DrawButton(_browserCancelHit, "CANCEL", false, Dim);

        _browserPlayHit = new Rectangle(dx + dw - 176, barY + 12, 76, 18);
        DrawButton(_browserPlayHit, _browserPick switch
        {
            BrowserPick.None => "PLAY",
            BrowserPick.Folder => "USE FOLDER",
            _ => "USE",
        }, true, Accent);

        Text(_browserPick switch
        {
            BrowserPick.None => "ENTER PLAYS · BACKSPACE UP · F5 RESCAN",
            BrowserPick.Folder => "ENTER USES THIS FOLDER · BACKSPACE UP · F5 RESCAN",
            _ => "ENTER USES THIS FILE · BACKSPACE UP · F5 RESCAN",
        }, dx + 12, barY + 34, Dim);
    }

    /// <summary>
    /// Clickable breadcrumb for the current folder. The last segment is highlighted; when the path
    /// does not fit, the middle is collapsed into "…" and the tail is kept clickable.
    /// </summary>
    private void DrawBreadcrumb(int x, int y, int maxWidth)
    {
        _browserCrumbHit.Clear();

        var segments = new List<(string Label, string Path)>();

        string root = Path.GetPathRoot(_browserDir) ?? string.Empty;
        string rootLabel = root.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        if (rootLabel.Length == 0)
            rootLabel = root.ToUpperInvariant();

        segments.Add((rootLabel, root.Length > 0 ? root : _browserDir));

        string rest = root.Length > 0 ? _browserDir[root.Length..] : _browserDir;
        string accumulated = root;

        foreach (string part in rest.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            accumulated = Path.Combine(accumulated, part);
            segments.Add((part.ToUpperInvariant(), accumulated));
        }

        int WidthOf(string label) => PixelFont.Measure(label) + 12;
        const int separator = 10;

        int total = 0;
        foreach (var segment in segments)
            total += WidthOf(segment.Label) + separator;

        var shown = new List<(string Label, string Path)>();

        if (total <= maxWidth)
        {
            shown.AddRange(segments);
        }
        else
        {
            shown.Add(segments[0]);

            int budget = maxWidth - WidthOf(segments[0].Label) - separator - 26;
            var tail = new List<(string, string)>();
            int used = 0;

            for (int i = segments.Count - 1; i >= 1; i--)
            {
                int w = WidthOf(segments[i].Label) + separator;
                if (used + w > budget)
                    break;

                used += w;
                tail.Insert(0, segments[i]);
            }

            shown.Add(("…", string.Empty));
            shown.AddRange(tail);
        }

        int cx = x;

        for (int i = 0; i < shown.Count; i++)
        {
            var (label, path) = shown[i];
            int width = WidthOf(label);

            if (i > 0)
            {
                Text(">", cx + 2, y + 5, Dim);
                cx += separator;
            }

            bool current = i == shown.Count - 1;
            var rect = new Rectangle(cx, y, width, 16);

            if (path.Length > 0)
            {
                DrawButton(rect, label, current, Accent);
                _browserCrumbHit.Add((rect, path));
            }
            else
            {
                Text(label, cx + 6, y + 5, Dim);
            }

            cx += width;
        }
    }

    private void DrawProfileDialog()
    {
        const int dx = 320, dy = 190, dw = 640, dh = 340;
        Panel(dx, dy, dw, dh);
        Rect(dx, dy, dw, 24, PanelColor);
        Text("MIXING PROFILE", dx + 12, dy + 8, Accent);

        _browserCloseHit = new Rectangle(dx + dw - 24, dy + 4, 16, 16);
        DrawButton(_browserCloseHit, "X", false, Warn);

        var profiles = AudioProfile.All;
        int cardY = dy + 34;

        for (int i = 0; i < profiles.Length; i++)
        {
            var profile = profiles[i];
            var details = ProfileDetails[i];
            var card = new Rectangle(dx + 12, cardY + i * 88, dw - 24, 80);
            _profileCardHit[i] = card;

            bool current = profile.Name == _profile.Name;
            bool selected = i == _profileSel;

            Panel(card.X, card.Y, card.Width, card.Height, selected ? PanelHi : PanelColor);
            if (current)
                Rect(card.X, card.Y, 3, card.Height, Accent);

            // Radio marker.
            var dot = new Rectangle(card.X + 12, card.Y + 12, 10, 10);
            Rect(dot, Grid);
            if (current)
                Rect(dot.X + 3, dot.Y + 3, 4, 4, Accent);

            Text(profile.Name.ToUpperInvariant(), card.X + 32, card.Y + 10, current ? Accent : TextColor);
            Text(details.Tag, card.X + 32 + PixelFont.Measure(profile.Name.ToUpperInvariant()) + 12,
                card.Y + 10, Dim);

            Text(details.Line1, card.X + 32, card.Y + 28, Dim);
            Text(details.Line2, card.X + 32, card.Y + 42, Dim);

            if (current)
                Text("IN USE", card.Right - 12 - PixelFont.Measure("IN USE"), card.Y + 10, Accent);
        }

        Text("ONLY THE MIX CHANGES — THE VGM AND THE CHIP EMULATION ARE THE SAME",
            dx + 14, dy + dh - 44, Dim);
        Text("UP/DOWN SELECT · ENTER APPLY · ESC CLOSE", dx + 14, dy + dh - 28, Dim);
    }
}
