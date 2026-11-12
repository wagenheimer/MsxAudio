using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>One VGM found by <see cref="Library"/>.</summary>
/// <param name="Path">File on disk (a <c>.vgm</c> or a <c>.msxaudio</c> package).</param>
/// <param name="Folder">Folder holding the file.</param>
/// <param name="Name">Display name (the song name inside a package).</param>
/// <param name="Seconds">Duration when known.</param>
/// <param name="Size">File size in bytes.</param>
/// <param name="SongId">Song id inside the package (null for a plain VGM).</param>
/// <param name="Source">Package file name, for the playlist (null for a plain VGM).</param>
public sealed record VgmEntry(string Path, string Folder, string Name, double? Seconds, long Size,
    string? SongId = null, string? Source = null);

/// <summary>Result of a folder scan.</summary>
public sealed class ScanResult
{
    public string Root { get; init; } = string.Empty;
    public bool Recursive { get; init; }
    public List<VgmEntry> Files { get; } = new();
    public int Folders { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Folder scanning for the visualizer: subfolders for the browser, and <c>*.vgm</c> files (optionally
/// recursing) with their duration read from the VGM header. Runs on a worker thread; never touches
/// the graphics device.
/// </summary>
public static class Library
{
    private static readonly string[] SkipFolders =
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", "$recycle.bin", "system volume information",
    };

    /// <summary>Subfolders of <paramref name="dir"/>, sorted, skipping build/VCS noise.</summary>
    public static List<(string Name, string Path)> Subfolders(string dir)
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (string path in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(path);
                if (IsSkipped(name))
                    continue;

                list.Add((name, path));
            }
        }
        catch (Exception)
        {
            // unreadable folder: show it empty instead of failing the dialog
        }

        list.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>
    /// Scans <paramref name="root"/> for <paramref name="pattern"/> (VGM by default).
    /// <paramref name="progress"/> receives the file count every few hits (called from the worker
    /// thread; keep it cheap).
    /// </summary>
    public static ScanResult Scan(string root, bool recursive, CancellationToken token,
        Action<int>? progress = null, string pattern = "*.vgm")
    {
        var result = new ScanResult { Root = root, Recursive = recursive };

        try
        {
            if (!Directory.Exists(root))
            {
                result.Error = "folder not found";
                return result;
            }

            if (!recursive)
            {
                result.Folders = 1;
                foreach (string file in Directory.EnumerateFiles(root, pattern))
                {
                    token.ThrowIfCancellationRequested();
                    result.Files.Add(Entry(file));
                }

                if (pattern == "*.vgm")
                {
                    foreach (string file in Directory.EnumerateFiles(root, "*.msxaudio"))
                    {
                        token.ThrowIfCancellationRequested();
                        AddPackage(result, file);
                    }
                }
            }
            else
            {
                var stack = new Stack<string>();
                stack.Push(root);

                while (stack.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    string dir = stack.Pop();
                    result.Folders++;

                    try
                    {
                        foreach (string sub in Directory.EnumerateDirectories(dir))
                        {
                            string name = Path.GetFileName(sub);
                            if (!IsSkipped(name) && !IsHidden(sub))
                                stack.Push(sub);
                        }
                    }
                    catch (Exception)
                    {
                        // keep scanning the rest
                    }

                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(dir, pattern))
                        {
                            token.ThrowIfCancellationRequested();
                            result.Files.Add(Entry(file));

                            if (result.Files.Count % 32 == 0)
                                progress?.Invoke(result.Files.Count);
                        }

                        if (pattern == "*.vgm")
                        {
                            foreach (string file in Directory.EnumerateFiles(dir, "*.msxaudio"))
                            {
                                token.ThrowIfCancellationRequested();
                                AddPackage(result, file);
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // keep scanning the rest
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            result.Files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            progress?.Invoke(result.Files.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    private static VgmEntry Entry(string file)
    {
        long size = 0;
        try { size = new FileInfo(file).Length; } catch (Exception) { }

        return new VgmEntry(
            file,
            Path.GetDirectoryName(file) ?? string.Empty,
            Path.GetFileNameWithoutExtension(file),
            TryReadSeconds(file),
            size);
    }

    /// <summary>
    /// Adds one entry per song/effect of a <c>.msxaudio</c> package. Only the manifest is read, so
    /// listing a folder stays cheap even with many packages.
    /// </summary>
    private static void AddPackage(ScanResult result, string file)
    {
        long size = 0;
        try { size = new FileInfo(file).Length; } catch (Exception) { }

        string folder = Path.GetDirectoryName(file) ?? string.Empty;
        string source = Path.GetFileName(file);

        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(file);
            var manifestEntry = archive.GetEntry("manifest.json");
            if (manifestEntry is null)
                return;

            using var stream = manifestEntry.Open();
            using var reader = new StreamReader(stream);
            var manifest = System.Text.Json.JsonSerializer.Deserialize<Songs.MsxAudioManifest>(
                reader.ReadToEnd(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (manifest is null)
                return;

            foreach (var song in manifest.Songs.Concat(manifest.Clips))
            {
                result.Files.Add(new VgmEntry(
                    file, folder, song.Name.Length > 0 ? song.Name : song.Id,
                    song.Seconds > 0 ? song.Seconds : null, size, song.Id, source));
            }
        }
        catch (Exception)
        {
            // not a package / locked file: ignore it instead of breaking the scan
        }
    }

    /// <summary>
    /// Reads only the VGM header (total samples at 44100 Hz), so listing hundreds of files stays
    /// cheap — no event parsing.
    /// </summary>
    public static double? TryReadSeconds(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[0x1C];
            if (stream.Read(header) < header.Length)
                return null;

            if (header[0] != (byte)'V' || header[1] != (byte)'g' || header[2] != (byte)'m' || header[3] != (byte)' ')
                return null;

            uint samples = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(0x18, 4));
            return samples == 0 ? null : samples / 44100.0;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsSkipped(string name) =>
        Array.Exists(SkipFolders, s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsHidden(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
