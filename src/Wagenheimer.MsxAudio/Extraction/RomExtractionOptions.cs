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

    /// <summary>openMSX machine (e.g. <c>C-BIOS_MSX2_JP</c>).</summary>
    public string Machine { get; set; } = "C-BIOS_MSX2_JP";

    /// <summary>Cartridge type (optional; e.g. <c>KonamiSCC</c>).</summary>
    public string? RomType { get; set; }

    /// <summary>Address of the "play sound A" routine (CPU).</summary>
    public int SoundCallAddress { get; set; } = 0x432E;

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
}

/// <summary>Extraction result: the library and the generated files.</summary>
public sealed class ExtractionResult
{
    /// <summary>Library ready to be saved as <c>.msxaudio</c>.</summary>
    public Songs.MsxAudioLibrary Library { get; set; } = null!;

    /// <summary>Generated VGM files (id -> path).</summary>
    public Dictionary<int, string> VgmFiles { get; set; } = new();
}
