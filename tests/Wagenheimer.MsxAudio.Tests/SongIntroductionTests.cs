using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Playback;
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Vgm;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

public class SongIntroductionTests
{
    [Fact]
    public void LoopingSongPlaysItsIntroductionAndRestartsItBeforeRepeatingTheLoop()
    {
        var events = new[] {
            new VgmEvent(0, VgmEventKind.Psg, 7, 0x3e, 0),
            new VgmEvent(0, VgmEventKind.Psg, 0, 200, 0),
            new VgmEvent(0, VgmEventKind.Psg, 1, 0, 0),
            new VgmEvent(0, VgmEventKind.Psg, 8, 0, 0),
            new VgmEvent(11025, VgmEventKind.Psg, 8, 15, 0),
        };
        using var library = new MsxAudioLibrary("Introduction regression");
        var vgm = VgmFile.FromEvents(events, 44100, 1789772.5, "theme");
        vgm.SetLoop(0.5, 0.5);
        library.AddSong("theme", "Theme", vgm, true, 0.5, 0.5);
        using var engine = new MsxAudioEngine(library, AudioProfile.Original, 8000);
        engine.PlaySong("theme");
        var buffer = new float[1600];
        engine.Render(buffer, 800);
        Assert.All(buffer, value => Assert.Equal(0f, value));
        for (int i = 0; i < 20; i++) engine.Render(buffer, 800);
        Assert.True(engine.IsPlaying);
        Assert.Contains(buffer, value => Math.Abs(value) > 0.001f);
        engine.PlaySong("theme"); // A new stage using the same theme must restart its introduction.
        engine.Render(buffer, 800);
        Assert.All(buffer, value => Assert.Equal(0f, value));
    }

    [Theory]
    [InlineData("msx1")]
    [InlineData("msx2")]
    public void SavedStageThemeStartsWithTheSamePcmWhetherLoopingIsEnabledOrDisabled(string variant)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "MSXMusic", "kings-valley-2", variant, "Audio", "audio.msxaudio");
        if (!File.Exists(path)) return;
        using var library = MsxAudioLibrary.Load(path);
        var song = library.Find("stage_a")!;
        Assert.Equal(5, song.OriginalId);
        Assert.True(song.LoopStart > 1);
        using var looping = new MsxAudioEngine(library, AudioProfile.Original, 8000);
        using var once = new MsxAudioEngine(library, AudioProfile.Original, 8000);
        looping.PlaySong("stage_a", true);
        once.PlaySong("stage_a", false);
        var actual = new float[16000]; var expected = new float[16000];
        looping.Render(actual, 8000); once.Render(expected, 8000);
        Assert.Equal(expected, actual);
    }
}
