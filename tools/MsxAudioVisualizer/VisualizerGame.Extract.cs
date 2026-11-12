using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Wagenheimer.MsxAudio.Extraction;
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// "Extract from ROM" dialog: boots a cartridge on the in-process MSX machine and records its sound.
/// </summary>
/// <remarks>
/// Three ways to get music out of a ROM, from the most generic to the most complete:
/// <list type="bullet">
/// <item><b>CAPTURE</b> — boot + a key script, record whatever plays (any cartridge, no profile).</item>
/// <item><b>PROFILE</b> — a JSON with the driver's addresses and ids: the full song list, named.</item>
/// <item><b>FIND ALL SONGS</b> — probe the ids the driver accepts, keep what makes sound (no profile).</item>
/// </list>
/// </remarks>
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

    private enum ExtractMode { Capture, Profile }

    // ── State ────────────────────────────────────────────────────────────────────

    private ExtractMode _extractMode;
    private string _extractRom = string.Empty;
    private string _extractProfile = string.Empty;
    private int _extractMapper;
    private string _extractKeys = "START@1";
    private int _extractSeconds = 30;
    private int _extractField = 1;            // 0 = ROM path, 1 = key script, 2 = profile path

    private bool _extractRunning;
    private CancellationTokenSource? _extractCts;
    private Task<string?>? _extractTask;
    private readonly List<string> _extractLog = new();
    private readonly object _extractLogLock = new();
    private volatile int _extractFrame;
    private volatile int _extractTotalFrames = 1;
    private string? _extractError;
    private string? _extractOutput;

    // Clickable areas (design coordinates, filled by DrawExtractDialog).
    private Rectangle _exRomHit, _exBrowseHit, _exMapperHit, _exKeysHit, _exProfileHit, _exProfileBrowseHit;
    private Rectangle _exMinusHit, _exPlusHit, _exStartHit, _exScanHit, _exCloseHit;
    private Rectangle _exModeCaptureHit, _exModeProfileHit;
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

        if (Environment.GetEnvironmentVariable("MSXAUDIO_ROM_PROFILE") is { Length: > 0 } profile)
        {
            _extractProfile = profile;
            _extractMode = ExtractMode.Profile;
        }

        _extractField = _extractRom.Length == 0 ? 0 : 1;
        _extractError = null;
    }

    /// <summary>The folder browser hands a picked cartridge back here.</summary>
    private void PickRom(string path)
    {
        _extractRom = path;
        _dialog = DialogKind.Extract;
        _browserPick = BrowserPick.None;
        _extractField = 1;
        _extractError = null;

        _settings.LastRom = path;
        _settings.Save();
    }

    /// <summary>The folder browser hands a picked profile back here.</summary>
    private void PickProfile(string path)
    {
        _extractProfile = path;
        _dialog = DialogKind.Extract;
        _browserPick = BrowserPick.None;
        _extractMode = ExtractMode.Profile;
        _extractError = null;
    }

    /// <summary>Where the result goes: next to the ROM.</summary>
    private string OutputPath(string suffix, string extension)
    {
        if (_extractRom.Length == 0)
            return string.Empty;

        string folder = Path.GetDirectoryName(Path.GetFullPath(_extractRom)) ?? string.Empty;
        string name = Path.GetFileNameWithoutExtension(_extractRom);
        return Path.Combine(folder, name + suffix + extension);
    }

    /// <summary>Passes the current ROM (and key script) to the profile scanner.</summary>
    private RomExtractionProfile? LoadSelectedProfile()
    {
        if (!File.Exists(_extractProfile))
            return null;

        return RomExtractionProfiles.Load(_extractProfile);
    }

    // ── Jobs ─────────────────────────────────────────────────────────────────────

    private void StartExtraction(bool scan)
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

        if (!scan && _extractMode == ExtractMode.Profile && !File.Exists(_extractProfile))
        {
            _extractError = "CHOOSE A PROFILE .JSON (OR SWITCH TO CAPTURE)";
            return;
        }

        // Snapshot the fields: the worker must not read them while the user edits.
        string rom = _extractRom;
        string keys = _extractKeys;
        string profilePath = _extractProfile;
        int seconds = _extractSeconds;
        int mapperIndex = _extractMapper;
        ExtractMode mode = _extractMode;

        _extractError = null;
        lock (_extractLogLock)
            _extractLog.Clear();

        _extractFrame = 0;
        _extractTotalFrames = 1;
        _extractRunning = true;
        _extractOutput = scan
            ? OutputPath("_songs", ".msxaudio")
            : mode == ExtractMode.Profile
                ? OutputPath("_" + Path.GetFileNameWithoutExtension(profilePath), ".msxaudio")
                : OutputPath("_capture", ".vgm");

        string output = _extractOutput;

        _settings.LastRom = rom;
        _settings.LastRomKeys = keys;
        _settings.LastRomMapper = mapperIndex;
        _settings.LastRomSeconds = seconds;
        _settings.Save();

        var cts = new CancellationTokenSource();
        _extractCts = cts;
        CancellationToken token = cts.Token;

        _extractTask = Task.Run(() =>
        {
            try
            {
                if (scan)
                    return RunScanJob(rom, mapperIndex, profilePath, token, output);

                return mode == ExtractMode.Profile
                    ? RunProfileJob(rom, profilePath, token, output)
                    : RunCaptureJob(rom, mapperIndex, keys, seconds, token, output);
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

    /// <summary>Boot + key script: whatever the cartridge plays in that window.</summary>
    private string? RunCaptureJob(string rom, int mapperIndex, string keys, int seconds,
        CancellationToken token, string output)
    {
        var options = new RomCaptureOptions
        {
            RomPath = rom,
            Mapper = ExtractMappers[mapperIndex].Mapper,
            Seconds = seconds,
        };

        options.Keys.AddRange(KeyPress.ParseScript(keys));

        int frames = Math.Max(1, seconds * 60);
        _extractTotalFrames = frames;

        var result = RomCapture.Run(
            options,
            log: AddExtractLog,
            progress: (frame, _) => _extractFrame = frame,
            token: token);

        File.WriteAllBytes(output, VgmWriter.Write(result.Vgm));
        AddExtractLog($"saved {output} ({result.NoteWrites} pitch writes)");

        if (result.NoteWrites < 20)
        {
            _extractError = "NO MUSIC CAPTURED — TRY A LONGER CAPTURE OR ADD KEYS";
            return null;
        }

        return output;
    }

    /// <summary>Curated profile: every id, with the profile's names and durations.</summary>
    private string? RunProfileJob(string rom, string profilePath, CancellationToken token, string output)
    {
        RomExtractionProfile profile = RomExtractionProfiles.Load(profilePath);
        AddExtractLog($"profile {profile.Name} ({profile.Ids.Count} id(s))");

        _extractTotalFrames = Math.Max(1, profile.Ids.Count);

        var result = DirectRomExtractor.Extract(
            new RomExtractionOptions { RomPath = rom, Profile = profile },
            AddExtractLog,
            (done, _) => _extractFrame = done);

        token.ThrowIfCancellationRequested();

        result.Library.Save(output);
        AddExtractLog($"saved {result.Library.Songs.Count} songs + {result.Library.Clips.Count} effects");
        return output;
    }

    /// <summary>No profile: probe the driver's ids and keep what makes sound.</summary>
    private string? RunScanJob(string rom, int mapperIndex, string profilePath, CancellationToken token,
        string output)
    {
        RomExtractionProfile? profile = profilePath.Length > 0 && File.Exists(profilePath)
            ? RomExtractionProfiles.Load(profilePath)
            : null;

        // The profile's own ids bound the scan (and skip the ids its table does not have).
        int firstId = 1;
        int lastId = 63;
        if (profile is { Ids.Count: > 0 })
        {
            firstId = int.MaxValue;
            lastId = 0;
            foreach (int id in profile.Ids)
            {
                firstId = Math.Min(firstId, id);
                lastId = Math.Max(lastId, id);
            }
        }

        var options = new RomScanOptions
        {
            RomPath = rom,
            Mapper = ExtractMappers[mapperIndex].Mapper,
            SoundCallAddress = profile?.SoundCallAddress ?? 0,
            PlayerAddress = profile?.PlayerAddress ?? 0,
            IdMask = profile?.IdMask ?? 0,
            StopId = profile?.StopId ?? 0,
            FirstId = firstId,
            LastId = lastId,
            SecondsPerId = Math.Clamp(_extractSeconds, 4, 60),
        };

        if (profile is not null)
            options.ResetAddresses.AddRange(profile.ResetAddresses);

        AddExtractLog(profile is null
            ? "no profile: looking for the sound routine..."
            : $"profile {profile.Name}: using its driver addresses");

        var result = RomSoundScan.Run(
            options,
            AddExtractLog,
            (done, total) =>
            {
                _extractFrame = done;
                _extractTotalFrames = Math.Max(1, total);
            },
            token);

        if (result.Songs.Count == 0)
        {
            _extractError = "NO SOUND FOUND — TRY A PROFILE OR THE CAPTURE MODE";
            return null;
        }

        using MsxAudioLibrary library = result.ToLibrary(Path.GetFileNameWithoutExtension(rom), profile?.Names);
        library.Save(output);
        AddExtractLog($"saved {result.Songs.Count} sound(s) (routine {result.Routine:X4})");
        return output;
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

            // The dialog shows the tail; keep the list bounded for long runs.
            if (_extractLog.Count > 600)
                _extractLog.RemoveRange(0, _extractLog.Count - 600);
        }
    }

    /// <summary>Called from Update: finishes a background job and loads the result in the player.</summary>
    private void PollExtraction()
    {
        if (_extractTask is not { IsCompleted: true })
            return;

        Task<string?> task = _extractTask;
        _extractTask = null;
        _extractRunning = false;

        _extractCts?.Dispose();
        _extractCts = null;

        if (task.Status == TaskStatus.RanToCompletion && task.Result is { } output)
        {
            CloseDialog();

            string folder = Path.GetDirectoryName(output) ?? Directory.GetCurrentDirectory();
            ScanTracks(folder, false, output);
            LoadFile(output, rescan: false);

            _status = "EXTRACTED " + Path.GetFileName(output).ToUpperInvariant();
            _statusTimer = 4f;
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

        switch (_extractField)
        {
            case 0:
                _extractRom += character;
                break;
            case 2:
                _extractProfile += character;
                _extractMode = ExtractMode.Profile;
                break;
            default:
                _extractKeys += character;
                break;
        }
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
            StartExtraction(scan: false);
            return;
        }

        if (Pressed(Keys.Tab))
        {
            _extractField = _extractMode == ExtractMode.Profile
                ? (_extractField == 2 ? 0 : 2)
                : (_extractField == 1 ? 0 : 1);
            return;
        }

        if (Pressed(Keys.Back) && !_extractRunning)
        {
            if (_extractField == 0 && _extractRom.Length > 0)
                _extractRom = _extractRom[..^1];
            else if (_extractField == 1 && _extractKeys.Length > 0)
                _extractKeys = _extractKeys[..^1];
            else if (_extractField == 2 && _extractProfile.Length > 0)
                _extractProfile = _extractProfile[..^1];

            return;
        }

        if (Pressed(Keys.Delete) && !_extractRunning && _extractField == 0)
        {
            _extractRom = string.Empty;
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

        if (_exProfileHit.Contains(p))
        {
            _extractField = 2;
            return;
        }

        if (_exBrowseHit.Contains(p))
        {
            OpenRomBrowser();
            return;
        }

        if (_exProfileBrowseHit.Contains(p))
        {
            OpenProfileBrowser();
            return;
        }

        if (_exModeCaptureHit.Contains(p) && !_extractRunning)
        {
            _extractMode = ExtractMode.Capture;
            _extractField = 1;
            return;
        }

        if (_exModeProfileHit.Contains(p) && !_extractRunning)
        {
            _extractMode = ExtractMode.Profile;
            _extractField = 2;
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
        {
            StartExtraction(scan: false);
            return;
        }

        if (_exScanHit.Contains(p))
            StartExtraction(scan: true);
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
        const int dx = 220, dy = 100, dw = 840, dh = 520;
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

        // Mode: capture (any ROM) or profile (curated song list).
        Text("MODE", labelX, dy + 72, Dim);
        bool capture = _extractMode == ExtractMode.Capture;

        _exModeCaptureHit = new Rectangle(fieldX, dy + 66, 92, 18);
        DrawButton(_exModeCaptureHit, "CAPTURE", capture, Accent);

        _exModeProfileHit = new Rectangle(fieldX + 96, dy + 66, 92, 18);
        DrawButton(_exModeProfileHit, "PROFILE", !capture, Accent);

        if (!capture)
        {
            int profileX = fieldX + 96 + 92 + 10;
            int profileW = dx + dw - 116 - profileX;

            _exProfileHit = new Rectangle(profileX, dy + 66, profileW, 18);
            DrawField(_exProfileHit,
                _extractProfile.Length > 0 ? Path.GetFileName(_extractProfile) : "(NO PROFILE CHOSEN)",
                _extractField == 2, _extractProfile.Length > 0 ? null : Dim);

            _exProfileBrowseHit = new Rectangle(profileX + profileW + 8, dy + 66, 84, 18);
            DrawButton(_exProfileBrowseHit, "BROWSE", false, Accent);
        }
        else
        {
            _exProfileHit = Rectangle.Empty;
            _exProfileBrowseHit = Rectangle.Empty;
        }

        // Mapper + duration.
        Text("MAPPER", labelX, dy + 100, Dim);
        _exMapperHit = new Rectangle(fieldX, dy + 94, 150, 18);
        DrawButton(_exMapperHit, ExtractMappers[_extractMapper].Label, true, Accent);

        int secondsX = fieldX + 190;
        Text("SECONDS", secondsX, dy + 100, Dim);
        _exMinusHit = new Rectangle(secondsX + 70, dy + 94, 18, 18);
        DrawButton(_exMinusHit, "-", false, Dim);

        string seconds = _extractSeconds.ToString();
        Text(seconds, secondsX + 96 + (24 - PixelFont.Measure(seconds)) / 2, dy + 100, TextColor);

        _exPlusHit = new Rectangle(secondsX + 124, dy + 94, 18, 18);
        DrawButton(_exPlusHit, "+", false, Dim);

        // Capture: key script. Profile: what the profile holds.
        if (capture)
        {
            Text("KEYS", labelX, dy + 128, Dim);
            _exKeysHit = new Rectangle(fieldX, dy + 122, fieldW + 92, 18);
            DrawField(_exKeysHit, _extractKeys.Length > 0 ? _extractKeys : "(NONE)", _extractField == 1,
                _extractKeys.Length > 0 ? null : Dim);

            Text("NAME@SECOND (START, FIRE, UP, DOWN, LEFT, RIGHT) OR ROW:MASK@SECOND",
                fieldX, dy + 146, Dim);
        }
        else
        {
            _exKeysHit = Rectangle.Empty;
            Text("CONTENT", labelX, dy + 128, Dim);
            Text(FitText(ProfileSummary(), dw - 130), fieldX, dy + 128, TextColor);
            Text("THE PROFILE KNOWS THE DRIVER ADDRESSES, THE IDS AND THE NAMES",
                fieldX, dy + 146, Dim);
        }

        // Output path.
        string output = _extractOutput is { Length: > 0 } last && _extractRunning
            ? last
            : _extractMode == ExtractMode.Profile
                ? OutputPath("_" + Path.GetFileNameWithoutExtension(_extractProfile), ".msxaudio")
                : OutputPath("_capture", ".vgm");

        Text("OUTPUT", labelX, dy + 170, Dim);
        Text(FitText(output.Length > 0 ? output : "(CHOOSE A ROM)", dw - 120), fieldX, dy + 170, Dim);

        // Start / scan / progress.
        _exStartHit = new Rectangle(fieldX, dy + 196, 110, 20);
        DrawButton(_exStartHit,
            _extractRunning ? "STOP" : _extractMode == ExtractMode.Profile ? "EXTRACT ALL" : "CAPTURE",
            _extractRunning, _extractRunning ? Warn : Accent);

        _exScanHit = new Rectangle(fieldX + 118, dy + 196, 152, 20);
        DrawButton(_exScanHit, "FIND ALL SONGS", false, Accent);

        int barX = _exScanHit.Right + 12;
        int barW = dx + dw - 16 - barX - 46;
        float filled = Math.Clamp(_extractFrame / (float)_extractTotalFrames, 0f, 1f);

        Rect(barX, dy + 202, barW, 8, Grid);
        Rect(barX, dy + 202, (int)(barW * filled), 8, _extractRunning ? PsgColor : Accent);

        string percent = $"{(int)(filled * 100)}%";
        Text(percent, barX + barW + 10, dy + 202, _extractRunning ? TextColor : Dim);

        if (_extractError is { } error)
            Text(FitText(error, dw - 190), barX, dy + 184, Warn);

        // Log tail.
        int logTop = dy + 232;
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
        {
            Text("CAPTURE: BOOTS THE ROM AND RECORDS WHAT IT PLAYS (ANY CARTRIDGE).",
                dx + 16, logTop, Dim);
            Text("PROFILE: USES A .JSON WITH THE DRIVER'S ADDRESSES FOR THE FULL SONG LIST.",
                dx + 16, logTop + 12, Dim);
            Text("FIND ALL SONGS: TRIES EVERY ID AND KEEPS WHAT MAKES SOUND (NO PROFILE NEEDED).",
                dx + 16, logTop + 24, Dim);
        }

        Text("ENTER RUNS · ESC CANCELS · THE RESULT IS SAVED NEXT TO THE ROM AND OPENED HERE",
            dx + 16, dy + dh - 22, Dim);
    }

    /// <summary>One-line description of the selected profile (name, ids, songs).</summary>
    private string ProfileSummary()
    {
        if (_extractProfile.Length == 0)
            return "(NONE)";

        try
        {
            RomExtractionProfile profile = RomExtractionProfiles.Load(_extractProfile);
            return $"{profile.Name}: {profile.Ids.Count} ID(S), {profile.MusicIds.Count} SONG(S)";
        }
        catch (Exception)
        {
            return "(INVALID PROFILE)";
        }
    }
}
