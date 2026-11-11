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
    private bool _showRoll;
    private const double RollSeconds = 12.0;

    private static readonly Color[] ChannelColors =
    {
        new(79, 195, 247), new(41, 182, 246), new(129, 212, 250),
        new(126, 240, 130), new(76, 217, 100), new(0, 200, 83),
        new(168, 230, 163), new(0, 150, 136),
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
        int windowWidth = VW, int windowHeight = VH, AudioProfile? profile = null, bool startRoll = false)
    {
        _profile = profile ?? AudioProfile.Stereo;
        _showRoll = startRoll;
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

            if (!_paused)
            {
                if (_player.Finished && _autoNext && _tracks.Count > 0)
                    LoadTrack((_selected + 1) % _tracks.Count);

                Feed();
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

    private void Feed()
    {
        if (_sound is null || _player is null)
            return;

        int guard = 0;
        while (_sound.PendingBufferCount < 3 && guard++ < 4)
        {
            _player.Render(_render, ChunkFrames);

            for (int i = 0; i < _render.Length; i++)
            {
                short v = (short)Math.Clamp((int)Math.Round(_render[i] * 32767f), short.MinValue, short.MaxValue);
                _pcm[i * 2] = (byte)(v & 0xFF);
                _pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }

            try { _sound.SubmitBuffer(_pcm); }
            catch (InvalidOperationException) { break; }
        }
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
        {
            _showRoll = !_showRoll;
            _status = _showRoll ? "PIANO ROLL" : "SPECTRUM";
            _statusTimer = 2f;
        }

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
            if (_paused) _sound.Pause(); else _sound.Resume();
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

        // Per-channel S (solo) and M (mute) buttons, then the label (solo too).
        for (int ch = 0; ch < 8; ch++)
        {
            if (_soloHit[ch].Contains(p))
            {
                _player.SoloChannel = _player.SoloChannel == ch ? null : ch;
                _status = _player.SoloChannel is int s ? $"SOLO {ChannelName(s)}" : "SOLO OFF";
                _statusTimer = 2f;
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
                _player.SoloChannel = _player.SoloChannel == ch ? null : ch;
                _status = _player.SoloChannel is int s ? $"SOLO {ChannelName(s)}" : "SOLO OFF";
                _statusTimer = 2f;
                return;
            }
        }
    }

    private static string ChannelName(int ch) => ch < 3 ? $"PSG{ch + 1}" : $"SCC{ch - 2}";

    private void CycleProfile()
    {
        if (_player is null)
            return;

        var profiles = AudioProfile.All;
        int current = Array.FindIndex(profiles, p => p.Name == _player.Profile.Name);
        var next = profiles[(current + 1 + profiles.Length) % profiles.Length];
        double time = _player.Time;

        var vgm = _player.Song;
        _player.Dispose();
        _player = new VgmPlayer(vgm, loop: true, sampleRate: 48000, oversample: next.Oversample, profile: next);
        _player.Seek(time);
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

            _bars[b] = Math.Max(value, _bars[b] * 0.86f);
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
            if (_showRoll)
                DrawRoll();
            else
                DrawSpectrum();
            DrawScope();
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

        int left = x + 10, top = y + 22, innerW = w - 20, innerH = h - 32;

        for (int i = 1; i < 4; i++)
            Rect(left, top + innerH * i / 4, innerW, 1, Grid);

        float barW = innerW / (float)SpectrumBars;
        for (int b = 0; b < SpectrumBars; b++)
        {
            float value = Math.Clamp(_bars[b], 0f, 1f);
            int barH = (int)(value * innerH);
            if (barH <= 0) continue;

            int bx = left + (int)(b * barW);
            int bw = Math.Max(1, (int)barW - 1);
            var color = Color.Lerp(PsgColor, SccColor, b / (float)SpectrumBars);
            Rect(bx, top + innerH - barH, bw, barH, color);
        }
    }

    /// <summary>Scrolling piano roll: time to the right, pitch up, one colour per channel.</summary>
    private void DrawRoll()
    {
        const int x = 336, y = 104, w = VW - 348, h = 182;
        Panel(x, y, w, h);
        Text("PIANO ROLL", x + 10, y + 6, Accent);
        Text("TAB: SPECTRUM", x + 120, y + 6, Dim);

        int left = x + 10, top = y + 22, innerW = w - 20, innerH = h - 32;
        const int lowMidi = 21, highMidi = 108;

        int PitchY(int midi) => top + innerH - (midi - lowMidi) * innerH / (highMidi - lowMidi);

        // Octave lines (C2..C8).
        for (int midi = 24; midi <= highMidi; midi += 12)
        {
            int py = PitchY(midi);
            if (py < top) break;
            Rect(left, py, innerW, 1, Grid);
            Text(Notes[midi % 12] + (midi / 12 - 1), x + w - 26, py - 3, Dim);
        }

        if (_player is null)
            return;

        double now = _player.Time;

        for (int ch = 0; ch < 8; ch++)
        {
            var color = ChannelColors[ch];
            var list = _notes[ch];

            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (!e.Key || e.Frequency < 20)
                    continue;

                double t0 = e.Time;
                double t1 = i + 1 < list.Count ? list[i + 1].Time : now;
                if (t1 <= t0)
                    continue;

                int midi = (int)Math.Round(69 + 12 * Math.Log2(e.Frequency / 440.0));
                int py = PitchY(Math.Clamp(midi, lowMidi, highMidi));

                int px0 = left + (int)(innerW * (1 - (now - t0) / RollSeconds));
                int px1 = left + (int)(innerW * (1 - (now - t1) / RollSeconds));
                if (px1 < left || px0 > left + innerW)
                    continue;

                px0 = Math.Clamp(px0, left, left + innerW - 1);
                px1 = Math.Clamp(px1, left, left + innerW);

                int thickness = 2 + (int)Math.Round(Math.Clamp(e.Volume, 0f, 1f) * 3f);
                Rect(px0, py, Math.Max(1, px1 - px0), thickness, color);
            }
        }

        // "Now" line at the right edge.
        Rect(left + innerW - 1, top, 1, innerH, Accent);
    }

    private void DrawScope()    {
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
        Text("CLICK: TRACK / SEEK BAR / LABEL = SOLO   S = SOLO   M = MUTE", x + 110, y + 6, Dim);

        int stripW = (w - 20) / 8;
        int stripY = y + 24;
        int stripH = h - 34;

        for (int ch = 0; ch < 8; ch++)
        {
            var info = _player!.Channels[ch];
            bool isPsg = ch < 3;
            bool chipOn = isPsg ? _player.PsgEnabled : _player.SccEnabled;
            bool muted = _player.ChannelMuted[ch];
            bool soloed = _player.SoloChannel == ch;
            bool audible = !muted && chipOn && (_player.SoloChannel is null || soloed);

            var color = isPsg ? PsgColor : SccColor;
            if (!audible) color = Dim;

            int sx = x + 10 + ch * stripW;
            _stripHit[ch] = new Rectangle(sx + 2, stripY, stripW - 4, 20);

            Panel(sx + 2, stripY, stripW - 4, stripH, PanelHi);

            Text(isPsg ? $"PSG{info.Number}" : $"SCC{info.Number}", sx + 8, stripY + 6, color);

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

                int stepX = Math.Max(1, scopeW / 32);
                for (int i = 0; i < 32; i++)
                {
                    int px = scopeX + i * stepX;
                    int py = waveY + waveH / 2 - (int)(_wave[i] * waveH / 2);
                    py = Math.Clamp(py, waveY, waveY + waveH - 1);
                    Rect(px, Math.Min(py, waveY + waveH / 2), Math.Max(1, stepX - 1),
                        Math.Max(1, Math.Abs(py - (waveY + waveH / 2))), color);
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

    private void DrawButton(Rectangle rect, string label, bool active, Color activeColor)
    {
        Rect(rect, active ? activeColor : Grid);
        Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2, active ? PanelHi : PanelColor);
        Text(label, rect.X + rect.Width / 2 - 2, rect.Y + 3, active ? activeColor : Dim);
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

        Text("UP/DOWN TRACK   ENTER PLAY   SPACE PAUSE   LEFT/RIGHT SEEK   CLICK: TRACK / SEEK BAR / S-M", 16, VH - 36, Dim);
        Text("P PSG   S SCC   1-8 CHANNEL   O PROFILE   L AUTONEXT   R RESTART   H HELP   ESC QUIT", 16, VH - 22, Dim);

        if (_statusTimer > 0)
        {
            int w = PixelFont.Measure(_status, 2);
            Text(_status, VW - 16 - w, VH - 82, Accent, 2);
        }
    }

    private void DrawHelp()
    {
        const int w = 640, h = 286;
        int x = (VW - w) / 2, y = (VH - h) / 2;
        Panel(x, y, w, h, PanelHi);
        Rect(x, y, w, 24, PanelColor);
        Text("HELP", x + 10, y + 8, Accent);

        string[] lines =
        {
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
            "DRAG A .VGM FILE INTO THE WINDOW TO PLAY IT",
            "MSXAUDIOVISUALIZER SONG.VGM --RECORD OUT.GIF --SECONDS 10",
        };

        for (int i = 0; i < lines.Length; i++)
            Text(lines[i], x + 16, y + 36 + i * 14, i >= lines.Length - 2 ? Dim : TextColor);
    }

    // -- Capture / recording -------------------------------------------------------

    private int _frame;
    private bool _shotDone;

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
