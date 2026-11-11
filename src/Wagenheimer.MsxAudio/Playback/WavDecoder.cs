using System;
using System.IO;
using System.Text;

namespace Wagenheimer.MsxAudio.Playback;

/// <summary>WAV PCM decodificado (8/16/24/32 bits inteiros ou float).</summary>
public sealed class DecodedWav
{
    internal DecodedWav(int sampleRate, int channels, float[] samples)
    {
        SampleRate = sampleRate;
        Channels = channels;
        Samples = samples;
    }

    /// <summary>Taxa de amostragem.</summary>
    public int SampleRate { get; }

    /// <summary>Canais.</summary>
    public int Channels { get; }

    /// <summary>Amostras intercaladas (-1..1).</summary>
    public float[] Samples { get; }

    /// <summary>Converte para mono (média dos canais).</summary>
    public float[] ToMono()
    {
        if (Channels == 1) return Samples;

        int frames = Samples.Length / Channels;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            float sum = 0;
            for (int c = 0; c < Channels; c++) sum += Samples[i * Channels + c];
            mono[i] = sum / Channels;
        }

        return mono;
    }
}

/// <summary>Leitor mínimo de WAV (RIFF) usado pelos samples dos pacotes.</summary>
public static class WavDecoder
{
    /// <summary>Decodifica um WAV em memória.</summary>
    public static DecodedWav Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 44 || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F')
            throw new InvalidDataException("WAV inválido (sem cabeçalho RIFF).");

        int format = 1;
        int channels = 1;
        int sampleRate = 44100;
        int bits = 16;
        int blockAlign = 2;

        int position = 12;
        while (position + 8 <= data.Length)
        {
            string id = Encoding.ASCII.GetString(data, position, 4);
            int size = BitConverter.ToInt32(data, position + 4);

            if (id == "fmt ")
            {
                format = BitConverter.ToInt16(data, position + 8);
                channels = BitConverter.ToInt16(data, position + 10);
                sampleRate = BitConverter.ToInt32(data, position + 12);
                blockAlign = BitConverter.ToInt16(data, position + 20);
                bits = BitConverter.ToInt16(data, position + 22);
            }
            else if (id == "data")
            {
                int bytesPerSample = Math.Max(1, bits / 8);
                if (blockAlign <= 0) blockAlign = bytesPerSample * channels;
                int frames = size / Math.Max(1, blockAlign);
                var samples = new float[frames * channels];

                for (int i = 0; i < frames * channels; i++)
                {
                    int p = position + 8 + i * bytesPerSample;
                    samples[i] = ReadSample(data, p, bytesPerSample, format);
                }

                return new DecodedWav(sampleRate, channels, samples);
            }

            position += 8 + size + (size & 1);
        }

        throw new InvalidDataException("WAV sem bloco 'data'.");
    }

    private static float ReadSample(byte[] data, int offset, int bytesPerSample, int format)
    {
        switch (format)
        {
            case 3 when bytesPerSample == 4:
                return BitConverter.ToSingle(data, offset);
            case 1:
                return bytesPerSample switch
                {
                    1 => (data[offset] - 128) / 128f,
                    2 => BitConverter.ToInt16(data, offset) / 32768f,
                    3 => ((data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16)) << 8 >> 8) / 8388608f,
                    4 => BitConverter.ToInt32(data, offset) / 2147483648f,
                    _ => 0f,
                };
            default:
                return 0f;
        }
    }
}
