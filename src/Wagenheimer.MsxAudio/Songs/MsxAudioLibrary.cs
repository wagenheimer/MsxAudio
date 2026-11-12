using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Songs;

/// <summary>A song or effect stored in a <c>.msxaudio</c> library.</summary>
public sealed class MsxAudioSong
{
    /// <summary>Stable identifier (e.g. <c>stage_a</c>, <c>jump</c>).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>VGM entry inside the package.</summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>Is it music (true) or a short effect (false)?</summary>
    public bool Music { get; set; } = true;

    /// <summary>Does it loop?</summary>
    public bool Loop { get; set; }

    /// <summary>Loop start in seconds (when <see cref="Loop"/>).</summary>
    public double LoopStart { get; set; }

    /// <summary>Loop duration in seconds (when <see cref="Loop"/>).</summary>
    public double LoopSeconds { get; set; }

    /// <summary>Total captured VGM duration.</summary>
    public double Seconds { get; set; }

    /// <summary>Free-form tags (e.g. <c>stage</c>, <c>menu</c>, <c>sfx</c>).</summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>Sound id in the original (e.g. the <c>432E</c> id), when known.</summary>
    public int? OriginalId { get; set; }
}

/// <summary>Package manifest.</summary>
public sealed class MsxAudioManifest
{
    /// <summary>Format version.</summary>
    public string Format { get; set; } = "msxaudio/1";

    /// <summary>Collection title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Origin (ROM, VGM, emulator...).</summary>
    public string? Source { get; set; }

    /// <summary>PSG clock in Hz.</summary>
    public double PsgClock { get; set; } = 1789772.5;

    /// <summary>SCC clock in Hz (half of the "classic" 3579545).</summary>
    public double SccClock { get; set; } = 1789772.5;

    /// <summary>Songs.</summary>
    public List<MsxAudioSong> Songs { get; set; } = new();

    /// <summary>Effects.</summary>
    public List<MsxAudioSong> Clips { get; set; } = new();
}

/// <summary>
/// MSX audio library (<c>.msxaudio</c>): a ZIP with the manifest and the VGMs.
/// </summary>
/// <remarks>
/// A package stores the <b>register writes</b> (not rendered audio): a few KB per song and faithful
/// playback on any machine. It is the format used to <i>save</i> an extraction and to ship it with
/// a game.
///
/// <para>Layout:</para>
/// <code>
/// manifest.json
/// songs/stage_a.vgm
/// clips/jump.vgm
/// </code>
/// </remarks>
public sealed class MsxAudioLibrary : IDisposable
{
    private readonly Dictionary<string, byte[]> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VgmFile> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates an empty library.</summary>
    public MsxAudioLibrary(string title = "MSX audio")
    {
        Manifest = new MsxAudioManifest { Title = title };
    }

    /// <summary>Manifest (songs, effects, clocks).</summary>
    public MsxAudioManifest Manifest { get; }

    /// <summary>Songs in the library.</summary>
    public IReadOnlyList<MsxAudioSong> Songs => Manifest.Songs;

    /// <summary>Effects in the library.</summary>
    public IReadOnlyList<MsxAudioSong> Clips => Manifest.Clips;

    /// <summary>Finds a song or effect by id.</summary>
    public MsxAudioSong? Find(string id) =>
        Manifest.Songs.Concat(Manifest.Clips).FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a VGM as a song.</summary>
    public MsxAudioSong AddSong(
        string id,
        string name,
        VgmFile vgm,
        bool loop = true,
        double loopStart = 0,
        double loopSeconds = 0,
        int? originalId = null,
        params string[] tags)
    {
        var song = new MsxAudioSong
        {
            Id = id,
            Name = name,
            Music = true,
            Loop = loop,
            LoopStart = loopStart,
            LoopSeconds = loopSeconds,
            Seconds = vgm.TotalSeconds,
            OriginalId = originalId,
            Tags = tags,
            Entry = $"songs/{Sanitize(id)}.vgm",
        };

        _entries[song.Entry] = VgmWriter.Write(vgm, loopStartSeconds: loop ? loopStart : null, loopSeconds: loop ? loopSeconds : null);
        _loaded[song.Entry] = vgm;
        Manifest.Songs.Add(song);
        return song;
    }

    /// <summary>Adds a VGM as an effect.</summary>
    public MsxAudioSong AddClip(string id, string name, VgmFile vgm, int? originalId = null, params string[] tags)
    {
        var clip = new MsxAudioSong
        {
            Id = id,
            Name = name,
            Music = false,
            Loop = false,
            Seconds = vgm.TotalSeconds,
            OriginalId = originalId,
            Tags = tags,
            Entry = $"clips/{Sanitize(id)}.vgm",
        };

        _entries[clip.Entry] = VgmWriter.Write(vgm);
        _loaded[clip.Entry] = vgm;
        Manifest.Clips.Add(clip);
        return clip;
    }

    /// <summary>Removes a song or effect (and its VGM) from the package. False when the id is unknown.</summary>
    public bool Remove(string id)
    {
        MsxAudioSong? song = Find(id);
        if (song is null)
            return false;

        Manifest.Songs.Remove(song);
        Manifest.Clips.Remove(song);
        _entries.Remove(song.Entry);
        _loaded.Remove(song.Entry);
        return true;
    }

    /// <summary>Adds an arbitrary file (e.g. a sample WAV, a readme).</summary>
    public void AddEntry(string entry, byte[] data) => _entries[entry] = data;

    /// <summary>Reads a file from the package (or <c>null</c>).</summary>
    public byte[]? ReadEntry(string entry) => _entries.TryGetValue(entry, out var data) ? data : null;

    /// <summary>Loads a song's VGM (cached).</summary>
    public VgmFile LoadVgm(MsxAudioSong song)
    {
        if (_loaded.TryGetValue(song.Entry, out var cached)) return cached;
        if (!_entries.TryGetValue(song.Entry, out var data))
            throw new InvalidDataException($"entry '{song.Entry}' does not exist in the package");

        var vgm = VgmFile.Parse(data, song.Id);
        if (!vgm.HasLoop && song.Loop && song.LoopSeconds > 0)
            vgm.SetLoop(song.LoopStart, song.LoopSeconds);
        _loaded[song.Entry] = vgm;
        return vgm;
    }

    /// <summary>Saves the package to disk.</summary>
    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        Save(stream);
    }

    /// <summary>Saves the package to a stream (ZIP format).</summary>
    public void Save(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(Manifest, JsonOptions));
        foreach (var (name, data) in _entries)
        {
            // The manifest above is the current one: a copy kept from Load would be stale (and would
            // shadow it, silently undoing a rename or a delete).
            if (string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase))
                continue;

            WriteEntry(archive, name, data);
        }
    }

    /// <summary>Loads a package from disk.</summary>
    public static MsxAudioLibrary Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Loads a package from a stream (ZIP format).</summary>
    public static MsxAudioLibrary Load(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var library = new MsxAudioLibrary();
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            using var memory = new MemoryStream();
            using (var input = entry.Open()) input.CopyTo(memory);
            library._entries[entry.FullName] = memory.ToArray();
        }

        if (library._entries.TryGetValue("manifest.json", out var manifestBytes))
        {
            var manifest = JsonSerializer.Deserialize<MsxAudioManifest>(manifestBytes, JsonOptions);
            if (manifest != null)
            {
                library.Manifest.Format = manifest.Format;
                library.Manifest.Title = manifest.Title;
                library.Manifest.Source = manifest.Source;
                library.Manifest.PsgClock = manifest.PsgClock;
                library.Manifest.SccClock = manifest.SccClock;
                library.Manifest.Songs = manifest.Songs ?? new List<MsxAudioSong>();
                library.Manifest.Clips = manifest.Clips ?? new List<MsxAudioSong>();
            }
        }

        library._entries.Remove("manifest.json");
        return library;
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] data)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var output = entry.Open();
        output.Write(data, 0, data.Length);
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
            builder.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        return builder.ToString();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _entries.Clear();
        _loaded.Clear();
    }
}
