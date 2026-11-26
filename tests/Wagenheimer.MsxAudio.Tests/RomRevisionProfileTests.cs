using Wagenheimer.MsxAudio.Extraction;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

public class RomRevisionProfileTests
{
    [Theory]
    [InlineData("msx1", "cfd872d005b7bd4cdd6e06c4c0162191f0b0415d", "f3306e6f25d111da21ce66db3404f5f48acb25a1")]
    [InlineData("msx2", "5ec8811254dc762c6852289a08acf22954404f0d", "f25628e474035e6bf0440b274c5df04f94c49fb8")]
    public void MultipleHardwareRecipesKeepDistinctHashesAndFreshInstances(string variant, string originalHash, string slotHash)
    {
        var recipes = RomExtractionProfiles.FindAllByGameVariant("kings-valley-2", variant);
        var original = Assert.Single(recipes, profile => profile.RomSha1.Contains(originalHash));
        var converted = Assert.Single(recipes, profile => profile.RomSha1.Contains(slotHash));
        Assert.NotEqual(original.ProfileId, converted.ProfileId);
        Assert.Equal("ASCII8", converted.RomType);
        Assert.Equal(2, converted.ExternalSccSlot);
        Assert.Contains(originalHash, RomExtractionProfiles.FindByGameVariant("kings-valley-2", variant)!.RomSha1);
        original.RomSha1.Clear();
        Assert.Contains(RomExtractionProfiles.FindAllByGameVariant("kings-valley-2", variant), profile => profile.RomSha1.Contains(originalHash));
    }
}
