using System;
using Microsoft.Xna.Framework.Audio;
using Wagenheimer.MsxAudio.Playback;

namespace Wagenheimer.MsxAudio.MonoGame;

/// <summary>
/// Bridge between <see cref="MsxAudioEngine"/> and MonoGame: plays MSX music and effects through a
/// single <see cref="DynamicSoundEffectInstance"/> (streaming, no intermediate file).
/// </summary>
/// <remarks>
/// <para>Call <see cref="Update"/> once per frame — it keeps the buffer queue full. Instead of the
/// <c>BufferNeeded</c> event (which may fire on another thread), buffers are submitted on the game
/// thread, which avoids contention with <see cref="PlaySong"/>.</para>
///
/// <code>
/// var library = MsxAudioLibrary.Load("Content/Audio/kingsvalley.msxaudio");
/// var engine  = new MsxAudioEngine(library, AudioProfile.Stereo);
/// var player  = new MsxAudioPlayer(engine);
/// player.Start();
/// player.PlaySong("stage_a");
/// // in Update: player.Update();
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

    /// <summary>Creates the player.</summary>
    /// <param name="engine">Engine with the library already loaded.</param>
    /// <param name="chunkFrames">Frames per buffer (1024 ≈ 21 ms at 48 kHz).</param>
    public MsxAudioPlayer(MsxAudioEngine engine, int chunkFrames = 1024)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _chunkFrames = Math.Max(256, chunkFrames);
        _floatBuffer = new float[_chunkFrames * 2];
        _byteBuffer = new byte[_chunkFrames * 2 * 2];
    }

    /// <summary>Is the stream playing?</summary>
    public bool IsRunning => _instance is { State: SoundState.Playing };

    /// <summary>Safety margin: how many buffers stay queued.</summary>
    public int QueuedBuffers { get; set; } = 3;

    /// <summary>Id of the playing song, or <c>null</c>.</summary>
    public string? CurrentSongId => _engine.CurrentSongId;

    /// <summary>Starts the stream.</summary>
    public void Start()
    {
        if (_instance != null) return;

        _instance = new DynamicSoundEffectInstance(_engine.SampleRate, AudioChannels.Stereo);
        _instance.Play();
        Fill();
    }

    /// <summary>Stops the stream and releases the instance.</summary>
    public void Stop()
    {
        if (_instance == null) return;
        try { _instance.Stop(); } catch { /* already stopped */ }
        _instance.Dispose();
        _instance = null;
    }

    /// <summary>Plays a song from the library.</summary>
    public void PlaySong(string id, bool loop = true)
    {
        lock (_gate) _engine.PlaySong(id, loop);
    }

    /// <summary>Cuts the music.</summary>
    public void StopSong()
    {
        lock (_gate) _engine.StopSong();
    }

    /// <summary>Plays an effect (sample) by id.</summary>
    public void PlaySample(string id, float volume = 1f, float pan = 0f)
    {
        lock (_gate) _engine.PlaySample(id, volume, pan);
    }

    /// <summary>Keeps the buffer queue full. Call once per frame.</summary>
    public void Update()
    {
        if (_instance == null) return;
        Fill();
    }

    /// <summary>Music volume (0..1).</summary>
    public float MusicVolume
    {
        get => _engine.MusicVolume;
        set => _engine.MusicVolume = value;
    }

    /// <summary>Effect volume (0..1).</summary>
    public float SampleVolume
    {
        get => _engine.SampleVolume;
        set => _engine.SampleVolume = value;
    }

    /// <summary>Master volume (0..1).</summary>
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
                break; // stream stopped/disposed
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
