using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Playback;
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Vgm;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>Tests for the <c>.msxaudio</c> package and the playback engine.</summary>
public class MsxAudioPlaybackTests
{
    private const double Clock = 1789772.5;

    [Fact]
    public void Library_RoundTripsSongsAndLoops()
    {
        var library = new MsxAudioLibrary("teste");
        var vgm = BuildTone(totalSeconds: 6, loopStart: 1, loopSeconds: 2);
        library.AddSong("stage", "Stage", vgm, loop: true, loopStart: 1, loopSeconds: 2, originalId: 5);
        library.AddClip("jump", "Jump", BuildTone(totalSeconds: 1));

        string path = Path.Combine(Path.GetTempPath(), $"msxaudio-test-{Guid.NewGuid():N}.msxaudio");
        try
        {
            library.Save(path);
            using var loaded = MsxAudioLibrary.Load(path);

            Assert.Equal("teste", loaded.Manifest.Title);
            Assert.Single(loaded.Songs);
            Assert.Single(loaded.Clips);
            Assert.Equal(5, loaded.Songs[0].OriginalId);
            Assert.True(loaded.Songs[0].Loop);
            Assert.Equal(2, loaded.Songs[0].LoopSeconds, 3);

            var restored = loaded.LoadVgm(loaded.Songs[0]);
            Assert.True(restored.HasLoop);
            Assert.Equal(2 * 44100, restored.LoopDuration);
            Assert.Equal(vgm.Events.Count, restored.Events.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Engine_LoopsWithoutDiscontinuity()
    {
        var library = new MsxAudioLibrary("loop");
        var vgm = BuildTone(totalSeconds: 8, loopStart: 1, loopSeconds: 2);
        library.AddSong("tone", "Tone", vgm, loop: true, loopStart: 1, loopSeconds: 2);

        using var engine = new MsxAudioEngine(library, AudioProfile.Original, 48000);
        engine.PlaySong("tone");

        var buffer = new float[2048 * 2];
        var collected = new List<float>();
        for (int i = 0; i < 3 * 48000 / 2048 + 2; i++)
        {
            engine.Render(buffer, 2048);
            for (int f = 0; f < 2048; f++)
                collected.Add((buffer[f * 2] + buffer[f * 2 + 1]) / 2f);
        }

        Assert.Contains(collected, v => Math.Abs(v) > 0.005f);
        Assert.Equal("tone", engine.CurrentSongId);

        // nenhum salto grande perto das emendas (1 loop ≈ 2 s)
        float maxJump = 0;
        for (int i = 1; i < collected.Count; i++)
        {
            float jump = Math.Abs(collected[i] - collected[i - 1]);
            if (jump > maxJump) maxJump = jump;
        }

        Assert.True(maxJump < 0.35f, $"max jump {maxJump} (expected < 0.35, seamless loop)");
    }

    [Fact]
    public void Engine_PlaysSampleOnceAndStops()
    {
        var library = new MsxAudioLibrary("sample");
        var clip = BuildTone(totalSeconds: 0.5);
        library.AddClip("blip", "Blip", clip);

        using var engine = new MsxAudioEngine(library, AudioProfile.Original, 48000);
        engine.PlaySample("blip");

        var buffer = new float[1024 * 2];
        float first = 0;
        float later = 0;
        for (int i = 0; i < 40; i++)
        {
            engine.Render(buffer, 1024);
            float peak = 0;
            for (int f = 0; f < 1024; f++) peak = Math.Max(peak, Math.Abs(buffer[f * 2]));
            if (i == 0) first = peak;
            if (i == 39) later = peak;
        }

        Assert.True(first > 0.001f);
        Assert.True(later < 0.0005f);
    }

    [Fact]
    public void Engine_SongEndsWhenNotLooping()
    {
        var library = new MsxAudioLibrary("one-shot");
        library.AddSong("jingle", "Jingle", BuildTone(totalSeconds: 0.5), loop: false);

        using var engine = new MsxAudioEngine(library, AudioProfile.Original, 48000);
        engine.PlaySong("jingle");

        var buffer = new float[512 * 2];
        for (int i = 0; i < 200; i++) engine.Render(buffer, 512);

        Assert.False(engine.IsPlaying);
    }

    [Fact]
    public void Engine_VolumeControlsSilence()
    {
        var library = new MsxAudioLibrary("volume");
        library.AddSong("tone", "Tone", BuildTone(totalSeconds: 4, loopStart: 0, loopSeconds: 2), loop: true, loopStart: 0, loopSeconds: 2);

        using var engine = new MsxAudioEngine(library, AudioProfile.Original, 48000);
        engine.MasterVolume = 0f;
        engine.PlaySong("tone");

        var buffer = new float[2048 * 2];
        engine.Render(buffer, 2048);

        Assert.All(buffer, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void Engine_MatchesOfflineRenderer()
    {
        var library = new MsxAudioLibrary("parity");
        var vgm = TestSignals.Tone(3, 0, 0);
        library.AddSong("tone", "Tone", vgm, loop: false);

        const int rate = 48000;
        var offline = MsxSoundRenderer.Render(vgm, AudioProfile.Original, rate, 0, 2);

        using var engine = new MsxAudioEngine(library, AudioProfile.Original, rate);
        engine.PlaySong("tone", loop: false);

        int frames = Math.Min(offline.Frames, 2 * rate);
        var buffer = new float[frames * 2];
        engine.Render(buffer, frames);

        double sum = 0, sa = 0, sb = 0, rmsA = 0, rmsB = 0;
        for (int i = 0; i < frames; i++)
        {
            double a = (offline.Samples[i * 2] + offline.Samples[i * 2 + 1]) / 65536.0;
            double b = (buffer[i * 2] + buffer[i * 2 + 1]) / 2.0;
            sum += a * b;
            sa += a * a;
            sb += b * b;
            rmsA += a * a;
            rmsB += b * b;
        }

        double correlation = sum / Math.Sqrt(sa * sb);
        double rmsRatio = Math.Sqrt(rmsB / rmsA);
        Assert.True(correlation > 0.98, $"streaming vs offline correlation = {correlation:F4}");
        Assert.InRange(rmsRatio, 0.95, 1.05);
    }

    /// <summary>
    /// Builds a synthetic VGM: a PSG note playing continuously; the "loop" is only the marker
    /// (the event stream is continuous, like a real song's).
    /// </summary>
    private static VgmFile BuildTone(double totalSeconds, double loopStart = 0, double loopSeconds = 0)
    {
        var commands = new List<byte[]>
        {
            new byte[] { 0xA0, 7, 0x3E },
            new byte[] { 0xA0, 0, 200 },
            new byte[] { 0xA0, 1, 0 },
            new byte[] { 0xA0, 8, 15 },
        };

        int remaining = (int)(totalSeconds * 44100);
        while (remaining > 0)
        {
            int step = Math.Min(remaining, 65535);
            commands.Add(new[] { (byte)0x61, (byte)(step & 0xFF), (byte)((step >> 8) & 0xFF) });
            remaining -= step;
        }

        var vgm = VgmFile.Parse(BuildVgm(commands, (int)(totalSeconds * 44100)), "tone");
        if (loopSeconds > 0) vgm.SetLoop(loopStart, loopSeconds);
        return vgm;
    }

    private static byte[] BuildVgm(List<byte[]> commands, int totalSamples)
    {
        using var stream = new MemoryStream();
        var header = new byte[0x100];
        header[0] = (byte)'V';
        header[1] = (byte)'g';
        header[2] = (byte)'m';
        header[3] = (byte)' ';
        BitConverter.GetBytes(0x161).CopyTo(header, 0x08);
        BitConverter.GetBytes(totalSamples).CopyTo(header, 0x18);
        BitConverter.GetBytes(0x100 - 0x34).CopyTo(header, 0x34);
        BitConverter.GetBytes((uint)Clock).CopyTo(header, 0x74);
        stream.Write(header);
        foreach (var command in commands) stream.Write(command);
        stream.WriteByte(0x66);
        return stream.ToArray();
    }
}
