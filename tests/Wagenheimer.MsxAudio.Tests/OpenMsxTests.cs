using System;
using System.IO;
using Wagenheimer.MsxAudio.Extraction;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

public class OpenMsxTests
{
    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "msxaudio-omsx-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string ExeName => OperatingSystem.IsWindows() ? "openmsx.exe" : "openmsx";

    [Fact]
    public void Resolve_FindsTheExecutableInsideAFolderTree()
    {
        string root = NewFolder();

        try
        {
            string nested = Path.Combine(root, "openmsx-22.0-windows-vc-x64-bin");
            Directory.CreateDirectory(nested);
            string exe = Path.Combine(nested, ExeName);
            File.WriteAllText(exe, "x");

            Assert.Equal(exe, OpenMsxLocator.Resolve(root));
            Assert.Equal(exe, OpenMsxLocator.Resolve(exe));
            Assert.Equal(exe, OpenMsxLocator.Resolve("\"" + exe + "\""));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Resolve_RejectsMissingPathsAndOtherPrograms()
    {
        string root = NewFolder();

        try
        {
            string other = Path.Combine(root, "notepad.exe");
            File.WriteAllText(other, "x");

            Assert.Null(OpenMsxLocator.Resolve(null));
            Assert.Null(OpenMsxLocator.Resolve("   "));
            Assert.Null(OpenMsxLocator.Resolve(Path.Combine(root, "nope")));
            Assert.Null(OpenMsxLocator.Resolve(other));
            Assert.Null(OpenMsxLocator.Resolve(root));          // an empty folder: no openMSX in it
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
