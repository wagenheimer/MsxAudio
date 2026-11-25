using Wagenheimer.MsxAudio.Scope;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

public sealed class ScopePlaybackTests
{
    private static VgmFile Pattern()
    {
        var vgm = VgmFile.FromEvents(new[]
        {
            new VgmEvent(0, VgmEventKind.Psg, 7, 0x3E, 0),
            new VgmEvent(0, VgmEventKind.Psg, 8, 15, 0),
            new VgmEvent(0, VgmEventKind.Psg, 0, 100, 0),
            new VgmEvent(44100, VgmEventKind.Psg, 0, 200, 0),
            new VgmEvent(132300, VgmEventKind.Psg, 0, 250, 0), // captured after the declared loop
        }, 352800, 1789772.5, "pattern");
        vgm.SetLoop(0, 2);
        return vgm;
    }

    [Fact]
    public void LoopsAtTheDeclaredEndAndSchedulesFutureWritesAcrossManyCycles()
    {
        using var player = new VgmPlayer(Pattern(), true, sampleRate: 8000, oversample: 1, profile: AudioProfile.Original);
        float[] buffer = new float[1600];
        for (int step = 0; step < 60; step++)
        {
            player.Render(buffer, 800);
            if (step % 10 == 4)
            {
                int expected = step / 10 % 2 == 0 ? 100 : 200;
                Assert.Equal(expected, player.Channels[0].Period);
                Assert.InRange(player.Time, 0, 2);
            }
        }
    }

    [Fact]
    public void FinishedTrackTimeDoesNotExceedItsActualDuration()
    {
        using var player = new VgmPlayer(Pattern(), false, sampleRate: 8000, oversample: 1);
        float[] buffer = new float[4096];
        for (int step = 0; step < 40; step++) player.Render(buffer, 2048);
        Assert.True(player.Finished);
        Assert.Equal(player.TotalSeconds, player.Time);
    }
}
