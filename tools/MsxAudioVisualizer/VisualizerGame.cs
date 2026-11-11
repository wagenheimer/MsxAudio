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
public sealed partial class VisualizerGame : Game
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

    private readonly List<VgmEntry> _tracks = new();
    private int _selected;
    private readonly VisualizerSettings _settings;

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
    private float _volume = 1f;
    private bool _loopEnabled = true;
    private AudioProfile _profile;

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
    private const int SongMapBuckets = 192;

    // Whole-song density map (8 x buckets) and its clickable area (design coordinates).
    private float[][]? _songMap;
    private Rectangle _songMapHit;

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
        int windowWidth = 0, int windowHeight = 0, AudioProfile? profile = null, string? startView = null,
        string? startFolder = null, bool? recursive = null)
    {
        _settings = VisualizerSettings.Load();
        if (recursive is not null)
            _settings.Recursive = recursive.Value;

        _volume = Math.Clamp(_settings.Volume, 0f, 1f);
        _loopEnabled = _settings.Loop;

        // CLI wins; otherwise use what the user had last time.
        _profile = profile ?? AudioProfile.ByName(_settings.Profile);

        // A screenshot gate must not depend on the audio device pacing the buffers: one update per
        // drawn frame, each advancing a fixed 1/60 s (see AdvanceForScreenshot).
        if (_shotPending)
            IsFixedTimeStep = false;

        string view = startView ?? _settings.View;
        _view = view.ToLowerInvariant() switch
        {
            "roll" or "pianoroll" => PanelView.Roll,
            "piano" or "keys" => PanelView.Piano,
            _ => PanelView.Spectrum,
        };

        int width = windowWidth > 0 ? windowWidth : _settings.WindowWidth;
        int height = windowHeight > 0 ? windowHeight : _settings.WindowHeight;

        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Math.Max(640, width),
            PreferredBackBufferHeight = Math.Max(360, height),
            SynchronizeWithVerticalRetrace = true,
        };

        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        Window.Title = "MSX Audio Visualizer — PSG + SCC";

        _startFile = file;

        // No positional argument: reopen the last folder when it still exists.
        string? startDir = null;
        if (file is not null)
            startDir = Path.GetDirectoryName(Path.GetFullPath(file));
        else if (!string.IsNullOrWhiteSpace(startFolder))
            startDir = startFolder;
        else if (!string.IsNullOrWhiteSpace(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
            startDir = _settings.LastFolder;

        _startDir = startDir ?? Directory.GetCurrentDirectory();

        _recordPath = recordPath;
        _recordSeconds = Math.Max(1, recordSeconds);
        _recordFps = Math.Clamp(recordFps, 5, 60);
    }

    protected override void Initialize()
    {
        base.Initialize();

        Window.FileDrop += (_, e) =>
        {
            if (e.Files.Length == 0)
                return;

            string dropped = e.Files[0];
            if (Directory.Exists(dropped))
                SetFolder(dropped);
            else
                LoadFile(dropped);
        };

        Window.ClientSizeChanged += (_, _) => ApplyWindowSize();

        Exiting += (_, _) => SaveSettings();

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

        if (_startFile is null &&
            !string.Equals(_settings.LastFolder, _startDir, StringComparison.OrdinalIgnoreCase))
        {
            _settings.LastFolder = _startDir;
            _settings.AddRecent(_startDir);
            _settings.Save();
        }

        ScanTracks(_startDir, _settings.Recursive, _startFile);

        if (_startFile is not null && File.Exists(_startFile))
            LoadFile(_startFile);

        // Docs/CI: open a dialog at startup (input is ignored in screenshot runs).
        string? dialog = Environment.GetEnvironmentVariable("MSXAUDIO_DIALOG");
        if (!string.IsNullOrWhiteSpace(dialog))
        {
            if (dialog.Equals("folder", StringComparison.OrdinalIgnoreCase))
                OpenFolderDialog();
            else if (dialog.Equals("profile", StringComparison.OrdinalIgnoreCase))
                OpenProfileDialog();
            else if (dialog.Equals("help", StringComparison.OrdinalIgnoreCase))
                _showHelp = true;
        }

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


    protected override void Update(GameTime gameTime)
    {
        _prevKeys = _keys;
        _keys = Keyboard.GetState();
        _prevMouse = _mouse;
        _mouse = Mouse.GetState();

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_statusTimer > 0) _statusTimer -= dt;

        // A screenshot run must be reproducible: ignore keyboard and mouse (a stray Tab or click
        // during the few frames of the gate would change the view or the track).
        if (!_shotPending)
            HandleInput();

        PollScan();

        if (_dialog != DialogKind.None)
            PollDialogScan();

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


    private void CycleProfile()
    {
        var profiles = AudioProfile.All;
        int current = Array.FindIndex(profiles, p => p.Name == _profile.Name);
        var next = profiles[(current + 1 + profiles.Length) % profiles.Length];
        ApplyProfile(next);
    }

    private void FlushAudio()
    {
        if (_sound is null)
            return;

        _sound.Stop();
        _sound.Play();
    }

    /// <summary>Remembers the window size, view and profile for the next run.</summary>
    private void SaveSettings()
    {
        _settings.Profile = _profile.Name;
        _settings.View = _view.ToString().ToLowerInvariant();
        _settings.Volume = _volume;
        _settings.Loop = _loopEnabled;

        try
        {
            _settings.WindowWidth = Math.Max(640, Window.ClientBounds.Width);
            _settings.WindowHeight = Math.Max(360, Window.ClientBounds.Height);
        }
        catch (Exception)
        {
            // window already gone: keep the previous size
        }

        _settings.Save();
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

        DrawDialog();

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


    private static readonly double[] FrequencyMarks = { 100, 1000, 10000 };


    // Clickable "next view" toggle in the panel header.
    private Rectangle _viewToggleHit;


    private static readonly string[] Notes = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };


    // -- Capture / recording -------------------------------------------------------

    private int _frame;
    private bool _shotDone;
    private readonly bool _shotPending = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSXAUDIO_SHOT"));


}
