using System;
using System.Collections.Generic;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Playback;
using Wagenheimer.MsxAudio.Songs;

namespace Wagenheimer.MsxAudio.MonoGame;

/// <summary>
/// A ready-to-use MSX audio host for MonoGame: loads a <c>.msxaudio</c> package and keeps the stream
/// fed, so a game only has to say "play this song / this effect".
/// </summary>
/// <remarks>
/// <para>This is the plumbing every game would otherwise repeat: library + engine + player, per-frame
/// <see cref="Update"/>, music and effect volumes, profile switching (rebuilding the engine while
/// keeping the current song) and looking sounds up by the id the game's own code carries.</para>
///
/// <code>
/// _audio = new MsxAudioHost("Content/Audio/game.msxaudio", AudioProfile.Stereo);
/// _audio.PlayMusic("stage_a", loop: true);
/// _audio.PlayEffectByOriginalId(0x23);
/// // in Update: _audio.Update();
/// </code>
/// </remarks>
public sealed class MsxAudioHost : IDisposable
{
    private readonly MsxAudioLibrary _library;
    private readonly Dictionary<int, MsxAudioSong> _byOriginalId = new();

    private MsxAudioEngine _engine;
    private MsxAudioPlayer _player;
    private bool _musicEnabled = true;
    private bool _soundEnabled = true;

    /// <summary>Opens a package from disk.</summary>
    /// <param name="packagePath">The <c>.msxaudio</c> file.</param>
    /// <param name="profile">Mixing profile (defaults to <see cref="AudioProfile.Original"/>).</param>
    /// <param name="startStream">Start feeding the audio device right away (disable for silent runs).</param>
    public MsxAudioHost(string packagePath, AudioProfile? profile = null, bool startStream = true)
        : this(MsxAudioLibrary.Load(packagePath), profile, startStream)
    {
    }

    /// <summary>Uses an already loaded package (the host owns it and disposes it).</summary>
    public MsxAudioHost(MsxAudioLibrary library, AudioProfile? profile = null, bool startStream = true)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        Profile = profile ?? AudioProfile.Original;

        foreach (MsxAudioSong song in _library.Songs)
        {
            if (song.OriginalId is int id) _byOriginalId[id] = song;
        }

        foreach (MsxAudioSong clip in _library.Clips)
        {
            if (clip.OriginalId is int id) _byOriginalId[id] = clip;
        }

        _engine = new MsxAudioEngine(_library, Profile);
        _engine.PreloadSamples();
        _player = new MsxAudioPlayer(_engine);

        if (startStream)
            _player.Start();
    }

    /// <summary>The loaded package (songs, effects, manifest).</summary>
    public MsxAudioLibrary Library => _library;

    /// <summary>Active mixing profile.</summary>
    public AudioProfile Profile { get; private set; }

    /// <summary>Music on/off (menu switch).</summary>
    public bool MusicEnabled
    {
        get => _musicEnabled;
        set
        {
            _musicEnabled = value;
            _engine.MusicVolume = value ? 1f : 0f;
        }
    }

    /// <summary>Effects on/off (menu switch).</summary>
    public bool SoundEnabled
    {
        get => _soundEnabled;
        set
        {
            _soundEnabled = value;
            _engine.SampleVolume = value ? 1f : 0f;
        }
    }

    /// <summary>Master volume (0..1).</summary>
    public float MasterVolume
    {
        get => _engine.MasterVolume;
        set => _engine.MasterVolume = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>Id of the song being played, or <c>null</c>.</summary>
    public string? CurrentSongId => _engine.CurrentSongId;

    /// <summary>Is a song playing?</summary>
    public bool IsMusicPlaying => _engine.IsPlaying;

    /// <summary>Finds a song or effect by the id used by the original game.</summary>
    public MsxAudioSong? FindByOriginalId(int originalId) =>
        _byOriginalId.TryGetValue(originalId, out MsxAudioSong? song) ? song : null;

    /// <summary>Keeps the audio queue full. Call once per frame.</summary>
    public void Update() => _player.Update();

    // â”€â”€ Music â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>Plays a song of the package (silently ignored when the id does not exist).</summary>
    /// <param name="id">Package id (e.g. <c>stage_a</c>).</param>
    /// <param name="loop">Loop the song.</param>
    /// <param name="restart">Restart even when it is already the current song.</param>
    public void PlayMusic(string id, bool loop = true, bool restart = false)
    {
        if (_engine.CurrentSongId == id && _engine.IsPlaying && !restart)
            return;

        if (restart)
            _player.StopSong();

        _player.PlaySong(id, loop);
    }

    /// <summary>Plays the song whose id in the original game is <paramref name="originalId"/>.</summary>
    /// <returns>False when the package has no such song.</returns>
    public bool PlayMusicByOriginalId(int originalId, bool loop = true, bool restart = false)
    {
        MsxAudioSong? song = FindByOriginalId(originalId);
        if (song is null || !song.Music)
            return false;

        PlayMusic(song.Id, loop, restart);
        return true;
    }

    /// <summary>Cuts the music.</summary>
    public void StopMusic() => _player.StopSong();

    // â”€â”€ Effects â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>Plays an effect of the package (silently ignored when the id does not exist).</summary>
    public void PlayEffect(string id) => _player.PlaySample(id);

    /// <summary>Plays the effect whose id in the original game is <paramref name="originalId"/>.</summary>
    /// <returns>False when the package has no such effect.</returns>
    public bool PlayEffectByOriginalId(int originalId)
    {
        MsxAudioSong? clip = FindByOriginalId(originalId);
        if (clip is null || clip.Music)
            return false;

        PlayEffect(clip.Id);
        return true;
    }

    /// <summary>Stops one effect (the original PSG has a single channel per effect).</summary>
    public void StopEffect(string id) => _player.StopSample(id);

    /// <summary>Stops every effect.</summary>
    public void StopAllEffects() => _player.StopAllSamples();

    // â”€â”€ Profile â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Switches the mixing profile, rebuilding the engine and resuming the same song.
    /// </summary>
    public void SetProfile(AudioProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.Equals(profile.Name, Profile.Name, StringComparison.OrdinalIgnoreCase))
            return;

        Profile = profile;

        string? current = _engine.CurrentSongId;
        bool wasPlaying = _engine.IsPlaying;
        bool music = _musicEnabled;
        bool sound = _soundEnabled;
        float master = MasterVolume;

        _player.Dispose();
        _engine.Dispose();

        _engine = new MsxAudioEngine(_library, Profile);
        _engine.PreloadSamples();
        _player = new MsxAudioPlayer(_engine);
        _player.Start();

        MusicEnabled = music;
        SoundEnabled = sound;
        MasterVolume = master;

        if (current != null && wasPlaying)
            _player.PlaySong(current);
    }

    /// <summary>Switches the profile by name (see <see cref="AudioProfile.ByName"/>).</summary>
    public void SetProfile(string name) => SetProfile(AudioProfile.ByName(name));

    /// <summary>Moves to the next (or previous) profile in <see cref="AudioProfile.All"/>.</summary>
    public AudioProfile NextProfile(int direction = 1)
    {
        SetProfile(AudioProfile.Next(Profile.Name, direction));
        return Profile;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _player.Dispose();
        _engine.Dispose();
        _library.Dispose();
    }
}
