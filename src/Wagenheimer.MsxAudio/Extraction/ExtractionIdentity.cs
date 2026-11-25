using System.Security.Cryptography;
using Wagenheimer.MsxAudio.Songs;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>Creates cartridge provenance from the resolved ROM bytes, never from its filename.</summary>
public static class ExtractionIdentity
{
    public static MsxAudioIdentity? Create(RomExtractionProfile profile, byte[] rom)
    {
        if (string.IsNullOrWhiteSpace(profile.GameId) || string.IsNullOrWhiteSpace(profile.Variant)) return null;
        _ = MsxAudioPaths.PackagePath(profile.GameId, profile.Variant);
        string hash = Convert.ToHexString(SHA1.HashData(rom)).ToLowerInvariant();
        if (profile.RomSha1.Count > 0 && !profile.RomSha1.Contains(hash, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The cartridge hash does not match the identified extraction profile.");
        return new MsxAudioIdentity
        {
            Complete = !profile.PartialExtraction, GameId = profile.GameId, Variant = profile.Variant, RomSha1 = hash,
            ProfileId = profile.ProfileId, ProfileVersion = profile.ProfileVersion,
            ExtractorVersion = typeof(ExtractionIdentity).Assembly.GetName().Version?.ToString(),
        };
    }
}
