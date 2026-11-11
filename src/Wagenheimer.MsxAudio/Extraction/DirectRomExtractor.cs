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
/// <para><b>Experimental.</b> The cartridge boots, installs its interrupt hook and the driver runs,
/// but the minimal machine does not model the VDP vblank flag/status precisely yet, so the driver
/// advances slightly off the hardware cadence (a ~6% tempo difference on King's Valley II) and the
/// loop is not detected. Use <see cref="OpenMsxRomExtractor"/> for bit-exact reference captures.</para>

/// <para>The <see cref="MiniMsx"/> boots the cartridge, then each id is captured by calling the game's
/// "play sound" routine and running the interrupt handler at 60 Hz for the requested duration.
/// The captured PSG/SCC register writes become a VGM (and therefore a <c>.msxaudio</c> entry),
/// exactly like the openMSX path.</para>
/// </remarks>
public static class DirectRomExtractor
{
    /// <summary>Runs the extraction.</summary>
    public static ExtractionResult Extract(RomExtractionOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(options.RomPath))
            throw new FileNotFoundException("ROM not found", options.RomPath);

        RomExtractionProfile profile = options.Profile;
        byte[] rom = File.ReadAllBytes(options.RomPath);
        var machine = new MiniMsx(rom);

        bool hooked = machine.Boot();
        log?.Invoke($"boot: PC={machine.Pc:X4}, H.TIMI={machine.InterruptHook:X4}, " +
                    $"sound call={profile.SoundCallAddress:X4}");

        if (!hooked)
            log?.Invoke("warning: the cartridge did not install an interrupt handler; " +
                        "the driver may not advance");

        var library = new MsxAudioLibrary(profile.Name);
        library.Manifest.Source = $"direct driver run ({Path.GetFileName(options.RomPath)})";
        var files = new Dictionary<int, string>();

        foreach (int id in profile.Ids)
        {
            double duration = profile.SecondsById.TryGetValue(id, out double specific)
                ? specific
                : profile.MusicIds.Contains(id) ? profile.SecondsPerSong : profile.SecondsPerId;

            // silence everything first, then start the wanted sound
            machine.TriggerSound((ushort)profile.SoundCallAddress, profile.StopId);
            for (int i = 0; i < 3; i++) machine.Frame();

            machine.ResetCapture();
            machine.TriggerSound((ushort)profile.SoundCallAddress, id);

            int frames = Math.Max(1, (int)Math.Round(duration * 60));
            for (int i = 0; i < frames; i++) machine.Frame();

            var vgm = machine.BuildVgm($"id{id:00}", frames * 735);
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
        }

        return new ExtractionResult { Library = library, VgmFiles = files };
    }
}
