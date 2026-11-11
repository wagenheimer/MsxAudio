using System;

namespace Wagenheimer.MsxAudio.Chips;

/// <summary>
/// Emulador do Konami SCC (K051649), o chip de 5 canais de wavetable dos cartuchos MSX.
/// </summary>
/// <remarks>
/// Porte fiel do núcleo <c>k051649</c> do MAME/libvgm: mesma tabela de 32 bytes por canal,
/// mesmo acumulador de fase de 16 bits, mesmo reset de contador na escrita da frequência e
/// o mesmo corte de canais com período &lt; 9. A saída é por canal, sem mixagem.
///
/// <para>O clock usado nos VGMs do MSX é <b>1789772,5 Hz</b> (metade do clock "clássico" de
/// 3579545), o que reproduz a fórmula de hardware <c>f = clock / (32 * (periodo + 1))</c>.</para>
/// </remarks>
public sealed class KonamiScc
{
    private const int FreqBits = 16;
    private const int Channels = 5;

    private struct Channel
    {
        public uint Counter;
        public int Frequency;
        public int Volume;
        public bool Key;
        public int Muted;
    }

    private readonly Channel[] _channels = new Channel[Channels];
    private readonly sbyte[] _wave = new sbyte[Channels * 32];

    private int _test;

    /// <summary>Cria o SCC para uma taxa de amostragem interna.</summary>
    /// <param name="clock">Clock do chip em Hz (MSX: 1789772,5).</param>
    /// <param name="sampleRate">Taxa de amostragem interna em Hz.</param>
    public KonamiScc(double clock = 1789772.5, double sampleRate = 192000)
    {
        if (clock <= 0) throw new ArgumentOutOfRangeException(nameof(clock));
        Clock = clock;
        SampleRate = sampleRate;
        Reset();
    }

    /// <summary>Clock configurado (Hz).</summary>
    public double Clock { get; private set; }

    /// <summary>Taxa interna configurada (Hz).</summary>
    public double SampleRate { get; private set; }

    /// <summary>Recalcula a taxa de amostragem usada para gerar as amostras.</summary>
    public void SetSampleRate(double sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
    }

    /// <summary>Volta ao estado de ligar (sem som).</summary>
    public void Reset()
    {
        Array.Clear(_wave);
        for (int i = 0; i < Channels; i++)
        {
            _channels[i] = default;
        }

        _test = 0;
    }

    /// <summary>
    /// Escrita no chip. <paramref name="port"/> é a "página" de registradores usada pelo VGM:
    /// 0 = wave RAM (0x00..0x7F), 1 = frequência (0x00..0x09), 2 = volume (0x00..0x04),
    /// 3 = key on/off, 4 = wave do canal 5, 5 = test.
    /// </summary>
    public void Write(int port, int register, int value)
    {
        switch (port)
        {
            case 0:
                WriteWave(register, value);
                break;
            case 1:
                WriteFrequency(register, value);
                break;
            case 2:
                WriteVolume(register, value);
                break;
            case 3:
                WriteKeyOnOff(value);
                break;
            case 4:
                WriteWave(register, value);
                break;
            case 5:
                _test = value & 0xff;
                break;
        }
    }

    private void WriteWave(int offset, int value)
    {
        // wave RAM é somente leitura quando o test register manda (bits 6/7)
        if ((_test & 0x40) != 0 || ((_test & 0x80) != 0 && offset >= 0x60)) return;

        if (offset >= 0x60)
        {
            // o canal 5 compartilha a wave RAM com o canal 4 (índice 3)
            _wave[3 * 32 + (offset & 0x1f)] = (sbyte)value;
            _wave[4 * 32 + (offset & 0x1f)] = (sbyte)value;
        }
        else
        {
            _wave[(offset >> 5) * 32 + (offset & 0x1f)] = (sbyte)value;
        }
    }

    private void WriteFrequency(int offset, int value)
    {
        int channel = offset >> 1;
        if (channel >= Channels) return;

        if ((offset & 1) != 0)
            _channels[channel].Frequency = (_channels[channel].Frequency & 0x0ff) | ((value << 8) & 0xf00);
        else
            _channels[channel].Frequency = (_channels[channel].Frequency & 0xf00) | (value & 0xff);

        _channels[channel].Counter &= 0xFFFF0000;

        if ((_test & 0x20) != 0)
        {
            _channels[channel].Counter = 0xFFFFFFFF;
        }
        else if (_channels[channel].Frequency < 9)
        {
            _channels[channel].Counter |= (1u << FreqBits) - 1;
        }
    }

    private void WriteVolume(int offset, int value)
    {
        int channel = offset & 7;
        if (channel >= Channels) return;
        _channels[channel].Volume = value & 0x0f;
    }

    private void WriteKeyOnOff(int value)
    {
        for (int i = 0; i < Channels; i++)
        {
            _channels[i].Key = (value & 1) != 0;
            value >>= 1;
        }
    }

    /// <summary>Silencia um canal (usado pelo mixer para perfis alternativos).</summary>
    public void SetMute(int channel, bool muted)
    {
        if (channel is < 0 or >= Channels) return;
        _channels[channel].Muted = muted ? 1 : 0;
    }

    /// <summary>Renderiza um bloco nos 5 buffers de canal.</summary>
    public void Render(float[][] channels, int count) => Render(channels, 0, count);

    /// <summary>Renderiza <paramref name="count"/> amostras a partir de <paramref name="offset"/>.</summary>
    public void Render(float[][] channels, int offset, int count)
    {
        if (channels.Length < Channels)
            throw new ArgumentException("são necessários 5 buffers de canal", nameof(channels));

        for (int ch = 0; ch < Channels; ch++)
        {
            if (channels[ch].Length < offset + count)
                throw new ArgumentException("buffer de canal menor que a contagem pedida", nameof(count));
        }

        // Passo de fase por amostra: mesmo cálculo do k051649 (mclock << 16 / ((freq+1) * rate/2)).
        Span<uint> steps = stackalloc uint[Channels];
        for (int ch = 0; ch < Channels; ch++)
        {
            int frequency = _channels[ch].Frequency;
            if (frequency > 8 && _channels[ch].Muted == 0)
            {
                double denominator = (frequency + 1) * SampleRate / 2.0;
                steps[ch] = (uint)(Clock * (1 << FreqBits) / denominator + 0.5);
            }
            else
            {
                steps[ch] = 0;
            }
        }

        for (int i = offset; i < offset + count; i++)
        {
            for (int ch = 0; ch < Channels; ch++)
            {
                int value = 0;
                if (steps[ch] != 0)
                {
                    ref Channel c = ref _channels[ch];
                    c.Counter += steps[ch];
                    if (c.Key)
                    {
                        int offs = (int)(c.Counter >> FreqBits) & 0x1f;
                        // 0x80 [wave] * 0x0F [volume] -> +-0x780 por canal
                        int sample = _wave[ch * 32 + offs] * c.Volume;
                        sample >>= 4;
                        value = sample * 32; // escala final do núcleo (256/8)
                    }
                }

                channels[ch][i] = value;
            }
        }
    }
}
