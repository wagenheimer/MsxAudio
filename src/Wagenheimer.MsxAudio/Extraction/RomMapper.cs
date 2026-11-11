using System;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>Cartridge mapper used by <see cref="MiniMsx"/>.</summary>
public enum RomMapper
{
    /// <summary>Picks a mapper from the ROM size: up to 32 KB linear, larger Konami with SCC.</summary>
    Auto,
    /// <summary>Plain 16/32 KB cartridge (0x4000-0xBFFF fixed, no bank switching).</summary>
    Linear,
    /// <summary>Konami mapper (8 KB banks), without the SCC.</summary>
    Konami,
    /// <summary>Konami SCC mapper (8 KB banks + SCC at 0x9800-0x9FFF).</summary>
    KonamiScc,
    /// <summary>ASCII8 mapper (8 KB banks at 0x6000/0x6800/0x7000/0x7800).</summary>
    Ascii8,
    /// <summary>ASCII16 mapper (two 16 KB banks at 0x6000/0x7000).</summary>
    Ascii16,
}

/// <summary>Maps profile/CLI names (mostly openMSX cartridge types) to a <see cref="RomMapper"/>.</summary>
public static class RomMapperNames
{
    public static RomMapper Parse(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "konamiscc" or "konami scc" or "scc" => RomMapper.KonamiScc,
        "konami" or "konami5" => RomMapper.Konami,
        "ascii8" => RomMapper.Ascii8,
        "ascii16" => RomMapper.Ascii16,
        "linear" or "plain" or "none" => RomMapper.Linear,
        _ => RomMapper.Auto,
    };

    public static string Name(RomMapper mapper) => mapper switch
    {
        RomMapper.Linear => "linear",
        RomMapper.Konami => "konami",
        RomMapper.KonamiScc => "konami-scc",
        RomMapper.Ascii8 => "ascii8",
        RomMapper.Ascii16 => "ascii16",
        _ => "auto",
    };
}

/// <summary>
/// Cartridge memory window (0x4000-0xBFFF) with the common MSX mappers. The SCC itself is not
/// emulated here: <see cref="MiniMsx"/> captures its writes; this class only reserves its window.
/// </summary>
internal sealed class Cartridge
{
    private const int Bank8 = 0x2000;
    private const int Bank16 = 0x4000;

    private readonly byte[] _rom;
    private readonly int _banks8;
    private readonly int _banks16;
    private readonly int[] _map8 = { 0, 1, 2, 3 };
    private readonly int[] _map16 = { 0, 1 };

    public RomMapper Mapper { get; }
    public bool Scc { get; }

    public Cartridge(byte[] rom, RomMapper mapper, bool? scc)
    {
        _rom = rom;
        _banks8 = Math.Max(1, rom.Length / Bank8);
        _banks16 = Math.Max(1, rom.Length / Bank16);

        if (mapper == RomMapper.Auto)
            mapper = rom.Length <= 0x8000 ? RomMapper.Linear : RomMapper.KonamiScc;

        Mapper = mapper;
        Scc = scc ?? mapper == RomMapper.KonamiScc;

        // Initial banks: 0,1,2,3 (clamped so a small ROM never indexes past the end).
        for (int i = 0; i < _map8.Length; i++)
            _map8[i] = i % _banks8;

        for (int i = 0; i < _map16.Length; i++)
            _map16[i] = i % _banks16;
    }

    /// <summary>True when the address belongs to the SCC window (0x9800-0x9FFF).</summary>
    public bool IsScc(ushort address) => Scc && address >= 0x9800 && address < 0x9A00;

    public byte Read(ushort address)
    {
        if (address < 0x4000 || address >= 0xC000)
            return 0xFF;

        if (IsScc(address))
            return 0xFF; // write-only chip

        return Mapper switch
        {
            RomMapper.Ascii16 => Read16(address),
            RomMapper.Linear => Read8((address - 0x4000) / Bank8, address),
            _ => Read8(_map8[(address - 0x4000) / Bank8], address),
        };
    }

    /// <summary>Handles a write in the cartridge window. Returns true when the mapper consumed it.</summary>
    public bool Write(ushort address, byte value)
    {
        if (address < 0x4000 || address >= 0xC000)
            return false;

        if (IsScc(address))
            return true;

        switch (Mapper)
        {
            case RomMapper.Linear:
                return true;

            case RomMapper.Konami:
            case RomMapper.KonamiScc:
                // Konami SCC: 0x5000 -> 0x4000, 0x7000 -> 0x6000, 0x9000 -> 0x8000, 0xB000 -> 0xA000
                // (confirmed against the King's Valley II disassembly comments).
                if (address >= 0x5000 && address < 0x5800) Set8(0, value);
                else if (address >= 0x7000 && address < 0x7800) Set8(1, value);
                else if (address >= 0x9000 && address < 0x9800) Set8(2, value);
                else if (address >= 0xB000 && address < 0xB800) Set8(3, value);
                return true;

            case RomMapper.Ascii8:
                if (address >= 0x6000 && address < 0x6800) Set8(0, value);
                else if (address >= 0x6800 && address < 0x7000) Set8(1, value);
                else if (address >= 0x7000 && address < 0x7800) Set8(2, value);
                else if (address >= 0x7800 && address < 0x8000) Set8(3, value);
                return true;

            case RomMapper.Ascii16:
                if (address >= 0x6000 && address < 0x6800) Set16(0, value);
                else if (address >= 0x7000 && address < 0x7800) Set16(1, value);
                return true;

            default:
                return true;
        }
    }

    private byte Read8(int bank, ushort address)
    {
        int offset = bank * Bank8 + (address & (Bank8 - 1));
        return offset < _rom.Length ? _rom[offset] : (byte)0xFF;
    }

    private byte Read16(ushort address)
    {
        int bank = address < 0x8000 ? _map16[0] : _map16[1];
        int offset = bank * Bank16 + (address & (Bank16 - 1));
        return offset < _rom.Length ? _rom[offset] : (byte)0xFF;
    }

    private void Set8(int slot, byte value) => _map8[slot] = value % _banks8;

    private void Set16(int slot, byte value) => _map16[slot] = value % _banks16;
}
