using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// What the visualizer remembers between runs. Stored as JSON next to the executable when a
/// <c>visualizer.json</c> is already there (portable mode), otherwise under
/// <c>%APPDATA%\MsxAudio\visualizer.json</c>.
/// </summary>
public sealed class VisualizerSettings
{
    public int Version { get; set; } = 1;
    public string? LastFolder { get; set; }
    public bool Recursive { get; set; }
    public List<string> RecentFolders { get; set; } = new();
    public string Profile { get; set; } = "stereo";
    public string View { get; set; } = "spectrum";
    public float Volume { get; set; } = 1f;
    public bool Loop { get; set; } = true;
    public bool AutoNext { get; set; } = true;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
    public bool PixelPerfect { get; set; }

    private const int MaxRecent = 8;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Portable file next to the executable wins; otherwise the per-user AppData path.</summary>
    public static string ResolvePath()
    {
        string portable = Path.Combine(AppContext.BaseDirectory, "visualizer.json");
        if (File.Exists(portable))
            return portable;

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MsxAudio", "visualizer.json");
    }

    /// <summary>Reads the settings; any problem (missing, corrupt, locked) falls back to defaults.</summary>
    public static VisualizerSettings Load()
    {
        try
        {
            string path = ResolvePath();
            if (!File.Exists(path))
                return new VisualizerSettings();

            return JsonSerializer.Deserialize<VisualizerSettings>(File.ReadAllText(path), Options)
                   ?? new VisualizerSettings();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Visualizer] settings ignored: {ex.Message}");
            return new VisualizerSettings();
        }
    }

    /// <summary>Writes atomically (temp file + move) so a crash never leaves half a JSON.</summary>
    public void Save()
    {
        try
        {
            string path = ResolvePath();
            string? folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Visualizer] settings not saved: {ex.Message}");
        }
    }

    /// <summary>Moves <paramref name="folder"/> to the front of the recent list (max 8).</summary>
    public void AddRecent(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;

        RecentFolders.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);

        while (RecentFolders.Count > MaxRecent)
            RecentFolders.RemoveAt(RecentFolders.Count - 1);
    }
}
