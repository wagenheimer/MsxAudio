using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Wagenheimer.MsxAudio.Extraction;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// "Extract from ROM" dialog: boots a cartridge on the in-process MSX machine and records whatever
/// its own sound driver plays into a VGM — the generic path that works for any ROM, without a
/// per-game profile.
/// </summary>
public sealed partial class VisualizerGame : Game
{
    /// <summary>Mapper choices offered by the dialog (label + value).</summary>
    private static readonly (string Label, RomMapper Mapper)[] ExtractMappers =
    {
        ("AUTO", RomMapper.Auto),
        ("LINEAR", RomMapper.Linear),
        ("KONAMI", RomMapper.Konami),
        ("KONAMI SCC", RomMapper.KonamiScc),
        ("ASCII8", RomMapper.Ascii8),
        ("ASCII16", RomMapper.Ascii16),
    };

    // ── State ────────────────────────────────────────────────────────────────────

    private string _extractRom = string.Empty;
    private int _extractMapper;
    private string _extractKeys = "START@1";
    private int _extractSeconds = 30;
    private int _extractField = 1;            // 0 = ROM path, 1 = key script

    private bool _extractRunning;
    private CancellationTokenSource? _extractCts;
    private Task<RomCaptureResult?>? _extractTask;
    private readonly List<string> _extractLog = new();
    private readonly object _extractLogLock = new();
    private volatile int _extractFrame;
    private volatile int _extractTotalFrames = 1;
    private string? _extractError;
    private string? _extractOutput;

    // Clickable areas (design coordinates, filled by DrawExtractDialog).
    private Rectangle _exRomHit, _exBrowseHit, _exMapperHit, _exKeysHit;
    private Rectangle _exMinusHit, _exPlusHit, _exStartHit, _exCloseHit;
    private Rectangle _extractButtonHit;      // header chip

    /// <summary>Opens the extraction dialog (F3 or the header chip).</summary>
    private void OpenExtractDialog()
    {
        _dialog = DialogKind.Extract;

        if (_extractRom.Length == 0 && _settings.LastRom is { } last)
            _extractRom = last;

        _extractKeys = _settings.LastRomKeys is { Length: > 0 } keys ? keys : "START@1";
        _extractMapper = Math.Clamp(_settings.LastRomMapper, 0, ExtractMappers.Length - 1);
        _extractSeconds = Math.Clamp(_settings.LastRomSeconds, 5, 600);

        // Docs/CI: pre-fill the dialog without touching the user's saved settings.
        if (Environment.GetEnvironmentVariable("MSXAUDIO_ROM") is { Length: > 0 } rom)
            _extractRom = rom;

        if (Environment.GetEnvironmentVariable("MSXAUDIO_ROM_KEYS") is { Length: > 0 } script)
            _extractKeys = script;

        if (int.TryParse(Environment.GetEnvironmentVariable("MSXAUDIO_ROM_SECONDS"), out int seconds))
            _extractSeconds = Math.Clamp(seconds, 5, 600);

        _extractField = _extractRom.Length == 0 ? 0 : 1;
        _extractError = null;
    }

    /// <summary>The folder browser hands a picked cartridge back here.</summary>
    private void PickRom(string path)
    {
        _extractRom = path;
        _dialog = DialogKind.Extract;
        _extractField = 1;
        _extractError = null;

        _settings.LastRom = path;
        _settings.Save();
    }

    /// <summary>Where the capture is written: next to the ROM, <c>&lt;rom&gt;_capture.vgm</c>.</summary>
    private string DefaultOutput()
    {
        if (_extractRom.Length == 0)
            return string.Empty;

        string folder = Path.GetDirectoryName(Path.GetFullPath(_extractRom)) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(_extractRom);
        return Path.Combine(folder, name + "_capture.vgm");
    }

    // ── Run ──────────────────────────────────────────────────────────────────────

    private void StartExtraction()
    {
        if (_extractRunning)
        {
            StopExtraction();
            return;
        }

        if (!File.Exists(_extractRom))
        {
            _extractError = "CHOOSE A .ROM FIRST";
            return;
        }

        RomMapper mapper = ExtractMappers[_extractMapper].Mapper;
        var options = new RomCaptureOptions
        {
            RomPath = _extractRom,
            Mapper = mapper,
            Seconds = _extractSeconds,
        };

        options.Keys.AddRange(KeyPress.ParseScript(_extractKeys));

        _extractOutput = DefaultOutput();
        _extractError = null;
        lock (_extractLogLock)
            _extractLog.Clear();

        _extractFrame = 0;
        _extractTotalFrames = Math.Max(1, _extractSeconds * 60);
        _extractRunning = true;

        _settings.LastRom = _extractRom;
        _settings.LastRomKeys = _extractKeys;
        _settings.LastRomMapper = _extractMapper;
        _settings.LastRomSeconds = _extractSeconds;
        _settings.Save();

        var cts = new CancellationTokenSource();
        _extractCts = cts;
        CancellationToken token = cts.Token;

        _extractTask = Task.Run(() =>
        {
            try
            {
                return RomCapture.Run(
                    options,
                    log: AddExtractLog,
                    progress: (frame, total) =>
                    {
                        _extractFrame = frame;
                        _extractTotalFrames = Math.Max(1, total);
                    },
                    token: token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                _extractError = ex.Message.ToUpperInvariant();
                return null;
            }
        }, token);
    }

    private void StopExtraction()
    {
        _extractCts?.Cancel();
        AddExtractLog("cancelling...");
    }

    private void AddExtractLog(string message)
    {
        lock (_extractLogLock)
        {
            _extractLog.Add(message);

            // The dialog shows the tail; keep the list bounded for long captures.
            if (_extractLog.Count > 400)
                _extractLog.RemoveRange(0, _extractLog.Count - 400);
        }
    }

    /// <summary>Called from Update: finishes a background capture and loads the result.</summary>
    private void PollExtraction()
    {
        if (_extractTask is not { IsCompleted: true })
            return;

        Task<RomCaptureResult?> task = _extractTask;
        _extractTask = null;
        _extractRunning = false;

        _extractCts?.Dispose();
        _extractCts = null;

        if (task.Status == TaskStatus.RanToCompletion && task.Result is { } result)
        {
            try
            {
                File.WriteAllBytes(_extractOutput!, VgmWriter.Write(result.Vgm));
                AddExtractLog($"saved {_extractOutput} ({result.NoteWrites} pitch writes)");

                // Silence means the game never left its title screen: keep the dialog (and the log) up
                // so the user can read why, instead of dropping them into a VGM that plays nothing.
                if (result.NoteWrites < 20)
                {
                    _extractError = "NO MUSIC CAPTURED — TRY A LONGER CAPTURE OR ADD KEYS";
                    return;
                }

                string output = _extractOutput!;
                CloseDialog();

                string folder = Path.GetDirectoryName(output) ?? Directory.GetCurrentDirectory();
                ScanTracks(folder, false, output);
                LoadFile(output, rescan: false);

                _status = $"EXTRACTED {result.Vgm.Events.Count} WRITES";
                _statusTimer = 4f;
            }
            catch (Exception ex)
            {
                _extractError = "SAVE FAILED: " + ex.Message.ToUpperInvariant();
            }

            return;
        }

        if (_extractError is null)
            _extractError = "CANCELLED";
    }

    /// <summary>Text typed into the dialog (wired to <c>Window.TextInput</c> in Initialize).</summary>
    private void OnExtractTextInput(char character)
    {
        if (_dialog != DialogKind.Extract || _extractRunning || char.IsControl(character))
            return;

        if (_extractField == 0)
            _extractRom += character;
        else
            _extractKeys += character;
    }

    // ── Input ────────────────────────────────────────────────────────────────────

    private void HandleExtractDialogInput()
    {
        if (Pressed(Keys.Escape))
        {
            if (_extractRunning)
                StopExtraction();
            else
                CloseDialog();

            return;
        }

        if (Pressed(Keys.Enter) && !_extractRunning)
        {
            StartExtraction();
            return;
        }

        if (Pressed(Keys.Tab))
        {
            _extractField = _extractField == 0 ? 1 : 0;
            return;
        }

        if (Pressed(Keys.Back) && !_extractRunning)
        {
            if (_extractField == 0 && _extractRom.Length > 0)
                _extractRom = _extractRom[..^1];
            else if (_extractField == 1 && _extractKeys.Length > 0)
                _extractKeys = _extractKeys[..^1];

            return;
        }

        if (Pressed(Keys.Delete) && !_extractRunning)
        {
            _extractRom = string.Empty;
            _extractField = 0;
            return;
        }

        bool pressed = _mouse.LeftButton == ButtonState.Pressed &&
                       _prevMouse.LeftButton == ButtonState.Released;

        if (!pressed)
            return;

        Point p = MouseDesign();

        if (_exCloseHit.Contains(p))
        {
            if (_extractRunning)
                StopExtraction();
            else
                CloseDialog();

            return;
        }

        if (_exRomHit.Contains(p))
        {
            _extractField = 0;
            return;
        }

        if (_exKeysHit.Contains(p))
        {
            _extractField = 1;
            return;
        }

        if (_exBrowseHit.Contains(p))
        {
            OpenRomBrowser();
            return;
        }

        if (_exMapperHit.Contains(p) && !_extractRunning)
        {
            _extractMapper = (_extractMapper + 1) % ExtractMappers.Length;
            return;
        }

        if ((_exMinusHit.Contains(p) || _exPlusHit.Contains(p)) && !_extractRunning)
        {
            int step = _keys.IsKeyDown(Keys.LeftShift) || _keys.IsKeyDown(Keys.RightShift) ? 60 : 5;
            _extractSeconds = Math.Clamp(_extractSeconds + (_exPlusHit.Contains(p) ? step : -step), 5, 600);
            return;
        }

        if (_exStartHit.Contains(p))
            StartExtraction();
    }

    // ── Draw ─────────────────────────────────────────────────────────────────────

    /// <summary>Truncates <paramref name="text"/> with an ellipsis so it fits in <paramref name="width"/>.</summary>
    private static string FitText(string text, int width)
    {
        if (width <= 0)
            return string.Empty;

        if (PixelFont.Measure(text) <= width)
            return text;

        while (text.Length > 1 && PixelFont.Measure(text + "…") > width)
            text = text[..^1];

        return text + "…";
    }

    private void DrawField(Rectangle rect, string text, bool focused, Color? textColor = null)
    {
        Rect(rect, Grid);
        Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2, PanelColor);
        Text(FitText(text, rect.Width - 12), rect.X + 6, rect.Y + 5, textColor ?? (focused ? Accent : TextColor));

        // Caret, so it is obvious which field receives typing.
        if (focused)
        {
            int caret = rect.X + 6 + Math.Min(PixelFont.Measure(text), rect.Width - 12);
            Rect(caret, rect.Y + 3, 1, rect.Height - 6, Accent);
        }
    }

    private void DrawExtractDialog()
    {
        const int dx = 230, dy = 120, dw = 820, dh = 470;
        Panel(dx, dy, dw, dh);
        Rect(dx, dy, dw, 24, PanelColor);
        Text("EXTRACT FROM ROM", dx + 12, dy + 8, Accent);

        _exCloseHit = new Rectangle(dx + dw - 24, dy + 4, 16, 16);
        DrawButton(_exCloseHit, "X", false, Warn);

        int labelX = dx + 16;
        int fieldX = dx + 92;
        int fieldW = dw - 92 - 116;

        // ROM.
        Text("ROM", labelX, dy + 44, Dim);
        _exRomHit = new Rectangle(fieldX, dy + 38, fieldW, 18);
        DrawField(_exRomHit, _extractRom.Length > 0 ? Path.GetFileName(_extractRom) : "(NO ROM CHOSEN)",
            _extractField == 0, _extractRom.Length > 0 ? null : Dim);

        _exBrowseHit = new Rectangle(fieldX + fieldW + 8, dy + 38, 84, 18);
        DrawButton(_exBrowseHit, "BROWSE", false, Accent);

        // Mapper + duration.
        Text("MAPPER", labelX, dy + 72, Dim);
        _exMapperHit = new Rectangle(fieldX, dy + 66, 150, 18);
        DrawButton(_exMapperHit, ExtractMappers[_extractMapper].Label, true, Accent);

        int secondsX = fieldX + 190;
        Text("SECONDS", secondsX, dy + 72, Dim);
        _exMinusHit = new Rectangle(secondsX + 70, dy + 66, 18, 18);
        DrawButton(_exMinusHit, "-", false, Dim);

        string seconds = _extractSeconds.ToString();
        Text(seconds, secondsX + 96 + (24 - PixelFont.Measure(seconds)) / 2, dy + 72, TextColor);

        _exPlusHit = new Rectangle(secondsX + 124, dy + 66, 18, 18);
        DrawButton(_exPlusHit, "+", false, Dim);

        // Key script.
        Text("KEYS", labelX, dy + 100, Dim);
        _exKeysHit = new Rectangle(fieldX, dy + 94, fieldW + 92, 18);
        DrawField(_exKeysHit, _extractKeys.Length > 0 ? _extractKeys : "(NONE)", _extractField == 1,
            _extractKeys.Length > 0 ? null : Dim);

        Text("NAME@SECOND (START, FIRE, UP, DOWN, LEFT, RIGHT) OR ROW:MASK@SECOND, E.G. START@1, RIGHT@4",
            fieldX, dy + 118, Dim);

        // Output path.
        string output = DefaultOutput();
        Text("OUTPUT", labelX, dy + 142, Dim);
        Text(FitText(output.Length > 0 ? output : "(CHOOSE A ROM)", dw - 120), fieldX, dy + 142, Dim);

        // Start / stop + progress.
        _exStartHit = new Rectangle(fieldX, dy + 168, 110, 20);
        DrawButton(_exStartHit, _extractRunning ? "STOP" : "EXTRACT", _extractRunning, _extractRunning ? Warn : Accent);

        int barX = _exStartHit.Right + 12;
        int barW = dx + dw - 16 - barX - 46;
        float filled = Math.Clamp(_extractFrame / (float)_extractTotalFrames, 0f, 1f);

        Rect(barX, dy + 174, barW, 8, Grid);
        Rect(barX, dy + 174, (int)(barW * filled), 8, _extractRunning ? PsgColor : Accent);

        string percent = $"{(int)(filled * 100)}%";
        Text(percent, barX + barW + 10, dy + 174, _extractRunning ? TextColor : Dim);

        if (_extractError is { } error)
            Text(FitText(error, dw - 190), barX, dy + 156, Warn);

        // Log tail.
        int logTop = dy + 200;
        int logBottom = dy + dh - 30;
        Rect(dx + 16, logTop - 4, dw - 32, 1, Grid);

        var lines = new List<string>();
        lock (_extractLogLock)
        {
            int max = Math.Max(1, (logBottom - logTop) / 12);
            int start = Math.Max(0, _extractLog.Count - max);
            for (int i = start; i < _extractLog.Count; i++)
                lines.Add(_extractLog[i]);
        }

        for (int i = 0; i < lines.Count; i++)
            Text(FitText(lines[i], dw - 40), dx + 16, logTop + i * 12, i == lines.Count - 1 ? TextColor : Dim);

        if (lines.Count == 0)
            Text("BOOTS THE CARTRIDGE AND RECORDS WHAT ITS SOUND DRIVER PLAYS. NO PROFILE NEEDED.",
                dx + 16, logTop, Dim);

        Text("ENTER EXTRACTS · ESC CANCELS · THE VGM IS SAVED NEXT TO THE ROM AND LOADED HERE",
            dx + 16, dy + dh - 22, Dim);
    }
}
