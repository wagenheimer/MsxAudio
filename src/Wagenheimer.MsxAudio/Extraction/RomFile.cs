using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>
/// Resolves a cartridge file, accepting a <c>.rom</c> or a <c>.zip</c> as downloaded from the web.
/// </summary>
/// <remarks>
/// MSX ROM archives almost always hold a single <c>.rom</c> (sometimes with a long name full of
/// brackets). Extracting it to a cache folder means the user can point at the download as-is and the
/// rest of the pipeline keeps working with a plain file path.
/// </remarks>
public static class RomFile
{
    private static readonly string[] RomExtensions = { ".rom", ".mx1", ".mx2", ".ri", ".bin" };

    /// <summary>
    /// Returns a path to a readable ROM file. A <c>.zip</c> is unpacked (once) into a cache folder and
    /// the first cartridge inside is returned; anything else is returned unchanged.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The archive has no cartridge inside.</exception>
    public static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("empty ROM path", nameof(path));

        if (!File.Exists(path))
            throw new FileNotFoundException("ROM not found", path);

        if (!Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return path;

        string entry = FindCartridge(path)
                       ?? throw new InvalidDataException($"no .rom inside {Path.GetFileName(path)}");

        // Cache per archive (path + size + timestamp), so re-running an extraction is instant.
        var info = new FileInfo(path);
        string stamp = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        string hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(stamp)))[..16];

        string folder = Path.Combine(Path.GetTempPath(), "msxaudio-rom", hash);
        string target = Path.Combine(folder, Path.GetFileName(entry));

        if (File.Exists(target) && new FileInfo(target).Length > 0)
            return target;

        Directory.CreateDirectory(folder);

        using var archive = ZipFile.OpenRead(path);
        var zipEntry = archive.GetEntry(entry)
                       ?? throw new InvalidDataException($"'{entry}' disappeared from the archive");

        // Write to a temp name and move, so a cancelled run never leaves a half file.
        string temp = target + ".tmp";
        zipEntry.ExtractToFile(temp, overwrite: true);
        File.Move(temp, target, overwrite: true);

        return target;
    }

    /// <summary>Name of the cartridge inside a <c>.zip</c> (null when there is none).</summary>
    public static string? FindCartridge(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.StartsWith("__MACOSX", StringComparison.OrdinalIgnoreCase))
                continue;

            if (entry.FullName.EndsWith("/"))
                continue;

            string extension = Path.GetExtension(entry.FullName);
            if (Array.Exists(RomExtensions, e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)))
                return entry.FullName;
        }

        return null;
    }

    /// <summary>True when <paramref name="path"/> is an archive this class can unpack.</summary>
    public static bool IsArchive(string path) =>
        Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase);
}
