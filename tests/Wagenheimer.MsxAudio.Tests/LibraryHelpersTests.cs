using System.Collections.Generic;
using System.IO;
using Wagenheimer.MsxAudio.Extraction;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Vgm;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>Tests for the helpers a game uses on top of a package (ids, profiles, manifest import).</summary>
public class LibraryHelpersTests
{
    private static VgmFile OneSecondVgm()
    {
        var events = new List<VgmEvent>
        {
            new(0, VgmEventKind.Psg, 7, 0x3E, 0),
            new(0, VgmEventKind.Psg, 0, 0x50, 0),
            new(44100, VgmEventKind.Psg, 0, 0x60, 0),
        };

        return VgmFile.FromEvents(events, 44100, 1789772.5, "test");
    }

    [Fact]
    public void BuildOriginalIdMap_SplitsSongsAndEffects()
    {
        using var library = new MsxAudioLibrary("test");
        library.AddSong("stage_a", "stage_a", OneSecondVgm(), originalId: 5);
        library.AddSong("title", "title", OneSecondVgm(), originalId: 3);
        library.AddClip("jump", "jump", OneSecondVgm(), originalId: 19);

        var (songs, effects) = library.BuildOriginalIdMap();

        Assert.Equal("stage_a", songs[5]);
        Assert.Equal("title", songs[3]);
        Assert.Equal("jump", effects[19]);
        Assert.Equal(2, songs.Count);
        Assert.Single(effects);
    }

    [Fact]
    public void FindByOriginalId_FindsSongsAndEffects()
    {
        using var library = new MsxAudioLibrary("test");
        library.AddSong("stage_a", "stage_a", OneSecondVgm(), originalId: 5);
        library.AddClip("jump", "jump", OneSecondVgm(), originalId: 19);

        Assert.Equal("stage_a", library.FindByOriginalId(5)?.Id);
        Assert.Equal("jump", library.FindByOriginalId(19)?.Id);
        Assert.Null(library.FindByOriginalId(200));
    }

    [Theory]
    [InlineData("original", 1, "stereo")]
    [InlineData("stereo", 1, "remaster")]
    [InlineData("original", -1, "auto")]
    [InlineData("remaster", 1, "auto")]
    [InlineData("unknown", 1, "stereo")]
    public void Next_CyclesProfilesInBothDirections(string from, int direction, string expected)
    {
        Assert.Equal(expected, AudioProfile.Next(from, direction).Name);
    }

    [Fact]
    public void ImportManifest_KeepsIdsAndTheNoLoopDecision()
    {
        string root = Path.Combine(Path.GetTempPath(), "msxaudio-manifest-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Music"));
        Directory.CreateDirectory(Path.Combine(root, "Sfx"));

        string path = Path.Combine(root, "test.vgm");
        File.WriteAllBytes(path, VgmWriter.Write(OneSecondVgm()));
        File.Copy(path, Path.Combine(root, "Music", "stage_a.vgm"));
        File.Copy(path, Path.Combine(root, "Sfx", "jump.vgm"));

        string manifest = Path.Combine(root, "audio.json");
        File.WriteAllText(manifest, """
            { "music": { "stage_a": { "id": 5, "loop": false } },
              "sfx":   { "jump":    { "id": 19 } } }
            """);

        try
        {
            using var library = VgmImporter.ImportManifest(root, manifest, "test");

            Assert.Single(library.Songs);
            Assert.Single(library.Clips);
            Assert.Equal(5, library.Songs[0].OriginalId);
            Assert.False(library.Songs[0].Loop);
            Assert.Equal(19, library.Clips[0].OriginalId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
