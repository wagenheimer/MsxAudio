using System;
using System.IO;
using System.Text;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Export;

/// <summary>Renders a song the way it is exported: loops repeated, a fade at the end.</summary>
public static class AudioExporter
{
    /// <summary>
    /// Renders <paramref name="vgm"/> with <paramref name="profile"/>. A song with a loop plays its
    /// intro and then the loop <paramref name="loops"/> times, with a fade-out over the end; one
    /// without plays once to its last audible note (plus a short tail).
    /// </summary>
    /// <param name="vgm">The song.</param>
    /// <param name="profile">The mix.</param>
    /// <param name="sampleRate">Output rate.</param>
    /// <param name="loops">How many times the loop section plays (at least 1).</param>
    /// <param name="fadeSeconds">Length of the fade-out at the end of a looped song (0 = none).</param>
    public static RenderedAudio Render(VgmFile vgm, AudioProfile profile, int sampleRate = 48000, int loops = 2,
        double fadeSeconds = 3.0)
    {
        ArgumentNullException.ThrowIfNull(vgm);
        ArgumentNullException.ThrowIfNull(profile);

        loops = Math.Max(1, loops);

        if (!vgm.HasLoop)
        {
            double end = Math.Max(0.2, MsxSoundRenderer.FindAudibleEnd(vgm, 0.6));
            return MsxSoundRenderer.Render(vgm, profile, sampleRate, 0, end);
        }

        // Intro + the first pass of the loop in one go (the chips and effects run through the join),
        // then the loop on its own for every further pass.
        double loopStart = vgm.LoopStartSeconds;
        double loopLength = vgm.LoopSeconds;
        RenderedAudio first = MsxSoundRenderer.Render(vgm, profile, sampleRate, 0, loopStart + loopLength);

        int channels = first.Channels;
        int firstSamples = first.Samples.Length;
        int loopFrames = 0;
        short[]? loopSamples = null;

        if (loops > 1)
        {
            RenderedAudio loop = MsxSoundRenderer.Render(vgm, profile, sampleRate, loopStart, loopLength);
            loopSamples = loop.Samples;
            loopFrames = loop.Frames;
        }

        var samples = new short[firstSamples + (loops - 1) * (loopSamples?.Length ?? 0)];
        Array.Copy(first.Samples, samples, firstSamples);

        int at = firstSamples;
        for (int i = 1; i < loops && loopSamples is not null; i++)
        {
            Array.Copy(loopSamples, 0, samples, at, loopSamples.Length);
            at += loopSamples.Length;
        }

        ApplyFade(samples, channels, sampleRate, fadeSeconds);

        _ = loopFrames;
        return new RenderedAudio(samples, sampleRate, channels, Peak(samples));
    }

    /// <summary>Cosine fade-out over the last seconds of the audio.</summary>
    private static void ApplyFade(short[] samples, int channels, int sampleRate, double fadeSeconds)
    {
        int frames = samples.Length / channels;
        int fadeFrames = Math.Min(frames, (int)Math.Round(fadeSeconds * sampleRate));
        if (fadeFrames <= 0)
            return;

        int start = frames - fadeFrames;
        for (int i = 0; i < fadeFrames; i++)
        {
            double gain = 0.5 * (1 + Math.Cos(Math.PI * i / fadeFrames));
            for (int c = 0; c < channels; c++)
            {
                int index = (start + i) * channels + c;
                samples[index] = (short)Math.Round(samples[index] * gain);
            }
        }
    }

    private static float Peak(short[] samples)
    {
        int peak = 0;
        foreach (short sample in samples)
            peak = Math.Max(peak, Math.Abs((int)sample));

        return peak / 32768f;
    }

    /// <summary>The audio as a 16-bit PCM WAV file.</summary>
    public static byte[] ToWav(RenderedAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);

        int dataBytes = audio.Samples.Length * 2;
        using var stream = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);                                   // PCM
        writer.Write((short)audio.Channels);
        writer.Write(audio.SampleRate);
        writer.Write(audio.SampleRate * audio.Channels * 2);      // byte rate
        writer.Write((short)(audio.Channels * 2));                // block align
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        writer.Write(audio.ToPcmBytes());

        writer.Flush();
        return stream.ToArray();
    }
}
