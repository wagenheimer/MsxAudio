using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Wagenheimer.MsxAudio.Extraction;
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// The export assistant: 1 ROM, 2 METHOD (openMSX or the built-in extractor), 3 EXTRACT. Each step
/// says what it is for, and the app recommends a method after a short test run of the cartridge.
/// </summary>
public sealed partial class VisualizerGame
{
    // 0 = ROM, 1 = method, 2 = extract (options + run).
    private int _wizardStep;

    // Method: 0 = built-in (native, experimental), 1 = openMSX.
    private int _extractMethod;
    private bool _methodTouched;             // the user picked (or installed) a method: stop recommending

    // openMSX discovery and install.
    private string? _omsxPath;
    private bool _omsxChecked;
    private OpenMsxRelease? _omsxRelease;
    private string? _omsxError;
    private bool _omsxLookingUp;
    private bool _omsxInstalling;
    private long _omsxDone, _omsxTotal;
    private CancellationTokenSource? _omsxCts;

    // The short test run of the chosen cartridge (what it writes, which chips).
    private sealed record Preflight(string Path, int NoteWrites, bool Fm, bool Y8950, bool SccPlus, string? Error);

    private volatile Preflight? _preflight;
    private volatile bool _preflightRunning;
    private string _preflightPath = string.Empty;

    private readonly Rectangle[] _exStepHit = new Rectangle[3];
    private Rectangle _exBackHit, _exNextHit, _exChangeRomHit, _exCardNativeHit, _exCardOmsxHit,
        _exInstallHit, _exPastePathHit, _exCancelInstallHit;

    private static readonly string[] WizardStepNames = { "ROM", "METHOD", "EXTRACT" };

    private void ClearExtractHits()
    {
        _exRomHit = _exRomPasteHit = _exBrowseHit = _exModeCaptureHit = _exModeProfileHit = Rectangle.Empty;
        _exProfileHit = _exProfileBrowseHit = _exMapperHit = _exMinusHit = _exPlusHit = Rectangle.Empty;
        _exHzHit = _exAutoHit = _exKeysHit = _exOutHit = _exOutPasteHit = _exOutBrowseHit = Rectangle.Empty;
        _exOpenFolderHit = _exFmtAutoHit = _exFmtVgmHit = _exFmtPkgHit = _exPsgHit = _exSccHit = Rectangle.Empty;
        _exStartHit = _exScanHit = Rectangle.Empty;
        _exBackHit = _exNextHit = _exChangeRomHit = _exCardNativeHit = _exCardOmsxHit = Rectangle.Empty;
        _exInstallHit = _exPastePathHit = _exCancelInstallHit = Rectangle.Empty;
        Array.Fill(_exStepHit, Rectangle.Empty);
    }

    // ── Navigation ───────────────────────────────────────────────────────────────

    private bool RomReady => File.Exists(_extractRom) && _extractInfoError is null;

    private bool MethodReady => _extractMethod == 0 || _omsxPath is not null;

    private void GoToStep(int step)
    {
        if (_extractRunning)
            return;

        step = Math.Clamp(step, 0, 2);

        if (step >= 1 && !RomReady)
        {
            _extractError = "CHOOSE A ROM FIRST";
            return;
        }

        if (step >= 2 && !MethodReady)
        {
            _extractError = "INSTALL OPENMSX OR PICK THE BUILT-IN EXTRACTOR";
            return;
        }

        _wizardStep = step;
        _extractError = null;
        _extractField = step == 0 ? 0 : _extractMode == ExtractMode.Profile ? 2 : 1;

        if (step == 1)
            PrepareMethodStep();
    }

    private void GoNext() => GoToStep(_wizardStep + 1);

    /// <summary>Looks for openMSX and, when it is missing, which release the installer would fetch.</summary>
    private void PrepareMethodStep()
    {
        StartPreflight();

        if (!_omsxChecked)
        {
            _omsxChecked = true;
            string? saved = _settings.OpenMsxPath is { Length: > 0 } s ? OpenMsxLocator.Resolve(s) : null;
            _omsxPath = saved ?? OpenMsxLocator.Find();
        }

        if (_omsxPath is null && OpenMsxInstaller.IsSupported && _omsxRelease is null && !_omsxLookingUp)
        {
            _omsxLookingUp = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    _omsxRelease = await OpenMsxInstaller.GetLatestAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _omsxError = "CANNOT REACH GITHUB: " + ex.Message.ToUpperInvariant();
                }
                finally
                {
                    _omsxLookingUp = false;
                }
            });
        }

        // Start with the recommended method (the user can change it).
        if (!_methodTouched && _preflight is { Error: null } flight && flight.Path == _extractRom)
            _extractMethod = (flight.Fm || flight.Y8950 || flight.SccPlus) && (_omsxPath is not null || OpenMsxInstaller.IsSupported)
                ? 1
                : 0;
    }

    // ── Test run of the cartridge ────────────────────────────────────────────────

    /// <summary>Runs the cartridge for a few emulated seconds to see which chips it uses.</summary>
    private void StartPreflight()
    {
        if (_preflightPath == _extractRom)
            return;

        _preflightPath = _extractRom;
        _preflight = null;

        if (!File.Exists(_extractRom))
            return;

        string rom = _extractRom;
        _preflightRunning = true;

        _ = Task.Run(() =>
        {
            Preflight result;
            try
            {
                var options = new RomCaptureOptions { RomPath = rom, Seconds = 8, AutoPlay = true };
                RomCaptureResult capture = RomCapture.Run(options);
                result = new Preflight(rom, capture.NoteWrites, capture.UsesFm, capture.UsesY8950,
                    capture.UsesSccPlus, null);
            }
            catch (Exception ex)
            {
                result = new Preflight(rom, 0, false, false, false, ex.Message);
            }

            if (_preflightPath == rom)
                _preflight = result;

            _preflightRunning = false;
        });
    }

    private string RecommendationText()
    {
        if (_preflightRunning || _preflight is null || _preflight.Path != _extractRom)
            return "THE TEST RUN OF THE CARTRIDGE IS STILL GOING; A RECOMMENDATION APPEARS WHEN IT ENDS.";

        if (_preflight is not { Error: null } flight)
            return "THE TEST RUN COULD NOT FINISH, SO THERE IS NO RECOMMENDATION. OPENMSX IS THE SAFE CHOICE.";

        if (flight.Fm || flight.Y8950 || flight.SccPlus)
        {
            string chip = flight.Fm ? "FM (MSX-MUSIC)" : flight.Y8950 ? "MSX-AUDIO" : "SCC+";
            return $"RECOMMENDED: OPENMSX. THIS GAME USES {chip}, WHICH THE BUILT-IN EXTRACTOR CANNOT RECORD.";
        }

        return "RECOMMENDED: BUILT-IN. THIS GAME ONLY USES PSG/SCC, AND IT CAN FIND EVERY SONG BY ITSELF.";
    }

    private bool RecommendsOpenMsx =>
        _preflight is { Error: null } f && f.Path == _extractRom && (f.Fm || f.Y8950 || f.SccPlus);

    private bool RecommendsBuiltIn =>
        _preflight is { Error: null } f && f.Path == _extractRom && !(f.Fm || f.Y8950 || f.SccPlus);

    // ── openMSX install ──────────────────────────────────────────────────────────

    private void StartOpenMsxInstall()
    {
        if (_omsxInstalling || _omsxRelease is not { } release)
            return;

        _omsxError = null;
        _omsxInstalling = true;
        _omsxDone = 0;
        _omsxTotal = release.Size;
        var cts = new CancellationTokenSource();
        _omsxCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<(long Done, long Total)>(p =>
                {
                    _omsxDone = p.Done;
                    _omsxTotal = p.Total;
                });

                _omsxPath = await OpenMsxInstaller.InstallAsync(release, progress, cts.Token).ConfigureAwait(false);
                _extractMethod = 1;
                _methodTouched = true;
                _settings.OpenMsxPath = _omsxPath;
                _settings.Save();
            }
            catch (OperationCanceledException)
            {
                _omsxError = "DOWNLOAD CANCELLED";
            }
            catch (Exception ex)
            {
                _omsxError = "INSTALL FAILED: " + ex.Message.ToUpperInvariant();
            }
            finally
            {
                _omsxInstalling = false;
                cts.Dispose();
            }
        });
    }

    private void PasteOpenMsxPath()
    {
        string? text = Clipboard.TryReadText();
        string? exe = OpenMsxLocator.Resolve(text?.Split('\n')[0]);

        if (exe is null)
        {
            _omsxError = "COPY THE PATH OF OPENMSX.EXE (OR ITS FOLDER) FIRST, THEN PRESS THIS";
            return;
        }

        _omsxError = null;
        _omsxPath = exe;
        _extractMethod = 1;
        _settings.OpenMsxPath = exe;
        _settings.Save();
    }

    // ── Drawing: stepper and navigation ──────────────────────────────────────────

    private void DrawWizardStepper(int dx, int dy, int dw)
    {
        int x = dx + 16;

        for (int i = 0; i < WizardStepNames.Length; i++)
        {
            bool current = i == _wizardStep;
            bool reachable = i == 0 || RomReady;
            string label = $"{i + 1} {WizardStepNames[i]}";
            int width = PixelFont.Measure(label) + 24;

            _exStepHit[i] = new Rectangle(x, dy + 30, width, 18);
            DrawButton(_exStepHit[i], label, current, current ? Accent : reachable ? TextColor : Dim);

            x += width + 6;
            if (i < WizardStepNames.Length - 1)
            {
                Text(">", x - 1, dy + 36, Dim);
                x += 14;
            }
        }
    }

    private void DrawWizardNav(int dx, int dy, int dw, int dh, bool canNext, string nextLabel)
    {
        if (_wizardStep > 0)
        {
            _exBackHit = new Rectangle(dx + 16, dy + dh - 30, 90, 20);
            DrawButton(_exBackHit, "< BACK", false, Accent);
        }

        _exNextHit = new Rectangle(dx + dw - 136, dy + dh - 30, 120, 20);
        DrawButton(_exNextHit, nextLabel, canNext, canNext ? Accent : Dim);

        if (_extractError is { } error)
            Text(FitText(error, dw - 300), dx + 124, dy + dh - 24, Warn);
    }

    // ── Step 1: ROM ──────────────────────────────────────────────────────────────

    private void DrawStepRom(int dx, int dy, int dw, int dh)
    {
        int labelX = dx + 16;
        int fieldX = dx + 92;
        int fieldW = dw - 92 - 116;
        int pathW = fieldW - 66;

        Text("STEP 1 OF 3 - CHOOSE THE CARTRIDGE", labelX, dy + 64, Accent);
        Text("PICK A .ROM, OR THE .ZIP EXACTLY AS DOWNLOADED. TYPE OR PASTE THE PATH, OR PRESS BROWSE.",
            labelX, dy + 80, Dim);
        Text("A .ZIP IS OPENED FOR YOU: THERE IS NOTHING TO EXTRACT BY HAND.", labelX, dy + 92, Dim);

        Text("ROM", labelX, dy + 120, Dim);
        _exRomHit = new Rectangle(fieldX, dy + 114, pathW, 18);
        DrawPathField(_exRomHit, _extractRom, "(PASTE OR TYPE THE ROM / ZIP PATH, OR BROWSE)", _extractField == 0);

        _exRomPasteHit = new Rectangle(fieldX + pathW + 6, dy + 114, 60, 18);
        DrawButton(_exRomPasteHit, "PASTE", false, Accent);

        _exBrowseHit = new Rectangle(fieldX + fieldW + 8, dy + 114, 84, 18);
        DrawButton(_exBrowseHit, "BROWSE", false, Accent);

        RefreshExtractInfo();
        StartPreflight();

        bool missing = _extractRom.Length > 0 && !File.Exists(_extractRom);
        int y = dy + 150;

        if (_extractRom.Length == 0)
        {
            Text("WHAT HAPPENS NEXT", labelX, y, Accent);
            Text("1. THE APP LOOKS AT THE CARTRIDGE (SIZE, MAPPER, HEADER) AND TRIES IT FOR A FEW SECONDS.",
                labelX, y + 16, Dim);
            Text("2. YOU PICK HOW TO EXPORT: OPENMSX (EXACT, ALL CHIPS) OR THE BUILT-IN EXTRACTOR.",
                labelX, y + 28, Dim);
            Text("3. YOU PICK WHAT TO EXTRACT: WHAT PLAYS NOW, EVERY SONG, OR A READY-MADE PROFILE.",
                labelX, y + 40, Dim);
            DrawWizardNav(dx, dy, dw, dh, false, "NEXT >");
            return;
        }

        if (missing)
        {
            Text("FILE NOT FOUND", fieldX, y, Warn);
            DrawWizardNav(dx, dy, dw, dh, false, "NEXT >");
            return;
        }

        Text("THIS CARTRIDGE", labelX, y, Accent);

        if (_extractInfoError is not null)
        {
            Text(FitText(_extractInfoError.ToUpperInvariant(), dw - 40), labelX, y + 16, Warn);
        }
        else
        {
            for (int i = 0; i < _extractInfoLines.Length; i++)
                Text(FitText(_extractInfoLines[i], dw - 40), labelX, y + 16 + i * 12, i == 2 ? Accent : Dim);
        }

        // The test run: does it make music by itself, and which chips does it use?
        int ty = y + 66;
        Text("TEST RUN", labelX, ty, Accent);

        if (_preflightRunning || _preflight is null)
        {
            DrawSpinner(labelX + 6, ty + 22, 6);
            Text("RUNNING THE GAME FOR 8 SECONDS TO SEE WHICH CHIPS IT USES...", labelX + 22, ty + 18, Dim);
        }
        else if (_preflight.Error is { } error)
        {
            Text(FitText("COULD NOT RUN IT: " + error.ToUpperInvariant(), dw - 40), labelX, ty + 16, Warn);
        }
        else
        {
            var chips = new List<string>();
            if (_preflight.NoteWrites > 0) chips.Add("PSG/SCC");
            if (_preflight.Fm) chips.Add("FM (MSX-MUSIC)");
            if (_preflight.Y8950) chips.Add("MSX-AUDIO");
            if (_preflight.SccPlus) chips.Add("SCC+");

            Text(chips.Count > 0 ? "CHIPS SEEN: " + string.Join(" + ", chips)
                                 : "NO MUSIC IN THE FIRST 8 SECONDS (THE GAME MAY WAIT FOR A KEY PRESS)",
                labelX, ty + 16, chips.Count > 0 ? TextColor : Dim);

            Text(RecommendationText(), labelX, ty + 30, Accent);
        }

        DrawWizardNav(dx, dy, dw, dh, RomReady, "NEXT >");
    }

    // ── Step 2: method ───────────────────────────────────────────────────────────

    private void DrawStepMethod(int dx, int dy, int dw, int dh)
    {
        int labelX = dx + 16;

        Text("STEP 2 OF 3 - CHOOSE HOW TO EXPORT", labelX, dy + 64, Accent);
        Text("BOTH METHODS PRODUCE THE SAME FILES. THEY DIFFER IN WHICH CHIPS THEY HEAR AND HOW THEY WORK.",
            labelX, dy + 80, Dim);
        Text(FitText(RecommendationText(), dw - 32), labelX, dy + 92, Accent);

        const int top = 114, height = 296, gap = 12;
        int cardW = (dw - 32 - gap) / 2;
        var omsxRect = new Rectangle(dx + 16, dy + top, cardW, height);
        var nativeRect = new Rectangle(dx + 16 + cardW + gap, dy + top, cardW, height);

        _exCardOmsxHit = omsxRect;
        _exCardNativeHit = nativeRect;

        DrawMethodCard(omsxRect, "OPENMSX", "EXACT - EVERY CHIP", _extractMethod == 1, RecommendsOpenMsx,
            new[]
            {
                "A REAL MSX EMULATOR RUNS THE GAME.",
                "HEARS PSG, SCC, SCC+, FM (MSX-MUSIC) AND MSX-AUDIO.",
                "CAPTURE WHAT PLAYS, OR EXTRACT A PROFILE'S SONG LIST.",
                "NEEDS OPENMSX (FREE, ABOUT 9 MB).",
            });

        DrawMethodCard(nativeRect, "BUILT-IN", "EXPERIMENTAL - FAST", _extractMethod == 0, RecommendsBuiltIn,
            new[]
            {
                "A SMALL MSX BUILT INTO THIS APP, NOTHING TO INSTALL.",
                "HEARS PSG AND SCC ONLY (NO FM).",
                "CAN FIND EVERY SONG BY ITSELF, NO PROFILE NEEDED.",
                "NOT EVERY GAME RUNS ON IT YET.",
            });

        // Status inside the openMSX card.
        int sx = omsxRect.X + 14;
        int sy = omsxRect.Y + 150;
        int sw = omsxRect.Width - 28;

        if (_omsxPath is not null)
        {
            Text("INSTALLED", sx, sy, SccColor);
            Text(FitTextLeft(_omsxPath.ToUpperInvariant(), sw), sx, sy + 14, Dim);
            Text("READY TO USE.", sx, sy + 28, Dim);
        }
        else if (_omsxInstalling)
        {
            DrawSpinner(sx + 6, sy + 6, 6);
            Text("DOWNLOADING OPENMSX...", sx + 22, sy + 2, PsgColor);

            float filled = _omsxTotal > 0 ? Math.Clamp(_omsxDone / (float)_omsxTotal, 0f, 1f) : 0f;
            Rect(sx, sy + 22, sw, 8, Grid);
            Rect(sx, sy + 22, (int)(sw * filled), 8, PsgColor);
            Text($"{_omsxDone / 1048576.0:0.0} / {_omsxTotal / 1048576.0:0.0} MB", sx, sy + 36, Dim);

            _exCancelInstallHit = new Rectangle(sx, sy + 54, 80, 18);
            DrawButton(_exCancelInstallHit, "CANCEL", false, Warn);
        }
        else
        {
            if (OpenMsxInstaller.IsSupported)
            {
                if (_omsxRelease is { } release)
                {
                    Text(FitText($"LATEST: {release.AssetName.ToUpperInvariant()}", sw), sx, sy, Dim);
                    Text($"{release.Size / 1048576.0:0.0} MB FROM GITHUB.COM/OPENMSX, UNPACKED IN YOUR USER FOLDER.",
                        sx, sy + 12, Dim);
                    Text("NO ADMIN RIGHTS. NOTHING ELSE ON THE PC IS CHANGED.", sx, sy + 24, Dim);

                    _exInstallHit = new Rectangle(sx, sy + 44, sw, 20);
                    DrawButton(_exInstallHit, $"DOWNLOAD AND INSTALL ({release.Size / 1048576.0:0.0} MB)", true, Accent);
                }
                else
                {
                    if (_omsxLookingUp)
                        DrawSpinner(sx + 6, sy + 6, 6);

                    Text(_omsxLookingUp ? "CHECKING THE LATEST RELEASE..." : "COULD NOT CHECK THE LATEST RELEASE.",
                        sx + (_omsxLookingUp ? 22 : 0), sy + 2, Dim);
                }
            }
            else
            {
                Text("AUTOMATIC INSTALL IS FOR WINDOWS 64-BIT.", sx, sy, Dim);
                Text("INSTALL OPENMSX FROM OPENMSX.ORG, THEN PASTE ITS PATH BELOW.", sx, sy + 12, Dim);
            }

            _exPastePathHit = new Rectangle(sx, sy + 72, sw, 18);
            DrawButton(_exPastePathHit, "I ALREADY HAVE IT: PASTE ITS PATH", false, Accent);
        }

        if (_omsxError is { } error)
            Text(FitText(error, sw), sx, sy + 98, Warn);

        // Status inside the built-in card.
        Text("READY", nativeRect.X + 14, nativeRect.Y + 150, SccColor);
        Text("BUILT IN, NOTHING TO INSTALL.", nativeRect.X + 14, nativeRect.Y + 164, Dim);

        DrawWizardNav(dx, dy, dw, dh, MethodReady, "NEXT >");
    }

    private void DrawMethodCard(Rectangle rect, string title, string tagline, bool selected, bool recommended,
        string[] lines)
    {
        Rect(rect, selected ? Accent : Grid);
        Rect(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4, PanelColor);

        Text(title, rect.X + 14, rect.Y + 14, selected ? Accent : TextColor, 2);
        Text(tagline, rect.X + 14, rect.Y + 36, Dim);

        if (recommended)
        {
            var badge = new Rectangle(rect.Right - 116, rect.Y + 12, 102, 16);
            Rect(badge, SccColor);
            Text("RECOMMENDED", badge.X + 8, badge.Y + 5, PanelColor);
        }

        for (int i = 0; i < lines.Length; i++)
            Text(FitText("- " + lines[i], rect.Width - 28), rect.X + 14, rect.Y + 62 + i * 18, TextColor);

        Rect(rect.X + 14, rect.Y + 140, rect.Width - 28, 1, Grid);

        if (selected)
            Text("SELECTED", rect.X + 14, rect.Bottom - 18, Accent);
        else
            Text("CLICK TO SELECT", rect.X + 14, rect.Bottom - 18, Dim);
    }

    // ── Wizard clicks ────────────────────────────────────────────────────────────

    /// <summary>Handles the wizard's own buttons. True when the click was one of them.</summary>
    private bool HandleWizardClick(Point p)
    {
        for (int i = 0; i < _exStepHit.Length; i++)
        {
            if (_exStepHit[i].Contains(p))
            {
                GoToStep(i);
                return true;
            }
        }

        if (_exBackHit.Contains(p))
        {
            GoToStep(_wizardStep - 1);
            return true;
        }

        if (_exNextHit.Contains(p))
        {
            GoNext();
            return true;
        }

        if (_exChangeRomHit.Contains(p))
        {
            GoToStep(0);
            return true;
        }

        if (_exCardOmsxHit.Contains(p) && !_exInstallHit.Contains(p) && !_exPastePathHit.Contains(p) &&
            !_exCancelInstallHit.Contains(p))
        {
            _extractMethod = 1;
            _methodTouched = true;
            return true;
        }

        if (_exCardNativeHit.Contains(p))
        {
            _extractMethod = 0;
            _methodTouched = true;
            return true;
        }

        if (_exInstallHit.Contains(p))
        {
            StartOpenMsxInstall();
            return true;
        }

        if (_exPastePathHit.Contains(p))
        {
            PasteOpenMsxPath();
            return true;
        }

        if (_exCancelInstallHit.Contains(p))
        {
            _omsxCts?.Cancel();
            return true;
        }

        return false;
    }

    // ── openMSX jobs ─────────────────────────────────────────────────────────────

    /// <summary>Whatever plays in openMSX during the window, every chip the machine has.</summary>
    private string? RunOpenMsxCaptureJob(string rom, string omsx, string keys, int seconds, bool package,
        bool autoPress, CancellationToken token, string output)
    {
        List<KeyPress> script = autoPress ? RomCapture.AutoPlayScript(seconds) : KeyPress.ParseScript(keys);
        string work = Path.Combine(Path.GetTempPath(), "msxaudio-openmsx", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        try
        {
            string vgmFile = Path.Combine(work, "capture.vgm");
            OpenMsxCapture.Record(omsx, RomFile.Resolve(rom), vgmFile, seconds, script, work, AddExtractLog, token);

            if (package)
            {
                using var library = new MsxAudioLibrary(Path.GetFileNameWithoutExtension(rom));
                library.Manifest.Source = $"openMSX capture ({Path.GetFileName(rom)})";
                library.AddSong("capture", "capture", VgmFile.Load(vgmFile), loop: false, originalId: null,
                    tags: new[] { "capture", "openmsx" });
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                library.Save(output);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(vgmFile, output, overwrite: true);
            }

            AddExtractLog($"saved {output}");
            return output;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { /* temp folder: harmless */ }
        }
    }

    /// <summary>A curated profile played in openMSX: every id, every chip.</summary>
    private string? RunOpenMsxProfileJob(string rom, string omsx, string profilePath, CancellationToken token,
        string output)
    {
        RomExtractionProfile profile = RomExtractionProfiles.Load(profilePath);
        string work = Path.Combine(Path.GetTempPath(), "msxaudio-openmsx", Guid.NewGuid().ToString("N")[..8]);

        try
        {
            AddExtractLog($"profile {profile.Name} ({profile.Ids.Count} id(s)) in openMSX");

            ExtractionResult result = OpenMsxRomExtractor.Extract(
                new RomExtractionOptions
                {
                    RomPath = RomFile.Resolve(rom),
                    OpenMsxPath = omsx,
                    Profile = profile,
                    WorkDirectory = work,
                },
                AddExtractLog);

            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            result.Library.Save(output);
            AddExtractLog($"saved {result.Library.Songs.Count} songs + {result.Library.Clips.Count} effects");
            return output;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { /* temp folder: harmless */ }
        }
    }
}
