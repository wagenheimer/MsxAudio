using System.IO.Compression;
using System.Text.Json;
using Wagenheimer.MsxAudio.Songs;

namespace Wagenheimer.MsxAudio;

public sealed record AudioCatalogPackage(string Path, MsxAudioManifest Manifest);
public sealed record AudioCatalogIssue(string Path, string Message);
public sealed record AudioCatalogResult(IReadOnlyList<AudioCatalogPackage> Packages, IReadOnlyList<AudioCatalogIssue> Issues);

/// <summary>Discovers published packages using manifests only; never parses or renders their VGMs.</summary>
public static class AudioCatalog
{
    /// <summary>Reads package metadata while allowing an atomic replacement by its producer.</summary>
    public static MsxAudioManifest ReadManifest(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Package has no manifest.");
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("Package manifest is too large.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<MsxAudioManifest>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Package manifest is empty.");
        if (manifest.Format != "msxaudio/1" || manifest.Songs is null || manifest.Clips is null)
            throw new InvalidDataException("Unsupported or malformed package manifest.");
        return manifest;
    }

    /// <summary>Recursively finds packages, skipping symbolic links and build/export folders.</summary>
    public static AudioCatalogResult Scan(string root, CancellationToken token = default)
    {
        var packages = new List<AudioCatalogPackage>();
        var issues = new List<AudioCatalogIssue>();
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(root))
            return new(packages, new[] { new AudioCatalogIssue(root, "Library folder not found.") });
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (!string.Equals(Path.GetExtension(file), ".msxaudio", StringComparison.OrdinalIgnoreCase)) continue;
                    try { packages.Add(new(file, ReadManifest(file))); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
                    { issues.Add(new(file, ex.Message)); }
                }
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(child);
                    if (name.StartsWith('.') || new[] { "bin", "obj", "export", "node_modules" }.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { issues.Add(new(directory, ex.Message)); }
        }
        packages.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        return new(packages, issues);
    }
}
