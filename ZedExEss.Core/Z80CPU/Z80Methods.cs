using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Debugging;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;

namespace ZedExEss.Z80CPU
{
    // Instruction execution and bus timing live together in this partial because
    // every memory or port access must advance the rest of the machine at the exact
    // point where the Z80 performs that bus cycle.
    public partial class Z80Core<TMemory, TPorts>(TMemory memory, TPorts ports)
        where TMemory : class, IZ80MemoryBus
        where TPorts : class, IZ80PortBus
    {
        private int _remainingCycles;
        private SpectrumEmulator? _tstateConsumer;
        private bool _batchInstructionTstates;
        private int _batchedInstructionTstates;
        private ulong _batchedSyncDeadline = ulong.MaxValue;
        private IContendedPageProvider? _noMreqContendedPages;
        private IContentionProfile? _noMreqContentionProfile;
        private bool _hasTstateConsumer;
        private bool _hasNoMreqContention;
        private bool _hasIoContentionBeforeCycle;
        private bool _ioWritesLatchAtEndOfCycle;
        private IZ80DebugHook? _debugHook;
        private bool _hasDebugHook;
        private IZ80TapeAccelerationHook? _tapeAccelerationHook;
        private bool _hasTapeAccelerationHook;
        private static readonly bool[] EvenParityTable = BuildEvenParityTable();
        private static readonly byte[] SzpxyFlagsTable = BuildSzpxyFlagsTable();

        public void ConfigureTstateConsumer(SpectrumEmulator? consumer)
        {
            FlushBatchedInstructionTstates();
            _tstateConsumer = consumer;
            _hasTstateConsumer = consumer != null;
            RefreshBatchedSyncDeadline();
        }

        /// <summary>
        /// Coalesces ordinary cycle-consumer callbacks across instructions until a
        /// CPU-visible timing deadline. I/O sample/latch points still force an exact
        /// synchronisation.
        /// </summary>
        /// <remarks>
        /// CPU.Cyc advances at every original cycle point, so contention calculations
        /// and pending-write timestamps are unchanged. This only reduces scheduler
        /// callback overhead while the dedicated fast tape runner owns execution.
        /// </remarks>
        internal void ConfigureInstructionTstateBatching(bool enabled)
        {
            if (_batchInstructionTstates == enabled)
            {
                return;
            }

            FlushBatchedInstructionTstates();
            _batchInstructionTstates = enabled;
            RefreshBatchedSyncDeadline();
        }

        public void ConfigureNoMreqContention(IContendedPageProvider? contendedPages, IContentionProfile? contention)
        {
            _noMreqContendedPages = contendedPages;
            _noMreqContentionProfile = contention;
            _hasNoMreqContention = contendedPages != null && contention != null;
        }

        public void ConfigureIoContention(bool enabled, bool writesLatchAtEndOfCycle = false)
        {
            _hasIoContentionBeforeCycle = enabled;
            _ioWritesLatchAtEndOfCycle = writesLatchAtEndOfCycle;
        }

        public void ConfigureDebugHook(IZ80DebugHook? debugHook)
        {
            _debugHook = debugHook;
            _hasDebugHook = debugHook != null;
        }

        public void ConfigureTapeAccelerationHook(IZ80TapeAccelerationHook? hook)
        {
            _tapeAccelerationHook = hook;
            _hasTapeAccelerationHook = hook != null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte ReadByte(ushort addr)
        {
            byte value = _memory.Read(addr);
            if (_hasDebugHook && _debugHook!.AccessWatchpointsEnabled)
            {
                _debugHook.OnMemoryRead(addr, value);
            }

            ConsumeCycles(3);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void WriteByte(ushort addr, byte val)
        {
            _memory.WriteCpu(addr, val);
            if (_hasDebugHook && _debugHook!.AccessWatchpointsEnabled)
            {
                _debugHook.OnMemoryWrite(addr, val);
            }

            ConsumeCycles(3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ushort ReadWord(ushort addr)
        {
            byte lo = ReadByte(addr);
            byte hi = ReadByte((ushort)(addr + 1));
            return (ushort)(lo | (hi << 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void WriteWord(ushort addr, ushort val)
        {
            WriteByte(addr, (byte)(val & 0xFF));
            WriteByte((ushort)(addr + 1), (byte)(val >> 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void PushWord(ushort val)
        {
            SP--;
            WriteByte(SP, (byte)(val >> 8));
            SP--;
            WriteByte(SP, (byte)(val & 0xFF));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ushort PopWord()
        {
            SP += 2;
            return ReadWord((ushort)(SP - 2));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte NextByte()
        {
            byte value = ReadByte(PC);
            PC++;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ushort NextWord()
        {
            ushort loAddr = PC;
            byte lo = ReadByte(loAddr);
            PC++;
            byte hi = ReadByte(PC);
            PC++;
            return (ushort)(lo | (hi << 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte FetchOpcode()
        {
            byte value = _memory.FetchOpcode(PC);
            ConsumeCycles(4);
            PC++;
            IncR();
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ReadPortByte(ushort port)
        {
            if (_hasIoContentionBeforeCycle)
            {
                // Z80 input data is sampled during T4. Advance/apply contention through T3,
                // sample the bus, then account for the final T-state.
                ConsumeIoCycle(port, 0);
                ConsumeIoCycle(port, 1);
                ConsumeIoCycle(port, 2);
                _ports.ApplyIoContentionBeforeCycle(port, 3);
                FlushBatchedInstructionTstates();
                byte timedValue = _ports.ReadUncontended(port);
                if (_hasDebugHook && _debugHook!.AccessWatchpointsEnabled)
                {
                    _debugHook.OnPortRead(port, timedValue);
                }

                ConsumeCycles(1);
                return timedValue;
            }

            FlushBatchedInstructionTstates();
            byte value = _ports.ReadUncontended(port);
            if (_hasDebugHook && _debugHook!.AccessWatchpointsEnabled)
            {
                _debugHook.OnPortRead(port, value);
            }

            ConsumeCycles(4);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WritePortByte(ushort port, byte value)
        {
            if (_ioWritesLatchAtEndOfCycle)
            {
                // Pentagon clones expose the port latch at the trailing edge of the
                // I/O cycle. Consume the same four T-states before applying the write.
                if (_hasIoContentionBeforeCycle)
                {
                    ConsumeIoCycle(port, 0);
                    ConsumeIoCycle(port, 1);
                    ConsumeIoCycle(port, 2);
                    ConsumeIoCycle(port, 3);
                }
                else
                {
                    ConsumeCycles(4);
                }

                FlushBatchedInstructionTstates();
                _ports.WriteUncontended(port, value);
                if (_hasDebugHook && _debugHook!.AccessWatchpointsEnabled)
                {
                    _debugHook.OnPortWrite(port, value);
                }

                return;
            }

            if (_hasIoContentionBeforeCycle)
            {
                ConsumeIoCycle(port, 0);
                FlushBatchedInstructionTstates();
                _ports.WriteUncontended(port, value);
                if (_hasDebugHook && _debugHook!.AccessWatchpointsEnabled)
                {
                    _debugHook.OnPortWrite(port, value);
                }

                ConsumeIoCycle(port, 1);
                ConsumeIoCycle(port, 2);
                ConsumeIoCycle(port, 3);
                return;
            }

            FlushBatchedInstructionTstates();
            _ports.WriteUncontended(port, value);
            if (_hasDebugHook && _debugHook!.AccessWatchpointsEnabled)
            {
                _debugHook.OnPortWrite(port, value);
            }

            ConsumeCycles(4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ConsumeIoCycle(ushort port, int phase)
        {
            _ports.ApplyIoContentionBeforeCycle(port, phase);
            ConsumeCycles(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BeginInstruction(int totalCycles, int consumedCycles)
        {
            // Opcode fetches and explicit bus accesses consume their own cycles.
            // Keep only the unassigned portion; EndInstruction emits it as internal
            // no-MREQ cycles so contention can still be applied to the right address.
            int remaining = totalCycles - consumedCycles;
            _remainingCycles = remaining > 0 ? remaining : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InternalCycle(int cycles)
        {
            InternalCycle(GetIrAddress(), cycles);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InternalCycle(ushort address, int cycles)
        {
            if (cycles <= 0)
            {
                return;
            }

            if (!_hasNoMreqContention)
            {
                ConsumeCycles(cycles);
                return;
            }

            if (!_noMreqContendedPages!.IsContendedPage(address >> 14))
            {
                ConsumeCycles(cycles);
                return;
            }

            for (int i = 0; i < cycles; i++)
            {
                int delay = _noMreqContentionProfile!.GetNoMreqDelay(Cyc);
                if (delay > 0)
                {
                    AddWaitStates(delay);
                }

                ConsumeCycles(1);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EndInstruction()
        {
            if (_remainingCycles > 0)
            {
                InternalCycle(_remainingCycles);
            }

            _remainingCycles = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ushort GetIrAddress()
        {
            return (ushort)((I << 8) | R);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ConsumeCycles(int cycles)
        {
            if (cycles <= 0)
            {
                return;
            }

            Cyc += (ulong)cycles;
            _remainingCycles -= cycles;
            if (_remainingCycles < 0)
            {
                _remainingCycles = 0;
            }

            if (_hasTstateConsumer)
            {
                if (_batchInstructionTstates)
                {
                    _batchedInstructionTstates += cycles;
                }
                else
                {
                    _tstateConsumer!.OnTstatesConsumed(cycles);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AddWaitStates(int cycles)
        {
            if (cycles <= 0)
            {
                return;
            }

            Cyc += (ulong)cycles;
            if (_hasTstateConsumer)
            {
                if (_batchInstructionTstates)
                {
                    _batchedInstructionTstates += cycles;
                }
                else
                {
                    _tstateConsumer!.OnTstatesConsumed(cycles);
                }
            }
        }
        internal void AdvanceTapeLoadSkipTime(int cycles)
        {
            if (cycles <= 0)
            {
                return;
            }

            // Preserve ordering if the detector is entered after part of an
            // instruction has accumulated in the fast tape execution path.
            FlushBatchedInstructionTstates();

            // A tape accelerator may skip an idle polling interval, but all devices
            // must still observe the elapsed emulated time. This path intentionally
            // avoids pretending that the CPU executed ordinary internal cycles.
            Cyc += (ulong)cycles;
            if (_hasTstateConsumer)
            {
                _tstateConsumer!.OnTapeLoadTstatesSkipped(cycles);
                RefreshBatchedSyncDeadline();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FlushBatchedInstructionTstatesAtBoundary()
        {
            if (_batchInstructionTstates && Cyc >= _batchedSyncDeadline)
            {
                FlushBatchedInstructionTstates();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FlushBatchedInstructionTstates()
        {
            int pending = _batchedInstructionTstates;
            if (pending <= 0 || !_hasTstateConsumer)
            {
                _batchedInstructionTstates = 0;
                return;
            }

            // Clear before entering the scheduler so a device-side synchronisation
            // cannot observe and flush the same interval twice.
            _batchedInstructionTstates = 0;
            _tstateConsumer!.OnTstatesConsumed(pending);
            RefreshBatchedSyncDeadline();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void RefreshBatchedSyncDeadline()
        {
            _batchedSyncDeadline = _batchInstructionTstates && _hasTstateConsumer
                ? _tstateConsumer!.GetNextCpuBoundarySyncTstate()
                : ulong.MaxValue;
        }

        public bool InterruptsEnabled => IFF1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ushort GetBC()
        {
            return (ushort)((B << 8) | C);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ushort GetDE()
        {
            return (ushort)((D << 8) | E);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ushort GetHL()
        {
            return (ushort)((H << 8) | L);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetBC(ushort val)
        {
            B = (byte)(val >> 8);
            C = (byte)(val & 0xFF);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetDE(ushort val)
        {
            D = (byte)(val >> 8);
            E = (byte)(val & 0xFF);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetHL(ushort val)
        {
            H = (byte)(val >> 8);
            L = (byte)(val & 0xFF);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryExecuteDecodedOpcode(byte opcode)
        {
            if (TryExecuteRegisterLoad(opcode)
                || TryExecuteImmediateRegisterLoad(opcode)
                || TryExecuteAluRegister(opcode)
                || TryExecuteImmediateAlu(opcode))
            {
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryExecuteRegisterLoad(byte opcode)
        {
            if ((opcode & 0xC0) != 0x40 || opcode == 0x76)
            {
                return false;
            }

            byte value = ReadRegisterOrHl((byte)(opcode & 0x07));
            WriteRegisterOrHl((byte)((opcode >> 3) & 0x07), value);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryExecuteImmediateRegisterLoad(byte opcode)
        {
            if ((opcode & 0xC7) != 0x06)
            {
                return false;
            }

            byte destination = (byte)((opcode >> 3) & 0x07);
            WriteRegisterOrHl(destination, NextByte());
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryExecuteAluRegister(byte opcode)
        {
            if ((opcode & 0xC0) != 0x80)
            {
                return false;
            }

            ExecuteAlu((byte)((opcode >> 3) & 0x07), ReadRegisterOrHl((byte)(opcode & 0x07)));
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryExecuteImmediateAlu(byte opcode)
        {
            if ((opcode & 0xC7) != 0xC6)
            {
                return false;
            }

            ExecuteAlu((byte)((opcode >> 3) & 0x07), NextByte());
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExecuteAlu(byte operation, byte value)
        {
            switch (operation)
            {
                case 0:
                    A = AddByte(A, value, false);
                    break;
                case 1:
                    A = AddByte(A, value, (F & CFMask) != 0);
                    break;
                case 2:
                    A = SubByte(A, value, false);
                    break;
                case 3:
                    A = SubByte(A, value, (F & CFMask) != 0);
                    break;
                case 4:
                    LAnd(value);
                    break;
                case 5:
                    LXor(value);
                    break;
                case 6:
                    LOr(value);
                    break;
                default:
                    CP(value);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte ReadRegisterOrHl(byte registerCode)
        {
            return registerCode switch
            {
                0 => B,
                1 => C,
                2 => D,
                3 => E,
                4 => H,
                5 => L,
                6 => ReadByte(GetHL()),
                _ => A,
            };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteRegisterOrHl(byte registerCode, byte value)
        {
            switch (registerCode)
            {
                case 0: B = value; break;
                case 1: C = value; break;
                case 2: D = value; break;
                case 3: E = value; break;
                case 4: H = value; break;
                case 5: L = value; break;
                case 6: WriteByte(GetHL(), value); break;
                default: A = value; break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte GetF()
        {
            return F;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetF(byte val)
        {
            // Loading/exchanging AF changes the stored F byte but does not count as
            // an ALU flag result and therefore must not populate the hidden Q latch.
            _f = val;
        }

        /// <summary>Performs the M1 refresh increment: only R bits 0-6 participate.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void IncR()
        {
            R = (byte)((R & 0x80) | ((R + 1) & 0x7f));
            if (HasRefreshObserver)
            {
                _refreshObserver!.OnRefresh(R);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Carry(int bit_no, ushort a, ushort b, bool cy)
        {
            int result = a + b + (cy ? 1 : 0);
            int carry = result ^ a ^ b;
            return (carry & (1 << bit_no)) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Parity(byte val)
        {
            return EvenParityTable[val];
        }
        private static bool[] BuildEvenParityTable()
        {
            var table = new bool[256];
            for (int value = 0; value < table.Length; value++)
            {
                int bits = value;
                bits ^= bits >> 4;
                bits ^= bits >> 2;
                bits ^= bits >> 1;
                table[value] = (bits & 1) == 0;
            }

            return table;
        }
        private static byte[] BuildSzpxyFlagsTable()
        {
            var table = new byte[256];
            for (int value = 0; value < table.Length; value++)
            {
                byte flags = (byte)(value & (SFMask | YFMask | XFMask));
                if (value == 0)
                {
                    flags |= ZFMask;
                }

                if (EvenParityTable[value])
                {
                    flags |= PFMask;
                }

                table[value] = flags;
            }

            return table;
        }

        /// <summary>Dispatches a fetched base opcode or transfers to its prefix decoder.</summary>
        internal void ExecOpcode(byte opcode)
        {
            if (opcode == 0xCB)
            {
                byte cb = FetchOpcode();
                ExecOpcodeCB(cb);
                return;
            }

            if (opcode == 0xED)
            {
                byte ed = FetchOpcode();
                ExecOpcodeED(ed);
                return;
            }

            if (opcode == 0xDD)
            {
                byte dd = FetchOpcode();
                ExecOpcodeDDFD(dd, ref IX);
                return;
            }

            if (opcode == 0xFD)
            {
                byte fd = FetchOpcode();
                ExecOpcodeDDFD(fd, ref IY);
                return;
            }

            BeginInstruction(cyc_00[opcode], 4);

            if (TryExecuteDecodedOpcode(opcode))
            {
                EndInstruction();
                return;
            }

            switch (opcode)
            {
                case 0x0A:
                    A = ReadByte(GetBC());
                    MemPtr = (ushort)(GetBC() + 1);
                    break; // ld a,(bc)
                case 0x1A:
                    A = ReadByte(GetDE());
                    MemPtr = (ushort)(GetDE() + 1);
                    break; // ld a,(de)
                case 0x3A:
                    {
                        ushort addr = NextWord();
                        A = ReadByte(addr);
                        MemPtr = (ushort)(addr + 1);
                    }
                    break; // ld a,(**)

                case 0x02:
                    WriteByte(GetBC(), A);
                    MemPtr = (ushort)((A << 8) | ((GetBC() + 1) & 0xFF));
                    break; // ld (bc),a

                case 0x12:
                    WriteByte(GetDE(), A);
                    MemPtr = (ushort)((A << 8) | ((GetDE() + 1) & 0xFF));
                    break; // ld (de),a

                case 0x32:
                    {
                        ushort addr = NextWord();
                        WriteByte(addr, A);
                        MemPtr = (ushort)((A << 8) | ((addr + 1) & 0xFF));
                    }
                    break; // ld (**),a

                case 0x01:
                    SetBC(NextWord());
                    break; // ld bc,**
                case 0x11:
                    SetDE(NextWord());
                    break; // ld de,**
                case 0x21:
                    SetHL(NextWord());
                    break; // ld hl,**
                case 0x31:
                    SP = NextWord();
                    break; // ld sp,**

                case 0x2A:
                    {
                        ushort addr = NextWord();
                        SetHL(ReadWord(addr));
                        MemPtr = (ushort)(addr + 1);
                    }
                    break; // ld hl,(**)

                case 0x22:
                    {
                        ushort addr = NextWord();
                        WriteWord(addr, GetHL());
                        MemPtr = (ushort)(addr + 1);
                    }
                    break; // ld (**),hl

                case 0xF9:
                    SP = GetHL();
                    break; // ld sp,hl

                case 0xEB:
                    {
                        ushort de = GetDE();
                        SetDE(GetHL());
                        SetHL(de);
                    }
                    break; // ex de,hl

                case 0xE3:
                    {
                        ushort val = ReadWord(SP);
                        WriteWord(SP, GetHL());
                        SetHL(val);
                        MemPtr = val;
                    }
                    break; // ex (sp),hl

                case 0x09:
                    AddHL(GetBC());
                    break; // add hl,bc
                case 0x19:
                    AddHL(GetDE());
                    break; // add hl,de
                case 0x29:
                    AddHL(GetHL());
                    break; // add hl,hl
                case 0x39:
                    AddHL(SP);
                    break; // add hl,sp

                case 0xF3:
                    IFF1 = false;
                    IFF2 = false;
                    IffDelay = 0;
                    break; // di
                case 0xFB:
                    IffDelay = 2;
                    break; // ei
                case 0x00:
                    break; // nop
                case 0x76:
                    Halted = true;
                    break; // halt

                case 0x3C:
                    A = Inc(A);
                    break; // inc a
                case 0x04:
                    B = Inc(B);
                    break; // inc b
                case 0x0C:
                    C = Inc(C);
                    break; // inc c
                case 0x14:
                    D = Inc(D);
                    break; // inc d
                case 0x1C:
                    E = Inc(E);
                    break; // inc e
                case 0x24:
                    H = Inc(H);
                    break; // inc h
                case 0x2C:
                    L = Inc((byte)L);
                    break; // inc l
                case 0x34:
                    {
                        byte result = Inc(ReadByte(GetHL()));
                        WriteByte(GetHL(), result);
                    }
                    break; // inc (hl)

                case 0x3D:
                    A = Dec(A);
                    break; // dec a
                case 0x05:
                    B = Dec(B);
                    break; // dec b
                case 0x0D:
                    C = Dec(C);
                    break; // dec c
                case 0x15:
                    D = Dec(D);
                    break; // dec d
                case 0x1D:
                    E = Dec(E);
                    break; // dec e
                case 0x25:
                    H = Dec(H);
                    break; // dec h
                case 0x2D:
                    L = Dec((byte)L);
                    break; // dec l
                case 0x35:
                    {
                        byte result = Dec(ReadByte(GetHL()));
                        WriteByte(GetHL(), result);
                    }
                    break; // dec (hl)

                case 0x03:
                    SetBC((ushort)(GetBC() + 1));
                    break; // inc bc
                case 0x13:
                    SetDE((ushort)(GetDE() + 1));
                    break; // inc de
                case 0x23:
                    SetHL((ushort)(GetHL() + 1));
                    break; // inc hl
                case 0x33:
                    SP = (ushort)(SP + 1);
                    break; // inc sp

                case 0x0B:
                    SetBC((ushort)(GetBC() - 1));
                    break; // dec bc
                case 0x1B:
                    SetDE((ushort)(GetDE() - 1));
                    break; // dec de
                case 0x2B:
                    SetHL((ushort)(GetHL() - 1));
                    break; // dec hl
                case 0x3B:
                    SP = (ushort)(SP - 1);
                    break; // dec sp

                case 0x27:
                    DAA();
                    break; // daa

                case 0x2F:
                    A = (byte)~A;
                    F = (byte)((F & (SFMask | ZFMask | PFMask | CFMask)) | HFMask | NFMask | (A & (YFMask | XFMask)));
                    break; // cpl

                case 0x37:
                    {
                        byte oldFlags = F;
                        byte undocumented = (byte)(((_lastQ ^ oldFlags) | A) & (YFMask | XFMask));
                        F = (byte)((oldFlags & (SFMask | ZFMask | PFMask)) | undocumented | CFMask);
                    }
                    break; // scf

                case 0x3F:
                    {
                        byte oldFlags = F;
                        byte undocumented = (byte)(((_lastQ ^ oldFlags) | A) & (YFMask | XFMask));
                        F = (byte)((oldFlags & (SFMask | ZFMask | PFMask)) |
                                   undocumented |
                                   ((oldFlags & CFMask) != 0 ? HFMask : CFMask));
                    }
                    break; // ccf

                case 0x07:
                    {
                        bool carry = A >> 7 != 0;
                        A = (byte)((A << 1) | (carry ? 1 : 0));
                        F = (byte)((F & (SFMask | ZFMask | PFMask)) | (A & (YFMask | XFMask)) | (carry ? CFMask : 0));
                    }
                    break; // rlca (rotate left)

                case 0x0F:
                    {
                        bool carry = (A & 1) != 0;
                        A = (byte)((A >> 1) | ((carry ? 1 : 0) << 7));
                        F = (byte)((F & (SFMask | ZFMask | PFMask)) | (A & (YFMask | XFMask)) | (carry ? CFMask : 0));
                    }
                    break; // rrca (rotate right)

                case 0x17:
                    {
                        bool cy = (F & CFMask) != 0;
                        bool carry = A >> 7 != 0;
                        A = (byte)((A << 1) | (cy ? 1 : 0));
                        F = (byte)((F & (SFMask | ZFMask | PFMask)) | (A & (YFMask | XFMask)) | (carry ? CFMask : 0));
                    }
                    break; // rla

                case 0x1F:
                    {
                        bool cy = (F & CFMask) != 0;
                        bool carry = (A & 1) != 0;
                        A = (byte)((A >> 1) | ((cy ? 1 : 0) << 7));
                        F = (byte)((F & (SFMask | ZFMask | PFMask)) | (A & (YFMask | XFMask)) | (carry ? CFMask : 0));
                    }
                    break; // rra

                case 0xC3:
                    Jump(NextWord());
                    break; // jm **
                case 0xC2:
                    CondJump((F & ZFMask) == 0);
                    break; // jp n**
                case 0xCA:
                    CondJump((F & ZFMask) != 0);
                    break; // jp **
                case 0xD2:
                    CondJump((F & CFMask) == 0);
                    break; // jp nc, **
                case 0xDA:
                    CondJump((F & CFMask) != 0);
                    break; // jp c, **
                case 0xE2:
                    CondJump((F & PFMask) == 0);
                    break; // jp po, **
                case 0xEA:
                    CondJump((F & PFMask) != 0);
                    break; // jp pe, **
                case 0xF2:
                    CondJump((F & SFMask) == 0);
                    break; // jp p, **
                case 0xFA:
                    CondJump((F & SFMask) != 0);
                    break; // jp m, **

                case 0x10:
                    DJNZ();
                    break; // djnz *
                case 0x18:
                    {
                        sbyte displacement = (sbyte)NextByte();
                        InternalCycle((ushort)(PC - 1), 5);
                        JR(displacement);
                    }
                    break; // jr *
                case 0x20:
                    CondJR((F & ZFMask) == 0);
                    break; // jr n*
                case 0x28:
                    CondJR((F & ZFMask) != 0);
                    break; // jr *
                case 0x30:
                    CondJR((F & CFMask) == 0);
                    break; // jr nc, *
                case 0x38:
                    CondJR((F & CFMask) != 0);
                    break; // jr c, *

                case 0xE9:
                    PC = GetHL();
                    break; // jp (hl)
                case 0xCD:
                    {
                        ushort addr = NextWord();
                        InternalCycle((ushort)(PC - 1), 1);
                        Call(addr);
                    }
                    break; // call

                case 0xC4:
                    CondCall((F & ZFMask) == 0);
                    break; // cnz
                case 0xCC:
                    CondCall((F & ZFMask) != 0);
                    break; // cz
                case 0xD4:
                    CondCall((F & CFMask) == 0);
                    break; // cnc
                case 0xDC:
                    CondCall((F & CFMask) != 0);
                    break; // cc
                case 0xE4:
                    CondCall((F & PFMask) == 0);
                    break; // cpo
                case 0xEC:
                    CondCall((F & PFMask) != 0);
                    break; // cpe
                case 0xF4:
                    CondCall((F & SFMask) == 0);
                    break; // cp
                case 0xFC:
                    CondCall((F & SFMask) != 0);
                    break; // cm

                case 0xC9:
                    Ret();
                    break; // ret
                case 0xC0:
                    CondRet((F & ZFMask) == 0);
                    break; // ret nz
                case 0xC8:
                    CondRet((F & ZFMask) != 0);
                    break; // ret z
                case 0xD0:
                    CondRet((F & CFMask) == 0);
                    break; // ret nc
                case 0xD8:
                    CondRet((F & CFMask) != 0);
                    break; // ret c
                case 0xE0:
                    CondRet((F & PFMask) == 0);
                    break; // ret po
                case 0xE8:
                    CondRet((F & PFMask) != 0);
                    break; // ret pe
                case 0xF0:
                    CondRet((F & SFMask) == 0);
                    break; // ret p
                case 0xF8:
                    CondRet((F & SFMask) != 0);
                    break; // ret m

                case 0xC7:
                    InternalCycle(1);
                    Call(0x00);
                    break; // rst 0
                case 0xCF:
                    InternalCycle(1);
                    Call(0x08);
                    break; // rst 1
                case 0xD7:
                    InternalCycle(1);
                    Call(0x10);
                    break; // rst 2
                case 0xDF:
                    InternalCycle(1);
                    Call(0x18);
                    break; // rst 3
                case 0xE7:
                    InternalCycle(1);
                    Call(0x20);
                    break; // rst 4
                case 0xEF:
                    InternalCycle(1);
                    Call(0x28);
                    break; // rst 5
                case 0xF7:
                    InternalCycle(1);
                    Call(0x30);
                    break; // rst 6
                case 0xFF:
                    InternalCycle(1);
                    Call(0x38);
                    break; // rst 7

                case 0xC5:
                    InternalCycle(1);
                    PushWord(GetBC());
                    break; // push bc
                case 0xD5:
                    InternalCycle(1);
                    PushWord(GetDE());
                    break; // push de
                case 0xE5:
                    InternalCycle(1);
                    PushWord(GetHL());
                    break; // push hl
                case 0xF5:
                    InternalCycle(1);
                    PushWord((ushort)((A << 8) | GetF()));
                    break; // push af

                case 0xC1:
                    SetBC(PopWord());
                    break; // pop bc
                case 0xD1:
                    SetDE(PopWord());
                    break; // pop de
                case 0xE1:
                    SetHL(PopWord());
                    break; // pop hl
                case 0xF1:
                    {
                        ushort val = PopWord();
                        A = (byte)(val >> 8);
                        SetF((byte)(val & 0xFF));
                    }
                    break; // pop af

                case 0xDB:
                    {
                        ushort opcodePc = unchecked((ushort)(PC - 1));
                        byte portLow = NextByte();
                        byte a = A;
                        if (_hasTapeAccelerationHook)
                        {
                            // Recognition must inspect the pulse current after the
                            // opcode and immediate-fetch cycles, not stale playback.
                            FlushBatchedInstructionTstates();
                            _tapeAccelerationHook!.BeforeInAImmediate(opcodePc, portLow);
                        }

                        ushort port = (ushort)((a << 8) | portLow);
                        A = ReadPortByte(port);
                        MemPtr = (ushort)((a << 8) | (byte)(portLow + 1));
                    }
                    break; // in a,(n)

                case 0xD3:
                    {
                        byte portLow = NextByte();
                        ushort port = (ushort)((A << 8) | portLow);
                        WritePortByte(port, A);
                        MemPtr = (ushort)((portLow + 1) | (A << 8));
                    }
                    break; // out (n), a

                case 0x08:
                    {
                        byte a = A;
                        byte f = GetF();

                        A = A_;
                        SetF(F_);

                        A_ = a;
                        F_ = f;
                    }
                    break; // ex af,af'
                case 0xD9:
                    {
                        byte b = B;
                        byte c = C;
                        byte d = D;
                        byte e = E;
                        byte h = H;
                        byte l = (byte)L;

                        B = B_;
                        C = C_;
                        D = D_;
                        E = E_;
                        H = H_;
                        L = L_;

                        B_ = b;
                        C_ = c;
                        D_ = d;
                        E_ = e;
                        H_ = h;
                        L_ = l;
                    }
                    break; // exx

                default:
                    Console.Error.Write("unknown opcode {0:X2}\n", opcode);
                    break;
            }

            EndInstruction();
        }

        /// <summary>Decodes CB opcodes as x=group, y=operation/bit and z=register/(HL).</summary>
        public void ExecOpcodeCB(byte opcode)
        {
            byte x_ = (byte)((opcode >> 6) & 0x03);
            byte y_ = (byte)((opcode >> 3) & 0x07);
            byte z_ = (byte)(opcode & 0x07);
            bool isMemoryReference = z_ == 6;
            bool isBit = x_ == 1;
            int totalCycles = isMemoryReference ? (isBit ? 12 : 15) : 8;
            BeginInstruction(totalCycles, 8);

            byte regVal = 0;
            byte hlVal;
            switch (z_)
            {
                case 0: regVal = B; break;
                case 1: regVal = C; break;
                case 2: regVal = D; break;
                case 3: regVal = E; break;
                case 4: regVal = H; break;
                case 5: regVal = L; break;
                case 6:
                    hlVal = ReadByte(GetHL());  // read byte from (HL)
                    regVal = hlVal;
                    break;
                case 7: regVal = A; break;
            }

            switch (x_)
            {
                case 0:
                    switch (y_)
                    {
                        case 0: regVal = CBRLC(regVal); break;
                        case 1: regVal = CBRRC(regVal); break;
                        case 2: regVal = CBRL(regVal); break;
                        case 3: regVal = CBRR(regVal); break;
                        case 4: regVal = CBSLA(regVal); break;
                        case 5: regVal = CBSRA(regVal); break;
                        case 6: regVal = CBSLL(regVal); break;
                        case 7: regVal = CBSRL(regVal); break;
                    }
                    break;

                case 1:
                    CBBit(regVal, y_);
                    if (z_ == 6)
                    {
                        // BIT (HL) exposes flags 5/3 from the existing WZ high byte. It does not
                        // load WZ with HL first; doing so destroys the previous instruction's
                        // MEMPTR value and makes diagnostic sequences unable to observe it.
                        byte high = (byte)(MemPtr >> 8);
                        F = (byte)((F & ~(YFMask | XFMask)) | (high & (YFMask | XFMask)));

                        InternalCycle(GetHL(), 1);
                    }
                    break;

                case 2:
                    regVal = (byte)(regVal & ~(1 << y_));
                    break;

                case 3:
                    regVal = (byte)(regVal | (1 << y_));
                    break;
            }

            if ((x_ == 0 || x_ == 2 || x_ == 3) && (z_ == 6))
            {
                InternalCycle(GetHL(), 1);
            }

            if (isMemoryReference && x_ != 1)
            {
                WriteByte(GetHL(), regVal);
            }
            else
            {
                switch (z_)
                {
                    case 0: B = regVal; break;
                    case 1: C = regVal; break;
                    case 2: D = regVal; break;
                    case 3: E = regVal; break;
                    case 4: H = regVal; break;
                    case 5: L = regVal; break;
                    case 7: A = regVal; break;
                }
            }

            EndInstruction();
        }

        /// <summary>Executes the final byte of a DD/FD-CB displacement instruction.</summary>
        internal void ExecOpcodeDCB(byte opcode, ushort addr)
        {
            byte val = ReadByte(addr);
            byte result = 0;

            byte x_ = (byte)((opcode >> 6) & 3);
            byte y_ = (byte)((opcode >> 3) & 7);
            byte z_ = (byte)(opcode & 7);

            switch (x_)
            {
                case 0:
                    {
                        switch (y_)
                        {
                            case 0:
                                result = CBRLC(val);
                                break;
                            case 1:
                                result = CBRRC(val);
                                break;
                            case 2:
                                result = CBRL(val);
                                break;
                            case 3:
                                result = CBRR(val);
                                break;
                            case 4:
                                result = CBSLA(val);
                                break;
                            case 5:
                                result = CBSRA(val);
                                break;
                            case 6:
                                result = CBSLL(val);
                                break;
                            case 7:
                                result = CBSRL(val);
                                break;
                        }
                    }
                    break;
                case 1:
                    {
                        result = CBBit(val, y_);
                        byte high = (byte)(addr >> 8);
                        F = (byte)((F & ~(YFMask | XFMask)) | (high & (YFMask | XFMask)));
                    }
                    break; // bit y,(iz+d)
                case 2:
                    result = (byte)(val & ~(1 << y_));
                    break; // res y, (iz+d)
                case 3:
                    result = (byte)(val | (1 << y_));
                    break; // set y, (iz+d)

                default:
                    Console.Error.Write("unknown XYCB opcode: {0:X2}\n", opcode);
                    break;
            }

            InternalCycle(addr, 2);

            // Undocumented DDCB/FDCB forms copy the transformed memory value to z as
            // well as writing it back; z=6 is the documented memory-only encoding.
            if (x_ != 1 && z_ != 6)
            {
                switch (z_)
                {
                    case 0:
                        B = result;
                        break;
                    case 1:
                        C = result;
                        break;
                    case 2:
                        D = result;
                        break;
                    case 3:
                        E = result;
                        break;
                    case 4:
                        H = result;
                        break;
                    case 5:
                        L = result;
                        break;
                    case 6:
                        WriteByte(GetHL(), result);
                        break;
                    case 7:
                        A = result;
                        break;
                }
            }

            if (x_ != 1)
            {
                WriteByte(addr, result);
            }
        }

        /// <summary>Executes ED arithmetic, I/O, interrupt and block-operation opcodes.</summary>
        internal void ExecOpcodeED(byte opcode)
        {
            BeginInstruction(cyc_ed[opcode], 8);
            switch (opcode)
            {
                case 0x47:
                    I = A;
                    break; // ld i,a
                case 0x4F:
                    R = A;
                    if (HasRefreshObserver)
                    {
                        _refreshObserver!.OnRefreshRegisterLoaded(R);
                    }
                    break; // ld r,a

                case 0x57:
                    A = I;
                    F = (byte)((F & CFMask) | (A & (SFMask | YFMask | XFMask)) | (A == 0 ? ZFMask : 0) | (IFF2 ? PFMask : 0));
                    break; // ld a,i

                case 0x5F:
                    A = R;
                    F = (byte)((F & CFMask) | (A & (SFMask | YFMask | XFMask)) | (A == 0 ? ZFMask : 0) | (IFF2 ? PFMask : 0));
                    break; // ld a,r

                case 0x45:
                case 0x55:
                case 0x5D:
                case 0x65:
                case 0x6D:
                case 0x75:
                case 0x7D:
                    IFF1 = IFF2;
                    Ret();
                    break; // retn
                case 0x4D:
                    Ret();
                    break; // reti

                case 0xA0:
                    LDI();
                    break; // ldi
                case 0xB0:
                    {
                        LDI();

                        if (GetBC() != 0)
                        {
                            PC -= 2;
                            InternalCycle((ushort)(GetDE() - 1), 5);
                            SetBlockRepeatFlags();
                            MemPtr = (ushort)(PC + 1);
                        }
                    }
                    break; // ldir

                case 0xA8:
                    LDD();
                    break; // ldd
                case 0xB8:
                    {
                        LDD();

                        if (GetBC() != 0)
                        {
                            PC -= 2;
                            InternalCycle((ushort)(GetDE() + 1), 5);
                            SetBlockRepeatFlags();
                            MemPtr = (ushort)(PC + 1);
                        }
                    }
                    break; // lddr

                case 0xA1:
                    CPI();
                    break; // cpi
                case 0xA9:
                    CPD();
                    break; // cpd
                case 0xB1:
                    {
                        CPI();
                        if (GetBC() != 0 && (F & ZFMask) == 0)
                        {
                            PC -= 2;
                            InternalCycle((ushort)(GetHL() - 1), 5);
                            SetBlockRepeatFlags();
                            MemPtr = (ushort)(PC + 1);
                        }
                    }
                    break; // cpir
                case 0xB9:
                    {
                        CPD();
                        if (GetBC() != 0 && (F & ZFMask) == 0)
                        {
                            PC -= 2;
                            InternalCycle((ushort)(GetHL() + 1), 5);
                            SetBlockRepeatFlags();
                            MemPtr = (ushort)(PC + 1);
                        }
                    }
                    break; // cpdr

                case 0x40:
                    IN_R_C(ref B);
                    break; // in b, (c)
                case 0x48:
                    IN_R_C(ref C);
                    break; // in c, (c)
                case 0x50:
                    IN_R_C(ref D);
                    break; // in d, (c)
                case 0x58:
                    IN_R_C(ref E);
                    break; // in e, (c)
                case 0x60:
                    IN_R_C(ref H);
                    break; // in h, (c)
                case 0x68:
                    IN_R_C(ref L);
                    break; // in l, (c)
                case 0x70:
                    {
                        byte val = 0;
                        IN_R_C(ref val);
                    }
                    break; // in (c)
                case 0x78:
                    IN_R_C(ref A);
                    MemPtr = (ushort)(GetBC() + 1);
                    break; // in a, (c)

                case 0xA2:
                    INI();
                    break; // ini
                case 0xB2:
                    {
                        byte value = INI();
                        if (B > 0)
                        {
                            PC -= 2;
                            InternalCycle((ushort)(GetHL() - 1), 5);
                            SetBlockIoRepeatFlags(value, unchecked((byte)(C + 1)));
                            MemPtr = (ushort)(PC + 1);
                        }
                    }
                    break; // inir
                case 0xAA:
                    IND();
                    break; // ind
                case 0xBA:
                    {
                        byte value = IND();
                        if (B > 0)
                        {
                            PC -= 2;
                            InternalCycle((ushort)(GetHL() + 1), 5);
                            SetBlockIoRepeatFlags(value, unchecked((byte)(C - 1)));
                            MemPtr = (ushort)(PC + 1);
                        }
                    }
                    break; // indr

                case 0x41:
                    WritePortByte(GetBC(), B);
                    MemPtr = (ushort)(GetBC() + 1);
                    break; // out (c), b
                case 0x49:
                    WritePortByte(GetBC(), C);
                    MemPtr = (ushort)(GetBC() + 1);
                    break; // out (c), c
                case 0x51:
                    WritePortByte(GetBC(), D);
                    MemPtr = (ushort)(GetBC() + 1);
                    break; // out (c), d
                case 0x59:
                    WritePortByte(GetBC(), E);
                    MemPtr = (ushort)(GetBC() + 1);
                    break; // out (c), e
                case 0x61:
                    WritePortByte(GetBC(), H);
                    MemPtr = (ushort)(GetBC() + 1);
                    break; // out (c), h
                case 0x69:
                    WritePortByte(GetBC(), L);
                    MemPtr = (ushort)(GetBC() + 1);
                    break; // out (c), l
                case 0x71:
                    WritePortByte(GetBC(), 0);
                    MemPtr = (ushort)(GetBC() + 1);
                    break; // out (c), 0
                case 0x79:
                    WritePortByte(GetBC(), A);
                    MemPtr = (ushort)(GetBC() + 1);
                    break; // out (c), a

                case 0xA3:
                    OUTI();
                    break; // outi
                case 0xB3:
                    {
                        byte value = OUTI();
                        if (B > 0)
                        {
                            PC -= 2;
                            InternalCycle(GetBC(), 5);
                            SetBlockIoRepeatFlags(value, L);
                            MemPtr = (ushort)(PC + 1);
                        }
                    }
                    break; // otir
                case 0xAB:
                    OUTD();
                    break; // outd
                case 0xBB:
                    {
                        byte value = OUTD();
                        if (B > 0)
                        {
                            PC -= 2;
                            InternalCycle(GetBC(), 5);
                            SetBlockIoRepeatFlags(value, L);
                            MemPtr = (ushort)(PC + 1);
                        }
                    }
                    break; // otdr

                case 0x42:
                    SbcHL(GetBC());
                    break; // sbc hl,bc
                case 0x52:
                    SbcHL(GetDE());
                    break; // sbc hl,de
                case 0x62:
                    SbcHL(GetHL());
                    break; // sbc hl,hl
                case 0x72:
                    SbcHL(SP);
                    break; // sbc hl,sp

                case 0x4A:
                    AdcHL(GetBC());
                    break; // adc hl,bc
                case 0x5A:
                    AdcHL(GetDE());
                    break; // adc hl,de
                case 0x6A:
                    AdcHL(GetHL());
                    break; // adc hl,hl
                case 0x7A:
                    AdcHL(SP);
                    break; // adc hl,sp

                case 0x43:
                    {
                        ushort addr = NextWord();
                        WriteWord(addr, GetBC());
                        MemPtr = (ushort)(addr + 1);
                    }
                    break; // ld (**), bc

                case 0x53:
                    {
                        ushort addr = NextWord();
                        WriteWord(addr, GetDE());
                        MemPtr = (ushort)(addr + 1);
                    }
                    break; // ld (**), de

                case 0x63:
                    {
                        ushort addr = NextWord();
                        WriteWord(addr, GetHL());
                        MemPtr = (ushort)(addr + 1);
                    }
                    break; // ld (**), hl

                case 0x73:
                    {
                        ushort addr = NextWord();
                        WriteWord(addr, SP);
                        MemPtr = (ushort)(addr + 1);
                    }
                    break; // ld (**),sp

                case 0x4B:
                    {
                        ushort addr = NextWord();
                        SetBC(ReadWord(addr));
                        MemPtr = (ushort)(addr + 1);
                    }
                    break; // ld bc, (**)

                case 0x5B:
                    {
                        ushort addr = NextWord();
                        SetDE(ReadWord(addr));
                        MemPtr = (ushort)(addr + 1);
                    }
                    break; // ld de, (**)

                case 0x6B:
                    {
                        ushort addr = NextWord();
                        SetHL(ReadWord(addr));
                        MemPtr = (ushort)(addr + 1);
                    }
                    break; // ld hl, (**)

                case 0x7B:
                    {
                        ushort addr = NextWord();
                        SP = ReadWord(addr);
                        MemPtr = (ushort)(addr + 1);
                    }
                    break; // ld sp,(**)

                case 0x44:
                case 0x54:
                case 0x64:
                case 0x74:
                case 0x4C:
                case 0x5C:
                case 0x6C:
                case 0x7C:
                    A = SubByte(0, A, false);
                    break; // neg

                case 0x46:
                case 0x66:
                    InterruptMode = 0;
                    break; // im 0
                case 0x56:
                case 0x76:
                    InterruptMode = 1;
                    break; // im 1
                case 0x5E:
                case 0x7E:
                    InterruptMode = 2;
                    break; // im 2

                case 0x67:
                    {
                        byte a = A;
                        byte val = ReadByte(GetHL());
                        A = (byte)((a & 0xF0) | (val & 0xF));
                        WriteByte(GetHL(), (byte)((val >> 4) | (a << 4)));

                        F = (byte)((F & CFMask) | SzpxyFlagsTable[A]);
                        MemPtr = (ushort)(GetHL() + 1);
                    }
                    break; // rrd

                case 0x6F:
                    {
                        byte a = A;
                        byte val = ReadByte(GetHL());
                        A = (byte)((a & 0xF0) | (val >> 4));
                        WriteByte(GetHL(), (byte)((val << 4) | (a & 0xF)));

                        F = (byte)((F & CFMask) | SzpxyFlagsTable[A]);
                        MemPtr = (ushort)(GetHL() + 1);
                    }
                    break; // rld

                default:
                    Console.Error.Write("unknown ED opcode: {0:X2}\n", opcode);
                    break;
            }

            EndInstruction();
        }

        /// <summary>Executes a DD/FD opcode against the supplied IX or IY register.</summary>
        internal void ExecOpcodeDDFD(byte opcode, ref ushort iz)
        {
            if (opcode == 0xCB)
            {
                sbyte disp = (sbyte)NextByte();
                ushort addr = Displace(iz, disp);
                byte op = FetchOpcode();
                int totalCycles = ((op & 0xC0) == 0x40) ? 20 : 23;
                BeginInstruction(totalCycles, 15);
                ExecOpcodeDCB(op, addr);
                EndInstruction();
                return;
            }

            BeginInstruction(cyc_ddfd[opcode], 8);

            switch (opcode)
            {
                case 0xE1:
                    iz = PopWord();
                    break; // pop iz
                case 0xE5:
                    InternalCycle(1);
                    PushWord(iz);
                    break; // push iz

                case 0xE9:
                    Jump(iz);
                    break; // jp iz

                case 0x09:
                    AddIZ(ref iz, GetBC());
                    break; // add iz,bc
                case 0x19:
                    AddIZ(ref iz, GetDE());
                    break; // add iz,de
                case 0x29:
                    AddIZ(ref iz, iz);
                    break; // add iz,iz
                case 0x39:
                    AddIZ(ref iz, SP);
                    break; // add iz,sp

                case 0x84:
                    A = AddByte(A, (byte)(iz >> 8), false);
                    break; // add a,izh
                case 0x85:
                    A = AddByte(A, (byte)(iz & 0xFF), false);
                    break; // add a,izl
                case 0x8C:
                    A = AddByte(A, (byte)(iz >> 8), (F & CFMask) != 0);
                    break; // adc a,izh
                case 0x8D:
                    A = AddByte(A, (byte)(iz & 0xFF), (F & CFMask) != 0);
                    break; // adc a,izl

                case 0x86:
                    A = AddByte(A, ReadByte(Displace(iz, (sbyte)NextByte())), false);
                    break; // add a,(iz+*)
                case 0x8E:
                    A = AddByte(A, ReadByte(Displace(iz,(sbyte)NextByte())), (F & CFMask) != 0);
                    break; // adc a,(iz+*)
                case 0x96:
                    A = SubByte(A, ReadByte(Displace(iz, (sbyte)NextByte())), false);
                    break; // sub (iz+*)
                case 0x9E:
                    A = SubByte(A, ReadByte(Displace(iz, (sbyte)NextByte())), (F & CFMask) != 0);
                    break; // sbc (iz+*)

                case 0x94:
                    A = SubByte(A, (byte)(iz >> 8), false);
                    break; // sub izh
                case 0x95:
                    A = SubByte(A, (byte)(iz & 0xFF), false);
                    break; // sub izl
                case 0x9C:
                    A = SubByte(A, (byte)(iz >> 8), (F & CFMask) != 0);
                    break; // sbc izh
                case 0x9D:
                    A = SubByte(A, (byte)(iz & 0xFF), (F & CFMask) != 0);
                    break; // sbc izl

                case 0xA6:
                    LAnd(ReadByte(Displace(iz, (sbyte)NextByte())));
                    break; // and (iz+*)
                case 0xA4:
                    LAnd((byte)(iz >> 8));
                    break; // and izh
                case 0xA5:
                    LAnd((byte)(iz & 0xFF));
                    break; // and izl

                case 0xAE:
                    LXor(ReadByte(Displace(iz, (sbyte)NextByte())));
                    break; // xor (iz+*)
                case 0xAC:
                    LXor((byte)(iz >> 8));
                    break; // xor izh
                case 0xAD:
                    LXor((byte)(iz & 0xFF));
                    break; // xor izl

                case 0xB6:
                    LOr(ReadByte(Displace(iz, (sbyte)NextByte())));
                    break; // or (iz+*)
                case 0xB4:
                    LOr((byte)(iz >> 8));
                    break; // or izh
                case 0xB5:
                    LOr((byte)(iz & 0xFF));
                    break; // or izl

                case 0xBE:
                    CP(ReadByte(Displace(iz, (sbyte)NextByte())));
                    break; // cp (iz+*)
                case 0xBC:
                    CP((byte)(iz >> 8));
                    break; // cp izh
                case 0xBD:
                    CP((byte)(iz & 0xFF));
                    break; // cp izl

                case 0x23:
                    iz += 1;
                    break; // inc iz
                case 0x2B:
                    iz -= 1;
                    break; // dec iz

                case 0x34:
                    {
                        ushort addr = Displace(iz, (sbyte)NextByte());
                        WriteByte(addr, Inc(ReadByte(addr)));
                    }
                    break; // inc (iz+*)

                case 0x35:
                    {
                        ushort addr = Displace(iz, (sbyte)NextByte());
                        WriteByte(addr, Dec(ReadByte(addr)));
                    }
                    break; // dec (iz+*)

                case 0x24:
                    iz = (ushort)((iz & 0xFF) | ((Inc((byte)(iz >> 8))) << 8));
                    break; // inc izh
                case 0x25:
                    iz = (ushort)((iz & 0xFF) | ((Dec((byte)(iz >> 8))) << 8));
                    break; // dec izh
                case 0x2C:
                    iz = (ushort)(((iz >> 8) << 8) | Inc((byte)(iz & 0xFF)));
                    break; // inc izl
                case 0x2D:
                    iz = (ushort)(((iz >> 8) << 8) | Dec((byte)(iz & 0xFF)));
                    break; // dec izl

                case 0x2A:
                    iz = ReadWord(NextWord());
                    break; // ld iz,(**)
                case 0x22:
                    WriteWord(NextWord(), iz);
                    break; // ld (**),iz
                case 0x21:
                    iz = NextWord();
                    break; // ld iz,**

                case 0x36:
                    {
                        ushort addr = Displace(iz, (sbyte)NextByte());
                        WriteByte(addr, NextByte());
                    }
                    break; // ld (iz+*),*

                case 0x70:
                    WriteByte(Displace(iz, (sbyte)NextByte()), B);
                    break; // ld (iz+*),b
                case 0x71:
                    WriteByte(Displace(iz, (sbyte)NextByte()), C);
                    break; // ld (iz+*),c
                case 0x72:
                    WriteByte(Displace(iz, (sbyte)NextByte()), D);
                    break; // ld (iz+*),d
                case 0x73:
                    WriteByte(Displace(iz, (sbyte)NextByte()), E);
                    break; // ld (iz+*),e
                case 0x74:
                    WriteByte(Displace(iz, (sbyte)NextByte()), H);
                    break; // ld (iz+*),h
                case 0x75:
                    WriteByte(Displace(iz, (sbyte)NextByte()), (byte)L);
                    break; // ld (iz+*),l
                case 0x77:
                    WriteByte(Displace(iz, (sbyte)NextByte()), A);
                    break; // ld (iz+*),a

                case 0x46:
                    B = ReadByte(Displace(iz, (sbyte)NextByte()));
                    break; // ld b,(iz+*)
                case 0x4E:
                    C = ReadByte(Displace(iz, (sbyte)NextByte()));
                    break; // ld c,(iz+*)
                case 0x56:
                    D = ReadByte(Displace(iz, (sbyte)NextByte()));
                    break; // ld d,(iz+*)
                case 0x5E:
                    E = ReadByte(Displace(iz, (sbyte)NextByte()));
                    break; // ld e,(iz+*)
                case 0x66:
                    H = ReadByte(Displace(iz, (sbyte)NextByte()));
                    break; // ld h,(iz+*)
                case 0x6E:
                    L = ReadByte(Displace(iz, (sbyte)NextByte()));
                    break; // ld l,(iz+*)
                case 0x7E:
                    A = ReadByte(Displace(iz, (sbyte)NextByte()));
                    break; // ld a,(iz+*)

                case 0x44:
                    B = (byte)(iz >> 8);
                    break; // ld b,izh
                case 0x4C:
                    C = (byte)(iz >> 8);
                    break; // ld c,izh
                case 0x54:
                    D = (byte)(iz >> 8);
                    break; // ld d,izh
                case 0x5C:
                    E = (byte)(iz >> 8);
                    break; // ld e,izh
                case 0x7C:
                    A = (byte)(iz >> 8);
                    break; // ld a,izh

                case 0x45:
                    B = (byte)(iz & 0xFF);
                    break; // ld b,izl
                case 0x4D:
                    C = (byte)(iz & 0xFF);
                    break; // ld c,izl
                case 0x55:
                    D = (byte)(iz & 0xFF);
                    break; // ld d,izl
                case 0x5D:
                    E = (byte)(iz & 0xFF);
                    break; // ld e,izl
                case 0x7D:
                    A = (byte)(iz & 0xFF);
                    break; // ld a,izl

                case 0x60:
                    iz = (ushort)((iz & 0xFF) | (B << 8));
                    break; // ld izh,b
                case 0x61:
                    iz = (ushort)((iz & 0xFF) | (C << 8));
                    break; // ld izh,c
                case 0x62:
                    iz = (ushort)((iz & 0xFF) | (D << 8));
                    break; // ld izh,d
                case 0x63:
                    iz = (ushort)((iz & 0xFF) | (E << 8));
                    break; // ld izh,e
                case 0x64:
                    break; // ld izh,izh
                case 0x65:
                    iz = (ushort)(((iz & 0xFF) << 8) | (iz & 0xFF));
                    break; // ld izh,izl
                case 0x67:
                    iz = (ushort)((iz & 0xFF) | (A << 8));
                    break; // ld izh,a
                case 0x26:
                    iz = (ushort)((iz & 0xFF) | (NextByte() << 8));
                    break; // ld izh,*

                case 0x68:
                    iz = (ushort)(((iz >> 8) << 8) | B);
                    break; // ld izl,b
                case 0x69:
                    iz = (ushort)(((iz >> 8) << 8) | C);
                    break; // ld izl,c
                case 0x6A:
                    iz = (ushort)(((iz >> 8) << 8) | D);
                    break; // ld izl,d
                case 0x6B:
                    iz = (ushort)(((iz >> 8) << 8) | E);
                    break; // ld izl,e
                case 0x6C:
                    iz = (ushort)(((iz >> 8) << 8) | (iz >> 8));
                    break; // ld izl,izh
                case 0x6D:
                    break; // ld izl,izl
                case 0x6F:
                    iz = (ushort)(((iz >> 8) << 8) | A);
                    break; // ld izl,a
                case 0x2E:
                    iz = (ushort)(((iz >> 8) << 8) | NextByte());
                    break; // ld izl,*

                case 0xF9:
                    SP = iz;
                    break; // ld sp,iz

                case 0xE3:
                    {
                        ushort val = ReadWord(SP);
                        WriteWord(SP, iz);
                        iz = val;
                        MemPtr = val;
                    }
                    break; // ex (sp),iz

                default:
                    {
                        // A prefix ignored by this opcode still consumed an M1 fetch; the base
                        // decoder handles the operation while the prefix timing remains charged.
                        _remainingCycles = 0;
                        ExecOpcode(opcode);
                        return;
                    }
            }

            EndInstruction();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Jump(ushort addr)
        {
            PC = addr;
            MemPtr = addr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CondJump(bool condition)
        {
            ushort addr = NextWord();
            if (condition)
            {
                Jump(addr);
            }
            MemPtr = addr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Call(ushort addr)
        {
            PushWord(PC);
            PC = addr;
            MemPtr = addr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CondCall(bool condition)
        {
            ushort addr = NextWord();
            if (condition)
            {
                InternalCycle((ushort)(PC - 1), 1);
                Call(addr);
            }
            MemPtr = addr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Ret()
        {
            PC = PopWord();
            MemPtr = PC;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CondRet(bool condition)
        {
            if (condition)
            {
                InternalCycle(1);
                Ret();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void JR(sbyte displacement)
        {
            short newPc = (short)PC;
            newPc += (short)displacement;
            PC = (ushort)newPc;
            MemPtr = PC;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CondJR(bool condition)
        {
            sbyte b = (sbyte)NextByte();
            if (condition)
            {
                InternalCycle((ushort)(PC - 1), 5);
                JR(b);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DJNZ()
        {
            sbyte b = (sbyte)NextByte();
            B--;
            if (B != 0)
            {
                InternalCycle((ushort)(PC - 1), 6);
                JR(b);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte AddByte(byte a, byte b, bool cy)
        {
            int carryIn = cy ? 1 : 0;
            int sum = a + b + carryIn;
            byte result = (byte)sum;
            byte flags = (byte)(result & (SFMask | YFMask | XFMask));
            if (result == 0)
            {
                flags |= ZFMask;
            }

            if (((a ^ b ^ result) & HFMask) != 0)
            {
                flags |= HFMask;
            }

            if (((~(a ^ b) & (a ^ result)) & SFMask) != 0)
            {
                flags |= PFMask;
            }

            if ((sum & 0x100) != 0)
            {
                flags |= CFMask;
            }

            F = flags;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte SubByte(byte a, byte b, bool cy)
        {
            int carryIn = cy ? 1 : 0;
            int diff = a - b - carryIn;
            byte result = (byte)diff;
            byte flags = (byte)(NFMask | (result & (SFMask | YFMask | XFMask)));
            if (result == 0)
            {
                flags |= ZFMask;
            }

            if (((a ^ b ^ result) & HFMask) != 0)
            {
                flags |= HFMask;
            }

            if ((((a ^ b) & (a ^ result)) & SFMask) != 0)
            {
                flags |= PFMask;
            }

            if (diff < 0)
            {
                flags |= CFMask;
            }

            F = flags;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ushort AddWord(ushort a, ushort b, bool cy)
        {
            int carryIn = cy ? 1 : 0;
            int sum = a + b + carryIn;
            ushort result = (ushort)sum;
            byte flags = (byte)((result >> 8) & (YFMask | XFMask));

            if (result == 0)
            {
                flags |= ZFMask;
            }

            if ((result & 0x8000) != 0)
            {
                flags |= SFMask;
            }

            if (((a ^ b ^ result) & 0x1000) != 0)
            {
                flags |= HFMask;
            }

            if (((~(a ^ b) & (a ^ result)) & 0x8000) != 0)
            {
                flags |= PFMask;
            }

            if ((sum & 0x10000) != 0)
            {
                flags |= CFMask;
            }

            F = flags;
            MemPtr = (ushort)(a + 1);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ushort SubWord(ushort a, ushort b, bool cy)
        {
            int carryIn = cy ? 1 : 0;
            int diff = a - b - carryIn;
            ushort result = (ushort)diff;
            byte flags = (byte)(NFMask | ((result >> 8) & (YFMask | XFMask)));

            if (result == 0)
            {
                flags |= ZFMask;
            }

            if ((result & 0x8000) != 0)
            {
                flags |= SFMask;
            }

            if (((a ^ b ^ result) & 0x1000) != 0)
            {
                flags |= HFMask;
            }

            if ((((a ^ b) & (a ^ result)) & 0x8000) != 0)
            {
                flags |= PFMask;
            }

            if (diff < 0)
            {
                flags |= CFMask;
            }

            F = flags;
            MemPtr = (ushort)(a + 1);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AddHL(ushort val)
        {
            byte preserved = (byte)(F & (SFMask | ZFMask | PFMask));
            InternalCycle(7);
            ushort result = AddWord(GetHL(), val, false);
            SetHL(result);
            F = (byte)((F & ~(SFMask | ZFMask | PFMask)) | preserved);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AddIZ(ref ushort reg, ushort val)
        {
            byte preserved = (byte)(F & (SFMask | ZFMask | PFMask));
            InternalCycle(7);
            ushort result = AddWord(reg, val, false);
            reg = result;
            F = (byte)((F & ~(SFMask | ZFMask | PFMask)) | preserved);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AdcHL(ushort val)
        {
            SetHL(AddWord(GetHL(), val, (F & CFMask) != 0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SbcHL(ushort val)
        {
            SetHL(SubWord(GetHL(), val, (F & CFMask) != 0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte Inc(byte a)
        {
            byte result = (byte)(a + 1);
            byte flags = (byte)((F & CFMask) | (result & (SFMask | YFMask | XFMask)));
            if (result == 0)
            {
                flags |= ZFMask;
            }

            if ((a & 0x0F) == 0x0F)
            {
                flags |= HFMask;
            }

            if (a == 0x7F)
            {
                flags |= PFMask;
            }

            F = flags;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte Dec(byte a)
        {
            byte result = (byte)(a - 1);
            byte flags = (byte)((F & CFMask) | NFMask | (result & (SFMask | YFMask | XFMask)));
            if (result == 0)
            {
                flags |= ZFMask;
            }

            if ((a & 0x0F) == 0)
            {
                flags |= HFMask;
            }

            if (a == 0x80)
            {
                flags |= PFMask;
            }

            F = flags;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void LAnd(byte val)
        {
            if (_hasTapeAccelerationHook)
            {
                _tapeAccelerationHook!.NotifyAndOperand(val);
            }

            byte result = (byte)((byte)A & val);
            F = (byte)(SzpxyFlagsTable[result] | HFMask);
            A = result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void LXor(in byte val)
        {
            byte result = (byte)((byte)A ^ val);
            F = SzpxyFlagsTable[result];
            A = result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void LOr(in byte val)
        {
            byte result = (byte)((byte)A | val);
            F = SzpxyFlagsTable[result];
            A = result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CP(in byte val)
        {
            SubByte(A, val, false);

            // Unlike SUB, CP sources undocumented flags 5/3 from the operand.
            F = (byte)((F & ~(YFMask | XFMask)) | (val & (YFMask | XFMask)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte CBRLC(byte val)
        {
            bool old = (val >> 7) != 0;
            val = (byte)((val << 1) | (old ? 1 : 0));
            F = (byte)(SzpxyFlagsTable[val] | (old ? CFMask : 0));
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte CBRRC(byte val)
        {
            bool old = (val & 1) != 0;
            val = (byte)((val >> 1) | ((old ? 1 : 0) << 7));
            F = (byte)(SzpxyFlagsTable[val] | (old ? CFMask : 0));
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte CBRL(byte val)
        {
            bool cf = (F & CFMask) != 0;
            bool old = val >> 7 != 0;
            val = (byte)((val << 1) | (cf ? 1 : 0));
            F = (byte)(SzpxyFlagsTable[val] | (old ? CFMask : 0));
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte CBRR(byte val)
        {
            bool c = (F & CFMask) != 0;
            bool old = (val & 1) != 0;
            val = (byte)((val >> 1) | ((c ? 1 : 0) << 7));
            F = (byte)(SzpxyFlagsTable[val] | (old ? CFMask : 0));
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte CBSLA(byte val)
        {
            bool old = val >> 7 != 0;
            val <<= 1;
            F = (byte)(SzpxyFlagsTable[val] | (old ? CFMask : 0));
            return val;
        }

        // Undocumented SLL is SLA with bit 0 forced high.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte CBSLL(byte val)
        {
            bool old = val >> 7 != 0;
            val <<= 1;
            val |= 1;
            F = (byte)(SzpxyFlagsTable[val] | (old ? CFMask : 0));
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte CBSRA(byte val)
        {
            bool old = (val & 1) != 0;
            val = (byte)((val >> 1) | (val & 0x80));
            F = (byte)(SzpxyFlagsTable[val] | (old ? CFMask : 0));
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte CBSRL(byte val)
        {
            bool old = (val & 1) != 0;
            val >>= 1;
            F = (byte)(SzpxyFlagsTable[val] | (old ? CFMask : 0));
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte CBBit(byte val, byte n)
        {
            byte result = (byte)(val & (1 << n));
            byte flags = (byte)((F & CFMask) | HFMask | (val & (YFMask | XFMask)));
            if ((result & SFMask) != 0)
            {
                flags |= SFMask;
            }

            if (result == 0)
            {
                flags |= ZFMask | PFMask;
            }

            F = flags;
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void LDI()
        {
            byte preservedFlags = (byte)(F & (SFMask | ZFMask | CFMask));
            ushort de = GetDE();
            ushort hl = GetHL();
            byte val = ReadByte(hl);

            WriteByte(de, val);
            InternalCycle(de, 2);

            hl += 1;
            de += 1;
            ushort bc = (ushort)(GetBC() - 1);

            SetHL(hl);
            SetDE(de);
            SetBC(bc);

            byte result = (byte)(val + A);
            byte flags = (byte)(preservedFlags | (result & XFMask));
            if ((result & 0x02) != 0)
            {
                flags |= YFMask;
            }

            if (bc != 0)
            {
                flags |= PFMask;
            }

            F = flags;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void LDD()
        {
            byte preservedFlags = (byte)(F & (SFMask | ZFMask | CFMask));
            ushort de = GetDE();
            ushort hl = GetHL();
            byte val = ReadByte(hl);

            WriteByte(de, val);
            InternalCycle(de, 2);

            hl -= 1;
            de -= 1;
            ushort bc = (ushort)(GetBC() - 1);

            SetHL(hl);
            SetDE(de);
            SetBC(bc);

            byte result = (byte)(val + A);
            byte flags = (byte)(preservedFlags | (result & XFMask));
            if ((result & 0x02) != 0)
            {
                flags |= YFMask;
            }

            if (bc != 0)
            {
                flags |= PFMask;
            }

            F = flags;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CPI()
        {
            byte carryFlag = (byte)(F & CFMask);
            ushort hl = GetHL();
            byte val = ReadByte(hl);
            InternalCycle(hl, 5);
            hl += 1;
            ushort bc = (ushort)(GetBC() - 1);
            SetHL(hl);
            SetBC(bc);

            int diff = A - val;
            byte result = (byte)diff;
            bool h = ((A ^ val ^ result) & 0x10) != 0;

            byte temp = (byte)(result - (h ? 1 : 0));
            byte flags = (byte)(carryFlag | NFMask | (result & SFMask) | (temp & XFMask));
            if (result == 0)
            {
                flags |= ZFMask;
            }

            if (h)
            {
                flags |= HFMask;
            }

            if (bc != 0)
            {
                flags |= PFMask;
            }

            if ((temp & 0x02) != 0)
            {
                flags |= YFMask;
            }

            F = flags;
            MemPtr = unchecked((ushort)(MemPtr + 1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CPD()
        {
            byte carryFlag = (byte)(F & CFMask);
            ushort hl = GetHL();
            byte val = ReadByte(hl);
            InternalCycle(hl, 5);
            hl -= 1;
            ushort bc = (ushort)(GetBC() - 1);
            SetHL(hl);
            SetBC(bc);

            int diff = A - val;
            byte result = (byte)diff;
            bool h = ((A ^ val ^ result) & 0x10) != 0;

            byte temp = (byte)(result - (h ? 1 : 0));
            byte flags = (byte)(carryFlag | NFMask | (result & SFMask) | (temp & XFMask));
            if (result == 0)
            {
                flags |= ZFMask;
            }

            if (h)
            {
                flags |= HFMask;
            }

            if (bc != 0)
            {
                flags |= PFMask;
            }

            if ((temp & 0x02) != 0)
            {
                flags |= YFMask;
            }

            F = flags;
            MemPtr = unchecked((ushort)(MemPtr - 1));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void IN_R_C(ref byte r)
        {
            if (_hasTapeAccelerationHook)
            {
                // PC has consumed the ED prefix and sub-opcode; C is read before
                // the target register can overwrite it (IN C,(C)).
                _tapeAccelerationHook!.BeforeInRegC(unchecked((ushort)(PC - 2)), C);
            }

            r = ReadPortByte(GetBC());
            F = (byte)((F & CFMask) | SzpxyFlagsTable[r]);
            MemPtr = (ushort)(GetBC() + 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte INI()
        {
            ushort hl = GetHL();
            ushort bc = GetBC();
            InternalCycle(1);
            byte val = ReadPortByte(bc);
            WriteByte(hl, val);
            MemPtr = (ushort)(bc + 1);
            byte b = (byte)(B - 1);
            B = b;
            hl += 1;
            SetHL(hl);

            byte sum8 = (byte)(val + C + 1);
            bool carry = sum8 < val;
            SetBlockIoFlags(b, val, sum8, carry);
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte IND()
        {
            ushort hl = GetHL();
            ushort bc = GetBC();
            InternalCycle(1);
            byte val = ReadPortByte(bc);
            WriteByte(hl, val);
            MemPtr = (ushort)(bc - 1);
            byte b = (byte)(B - 1);
            B = b;
            hl -= 1;
            SetHL(hl);

            byte sum8 = (byte)(val + C - 1);
            bool carry = sum8 < val;
            SetBlockIoFlags(b, val, sum8, carry);
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte OUTI()
        {
            ushort hl = GetHL();
            InternalCycle(1);
            byte val = ReadByte(hl);
            byte b = (byte)(B - 1);
            B = b;
            MemPtr = (ushort)(GetBC() + 1);
            WritePortByte(GetBC(), val);
            hl += 1;
            SetHL(hl);

            byte l = (byte)(hl & 0xFF);
            byte sum8 = (byte)(val + l);
            bool carry = sum8 < val;
            SetBlockIoFlags(b, val, sum8, carry);
            return val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte OUTD()
        {
            ushort hl = GetHL();
            InternalCycle(1);
            byte val = ReadByte(hl);
            byte b = (byte)(B - 1);
            B = b;
            MemPtr = (ushort)(GetBC() - 1);
            WritePortByte(GetBC(), val);
            hl -= 1;
            SetHL(hl);

            byte l = (byte)(hl & 0xFF);
            byte sum8 = (byte)(val + l);
            bool carry = sum8 < val;
            SetBlockIoFlags(b, val, sum8, carry);
            return val;
        }

        /// <summary>
        /// Applies the undocumented flag changes made during the extra five-T-state
        /// M-cycle of a repeating block transfer or search instruction.
        /// </summary>
        /// <remarks>
        /// PC has already been moved back to the ED prefix. The Z80 copies bits 13
        /// and 11 of that instruction address into flags 5 and 3. Ordinarily these
        /// flags are overwritten by the final iteration, but self-modifying code can
        /// replace the repeated sub-opcode and make this intermediate state visible.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetBlockRepeatFlags()
        {
            F = (byte)((F & ~(YFMask | XFMask)) | ((PC >> 8) & (YFMask | XFMask)));
        }

        /// <summary>
        /// Applies the additional undocumented flags generated by the repeat M-cycle
        /// of INIR, INDR, OTIR and OTDR.
        /// </summary>
        /// <param name="value">The byte transferred during this iteration.</param>
        /// <param name="addressLow">C +/- 1 for input, or the updated L for output.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetBlockIoRepeatFlags(byte value, byte addressLow)
        {
            int total = value + addressLow;
            byte sum8 = (byte)total;
            byte b = B;
            bool subtract = (value & 0x80) != 0;
            byte flags = (byte)((b & SFMask) |
                                ((PC >> 8) & (YFMask | XFMask)) |
                                (subtract ? NFMask : 0));

            byte parityInput = (byte)((sum8 & 0x07) ^ b);
            if (total > 0xFF)
            {
                flags |= CFMask;
                if (subtract)
                {
                    if ((b & 0x0F) == 0)
                    {
                        flags |= HFMask;
                    }

                    parityInput ^= (byte)((b - 1) & 0x07);
                }
                else
                {
                    if ((b & 0x0F) == 0x0F)
                    {
                        flags |= HFMask;
                    }

                    parityInput ^= (byte)((b + 1) & 0x07);
                }
            }
            else
            {
                parityInput ^= (byte)(b & 0x07);
            }

            if (EvenParityTable[parityInput])
            {
                flags |= PFMask;
            }

            F = flags;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetBlockIoFlags(byte b, byte val, byte sum8, bool carry)
        {
            byte flags = (byte)(b & (SFMask | YFMask | XFMask));
            if (b == 0)
            {
                flags |= ZFMask;
            }

            if (carry)
            {
                flags |= HFMask | CFMask;
            }

            if (EvenParityTable[(byte)((sum8 & 0x07) ^ b)])
            {
                flags |= PFMask;
            }

            if ((val & 0x80) != 0)
            {
                flags |= NFMask;
            }

            F = flags;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DAA()
        {
            // Correction depends on the pre-adjust accumulator and H/N/C flags. Recomputing
            // from the adjusted result gives incorrect half-carry after subtraction.
            byte oldA = A;
            byte oldFlags = F;
            byte correction = 0;
            bool halfCarry = (oldFlags & HFMask) != 0;
            bool carry = (oldFlags & CFMask) != 0;
            bool subtraction = (oldFlags & NFMask) != 0;

            if ((oldA & 0x0F) > 0x09 || halfCarry)
            {
                correction += 0x06;
            }

            if (oldA > 0x99 || carry)
            {
                correction += 0x60;
                carry = true;
            }

            if (subtraction)
            {
                halfCarry = halfCarry && (oldA & 0x0F) < 0x06;
                A = (byte)(oldA - correction);
            }
            else
            {
                halfCarry = (oldA & 0x0F) > 0x09;
                A = (byte)(oldA + correction);
            }

            F = (byte)(SzpxyFlagsTable[A]
                | (carry ? CFMask : 0)
                | (halfCarry ? HFMask : 0)
                | (subtraction ? NFMask : 0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ushort Displace(ushort base_addr, sbyte displacement)
        {
            short addr = (short)((short)base_addr + (short)displacement);
            MemPtr = (ushort)addr;
            return (ushort)addr;
        }
        internal void ProcessInterrupts()
        {
            // EI exposes interrupts only after one further instruction has completed.
            if (IffDelay > 0)
            {
                IffDelay -= 1;
                if (IffDelay == 0)
                {
                    IFF1 = true;
                    IFF2 = true;
                }
                if (IffDelay > 0)
                {
                    return;
                }
            }

            if (NmiPending)
            {
                NmiPending = false;
                Halted = false;
                IFF1 = false;
                IncR();

                BeginInstruction(11, 0);
                InternalCycle(5);
                Call(0x66);
                EndInstruction();
                return;
            }

            if (IntPending && IFF1)
            {
                IntPending = false;
                Halted = false;
                IFF1 = false;
                IFF2 = false;
                IncR();

                switch (InterruptMode)
                {
                    case 0:
                        ConsumeCycles(4);
                        ExecOpcode(IntData);
                        break;

                    case 1:
                        BeginInstruction(13, 0);
                        InternalCycle(7);
                        Call(0x38);
                        EndInstruction();
                        break;

                    case 2:
                        BeginInstruction(19, 0);
                        InternalCycle(7);
                        Call(ReadWord((ushort)((I << 8) | IntData)));
                        EndInstruction();
                        break;

                    default:
                        Console.Error.Write("unsupported interrupt mode {0:D}\n", InterruptMode);
                        break;
                }

                return;
            }
        }

        /// <summary>
        /// Services an interrupt which an external machine clock asserted during
        /// the instruction that has just completed, without fetching another opcode.
        /// </summary>
        /// <remarks>
        /// Spectrum timing raises interrupt lines from inside its T-state consumer.
        /// CPU-driven machines such as the ZX81 instead detect the horizontal NMI
        /// boundary immediately after a complete instruction and use this boundary
        /// entry point. It is intentionally internal to the core assembly.
        /// </remarks>
        internal void ServicePendingInterruptsAtBoundary()
        {
            if (IffDelay != 0 || NmiPending || (IntPending && IFF1))
            {
                ProcessInterrupts();
            }
        }

        /// <summary>Resets CPU-owned state without altering memory or peripheral devices.</summary>
        /// <remarks>Undefined hardware registers receive deterministic values for reproducible tests.</remarks>
        public void Z80Init()
        {
            Cyc = 0;

            PC = 0;
            SP = 0xFFFF;
            IX = 0;
            IY = 0;
            MemPtr = 0;
            
            A = 0xFF;
            B = 0;
            C = 0;
            D = 0;
            E = 0;
            H = 0;
            L = 0;

            A_ = 0;
            B_ = 0;
            C_ = 0;
            D_ = 0;
            E_ = 0;
            H_ = 0;
            L_ = 0;
            F_ = 0;

            I = 0;
            R = 0;

            _f = 0xFF;
            _q = 0;
            _lastQ = 0;

            IffDelay = 0;
            InterruptMode = 0;
            IFF1 = false;
            IFF2 = false;
            Halted = false;
            IntPending = false;
            NmiPending = false;
            IntData = 0;
        }

        /// <summary>Executes one instruction boundary, including HALT refresh and pending interrupts.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Z80Step()
        {
            // The NMOS Zilog model snapshots Q at every opcode boundary. If the
            // opcode does not write F, Q remains zero for the following SCF/CCF.
            _lastQ = _q;
            _q = 0;

            if (Halted)
            {
                BeginInstruction(4, 0);
                _ = _memory.Read(PC);
                ConsumeCycles(4);
                IncR();
                EndInstruction();
            }
            else
            {
                byte opcode = FetchOpcode();
                ExecOpcode(opcode);
            }

            // INT is sampled at the instruction boundary. Advance the ULA and its
            // interrupt line before consulting the pending interrupt latch.
            FlushBatchedInstructionTstatesAtBoundary();
            if (IffDelay != 0 || NmiPending || (IntPending && IFF1))
            {
                // An asynchronously requested NMI or an already asserted INT can
                // require service before the cached ULA deadline is reached.
                FlushBatchedInstructionTstates();
                ProcessInterrupts();
                FlushBatchedInstructionTstates();
            }
        }

        /// <summary>Latches an NMI request for the next instruction boundary.</summary>
        public void Z80GenNMI()
        {
            NmiPending = true;
        }

        /// <summary>Latches an INT request and the data-bus byte used by interrupt modes 0 and 2.</summary>
        public void Z80GenINT(byte data)
        {
            IntPending = true;
            IntData = data;
        }
        public void Z80SetINTLine(bool active, byte data = 0xFF)
        {
            if (active)
            {
                IntPending = true;
                IntData = data;
            }
            else
            {
                IntPending = false;
            }
        }
    }
}
