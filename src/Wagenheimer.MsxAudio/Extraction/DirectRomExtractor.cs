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
/// <para>The cartridge boots, installs its interrupt hook and the driver runs; the minimal machine
/// models the VDP vblank flag/status and the BIOS entry points the drivers use, so the cadence matches
/// the hardware. Use <see cref="OpenMsxRomExtractor"/> for bit-exact reference captures.</para>
/// <para>The <see cref="MiniMsx"/> boots the cartridge, then each id is captured by calling the game's
/// "play sound" routine and running the interrupt handler at 60 Hz for the requested duration.
/// The captured PSG/SCC register writes become a VGM (and therefore a <c>.msxaudio</c> entry),
/// exactly like the openMSX path.</para>
/// </remarks>
public static class DirectRomExtractor
{
    /// <summary>Runs the extraction.</summary>
    /// <param name="options">ROM, profile and output settings.</param>
    /// <param name="log">Receives progress lines.</param>
    /// <param name="progress">Receives (done, total) ids, for a progress bar.</param>
    public static ExtractionResult Extract(RomExtractionOptions options, Action<string>? log = null,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(options.RomPath))
            throw new FileNotFoundException("ROM not found", options.RomPath);

        RomExtractionProfile profile = options.Profile;
        string romPath = RomFile.Resolve(options.RomPath);
        byte[] rom = File.ReadAllBytes(romPath);
        RomMapper mapper = RomMapperNames.Parse(profile.RomType);
        if (mapper == RomMapper.Auto)
            mapper = RomInfo.Analyze(rom).Mapper;

        var machine = new MiniMsx(rom, mapper)
        {
            CapturePsg = options.CapturePsg,
            CaptureScc = options.CaptureScc,
            RefreshRate = profile.RefreshRate,
        };

        SoundIdMode idMode = Enum.TryParse(profile.IdRegister, true, out SoundIdMode parsed) ? parsed : SoundIdMode.A;
        int fps = machine.FramesPerSecond;

        bool hooked = machine.Boot();
        var (resolvedMapper, scc) = machine.CartridgeInfo;
        log?.Invoke($"boot: PC={machine.Pc:X4}, hook={machine.InterruptHook:X4}, " +
                    $"mapper={RomMapperNames.Name(resolvedMapper)}{(scc ? " +SCC" : "")}, " +
                    $"sound call={profile.SoundCallAddress:X4}");

        if (!hooked)
            log?.Invoke("warning: the cartridge did not install an interrupt handler; " +
                        "the driver may not advance");

        var library = new MsxAudioLibrary(profile.Name);
        library.Manifest.Source = $"direct driver run ({Path.GetFileName(romPath)})";
        var files = new Dictionary<int, string>();
        int done = 0;

        foreach (int id in profile.Ids)
        {
            double duration = profile.SecondsById.TryGetValue(id, out double specific)
                ? specific
                : profile.MusicIds.Contains(id) ? profile.SecondsPerSong : profile.SecondsPerId;

            // silence everything first, then start the wanted sound
            TriggerSound(machine, profile, profile.StopId, idMode);
            for (int i = 0; i < 3; i++) machine.Frame();

            // Stream drivers gate by id (lower wins) and remember the song per channel: clearing the
            // channel state is what makes the next song start even after a higher-priority one.
            foreach (int address in profile.ResetAddresses)
                machine.Write((ushort)address, 0x00);

            machine.ResetCapture();
            TriggerSound(machine, profile, id | profile.IdMask, idMode);

            int frames = Math.Max(1, (int)Math.Round(duration * fps));

            for (int i = 0; i < frames; i++)
            {
                // Sound-only mode: step the driver itself, so the game's logic cannot replace the song.
                if (profile.PlayerAddress != 0)
                {
                    machine.CallAddress((ushort)profile.PlayerAddress);
                    machine.Advance();
                }
                else
                {
                    machine.Frame();
                }
            }

            var vgm = machine.BuildVgm($"id{id:00}", frames * machine.SamplesPerFrame);
            bool music = profile.MusicIds.Contains(id);
            if (music && !vgm.HasLoop)
            {
                var loop = VgmLoopFinder.Find(vgm);
                if (loop is { } found) vgm.SetLoop(found.Start, found.Duration);
            }

            string name = profile.Names.TryGetValue(id, out var friendly) ? friendly : $"id_{id:00}";
            var song = music
                ? library.AddSong(name, name, vgm, vgm.HasLoop, vgm.LoopStartSeconds, vgm.LoopSeconds, id, "music")
                : library.AddClip(name, name, vgm, id, "sfx");

            files[id] = song.Entry;
            log?.Invoke($"{name,-16} id {id,2}  {vgm.TotalSeconds,7:F2}s  {vgm.Events.Count,6} writes  " +
                        (vgm.HasLoop ? $"loop {vgm.LoopStartSeconds:F3}s + {vgm.LoopSeconds:F3}s" : "no loop"));

            progress?.Invoke(++done, profile.Ids.Count);
        }

        return new ExtractionResult { Library = library, VgmFiles = files };
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
