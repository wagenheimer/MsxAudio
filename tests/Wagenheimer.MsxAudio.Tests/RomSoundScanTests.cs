using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wagenheimer.MsxAudio.Extraction;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>
/// Tests for the sound scan: finding the "play sound" routine and probing the ids.
/// </summary>
public class RomSoundScanTests
{
    private const ushort InitAddress = 0x4010;
    private const ushort SoundRoutine = 0x4100;

    /// <summary>
    /// A cartridge whose INIT calls a "play sound" routine; the routine writes the id (in A) to PSG
    /// register 0, so every id that is called produces exactly one pitch write.
    /// </summary>
    private static byte[] BuildRom()
    {
        var rom = new byte[0x4000];

        rom[0] = (byte)'A';
        rom[1] = (byte)'B';
        rom[2] = InitAddress & 0xFF;
        rom[3] = InitAddress >> 8;

        // INIT: CALL SoundRoutine; RET
        int init = InitAddress - 0x4000;
        rom[init + 0] = 0xCD;
        rom[init + 1] = SoundRoutine & 0xFF;
        rom[init + 2] = SoundRoutine >> 8;
        rom[init + 3] = 0xC9;

        // Sound routine: LD C,A / LD A,0 / OUT (A0h),A / LD A,C / OUT (A1h),A / RET
        int sound = SoundRoutine - 0x4000;
        rom[sound + 0] = 0x4F;
        rom[sound + 1] = 0x3E;
        rom[sound + 2] = 0x00;
        rom[sound + 3] = 0xD3;
        rom[sound + 4] = 0xA0;
        rom[sound + 5] = 0x79;
        rom[sound + 6] = 0xD3;
        rom[sound + 7] = 0xA1;
        rom[sound + 8] = 0xC9;

        return rom;
    }

    private static string WriteRom()
    {
        string path = Path.Combine(Path.GetTempPath(), "msxaudio-scan-" + Guid.NewGuid().ToString("N") + ".rom");
        File.WriteAllBytes(path, BuildRom());
        return path;
    }

    [Fact]
    public void FindCandidates_FindsTheRoutineThatWritesThePsg()
    {
        List<ushort> candidates = RomSoundScan.FindCandidates(BuildRom());

        Assert.Contains(SoundRoutine, candidates);
    }

    [Fact]
    public void Scan_FindsTheSoundRoutineAndEveryIdThatProducesSound()
    {
        string rom = WriteRom();

        try
        {
            var options = new RomScanOptions
            {
                RomPath = rom,
                Mapper = RomMapper.Linear,
                FirstId = 1,
                LastId = 3,
                SecondsPerId = 0.4,
                FailureSeconds = 0.1,
                MinNoteWrites = 1,
            };

            RomScanResult result = RomSoundScan.Run(options);

            Assert.Equal(SoundRoutine, result.Routine);
            Assert.Equal(new[] { 1, 2, 3 }, result.Songs.Select(s => s.Id).ToArray());
            Assert.All(result.Songs, song => Assert.Equal(1, song.NoteWrites));
        }
        finally
        {
            File.Delete(rom);
        }
    }

    [Fact]
    public void Scan_WithAnExplicitRoutineSkipsDiscovery()
    {
        string rom = WriteRom();

        try
        {
            var options = new RomScanOptions
            {
                RomPath = rom,
                Mapper = RomMapper.Linear,
                SoundCallAddress = SoundRoutine,
                FirstId = 5,
                LastId = 6,
                SecondsPerId = 0.2,
                FailureSeconds = 0.1,
                MinNoteWrites = 1,
            };

            RomScanResult result = RomSoundScan.Run(options);

            Assert.Equal(SoundRoutine, result.Routine);
            Assert.Empty(result.Candidates);
            Assert.Equal(2, result.Songs.Count);
            Assert.Equal(5, result.Songs[0].Id);
        }
        finally
        {
            File.Delete(rom);
        }
    }

    [Fact]
    public void ToLibrary_NamesSongsFromTheProfileOrId()
    {
        string rom = WriteRom();

        try
        {
            var options = new RomScanOptions
            {
                RomPath = rom,
                Mapper = RomMapper.Linear,
                SoundCallAddress = SoundRoutine,
                FirstId = 1,
                LastId = 2,
                SecondsPerId = 0.2,
                FailureSeconds = 0.1,
                MinNoteWrites = 1,
            };

            RomScanResult result = RomSoundScan.Run(options);
            using var library = result.ToLibrary("test", new Dictionary<int, string> { [2] = "jump" });

            Assert.Equal(2, library.Songs.Count + library.Clips.Count);
            Assert.Contains(library.Songs.Concat(library.Clips), s => s.Name == "id_01");
            Assert.Contains(library.Songs.Concat(library.Clips), s => s.Name == "jump");
        }
        finally
        {
            File.Delete(rom);
        }
    }
}
