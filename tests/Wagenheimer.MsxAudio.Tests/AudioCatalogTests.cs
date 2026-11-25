using Wagenheimer.MsxAudio.Songs;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

public sealed class AudioCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "msxaudio-catalog-" + Guid.NewGuid().ToString("N"));
    public AudioCatalogTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void CacheRequiresCompleteIdentityAndTheSameProfileRevision()
    {
        string path=Path.Combine(_root,"cache.msxaudio");
        var profile=new Extraction.RomExtractionProfile { GameId="test",Variant="msx1",ProfileId="test-driver",ProfileVersion="2",RomSha1=new(){"abc"} };
        using var library=new MsxAudioLibrary("test");
        library.Manifest.Identity=new MsxAudioIdentity {GameId="test",Variant="msx1",ProfileId="test-driver",ProfileVersion="1",RomSha1="abc",Complete=true};
        library.Save(path);
        Assert.False(Extraction.RomExtractionProfiles.IsCurrentPackage(path,profile));
        library.Manifest.Identity.ProfileVersion="2";library.Manifest.Identity.Complete=false;library.Save(path);
        Assert.False(Extraction.RomExtractionProfiles.IsCurrentPackage(path,profile));
        library.Manifest.Identity.Complete=true;library.Save(path);
        Assert.True(Extraction.RomExtractionProfiles.IsCurrentPackage(path,profile));
        profile.RequiredIds.Add(19);
        Assert.False(Extraction.RomExtractionProfiles.IsCurrentPackage(path,profile));
    }

    [Fact]
    public void VariantPathsAreIndependentAndDoNotCreateDirectories()
    {
        string a = MsxAudioPaths.PackagePath("kings-valley-2", "msx1", _root);
        string b = MsxAudioPaths.PackagePath("kings-valley-2", "msx2", _root);
        Assert.Equal(Path.Combine(_root, "kings-valley-2", "msx1", "Audio", "audio.msxaudio"), a);
        Assert.NotEqual(a, b);
        Assert.False(Directory.Exists(Path.GetDirectoryName(a)));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("CON")]
    public void UnsafeIdentifiersAreRejected(string id) =>
        Assert.Throws<ArgumentException>(() => MsxAudioPaths.PackagePath(id, "msx1", _root));

    [Fact]
    public void IdentitySurvivesSavingLoadingAndReplacement()
    {
        string path = MsxAudioPaths.PackagePath("kings-valley-2", "msx1", _root);
        using var library = new MsxAudioLibrary("King's Valley II");
        library.Manifest.Identity = new MsxAudioIdentity { GameId = "kings-valley-2", Variant = "msx1", RomSha1 = "abc", ProfileId = "kv2-msx1", ProfileVersion = "1", ExtractorVersion = "test" };
        library.Save(path);
        library.Manifest.Title = "Updated title";
        library.Save(path);
        using var loaded = MsxAudioLibrary.Load(path);
        Assert.Equal("msx1", loaded.Manifest.Identity?.Variant);
        Assert.Equal("kv2-msx1", loaded.Manifest.Identity?.ProfileId);
        Assert.Equal("Updated title", loaded.Manifest.Title);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public void CatalogFindsBothVersionsAndLegacyPackagesWithoutLoadingVgm()
    {
        foreach (string variant in new[] { "msx1", "msx2" })
        {
            using var library = new MsxAudioLibrary("King's Valley II");
            library.Manifest.Identity = new MsxAudioIdentity { GameId = "kings-valley-2", Variant = variant };
            library.Save(MsxAudioPaths.PackagePath("kings-valley-2", variant, _root));
        }
        using (var legacy = new MsxAudioLibrary("Old collection")) legacy.Save(Path.Combine(_root, "legacy.msxaudio"));
        File.WriteAllText(Path.Combine(_root, "broken.msxaudio"), "not a zip");
        File.WriteAllText(Path.Combine(_root, "ignored.msxaudio.tmp"), "in progress");
        var catalog = AudioCatalog.Scan(_root);
        Assert.Equal(3, catalog.Packages.Count);
        Assert.Single(catalog.Issues);
        Assert.Single(catalog.Packages, p => p.Manifest.Identity is null);
        Assert.Equal(2, catalog.Packages.Count(p => p.Manifest.Identity?.GameId == "kings-valley-2"));
    }

    [Fact]
    public void PlayerDiscoversNestedPackagesAndKeepsIdentityAndTrackKind()
    {
        string path = MsxAudioPaths.PackagePath("kings-valley-2", "msx1", _root);
        using var library = new MsxAudioLibrary("King's Valley II");
        library.Manifest.Identity = new MsxAudioIdentity { GameId = "kings-valley-2", Variant = "msx1" };
        library.Manifest.Songs.Add(new MsxAudioSong { Id = "title", Name = "Title", Music = true });
        for (int i = 0; i < 30; i++) library.Manifest.Songs.Add(new MsxAudioSong { Id = "stage" + i, Name = "Stage", Music = true });
        library.Manifest.Clips.Add(new MsxAudioSong { Id = "jump", Name = "Jump", Music = false });
        library.Save(path);
        var result = Wagenheimer.MsxAudio.Scope.Library.Scan(_root, true, CancellationToken.None);
        Assert.Equal(32, result.Files.Count);
        Assert.Equal(library.Manifest.Songs.Concat(library.Manifest.Clips).Select(entry => entry.Id), result.Files.Select(entry => entry.SongId));
        Assert.All(result.Files, entry => Assert.Equal("msx1", entry.Variant));
        Assert.All(result.Files, entry => Assert.Equal("King's Valley II [MSX1]", entry.Source));
        Assert.Equal(31, result.Files.Count(entry => entry.Music == true));
        Assert.Single(result.Files, entry => entry.Music == false);
    }

    [Fact]
    public void ExtractionIdentityChecksRomHashRatherThanTrustingTheProfileLabel()
    {
        var profile = new Extraction.RomExtractionProfile { GameId = "kings-valley-2", Variant = "msx1", RomSha1 = new() { "wrong" } };
        Assert.Throws<InvalidDataException>(() => Extraction.ExtractionIdentity.Create(profile, new byte[] { 1, 2, 3 }));
        profile.RomSha1.Clear();
        Assert.Equal(40, Extraction.ExtractionIdentity.Create(profile, new byte[] { 1, 2, 3 })!.RomSha1!.Length);
    }

    [Fact]
    public void MissingRootIsReportedAndCancellationPropagates()
    {
        Assert.Single(AudioCatalog.Scan(Path.Combine(_root, "absent")).Issues);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => AudioCatalog.Scan(_root, cts.Token));
    }
}
