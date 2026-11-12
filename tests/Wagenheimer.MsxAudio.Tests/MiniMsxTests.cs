using System;
using System.Collections.Generic;
using System.IO;
using Wagenheimer.MsxAudio.Extraction;
using Wagenheimer.MsxAudio.Vgm;
using Xunit;
using Xunit.Abstractions;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>Tests for the built-in MSX machine used by the direct extractor.</summary>
public class MiniMsxTests
{
    private const ushort InitAddress = 0x4020;
    private const ushort SoundRoutine = 0x4040;

    private readonly ITestOutputHelper _output;

    public MiniMsxTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Boot_RunsInitAndCapturesPsgAndSccWrites()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.KonamiScc);
        machine.ResetCapture();

        machine.Boot();
        machine.TriggerSound(InitAddress, 5);

        var vgm = machine.BuildVgm("test", 735 * 10);
        Dump(vgm);

        Assert.True(vgm.Events.Count >= 5, $"expected PSG+SCC writes, got {vgm.Events.Count}");
        Assert.Contains(vgm.Events, e => e.Kind == VgmEventKind.Psg && e.P0 == 7 && e.P1 == 0x3E);
        Assert.Contains(vgm.Events, e => e.Kind == VgmEventKind.Psg && e.P0 == 8 && e.P1 == 0x0F);
        Assert.Contains(vgm.Events, e => e.Kind == VgmEventKind.Scc && e.P1 == 4 && e.P2 == 0x0A);
    }

    [Fact]
    public void TriggerSound_PassesTheIdInRegisterA()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.KonamiScc);
        machine.Boot();
        machine.ResetCapture();

        machine.TriggerSound(SoundRoutine, 0x2A);

        var vgm = machine.BuildVgm("test", 735);
        Dump(vgm);

        Assert.Contains(vgm.Events, e => e.Kind == VgmEventKind.Psg && e.P0 == 9 && e.P1 == 0x2A);
    }

    [Fact]
    public void MemoryMapper_SwitchesBanks()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.KonamiScc);

        // Konami mapper: 0x7000 selects the bank at 0x6000 (confirmed by the disassembly).
        machine.Write(0x7000, 1);
        Assert.Equal(0x11, machine.Memory(0x6000));

        machine.Write(0x7000, 2);
        Assert.Equal(0x22, machine.Memory(0x6000));
    }

    [Fact]
    public void LinearCartridge_MapsOnlyTheFirstWindow()
    {
        var rom = new byte[0x4000];
        rom[0] = (byte)'A';
        rom[1] = (byte)'B';
        rom[0x1000] = 0x5A;

        var machine = new MiniMsx(rom, RomMapper.Linear);
        Assert.Equal(0x5A, machine.Memory(0x5000));
        Assert.Equal(0x00, machine.Memory(0x8000)); // a 16 KB cartridge leaves RAM in page 2
        Assert.Equal(RomMapper.Linear, machine.CartridgeInfo.Mapper);
    }

    [Fact]
    public void Ascii8Mapper_SwitchesBanks()
    {
        var rom = new byte[0x10000]; // 64 KB = 8 banks of 8 KB
        rom[0x2000] = 0x33;
        rom[0x4000] = 0x44;

        var machine = new MiniMsx(rom, RomMapper.Ascii8);
        machine.Write(0x6000, 1);
        Assert.Equal(0x33, machine.Memory(0x4000));

        machine.Write(0x6000, 2);
        Assert.Equal(0x44, machine.Memory(0x4000));
    }

    [Fact]
    public void VdpStatus_LatchesVBlankAndClearsOnRead()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.KonamiScc);

        machine.Frame();
        Assert.Equal(0x80, machine.ReadPort(0x99) & 0x80);   // vblank flag
        Assert.Equal(0x80, machine.Memory(0xF3E7) & 0x80);   // BIOS STATFL mirrors the last read
        Assert.Equal(0x00, machine.ReadPort(0x99) & 0x80);   // cleared by the read
    }

    [Fact]
    public void KeyboardMatrix_SelectsRowsAndReadsPressedBits()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.KonamiScc);

        machine.PressKey(8, 0, true);            // Space
        machine.WritePort(0xAA, 8);              // PPI port C selects the row
        Assert.Equal(0xFE, machine.ReadPort(0xA9));

        machine.ReleaseAllKeys();
        Assert.Equal(0xFF, machine.ReadPort(0xA9));
    }

    [Fact]
    public void PortA8_IsTheSlotRegisterAndDoesNotTouchTheKeyboard()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.KonamiScc);

        machine.PressKey(8, 0, true);
        machine.WritePort(0xAA, 8);
        machine.WritePort(0xA8, 0xFF);           // all pages -> RAM slot
        Assert.Equal(0xFE, machine.ReadPort(0xA9));
        Assert.Equal(0xFF, machine.ReadPort(0xA8));
    }

    [Fact]
    public void Konami4_UsesItsOwnBankRegisters_AndKeepsBank0AtTheFirstWindow()
    {
        var rom = new byte[0x20000]; // 128 KB = 16 banks of 8 KB
        for (int bank = 0; bank < 16; bank++)
            rom[bank * 0x2000] = (byte)(0xA0 + bank);

        var machine = new MiniMsx(rom, RomMapper.Konami);

        machine.Write(0x6000, 5);                // registers live at 0x6000/0x8000/0xA000
        machine.Write(0x8000, 6);
        machine.Write(0xA000, 7);

        Assert.Equal(0xA0, machine.Memory(0x4000)); // fixed bank 0
        Assert.Equal(0xA5, machine.Memory(0x6000));
        Assert.Equal(0xA6, machine.Memory(0x8000));
        Assert.Equal(0xA7, machine.Memory(0xA000));

        machine.Write(0x5000, 9);                // the SCC register address does nothing here
        Assert.Equal(0xA0, machine.Memory(0x4000));
    }

    [Fact]
    public void SccWindow_IsOnlyVisibleWhileBank2HoldsTheSccBank()
    {
        var rom = new byte[0x20000];
        rom[4 * 0x2000 + 0x1800] = 0x77;         // a data byte at 0x9800 when bank 4 is mapped at 0x8000

        var machine = new MiniMsx(rom, RomMapper.KonamiScc);
        machine.ResetCapture();

        machine.Write(0x9000, 4);
        Assert.Equal(0x77, machine.Memory(0x9800));          // plain ROM data, not 0xFF
        machine.Write(0x9804, 0x11);
        Assert.Equal(0, machine.EventCount);                  // not an SCC write

        machine.Write(0x9000, 0x3F);
        machine.Write(0x9804, 0x11);
        Assert.Equal(1, machine.EventCount);                  // now it is
    }

    [Fact]
    public void Ascii16_AndRType_SwitchBanks()
    {
        var rom = new byte[0x20000];             // 8 banks of 16 KB
        for (int bank = 0; bank < 8; bank++)
            rom[bank * 0x4000] = (byte)(0xB0 + bank);

        var ascii16 = new MiniMsx(rom, RomMapper.Ascii16);
        ascii16.Write(0x6000, 3);
        ascii16.Write(0x7000, 5);
        Assert.Equal(0xB3, ascii16.Memory(0x4000));
        Assert.Equal(0xB5, ascii16.Memory(0x8000));

        var rtype = new MiniMsx(rom, RomMapper.RType);
        Assert.Equal(0xB7, rtype.Memory(0x4000));            // bank 0x0F % 8 fixed
        rtype.Write(0x6000, 2);
        Assert.Equal(0xB2, rtype.Memory(0x8000));
    }

    [Fact]
    public void Ascii8_StartsOnBankZeroEverywhere()
    {
        var rom = new byte[0x10000];
        rom[0] = 0x41;
        rom[0x2000] = 0x42;

        var machine = new MiniMsx(rom, RomMapper.Ascii8);
        Assert.Equal(0x41, machine.Memory(0x6000));
    }

    [Fact]
    public void Slots_EnaSltPutsRamInPage2()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.KonamiScc);

        Assert.Equal(0x22, machine.Memory(0x8000));           // cartridge bank 2 by default

        // ENASLT: A = slot 3, H = 0x80 (page 2)
        machine.CallRoutine(0x0024, a: 3, hl: 0x8000);
        machine.Write(0x8000, 0x5A);
        Assert.Equal(0x5A, machine.Memory(0x8000));           // RAM now
        Assert.Equal(0x11, machine.Memory(0x6000));           // page 1 still the cartridge
    }

    [Fact]
    public void Boot_ReportsAMissingHeader()
    {
        var rom = new byte[0x4000];
        var machine = new MiniMsx(rom, RomMapper.Linear);

        machine.Boot();

        Assert.False(machine.HasHeader);
        Assert.Contains(machine.Diagnose(), line => line.Contains("AB"));
    }

    [Fact]
    public void Page2Cartridge_LoadsAt0x8000()
    {
        var rom = new byte[0x4000];
        rom[0] = (byte)'A';
        rom[1] = (byte)'B';
        rom[2] = 0x10; rom[3] = 0x80;    // INIT 0x8010
        rom[8] = 0x00; rom[9] = 0x80;    // TEXT in page 2
        rom[0x10] = 0xC9;

        var machine = new MiniMsx(rom, RomMapper.Auto);
        Assert.Equal((byte)'A', machine.Memory(0x8000));

        machine.Boot();

        Assert.True(machine.HasHeader);
        Assert.Equal(0x8010, machine.InitAddress);
    }

    [Fact]
    public void MapperGuess_FollowsOpenMsxVotes()
    {
        static byte[] RomWithStores(params int[] addresses)
        {
            var rom = new byte[0x20000];
            int at = 0x100;
            foreach (int address in addresses)
            {
                rom[at++] = 0x32;
                rom[at++] = (byte)(address & 0xFF);
                rom[at++] = (byte)(address >> 8);
            }

            return rom;
        }

        Assert.Equal(RomMapper.KonamiScc, RomMapperNames.Guess(RomWithStores(0x5000, 0x9000, 0xB000, 0x7000)));
        Assert.Equal(RomMapper.Konami, RomMapperNames.Guess(RomWithStores(0x8000, 0xA000, 0x8000, 0xA000)));
        Assert.Equal(RomMapper.Ascii8, RomMapperNames.Guess(RomWithStores(0x6800, 0x7800, 0x6800, 0x7800)));
        Assert.Equal(RomMapper.Ascii16, RomMapperNames.Guess(RomWithStores(0x6000, 0x77FF, 0x7000)));
        Assert.Equal(RomMapper.Linear, RomMapperNames.Guess(new byte[0x8000]));
    }

    [Fact]
    public void Halt_WaitsForTheNextFrame_InsteadOfSpinning()
    {
        // INIT never returns: HALT; LD HL,0xC000; INC (HL); JR back (a vblank-synchronised main loop)
        var rom = new byte[0x4000];
        rom[0] = (byte)'A'; rom[1] = (byte)'B';
        rom[2] = 0x10; rom[3] = 0x40;
        byte[] loop = { 0x76, 0x21, 0x00, 0xC0, 0x34, 0x18, 0xF9 };
        Array.Copy(loop, 0, rom, 0x10, loop.Length);

        var machine = new MiniMsx(rom, RomMapper.Linear);
        machine.Boot(20_000);
        machine.Write(0xC000, 0);                // forget the iterations the boot spent

        for (int i = 0; i < 10; i++)
            machine.Frame();

        // About one iteration per frame, never thousands.
        Assert.InRange(machine.Memory(0xC000), 8, 12);
    }

    [Fact]
    public void Timestamps_AreMonotonicAndAdvanceWithTheFrame()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.KonamiScc);
        machine.Boot();
        machine.ResetCapture();

        machine.TriggerSound(SoundRoutine, 1);
        machine.Advance();
        machine.TriggerSound(SoundRoutine, 2);

        var events = machine.CapturedEvents;
        Assert.True(events.Count >= 2);
        Assert.True(events[0].Sample <= events[^1].Sample);
        Assert.True(events[^1].Sample >= machine.SamplesPerFrame);   // second trigger is in the next frame
    }

    [Fact]
    public void RefreshRate_ChangesTheFrameLength()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.KonamiScc) { RefreshRate = 50 };
        Assert.Equal(882, machine.SamplesPerFrame);
        Assert.Equal(50, machine.FramesPerSecond);
    }

    [Fact]
    public void Diagnose_ReportsFmWritesAndBiosStubs()
    {
        // INIT: LD A,1 ; OUT (7Ch),A ; CALL 0x00DB (GTPAD, a RET stub) ; RET
        var rom = new byte[0x4000];
        rom[0] = (byte)'A'; rom[1] = (byte)'B';
        rom[2] = 0x10; rom[3] = 0x40;
        byte[] init = { 0x3E, 0x01, 0xD3, 0x7C, 0xCD, 0xDB, 0x00, 0xC9 };
        Array.Copy(init, 0, rom, 0x10, init.Length);

        var machine = new MiniMsx(rom, RomMapper.Linear);
        machine.Boot();

        IReadOnlyList<string> lines = machine.Diagnose();
        Assert.Contains(lines, l => l.Contains("MSX-MUSIC"));
        Assert.Contains(lines, l => l.Contains("00DB"));
    }

    [Fact]
    public void Psg_ReadsBackWhatWasWritten_AndSkipsTheJoystickRegistersInTheCapture()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.KonamiScc);
        machine.ResetCapture();

        machine.WritePort(0xA0, 7);
        machine.WritePort(0xA1, 0xB8);
        machine.WritePort(0xA0, 15);
        machine.WritePort(0xA1, 0x8F);           // joystick select: not audio

        machine.WritePort(0xA0, 7);
        Assert.Equal(0xB8, machine.ReadPort(0xA2));
        Assert.Equal(1, machine.EventCount);
    }

    [Fact]
    public void Scan_FindsARoutineThatLivesInAnotherBank_AndATakesTheIdInHl()
    {
        // 64 KB Konami-style cartridge: bank 0 = header + INIT, bank 5 = the sound routine (PSG reg 0
        // gets L). The routine is called through "LD A,5 / LD (0x6000),A / CALL 0x6100".
        var rom = new byte[0x10000];
        rom[0] = (byte)'A'; rom[1] = (byte)'B';
        rom[2] = 0x10; rom[3] = 0x40;
        rom[0x10] = 0xC9;

        int code = 5 * 0x2000;
        byte[] routine =
        {
            0x7D,             // LD A,L
            0x4F,             // LD C,A
            0x3E, 0x00,       // LD A,0
            0xD3, 0xA0,       // OUT (0xA0),A
            0x79,             // LD A,C
            0xD3, 0xA1,       // OUT (0xA1),A
            0xC9,
        };
        Array.Copy(routine, 0, rom, code + 0x100, routine.Length);

        // Callers (so it ranks): CALL 0x6100 from bank 0, three times.
        int at = 0x40;
        for (int i = 0; i < 3; i++)
        {
            rom[at++] = 0xCD; rom[at++] = 0x00; rom[at++] = 0x61;
        }

        List<RomSoundScan.Candidate> list = RomSoundScan.FindCandidateList(rom, RomMapper.Konami, 8);
        Assert.Contains(list, c => c.Address == 0x6100 && c.Bank == 5);
    }

    private void Dump(VgmFile vgm)
    {
        foreach (VgmEvent e in vgm.Events)
            _output.WriteLine($"{e.Sample,6} {e.Kind,-3} p0={e.P0:X2} p1={e.P1:X2} p2={e.P2:X2}");
    }

    /// <summary>
    /// Builds a tiny Konami SCC cartridge: header + an INIT that writes PSG/SCC registers and a
    /// sound routine that copies A to PSG register 9.
    /// </summary>
    private static byte[] BuildRom()
    {
        var rom = new byte[0x8000]; // 32 KB = 4 banks of 8 KB

        // bank 1 and 2 start with a marker, used by the mapper test
        rom[0x2000] = 0x11;
        rom[0x4000] = 0x22;

        // header at 0x4000 (ROM offset 0)
        rom[0] = (byte)'A';
        rom[1] = (byte)'B';
        rom[2] = (byte)(InitAddress & 0xFF);
        rom[3] = (byte)(InitAddress >> 8);

        int code = InitAddress - 0x4000;
        var init = new byte[]
        {
            0x3E, 0x07,             // LD A,7
            0xD3, 0xA0,             // OUT (0xA0),A   -> register latch = 7
            0x3E, 0x3E,             // LD A,0x3E
            0xD3, 0xA1,             // OUT (0xA1),A   -> PSG[7] = 0x3E
            0x3E, 0x08,             // LD A,8
            0xD3, 0xA0,             // OUT (0xA0),A   -> register latch = 8
            0x3E, 0x0F,             // LD A,0x0F
            0xD3, 0xA1,             // OUT (0xA1),A   -> PSG[8] = 0x0F
            0x3E, 0x3F,             // LD A,0x3F
            0x32, 0x00, 0x90,       // LD (0x9000),A  -> bank 2 = SCC bank
            0x3E, 0x0A,             // LD A,0x0A
            0x32, 0x04, 0x98,       // LD (0x9804),A  -> SCC wave byte 4
            0xC9,                   // RET
        };
        Array.Copy(init, 0, rom, code, init.Length);

        int sound = SoundRoutine - 0x4000;
        var routine = new byte[]
        {
            0xF5,                   // PUSH AF        (keeps the id passed in A)
            0x3E, 0x09,             // LD A,9
            0xD3, 0xA0,             // OUT (0xA0),A   -> register latch = 9
            0xF1,                   // POP AF         (A = id again)
            0xD3, 0xA1,             // OUT (0xA1),A   -> PSG[9] = id
            0xC9,                   // RET
        };
        Array.Copy(routine, 0, rom, sound, routine.Length);

        return rom;
    }
}
