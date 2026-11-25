using Wagenheimer.MsxAudio.Extraction;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Playback;
using Wagenheimer.MsxAudio.Scope;
using Wagenheimer.MsxAudio.Songs;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>Optional integration checks using the player's local ROMs; no ROM is distributed.</summary>
public sealed class KingsValleyDriverTests
{
    [Theory]
    [InlineData("msx1")]
    [InlineData("msx2_scc")]
    public void ExtractedShortAndSustainedEffectsSurviveSavingAndAllPlaybackPaths(string flavor)
    {
        string rom = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KingsValley", "roms", "kv2_" + flavor + ".rom");
        if (!File.Exists(rom)) return;
        var profile = RomExtractionProfiles.FindForRom(File.ReadAllBytes(rom))!;
        profile.PartialExtraction = true;
        profile.Ids = new() { 2, 19, 32, 55 };
        var result = DirectRomExtractor.Extract(new() { RomPath = rom, Profile = profile });
        string path = Path.Combine(Path.GetTempPath(), "kv2-driver-" + Guid.NewGuid().ToString("N") + ".msxaudio");
        using var captured = result.Library;
        try
        {
            captured.Save(path);
            Assert.Equal(4, captured.Clips.Count);
            Assert.All(result.Diagnostics, d => { Assert.False(d.Silent); Assert.False(d.ReachedCaptureLimit); });
            using var saved = MsxAudioLibrary.Load(path);
            foreach (var clip in saved.Clips)
            {
                var vgm = saved.LoadVgm(clip);
                var automatic = AudioProfile.ByName("auto");
                Assert.True(MsxSoundRenderer.Render(vgm, automatic, 8000).Peak > 0.001f, clip.Id);
                using var live = new VgmPlayer(vgm, loop: true, sampleRate: 8000, oversample: 1, profile: automatic);
                using var game = new MsxAudioEngine(saved, automatic, 8000);
                game.PlaySample(clip.Id);
                float[] buffer = new float[1600];
                float livePeak = 0, gamePeak = 0;
                for (int i = 0; i < 12; i++)
                {
                    live.Render(buffer, 800); livePeak = Math.Max(livePeak, buffer.Max(Math.Abs));
                    game.Render(buffer, 800); gamePeak = Math.Max(gamePeak, buffer.Max(Math.Abs));
                }
                Assert.True(livePeak > 0.001f, clip.Id);
                Assert.True(gamePeak > 0.001f, clip.Id);
                if (clip.OriginalId == 55)
                {
                    Assert.True(vgm.HasLoop);
                    Assert.False(live.Finished);
                    Assert.InRange(live.Time, clip.LoopStart, clip.LoopStart + clip.LoopSeconds);
                }
            }
        }
        finally { File.Delete(path); }
    }
}
