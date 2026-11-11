using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Visual player for MSX VGMs: plays the PSG/SCC in real time and shows the chips, the channels,
/// the master waveform, the spectrum and the SCC wave RAM.
/// </summary>
/// <remarks>
/// The UI is laid out on a fixed 1280x720 canvas and scaled to the window, so maximising it keeps
/// everything aligned. It can also render a PNG sequence (and call ffmpeg to build a GIF/MP4).
/// </remarks>
public sealed class VisualizerGame : Game
{
    private const int VW = 1280;
    private const int VH = 720;
    private const int ChunkFrames = 2048;
    private const int FftSize = 1024;
    private const int SpectrumBars = 96;

    private static readonly Color Background = new(9, 12, 18);
    private static readonly Color PanelColor = new(16, 22, 32);
    private static readonly Color PanelHi = new(22, 30, 42);
    private static readonly Color Grid = new(32, 42, 58);
    private static readonly Color TextColor = new(222, 232, 242);
    private static readonly Color Dim = new(116, 132, 150);
    private static readonly Color Accent = new(255, 196, 64);
    private static readonly Color PsgColor = new(79, 195, 247);
    private static readonly Color SccColor = new(126, 240, 130);
    private static readonly Color Warn = new(255, 96, 96);

    private readonly GraphicsDeviceManager _graphics;
    private readonly string? _startFile;
    private readonly string _startDir;
    private readonly string? _recordPath;
    private readonly int _recordSeconds;
    private readonly int _recordFps;

    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private RenderTarget2D _target = null!;

    private readonly List<string> _tracks = new();
    private int _selected;

    private VgmPlayer? _player;
    private DynamicSoundEffectInstance? _sound;
    private readonly float[] _render = new float[ChunkFrames * 2];
    private readonly byte[] _pcm = new byte[ChunkFrames * 2 * 2];

    private readonly float[] _fftSamples = new float[FftSize];
    private readonly float[] _fftMagnitude = new float[FftSize / 2];
    private readonly float[] _fftWindow = Fft.HannWindow(FftSize);
    private readonly float[] _bars = new float[SpectrumBars];
    private readonly float[] _barPeak = new float[SpectrumBars];
    private readonly float[] _peakHold = new float[8];
    private readonly float[] _wave = new float[32];

    private KeyboardState _keys;
    private KeyboardState _prevKeys;
    private MouseState _mouse;
    private MouseState _prevMouse;
    private bool _paused;
    private bool _autoNext = true;
    private bool _showHelp;
    private readonly AudioProfile _profile;

    // Piano roll: one event list per channel (only on key / pitch changes).
    private sealed class NoteEvent
    {
        public double Time;
        public double Frequency;
        public float Volume;
        public bool Key;
    }

    private readonly List<NoteEvent>[] _notes =
        Enumerable.Range(0, 8).Select(_ => new List<NoteEvent>()).ToArray();

    private readonly int[] _lastMidi = Enumerable.Repeat(-1, 8).ToArray();

    private enum PanelView { Spectrum, Roll, Piano }

    private PanelView _view = PanelView.Spectrum;
    private const double RollSeconds = 12.0;
    private const double NoteTailSeconds = 0.09;

    // One distinct, saturated colour per channel. PSG uses warm hues, SCC uses cool ones, so the
    // chip is recognisable at a glance while every channel is still told apart (roll, piano, strips).
    private static readonly Color[] ChannelColors =
    {
        new(255, 107, 107),   // PSG1 red
        new(255, 169, 77),    // PSG2 orange
        new(255, 212, 59),    // PSG3 yellow
        new(105, 219, 124),   // SCC1 green
        new(56, 217, 169),    // SCC2 mint
        new(77, 171, 247),    // SCC3 blue
        new(151, 117, 250),   // SCC4 violet
        new(247, 131, 172),   // SCC5 pink
    };
    private string _status = string.Empty;
    private float _statusTimer;

    // Scaling of the 1280x720 canvas to the window.
    private float _scale = 1f;
    private float _offX, _offY;

    // Seek bar (design coordinates, set in DrawSeekBar).
    private Rectangle _seekBar;

    // Channel strip areas (design coordinates, set in DrawChannels).
    private readonly Rectangle[] _stripHit = new Rectangle[8];
    private readonly Rectangle[] _soloHit = new Rectangle[8];
    private readonly Rectangle[] _muteHit = new Rectangle[8];

    // Track rows (design coordinates, filled by DrawPlaylist).
    private readonly List<(Rectangle Rect, int Index)> _trackHit = new();

    // Piano panel (design coordinates, filled by DrawPiano).
    private readonly Rectangle[] _pianoChannelHit = new Rectangle[8];
    private Rectangle _pianoAllHit;
    private Rectangle _pianoOctDownHit, _pianoOctUpHit;
    private int _pianoChannel;
    private int _pianoBaseMidi = 48;      // C3 at the left edge; 3 octaves visible
    private float _auditionTimer;         // time left to feed the audition while paused
    private int _heldKeyMidi = -1;        // piano key held with the mouse (note-on until release)
    private bool _demoRunning;
    private int _demoChannel = -1;
    private float _demoTimer;
    private readonly Rectangle[] _pianoKeyRect = new Rectangle[128];
    private readonly int[] _keyLit = new int[128];

    // Recording.
    private bool _recording;
    private string _recordDir = string.Empty;
    private int _recordFrame;
    private int _recordTotal;
    private GifEncoder? _gif;
    private WavWriter? _wav;
    private string? _wavPath;
    private int _gifWidth, _gifHeight, _gifFactor = 1;

    public VisualizerGame(string? file, string? recordPath = null, int recordSeconds = 10, int recordFps = 30,
        int windowWidth = VW, int windowHeight = VH, AudioProfile? profile = null, string? startView = null)
    {
        _profile = profile ?? AudioProfile.Stereo;

        // A screenshot gate must not depend on the audio device pacing the buffers: one update per
        // drawn frame, each advancing a fixed 1/60 s (see AdvanceForScreenshot).
        if (_shotPending)
            IsFixedTimeStep = false;

        _view = startView?.ToLowerInvariant() switch
        {
            "roll" or "pianoroll" => PanelView.Roll,
            "piano" or "keys" => PanelView.Piano,
            _ => PanelView.Spectrum,
        };
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Math.Max(640, windowWidth),
            PreferredBackBufferHeight = Math.Max(360, windowHeight),
            SynchronizeWithVerticalRetrace = true,
        };

        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "MSX Audio Visualizer — PSG + SCC";

        _startFile = file;
        _startDir = file is not null
            ? Path.GetDirectoryName(Path.GetFullPath(file)) ?? "."
            : Directory.GetCurrentDirectory();

        _recordPath = recordPath;
        _recordSeconds = Math.Max(1, recordSeconds);
        _recordFps = Math.Clamp(recordFps, 5, 60);
    }

    protected override void Initialize()
    {
        base.Initialize();

        Window.FileDrop += (_, e) =>
        {
            if (e.Files.Length > 0)
                LoadFile(e.Files[0]);
        };

        Window.ClientSizeChanged += (_, _) => ApplyWindowSize();

        if (!string.IsNullOrWhiteSpace(_recordPath))
        {
            _recording = true;
            _recordTotal = _recordSeconds * _recordFps;
            _recordDir = Path.Combine(Path.GetTempPath(), "msxaudio_vis_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_recordDir);

            // GIF: encoded in-process (no ffmpeg), at half resolution to keep the file small.
            if (Path.GetExtension(_recordPath).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                _gifWidth = VW / 2;
                _gifHeight = VH / 2;
                _gifFactor = Math.Max(1, VW / _gifWidth);
                _gif = new GifEncoder(_recordPath, _gifWidth, _gifHeight, _recordFps);
            }
            else
            {
                // Video with audio: the same samples that are heard go to a WAV next to the output,
                // then ffmpeg muxes frames + WAV (see TryEncode).
                _wavPath = Path.ChangeExtension(Path.GetFullPath(_recordPath), ".wav");
            }
        }

        ScanTracks(_startDir);

        if (_startFile is not null && File.Exists(_startFile))
            LoadFile(_startFile);
        else if (_tracks.Count > 0)
            LoadTrack(0);

        // Recording starts with audio already flowing, so the first frame is not empty. This chunk
        // belongs to the interval *before* frame 0, so it is not written to the WAV.
        if (_recording && _player is not null)
        {
            int remaining = Math.Max(1, 48000 / _recordFps);
            while (remaining > 0)
            {
                int chunk = Math.Min(2048, remaining);
                _player.Render(_render, chunk);
                remaining -= chunk;
            }

            UpdateSpectrum();
            UpdateMeters(1f / _recordFps);

            if (_wavPath is not null)
                _wav = new WavWriter(_wavPath, 48000, 2);
        }
    }

    private void ApplyWindowSize()
    {
        int w = Math.Max(320, Window.ClientBounds.Width);
        int h = Math.Max(200, Window.ClientBounds.Height);
        if (_graphics.PreferredBackBufferWidth == w && _graphics.PreferredBackBufferHeight == h)
            return;

        _graphics.PreferredBackBufferWidth = w;
        _graphics.PreferredBackBufferHeight = h;
        _graphics.ApplyChanges();
    }

    protected override void LoadContent()
    {
        _batch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
        _target = new RenderTarget2D(GraphicsDevice, VW, VH, false, SurfaceFormat.Color, DepthFormat.None);
    }

    private void ScanTracks(string dir)
    {
        _tracks.Clear();
        if (!Directory.Exists(dir))
            return;

        _tracks.AddRange(Directory
            .EnumerateFiles(dir, "*.vgm", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Freezes a mute/solo state at startup, for docs and screenshots:
    /// <c>MSXAUDIO_MUTE=0,3</c> and <c>MSXAUDIO_SOLO=4</c> (channel indexes 0..7).
    /// </summary>
    private void ApplyStartupMix(VgmPlayer player)
    {
        var mute = Environment.GetEnvironmentVariable("MSXAUDIO_MUTE");
        if (!string.IsNullOrWhiteSpace(mute))
        {
            foreach (var part in mute.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(part, out int ch) && ch is >= 0 and < 8)
                    player.ChannelMuted[ch] = true;
        }

        var solo = Environment.GetEnvironmentVariable("MSXAUDIO_SOLO");
        if (!string.IsNullOrWhiteSpace(solo) &&
            int.TryParse(solo.Trim(), out int soloChannel) &&
            soloChannel is >= 0 and < 8)
        {
            player.SoloChannel = soloChannel;
            _pianoChannel = soloChannel;
        }
    }

    private void LoadTrack(int index)
    {
        if (index < 0 || index >= _tracks.Count)
            return;

        _selected = index;
        LoadFile(_tracks[index]);
    }

    private void LoadFile(string path)
    {
        try
        {
            var vgm = VgmFile.Load(path);
            _player?.Dispose();
            _sound?.Dispose();

            _player = new VgmPlayer(vgm, loop: true, sampleRate: 48000, oversample: _profile.Oversample, profile: _profile);
            ApplyStartupMix(_player);
            _heldKeyMidi = -1;
            _auditionTimer = 0f;

            if (!_recording)
            {
                _sound = new DynamicSoundEffectInstance(48000, AudioChannels.Stereo);
                _sound.Play();
            }

            _paused = false;

            var folder = Path.GetDirectoryName(path) ?? ".";
            if (!string.Equals(folder, _startDir, StringComparison.OrdinalIgnoreCase))
            {
                ScanTracks(folder);
                int found = _tracks.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                if (found >= 0) _selected = found;
            }

            Console.WriteLine($"[Visualizer] loaded {path} events={vgm.Events.Count} len={vgm.TotalSeconds:0.0}s");
            _status = "LOADED " + Path.GetFileName(path);
            ClearNotes();
            _statusTimer = 3f;
            Array.Clear(_bars);
            Array.Clear(_barPeak);
            Array.Clear(_peakHold);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Visualizer] erro: {ex}");
            _status = "ERROR: " + ex.Message;
            _statusTimer = 6f;
        }
    }

    protected override void Update(GameTime gameTime)
    {
        _prevKeys = _keys;
        _keys = Keyboard.GetState();
        _prevMouse = _mouse;
        _mouse = Mouse.GetState();

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_statusTimer > 0) _statusTimer -= dt;

        HandleInput();

        if (_player is not null && !_recording)
        {
            UpdateMeters(dt);

            if (_auditionTimer > 0)
                _auditionTimer -= dt;

            UpdateInstrumentDemo(dt);

            if (!_paused)
            {
                if (!_shotPending && _player.Finished && _autoNext && _tracks.Count > 0)
                    LoadTrack((_selected + 1) % _tracks.Count);

                if (_shotPending)
                    AdvanceForScreenshot();
                else
                    Feed();
            }
            else
            {
                FeedPausedAudition();
            }
        }

        UpdateSpectrum();
        UpdateNotes();
        base.Update(gameTime);
    }

    /// <summary>Records a note event whenever a channel changes key or pitch (for the piano roll).</summary>
    private void UpdateNotes()
    {
        if (_player is null)
            return;

        double now = _player.Time;
        for (int ch = 0; ch < 8; ch++)
        {
            var info = _player.Channels[ch];
            int midi = info.Key && info.Frequency >= 20
                ? (int)Math.Round(69 + 12 * Math.Log2(info.Frequency / 440.0))
                : -1;

            var list = _notes[ch];

            if (midi == _lastMidi[ch])
            {
                if (list.Count > 0)
                    list[^1].Volume = Math.Max(list[^1].Volume, info.Volume);
                continue;
            }

            _lastMidi[ch] = midi;
            list.Add(new NoteEvent
            {
                Time = now,
                Frequency = info.Frequency,
                Volume = info.Volume,
                Key = midi >= 0,
            });

            while (list.Count > 1 && list[0].Time < now - RollSeconds * 2)
                list.RemoveAt(0);
        }
    }

    private void ClearNotes()
    {
        for (int ch = 0; ch < 8; ch++)
        {
            _notes[ch].Clear();
            _lastMidi[ch] = -1;
        }
    }

    private void UpdateMeters(float dt)
    {
        if (_player is null)
            return;

        for (int i = 0; i < 8; i++)
        {
            float level = _player.Channels[i].Level;
            _peakHold[i] = Math.Max(level, _peakHold[i] - dt * 0.35f);
        }
    }

    /// <summary>
    /// Deterministic advance for the screenshot gate: exactly 1/60 s of music per drawn frame, so
    /// frame N always shows the same moment (the audio device is not asked to pace anything).
    /// </summary>
    private void AdvanceForScreenshot()
    {
        if (_player is null)
            return;

        int remaining = 48000 / 60;
        while (remaining > 0)
        {
            int chunk = Math.Min(ChunkFrames, remaining);
            _player.Render(_render, chunk);
            remaining -= chunk;
        }

        UpdateSpectrum();
        UpdateMeters(1f / 60f);
        UpdateNotes();
    }

    private void Feed()
    {        if (_sound is null || _player is null)
            return;

        int guard = 0;
        while (_sound.PendingBufferCount < 3 && guard++ < 4)
        {
            _player.Render(_render, ChunkFrames);
            SubmitPcm();
        }
    }

    /// <summary>Feeds the audition alone while the song is paused (the song position does not move).</summary>
    private void FeedPausedAudition()
    {
        if (_sound is null || _player is null)
            return;

        if (_auditionTimer <= 0)
        {
            if (_sound.State == SoundState.Playing)
                _sound.Stop();

            return;
        }

        if (_sound.State != SoundState.Playing)
            _sound.Play();

        int guard = 0;
        while (_sound.PendingBufferCount < 3 && guard++ < 4)
        {
            _player.RenderAudition(_render, ChunkFrames);
            SubmitPcm();
        }
    }

    private void SubmitPcm()
    {
        for (int i = 0; i < _render.Length; i++)
        {
            short v = (short)Math.Clamp((int)Math.Round(_render[i] * 32767f), short.MinValue, short.MaxValue);
            _pcm[i * 2] = (byte)(v & 0xFF);
            _pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }

        try { _sound!.SubmitBuffer(_pcm); }
        catch (InvalidOperationException) { }
    }

    private bool Pressed(Keys key) => _keys.IsKeyDown(key) && !_prevKeys.IsKeyDown(key);

    private Point MouseDesign()
    {
        if (_scale <= 0) return Point.Zero;
        return new Point((int)((_mouse.X - _offX) / _scale), (int)((_mouse.Y - _offY) / _scale));
    }

    private void HandleInput()
    {
        if (Pressed(Keys.Escape))
            Exit();

        if (Pressed(Keys.H))
            _showHelp = !_showHelp;

        if (Pressed(Keys.Tab))
            CycleView();

        if (Pressed(Keys.Up) && _tracks.Count > 0)
        {
            _selected = (_selected - 1 + _tracks.Count) % _tracks.Count;
            LoadTrack(_selected);
        }

        if (Pressed(Keys.Down) && _tracks.Count > 0)
        {
            _selected = (_selected + 1) % _tracks.Count;
            LoadTrack(_selected);
        }

        if (Pressed(Keys.Enter) && _tracks.Count > 0)
            LoadTrack(_selected);

        if (Pressed(Keys.Space) && _sound is not null)
        {
            _paused = !_paused;
            if (_paused)
                _sound.Pause();
            else
                FlushAudio();
        }

        if (Pressed(Keys.R) && _player is not null)
        {
            _player.Restart();
            ClearNotes();
            FlushAudio();
        }

        if (Pressed(Keys.Left) && _player is not null)
        {
            _player.Seek(Math.Max(0, _player.Time - 5));
            ClearNotes();
            FlushAudio();
        }

        if (Pressed(Keys.Right) && _player is not null)
        {
            _player.Seek(Math.Min(_player.TotalSeconds, _player.Time + 5));
            ClearNotes();
            FlushAudio();
        }

        if (Pressed(Keys.P) && _player is not null)
            _player.PsgEnabled = !_player.PsgEnabled;

        if (Pressed(Keys.S) && _player is not null)
            _player.SccEnabled = !_player.SccEnabled;

        for (int i = 0; i < 8; i++)
        {
            var key = i < 3 ? Keys.D1 + i : Keys.D4 + (i - 3);
            if (Pressed(key) && _player is not null)
                _player.ChannelMuted[i] = !_player.ChannelMuted[i];
        }

        if (Pressed(Keys.O))
            CycleProfile();

        if (Pressed(Keys.L))
        {
            _autoNext = !_autoNext;
            _status = _autoNext ? "AUTO NEXT ON" : "AUTO NEXT OFF";
            _statusTimer = 2f;
        }

        HandleMouse();
    }

    private void HandleMouse()
    {
        bool down = _mouse.LeftButton == ButtonState.Pressed;
        bool pressed = down && _prevMouse.LeftButton == ButtonState.Released;
        var p = MouseDesign();

        // Piano note held with the mouse: it follows the pointer and stops on release, like a key.
        if (_heldKeyMidi >= 0)
        {
            if (!down)
                StopPianoHold();
            else if (_view == PanelView.Piano && _player is not null)
                DragPianoHold(p);
        }

        // Playlist: click a track to select and play it (works even before a song is loaded).
        if (pressed)
        {
            foreach (var (rect, index) in _trackHit)
            {
                if (!rect.Contains(p))
                    continue;

                _selected = index;
                LoadTrack(index);
                return;
            }
        }

        if (_player is null)
            return;

        // Seek bar: click or drag.
        if (down && _seekBar.Width > 0 && _seekBar.Contains(p))
        {
            double t = (p.X - _seekBar.Left) / (double)_seekBar.Width * _player.TotalSeconds;
            _player.Seek(Math.Clamp(t, 0, _player.TotalSeconds));
            if (pressed) ClearNotes();
            if (pressed) FlushAudio();
            return;
        }

        if (!pressed)
            return;

        // Header toggle: cycle spectrum -> piano roll -> piano.
        if (_viewToggleHit.Contains(p))
        {
            CycleView();
            return;
        }

        // Piano view: channel selectors, "play all", octave and the keys themselves.
        if (_view == PanelView.Piano)
        {
            for (int ch = 0; ch < 8; ch++)
            {
                if (!_pianoChannelHit[ch].Contains(p))
                    continue;

                _pianoChannel = ch;
                _status = "AUDITION " + ChannelName(ch);
                _statusTimer = 2f;
                return;
            }

            if (_pianoAllHit.Contains(p))
            {
                ToggleInstrumentDemo();
                return;
            }

            if (_pianoOctDownHit.Contains(p))
            {
                ShiftPianoOctave(-1);
                return;
            }

            if (_pianoOctUpHit.Contains(p))
            {
                ShiftPianoOctave(1);
                return;
            }

            // Black keys overlap the white ones, so they are tested first.
            int key = KeyAt(p);
            if (key >= 0)
            {
                StartPianoHold(key);
                return;
            }
        }

        // Per-channel S (solo) and M (mute) buttons, then the label (solo too).
        for (int ch = 0; ch < 8; ch++)
        {
            if (_soloHit[ch].Contains(p))
            {
                ToggleSolo(ch);
                return;
            }

            if (_muteHit[ch].Contains(p))
            {
                _player.ChannelMuted[ch] = !_player.ChannelMuted[ch];
                _status = $"{ChannelName(ch)} {(_player.ChannelMuted[ch] ? "MUTED" : "ON")}";
                _statusTimer = 2f;
                return;
            }

            if (_stripHit[ch].Contains(p))
            {
                ToggleSolo(ch);
                return;
            }
        }
    }

    /// <summary>Solo on/off; when soloing, the piano follows the channel that is being heard.</summary>
    private void ToggleSolo(int channel)
    {
        if (_player is null)
            return;

        _player.SoloChannel = _player.SoloChannel == channel ? null : channel;

        if (_player.SoloChannel is int solo)
            _pianoChannel = solo;

        _status = _player.SoloChannel is int s ? $"SOLO {ChannelName(s)}" : "SOLO OFF";
        _statusTimer = 2f;
    }

    private static string ChannelName(int ch) => ch < 3 ? $"PSG{ch + 1}" : $"SCC{ch - 2}";

    /// <summary>
    /// Whether a channel is actually audible right now: not muted, its chip is on and it passes the
    /// solo filter. The piano, the roll and the channel strips all use this so the screen matches
    /// what is heard.
    /// </summary>
    private bool IsAudible(int channel)
    {
        if (_player is null)
            return false;

        if (_player.ChannelMuted[channel])
            return false;

        bool chipOn = channel < 3 ? _player.PsgEnabled : _player.SccEnabled;
        if (!chipOn)
            return false;

        return _player.SoloChannel is not int solo || solo == channel;
    }

    /// <summary>MIDI note under a design-space point, or -1 (black keys are tested first).</summary>
    private int KeyAt(Point p)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            bool black = pass == 0;
            for (int midi = 0; midi < 128; midi++)
            {
                var rect = _pianoKeyRect[midi];
                if (rect.IsEmpty || Notes[midi % 12].Contains('#') != black)
                    continue;

                if (rect.Contains(p))
                    return midi;
            }
        }

        return -1;
    }

    /// <summary>
    /// Note-on: the note keeps sounding while the mouse button is held (drag retunes it), so the
    /// duration is exactly the duration of the click.
    /// </summary>
    private void StartPianoHold(int midi)
    {
        if (_player is null)
            return;

        if (_demoRunning)
        {
            _demoRunning = false;
            _demoChannel = -1;
        }

        _heldKeyMidi = midi;
        _player.AuditionHold(_pianoChannel, midi);
        _auditionTimer = 600f;   // keep the paused feeder alive while the key is held

        _status = $"{ChannelName(_pianoChannel)} {NoteName(FrequencyOf(midi))}";
        _statusTimer = 2f;

        if (_paused && _sound is not null)
        {
            // The song is paused: drop its queued buffers so the note is heard immediately.
            _sound.Stop();
            _sound.Play();
        }
    }

    /// <summary>While dragging with the button down, move the sounding note to the key under the cursor.</summary>
    private void DragPianoHold(Point p)
    {
        int midi = KeyAt(p);
        if (midi < 0 || midi == _heldKeyMidi || _player is null)
            return;

        _heldKeyMidi = midi;
        _player.AuditionHold(_pianoChannel, midi);

        _status = $"{ChannelName(_pianoChannel)} {NoteName(FrequencyOf(midi))}";
        _statusTimer = 2f;
    }

    /// <summary>Note-off on mouse release (the player applies a short fade to avoid a click).</summary>
    private void StopPianoHold()
    {
        if (_heldKeyMidi < 0)
            return;

        _heldKeyMidi = -1;
        _player?.StopAudition();
        _auditionTimer = 0.1f;   // feed the release tail while paused
    }

    /// <summary>
    /// Plays a fixed-length note with <paramref name="channel"/>'s instrument (used by the "play all"
    /// demo). Works while the song is paused too: the audition is rendered alone.
    /// </summary>
    private void PlayPianoFor(int channel, int midi)
    {
        if (_player is null)
            return;

        _player.Audition(channel, midi, 0.85);
        _auditionTimer = 1.05f;

        _status = $"{ChannelName(channel)} {NoteName(FrequencyOf(midi))}";
        _statusTimer = 2f;

        if (_paused && _sound is not null)
        {
            // The song is paused: drop its queued buffers so the audition is heard immediately.
            _sound.Stop();
            _sound.Play();
        }
    }

    private void ShiftPianoOctave(int delta)
    {
        _pianoBaseMidi = Math.Clamp(_pianoBaseMidi + delta * 12, 24, 72);
        _status = "OCTAVE " + (_pianoBaseMidi / 12 - 1);
        _statusTimer = 2f;
    }

    private void ToggleInstrumentDemo()
    {
        if (_demoRunning)
        {
            _demoRunning = false;
            _demoChannel = -1;
            _player?.StopAudition();
            _status = "DEMO STOPPED";
            _statusTimer = 2f;
            return;
        }

        bool any = false;
        for (int ch = 0; ch < 8; ch++)
            any |= IsAudible(ch);

        if (!any)
        {
            _status = "ALL CHANNELS MUTED";
            _statusTimer = 2f;
            return;
        }

        StopPianoHold();

        _demoRunning = true;
        _demoChannel = -1;
        _demoTimer = 0f;
        _status = "PLAYING ALL INSTRUMENTS";
        _statusTimer = 2f;
    }

    /// <summary>
    /// Walks the audible channels, playing each instrument at the note the song is using. Muted or
    /// soloed-out channels are skipped, so the demo matches the mix that is being heard.
    /// </summary>
    private void UpdateInstrumentDemo(float dt)
    {
        if (!_demoRunning || _player is null)
            return;

        _demoTimer -= dt;
        if (_demoTimer > 0)
            return;

        do
        {
            _demoChannel++;
        }
        while (_demoChannel < 8 && !IsAudible(_demoChannel));

        if (_demoChannel >= 8)
        {
            _demoRunning = false;
            _demoChannel = -1;
            return;
        }

        _pianoChannel = _demoChannel;
        PlayPianoFor(_demoChannel, DemoNote(_demoChannel));
        _demoTimer = 0.95f;
    }

    private int DemoNote(int channel)
    {
        if (_player is null)
            return 60;

        var info = _player.Channels[channel];
        int midi = info.Key && info.Frequency >= 20 ? MidiOf(info.Frequency) : 60;
        return Math.Clamp(midi, _pianoBaseMidi, _pianoBaseMidi + 35);
    }

    private void CycleProfile()
    {
        if (_player is null)
            return;

        var profiles = AudioProfile.All;
        int current = Array.FindIndex(profiles, p => p.Name == _player.Profile.Name);
        var next = profiles[(current + 1 + profiles.Length) % profiles.Length];
        double time = _player.Time;

        var vgm = _player.Song;
        var muted = (bool[])_player.ChannelMuted.Clone();
        int? solo = _player.SoloChannel;
        bool psgOn = _player.PsgEnabled;
        bool sccOn = _player.SccEnabled;

        _player.Dispose();
        _player = new VgmPlayer(vgm, loop: true, sampleRate: 48000, oversample: next.Oversample, profile: next);
        _player.Seek(time);

        // Keep what the user was listening to: mute, solo and chip toggles survive a profile change.
        for (int i = 0; i < 8; i++)
            _player.ChannelMuted[i] = muted[i];

        _player.SoloChannel = solo;
        _player.PsgEnabled = psgOn;
        _player.SccEnabled = sccOn;

        _heldKeyMidi = -1;
        _auditionTimer = 0f;
        ClearNotes();
        FlushAudio();

        _status = "PROFILE: " + next.Name.ToUpperInvariant();
        _statusTimer = 2f;
    }

    private void FlushAudio()
    {
        if (_sound is null)
            return;

        _sound.Stop();
        _sound.Play();
    }

    private void UpdateSpectrum()
    {
        if (_player is null)
            return;

        var scope = _player.Scope;
        int pos = _player.ScopePos;
        for (int i = 0; i < FftSize; i++)
            _fftSamples[i] = scope[(pos + scope.Length - FftSize + i) % scope.Length];

        Fft.Magnitude(_fftSamples, _fftMagnitude, _fftWindow);

        int bins = _fftMagnitude.Length;
        for (int b = 0; b < SpectrumBars; b++)
        {
            double f0 = Math.Pow(bins, b / (double)SpectrumBars);
            double f1 = Math.Pow(bins, (b + 1) / (double)SpectrumBars);
            int i0 = Math.Clamp((int)f0, 1, bins - 1);
            int i1 = Math.Clamp((int)Math.Ceiling(f1), i0 + 1, bins);

            float value = 0;
            for (int i = i0; i < i1; i++)
                value = Math.Max(value, _fftMagnitude[i]);

            // dB scale: 0 dB = full scale, -60 dB = floor. A linear bar would make everything but
            // the fundamental invisible; this is how audio analysers show the whole range.
            float db = 20f * MathF.Log10(Math.Max(value, 1e-5f));
            float norm = Math.Clamp((db + 60f) / 60f, 0f, 1f);

            _bars[b] = Math.Max(norm, _bars[b] - 0.03f);
            _barPeak[b] = Math.Max(norm, _barPeak[b] - 0.008f);
        }
    }

    // ── Desenho ─────────────────────────────────────────────────────────────────────

    protected override void Draw(GameTime gameTime)
    {
        int w = GraphicsDevice.PresentationParameters.BackBufferWidth;
        int h = GraphicsDevice.PresentationParameters.BackBufferHeight;

        // UI always renders on a fixed 1280x720 target, so resizing/maximising never moves anything.
        GraphicsDevice.SetRenderTarget(_target);
        GraphicsDevice.Clear(Background);

        _batch.Begin(samplerState: SamplerState.PointClamp);

        DrawHeader();
        DrawSeekBar();
        DrawPlaylist();

        if (_player is not null)
        {
            switch (_view)
            {
                case PanelView.Roll:
                    DrawRoll();
                    break;
                case PanelView.Piano:
                    DrawPiano();
                    break;
                default:
                    DrawSpectrum();
                    DrawScope();
                    break;
            }

            DrawChannels();
        }
        else
        {
            DrawEmpty();
        }

        DrawFooter();

        if (_showHelp)
            DrawHelp();

        _batch.End();

        // Letterbox the target into the window.
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Background);

        _scale = Math.Min(w / (float)VW, h / (float)VH);
        int destW = Math.Max(1, (int)(VW * _scale));
        int destH = Math.Max(1, (int)(VH * _scale));
        _offX = (w - destW) / 2f;
        _offY = (h - destH) / 2f;

        _batch.Begin(samplerState: SamplerState.LinearClamp);
        _batch.Draw(_target, new Rectangle((int)_offX, (int)_offY, destW, destH), Color.White);
        _batch.End();

        base.Draw(gameTime);

        if (_recording)
            CaptureFrame();
        else
            TryScreenshot();
    }

    private void Rect(int x, int y, int w, int h, Color color) =>
        _batch.Draw(_pixel, new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h)), color);

    private void Rect(Rectangle r, Color color) =>
        _batch.Draw(_pixel, r, color);

    private void Panel(int x, int y, int w, int h, Color? fill = null)
    {
        Rect(x, y, w, h, fill ?? PanelColor);
        Rect(x, y, w, 1, Grid);
        Rect(x, y + h - 1, w, 1, Grid);
        Rect(x, y, 1, h, Grid);
        Rect(x + w - 1, y, 1, h, Grid);
    }

    private void Text(string text, int x, int y, Color color, int scale = 1) =>
        PixelFont.Draw(_batch, _pixel, text, x, y, color, scale);

    private void DrawHeader()
    {
        Rect(0, 0, VW, 46, PanelHi);
        Rect(0, 45, VW, 1, Grid);

        Text("MSX AUDIO VISUALIZER", 16, 12, Accent, 2);
        Text("PSG AY-3-8910 + KONAMI SCC", 16, 30, Dim);

        if (_player is not null)
        {
            string name = Path.GetFileNameWithoutExtension(_player.Name).ToUpperInvariant();
            Text(name, 360, 16, TextColor, 2);

            string time = $"{FormatTime(_player.Time)} / {FormatTime(_player.TotalSeconds)}";
            Text(time, VW - 16 - PixelFont.Measure(time), 12, TextColor, 2);

            string info = $"PSG {(int)(_player.Song.AyClock / 1000)}KHZ  SCC {(int)(1789772.5 / 1000)}KHZ  OUT 48KHZ  X{_player.Oversample}  {_player.Profile.Name.ToUpperInvariant()}";
            Text(info, VW - 16 - PixelFont.Measure(info), 30, Dim);
        }
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int total = (int)seconds;
        return $"{total / 60:00}:{total % 60:00}";
    }

    private void DrawSeekBar()
    {
        const int x = 336, y = 52, w = VW - 348, h = 44;
        Panel(x, y, w, h);
        Text("POSITION", x + 10, y + 6, Accent);

        int barX = x + 10, barY = y + 24, barW = w - 20, barH = 10;
        _seekBar = new Rectangle(barX, barY - 4, barW, barH + 8);

        Rect(barX, barY, barW, barH, Grid);

        if (_player is null)
            return;

        double total = Math.Max(0.001, _player.TotalSeconds);
        double time = Math.Clamp(_player.Time, 0, total);
        int filled = (int)(barW * time / total);

        Rect(barX, barY, filled, barH, PsgColor);

        // Loop window (start..end) when the VGM exposes it.
        var song = _player.Song;
        if (song.HasLoop)
        {
            int lx = barX + (int)(barW * (song.LoopStartSeconds / total));
            int lw = Math.Max(2, (int)(barW * (song.LoopSeconds / total)));
            Rect(lx, barY - 3, lw, 3, Accent);
        }

        // Marker.
        int mx = barX + filled;
        Rect(mx - 1, barY - 4, 3, barH + 8, Accent);

        string text = $"{FormatTime(time)} / {FormatTime(total)}";
        Text(text, x + w - 10 - PixelFont.Measure(text), y + 6, TextColor);
    }

    private void DrawPlaylist()
    {
        const int x = 12, y = 52, w = 316, h = 614;
        Panel(x, y, w, h);

        Text("TRACKS", x + 10, y + 8, Accent);
        Rect(x + 10, y + 24, w - 20, 1, Grid);

        int rows = (h - 90) / 18;
        int first = Math.Max(0, Math.Min(_selected - rows / 2, Math.Max(0, _tracks.Count - rows)));

        _trackHit.Clear();

        for (int i = 0; i < rows && first + i < _tracks.Count; i++)
        {
            int index = first + i;
            int rowY = y + 30 + i * 18;
            bool active = index == _selected;

            if (active)
                Rect(x + 6, rowY - 4, w - 12, 17, PanelHi);

            _trackHit.Add((new Rectangle(x + 4, rowY - 5, w - 8, 18), index));

            string name = Path.GetFileNameWithoutExtension(_tracks[index]).ToUpperInvariant();
            if (name.Length > 30) name = name[..30];
            Text(name, x + 12, rowY, active ? Accent : Dim);
        }

        if (_tracks.Count == 0)
        {
            Text("NO .VGM FILES", x + 10, y + 40, Warn);
            Text("DROP A .VGM HERE", x + 10, y + 56, Dim);
        }

        int listY = y + h - 46;
        Rect(x + 10, listY, w - 20, 1, Grid);
        string length = _player is null ? "--:--" : FormatTime(_player.TotalSeconds);
        Text($"LENGTH {length}", x + 10, listY + 6, Dim);
        Text($"{_tracks.Count} FILE(S)", x + 10, listY + 20, Dim);
    }

    private void DrawEmpty()
    {
        Panel(336, 52, VW - 348, 616);
        Text("DROP A .VGM FILE OR PASS A FOLDER AS THE FIRST ARGUMENT", 368, 330, Dim);
        Text("MSXAUDIOVISUALIZER <FILE.VGM | FOLDER> [--RECORD OUT.GIF --SECONDS 10]", 368, 350, Dim);
    }

    private void DrawSpectrum()
    {
        const int x = 336, y = 104, w = VW - 348, h = 182;
        Panel(x, y, w, h);

        Text("SPECTRUM", x + 10, y + 6, Accent);
        string next = NextViewLabel();
        DrawViewToggle(x + w - PixelFont.Measure(next) - 22, y + 2, next);

        // Room for the dB scale on the left and the frequency labels under the plot.
        const int gutter = 40;
        const int footer = 12;
        int left = x + gutter, top = y + 22;
        int innerW = w - gutter - 12, innerH = h - 34 - footer;

        // dB grid: 0 (full scale) down to -60 dB.
        for (int db = 0; db >= -60; db -= 20)
        {
            int gy = top + (int)((1 - (db + 60) / 60f) * (innerH - 1));
            Rect(left, gy, innerW, 1, Grid);
            Text(db == 0 ? "0 DB" : $"{db}", x + 8, gy - 3, db == 0 ? TextColor : Dim);
        }

        // Frequency grid (log axis, matching the bars).
        foreach (double freq in FrequencyMarks)
        {
            int gx = BarX(freq, left, innerW);
            Rect(gx, top, 1, innerH, Grid);

            string label = freq >= 1000 ? $"{freq / 1000:0}K" : $"{freq:0}";
            Text(label, gx + 3, top + innerH + 2, Dim);
        }

        float barW = innerW / (float)SpectrumBars;
        for (int b = 0; b < SpectrumBars; b++)
        {
            float value = Math.Clamp(_bars[b], 0f, 1f);
            int barH = (int)(value * innerH);
            int bx = left + (int)(b * barW);
            int bw = Math.Max(1, (int)barW - 1);

            if (barH > 0)
            {
                var color = Color.Lerp(PsgColor, SccColor, b / (float)SpectrumBars);
                Rect(bx, top + innerH - barH, bw, barH, color);
            }

            // Peak hold: a bright cap that falls slowly, so short transients stay visible.
            int peakY = top + innerH - (int)(Math.Clamp(_barPeak[b], 0f, 1f) * innerH);
            Rect(bx, Math.Clamp(peakY, top, top + innerH - 1), bw, 2, new Color(235, 244, 252));
        }
    }

    private static readonly double[] FrequencyMarks = { 100, 1000, 10000 };

    /// <summary>Horizontal position of a frequency on the log-spaced spectrum axis.</summary>
    private static int BarX(double frequency, int left, int innerW)
    {
        double bin = frequency * FftSize / 48000.0;
        double norm = Math.Log(Math.Max(1.0, bin)) / Math.Log(FftSize / 2.0);
        return left + (int)Math.Round(Math.Clamp(norm, 0, 1) * (innerW - 1));
    }

    /// <summary>
    /// Scrolling piano roll: time to the right, pitch up, one colour per channel. Arpeggios leave a
    /// short tail, so fast SCC runs and PSG chords stay stacked (a chord is visible, not a staircase
    /// of disconnected dots).
    /// </summary>
    private void DrawRoll()
    {
        const int x = 336, y = 104, w = VW - 348, h = 326;
        Panel(x, y, w, h);

        Text("PIANO ROLL", x + 10, y + 6, Accent);

        string next = NextViewLabel();
        DrawViewToggle(x + w - PixelFont.Measure(next) - 22, y + 2, next);

        // Channel legend (same colours as the notes).
        int lx = x + 88;
        for (int ch = 0; ch < 8; ch++)
        {
            Rect(lx, y + 8, 5, 5, ChannelColors[ch]);
            Text(ChannelName(ch), lx + 8, y + 7, Dim);
            lx += 42;
        }

        const int gutter = 22;   // mini keyboard on the left
        const int axis = 10;     // time labels under the plot
        int left = x + 10 + gutter, top = y + 24;
        int innerW = w - gutter - 22, innerH = h - 34 - axis;

        const int lowMidi = 24, highMidi = 96;   // C1..C7
        int semis = highMidi - lowMidi + 1;
        int step = Math.Max(2, innerH / semis);
        int axisY = top + innerH + 2;

        int RowY(int midi) => top + innerH - (midi - lowMidi + 1) * innerH / semis;
        int RowH() => Math.Max(2, innerH / semis - 1);

        // Mini keyboard gutter (white/black rows) with the C labels.
        for (int midi = lowMidi; midi <= highMidi; midi++)
        {
            bool black = Notes[midi % 12].Contains('#');
            Rect(x + 10, RowY(midi), gutter - 2, RowH(),
                black ? new Color(44, 52, 64) : new Color(140, 152, 166));
        }

        // Alternate octave bands, so the pitch is readable at a glance.
        for (int midi = lowMidi; midi < highMidi; midi += 12)
        {
            if (((midi / 12) & 1) != 0)
                continue;

            int y0 = RowY(midi + 12);
            int y1 = RowY(midi);
            Rect(left, y0, innerW, Math.Max(1, y1 - y0), new Color(20, 27, 38));
        }

        for (int midi = lowMidi; midi <= highMidi; midi += 12)
        {
            int py = RowY(midi);
            Rect(left, py, innerW, 1, Grid);
            if (py >= top + 4)
                Text("C" + (midi / 12 - 1), left + 3, py - 3, Dim);
        }

        // Time grid every 2 s (0 = now at the right edge).
        for (int s = 0; s <= (int)RollSeconds; s += 2)
        {
            int gx = left + (int)(innerW * (1 - s / RollSeconds));
            if (s > 0)
                Rect(gx, top, 1, innerH, Grid);

            string label = s == 0 ? "NOW" : $"-{s}S";
            int tx = s == 0 ? gx - PixelFont.Measure(label) + 1 : gx + 2;
            Text(label, tx, axisY, s == 0 ? Accent : Dim);
        }

        if (_player is null)
            return;

        double now = _player.Time;

        for (int ch = 0; ch < 8; ch++)
        {
            bool audible = IsAudible(ch);
            var color = audible ? ChannelColors[ch] : Dim;
            var list = _notes[ch];

            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.Key || e.Frequency < 20)
                    continue;

                double t0 = e.Time;
                double nextTime = i + 1 < list.Count ? list[i + 1].Time : now;
                double t1 = Math.Min(nextTime, now) + NoteTailSeconds;
                if (t1 <= t0)
                    continue;

                int midi = (int)Math.Round(69 + 12 * Math.Log2(e.Frequency / 440.0));
                int py = RowY(Math.Clamp(midi, lowMidi, highMidi));

                int px0 = left + (int)(innerW * (1 - (now - t0) / RollSeconds));
                int px1 = left + (int)(innerW * (1 - (now - t1) / RollSeconds));
                if (px1 < left || px0 > left + innerW)
                    continue;

                px0 = Math.Clamp(px0, left, left + innerW - 1);
                px1 = Math.Clamp(px1, left, left + innerW);

                int thickness = Math.Clamp(3 + (int)Math.Round(Math.Clamp(e.Volume, 0f, 1f) * 4f), 3, Math.Max(3, step));
                Rect(px0, py, Math.Max(1, px1 - px0), thickness, color);
            }
        }

        // "Now" line at the right edge.
        Rect(left + innerW - 1, top, 2, innerH, Accent);
    }

    /// <summary>
    /// Playable three-octave keyboard. Clicking a key auditions the selected channel with the very
    /// instrument the song is using (PSG envelope/noise or SCC wave RAM); the bars at the bottom of
    /// each key show which notes each channel of the song is playing right now.
    /// </summary>
    private void DrawPiano()
    {
        const int x = 336, y = 104, w = VW - 348, h = 326;
        Panel(x, y, w, h);

        Text("PIANO", x + 10, y + 6, Accent);
        Text("CLICK AND HOLD A KEY TO PLAY - DRAG TO SLIDE", x + 74, y + 7, Dim);

        string next = NextViewLabel();
        DrawViewToggle(x + w - PixelFont.Measure(next) - 22, y + 2, next);

        // Channel selector: the key plays the instrument of the selected channel.
        int rowY = y + 24;
        for (int ch = 0; ch < 8; ch++)
        {
            var rect = new Rectangle(x + 10 + ch * 46, rowY, 42, 16);
            _pianoChannelHit[ch] = rect;

            // Audible channels keep their colour; muted / soloed-out ones fade, like the strips.
            var color = IsAudible(ch)
                ? ChannelColors[ch]
                : Color.Lerp(ChannelColors[ch], PanelColor, 0.7f);

            DrawButton(rect, ChannelName(ch), _pianoChannel == ch, color);

            // Always show the channel colour as a swatch, even when the button is not selected.
            Rect(rect.X + 3, rect.Y + 3, 3, rect.Height - 6, color);
        }

        _pianoAllHit = new Rectangle(x + 10 + 8 * 46 + 8, rowY, 80, 16);
        DrawButton(_pianoAllHit, _demoRunning ? "STOP" : "PLAY ALL", _demoRunning, Accent);

        int navX = _pianoAllHit.Right + 16;
        _pianoOctDownHit = new Rectangle(navX, rowY, 20, 16);
        _pianoOctUpHit = new Rectangle(navX + 24, rowY, 20, 16);
        DrawButton(_pianoOctDownHit, "<", false, Accent);
        DrawButton(_pianoOctUpHit, ">", false, Accent);
        Text($"OCT {_pianoBaseMidi / 12 - 1}", navX + 52, rowY + 5, Dim);

        int kx = x + 12, ky = y + 50, kw = w - 24, kh = h - 64;
        const int whiteCount = 21;                 // 3 octaves
        int whiteW = kw / whiteCount;

        Array.Clear(_pianoKeyRect);

        // Which notes the song is playing right now (bit per channel on the MIDI note). Muted/soloed
        // out channels are skipped, so the keyboard only lights what is actually audible.
        Array.Clear(_keyLit);
        var player = _player!;
        for (int ch = 0; ch < 8; ch++)
        {
            if (!IsAudible(ch))
                continue;

            var info = player.Channels[ch];
            if (!info.Key || info.Frequency < 20)
                continue;

            int midi = MidiOf(info.Frequency);
            if (midi is >= 0 and < 128)
                _keyLit[midi] |= 1 << ch;
        }

        int auditionNote = player.AuditionNote;

        // White keys first, then the black ones on top (drawing order = z order).
        int wi = 0;
        for (int midi = _pianoBaseMidi; midi < _pianoBaseMidi + 36; midi++)
        {
            if (Notes[midi % 12].Contains('#'))
                continue;

            var rect = new Rectangle(kx + wi * whiteW, ky, whiteW - 1, kh);
            _pianoKeyRect[midi] = rect;

            var fill = midi == auditionNote ? new Color(255, 224, 150) : new Color(215, 223, 232);
            Rect(rect, fill);
            Rect(rect.X, rect.Y, 1, rect.Height, new Color(150, 162, 176));
            wi++;
        }

        for (int midi = _pianoBaseMidi; midi < _pianoBaseMidi + 36; midi++)
        {
            if (!Notes[midi % 12].Contains('#'))
                continue;

            int bw = Math.Max(10, (int)(whiteW * 0.62f));

            // Centre the black key on the boundary of the white keys around it.
            int before = WhiteIndexBefore(_pianoBaseMidi, midi);
            var rect = new Rectangle(kx + before * whiteW - bw / 2, ky, bw, (int)(kh * 0.62f));

            _pianoKeyRect[midi] = rect;

            var fill = midi == auditionNote ? new Color(120, 92, 26) : new Color(24, 30, 40);
            Rect(rect, fill);
        }

        // Octave labels on the white C keys.
        for (int midi = _pianoBaseMidi; midi < _pianoBaseMidi + 36; midi += 12)
        {
            var rect = _pianoKeyRect[midi];
            if (rect.IsEmpty)
                continue;

            Text("C" + (midi / 12 - 1), rect.X + (rect.Width - 12) / 2, rect.Bottom - 26, new Color(70, 82, 96));
        }

        // Song notes: a translucent wash over the whole key plus one solid band per channel, so the
        // lit keys are readable from across the room (the old 4 px bars were too subtle).
        for (int midi = _pianoBaseMidi; midi < _pianoBaseMidi + 36; midi++)
        {
            int mask = _keyLit[midi];
            if (mask == 0)
                continue;

            var rect = _pianoKeyRect[midi];
            if (rect.IsEmpty)
                continue;

            int first = 0;
            while (first < 8 && (mask & (1 << first)) == 0)
                first++;

            var wash = ChannelColors[first] * 0.45f;
            Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2, wash);

            int bands = 0;
            for (int ch = 0; ch < 8; ch++)
                if ((mask & (1 << ch)) != 0)
                    bands++;

            int bandH = bands > 1 ? 7 : 12;
            int band = 0;

            for (int ch = 0; ch < 8; ch++)
            {
                if ((mask & (1 << ch)) == 0)
                    continue;

                int by = rect.Bottom - 2 - bandH - band * (bandH + 1);
                Rect(rect.X + 2, by, rect.Width - 4, bandH, ChannelColors[ch]);

                // Dark outline keeps the band readable even on a white key.
                Rect(rect.X + 2, by, rect.Width - 4, 1, Background);
                band++;
            }
        }

        // Audition note: outline the whole key so the note being played stands out.
        if (auditionNote is >= 0 and < 128)
        {
            var rect = _pianoKeyRect[auditionNote];
            if (!rect.IsEmpty)
            {
                Rect(rect.X, rect.Y, 2, rect.Height, Accent);
                Rect(rect.Right - 2, rect.Y, 2, rect.Height, Accent);
                Rect(rect.X, rect.Y, rect.Width, 4, Accent);
            }
        }

        var target = player.Channels[_pianoChannel];
        int targetNote = target.Key && target.Frequency >= 20 ? MidiOf(target.Frequency) : -1;
        string playing = targetNote >= 0 ? NoteName(target.Frequency) : "---";
        bool audibleTarget = IsAudible(_pianoChannel);

        string state = audibleTarget ? "" : "  MUTED / SOLOED OUT";
        if (targetNote >= 0 && targetNote < _pianoBaseMidi)
            state += "  NOTE BELOW - USE '<'";
        else if (targetNote > _pianoBaseMidi + 35)
            state += "  NOTE ABOVE - USE '>'";

        Text($"{ChannelName(_pianoChannel)}  {playing}  VOL {target.Volume * 100:0}%  " +
             $"OCT {_pianoBaseMidi / 12 - 1}  ({(_pianoChannel < 3 ? "PSG" : "SCC")} INSTRUMENT){state}",
            x + 12, ky + kh + 6, !audibleTarget ? Warn : state.Length > 0 ? Accent : Dim);
    }

    /// <summary>Index (0-based) of the white key that starts before <paramref name="midi"/>.</summary>
    private static int WhiteIndexBefore(int baseMidi, int midi)
    {
        int index = 0;
        for (int m = baseMidi; m < midi; m++)
            if (!Notes[m % 12].Contains('#'))
                index++;

        return index;
    }

    private static int MidiOf(double frequency) =>
        (int)Math.Round(69 + 12 * Math.Log2(frequency / 440.0));

    private static double FrequencyOf(int midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);

    private void DrawScope()
    {
        const int x = 336, y = 292, w = VW - 348, h = 138;
        Panel(x, y, w, h);
        Text("MASTER", x + 10, y + 6, Accent);

        int left = x + 10, top = y + 22, innerW = w - 20, innerH = h - 32;
        Rect(left, top + innerH / 2, innerW, 1, Grid);

        var scope = _player!.Scope;
        int pos = _player.ScopePos;
        int n = scope.Length;

        // Trigger on an upward zero crossing so the waveform does not jitter frame to frame.
        int trig = 0;
        for (int i = 0; i < n / 2; i++)
        {
            float a = scope[(pos + i) % n];
            float b = scope[(pos + i + 1) % n];
            if (a < 0 && b >= 0)
            {
                trig = i;
                break;
            }
        }

        int available = n - trig - 1;
        if (available < innerW)
            available = n - trig;

        // Min/max envelope per column (keeps the peaks instead of averaging them away).
        for (int col = 0; col < innerW; col++)
        {
            int i0 = trig + col * available / innerW;
            int i1 = trig + (col + 1) * available / innerW;
            if (i1 <= i0) i1 = i0 + 1;

            float min = 1f, max = -1f;
            for (int i = i0; i < i1 && i < n; i++)
            {
                float v = scope[(pos + i) % n];
                if (v < min) min = v;
                if (v > max) max = v;
            }

            int y0 = top + innerH / 2 - (int)(max * innerH * 1.7f);
            int y1 = top + innerH / 2 - (int)(min * innerH * 1.7f);
            y0 = Math.Clamp(y0, top, top + innerH - 1);
            y1 = Math.Clamp(y1, top, top + innerH - 1);

            int topY = Math.Min(y0, y1);
            int height = Math.Max(1, Math.Abs(y1 - y0));
            Rect(left + col, topY, 1, height, PsgColor);
        }
    }

    private void DrawChannels()
    {
        const int x = 336, y = 436, w = VW - 348, h = 222;
        Panel(x, y, w, h);
        Text("CHANNELS", x + 10, y + 6, Accent);
        Text("COLOUR = CHANNEL   CLICK LABEL = SOLO   S = SOLO   M = MUTE", x + 96, y + 6, Dim);

        int stripW = (w - 20) / 8;
        int stripY = y + 24;
        int stripH = h - 34;

        for (int ch = 0; ch < 8; ch++)
        {
            var info = _player!.Channels[ch];
            bool isPsg = ch < 3;
            bool muted = _player.ChannelMuted[ch];
            bool soloed = _player.SoloChannel == ch;
            bool audible = IsAudible(ch);

            // The strip is painted with the same colour the channel uses in the roll and the piano.
            var color = ChannelColors[ch];
            if (!audible) color = Dim;

            int sx = x + 10 + ch * stripW;
            _stripHit[ch] = new Rectangle(sx + 2, stripY, stripW - 4, 20);

            Panel(sx + 2, stripY, stripW - 4, stripH, PanelHi);
            Rect(sx + 4, stripY + 19, stripW - 8, 2, color);

            // Colour key: filled square + name in the channel colour.
            Rect(sx + 8, stripY + 6, 6, 6, color);
            Text(isPsg ? $"PSG{info.Number}" : $"SCC{info.Number}", sx + 18, stripY + 6, color);

            var soloRect = new Rectangle(sx + stripW - 38, stripY + 4, 14, 12);
            var muteRect = new Rectangle(sx + stripW - 20, stripY + 4, 14, 12);
            _soloHit[ch] = soloRect;
            _muteHit[ch] = muteRect;

            DrawButton(soloRect, "S", soloed, Accent);
            DrawButton(muteRect, "M", muted, Warn);

            // Live mini scope (min/max envelope per column).
            int scopeX = sx + 8, scopeY = stripY + 24, scopeW = stripW - 16, scopeH = 46;
            Rect(scopeX, scopeY + scopeH / 2, scopeW, 1, Grid);

            var ring = _player.ChannelScope(ch);
            int ringPos = _player.ChannelScopePos;
            int ringLen = ring.Length;

            int usable = Math.Min(ringLen, scopeW * 8);
            for (int col = 0; col < scopeW; col++)
            {
                int i0 = col * usable / scopeW;
                int i1 = (col + 1) * usable / scopeW;
                if (i1 <= i0) i1 = i0 + 1;

                float min = 1f, max = -1f;
                for (int i = i0; i < i1 && i < ringLen; i++)
                {
                    float v = ring[(ringPos + ringLen - usable + i) % ringLen];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }

                int y0 = scopeY + scopeH / 2 - (int)(max * scopeH * 1.2f);
                int y1 = scopeY + scopeH / 2 - (int)(min * scopeH * 1.2f);
                y0 = Math.Clamp(y0, scopeY, scopeY + scopeH - 1);
                y1 = Math.Clamp(y1, scopeY, scopeY + scopeH - 1);

                Rect(scopeX + col, Math.Min(y0, y1), 1, Math.Max(1, Math.Abs(y1 - y0)), color);
            }

            // SCC wave RAM (32 samples).
            if (!isPsg)
            {
                int waveY = stripY + 74, waveH = 20;
                Rect(scopeX, waveY + waveH / 2, scopeW, 1, Grid);
                _player.CopySccWave(ch - 3, _wave);

                var waveColor = Color.Lerp(color, Color.White, 0.3f);
                int stepX = Math.Max(1, scopeW / 32);
                for (int i = 0; i < 32; i++)
                {
                    int px = scopeX + i * stepX;
                    int py = waveY + waveH / 2 - (int)(_wave[i] * waveH / 2);
                    py = Math.Clamp(py, waveY, waveY + waveH - 1);
                    Rect(px, Math.Min(py, waveY + waveH / 2), Math.Max(1, stepX - 1),
                        Math.Max(1, Math.Abs(py - (waveY + waveH / 2))), waveColor);
                }
            }

            // Volume bar.
            int volX = sx + stripW - 16, volY = stripY + 24, volH = 70;
            Rect(volX, volY, 6, volH, Grid);
            int vol = (int)(info.Volume * volH);
            Rect(volX, volY + volH - vol, 6, vol, color);

            // Level bar + peak hold.
            int levelY = stripY + 104, levelW = stripW - 16;
            Rect(scopeX, levelY, levelW, 6, Grid);
            Rect(scopeX, levelY, (int)(Math.Clamp(info.Level * 3f, 0, 1) * levelW), 6, color);

            int peakX = scopeX + (int)(Math.Clamp(_peakHold[ch] * 3f, 0, 1) * levelW);
            Rect(Math.Min(peakX, scopeX + levelW - 2), levelY - 2, 2, 10, Accent);

            string freq = info.Frequency >= 1000
                ? $"{info.Frequency / 1000:0.0}K"
                : $"{info.Frequency:0}HZ";
            Text(freq, sx + 8, stripY + 120, info.Key ? TextColor : Dim);
            Text(NoteName(info.Frequency), sx + 8, stripY + 134, info.Key ? Accent : Dim);
            Text(info.Key ? "KEY" : "---", sx + 8, stripY + 148, info.Key ? color : Dim);
        }
    }

    // Clickable "next view" toggle in the panel header.
    private Rectangle _viewToggleHit;

    private void CycleView()
    {
        _view = _view switch
        {
            PanelView.Spectrum => PanelView.Roll,
            PanelView.Roll => PanelView.Piano,
            _ => PanelView.Spectrum,
        };

        if (_view != PanelView.Piano)
        {
            StopPianoHold();

            if (_demoRunning)
            {
                _demoRunning = false;
                _demoChannel = -1;
                _player?.StopAudition();
            }
        }

        _status = ViewName(_view);
        _statusTimer = 2f;
    }

    private static string ViewName(PanelView view) => view switch
    {
        PanelView.Roll => "PIANO ROLL",
        PanelView.Piano => "PIANO",
        _ => "SPECTRUM",
    };

    /// <summary>Label of the view that the header button switches to.</summary>
    private string NextViewLabel() => ViewName(_view switch
    {
        PanelView.Spectrum => PanelView.Roll,
        PanelView.Roll => PanelView.Piano,
        _ => PanelView.Spectrum,
    });

    private void DrawViewToggle(int x, int y, string label)
    {
        int w = PixelFont.Measure(label) + 12;
        _viewToggleHit = new Rectangle(x, y, w, 16);
        DrawButton(_viewToggleHit, label, false, Accent);
        Text(label, x + 6, y + 5, Dim);
    }

    private void DrawButton(Rectangle rect, string label, bool active, Color activeColor)
    {
        Rect(rect, active ? activeColor : Grid);
        Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2, active ? PanelHi : PanelColor);

        int tw = Math.Max(0, PixelFont.Measure(label) - 1);
        Text(label, rect.X + (rect.Width - tw) / 2, rect.Y + (rect.Height - 7) / 2,
            active ? activeColor : Dim);
    }

    private static readonly string[] Notes = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    private static string NoteName(double frequency)
    {
        if (frequency < 20 || double.IsNaN(frequency))
            return "---";

        int midi = (int)Math.Round(69 + 12 * Math.Log2(frequency / 440.0));
        if (midi < 0 || midi > 127) return "---";
        return Notes[midi % 12] + (midi / 12 - 1);
    }

    private void DrawFooter()
    {
        // Kept away from the very bottom edge: a maximised window can have the taskbar over the
        // last pixels, and the help line must stay readable.
        Rect(0, VH - 56, VW, 44, PanelHi);
        Rect(0, VH - 56, VW, 1, Grid);

        var p = _player;
        string chipState = p is null ? "" : $"PSG[{(p.PsgEnabled ? "ON" : "OFF")}] SCC[{(p.SccEnabled ? "ON" : "OFF")}]{(p.SoloChannel is int s ? "  SOLO " + ChannelName(s) : "")}";
        Text(chipState, 16, VH - 50, TextColor);

        Text("TAB/HEADER VIEW   UP/DOWN TRACK   ENTER PLAY   SPACE PAUSE   LEFT/RIGHT SEEK   CLICK: TRACK / BAR / S-M", 16, VH - 36, Dim);
        Text("P PSG   S SCC   1-8 CHANNEL   O PROFILE   L AUTONEXT   R RESTART   H HELP   ESC QUIT", 16, VH - 22, Dim);

        if (_statusTimer > 0)
        {
            int w = PixelFont.Measure(_status, 2);
            Text(_status, VW - 16 - w, VH - 82, Accent, 2);
        }
    }

    private void DrawHelp()
    {
        const int w = 660, h = 356;
        int x = (VW - w) / 2, y = (VH - h) / 2;
        Panel(x, y, w, h, PanelHi);
        Rect(x, y, w, 24, PanelColor);
        Text("HELP", x + 10, y + 8, Accent);

        string[] lines =
        {
            "TAB / HEADER   SPECTRUM / PIANO ROLL / PIANO",
            "UP / DOWN      SELECT TRACK",
            "ENTER          PLAY SELECTED",
            "SPACE          PAUSE / RESUME",
            "LEFT / RIGHT   SEEK -/+ 5 S",
            "CLICK BAR      SEEK TO POSITION",
            "CLICK LABEL    SOLO THAT CHANNEL",
            "P / S          TOGGLE PSG / SCC",
            "1 2 3          PSG CHANNELS",
            "4 5 6 7 8      SCC CHANNELS",
            "O              AUDIO PROFILE",
            "L              AUTO NEXT",
            "R              RESTART",
            "H              THIS HELP",
            "ESC            QUIT",
            "",
            "PIANO: CLICK AND HOLD A KEY TO PLAY THE SELECTED",
            "CHANNEL'S INSTRUMENT (WORKS WHILE PAUSED); RELEASE",
            "TO STOP. DRAG ACROSS THE KEYS TO SLIDE. 'PLAY ALL'",
            "WALKS THE AUDIBLE CHANNELS; '<' / '>' CHANGE OCTAVE.",
            "",
            "DRAG A .VGM FILE INTO THE WINDOW TO PLAY IT",
            "MSXAUDIOVISUALIZER SONG.VGM --RECORD OUT.GIF --SECONDS 10",
        };

        for (int i = 0; i < lines.Length; i++)
            Text(lines[i], x + 16, y + 36 + i * 14, i >= lines.Length - 2 ? Dim : TextColor);
    }

    // -- Capture / recording -------------------------------------------------------

    private int _frame;
    private bool _shotDone;
    private readonly bool _shotPending = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSXAUDIO_SHOT"));

    private void TryScreenshot()
    {
        if (_shotDone)
            return;

        _frame++;
        string? path = Environment.GetEnvironmentVariable("MSXAUDIO_SHOT");
        if (string.IsNullOrWhiteSpace(path))
            return;

        int target = int.TryParse(Environment.GetEnvironmentVariable("MSXAUDIO_SHOT_FRAME"), out int f) ? f : 90;
        if (_frame < target)
            return;

        _shotDone = true;
        SavePng(path);
        Exit();
    }

    private Color[] ReadFrame(out int w, out int h)
    {
        w = VW;
        h = VH;
        var data = new Color[VW * VH];
        _target.GetData(data);
        return data;
    }

    private static Color[] Downscale(Color[] src, int sw, int sh, int factor, out int dw, out int dh)
    {
        dw = Math.Max(1, sw / factor);
        dh = Math.Max(1, sh / factor);
        var dst = new Color[dw * dh];
        int n = factor * factor;

        for (int y = 0; y < dh; y++)
        for (int x = 0; x < dw; x++)
        {
            int r = 0, g = 0, b = 0;
            for (int dy = 0; dy < factor; dy++)
            for (int dx = 0; dx < factor; dx++)
            {
                var c = src[(y * factor + dy) * sw + x * factor + dx];
                r += c.R;
                g += c.G;
                b += c.B;
            }

            dst[y * dw + x] = new Color(r / n, g / n, b / n);
        }

        return dst;
    }

    private void SavePng(string path)
    {
        try
        {
            var data = ReadFrame(out int w, out int h);

            using var texture = new Texture2D(GraphicsDevice, w, h);
            texture.SetData(data);
            using var stream = File.Create(path);
            texture.SaveAsPng(stream, w, h);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Visualizer] png falhou: {ex.Message}");
        }
    }

    private void CaptureFrame()
    {
        if (_player is null)
        {
            Exit();
            return;
        }

        if (_gif is not null)
        {
            var frame = ReadFrame(out int w, out int h);
            var small = Downscale(frame, w, h, _gifFactor, out int dw, out int dh);
            _gif.AddFrame(small, dw, dh);
        }
        else
        {
            SavePng(Path.Combine(_recordDir, $"frame_{_recordFrame:D5}.png"));
        }

        // Audio for this frame's interval: exactly 1/fps of music, so the WAV matches the video.
        AdvanceAudio(recorded: true);

        _recordFrame++;

        if (_recordFrame >= _recordTotal)
            FinishRecording();
    }

    private void AdvanceAudio(bool recorded)
    {
        if (_player is null)
            return;

        int remaining = Math.Max(1, 48000 / _recordFps);
        while (remaining > 0)
        {
            int chunk = Math.Min(2048, remaining);
            _player.Render(_render, chunk);
            if (recorded)
                _wav?.Write(_render, chunk);
            remaining -= chunk;
        }

        UpdateSpectrum();
        UpdateMeters(1f / _recordFps);
        UpdateNotes();
    }

    private void FinishRecording()
    {
        _wav?.Dispose();
        _wav = null;

        if (_gif is not null)
        {
            _gif.Save();
            _gif.Dispose();
            Console.WriteLine($"[Visualizer] GIF written: {_recordPath} ({_recordFrame} frames)");
            Exit();
            return;
        }

        Console.WriteLine($"[Visualizer] {_recordFrame} frames in {_recordDir}");

        if (_recordPath is not null && TryEncode(_recordPath))
            Console.WriteLine($"[Visualizer] recording written: {_recordPath}");
        else if (_wavPath is not null)
            Console.WriteLine($"[Visualizer] ffmpeg not found: frames in {_recordDir} + audio in {_wavPath}\n" +
                              $"              ffmpeg -framerate {_recordFps} -i \"{Path.Combine(_recordDir, "frame_%05d.png")}\" " +
                              $"-i \"{_wavPath}\" -c:v libx264 -pix_fmt yuv420p -crf 20 -c:a aac -b:a 192k -shortest \"{_recordPath}\"");
        else
            Console.WriteLine("[Visualizer] ffmpeg not found: use the PNG sequence above to build the video");

        Exit();
    }

    private bool TryEncode(string output)
    {
        string input = Path.Combine(_recordDir, "frame_%05d.png");
        string audio = string.Empty;
        if (_wavPath is not null && File.Exists(_wavPath))
            audio = $"-i \"{_wavPath}\" -c:a aac -b:a 192k -shortest ";

        string ext = Path.GetExtension(output).ToLowerInvariant();
        string args = ext switch
        {
            ".gif" =>
                $"-y -framerate {_recordFps} -i \"{input}\" -vf \"fps={Math.Min(30, _recordFps)},scale=960:-1:flags=lanczos,split[a][b];[a]palettegen[p];[b][p]paletteuse\" -loop 0 \"{output}\"",
            _ =>
                $"-y -framerate {_recordFps} -i \"{input}\" {audio}-c:v libx264 -pix_fmt yuv420p -crf 20 \"{output}\"",
        };

        try
        {
            var info = new ProcessStartInfo("ffmpeg", args)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            using var process = Process.Start(info);
            if (process is null)
                return false;

            process.WaitForExit(120_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
