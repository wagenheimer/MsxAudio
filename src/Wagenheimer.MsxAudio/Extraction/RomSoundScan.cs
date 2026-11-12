using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>Last id to try. Tables vary: King's Valley ends at 32, Penguin Adventure's music goes past 120.</summary>
    public int LastId { get; init; } = 255;

    /// <summary>
    /// Ignore the game's own calls to the sound routine while scanning. A game on its title screen
    /// starts its own music a few seconds after boot, and that music replaces or mixes with the id
    /// being tested. The scan's own calls always go through.
    /// </summary>
    public bool BlockGameCalls { get; init; } = true;

    /// <summary>
    /// A sound that never ends must change notes at least this often (per second), or it is taken
    /// for junk read past the end of the id table and skipped.
    /// </summary>
    public double MinNotesPerSecond { get; init; } = 2.5;

    /// <summary>A sound is over when nothing audible was written for this long.</summary>
    public double EndSilenceSeconds { get; init; } = 1.5;

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
        bool routineSearched = routine == 0;

        if (routine == 0)
        {
            // The routine search is the first third of the bar; the ids fill the rest.
            var found = Discover(rom, requested, options, log,
                (done, all) => progress?.Invoke(done * 35 / Math.Max(1, all), 100), token);

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

        int total = Math.Max(1, options.LastId - options.FirstId + 1);
        bool block = options.BlockGameCalls && options.PlayerAddress == 0;

        List<IdResult> results = ScanIds(rom, requested, options, routine, bank, mode, block, total, log, progress,
            routineSearched, token);

        // The routine may be the game's own per-frame tick: blocking its calls would silence everything.
        // Nothing found with the block on means it is not safe, so try again without it.
        if (block && results.All(r => r.Song is null))
        {
            log?.Invoke("nothing played with the game's own sound calls blocked: trying again without the block");
            results = ScanIds(rom, requested, options, routine, bank, mode, false, total, log, progress,
                routineSearched, token);
        }

        var songs = new List<RomScanSong>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int duplicates = 0;

        foreach (IdResult result in results)
        {
            if (result.Song is not { } song)
                continue;

            // Two ids that produce the same stream are the same sound (aliases in the table).
            if (!seen.Add(Signature(song.Vgm)))
            {
                duplicates++;
                log?.Invoke($"  id {song.Id,3}: duplicate, skipped");
                continue;
            }

            songs.Add(song);
            log?.Invoke($"  id {song.Id,3}: {(song.Music ? "music" : "sfx  ")}, {song.Vgm.TotalSeconds,5:0.0}s, " +
                        $"{song.NoteWrites,5} pitch" +
                        (song.Loop ? $", loop {song.LoopStart:0.000}s + {song.LoopSeconds:0.000}s" : ""));
        }

        progress?.Invoke(1, 1);

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

    private readonly record struct IdResult(int Id, RomScanSong? Song);

    /// <summary>
    /// Tries every id on its own machine, side by side. A fresh machine per id means a sound never
    /// starts in the middle of the previous one, and the order of the ids does not matter.
    /// </summary>
    private static List<IdResult> ScanIds(byte[] rom, RomMapper mapper, RomScanOptions options, ushort routine,
        int bank, SoundIdMode mode, bool block, int total, Action<string>? log, Action<int, int>? progress,
        bool routineSearched, CancellationToken token)
    {
        var results = new IdResult[total];
        int done = 0;
        int found = 0;

        var parallel = new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
        };

        try
        {
            Parallel.For(0, total, parallel, index =>
            {
                int id = options.FirstId + index;
                RomScanSong? song = ScanOne(rom, mapper, options, routine, bank, mode, id, block, token);
                results[index] = new IdResult(id, song);

                int finished = Interlocked.Increment(ref done);
                if (song is not null)
                    Interlocked.Increment(ref found);

                progress?.Invoke(routineSearched ? 35 + finished * 65 / total : finished,
                    routineSearched ? 100 : total);

                if (finished % 16 == 0 || finished == total)
                    log?.Invoke($"  tested {finished}/{total} ids, {Volatile.Read(ref found)} with sound so far");
            });
        }
        catch (AggregateException ex) when (ex.InnerException is OperationCanceledException cancelled)
        {
            throw cancelled;
        }

        return results.ToList();
    }

    /// <summary>One id on a fresh machine: records until the sound is over (or the limit), then trims.</summary>
    private static RomScanSong? ScanOne(byte[] rom, RomMapper mapper, RomScanOptions options, ushort routine,
        int bank, SoundIdMode mode, int id, bool block, CancellationToken token)
    {
        MiniMsx machine = NewMachine(rom, mapper, options);
        machine.Boot();

        if (block)
            machine.BlockGameCalls(routine);

        int fps = machine.FramesPerSecond;
        RunFrames(machine, options, 10);

        if (options.StopId != 0)
        {
            Trigger(machine, routine, bank, options.StopId, mode);
            RunFrames(machine, options, 3);
        }

        foreach (int address in options.ResetAddresses)
            machine.Write((ushort)address, 0x00);

        machine.ResetCapture();
        Trigger(machine, routine, bank, id | options.IdMask, mode);

        int maxFrames = Math.Max(1, (int)Math.Round(options.SecondsPerId * fps));
        int failFrames = Math.Max(1, (int)Math.Round(options.FailureSeconds * fps));
        int endFrames = Math.Max(1, (int)Math.Round(options.EndSilenceSeconds * fps));
        int tailFrames = (int)Math.Round(0.4 * fps);

        int seen = 0;
        int lastActive = -1;
        bool ended = false;
        int frame;

        for (frame = 0; frame < maxFrames; frame++)
        {
            token.ThrowIfCancellationRequested();
            Step(machine, options);

            IReadOnlyList<VgmEvent> events = machine.CapturedEvents;
            for (; seen < events.Count; seen++)
            {
                if (IsAudible(events[seen]))
                    lastActive = frame;
            }

            if (frame == failFrames && machine.PitchWriteCount < options.MinNoteWrites)
                return null;

            if (lastActive >= 0 && frame - lastActive >= endFrames)
            {
                ended = true;
                break;
            }
        }

        int produced = machine.PitchWriteCount;
        if (produced < options.MinNoteWrites || lastActive < 0)
            return null;

        // Ids past the end of the driver's table read whatever follows it: they "play" a sparse
        // tone that never ends (a few writes in the whole capture). Real music keeps changing notes.
        double seconds = machine.SamplesPerFrame > 0 ? maxFrames / (double)fps : options.SecondsPerId;
        if (!ended && seconds >= 5 && produced / seconds < options.MinNotesPerSecond)
            return null;

        // A sound that ended on its own is cut shortly after its last note; one that was still going
        // when the limit came keeps the whole capture.
        int keepFrames = ended ? Math.Min(frame + 1, lastActive + tailFrames + 1) : maxFrames;
        VgmFile vgm = machine.BuildVgm($"id_{id:00}", keepFrames * machine.SamplesPerFrame);

        // Writing notes is not enough: a capture that never makes a sound (volume at zero, channels
        // muted in the mixer) is not audio, however long it ran.
        if (!HasAudibleSound(vgm.Events))
            return null;

        (double Start, double Duration)? loop = null;
        if (vgm.HasLoop && vgm.LoopSeconds >= 2)
        {
            loop = (vgm.LoopStartSeconds, vgm.LoopSeconds);
        }
        else if (!ended && VgmLoopFinder.Find(vgm, Math.Max(3, options.SecondsPerId / 4)) is { } foundLoop &&
                 foundLoop.Duration >= 2)
        {
            vgm.SetLoop(foundLoop.Start, foundLoop.Duration);
            loop = foundLoop;
        }

        // Music = repeats, keeps sounding until the limit, or simply lasts (a jingle). An effect is a
        // short burst. The caller can always rename or reclassify in the package.
        bool music = loop is not null || (!ended && StillPlaying(vgm)) || vgm.TotalSeconds >= 5;

        return new RomScanSong
        {
            Id = id,
            Vgm = vgm,
            Music = music,
            NoteWrites = produced,
            Loop = loop is not null,
            LoopStart = loop?.Start ?? 0,
            LoopSeconds = loop?.Duration ?? 0,
        };
    }

    /// <summary>
    /// True when the register stream, replayed in order, ever has a channel that can be heard: a PSG
    /// channel with volume (or the envelope) and its tone or noise enabled in the mixer, or an SCC
    /// channel with volume. A stream of pitch changes with the volume at zero is silence.
    /// </summary>
    internal static bool HasAudibleSound(IReadOnlyList<VgmEvent> events)
    {
        // Typical state right after a driver's init: tone on, noise off, everything else zero.
        var psg = new byte[16];
        psg[7] = 0x38;
        var sccVolume = new byte[5];

        foreach (VgmEvent e in events)
        {
            if (e.Kind == VgmEventKind.Psg)
            {
                if (e.P0 >= psg.Length)
                    continue;

                psg[e.P0] = e.P1;

                for (int channel = 0; channel < 3; channel++)
                {
                    byte volume = psg[8 + channel];
                    bool loud = (volume & 0x0F) > 0 || ((volume & 0x10) != 0 && (psg[11] | psg[12]) != 0);
                    bool toneOn = (psg[7] & (1 << channel)) == 0;
                    bool noiseOn = (psg[7] & (8 << channel)) == 0;

                    if (loud && (toneOn || noiseOn))
                        return true;
                }
            }
            else if (e.P0 == 2 && e.P1 < sccVolume.Length)
            {
                sccVolume[e.P1] = (byte)(e.P2 & 0x0F);
                if (sccVolume[e.P1] > 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A write that shows something is playing: a PSG volume above zero or a tone-period write, an
    /// SCC volume above zero or a frequency write.
    /// </summary>
    private static bool IsAudible(VgmEvent e) =>
        e.Kind == VgmEventKind.Psg
            ? e.P0 <= 5 || (e.P0 is >= 8 and <= 10 && (e.P1 & 0x0F) > 0)
            : e.P0 == 1 || (e.P0 == 2 && (e.P2 & 0x0F) > 0);

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
        Discover(byte[] rom, RomMapper mapper, RomScanOptions options, Action<string>? log,
            Action<int, int>? progress, CancellationToken token)
    {
        List<Candidate> candidates = FindCandidateList(rom, mapper, options.MaxCandidates);
        log?.Invoke($"{candidates.Count} routines may play sound: testing each one with a few ids " +
                    "(this is the slow part, about a minute)");

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

        int probed = 0;
        int totalProbes = candidates.Count * modes.Length;

        foreach (SoundIdMode mode in modes)
        {
            // Every probe runs on its own machine, so the candidates are tested side by side; the
            // scores are collected by index and reported in ranking order, so the result is the same
            // as testing them one after another.
            var scores = new int[candidates.Count];
            var options2 = new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
            };

            try
            {
                Parallel.For(0, candidates.Count, options2, index =>
                {
                    scores[index] = Probe(rom, mapper, options, candidates[index], mode, idle, token);
                    progress?.Invoke(Interlocked.Increment(ref probed), totalProbes);
                });
            }
            catch (AggregateException ex) when (ex.InnerException is OperationCanceledException cancelled)
            {
                throw cancelled;
            }

            for (int index = 0; index < candidates.Count; index++)
            {
                int score = scores[index];
                if (score == 0)
                    continue;

                Candidate candidate = candidates[index];
                log?.Invoke($"  routine {candidate.Address:X4}{(candidate.Bank >= 0 ? $" (bank {candidate.Bank})" : "")} " +
                            $"answers with id in {mode}: {score / 1000} distinct sounds ({candidate.Sites} callers)");
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

            // Konami (4) has no register for the 0x4000 window: it always shows bank 0. A routine
            // "found" there in any other bank can never be reached, so only bank 0 is searched.
            if (mapper == RomMapper.Konami && target < 0x6000)
                banks = 1;

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
