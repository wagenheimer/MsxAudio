using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using NAudio.Lame;
using NAudio.Wave;
using Wagenheimer.MsxAudio.Export;
using Wagenheimer.MsxAudio.Mixing;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Visualizer;

/// <summary>
/// Export dialog: the current song (or every song in the list) as WAV, MP3 and/or MIDI. WAV and MP3
/// are rendered with the mix in use; MIDI is the notes themselves, for a DAW.
/// </summary>
public sealed partial class VisualizerGame
{
    private bool _expWav = true, _expMp3, _expMidi;
    private bool _expQuantize = true, _expChords = true;     // MIDI: notes on a 16th grid; arpeggios as held chords
    private int _expScope;                              // 0 = this song, 1 = every song in the list
    private int _expLoops = 2;
    private int _expFade = 3;
    private int _expBitrate = 192;
    private string _expFolder = string.Empty;
    private bool _expFolderAuto = true;
    private bool _expTyping;

    private bool _expRunning;
    private Task? _expTask;
    private CancellationTokenSource? _expCts;
    private volatile int _expDone, _expTotal;
    private string? _expMessage;
    private bool _expFailed;
    private string _expWorkingOn = string.Empty;
    private DateTime _expStarted;
    private string? _expFinishedFolder;

    private Rectangle _expCloseHit, _expThisHit, _expAllHit, _expWavHit, _expMp3Hit, _expMidiHit, _expFolderHit,
        _expPasteHit, _expOpenHit, _expStartHit, _expCancelHit, _expLoopMinusHit, _expLoopPlusHit,
        _expFadeMinusHit, _expFadePlusHit, _expQuantizeHit, _expChordsHit;
    private readonly Rectangle[] _expBitrateHit = new Rectangle[4];

    private static readonly int[] Bitrates = { 128, 192, 256, 320 };

    private bool CanExport => _tracks.Count > 0 && _player is not null;

    private void OpenExportDialog()
    {
        if (!CanExport)
        {
            _status = "LOAD A SONG FIRST";
            _statusTimer = 2f;
            return;
        }

        _expMessage = null;
        _expFailed = false;
        _expFinishedFolder = null;
        _expTyping = false;

        if (_expFolderAuto)
            _expFolder = DefaultExportFolder();

        _dialog = DialogKind.Export;
    }

    private string DefaultExportFolder()
    {
        string source = _selected >= 0 && _selected < _tracks.Count ? _tracks[_selected].Path : _scanRoot;
        string folder = Directory.Exists(source) ? source : Path.GetDirectoryName(source) ?? _scanRoot;
        return Path.Combine(folder, "export");
    }

    // ── Input ────────────────────────────────────────────────────────────────────

    private void OnExportTextInput(char character)
    {
        if (_dialog != DialogKind.Export || !_expTyping || _expRunning || char.IsControl(character))
            return;

        _expFolder += character;
        _expFolderAuto = false;
    }

    private void HandleExportInput()
    {
        if (Pressed(Keys.Escape))
        {
            if (_expRunning)
                _expCts?.Cancel();
            else
                _dialog = DialogKind.None;

            return;
        }

        if (_expRunning)
            return;

        bool ctrl = _keys.IsKeyDown(Keys.LeftControl) || _keys.IsKeyDown(Keys.RightControl);

        if (_expTyping)
        {
            if (ctrl && Pressed(Keys.V))
            {
                PasteExportFolder();
                return;
            }

            if (Pressed(Keys.Back) && _expFolder.Length > 0)
            {
                _expFolder = ctrl ? string.Empty : _expFolder[..^1];
                _expFolderAuto = _expFolder.Length == 0;
                if (_expFolderAuto)
                    _expFolder = DefaultExportFolder();

                return;
            }
        }

        if (Pressed(Keys.Enter))
        {
            StartExport();
            return;
        }

        bool pressed = _mouse.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
        if (!pressed)
            return;

        Point p = MouseDesign();

        if (_expCloseHit.Contains(p) || _expCancelHit.Contains(p)) { _dialog = DialogKind.None; return; }
        if (_expThisHit.Contains(p)) { _expScope = 0; return; }
        if (_expAllHit.Contains(p)) { _expScope = 1; return; }
        if (_expWavHit.Contains(p)) { _expWav = !_expWav; return; }
        if (_expMp3Hit.Contains(p)) { _expMp3 = !_expMp3; return; }
        if (_expMidiHit.Contains(p)) { _expMidi = !_expMidi; return; }
        if (_expQuantizeHit.Contains(p)) { _expQuantize = !_expQuantize; return; }
        if (_expChordsHit.Contains(p)) { _expChords = !_expChords; return; }
        if (_expLoopMinusHit.Contains(p)) { _expLoops = Math.Max(1, _expLoops - 1); return; }
        if (_expLoopPlusHit.Contains(p)) { _expLoops = Math.Min(8, _expLoops + 1); return; }
        if (_expFadeMinusHit.Contains(p)) { _expFade = Math.Max(0, _expFade - 1); return; }
        if (_expFadePlusHit.Contains(p)) { _expFade = Math.Min(15, _expFade + 1); return; }
        if (_expPasteHit.Contains(p)) { PasteExportFolder(); return; }
        if (_expStartHit.Contains(p)) { StartExport(); return; }

        if (_expOpenHit.Contains(p))
        {
            string? error = OpenInFileManager(_expFinishedFolder ?? _expFolder);
            if (error is not null) { _expMessage = error; _expFailed = true; }
            return;
        }

        for (int i = 0; i < _expBitrateHit.Length; i++)
        {
            if (_expBitrateHit[i].Contains(p))
            {
                _expBitrate = Bitrates[i];
                return;
            }
        }

        _expTyping = _expFolderHit.Contains(p);
    }

    private void PasteExportFolder()
    {
        if (Clipboard.TryReadText() is not { Length: > 0 } text)
            return;

        string line = text.Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim().Trim('"');

        // A file was copied: put the exports in its folder.
        if (!Directory.Exists(line) && Path.HasExtension(line))
            line = Path.GetDirectoryName(line) ?? line;

        _expFolder = line;
        _expFolderAuto = false;
        _expTyping = true;
    }

    // ── Job ──────────────────────────────────────────────────────────────────────

    private sealed record ExportItem(string Name, VgmFile Vgm);

    private void StartExport()
    {
        if (_expRunning)
            return;

        if (!_expWav && !_expMp3 && !_expMidi)
        {
            _expMessage = "PICK AT LEAST ONE FORMAT";
            _expFailed = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(_expFolder))
        {
            _expMessage = "CHOOSE A FOLDER";
            _expFailed = true;
            return;
        }

        // The songs are loaded here, on the UI thread: the package cache is not made for threads.
        var items = new List<ExportItem>();

        try
        {
            if (_expScope == 0 || _tracks.Count == 1)
            {
                VgmEntry entry = _tracks[Math.Clamp(_selected, 0, _tracks.Count - 1)];
                items.Add(new ExportItem(ExportName(entry), LoadVgm(entry.Path, entry.SongId)));
            }
            else
            {
                foreach (VgmEntry entry in _tracks)
                    items.Add(new ExportItem(ExportName(entry), LoadVgm(entry.Path, entry.SongId)));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _expMessage = "CANNOT READ A SONG: " + ex.Message.ToUpperInvariant();
            _expFailed = true;
            return;
        }

        AudioProfile profile = _profile;
        string folder = _expFolder;
        bool wav = _expWav, mp3 = _expMp3, midi = _expMidi;
        var midiOptions = new MidiOptions { Quantize = _expQuantize, ArpeggiosAsChords = _expChords };
        int loops = _expLoops, fade = _expFade, bitrate = _expBitrate;

        _expMessage = null;
        _expFailed = false;
        _expFinishedFolder = null;
        _expDone = 0;
        _expTotal = items.Count;
        _expStarted = DateTime.UtcNow;
        _expRunning = true;

        var cts = new CancellationTokenSource();
        _expCts = cts;

        _expTask = Task.Run(() =>
        {
            int files = 0;

            try
            {
                Directory.CreateDirectory(folder);
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (ExportItem item in items)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    _expWorkingOn = item.Name;

                    string stem = UniqueStem(folder, item.Name, used);

                    if (wav || mp3)
                    {
                        RenderedAudio audio = AudioExporter.Render(item.Vgm, profile, 48000, loops, fade);

                        if (wav)
                        {
                            File.WriteAllBytes(Path.Combine(folder, stem + ".wav"), AudioExporter.ToWav(audio));
                            files++;
                        }

                        if (mp3)
                        {
                            WriteMp3(Path.Combine(folder, stem + ".mp3"), audio, item.Name, bitrate);
                            files++;
                        }
                    }

                    if (midi)
                    {
                        File.WriteAllBytes(Path.Combine(folder, stem + ".mid"), MidiExporter.Export(item.Vgm, item.Name, midiOptions));
                        files++;
                    }

                    _expDone++;
                }

                _expMessage = $"DONE: {files} FILE(S) IN {folder}";
                _expFinishedFolder = folder;
            }
            catch (OperationCanceledException)
            {
                _expMessage = $"CANCELLED AFTER {files} FILE(S)";
                _expFailed = true;
                _expFinishedFolder = folder;
            }
            catch (Exception ex)
            {
                _expMessage = "EXPORT FAILED: " + ex.Message.ToUpperInvariant();
                _expFailed = true;
            }
            finally
            {
                _expRunning = false;
                cts.Dispose();
            }
        });
    }

    private static string ExportName(VgmEntry entry) =>
        entry.SongId is not null
            ? Path.GetFileNameWithoutExtension(entry.Source ?? entry.Path) + "_" + entry.Name
            : entry.Name;

    private static string UniqueStem(string folder, string name, HashSet<string> used)
    {
        string clean = name;
        foreach (char invalid in Path.GetInvalidFileNameChars())
            clean = clean.Replace(invalid, '_');

        clean = clean.Trim().TrimEnd('.');
        if (clean.Length == 0)
            clean = "song";

        string stem = clean;
        for (int n = 2; !used.Add(stem); n++)
            stem = $"{clean}_{n}";

        return stem;
    }

    /// <summary>Encodes with LAME. Fails with a clear message where its native library is missing.</summary>
    private static void WriteMp3(string path, RenderedAudio audio, string title, int bitrate)
    {
        try
        {
            var tag = new ID3TagData { Title = title, Album = "MSXScope", Comment = "Exported from MSX chip registers" };
            using var writer = new LameMP3FileWriter(path, new WaveFormat(audio.SampleRate, 16, audio.Channels),
                bitrate, tag);

            byte[] bytes = audio.ToPcmBytes();
            writer.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException("the MP3 encoder (LAME) is not available on this system: " + ex.Message);
        }
    }

    // ── Drawing ──────────────────────────────────────────────────────────────────

    private void DrawExportDialog()
    {
        const int dw = 700, dh = 452;
        int dx = (VW - dw) / 2, dy = 110;

        _expCloseHit = _expThisHit = _expAllHit = _expWavHit = _expMp3Hit = _expMidiHit = _expFolderHit =
            _expPasteHit = _expOpenHit = _expStartHit = _expCancelHit = Rectangle.Empty;
        _expLoopMinusHit = _expLoopPlusHit = _expFadeMinusHit = _expFadePlusHit = Rectangle.Empty;
        _expQuantizeHit = _expChordsHit = Rectangle.Empty;
        Array.Fill(_expBitrateHit, Rectangle.Empty);

        Panel(dx, dy, dw, dh);
        Rect(dx, dy, dw, 24, PanelColor);
        Text("EXPORT AUDIO", dx + 12, dy + 8, Accent);

        _expCloseHit = new Rectangle(dx + dw - 24, dy + 4, 16, 16);
        DrawButton(_expCloseHit, "X", false, Warn);

        int labelX = dx + 16, fieldX = dx + 96;
        int y = dy + 40;

        // What to export.
        Text("SONGS", labelX, y + 6, Dim);
        string current = _selected >= 0 && _selected < _tracks.Count ? _tracks[_selected].Name.ToUpperInvariant() : "-";
        _expThisHit = new Rectangle(fieldX, y, 230, 18);
        DrawButton(_expThisHit, "THIS SONG: " + FitText(current, 150), _expScope == 0, Accent);
        _expAllHit = new Rectangle(fieldX + 238, y, 190, 18);
        DrawButton(_expAllHit, $"ALL IN THE LIST ({_tracks.Count})", _expScope == 1, Accent);

        // Formats (any combination).
        y += 32;
        Text("FORMAT", labelX, y + 6, Dim);
        _expWavHit = new Rectangle(fieldX, y, 64, 18);
        DrawButton(_expWavHit, "WAV", _expWav, Accent);
        _expMp3Hit = new Rectangle(fieldX + 70, y, 64, 18);
        DrawButton(_expMp3Hit, "MP3", _expMp3, Accent);
        _expMidiHit = new Rectangle(fieldX + 140, y, 64, 18);
        DrawButton(_expMidiHit, "MIDI", _expMidi, Accent);

        Text("WAV LOSSLESS · MP3 SMALL · MIDI = THE NOTES, FOR A DAW", fieldX + 216, y + 6, Dim);

        // MIDI clean-up: what turns the raw register stream into something playable and editable.
        y += 26;
        Text("MIDI", labelX, y + 6, _expMidi ? Dim : Grid);
        _expQuantizeHit = new Rectangle(fieldX, y, 150, 18);
        DrawButton(_expQuantizeHit, "TEMPO + 16TH GRID", _expQuantize, _expMidi ? Accent : Dim);
        _expChordsHit = new Rectangle(fieldX + 158, y, 170, 18);
        DrawButton(_expChordsHit, "ARPEGGIOS AS CHORDS", _expChords, _expMidi ? Accent : Dim);
        Text("BPM FOUND · VIBRATO = BEND", fieldX + 338, y + 6, Dim);

        // The mix.
        y += 28;
        Text("MIX", labelX, y, Dim);
        Text($"THE MIX IN USE: {_profile.Name.ToUpperInvariant()}  (WAV AND MP3; MIDI IS THE SAME WHATEVER THE MIX)",
            fieldX, y, TextColor);

        // Loops and fade.
        y += 22;
        Text("LOOPS", labelX, y + 6, Dim);
        _expLoopMinusHit = new Rectangle(fieldX, y, 18, 18);
        DrawButton(_expLoopMinusHit, "-", false, Dim);
        Text(_expLoops.ToString(), fieldX + 28, y + 6, TextColor);
        _expLoopPlusHit = new Rectangle(fieldX + 48, y, 18, 18);
        DrawButton(_expLoopPlusHit, "+", false, Dim);

        Text("FADE-OUT", fieldX + 90, y + 6, Dim);
        _expFadeMinusHit = new Rectangle(fieldX + 150, y, 18, 18);
        DrawButton(_expFadeMinusHit, "-", false, Dim);
        Text($"{_expFade} S", fieldX + 176, y + 6, TextColor);
        _expFadePlusHit = new Rectangle(fieldX + 206, y, 18, 18);
        DrawButton(_expFadePlusHit, "+", false, Dim);

        Text("INTRO, THEN THE LOOP THIS MANY TIMES", fieldX + 240, y + 6, Dim);

        // MP3 quality.
        y += 28;
        Text("MP3", labelX, y + 6, _expMp3 ? Dim : Grid);
        for (int i = 0; i < Bitrates.Length; i++)
        {
            _expBitrateHit[i] = new Rectangle(fieldX + i * 74, y, 68, 18);
            DrawButton(_expBitrateHit[i], Bitrates[i] + " KBPS", _expBitrate == Bitrates[i], _expMp3 ? Accent : Dim);
        }

        // Where.
        y += 32;
        Text("FOLDER", labelX, y + 6, Dim);
        int pathW = dw - 96 - 16 - 66 - 8 - 96;
        _expFolderHit = new Rectangle(fieldX, y, pathW, 18);
        DrawPathField(_expFolderHit, _expFolder, "(TYPE OR PASTE A FOLDER)", _expTyping);
        _expPasteHit = new Rectangle(fieldX + pathW + 6, y, 60, 18);
        DrawButton(_expPasteHit, "PASTE", false, Accent);
        _expOpenHit = new Rectangle(fieldX + pathW + 72, y, 100, 18);
        DrawButton(_expOpenHit, "OPEN FOLDER", false, Accent);

        // Go.
        y += 40;
        _expStartHit = new Rectangle(fieldX, y, 120, 22);
        DrawButton(_expStartHit, _expRunning ? "EXPORTING..." : "EXPORT", !_expRunning, _expRunning ? Dim : Accent);
        _expCancelHit = new Rectangle(fieldX + 128, y, 90, 22);
        DrawButton(_expCancelHit, "CLOSE", false, Dim);

        int total = Math.Max(1, _expTotal);
        float filled = _expRunning ? Math.Clamp(_expDone / (float)total, 0f, 1f) : _expFinishedFolder is not null && !_expFailed ? 1f : 0f;

        int barX = fieldX + 240;
        if (_expRunning)
        {
            DrawSpinner(barX + 8, y + 11, 7);
            barX += 24;
        }

        int barW = dx + dw - 16 - barX - 50;
        Rect(barX, y + 7, barW, 8, Grid);
        Rect(barX, y + 7, (int)(barW * filled), 8, _expRunning ? PsgColor : Accent);
        Text($"{(int)(filled * 100)}%", barX + barW + 10, y + 7, _expRunning ? TextColor : Dim);

        y += 36;
        if (_expRunning)
        {
            TimeSpan elapsed = DateTime.UtcNow - _expStarted;
            Text(FitText($"WORKING ON {_expWorkingOn.ToUpperInvariant()}  ({_expDone}/{_expTotal})  " +
                         $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}  (ESC STOPS)", dw - 32),
                labelX, y, PsgColor);
        }
        else if (_expMessage is { } message)
        {
            Text(FitText(message, dw - 32), labelX, y, _expFailed ? Warn : SccColor);
        }

        Text("ENTER EXPORTS · ESC CLOSES · FILES ARE NAMED AFTER THE SONGS (A PACK'S NAME IS ADDED FOR ITS SONGS)",
            labelX, dy + dh - 20, Dim);
    }
}
