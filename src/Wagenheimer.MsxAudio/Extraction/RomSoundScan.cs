using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>Options for <see cref="RomSoundScan"/>.</summary>
public sealed class RomScanOptions
{
    /// <summary>Cartridge image to scan.</summary>
    public required string RomPath { get; init; }

    /// <summary>Mapper; <see cref="RomMapper.Auto"/> picks one from the ROM size.</summary>
    public RomMapper Mapper { get; init; } = RomMapper.Auto;

    /// <summary>
    /// Address of the "play sound, id in A" routine. When 0, the scan looks for it in the ROM and
    /// probes the candidates.
    /// </summary>
    public int SoundCallAddress { get; init; }

    /// <summary>
    /// Per-frame sound player, when the driver can run alone (see
    /// <see cref="RomExtractionProfile.PlayerAddress"/>). 0 runs the game's hooks.
    /// </summary>
    public int PlayerAddress { get; init; }

    /// <summary>RAM bytes cleared before each id (per-channel current-song bytes).</summary>
    public List<int> ResetAddresses { get; } = new();

    /// <summary>Bits OR-ed into the id (e.g. <c>0x80</c> = music stream).</summary>
    public int IdMask { get; init; }

    /// <summary>Id that silences everything before each capture (0 = none).</summary>
    public int StopId { get; init; }

    /// <summary>First id to try.</summary>
    public int FirstId { get; init; } = 1;

    /// <summary>Last id to try (the King's Valley table, for instance, ends at 32).</summary>
    public int LastId { get; init; } = 63;

    /// <summary>Capture length per id that produces sound.</summary>
    public double SecondsPerId { get; init; } = 12;

    /// <summary>If nothing is written within this many seconds, the id is skipped.</summary>
    public double FailureSeconds { get; init; } = 2;

    /// <summary>Instruction budget per frame.</summary>
    public int FrameBudget { get; init; } = 200_000;

    /// <summary>Minimum pitch writes for an id to count as a sound.</summary>
    public int MinNoteWrites { get; init; } = 4;

    /// <summary>How many candidate routines to probe when <see cref="SoundCallAddress"/> is 0.</summary>
    public int MaxCandidates { get; init; } = 16;

    /// <summary>Record PSG writes (default true).</summary>
    public bool CapturePsg { get; init; } = true;

    /// <summary>Record SCC writes (default true).</summary>
    public bool CaptureScc { get; init; } = true;
}

/// <summary>One id that produced sound.</summary>
public sealed class RomScanSong
{
    /// <summary>Sound id passed to the driver.</summary>
    public int Id { get; init; }

    /// <summary>Captured register writes.</summary>
    public required VgmFile Vgm { get; init; }

    /// <summary>True when it looks like music (loops, or keeps playing until the end).</summary>
    public bool Music { get; init; }

    /// <summary>Pitch writes in the capture.</summary>
    public int NoteWrites { get; init; }

    /// <summary>Loop start in seconds (when <see cref="Loop"/>).</summary>
    public double LoopStart { get; init; }

    /// <summary>Loop duration in seconds (when <see cref="Loop"/>).</summary>
    public double LoopSeconds { get; init; }

    /// <summary>True when a loop was found.</summary>
    public bool Loop { get; init; }

    /// <summary>Default name (<c>id_11</c>); the caller may rename it.</summary>
    public string SuggestedName => $"id_{Id:00}";
}

/// <summary>What the scan found.</summary>
public sealed class RomScanResult
{
    /// <summary>Routine used (0 when none was found).</summary>
    public ushort Routine { get; init; }

    /// <summary>Candidate routines and their probe score, highest first.</summary>
    public IReadOnlyList<(ushort Address, int Score)> Candidates { get; init; } =
        Array.Empty<(ushort, int)>();

    /// <summary>Mapper the cartridge resolved to.</summary>
    public RomMapper Mapper { get; init; }

    /// <summary>True when the SCC window is enabled.</summary>
    public bool Scc { get; init; }

    /// <summary>Ids that produced sound, in id order.</summary>
    public List<RomScanSong> Songs { get; init; } = new();

    /// <summary>Ids skipped because they produced the same stream as an earlier one.</summary>
    public int Duplicates { get; init; }

    /// <summary>Ids tried.</summary>
    public int Tried { get; init; }

    /// <summary>
    /// Builds a <c>.msxaudio</c> library from the songs found. <paramref name="names"/> renames ids
    /// (<c>11 → "ingame"</c>); ids without a name keep <c>id_NN</c>.
    /// </summary>
    public Songs.MsxAudioLibrary ToLibrary(string title, IReadOnlyDictionary<int, string>? names = null)
    {
        var library = new Songs.MsxAudioLibrary(title);
        library.Manifest.Source = $"sound scan ({RomMapperNames.Name(Mapper)}), routine {Routine:X4}";

        foreach (RomScanSong song in Songs)
        {
            string name = names is not null && names.TryGetValue(song.Id, out string? friendly)
                ? friendly
                : song.SuggestedName;

            // The id is part of the package id: two songs can share a name after a rename.
            string id = $"{name}_{song.Id:00}";

            if (song.Music)
            {
                library.AddSong(id, name, song.Vgm, song.Loop, song.LoopStart, song.LoopSeconds, song.Id,
                    song.Loop ? "music" : "music", "scanned");
            }
            else
            {
                library.AddClip(id, name, song.Vgm, song.Id, "sfx", "scanned");
            }
        }

        return library;
    }
}

/// <summary>
/// Finds a cartridge's sounds without a curated profile: locates the "play sound" routine (or takes
/// it from the options), calls it with every id, and keeps the ones that produce sound.
/// </summary>
/// <remarks>
/// The scan proves each id empirically, so it does not need to understand the driver's data format.
/// It is how a game without a profile gets its first song list: ids come out as <c>id_NN</c>, music
/// is told from effects by looking for a loop (or by the sound still playing at the end), and the
/// caller renames what it recognises.
/// </remarks>
public static class RomSoundScan
{
    /// <summary>Runs the scan on the calling thread.</summary>
    /// <param name="options">ROM, id range and driver addresses.</param>
    /// <param name="log">Receives progress lines.</param>
    /// <param name="progress">Receives (done, total) ids.</param>
    /// <param name="token">Cancels the scan.</param>
    public static RomScanResult Run(
        RomScanOptions options,
        Action<string>? log = null,
        Action<int, int>? progress = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(options.RomPath))
            throw new FileNotFoundException("ROM not found", options.RomPath);

        byte[] rom = File.ReadAllBytes(RomFile.Resolve(options.RomPath));

        ushort routine = (ushort)options.SoundCallAddress;
        var candidates = new List<(ushort Address, int Score)>();

        if (routine == 0)
        {
            foreach (ushort candidate in FindCandidates(rom, options.MaxCandidates))
            {
                token.ThrowIfCancellationRequested();

                int score = Probe(rom, options, candidate, token);
                log?.Invoke($"  probe {candidate:X4}: {score} pitch writes");

                if (score > 0)
                    candidates.Add((candidate, score));
            }

            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

            if (candidates.Count == 0)
            {
                log?.Invoke("no routine produced sound; the driver may need a profile");
                return new RomScanResult { Mapper = RomMapper.Auto };
            }

            routine = candidates[0].Address;
            log?.Invoke($"using {routine:X4} ({candidates[0].Score} pitch writes on the probe)");
        }

        var machine = new MiniMsx(rom, options.Mapper)
        {
            CapturePsg = options.CapturePsg,
            CaptureScc = options.CaptureScc,
        };

        machine.Boot(1_000_000);
        var (mapper, scc) = machine.CartridgeInfo;

        log?.Invoke($"mapper={RomMapperNames.Name(mapper)}{(scc ? " +SCC" : "")}, sound call={routine:X4}");

        var songs = new List<RomScanSong>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int duplicates = 0;
        int total = Math.Max(1, options.LastId - options.FirstId + 1);
        int done = 0;

        for (int id = options.FirstId; id <= options.LastId; id++)
        {
            token.ThrowIfCancellationRequested();

            if (options.StopId != 0)
            {
                machine.TriggerSound(routine, options.StopId);
                RunFrames(machine, options, 3);
            }

            foreach (int address in options.ResetAddresses)
                machine.Write((ushort)address, 0x00);

            machine.ResetCapture();
            machine.TriggerSound(routine, id | options.IdMask);

            int failFrames = Math.Max(1, (int)Math.Round(options.FailureSeconds * 60));
            int frames = Math.Max(1, (int)Math.Round(options.SecondsPerId * 60));
            int produced = 0;

            for (int frame = 0; frame < frames; frame++)
            {
                Step(machine, options);

                if (frame == failFrames && machine.PitchWriteCount < options.MinNoteWrites)
                    break;

                produced = machine.PitchWriteCount;
            }

            progress?.Invoke(++done, total);

            if (produced < options.MinNoteWrites)
                continue;

            var vgm = machine.BuildVgm($"id_{id:00}", frames * 735);

            // Two ids that produce the same stream are the same sound (aliases in the table).
            string signature = Signature(vgm);
            if (!seen.Add(signature))
            {
                duplicates++;
                log?.Invoke($"  id {id,2}: duplicate, skipped");
                continue;
            }

            (double Start, double Duration)? loop = null;
            if (vgm.HasLoop && vgm.LoopSeconds >= 2)
            {
                loop = (vgm.LoopStartSeconds, vgm.LoopSeconds);
            }
            else if (VgmLoopFinder.Find(vgm, Math.Max(3, options.SecondsPerId / 4)) is { } found &&
                     found.Duration >= 2)
            {
                vgm.SetLoop(found.Start, found.Duration);
                loop = found;
            }

            // Music = repeats (loop) or keeps sounding until the end of the capture. An effect is a
            // burst followed by silence; the caller can always rename/reclassify in the package.
            bool music = loop is not null || StillPlaying(vgm);
            var song = new RomScanSong
            {
                Id = id,
                Vgm = vgm,
                Music = music,
                NoteWrites = produced,
                Loop = loop is not null,
                LoopStart = loop?.Start ?? 0,
                LoopSeconds = loop?.Duration ?? 0,
            };

            songs.Add(song);

            log?.Invoke($"  id {id,2}: {(music ? "music" : "sfx  ")}, {produced,5} pitch" +
                        (loop is { } l ? $", loop {l.Start:0.000}s + {l.Duration:0.000}s" : ""));
        }

        progress?.Invoke(total, total);

        return new RomScanResult
        {
            Routine = routine,
            Candidates = candidates,
            Mapper = mapper,
            Scc = scc,
            Songs = songs,
            Duplicates = duplicates,
            Tried = total,
        };
    }

    /// <summary>
    /// True when the notes fill most of the capture, i.e. it keeps playing (music) instead of being a
    /// short burst followed by silence (an effect).
    /// </summary>
    private static bool StillPlaying(VgmFile vgm)
    {
        double first = -1;
        double last = -1;

        foreach (VgmEvent e in vgm.Events)
        {
            if (!(e.Kind == VgmEventKind.Psg ? e.P0 <= 5 : e.P1 is 1 or 3))
                continue;

            double time = e.Sample / 44100.0;
            if (first < 0)
                first = time;

            last = time;
        }

        if (first < 0 || vgm.TotalSeconds <= 0)
            return false;

        return (last - first) / vgm.TotalSeconds >= 0.75;
    }

    /// <summary>Cheap signature of a capture, to drop ids that produce the same stream.</summary>
    private static string Signature(VgmFile vgm)
    {
        var hash = new HashCode();
        foreach (VgmEvent e in vgm.Events)
        {
            hash.Add(e.Kind);
            hash.Add(e.P0);
            hash.Add(e.P1);
            hash.Add(e.P2);
        }

        return hash.ToHashCode().ToString("X8") + ":" + vgm.Events.Count;
    }

    /// <summary>Steps the driver (sound-only when a player address is known, else the game hook).</summary>
    private static void Step(MiniMsx machine, RomScanOptions options)
    {
        if (options.PlayerAddress != 0)
        {
            machine.CallAddress((ushort)options.PlayerAddress);
            machine.Advance();
            return;
        }

        machine.Frame(options.FrameBudget);
    }

    private static void RunFrames(MiniMsx machine, RomScanOptions options, int frames)
    {
        for (int i = 0; i < frames; i++)
            Step(machine, options);
    }

    /// <summary>
    /// Probes a candidate routine: calls it with a few ids and counts the pitch writes. This is what
    /// tells the "play sound" routine apart from the helpers that merely write one PSG register.
    /// </summary>
    private static int Probe(byte[] rom, RomScanOptions options, ushort routine, CancellationToken token)
    {
        var machine = new MiniMsx(rom, options.Mapper)
        {
            CapturePsg = options.CapturePsg,
            CaptureScc = options.CaptureScc,
        };

        machine.Boot(500_000);

        int score = 0;

        foreach (int id in new[] { 1, 3, 5, 11, 17 })
        {
            token.ThrowIfCancellationRequested();

            foreach (int address in options.ResetAddresses)
                machine.Write((ushort)address, 0x00);

            machine.ResetCapture();
            machine.TriggerSound(routine, id | options.IdMask);

            for (int frame = 0; frame < 60; frame++)
                Step(machine, options);

            score += machine.PitchWriteCount;
        }

        return score;
    }

    /// <summary>
    /// Finds routines that write to the PSG/SCC: every <c>CALL nn</c> target whose body (or the body of
    /// a routine it calls) contains a PSG/SCC write.
    /// </summary>
    public static List<ushort> FindCandidates(byte[] rom, int max = 16)
    {
        var targets = new HashSet<ushort>();

        int limit = rom.Length - 2;
        for (int i = 0; i < limit; i++)
        {
            if (rom[i] != 0xCD) // CALL nn
                continue;

            ushort target = (ushort)(rom[i + 1] | (rom[i + 2] << 8));
            if (target >= 0x4000 && target < 0xC000)
                targets.Add(target);
        }

        var list = new List<ushort>();
        foreach (ushort target in targets)
        {
            if (WritesSound(rom, target, 0))
                list.Add(target);
        }

        list.Sort();
        return list.Count <= max ? list : list.GetRange(0, max);
    }

    /// <summary>True when the routine at <paramref name="address"/> (or one it calls) writes PSG/SCC.</summary>
    private static bool WritesSound(byte[] rom, ushort address, int depth)
    {
        if (depth > 2 || address < 0x4000 || address >= 0xC000)
            return false;

        int start = address - 0x4000;
        int end = Math.Min(rom.Length - 1, start + 0x300);

        for (int i = start; i + 2 < end; i++)
        {
            byte op = rom[i];

            // RET / JP: the routine body ends here.
            if (op is 0xC9 or 0xC3 or 0xE9)
                break;

            // CALL WRTPSG (0x0093) or RDPSG
            if (op == 0xCD && rom[i + 1] is 0x93 && rom[i + 2] == 0x00)
                return true;

            // OUT (n),A to the PSG (0xA0/0xA1) or the SCC (0x98..0x9F)
            if (op == 0xD3 && (rom[i + 1] is 0xA0 or 0xA1 || rom[i + 1] is >= 0x98 and <= 0x9F))
                return true;

            // LD (nnnn),A into the SCC window
            if (op == 0x32 && rom[i + 2] is >= 0x98 and <= 0x9F)
                return true;

            // LD HL,&9800..&9FFF followed by LD (HL),A: the usual way to reach the SCC
            if (op == 0x21 && rom[i + 2] is >= 0x98 and <= 0x9F)
            {
                int stop = Math.Min(end, i + 12);
                for (int j = i + 3; j < stop; j++)
                {
                    if (rom[j] == 0x77) // LD (HL),A
                        return true;
                }
            }

            // LD C,&A0/&A1 followed by OUT (C),A: PSG writes without the BIOS call
            if (op == 0x0E && rom[i + 1] is 0xA0 or 0xA1)
            {
                int stop = Math.Min(end, i + 12);
                for (int j = i + 2; j < stop - 1; j++)
                {
                    if (rom[j] == 0xED && rom[j + 1] == 0x79) // OUT (C),A
                        return true;
                }
            }

            // Nested call: a wrapper around the real writer
            if (op == 0xCD)
            {
                ushort target = (ushort)(rom[i + 1] | (rom[i + 2] << 8));
                if (target >= 0x4000 && target < 0xC000 && WritesSound(rom, target, depth + 1))
                    return true;
            }
        }

        return false;
    }
}
