using System;
using System.Collections.Generic;
using System.Globalization;
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

/// <summary>Package CLI: import VGMs, extract from a ROM, render/verify and unpack.</summary>
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
                "capture" => Capture(args.Skip(1).ToArray()),
                "render" => Render(args.Skip(1).ToArray()),
                "verify" => Verify(args.Skip(1).ToArray()),
                "unpack" => Unpack(args.Skip(1).ToArray()),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            MsxAudioTool - MSX audio (PSG + SCC) in .msxaudio packages

            usage:
              import <vgm-dir> <out.msxaudio> [--title T] [--clips]
                    convert VGMs into a package (detects song loops)
              extract <rom> <out.msxaudio> --profile profile.json [--openmsx path]
                    drive openMSX to record a ROM's VGMs and build the package
              extract-direct <rom> <out.msxaudio> --profile profile.json
                    run the cartridge driver in process (no external emulator, experimental)
              probe <rom>
                    boot the ROM in the built-in machine and dump the hook state (diagnostics)
              capture <rom> <out.vgm> [--seconds N] [--mapper ...]
                    boot the ROM and record whatever the driver plays (no profile needed)
              info <package.msxaudio>
                    list songs/effects, loops and clocks
              render <package.msxaudio> <id|all> <out-dir-or-file> [--profile original|stereo|remaster] [--rate 48000]
                    render one song/effect to WAV - "all" writes a folder
              verify <vgm> <capture.wav>
                    compare the render with an emulator capture (correlation/RMS/offset)
              unpack <package.msxaudio> <dir>
                    extract the VGMs + manifest.json from the package

            mixing profiles: original (faithful), stereo (spread channels), remaster (reverb + filters)
            """);
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        return 1;
    }

    private static int Info(string[] args)
    {
        if (args.Length < 1) return Usage("info <package.msxaudio>");

        using var library = MsxAudioLibrary.Load(args[0]);
        Console.WriteLine($"{library.Manifest.Title}  ({library.Manifest.Format})");
        if (!string.IsNullOrEmpty(library.Manifest.Source))
            Console.WriteLine($"source: {library.Manifest.Source}");
        Console.WriteLine($"PSG {library.Manifest.PsgClock:F1} Hz | SCC {library.Manifest.SccClock:F1} Hz");
        Console.WriteLine($"\nsongs ({library.Songs.Count}):");
        foreach (var song in library.Songs)
        {
            string loop = song.Loop ? $"loop {song.LoopStart:F3}s + {song.LoopSeconds:F3}s" : "no loop";
            Console.WriteLine($"  {song.Id,-16} {song.Name,-24} {song.Seconds,7:F2}s  {loop}");
        }

        Console.WriteLine($"\neffects ({library.Clips.Count}):");
        foreach (var clip in library.Clips)
            Console.WriteLine($"  {clip.Id,-16} {clip.Name,-24} {clip.Seconds,7:F2}s");
        return 0;
    }

    private static int Import(string[] args)
    {
        if (args.Length < 2) return Usage("import <vgm-dir> <out.msxaudio> [--title T] [--clips]");

        string directory = args[0];
        string output = args[1];
        string title = Value(args, "--title") ?? Path.GetFileNameWithoutExtension(output);
        bool clips = args.Contains("--clips");

        var library = VgmImporter.Import(directory, title, music: !clips, log: Console.WriteLine);
        library.Manifest.Source = directory;
        library.Save(output);
        Console.WriteLine($"{library.Songs.Count} songs, {library.Clips.Count} effects -> {output}");
        return 0;
    }

    private static int Extract(string[] args)
    {
        if (args.Length < 2) return Usage("extract <rom> <out.msxaudio> --profile profile.json [--openmsx path]");

        var profile = LoadProfile(args);
        var options = new RomExtractionOptions
        {
            RomPath = args[0],
            Profile = profile,
            OpenMsxPath = Value(args, "--openmsx") ?? "openmsx",
            WorkDirectory = Value(args, "--work") ?? Path.Combine(Path.GetTempPath(), "msxaudio-extract"),
        };

        var result = OpenMsxRomExtractor.Extract(options, Console.WriteLine);
        result.Library.Save(args[1]);
        Console.WriteLine($"{result.Library.Songs.Count} songs, {result.Library.Clips.Count} effects -> {args[1]}");
        return 0;
    }

    private static int ExtractDirect(string[] args)
    {
        if (args.Length < 2) return Usage("extract-direct <rom> <out.msxaudio> --profile profile.json");

        var profile = LoadProfile(args);
        var options = new RomExtractionOptions { RomPath = args[0], Profile = profile };

        var result = DirectRomExtractor.Extract(options, Console.WriteLine);
        result.Library.Save(args[1]);
        Console.WriteLine($"{result.Library.Songs.Count} songs, {result.Library.Clips.Count} effects -> {args[1]}");
        return 0;
    }

    /// <summary>
    /// Boots the ROM and records whatever the sound driver plays, without a profile. Useful to grab
    /// a title theme (or to check that a cartridge's driver works) for any ROM. <c>--press</c> can
    /// simulate keys (<c>row:mask@seconds</c>, e.g. <c>8:1@2</c> = Space after 2 s).
    /// </summary>
    private static int Capture(string[] args)
    {
        if (args.Length < 2)
            return Usage("capture <rom> <out.vgm> [--seconds N] [--mapper ...] [--press row:mask@seconds,...]");

        byte[] rom = File.ReadAllBytes(args[0]);
        var mapper = RomMapperNames.Parse(Value(args, "--mapper"));
        double seconds = double.TryParse(Value(args, "--seconds") ?? "10", out double parsed) ? parsed : 10;

        var machine = new MiniMsx(rom, mapper);
        bool hooked = machine.Boot();
        var info = machine.CartridgeInfo;

        Console.WriteLine($"boot {hooked}, mapper={RomMapperNames.Name(info.Mapper)}{(info.Scc ? " +SCC" : "")}, " +
                          $"KEYI={machine.KeyHook:X4}, TIMI={machine.TimerHook:X4}");

        var presses = new List<(int Frame, int Row, int Mask)>();
        foreach (string part in (Value(args, "--press") ?? string.Empty)
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] at = part.Split('@');
            string[] rowMask = at[0].Split(':');
            if (rowMask.Length != 2 ||
                !int.TryParse(rowMask[0], out int row) ||
                !int.TryParse(rowMask[1], out int mask))
                continue;

            double time = 1.0;
            if (at.Length > 1)
                double.TryParse(at[1], NumberStyles.Float, CultureInfo.InvariantCulture, out time);

            presses.Add(((int)Math.Round(time * 60), row, mask));
            Console.WriteLine($"press {row}:{mask} at {time:0.0}s");
        }

        machine.ResetCapture();

        bool trace = (Value(args, "--trace") ?? "").Equals("1", StringComparison.OrdinalIgnoreCase);
        int frames = Math.Max(1, (int)Math.Round(seconds * 60));
        int budget = int.TryParse(Value(args, "--budget") ?? "", out int parsedBudget) ? parsedBudget : 200_000;

        for (int frame = 0; frame < frames; frame++)
        {
            foreach (var press in presses)
            {
                if (frame == press.Frame) machine.PressKey(press.Row, press.Mask, true);
                else if (frame == press.Frame + 12) machine.PressKey(press.Row, press.Mask, false);
            }

            machine.Frame(budget);

            if (trace && frame % 60 == 0)
            {
                Console.WriteLine($"  t={frame / 60,3}s E000={machine.Memory(0xE000):X2} " +
                                  $"E002={machine.Memory(0xE002):X2} E003={machine.Memory(0xE003):X2} " +
                                  $"E005={machine.Memory(0xE005):X2} E008={machine.Memory(0xE008):X2} " +
                                  $"E009={machine.Memory(0xE009):X2} events={machine.EventCount}" +
                                  $" hookPC={machine.HookPc:X4}" +
                                  (machine.HookBudgetExhausted ? " EXHAUSTED" : ""));
            }
        }

        var vgm = machine.BuildVgm("capture", frames * 735);
        File.WriteAllBytes(args[1], VgmWriter.Write(vgm));

        var loop = VgmLoopFinder.Find(vgm);
        Console.WriteLine($"{vgm.Events.Count} writes, {vgm.TotalSeconds:0.0}s -> {args[1]}" +
                          (loop is { } found ? $"; loop {found.Start:0.000}s + {found.Duration:0.000}s" : "; no loop detected"));
        return 0;
    }

    private static int Probe(string[] args)    {
        if (args.Length < 1) return Usage("probe <rom> [--mapper linear|konami|konami-scc|ascii8|ascii16]");

        byte[] rom = File.ReadAllBytes(args[0]);
        var mapper = RomMapperNames.Parse(Value(args, "--mapper"));
        var machine = new MiniMsx(rom, mapper) { TraceWrites = true };
        var info = machine.CartridgeInfo;

        Console.WriteLine($"rom {rom.Length} bytes; header INIT={rom[2] | (rom[3] << 8):X4}; " +
                          $"mapper={RomMapperNames.Name(info.Mapper)}{(info.Scc ? " +SCC" : "")}");
        Console.WriteLine($"after boot: {machine.Boot()}, PC={machine.Pc:X4}, " +
                          $"KEYI={machine.KeyHook:X4}, TIMI={machine.TimerHook:X4}");
        Console.WriteLine($"high-RAM writes during boot: {machine.Trace.Count}");
        foreach (var (address, value) in machine.Trace)
            Console.WriteLine($"  {address:X4} <- {value:X2}");

        for (int i = 0; i < 5; i++) machine.Frame();
        Console.WriteLine($"after 5 frames: KEYI={machine.KeyHook:X4}, TIMI={machine.TimerHook:X4}, PC={machine.Pc:X4}");

        // Where does the interrupt hook spend its budget? A driver that never returns is waiting on
        // hardware the minimal machine does not model, and the hot addresses show where.
        var profile = machine.ProfileHook(400_000);
        Console.WriteLine($"hook profile: returned={profile.Returned}, stopped at {profile.Pc:X4}");
        foreach (var (address, hits) in profile.Hot)
            Console.WriteLine($"  {address:X4}: {hits} samples");

        return 0;
    }

    private static int Render(string[] args)
    {
        if (args.Length < 3) return Usage("render <package.msxaudio> <id|all> <out> [--profile ...] [--rate 48000]");

        using var library = MsxAudioLibrary.Load(args[0]);
        var profile = AudioProfile.ByName(Value(args, "--profile"));
        int rate = int.TryParse(Value(args, "--rate") ?? "48000", out int parsed) ? parsed : 48000;
        string target = args[2];

        if (!args[1].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var song = library.Find(args[1]) ?? throw new ArgumentException($"id '{args[1]}' does not exist");
            var audio = RenderSong(library, song, profile, rate);
            Wav.Write(target, audio);
            Console.WriteLine($"{song.Id}: {audio.Seconds:F2}s, peak {audio.Peak:F3} -> {target}");
            return 0;
        }

        Directory.CreateDirectory(target);
        foreach (var song in library.Songs.Concat(library.Clips))
        {
            var audio = RenderSong(library, song, profile, rate);
            Wav.Write(Path.Combine(target, song.Id + ".wav"), audio);
            Console.WriteLine($"{song.Id,-16} {audio.Seconds,7:F2}s  peak {audio.Peak:F3}");
        }

        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length < 2) return Usage("verify <vgm> <capture.wav>");

        var vgm = VgmFile.Load(args[0]);
        if (!vgm.HasLoop)
        {
            var loop = VgmLoopFinder.Find(vgm);
            if (loop is { } found) vgm.SetLoop(found.Start, found.Duration);
        }

        var reference = WavDecoder.Decode(File.ReadAllBytes(args[1]));
        var mono = reference.ToMono();
        var profile = AudioProfile.Original;
        var audio = MsxSoundRenderer.Render(vgm, profile, reference.SampleRate, 0,
            Math.Min(10, mono.Length / (double)reference.SampleRate));

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
        Console.WriteLine($"correlation: {best:F4}  alignment: {bestOffset} samples  RMS {rmsA:F4} / {rmsB:F4} (ratio {rmsA / Math.Max(1e-9, rmsB):F3})");
        return 0;
    }

    private static int Unpack(string[] args)
    {
        if (args.Length < 2) return Usage("unpack <package.msxaudio> <dir>");

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
        Console.WriteLine($"extracted to {args[1]}");
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

    private static RomExtractionProfile LoadProfile(string[] args)
    {
        string profilePath = Value(args, "--profile") ?? throw new ArgumentException("missing --profile profile.json");
        return JsonSerializer.Deserialize<RomExtractionProfile>(File.ReadAllText(profilePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("invalid profile");
    }

    private static int Usage(string text)
    {
        Console.Error.WriteLine("usage: " + text);
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

    /// <summary>Writes 16-bit PCM WAV.</summary>
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
