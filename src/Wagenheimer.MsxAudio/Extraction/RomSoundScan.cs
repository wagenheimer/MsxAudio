using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public int MaxCandidates { get; init; } = 32;

    /// <summary>
    /// How the sound routine takes its id. Null (default) lets the probe try <c>A</c> first and then
    /// <c>HL</c>/<c>DE</c>/<c>BC</c> when nothing answers.
    /// </summary>
    public SoundIdMode? IdMode { get; init; }

    /// <summary>ROM bank the sound routine lives in (-1 = whatever is mapped after boot).</summary>
    public int SoundCallBank { get; init; } = -1;

    /// <summary>Video refresh rate: 60 (NTSC, default) or 50 (PAL).</summary>
    public double RefreshRate { get; init; } = 60;

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

    /// <summary>ROM bank the routine was found in (-1 when the cartridge is not banked or it is mapped already).</summary>
    public int Bank { get; init; } = -1;

    /// <summary>How the routine takes its id.</summary>
    public SoundIdMode IdMode { get; init; }

    /// <summary>Why nothing (or little) was found: unsupported chips, missing BIOS services, boot warnings.</summary>
    public IReadOnlyList<string> Diagnosis { get; init; } = Array.Empty<string>();

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
/// caller renames what it recognises. Candidate routines are ranked by how many places call them,
/// looked up in every ROM bank (not only the one mapped at boot), and probed with the id passed in
/// <c>A</c>, <c>HL</c>, <c>DE</c> or <c>BC</c>.
/// </remarks>
public static class RomSoundScan
{
    private static readonly int[] ProbeIds = { 1, 2, 3, 4, 5, 8, 11, 17 };

    private const int ProbeFrames = 45;
    private const int ExtraCallers = 24;

    /// <summary>A routine that looks like it writes to the sound chips.</summary>
    internal readonly record struct Candidate(ushort Address, int Bank, int Sites);

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
        RomMapper requested = options.Mapper == RomMapper.Auto ? RomInfo.Analyze(rom).Mapper : options.Mapper;

        ushort routine = (ushort)options.SoundCallAddress;
        int bank = options.SoundCallBank;
        SoundIdMode mode = options.IdMode ?? SoundIdMode.A;
        var scored = new List<(ushort Address, int Score)>();
        var notes = new List<string>();

        if (routine == 0)
        {
            var found = Discover(rom, requested, options, log, token);

            if (found is null)
            {
                log?.Invoke("no routine produced sound; the driver may need a profile");
                return new RomScanResult
                {
                    Mapper = requested,
                    Diagnosis = Diagnose(rom, requested, options),
                };
            }

            routine = found.Value.Address;
            bank = found.Value.Bank;
            mode = found.Value.Mode;
            scored = found.Value.Scored;
            log?.Invoke($"using {routine:X4}{(bank >= 0 ? $" (bank {bank})" : "")}, id in {mode} " +
                        $"({found.Value.Score / 1000} distinct sounds on the probe)");

            // A game that plays its own music while the probe runs makes every routine look alike:
            // with fewer than three distinct sounds the pick is a guess, and a profile is the answer.
            if (found.Value.Score < 3000)
            {
                const string ambiguous = "the sound routine could not be told apart from the game's own " +
                                         "music: the list below may contain copies of it; use a profile";
                log?.Invoke("warning: " + ambiguous);
                notes.Add(ambiguous);
            }
        }

        MiniMsx machine = NewMachine(rom, requested, options);
        machine.Boot();
        var (mapper, scc) = machine.CartridgeInfo;

        log?.Invoke($"mapper={RomMapperNames.Name(mapper)}{(scc ? " +SCC" : "")}, sound call={routine:X4}, " +
                    $"{machine.FramesPerSecond} Hz");

        var songs = new List<RomScanSong>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int duplicates = 0;
        int total = Math.Max(1, options.LastId - options.FirstId + 1);
        int done = 0;
        int fps = machine.FramesPerSecond;

        for (int id = options.FirstId; id <= options.LastId; id++)
        {
            token.ThrowIfCancellationRequested();

            if (options.StopId != 0)
            {
                Trigger(machine, routine, bank, options.StopId, mode);
                RunFrames(machine, options, 3);
            }

            foreach (int address in options.ResetAddresses)
                machine.Write((ushort)address, 0x00);

            machine.ResetCapture();
            Trigger(machine, routine, bank, id | options.IdMask, mode);

            int failFrames = Math.Max(1, (int)Math.Round(options.FailureSeconds * fps));
            int frames = Math.Max(1, (int)Math.Round(options.SecondsPerId * fps));
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

            var vgm = machine.BuildVgm($"id_{id:00}", frames * machine.SamplesPerFrame);

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
            else if (VgmLoopFinder.Find(vgm, Math.Max(3, options.SecondsPerId / 4)) is { } foundLoop &&
                     foundLoop.Duration >= 2)
            {
                vgm.SetLoop(foundLoop.Start, foundLoop.Duration);
                loop = foundLoop;
            }

            // Music = repeats (loop) or keeps sounding until the end of the capture. An effect is a
            // burst followed by silence; the caller can always rename/reclassify in the package.
            bool music = loop is not null || StillPlaying(vgm);
            songs.Add(new RomScanSong
            {
                Id = id,
                Vgm = vgm,
                Music = music,
                NoteWrites = produced,
                Loop = loop is not null,
                LoopStart = loop?.Start ?? 0,
                LoopSeconds = loop?.Duration ?? 0,
            });

            log?.Invoke($"  id {id,2}: {(music ? "music" : "sfx  ")}, {produced,5} pitch" +
                        (loop is { } l ? $", loop {l.Start:0.000}s + {l.Duration:0.000}s" : ""));
        }

        progress?.Invoke(total, total);

        return new RomScanResult
        {
            Routine = routine,
            Bank = bank,
            IdMode = mode,
            Candidates = scored,
            Mapper = mapper,
            Scc = scc,
            Songs = songs,
            Duplicates = duplicates,
            Tried = total,
            Diagnosis = notes.Concat(machine.Diagnose()).ToList(),
        };
    }

    private static MiniMsx NewMachine(byte[] rom, RomMapper mapper, RomScanOptions options) =>
        new(rom, mapper)
        {
            CapturePsg = options.CapturePsg,
            CaptureScc = options.CaptureScc,
            RefreshRate = options.RefreshRate,
        };

    /// <summary>Calls the sound routine, forcing the bank it lives in for the duration of the call.</summary>
    private static void Trigger(MiniMsx machine, ushort routine, int bank, int id, SoundIdMode mode)
    {
        if (bank < 0 || !machine.IsBanked)
        {
            machine.TriggerSound(routine, id, mode);
            return;
        }

        int[] saved = machine.SaveBanks();
        machine.SetBank(routine, bank);
        machine.TriggerSound(routine, id, mode);
        machine.RestoreBanks(saved);
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
    private static string Signature(VgmFile vgm) => Signature(vgm.Events);

    /// <summary>Signature of a register stream: what was written, not when (a call shifts the timing).</summary>
    private static string Signature(IReadOnlyList<VgmEvent> events)
    {
        var hash = new HashCode();
        foreach (VgmEvent e in events)
        {
            hash.Add(e.Kind);
            hash.Add(e.P0);
            hash.Add(e.P1);
            hash.Add(e.P2);
        }

        return hash.ToHashCode().ToString("X8") + ":" + events.Count;
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
    /// Finds the sound routine: ranks the candidates, probes them (id in <c>A</c> first, then the other
    /// registers) and returns the first strong one, or the best weak one.
    /// </summary>
    private static (ushort Address, int Bank, SoundIdMode Mode, int Score, List<(ushort Address, int Score)> Scored)?
        Discover(byte[] rom, RomMapper mapper, RomScanOptions options, Action<string>? log, CancellationToken token)
    {
        List<Candidate> candidates = FindCandidateList(rom, mapper, options.MaxCandidates);
        log?.Invoke($"{candidates.Count} candidate routines write to the sound chips");

        // What the game does on its own for the same number of frames: the yardstick for every probe.
        MiniMsx idleMachine = NewMachine(rom, mapper, options);
        idleMachine.Boot(2_000_000);
        idleMachine.ResetCapture();
        for (int frame = 0; frame < ProbeFrames; frame++)
            Step(idleMachine, options);

        var idle = (Signature(idleMachine.CapturedEvents), idleMachine.PitchWriteCount);

        SoundIdMode[] modes = options.IdMode is { } forced
            ? new[] { forced }
            : new[] { SoundIdMode.A, SoundIdMode.HL, SoundIdMode.DE, SoundIdMode.BC };

        var scored = new List<(ushort Address, int Score)>();
        Candidate? best = null;
        SoundIdMode bestMode = SoundIdMode.A;
        int bestScore = 0;

        foreach (SoundIdMode mode in modes)
        {
            foreach (Candidate candidate in candidates)
            {
                token.ThrowIfCancellationRequested();

                int score = Probe(rom, mapper, options, candidate, mode, idle, token);
                if (score == 0)
                    continue;

                log?.Invoke($"  probe {candidate.Address:X4}{(candidate.Bank >= 0 ? $"@{candidate.Bank}" : "")} " +
                            $"({mode}, {candidate.Sites} callers): {score} pitch writes");
                scored.Add((candidate.Address, score));

                if (score > bestScore)
                {
                    best = candidate;
                    bestMode = mode;
                    bestScore = score;
                }
            }

            // Only fall back to the other id registers when the usual one found nothing at all.
            if (best is not null)
                break;
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        return best is { } winner
            ? (winner.Address, winner.Bank, bestMode, bestScore, scored)
            : null;
    }

    /// <summary>
    /// Probes a candidate routine: calls it with several ids and scores how many of them make a
    /// <b>different</b> sound. A game that plays its own title music would make every routine look
    /// good, so each id is compared with a run where nothing was called, and with the other ids.
    /// A register helper (it writes whatever it is given) fails the "enough notes" bar.
    /// </summary>
    private static int Probe(byte[] rom, RomMapper mapper, RomScanOptions options, Candidate candidate,
        SoundIdMode mode, (string Signature, int Pitch) baseline, CancellationToken token)
    {
        string idle = baseline.Signature;

        MiniMsx machine = NewMachine(rom, mapper, options);
        machine.Boot(2_000_000);

        var streams = new HashSet<string> { idle };
        int sounding = 0;
        int extra = 0;

        foreach (int id in ProbeIds)
        {
            token.ThrowIfCancellationRequested();

            foreach (int address in options.ResetAddresses)
                machine.Write((ushort)address, 0x00);

            machine.ResetCapture();
            Trigger(machine, candidate.Address, candidate.Bank, id | options.IdMask, mode);

            for (int frame = 0; frame < ProbeFrames; frame++)
                Step(machine, options);

            int notes = machine.PitchWriteCount;
            if (notes >= Math.Max(1, options.MinNoteWrites) && streams.Add(Signature(machine.CapturedEvents)))
            {
                sounding++;
                extra += notes;
            }
        }

        return sounding >= 2 ? sounding * 1000 + Math.Min(extra, 999) : 0;
    }

    /// <summary>When nothing answered, run the game a little and report what it needed.</summary>
    private static IReadOnlyList<string> Diagnose(byte[] rom, RomMapper mapper, RomScanOptions options)
    {
        MiniMsx machine = NewMachine(rom, mapper, options);
        machine.Boot(2_000_000);
        RunFrames(machine, options, 180);
        return machine.Diagnose();
    }

    /// <summary>
    /// Finds routines that write to the PSG/SCC: every call target whose body (or the body of a
    /// routine it calls) contains a PSG/SCC write. Linear cartridges only; see the overload for banks.
    /// </summary>
    public static List<ushort> FindCandidates(byte[] rom, int max = 16)
    {
        var list = new List<ushort>();
        foreach (Candidate candidate in FindCandidateList(rom, RomMapper.Linear, max))
        {
            if (!list.Contains(candidate.Address))
                list.Add(candidate.Address);
        }

        return list;
    }

    /// <summary>
    /// Candidate sound routines ranked by how many places in the ROM call them (the "play sound"
    /// entry is called from everywhere, a register helper from a handful of spots). On banked
    /// cartridges every bank is searched, because the driver rarely lives in the bank mapped at boot.
    /// </summary>
    internal static List<Candidate> FindCandidateList(byte[] rom, RomMapper mapper, int max)
    {
        bool banked = mapper is RomMapper.Konami or RomMapper.KonamiScc or RomMapper.Ascii8 or
            RomMapper.Ascii16 or RomMapper.RType;
        int bankSize = mapper is RomMapper.Ascii16 or RomMapper.RType ? 0x4000 : 0x2000;
        int loadBase = !banked && Cartridge.IsPage2Rom(rom) ? 0x8000 : 0x4000;

        // Call sites per target (CALL nn and the conditional calls).
        var sites = new Dictionary<ushort, int>();
        int limit = rom.Length - 2;

        for (int i = 0; i < limit; i++)
        {
            if (rom[i] is not (0xCD or 0xC4 or 0xCC or 0xD4 or 0xDC or 0xE4 or 0xEC or 0xF4 or 0xFC))
                continue;

            ushort target = (ushort)(rom[i + 1] | (rom[i + 2] << 8));
            if (target >= 0x4000 && target < 0xC000)
                sites[target] = sites.TryGetValue(target, out int n) ? n + 1 : 1;
        }

        var found = new List<Candidate>();

        foreach (var (target, count) in sites)
        {
            if (!banked)
            {
                int offset = target - loadBase;
                if (offset >= 0 && offset < rom.Length && WritesSound(rom, offset, 0, false, 0, loadBase))
                    found.Add(new Candidate(target, -1, count));

                continue;
            }

            int inBank = (target - 0x4000) & (bankSize - 1);
            int banks = Math.Max(1, rom.Length / bankSize);

            for (int bank = 0; bank < banks; bank++)
            {
                if (WritesSound(rom, bank * bankSize + inBank, 0, true, bankSize, 0x4000))
                    found.Add(new Candidate(target, bank, count));
            }
        }

        found.Sort((a, b) =>
        {
            int c = b.Sites.CompareTo(a.Sites);
            return c != 0 ? c : a.Address != b.Address ? a.Address.CompareTo(b.Address) : a.Bank.CompareTo(b.Bank);
        });

        if (found.Count > max)
            found = found.GetRange(0, max);

        // The "play sound" entry often only queues the id for a per-frame player and writes nothing
        // itself, so the byte scan above misses it. The busiest call targets are added too, and the
        // probe (which measures what they really do) decides.
        var known = new HashSet<ushort>();
        foreach (Candidate candidate in found)
            known.Add(candidate.Address);

        var busiest = new List<KeyValuePair<ushort, int>>(sites);
        busiest.Sort((a, b) => b.Value.CompareTo(a.Value));

        foreach (var (target, count) in busiest)
        {
            if (found.Count >= max + ExtraCallers)
                break;

            if (count >= 3 && known.Add(target))
                found.Add(new Candidate(target, -1, count));
        }

        return found;
    }

    /// <summary>
    /// True when the routine at ROM offset <paramref name="start"/> (or one it calls or jumps to) writes
    /// to the PSG/SCC. A byte scan, not a disassembler: good enough to rank candidates, which the probe
    /// then confirms by running them.
    /// </summary>
    private static bool WritesSound(byte[] rom, int start, int depth, bool banked, int bankSize, int loadBase)
    {
        if (depth > 3 || start < 0 || start >= rom.Length)
            return false;

        int end = Math.Min(rom.Length - 1, start + 0x300);

        for (int i = start; i + 2 < end; i++)
        {
            byte op = rom[i];

            // Unconditional end of the routine body.
            if (op is 0xC9 or 0xE9)
                break;

            // CALL WRTPSG (0x0093) / GICINI (0x0090)
            if (op == 0xCD && rom[i + 2] == 0x00 && (rom[i + 1] is 0x93 or 0x90))
                return true;

            // OUT (n),A to the PSG (0xA0/0xA1) or the SCC (0x98..0x9F)
            if (op == 0xD3 && (rom[i + 1] is 0xA0 or 0xA1 || rom[i + 1] is >= 0x98 and <= 0x9F))
                return true;

            // LD (nnnn),A into the SCC window
            if (op == 0x32 && rom[i + 2] is >= 0x98 and <= 0x9F)
                return true;

            // LD HL/DE,&9800..&9FFF followed by a store through (HL)/(DE): the usual way to reach the SCC
            if ((op is 0x21 or 0x11) && rom[i + 2] is >= 0x98 and <= 0x9F)
            {
                int stop = Math.Min(end, i + 14);
                for (int j = i + 3; j < stop; j++)
                {
                    if (rom[j] is (>= 0x70 and <= 0x77) or 0x36 or 0x12)
                        return true;
                }
            }

            // LD C,&A0/&A1 followed by OUT (C),r: PSG writes without the BIOS call
            if (op == 0x0E && (rom[i + 1] is 0xA0 or 0xA1))
            {
                int stop = Math.Min(end, i + 14);
                for (int j = i + 2; j < stop - 1; j++)
                {
                    if (rom[j] == 0xED && (rom[j + 1] is 0x41 or 0x49 or 0x51 or 0x59 or 0x61 or 0x69 or 0x79))
                        return true;
                }
            }

            // Nested call or tail jump: a wrapper around the real writer
            if (op is 0xCD or 0xC3)
            {
                ushort target = (ushort)(rom[i + 1] | (rom[i + 2] << 8));
                if (target >= 0x4000 && target < 0xC000)
                {
                    int next = banked
                        ? (start / bankSize) * bankSize + ((target - 0x4000) & (bankSize - 1))
                        : target - loadBase;

                    if (WritesSound(rom, next, depth + 1, banked, bankSize, loadBase))
                        return true;
                }

                if (op == 0xC3)
                    break;
            }
        }

        return false;
    }
}
