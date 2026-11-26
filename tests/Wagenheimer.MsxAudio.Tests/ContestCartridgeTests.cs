using Wagenheimer.MsxAudio.Extraction;
using Wagenheimer.MsxAudio.Songs;
using Xunit;
namespace Wagenheimer.MsxAudio.Tests;

public sealed class ContestRomFactAttribute : FactAttribute
{
    public ContestRomFactAttribute()
    {
        string? path=Environment.GetEnvironmentVariable("KINGSVALLEY_TEST_CONTEST_ROM");
        if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))Skip="Set KINGSVALLEY_TEST_CONTEST_ROM to the locally owned Contest ROM or ZIP.";
    }
}
public sealed class ContestCartridgeTests
{
    [Fact]
    public void ContestProfileIsIndependentOfTheOriginalMsx2Profile()
    {
        var contest=RomExtractionProfiles.FindByGameVariant("kings-valley-2","msx2-contest")!;
        var original=RomExtractionProfiles.FindByGameVariant("kings-valley-2","msx2")!;
        Assert.Contains("4a157e6e217f6af4ab3dec5d7dd5188e5cef7954",contest.RomSha1);
        Assert.DoesNotContain("4a157e6e217f6af4ab3dec5d7dd5188e5cef7954",original.RomSha1);
        Assert.NotEqual(contest.ProfileId,original.ProfileId);
        Assert.Empty(contest.MemoryCopies);Assert.Equal(-1,contest.ExternalSccSlot);
    }
    [ContestRomFact]
    public void ContestZipExtractsCompleteAudioWithoutBorrowingAnotherVariant()
    {
        string source=Environment.GetEnvironmentVariable("KINGSVALLEY_TEST_CONTEST_ROM")!;
        var profile=RomExtractionProfiles.FindForRom(File.ReadAllBytes(RomFile.Resolve(source)))!;
        Assert.Equal("msx2-contest",profile.Variant);
        var result=DirectRomExtractor.Extract(new(){RomPath=source,Profile=profile});
        using var library=result.Library;
        string file=Path.Combine(Path.GetTempPath(),"contest-audio-"+Guid.NewGuid().ToString("N")+".msxaudio");
        try
        {
            library.Save(file);Assert.True(RomExtractionProfiles.IsCurrentPackage(file,profile));
            using var saved=MsxAudioLibrary.Load(file);
            Assert.Equal("msx2-contest",saved.Manifest.Identity!.Variant);
            Assert.Equal(11,saved.Manifest.Songs.Count);Assert.Equal(48,saved.Manifest.Clips.Count);
            Assert.All(result.Diagnostics,d=>Assert.False(d.ReachedCaptureLimit));
            Assert.Equal(new[]{1,4,47,51,52,63},result.Diagnostics.Where(d=>d.Silent).Select(d=>d.Id).Order());
            Assert.Contains(result.Diagnostics,d=>d.Id==19 && !d.Silent && d.Peak>0.001f);
        }
        finally {if(File.Exists(file))File.Delete(file);}
    }
}
