using System;

namespace Wagenheimer.MsxAudio.Chips;

/// <summary>
/// Emulador do PSG AY-3-8910 (o chip de som do MSX, 3 canais + ruÃ­do + envelope).
/// </summary>
/// <remarks>
/// Porte fiel do nÃºcleo <c>emu2149</c> (Mitsutaka Okazaki, usado pelo VGMPlay), com o caminho
/// de qualidade desligado: cada amostra avanÃ§a o chip por uma fraÃ§Ã£o de ciclo. Isso mantÃ©m
/// exatamente o mesmo comportamento (tom, ruÃ­do, envelope e tabela de volume do AY) que o
/// player de VGM de referÃªncia â€” Ã© o que garante que a mÃºsica renderizada aqui soe como o
/// original e nÃ£o como "uma aproximaÃ§Ã£o".
///
/// A saÃ­da Ã© por canal (0..2), em unidades inteiras do chip (0..8160), sem mixagem: quem
/// mixa Ã© o <see cref="Wagenheimer.MsxAudio.Mixing.MsxSoundRenderer"/>, que aplica ganho/pan por canal.
/// </remarks>
public sealed class Ay8910
{
    /// <summary>Tabela de volume do AY-3-8910 (Ã­ndice 0..31, dois degraus por nÃ­vel).</summary>
    private static readonly int[] VolumeAy = BuildVolumeAy();

    /// <summary>MÃ¡scaras dos registradores (bits nÃ£o usados sÃ£o descartados, como no chip).</summary>
    private static readonly byte[] RegisterMask =
    {
        0xff, 0x0f, 0xff, 0x0f, 0xff, 0x0f, 0x1f, 0x3f,
        0x1f, 0x1f, 0x1f, 0xff, 0xff, 0x0f, 0xff, 0xff,
    };

    private const int GetaBits = 24;

    private readonly byte[] _reg = new byte[16];
    private readonly uint[] _count = new uint[3];
    private readonly int[] _freq = new int[3];
    private readonly bool[] _edge = new bool[3];
    private readonly int[] _volume = new int[3];
    private readonly bool[] _toneDisabled = new bool[3];
    private readonly bool[] _noiseDisabled = new bool[3];
    private readonly int[] _output = new int[3];

    private uint _baseCount;
    private uint _baseIncr;

    private int _noiseSeed = 0xffff;
    private int _noiseCount = 0x40;
    private int _noiseFreq;

    private int _envPtr;
    private int _envFreq;
    private int _envCount;
    private bool _envPause = true;
    private bool _envFace;
    private bool _envContinue;
    private bool _envAlternate;
    private bool _envHold;

    /// <summary>Cria o PSG para uma taxa de amostragem interna (normalmente 4x a taxa de saÃ­da).</summary>
    /// <param name="clock">Clock do chip em Hz (no MSX: 1789772,5).</param>
    /// <param name="sampleRate">Taxa de amostragem interna em Hz.</param>
    public Ay8910(double clock = 1789772.5, double sampleRate = 192000)
    {
        if (clock <= 0) throw new ArgumentOutOfRangeException(nameof(clock));
        SetClock(clock, sampleRate);
        Reset();
    }

    /// <summary>Clock configurado (Hz).</summary>
    public double Clock { get; private set; }

    /// <summary>Taxa interna configurada (Hz).</summary>
    public double SampleRate { get; private set; }

    /// <summary>Recalcula os incrementos do chip (ao mudar clock ou taxa de amostragem).</summary>
    public void SetClock(double clock, double sampleRate)
    {
        if (clock <= 0) throw new ArgumentOutOfRangeException(nameof(clock));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        Clock = clock;
        SampleRate = sampleRate;
        // emu2149: base_incr = clk * 2^24 / (8 * rate) â€” a unidade interna Ã© clock/8.
        _baseIncr = (uint)(clock * (1 << GetaBits) / (8.0 * sampleRate));
    }

    /// <summary>Volta ao estado de ligar (registradores em zero, sem som).</summary>
    public void Reset()
    {
        _baseCount = 0;
        Array.Clear(_reg);
        for (int i = 0; i < 3; i++)
        {
            _count[i] = 0x1000;
            _freq[i] = 0;
            _edge[i] = false;
            _volume[i] = 0;
            _toneDisabled[i] = false;
            _noiseDisabled[i] = false;
            _output[i] = 0;
        }

        _noiseSeed = 0xffff;
        _noiseCount = 0x40;
        _noiseFreq = 0;

        _envPtr = 0;
        _envFreq = 0;
        _envCount = 0;
        _envPause = true;
        _envFace = false;
        _envContinue = false;
        _envAlternate = false;
        _envHold = false;
    }

    /// <summary>Escreve um registrador do PSG (0..15).</summary>
    public void Write(int register, int value)
    {
        if (register is < 0 or > 15) return;

        value &= RegisterMask[register];
        _reg[register] = (byte)value;

        switch (register)
        {
            case 0:
            case 1:
            case 2:
            case 3:
            case 4:
            case 5:
            {
                int ch = register >> 1;
                _freq[ch] = ((_reg[ch * 2 + 1] & 15) << 8) + _reg[ch * 2];
                break;
            }
            case 6:
                _noiseFreq = (value & 31) << 1;
                break;
            case 7:
                _toneDisabled[0] = (value & 1) != 0;
                _toneDisabled[1] = (value & 2) != 0;
                _toneDisabled[2] = (value & 4) != 0;
                _noiseDisabled[0] = (value & 8) != 0;
                _noiseDisabled[1] = (value & 16) != 0;
                _noiseDisabled[2] = (value & 32) != 0;
                break;
            case 8:
            case 9:
            case 10:
                _volume[register - 8] = value << 1;
                break;
            case 11:
            case 12:
                _envFreq = (_reg[12] << 8) + _reg[11];
                _envCount = 0x10000 - _envFreq;
                break;
            case 13:
                _envContinue = ((value >> 3) & 1) != 0;
                bool attack = ((value >> 2) & 1) != 0;
                _envAlternate = ((value >> 1) & 1) != 0;
                _envHold = (value & 1) != 0;
                _envFace = attack;
                _envPause = false;
                _envCount = 0x10000 - _envFreq;
                _envPtr = _envFace ? 0 : 0x1f;
                break;
        }
    }

    /// <summary>SaÃ­da bruta (0..8160) do canal <paramref name="channel"/> na Ãºltima amostra.</summary>
    public int GetOutput(int channel) => _output[channel];

    /// <summary>
    /// AvanÃ§a uma amostra e escreve a saÃ­da dos 3 canais nos destinos (pode passar <c>null</c>
    /// para ignorar um canal).
    /// </summary>
    public void RenderSample(float[]? ch0, float[]? ch1, float[]? ch2, int index)
    {
        UpdateOutput();
        ch0?[index] = _output[0];
        ch1?[index] = _output[1];
        ch2?[index] = _output[2];
    }

    /// <summary>Renderiza um bloco de amostras nos 3 buffers de canal (mesmo tamanho).</summary>
    public void Render(float[] ch0, float[] ch1, float[] ch2, int count) =>
        Render(ch0, ch1, ch2, 0, count);

    /// <summary>Renderiza <paramref name="count"/> amostras nos buffers, a partir de <paramref name="offset"/>.</summary>
    public void Render(float[] ch0, float[] ch1, float[] ch2, int offset, int count)
    {
        if (ch0.Length < offset + count || ch1.Length < offset + count || ch2.Length < offset + count)
            throw new ArgumentException("buffers de canal menores que a contagem pedida", nameof(count));

        for (int i = offset; i < offset + count; i++)
        {
            UpdateOutput();
            ch0[i] = _output[0];
            ch1[i] = _output[1];
            ch2[i] = _output[2];
        }
    }

    /// <summary>Um passo do nÃºcleo (mesma ordem de operaÃ§Ãµes do emu2149).</summary>
    private void UpdateOutput()
    {
        _baseCount += _baseIncr;
        int incr = (int)(_baseCount >> GetaBits);
        _baseCount &= (1u << GetaBits) - 1;

        // Envelope
        _envCount += incr;
        while (_envCount >= 0x10000)
        {
            if (!_envPause)
            {
                _envPtr = _envFace ? (_envPtr + 1) & 0x3f : (_envPtr + 0x3f) & 0x3f;
            }

            if ((_envPtr & 0x20) != 0)
            {
                if (_envContinue)
                {
                    if (_envAlternate ^ _envHold) _envFace = !_envFace;
                    if (_envHold) _envPause = true;
                    _envPtr = _envFace ? 0 : 0x1f;
                }
                else
                {
                    _envPause = true;
                    _envPtr = 0;
                }
            }

            _envCount -= _envFreq != 0 ? _envFreq : 1;
        }

        // RuÃ­do (LFSR de 17 bits)
        _noiseCount += incr;
        if ((_noiseCount & 0x40) != 0)
        {
            if ((_noiseSeed & 1) != 0) _noiseSeed ^= 0x24000;
            _noiseSeed >>= 1;
            _noiseCount -= _noiseFreq != 0 ? _noiseFreq : 2;
        }

        int noise = _noiseSeed & 1;

        // Tom
        for (int i = 0; i < 3; i++)
        {
            _count[i] += (uint)incr;
            if ((_count[i] & 0x1000) != 0)
            {
                if (_freq[i] > 1)
                {
                    _edge[i] = !_edge[i];
                    _count[i] -= (uint)_freq[i];
                }
                else
                {
                    _edge[i] = true;
                }
            }

            int value = 0;
            // mÃ¡scaras como no chip: 1 = canal desligado (bit ligado desabilita)
            if ((_toneDisabled[i] || _edge[i]) && (_noiseDisabled[i] || noise != 0))
            {
                int volume = (_volume[i] & 32) != 0 ? VolumeAy[_envPtr] : VolumeAy[_volume[i] & 31];
                value = volume << 5;
            }

            _output[i] = value;
        }
    }

    private static int[] BuildVolumeAy()
    {
        // Tabela EMU2149_VOL_AY_3_8910 (32 degraus, medida no chip).
        return new[]
        {
            0x00, 0x00, 0x03, 0x03, 0x04, 0x04, 0x06, 0x06,
            0x09, 0x09, 0x0D, 0x0D, 0x12, 0x12, 0x1D, 0x1D,
            0x22, 0x22, 0x37, 0x37, 0x4D, 0x4D, 0x62, 0x62,
            0x82, 0x82, 0xA6, 0xA6, 0xD0, 0xD0, 0xFF, 0xFF,
        };
    }
}

