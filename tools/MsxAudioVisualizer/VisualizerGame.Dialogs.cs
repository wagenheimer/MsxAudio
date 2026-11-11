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
    private enum DialogKind { None, Folder, Profile }

    private DialogKind _dialog;

    // ── Folder browser ───────────────────────────────────────────────────────────
    private string _browserDir = string.Empty;
    private readonly List<(string Name, string Path)> _browserDirs = new();
    private readonly List<VgmEntry> _browserFiles = new();
    private int _browserDirSel;
    private int _browserFileSel;
    private int _browserDirFirst;
    private int _browserFileFirst;
    private bool _browserRecursive;
    private bool _browserFocusFiles;
    private bool _browserScanning;
    private volatile int _browserFound;
    private string? _browserError;
    private Task<ScanResult>? _browserScan;
    private CancellationTokenSource? _browserCts;
    private int _browserWheel;
    private readonly List<(Rectangle Rect, int Index)> _browserDirHit = new();
    private readonly List<(Rectangle Rect, int Index)> _browserFileHit = new();
    private Rectangle _browserUpHit, _browserRecursiveHit, _browserPlayHit, _browserCancelHit, _browserCloseHit;
    private Rectangle _profileButtonHit;

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
        _browserRecursive = _settings.Recursive;
        _browserFocusFiles = false;
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

        _browserDirs.Clear();
        _browserDirs.AddRange(Library.Subfolders(_browserDir));
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
            _browserScanning = true;
            _browserFound = 0;
            _browserScan = Task.Run(() =>
            {
                try
                {
                    return Library.Scan(root, true, token, n => _browserFound = n);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }, token);

            return;
        }

        _browserScanning = false;
        ScanResult result = Library.Scan(_browserDir, false, CancellationToken.None);
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
    }

    private void HandleFolderDialogInput()
    {
        if (Pressed(Keys.Escape))
        {
            CloseDialog();
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
            if (_browserFocusFiles)
                PlayBrowserFile();
            else if (_browserDirs.Count > 0)
                LoadBrowserFolder(_browserDirs[Math.Clamp(_browserDirSel, 0, _browserDirs.Count - 1)].Path);

            return;
        }

        int wheel = _mouse.ScrollWheelValue - _browserWheel;
        _browserWheel = _mouse.ScrollWheelValue;
        if (wheel != 0)
            ScrollBrowser(wheel > 0 ? -3 : 3);

        bool pressed = _mouse.LeftButton == ButtonState.Pressed &&
                       _prevMouse.LeftButton == ButtonState.Released;
        if (!pressed)
            return;

        Point p = MouseDesign();

        if (_browserCloseHit.Contains(p) || _browserCancelHit.Contains(p))
        {
            CloseDialog();
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
            PlayBrowserFile();
            return;
        }

        foreach (var (rect, index) in _browserDirHit)
        {
            if (!rect.Contains(p))
                continue;

            _browserFocusFiles = false;
            _browserDirSel = index;
            LoadBrowserFolder(_browserDirs[index].Path);
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

        int dirs = _browserDirs.Count;
        if (dirs == 0)
            return;

        _browserDirSel = Math.Clamp(_browserDirSel + delta, 0, dirs - 1);
        KeepFirstVisible(ref _browserDirFirst, _browserDirSel, dirs);
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
            int max = Math.Max(0, _browserDirs.Count - DialogRows);
            _browserDirFirst = Math.Clamp(_browserDirFirst + delta, 0, max);
            _browserDirSel = Math.Clamp(_browserDirSel, _browserDirFirst, _browserDirFirst + DialogRows - 1);
        }
    }

    private void ScrollBrowserIntoView()
    {
        if (_browserFocusFiles)
            KeepFirstVisible(ref _browserFileFirst, _browserFileSel, _browserFiles.Count);
        else
            KeepFirstVisible(ref _browserDirFirst, _browserDirSel, _browserDirs.Count);
    }

    private static void KeepFirstVisible(ref int first, int selected, int count)
    {
        if (selected < first)
            first = selected;
        else if (selected >= first + DialogRows)
            first = selected - DialogRows + 1;

        first = Math.Clamp(first, 0, Math.Max(0, count - DialogRows));
    }

    private void HandleProfileDialogInput()
    {
        if (Pressed(Keys.Escape) || Pressed(Keys.Enter))
        {
            if (Pressed(Keys.Enter))
                ApplyProfile(AudioProfile.All[Math.Clamp(_profileSel, 0, 2)]);

            CloseDialog();
            return;
        }

        if (Pressed(Keys.Up))
            _profileSel = (_profileSel + 2) % 3;

        if (Pressed(Keys.Down))
            _profileSel = (_profileSel + 1) % 3;

        bool pressed = _mouse.LeftButton == ButtonState.Pressed &&
                       _prevMouse.LeftButton == ButtonState.Released;
        if (!pressed)
            return;

        Point p = MouseDesign();

        for (int i = 0; i < 3; i++)
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

        if (_dialog == DialogKind.Folder)
            DrawFolderDialog();
        else
            DrawProfileDialog();
    }

    private void DrawFolderDialog()
    {
        const int dx = 190, dy = 100, dw = 900, dh = 520;
        Panel(dx, dy, dw, dh);
        Rect(dx, dy, dw, 24, PanelColor);
        Text("OPEN FOLDER", dx + 12, dy + 8, Accent);

        _browserCloseHit = new Rectangle(dx + dw - 24, dy + 4, 16, 16);
        DrawButton(_browserCloseHit, "X", false, Warn);

        // Path + up button.
        _browserUpHit = new Rectangle(dx + 12, dy + 30, 24, 16);
        DrawButton(_browserUpHit, "<", false, Accent);

        string path = _browserDir.ToUpperInvariant();
        int pathRoom = (dw - 60) / 6;
        if (path.Length > pathRoom)
            path = "…" + path[^(pathRoom - 1)..];
        Text(path, dx + 44, dy + 33, TextColor);

        int listTop = dy + 60;
        int listH = dh - 60 - 56;
        int leftX = dx + 12, leftW = 280;
        int rightX = dx + 304, rightW = dw - 316;

        Text("FOLDERS", leftX + 4, listTop - 12, _browserFocusFiles ? Dim : Accent);
        Text("*.VGM", rightX + 4, listTop - 12, _browserFocusFiles ? Accent : Dim);

        Rect(leftX, listTop, 1, listH, Grid);
        Rect(rightX - 12, listTop, 1, listH, Grid);

        // Folders.
        _browserDirHit.Clear();
        for (int i = 0; i < DialogRows && _browserDirFirst + i < _browserDirs.Count; i++)
        {
            int index = _browserDirFirst + i;
            int rowY = listTop + i * 18;
            bool active = index == _browserDirSel;

            if (active)
                Rect(leftX + 2, rowY - 3, leftW - 8, 17, PanelHi);

            _browserDirHit.Add((new Rectangle(leftX, rowY - 4, leftW - 4, 18), index));

            string name = _browserDirs[index].Name.ToUpperInvariant();
            if (name.Length > 22) name = name[..22];
            Text(name + "/", leftX + 8, rowY, active && !_browserFocusFiles ? Accent : Dim);
        }

        if (_browserDirs.Count == 0)
            Text("(NO SUBFOLDERS)", leftX + 8, listTop, Dim);

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

            _browserFileHit.Add((new Rectangle(rightX, rowY - 4, rightW - 4, 18), index));

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
            Text(_browserError is null ? "NO .VGM FILES IN THIS FOLDER" : "ERROR: " + _browserError.ToUpperInvariant(),
                rightX + 8, listTop, _browserError is null ? Dim : Warn);
        }

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
            : $"{_browserFiles.Count} FILE(S)   {_browserDirs.Count} FOLDER(S)";
        Text(status, dx + 260, barY + 18, Dim);

        _browserCancelHit = new Rectangle(dx + dw - 92, barY + 12, 76, 18);
        DrawButton(_browserCancelHit, "CANCEL", false, Dim);

        _browserPlayHit = new Rectangle(dx + dw - 176, barY + 12, 76, 18);
        DrawButton(_browserPlayHit, "PLAY", true, Accent);

        Text("ENTER PLAYS · BACKSPACE UP · F5 RESCAN", dx + 12, barY + 34, Dim);
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
