using System;
using System.Collections.Generic;
using System.IO;
using Wagenheimer.MsxAudio.Chips;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Playback;

/// <summary>
/// MSX audio engine: plays songs <b>straight from the VGM</b> (emulating PSG/SCC in real time) and
/// mixes effects as PCM samples, all into a single stereo stream.
/// </summary>
/// <remarks>
/// <para><b>Streaming, not a file:</b> the music is not pre-rendered. The VGM is consumed while it
/// plays, so the loop is <b>genuinely continuous</b> (the chip is never restarted, as on hardware)
/// and each track takes only the KBs of its VGM. The output is 16-bit PCM ready for the sound card
/// — see <c>Wagenheimer.MsxAudio.MonoGame</c> for the MonoGame bridge.</para>
///
/// <para><b>Sample player:</b> <see cref="PlaySample"/> plays an effect (a short pre-rendered VGM or
/// a WAV from the package) with volume/pan and limited polyphony.</para>
/// </remarks>
public sealed class MsxAudioEngine : IDisposable
{
    private readonly MsxAudioLibrary _library;
    private readonly Dictionary<string, float[]> _sampleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SampleVoice> _voices = new();
    private ChipSongStream? _song;

    /// <summary>Creates the engine for a library.</summary>
    /// <param name="library">Loaded <c>.msxaudio</c> package.</param>
    /// <param name="profile">Mixing profile (default: original).</param>
    /// <param name="sampleRate">Output rate in Hz.</param>
    public MsxAudioEngine(MsxAudioLibrary library, AudioProfile? profile = null, int sampleRate = 48000)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        Profile = profile ?? AudioProfile.Original;
        SampleRate = sampleRate;
    }

    /// <summary>Mixing profile (affects the next <see cref="PlaySong"/>).</summary>
    public AudioProfile Profile { get; set; }

    /// <summary>Output rate (Hz).</summary>
    public int SampleRate { get; }

    /// <summary>Music volume (0..1).</summary>
    public float MusicVolume { get; set; } = 1f;

    /// <summary>Effect volume (0..1).</summary>
    public float SampleVolume { get; set; } = 1f;

    /// <summary>Master volume (0..1).</summary>
    public float MasterVolume { get; set; } = 1f;

    /// <summary>How many effects can play at once (steals the oldest).</summary>
    public int MaxSampleVoices { get; set; } = 8;

    /// <summary>Id of the playing song (or <c>null</c>).</summary>
    public string? CurrentSongId => _song?.Id;

    /// <summary>Is a song playing?</summary>
    public bool IsPlaying => _song is { Finished: false };

    /// <summary>Plays a song from the library.</summary>
    /// <param name="id">Song id.</param>
    /// <param name="loop">Honours the manifest loop (true) or plays once?</param>
    public void PlaySong(string id, bool loop = true)
    {
        var song = _library.Find(id) ?? throw new ArgumentException($"song '{id}' does not exist in the library", nameof(id));
        var vgm = _library.LoadVgm(song);

        double start = song.Loop ? song.LoopStart : 0;
        double end = song.Loop ? song.LoopSeconds : 0;
        if (!vgm.HasLoop && end > 0) vgm.SetLoop(start, end);

        _song = new ChipSongStream(vgm, Profile, SampleRate, loop && song.Loop);
    }

    /// <summary>Stops the music.</summary>
    public void StopSong() => _song = null;

    /// <summary>Plays an effect by id (short VGM or WAV from the package).</summary>
    /// <remarks>
    /// As on the real chip, each effect has <b>one voice</b>: retriggering the same id restarts the
    /// sound instead of stacking (which is what makes a long fall sound like the original).
    /// </remarks>
    public void PlaySample(string id, float volume = 1f, float pan = 0f)
    {
        var clip = _library.Find(id);
        if (clip == null) return;

        var data = GetSample(clip);
        if (data == null || data.Length == 0) return;

        StopSample(id);

        if (_voices.Count >= Math.Max(1, MaxSampleVoices))
            _voices.RemoveAt(0);

        (float left, float right) = Pan(pan);
        _voices.Add(new SampleVoice(id, data, volume, left, right));
    }

    /// <summary>Stops a specific effect (when playing).</summary>
    public void StopSample(string id)
    {
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_voices[i].Id, id, StringComparison.OrdinalIgnoreCase))
                _voices.RemoveAt(i);
        }
    }

    /// <summary>Stops all effects.</summary>
    public void StopAllSamples() => _voices.Clear();

    /// <summary>
    /// Renders the next block of <b>interleaved stereo</b> audio into <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">Buffer with <c>frames × 2</c> samples (float -1..1).</param>
    /// <param name="frames">Frames to render.</param>
    public void Render(float[] destination, int frames)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length < frames * 2)
            throw new ArgumentException("buffer smaller than frames × 2", nameof(destination));

        Array.Clear(destination, 0, frames * 2);
        if (frames == 0) return;

        float master = MasterVolume;

        if (_song is { Finished: false } song)
        {
            song.Render(destination, frames, MusicVolume * master);
            if (song.Finished) _song = null;
        }

        RenderSamples(destination, frames, master);
    }

    /// <summary>Renders (prepares) the samples of every effect, to avoid a hitch on first use.</summary>
    public void PreloadSamples()
    {
        foreach (var clip in _library.Clips)
            _ = GetSample(clip);
    }

    /// <summary>Discards everything (streams, cached samples and voices).</summary>
    public void Dispose()
    {
        _song = null;
        _voices.Clear();
        _sampleCache.Clear();
    }

    private void RenderSamples(float[] destination, int frames, float master)
    {
        float gain = SampleVolume * master;

        for (int v = _voices.Count - 1; v >= 0; v--)
        {
            SampleVoice voice = _voices[v];
            float[] data = voice.Data;
            int position = voice.Position;
            int count = Math.Min(frames, data.Length - position);
            float left = voice.Volume * gain * voice.PanLeft;
            float right = voice.Volume * gain * voice.PanRight;

            for (int i = 0; i < count; i++)
            {
                float value = data[position + i];
                destination[i * 2] += value * left;
                destination[i * 2 + 1] += value * right;
            }

            voice.Position = position + count;
            if (voice.Position >= data.Length) _voices.RemoveAt(v);
        }

        // limit the final sum (avoids clipping when music and effects coincide)
        for (int i = 0; i < frames * 2; i++)
        {
            float value = destination[i];
            if (value > 1f) destination[i] = 1f;
            else if (value < -1f) destination[i] = -1f;
        }
    }

    private float[]? GetSample(MsxAudioSong clip)
    {
        if (_sampleCache.TryGetValue(clip.Id, out var cached)) return cached;

        // 1) WAV inside the package (a "real" sample)
        if (clip.Entry.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            byte[]? bytes = _library.ReadEntry(clip.Entry);
            if (bytes == null) return null;
            var decoded = WavDecoder.Decode(bytes);
            var mono = decoded.ToMono();
            _sampleCache[clip.Id] = mono;
            return mono;
        }

        // 2) VGM (chip effect): render and convert to mono
        var vgm = _library.LoadVgm(clip);
        double end = MsxSoundRenderer.FindAudibleEnd(vgm);
        var sampleProfile = Profile.WithOversample(Math.Min(Profile.Oversample, 2));
        var audio = MsxSoundRenderer.Render(vgm, sampleProfile, SampleRate, 0, end);
        var samples = new float[audio.Frames];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (audio.Samples[i * 2] + audio.Samples[i * 2 + 1]) / 65536f;

        _sampleCache[clip.Id] = samples;
        return samples;
    }

    private static (float Left, float Right) Pan(float pan)
    {
        double theta = (Math.Clamp(pan, -1f, 1f) + 1.0) * Math.PI / 4.0;
        return ((float)Math.Cos(theta), (float)Math.Sin(theta));
    }

    private sealed class SampleVoice
    {
        public SampleVoice(string id, float[] data, float volume, float panLeft, float panRight)
        {
            Id = id;
            Data = data;
            Volume = volume;
            PanLeft = panLeft;
            PanRight = panRight;
        }

        public string Id { get; }
        public float[] Data { get; }
        public int Position { get; set; }
        public float Volume { get; }
        public float PanLeft { get; }
        public float PanRight { get; }
    }

    /// <summary>Streaming of one song: consumes the VGM and emulates the chips sample by sample.</summary>
    private sealed class ChipSongStream
    {
        private const int ChunkFrames = 2048;

        private readonly VgmFile _vgm;
        private readonly Ay8910 _psg;
        private readonly KonamiScc _scc;
        private readonly int _oversample;
        private readonly double _samplesPerTick;
        private readonly float _psgGain;
        private readonly float _sccGain;
        private readonly float _masterGain;
        private readonly (float L, float R)[] _psgPan;
        private readonly (float L, float R)[] _sccPan;
        private readonly FirDecimator _decimator;
        private readonly Biquad? _highPassL;
        private readonly Biquad? _highPassR;
        private readonly Biquad? _lowPassL;
        private readonly Biquad? _lowPassR;
        private readonly StereoReverb? _reverb;
        private readonly float _reverbMix;
        private readonly bool _loop;
        private readonly double _loopStart;
        private readonly double _loopLength;
        private readonly int _startIndex;
        private readonly int _endIndex;
        private readonly double _endOfSong;
        private readonly float[][] _psgBuffer;
        private readonly float[][] _sccBuffer;
        private readonly float[] _mixL;
        private readonly float[] _mixR;
        private readonly float[] _outL;
        private readonly float[] _outR;

        private double _position;
        private int _index;

        public ChipSongStream(VgmFile vgm, AudioProfile profile, int sampleRate, bool loop)
        {
            _vgm = vgm;
            Id = vgm.Name;
            _oversample = Math.Clamp(profile.Oversample, 1, 16);
            double internalRate = (double)sampleRate * _oversample;
            _psg = new Ay8910(vgm.AyClock, internalRate);
            _scc = new KonamiScc(MsxSoundRenderer.SccClock, internalRate);
            _samplesPerTick = 44100.0 / internalRate;
            _psgGain = profile.PsgGain;
            _sccGain = profile.SccGain;
            _masterGain = profile.MasterGain;
            _psgPan = BuildPan(profile.PsgPan, 3);
            _sccPan = BuildPan(profile.SccPan, 5);
            _decimator = new FirDecimator(_oversample);

            // same filters as the offline renderer (otherwise streaming would sound different)
            if (profile.HighPassHz > 0)
            {
                _highPassL = Biquad.CreateHighPass(sampleRate, profile.HighPassHz, 0.707);
                _highPassR = Biquad.CreateHighPass(sampleRate, profile.HighPassHz, 0.707);
            }

            if (profile.LowPassHz > 0)
            {
                _lowPassL = Biquad.CreateLowPass(sampleRate, profile.LowPassHz, 0.707);
                _lowPassR = Biquad.CreateLowPass(sampleRate, profile.LowPassHz, 0.707);
            }

            if (profile.ReverbMix > 0)
            {
                _reverb = new StereoReverb(sampleRate);
                _reverbMix = Math.Min(1f, profile.ReverbMix);
            }

            _loop = loop && vgm.LoopDuration > 0;
            _loopStart = vgm.LoopSample;
            _loopLength = vgm.LoopDuration;

            var events = vgm.Events;
            _startIndex = _loop ? FindIndex(events, (long)_loopStart) : 0;
            _endIndex = _loop ? FindIndex(events, (long)(_loopStart + _loopLength)) : events.Count;
            _position = _loop ? _loopStart : 0;
            _endOfSong = MsxSoundRenderer.FindAudibleEnd(vgm, 1.5) * 44100.0;

            // warm the chip state up to the loop start (registers only, no audio)
            _index = 0;
            while (_index < _startIndex)
            {
                Apply(events[_index]);
                _index++;
            }

            int bufferSize = ChunkFrames * _oversample;
            _psgBuffer = new[] { new float[bufferSize], new float[bufferSize], new float[bufferSize] };
            _sccBuffer = new[]
            {
                new float[bufferSize], new float[bufferSize], new float[bufferSize], new float[bufferSize],
                new float[bufferSize],
            };
            _mixL = new float[bufferSize];
            _mixR = new float[bufferSize];
            _outL = new float[ChunkFrames];
            _outR = new float[ChunkFrames];
        }

        public string Id { get; }

        public bool Finished { get; private set; }

        public void Render(float[] destination, int frames, float gain)
        {
            int written = 0;
            var events = _vgm.Events;

            while (written < frames && !Finished)
            {
                int chunkFrames = Math.Min(ChunkFrames, frames - written);
                int inCount = chunkFrames * _oversample;

                int produced = 0;
                for (int i = 0; i < inCount; i++)
                {
                    while (_index < _endIndex && events[_index].Sample <= _position)
                    {
                        Apply(events[_index]);
                        _index++;
                    }

                    _psg.Render(_psgBuffer[0], _psgBuffer[1], _psgBuffer[2], i, 1);
                    _scc.Render(_sccBuffer, i, 1);

                    _position += _samplesPerTick;
                    produced++;

                    if (_loop)
                    {
                        if (_position >= _loopStart + _loopLength)
                        {
                            _position -= _loopLength;
                            _index = _startIndex;
                        }
                    }
                    else if (_position > _endOfSong && _index >= events.Count)
                    {
                        Finished = true;
                        break;
                    }
                }

                if (produced == 0) break;

                for (int i = 0; i < produced; i++)
                {
                    float l = 0f;
                    float r = 0f;
                    for (int ch = 0; ch < 3; ch++)
                    {
                        float v = _psgBuffer[ch][i] * _psgGain;
                        l += v * _psgPan[ch].L;
                        r += v * _psgPan[ch].R;
                    }

                    for (int ch = 0; ch < 5; ch++)
                    {
                        float v = _sccBuffer[ch][i] * _sccGain;
                        l += v * _sccPan[ch].L;
                        r += v * _sccPan[ch].R;
                    }

                    _mixL[i] = l * _masterGain;
                    _mixR[i] = r * _masterGain;
                }

                int outFrames = produced / _oversample;
                if (outFrames > 0)
                {
                    _decimator.Process(_mixL, _mixR, outFrames * _oversample, _outL, _outR, outFrames);

                    if (_highPassL != null)
                    {
                        for (int i = 0; i < outFrames; i++)
                        {
                            _outL[i] = (float)_highPassL.Process(_outL[i]);
                            _outR[i] = (float)_highPassR!.Process(_outR[i]);
                        }
                    }

                    if (_lowPassL != null)
                    {
                        for (int i = 0; i < outFrames; i++)
                        {
                            _outL[i] = (float)_lowPassL.Process(_outL[i]);
                            _outR[i] = (float)_lowPassR!.Process(_outR[i]);
                        }
                    }

                    if (_reverb != null)
                        _reverb.Process(_outL, _outR, outFrames, _reverbMix);

                    for (int i = 0; i < outFrames; i++)
                    {
                        destination[(written + i) * 2] += _outL[i] * gain;
                        destination[(written + i) * 2 + 1] += _outR[i] * gain;
                    }

                    written += outFrames;
                }

                if (produced < inCount) break;
            }
        }

        private void Apply(VgmEvent e)
        {
            if (e.Kind == VgmEventKind.Psg) _psg.Write(e.P0, e.P1);
            else _scc.Write(e.P0, e.P1, e.P2);
        }

        private static int FindIndex(IReadOnlyList<VgmEvent> events, long sample)
        {
            for (int i = 0; i < events.Count; i++)
                if (events[i].Sample >= sample) return i;
            return events.Count;
        }

        private static (float L, float R)[] BuildPan(float[] pan, int channels)
        {
            var result = new (float L, float R)[channels];
            for (int i = 0; i < channels; i++)
            {
                float p = i < pan.Length ? Math.Clamp(pan[i], -1f, 1f) : 0f;
                double theta = (p + 1.0) * Math.PI / 4.0;
                result[i] = ((float)Math.Cos(theta), (float)Math.Sin(theta));
            }

            return result;
        }
    }
}
