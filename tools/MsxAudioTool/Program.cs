using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Wagenheimer.MsxAudio.Extraction;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Playback;
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Tool;

/// <summary>CLI do pacote: importar VGMs, extrair de uma ROM, ouvir/exportar e comparar.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "info" => Info(args.Skip(1).ToArray()),
                "import" => Import(args.Skip(1).ToArray()),
                "extract" => Extract(args.Skip(1).ToArray()),
                "extract-direct" => ExtractDirect(args.Skip(1).ToArray()),
                "probe" => Probe(args.Skip(1).ToArray()),
                "render" => Render(args.Skip(1).ToArray()),
                "verify" => Verify(args.Skip(1).ToArray()),
                "unpack" => Unpack(args.Skip(1).ToArray()),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"erro: {ex.Message}");
            return 2;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            MsxAudioTool — áudio de MSX (PSG + SCC) em pacotes .msxaudio

            uso:
              import <pasta-vgm> <saida.msxaudio> [--title T] [--clips]
                    converte VGMs em um pacote (detecta loop das músicas)
              extract <rom> <saida.msxaudio> [--profile perfil.json] [--openmsx caminho]
                    dirige o openMSX para gravar os VGMs de uma ROM e monta o pacote
              info <pacote.msxaudio>
                    lista músicas/efeitos, loops e clocks
              render <pacote.msxaudio> <id|all> <saida> [--profile original|stereo|remaster] [--rate 48000]
                    renderiza uma música/efeito (WAV) — "all" gera uma pasta
              verify <vgm> <captura.wav>
                    compara o render com uma captura do emulador (correlação/RMS/offset)
              unpack <pacote.msxaudio> <pasta>
                    extrai os VGMs + manifest.json do pacote

            perfis de mixagem: original (fiel), stereo (canais espalhados), remaster (reverb + filtros)
            """);
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"comando desconhecido: {command}");
        return 1;
    }

    private static int Info(string[] args)
    {
        if (args.Length < 1) return Usage("info <pacote.msxaudio>");

        using var library = MsxAudioLibrary.Load(args[0]);
        Console.WriteLine($"{library.Manifest.Title}  ({library.Manifest.Format})");
        if (!string.IsNullOrEmpty(library.Manifest.Source))
            Console.WriteLine($"origem: {library.Manifest.Source}");
        Console.WriteLine($"PSG {library.Manifest.PsgClock:F1} Hz | SCC {library.Manifest.SccClock:F1} Hz");
        Console.WriteLine($"\nmúsicas ({library.Songs.Count}):");
        foreach (var song in library.Songs)
        {
            string loop = song.Loop ? $"loop {song.LoopStart:F3}s + {song.LoopSeconds:F3}s" : "sem loop";
            Console.WriteLine($"  {song.Id,-16} {song.Name,-24} {song.Seconds,7:F2}s  {loop}");
        }

        Console.WriteLine($"\nefeitos ({library.Clips.Count}):");
        foreach (var clip in library.Clips)
            Console.WriteLine($"  {clip.Id,-16} {clip.Name,-24} {clip.Seconds,7:F2}s");
        return 0;
    }

    private static int Import(string[] args)
    {
        if (args.Length < 2) return Usage("import <pasta-vgm> <saida.msxaudio> [--title T] [--clips]");

        string directory = args[0];
        string output = args[1];
        string title = Value(args, "--title") ?? Path.GetFileNameWithoutExtension(output);
        bool clips = args.Contains("--clips");

        var library = VgmImporter.Import(directory, title, music: !clips, log: Console.WriteLine);
        library.Manifest.Source = directory;
        library.Save(output);
        Console.WriteLine($"{library.Songs.Count} músicas, {library.Clips.Count} efeitos -> {output}");
        return 0;
    }

    private static int Extract(string[] args)
    {
        if (args.Length < 2) return Usage("extract <rom> <saida.msxaudio> [--profile perfil.json] [--openmsx caminho]");

        string profilePath = Value(args, "--profile") ?? throw new ArgumentException("informe --profile perfil.json");
        var profile = JsonSerializer.Deserialize<RomExtractionProfile>(File.ReadAllText(profilePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("perfil inválido");

        var options = new RomExtractionOptions
        {
            RomPath = args[0],
            Profile = profile,
            OpenMsxPath = Value(args, "--openmsx") ?? "openmsx",
            WorkDirectory = Value(args, "--work") ?? Path.Combine(Path.GetTempPath(), "msxaudio-extract"),
        };

        var result = OpenMsxRomExtractor.Extract(options, Console.WriteLine);
        result.Library.Save(args[1]);
        Console.WriteLine($"{result.Library.Songs.Count} músicas, {result.Library.Clips.Count} efeitos -> {args[1]}");
        return 0;
    }

    private static int Probe(string[] args)
    {
        if (args.Length < 1) return Usage("probe <rom>");

        byte[] rom = File.ReadAllBytes(args[0]);
        var machine = new MiniMsx(rom) { TraceWrites = true };
        Console.WriteLine($"rom {rom.Length} bytes; header INIT={rom[2] | (rom[3] << 8):X4}");
        Console.WriteLine($"after boot: {machine.Boot()}, PC={machine.Pc:X4}, H.TIMI={machine.InterruptHook:X4}");
        Console.WriteLine($"high-RAM writes during boot: {machine.Trace.Count}");
        foreach (var (address, value) in machine.Trace)
            Console.WriteLine($"  {address:X4} <- {value:X2}");

        for (ushort address = 0xFEDA; address <= 0xFEDC; address++)
            Console.Write($"{machine.Memory(address):X2} ");
        Console.WriteLine(" <- H.STKE");

        for (int i = 0; i < 5; i++) machine.Frame();
        Console.WriteLine($"after 5 frames: H.TIMI={machine.InterruptHook:X4}, PC={machine.Pc:X4}");
        return 0;
    }

    private static int ExtractDirect(string[] args)
    {
        if (args.Length < 2) return Usage("extract-direct <rom> <out.msxaudio> --profile profile.json");

        string profilePath = Value(args, "--profile") ?? throw new ArgumentException("missing --profile profile.json");
        var profile = JsonSerializer.Deserialize<RomExtractionProfile>(File.ReadAllText(profilePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("invalid profile");

        var options = new RomExtractionOptions { RomPath = args[0], Profile = profile };
        var result = DirectRomExtractor.Extract(options, Console.WriteLine);
        result.Library.Save(args[1]);
        Console.WriteLine($"{result.Library.Songs.Count} songs, {result.Library.Clips.Count} effects -> {args[1]}");
        return 0;
    }

    private static int Render(string[] args)
    {
        if (args.Length < 3) return Usage("render <pacote.msxaudio> <id|all> <saida> [--profile ...] [--rate 48000]");

        using var library = MsxAudioLibrary.Load(args[0]);
        var profile = AudioProfile.ByName(Value(args, "--profile"));
        int rate = int.TryParse(Value(args, "--rate") ?? "48000", out int parsed) ? parsed : 48000;
        string target = args[2];

        if (!args[1].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var song = library.Find(args[1]) ?? throw new ArgumentException($"id '{args[1]}' não existe");
            var audio = RenderSong(library, song, profile, rate);
            Wav.Write(target, audio);
            Console.WriteLine($"{song.Id}: {audio.Seconds:F2}s, pico {audio.Peak:F3} -> {target}");
            return 0;
        }

        Directory.CreateDirectory(target);
        foreach (var song in library.Songs.Concat(library.Clips))
        {
            var audio = RenderSong(library, song, profile, rate);
            Wav.Write(Path.Combine(target, song.Id + ".wav"), audio);
            Console.WriteLine($"{song.Id,-16} {audio.Seconds,7:F2}s  pico {audio.Peak:F3}");
        }

        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length < 2) return Usage("verify <vgm> <captura.wav>");

        var vgm = VgmFile.Load(args[0]);
        if (!vgm.HasLoop)
        {
            var loop = VgmLoopFinder.Find(vgm);
            if (loop is { } found) vgm.SetLoop(found.Start, found.Duration);
        }

        var reference = WavDecoder.Decode(File.ReadAllBytes(args[1]));
        var mono = reference.ToMono();
        var profile = AudioProfile.Original;
        var audio = MsxSoundRenderer.Render(vgm, profile, reference.SampleRate, 0, Math.Min(10, mono.Length / (double)reference.SampleRate));

        int offset = (int)Math.Round((vgm.HasLoop ? vgm.LoopStartSeconds : 0) * reference.SampleRate);
        int window = Math.Min(audio.Frames, (int)(0.5 * reference.SampleRate));
        int probe = Math.Min(audio.Frames - window, (int)(2.0 * reference.SampleRate));
        if (probe < 0) probe = 0;

        double best = double.MinValue;
        int bestOffset = 0;
        for (int delta = -(int)(0.05 * reference.SampleRate); delta <= (int)(0.05 * reference.SampleRate); delta++)
        {
            int start = offset + probe + delta;
            if (start < 0 || start + window > mono.Length) continue;
            double correlation = Correlation(audio, probe, mono, start, window);
            if (correlation > best)
            {
                best = correlation;
                bestOffset = delta;
            }
        }

        double rmsA = Rms(audio, probe, window);
        double rmsB = Rms(mono, offset + probe + bestOffset, window);
        Console.WriteLine($"correlação: {best:F4}  alinhamento: {bestOffset} amostras  RMS {rmsA:F4} / {rmsB:F4} (razão {rmsA / Math.Max(1e-9, rmsB):F3})");
        return 0;
    }

    private static int Unpack(string[] args)
    {
        if (args.Length < 2) return Usage("unpack <pacote.msxaudio> <pasta>");

        using var library = MsxAudioLibrary.Load(args[0]);
        Directory.CreateDirectory(args[1]);
        foreach (var song in library.Songs.Concat(library.Clips))
        {
            byte[]? data = library.ReadEntry(song.Entry);
            if (data == null) continue;
            string path = Path.Combine(args[1], song.Entry);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }

        File.WriteAllText(Path.Combine(args[1], "manifest.json"),
            JsonSerializer.Serialize(library.Manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"extraído em {args[1]}");
        return 0;
    }

    private static RenderedAudio RenderSong(MsxAudioLibrary library, MsxAudioSong song, AudioProfile profile, int rate)
    {
        var vgm = library.LoadVgm(song);
        if (song.Music)
        {
            if (song.Loop && song.LoopSeconds > 0)
                return MsxSoundRenderer.Render(vgm, profile, rate, song.LoopStart, song.LoopSeconds);
            return MsxSoundRenderer.RenderForPlayback(vgm, profile, rate);
        }

        return MsxSoundRenderer.Render(vgm, profile, rate, 0, MsxSoundRenderer.FindAudibleEnd(vgm));
    }

    private static int Usage(string text)
    {
        Console.Error.WriteLine("uso: " + text);
        return 1;
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static double Correlation(RenderedAudio audio, int aStart, float[] b, int bStart, int count)
    {
        double sum = 0, sa = 0, sb = 0;
        for (int i = 0; i < count; i++)
        {
            double x = (audio.Samples[(aStart + i) * 2] + audio.Samples[(aStart + i) * 2 + 1]) / 65536.0;
            double y = b[bStart + i];
            sum += x * y;
            sa += x * x;
            sb += y * y;
        }

        double denominator = Math.Sqrt(sa * sb);
        return denominator < 1e-12 ? 0 : sum / denominator;
    }

    private static double Rms(RenderedAudio audio, int start, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            double x = (audio.Samples[(start + i) * 2] + audio.Samples[(start + i) * 2 + 1]) / 65536.0;
            sum += x * x;
        }

        return Math.Sqrt(sum / Math.Max(1, count));
    }

    private static double Rms(float[] data, int start, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++) sum += data[start + i] * (double)data[start + i];
        return Math.Sqrt(sum / Math.Max(1, count));
    }

    /// <summary>Gravação WAV PCM 16 bits.</summary>
    private static class Wav
    {
        public static void Write(string path, RenderedAudio audio)
        {
            byte[] pcm = audio.ToPcmBytes();
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + pcm.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)audio.Channels);
            writer.Write(audio.SampleRate);
            writer.Write(audio.SampleRate * audio.Channels * 2);
            writer.Write((short)(audio.Channels * 2));
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }
    }
}
