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
        var machine = new MiniMsx(BuildRom());
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
        var machine = new MiniMsx(BuildRom());
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
        var machine = new MiniMsx(BuildRom());

        // bank 1 (0x6000) is selected by a write to 0x7000
        machine.Write(0x7000, 1);
        Assert.Equal(0x11, machine.Memory(0x6000));

        machine.Write(0x7000, 2);
        Assert.Equal(0x22, machine.Memory(0x6000));
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
