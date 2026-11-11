using System;
using System.Collections.Generic;
using Konamiman.Z80dotNet;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>
/// Minimal MSX1 machine used to run a cartridge's sound driver **in process** (no external
/// emulator): 64 KB map, Konami SCC mapper, 16 KB RAM, PSG and SCC register capture.
/// </summary>
/// <remarks>
/// <para>The goal is not to emulate the game, only its sound driver. The cartridge INIT and the
/// <c>H.STKE</c> hook are executed so the game installs its own interrupt handler
/// (<c>H.TIMI</c>); after that the machine stops and each frame calls <c>H.TIMI</c> directly,
/// exactly like the VDP interrupt would, while <c>CALL &lt;sound routine&gt;</c> triggers a sound.
/// Every write to the PSG/SCC is recorded with its timestamp and becomes a VGM.</para>
///
/// <para>Because no VDP/BIOS is emulated, a game whose driver depends on video state will not
/// work here — the openMSX path remains the general fallback.</para>
/// </remarks>
public sealed class MiniMsx
{
    /// <summary>PSG clock (MSX).</summary>
    public const double PsgClock = 1789772.5;

    private const int BankSize = 0x2000;
    private const int RamStart = 0xC000;
    private const int RamSize = 0x4000;

    private readonly byte[] _rom;
    private readonly byte[] _ram = new byte[RamSize];
    private readonly byte[] _bios = new byte[0x4000];
    private readonly int[] _banks = { 0, 1, 2, 3 };
    private readonly Z80Processor _cpu = new();
    private readonly List<VgmEvent> _events = new(4096);

    private int _sample;
    private byte _psgLatch;

    /// <summary>Records high-RAM writes (diagnostics).</summary>
    public bool TraceWrites { get; set; }

    /// <summary>High-RAM writes captured while <see cref="TraceWrites"/> is on.</summary>
    public List<(ushort Address, byte Value)> Trace { get; } = new();

    /// <summary>Creates the machine for a ROM image.</summary>
    public MiniMsx(byte[] rom)
    {
        _rom = rom ?? throw new ArgumentNullException(nameof(rom));
        if (rom.Length < 0x4000) throw new ArgumentException("ROM too small", nameof(rom));

        Array.Fill(_bios, (byte)0xC9); // every BIOS entry is a RET

        _cpu.Memory = new MachineMemory(this);
        _cpu.PortsSpace = new MachinePorts(this);
        _cpu.Registers.SP = unchecked((short)0xF000);
    }

    /// <summary>Program counter of the emulated CPU (diagnostics).</summary>
    public ushort Pc => _cpu.Registers.PC;

    /// <summary>
    /// Address of the cartridge's interrupt hook, or 0 when not installed.
    /// </summary>
    /// <remarks>
    /// Konami cartridges usually hook <c>H.KEYI</c> (0xFD9A) and leave <c>H.TIMI</c> as a plain
    /// <c>RET</c>; both are called by the MSX BIOS on every VDP interrupt, so either one works as
    /// the "50 Hz frame" entry point.
    /// </remarks>
    public ushort InterruptHook
    {
        get
        {
            if (IsHookInstalled(0xFD9A)) return 0xFD9A;
            if (IsHookInstalled(0xFD9F)) return 0xFD9F;
            return 0;
        }
    }

    private bool IsHookInstalled(ushort address) => Memory(address) is not (0x00 or 0xC9);

    /// <summary>
    /// Boots the cartridge: runs its INIT and the <c>H.STKE</c> hook.
    /// </summary>
    /// <param name="maxInstructions">Instruction budget for the boot.</param>
    /// <returns>True when an interrupt handler was installed.</returns>
    public bool Boot(int maxInstructions = 4_000_000)
    {
        ushort init = ReadWord(0x4002); // ROM header at 0x4000: "AB", INIT, STATEMENT, DEVICE, TEXT
        Call(init, maxInstructions);

        if (InterruptHook == 0 && Memory(0xFEDA) != 0x00)
        {
            // the cartridge INIT usually only installs H.STKE; the BIOS would then call 0xFEDA
            Call(0xFEDA, maxInstructions / 2);
        }

        return InterruptHook != 0;
    }

    /// <summary>Runs the game's interrupt handler for one 60 Hz frame.</summary>
    public void Frame(int maxInstructions = 200_000)
    {
        // MSX JIFFY counter (0xFC9E): some drivers use it as a tempo divider.
        ushort jiffy = (ushort)(Memory(0xFC9E) | (Memory(0xFC9F) << 8));
        jiffy++;
        Write(0xFC9E, (byte)(jiffy & 0xFF));
        Write(0xFC9F, (byte)(jiffy >> 8));

        ushort handler = InterruptHook;
        if (handler != 0)
            Call(handler, maxInstructions);

        _sample += 735; // 44100 / 60 (Japanese MSX VDP rate)
    }

    /// <summary>Calls the cartridge sound routine with the given id (as the debugger injection does).</summary>
    public void TriggerSound(ushort routine, int id, int maxInstructions = 200_000)
    {
        ((MainZ80Registers)_cpu.Registers).A = (byte)id;
        Call(routine, maxInstructions);
    }

    /// <summary>Clears the captured writes (start of a new song).</summary>
    public void ResetCapture()
    {
        _events.Clear();
        _sample = 0;
    }

    /// <summary>Builds a VGM from the captured register writes.</summary>
    public VgmFile BuildVgm(string name, int totalSamples = 0) =>
        VgmFile.FromEvents(_events, totalSamples > 0 ? totalSamples : _sample, PsgClock, name);

    /// <summary>Runs a routine with a return-address sentinel and an instruction budget.</summary>
    private void Call(ushort address, int maxInstructions)
    {
        var registers = (Z80Registers)_cpu.Registers;
        const ushort sentinel = 0x0001;

        registers.SP -= 2;
        WriteWord((ushort)registers.SP, sentinel);
        registers.PC = address;

        for (int i = 0; i < maxInstructions; i++)
        {
            if (registers.PC == sentinel)
                return;

            if (registers.PC == 0x0030)
            {
                InterSlotCall(registers);
                continue;
            }

            if (registers.PC == 0x0093)
            {
                // BIOS WRTPSG: A = register, E = value
                var main = (MainZ80Registers)registers;
                _events.Add(new VgmEvent(_sample, VgmEventKind.Psg, main.A, main.E, 0));
                ReturnFromBios(registers);
                continue;
            }

            if (registers.PC == 0x0096)
            {
                // BIOS RDPSG: A = register -> A = value (we do not model reads)
                ((MainZ80Registers)registers).A = 0;
                ReturnFromBios(registers);
                continue;
            }

            _cpu.ExecuteNextInstruction();
        }
    }

    /// <summary>Pops the return address (BIOS entry point emulation).</summary>
    private void ReturnFromBios(Z80Registers registers)
    {
        registers.PC = ReadWord((ushort)registers.SP);
        registers.SP += 2;
    }

    /// <summary>
    /// Emulates the MSX BIOS entry <c>RST 30h</c> (inter-slot call): the inline bytes after the
    /// instruction carry the slot and the target address. Cartridges use it to install hooks
    /// (e.g. <c>H.STKE</c>) and to call BIOS routines.
    /// </summary>
    private void InterSlotCall(Z80Registers registers)
    {
        ushort inline = ReadWord((ushort)registers.SP);
        registers.SP += 2;

        ushort target = ReadWord((ushort)(inline + 1));
        ushort returnAddress = (ushort)(inline + 3);

        registers.SP -= 2;
        WriteWord((ushort)registers.SP, returnAddress);
        registers.PC = target;
    }

    private ushort ReadWord(ushort address) => (ushort)(Memory(address) | (Memory((ushort)(address + 1)) << 8));

    private void WriteWord(ushort address, ushort value)
    {
        Write(address, (byte)(value & 0xFF));
        Write((ushort)(address + 1), (byte)(value >> 8));
    }

    /// <summary>Reads a byte from the 64 KB map.</summary>
    public byte Memory(ushort address)
    {
        int a = address;
        if (a < 0x4000) return _bios[a];

        if (a < RamStart)
        {
            if (a >= 0x9800 && a < 0x9900) return 0xFF; // SCC wave RAM is write-only here
            return _rom[(_banks[(a - 0x4000) / BankSize] * BankSize) + (a & (BankSize - 1))];
        }

        return _ram[a - RamStart];
    }

    /// <summary>Writes a byte into the 64 KB map (mapper, SCC or RAM).</summary>
    public void Write(ushort address, byte value)
    {
        int a = address;
        if (a < 0x4000) return;

        if (a >= 0x9800 && a < 0x9900)
        {
            CaptureScc(a - 0x9800, value);
            return;
        }

        if (a >= 0x4000 && a < RamStart)
        {
            if (a < 0x5800) { SetBank(0, value); return; }
            if (a >= 0x7000 && a < 0x7800) { SetBank(1, value); return; }
            if (a >= 0x9000 && a < 0x9800) { SetBank(2, value); return; }
            if (a >= 0xB000 && a < 0xB800) { SetBank(3, value); return; }
            return;
        }

        if (TraceWrites && a >= 0xF000) Trace.Add(((ushort)a, value));
        _ram[a - RamStart] = value;
    }

    private void SetBank(int slot, byte value)
    {
        int bankCount = _rom.Length / BankSize;
        _banks[slot] = value % Math.Max(1, bankCount);
    }

    /// <summary>PSG register write (port 0xA0/0xA1).</summary>
    private void WritePsgPort(ushort port, byte value)
    {
        if ((port & 0xFF) == 0xA0)
        {
            _psgLatch = value;
            return;
        }

        _events.Add(new VgmEvent(_sample, VgmEventKind.Psg, _psgLatch, value, 0));
    }

    /// <summary>Konami SCC register write (memory mapped at 0x9800-0x98FF).</summary>
    private void CaptureScc(int register, byte value)
    {
        if (register < 0x80)
            _events.Add(new VgmEvent(_sample, VgmEventKind.Scc, 0, (byte)register, value));
        else if (register < 0x8A)
            _events.Add(new VgmEvent(_sample, VgmEventKind.Scc, 1, (byte)(register - 0x80), value));
        else if (register < 0x8F)
            _events.Add(new VgmEvent(_sample, VgmEventKind.Scc, 2, (byte)(register - 0x8A), value));
        else if (register == 0x8F)
            _events.Add(new VgmEvent(_sample, VgmEventKind.Scc, 3, 0, value));
        else if (register >= 0xE0)
            _events.Add(new VgmEvent(_sample, VgmEventKind.Scc, 0, (byte)(0x60 + (register - 0xE0)), value));
    }

    /// <summary>Memory bus for the Z80 core.</summary>
    private sealed class MachineMemory : IMemory
    {
        private readonly MiniMsx _machine;

        public MachineMemory(MiniMsx machine) => _machine = machine;

        public int Size => 0x10000;

        public byte this[int address]
        {
            get => _machine.Memory((ushort)address);
            set => _machine.Write((ushort)address, value);
        }

        public void SetContents(int startAddress, byte[] contents, int startIndex = 0, int? length = null)
        {
            int count = length ?? contents.Length - startIndex;
            for (int i = 0; i < count; i++) this[startAddress + i] = contents[startIndex + i];
        }

        public byte[] GetContents(int startAddress, int length)
        {
            var result = new byte[length];
            for (int i = 0; i < length; i++) result[i] = this[startAddress + i];
            return result;
        }
    }

    /// <summary>I/O ports for the Z80 core (PSG and a few stubs).</summary>
    private sealed class MachinePorts : IMemory
    {
        private readonly MiniMsx _machine;

        public MachinePorts(MiniMsx machine) => _machine = machine;

        public int Size => 0x100;

        public byte this[int port]
        {
            get => (byte)((port & 0xFF) switch
            {
                0x99 => 0x00, // VDP status: no vblank flag pending
                _ => 0xFF,
            });
            set
            {
                switch (port & 0xFF)
                {
                    case 0xA0:
                    case 0xA1:
                        _machine.WritePsgPort((ushort)port, value);
                        break;
                }
            }
        }

        public void SetContents(int startAddress, byte[] contents, int startIndex = 0, int? length = null)
        {
        }

        public byte[] GetContents(int startAddress, int length) => new byte[length];
    }
}
