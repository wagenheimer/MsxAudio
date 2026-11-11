using System;
using Wagenheimer.MsxAudio.Chips;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Live VGM player: walks the register-write log in real time, emulates the PSG and the SCC and
/// mixes the 8 channels with individual gains, exposing per-channel telemetry for the visualizer.
/// </summary>
/// <remarks>
/// Unlike <see cref="MsxSoundRenderer"/> (offline), this keeps the chip state while playing and
/// supports seeking, looping without restarting the chips, and muting the chips or single channels.
/// </remarks>
public sealed class VgmPlayer : IDisposable
{
    private const double SccClock = 1789772.5;
    private const int MaxFrames = 2048;

    private readonly VgmFile _vgm;
    private readonly AudioProfile _profile;
    private readonly int _oversample;

    private readonly Ay8910 _psg;
    private readonly KonamiScc _scc;
    private readonly FirDecimator _decimator;

    private readonly float[][] _psgBuf;
    private readonly float[][] _sccBuf;
    private readonly float[] _mixL;
    private readonly float[] _mixR;
    private readonly float[] _decL;
    private readonly float[] _decR;

    private readonly (float L, float R)[] _psgPan = new (float, float)[3];
    private readonly (float L, float R)[] _sccPan = new (float, float)[5];

    private readonly float[][] _channelRing;
    private int _ringPos;

    private readonly float[] _scopeRing = new float[2048];
    private int _scopePos;

    // Register copies (for the UI; the chips keep their own state).
    private readonly byte[] _psgRegs = new byte[16];
    private readonly int[] _sccPeriod = new int[5];
    private readonly int[] _sccVolume = new int[5];
    private int _sccKeyMask;

    private long _samplePos;      // position in VGM samples (44100 Hz)
    private long _timeShift;      // added to event samples (the loop shifts it back)
    private long _internalPos;
    private int _eventIndex;

    private readonly bool _loop;
    private bool _finished;

    public VgmPlayer(VgmFile vgm, bool loop, int sampleRate = 48000, int oversample = 4, AudioProfile? profile = null)
    {
        _vgm = vgm;
        _loop = loop && vgm.HasLoop;
        SampleRate = sampleRate;
        _oversample = Math.Clamp(oversample, 1, 8);
        _profile = profile ?? AudioProfile.Stereo;

        double internalRate = (double)sampleRate * _oversample;
        _psg = new Ay8910(vgm.AyClock, internalRate);
        _scc = new KonamiScc(SccClock, internalRate);
        _decimator = new FirDecimator(_oversample);

        int capacity = MaxFrames * _oversample;
        _psgBuf = new[] { new float[capacity], new float[capacity], new float[capacity] };
        _sccBuf = Enumerable.Range(0, 5).Select(_ => new float[capacity]).ToArray();
        _mixL = new float[capacity];
        _mixR = new float[capacity];
        _decL = new float[MaxFrames];
        _decR = new float[MaxFrames];

        for (int i = 0; i < 3; i++)
        {
            float pan = _profile.PsgPan.Length > i ? _profile.PsgPan[i] : 0f;
            _psgPan[i] = ((float)Math.Cos((pan + 1) * Math.PI / 4), (float)Math.Sin((pan + 1) * Math.PI / 4));
        }

        for (int i = 0; i < 5; i++)
        {
            float pan = _profile.SccPan.Length > i ? _profile.SccPan[i] : 0f;
            _sccPan[i] = ((float)Math.Cos((pan + 1) * Math.PI / 4), (float)Math.Sin((pan + 1) * Math.PI / 4));
        }

        _channelRing = Enumerable.Range(0, ChannelCount).Select(_ => new float[192]).ToArray();

        Channels = new ChannelInfo[ChannelCount];
        for (int i = 0; i < ChannelCount; i++)
            Channels[i] = new ChannelInfo(i < 3 ? "PSG" : "SCC", i < 3 ? i + 1 : i - 2);

        Restart();
    }

    public const int ChannelCount = 8;

    public int SampleRate { get; }
    public VgmFile Song => _vgm;
    public string Name => _vgm.Name;
    public double TotalSeconds => _vgm.TotalSeconds;
    public double Time => (double)_samplePos / 44100.0;
    public bool Finished => _finished;
    public bool Looping => _loop;
    public int Oversample => _oversample;

    /// <summary>Per-channel state for the UI (frequency, volume, level, key).</summary>
    public sealed class ChannelInfo
    {
        internal ChannelInfo(string chip, int number)
        {
            Chip = chip;
            Number = number;
        }

        public string Chip { get; }
        public int Number { get; }
        public double Frequency { get; internal set; }
        public float Volume { get; internal set; }
        public float Level { get; internal set; }
        public bool Key { get; internal set; }
        public float Peak { get; internal set; }
        public int Period { get; internal set; }
    }

    public ChannelInfo[] Channels { get; }

    public bool PsgEnabled { get; set; } = true;
    public bool SccEnabled { get; set; } = true;
    public bool[] ChannelMuted { get; } = new bool[ChannelCount];

    /// <summary>When set, only this channel is audible (0..7); <c>null</c> = all.</summary>
    public int? SoloChannel { get; set; }

    /// <summary>Copies the 32-sample SCC wave RAM of a channel (normalised -1..1).</summary>
    public void CopySccWave(int sccChannel, Span<float> destination)
    {
        int count = Math.Min(32, destination.Length);
        for (int i = 0; i < count; i++)
            destination[i] = _scc.GetWaveSample(sccChannel, i) / 128f;
    }

    public AudioProfile Profile => _profile;

    /// <summary>Ring of decimated samples per channel.</summary>
    public float[] ChannelScope(int channel) => _channelRing[channel];
    public int ChannelScopePos => _ringPos;
    public int ChannelScopeLength => _channelRing[0].Length;

    /// <summary>Ring of the mixed mono signal (oscilloscope/spectrum).</summary>
    public float[] Scope => _scopeRing;
    public int ScopePos => _scopePos;

    public void Restart() => Seek(0);

    /// <summary>Jumps to an instant (seconds), replaying the register writes to rebuild the chip state.</summary>
    public void Seek(double seconds)
    {
        _psg.Reset();
        _scc.Reset();
        _decimator.Reset();
        _eventIndex = 0;
        _timeShift = 0;
        _samplePos = (long)Math.Clamp(seconds * 44100.0, 0, _vgm.TotalSamples);

        // The internal timeline must be aligned with the VGM samples, otherwise the next writes are
        // considered "far in the future" and the chips stay frozen (the music is lost after a seek).
        _internalPos = (long)Math.Round(_samplePos * ((double)SampleRate * _oversample / 44100.0));

        _finished = false;
        Array.Clear(_psgRegs);
        Array.Clear(_sccPeriod);
        Array.Clear(_sccVolume);
        _sccKeyMask = 0;

        var events = _vgm.Events;
        while (_eventIndex < events.Count && events[_eventIndex].Sample <= _samplePos)
        {
            var e = events[_eventIndex++];
            if (e.Kind == VgmEventKind.Psg) _psg.Write(e.P0, e.P1);
            else _scc.Write(e.P0, e.P1, e.P2);
        }

        RefreshRegisters();
    }

    /// <summary>Renders interleaved stereo into <paramref name="destination"/>.</summary>
    public void Render(float[] destination, int frames)
    {
        if (_finished || frames <= 0)
        {
            Array.Clear(destination, 0, Math.Max(0, frames) * 2);
            return;
        }

        int os = _oversample;
        int inCount = frames * os;
        if (inCount > _mixL.Length)
            throw new ArgumentOutOfRangeException(nameof(frames), "chunk too large (max 2048 frames)");

        // 1) Walk the event log and render the chips in segments between writes.
        int done = 0;
        while (done < inCount)
        {
            long nextEvent = long.MaxValue;
            if (_eventIndex < _vgm.Events.Count)
                nextEvent = EventInternalFrame(_vgm.Events[_eventIndex].Sample);

            long room = nextEvent - (_internalPos + done);
            if (room <= 0)
            {
                while (_eventIndex < _vgm.Events.Count &&
                       EventInternalFrame(_vgm.Events[_eventIndex].Sample) <= _internalPos + done)
                {
                    var e = _vgm.Events[_eventIndex++];
                    if (e.Kind == VgmEventKind.Psg) _psg.Write(e.P0, e.P1);
                    else _scc.Write(e.P0, e.P1, e.P2);
                    RecordRegister(e);
                }

                RefreshRegisters();
                continue;
            }

            int segment = (int)Math.Min(inCount - done, room);
            _psg.Render(_psgBuf[0], _psgBuf[1], _psgBuf[2], done, segment);
            _scc.Render(_sccBuf, done, segment);
            done += segment;
        }

        // 2) Mix with per-chip/per-channel gains.
        float psgGain = PsgEnabled ? _profile.PsgGain : 0f;
        float sccGain = SccEnabled ? _profile.SccGain : 0f;

        Array.Clear(_mixL, 0, inCount);
        Array.Clear(_mixR, 0, inCount);

        for (int ch = 0; ch < ChannelCount; ch++)
        {
            bool on = ch < 3 ? PsgEnabled : SccEnabled;
            if (!on || ChannelMuted[ch])
                continue;
            if (SoloChannel is int solo && solo != ch)
                continue;

            float[] buf = ch < 3 ? _psgBuf[ch] : _sccBuf[ch - 3];
            float gain = ch < 3 ? psgGain : sccGain;
            var (pl, pr) = ch < 3 ? _psgPan[ch] : _sccPan[ch - 3];
            for (int i = 0; i < inCount; i++)
            {
                float v = buf[i] * gain * _profile.MasterGain;
                _mixL[i] += v * pl;
                _mixR[i] += v * pr;
            }
        }

        // 3) Decimate to the output rate, fill the scopes.
        _decimator.Process(_mixL, _mixR, inCount, _decL, _decR, frames);

        for (int i = 0; i < frames; i++)
        {
            destination[i * 2] = Math.Clamp(_decL[i], -1f, 1f);
            destination[i * 2 + 1] = Math.Clamp(_decR[i], -1f, 1f);

            float mono = (destination[i * 2] + destination[i * 2 + 1]) * 0.5f;
            _scopeRing[_scopePos] = mono;
            _scopePos = (_scopePos + 1) % _scopeRing.Length;
        }

        // 4) Per-channel scope (one point per output frame) + levels.
        for (int ch = 0; ch < ChannelCount; ch++)
        {
            float[] buf = ch < 3 ? _psgBuf[ch] : _sccBuf[ch - 3];
            var ring = _channelRing[ch];
            float peak = 0f;
            double sum = 0;

            for (int i = 0; i < frames; i++)
            {
                int start = i * os;
                float acc = 0f;
                for (int k = 0; k < os; k++)
                    acc += buf[start + k];
                acc /= os;

                ring[(_ringPos + i) % ring.Length] = acc;
                float abs = Math.Abs(acc);
                if (abs > peak) peak = abs;
                sum += (double)acc * acc;
            }

            var info = Channels[ch];
            info.Level = (float)Math.Sqrt(sum / Math.Max(1, frames));
            info.Peak = peak;
        }

        _ringPos = (_ringPos + frames) % _channelRing[0].Length;

        _internalPos += inCount;
        _samplePos += (long)Math.Round(frames * 44100.0 / SampleRate);

        // 5) Loop / end.
        if (_samplePos >= _vgm.TotalSamples)
        {
            if (_loop)
                JumpToLoop();
            else
                _finished = true;
        }
    }

    private void JumpToLoop()
    {
        int loopStart = (int)_vgm.LoopSample;
        int loopLen = (int)_vgm.LoopDuration;
        if (loopLen <= 0 || loopStart < 0)
        {
            _finished = true;
            return;
        }

        int timeline = loopStart + loopLen;
        _timeShift -= loopLen;
        _samplePos = loopStart + (_samplePos - timeline);

        int idx = 0;
        var events = _vgm.Events;
        while (idx < events.Count && events[idx].Sample < loopStart)
            idx++;
        _eventIndex = idx;
    }

    private long EventInternalFrame(long eventSample) =>
        (long)Math.Round((eventSample + _timeShift) * ((double)SampleRate * _oversample / 44100.0));

    private void RecordRegister(VgmEvent e)
    {
        if (e.Kind == VgmEventKind.Psg)
        {
            if (e.P0 < _psgRegs.Length)
                _psgRegs[e.P0] = e.P1;
            return;
        }

        switch (e.P0)
        {
            case 1: // frequency, two bytes per channel
                if (e.P1 < 10)
                {
                    int ch = e.P1 / 2;
                    if ((e.P1 & 1) == 0)
                        _sccPeriod[ch] = (_sccPeriod[ch] & 0xFF00) | e.P2;
                    else
                        _sccPeriod[ch] = (_sccPeriod[ch] & 0x00FF) | (e.P2 << 8);
                }

                break;
            case 2: // volume
                if (e.P1 < 5) _sccVolume[e.P1] = e.P2 & 0x0F;
                break;
            case 3: // key on/off (5 bits)
                _sccKeyMask = e.P1;
                break;
        }
    }

    /// <summary>Recomputes frequency/volume/key from the register copies.</summary>
    private void RefreshRegisters()
    {
        for (int ch = 0; ch < 3; ch++)
        {
            int period = _psgRegs[ch * 2] | (_psgRegs[ch * 2 + 1] << 8);
            int volume = _psgRegs[8 + ch] & 0x0F;
            int noise = _psgRegs[7] & 0x1F;
            bool toneOff = (_psgRegs[7] & (1 << ch)) != 0;
            bool noiseOff = (_psgRegs[7] & (1 << (ch + 3))) != 0;

            var info = Channels[ch];
            info.Period = period;
            info.Frequency = period > 0 ? _psg.Clock / (16.0 * (period + 1)) : 0;
            info.Volume = Math.Clamp(volume / 15f, 0f, 1f);
            info.Key = volume > 0 && (!toneOff || !noiseOff);
        }

        for (int ch = 0; ch < 5; ch++)
        {
            int period = _sccPeriod[ch];
            int volume = _sccVolume[ch];
            var info = Channels[ch + 3];
            info.Period = period;
            info.Frequency = period > 0 ? SccClock / (32.0 * (period + 1)) : 0;
            info.Volume = Math.Clamp(volume / 15f, 0f, 1f);
            info.Key = (_sccKeyMask & (1 << ch)) != 0 && volume > 0;
        }
    }

    public void Dispose()
    {
    }
}
