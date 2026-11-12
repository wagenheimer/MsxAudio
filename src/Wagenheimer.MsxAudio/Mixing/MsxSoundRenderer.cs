using System;
using System.Linq;
using Wagenheimer.MsxAudio.Chips;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Mixing;

/// <summary>Ready-to-use PCM audio (16-bit, interleaved).</summary>
public sealed class RenderedAudio
{
    /// <summary>Creates the result.</summary>
    public RenderedAudio(short[] samples, int sampleRate, int channels, float peak)
    {
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
        Peak = peak;
    }

    /// <summary>Interleaved 16-bit PCM samples.</summary>
    public short[] Samples { get; }

    /// <summary>Sample rate (Hz).</summary>
    public int SampleRate { get; }

    /// <summary>Channels (1 = mono, 2 = stereo).</summary>
    public int Channels { get; }

    /// <summary>Absolute peak (0..1) after processing.</summary>
    public float Peak { get; }

    /// <summary>Frame count (samples per channel).</summary>
    public int Frames => Channels > 0 ? Samples.Length / Channels : 0;

    /// <summary>Duration in seconds.</summary>
    public double Seconds => SampleRate > 0 ? (double)Frames / SampleRate : 0;

    /// <summary>PCM bytes (little endian), in the format MonoGame expects.</summary>
    public byte[] ToPcmBytes()
    {
        var bytes = new byte[Samples.Length * 2];
        for (int i = 0; i < Samples.Length; i++)
        {
            bytes[i * 2] = (byte)(Samples[i] & 0xff);
            bytes[i * 2 + 1] = (byte)((Samples[i] >> 8) & 0xff);
        }

        return bytes;
    }
}

/// <summary>
/// Renders a VGM (PSG + SCC) to PCM using the chosen <see cref="AudioProfile"/>.
/// </summary>
/// <remarks>
/// The path is always the same: the chips are emulated at <c>rate × oversampling</c> and decimated
/// with an FIR filter (Blackman-windowed sinc), which avoids the aliasing of a 44.1 kHz capture.
/// The chip state starts from zero and is walked from the beginning of the VGM, so every section is
/// reproduced in phase with the original.
/// </remarks>
public static class MsxSoundRenderer
{
    /// <summary>SCC clock used by MSX VGMs (Hz).</summary>
    public const double SccClock = 1789772.5;

    /// <summary>Renders a range of the VGM.</summary>
    /// <param name="vgm">Source VGM.</param>
    /// <param name="profile">Mixing profile.</param>
    /// <param name="sampleRate">Output rate (default 48000).</param>
    /// <param name="startSeconds">Start instant in the VGM.</param>
    /// <param name="durationSeconds">Duration (0 = until the end of the capture).</param>
    public static RenderedAudio Render(
        VgmFile vgm,
        AudioProfile profile,
        int sampleRate = 48000,
        double startSeconds = 0,
        double durationSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(vgm);
        ArgumentNullException.ThrowIfNull(profile);
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (startSeconds < 0) throw new ArgumentOutOfRangeException(nameof(startSeconds));

        int oversample = Math.Clamp(profile.Oversample, 1, 16);
        double internalRate = (double)sampleRate * oversample;

        if (durationSeconds <= 0)
            durationSeconds = Math.Max(0.05, vgm.TotalSeconds - startSeconds);

        int startFrame = (int)Math.Round(startSeconds * sampleRate);
        int outFrames = Math.Max(1, (int)Math.Round(durationSeconds * sampleRate));
        int totalFrames = startFrame + outFrames;

        var psg = new Ay8910(vgm.AyClock, internalRate);
        var scc = new KonamiScc(SccClock, internalRate);

        (float L, float R)[] psgPan = BuildPan(profile.PsgPan, 3);
        (float L, float R)[] sccPan = BuildPan(profile.SccPan, 5);

        const int chunkOut = 2048;
        int chunkIn = chunkOut * oversample;

        float[][] psgBuf = { new float[chunkIn], new float[chunkIn], new float[chunkIn] };
        float[][] sccBuf =
        {
            new float[chunkIn], new float[chunkIn], new float[chunkIn], new float[chunkIn], new float[chunkIn],
        };
        float[] mixL = new float[chunkIn];
        float[] mixR = new float[chunkIn];
        float[] decL = new float[chunkOut];
        float[] decR = new float[chunkOut];

        float[] outL = new float[totalFrames];
        float[] outR = new float[totalFrames];

        var decimator = new FirDecimator(oversample);
        var events = vgm.Events;
        int eventIndex = 0;
        long internalPos = 0;
        int renderedFrames = 0;

        double masterGain = profile.MasterGain;
        float psgGain = profile.PsgGain;
        float sccGain = profile.SccGain;

        // Automatic mix: the whole song is analysed once, so a loop section is mixed like the rest.
        AutoMixProcessor? auto = profile.AutoMix
            ? new AutoMixProcessor(AutoMixAnalyzer.Analyze(vgm), oversample, sampleRate)
            : null;
        float[][] allChannels = psgBuf.Concat(sccBuf).ToArray();
        float[] channelGain = { psgGain, psgGain, psgGain, sccGain, sccGain, sccGain, sccGain, sccGain };

        while (renderedFrames < totalFrames)
        {
            int framesThisChunk = Math.Min(chunkOut, totalFrames - renderedFrames);
            int inCount = framesThisChunk * oversample;
            int done = 0;

            while (done < inCount)
            {
                long nextEvent = long.MaxValue;
                if (eventIndex < events.Count)
                    nextEvent = EventInternalFrame(events[eventIndex].Sample, internalRate);

                long room = nextEvent - internalPos;
                if (room <= 0)
                {
                    while (eventIndex < events.Count &&
                           EventInternalFrame(events[eventIndex].Sample, internalRate) <= internalPos)
                    {
                        VgmEvent e = events[eventIndex++];
                        if (e.Kind == VgmEventKind.Psg)
                            psg.Write(e.P0, e.P1);
                        else
                            scc.Write(e.P0, e.P1, e.P2);
                    }

                    continue;
                }

                int segment = (int)Math.Min(inCount - done, room);
                psg.Render(psgBuf[0], psgBuf[1], psgBuf[2], done, segment);
                scc.Render(sccBuf, done, segment);
                done += segment;
                internalPos += segment;
            }

            if (auto is not null)
            {
                auto.Process(allChannels, inCount, framesThisChunk, (float)masterGain, channelGain, decL, decR);
                Array.Copy(decL, 0, outL, renderedFrames, framesThisChunk);
                Array.Copy(decR, 0, outR, renderedFrames, framesThisChunk);
                renderedFrames += framesThisChunk;
                continue;
            }

            for (int i = 0; i < inCount; i++)
            {
                float l = 0f;
                float r = 0f;

                float v0 = psgBuf[0][i] * psgGain;
                float v1 = psgBuf[1][i] * psgGain;
                float v2 = psgBuf[2][i] * psgGain;
                l += v0 * psgPan[0].L + v1 * psgPan[1].L + v2 * psgPan[2].L;
                r += v0 * psgPan[0].R + v1 * psgPan[1].R + v2 * psgPan[2].R;

                for (int ch = 0; ch < 5; ch++)
                {
                    float v = sccBuf[ch][i] * sccGain;
                    l += v * sccPan[ch].L;
                    r += v * sccPan[ch].R;
                }

                mixL[i] = (float)(l * masterGain);
                mixR[i] = (float)(r * masterGain);
            }

            decimator.Process(mixL, mixR, inCount, decL, decR, framesThisChunk);
            Array.Copy(decL, 0, outL, renderedFrames, framesThisChunk);
            Array.Copy(decR, 0, outR, renderedFrames, framesThisChunk);
            renderedFrames += framesThisChunk;
        }

        // Post-processing on the whole signal (keeps the filter state continuous).
        if (profile.HighPassHz > 0 && auto is null)
        {
            var hpL = Biquad.CreateHighPass(sampleRate, profile.HighPassHz, 0.707);
            var hpR = Biquad.CreateHighPass(sampleRate, profile.HighPassHz, 0.707);
            for (int i = 0; i < totalFrames; i++)
            {
                outL[i] = (float)hpL.Process(outL[i]);
                outR[i] = (float)hpR.Process(outR[i]);
            }
        }

        if (profile.LowPassHz > 0 && auto is null)
        {
            var lpL = Biquad.CreateLowPass(sampleRate, profile.LowPassHz, 0.707);
            var lpR = Biquad.CreateLowPass(sampleRate, profile.LowPassHz, 0.707);
            for (int i = 0; i < totalFrames; i++)
            {
                outL[i] = (float)lpL.Process(outL[i]);
                outR[i] = (float)lpR.Process(outR[i]);
            }
        }

        if (profile.ReverbMix > 0 && auto is null)
        {
            var reverb = new StereoReverb(sampleRate);
            reverb.Process(outL, outR, totalFrames, Math.Min(1f, profile.ReverbMix));
        }

        // Slice the requested range and apply optional normalization.
        float peak = 0f;
        for (int i = startFrame; i < totalFrames; i++)
        {
            peak = Math.Max(peak, Math.Abs(outL[i]));
            peak = Math.Max(peak, Math.Abs(outR[i]));
        }

        float scale = 1f;
        if (profile.NormalizePeak > 0 && peak > 0.0001f)
            scale = profile.NormalizePeak / peak;
        if (peak * scale > 1f)
            scale = 1f / peak;

        var samples = new short[outFrames * 2];
        float finalPeak = 0f;
        for (int i = 0; i < outFrames; i++)
        {
            float l = outL[startFrame + i] * scale;
            float r = outR[startFrame + i] * scale;
            if (l > 1f) l = 1f; else if (l < -1f) l = -1f;
            if (r > 1f) r = 1f; else if (r < -1f) r = -1f;

            finalPeak = Math.Max(finalPeak, Math.Abs(l));
            finalPeak = Math.Max(finalPeak, Math.Abs(r));

            samples[i * 2] = (short)Math.Clamp((int)Math.Round(l * 32767f), short.MinValue, short.MaxValue);
            samples[i * 2 + 1] = (short)Math.Clamp((int)Math.Round(r * 32767f), short.MinValue, short.MaxValue);
        }

        return new RenderedAudio(samples, sampleRate, 2, finalPeak);
    }

    /// <summary>
    /// Estimates when a VGM stops being audible, looking only at the volume/key registers (without
    /// rendering). Used to avoid rendering the ~12 s of silence that a short effect capture carries
    /// at the end.
    /// </summary>
    /// <param name="vgm">Source VGM.</param>
    /// <param name="tailSeconds">Extra tail after the last audible instant.</param>
    public static double FindAudibleEnd(VgmFile vgm, double tailSeconds = 0.3)
    {
        ArgumentNullException.ThrowIfNull(vgm);

        var psgVolume = new int[3];
        var psgMask = 0x3F; // everything off until the game enables it
        var sccVolume = new int[5];
        var sccKey = 0;
        long lastAudible = 0;

        foreach (VgmEvent e in vgm.Events)
        {
            if (e.Kind == VgmEventKind.Psg)
            {
                switch (e.P0)
                {
                    case 7:
                        psgMask = e.P1;
                        break;
                    case 8:
                    case 9:
                    case 10:
                        psgVolume[e.P0 - 8] = e.P1;
                        break;
                }

                for (int ch = 0; ch < 3; ch++)
                {
                    bool channelOff = ((psgMask >> ch) & 1) != 0 && ((psgMask >> (ch + 3)) & 1) != 0;
                    if (!channelOff && psgVolume[ch] != 0) lastAudible = e.Sample;
                }
            }
            else
            {
                if (e.P0 == 2 && e.P1 < 5)
                {
                    sccVolume[e.P1] = e.P2 & 0x0F;
                }
                else if (e.P0 == 3)
                {
                    sccKey = e.P2;
                }

                for (int ch = 0; ch < 5; ch++)
                {
                    if (((sccKey >> ch) & 1) != 0 && sccVolume[ch] != 0) lastAudible = e.Sample;
                }
            }
        }

        return Math.Min(lastAudible / 44100.0 + tailSeconds, vgm.TotalSeconds > 0 ? vgm.TotalSeconds : double.MaxValue);
    }

    /// <summary>
    /// Renders the way the game plays it: an exact loop when the song has one, or from the start to
    /// the last event (+ tail) for a jingle.
    /// </summary>
    public static RenderedAudio RenderForPlayback(VgmFile vgm, AudioProfile profile, int sampleRate = 48000)
    {
        if (vgm.HasLoop)
            return Render(vgm, profile, sampleRate, vgm.LoopStartSeconds, vgm.LoopSeconds);

        double end = vgm.Events.Count > 0 ? vgm.Events[^1].Sample / 44100.0 + 1.2 : vgm.TotalSeconds;
        return Render(vgm, profile, sampleRate, 0, end);
    }

    private static long EventInternalFrame(long sample44100, double internalRate) =>
        (long)Math.Round(sample44100 * internalRate / 44100.0);

    private static (float L, float R)[] BuildPan(float[] pan, int channels)
    {
        var result = new (float L, float R)[channels];
        for (int i = 0; i < channels; i++)
        {
            float p = i < pan.Length ? Math.Clamp(pan[i], -1f, 1f) : 0f;
            double theta = (p + 1.0) * Math.PI / 4.0; // constant-power panning
            result[i] = ((float)Math.Cos(theta), (float)Math.Sin(theta));
        }

        return result;
    }
}
