using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>A key or joystick button held during a capture, at a given time.</summary>
/// <param name="Seconds">When the button is pressed (the capture releases it 12 frames later).</param>
/// <param name="Row">Keyboard row (0..8), or 0x0F for the joystick/PSG port A.</param>
/// <param name="Mask">Bit inside the row: 0..7 (7..0 for raw matrix bits).</param>
public readonly record struct KeyPress(double Seconds, int Row, int Mask)
{
    /// <summary>Joystick port A (PSG register 14) row.</summary>
    public const int JoystickRow = 0x0F;

    /// <summary>
    /// Known names, so a capture script reads as "START@2" instead of "15:4@2". Only the joystick is
    /// named: its bits are fixed by the PSG port A wiring, whereas keyboard rows vary in folklore.
    /// </summary>
    private static readonly Dictionary<string, (int Row, int Mask)> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["START"] = (JoystickRow, 4),
        ["FIRE"] = (JoystickRow, 4),
        ["TRIGGER-A"] = (JoystickRow, 4),
        ["A"] = (JoystickRow, 4),
        ["TRIGGER-B"] = (JoystickRow, 5),
        ["B"] = (JoystickRow, 5),
        ["UP"] = (JoystickRow, 0),
        ["DOWN"] = (JoystickRow, 1),
        ["LEFT"] = (JoystickRow, 2),
        ["RIGHT"] = (JoystickRow, 3),
    };

    /// <summary>
    /// Parses <c>name@seconds</c> (e.g. <c>START@2</c>) or <c>row:mask@seconds</c>
    /// (e.g. <c>8:1@2</c> = keyboard row 8, bit 1).
    /// </summary>
    public static bool TryParse(string? text, out KeyPress press)
    {
        press = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] parts = text.Trim().Split('@', 2);
        double seconds = 1;
        if (parts.Length > 1 &&
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            return false;

        string key = parts[0].Trim();

        if (Names.TryGetValue(key, out var named))
        {
            press = new KeyPress(Math.Max(0, seconds), named.Row, named.Mask);
            return true;
        }

        string[] rowMask = key.Split(':');
        if (rowMask.Length != 2 ||
            !int.TryParse(rowMask[0], out int row) ||
            !int.TryParse(rowMask[1], out int mask))
            return false;

        press = new KeyPress(Math.Max(0, seconds), row, mask);
        return true;
    }

    /// <summary>Parses a comma-separated script (<c>"START@2, RIGHT@4"</c>).</summary>
    public static List<KeyPress> ParseScript(string? script)
    {
        var list = new List<KeyPress>();
        if (string.IsNullOrWhiteSpace(script))
            return list;

        foreach (string part in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParse(part, out KeyPress press))
                list.Add(press);
        }

        return list;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        string key = (Row, Mask) switch
        {
            (JoystickRow, 4) => "START",
            (JoystickRow, 5) => "TRIGGER-B",
            (JoystickRow, 0) => "UP",
            (JoystickRow, 1) => "DOWN",
            (JoystickRow, 2) => "LEFT",
            (JoystickRow, 3) => "RIGHT",
            _ => $"row {Row} bit {Mask}",
        };

        return $"{key} at {Seconds:0.##}s";
    }
}

/// <summary>Options for <see cref="RomCapture"/>.</summary>
public sealed class RomCaptureOptions
{
    /// <summary>Cartridge image to boot.</summary>
    public required string RomPath { get; init; }

    /// <summary>Mapper; <see cref="RomMapper.Auto"/> picks one from the ROM size.</summary>
    public RomMapper Mapper { get; init; } = RomMapper.Auto;

    /// <summary>How long to record, in seconds of emulated 60 Hz frames.</summary>
    public double Seconds { get; init; } = 20;

    /// <summary>Buttons to press while recording, in order.</summary>
    public List<KeyPress> Keys { get; } = new();

    /// <summary>Instruction budget per frame (the hook itself may span frames).</summary>
    public int FrameBudget { get; init; } = 200_000;

    /// <summary>Record PSG writes (default true).</summary>
    public bool CapturePsg { get; init; } = true;

    /// <summary>Record SCC writes (default true).</summary>
    public bool CaptureScc { get; init; } = true;

    /// <summary>Video refresh rate: 60 (NTSC, default) or 50 (PAL). Sets the tempo of the capture.</summary>
    public double RefreshRate { get; init; } = 60;

    /// <summary>
    /// When no <see cref="Keys"/> are given, tap the fire button and the space bar every few seconds so
    /// the game leaves its title/menu screens by itself.
    /// </summary>
    public bool AutoPlay { get; init; }
}

/// <summary>What a capture produced.</summary>
public sealed class RomCaptureResult
{
    /// <summary>The recorded register writes.</summary>
    public required VgmFile Vgm { get; init; }

    /// <summary>Mapper the cartridge resolved to.</summary>
    public RomMapper Mapper { get; init; }

    /// <summary>True when the SCC window is enabled for this cartridge.</summary>
    public bool Scc { get; init; }

    /// <summary>True when the cartridge installed an interrupt handler.</summary>
    public bool Hooked { get; init; }

    /// <summary>Interrupt hook address (0 when none).</summary>
    public ushort Hook { get; init; }

    /// <summary>Loop found in the recording (start and duration in seconds), when any.</summary>
    public (double Start, double Duration)? Loop { get; init; }

    /// <summary>
    /// Register writes that carry pitch (PSG tone period, SCC frequency/key-on). Near zero means the
    /// recording only has mixer/volume traffic — the game did not start its music.
    /// </summary>
    public int NoteWrites { get; init; }

    /// <summary>Why the capture may be silent or incomplete (unsupported chips, BIOS stubs, boot warnings).</summary>
    public IReadOnlyList<string> Diagnosis { get; init; } = Array.Empty<string>();

    /// <summary>The game wrote to MSX-MUSIC (YM2413): this extractor does not record it, openMSX does.</summary>
    public bool UsesFm { get; init; }

    /// <summary>The game wrote to MSX-AUDIO (Y8950).</summary>
    public bool UsesY8950 { get; init; }

    /// <summary>The game wrote to the SCC+ registers.</summary>
    public bool UsesSccPlus { get; init; }

    /// <summary>Refresh rate used (50 or 60).</summary>
    public double RefreshRate { get; init; } = 60;
}

/// <summary>
/// Records whatever a cartridge plays, without a profile: it boots the ROM on the in-process machine
/// (<see cref="MiniMsx"/>), optionally presses buttons, and captures the PSG/SCC writes as a VGM.
/// </summary>
/// <remarks>
/// This is the generic path — it works for any cartridge because MSX has no standard music format:
/// the game's own driver produces the register stream. A curated profile
/// (<see cref="DirectRomExtractor"/>) is still the way to enumerate every song by id.
/// </remarks>
public static class RomCapture
{
    /// <summary>Runs the capture on the calling thread.</summary>
    /// <param name="options">ROM, mapper, duration and key script.</param>
    /// <param name="log">Receives human-readable progress lines.</param>
    /// <param name="progress">Receives (frame, total frames), useful for a progress bar.</param>
    /// <param name="token">Cancels the run.</param>
    public static RomCaptureResult Run(
        RomCaptureOptions options,
        Action<string>? log = null,
        Action<int, int>? progress = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(options.RomPath))
            throw new FileNotFoundException("ROM not found", options.RomPath);

        string romPath = RomFile.Resolve(options.RomPath);
        byte[] rom = File.ReadAllBytes(romPath);
        RomMapper requested = options.Mapper == RomMapper.Auto
            ? RomInfo.Analyze(rom).Mapper
            : options.Mapper;

        var machine = new MiniMsx(rom, requested)
        {
            CapturePsg = options.CapturePsg,
            CaptureScc = options.CaptureScc,
            RefreshRate = options.RefreshRate,
        };

        log?.Invoke($"booting {Path.GetFileName(romPath)} ({rom.Length / 1024} KB)...");
        bool hooked = machine.Boot();
        var (mapper, scc) = machine.CartridgeInfo;

        log?.Invoke($"mapper={RomMapperNames.Name(mapper)}{(scc ? " +SCC" : "")}, " +
                    $"hook={(machine.InterruptHook == 0 ? "none" : machine.KeyHook != 0 ? $"{machine.KeyHook:X4}" : $"{machine.TimerHook:X4}")}");

        if (!hooked)
            log?.Invoke("warning: no interrupt hook installed; the driver may never advance");

        machine.ResetCapture();

        int fps = machine.FramesPerSecond;
        int frames = Math.Max(1, (int)Math.Round(options.Seconds * fps));
        var presses = new List<(int Frame, KeyPress Press)>();

        IEnumerable<KeyPress> script = options.Keys.Count == 0 && options.AutoPlay
            ? AutoPlayScript(options.Seconds)
            : options.Keys;

        foreach (KeyPress press in script)
        {
            presses.Add(((int)Math.Round(press.Seconds * fps), press));
            log?.Invoke($"press {press}");
        }

        int lastReport = -1;

        for (int frame = 0; frame < frames; frame++)
        {
            if ((frame & 31) == 0)
                token.ThrowIfCancellationRequested();

            foreach (var (pressFrame, press) in presses)
            {
                if (frame == pressFrame)
                    machine.PressKey(press.Row, press.Mask, true);
                else if (frame == pressFrame + 12)
                    machine.PressKey(press.Row, press.Mask, false);
            }

            machine.Frame(options.FrameBudget);

            // One log line per second of captured audio.
            int second = frame / fps;
            if (second != lastReport)
            {
                lastReport = second;
                log?.Invoke($"  {second,3}s  {machine.EventCount,6} writes");
                progress?.Invoke(frame, frames);
            }
        }

        progress?.Invoke(frames, frames);

        var vgm = machine.BuildVgm(Path.GetFileNameWithoutExtension(romPath), frames * machine.SamplesPerFrame);
        (double Start, double Duration)? loop = null;
        if (vgm.HasLoop)
        {
            loop = (vgm.LoopStartSeconds, vgm.LoopSeconds);
        }
        else if (VgmLoopFinder.Find(vgm) is { } found)
        {
            vgm.SetLoop(found.Start, found.Duration);
            loop = found;
        }

        log?.Invoke($"{vgm.Events.Count} writes, {vgm.TotalSeconds:0.0}s" +
                    (loop is { } l ? $", loop {l.Start:0.000}s + {l.Duration:0.000}s" : ", no loop detected"));

        // "Only the mixer" is the signature of a game that stayed at its title screen: worth saying
        // out loud, because the VGM itself looks fine (it just plays silence).
        int noteWrites = 0;
        foreach (VgmEvent write in vgm.Events)
        {
            if (write.Kind == VgmEventKind.Psg ? write.P0 <= 5 : write.P1 is 1 or 3)
                noteWrites++;
        }

        IReadOnlyList<string> diagnosis = machine.Diagnose();

        if (noteWrites < 20)
        {
            log?.Invoke($"warning: only {noteWrites} pitch writes — no music captured; " +
                        "try a longer duration, a key script (e.g. START@1) or auto-play");

            foreach (string line in diagnosis)
                log?.Invoke("  why: " + line);
        }

        return new RomCaptureResult
        {
            Vgm = vgm,
            Mapper = mapper,
            Scc = scc,
            Hooked = hooked,
            Hook = machine.InterruptHook,
            Loop = loop,
            NoteWrites = noteWrites,
            Diagnosis = diagnosis,
            UsesFm = machine.FmWrites > 0,
            UsesY8950 = machine.Y8950Writes > 0,
            UsesSccPlus = machine.SccPlusWrites > 200,
            RefreshRate = machine.RefreshRate,
        };
    }

    /// <summary>
    /// A script that gets most games past their title screen: fire at 1 s, then the fire button and
    /// the space bar alternately every 3 s.
    /// </summary>
    public static List<KeyPress> AutoPlayScript(double seconds)
    {
        var script = new List<KeyPress> { new(1, KeyPress.JoystickRow, 4) };

        bool space = true;
        for (double at = 4; at < seconds - 1; at += 3)
        {
            script.Add(space ? new KeyPress(at, 8, 0) : new KeyPress(at, KeyPress.JoystickRow, 4));
            space = !space;
        }

        return script;
    }
}
