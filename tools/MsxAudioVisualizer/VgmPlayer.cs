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

    // Audition: a second chip pair plays notes triggered from the piano without touching the song.
    private readonly Ay8910 _psgTry;
    private readonly KonamiScc _sccTry;
    private readonly float[][] _psgTryBuf;
    private readonly float[][] _sccTryBuf;
    private int _tryChannel = -1;
    private int _tryRemaining;

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

    private readonly float[] _scopeRing = new float[4096];
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

        // The audition chips are silent until a key is pressed; they never touch the song state.
        _psgTry = new Ay8910(vgm.AyClock, internalRate);
        _sccTry = new KonamiScc(SccClock, internalRate);

        int capacity = MaxFrames * _oversample;
        _psgBuf = new[] { new float[capacity], new float[capacity], new float[capacity] };
        _sccBuf = Enumerable.Range(0, 5).Select(_ => new float[capacity]).ToArray();
        _psgTryBuf = new[] { new float[capacity], new float[capacity], new float[capacity] };
        _sccTryBuf = Enumerable.Range(0, 5).Select(_ => new float[capacity]).ToArray();
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

        _channelRing = Enumerable.Range(0, ChannelCount).Select(_ => new float[1024]).ToArray();

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

    /// <summary>Master volume applied to the final output (0..1).</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>When false the song stops at the end instead of jumping to the loop point.</summary>
    public bool LoopEnabled { get; set; } = true;

    /// <summary>Optional A-B loop: jumps back to <see cref="LoopA"/> after passing <see cref="LoopB"/>.</summary>
    public double? LoopA { get; set; }
    public double? LoopB { get; set; }

    /// <summary>Copies the 32-sample SCC wave RAM of a channel (normalised -1..1).</summary>
    public void CopySccWave(int sccChannel, Span<float> destination)
    {
        int count = Math.Min(32, destination.Length);
        for (int i = 0; i < count; i++)
            destination[i] = _scc.GetWaveSample(sccChannel, i) / 128f;
    }

    public AudioProfile Profile => _profile;

    /// <summary>Channel currently auditioning (0..7), or -1 when idle.</summary>
    public int AuditionChannel => HasAudition ? _tryChannel : -1;

    /// <summary>MIDI note currently auditioning, or -1 when idle.</summary>
    public int AuditionNote => HasAudition ? _auditionNote : -1;

    private int _auditionNote = -1;

    /// <summary>True while an audition note is sounding (including its short release tail).</summary>
    public bool HasAudition => _tryRemaining > 0 || _tryRelease > 0;

    // Release ramp: a held note ends with a few milliseconds of fade instead of a click.
    private int _tryRelease;
    private int _tryReleaseTotal;

    /// <summary>
    /// Plays a fixed-length note on a channel using the instrument the song is using right now (PSG
    /// envelope and noise, SCC wave RAM), without disturbing the song.
    /// </summary>
    /// <param name="channel">0..2 = PSG1..3, 3..7 = SCC1..5.</param>
    /// <param name="midi">MIDI note (60 = C4).</param>
    /// <param name="seconds">Note length in seconds.</param>
    public void Audition(int channel, int midi, double seconds = 0.9)
    {
        int frames = (int)Math.Round(Math.Max(0.05, seconds) * SampleRate * _oversample);
        StartAudition(channel, midi, Math.Max(1, frames));
    }

    /// <summary>
    /// Note-on that keeps sounding until <see cref="StopAudition"/> (used by the piano keys, so the
    /// note lasts as long as the key is held). Changing the pitch while held retunes the same voice.
    /// </summary>
    public void AuditionHold(int channel, int midi) => StartAudition(channel, midi, int.MaxValue);

    private void StartAudition(int channel, int midi, int frames)
    {
        if (channel < 0 || channel >= ChannelCount || midi < 0 || midi > 127)
            return;

        _tryChannel = channel;
        _tryRemaining = frames;
        _tryRelease = 0;
        _tryReleaseTotal = 0;
        _auditionNote = midi;

        double frequency = 440.0 * Math.Pow(2, (midi - 69) / 12.0);

        if (channel < 3)
        {
            AuditionPsg(channel, frequency);
            return;
        }

        AuditionScc(channel - 3, frequency);
    }

    /// <summary>Note-off: starts the short release ramp (the note stops after it).</summary>
    public void StopAudition()
    {
        if (_tryRemaining > 0)
            BeginRelease();

        _tryRemaining = 0;
        _auditionNote = -1;
    }

    private void BeginRelease()
    {
        if (_tryChannel < 0)
            return;

        _tryReleaseTotal = Math.Max(1, (int)Math.Round(0.015 * SampleRate * _oversample));
        _tryRelease = _tryReleaseTotal;
    }

    private void KeyOffTry()
    {
        if (_tryChannel < 3)
        {
            for (int ch = 0; ch < 3; ch++)
                _psgTry.Write(8 + ch, 0);
        }
        else
        {
            _sccTry.Write(3, 0, 0);
        }
    }

    private void AuditionPsg(int channel, double frequency)
    {
        // Same instrument as the song: envelope period/shape, noise frequency and mixer routing,
        // but a single channel keyed and the pitch taken from the requested note.
        _psgTry.Write(6, _psgRegs[6]);
        _psgTry.Write(11, _psgRegs[11]);
        _psgTry.Write(12, _psgRegs[12]);
        _psgTry.Write(13, _psgRegs[13]);

        bool songToneOn = (_psgRegs[7] & (1 << channel)) == 0;
        bool songNoiseOn = (_psgRegs[7] & (1 << (channel + 3))) == 0;

        // Disable tone and noise on the two channels that are not auditioning.
        int mixer = 0x07 & ~(1 << channel);
        mixer |= 0x38 & ~(1 << (channel + 3));
        if (!songToneOn) mixer |= 1 << channel;
        if (!songNoiseOn) mixer |= 1 << (channel + 3);
        _psgTry.Write(7, mixer);

        int volume = _psgRegs[8 + channel] & 0x0F;
        if (volume < 12) volume = 12;   // audition at a usable level; the timbre is in wave/envelope

        for (int ch = 0; ch < 3; ch++)
            _psgTry.Write(8 + ch, ch == channel ? volume : 0);

        int period = (int)Math.Round(_psg.Clock / (16.0 * frequency) - 1);
        period = Math.Clamp(period, 1, 4095);
        _psgTry.Write(channel * 2, period & 0xFF);
        _psgTry.Write(channel * 2 + 1, (period >> 8) & 0x0F);
    }

    private void AuditionScc(int sccChannel, double frequency)
    {
        // Copy the instrument (32-sample wave) into bank 0 of the audition chip, so the pitch does
        // not depend on which bank the song happens to use.
        for (int i = 0; i < 32; i++)
            _sccTry.Write(0, i, _scc.GetWaveSample(sccChannel, i) & 0xFF);

        int period = (int)Math.Round(SccClock / (32.0 * frequency) - 1);
        period = Math.Clamp(period, 0, 4095);

        _sccTry.Write(1, 0, period & 0xFF);
        _sccTry.Write(1, 1, (period >> 8) & 0x0F);

        int volume = _sccVolume[sccChannel];
        if (volume < 12) volume = 12;   // audition at a usable level
        _sccTry.Write(2, 0, volume);

        _sccTry.Write(3, 0, 1);
    }

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

        // 3) Audition: a separate chip pair plays the notes triggered from the piano, mixed over the
        // song without touching its register state.
        if (_tryRemaining > 0 || _tryRelease > 0)
            MixAudition(inCount);

        // 4) Decimate to the output rate, fill the scopes.
        _decimator.Process(_mixL, _mixR, inCount, _decL, _decR, frames);

        for (int i = 0; i < frames; i++)
        {
            float volume = Math.Clamp(Volume, 0f, 1f);
            destination[i * 2] = Math.Clamp(_decL[i] * volume, -1f, 1f);
            destination[i * 2 + 1] = Math.Clamp(_decR[i] * volume, -1f, 1f);

            float mono = (destination[i * 2] + destination[i * 2 + 1]) * 0.5f;
            _scopeRing[_scopePos] = mono;
            _scopePos = (_scopePos + 1) % _scopeRing.Length;
        }

        // 5) Per-channel scope (one point per output frame) + levels.
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

        // 6) A-B loop, then loop / end.
        if (LoopA is double a && LoopB is double b && b > a &&
            _samplePos >= (long)Math.Round(b * 44100.0))
        {
            Seek(a);
            return;
        }

        if (_samplePos >= _vgm.TotalSamples)
        {
            if (_loop && LoopEnabled)
                JumpToLoop();
            else
                _finished = true;
        }
    }

    /// <summary>
    /// Renders the audition note alone (no song), for when playback is paused. The song position
    /// does not advance; only the audition chips are clocked.
    /// </summary>
    public void RenderAudition(float[] destination, int frames)
    {
        if (frames <= 0)
            return;

        int os = _oversample;
        int inCount = frames * os;
        if (inCount > _mixL.Length)
            throw new ArgumentOutOfRangeException(nameof(frames), "chunk too large (max 2048 frames)");

        Array.Clear(_mixL, 0, inCount);
        Array.Clear(_mixR, 0, inCount);

        if (_tryRemaining > 0 || _tryRelease > 0)
            MixAudition(inCount);

        _decimator.Process(_mixL, _mixR, inCount, _decL, _decR, frames);

        for (int i = 0; i < frames; i++)
        {
            float volume = Math.Clamp(Volume, 0f, 1f);
            destination[i * 2] = Math.Clamp(_decL[i] * volume, -1f, 1f);
            destination[i * 2 + 1] = Math.Clamp(_decR[i] * volume, -1f, 1f);
        }
    }

    private void MixAudition(int inCount)
    {
        // While the note is held, render the whole chunk; while releasing, only the ramp is left.
        int count = _tryRemaining > 0
            ? Math.Min(inCount, _tryRemaining)
            : Math.Min(inCount, _tryRelease);

        if (count <= 0)
        {
            KeyOffTry();
            return;
        }

        bool releasing = _tryRelease > 0;
        int releaseLeft = _tryRelease;
        float releaseTotal = Math.Max(1, _tryReleaseTotal);

        const float auditionBoost = 1.35f;

        if (_tryChannel < 3)
        {
            _psgTry.Render(_psgTryBuf[0], _psgTryBuf[1], _psgTryBuf[2], 0, count);

            float[] buf = _psgTryBuf[_tryChannel];
            var (pl, pr) = _psgPan[_tryChannel];
            float gain = _profile.PsgGain * _profile.MasterGain * auditionBoost;

            for (int i = 0; i < count; i++)
            {
                float env = 1f;
                if (releasing)
                {
                    env = releaseLeft / releaseTotal;
                    if (releaseLeft > 0) releaseLeft--;
                }

                float v = buf[i] * gain * env;
                _mixL[i] += v * pl;
                _mixR[i] += v * pr;
            }
        }
        else
        {
            _sccTry.Render(_sccTryBuf, 0, count);

            float[] buf = _sccTryBuf[0];
            var (pl, pr) = _sccPan[Math.Clamp(_tryChannel - 3, 0, 4)];
            float gain = _profile.SccGain * _profile.MasterGain * auditionBoost;

            for (int i = 0; i < count; i++)
            {
                float env = 1f;
                if (releasing)
                {
                    env = releaseLeft / releaseTotal;
                    if (releaseLeft > 0) releaseLeft--;
                }

                float v = buf[i] * gain * env;
                _mixL[i] += v * pl;
                _mixR[i] += v * pr;
            }
        }

        if (releasing)
            _tryRelease = releaseLeft;

        if (_tryRemaining > 0)
        {
            _tryRemaining -= inCount;
            if (_tryRemaining <= 0)
            {
                // The fixed-length note (demo) ended: fade it out instead of cutting it.
                _tryRemaining = 0;
                _auditionNote = -1;
                BeginRelease();
            }
        }

        if (_tryRemaining <= 0 && _tryRelease <= 0)
            KeyOffTry();
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
            case 3: // key on/off (5 bits) — the value travels in the third byte
                _sccKeyMask = e.P2;
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
