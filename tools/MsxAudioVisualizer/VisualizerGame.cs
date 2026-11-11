using System;
using System.Collections.Generic;
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
/// the master waveform and the spectrum.
/// </summary>
public sealed class VisualizerGame : Game
{
    private const int Width = 1280;
    private const int Height = 720;
    private const int ChunkFrames = 1024;
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

    private SpriteBatch _batch = null!;
    private Texture2D _pixel = null!;
    private SpriteFont? _font;

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

    private KeyboardState _keys;
    private KeyboardState _prevKeys;
    private bool _paused;
    private bool _autoNext = true;
    private bool _showHelp;
    private string _status = string.Empty;
    private float _statusTimer;

    public VisualizerGame(string? file)
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Width,
            PreferredBackBufferHeight = Height,
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
    }

    protected override void Initialize()
    {
        base.Initialize();

        Window.FileDrop += (_, e) =>
        {
            if (e.Files.Length > 0)
                LoadFile(e.Files[0]);
        };

        ScanTracks(_startDir);

        if (_startFile is not null && File.Exists(_startFile))
            LoadFile(_startFile);
        else if (_tracks.Count > 0)
            LoadTrack(0);
    }

    protected override void LoadContent()
    {
        _batch = new SpriteBatch(GraphicsDevice);
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Color.White });
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

            _player = new VgmPlayer(vgm, loop: true, sampleRate: 48000, oversample: 4, AudioProfile.Stereo);
            _sound = new DynamicSoundEffectInstance(48000, AudioChannels.Stereo);
            _sound.Play();
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
            _statusTimer = 3f;
            Array.Clear(_bars);
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

        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_statusTimer > 0) _statusTimer -= dt;

        HandleInput();

        if (_player is not null && _sound is not null && !_paused)
        {
            if (_player.Finished && _autoNext && _tracks.Count > 0)
                LoadTrack((_selected + 1) % _tracks.Count);

            Feed();
        }

        UpdateSpectrum();
        base.Update(gameTime);
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

    private void HandleInput()
    {
        if (Pressed(Keys.Escape))
            Exit();

        if (Pressed(Keys.H))
            _showHelp = !_showHelp;

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
            FlushAudio();
        }

        if (Pressed(Keys.Left) && _player is not null)
        {
            _player.Seek(Math.Max(0, _player.Time - 5));
            FlushAudio();
        }

        if (Pressed(Keys.Right) && _player is not null)
        {
            _player.Seek(Math.Min(_player.TotalSeconds, _player.Time + 5));
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
        _player.Dispose();
        _player = new VgmPlayer(vgm, loop: true, sampleRate: 48000, oversample: next.Oversample, profile: next);
        _player.Seek(time);
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

        // Log-spaced bars (bass on the left).
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
        GraphicsDevice.Clear(Background);
        _batch.Begin(samplerState: SamplerState.PointClamp);

        DrawHeader();
        DrawPlaylist();

        if (_player is not null)
        {
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
        base.Draw(gameTime);
        TryScreenshot();
    }

    // ── Gate de screenshot (docs/CI): MSXAUDIO_SHOT=<png> + MSXAUDIO_SHOT_FRAME=<n> ───────────
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
        try
        {
            var data = new Color[GraphicsDevice.PresentationParameters.BackBufferWidth *
                                 GraphicsDevice.PresentationParameters.BackBufferHeight];
            GraphicsDevice.GetBackBufferData(data);

            using var texture = new Texture2D(GraphicsDevice,
                GraphicsDevice.PresentationParameters.BackBufferWidth,
                GraphicsDevice.PresentationParameters.BackBufferHeight);
            texture.SetData(data);
            using var stream = File.Create(path);
            texture.SaveAsPng(stream, texture.Width, texture.Height);
            Console.WriteLine($"[Visualizer] screenshot: {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Visualizer] screenshot falhou: {ex.Message}");
        }

        Exit();
    }

    private void Rect(int x, int y, int w, int h, Color color) =>
        _batch.Draw(_pixel, new Rectangle(x, y, w, h), color);

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
        Rect(0, 0, Width, 46, PanelHi);
        Rect(0, 45, Width, 1, Grid);

        Text("MSX AUDIO VISUALIZER", 16, 12, Accent, 2);
        Text("PSG AY-3-8910 + KONAMI SCC", 16, 30, Dim);

        if (_player is not null)
        {
            string name = Path.GetFileNameWithoutExtension(_player.Name).ToUpperInvariant();
            Text(name, 360, 16, TextColor, 2);

            string time = $"{FormatTime(_player.Time)} / {FormatTime(_player.TotalSeconds)}";
            Text(time, Width - 16 - PixelFont.Measure(time), 12, TextColor, 2);

            string info = $"PSG {(int)(_player.Song.AyClock / 1000)}KHZ  SCC {(int)(1789772.5 / 1000)}KHZ  OUT {(int)(48000 / 1000)}KHZ  X{_player.Oversample}  {_player.Profile.Name.ToUpperInvariant()}";
            Text(info, Width - 16 - PixelFont.Measure(info), 30, Dim);
        }
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int total = (int)seconds;
        return $"{total / 60:00}:{total % 60:00}";
    }

    private void DrawPlaylist()
    {
        const int x = 12, y = 58, w = 316, h = 600;
        Panel(x, y, w, h);

        Text("TRACKS", x + 10, y + 8, Accent);
        Rect(x + 10, y + 24, w - 20, 1, Grid);

        int rows = (h - 40) / 18;
        int first = Math.Max(0, Math.Min(_selected - rows / 2, Math.Max(0, _tracks.Count - rows)));

        for (int i = 0; i < rows && first + i < _tracks.Count; i++)
        {
            int index = first + i;
            int rowY = y + 30 + i * 18;
            bool active = index == _selected;

            if (active)
                Rect(x + 6, rowY - 4, w - 12, 17, PanelHi);

            string name = Path.GetFileNameWithoutExtension(_tracks[index]).ToUpperInvariant();
            if (name.Length > 30) name = name[..30];
            Text(name, x + 12, rowY, active ? Accent : (index == _selected ? TextColor : Dim));
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
        Panel(336, 58, Width - 348, 600);
        Text("DROP A .VGM FILE OR PASS A FOLDER AS THE FIRST ARGUMENT", 368, 330, Dim);
        Text("MSXAUDIOVISUALIZER <FILE.VGM | FOLDER>", 368, 350, Dim);
    }

    private void DrawSpectrum()
    {
        const int x = 336, y = 58, w = Width - 348, h = 196;
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

    private void DrawScope()
    {
        const int x = 336, y = 262, w = Width - 348, h = 150;
        Panel(x, y, w, h);
        Text("MASTER", x + 10, y + 6, Accent);

        int left = x + 10, top = y + 22, innerW = w - 20, innerH = h - 32;
        Rect(left, top + innerH / 2, innerW, 1, Grid);

        var scope = _player!.Scope;
        int pos = _player.ScopePos;
        int points = Math.Min(innerW, scope.Length);

        int prevY = -1;
        for (int i = 0; i < points; i++)
        {
            float sample = scope[(pos + scope.Length - points + i) % scope.Length];
            int py = top + innerH / 2 - (int)(sample * innerH * 3.0f);
            py = Math.Clamp(py, top, top + innerH - 1);

            if (prevY >= 0)
            {
                int from = Math.Min(prevY, py);
                int to = Math.Max(prevY, py);
                Rect(left + i, from, 1, Math.Max(1, to - from), PsgColor);
            }

            prevY = py;
        }
    }

    private void DrawChannels()
    {
        const int x = 336, y = 420, w = Width - 348, h = 238;
        Panel(x, y, w, h);
        Text("CHANNELS", x + 10, y + 6, Accent);

        int stripW = (w - 20) / 8;
        int stripY = y + 24;
        int stripH = h - 34;

        for (int ch = 0; ch < 8; ch++)
        {
            var info = _player!.Channels[ch];
            bool isPsg = ch < 3;
            bool chipOn = isPsg ? _player.PsgEnabled : _player.SccEnabled;
            bool muted = _player.ChannelMuted[ch];
            var color = isPsg ? PsgColor : SccColor;
            if (muted || !chipOn) color = Dim;

            int sx = x + 10 + ch * stripW;
            Panel(sx + 2, stripY, stripW - 4, stripH, PanelHi);

            Text(isPsg ? $"PSG{info.Number}" : $"SCC{info.Number}", sx + 8, stripY + 6, color);

            if (muted || !chipOn)
                Text("OFF", sx + stripW - 34, stripY + 6, Warn);

            // Mini scope.
            int scopeX = sx + 8, scopeY = stripY + 22, scopeW = stripW - 16, scopeH = 70;
            Rect(scopeX, scopeY + scopeH / 2, scopeW, 1, Grid);

            var ring = _player.ChannelScope(ch);
            int ringPos = _player.ChannelScopePos;
            int points = Math.Min(scopeW, ring.Length);
            int prevY = -1;

            for (int i = 0; i < points; i++)
            {
                float sample = ring[(ringPos + ring.Length - points + i) % ring.Length];
                int py = scopeY + scopeH / 2 - (int)(sample * scopeH * 2.0f);
                py = Math.Clamp(py, scopeY, scopeY + scopeH - 1);
                if (prevY >= 0)
                    Rect(scopeX + i, Math.Min(prevY, py), 1, Math.Max(1, Math.Abs(py - prevY)), color);
                prevY = py;
            }

            // Volume bar.
            int volX = sx + stripW - 16, volY = stripY + 22, volH = 70;
            Rect(volX, volY, 6, volH, Grid);
            int vol = (int)(info.Volume * volH);
            Rect(volX, volY + volH - vol, 6, vol, color);

            // Level bar.
            int levelY = stripY + 100, levelW = stripW - 16;
            Rect(scopeX, levelY, levelW, 5, Grid);
            Rect(scopeX, levelY, (int)(Math.Clamp(info.Level * 3f, 0, 1) * levelW), 5, color);

            // Frequency / note.
            string freq = info.Frequency >= 1000
                ? $"{info.Frequency / 1000:0.0}K"
                : $"{info.Frequency:0}HZ";
            Text(freq, sx + 8, stripY + 112, info.Key ? TextColor : Dim);

            string note = NoteName(info.Frequency);
            Text(note, sx + 8, stripY + 126, info.Key ? Accent : Dim);

            string key = info.Key ? "KEY" : "---";
            Text(key, sx + 8, stripY + 140, info.Key ? color : Dim);
        }
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
        Rect(0, Height - 40, Width, 40, PanelHi);
        Rect(0, Height - 40, Width, 1, Grid);

        var p = _player;
        string chipState = p is null ? "" : $"PSG[{(p.PsgEnabled ? "ON" : "OFF")}] SCC[{(p.SccEnabled ? "ON" : "OFF")}]";
        Text(chipState, 16, Height - 26, TextColor);

        string help = "UP/DOWN TRACK  ENTER PLAY  SPACE PAUSE  LEFT/RIGHT SEEK  P PSG  S SCC  1-8 CHANNEL  O PROFILE  L AUTONEXT  R RESTART  H HELP  ESC QUIT";
        Text(help, 16, Height - 12, Dim);

        if (_statusTimer > 0)
        {
            int w = PixelFont.Measure(_status, 2);
            Text(_status, Width - 16 - w, Height - 66, Accent, 2);
        }
    }

    private void DrawHelp()
    {
        const int w = 560, h = 250;
        int x = (Width - w) / 2, y = (Height - h) / 2;
        Panel(x, y, w, h, PanelHi);
        Rect(x, y, w, 24, PanelColor);
        Text("HELP", x + 10, y + 8, Accent);

        string[] lines =
        {
            "UP / DOWN      SELECT TRACK",
            "ENTER          PLAY SELECTED",
            "SPACE          PAUSE / RESUME",
            "LEFT / RIGHT   SEEK -/+ 5 S",
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
        };

        for (int i = 0; i < lines.Length; i++)
            Text(lines[i], x + 16, y + 36 + i * 14, i == lines.Length - 1 ? Dim : TextColor);
    }
}
