#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>Uma release do MSXScope: a tag, o arquivo da plataforma e o hash publicado.</summary>
/// <param name="Tag">Tag da release (ex.: <c>v1.1.0</c>).</param>
/// <param name="AssetName">Nome do arquivo publicado.</param>
/// <param name="Url">URL de download.</param>
/// <param name="Size">Tamanho em bytes.</param>
/// <param name="Sha256">Hash publicado pelo GitHub (hex), quando existe.</param>
public sealed record MsxScopeRelease(string Tag, string AssetName, string Url, long Size, string? Sha256);

/// <summary>
/// Instala o **MSXScope** (o player/visualizer do pacote) sob demanda, a partir das releases do
/// prÃ³prio repositÃ³rio â€” nada Ã© distribuÃ­do junto: o usuÃ¡rio sÃ³ baixa quando pede para ouvir.
///
/// <para>Mesmo desenho do <see cref="OpenMsxInstaller"/>: baixa para <c>.part</c>, confere tamanho e
/// SHA-256 publicado e descompacta em <c>%APPDATA%\MSXScope\&lt;tag&gt;</c>.</para>
/// </summary>
public static class MsxScopeInstaller
{
    private const string LatestRelease = "https://api.github.com/repos/wagenheimer/MsxAudio/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Pasta onde as versÃµes ficam instaladas (uma subpasta por tag).</summary>
    public static string InstallRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MSXScope");


    private static HttpClient CreateClient()
    {
        // Sem timeout no cliente: a release tem ~37 MB e uma linha lenta nÃ£o pode abortar; quem
        // cancela Ã© o token. User-agent genÃ©rico (nada sobre o usuÃ¡rio ou o projeto).
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MsxAudio-Installer/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Plataforma com build publicado (Windows, Linux e macOS, x64 ou arm64).</summary>
    public static bool IsSupported => AssetName is not null;

    /// <summary>
    /// Arquivo da release para o SO/arquitetura atual:
    /// <c>MSXScope-win-x64.zip</c>, <c>MSXScope-linux-arm64.tar.gz</c>, <c>MSXScope-osx-x64.tar.gz</c>â€¦
    /// </summary>
    public static string? AssetName
    {
        get
        {
            string? architecture =
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.X64 => "x64",
                    System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                    _ => null,
                };

            if (architecture is null)
                return null;

            if (OperatingSystem.IsWindows())
                return $"MSXScope-win-{architecture}.zip";

            if (OperatingSystem.IsLinux())
                return $"MSXScope-linux-{architecture}.tar.gz";

            if (OperatingSystem.IsMacOS())
                return $"MSXScope-osx-{architecture}.tar.gz";

            return null;
        }
    }

    /// <summary>Nome do executÃ¡vel instalado (sem extensÃ£o fora do Windows).</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "MSXScope.exe" : "MSXScope";

    /// <summary>O executÃ¡vel jÃ¡ instalado (a versÃ£o mais recente presente), ou nulo.</summary>
    public static string? Locate()
    {
        if (!Directory.Exists(InstallRoot))
            return null;

        foreach (string directory in Directory.GetDirectories(InstallRoot)
                     .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string? exe = FindExecutable(directory);
            if (exe is not null)
                return exe;
        }

        return null;
    }

    /// <summary>Procura a release mais recente e o arquivo da arquitetura atual.</summary>
    public static async Task<MsxScopeRelease> GetLatestAsync(CancellationToken token = default)
    {
        using HttpResponseMessage response = await Http.GetAsync(LatestRelease, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));

        JsonElement root = document.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "latest";
        string wanted = AssetName ?? throw new PlatformNotSupportedException("no MSXScope build for this platform");

        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                continue;

            string? digest = asset.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null;
            string? sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest[7..]
                : null;

            return new MsxScopeRelease(tag, name, asset.GetProperty("browser_download_url").GetString()!,
                asset.GetProperty("size").GetInt64(), sha);
        }

        throw new InvalidDataException($"the latest release has no {wanted}");
    }

    /// <summary>
    /// Baixa e instala <paramref name="release"/>, devolvendo o caminho do executÃ¡vel.
    /// </summary>
    public static async Task<string> InstallAsync(MsxScopeRelease release,
        IProgress<(long Done, long Total)>? progress, CancellationToken token = default)
    {
        string folder = Path.Combine(InstallRoot, release.Tag);
        Directory.CreateDirectory(InstallRoot);

        string part = Path.Combine(InstallRoot, release.AssetName + ".part");

        try
        {
            using (HttpResponseMessage response = await Http
                       .GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, token)
                       .ConfigureAwait(false))
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

            Verify(part, release);

            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);

            if (release.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ZipFile.ExtractToDirectory(part, folder);
            else
                ExtractTarGz(part, folder);

            string? exe = FindExecutable(folder);
            if (exe is null)
                throw new InvalidDataException("the archive has no " + ExecutableName);

            // Linux/macOS: o tar nÃ£o carrega a permissÃ£o de execuÃ§Ã£o no Windows; garante +x.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(exe,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            return exe;
        }
        finally
        {
            if (File.Exists(part))
                File.Delete(part);
        }
    }

    private static void Verify(string path, MsxScopeRelease release)
    {
        var info = new FileInfo(path);
        if (release.Size > 0 && info.Length != release.Size)
            throw new InvalidDataException($"size mismatch: {info.Length} != {release.Size}");

        if (string.IsNullOrWhiteSpace(release.Sha256))
            return;

        using FileStream stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("sha256 mismatch");
    }

    private static string WantedAsset()
    {
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        return architecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "MSXScope-win-arm64.zip"
            : "MSXScope-win-x64.zip";
    }

    private static string? FindExecutable(string root)
    {
        string direct = Path.Combine(root, ExecutableName);
        if (File.Exists(direct))
            return direct;

        return Directory.Exists(root)
            ? Directory.GetFiles(root, ExecutableName, SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    /// <summary>Descompacta <c>.tar.gz</c> (Linux/macOS) preservando a estrutura de pastas.</summary>
    private static void ExtractTarGz(string archive, string folder)
    {
        using FileStream file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        System.Formats.Tar.TarFile.ExtractToDirectory(gzip, folder, overwriteFiles: true);
    }
}
