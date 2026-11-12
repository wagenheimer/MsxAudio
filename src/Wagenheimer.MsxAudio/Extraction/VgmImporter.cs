using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>Imports a folder of VGMs into a <c>.msxaudio</c> library.</summary>
public static class VgmImporter
{
    /// <summary>
    /// Imports every <c>*.vgm</c> from a folder.
    /// </summary>
    /// <param name="directory">Folder with the VGMs.</param>
    /// <param name="title">Library title.</param>
    /// <param name="music">When true they are songs (loop detection); otherwise effects.</param>
    /// <param name="names">Friendly names by file name (without extension).</param>
    /// <param name="log">Optional diagnostics.</param>
    public static MsxAudioLibrary Import(
        string directory,
        string title,
        bool music = true,
        IReadOnlyDictionary<string, string>? names = null,
        Action<string>? log = null)
    {
        var library = new MsxAudioLibrary(title);
        foreach (string file in Directory.GetFiles(directory, "*.vgm").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            ImportFile(library, file, music, names, log);
        }

        return library;
    }

    /// <summary>Imports a single VGM.</summary>
    /// <param name="idOverride">Explicit id (defaults to the file name).</param>
    /// <param name="nameOverride">Explicit display name (defaults to <paramref name="names"/> or the id).</param>
    public static MsxAudioSong ImportFile(
        MsxAudioLibrary library,
        string file,
        bool music,
        IReadOnlyDictionary<string, string>? names = null,
        Action<string>? log = null,
        string? idOverride = null,
        string? nameOverride = null)
    {
        var vgm = VgmFile.Load(file);
        string id = idOverride ?? Path.GetFileNameWithoutExtension(file);
        string name = nameOverride
            ?? (names != null && names.TryGetValue(id, out var friendly) ? friendly : id);

        if (music)
        {
            if (!vgm.HasLoop)
            {
                var loop = VgmLoopFinder.Find(vgm);
                if (loop is { } found) vgm.SetLoop(found.Start, found.Duration);
            }

            log?.Invoke($"{id,-16} {vgm.TotalSeconds,7:F2}s  " +
                        (vgm.HasLoop ? $"loop {vgm.LoopStartSeconds:F3}s + {vgm.LoopSeconds:F3}s" : "no loop"));
            return library.AddSong(id, name, vgm, vgm.HasLoop, vgm.LoopStartSeconds, vgm.LoopSeconds);
        }

        log?.Invoke($"{id,-16} {vgm.TotalSeconds,7:F2}s");
        return library.AddClip(id, name, vgm);
    }

    /// <summary>
    /// Imports a content folder described by a manifest (<c>audio.json</c>), keeping the original
    /// sound ids: <c>{"music": {"title": {"id": 3, "loop": false}}, "sfx": {...}}</c>.
    /// </summary>
    /// <remarks>
    /// A manifest is what a game repository needs to rebuild its package without knowing the ROM:
    /// the ids and the "loops or not" decision live with the content, not in a tool. Files are looked
    /// up as <c>&lt;root&gt;/Music/&lt;name&gt;.vgm</c> and <c>&lt;root&gt;/Sfx/&lt;name&gt;.vgm</c>
    /// (or directly under <c>&lt;root&gt;</c>).
    /// </remarks>
    public static MsxAudioLibrary ImportManifest(
        string root,
        string manifestPath,
        string title,
        Action<string>? log = null)
    {
        var library = new MsxAudioLibrary(title);
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
        var rootElement = document.RootElement;

        if (rootElement.TryGetProperty("music", out var music))
        {
            foreach (var entry in music.EnumerateObject())
            {
                string name = entry.Name;
                if (Locate(root, "Music", name) is not { } file)
                {
                    log?.Invoke($"{name,-16} (no Music/{name}.vgm)");
                    continue;
                }

                var vgm = VgmFile.Load(file);
                int? originalId = ReadInt(entry.Value, "id");
                bool loop = !entry.Value.TryGetProperty("loop", out var loopFlag) || loopFlag.GetBoolean();

                if (loop && !vgm.HasLoop)
                {
                    var found = VgmLoopFinder.Find(vgm, 4);
                    if (found is { } value) vgm.SetLoop(value.Start, value.Duration);
                }

                log?.Invoke($"{name,-16} id {originalId,-3} {vgm.TotalSeconds,7:F2}s  " +
                            (loop && vgm.HasLoop ? $"loop {vgm.LoopStartSeconds:F3}s + {vgm.LoopSeconds:F3}s" : "once"));

                library.AddSong(name, name, vgm, loop && vgm.HasLoop,
                    vgm.LoopStartSeconds, vgm.LoopSeconds, originalId, "music");
            }
        }

        if (rootElement.TryGetProperty("sfx", out var sfx))
        {
            foreach (var entry in sfx.EnumerateObject())
            {
                string name = entry.Name;
                if (Locate(root, "Sfx", name) is not { } file)
                {
                    log?.Invoke($"{name,-16} (no Sfx/{name}.vgm)");
                    continue;
                }

                var vgm = VgmFile.Load(file);
                int? originalId = ReadInt(entry.Value, "id");
                log?.Invoke($"{name,-16} id {originalId,-3} {vgm.TotalSeconds,7:F2}s  effect");
                library.AddClip(name, name, vgm, originalId, "sfx");
            }
        }

        return library;
    }

    private static string? Locate(string root, string folder, string name)
    {
        string[] candidates =
        {
            Path.Combine(root, folder, name + ".vgm"),
            Path.Combine(root, name + ".vgm"),
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static int? ReadInt(System.Text.Json.JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out int number) ? number : null;
}
