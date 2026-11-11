using System;

namespace Wagenheimer.MsxAudio.Mixing;

/// <summary>
/// MSX audio mixing profile. The profile defines <b>how</b> PSG and SCC are combined — the source
/// (VGM) is always the same, so this chooses between "exactly the original" and improved versions
/// (stereo, filters, reverb).
/// </summary>
public sealed class AudioProfile
{
    /// <summary>Profile name (used in options and on the command line).</summary>
    public string Name { get; init; } = "original";

    /// <summary>Internal render oversampling factor (1, 2, 4 or 8).</summary>
    public int Oversample { get; init; } = 4;

    /// <summary>Gain applied to the PSG (relative to the SCC — preserves the chip balance).</summary>
    public float PsgGain { get; init; } = 1f;

    /// <summary>Gain applied to the SCC.</summary>
    public float SccGain { get; init; } = 1f;

    /// <summary>Panning of the three PSG channels (-1 = left, 0 = centre, +1 = right).</summary>
    public float[] PsgPan { get; init; } = new float[3];

    /// <summary>Panning of the five SCC channels.</summary>
    public float[] SccPan { get; init; } = new float[5];

    /// <summary>High-pass cutoff in Hz (0 = off). Removes the chip's DC offset.</summary>
    public float HighPassHz { get; init; } = 12f;

    /// <summary>Low-pass cutoff in Hz (0 = off). Approximates the MSX output filter.</summary>
    public float LowPassHz { get; init; }

    /// <summary>Reverb mix (0 = dry, 1 = wet only).</summary>
    public float ReverbMix { get; init; }

    /// <summary>Normalizes the peak to this value (0 = keep the chip level, authentic).</summary>
    public float NormalizePeak { get; init; }

    /// <summary>Master gain applied to the chip sum (before becoming PCM).</summary>
    public float MasterGain { get; init; } = DefaultMasterGain;

    /// <summary>Gain that brings the theoretical maximum chip sum to ~1.0.</summary>
    /// <remarks>
    /// Calibrated against the openMSX capture: the render used to sit 3.4 dB below the reference
    /// recording (same spectral shape, just gain). 1/27000 = 1.48x the previous 1/40000.
    /// </remarks>
    public const float DefaultMasterGain = 1f / 27000f;

    /// <summary>Faithful to the hardware: mono, no filters beyond the DC cut.</summary>
    public static AudioProfile Original => new()
    {
        Name = "original",
        Oversample = 4,
        HighPassHz = 12f,
    };

    /// <summary>Faithful to the chip, but with the channels spread in stereo.</summary>
    public static AudioProfile Stereo => new()
    {
        Name = "stereo",
        Oversample = 4,
        PsgPan = new[] { -0.30f, 0f, 0.30f },
        SccPan = new[] { -0.55f, -0.25f, 0f, 0.25f, 0.55f },
        HighPassHz = 20f,
        LowPassHz = 16000f,
    };

    /// <summary>Master: wider stereo, higher oversampling, gentle filters and a short reverb.</summary>
    public static AudioProfile Remaster => new()
    {
        Name = "remaster",
        Oversample = 8,
        PsgPan = new[] { -0.40f, 0f, 0.40f },
        SccPan = new[] { -0.70f, -0.35f, 0f, 0.35f, 0.70f },
        HighPassHz = 25f,
        LowPassHz = 17000f,
        ReverbMix = 0.18f,
        NormalizePeak = 0.89f,
    };

    /// <summary>
    /// PSG only: the SCC chip is muted. The sound engine writes the same PSG+SCC score on every
    /// cartridge, so this is the "machine without an SCC" mix (the PSG channels alone), not a
    /// different arrangement.
    /// </summary>
    public static AudioProfile PsgOnly => new()
    {
        Name = "psg",
        Oversample = 4,
        SccGain = 0f,
        HighPassHz = 12f,
    };

    /// <summary>Ready-made profiles.</summary>
    public static AudioProfile[] All => new[] { Original, Stereo, Remaster, PsgOnly };

    /// <summary>Copy of the profile with another oversampling factor (short effects can use less).</summary>
    public AudioProfile WithOversample(int oversample) => new()
    {
        Name = Name,
        Oversample = Math.Clamp(oversample, 1, 16),
        PsgGain = PsgGain,
        SccGain = SccGain,
        PsgPan = PsgPan,
        SccPan = SccPan,
        HighPassHz = HighPassHz,
        LowPassHz = LowPassHz,
        ReverbMix = ReverbMix,
        NormalizePeak = NormalizePeak,
        MasterGain = MasterGain,
    };

    /// <summary>Finds a profile by name (default: original).</summary>
    public static AudioProfile ByName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "stereo" or "estéreo" => Stereo,
        "remaster" or "master" => Remaster,
        "psg" or "psgonly" or "psg-only" => PsgOnly,
        _ => Original,
    };
}
