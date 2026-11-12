using System;
using System.Collections.Generic;
using System.Linq;
using Konamiman.Z80dotNet;
using Wagenheimer.MsxAudio.Vgm;

namespace Wagenheimer.MsxAudio.Extraction;

/// <summary>How a sound routine receives the sound id.</summary>
public enum SoundIdMode
{
    /// <summary>Id in <c>A</c> (the common case).</summary>
    A,
    /// <summary>Id in <c>L</c> (and <c>A</c>), with <c>H = 0</c>.</summary>
    HL,
    /// <summary>Id in <c>E</c> (and <c>A</c>), with <c>D = 0</c>.</summary>
    DE,
    /// <summary>Id in <c>C</c> (and <c>A</c>), with <c>B = 0</c>.</summary>
    BC,
}

/// <summary>
/// Minimal MSX1 machine used to run a cartridge's sound driver **in process** (no external
/// emulator): primary slots, 64 KB RAM, the common cartridge mappers, the PPI, the PSG, the SCC and
/// the BIOS entries games actually call.
/// </summary>
/// <remarks>
/// <para>The goal is not to emulate the game, only its sound driver. The cartridge INIT and the
/// <c>H.STKE</c> hook are executed so the game installs its own interrupt handler
/// (<c>H.TIMI</c>); after that each frame calls the hooks exactly like the VDP interrupt would, while
/// <c>CALL &lt;sound routine&gt;</c> triggers a sound. Every write to the PSG/SCC is recorded with a
/// timestamp derived from the Z80 T-states and becomes a VGM.</para>
/// <para>The design follows what KSS players do (a RET-filled page 0, PSG read/write stubs, INIT then a
/// per-frame call), with a few more BIOS entries and a slot model, so more games get as far as their
/// music. <see cref="Diagnose"/> explains what is missing when nothing plays.</para>
/// </remarks>
public sealed class MiniMsx
{
    /// <summary>PSG clock (MSX).</summary>
    public const double PsgClock = 1789772.5;

    private const double CpuClock = 3579545.0;
    private const int SampleRate = 44100;

    private static readonly IReadOnlyDictionary<ushort, string> BiosNames = new Dictionary<ushort, string>
    {
        [0x0041] = "DISSCR", [0x0044] = "ENASCR", [0x0047] = "WRTVDP", [0x004A] = "RDVRM",
        [0x004D] = "WRTVRM", [0x0050] = "SETRD", [0x0053] = "SETWRT", [0x0056] = "FILVRM",
        [0x0059] = "LDIRMV", [0x005C] = "LDIRVM", [0x005F] = "CHGMOD", [0x0062] = "CHGCLR",
        [0x006C] = "INITXT", [0x006F] = "INIT32", [0x0072] = "INIGRP", [0x0075] = "INIMLT",
        [0x0090] = "GICINI", [0x009C] = "CHSNS", [0x009F] = "CHGET", [0x00A2] = "CHPUT",
        [0x00D5] = "GTSTCK", [0x00D8] = "GTTRIG", [0x00DB] = "GTPAD", [0x00DE] = "GTPDL",
        [0x0138] = "RSLREG", [0x013B] = "WSLREG", [0x013E] = "RDVDP", [0x0141] = "SNSMAT", [0x0156] = "KILBUF",
        [0x0159] = "CALBAS",
    };

    private readonly byte[] _rom;
    private readonly byte[] _ram = new byte[0x10000];
    private readonly byte[] _bios = new byte[0x4000];
    private readonly byte[] _psgRegisters = new byte[16];
    private readonly Cartridge _cartridge;
    private readonly Z80Processor _cpu = new();
    private readonly Z80Processor _isrCpu = new();
    private readonly List<VgmEvent> _events = new(4096);
    private readonly Dictionary<ushort, int> _biosHits = new();
    private readonly byte[] _keys = new byte[9];   // bit set = pressed (inverted on read)

    private int _sample;
    private long _tStates;
    private long _tStatesAtBase;
    private byte _psgLatch;
    private byte _vdpStatus;
    private byte _slotRegister;
    private byte _portC = 0x50;
    private byte _joystick = 0xFF;   // PSG port A, active low (1 = released)
    private bool _mainRunning;
    private bool _hookRunning;
    private bool _halted;

    /// <summary>Records high-RAM writes (diagnostics).</summary>
    public bool TraceWrites { get; set; }

    /// <summary>Records PSG register writes (default true). Off keeps the PSG out of the capture.</summary>
    public bool CapturePsg { get; set; } = true;

    /// <summary>Records SCC register writes (default true). Off keeps the SCC out of the capture.</summary>
    public bool CaptureScc { get; set; } = true;

    /// <summary>High-RAM writes captured while <see cref="TraceWrites"/> is on.</summary>
    public List<(ushort Address, byte Value)> Trace { get; } = new();

    /// <summary>Things worth telling the user about the boot (missing header, INIT that never returns...).</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Video refresh rate: 60 (NTSC) or 50 (PAL). It sets the tempo of the whole capture.</summary>
    public double RefreshRate
    {
        get => _refreshRate;
        set => _refreshRate = Math.Clamp(value, 25, 120);
    }

    private double _refreshRate = 60;

    /// <summary>Frames per second as an integer (50 or 60).</summary>
    public int FramesPerSecond => (int)Math.Round(_refreshRate);

    /// <summary>Capture samples (44.1 kHz) that one frame lasts.</summary>
    public int SamplesPerFrame => (int)Math.Round(SampleRate / _refreshRate);

    /// <summary>Address of the timer hook (<c>H.TIMI</c>) when installed, else 0.</summary>
    public ushort TimerHook => IsHookInstalled(0xFD9F) ? (ushort)0xFD9F : (ushort)0;

    /// <summary>Address of the keyboard hook (<c>H.KEYI</c>) when installed, else 0.</summary>
    public ushort KeyHook => IsHookInstalled(0xFD9A) ? (ushort)0xFD9A : (ushort)0;

    /// <summary>Creates the machine for a ROM image.</summary>
    /// <param name="rom">Cartridge image (16/32 KB linear or a banked ROM).</param>
    /// <param name="mapper">Mapper; <see cref="RomMapper.Auto"/> picks it from the image.</param>
    /// <param name="scc">Force the SCC window on/off; null follows the mapper.</param>
    public MiniMsx(byte[] rom, RomMapper mapper = RomMapper.Auto, bool? scc = null)
    {
        _rom = rom ?? throw new ArgumentNullException(nameof(rom));
        if (rom.Length < 0x4000) throw new ArgumentException("ROM too small", nameof(rom));

        _cartridge = new Cartridge(rom, mapper, scc);

        Array.Fill(_bios, (byte)0xC9); // every BIOS entry is a RET

        // Slots: 0 = BIOS (page 0), 1 = the cartridge, 3 = 64 KB of RAM. The BIOS leaves RAM in page 2
        // for a 16 KB cartridge and the cartridge itself otherwise; page 3 is always RAM.
        bool cartInPage2 = _cartridge.IsBanked || rom.Length > 0x4000 || _cartridge.LoadBase == 0x8000;
        int page1 = _cartridge.LoadBase == 0x8000 ? 3 : 1;
        int page2 = cartInPage2 ? 1 : 3;
        _slotRegister = (byte)(0 | (page1 << 2) | (page2 << 4) | (3 << 6));

        InitSystemArea();

        var memory = new MachineMemory(this);
        var ports = new MachinePorts(this);

        _cpu.Memory = memory;
        _cpu.PortsSpace = ports;
        _cpu.Registers.SP = unchecked((short)0xF380);

        // The interrupt hooks run on their own CPU (shared memory/ports), so the main program keeps
        // its registers intact across a frame, like the BIOS interrupt handler does on hardware.
        _isrCpu.Memory = memory;
        _isrCpu.PortsSpace = ports;
        _isrCpu.Registers.SP = unchecked((short)0xFF00);
    }

    /// <summary>Mapper and SCC state resolved by the cartridge.</summary>
    public (RomMapper Mapper, bool Scc) CartridgeInfo => (_cartridge.Mapper, _cartridge.Scc);

    /// <summary>True when the mapper switches banks.</summary>
    public bool IsBanked => _cartridge.IsBanked;

    /// <summary>Number of 8 KB banks in the image.</summary>
    public int Banks8 => _cartridge.Banks8;

    /// <summary>True when the cartridge starts with the "AB" header at its load address.</summary>
    public bool HasHeader { get; private set; }

    /// <summary>INIT address from the header (0 = none).</summary>
    public ushort InitAddress { get; private set; }

    /// <summary>Reads an I/O port (diagnostics and tests).</summary>
    public byte ReadPort(int port) => ((MachinePorts)_cpu.PortsSpace)[port & 0xFF];

    /// <summary>Writes an I/O port (diagnostics and tests).</summary>
    public void WritePort(int port, byte value) => ((MachinePorts)_cpu.PortsSpace)[port & 0xFF] = value;

    /// <summary>Program counter of the emulated CPU (diagnostics).</summary>
    public ushort Pc => _cpu.Registers.PC;

    /// <summary>Program counter of the interrupt-hook CPU (diagnostics).</summary>
    public ushort HookPc => _isrCpu.Registers.PC;

    /// <summary>True when the last <see cref="Frame"/> exhausted the budget inside an interrupt hook.</summary>
    public bool HookBudgetExhausted { get; private set; }

    /// <summary>Writes the game sent to the MSX-MUSIC (YM2413) ports, which this machine does not record.</summary>
    public int FmWrites { get; private set; }

    /// <summary>Writes the game sent to the MSX-AUDIO (Y8950) ports, which this machine does not record.</summary>
    public int Y8950Writes { get; private set; }

    /// <summary>Writes to the SCC+ register area (0xB800-0xB8FF), which this machine does not record.</summary>
    public int SccPlusWrites { get; private set; }

    /// <summary>BIOS entries the game called (address -> count) that are plain <c>RET</c>s here.</summary>
    public IReadOnlyDictionary<ushort, int> BiosCalls => _biosHits;

    /// <summary>
    /// Address of the interrupt hook that runs the driver, or 0 when none is installed.
    /// </summary>
    /// <remarks>
    /// The MSX BIOS interrupt handler calls <b>both</b> <c>H.KEYI</c> (0xFD9A) and <c>H.TIMI</c>
    /// (0xFD9F) on every VDP interrupt; <see cref="Frame"/> does the same. This property reports the
    /// timer hook when present (Konami games usually run the music there) and the keyboard hook
    /// otherwise.
    /// </remarks>
    public ushort InterruptHook => TimerHook != 0 ? TimerHook : KeyHook;

    private bool IsHookInstalled(ushort address) => _ram[address] is not (0x00 or 0xC9);

    private void InitSystemArea()
    {
        // EXPTBL/SLTTBL: no expanded slots. BOTTOM/HIMEM as a 64 KB machine leaves them.
        _ram[0xFCC1] = 0;
        _ram[0xFC48] = 0x00; _ram[0xFC49] = 0x80;
        _ram[0xFC4A] = 0x80; _ram[0xFC4B] = 0xF3;
        _ram[0xF3E8] = 0xFF; // TRGFLG: nothing pressed
        Array.Fill(_ram, (byte)0xFF, 0xFBE5, 11);
        Array.Fill(_ram, (byte)0xFF, 0xFBDA, 11);
    }

    /// <summary>
    /// Boots the cartridge: runs its INIT and the <c>H.STKE</c> hook.
    /// </summary>
    /// <param name="maxInstructions">Instruction budget for the boot.</param>
    /// <returns>True when an interrupt handler was installed.</returns>
    public bool Boot(int maxInstructions = 4_000_000)
    {
        int header = _cartridge.LoadBase;
        HasHeader = Memory((ushort)header) == (byte)'A' && Memory((ushort)(header + 1)) == (byte)'B';
        InitAddress = HasHeader ? ReadWord((ushort)(header + 2)) : (ushort)0;

        if (!HasHeader)
            Warnings.Add($"no 'AB' header at 0x{header:X4}: this does not look like a plain cartridge " +
                         "(wrong mapper, or a BASIC/disk-loaded program)");

        bool returned = true;

        if (InitAddress != 0)
            returned = Call(InitAddress, maxInstructions);
        else if (HasHeader)
            Warnings.Add("the header has no INIT (BASIC/statement ROM): only the hooks can run the game");

        // The BIOS calls H.STKE after every cartridge INIT, even when the cartridge already
        // installed an interrupt hook. King's Valley 1 only starts the game from here (its H.KEYI
        // just silences the PSG), so skipping it left the driver without music. It is also called
        // when INIT used up its budget: those games (King's Valley II) run their driver from the hook.
        if (IsHookInstalled(0xFEDA))
            Call(0xFEDA, maxInstructions);

        // INIT that never returns and installed nothing to call is a game running its own main loop:
        // keep stepping it per frame.
        _mainRunning = _cpu.Registers.PC >= 0x4000;

        return InterruptHook != 0;
    }

    /// <summary>Runs the game's interrupt handlers and the main program for one frame.</summary>
    public void Frame(int maxInstructions = 200_000)
    {
        HookBudgetExhausted = false;
        _tStatesAtBase = _tStates;

        // VDP vblank: status bit 7 is latched until the status port is read, and the BIOS mirrors
        // it in STATFL (0xF3E7), which many drivers test instead of reading the port.
        _vdpStatus = 0x80;
        _ram[0xF3E7] = 0x80;

        // MSX JIFFY counter (0xFC9E): some drivers use it as a tempo divider.
        ushort jiffy = (ushort)(_ram[0xFC9E] | (_ram[0xFC9F] << 8));
        jiffy++;
        _ram[0xFC9E] = (byte)(jiffy & 0xFF);
        _ram[0xFC9F] = (byte)(jiffy >> 8);

        RefreshBiosKeyState();

        // The BIOS interrupt handler calls both hooks, H.KEYI first and then H.TIMI. A handler that
        // does not fit the per-frame budget keeps running into the next frame (hardware overrun),
        // instead of being restarted: restarting it left the game's re-entry guard set forever.
        int hookBudget = Math.Max(2000, maxInstructions / 3);
        if (_hookRunning)
        {
            ResumeHook(hookBudget);
        }
        else
        {
            CallHook(KeyHook, hookBudget);
            if (!_hookRunning)
                CallHook(TimerHook, hookBudget);
        }

        // Games whose music starts in the main program (not in a hook) need it to keep running.
        if (_mainRunning)
            Run(_cpu, Math.Max(2000, maxInstructions - hookBudget * 2));

        _sample += SamplesPerFrame;
    }

    /// <summary>Mirrors the key matrix into the BIOS work area, like the BIOS keyboard scan does.</summary>
    private void RefreshBiosKeyState()
    {
        const int oldKey = 0xFBDA;
        const int newKey = 0xFBE5;

        Array.Copy(_ram, newKey, _ram, oldKey, 11);

        for (int row = 0; row < 9; row++)
            _ram[newKey + row] = (byte)~_keys[row];

        // TRGFLG (0xF3E8): bit 0 space, bit 4 trigger A, bit 5 trigger B; 0 = pressed.
        byte trigger = 0xFF;
        if ((_keys[8] & 0x01) != 0) trigger &= 0xFE;
        if ((_joystick & 0x10) == 0) trigger &= 0xEF;
        if ((_joystick & 0x20) == 0) trigger &= 0xDF;
        _ram[0xF3E8] = trigger;
    }

    /// <summary>Return address (sentinel) used by the interrupt-hook CPU.</summary>
    private const ushort HookSentinel = 0x0002;

    private void CallHook(ushort address, int maxInstructions)
    {
        if (address == 0 || !IsHookInstalled(address))
            return;

        var registers = (Z80Registers)_isrCpu.Registers;

        registers.SP = unchecked((short)0xFF00);
        registers.SP -= 2;
        WriteWord((ushort)registers.SP, HookSentinel);
        registers.PC = address;

        _hookRunning = true;
        ResumeHook(maxInstructions);
    }

    /// <summary>
    /// Runs (or continues) the interrupt handler for up to <paramref name="maxInstructions"/>.
    /// </summary>
    private void ResumeHook(int maxInstructions)
    {
        var registers = (Z80Registers)_isrCpu.Registers;

        for (int i = 0; i < maxInstructions; i++)
        {
            if (registers.PC == HookSentinel)
            {
                _hookRunning = false;
                return;
            }

            Step(_isrCpu);
        }

        HookBudgetExhausted = true;
    }

    /// <summary>
    /// Runs the interrupt hook for diagnostics and reports where it spent its time (or whether it
    /// returned). Useful to find a driver that waits on hardware the minimal machine does not model.
    /// </summary>
    /// <param name="maxInstructions">Instruction budget.</param>
    /// <param name="top">How many hot addresses to report.</param>
    public (bool Returned, ushort Pc, IReadOnlyList<(ushort Address, int Hits)> Hot)
        ProfileHook(int maxInstructions = 200_000, int top = 8)
    {
        ushort hook = KeyHook != 0 ? KeyHook : TimerHook;
        if (hook == 0)
            return (true, 0, Array.Empty<(ushort, int)>());

        var registers = (Z80Registers)_isrCpu.Registers;
        _hookRunning = false;

        registers.SP = unchecked((short)0xFF00);
        registers.SP -= 2;
        WriteWord((ushort)registers.SP, HookSentinel);
        registers.PC = hook;

        var hits = new Dictionary<ushort, int>();
        bool returned = false;

        for (int i = 0; i < maxInstructions; i++)
        {
            if (registers.PC == HookSentinel)
            {
                returned = true;
                break;
            }

            if ((i & 7) == 0)
            {
                ushort pc = registers.PC;
                hits[pc] = hits.TryGetValue(pc, out int n) ? n + 1 : 1;
            }

            Step(_isrCpu);
        }

        var hottest = new List<(ushort Address, int Hits)>(hits.Count);
        foreach (var pair in hits)
            hottest.Add((pair.Key, pair.Value));

        hottest.Sort((a, b) => b.Hits.CompareTo(a.Hits));

        return (returned, registers.PC, hottest.GetRange(0, Math.Min(top, hottest.Count)));
    }

    /// <summary>Executes the main program for up to <paramref name="maxInstructions"/> instructions.</summary>
    private void Run(Z80Processor cpu, int maxInstructions)
    {
        var registers = (Z80Registers)cpu.Registers;
        _halted = false;

        for (int i = 0; i < maxInstructions; i++)
        {
            if (registers.PC is 0x0001 or 0x0002) // returned to a sentinel: nothing left to run
            {
                if (ReferenceEquals(cpu, _cpu))
                    _mainRunning = false;

                return;
            }

            Step(cpu);

            if (_halted)
            {
                // HALT waits for the next vblank: the rest of this frame's slice is idle time.
                _halted = false;
                return;
            }
        }
    }

    /// <summary>Calls the cartridge sound routine with the given id in <c>A</c>.</summary>
    public void TriggerSound(ushort routine, int id, int maxInstructions = 200_000) =>
        TriggerSound(routine, id, SoundIdMode.A, maxInstructions);

    /// <summary>Calls the sound routine with the id passed the way the driver expects it.</summary>
    public void TriggerSound(ushort routine, int id, SoundIdMode mode, int maxInstructions = 200_000)
    {
        byte value = (byte)id;

        switch (mode)
        {
            case SoundIdMode.HL:
                CallRoutine(routine, value, hl: value, maxInstructions: maxInstructions);
                break;
            case SoundIdMode.DE:
                CallRoutine(routine, value, de: value, maxInstructions: maxInstructions);
                break;
            case SoundIdMode.BC:
                CallRoutine(routine, value, bc: value, maxInstructions: maxInstructions);
                break;
            default:
                CallRoutine(routine, value, maxInstructions: maxInstructions);
                break;
        }
    }

    /// <summary>
    /// Calls a routine with registers preset. Some drivers do not take the song id in <c>A</c>:
    /// they are started by a routine that reads game state from <c>HL</c> or from memory, so a
    /// curated profile needs to set the registers (and RAM) first.
    /// </summary>
    public void CallRoutine(ushort routine, byte a = 0, ushort hl = 0, ushort de = 0, ushort bc = 0,
        int maxInstructions = 200_000)
    {
        var main = (MainZ80Registers)_cpu.Registers;
        main.A = a;
        main.HL = unchecked((short)hl);
        main.DE = unchecked((short)de);
        main.BC = unchecked((short)bc);
        Call(routine, maxInstructions);
    }

    /// <summary>Presses or releases a key in the matrix (row 0..8, mask bit 0..7; row 0x0F = joystick).</summary>
    public void PressKey(int row, int mask, bool pressed)
    {
        if (row == 0x0F)
        {
            // PSG port A / joystick: active low, bit 4 = trigger A, bit 5 = trigger B.
            if (pressed)
                _joystick &= (byte)~(1 << mask);
            else
                _joystick |= (byte)(1 << mask);

            return;
        }

        if (row is < 0 or > 8)
            return;

        if (pressed)
            _keys[row] |= (byte)(1 << mask);
        else
            _keys[row] &= (byte)~(1 << mask);
    }

    /// <summary>Releases every key and joystick button (between scripted presses).</summary>
    public void ReleaseAllKeys()
    {
        Array.Clear(_keys);
        _joystick = 0xFF;
    }

    /// <summary>Clears the captured writes (start of a new song).</summary>
    public void ResetCapture()
    {
        _events.Clear();
        _sample = 0;
        _tStatesAtBase = _tStates;
        PitchWriteCount = 0;
    }

    /// <summary>
    /// Advances the capture clock without running the machine. Used when a driver is stepped directly
    /// (<see cref="CallAddress"/>) instead of through <see cref="Frame"/>.
    /// </summary>
    /// <param name="samples">Samples at 44100 Hz (default: one frame).</param>
    public void Advance(int samples = -1)
    {
        _sample += samples < 0 ? SamplesPerFrame : samples;
        _tStatesAtBase = _tStates;
    }

    /// <summary>Builds a VGM from the captured register writes.</summary>
    public VgmFile BuildVgm(string name, int totalSamples = 0) =>
        VgmFile.FromEvents(_events, totalSamples > 0 ? totalSamples : _sample, PsgClock, name);

    /// <summary>Number of register writes captured since <see cref="ResetCapture"/>.</summary>
    public int EventCount => _events.Count;

    /// <summary>Captured writes (diagnostics: lets a caller inspect what the driver wrote).</summary>
    public IReadOnlyList<VgmEvent> CapturedEvents => _events;

    /// <summary>
    /// Pitch writes captured since <see cref="ResetCapture"/> (PSG tone period, SCC frequency and
    /// key-on). Cheap "is anything playing?" test for the id scan, without building a VGM.
    /// </summary>
    public int PitchWriteCount { get; private set; }

    /// <summary>Calls an address directly (diagnostics; the game's interrupt model is bypassed).</summary>
    public void CallAddress(ushort address, int maxInstructions = 50_000) => Call(address, maxInstructions);

    /// <summary>Snapshot of the cartridge bank registers (see <see cref="RestoreBanks"/>).</summary>
    public int[] SaveBanks() => _cartridge.Snapshot();

    /// <summary>Restores bank registers saved with <see cref="SaveBanks"/>.</summary>
    public void RestoreBanks(int[] state) => _cartridge.Restore(state);

    /// <summary>Forces the bank visible at <paramref name="address"/> (no effect on unbanked cartridges).</summary>
    public void SetBank(ushort address, int bank) => _cartridge.SetBankAt(address, bank);

    /// <summary>Runs a routine with a return-address sentinel and an instruction budget. True if it returned.</summary>
    private bool Call(ushort address, int maxInstructions)
    {
        var registers = (Z80Registers)_cpu.Registers;
        const ushort sentinel = 0x0001;

        registers.SP -= 2;
        WriteWord((ushort)registers.SP, sentinel);
        registers.PC = address;

        for (int i = 0; i < maxInstructions; i++)
        {
            if (registers.PC == sentinel)
                return true;

            Step(_cpu);
        }

        return false;
    }

    /// <summary>
    /// Called with the program counter before every instruction (diagnostics). Set from a probe to
    /// follow a driver's flow without a debugger.
    /// </summary>
    public Action<ushort>? PcTrace { get; set; }

    /// <summary>One instruction (or one emulated BIOS entry) on <paramref name="cpu"/>.</summary>
    private void Step(Z80Processor cpu)
    {
        var registers = (Z80Registers)cpu.Registers;
        ushort pc = registers.PC;

        PcTrace?.Invoke(pc);

        if (pc < 0x4000 && SlotOf(0) == 0 && TryBios(registers, pc))
            return;

        if (Memory(pc) == 0x76) // HALT: wait for the next interrupt, which the frame loop provides
        {
            registers.PC = (ushort)(pc + 1);
            if (ReferenceEquals(cpu, _cpu))
                _halted = true;

            return;
        }

        _tStates += cpu.ExecuteNextInstruction();
    }

    /// <summary>
    /// Emulates the BIOS entries games call. Anything else in page 0 is a <c>RET</c> (counted, so the
    /// diagnosis can say which BIOS service a game wanted).
    /// </summary>
    private bool TryBios(Z80Registers registers, ushort pc)
    {
        var main = (MainZ80Registers)registers;

        switch (pc)
        {
            case 0x000C: // RDSLT: A = slot, HL = address -> A
                main.A = ReadSlot(main.A & 3, (ushort)main.HL);
                ReturnFromBios(registers);
                return true;

            case 0x0014: // WRSLT: A = slot, HL = address, E = value
                WriteSlot(main.A & 3, (ushort)main.HL, main.E);
                ReturnFromBios(registers);
                return true;

            case 0x001C: // CALSLT: IX = address (the slot only matters for page switching, which we skip)
                registers.PC = (ushort)registers.IX;
                return true;

            case 0x0024: // ENASLT: A = slot, H = address (page in bits 7-6)
            {
                int page = (main.H >> 6) & 3;
                int slot = main.A & 3;
                _slotRegister = (byte)((_slotRegister & ~(3 << (page * 2))) | (slot << (page * 2)));
                ReturnFromBios(registers);
                return true;
            }

            case 0x0030:
                InterSlotCall(registers);
                return true;

            case 0x0090: // GICINI: silence the PSG, mixer = tones on
                for (int reg = 0; reg <= 6; reg++) PsgWrite(reg, 0);
                PsgWrite(7, 0xB8);
                for (int reg = 8; reg <= 10; reg++) PsgWrite(reg, 0);
                ReturnFromBios(registers);
                return true;

            case 0x0093: // WRTPSG: A = register, E = value
                PsgWrite(main.A, main.E);
                ReturnFromBios(registers);
                return true;

            case 0x0096: // RDPSG: A = register -> A
                main.A = ReadPsgRegister(main.A);
                ReturnFromBios(registers);
                return true;

            case 0x00D5: // GTSTCK: A = 0 cursor keys, 1/2 joystick ports -> A = direction 0..8
                main.A = main.A switch
                {
                    0 => CursorDirection(),
                    1 => JoystickDirection(),
                    _ => 0,
                };
                ReturnFromBios(registers);
                return true;

            case 0x00D8: // GTTRIG: A = 0 space, 1/3 joystick-1 trigger A/B -> A = 0xFF pressed
                main.A = main.A switch
                {
                    0 => (byte)((_keys[8] & 0x01) != 0 ? 0xFF : 0),
                    1 => (byte)((_joystick & 0x10) == 0 ? 0xFF : 0),
                    3 => (byte)((_joystick & 0x20) == 0 ? 0xFF : 0),
                    _ => 0,
                };
                ReturnFromBios(registers);
                return true;

            case 0x013E: // RDVDP: A = VDP status (and the vblank flag is consumed, like a real read)
                main.A = ReadVdpStatus();
                ReturnFromBios(registers);
                return true;

            case 0x0138: // RSLREG
                main.A = _slotRegister;
                ReturnFromBios(registers);
                return true;

            case 0x013B: // WSLREG
                _slotRegister = main.A;
                ReturnFromBios(registers);
                return true;

            case 0x0141: // SNSMAT: A = row -> A = matrix byte (0 = pressed); row 15 is the joystick
            {
                int row = main.A & 0x0F;
                main.A = row == 0x0F ? _joystick : row <= 8 ? (byte)~_keys[row] : (byte)0xFF;
                ReturnFromBios(registers);
                return true;
            }

            default:
                if (pc >= 0x0004)
                    _biosHits[pc] = _biosHits.TryGetValue(pc, out int n) ? n + 1 : 1;

                return false;
        }
    }

    private byte CursorDirection()
    {
        byte row = _keys[8];
        bool left = (row & 0x10) != 0, up = (row & 0x20) != 0, down = (row & 0x40) != 0, right = (row & 0x80) != 0;
        return Direction(up, down, left, right);
    }

    private byte JoystickDirection() =>
        Direction((_joystick & 1) == 0, (_joystick & 2) == 0, (_joystick & 4) == 0, (_joystick & 8) == 0);

    private static byte Direction(bool up, bool down, bool left, bool right)
    {
        if (up && right) return 2;
        if (down && right) return 4;
        if (down && left) return 6;
        if (up && left) return 8;
        if (up) return 1;
        if (right) return 3;
        if (down) return 5;
        if (left) return 7;
        return 0;
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

    /// <summary>Primary slot currently selected for the page that holds <paramref name="address"/>.</summary>
    private int SlotOf(int address) => (_slotRegister >> ((address >> 14) * 2)) & 3;

    private byte ReadSlot(int slot, ushort address)
    {
        int a = address;

        switch (slot)
        {
            case 0:
                return a < 0x4000 ? _bios[a] : (byte)0xFF;

            case 1:
                if (a < 0x4000 || a >= 0xC000 || _cartridge.IsScc(address))
                    return 0xFF; // SCC registers are write-only

                return _cartridge.Read(address);

            case 3:
                return _ram[a];

            default:
                return 0xFF;
        }
    }

    private void WriteSlot(int slot, ushort address, byte value)
    {
        int a = address;

        if (slot == 3)
        {
            if (TraceWrites && a >= 0xF000) Trace.Add(((ushort)a, value));
            _ram[a] = value;
            return;
        }

        if (slot != 1 || a < 0x4000 || a >= 0xC000)
            return;

        if (_cartridge.IsScc(address))
        {
            CaptureSccRegister(a & 0xFF, value);
            return;
        }

        if (a >= 0xB800 && a < 0xB900 && _cartridge.Mapper == RomMapper.KonamiScc)
            SccPlusWrites++;

        _cartridge.Write(address, value);
    }

    /// <summary>Reads a byte from the 64 KB map as the CPU sees it.</summary>
    public byte Memory(ushort address) => ReadSlot(SlotOf(address), address);

    /// <summary>Writes a byte into the 64 KB map as the CPU sees it (mapper, SCC or RAM).</summary>
    public void Write(ushort address, byte value) => WriteSlot(SlotOf(address), address, value);

    /// <summary>Capture timestamp (44.1 kHz) of "now": the frame start plus the T-states already spent in it.</summary>
    private int Stamp()
    {
        double offset = (_tStates - _tStatesAtBase) * SampleRate / CpuClock;
        return _sample + Math.Clamp((int)offset, 0, SamplesPerFrame - 1);
    }

    /// <summary>PSG register write (BIOS WRTPSG or ports 0xA0/0xA1).</summary>
    private void PsgWrite(int register, byte value)
    {
        register &= 0x0F;
        _psgRegisters[register] = value;

        // Registers 14/15 are the joystick ports: not audio, never recorded.
        if (!CapturePsg || register > 13)
            return;

        _events.Add(new VgmEvent(Stamp(), VgmEventKind.Psg, (byte)register, value, 0));

        if (register <= 5)
            PitchWriteCount++;
    }

    private byte ReadPsgRegister(int register)
    {
        register &= 0x0F;

        // Port A (register 14) is the joystick; register 15 bit 6 selects which port is wired in.
        if (register == 14)
            return (_psgRegisters[15] & 0x40) == 0 ? _joystick : (byte)0xFF;

        return register == 15 ? _psgRegisters[15] : _psgRegisters[register];
    }

    /// <summary>Konami SCC register write (memory mapped at 0x9800-0x98FF).</summary>
    private void CaptureSccRegister(int register, byte value)
    {
        if (!CaptureScc)
            return;

        int sample = Stamp();

        if (register < 0x80)
            _events.Add(new VgmEvent(sample, VgmEventKind.Scc, 0, (byte)register, value));
        else if (register < 0x8A)
        {
            _events.Add(new VgmEvent(sample, VgmEventKind.Scc, 1, (byte)(register - 0x80), value));
            PitchWriteCount++;
        }
        else if (register < 0x8F)
            _events.Add(new VgmEvent(sample, VgmEventKind.Scc, 2, (byte)(register - 0x8A), value));
        else if (register == 0x8F)
        {
            _events.Add(new VgmEvent(sample, VgmEventKind.Scc, 3, 0, value));
            PitchWriteCount++;
        }
        else if (register >= 0xE0)
            _events.Add(new VgmEvent(sample, VgmEventKind.Scc, 0, (byte)(0x60 + (register - 0xE0)), value));
    }

    /// <summary>Keyboard column read (PPI port B): the row chosen in port C, 0 = pressed.</summary>
    private byte ReadKeyboard()
    {
        int row = _portC & 0x0F;
        return row == 0x0F ? _joystick : row <= 8 ? (byte)~_keys[row] : (byte)0xFF;
    }

    /// <summary>VDP status port (0x99): bit 7 is the vblank flag, cleared when read.</summary>
    private byte ReadVdpStatus()
    {
        byte value = _vdpStatus;
        _vdpStatus &= 0x7F;

        // The BIOS stores the last status in STATFL (0xF3E7); drivers test it instead of the port.
        _ram[0xF3E7] = value;
        return value;
    }

    /// <summary>
    /// A human-readable list of what stopped the game from getting further, or from sounding right:
    /// unsupported sound chips, BIOS services that are stubs here, a missing header.
    /// </summary>
    public IReadOnlyList<string> Diagnose()
    {
        var lines = new List<string>(Warnings);

        if (FmWrites > 0)
            lines.Add($"the game writes to MSX-MUSIC (YM2413): {FmWrites} writes that are NOT recorded");

        if (Y8950Writes > 0)
            lines.Add($"the game writes to MSX-AUDIO (Y8950): {Y8950Writes} writes that are NOT recorded");

        if (SccPlusWrites > 200)
            lines.Add($"the game writes to the SCC+ registers: {SccPlusWrites} writes that are NOT recorded");

        foreach (var hit in _biosHits.OrderByDescending(p => p.Value).Take(6))
        {
            string name = BiosNames.TryGetValue(hit.Key, out string? known) ? known : "?";
            if (hit.Key is 0x0041 or 0x0044 or 0x0047 or 0x004A or 0x004D or 0x0050 or 0x0053 or 0x0056 or
                0x0059 or 0x005C or 0x005F or 0x0062 or 0x006C or 0x006F or 0x0072 or 0x0075 or 0x0156)
                continue; // video/keyboard-buffer services: irrelevant to the sound

            lines.Add($"BIOS {hit.Key:X4} {name} was called {hit.Value}x and is a plain RET here");
        }

        return lines;
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

    /// <summary>I/O ports for the Z80 core: PPI, PSG, VDP status and the chips we only count.</summary>
    private sealed class MachinePorts : IMemory
    {
        private readonly MiniMsx _machine;

        public MachinePorts(MiniMsx machine) => _machine = machine;

        public int Size => 0x100;

        public byte this[int port]
        {
            get => (byte)((port & 0xFF) switch
            {
                0x99 => _machine.ReadVdpStatus(),
                0xA2 => _machine.ReadPsgRegister(_machine._psgLatch),
                0xA8 => _machine._slotRegister,
                0xA9 => _machine.ReadKeyboard(),
                0xAA => _machine._portC,
                _ => 0xFF,
            });
            set
            {
                switch (port & 0xFF)
                {
                    case 0xA0:
                        _machine._psgLatch = value;
                        break;
                    case 0xA1:
                        _machine.PsgWrite(_machine._psgLatch, value);
                        break;
                    case 0xA8:
                        _machine._slotRegister = value;
                        break;
                    case 0xAA:
                        _machine._portC = value;
                        break;
                    case 0xAB: // PPI control: bit 7 = 0 sets/resets one bit of port C
                        if ((value & 0x80) == 0)
                        {
                            int bit = (value >> 1) & 7;
                            _machine._portC = (byte)((value & 1) != 0
                                ? _machine._portC | (1 << bit)
                                : _machine._portC & ~(1 << bit));
                        }

                        break;
                    case 0x7C:
                    case 0x7D:
                        _machine.FmWrites++;
                        break;
                    case 0xC0:
                    case 0xC1:
                        _machine.Y8950Writes++;
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
