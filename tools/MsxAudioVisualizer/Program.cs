using System;
using System.IO;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Entry point.
/// <code>
/// MsxAudioVisualizer [file.vgm | folder] [options]
///
///   --record  out.gif|out.mp4   record the session (GIF uses the embedded encoder; MP4 muxes the
///                               audio WAV through ffmpeg when available)
///   --seconds N                 recording length (default 10)
///   --fps N                     recording frame rate (default 30)
///   --width / --height          initial window size
///   --profile name              mixing profile (original | stereo | remaster)
///   --view name                 start view: spectrum | roll | piano (--roll is kept as an alias)
///   --audio-only out.wav        render the audio only, without opening a window
/// </code>
/// With no positional argument it scans the current directory for <c>.vgm</c> files.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        string? file = null;
        string dir = Directory.GetCurrentDirectory();
        string? record = null;
        string? audioOnly = null;
        string profileName = "stereo";
        int seconds = 10;
        int fps = 30;
        int width = 1280;
        int height = 720;
        string? view = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--record":
                    if (i + 1 < args.Length) record = args[++i];
                    break;
                case "--audio-only":
                    if (i + 1 < args.Length) audioOnly = args[++i];
                    break;
                case "--seconds":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int s)) seconds = s;
                    break;
                case "--fps":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int f)) fps = f;
                    break;
                case "--width":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int iw)) width = iw;
                    break;
                case "--height":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int ih)) height = ih;
                    break;
                case "--roll":
                    view = "roll";
                    break;
                case "--view":
                    if (i + 1 < args.Length) view = args[++i];
                    break;
                case "--profile":
                    if (i + 1 < args.Length) profileName = args[++i];
                    break;
                default:
                    if (File.Exists(arg))
                    {
                        file = arg;
                        dir = Path.GetDirectoryName(Path.GetFullPath(arg)) ?? dir;
                    }
                    else if (Directory.Exists(arg))
                    {
                        dir = Path.GetFullPath(arg);
                    }

                    break;
            }
        }

        Console.WriteLine($"[Visualizer] file={file ?? "(none)"} dir={dir} record={record ?? (audioOnly is null ? "(live)" : "(audio only)")}");

        // Resolve output paths before the working directory is switched to the song folder, so a
        // relative --record/--audio-only path lands where the command was run.
        if (!string.IsNullOrWhiteSpace(record))
            record = Path.GetFullPath(record);
        if (!string.IsNullOrWhiteSpace(audioOnly))
            audioOnly = Path.GetFullPath(audioOnly);

        if (!string.IsNullOrWhiteSpace(audioOnly))
        {
            if (file is null)
            {
                Console.WriteLine("[Visualizer] --audio-only needs a .vgm file");
                return;
            }

            RenderAudioOnly(file, audioOnly, seconds, AudioProfile.ByName(profileName));
            return;
        }

        Directory.SetCurrentDirectory(dir);

        using var game = new VisualizerGame(file, record, seconds, fps, width, height, AudioProfile.ByName(profileName), view);
        game.Run();
    }

    /// <summary>Renders the song to a WAV without creating a window (same chips and profile).</summary>
    private static void RenderAudioOnly(string vgmPath, string wavPath, int seconds, AudioProfile profile)
    {
        try
        {
            var vgm = VgmFile.Load(vgmPath);
            double total = seconds > 0 ? Math.Min(seconds, vgm.TotalSeconds) : vgm.TotalSeconds;

            using var player = new VgmPlayer(vgm, loop: true, sampleRate: 48000, oversample: 4, profile: profile);
            using var wav = new WavWriter(wavPath, 48000, 2);

            var buffer = new float[2048 * 2];
            int remaining = (int)(total * 48000);
            while (remaining > 0)
            {
                int chunk = Math.Min(2048, remaining);
                player.Render(buffer, chunk);
                wav.Write(buffer, chunk);
                remaining -= chunk;
            }

            Console.WriteLine($"[Visualizer] audio written: {wavPath} ({total:0.0}s, {profile.Name})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Visualizer] audio-only failed: {ex.Message}");
        }
    }
}
