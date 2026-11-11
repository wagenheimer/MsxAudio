using System;
using System.Collections.Generic;
using System.IO;
using Wagenheimer.MsxAudio.Chips;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Playback;

/// <summary>
/// Motor de áudio do MSX: toca as músicas <b>direto do VGM</b> (emulando PSG/SCC em tempo real)
/// e mistura efeitos como samples PCM, tudo num único stream estéreo.
/// </summary>
/// <remarks>
/// <para><b>Streaming, não arquivo:</b> a música não é pré-renderizada. O VGM é consumido
/// enquanto se toca, então o loop é <b>contínuo de verdade</b> (o chip nunca é reiniciado, como no
/// hardware) e cada faixa ocupa só os KB do VGM. O áudio já sai como PCM 16 bits/float pronto
/// para a placa — veja <c>Wagenheimer.MsxAudio.MonoGame</c> para a ponte com o MonoGame.</para>
///
/// <para><b>Player de sample:</b> <see cref="PlaySample"/> toca um efeito (um VGM curto
/// pré-renderizado ou um WAV do pacote) com volume/pan e polifonia limitada.</para>
/// </remarks>
public sealed class MsxAudioEngine : IDisposable
{
    private readonly MsxAudioLibrary _library;
    private readonly Dictionary<string, float[]> _sampleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SampleVoice> _voices = new();
    private ChipSongStream? _song;
    private float[] _mixBuffer = Array.Empty<float>();

    /// <summary>Cria o motor para uma biblioteca.</summary>
    /// <param name="library">Pacote <c>.msxaudio</c> carregado.</param>
    /// <param name="profile">Perfil de mixagem (padrão: original).</param>
    /// <param name="sampleRate">Taxa de saída em Hz.</param>
    public MsxAudioEngine(MsxAudioLibrary library, AudioProfile? profile = null, int sampleRate = 48000)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        Profile = profile ?? AudioProfile.Original;
        SampleRate = sampleRate;
    }

    /// <summary>Perfil de mixagem (muda o próximo PlaySong).</summary>
    public AudioProfile Profile { get; set; }

    /// <summary>Taxa de saída (Hz).</summary>
    public int SampleRate { get; }

    /// <summary>Volume da música (0..1).</summary>
    public float MusicVolume { get; set; } = 1f;

    /// <summary>Volume dos efeitos (0..1).</summary>
    public float SampleVolume { get; set; } = 1f;

    /// <summary>Volume geral (0..1).</summary>
    public float MasterVolume { get; set; } = 1f;

    /// <summary>Quantos efeitos podem tocar ao mesmo tempo (rouba o mais antigo).</summary>
    public int MaxSampleVoices { get; set; } = 8;

    /// <summary>Id da música tocando (ou <c>null</c>).</summary>
    public string? CurrentSongId => _song?.Id;

    /// <summary>Há música tocando?</summary>
    public bool IsPlaying => _song is { Finished: false };

    /// <summary>Toca uma música da biblioteca.</summary>
    /// <param name="id">Id da música.</param>
    /// <param name="loop">Respeita o loop do manifesto (true) ou toca uma vez?</param>
    public void PlaySong(string id, bool loop = true)
    {
        var song = _library.Find(id) ?? throw new ArgumentException($"música '{id}' não existe na biblioteca", nameof(id));
        var vgm = _library.LoadVgm(song);

        double start = song.Loop ? song.LoopStart : 0;
        double end = song.Loop ? song.LoopSeconds : 0;
        if (!vgm.HasLoop && end > 0) vgm.SetLoop(start, end);

        _song = new ChipSongStream(vgm, Profile, SampleRate, loop && song.Loop);
    }

    /// <summary>Para a música.</summary>
    public void StopSong() => _song = null;

    /// <summary>Toca um efeito pelo id (VGM curto ou WAV do pacote).</summary>
    /// <remarks>
    /// Como no chip de verdade, cada efeito tem <b>uma voz</b>: re-tocar o mesmo id reinicia o som
    /// em vez de empilhar (é o que faz uma queda longa soar igual ao original).
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

    /// <summary>Para um efeito específico (se estiver tocando).</summary>
    public void StopSample(string id)
    {
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_voices[i].Id, id, StringComparison.OrdinalIgnoreCase))
                _voices.RemoveAt(i);
        }
    }

    /// <summary>Para todos os efeitos.</summary>
    public void StopAllSamples() => _voices.Clear();

    /// <summary>
    /// Renderiza o próximo bloco de áudio <b>estéreo intercalado</b> em <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">Buffer com <c>frames × 2</c> amostras (float -1..1).</param>
    /// <param name="frames">Quadros a renderizar.</param>
    public void Render(float[] destination, int frames)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.Length < frames * 2)
            throw new ArgumentException("buffer menor que frames × 2", nameof(destination));

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

    /// <summary>Prepara (renderiza) os samples de todos os efeitos, para não travar no primeiro uso.</summary>
    public void PreloadSamples()
    {
        foreach (var clip in _library.Clips)
            _ = GetSample(clip);
    }

    /// <summary>Descarta tudo (streams, samples em cache e vozes).</summary>
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

        // limita a soma final (evita estouro quando música + efeitos coincidem)
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

        // 1) WAV dentro do pacote (sample "de verdade")
        if (clip.Entry.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            byte[]? bytes = _library.ReadEntry(clip.Entry);
            if (bytes == null) return null;
            var decoded = WavDecoder.Decode(bytes);
            var mono = decoded.ToMono();
            _sampleCache[clip.Id] = mono;
            return mono;
        }

        // 2) VGM (efeito do chip): renderiza e converte para mono
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

    /// <summary>Streaming de uma música: consome o VGM e emula os chips amostra a amostra.</summary>
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

            // mesmos filtros do render offline (senão o streaming soaria diferente)
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

            // aquece o estado dos chips até o início do loop (registradores, sem gerar áudio)
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
