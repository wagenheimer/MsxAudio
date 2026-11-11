using System;

namespace Wagenheimer.MsxAudio.Chips;

/// <summary>
/// Konami SCC (K051649) emulator — the 5-channel wavetable chip of MSX cartridges.
/// </summary>
/// <remarks>
/// Faithful port of the MAME/libvgm <c>k051649</c> core: same 32-byte wave table per channel, same
/// 16-bit phase accumulator, same counter reset on frequency writes, same "period &lt; 9 stops the
/// channel" rule. Output is per channel, without mixing.
///
/// <para>The MSX VGM uses a clock of <b>1789772.5 Hz</b> (half of the "classic" 3579545), which
/// reproduces the hardware formula <c>f = clock / (32 * (period + 1))</c>.</para>
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

    /// <summary>Creates the SCC for an internal sample rate.</summary>
    /// <param name="clock">Chip clock in Hz (MSX: 1789772.5).</param>
    /// <param name="sampleRate">Internal sample rate in Hz.</param>
    public KonamiScc(double clock = 1789772.5, double sampleRate = 192000)
    {
        if (clock <= 0) throw new ArgumentOutOfRangeException(nameof(clock));
        Clock = clock;
        SampleRate = sampleRate;
        Reset();
    }

    /// <summary>Configured clock (Hz).</summary>
    public double Clock { get; }

    /// <summary>Configured internal sample rate (Hz).</summary>
    public double SampleRate { get; private set; }

    /// <summary>Changes the sample rate used to generate samples.</summary>
    public void SetSampleRate(double sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
    }

    /// <summary>Returns to the power-on state (no sound).</summary>
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
    /// Chip write. <paramref name="port"/> is the register "page" used by VGM:
    /// 0 = wave RAM (0x00..0x7F), 1 = frequency (0x00..0x09), 2 = volume (0x00..0x04),
    /// 3 = key on/off, 4 = channel 5 wave, 5 = test.
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
        // wave RAM is read-only when the test register says so (bits 6/7)
        if ((_test & 0x40) != 0 || ((_test & 0x80) != 0 && offset >= 0x60)) return;

        if (offset >= 0x60)
        {
            // channel 5 shares the wave RAM with channel 4 (index 3)
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

    /// <summary>Mutes a channel (used by the mixer for alternative profiles).</summary>
    public void SetMute(int channel, bool muted)
    {
        if (channel is < 0 or >= Channels) return;
        _channels[channel].Muted = muted ? 1 : 0;
    }

    /// <summary>
    /// Returns the raw wave-table byte (signed -128..127) of a channel, for visualisers.
    /// Channel 5 shares the bank of channel 4, as on the chip.
    /// </summary>
    public int GetWaveSample(int channel, int index)
    {
        if (channel is < 0 or >= Channels) return 0;
        return _wave[channel * 32 + (index & 31)];
    }

    /// <summary>Renders a block into the five channel buffers.</summary>
    public void Render(float[][] channels, int count) => Render(channels, 0, count);

    /// <summary>Renders <paramref name="count"/> samples from <paramref name="offset"/>.</summary>
    public void Render(float[][] channels, int offset, int count)
    {
        if (channels.Length < Channels)
            throw new ArgumentException("five channel buffers are required", nameof(channels));

        for (int ch = 0; ch < Channels; ch++)
        {
            if (channels[ch].Length < offset + count)
                throw new ArgumentException("channel buffer smaller than the requested count", nameof(count));
        }

        // Phase step per sample: same calculation as k051649 (mclock << 16 / ((freq+1) * rate/2)).
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
                        // 0x80 [wave] * 0x0F [volume] -> +-0x780 per channel
                        int sample = _wave[ch * 32 + offs] * c.Volume;
                        sample >>= 4;
                        value = sample * 32; // core's final scaling (256/8)
                    }
                }

                channels[ch][i] = value;
            }
        }
    }
}
