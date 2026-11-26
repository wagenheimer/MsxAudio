using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>An MSX key (matrix row/mask) used to navigate to the silent sound screen.</summary>
public sealed class KeyStep
{
    /// <summary>Emulated time (seconds) when the key is pressed.</summary>
    public double At { get; set; }

    /// <summary>Matrix row (0..8).</summary>
    public int Row { get; set; }

    /// <summary>Key mask on that row.</summary>
    public int Mask { get; set; }

    /// <summary>How long the key is held (seconds).</summary>
    public double Duration { get; set; } = 0.12;

    /// <summary>When set, types this sequence of characters (one every <see cref="TypeDelay"/>).</summary>
    public string? Type { get; set; }

    /// <summary>Delay between typed characters.</summary>
    public double TypeDelay { get; set; } = 0.6;
}

/// <summary>
/// Extraction profile for a ROM: how to reach a silent screen and which routine plays a sound.
/// </summary>
/// <remarks>
/// The sound driver differs from game to game. For Konami games on SCC cartridges (like King's
/// Valley II) the routine is <c>CALL 0x432E</c> with <c>A = id</c>; the "silent screen" is reached
/// with a key sequence. This profile describes both, which is what makes the extractor reusable
/// across games without recompiling anything.
/// </remarks>
public sealed class RomExtractionProfile
{
    /// <summary>Stable collection id; together with Variant enables canonical shared-library paths.</summary>
    public string? GameId { get; set; }
    /// <summary>Cartridge variant, for example msx1 or msx2. Never inferred from a filename.</summary>
    public string? Variant { get; set; }
    /// <summary>Stable extraction profile identifier.</summary>
    public string? ProfileId { get; set; }
    /// <summary>Extraction profile revision.</summary>
    public string? ProfileVersion { get; set; }

    /// <summary>Run cartridge INIT before initializing the sound driver.</summary>
    public bool BootCartridge { get; set; } = true;
    /// <summary>Optional sound-driver initialization routine.</summary>
    public int InitializeAddress { get; set; }
    /// <summary>CPU window to ROM bank mapping reapplied before every driver call.</summary>
    public Dictionary<int, int> DriverBanks { get; set; } = new();
    /// <summary>Authentic ROM code/data copied to RAM before driver initialization.</summary>
    public List<RomMemoryCopy> MemoryCopies { get; set; } = new();
    /// <summary>Optional external SCC primary slot. -1 disables it.</summary>
    public int ExternalSccSlot { get; set; } = -1;
    /// <summary>Block sound commands originating in the game when using its interrupt hooks.</summary>
    public bool BlockGameSoundCalls { get; set; } = true;
    /// <summary>Driver ticks needed to consume the stop command before starting an ID.</summary>
    public int StopSettleFrames { get; set; } = 3;
    /// <summary>Music that must play once, even if a repeated passage is found.</summary>
    public List<int> OneShotMusicIds { get; set; } = new();
    /// <summary>ROM call-site evidence for sound names; IDs without evidence stay explicitly unidentified.</summary>
    public Dictionary<int, string> NameSources { get; set; } = new();

    /// <summary>True for diagnostic subsets, which must not satisfy a game's production cache checks.</summary>
    public bool PartialExtraction { get; set; }
    /// <summary>Required audible IDs. A full extraction fails if one is silent or absent.</summary>
    public List<int> RequiredIds { get; set; } = new();
    /// <summary>Tracks that require a measured loop before a complete package can be published.</summary>
    public List<int> RequiredLoopIds { get; set; } = new();

    /// <summary>Optional sequencer-state contract for automatic termination and loop measurement.</summary>
    public DriverObservationOptions? DriverObservation { get; set; }

    /// <summary>Game/profile name.</summary>
    public string Name { get; set; } = "MSX";

    /// <summary>
    /// SHA-1 of the ROM images this profile was made for (lower-case hex). The player uses it to pick
    /// the profile by itself when such a ROM is chosen.
    /// </summary>
    public List<string> RomSha1 { get; set; } = new();

    /// <summary>openMSX machine (e.g. <c>C-BIOS_MSX2_JP</c>).</summary>
    public string Machine { get; set; } = "C-BIOS_MSX2_JP";

    /// <summary>Cartridge type (optional; e.g. <c>KonamiSCC</c>). Empty = detect from the ROM.</summary>
    public string? RomType { get; set; }

    /// <summary>Video refresh rate: 60 (NTSC, default) or 50 (PAL). It sets the tempo of every capture.</summary>
    public double RefreshRate { get; set; } = 60;

    /// <summary>How the sound routine takes its id: <c>A</c> (default), <c>HL</c>, <c>DE</c> or <c>BC</c>.</summary>
    public string IdRegister { get; set; } = "A";

    /// <summary>ROM bank the sound routine lives in (-1 = whatever is mapped after boot).</summary>
    public int SoundCallBank { get; set; } = -1;

    /// <summary>Address of the "play sound A" routine (CPU).</summary>
    public int SoundCallAddress { get; set; } = 0x432E;

    /// <summary>
    /// Address of the per-frame sound player, when the driver can be run alone (no game logic).
    /// </summary>
    /// <remarks>
    /// Many stream-based drivers need nothing from the game: setting the channels idle, calling the
    /// "play sound" routine and then stepping this routine at 60 Hz is enough. That is faster and
    /// deterministic (no game state to interfere), and it is what makes stream-based drivers extractable:
    /// its handler also runs the whole game, which overwrites the song on the first state change.
    /// Leave 0 to run the game's interrupt hooks instead.
    /// </remarks>
    public int PlayerAddress { get; set; }

    /// <summary>
    /// RAM bytes cleared before each capture (usually each channel's current-song byte).
    /// </summary>
    /// <remarks>The driver's priority gate (lower id wins) would otherwise block a new song.</remarks>
    public List<int> ResetAddresses { get; set; } = new();

    /// <summary>Bits OR-ed into the id when calling (some drivers use them to select a stream type).</summary>
    public int IdMask { get; set; }

    /// <summary>Id that stops every sound (used before each capture).</summary>
    public int StopId { get; set; } = 1;

    /// <summary>Ids to capture (in order).</summary>
    public List<int> Ids { get; set; } = new();

    /// <summary>Capture seconds per id.</summary>
    public double SecondsPerId { get; set; } = 12;

    /// <summary>Emulated time when each id capture starts.</summary>
    public double FirstCaptureAt { get; set; } = 23;

    /// <summary>Navigation to the silent screen.</summary>
    public List<KeyStep> Navigation { get; set; } = new();

    /// <summary>Ids that are music (captured for longer).</summary>
    public List<int> MusicIds { get; set; } = new();

    /// <summary>Capture seconds for music ids.</summary>
    public double SecondsPerSong { get; set; } = 120;

    /// <summary>Per-id duration (overrides the general rules).</summary>
    public Dictionary<int, double> SecondsById { get; set; } = new();

    /// <summary>Friendly names per id (optional).</summary>
    public Dictionary<int, string> Names { get; set; } = new();
}

/// <summary>A range of authentic ROM bytes required in RAM by a relocated driver.</summary>
public sealed class RomMemoryCopy
{
    public int RomOffset { get; set; }
    public int Address { get; set; }
    public int Length { get; set; }
}

/// <summary>Reads and writes <see cref="RomExtractionProfile"/> JSON files.</summary>
public static class RomExtractionProfiles
{
    private static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Loads a profile (throws <see cref="InvalidDataException"/> when it is unusable).</summary>
    public static RomExtractionProfile Load(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<RomExtractionProfile>(System.IO.File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"invalid profile: {path}");

    /// <summary>Returns a fresh curated profile for a specific game and cartridge variant.</summary>
    public static RomExtractionProfile? FindByGameVariant(string gameId, string variant)
        => FindAllByGameVariant(gameId, variant).OrderByDescending(profile => profile.RomSha1.Count).FirstOrDefault();

    /// <summary>Returns every hardware recipe belonging to a game variant, including mapper conversions.</summary>
    public static IReadOnlyList<RomExtractionProfile> FindAllByGameVariant(string gameId, string variant)
    {
        var profiles = new List<RomExtractionProfile>();
        var assembly = typeof(RomExtractionProfiles).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!resource.StartsWith("MsxAudio.Profiles.", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            var profile = System.Text.Json.JsonSerializer.Deserialize<RomExtractionProfile>(stream, Options);
            if (profile?.GameId == gameId && profile.Variant == variant) profiles.Add(profile);
        }
        return profiles;
    }

    /// <summary>Finds a curated embedded profile by the hash of the resolved ROM bytes.</summary>
    public static RomExtractionProfile? FindForRom(byte[] rom)
    {
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(rom));
        var assembly = typeof(RomExtractionProfiles).Assembly;
        foreach (string resource in assembly.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            if (!resource.StartsWith("MsxAudio.Profiles.", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            var profile = System.Text.Json.JsonSerializer.Deserialize<RomExtractionProfile>(stream, Options);
            if (profile?.RomSha1.Any(h => h.Equals(hash, StringComparison.OrdinalIgnoreCase)) == true) return profile;
        }
        return null;
    }

    /// <summary>Checks source and profile revision before reusing a published extraction.</summary>
    public static bool IsCurrentPackage(string path, RomExtractionProfile profile, string? expectedRomSha1 = null)
    {
        try
        {
            var manifest = AudioCatalog.ReadManifest(path);
            var identity = manifest.Identity;
            var sounds = manifest.Songs.Concat(manifest.Clips).ToArray();
            if (profile.RequiredIds.Any(id => !sounds.Any(s => s.OriginalId == id)) ||
                profile.RequiredLoopIds.Any(id => !sounds.Any(s => s.OriginalId == id && s.Loop && s.LoopSeconds > 0 && s.LoopStart + s.LoopSeconds <= s.Seconds))) return false;
            return identity is not null && identity.Complete && identity.GameId == profile.GameId && identity.Variant == profile.Variant &&
                identity.ProfileId == profile.ProfileId && identity.ProfileVersion == profile.ProfileVersion &&
                profile.RomSha1.Any(h => h.Equals(identity.RomSha1, StringComparison.OrdinalIgnoreCase)) &&
                (expectedRomSha1 == null || expectedRomSha1.Equals(identity.RomSha1, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException) { return false; }
    }

    /// <summary>Writes a profile.</summary>
    public static void Save(RomExtractionProfile profile, string path) =>
        System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(profile, Options));
}

/// <summary>Options for the extraction process.</summary>
public sealed class RomExtractionOptions
{
    /// <summary>Game ROM.</summary>
    public string RomPath { get; set; } = string.Empty;

    /// <summary>openMSX executable path.</summary>
    public string OpenMsxPath { get; set; } = "openmsx";

    /// <summary>Game profile.</summary>
    public RomExtractionProfile Profile { get; set; } = new();

    /// <summary>Working folder (scripts, VGMs).</summary>
    public string WorkDirectory { get; set; } = "msxaudio-work";

    /// <summary>Maximum session duration (safety).</summary>
    public double TimeoutSeconds { get; set; } = 3600;

    /// <summary>Record PSG writes (default true).</summary>
    public bool CapturePsg { get; set; } = true;

    /// <summary>Record SCC writes (default true).</summary>
    public bool CaptureScc { get; set; } = true;
}

/// <summary>Extraction result: the library and the generated files.</summary>
public sealed class ExtractionResult
{
    /// <summary>Library ready to be saved as <c>.msxaudio</c>.</summary>
    public Songs.MsxAudioLibrary Library { get; set; } = null!;

    /// <summary>Per-ID capture validation, including skipped silent commands and capture limits.</summary>
    public List<SoundCaptureDiagnostic> Diagnostics { get; set; } = new();

    /// <summary>Generated VGM files (id -> path).</summary>
    public Dictionary<int, string> VgmFiles { get; set; } = new();
}

/// <summary>Measured output of an individual driver request.</summary>
public sealed record SoundCaptureDiagnostic(int Id, string Name, int Writes, float Peak, double Seconds, bool Silent, bool ReachedCaptureLimit,
    string Termination = "unverified");
