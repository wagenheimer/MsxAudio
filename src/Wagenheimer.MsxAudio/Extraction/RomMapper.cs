using System;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>Cartridge mapper used by <see cref="MiniMsx"/>.</summary>
public enum RomMapper
{
    /// <summary>Picks a mapper from the ROM contents (see <see cref="RomMapperNames.Guess"/>).</summary>
    Auto,
    /// <summary>Plain 16/32 KB cartridge (no bank switching).</summary>
    Linear,
    /// <summary>Konami mapper without SCC: 8 KB banks, bank 0 fixed, registers at 0x6000/0x8000/0xA000.</summary>
    Konami,
    /// <summary>Konami SCC mapper: 8 KB banks, registers at 0x5000/0x7000/0x9000/0xB000, SCC at 0x9800.</summary>
    KonamiScc,
    /// <summary>ASCII8 mapper (8 KB banks at 0x6000/0x6800/0x7000/0x7800).</summary>
    Ascii8,
    /// <summary>ASCII16 mapper (two 16 KB banks at 0x6000/0x7000).</summary>
    Ascii16,
    /// <summary>R-Type mapper: bank 0x0F fixed at 0x4000, a 16 KB bank selectable at 0x8000.</summary>
    RType,
}

/// <summary>Maps profile/CLI names (mostly openMSX cartridge types) to a <see cref="RomMapper"/>.</summary>
public static class RomMapperNames
{
    /// <summary>Parses a mapper name; unknown names give <see cref="RomMapper.Auto"/>.</summary>
    public static RomMapper Parse(string? name)
    {
        string n = (name ?? string.Empty).Trim().ToLowerInvariant();

        return n switch
        {
            "konamiscc" or "konami scc" or "konami-scc" or "scc" or "konami5" or "2" => RomMapper.KonamiScc,
            "konami" or "konami4" or "3" or "generickonami" => RomMapper.Konami,
            "r-type" or "rtype" => RomMapper.RType,
            "linear" or "plain" or "none" or "normal" or "mirrored" or "page2" or "64kb" => RomMapper.Linear,
            _ when n.StartsWith("ascii8", StringComparison.Ordinal) => RomMapper.Ascii8,
            _ when n.StartsWith("ascii16", StringComparison.Ordinal) && !n.Contains('x') => RomMapper.Ascii16,
            _ when n.StartsWith("normal", StringComparison.Ordinal) ||
                   n.StartsWith("mirrored", StringComparison.Ordinal) => RomMapper.Linear,
            "4" => RomMapper.Ascii8,
            "5" => RomMapper.Ascii16,
            _ => RomMapper.Auto,
        };
    }

    /// <summary>Canonical lower-case name.</summary>
    public static string Name(RomMapper mapper) => mapper switch
    {
        RomMapper.Linear => "linear",
        RomMapper.Konami => "konami",
        RomMapper.KonamiScc => "konami-scc",
        RomMapper.Ascii8 => "ascii8",
        RomMapper.Ascii16 => "ascii16",
        RomMapper.RType => "r-type",
        _ => "auto",
    };

    /// <summary>
    /// Guesses the mapper from the image. Below 64 KB nothing is banked (same as openMSX); above, it
    /// counts <c>LD (nn),A</c> stores to the bank-register addresses and applies openMSX's votes.
    /// </summary>
    public static RomMapper Guess(byte[] rom)
    {
        ArgumentNullException.ThrowIfNull(rom);
        return Cartridge.DetectMapper(rom);
    }
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
    private readonly bool _sccGated;
    private int _sccBankRegister = 2;

    /// <summary>Resolved mapper.</summary>
    public RomMapper Mapper { get; }

    /// <summary>True when the SCC window exists for this cartridge.</summary>
    public bool Scc { get; }

    /// <summary>Address the first byte of the image appears at (0x4000, or 0x8000 for a page-2 ROM).</summary>
    public int LoadBase { get; }

    public Cartridge(byte[] rom, RomMapper mapper, bool? scc)
    {
        _rom = rom;
        _banks8 = Math.Max(1, rom.Length / Bank8);
        _banks16 = Math.Max(1, rom.Length / Bank16);

        if (mapper == RomMapper.Auto)
            mapper = DetectMapper(rom);

        Mapper = mapper;
        Scc = scc ?? mapper == RomMapper.KonamiScc;
        _sccGated = mapper == RomMapper.KonamiScc;
        LoadBase = mapper == RomMapper.Linear && IsPage2Rom(rom) ? 0x8000 : 0x4000;

        switch (mapper)
        {
            case RomMapper.Konami:
            case RomMapper.KonamiScc:
                for (int i = 0; i < _map8.Length; i++)
                    _map8[i] = i % _banks8;
                break;
            case RomMapper.Ascii8:
                Array.Clear(_map8);
                break;
            case RomMapper.Ascii16:
                Array.Clear(_map16);
                break;
            case RomMapper.RType:
                _map16[0] = 0x0F % _banks16;
                _map16[1] = 0;
                break;
        }
    }

    /// <summary>Number of 8 KB banks in the image.</summary>
    public int Banks8 => _banks8;

    /// <summary>True when the mapper switches banks (so code can live in any bank).</summary>
    public bool IsBanked => Mapper is RomMapper.Konami or RomMapper.KonamiScc or RomMapper.Ascii8
        or RomMapper.Ascii16 or RomMapper.RType;

    /// <summary>True when the address belongs to the (currently visible) SCC window.</summary>
    public bool IsScc(ushort address)
    {
        if (!Scc || address < 0x9800 || address >= 0xA000)
            return false;

        // The SCC only answers when bank register 0x9000 holds 0x3F (a real cartridge, otherwise the
        // window is ordinary ROM data and reading it must give the ROM bytes back).
        return !_sccGated || (_sccBankRegister & 0x3F) == 0x3F;
    }

    /// <summary>
    /// Detects a page-2 cartridge: a 16 KB image whose "AB" header points its INIT/TEXT into
    /// 0x8000-0xBFFF (openMSX's PAGE2 rule).
    /// </summary>
    internal static bool IsPage2Rom(byte[] rom)
    {
        if (rom.Length > 0x4000 || rom.Length < 16 || rom[0] != 'A' || rom[1] != 'B')
            return false;

        int init = rom[2] | (rom[3] << 8);
        int text = rom[8] | (rom[9] << 8);

        if ((text & 0xC000) != 0x8000)
            return false;

        return init == 0 || ((init & 0xC000) == 0x8000 && rom[init & (rom.Length - 1)] == 0xC9);
    }

    /// <summary>
    /// Picks a mapper the way openMSX does when its software database has no entry: nothing below
    /// 64 KB is banked; above, <c>LD (nn),A</c> stores vote for the mapper owning that register.
    /// </summary>
    internal static RomMapper DetectMapper(byte[] rom)
    {
        // Up to 32 KB fits the 0x4000-0xBFFF window: nothing to bank. From there to 64 KB openMSX also
        // says "not banked", but a 40, 48 or 56 KB image cannot sit in that window: the SCC release of
        // Knightmare (56 KB = seven 8 KB banks) starts by writing to the bank registers and runs from
        // the new bank. So those sizes are looked at too, and stay linear only if nothing writes a
        // bank register.
        bool bankable = rom.Length > 0x8000 && rom.Length < 0x10000;

        if (!bankable && (rom.Length < 0x10000 || (rom.Length == 0x10000 && !(rom[0] == 'A' && rom[1] == 'B'))))
            return RomMapper.Linear;

        int konamiScc = 0, konami = 0, ascii8 = 0, ascii16 = 0;

        for (int i = 0; i + 3 < rom.Length; i++)
        {
            if (rom[i] != 0x32)
                continue;

            switch (rom[i + 1] | (rom[i + 2] << 8))
            {
                case 0x5000: case 0x9000: case 0xB000:
                    konamiScc++;
                    break;
                case 0x4000: case 0x8000: case 0xA000:
                    konami++;
                    break;
                case 0x6800: case 0x7800:
                    ascii8++;
                    break;
                case 0x6000:
                    konami++; ascii8++; ascii16++;
                    break;
                case 0x7000:
                    konamiScc++; ascii8++; ascii16++;
                    break;
                case 0x77FF:
                    ascii16++;
                    break;
            }
        }

        if (ascii8 > 0)
            ascii8--; // openMSX penalises ASCII8 by one vote

        // Running max, later entry wins a tie (openMSX enum order: SCC, Konami, ASCII8, ASCII16).
        RomMapper best = UsesScc(rom) ? RomMapper.KonamiScc : RomMapper.Konami;
        int bestVotes = 0;

        void Consider(RomMapper candidate, int votes)
        {
            if (votes > 0 && votes >= bestVotes)
            {
                best = candidate;
                bestVotes = votes;
            }
        }

        Consider(RomMapper.KonamiScc, konamiScc);
        Consider(RomMapper.Konami, konami);
        Consider(RomMapper.Ascii8, ascii8);
        Consider(RomMapper.Ascii16, ascii16);

        if (bankable && bestVotes == 0)
            return RomMapper.Linear;                 // an odd size that never switches a bank: a plain ROM

        return best;
    }

    /// <summary>Heuristic: does the ROM reference the SCC window (0x9800-0x9FFF)?</summary>
    internal static bool UsesScc(byte[] rom)
    {
        for (int i = 0; i + 2 < rom.Length; i++)
        {
            byte op = rom[i];

            // LD (nnnn),A / LD A,(nnnn) / LD HL,nnnn / LD (nnnn),HL
            if (op is not (0x32 or 0x3A or 0x21 or 0x22))
                continue;

            if (rom[i + 2] is >= 0x98 and <= 0x9F)
                return true;
        }

        return false;
    }

    /// <summary>Snapshot of the bank registers (restored with <see cref="Restore"/>).</summary>
    public int[] Snapshot() => new[]
    {
        _map8[0], _map8[1], _map8[2], _map8[3], _map16[0], _map16[1], _sccBankRegister,
    };

    /// <summary>Restores a <see cref="Snapshot"/>.</summary>
    public void Restore(int[] state)
    {
        if (state is not { Length: 7 })
            return;

        for (int i = 0; i < 4; i++)
            _map8[i] = state[i];

        _map16[0] = state[4];
        _map16[1] = state[5];
        _sccBankRegister = state[6];
    }

    /// <summary>
    /// Forces the bank visible at <paramref name="address"/> (used by the scan to reach a routine that
    /// lives in a bank the game has not selected).
    /// </summary>
    public void SetBankAt(ushort address, int bank)
    {
        if (address < 0x4000 || address >= 0xC000)
            return;

        int window = (address - 0x4000) / Bank8;

        switch (Mapper)
        {
            case RomMapper.Konami:
            case RomMapper.KonamiScc:
            case RomMapper.Ascii8:
                _map8[window] = bank % _banks8;
                if (Mapper == RomMapper.KonamiScc && window == 2)
                    _sccBankRegister = bank;
                break;
            case RomMapper.Ascii16:
            case RomMapper.RType:
                _map16[address < 0x8000 ? 0 : 1] = bank % _banks16;
                break;
        }
    }

    public byte Read(ushort address)
    {
        if (address < 0x4000 || address >= 0xC000)
            return 0xFF;

        return Mapper switch
        {
            RomMapper.Ascii16 or RomMapper.RType => Read16(address),
            RomMapper.Linear => ReadLinear(address),
            _ => Read8(_map8[(address - 0x4000) / Bank8], address),
        };
    }

    /// <summary>Handles a write in the cartridge window (bank registers).</summary>
    public void Write(ushort address, byte value)
    {
        if (address < 0x4000 || address >= 0xC000)
            return;

        switch (Mapper)
        {
            case RomMapper.Konami:
                // Konami without SCC: one register per 8 KB region; 0x4000 is fixed to bank 0.
                if (address >= 0x6000 && address < 0x8000) Set8(1, value);
                else if (address >= 0x8000 && address < 0xA000) Set8(2, value);
                else if (address >= 0xA000) Set8(3, value);
                break;

            case RomMapper.KonamiScc:
                // 0x5000 -> 0x4000, 0x7000 -> 0x6000, 0x9000 -> 0x8000, 0xB000 -> 0xA000
                if (address >= 0x5000 && address < 0x5800) Set8(0, value);
                else if (address >= 0x7000 && address < 0x7800) Set8(1, value);
                else if (address >= 0x9000 && address < 0x9800)
                {
                    Set8(2, value);
                    _sccBankRegister = value;
                }
                else if (address >= 0xB000 && address < 0xB800) Set8(3, value);
                break;

            case RomMapper.Ascii8:
                if (address >= 0x6000 && address < 0x6800) Set8(0, value);
                else if (address >= 0x6800 && address < 0x7000) Set8(1, value);
                else if (address >= 0x7000 && address < 0x7800) Set8(2, value);
                else if (address >= 0x7800 && address < 0x8000) Set8(3, value);
                break;

            case RomMapper.Ascii16:
                if (address >= 0x6000 && address < 0x6800) Set16(0, value);
                else if (address >= 0x7000 && address < 0x7800) Set16(1, value);
                break;

            case RomMapper.RType:
                if (address >= 0x6000 && address < 0x8000)
                {
                    int bank = value & 0x1F;
                    if ((bank & 0x10) != 0)
                        bank &= 0x17;

                    _map16[1] = bank % _banks16;
                }

                break;
        }
    }

    private byte ReadLinear(ushort address)
    {
        int offset = address - LoadBase;
        return offset >= 0 && offset < _rom.Length ? _rom[offset] : (byte)0xFF;
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
