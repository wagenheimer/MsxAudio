using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Wagenheimer.MsxAudio.Songs;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Rename and delete from the track list: a song inside a <c>.msxaudio</c> pack (the package is
/// rewritten), a plain file, or a whole pack. Right-click a row for the menu, or F4 / Delete.
/// </summary>
public sealed partial class VisualizerGame
{
    private enum EditMode { Menu, Rename, Delete }

    private enum EditKind { Song, File, Pack }

    private readonly record struct EditTarget(EditKind Kind, string Path, string? SongId, string Label);

    private EditMode _editMode;
    private EditTarget _editTarget;
    private string _editText = string.Empty;
    private string? _editError;
    private Point _menuAt;
    private Rectangle _menuRenameHit, _menuDeleteHit, _editOkHit, _editCancelHit;

    private const int MaxNameLength = 60;

    // ── Opening ──────────────────────────────────────────────────────────────────

    private EditTarget TargetOf(int row)
    {
        PlaylistRow item = _rows[row];

        if (item.IsHeader)
        {
            return new EditTarget(EditKind.Pack, item.Pack!, null,
                System.IO.Path.GetFileNameWithoutExtension(item.Pack!));
        }

        VgmEntry entry = _tracks[item.Track];
        return entry.SongId is not null
            ? new EditTarget(EditKind.Song, entry.Path, entry.SongId, entry.Name)
            : new EditTarget(EditKind.File, entry.Path, null, entry.Name);
    }

    private void OpenRowMenu(int row, Point at)
    {
        EnsureRows();
        if (row < 0 || row >= _rows.Count)
            return;

        _cursor = row;
        _editTarget = TargetOf(row);
        _editMode = EditMode.Menu;
        _menuAt = at;
        _editError = null;
        _dialog = DialogKind.Edit;
    }

    private void BeginEdit(int row, EditMode mode)
    {
        EnsureRows();
        if (row < 0 || row >= _rows.Count)
            return;

        _cursor = row;
        _editTarget = TargetOf(row);
        StartEditMode(mode);
        _dialog = DialogKind.Edit;
    }

    private void StartEditMode(EditMode mode)
    {
        _editMode = mode;
        _editError = null;
        _editText = mode == EditMode.Rename ? _editTarget.Label : string.Empty;
    }

    private string KindWord => _editTarget.Kind switch
    {
        EditKind.Song => "SONG",
        EditKind.Pack => "PACK FILE",
        _ => "FILE",
    };

    // ── Input ────────────────────────────────────────────────────────────────────

    /// <summary>Text typed while renaming (wired to <c>Window.TextInput</c>).</summary>
    private void OnEditTextInput(char character)
    {
        if (_dialog != DialogKind.Edit || _editMode != EditMode.Rename || char.IsControl(character) ||
            _editText.Length >= MaxNameLength)
            return;

        _editText += character;
    }

    private void HandleEditInput()
    {
        if (Pressed(Keys.Escape))
        {
            _dialog = DialogKind.None;
            return;
        }

        bool pressed = _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
        Point p = MouseDesign();

        switch (_editMode)
        {
            case EditMode.Menu:
                if (!pressed)
                    return;

                if (_menuRenameHit.Contains(p))
                    StartEditMode(EditMode.Rename);
                else if (_menuDeleteHit.Contains(p))
                    StartEditMode(EditMode.Delete);
                else
                    _dialog = DialogKind.None;

                return;

            case EditMode.Rename:
                HandleRenameInput(pressed, p);
                return;

            default:
                HandleDeleteInput(pressed, p);
                return;
        }
    }

    private void HandleRenameInput(bool pressed, Point p)
    {
        bool ctrl = _keys.IsKeyDown(Keys.LeftControl) || _keys.IsKeyDown(Keys.RightControl);

        if (ctrl && Pressed(Keys.V) && Clipboard.TryReadText() is { Length: > 0 } pasted)
        {
            string line = pasted.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            _editText = line.Length > MaxNameLength ? line[..MaxNameLength] : line;
            return;
        }

        if (Pressed(Keys.Back) && _editText.Length > 0)
        {
            _editText = ctrl ? string.Empty : _editText[..^1];
            return;
        }

        if (Pressed(Keys.Enter) || (pressed && _editOkHit.Contains(p)))
        {
            ConfirmRename();
            return;
        }

        if (pressed && _editCancelHit.Contains(p))
            _dialog = DialogKind.None;
    }

    private void HandleDeleteInput(bool pressed, Point p)
    {
        // Enter does not delete: only an explicit Y or a click on DELETE does.
        if (Pressed(Keys.Y) || (pressed && _editOkHit.Contains(p)))
        {
            ConfirmDelete();
            return;
        }

        if (Pressed(Keys.N) || Pressed(Keys.Enter) || (pressed && _editCancelHit.Contains(p)))
            _dialog = DialogKind.None;
    }

    // ── Rename ───────────────────────────────────────────────────────────────────

    private void ConfirmRename()
    {
        string name = _editText.Trim();

        if (name.Length == 0)
        {
            _editError = "THE NAME CANNOT BE EMPTY";
            return;
        }

        if (string.Equals(name, _editTarget.Label, StringComparison.Ordinal))
        {
            _dialog = DialogKind.None;
            return;
        }

        try
        {
            switch (_editTarget.Kind)
            {
                case EditKind.Song:
                    RenameSongInPack(_editTarget, name);
                    break;
                default:
                    RenameFile(_editTarget, name);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or InvalidOperationException)
        {
            _editError = ex.Message.ToUpperInvariant();
            return;
        }

        _rowsDirty = true;
        _playlistLastSelected = -1;
        _status = "RENAMED TO " + name.ToUpperInvariant();
        _statusTimer = 3f;
        _dialog = DialogKind.None;
    }

    private void RenameSongInPack(EditTarget target, string name)
    {
        using MsxAudioLibrary library = MsxAudioLibrary.Load(target.Path);
        MsxAudioSong song = library.Find(target.SongId!)
                            ?? throw new InvalidOperationException("the song is no longer in the package");

        song.Name = name;
        SavePackage(library, target.Path);
        ForgetPackage(target.Path);

        for (int i = 0; i < _tracks.Count; i++)
        {
            VgmEntry entry = _tracks[i];
            if (string.Equals(entry.Path, target.Path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entry.SongId, target.SongId, StringComparison.OrdinalIgnoreCase))
                _tracks[i] = entry with { Name = name };
        }
    }

    private void RenameFile(EditTarget target, string name)
    {
        string clean = name;
        foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
            clean = clean.Replace(invalid, '_');

        clean = clean.Trim().TrimEnd('.');
        if (clean.Length == 0)
            throw new InvalidOperationException("that name is not valid for a file");

        string folder = System.IO.Path.GetDirectoryName(target.Path)!;
        string extension = System.IO.Path.GetExtension(target.Path);
        string destination = System.IO.Path.Combine(folder, clean + extension);

        if (File.Exists(destination) &&
            !string.Equals(destination, target.Path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("a file with that name already exists");

        File.Move(target.Path, destination, overwrite: false);
        ForgetPackage(target.Path);

        string newFileName = clean + extension;

        for (int i = 0; i < _tracks.Count; i++)
        {
            VgmEntry entry = _tracks[i];
            if (!string.Equals(entry.Path, target.Path, StringComparison.OrdinalIgnoreCase))
                continue;

            _tracks[i] = target.Kind == EditKind.Pack
                ? entry with { Path = destination, Source = newFileName }
                : entry with { Path = destination, Name = clean };
        }

        if (target.Kind == EditKind.Pack && _collapsedPacks.Remove(target.Path))
            _collapsedPacks.Add(destination);
    }

    // ── Delete ───────────────────────────────────────────────────────────────────

    private void ConfirmDelete()
    {
        try
        {
            switch (_editTarget.Kind)
            {
                case EditKind.Song:
                    DeleteSongFromPack(_editTarget);
                    break;
                default:
                    Recycle(_editTarget.Path);
                    ForgetPackage(_editTarget.Path);
                    RemoveTracks(t => string.Equals(t.Path, _editTarget.Path, StringComparison.OrdinalIgnoreCase));
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or InvalidOperationException)
        {
            _editError = ex.Message.ToUpperInvariant();
            return;
        }

        _status = "DELETED " + _editTarget.Label.ToUpperInvariant();
        _statusTimer = 3f;
        _dialog = DialogKind.None;
    }

    private void DeleteSongFromPack(EditTarget target)
    {
        using MsxAudioLibrary library = MsxAudioLibrary.Load(target.Path);

        if (!library.Remove(target.SongId!))
            throw new InvalidOperationException("the song is no longer in the package");

        if (library.Songs.Count + library.Clips.Count == 0)
        {
            // Nothing left in it: an empty pack is just clutter.
            Recycle(target.Path);
            ForgetPackage(target.Path);
            RemoveTracks(t => string.Equals(t.Path, target.Path, StringComparison.OrdinalIgnoreCase));
            return;
        }

        SavePackage(library, target.Path);
        ForgetPackage(target.Path);
        RemoveTracks(t => string.Equals(t.Path, target.Path, StringComparison.OrdinalIgnoreCase) &&
                          string.Equals(t.SongId, target.SongId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Drops tracks from the list and keeps the selection on the same track (or the next one).</summary>
    private void RemoveTracks(Predicate<VgmEntry> match)
    {
        int removedBefore = 0;
        bool selectedRemoved = false;

        for (int i = 0; i < _tracks.Count; i++)
        {
            if (!match(_tracks[i]))
                continue;

            if (i < _selected) removedBefore++;
            else if (i == _selected) selectedRemoved = true;
        }

        _tracks.RemoveAll(match);
        _selected = Math.Clamp(_selected - removedBefore, 0, Math.Max(0, _tracks.Count - 1));

        _ = selectedRemoved;   // the sound that was loaded keeps playing; the highlight moves to the next row
        _rowsDirty = true;
        _playlistLastSelected = -1;
    }

    // ── Files ────────────────────────────────────────────────────────────────────

    /// <summary>Writes the package next to the original and swaps it in, so a failure keeps the old file.</summary>
    private static void SavePackage(MsxAudioLibrary library, string path)
    {
        string temp = path + ".tmp";
        library.Save(temp);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Drops the cached copy of a package: it changed on disk.</summary>
    private void ForgetPackage(string path)
    {
        if (_packages.Remove(path, out PackageCache? cache))
            cache.Library.Dispose();
    }

    /// <summary>Sends the file to the recycle bin on Windows; elsewhere it is deleted.</summary>
    private static void Recycle(string path)
    {
        if (OperatingSystem.IsWindows())
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else
            File.Delete(path);
    }

    // ── Drawing ──────────────────────────────────────────────────────────────────

    private void DrawEditDialog()
    {
        _menuRenameHit = _menuDeleteHit = _editOkHit = _editCancelHit = Rectangle.Empty;

        switch (_editMode)
        {
            case EditMode.Menu:
                DrawRowMenu();
                return;
            case EditMode.Rename:
                DrawRenameDialog();
                return;
            default:
                DrawDeleteDialog();
                return;
        }
    }

    private void DrawRowMenu()
    {
        const int w = 170, h = 74;
        int x = Math.Clamp(_menuAt.X, 4, VW - w - 4);
        int y = Math.Clamp(_menuAt.Y, 4, VH - h - 4);

        Panel(x, y, w, h);
        Text(FitText(_editTarget.Label.ToUpperInvariant(), w - 16), x + 8, y + 7, Dim);

        _menuRenameHit = new Rectangle(x + 8, y + 22, w - 16, 18);
        DrawButton(_menuRenameHit, "RENAME  (F4)", false, Accent);

        _menuDeleteHit = new Rectangle(x + 8, y + 46, w - 16, 18);
        DrawButton(_menuDeleteHit, "DELETE  (DEL)", false, Warn);
    }

    private void DrawRenameDialog()
    {
        const int w = 540, h = 150;
        int x = (VW - w) / 2, y = (VH - h) / 2;

        Panel(x, y, w, h);
        Rect(x, y, w, 24, PanelColor);
        Text("RENAME " + KindWord, x + 12, y + 8, Accent);

        string where = _editTarget.Kind switch
        {
            EditKind.Song => "A SONG INSIDE " + System.IO.Path.GetFileName(_editTarget.Path).ToUpperInvariant() +
                             ": THE PACK FILE IS REWRITTEN.",
            EditKind.Pack => "THE .MSXAUDIO FILE ITSELF IS RENAMED.",
            _ => "THE FILE ON DISK IS RENAMED (THE EXTENSION STAYS).",
        };

        Text(FitText(where, w - 24), x + 12, y + 36, Dim);
        Text("NEW NAME", x + 12, y + 62, Dim);

        var field = new Rectangle(x + 84, y + 56, w - 96, 18);
        DrawPathField(field, _editText, "(TYPE THE NEW NAME)", true);

        if (_editError is { } error)
            Text(FitText(error, w - 24), x + 12, y + 84, Warn);

        _editOkHit = new Rectangle(x + w - 180, y + h - 32, 80, 20);
        DrawButton(_editOkHit, "RENAME", true, Accent);

        _editCancelHit = new Rectangle(x + w - 92, y + h - 32, 80, 20);
        DrawButton(_editCancelHit, "CANCEL", false, Dim);

        Text("ENTER SAVES · ESC CANCELS · CTRL+V PASTES", x + 12, y + h - 26, Dim);
    }

    private void DrawDeleteDialog()
    {
        const int w = 560, h = 160;
        int x = (VW - w) / 2, y = (VH - h) / 2;

        Panel(x, y, w, h);
        Rect(x, y, w, 24, PanelColor);
        Text("DELETE " + KindWord, x + 12, y + 8, Warn);

        Text(FitText("DELETE  " + _editTarget.Label.ToUpperInvariant() + "  ?", w - 24), x + 12, y + 40, TextColor);

        string[] lines = _editTarget.Kind switch
        {
            EditKind.Song => new[]
            {
                "IT IS REMOVED FROM " + System.IO.Path.GetFileName(_editTarget.Path).ToUpperInvariant() + ".",
                "THE PACK FILE IS REWRITTEN WITHOUT IT. THIS CANNOT BE UNDONE.",
            },
            EditKind.Pack => new[]
            {
                "THE WHOLE .MSXAUDIO FILE AND EVERY SONG IN IT GO TO THE RECYCLE BIN.",
                string.Empty,
            },
            _ => new[]
            {
                "THE FILE GOES TO THE RECYCLE BIN.",
                string.Empty,
            },
        };

        Text(FitText(lines[0], w - 24), x + 12, y + 62, Dim);
        Text(FitText(lines[1], w - 24), x + 12, y + 74, Dim);

        if (_editError is { } error)
            Text(FitText(error, w - 24), x + 12, y + 92, Warn);

        _editOkHit = new Rectangle(x + w - 180, y + h - 32, 80, 20);
        DrawButton(_editOkHit, "DELETE", true, Warn);

        _editCancelHit = new Rectangle(x + w - 92, y + h - 32, 80, 20);
        DrawButton(_editCancelHit, "CANCEL", false, Dim);

        Text("Y DELETES · N OR ESC CANCELS", x + 12, y + h - 26, Dim);
    }
}
