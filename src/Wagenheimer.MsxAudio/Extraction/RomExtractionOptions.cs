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

    /// <summary>Generated VGM files (id -> path).</summary>
    public Dictionary<int, string> VgmFiles { get; set; } = new();
}
