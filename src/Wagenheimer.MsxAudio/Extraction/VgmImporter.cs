using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>Importa uma pasta de VGMs para uma biblioteca <c>.msxaudio</c>.</summary>
public static class VgmImporter
{
    /// <summary>
    /// Importa todos os <c>*.vgm</c> de uma pasta.
    /// </summary>
    /// <param name="directory">Pasta com os VGMs.</param>
    /// <param name="title">Título da biblioteca.</param>
    /// <param name="music">Se true, são músicas (detecta loop); se false, são efeitos.</param>
    /// <param name="names">Nomes amigáveis por nome de arquivo (sem extensão).</param>
    /// <param name="log">Diagnóstico opcional.</param>
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

    /// <summary>Importa um único VGM.</summary>
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
                        (vgm.HasLoop ? $"loop {vgm.LoopStartSeconds:F3}s + {vgm.LoopSeconds:F3}s" : "sem loop"));
            return library.AddSong(id, name, vgm, vgm.HasLoop, vgm.LoopStartSeconds, vgm.LoopSeconds);
        }

        log?.Invoke($"{id,-16} {vgm.TotalSeconds,7:F2}s");
        return library.AddClip(id, name, vgm);
    }
}
