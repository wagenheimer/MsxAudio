using System;
using System.IO;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Entry point: <c>MsxAudioVisualizer [file.vgm | folder]</c>.
/// With no argument it scans the current directory for <c>.vgm</c> files.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        string? file = null;
        string dir = Directory.GetCurrentDirectory();

        foreach (string arg in args)
        {
            if (File.Exists(arg))
            {
                file = arg;
                dir = Path.GetDirectoryName(Path.GetFullPath(arg)) ?? dir;
            }
            else if (Directory.Exists(arg))
            {
                dir = Path.GetFullPath(arg);
            }
        }

        Console.WriteLine($"[Visualizer] file={file ?? "(none)"} dir={dir}");
        Directory.SetCurrentDirectory(dir);

        using var game = new VisualizerGame(file);
        game.Run();
    }
}
