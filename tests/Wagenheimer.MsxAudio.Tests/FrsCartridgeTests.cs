using Wagenheimer.MsxAudio.Extraction;
using Wagenheimer.MsxAudio.Songs;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

public sealed class FrsRomFactAttribute : FactAttribute
{
    public FrsRomFactAttribute()
    {
        string? path=Environment.GetEnvironmentVariable("KINGSVALLEY_TEST_FRS_ROM");
        if(string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            Skip="Set KINGSVALLEY_TEST_FRS_ROM to the locally owned verified MSX1 FRS cartridge.";
    }
}

public sealed class FrsCartridgeTests
{
    [Fact]
    public void Msx1ProfileIncludesTheVerifiedFrsRevision()
    {
        var profile=RomExtractionProfiles.FindByGameVariant("kings-valley-2","msx1");
        Assert.NotNull(profile);
        Assert.Contains("cb612cbfdf79dabe55164b386ca956a3e187bef7",profile.RomSha1,StringComparer.OrdinalIgnoreCase);
    }

    [FrsRomFact]
    public void FrsDriverExtractsEveryRequiredSoundAndPreservesSourceIdentity()
    {
        string rom=Environment.GetEnvironmentVariable("KINGSVALLEY_TEST_FRS_ROM")!;
        var profile=RomExtractionProfiles.FindForRom(File.ReadAllBytes(rom));
        Assert.NotNull(profile);Assert.Equal("msx1",profile.Variant);
        var result=DirectRomExtractor.Extract(new(){RomPath=rom,Profile=profile});
        using var captured=result.Library;
        string path=Path.Combine(Path.GetTempPath(),"frs-audio-"+Guid.NewGuid().ToString("N")+".msxaudio");
        try
        {
            captured.Save(path);
            using var saved=MsxAudioLibrary.Load(path);
            Assert.True(RomExtractionProfiles.IsCurrentPackage(path,profile));
            Assert.Equal("cb612cbfdf79dabe55164b386ca956a3e187bef7",saved.Manifest.Identity!.RomSha1);
            Assert.Equal(11,saved.Manifest.Songs.Count);Assert.Equal(48,saved.Manifest.Clips.Count);
            Assert.All(result.Diagnostics,d=>Assert.False(d.ReachedCaptureLimit));
            Assert.Equal(new[]{1,4,47,51,52,63},result.Diagnostics.Where(d=>d.Silent).Select(d=>d.Id).Order());
            Assert.Contains(result.Diagnostics,d=>d.Id==19 && !d.Silent && d.Peak>0.001f);
            Assert.Contains(saved.Manifest.Songs,s=>s.OriginalId==5 && s.Loop && s.LoopSeconds>0);
        }
        finally {if(File.Exists(path))File.Delete(path);}
    }
}
