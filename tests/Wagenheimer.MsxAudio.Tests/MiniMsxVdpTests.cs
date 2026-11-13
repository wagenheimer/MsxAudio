using System;
using Wagenheimer.MsxAudio.Extraction;
using Xunit;

namespace Wagenheimer.MsxAudio.Tests;

/// <summary>
/// Tests for the VDP model: the cartridge draws through the data port and through the BIOS services, so
/// the machine must keep the VRAM (that is how graphics can be read back from a ROM run).
/// </summary>
public class MiniMsxVdpTests
{
    private static byte[] BuildRom()
    {
        var rom = new byte[0x4000];
        rom[0] = (byte)'A';
        rom[1] = (byte)'B';
        rom[2] = 0x10; // INIT = 0x4010
        rom[3] = 0x40;
        rom[0x10] = 0xC9; // RET
        return rom;
    }

    [Fact]
    public void DataPort_WritesAtTheAddressSetByTheControlPort()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.Linear);

        machine.WritePort(0x99, 0x00); // address low
        machine.WritePort(0x99, 0x18); // address high (0x1800), write mode
        machine.WritePort(0x98, 0xAB);
        machine.WritePort(0x98, 0xCD);

        Assert.Equal(0xAB, machine.Vram[0x1800]);
        Assert.Equal(0xCD, machine.Vram[0x1801]);
        Assert.Equal(0x1802, machine.VdpAddress);
    }

    [Fact]
    public void ControlPort_WithBit7_WritesAVdpRegister()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.Linear);

        machine.WritePort(0x99, 0x07); // register 7
        machine.WritePort(0x99, 0x8E); // bit 7 = register write

        Assert.Equal(0x8E, machine.VdpRegisters[7]);
    }

    [Fact]
    public void BiosSetWrt_ThenDataPort_PlacesTheBytes()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.Linear);

        machine.CallRoutine(0x0053, hl: 0x2000);
        machine.WritePort(0x98, 0x11);

        Assert.Equal(0x11, machine.Vram[0x2000]);
    }

    [Fact]
    public void BiosFilvrm_FillsTheRange()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.Linear);

        machine.CallRoutine(0x0056, a: 0x55, hl: 0x3000, bc: 16);

        for (int i = 0; i < 16; i++)
            Assert.Equal(0x55, machine.Vram[0x3000 + i]);
    }

    [Fact]
    public void BiosLdirvm_CopiesRamIntoVram()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.Linear);

        for (int i = 0; i < 8; i++)
            machine.Write((ushort)(0xE000 + i), (byte)(0xA0 + i));

        machine.CallRoutine(0x005C, hl: 0xE000, de: 0x1000, bc: 8);

        for (int i = 0; i < 8; i++)
            Assert.Equal((byte)(0xA0 + i), machine.Vram[0x1000 + i]);
    }

    [Fact]
    public void Palette_WritesGRB444()
    {
        var machine = new MiniMsx(BuildRom(), RomMapper.Linear);

        machine.WritePort(0x9A, 0x02); // index 2
        machine.WritePort(0x9B, 0x45); // low nibble pair
        machine.WritePort(0x9B, 0x0A); // high nibble

        Assert.Equal(0x0A45, machine.Palette[2]);
    }
}
