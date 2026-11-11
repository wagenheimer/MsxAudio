using System;
using Microsoft.Xna.Framework.Audio;
using Wagenheimer.MsxAudio.Playback;

namespace Wagenheimer.MsxAudio.MonoGame;

/// <summary>
/// Ponte do <see cref="MsxAudioEngine"/> com o MonoGame: toca música e efeitos do MSX por um
/// único <see cref="DynamicSoundEffectInstance"/> (streaming, sem arquivo intermediário).
/// </summary>
/// <remarks>
/// <para>Chame <see cref="Update"/> uma vez por frame — ele mantém a fila de buffers cheia.
/// Em vez de usar o evento <c>BufferNeeded</c> (que pode disparar em outra thread), o envio é
/// feito no thread do jogo, o que evita disputa com <see cref="PlaySong"/>.</para>
///
/// <code>
/// var library = MsxAudioLibrary.Load("Content/Audio/kingsvalley.msxaudio");
/// var engine  = new MsxAudioEngine(library, AudioProfile.Stereo);
/// var player  = new MsxAudioPlayer(engine);
/// player.Start();
/// player.PlaySong("stage_a");
/// // no Update: player.Update();
/// </code>
/// </remarks>
public sealed class MsxAudioPlayer : IDisposable
{
    private readonly MsxAudioEngine _engine;
    private readonly object _gate = new();
    private readonly int _chunkFrames;
    private readonly float[] _floatBuffer;
    private readonly byte[] _byteBuffer;
    private DynamicSoundEffectInstance? _instance;

    /// <summary>Cria o player.</summary>
    /// <param name="engine">Motor já com a biblioteca carregada.</param>
    /// <param name="chunkFrames">Quadros por buffer (1024 ≈ 21 ms em 48 kHz).</param>
    public MsxAudioPlayer(MsxAudioEngine engine, int chunkFrames = 1024)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _chunkFrames = Math.Max(256, chunkFrames);
        _floatBuffer = new float[_chunkFrames * 2];
        _byteBuffer = new byte[_chunkFrames * 2 * 2];
    }

    /// <summary>O stream está tocando?</summary>
    public bool IsRunning => _instance is { State: SoundState.Playing };

    /// <summary>Buffer de segurança (quantos buffers ficam na fila).</summary>
    public int QueuedBuffers { get; set; } = 3;

    /// <summary>Música tocando (id) ou <c>null</c>.</summary>
    public string? CurrentSongId => _engine.CurrentSongId;

    /// <summary>Inicia o stream.</summary>
    public void Start()
    {
        if (_instance != null) return;

        _instance = new DynamicSoundEffectInstance(_engine.SampleRate, AudioChannels.Stereo);
        _instance.Play();
        Fill();
    }

    /// <summary>Para o stream e libera a instância.</summary>
    public void Stop()
    {
        if (_instance == null) return;
        try { _instance.Stop(); } catch { /* ignore */ }
        _instance.Dispose();
        _instance = null;
    }

    /// <summary>Toca uma música da biblioteca.</summary>
    public void PlaySong(string id, bool loop = true)
    {
        lock (_gate) _engine.PlaySong(id, loop);
    }

    /// <summary>Corta a música.</summary>
    public void StopSong()
    {
        lock (_gate) _engine.StopSong();
    }

    /// <summary>Toca um efeito (sample) pelo id.</summary>
    public void PlaySample(string id, float volume = 1f, float pan = 0f)
    {
        lock (_gate) _engine.PlaySample(id, volume, pan);
    }

    /// <summary>Mantém a fila de buffers abastecida. Chame uma vez por frame.</summary>
    public void Update()
    {
        if (_instance == null) return;
        Fill();
    }

    /// <summary>Volume da música (0..1).</summary>
    public float MusicVolume
    {
        get => _engine.MusicVolume;
        set => _engine.MusicVolume = value;
    }

    /// <summary>Volume dos efeitos (0..1).</summary>
    public float SampleVolume
    {
        get => _engine.SampleVolume;
        set => _engine.SampleVolume = value;
    }

    /// <summary>Volume geral (0..1).</summary>
    public float MasterVolume
    {
        get => _engine.MasterVolume;
        set => _engine.MasterVolume = value;
    }

    private void Fill()
    {
        if (_instance == null) return;

        int pending = _instance.PendingBufferCount;
        int guard = 0;
        while (pending < QueuedBuffers && guard++ < 8)
        {
            lock (_gate)
            {
                _engine.Render(_floatBuffer, _chunkFrames);
            }

            for (int i = 0; i < _floatBuffer.Length; i++)
            {
                short value = (short)Math.Clamp((int)Math.Round(_floatBuffer[i] * 32767f), short.MinValue, short.MaxValue);
                _byteBuffer[i * 2] = (byte)(value & 0xFF);
                _byteBuffer[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }

            try
            {
                _instance.SubmitBuffer(_byteBuffer);
            }
            catch (InvalidOperationException)
            {
                break; // stream parado/descartado
            }

            pending = _instance.PendingBufferCount;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
    }
}
