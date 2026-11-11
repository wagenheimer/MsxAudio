using Wagenheimer.MsxAudio.Extraction;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>Tests for the capture key script (<see cref="KeyPress"/>).</summary>
public class KeyPressTests
{
    [Theory]
    [InlineData("START@2", 0x0F, 4, 2.0)]
    [InlineData("fire@1.5", 0x0F, 4, 1.5)]
    [InlineData("TRIGGER-B@3", 0x0F, 5, 3.0)]
    [InlineData("up@0", 0x0F, 0, 0.0)]
    [InlineData("right@4", 0x0F, 3, 4.0)]
    [InlineData("8:1@2", 8, 1, 2.0)]
    [InlineData("15:4", 0x0F, 4, 1.0)]
    [InlineData("START", 0x0F, 4, 1.0)]
    public void Parse_AcceptsNamesAndRawRows(string text, int row, int mask, double seconds)
    {
        Assert.True(KeyPress.TryParse(text, out KeyPress press));
        Assert.Equal(row, press.Row);
        Assert.Equal(mask, press.Mask);
        Assert.Equal(seconds, press.Seconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("START@abc")]
    [InlineData("row:mask@1")]
    [InlineData("15@1")]
    public void Parse_RejectsGarbage(string text)
    {
        Assert.False(KeyPress.TryParse(text, out _));
    }

    [Fact]
    public void ParseScript_SplitsAndSkipsInvalidEntries()
    {
        var script = KeyPress.ParseScript("START@1, nope, RIGHT@4");

        Assert.Equal(2, script.Count);
        Assert.Equal(1.0, script[0].Seconds, 3);
        Assert.Equal(3, script[1].Mask);
    }

    [Fact]
    public void ToString_UsesTheFriendlyName()
    {
        Assert.True(KeyPress.TryParse("START@2", out KeyPress press));
        Assert.Equal("START at 2s", press.ToString());
    }
}
