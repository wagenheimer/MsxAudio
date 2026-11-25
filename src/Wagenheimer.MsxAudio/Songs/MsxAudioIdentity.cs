namespace Wagenheimer.MsxAudio.Songs;

/// <summary>Optional cartridge provenance. Legacy packages have no identity; do not infer their variant.</summary>
public sealed class MsxAudioIdentity
{
    public bool Complete { get; set; }
    public string GameId { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
    public string? RomSha1 { get; set; }
    public string? ProfileId { get; set; }
    public string? ProfileVersion { get; set; }
    public string? ExtractorVersion { get; set; }
}
