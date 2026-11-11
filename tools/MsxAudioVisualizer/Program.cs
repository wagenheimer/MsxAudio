using System;
using System.IO;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Entry point.
/// <code>
/// MsxAudioVisualizer [file.vgm | folder] [--record out.gif|out.mp4] [--seconds 10] [--fps 30]
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
        int seconds = 10;
        int fps = 30;
        int width = 1280;
        int height = 720;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--record":
                    if (i + 1 < args.Length) record = args[++i];
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

        Console.WriteLine($"[Visualizer] file={file ?? "(none)"} dir={dir} record={record ?? "(live)"}");
        Directory.SetCurrentDirectory(dir);

        using var game = new VisualizerGame(file, record, seconds, fps, width, height);
        game.Run();
    }
}
