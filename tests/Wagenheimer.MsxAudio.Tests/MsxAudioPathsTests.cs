using System;
using System.IO;
using Wagenheimer.MsxAudio;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

public class MsxAudioPathsTests
{
    [Fact]
    public void Home_DefaultsToTheUsersMusicFolder()
    {
        string path = MsxAudioPaths.Resolve(environmentValue: null, configuredRoot: null, musicFolder: "/home/player/Music");

        Assert.Equal(Path.Combine("/home/player/Music", "MSXMusic"), path);
    }

    [Fact]
    public void Home_EnvironmentVariableWinsOverEverything()
    {
        string path = MsxAudioPaths.Resolve(
            environmentValue: "  \"E:\\GreenSauceGames\\MSXMusic\"  ",
            configuredRoot: "/home/player/cfg",
            musicFolder: "/home/player/Music");

        Assert.Equal("E:\\GreenSauceGames\\MSXMusic", path);
    }

    [Fact]
    public void Home_ConfigFileIsUsedWhenThereIsNoEnvironmentVariable()
    {
        string path = MsxAudioPaths.Resolve(
            environmentValue: null,
            configuredRoot: "/mnt/games/MSXMusic",
            musicFolder: "/home/player/Music");

        Assert.Equal("/mnt/games/MSXMusic", path);
    }

    [Fact]
    public void Home_FallsBackToMusicWhenTheUserProfileIsUnknown()
    {
        Assert.False(string.IsNullOrWhiteSpace(MsxAudioPaths.Home));
        Assert.True(Path.IsPathRooted(MsxAudioPaths.Home));
    }

    [Fact]
    public void Sanitize_MatchesTheFolderNameRulesOfAPath()
    {
        Assert.Equal("KingsValley2", MsxAudioPaths.Sanitize("KingsValley2"));
        Assert.Equal("a_b_c", MsxAudioPaths.Sanitize("a:b/c"));
        Assert.Equal("games", MsxAudioPaths.Sanitize("   "));
    }

    [Fact]
    public void Sanitize_TrimsSpacesAndTrailingDots()
    {
        Assert.Equal("KingsValley2", MsxAudioPaths.Sanitize("  KingsValley2.  "));
    }

    [Fact]
    public void ConfigPath_IsAlwaysAJsonFile()
    {
        Assert.Equal(MsxAudioPaths.ConfigFileName, Path.GetFileName(MsxAudioPaths.ConfigPath));
    }
}
