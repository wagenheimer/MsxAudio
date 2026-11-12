using System;
using System.Collections.Generic;
using System.Linq;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Mixing;

/// <summary>What a channel does in the song.</summary>
public enum ChannelRole
{
    /// <summary>Nothing audible (or only a blip).</summary>
    Silent,

    /// <summary>The tune.</summary>
    Lead,

    /// <summary>The lowest voice.</summary>
    Bass,

    /// <summary>Fast jumps between a few notes: a chord played one note at a time.</summary>
    Arpeggio,

    /// <summary>Accompaniment: chords, pads, counter-melodies.</summary>
    Harmony,

    /// <summary>Noise: drums and effects.</summary>
    Percussion,
}

/// <summary>The mix chosen for one channel.</summary>
public sealed class ChannelMix
{
    /// <summary>0..2 = PSG A-C, 3..7 = SCC 1-5.</summary>
    public int Channel { get; init; }

    /// <summary>What the channel was recognised as.</summary>
    public ChannelRole Role { get; set; }

    /// <summary>Seconds the channel is audible in the whole song.</summary>
    public double AudibleSeconds { get; init; }

    /// <summary>Middle note (MIDI number) of what it plays; 0 when it is noise only.</summary>
    public double MedianMidi { get; init; }

    /// <summary>Level change in dB.</summary>
    public float GainDb { get; set; }

    /// <summary>-1 (left) .. +1 (right).</summary>
    public float Pan { get; set; }

    /// <summary>Low cut in Hz.</summary>
    public float HighPassHz { get; set; }

    /// <summary>Boost or cut below ~120 Hz, in dB.</summary>
    public float LowShelfDb { get; set; }

    /// <summary>Boost or cut above ~3 kHz, in dB.</summary>
    public float HighShelfDb { get; set; }

    /// <summary>Saturation amount, 0..1 (adds harmonics so the voice carries on small speakers).</summary>
    public float Drive { get; set; }

    /// <summary>How much goes to the reverb, 0..1.</summary>
    public float ReverbSend { get; set; }

    /// <summary>How much goes to the stereo echo, 0..1.</summary>
    public float DelaySend { get; set; }

    /// <summary>Short name: PSG1, SCC3...</summary>
    public string Name => Channel < 3 ? $"PSG{Channel + 1}" : $"SCC{Channel - 2}";
}

/// <summary>The automatic mix of one song: a role and a treatment per channel.</summary>
public sealed class AutoMixPlan
{
    /// <summary>Eight channels (3 PSG + 5 SCC).</summary>
    public ChannelMix[] Channels { get; init; } = Array.Empty<ChannelMix>();

    /// <summary>"BASS PSG1 · LEAD SCC1 · ARP SCC2, SCC3 ..." for the UI.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();

            foreach (ChannelRole role in new[]
                     {
                         ChannelRole.Lead, ChannelRole.Bass, ChannelRole.Arpeggio, ChannelRole.Harmony,
                         ChannelRole.Percussion,
                     })
            {
                string[] names = Channels.Where(c => c.Role == role).Select(c => c.Name).ToArray();
                if (names.Length > 0)
                    parts.Add($"{RoleTag(role)} {string.Join(",", names)}");
            }

            return parts.Count > 0 ? string.Join("  ", parts) : "NOTHING TO MIX";
        }
    }

    /// <summary>Four-letter tag of a role.</summary>
    public static string RoleTag(ChannelRole role) => role switch
    {
        ChannelRole.Lead => "LEAD",
        ChannelRole.Bass => "BASS",
        ChannelRole.Arpeggio => "ARP",
        ChannelRole.Harmony => "PAD",
        ChannelRole.Percussion => "DRUM",
        _ => string.Empty,
    };

    /// <summary>A flat plan: every channel untouched (used when there is nothing to analyse).</summary>
    public static AutoMixPlan Neutral()
    {
        var channels = new ChannelMix[8];
        for (int i = 0; i < channels.Length; i++)
            channels[i] = new ChannelMix { Channel = i, Role = ChannelRole.Harmony };

        return new AutoMixPlan { Channels = channels };
    }
}

/// <summary>
/// Listens to a song's register writes (not to audio) and works out what each channel is doing: the
/// tune, the bass, an arpeggio, a pad or the drums. The notes are known exactly, so the decision does
/// not depend on guessing from a waveform.
/// </summary>
public static class AutoMixAnalyzer
{
    private const double SccClock = 1789772.5;

    private sealed class Track
    {
        public double Audible, NoiseOnly, Noise, VolumeTime;
        public int Notes, FastNotes;
        public double LastNoteAt = -1;
        public int LastMidi = int.MinValue;
        public readonly List<int> Midis = new();

        public double MeanVolume => Audible > 0 ? VolumeTime / Audible : 0;
    }

    /// <summary>Analyses the song and returns the plan.</summary>
    public static AutoMixPlan Analyze(VgmFile vgm)
    {
        ArgumentNullException.ThrowIfNull(vgm);

        var psg = new byte[16];
        psg[7] = 0x38;                                   // typical state after a driver's init: tone on, noise off
        var sccRegs = new byte[10];
        var sccVolume = new int[5];
        int sccEnable = 0;

        double psgClock = vgm.AyClock > 0 ? vgm.AyClock : 1789772.5;
        var tracks = Enumerable.Range(0, 8).Select(_ => new Track()).ToArray();
        double last = 0;

        foreach (VgmEvent e in vgm.Events)
        {
            double now = e.Sample / 44100.0;
            double dt = now - last;
            last = now;

            if (dt > 0)
            {
                for (int c = 0; c < 8; c++)
                {
                    State state = StateOf(c, psg, sccRegs, sccVolume, sccEnable, psgClock);
                    if (!state.Audible)
                        continue;

                    Track t = tracks[c];
                    t.Audible += dt;
                    t.VolumeTime += state.Volume * dt;

                    if (state.Noise)
                    {
                        t.Noise += dt;
                        if (!state.Tone)
                            t.NoiseOnly += dt;
                    }
                }
            }

            if (e.Kind == VgmEventKind.Psg)
            {
                if (e.P0 < psg.Length)
                    psg[e.P0] = e.P1;
            }
            else if (e.P0 == 1 && e.P1 < sccRegs.Length)
            {
                sccRegs[e.P1] = e.P2;
            }
            else if (e.P0 == 2 && e.P1 < sccVolume.Length)
            {
                sccVolume[e.P1] = e.P2 & 0x0F;
            }
            else if (e.P0 == 3)
            {
                sccEnable = e.P2;
            }

            for (int c = 0; c < 8; c++)
                DetectNote(tracks[c], StateOf(c, psg, sccRegs, sccVolume, sccEnable, psgClock), now);
        }

        return Assign(tracks, vgm.TotalSeconds);
    }

    internal readonly record struct State(bool Audible, int Volume, double Frequency, bool Tone, bool Noise);

    internal static State StateOf(int channel, byte[] psg, byte[] sccRegs, int[] sccVolume, int sccEnable,
        double psgClock)
    {
        if (channel < 3)
        {
            int period = psg[channel * 2] | ((psg[channel * 2 + 1] & 0x0F) << 8);
            int raw = psg[8 + channel];
            int volume = (raw & 0x10) != 0 ? 12 : raw & 0x0F;
            bool tone = ((psg[7] >> channel) & 1) == 0;
            bool noise = ((psg[7] >> (3 + channel)) & 1) == 0;
            double frequency = period > 0 ? psgClock / (16.0 * period) : 0;

            return new State(volume > 0 && (tone || noise), volume, frequency, tone, noise);
        }

        int c = channel - 3;
        int sccPeriod = sccRegs[c * 2] | ((sccRegs[c * 2 + 1] & 0x0F) << 8);
        bool enabled = ((sccEnable >> c) & 1) != 0;
        int sccLevel = sccVolume[c];

        return new State(sccLevel > 0 && enabled, sccLevel, SccClock / (32.0 * (sccPeriod + 1)), true, false);
    }

    /// <summary>Counts a note when the pitch jumps 3 semitones or more (vibrato and slides stay under that).</summary>
    private static void DetectNote(Track track, State state, double now)
    {
        if (!state.Audible)
        {
            track.LastMidi = int.MinValue;      // the next sound is a new note
            return;
        }

        if (!state.Tone || state.Frequency <= 0)
            return;

        int midi = (int)Math.Round(69 + 12 * Math.Log2(state.Frequency / 440.0));

        if (track.LastMidi != int.MinValue && Math.Abs(midi - track.LastMidi) < 3)
            return;

        track.Notes++;
        if (track.LastNoteAt >= 0 && now - track.LastNoteAt < 0.09)
            track.FastNotes++;

        track.LastNoteAt = now;
        track.LastMidi = midi;
        track.Midis.Add(midi);
    }

    private static double Median(List<int> values)
    {
        if (values.Count == 0)
            return 0;

        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    private static AutoMixPlan Assign(Track[] tracks, double totalSeconds)
    {
        double minAudible = Math.Max(0.4, totalSeconds * 0.03);
        var channels = new ChannelMix[8];
        var roles = new ChannelRole[8];

        for (int c = 0; c < 8; c++)
        {
            Track t = tracks[c];

            if (t.Audible < minAudible)
                roles[c] = ChannelRole.Silent;
            else if (c < 3 && t.NoiseOnly + 0.5 * (t.Noise - t.NoiseOnly) >= 0.5 * t.Audible)
                roles[c] = ChannelRole.Percussion;
            else if (t.Notes >= 24 && t.FastNotes >= 0.45 * t.Notes)
                roles[c] = ChannelRole.Arpeggio;
            else
                roles[c] = ChannelRole.Harmony;                    // decided below
        }

        // Bass and lead come from the channels that are neither arpeggio nor drums.
        var candidates = Enumerable.Range(0, 8).Where(c => roles[c] == ChannelRole.Harmony).ToList();

        if (candidates.Count > 0)
        {
            int lowest = candidates.OrderBy(c => Median(tracks[c].Midis)).First();
            double lowestMidi = Median(tracks[lowest].Midis);
            double nextMidi = candidates.Where(c => c != lowest).Select(c => Median(tracks[c].Midis))
                .DefaultIfEmpty(double.MaxValue).Min();

            bool alone = candidates.Count == 1;
            bool isBass = alone ? lowestMidi <= 45 : lowestMidi <= 52 && lowestMidi <= nextMidi - 5;

            if (isBass)
            {
                roles[lowest] = ChannelRole.Bass;
                candidates.Remove(lowest);
            }
        }

        if (candidates.Count > 0)
        {
            // The tune: the voice that is loud, busy and not buried in the low notes.
            int lead = candidates
                .OrderByDescending(c => tracks[c].MeanVolume * Math.Min(tracks[c].Audible, totalSeconds) *
                                        (Median(tracks[c].Midis) >= 55 ? 1.0 : 0.6))
                .First();
            roles[lead] = ChannelRole.Lead;
        }

        int side = 1;                                           // alternates -, +, -, + for the voices that are spread

        for (int c = 0; c < 8; c++)
        {
            Track t = tracks[c];
            ChannelMix mix = new()
            {
                Channel = c,
                Role = roles[c],
                AudibleSeconds = t.Audible,
                MedianMidi = Median(t.Midis),
            };

            switch (roles[c])
            {
                case ChannelRole.Lead:
                    mix.GainDb = 1.5f;
                    mix.Pan = 0.10f;
                    mix.HighPassHz = 140f;
                    mix.HighShelfDb = 2.5f;
                    mix.Drive = 0.15f;
                    mix.ReverbSend = 0.16f;
                    mix.DelaySend = 0.20f;
                    break;

                case ChannelRole.Bass:
                    mix.GainDb = 1.0f;
                    mix.Pan = 0f;
                    mix.HighPassHz = 38f;
                    mix.LowShelfDb = 3.0f;
                    mix.Drive = 0.35f;
                    break;

                case ChannelRole.Arpeggio:
                    mix.GainDb = -3.0f;
                    mix.Pan = 0.62f * (side *= -1);
                    mix.HighPassHz = 260f;
                    mix.Drive = 0.05f;
                    mix.ReverbSend = 0.22f;
                    mix.DelaySend = 0.32f;
                    break;

                case ChannelRole.Percussion:
                    mix.GainDb = -1.5f;
                    mix.Pan = 0.18f * (side *= -1);
                    mix.HighPassHz = 200f;
                    mix.HighShelfDb = 1.0f;
                    mix.Drive = 0.20f;
                    mix.ReverbSend = 0.10f;
                    break;

                case ChannelRole.Harmony:
                    mix.GainDb = -2.5f;
                    mix.Pan = 0.45f * (side *= -1);
                    mix.HighPassHz = 120f;
                    mix.ReverbSend = 0.26f;
                    mix.DelaySend = 0.08f;
                    break;

                default:
                    mix.HighPassHz = 20f;
                    break;
            }

            channels[c] = mix;
        }

        return new AutoMixPlan { Channels = channels };
    }
}

/// <summary>
/// Applies an <see cref="AutoMixPlan"/> while rendering: per-channel low cut, tone, saturation, level
/// and pan; a stereo echo and a reverb fed by per-channel sends; then a gentle bus compressor and a
/// soft limiter. Works on blocks, so it serves both the live player and the offline renderer.
/// </summary>
public sealed class AutoMixProcessor
{
    private const float ReverbReturn = 1.6f;
    private const float DelayReturn = 0.9f;
    private const float DelayFeedback = 0.38f;
    private const float Makeup = 2.6f;
    private const float Ceiling = 0.95f;

    private readonly AutoMixPlan _plan;
    private readonly int _oversample;
    private readonly int _rate;

    private readonly Biquad?[] _highPass = new Biquad?[8];
    private readonly Biquad?[] _lowShelf = new Biquad?[8];
    private readonly Biquad?[] _highShelf = new Biquad?[8];
    private readonly float[] _gain = new float[8];
    private readonly float[] _panLeft = new float[8];
    private readonly float[] _panRight = new float[8];
    private readonly float[] _drive = new float[8];

    private readonly FirDecimator _mainDecimator;
    private readonly FirDecimator _delayDecimator;
    private readonly FirDecimator _reverbDecimator;
    private readonly StereoReverb _reverb;

    private float[] _mainL = Array.Empty<float>(), _mainR = Array.Empty<float>();
    private float[] _delayInL = Array.Empty<float>(), _delayInR = Array.Empty<float>();
    private float[] _reverbInL = Array.Empty<float>(), _reverbInR = Array.Empty<float>();
    private float[] _dryL = Array.Empty<float>(), _dryR = Array.Empty<float>();
    private float[] _dlyL = Array.Empty<float>(), _dlyR = Array.Empty<float>();
    private float[] _revL = Array.Empty<float>(), _revR = Array.Empty<float>();

    private readonly float[] _echoL;
    private readonly float[] _echoR;
    private int _echoIndexL, _echoIndexR;
    private float _echoLowL, _echoLowR;
    private readonly float _echoDamping;

    private float _envelope;
    private readonly float _attack;
    private readonly float _release;

    /// <summary>Creates the processor for one song.</summary>
    /// <param name="plan">The mix to apply.</param>
    /// <param name="oversample">Chip oversampling factor (the channel buffers are at rate × this).</param>
    /// <param name="rate">Output sample rate.</param>
    public AutoMixProcessor(AutoMixPlan plan, int oversample, int rate)
    {
        _plan = plan;
        _oversample = Math.Max(1, oversample);
        _rate = rate;
        double internalRate = (double)rate * _oversample;

        for (int c = 0; c < 8; c++)
        {
            ChannelMix mix = plan.Channels[c];
            if (mix.Role == ChannelRole.Silent)
                continue;

            _gain[c] = (float)Math.Pow(10, mix.GainDb / 20.0);
            double theta = (Math.Clamp(mix.Pan, -1f, 1f) + 1.0) * Math.PI / 4.0;
            _panLeft[c] = (float)Math.Cos(theta);
            _panRight[c] = (float)Math.Sin(theta);
            _drive[c] = mix.Drive;

            _highPass[c] = Biquad.CreateHighPass(internalRate, Math.Max(10, mix.HighPassHz), 0.707);

            if (Math.Abs(mix.LowShelfDb) > 0.1f)
                _lowShelf[c] = Biquad.CreateLowShelf(internalRate, 120, mix.LowShelfDb);

            if (Math.Abs(mix.HighShelfDb) > 0.1f)
                _highShelf[c] = Biquad.CreateHighShelf(internalRate, 3000, mix.HighShelfDb);
        }

        _mainDecimator = new FirDecimator(_oversample);
        _delayDecimator = new FirDecimator(_oversample);
        _reverbDecimator = new FirDecimator(_oversample);
        _reverb = new StereoReverb(rate);

        // A ping-pong echo: the two sides have different lengths, so the repeats walk across the field.
        _echoL = new float[Math.Max(8, (int)(rate * 0.19))];
        _echoR = new float[Math.Max(8, (int)(rate * 0.255))];
        _echoDamping = 1f - (float)Math.Exp(-2 * Math.PI * 3500 / rate);

        _attack = 1f - (float)Math.Exp(-1.0 / (0.005 * rate));
        _release = 1f - (float)Math.Exp(-1.0 / (0.140 * rate));
    }

    /// <summary>The plan this processor applies.</summary>
    public AutoMixPlan Plan => _plan;

    private void EnsureCapacity(int inCount, int frames)
    {
        if (_mainL.Length >= inCount && _dryL.Length >= frames)
            return;

        _mainL = new float[inCount]; _mainR = new float[inCount];
        _delayInL = new float[inCount]; _delayInR = new float[inCount];
        _reverbInL = new float[inCount]; _reverbInR = new float[inCount];
        _dryL = new float[frames]; _dryR = new float[frames];
        _dlyL = new float[frames]; _dlyR = new float[frames];
        _revL = new float[frames]; _revR = new float[frames];
    }

    /// <summary>Forgets every filter, echo and reverb tail (after a seek).</summary>
    public void Reset()
    {
        Array.Clear(_echoL);
        Array.Clear(_echoR);
        _echoLowL = _echoLowR = 0f;
        _envelope = 0f;
        _mainDecimator.Reset();
        _delayDecimator.Reset();
        _reverbDecimator.Reset();
    }

    /// <summary>
    /// Mixes one block. <paramref name="channels"/> are the eight chip channels at the internal rate;
    /// <paramref name="channelGain"/> carries mute/solo (0 silences a channel).
    /// </summary>
    public void Process(IReadOnlyList<float[]> channels, int inCount, int frames, float masterGain,
        float[] channelGain, float[] outL, float[] outR)
    {
        EnsureCapacity(inCount, frames);

        Array.Clear(_mainL, 0, inCount);
        Array.Clear(_mainR, 0, inCount);
        Array.Clear(_delayInL, 0, inCount);
        Array.Clear(_delayInR, 0, inCount);
        Array.Clear(_reverbInL, 0, inCount);
        Array.Clear(_reverbInR, 0, inCount);

        for (int c = 0; c < 8; c++)
        {
            ChannelMix mix = _plan.Channels[c];
            if (mix.Role == ChannelRole.Silent || channelGain[c] <= 0f)
                continue;

            float[] source = channels[c];
            float gain = _gain[c] * masterGain * channelGain[c];
            Biquad hp = _highPass[c]!;
            Biquad? low = _lowShelf[c];
            Biquad? high = _highShelf[c];
            float drive = _drive[c];
            double shaper = 1.0 + drive * 8.0;
            float pl = _panLeft[c], pr = _panRight[c];
            float delaySend = mix.DelaySend, reverbSend = mix.ReverbSend;

            for (int i = 0; i < inCount; i++)
            {
                double x = hp.Process(source[i] * gain);
                if (low is not null) x = low.Process(x);
                if (high is not null) x = high.Process(x);
                if (drive > 0f) x = Math.Tanh(x * shaper) / shaper;

                float v = (float)x;
                _mainL[i] += v * pl;
                _mainR[i] += v * pr;

                if (delaySend > 0f)
                {
                    _delayInL[i] += v * pl * delaySend;
                    _delayInR[i] += v * pr * delaySend;
                }

                if (reverbSend > 0f)
                {
                    _reverbInL[i] += v * pl * reverbSend;
                    _reverbInR[i] += v * pr * reverbSend;
                }
            }
        }

        _mainDecimator.Process(_mainL, _mainR, inCount, _dryL, _dryR, frames);
        _delayDecimator.Process(_delayInL, _delayInR, inCount, _dlyL, _dlyR, frames);
        _reverbDecimator.Process(_reverbInL, _reverbInR, inCount, _revL, _revR, frames);

        // Reverb returns wet only (mix = 1).
        _reverb.Process(_revL, _revR, frames, 1f);

        for (int n = 0; n < frames; n++)
        {
            // Ping-pong echo: each side feeds the other one.
            float echoOutL = _echoL[_echoIndexL];
            float echoOutR = _echoR[_echoIndexR];
            _echoLowL += (echoOutL - _echoLowL) * _echoDamping;
            _echoLowR += (echoOutR - _echoLowR) * _echoDamping;
            _echoL[_echoIndexL] = _dlyL[n] + DelayFeedback * _echoLowR;
            _echoR[_echoIndexR] = _dlyR[n] + DelayFeedback * _echoLowL;
            _echoIndexL = (_echoIndexL + 1) % _echoL.Length;
            _echoIndexR = (_echoIndexR + 1) % _echoR.Length;

            float l = _dryL[n] + echoOutL * DelayReturn + _revL[n] * ReverbReturn;
            float r = _dryR[n] + echoOutR * DelayReturn + _revR[n] * ReverbReturn;

            // Bus compressor (3:1 above ~ -9 dB, 5 ms attack, 140 ms release) and a soft limiter.
            float level = Math.Max(Math.Abs(l), Math.Abs(r));
            _envelope += (level - _envelope) * (level > _envelope ? _attack : _release);

            float gainReduction = 1f;
            const float threshold = 0.35f;
            if (_envelope > threshold)
                gainReduction = (float)Math.Pow(_envelope / threshold, 1.0 / 3.0 - 1.0);

            l *= gainReduction * Makeup;
            r *= gainReduction * Makeup;

            outL[n] = SoftLimit(l);
            outR[n] = SoftLimit(r);
        }
    }

    private static float SoftLimit(float x) => Ceiling * (float)Math.Tanh(x / Ceiling);
}
