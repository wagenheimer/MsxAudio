using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Wagenheimer.MsxAudio;

/// <summary>
/// The folder where extracted audio lives: <c>MSXMusic</c> — the same one for MSXScope and for every game,
/// on Windows, Linux and macOS.
/// </summary>
/// <remarks>
/// <para><b>Default:</b> the user's music folder (<see cref="Environment.SpecialFolder.MyMusic"/>, which follows
/// the XDG setting on Linux and is <c>~/Music</c> on macOS) with <c>MSXMusic</c> inside:</para>
/// <code>
/// Windows  C:\Users\&lt;you&gt;\Music\MSXMusic
/// Linux    /home/&lt;you&gt;/Music/MSXMusic
/// macOS    /Users/&lt;you&gt;/Music/MSXMusic
/// </code>
/// <para><b>Moving it</b> (another drive, a shared network path) needs no rebuild, in this order:</para>
/// <list type="number">
/// <item>the <c>MSXAUDIO_HOME</c> environment variable;</item>
/// <item><c>musicRoot</c> in <c>msxaudio.json</c> — next to the executable (portable install) or in
/// <c>%APPDATA%\MsxAudio\msxaudio.json</c> (<c>~/.config/MsxAudio/</c> on Linux,
/// <c>~/Library/Application Support/MsxAudio/</c> on macOS);</item>
/// <item>the default above.</item>
/// </list>
/// <para><b>One folder per game:</b> <see cref="ForGame"/> returns <c>&lt;Home&gt;/&lt;game&gt;</c> (e.g.
/// <c>MSXMusic/KingsValley2</c>), so every game writes and looks in the same place while the
/// <c>audio.msxaudio</c> packages of different games never collide.</para>
/// <para>The root is resolved once per process (the config file is read on first use) and never throws: a
/// folder that cannot be resolved falls back to the default.</para>
/// </remarks>
public static class MsxAudioPaths
{
    /// <summary>Name of the shared extracted-audio folder.</summary>
    public const string FolderName = "MSXMusic";

    /// <summary>Environment variable that points the root somewhere else.</summary>
    public const string EnvironmentVariable = "MSXAUDIO_HOME";

    /// <summary>Configuration file that may carry a <c>musicRoot</c> field.</summary>
    public const string ConfigFileName = "msxaudio.json";

    private static readonly Lazy<string> Root = new(ResolveHome, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The shared root (does not create it: see <see cref="EnsureHome"/>).</summary>
    public static string Home => Root.Value;

    /// <summary>
    /// The <c>msxaudio.json</c> in use: the one next to the executable (portable install) when it exists,
    /// otherwise the per-user one.
    /// </summary>
    public static string ConfigPath
    {
        get
        {
            string portable = Path.Combine(AppContext.BaseDirectory, ConfigFileName);
            return File.Exists(portable)
                ? portable
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MsxAudio", ConfigFileName);
        }
    }

    /// <summary>Creates the root and returns it — call it before writing or opening the folder.</summary>
    public static string EnsureHome()
    {
        try
        {
            Directory.CreateDirectory(Home);
        }
        catch (Exception)
        {
            // Read-only folder or a drive that is gone: whoever writes reports the real error.
        }

        return Home;
    }

    /// <summary>A game's folder inside the root (<c>&lt;Home&gt;/&lt;game&gt;</c>), created when missing.</summary>
    /// <param name="game">Short game name, e.g. <c>KingsValley2</c>.</param>
    public static string ForGame(string game)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(game);

        string folder = Path.Combine(Home, Sanitize(game));
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Replaces characters that are not allowed in a folder name with <c>_</c>.</summary>
    internal static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[name.Length];

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buffer[i] = Array.IndexOf(invalid, c) >= 0 || c is ':' or '/' or '\\' ? '_' : c;
        }

        string clean = new string(buffer).Trim().TrimEnd('.');
        return clean.Length > 0 ? clean : "games";
    }

    /// <summary>Resolves the root: environment, configuration file and, last, the user's music folder.</summary>
    private static string ResolveHome() =>
        FullPath(Resolve(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            ReadConfiguredRoot(),
            MusicFolder()));

    /// <summary>The user's music folder (XDG on Linux, <c>~/Music</c> on macOS).</summary>
    private static string MusicFolder()
    {
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (!string.IsNullOrWhiteSpace(music))
            return music;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 ? Path.Combine(profile, "Music") : AppContext.BaseDirectory;
    }

    /// <summary>Reads <c>musicRoot</c> from <c>msxaudio.json</c>; any problem returns <c>null</c>.</summary>
    private static string? ReadConfiguredRoot()
    {
        try
        {
            string path = ConfigPath;
            if (!File.Exists(path))
                return null;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("musicRoot") || property.NameEquals("MusicRoot"))
                {
                    string? value = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString()
                        : null;

                    return string.IsNullOrWhiteSpace(value) ? null : value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken configuration must never stop an extraction: fall back to the default.
        }

        return null;
    }

    /// <summary>Makes the path absolute and drops quotes/spaces (a path pasted from a shortcut or Explorer).</summary>
    private static string FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>
    /// The resolution used by the tests: untouched by the environment, the disk or the root already resolved
    /// by the process.
    /// </summary>
    internal static string Resolve(string? environmentValue, string? configuredRoot, string? musicFolder)
    {
        if (!string.IsNullOrWhiteSpace(environmentValue))
            return environmentValue.Trim().Trim('"');

        if (!string.IsNullOrWhiteSpace(configuredRoot))
            return configuredRoot.Trim().Trim('"');

        return Path.Combine(musicFolder ?? "Music", FolderName);
    }
}
