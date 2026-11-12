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

    private const int RamStart = 0xC000;
    private const int RamSize = 0x4000;

    private readonly byte[] _rom;
    private readonly byte[] _ram = new byte[RamSize];
    private readonly byte[] _bios = new byte[0x4000];
    private readonly Cartridge _cartridge;
    private readonly Z80Processor _cpu = new();
    private readonly Z80Processor _isrCpu = new();
    private readonly List<VgmEvent> _events = new(4096);

    private int _sample;
    private byte _psgLatch;
    private byte _vdpStatus;
    private byte _keyRow;
    private byte _joystick = 0xFF;   // PSG port A, active low (1 = released)
    private readonly byte[] _keys = new byte[9];   // bit set = pressed (inverted on read)

    /// <summary>Records high-RAM writes (diagnostics).</summary>
    public bool TraceWrites { get; set; }

    /// <summary>High-RAM writes captured while <see cref="TraceWrites"/> is on.</summary>
    public List<(ushort Address, byte Value)> Trace { get; } = new();

    /// <summary>Address of the timer hook (<c>H.TIMI</c>) when installed, else 0.</summary>
    public ushort TimerHook => IsHookInstalled(0xFD9F) ? (ushort)0xFD9F : (ushort)0;

    /// <summary>Address of the keyboard hook (<c>H.KEYI</c>) when installed, else 0.</summary>
    public ushort KeyHook => IsHookInstalled(0xFD9A) ? (ushort)0xFD9A : (ushort)0;

    /// <summary>Creates the machine for a ROM image.</summary>
    /// <param name="rom">Cartridge image (16/32 KB linear or a banked ROM).</param>
    /// <param name="mapper">Mapper; <see cref="RomMapper.Auto"/> picks from the ROM size.</param>
    /// <param name="scc">Force the SCC window on/off; null follows the mapper.</param>
    public MiniMsx(byte[] rom, RomMapper mapper = RomMapper.Auto, bool? scc = null)
    {
        _rom = rom ?? throw new ArgumentNullException(nameof(rom));
        if (rom.Length < 0x4000) throw new ArgumentException("ROM too small", nameof(rom));

        _cartridge = new Cartridge(rom, mapper, scc);

        Array.Fill(_bios, (byte)0xC9); // every BIOS entry is a RET

        var memory = new MachineMemory(this);
        var ports = new MachinePorts(this);

        _cpu.Memory = memory;
        _cpu.PortsSpace = ports;
        _cpu.Registers.SP = unchecked((short)0xF000);

        // The interrupt hooks run on their own CPU (shared memory/ports), so the main program keeps
        // its registers intact across a frame, like the BIOS interrupt handler does on hardware.
        _isrCpu.Memory = memory;
        _isrCpu.PortsSpace = ports;
        _isrCpu.Registers.SP = unchecked((short)0xFF00);
    }

    /// <summary>Mapper and SCC state resolved by the cartridge.</summary>
    public (RomMapper Mapper, bool Scc) CartridgeInfo => (_cartridge.Mapper, _cartridge.Scc);

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

        // The BIOS calls H.STKE after every cartridge INIT, even when the cartridge already
        // installed an interrupt hook. King's Valley 1 only starts the game from here (its H.KEYI
        // just silences the PSG), so skipping it left the driver without music.
        if (Memory(0xFEDA) is not (0x00 or 0xC9))
            Call(0xFEDA, maxInstructions);

        // If the boot left the CPU inside the program (a routine that does not return), keep running
        // it frame by frame. A returned INIT/H.STKE leaves the PC at the sentinel.
        _mainRunning = _cpu.Registers.PC >= 0x4000;

        return InterruptHook != 0;
    }

    /// <summary>Runs the game's interrupt handlers and the main program for one 60 Hz frame.</summary>
    public void Frame(int maxInstructions = 200_000)
    {
        HookBudgetExhausted = false;

        // VDP vblank: status bit 7 is latched until the status port is read, and the BIOS mirrors
        // it in STATFL (0xF3E7), which many drivers test instead of reading the port.
        _vdpStatus = 0x80;
        Write(0xF3E7, 0x80);

        // MSX JIFFY counter (0xFC9E): some drivers use it as a tempo divider.
        ushort jiffy = (ushort)(Memory(0xFC9E) | (Memory(0xFC9F) << 8));
        jiffy++;
        Write(0xFC9E, (byte)(jiffy & 0xFF));
        Write(0xFC9F, (byte)(jiffy >> 8));

        // The BIOS interrupt handler calls both hooks, H.KEYI first and then H.TIMI. A handler that
        // does not fit the per-frame budget keeps running into the next frame (hardware overrun),
        // instead of being restarted — restarting it left the game's re-entry guard set forever.
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

        _sample += 735; // 44100 / 60 (Japanese MSX VDP rate)
    }

    private bool _mainRunning;
    private bool _hookRunning;

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
    /// <remarks>
    /// A game whose handler overruns the frame budget continues it on the next frame, exactly like
    /// the hardware, where the CPU stays inside the interrupt (and the game's own re-entry flag
    /// stays set) until the handler returns. Restarting the handler mid-way instead left games such
    /// as King's Valley 1 stuck: their handler sets a guard byte it only clears on the way out.
    /// </remarks>
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

        for (int i = 0; i < maxInstructions; i++)
        {
            if (registers.PC is 0x0001 or 0x0002) // returned to a sentinel: nothing left to run
            {
                if (ReferenceEquals(cpu, _cpu))
                    _mainRunning = false;

                return;
            }

            Step(cpu);
        }
    }

    /// <summary>Calls the cartridge sound routine with the given id (as the debugger injection does).</summary>
    public void TriggerSound(ushort routine, int id, int maxInstructions = 200_000)
    {
        ((MainZ80Registers)_cpu.Registers).A = (byte)id;
        Call(routine, maxInstructions);
    }

    /// <summary>
    /// Calls a routine with registers preset. Some drivers do not take the song id in <c>A</c>:
    /// they are started by a routine that reads game state from <c>HL</c> or from memory, so a
    /// curated profile needs to set the registers (and RAM) first.
    /// </summary>
    /// <param name="routine">Address to call.</param>
    /// <param name="a">Value for <c>A</c>.</param>
    /// <param name="hl">Value for <c>HL</c>.</param>
    /// <param name="de">Value for <c>DE</c>.</param>
    /// <param name="bc">Value for <c>BC</c>.</param>
    /// <param name="maxInstructions">Instruction budget.</param>
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
        PitchWriteCount = 0;
    }

    /// <summary>
    /// Advances the capture clock without running the machine. Used when a driver is stepped directly
    /// (<see cref="CallAddress"/>) instead of through <see cref="Frame"/>.
    /// </summary>
    /// <param name="samples">Samples at 44100 Hz (one frame is 735).</param>
    public void Advance(int samples = 735) => _sample += samples;

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

            Step(_cpu);
        }
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

        PcTrace?.Invoke(registers.PC);

        if (registers.PC == 0x0030)
        {
            InterSlotCall(registers);
            return;
        }

        if (registers.PC == 0x0093)
        {
            // BIOS WRTPSG: A = register, E = value
            var main = (MainZ80Registers)registers;
            _events.Add(new VgmEvent(_sample, VgmEventKind.Psg, main.A, main.E, 0));

            if (main.A <= 5)
                PitchWriteCount++;

            ReturnFromBios(registers);
            return;
        }

        if (registers.PC == 0x0096)
        {
            // BIOS RDPSG: A = register -> A = value (joystick on port A, i.e. register 14)
            var main = (MainZ80Registers)registers;
            main.A = main.A == 14 ? _joystick : (byte)0xFF;
            ReturnFromBios(registers);
            return;
        }

        if (registers.PC == 0x0141)
        {
            // BIOS SNSMAT: A = row -> A = matrix byte (0 = pressed); row 15 is the joystick
            var main = (MainZ80Registers)registers;
            int row = main.A & 0x0F;
            main.A = row == 0x0F ? _joystick : (byte)~_keys[row];
            ReturnFromBios(registers);
            return;
        }

        cpu.ExecuteNextInstruction();
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

    /// <summary>Reads the selected keyboard row (0 = pressed bit).</summary>
    private byte ReadKeyboard() => _keyRow == 0x0F ? _joystick : _keyRow <= 8 ? (byte)~_keys[_keyRow] : (byte)0xFF;

    /// <summary>Selects a keyboard row (PPI port 0xA8).</summary>
    private void SelectKeyRow(byte value) => _keyRow = (byte)(value & 0x0F);

    /// <summary>VDP status port (0x99): bit 7 is the vblank flag, cleared when read.</summary>
    private byte ReadVdpStatus()    {
        byte value = _vdpStatus;
        _vdpStatus &= 0x7F;

        // The BIOS stores the last status in STATFL (0xF3E7); drivers test it instead of the port.
        Write(0xF3E7, value);
        return value;
    }

    /// <summary>Reads a byte from the 64 KB map.</summary>
    public byte Memory(ushort address)
    {
        int a = address;
        if (a < 0x4000) return _bios[a];

        if (a < RamStart)
        {
            if (_cartridge.IsScc(address))
                return 0xFF; // SCC registers are write-only

            return _cartridge.Read(address);
        }

        return _ram[a - RamStart];
    }

    /// <summary>Writes a byte into the 64 KB map (mapper, SCC or RAM).</summary>
    public void Write(ushort address, byte value)
    {
        int a = address;
        if (a < 0x4000) return;

        if (_cartridge.IsScc(address))
        {
            CaptureScc(a & 0xFF, value);
            return;
        }

        if (a >= 0x4000 && a < RamStart)
        {
            _cartridge.Write(address, value);
            return;
        }

        if (TraceWrites && a >= 0xF000) Trace.Add(((ushort)a, value));
        _ram[a - RamStart] = value;
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

        if (_psgLatch <= 5)
            PitchWriteCount++;
    }

    /// <summary>Konami SCC register write (memory mapped at 0x9800-0x98FF).</summary>
    private void CaptureScc(int register, byte value)
    {
        if (register < 0x80)
            _events.Add(new VgmEvent(_sample, VgmEventKind.Scc, 0, (byte)register, value));
        else if (register < 0x8A)
        {
            _events.Add(new VgmEvent(_sample, VgmEventKind.Scc, 1, (byte)(register - 0x80), value));
            PitchWriteCount++;
        }
        else if (register < 0x8F)
            _events.Add(new VgmEvent(_sample, VgmEventKind.Scc, 2, (byte)(register - 0x8A), value));
        else if (register == 0x8F)
        {
            _events.Add(new VgmEvent(_sample, VgmEventKind.Scc, 3, 0, value));
            PitchWriteCount++;
        }
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
                0x99 => _machine.ReadVdpStatus(),
                0xA9 => _machine.ReadKeyboard(),
                _ => 0xFF,
            });
            set
            {
                switch (port & 0xFF)
                {
                    case 0xA8:
                        _machine.SelectKeyRow(value);
                        break;
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
