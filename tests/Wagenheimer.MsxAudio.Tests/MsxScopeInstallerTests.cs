using System;
using System.Runtime.InteropServices;
using Wagenheimer.MsxAudio.Extraction;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>
/// The player is installed from the project's own releases, so the installer must pick the archive of
/// the machine it is running on (Windows/Linux/macOS × x64/arm64) and unpack it accordingly.
/// </summary>
public class MsxScopeInstallerTests
{
    [Fact]
    public void AssetName_FollowsTheCurrentPlatform()
    {
        string? asset = MsxScopeInstaller.AssetName;

        if (!MsxScopeInstaller.IsSupported)
            return; // plataforma sem build publicado

        Assert.NotNull(asset);

        string architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal($"MSXScope-win-{architecture}.zip", asset);
            Assert.EndsWith(".exe", MsxScopeInstaller.ExecutableName);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Equal($"MSXScope-linux-{architecture}.tar.gz", asset);
            Assert.DoesNotContain(".", MsxScopeInstaller.ExecutableName);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal($"MSXScope-osx-{architecture}.tar.gz", asset);
            Assert.DoesNotContain(".", MsxScopeInstaller.ExecutableName);
        }
    }

    [Fact]
    public void InstallRoot_IsPerUser()
    {
        Assert.Contains("MSXScope", MsxScopeInstaller.InstallRoot);
        Assert.True(Path.IsPathRooted(MsxScopeInstaller.InstallRoot));
    }
}
