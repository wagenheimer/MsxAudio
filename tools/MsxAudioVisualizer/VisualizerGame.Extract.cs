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
        ("R-TYPE", RomMapper.RType),
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

    private string _extractOutDir = string.Empty;   // empty = the folder next to the ROM
    private bool _extractOutAuto = true;            // follow the ROM's folder until the user picks one
    private int _extractFormat = 2;                 // 0 = .vgm, 1 = .msxaudio, 2 = auto (capture: .vgm; profile/scan: .msxaudio)
    private bool _extractPsg = true;
    private bool _extractScc = true;
    private bool _extractPal;                       // 50 Hz instead of 60 Hz
    private bool _extractAutoPress;                 // tap fire/space so the game leaves its menus

    private RomInfo? _extractInfo;                  // what the ROM turned out to be (shown under the path)
    private string _extractInfoPath = string.Empty;
    private string? _extractInfoError;

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
    private Rectangle _exOutHit, _exOutBrowseHit, _exFmtVgmHit, _exFmtPkgHit, _exPsgHit, _exSccHit;
    private Rectangle _exMinusHit, _exPlusHit, _exStartHit, _exScanHit, _exCloseHit;
    private Rectangle _exModeCaptureHit, _exModeProfileHit, _exHzHit, _exAutoHit;
    private Rectangle _exRomPasteHit, _exOutPasteHit, _exFmtAutoHit, _exOpenFolderHit;
    private bool _extractScanning;                   // the running job is FIND ALL SONGS (always a package)
    private DateTime _extractStarted;               // when the running job began (elapsed time + spinner)
    private string[] _extractInfoLines = Array.Empty<string>();   // what the dialog says about the chosen ROM
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

        _extractPal = Environment.GetEnvironmentVariable("MSXAUDIO_ROM_PAL") == "1";
        _extractAutoPress = Environment.GetEnvironmentVariable("MSXAUDIO_ROM_AUTOPRESS") == "1";

        if (Environment.GetEnvironmentVariable("MSXAUDIO_ROM_PROFILE") is { Length: > 0 } profile)
        {
            _extractProfile = profile;
            _extractMode = ExtractMode.Profile;
        }

        _extractField = _extractRom.Length == 0 ? 0 : 1;
        _extractError = null;

        if (_extractOutAuto)
            _extractOutDir = DefaultOutDir(_extractRom);
    }

    /// <summary>The folder browser hands a picked cartridge back here.</summary>
    private void PickRom(string path)
    {
        SetRom(path);
        _dialog = DialogKind.Extract;
        _browserPick = BrowserPick.None;
        _extractField = 1;
        _extractError = null;

        _settings.LastRom = path;
        _settings.Save();
    }

    /// <summary>
    /// Sets the ROM (also used while typing). While the output folder is "auto" it follows the ROM:
    /// <c>&lt;rom folder&gt;/extracted_audio</c>.
    /// </summary>
    private void SetRom(string path)
    {
        _extractRom = path;

        if (_extractOutAuto)
            _extractOutDir = DefaultOutDir(path);
    }

    /// <summary><c>&lt;folder of the ROM or archive&gt;/extracted_audio/&lt;rom name&gt;</c>.</summary>
    private static string DefaultOutDir(string rom)
    {
        if (string.IsNullOrWhiteSpace(rom))
            return string.Empty;

        try
        {
            string path = rom.Trim('"');
            string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
            if (folder.Length == 0)
                return string.Empty;

            return Path.Combine(folder, "extracted_audio", CleanStem(path));
        }
        catch (Exception)
        {
            return string.Empty;
        }
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

    /// <summary>The folder the result goes to (the ROM's folder unless the user picked one).</summary>
    private string OutputFolder()
    {
        if (_extractOutDir.Length > 0)
            return _extractOutDir;

        return _extractRom.Length > 0
            ? Path.GetDirectoryName(Path.GetFullPath(_extractRom)) ?? string.Empty
            : string.Empty;
    }

    /// <summary>Output file name, without the folder.</summary>
    private string OutputFileName(string suffix, string extension)
    {
        if (_extractRom.Length == 0)
            return string.Empty;

        return CleanStem(_extractRom) + suffix + extension;
    }

    /// <summary>Where the result goes: the chosen folder (or the ROM's) plus the derived name.</summary>
    private string OutputPath(string suffix, string extension)
    {
        string folder = OutputFolder();
        string name = OutputFileName(suffix, extension);

        if (folder.Length == 0 || name.Length == 0)
            return string.Empty;

        return Path.Combine(folder, name);
    }

    /// <summary>The folder browser hands a picked destination folder back here.</summary>
    private void PickOutFolder(string path)
    {
        _extractOutDir = path;
        _extractOutAuto = false;
        _dialog = DialogKind.Extract;
        _browserPick = BrowserPick.None;
        _extractError = null;
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
        bool psg = _extractPsg;
        bool scc = _extractScc;
        bool package = ExtractAsPackage();
        bool pal = _extractPal;
        bool autoPress = _extractAutoPress;
        ExtractMode mode = _extractMode;

        _extractError = null;
        lock (_extractLogLock)
            _extractLog.Clear();

        _extractFrame = 0;
        _extractTotalFrames = 1;
        _extractStarted = DateTime.UtcNow;
        _extractScanning = scan;
        _extractRunning = true;
        _extractOutput = scan
            ? OutputPath("_songs", ".msxaudio")
            : mode == ExtractMode.Profile
                ? OutputPath("_" + Path.GetFileNameWithoutExtension(profilePath), ".msxaudio")
                : OutputPath("_capture", package ? ".msxaudio" : ".vgm");

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
                double refresh = pal ? 50 : 60;

                if (scan)
                    return RunScanJob(rom, mapperIndex, profilePath, psg, scc, refresh, token, output);

                return mode == ExtractMode.Profile
                    ? RunProfileJob(rom, profilePath, psg, scc, refresh, token, output)
                    : RunCaptureJob(rom, mapperIndex, keys, seconds, package, psg, scc, refresh, autoPress,
                        token, output);
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
    private string? RunCaptureJob(string rom, int mapperIndex, string keys, int seconds, bool package,
        bool psg, bool scc, double refresh, bool autoPress, CancellationToken token, string output)
    {
        var options = new RomCaptureOptions
        {
            RomPath = rom,
            Mapper = ExtractMappers[mapperIndex].Mapper,
            Seconds = seconds,
            CapturePsg = psg,
            CaptureScc = scc,
            RefreshRate = refresh,
            AutoPlay = autoPress,
        };

        options.Keys.AddRange(KeyPress.ParseScript(keys));

        int frames = Math.Max(1, (int)(seconds * refresh));
        _extractTotalFrames = frames;

        var result = RomCapture.Run(
            options,
            log: AddExtractLog,
            progress: (frame, _) => _extractFrame = frame,
            token: token);

        if (package)
        {
            using var library = new MsxAudioLibrary(Path.GetFileNameWithoutExtension(rom));
            library.Manifest.Source = $"direct capture ({Path.GetFileName(rom)})";

            string name = "capture";
            library.AddSong(name, name, result.Vgm, loop: false, originalId: null, tags: new[] { "capture", "scanned" });
            library.Save(output);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllBytes(output, VgmWriter.Write(result.Vgm));
        }

        AddExtractLog($"saved {output} ({result.NoteWrites} pitch writes)");

        if (result.NoteWrites < 20)
        {
            _extractError = result.Diagnosis.Count > 0
                ? "NO MUSIC CAPTURED: " + result.Diagnosis[0].ToUpperInvariant()
                : "NO MUSIC CAPTURED — TRY A LONGER CAPTURE, KEYS OR AUTO-PRESS";
            return null;
        }

        return output;
    }

    /// <summary>Curated profile: every id, with the profile's names and durations.</summary>
    private string? RunProfileJob(string rom, string profilePath, bool psg, bool scc, double refresh,
        CancellationToken token, string output)
    {
        RomExtractionProfile profile = RomExtractionProfiles.Load(profilePath);
        if (refresh < 60)
            profile.RefreshRate = refresh;

        AddExtractLog($"profile {profile.Name} ({profile.Ids.Count} id(s))");

        _extractTotalFrames = Math.Max(1, profile.Ids.Count);

        var result = DirectRomExtractor.Extract(
            new RomExtractionOptions
            {
                RomPath = rom,
                Profile = profile,
                CapturePsg = psg,
                CaptureScc = scc,
            },
            AddExtractLog,
            (done, _) => _extractFrame = done);

        token.ThrowIfCancellationRequested();

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        result.Library.Save(output);
        AddExtractLog($"saved {result.Library.Songs.Count} songs + {result.Library.Clips.Count} effects");
        return output;
    }

    /// <summary>No profile: probe the driver's ids and keep what makes sound.</summary>
    private string? RunScanJob(string rom, int mapperIndex, string profilePath, bool psg, bool scc,
        double refresh, CancellationToken token, string output)
    {
        RomExtractionProfile? profile = profilePath.Length > 0 && File.Exists(profilePath)
            ? RomExtractionProfiles.Load(profilePath)
            : null;

        // The profile's own ids bound the scan (and skip the ids its table does not have).
        int firstId = 1;
        int lastId = 255;
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
            SoundCallBank = profile?.SoundCallBank ?? -1,
            RefreshRate = refresh,
            PlayerAddress = profile?.PlayerAddress ?? 0,
            IdMask = profile?.IdMask ?? 0,
            StopId = profile?.StopId ?? 0,
            FirstId = firstId,
            LastId = lastId,
            SecondsPerId = Math.Clamp(_extractSeconds, 4, 60),
            CapturePsg = psg,
            CaptureScc = scc,
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
            foreach (string line in result.Diagnosis)
                AddExtractLog("why: " + line);

            _extractError = result.Diagnosis.Count > 0
                ? "NO SOUND FOUND: " + result.Diagnosis[0].ToUpperInvariant()
                : "NO SOUND FOUND — TRY A PROFILE OR THE CAPTURE MODE";
            return null;
        }

        using MsxAudioLibrary library = result.ToLibrary(Path.GetFileNameWithoutExtension(rom), profile?.Names);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
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

    /// <summary>Keeps the tail of <paramref name="text"/> (a path's file name stays visible).</summary>
    private static string FitTextLeft(string text, int width)
    {
        if (width <= 0)
            return string.Empty;

        if (PixelFont.Measure(text) <= width)
            return text;

        while (text.Length > 1 && PixelFont.Measure("…" + text[1..]) > width)
            text = text[1..];

        return "…" + text;
    }

    /// <summary>File name of a ROM or archive without the browser's " (1)" duplicate suffix.</summary>
    private static string CleanStem(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);

        // "Penguin-Adventure_MSX_JA (1)" -> "Penguin-Adventure_MSX_JA"
        int open = stem.LastIndexOf(" (", StringComparison.Ordinal);
        if (open > 0 && stem.EndsWith(")", StringComparison.Ordinal) &&
            int.TryParse(stem[(open + 2)..^1], out _))
            stem = stem[..open];

        return stem;
    }

    /// <summary>Text typed into the dialog (wired to <c>Window.TextInput</c> in Initialize).</summary>
    private void OnExtractTextInput(char character)
    {
        if (_dialog != DialogKind.Extract || _extractRunning || char.IsControl(character))
            return;

        switch (_extractField)
        {
            case 0:
                SetRom(_extractRom + character);
                break;
            case 2:
                _extractProfile += character;
                _extractMode = ExtractMode.Profile;
                break;
            case 3:
                SetOutFolderText(_extractOutDir + character);
                break;
            default:
                _extractKeys += character;
                break;
        }
    }

    /// <summary>Output folder typed or pasted by hand; empty goes back to the automatic folder.</summary>
    private void SetOutFolderText(string text)
    {
        _extractOutAuto = text.Length == 0;
        _extractOutDir = _extractOutAuto ? DefaultOutDir(_extractRom) : text;
    }

    /// <summary>Pastes the clipboard (a path, or a file copied in Explorer) into a field.</summary>
    private void PasteInto(int field)
    {
        if (_extractRunning || Clipboard.TryReadText() is not { Length: > 0 } raw)
            return;

        string text = raw.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]
            .Trim().Trim('"');

        if (text.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            text = Uri.UnescapeDataString(text[8..]).Replace('/', Path.DirectorySeparatorChar);

        _extractField = field;

        // A file pasted into the output field means "put it in that file's folder".
        if (field == 3 && !Directory.Exists(text) && Path.HasExtension(text))
            text = Path.GetDirectoryName(text) ?? text;

        SetExtractFieldText(text);
        _extractError = null;
    }

    /// <summary>
    /// True when the result is a <c>.msxaudio</c> package. Auto: a capture is one song (a plain
    /// <c>.vgm</c>), a profile or a scan is a whole song list (a package).
    /// </summary>
    private bool ExtractAsPackage() =>
        _extractMode != ExtractMode.Capture || _extractFormat == 1 || (_extractRunning && _extractScanning);

    /// <summary>Fields the Tab key walks through, in screen order.</summary>
    private int[] ExtractFieldOrder() =>
        _extractMode == ExtractMode.Profile ? new[] { 0, 2, 3 } : new[] { 0, 1, 3 };

    /// <summary>Replaces the focused field with <paramref name="text"/> (paste, or a dropped path).</summary>
    private void SetExtractFieldText(string text)
    {
        switch (_extractField)
        {
            case 0:
                SetRom(text);
                break;
            case 2:
                _extractProfile = text;
                _extractMode = ExtractMode.Profile;
                break;
            case 3:
                SetOutFolderText(text);
                break;
            default:
                _extractKeys = text;
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

        // Ctrl+V (or Shift+Insert) pastes a full path into the focused field.
        bool ctrl = _keys.IsKeyDown(Keys.LeftControl) || _keys.IsKeyDown(Keys.RightControl);
        bool shift = _keys.IsKeyDown(Keys.LeftShift) || _keys.IsKeyDown(Keys.RightShift);

        if (!_extractRunning && ((ctrl && Pressed(Keys.V)) || (shift && Pressed(Keys.Insert))))
        {
            PasteInto(_extractField);
            return;
        }

        // Ctrl+Backspace / Ctrl+A-style clear: wipe the whole focused field.
        if (!_extractRunning && ctrl && Pressed(Keys.Back))
        {
            SetExtractFieldText(string.Empty);
            return;
        }

        if (Pressed(Keys.Enter) && !_extractRunning)
        {
            StartExtraction(scan: false);
            return;
        }

        if (Pressed(Keys.Tab))
        {
            int[] order = ExtractFieldOrder();
            int at = Array.IndexOf(order, _extractField);
            int step = shift ? order.Length - 1 : 1;
            _extractField = order[(Math.Max(at, 0) + step) % order.Length];
            return;
        }

        if (Pressed(Keys.Back) && !_extractRunning)
        {
            if (_extractField == 0 && _extractRom.Length > 0)
                SetRom(_extractRom[..^1]);
            else if (_extractField == 1 && _extractKeys.Length > 0)
                _extractKeys = _extractKeys[..^1];
            else if (_extractField == 2 && _extractProfile.Length > 0)
                _extractProfile = _extractProfile[..^1];
            else if (_extractField == 3 && !_extractOutAuto && _extractOutDir.Length > 0)
                SetOutFolderText(_extractOutDir[..^1]);

            return;
        }

        // Delete wipes the focused field (the output folder goes back to AUTO).
        if (Pressed(Keys.Delete) && !_extractRunning)
        {
            SetExtractFieldText(string.Empty);
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

        if (_exRomPasteHit.Contains(p))
        {
            PasteInto(0);
            return;
        }

        if (_exOutPasteHit.Contains(p))
        {
            PasteInto(3);
            return;
        }

        if (_exOpenFolderHit.Contains(p))
        {
            OpenOutputFolder();
            return;
        }

        if (_exOutHit.Contains(p) && !_extractRunning)
        {
            _extractField = 3;
            return;
        }

        if (_exOutBrowseHit.Contains(p))
        {
            OpenFolderPicker();
            return;
        }

        if (_exFmtAutoHit.Contains(p) && !_extractRunning)
        {
            _extractFormat = 2;
            return;
        }

        if (_exFmtVgmHit.Contains(p) && !_extractRunning && _extractMode == ExtractMode.Capture)
        {
            _extractFormat = 0;
            return;
        }

        if (_exFmtPkgHit.Contains(p) && !_extractRunning && _extractMode == ExtractMode.Capture)
        {
            _extractFormat = 1;
            return;
        }

        if (_exPsgHit.Contains(p) && !_extractRunning)
        {
            // Never leave both chips off: that would record silence.
            _extractPsg = !_extractPsg || !_extractScc;
            return;
        }

        if (_exSccHit.Contains(p) && !_extractRunning)
        {
            _extractScc = !_extractScc || !_extractPsg;
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

        if (_exHzHit.Contains(p) && !_extractRunning)
        {
            _extractPal = !_extractPal;
            return;
        }

        if (_exAutoHit.Contains(p) && !_extractRunning)
        {
            _extractAutoPress = !_extractAutoPress;
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

    /// <summary>Eight dots on a circle with a bright head that runs around: "busy".</summary>
    private void DrawSpinner(int centerX, int centerY, int radius)
    {
        const int dots = 8;
        int head = (int)(Environment.TickCount64 / 90 % dots);

        for (int i = 0; i < dots; i++)
        {
            double angle = i * Math.PI * 2 / dots - Math.PI / 2;
            int x = centerX + (int)Math.Round(Math.Cos(angle) * radius) - 1;
            int y = centerY + (int)Math.Round(Math.Sin(angle) * radius) - 1;

            // Distance behind the head: 0 = brightest, fading with every step.
            int behind = (head - i + dots) % dots;
            float fade = 1f - behind / (float)dots * 0.85f;
            Rect(x, y, 3, 3, Color.Lerp(Grid, Accent, fade));
        }
    }

    /// <summary>A path field: shows the end of the path so the file name is visible, with a caret when focused.</summary>
    private void DrawPathField(Rectangle rect, string value, string placeholder, bool focused)
    {
        Rect(rect, focused ? Accent : Grid);
        Rect(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2, PanelColor);

        int room = rect.Width - 14;
        string shown = value.Length == 0 ? placeholder : FitTextLeft(value, room);
        Color color = value.Length == 0 ? Dim : focused ? Accent : TextColor;
        Text(value.Length == 0 ? FitText(shown, room) : shown, rect.X + 6, rect.Y + 5, color);

        if (focused)
        {
            int caret = value.Length == 0 ? rect.X + 6 : rect.X + 6 + Math.Min(PixelFont.Measure(shown), room);
            Rect(caret, rect.Y + 3, 1, rect.Height - 6, Accent);
        }
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
        const int dx = 220, dy = 80, dw = 840, dh = 560;
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

        // The path is always shown whole (its tail, so the file name stays visible) and is editable:
        // type it, paste it (Ctrl+V or PASTE; a file copied in Explorer works too) or BROWSE.
        int pathW = fieldW - 66;
        _exRomHit = new Rectangle(fieldX, dy + 38, pathW, 18);
        DrawPathField(_exRomHit, _extractRom, "(PASTE OR TYPE THE ROM / ZIP PATH, OR BROWSE)", _extractField == 0);

        _exRomPasteHit = new Rectangle(fieldX + pathW + 6, dy + 38, 60, 18);
        DrawButton(_exRomPasteHit, "PASTE", false, Accent);

        _exBrowseHit = new Rectangle(fieldX + fieldW + 8, dy + 38, 84, 18);
        DrawButton(_exBrowseHit, "BROWSE", false, Accent);

        // What the ROM turned out to be: where it came from (a .zip is unpacked by itself), size,
        // mapper and how it was chosen, header, hash, and what to do next. Refreshed on path changes.
        RefreshExtractInfo();
        bool romMissing = _extractRom.Length > 0 && !File.Exists(_extractRom);
        if (romMissing)
            Text("FILE NOT FOUND", fieldX, dy + 60, Warn);
        else if (_extractInfoError is not null)
            Text(FitText(_extractInfoError.ToUpperInvariant(), fieldW + 92), fieldX, dy + 60, Warn);
        else
            for (int i = 0; i < _extractInfoLines.Length; i++)
                Text(FitText(_extractInfoLines[i], fieldW + 92), fieldX, dy + 60 + i * 11,
                    i == 2 ? Accent : Dim);

        // Mode: capture (any ROM) or profile (curated song list).
        Text("MODE", labelX, dy + 102, Dim);
        bool capture = _extractMode == ExtractMode.Capture;

        _exModeCaptureHit = new Rectangle(fieldX, dy + 96, 92, 18);
        DrawButton(_exModeCaptureHit, "CAPTURE", capture, Accent);

        _exModeProfileHit = new Rectangle(fieldX + 96, dy + 96, 92, 18);
        DrawButton(_exModeProfileHit, "PROFILE", !capture, Accent);

        if (!capture)
        {
            int profileX = fieldX + 96 + 92 + 10;
            int profileW = dx + dw - 116 - profileX;

            _exProfileHit = new Rectangle(profileX, dy + 96, profileW, 18);
            DrawField(_exProfileHit,
                _extractProfile.Length > 0 ? Path.GetFileName(_extractProfile) : "(NO PROFILE CHOSEN)",
                _extractField == 2, _extractProfile.Length > 0 ? null : Dim);

            _exProfileBrowseHit = new Rectangle(profileX + profileW + 8, dy + 96, 84, 18);
            DrawButton(_exProfileBrowseHit, "BROWSE", false, Accent);
        }
        else
        {
            _exProfileHit = Rectangle.Empty;
            _exProfileBrowseHit = Rectangle.Empty;
        }

        // Mapper + duration.
        Text("MAPPER", labelX, dy + 126, Dim);
        _exMapperHit = new Rectangle(fieldX, dy + 120, 150, 18);
        DrawButton(_exMapperHit, ExtractMappers[_extractMapper].Label, true, Accent);

        int secondsX = fieldX + 190;
        Text("SECONDS", secondsX, dy + 126, Dim);
        _exMinusHit = new Rectangle(secondsX + 70, dy + 120, 18, 18);
        DrawButton(_exMinusHit, "-", false, Dim);

        string seconds = _extractSeconds.ToString();
        Text(seconds, secondsX + 96 + (24 - PixelFont.Measure(seconds)) / 2, dy + 126, TextColor);

        _exPlusHit = new Rectangle(secondsX + 124, dy + 120, 18, 18);
        DrawButton(_exPlusHit, "+", false, Dim);

        // 60 Hz (NTSC) or 50 Hz (PAL): the tempo of the whole capture. Auto-press: tap fire and the
        // space bar every few seconds so the game leaves its title/menu screens by itself.
        _exHzHit = new Rectangle(secondsX + 160, dy + 120, 58, 18);
        DrawButton(_exHzHit, _extractPal ? "50 HZ" : "60 HZ", true, Accent);

        _exAutoHit = new Rectangle(secondsX + 224, dy + 120, 118, 18);
        DrawButton(_exAutoHit, "AUTO-PRESS", _extractAutoPress && capture, capture ? Accent : Dim);

        // Capture: key script. Profile: what the profile holds.
        if (capture)
        {
            Text("KEYS", labelX, dy + 154, Dim);
            _exKeysHit = new Rectangle(fieldX, dy + 148, fieldW + 92, 18);
            DrawField(_exKeysHit, _extractKeys.Length > 0 ? _extractKeys : "(NONE)", _extractField == 1,
                _extractKeys.Length > 0 ? null : Dim);

            Text("NAME@SECOND (START, FIRE, UP, DOWN, LEFT, RIGHT) OR ROW:MASK@SECOND",
                fieldX, dy + 172, Dim);
        }
        else
        {
            _exKeysHit = Rectangle.Empty;
            Text("CONTENT", labelX, dy + 154, Dim);
            Text(FitText(ProfileSummary(), dw - 130), fieldX, dy + 154, TextColor);
            Text("THE PROFILE KNOWS THE DRIVER ADDRESSES, THE IDS AND THE NAMES",
                fieldX, dy + 172, Dim);
        }

        // Output: folder (clickable field + FOLDER button), format and chips.
        Text("OUTPUT", labelX, dy + 202, Dim);

        string folder = OutputFolder();

        _exOutHit = new Rectangle(fieldX, dy + 196, pathW, 18);
        DrawPathField(_exOutHit, folder, "(NEXT TO THE ROM)", _extractField == 3);

        _exOutPasteHit = new Rectangle(fieldX + pathW + 6, dy + 196, 60, 18);
        DrawButton(_exOutPasteHit, "PASTE", false, Accent);

        _exOutBrowseHit = new Rectangle(fieldX + fieldW + 8, dy + 196, 84, 18);
        DrawButton(_exOutBrowseHit, "FOLDER", false, Accent);

        if (_extractField == 3)
            Text(_extractOutAuto ? "AUTOMATIC FOLDER: TYPE OR PASTE TO CHANGE IT" : "DEL = BACK TO THE AUTOMATIC FOLDER",
                fieldX, dy + 216, Dim);

        // Format: a capture can be saved as one VGM or as a one-song package. Profiles/scans always
        // produce a package, so the VGM chip is only meaningful in capture mode.
        Text("FORMAT", labelX, dy + 230, Dim);

        bool canPick = _extractMode == ExtractMode.Capture;
        bool auto = !canPick || _extractFormat == 2;
        bool asPackage = ExtractAsPackage();

        _exFmtAutoHit = new Rectangle(fieldX, dy + 224, 50, 18);
        DrawButton(_exFmtAutoHit, "AUTO", auto, Accent);

        _exFmtVgmHit = new Rectangle(fieldX + 54, dy + 224, 44, 18);
        DrawButton(_exFmtVgmHit, "VGM", !auto && !asPackage, canPick ? Accent : Dim);

        _exFmtPkgHit = new Rectangle(fieldX + 102, dy + 224, 96, 18);
        DrawButton(_exFmtPkgHit, "MSXAUDIO", !auto && asPackage, canPick ? Accent : Dim);

        // Say what AUTO resolves to, so nobody has to guess.
        Text(asPackage ? "= ALL SONGS (.MSXAUDIO)" : "= ONE SONG (.VGM)",
            fieldX + 206, dy + 230, Dim);

        // Chips: what gets recorded (both on = the raw driver output).
        int chipsX = fieldX + 420;
        Text("CHIPS", chipsX - 52, dy + 230, Dim);

        _exPsgHit = new Rectangle(chipsX, dy + 224, 44, 18);
        DrawButton(_exPsgHit, "PSG", _extractPsg, PsgColor);

        _exSccHit = new Rectangle(chipsX + 48, dy + 224, 44, 18);
        DrawButton(_exSccHit, "SCC", _extractScc, SccColor);

        // The file that will be written, so the whole path is visible without editing it.
        string fileName = _extractRunning && _extractOutput is { Length: > 0 } running
            ? Path.GetFileName(running)
            : FileNameForCurrentMode();

        string saveTo = fileName.Length == 0 ? string.Empty : Path.Combine(OutputFolder(), fileName);
        Text("SAVES TO", labelX, dy + 248, Dim);
        Text(saveTo.Length > 0 ? FitTextLeft(saveTo, fieldW) : "(CHOOSE A ROM)", fieldX, dy + 248, TextColor);

        // Jump to where the result goes (or went): opens the folder in the system file manager.
        _exOpenFolderHit = new Rectangle(fieldX + fieldW + 8, dy + 244, 84, 18);
        DrawButton(_exOpenFolderHit, "OPEN FOLDER", false, Accent);

        // Start / scan / progress.
        _exStartHit = new Rectangle(fieldX, dy + 270, 110, 20);
        DrawButton(_exStartHit,
            _extractRunning ? "STOP" : _extractMode == ExtractMode.Profile ? "EXTRACT ALL" : "CAPTURE",
            _extractRunning, _extractRunning ? Warn : Accent);

        _exScanHit = new Rectangle(fieldX + 118, dy + 270, 152, 20);
        DrawButton(_exScanHit, "FIND ALL SONGS", false, Accent);

        int barX = _exScanHit.Right + 12;
        float filled = Math.Clamp(_extractFrame / (float)_extractTotalFrames, 0f, 1f);

        // A spinner (so it is obvious the app is busy even when nothing is logged) and the elapsed time.
        if (_extractRunning)
        {
            DrawSpinner(barX + 8, dy + 280, 7);
            barX += 24;
        }

        int barW = dx + dw - 16 - barX - 46;

        Rect(barX, dy + 276, barW, 8, Grid);
        Rect(barX, dy + 276, (int)(barW * filled), 8, _extractRunning ? PsgColor : Accent);

        string percent = $"{(int)(filled * 100)}%";
        Text(percent, barX + barW + 10, dy + 276, _extractRunning ? TextColor : Dim);

        if (_extractRunning)
        {
            TimeSpan elapsed = DateTime.UtcNow - _extractStarted;
            Text($"WORKING... {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}  (ESC STOPS)", barX, dy + 258, PsgColor);
        }
        else if (_extractError is { } error)
        {
            Text(FitText(error, dw - 190), barX, dy + 258, Warn);
        }

        // Log tail.
        int logTop = dy + 306;
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

        Text("ENTER RUNS · ESC CANCELS · THE RESULT GOES TO THE OUTPUT FOLDER AND OPENS HERE",
            dx + 16, dy + dh - 22, Dim);
    }

    /// <summary>Analyses the chosen ROM once per path (size, SHA-1, mapper, header) for the dialog.</summary>
    private void RefreshExtractInfo()
    {
        if (_extractInfoPath == _extractRom)
            return;

        _extractInfoPath = _extractRom;
        _extractInfo = null;
        _extractInfoError = null;
        _extractInfoLines = Array.Empty<string>();

        if (!File.Exists(_extractRom))
            return;

        try
        {
            _extractInfo = RomInfo.Analyze(_extractRom);
            _extractInfoLines = DescribeRom(_extractRom, _extractInfo);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _extractInfoError = ex.Message;
        }
    }

    /// <summary>Opens the extraction output folder in the file manager.</summary>
    private void OpenOutputFolder()
    {
        string folder = OutputFolder();

        if (folder.Length == 0)
        {
            _extractError = "CHOOSE A ROM OR AN OUTPUT FOLDER FIRST";
            return;
        }

        _extractError = OpenInFileManager(folder);
    }

    /// <summary>
    /// Shows <paramref name="folder"/> in the system file manager. A folder that does not exist yet
    /// (nothing was extracted so far) falls back to its closest existing parent. Returns an error
    /// message, or <c>null</c> when it worked.
    /// </summary>
    private static string? OpenInFileManager(string folder)
    {
        while (folder.Length > 0 && !Directory.Exists(folder))
            folder = Path.GetDirectoryName(folder) ?? string.Empty;

        if (folder.Length == 0)
            return "NO FOLDER TO OPEN";

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? folder : OperatingSystem.IsMacOS() ? "open" : "xdg-open",
                UseShellExecute = true,
            };

            if (!OperatingSystem.IsWindows())
                start.ArgumentList.Add(folder);

            System.Diagnostics.Process.Start(start);
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "CANNOT OPEN THE FOLDER: " + ex.Message.ToUpperInvariant();
        }
    }

    /// <summary>Three lines for the dialog: where the ROM came from, what it is, and what to do next.</summary>
    private static string[] DescribeRom(string path, RomInfo info)
    {
        string source;
        if (RomFile.IsArchive(path))
        {
            string entry = RomFile.FindCartridge(path) ?? "?";
            source = "ZIP, UNPACKED AUTOMATICALLY: " + Path.GetFileName(entry);
        }
        else
        {
            source = "ROM FILE: " + Path.GetFileName(path);
        }

        string size = info.Size >= 1024 ? $"{info.Size / 1024} KB" : $"{info.Size} B";
        string mapper = RomMapperNames.Name(info.Mapper) + (info.Scc ? " + SCC" : string.Empty);
        string how = info.MapperSource == "database" ? "FOUND IN THE DATABASE" : "GUESSED FROM THE CODE";
        string header = info.HasHeader ? $"HEADER OK, INIT {info.InitAddress:X4}" : "NO AB HEADER";
        string detail = $"{size} · MAPPER {mapper} ({how}) · {header} · SHA-1 {info.Sha1[..8]}";

        string tip;
        if (!info.HasHeader)
            tip = "TIP: NO AB HEADER, THIS MAY NOT BE A NORMAL CARTRIDGE ROM";
        else if (info.Scc)
            tip = "TIP: SCC CARTRIDGE, KEEP BOTH CHIPS ON. FIND ALL SONGS LISTS EVERY SOUND";
        else if (info.MapperSource != "database" && info.Mapper != RomMapper.Linear)
            tip = "TIP: MAPPER WAS GUESSED. IF THE RESULT IS SILENT, PICK ANOTHER MAPPER BELOW";
        else
            tip = "TIP: CAPTURE RECORDS WHAT THE GAME PLAYS. FIND ALL SONGS LISTS EVERY SOUND";

        return new[] { source.ToUpperInvariant(), detail.ToUpperInvariant(), tip };
    }

    /// <summary>File name the current mode will produce (shown under the output row).</summary>
    private string FileNameForCurrentMode() => _extractMode switch
    {
        ExtractMode.Profile => OutputFileName("_" + Path.GetFileNameWithoutExtension(_extractProfile), ".msxaudio"),
        _ => OutputFileName("_capture", ExtractAsPackage() ? ".msxaudio" : ".vgm"),
    };

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
