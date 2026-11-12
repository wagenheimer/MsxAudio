using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>Finds an installed <b>openMSX</b> (the exact reference path for extraction).</summary>
public static class OpenMsxLocator
{
    /// <summary>Environment variable that points at <c>openmsx.exe</c> (or its folder).</summary>
    public const string EnvironmentVariable = "MSXAUDIO_OPENMSX";

    /// <summary>Folder the installer puts openMSX in (per user, no admin rights needed).</summary>
    public static string InstallRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MsxAudio", "openmsx");

    /// <summary>Path to openMSX, or null when none is found.</summary>
    public static string? Find()
    {
        foreach (string candidate in Candidates())
        {
            string? exe = Resolve(candidate);
            if (exe is not null)
                return exe;
        }

        return null;
    }

    /// <summary>Accepts the executable itself or a folder that holds it (anywhere below).</summary>
    public static string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = path.Trim().Trim('"');
        string name = OperatingSystem.IsWindows() ? "openmsx.exe" : "openmsx";

        try
        {
            if (File.Exists(path))
                return Path.GetFileName(path).StartsWith("openmsx", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFullPath(path)
                    : null;

            if (!Directory.Exists(path))
                return null;

            string direct = Path.Combine(path, name);
            if (File.Exists(direct))
                return Path.GetFullPath(direct);

            return Directory.EnumerateFiles(path, name, SearchOption.AllDirectories)
                .OrderBy(f => f.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } env)
            yield return env;

        // What the installer put there (newest folder first).
        if (Directory.Exists(InstallRoot))
        {
            foreach (string folder in Directory.EnumerateDirectories(InstallRoot).OrderByDescending(f => f))
                yield return folder;
        }

        yield return Path.Combine(AppContext.BaseDirectory, "omsx");
        yield return Path.Combine(AppContext.BaseDirectory, "openmsx");

        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programs.Length > 0)
            yield return Path.Combine(programs, "openMSX");

        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (pathVariable is not null)
        {
            foreach (string folder in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                yield return folder;
        }
    }
}

/// <summary>The openMSX release the installer would download.</summary>
/// <param name="Tag">Release tag, e.g. <c>RELEASE_22_0</c>.</param>
/// <param name="AssetName">File name of the portable Windows build.</param>
/// <param name="Url">Direct download address.</param>
/// <param name="Size">Size in bytes (the download is checked against it).</param>
/// <param name="Sha256">Hash published by GitHub for the file, when there is one.</param>
public sealed record OpenMsxRelease(string Tag, string AssetName, string Url, long Size, string? Sha256);

/// <summary>
/// Downloads and unpacks the portable openMSX build for Windows into a per-user folder. Nothing is
/// bundled with this library (openMSX is GPL): it is fetched from the project's own releases when
/// the user asks for it.
/// </summary>
public static class OpenMsxInstaller
{
    private const string LatestRelease = "https://api.github.com/repos/openMSX/openMSX/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // No timeout on the client: the build is ~9 MB, but a slow line must not abort it; the caller
        // cancels through the token. A generic client name only (nothing about the user or the project).
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MsxAudio-Installer/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>True where a portable openMSX build exists (Windows x64).</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
        System.Runtime.InteropServices.Architecture.X64;

    /// <summary>Looks up the latest release and the portable Windows asset.</summary>
    public static async Task<OpenMsxRelease> GetLatestAsync(CancellationToken token = default)
    {
        using HttpResponseMessage response = await Http.GetAsync(LatestRelease, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));

        JsonElement root = document.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "latest";

        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? string.Empty;

            // "openmsx-22.0-windows-vc-x64-bin.zip": not the installer (-msi) nor the debug symbols (-pdb).
            if (!name.EndsWith("windows-vc-x64-bin.zip", StringComparison.OrdinalIgnoreCase))
                continue;

            string? digest = asset.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null;
            string? sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..]
                : null;

            return new OpenMsxRelease(tag, name, asset.GetProperty("browser_download_url").GetString()!,
                asset.GetProperty("size").GetInt64(), sha);
        }

        throw new InvalidDataException("the latest openMSX release has no portable Windows build");
    }

    /// <summary>
    /// Downloads <paramref name="release"/> (to a <c>.part</c> file), checks its size and hash,
    /// unpacks it under <see cref="OpenMsxLocator.InstallRoot"/> and returns the path of openmsx.exe.
    /// </summary>
    public static async Task<string> InstallAsync(OpenMsxRelease release, IProgress<(long Done, long Total)>? progress,
        CancellationToken token = default)
    {
        string folder = Path.Combine(OpenMsxLocator.InstallRoot, release.Tag);
        Directory.CreateDirectory(OpenMsxLocator.InstallRoot);

        string part = Path.Combine(OpenMsxLocator.InstallRoot, release.AssetName + ".part");

        try
        {
            using (HttpResponseMessage response = await Http
                       .GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using FileStream target = File.Create(part);

                var buffer = new byte[81920];
                long done = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    done += read;
                    progress?.Report((done, release.Size));
                }
            }

            long length = new FileInfo(part).Length;
            if (length != release.Size)
                throw new InvalidDataException($"download incomplete ({length} of {release.Size} bytes)");

            if (release.Sha256 is { Length: > 0 } expected)
            {
                await using FileStream check = File.OpenRead(part);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(check, token).ConfigureAwait(false));

                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("the download does not match the published SHA-256");
            }

            // Unpack next to the final folder first, so a failed unzip never leaves a half install.
            string staging = folder + ".new";
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);

            ZipFile.ExtractToDirectory(part, staging);

            if (Directory.Exists(folder))
                Directory.Delete(folder, true);

            Directory.Move(staging, folder);
        }
        finally
        {
            try { File.Delete(part); } catch (IOException) { /* leave it, the next run overwrites it */ }
        }

        return OpenMsxLocator.Resolve(folder)
               ?? throw new InvalidDataException("openmsx.exe was not found in the downloaded build");
    }
}

/// <summary>
/// Records what a cartridge plays by running it in openMSX (all the chips it has, exactly), with the
/// keys of a script pressed along the way. The result is the VGM openMSX wrote.
/// </summary>
public static class OpenMsxCapture
{
    /// <summary>Seconds of emulated time the BIOS needs before the cartridge starts.</summary>
    private const double BootSeconds = 4;

    /// <summary>MSX2+ C-BIOS machine: it has MSX-MUSIC, so FM games are heard too.</summary>
    public const string DefaultMachine = "C-BIOS_MSX2+_JP";

    /// <summary>Runs openMSX and returns the recorded VGM (copied to <paramref name="outputVgm"/>).</summary>
    public static string Record(string openMsxPath, string romPath, string outputVgm, double seconds,
        IReadOnlyList<KeyPress> keys, string workDirectory, Action<string>? log, CancellationToken token)
    {
        string work = Path.GetFullPath(workDirectory);
        string vgmDirectory = Path.Combine(work, "vgm");
        if (Directory.Exists(vgmDirectory))
            Directory.Delete(vgmDirectory, true);

        Directory.CreateDirectory(vgmDirectory);

        string script = Path.Combine(work, "capture.tcl");
        File.WriteAllText(script, BuildScript(vgmDirectory, seconds, keys), Encoding.ASCII);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = openMsxPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-machine");
        startInfo.ArgumentList.Add(DefaultMachine);
        startInfo.ArgumentList.Add("-cart");
        startInfo.ArgumentList.Add(Path.GetFullPath(romPath));
        startInfo.ArgumentList.Add("-script");
        startInfo.ArgumentList.Add(script);

        log?.Invoke($"openMSX: recording {seconds:0} s of {Path.GetFileName(romPath)}");

        using System.Diagnostics.Process process =
            System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("could not start openMSX");

        // Emulation runs unthrottled; 10 minutes is far more than any capture needs.
        DateTime limit = DateTime.UtcNow.AddMinutes(10);
        while (!process.WaitForExit(300))
        {
            if (token.IsCancellationRequested || DateTime.UtcNow > limit)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { /* already gone */ }
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("openMSX did not finish in 10 minutes");
            }
        }

        string? recorded = Directory.GetFiles(vgmDirectory, "*.vgm").OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();

        if (recorded is null)
            throw new InvalidDataException("openMSX did not record anything (is the machine installed? " +
                                           "check that the ROM runs in openMSX)");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputVgm))!);
        File.Copy(recorded, outputVgm, overwrite: true);
        log?.Invoke($"openMSX: wrote {Path.GetFileName(outputVgm)} ({new FileInfo(outputVgm).Length / 1024} KB)");
        return outputVgm;
    }

    private static string BuildScript(string vgmDirectory, double seconds, IReadOnlyList<KeyPress> keys)
    {
        string vgmPath = vgmDirectory.Replace('\\', '/');
        var builder = new StringBuilder();

        builder.AppendLine("set throttle off");
        builder.AppendLine("set renderer none");
        builder.AppendLine("proc tap {row mask {dur 0.12}} { keymatrixdown $row $mask; after time $dur [list keymatrixup $row $mask] }");
        builder.AppendLine("proc at {t script} { after time $t $script }");

        foreach (KeyPress key in keys)
        {
            (int row, int mask) = ToMatrix(key);
            builder.AppendLine($"at {Seconds(BootSeconds + key.Seconds)} [list tap {row} {mask}]");
        }

        // Start with every chip the machine has; fall back to fewer when one is missing.
        builder.AppendLine($"at {Seconds(BootSeconds - 0.5)} {{");
        builder.AppendLine("  catch { vgm_rec prefix \"capture\" }");
        builder.AppendLine($"  set ::vgm::directory {{{vgmPath}}}");
        builder.AppendLine("  catch { ::vgm::set_next_filename }");
        builder.AppendLine("  foreach chips {{PSG SCC MSX-MUSIC} {PSG SCC} {PSG}} {");
        builder.AppendLine("    if {![catch { vgm_rec start {*}$chips }]} { break }");
        builder.AppendLine("  }");
        builder.AppendLine("}");
        builder.AppendLine($"at {Seconds(BootSeconds + seconds)} {{ catch {{ vgm_rec stop }}; exit }}");
        return builder.ToString();
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Joystick presses become the keyboard keys that do the same in most games.</summary>
    private static (int Row, int Mask) ToMatrix(KeyPress key)
    {
        if (key.Row != KeyPress.JoystickRow)
            return (key.Row, key.Mask);

        // The press stores the joystick bit: 0 up, 1 down, 2 left, 3 right, 4/5 the buttons.
        return key.Mask switch
        {
            0 => (8, 0x20),
            1 => (8, 0x40),
            2 => (8, 0x10),
            3 => (8, 0x80),
            _ => (8, 0x01),
        };
    }
}
