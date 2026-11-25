using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Wagenheimer.MsxAudio.Scope;

public sealed partial class VisualizerGame
{
    private FileSystemWatcher? _libraryWatcher;
    private string? _watchedFolder;
    private bool _watchedRecursive;
    private long _libraryChangedAt;

    private void WatchLibrary(string folder, bool recursive)
    {
        if (_watchedFolder == folder && _watchedRecursive == recursive) return;
        _libraryWatcher?.Dispose();
        _libraryWatcher = null;
        _watchedFolder = folder;
        _watchedRecursive = recursive;
        try
        {
            var watcher = new FileSystemWatcher(folder, "*")
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += OnLibraryChanged;
            watcher.Changed += OnLibraryChanged;
            watcher.Deleted += OnLibraryChanged;
            watcher.Renamed += (_, e) =>
            {
                if (IsPackage(e.FullPath) || IsPackage(e.OldFullPath) || Directory.Exists(e.FullPath))
                    Interlocked.Exchange(ref _libraryChangedAt, Stopwatch.GetTimestamp());
            };
            watcher.Error += (_, _) => Interlocked.Exchange(ref _libraryChangedAt, Stopwatch.GetTimestamp());
            watcher.EnableRaisingEvents = true;
            _libraryWatcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"[MSXScope] Library monitoring unavailable: {ex.Message}");
        }
    }

    private static bool IsPackage(string path) =>
        string.Equals(Path.GetExtension(path), ".msxaudio", StringComparison.OrdinalIgnoreCase);

    private void OnLibraryChanged(object sender, FileSystemEventArgs e)
    {
        if (IsPackage(e.FullPath) || Directory.Exists(e.FullPath) || e.ChangeType == WatcherChangeTypes.Deleted)
            Interlocked.Exchange(ref _libraryChangedAt, Stopwatch.GetTimestamp());
    }

    private void PollLibraryChanges()
    {
        long changed = Interlocked.Read(ref _libraryChangedAt);
        if (changed == 0 || _listScanning || _dialog != DialogKind.None ||
            Stopwatch.GetElapsedTime(changed).TotalMilliseconds < 500) return;
        if (Interlocked.CompareExchange(ref _libraryChangedAt, 0, changed) != changed) return;
        ScanTracks(_scanRoot, _watchedRecursive);
    }
}
