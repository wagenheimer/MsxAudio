using System;
using System.Collections.Generic;
using System.IO;
using Wagenheimer.MsxAudio.Songs;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>
/// Extracts a cartridge's music/sound effects by running its **sound driver in process** —
/// no external emulator.
/// </summary>
/// <remarks>
/// <para>Every ID starts with a fresh machine. Profiles can initialize and tick the sound driver
/// directly, without executing gameplay, or use a cartridge's interrupt hooks. Captured PSG/SCC
/// writes become VGM entries; rendered PCM detects silence and bounded captures are reported.</para>
/// </remarks>
public static class DirectRomExtractor
{
    /// <summary>Runs the extraction.</summary>
    /// <param name="options">ROM, profile and output settings.</param>
    /// <param name="log">Receives progress lines.</param>
    /// <param name="cancellationToken">Stops between driver ticks and IDs.</param>
    /// <param name="progress">Receives (done, total) ids, for a progress bar.</param>
    public static ExtractionResult Extract(RomExtractionOptions options, Action<string>? log = null,
        Action<int, int>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(options.RomPath)) throw new FileNotFoundException("ROM not found", options.RomPath);
        RomExtractionProfile profile = options.Profile;
        byte[] rom = File.ReadAllBytes(RomFile.Resolve(options.RomPath));
        var identity = ExtractionIdentity.Create(profile, rom);
        RomMapper mapper = RomMapperNames.Parse(profile.RomType);
        if (mapper == RomMapper.Auto) mapper = RomInfo.Analyze(rom).Mapper;
        SoundIdMode idMode = Enum.TryParse(profile.IdRegister, true, out SoundIdMode mode) ? mode : SoundIdMode.A;
        var result = new ExtractionResult { Library = new MsxAudioLibrary(profile.Name) };
        result.Library.Manifest.Identity = identity;
        result.Library.Manifest.Source = "isolated in-process ROM driver";
        int done = 0;
        try
        {
            foreach (int id in profile.Ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (id is < 0 or > 255) throw new InvalidDataException($"Invalid sound id {id}.");
                string name = profile.Names.TryGetValue(id, out string? friendly) ? friendly : $"sound_{id:00}";
                bool music = profile.MusicIds.Contains(id);
                double duration = profile.SecondsById.TryGetValue(id, out double specific) ? specific
                    : music ? profile.SecondsPerSong : profile.SecondsPerId;
                if (!double.IsFinite(duration) || duration <= 0 || duration > 3600)
                    throw new InvalidDataException($"Invalid capture duration for {name}.");
                log?.Invoke($"AUDIO: {name} ({done + 1}/{profile.Ids.Count}), fresh driver instance");
                var machine = new MiniMsx(rom, mapper)
                {
                    CapturePsg = options.CapturePsg, CaptureScc = options.CaptureScc,
                    RefreshRate = profile.RefreshRate, ExternalSccSlot = profile.ExternalSccSlot,
                };
                if (profile.BootCartridge) machine.Boot();
                foreach (var copy in profile.MemoryCopies)
                {
                    if (copy.RomOffset < 0 || copy.Length < 0 || copy.RomOffset > rom.Length - copy.Length ||
                        copy.Address < 0xC000 || copy.Address > 0x10000 - copy.Length)
                        throw new InvalidDataException("Invalid ROM-to-RAM driver copy.");
                    for (int offset = 0; offset < copy.Length; offset++)
                        machine.Write((ushort)(copy.Address + offset), rom[copy.RomOffset + offset]);
                }
                if (profile.BlockGameSoundCalls) machine.BlockGameCalls((ushort)profile.SoundCallAddress);
                MapDriver(machine, profile);
                if (profile.InitializeAddress != 0) machine.CallAddress((ushort)profile.InitializeAddress);
                MapDriver(machine, profile);
                if (profile.StopId != 0) TriggerSound(machine, profile, profile.StopId, idMode);
                for (int tick = 0; tick < profile.StopSettleFrames; tick++) StepDriver(machine, profile);
                foreach (int address in profile.ResetAddresses) machine.Write((ushort)address, 0);
                MapDriver(machine, profile);
                machine.ResetCapture(includeChipState: true);
                TriggerSound(machine, profile, id | profile.IdMask, idMode);
                int frames = Math.Max(1, (int)Math.Ceiling(duration * machine.FramesPerSecond));
                var observer = profile.DriverObservation is { } observation ? new DriverStateObserver(observation) : null;
                DriverLoop? driverLoop = null;
                bool driverFinished = false;
                int capturedFrames = 0;
                for (int tick = 0; tick < frames; tick++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    StepDriver(machine, profile);
                    capturedFrames++;
                    if (observer is null) continue;
                    var state = observer.Observe(a => machine.Memory((ushort)a), (long)capturedFrames * machine.SamplesPerFrame);
                    if (state.Finished)
                    {
                        driverFinished = true;
                        // Keep the driver's final mute writes and a short filter-release tail.
                        for (int tail = 0; tail < Math.Ceiling(machine.FramesPerSecond * 0.1); tail++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            StepDriver(machine, profile);
                            capturedFrames++;
                        }
                        break;
                    }
                    if (state.Loop is { } repeated && !profile.OneShotMusicIds.Contains(id))
                    {
                        driverLoop = repeated;
                        break;
                    }
                }
                var vgm = machine.BuildVgm(name, capturedFrames * machine.SamplesPerFrame);
                // Validate the actual chip output, not just the number of register writes.
                var pcm = Mixing.MsxSoundRenderer.Render(vgm, new Mixing.AudioProfile { Oversample = 1 }, 8000);
                cancellationToken.ThrowIfCancellationRequested();
                int last = Array.FindLastIndex(pcm.Samples, sample => Math.Abs((int)sample) > 2);
                bool silent = last < 0;
                double audibleEnd = silent ? 0 : (last / pcm.Channels + 1) / (double)pcm.SampleRate;
                bool limit = !silent && !driverFinished && driverLoop is null && audibleEnd >= vgm.TotalSeconds - 0.1;
                if (!silent && driverLoop is { } cycle)
                    vgm.SetLoop(cycle.StartSample / 44100.0, cycle.DurationSamples / 44100.0);
                if (!silent && observer is null && music && !profile.OneShotMusicIds.Contains(id))
                {
                    var loop = VgmLoopFinder.Find(vgm);
                    if (loop is { } found && found.Start + found.Duration <= vgm.TotalSeconds)
                        vgm.SetLoop(found.Start, found.Duration);
                }
                if (!silent && !vgm.HasLoop && !limit)
                {
                    long samples = Math.Min(vgm.TotalSamples, (long)Math.Ceiling((audibleEnd + 0.05) * 44100));
                    vgm = VgmFile.FromEvents(vgm.Events.Where(e => e.Sample <= samples).ToArray(), (int)samples, vgm.AyClock, name);
                }
                string termination = driverFinished ? "driver-stopped" : driverLoop is not null ? "driver-cycle" : vgm.HasLoop ? "event-pattern" : "capture-limit";
                if (!silent && profile.DriverObservation?.Required == true && !driverFinished && driverLoop is null)
                    throw new InvalidDataException($"{name}: no driver termination or stable cycle within {duration}s.");
                result.Diagnostics.Add(new(id, name, vgm.Events.Count, pcm.Peak, vgm.TotalSeconds, silent, limit, termination));
                if (silent) log?.Invoke($"SKIPPED {name}: register writes produced silence");
                else
                {
                    string evidence = profile.NameSources.TryGetValue(id, out string? source) ? "rom:" + source : "name:unverified";
                    var song = music
                        ? result.Library.AddSong(name, name, vgm, vgm.HasLoop, vgm.LoopStartSeconds, vgm.LoopSeconds, id, "music", evidence)
                        : result.Library.AddClip(name, name, vgm, id, "sfx", evidence);
                    if (limit && !vgm.HasLoop)
                        song.Tags = song.Tags.Concat(new[] { "capture-limit:unresolved" }).ToArray();
                    if (observer is not null)
                        song.Tags = song.Tags.Concat(new[] { "termination:" + termination }).ToArray();
                    result.VgmFiles[id] = song.Entry;
                    log?.Invoke($"{name}: {vgm.TotalSeconds:F3}s, peak={pcm.Peak:F4}, loop={vgm.HasLoop}, captureLimit={limit}");
                }
                done++;
                progress?.Invoke(done, profile.Ids.Count);
            }
            if (!profile.PartialExtraction)
            {
                foreach (int required in profile.RequiredIds)
                    if (result.Library.FindByOriginalId(required) is null)
                        throw new InvalidDataException($"Required sound {required} did not produce audible output.");
                foreach (int required in profile.RequiredLoopIds)
                    if (result.Library.FindByOriginalId(required)?.Loop != true)
                        throw new InvalidDataException($"Required music loop {required} was not verified; increase its capture duration.");
            }
            return result;
        }
        catch { result.Library.Dispose(); throw; }
    }

    private static void MapDriver(MiniMsx machine, RomExtractionProfile profile)
    {
        foreach (var bank in profile.DriverBanks) machine.SetBank((ushort)bank.Key, bank.Value);
    }

    private static void StepDriver(MiniMsx machine, RomExtractionProfile profile)
    {
        if (profile.PlayerAddress != 0)
        {
            MapDriver(machine, profile);
            machine.CallAddress((ushort)profile.PlayerAddress);
            machine.Advance();
        }
        else machine.Frame();
    }

    private static void TriggerSound(MiniMsx machine, RomExtractionProfile profile, int id, SoundIdMode mode)
    {
        ushort routine = (ushort)profile.SoundCallAddress;

        if (profile.SoundCallBank < 0 || !machine.IsBanked)
        {
            machine.TriggerSound(routine, id, mode);
            return;
        }

        int[] saved = machine.SaveBanks();
        machine.SetBank(routine, profile.SoundCallBank);
        machine.TriggerSound(routine, id, mode);
        machine.RestoreBanks(saved);
    }
}
