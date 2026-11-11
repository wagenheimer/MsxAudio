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

/// <summary>Uma música ou efeito guardado numa biblioteca <c>.msxaudio</c>.</summary>
public sealed class MsxAudioSong
{
    /// <summary>Identificador estável (ex.: <c>stage_a</c>, <c>jump</c>).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Nome para exibição.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Entrada do VGM dentro do pacote.</summary>
    public string Entry { get; set; } = string.Empty;

    /// <summary>É música (true) ou efeito curto (false)?</summary>
    public bool Music { get; set; } = true;

    /// <summary>Toca em loop?</summary>
    public bool Loop { get; set; }

    /// <summary>Início do loop em segundos (quando <see cref="Loop"/>).</summary>
    public double LoopStart { get; set; }

    /// <summary>Duração do loop em segundos (quando <see cref="Loop"/>).</summary>
    public double LoopSeconds { get; set; }

    /// <summary>Duração total capturada do VGM.</summary>
    public double Seconds { get; set; }

    /// <summary>Rótulos livres (ex.: <c>stage</c>, <c>menu</c>, <c>sfx</c>).</summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>Id do som no original (ex.: o id de <c>432E</c>), quando conhecido.</summary>
    public int? OriginalId { get; set; }
}

/// <summary>Manifesto do pacote.</summary>
public sealed class MsxAudioManifest
{
    /// <summary>Versão do formato.</summary>
    public string Format { get; set; } = "msxaudio/1";

    /// <summary>Título da coleção.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Origem (ROM, VGM, emulador...).</summary>
    public string? Source { get; set; }

    /// <summary>Clock do PSG em Hz.</summary>
    public double PsgClock { get; set; } = 1789772.5;

    /// <summary>Clock do SCC em Hz (metade do clock "clássico" de 3579545).</summary>
    public double SccClock { get; set; } = 1789772.5;

    /// <summary>Músicas.</summary>
    public List<MsxAudioSong> Songs { get; set; } = new();

    /// <summary>Efeitos.</summary>
    public List<MsxAudioSong> Clips { get; set; } = new();
}

/// <summary>
/// Biblioteca de áudio do MSX (<c>.msxaudio</c>): um ZIP com o manifesto e os VGMs.
/// </summary>
/// <remarks>
/// Um pacote guarda as <b>escritas de registrador</b> (não áudio pronto): alguns KB por música e
/// reprodução fiel em qualquer máquina. É o formato para <i>salvar</i> uma extração e para
/// distribuí-la com um jogo.
///
/// <para>Estrutura:</para>
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

    /// <summary>Cria uma biblioteca vazia.</summary>
    public MsxAudioLibrary(string title = "MSX audio")
    {
        Manifest = new MsxAudioManifest { Title = title };
    }

    /// <summary>Manifesto (músicas, efeitos, clocks).</summary>
    public MsxAudioManifest Manifest { get; }

    /// <summary>Músicas da biblioteca.</summary>
    public IReadOnlyList<MsxAudioSong> Songs => Manifest.Songs;

    /// <summary>Efeitos da biblioteca.</summary>
    public IReadOnlyList<MsxAudioSong> Clips => Manifest.Clips;

    /// <summary>Procura uma música ou efeito pelo id.</summary>
    public MsxAudioSong? Find(string id) =>
        Manifest.Songs.Concat(Manifest.Clips).FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adiciona um VGM como música.</summary>
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

    /// <summary>Adiciona um VGM como efeito.</summary>
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

    /// <summary>Adiciona um arquivo arbitrário (ex.: um WAV de sample, um readme).</summary>
    public void AddEntry(string entry, byte[] data) => _entries[entry] = data;

    /// <summary>Lê um arquivo do pacote (ou <c>null</c>).</summary>
    public byte[]? ReadEntry(string entry) => _entries.TryGetValue(entry, out var data) ? data : null;

    /// <summary>Carrega o VGM de uma música (com cache).</summary>
    public VgmFile LoadVgm(MsxAudioSong song)
    {
        if (_loaded.TryGetValue(song.Entry, out var cached)) return cached;
        if (!_entries.TryGetValue(song.Entry, out var data))
            throw new InvalidDataException($"entrada '{song.Entry}' não existe no pacote");

        var vgm = VgmFile.Parse(data, song.Id);
        if (!vgm.HasLoop && song.Loop && song.LoopSeconds > 0)
            vgm.SetLoop(song.LoopStart, song.LoopSeconds);
        _loaded[song.Entry] = vgm;
        return vgm;
    }

    /// <summary>Salva o pacote em disco.</summary>
    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        Save(stream);
    }

    /// <summary>Salva o pacote num stream (formato ZIP).</summary>
    public void Save(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        WriteEntry(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(Manifest, JsonOptions));
        foreach (var (name, data) in _entries)
            WriteEntry(archive, name, data);
    }

    /// <summary>Carrega um pacote do disco.</summary>
    public static MsxAudioLibrary Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Carrega um pacote de um stream (formato ZIP).</summary>
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
