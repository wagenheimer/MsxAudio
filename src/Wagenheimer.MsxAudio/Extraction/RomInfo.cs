using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Xml;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>What is known about a cartridge image before running it.</summary>
public sealed class RomInfo
{
    /// <summary>Image size in bytes.</summary>
    public int Size { get; init; }

    /// <summary>SHA-1 of the image (lower-case hex), the key of the openMSX software database.</summary>
    public string Sha1 { get; init; } = string.Empty;

    /// <summary>Mapper the extractor will use.</summary>
    public RomMapper Mapper { get; init; }

    /// <summary>Where the mapper came from: <c>database</c> or <c>heuristic</c>.</summary>
    public string MapperSource { get; init; } = "heuristic";

    /// <summary>True when the SCC window exists for this mapper.</summary>
    public bool Scc { get; init; }

    /// <summary>True when the image starts with the "AB" cartridge header.</summary>
    public bool HasHeader { get; init; }

    /// <summary>INIT address from the header (0 = none).</summary>
    public int InitAddress { get; init; }

    /// <summary>Address the image loads at (0x4000, or 0x8000 for a page-2 cartridge).</summary>
    public int LoadBase { get; init; }

    /// <summary>One-line description for the UI, e.g. <c>128 KB · Konami SCC (database) · header OK</c>.</summary>
    public string Summary
    {
        get
        {
            string size = Size >= 1024 ? $"{Size / 1024} KB" : $"{Size} B";
            string mapper = RomMapperNames.Name(Mapper) + (Scc ? " + SCC" : "");
            string header = HasHeader ? $"INIT {InitAddress:X4}" : "no AB header";
            return $"{size} · {mapper} ({MapperSource}) · {header}";
        }
    }

    /// <summary>Reads a ROM (or a <c>.zip</c> holding one) and analyses it.</summary>
    public static RomInfo Analyze(string path, string? databasePath = null) =>
        Analyze(File.ReadAllBytes(RomFile.Resolve(path)), databasePath);

    /// <summary>Analyses an image: size, hash, mapper (database first, heuristic otherwise) and header.</summary>
    public static RomInfo Analyze(byte[] rom, string? databasePath = null)
    {
        ArgumentNullException.ThrowIfNull(rom);

        string sha1 = Convert.ToHexString(SHA1.HashData(rom)).ToLowerInvariant();
        RomMapper mapper = RomMapperDatabase.Lookup(sha1, databasePath);
        string source = "database";

        if (mapper == RomMapper.Auto)
        {
            mapper = rom.Length >= 0x4000 ? RomMapperNames.Guess(rom) : RomMapper.Linear;
            source = "heuristic";
        }

        bool page2 = mapper == RomMapper.Linear && Cartridge.IsPage2Rom(rom);
        int baseAddress = page2 ? 0x8000 : 0x4000;
        bool header = rom.Length >= 4 && rom[0] == 'A' && rom[1] == 'B';

        return new RomInfo
        {
            Size = rom.Length,
            Sha1 = sha1,
            Mapper = mapper,
            MapperSource = source,
            Scc = mapper == RomMapper.KonamiScc,
            HasHeader = header,
            InitAddress = header ? rom[2] | (rom[3] << 8) : 0,
            LoadBase = baseAddress,
        };
    }
}

/// <summary>
/// Looks a ROM up in an openMSX <c>softwaredb.xml</c> (by SHA-1). When the database is not found the
/// caller falls back to the heuristic in <see cref="RomMapperNames.Guess"/>.
/// </summary>
public static class RomMapperDatabase
{
    /// <summary>Environment variable that points at a <c>softwaredb.xml</c>.</summary>
    public const string EnvironmentVariable = "MSXAUDIO_SOFTWAREDB";

    /// <summary>Folders searched for <c>softwaredb.xml</c> when no path is given.</summary>
    public static IEnumerable<string> SearchPaths()
    {
        string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
            yield return env;

        yield return Path.Combine(AppContext.BaseDirectory, "softwaredb.xml");

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (documents.Length > 0)
            yield return Path.Combine(documents, "openMSX", "share", "softwaredb.xml");

        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (programs.Length > 0)
            yield return Path.Combine(programs, "openMSX", "share", "softwaredb.xml");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0)
        {
            yield return Path.Combine(home, ".openMSX", "share", "softwaredb.xml");
            yield return Path.Combine(home, ".local", "share", "openmsx", "share", "softwaredb.xml");
        }

        yield return "/usr/share/openmsx/softwaredb.xml";
        yield return "/usr/local/share/openmsx/softwaredb.xml";
    }

    /// <summary>
    /// Returns the mapper the database lists for this hash, or <see cref="RomMapper.Auto"/> when there
    /// is no database, no entry, or the entry names a mapper this machine does not model.
    /// </summary>
    public static RomMapper Lookup(string sha1, string? databasePath = null)
    {
        IEnumerable<string> paths = string.IsNullOrWhiteSpace(databasePath)
            ? SearchPaths()
            : new[] { databasePath };

        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                    return Scan(path, sha1);
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
                // A broken database must never break an extraction: fall through to the heuristic.
            }
        }

        return RomMapper.Auto;
    }

    /// <summary>Streams the database looking for <paramref name="sha1"/> (stops at the first hit).</summary>
    internal static RomMapper Scan(string path, string sha1)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        };

        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, settings);

        string? type = null;
        string? hash = null;
        string? element = null;
        bool inDump = false;

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    element = reader.Name;

                    if (element is "rom" or "megarom")
                    {
                        inDump = true;
                        type = element == "rom" ? "Mirrored" : null;
                        hash = reader.GetAttribute("sha1");

                        if (reader.GetAttribute("type") is { Length: > 0 } attribute)
                            type = attribute;
                    }

                    break;

                case XmlNodeType.Text when inDump:
                    if (element == "type") type = reader.Value.Trim();
                    else if (element == "hash") hash = reader.Value.Trim();
                    break;

                case XmlNodeType.EndElement when reader.Name is "rom" or "megarom":
                    if (hash is not null && hash.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                        return RomMapperNames.Parse(type);

                    inDump = false;
                    type = null;
                    hash = null;
                    break;
            }
        }

        return RomMapper.Auto;
    }
}
