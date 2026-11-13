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

        machine.WritePort(0x99, 0x8E); // data for the register
        machine.WritePort(0x99, 0x87); // 0x80 | register 7

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

    [Fact]
    public void Bios_CarriesTheVdpDataPortInTheHeader()
    {
        // Konami's decompressor does `LD A,(0x0007); LD C,A; OUT (C),A`, so the BIOS header must carry
        // the data port; a BIOS of RETs sent every byte to port 0 and nothing reached the VRAM.
        var rom = BuildRom();
        rom[0x10] = 0x3A; rom[0x11] = 0x07; rom[0x12] = 0x00; // LD A,(0x0007)
        rom[0x13] = 0x32; rom[0x14] = 0x00; rom[0x15] = 0xE0; // LD (0xE000),A
        rom[0x16] = 0xC9;                                     // RET

        var machine = new MiniMsx(rom, RomMapper.Linear);
        machine.CallRoutine(0x4010);

        Assert.Equal(0x98, machine.Memory(0xE000));
    }

    [Fact]
    public void SetBiosByte_OverridesWhatTheCartridgeReads()
    {
        var rom = BuildRom();
        rom[0x10] = 0x3A; rom[0x11] = 0x2B; rom[0x12] = 0x00; // LD A,(0x002B)
        rom[0x13] = 0x32; rom[0x14] = 0x00; rom[0x15] = 0xE0; // LD (0xE000),A
        rom[0x16] = 0xC9;                                     // RET

        var machine = new MiniMsx(rom, RomMapper.Linear);
        machine.SetBiosByte(0x002B, 0x5A);
        machine.CallRoutine(0x4010);

        Assert.Equal(0x5A, machine.Memory(0xE000));
    }

    [Fact]
    public void VdpRegisterWrite_IsMirroredInTheBiosWorkArea()
    {
        // The BIOS keeps RG0SAV..RG7SAV at 0xF3DF and games read them back (e.g. RG1SAV | 0x22).
        var machine = new MiniMsx(BuildRom(), RomMapper.Linear);

        machine.WritePort(0x99, 0x62);
        machine.WritePort(0x99, 0x81); // register 1

        Assert.Equal(0x62, machine.VdpRegisters[1]);
        Assert.Equal(0x62, machine.Memory(0xF3E0));
    }
}
