using Wagenheimer.MsxAudio.Scope;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

public sealed class DurationTextTests
{
    [Theory]
    [InlineData(0.461383, "00:01")]
    [InlineData(0.00001, "00:01")]
    [InlineData(1, "00:01")]
    [InlineData(2.167, "00:02")]
    [InlineData(59.9, "00:59")]
    [InlineData(60, "01:00")]
    [InlineData(132, "02:12")]
    [InlineData(0, "00:00")]
    public void ShowsSubsecondEffectsWithoutRoundingThemToZero(double seconds, string expected)
        => Assert.Equal(expected, DurationText.Format(seconds));
}
