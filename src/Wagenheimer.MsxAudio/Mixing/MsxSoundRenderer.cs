using System;
using Wagenheimer.MsxAudio.Chips;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Mixing;

/// <summary>Áudio PCM pronto (16 bits, intercalado).</summary>
public sealed class RenderedAudio
{
    /// <summary>Cria o resultado.</summary>
    public RenderedAudio(short[] samples, int sampleRate, int channels, float peak)
    {
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
        Peak = peak;
    }

    /// <summary>Amostras PCM 16 bits intercaladas.</summary>
    public short[] Samples { get; }

    /// <summary>Taxa de amostragem (Hz).</summary>
    public int SampleRate { get; }

    /// <summary>Canais (1 = mono, 2 = estéreo).</summary>
    public int Channels { get; }

    /// <summary>Pico absoluto (0..1) depois do processamento.</summary>
    public float Peak { get; }

    /// <summary>Quantidade de quadros (amostras por canal).</summary>
    public int Frames => Channels > 0 ? Samples.Length / Channels : 0;

    /// <summary>Duração em segundos.</summary>
    public double Seconds => SampleRate > 0 ? (double)Frames / SampleRate : 0;

    /// <summary>PCM em bytes (little endian), no formato que o MonoGame espera.</summary>
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
/// Renderiza um VGM (PSG + SCC) em PCM, usando o <see cref="AudioProfile"/> escolhido.
/// </summary>
/// <remarks>
/// O caminho é sempre o mesmo: os chips são emulados em <c>taxa × sobreamostragem</c> e a
/// decimação usa um filtro FIR (sinc com janela Blackman), o que evita o serrilhado/aliasing
/// de uma captura em 44,1 kHz. O estado do chip começa em zero e é percorrido desde o início
/// do VGM, então cada trecho é reproduzido em fase com o original.
/// </remarks>
public static class MsxSoundRenderer
{
    /// <summary>Clock do SCC usado pelos VGMs de MSX (Hz).</summary>
    public const double SccClock = 1789772.5;

    /// <summary>Renderiza um intervalo do VGM.</summary>
    /// <param name="vgm">VGM de origem.</param>
    /// <param name="profile">Perfil de mixagem.</param>
    /// <param name="sampleRate">Taxa de saída (padrão 48000).</param>
    /// <param name="startSeconds">Instante inicial no VGM.</param>
    /// <param name="durationSeconds">Duração (0 = até o fim da captura).</param>
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

        // Pós-processamento no sinal inteiro (mantém o estado dos filtros contínuo).
        if (profile.HighPassHz > 0)
        {
            var hpL = Biquad.CreateHighPass(sampleRate, profile.HighPassHz, 0.707);
            var hpR = Biquad.CreateHighPass(sampleRate, profile.HighPassHz, 0.707);
            for (int i = 0; i < totalFrames; i++)
            {
                outL[i] = (float)hpL.Process(outL[i]);
                outR[i] = (float)hpR.Process(outR[i]);
            }
        }

        if (profile.LowPassHz > 0)
        {
            var lpL = Biquad.CreateLowPass(sampleRate, profile.LowPassHz, 0.707);
            var lpR = Biquad.CreateLowPass(sampleRate, profile.LowPassHz, 0.707);
            for (int i = 0; i < totalFrames; i++)
            {
                outL[i] = (float)lpL.Process(outL[i]);
                outR[i] = (float)lpR.Process(outR[i]);
            }
        }

        if (profile.ReverbMix > 0)
        {
            var reverb = new StereoReverb(sampleRate);
            reverb.Process(outL, outR, totalFrames, Math.Min(1f, profile.ReverbMix));
        }

        // Recorte do trecho pedido + normalização opcional.
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
    /// Estima em que instante o som de um VGM deixa de ser audível, olhando só os registradores
    /// de volume/chave (sem renderizar). Usado para não renderizar os ~12 s de silêncio que a
    /// gravação de um efeito curto carrega no fim.
    /// </summary>
    /// <param name="vgm">VGM de origem.</param>
    /// <param name="tailSeconds">Cauda extra depois do último instante audível.</param>
    public static double FindAudibleEnd(VgmFile vgm, double tailSeconds = 0.3)
    {
        ArgumentNullException.ThrowIfNull(vgm);

        var psgVolume = new int[3];
        var psgMask = 0x3F; // tudo desligado até o jogo ligar
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
    /// Renderiza como o jogo toca: um loop exato quando a música tem loop, ou do começo até o
    /// último evento (+ cauda) quando é jingle.
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
            double theta = (p + 1.0) * Math.PI / 4.0; // panning de potência constante
            result[i] = ((float)Math.Cos(theta), (float)Math.Sin(theta));
        }

        return result;
    }
}
