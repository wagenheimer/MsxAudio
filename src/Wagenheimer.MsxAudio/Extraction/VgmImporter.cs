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
    public static MsxAudioSong ImportFile(
        MsxAudioLibrary library,
        string file,
        bool music,
        IReadOnlyDictionary<string, string>? names = null,
        Action<string>? log = null)
    {
        var vgm = VgmFile.Load(file);
        string id = Path.GetFileNameWithoutExtension(file);
        string name = names != null && names.TryGetValue(id, out var friendly) ? friendly : id;

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
}
