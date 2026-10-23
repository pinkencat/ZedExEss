using System.Collections.Generic;
using System;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Tape
{
    /// <summary>
    /// Detects common tape edge-wait loops and advances them from edge to edge while preserving observable port writes.
    /// </summary>
    public sealed class TapeEdgeAccelerator(Z80 cpu, SpectrumMemory memory, Action<ushort, byte>? writePort = null)
    {
        private readonly Z80 _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        private readonly SpectrumMemory _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        private readonly Action<ushort, byte>? _writePort = writePort;
        // Detection results are cached by loop address because loader loops are
        // usually tight and stable once the pilot/data phase has started.
        private readonly Dictionary<ushort, uint> _unsupportedTailSignatures = [];
        private readonly Dictionary<ushort, DetectionCacheEntry> _detectedLoopSignatures = [];
        private ITapeEdgeSource? _edgeSource;
        private Func<bool>? _earHighProvider;
        private AccelerationMode _mode;
        private ushort _modePc;
        private ushort _modeTailStart;
        private ushort _detectedTailStart;
        private int _lastPulseIndex = -1;
        private bool _lengthKnown1;
        private bool _lengthLong1;
        private bool _lengthKnown2;
        private bool _lengthLong2;

        public long AccelerationCount { get; private set; }
        public ulong LastAccelerationCpuTstate { get; private set; }
        public void Configure(ITapeEdgeSource? edgeSource, Func<bool>? earHighProvider)
        {
            // A new tape source invalidates all loop assumptions. The same PC can contain
            // different loader code after a snapshot or program transition.
            _edgeSource = edgeSource;
            _earHighProvider = earHighProvider;
            _mode = AccelerationMode.None;
            _modeTailStart = 0;
            _detectedTailStart = 0;
            _lastPulseIndex = -1;
            _lengthKnown1 = false;
            _lengthLong1 = false;
            _lengthKnown2 = false;
            _lengthLong2 = false;
            AccelerationCount = 0;
            LastAccelerationCpuTstate = 0;
            _unsupportedTailSignatures.Clear();
            _detectedLoopSignatures.Clear();
        }
        public bool TryAccelerate()
        {
            if (_edgeSource == null || !_edgeSource.IsPlaying)
            {
                _mode = AccelerationMode.None;
                _modeTailStart = 0;
                _lastPulseIndex = -1;
                _lengthKnown1 = false;
                _lengthLong1 = false;
                _lengthKnown2 = false;
                _lengthLong2 = false;
                return false;
            }

            UpdateLengthFromEdge();

            if (_mode != AccelerationMode.None && _cpu.PC != _modePc)
            {
                // Acceleration is only valid while the CPU remains in the detected wait loop.
                _mode = AccelerationMode.None;
                _modeTailStart = 0;
            }

            if (_mode == AccelerationMode.None)
            {
                _mode = GetCachedAcceleration(_cpu.PC);
                _modePc = _cpu.PC;
                _modeTailStart = _detectedTailStart;
                if (_mode == AccelerationMode.None)
                {
                    return false;
                }
            }

            bool accelerated = false;
            if (_lengthKnown1)
            {
                // Most loaders classify the previous pulse by examining loop counter residue.
                // Seed B/C/flags to the values the detected loop would have produced at the edge.
                bool setBHigh = _lengthLong1 ^ (_mode == AccelerationMode.Decreasing);
                _cpu.B = setBHigh ? (byte)0xFE : (byte)0x00;

                bool earHigh = _earHighProvider?.Invoke() ?? false;
                _cpu.C = (byte)((_cpu.C & ~0x20) | (earHigh ? 0x00 : 0x20));

                _cpu.SetFlags((byte)(_cpu.GetFlags() | 0x01));

                int delta = _edgeSource.PeekNextEdgeDelta();
                if (delta > 0)
                {
                    // Skip wall-clock time to the next transition but let the tape source record
                    // that this edge was reached by acceleration for later length classification.
                    _edgeSource.MarkNextEdgeSemanticallyAccelerated();
                    _edgeSource.AdvanceToNextEdge(skipTime: true);

                    if (!TryExecutePostEdgeTail(_modeTailStart))
                    {
                        // If the loop tail is too complex to emulate safely, fall back to the
                        // standard return address rather than guessing at loader state.
                        ReturnFromAcceleratedRoutine();
                    }

                    ApplyLastEdgeFlags();
                    accelerated = true;
                }
            }

            _lengthKnown1 = _lengthKnown2;
            _lengthLong1 = _lengthLong2;
            if (accelerated)
            {
                AccelerationCount++;
                LastAccelerationCpuTstate = _cpu.Cyc;
            }

            return accelerated;
        }
        private AccelerationMode GetCachedAcceleration(ushort currentPc)
        {
            ushort scanStart = unchecked((ushort)(currentPc - 6));
            if (_detectedLoopSignatures.TryGetValue(currentPc, out DetectionCacheEntry cached))
            {
                // Cache entries are guarded by a small memory signature so self-modifying
                // loaders cannot reuse stale detections.
                uint cachedSignature = LoopSignature(scanStart);
                if (cached.ScanStart == scanStart && cached.Signature == cachedSignature)
                {
                    _detectedTailStart = cached.TailStart;
                    return cached.Mode;
                }

                _detectedLoopSignatures.Remove(currentPc);
            }

            AccelerationMode mode = DetectAcceleration(scanStart, currentPc);
            if (mode != AccelerationMode.None)
            {
                CacheDetectedLoop(currentPc, scanStart, LoopSignature(scanStart), mode, _detectedTailStart);
            }

            return mode;
        }
        private void CacheDetectedLoop(ushort currentPc, ushort scanStart, uint signature, AccelerationMode mode, ushort tailStart)
        {
            const int MaxCachedLoops = 256;
            if (_detectedLoopSignatures.Count >= MaxCachedLoops)
            {
                _detectedLoopSignatures.Clear();
            }

            _detectedLoopSignatures[currentPc] = new DetectionCacheEntry(scanStart, signature, mode, tailStart);
        }
        private void UpdateLengthFromEdge()
        {
            if (_edgeSource == null)
            {
                return;
            }

            int pulseIndex = _edgeSource.CurrentPulseIndex;
            if (pulseIndex == _lastPulseIndex)
            {
                return;
            }

            ApplyLastEdgeFlags();
        }
        private void ApplyLastEdgeFlags()
        {
            if (_edgeSource == null)
            {
                return;
            }

            if (_edgeSource.TryGetLastEdgeInfo(out int tstates, out bool isData, out bool isLong, out bool fromSemanticAcceleration))
            {
                if (TryClassifyPulseLength(tstates, isData, isLong, out bool classifiedLong))
                {
                    _lengthKnown2 = true;
                    _lengthLong2 = classifiedLong;
                }
                else
                {
                    _lengthKnown2 = false;
                }

                if (!fromSemanticAcceleration)
                {
                    _lengthKnown1 = false;
                }

                _lastPulseIndex = _edgeSource.CurrentPulseIndex;
            }
        }
        private bool TryClassifyPulseLength(int tstates, bool isData, bool isLong, out bool classifiedLong)
        {
            classifiedLong = false;
            if (isData)
            {
                classifiedLong = isLong;
                return true;
            }

            if (_edgeSource == null || !_edgeSource.TryGetDataPulseTimings(out int shortPulse, out int longPulse))
            {
                return false;
            }

            if (shortPulse <= 0 || longPulse <= shortPulse || tstates <= 0)
            {
                return false;
            }

            int spread = longPulse - shortPulse;
            int lowerBound = Math.Max(1, shortPulse - Math.Max(128, spread));
            int upperBound = longPulse + Math.Max(256, spread * 2);
            upperBound = Math.Max(upperBound, Math.Min(longPulse * 3, 3400));
            if (tstates < lowerBound || tstates > upperBound)
            {
                return false;
            }

            classifiedLong = tstates >= shortPulse + (spread / 2);
            return true;
        }
        private void ReturnFromAcceleratedRoutine()
        {
            ushort sp = _cpu.SP;
            byte lo = _memory.ReadDirect(sp);
            byte hi = _memory.ReadDirect(unchecked((ushort)(sp + 1)));
            _cpu.SP = unchecked((ushort)(sp + 2));
            _cpu.PC = (ushort)(lo | (hi << 8));
        }
        private bool TryExecutePostEdgeTail(ushort pc)
        {
            const int MaxInstructions = 32;
            ushort startPc = pc;
            if (TryExecuteSimplePostEdgeTail(startPc))
            {
                return true;
            }

            if (IsUnsupportedTailCached(startPc))
            {
                return false;
            }

            byte a = _cpu.A;
            byte b = _cpu.B;
            byte c = _cpu.C;
            byte d = _cpu.D;
            byte e = _cpu.E;
            byte h = _cpu.H;
            byte l = _cpu.L;
            byte flags = _cpu.GetFlags();
            ushort sp = _cpu.SP;
            int writeCount = 0;
            PortWrite write0 = default;
            PortWrite write1 = default;
            PortWrite write2 = default;
            PortWrite write3 = default;

            // The tail emulator is deliberately small: it handles the register/flag/OUT glue
            // immediately after known wait loops, then bails out on memory or control flow that
            // would require running a second CPU core inside the accelerator.
            bool Unsupported()
            {
                CacheUnsupportedTail(startPc);
                return false;
            }

            for (int instruction = 0; instruction < MaxInstructions; instruction++)
            {
                byte opcode = ReadTailByte(ref pc);

                if ((opcode & 0xC0) == 0x40 && opcode != 0x76)
                {
                    int destination = (opcode >> 3) & 0x07;
                    int source = opcode & 0x07;
                    if (destination == 6 || source == 6)
                    {
                        return Unsupported();
                    }

                    SetRegister(destination, GetRegister(source, a, b, c, d, e, h, l), ref a, ref b, ref c, ref d, ref e, ref h, ref l);
                    continue;
                }

                if ((opcode & 0xC7) == 0x06)
                {
                    int destination = (opcode >> 3) & 0x07;
                    if (destination == 6)
                    {
                        return Unsupported();
                    }

                    SetRegister(destination, ReadTailByte(ref pc), ref a, ref b, ref c, ref d, ref e, ref h, ref l);
                    continue;
                }

                if ((opcode & 0xC7) == 0x04)
                {
                    int register = (opcode >> 3) & 0x07;
                    if (register == 6)
                    {
                        return Unsupported();
                    }

                    byte original = GetRegister(register, a, b, c, d, e, h, l);
                    byte value = unchecked((byte)(original + 1));
                    bool carry = (flags & 0x01) != 0;
                    flags = IncFlags(original, value, carry);
                    SetRegister(register, value, ref a, ref b, ref c, ref d, ref e, ref h, ref l);
                    continue;
                }

                if ((opcode & 0xC7) == 0x05)
                {
                    int register = (opcode >> 3) & 0x07;
                    if (register == 6)
                    {
                        return Unsupported();
                    }

                    byte original = GetRegister(register, a, b, c, d, e, h, l);
                    byte value = unchecked((byte)(original - 1));
                    bool carry = (flags & 0x01) != 0;
                    flags = DecFlags(original, value, carry);
                    SetRegister(register, value, ref a, ref b, ref c, ref d, ref e, ref h, ref l);
                    continue;
                }

                switch (opcode)
                {
                    case 0x00:
                    case 0xF3:
                    case 0xFB:
                        break;

                    case 0x07:
                        {
                            bool newCarry = (a & 0x80) != 0;
                            a = (byte)((a << 1) | (newCarry ? 1 : 0));
                            flags = RotateAccumulatorFlags(a, flags, newCarry);
                            break;
                        }

                    case 0x0F:
                        {
                            bool newCarry = (a & 0x01) != 0;
                            a = (byte)((a >> 1) | (newCarry ? 0x80 : 0));
                            flags = RotateAccumulatorFlags(a, flags, newCarry);
                            break;
                        }

                    case 0x17:
                        {
                            bool oldCarry = (flags & 0x01) != 0;
                            bool newCarry = (a & 0x80) != 0;
                            a = (byte)((a << 1) | (oldCarry ? 1 : 0));
                            flags = RotateAccumulatorFlags(a, flags, newCarry);
                            break;
                        }

                    case 0x1F:
                        {
                            bool oldCarry = (flags & 0x01) != 0;
                            bool newCarry = (a & 0x01) != 0;
                            a = (byte)((a >> 1) | (oldCarry ? 0x80 : 0));
                            flags = RotateAccumulatorFlags(a, flags, newCarry);
                            break;
                        }

                    case 0x2F:
                        a = (byte)~a;
                        flags = (byte)((flags & 0xC5) | 0x12 | (a & 0x28));
                        break;

                    case 0x37:
                        flags = (byte)((flags & 0xC4) | 0x01 | (a & 0x28));
                        break;

                    case 0x3F:
                        {
                            bool oldCarry = (flags & 0x01) != 0;
                            flags = (byte)((flags & 0xC4) | (oldCarry ? 0x10 : 0x01) | (a & 0x28));
                            break;
                        }

                    case 0xA0:
                    case 0xA1:
                    case 0xA2:
                    case 0xA3:
                    case 0xA4:
                    case 0xA5:
                    case 0xA7:
                        a = (byte)(a & GetRegister(opcode & 0x07, a, b, c, d, e, h, l));
                        flags = LogicFlags(a, halfCarry: true);
                        break;

                    case 0xA8:
                    case 0xA9:
                    case 0xAA:
                    case 0xAB:
                    case 0xAC:
                    case 0xAD:
                    case 0xAF:
                        a = (byte)(a ^ GetRegister(opcode & 0x07, a, b, c, d, e, h, l));
                        flags = LogicFlags(a, halfCarry: false);
                        break;

                    case 0xB0:
                    case 0xB1:
                    case 0xB2:
                    case 0xB3:
                    case 0xB4:
                    case 0xB5:
                    case 0xB7:
                        a = (byte)(a | GetRegister(opcode & 0x07, a, b, c, d, e, h, l));
                        flags = LogicFlags(a, halfCarry: false);
                        break;

                    case 0xC0:
                    case 0xC8:
                    case 0xD0:
                    case 0xD8:
                    case 0xE0:
                    case 0xE8:
                    case 0xF0:
                    case 0xF8:
                        if (ConditionTrue((opcode >> 3) & 0x07, flags))
                        {
                            CommitTailState(a, b, c, d, e, h, l, flags, ReturnFromStack(ref sp), sp, writeCount, write0, write1, write2, write3);
                            return true;
                        }

                        break;

                    case 0xC2:
                    case 0xCA:
                    case 0xD2:
                    case 0xDA:
                    case 0xE2:
                    case 0xEA:
                    case 0xF2:
                    case 0xFA:
                        {
                            ushort address = ReadTailWord(ref pc);
                            if (ConditionTrue((opcode >> 3) & 0x07, flags))
                            {
                                pc = address;
                            }

                            break;
                        }

                    case 0xC3:
                        pc = ReadTailWord(ref pc);
                        break;

                    case 0xC9:
                        CommitTailState(a, b, c, d, e, h, l, flags, ReturnFromStack(ref sp), sp, writeCount, write0, write1, write2, write3);
                        return true;

                    case 0xD3:
                        {
                            byte portLow = ReadTailByte(ref pc);
                            ushort port = (ushort)((a << 8) | portLow);
                            AddUlaWrite(ref writeCount, ref write0, ref write1, ref write2, ref write3, port, a);
                            break;
                        }

                    case 0xE6:
                        a = (byte)(a & ReadTailByte(ref pc));
                        flags = LogicFlags(a, halfCarry: true);
                        break;

                    case 0xEE:
                        a = (byte)(a ^ ReadTailByte(ref pc));
                        flags = LogicFlags(a, halfCarry: false);
                        break;

                    case 0xF1:
                        {
                            byte lo = _memory.ReadDirect(sp);
                            byte hi = _memory.ReadDirect(unchecked((ushort)(sp + 1)));
                            sp = unchecked((ushort)(sp + 2));
                            flags = lo;
                            a = hi;
                            break;
                        }

                    case 0xF6:
                        a = (byte)(a | ReadTailByte(ref pc));
                        flags = LogicFlags(a, halfCarry: false);
                        break;

                    case 0x18:
                        pc = unchecked((ushort)(pc + (sbyte)ReadTailByte(ref pc)));
                        break;

                    case 0x20:
                    case 0x28:
                    case 0x30:
                    case 0x38:
                        {
                            sbyte displacement = (sbyte)ReadTailByte(ref pc);
                            int condition = (opcode >> 3) & 0x03;
                            if (ConditionTrue(condition, flags))
                            {
                                pc = unchecked((ushort)(pc + displacement));
                            }

                            break;
                        }

                    case 0xED:
                        {
                            byte edOpcode = ReadTailByte(ref pc);
                            if (!TryGetOutCRegisterValue(edOpcode, a, b, c, d, e, h, l, out byte value))
                            {
                                return Unsupported();
                            }

                            AddUlaWrite(ref writeCount, ref write0, ref write1, ref write2, ref write3, (ushort)((b << 8) | c), value);
                            break;
                        }

                    default:
                        return Unsupported();
                }
            }

            return Unsupported();
        }
        private bool TryExecuteSimplePostEdgeTail(ushort pc)
        {
            byte opcode = _memory.ReadDirect(pc);
            if (opcode == 0xC9)
            {
                ReturnFromAcceleratedRoutine();
                return true;
            }

            byte next = _memory.ReadDirect(unchecked((ushort)(pc + 1)));
            if ((opcode == 0x00 || opcode == 0xF3 || opcode == 0xFB) && next == 0xC9)
            {
                ReturnFromAcceleratedRoutine();
                return true;
            }

            byte a = _cpu.A;
            byte b = _cpu.B;
            byte c = _cpu.C;
            byte d = _cpu.D;
            byte e = _cpu.E;
            byte h = _cpu.H;
            byte l = _cpu.L;
            byte flags = _cpu.GetFlags();
            ushort sp = _cpu.SP;

            if (opcode == 0xF1 && next == 0xC9)
            {
                flags = _memory.ReadDirect(sp);
                a = _memory.ReadDirect(unchecked((ushort)(sp + 1)));
                sp = unchecked((ushort)(sp + 2));
                CommitTailState(a, b, c, d, e, h, l, flags, ReturnFromStack(ref sp), sp, 0, default, default, default, default);
                return true;
            }

            if (opcode == 0xD3 && _memory.ReadDirect(unchecked((ushort)(pc + 2))) == 0xC9)
            {
                byte portLow = next;
                ushort port = (ushort)((a << 8) | portLow);
                PortWrite write = (port & 0x0001) == 0 ? new PortWrite(port, a) : default;
                CommitTailState(a, b, c, d, e, h, l, flags, ReturnFromStack(ref sp), sp, (port & 0x0001) == 0 ? 1 : 0, write, default, default, default);
                return true;
            }

            if (opcode == 0xED && _memory.ReadDirect(unchecked((ushort)(pc + 2))) == 0xC9)
            {
                if (!TryGetOutCRegisterValue(next, a, b, c, d, e, h, l, out byte value))
                {
                    return false;
                }

                ushort port = (ushort)((b << 8) | c);
                PortWrite write = (port & 0x0001) == 0 ? new PortWrite(port, value) : default;
                CommitTailState(a, b, c, d, e, h, l, flags, ReturnFromStack(ref sp), sp, (port & 0x0001) == 0 ? 1 : 0, write, default, default, default);
                return true;
            }

            return false;
        }
        private AccelerationMode DetectAcceleration(ushort pc, ushort currentPc)
        {
            _detectedTailStart = 0;
            int state = 0;
            int count = 0;
            ushort target = unchecked((ushort)(currentPc - 4));
            byte targetLo = (byte)(target & 0xFF);
            byte targetHi = (byte)((target >> 8) & 0xFF);

            while (true)
            {
                byte b = _memory.ReadDirect(pc);
                pc = unchecked((ushort)(pc + 1));
                count++;
                switch (state)
                {
                    case 0:
                        state = b switch
                        {
                            0x03 => 28,
                            0x04 => 1,
                            _ => 13,
                        };
                        break;
                    case 1:
                        switch (b)
                        {
                            case 0x20: state = 40; break;
                            case 0xC8: state = 2; break;
                            default: return AccelerationMode.None;
                        }
                        break;
                    case 2:
                        if (b == 0x3E) state = 3; else return AccelerationMode.None;
                        break;
                    case 3:
                        switch (b)
                        {
                            case 0x00:
                            case 0x7F:
                            case 0xFF:
                                state = 4; break;
                            default:
                                return AccelerationMode.None;
                        }
                        break;
                    case 4:
                        if (b == 0xDB) state = 5; else return AccelerationMode.None;
                        break;
                    case 5:
                        if (b == 0xFE) state = 6; else return AccelerationMode.None;
                        break;
                    case 6:
                        switch (b)
                        {
                            case 0x1F: state = 7; break;
                            case 0xA9: state = 24; break;
                            default: return AccelerationMode.None;
                        }
                        break;
                    case 7:
                        switch (b)
                        {
                            case 0x00:
                            case 0xA7:
                            case 0xC8:
                            case 0xD0:
                                state = 8; break;
                            case 0xA9:
                                state = 9; break;
                            default:
                                return AccelerationMode.None;
                        }
                        break;
                    case 8:
                        if (b == 0xA9) state = 9; else return AccelerationMode.None;
                        break;
                    case 9:
                        if (b == 0xE6) state = 10; else return AccelerationMode.None;
                        break;
                    case 10:
                        if (b == 0x20) state = 11; else return AccelerationMode.None;
                        break;
                    case 11:
                        if (b == 0x28) state = 12; else return AccelerationMode.None;
                        break;
                    case 12:
                        if (b == (byte)(0x100 - count))
                        {
                            _detectedTailStart = pc;
                            return AccelerationMode.Increasing;
                        }

                        return AccelerationMode.None;

                    case 13:
                        state = 14; break;
                    case 14:
                        if (b == 0x05) state = 15; else return AccelerationMode.None;
                        break;
                    case 15:
                        if (b == 0xC8) state = 16; else return AccelerationMode.None;
                        break;
                    case 16:
                        if (b == 0xDB) state = 17; else return AccelerationMode.None;
                        break;
                    case 17:
                        if (b == 0xFE) state = 18; else return AccelerationMode.None;
                        break;
                    case 18:
                        if (b == 0xA9) state = 19; else return AccelerationMode.None;
                        break;
                    case 19:
                        if (b == 0xE6) state = 20; else return AccelerationMode.None;
                        break;
                    case 20:
                        if (b == 0x40) state = 21; else return AccelerationMode.None;
                        break;
                    case 21:
                        if (b == 0xCA) state = 22; else return AccelerationMode.None;
                        break;
                    case 22:
                        if (b == targetLo) state = 23; else return AccelerationMode.None;
                        break;
                    case 23:
                        if (b == targetHi)
                        {
                            _detectedTailStart = pc;
                            return AccelerationMode.Decreasing;
                        }

                        return AccelerationMode.None;

                    case 24:
                        if (b == 0xE6) state = 25; else return AccelerationMode.None;
                        break;
                    case 25:
                        if (b == 0x40) state = 26; else return AccelerationMode.None;
                        break;
                    case 26:
                        switch (b)
                        {
                            case 0x28: state = 12; break;
                            case 0xD8: state = 27; break;
                            default: return AccelerationMode.None;
                        }
                        break;
                    case 27:
                        if (b == 0x00) state = 11; else return AccelerationMode.None;
                        break;

                    case 28:
                        if (b == 0xC3) state = 29; else return AccelerationMode.None;
                        break;
                    case 29:
                        state = 30; break;
                    case 30:
                        state = 31; break;
                    case 31:
                        if (b == 0xDB) state = 32; else return AccelerationMode.None;
                        break;
                    case 32:
                        if (b == 0xFE) state = 33; else return AccelerationMode.None;
                        break;
                    case 33:
                        if (b == 0x1F) state = 34; else return AccelerationMode.None;
                        break;
                    case 34:
                        if (b == 0xC8) state = 35; else return AccelerationMode.None;
                        break;
                    case 35:
                        if (b == 0xA9) state = 36; else return AccelerationMode.None;
                        break;
                    case 36:
                        if (b == 0xE6) state = 37; else return AccelerationMode.None;
                        break;
                    case 37:
                        if (b == 0x20) state = 38; else return AccelerationMode.None;
                        break;
                    case 38:
                        if (b == 0x28) state = 39; else return AccelerationMode.None;
                        break;
                    case 39:
                        if (b == 0xF1 || b == 0xF3)
                        {
                            _detectedTailStart = pc;
                            return AccelerationMode.Increasing;
                        }

                        return AccelerationMode.None;

                    case 40:
                        if (b == 0x01) state = 41; else return AccelerationMode.None;
                        break;
                    case 41:
                        if (b == 0xC9) state = 31; else return AccelerationMode.None;
                        break;
                    default:
                        return AccelerationMode.None;
                }
            }
        }
        private byte ReadTailByte(ref ushort pc)
        {
            byte value = _memory.ReadDirect(pc);
            pc = unchecked((ushort)(pc + 1));
            return value;
        }
        private ushort ReadTailWord(ref ushort pc)
        {
            byte lo = ReadTailByte(ref pc);
            byte hi = ReadTailByte(ref pc);
            return (ushort)(lo | (hi << 8));
        }
        private ushort ReturnFromStack(ref ushort sp)
        {
            byte lo = _memory.ReadDirect(sp);
            byte hi = _memory.ReadDirect(unchecked((ushort)(sp + 1)));
            sp = unchecked((ushort)(sp + 2));
            return (ushort)(lo | (hi << 8));
        }
        private bool IsUnsupportedTailCached(ushort pc)
        {
            return _unsupportedTailSignatures.TryGetValue(pc, out uint signature)
                && signature == TailSignature(pc);
        }
        private void CacheUnsupportedTail(ushort pc)
        {
            _unsupportedTailSignatures[pc] = TailSignature(pc);
        }
        private uint TailSignature(ushort pc)
        {
            const int SignatureBytes = 24;
            return MemorySignature(pc, SignatureBytes);
        }
        private uint LoopSignature(ushort pc)
        {
            const int SignatureBytes = 32;
            return MemorySignature(pc, SignatureBytes);
        }
        private uint MemorySignature(ushort pc, int byteCount)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < byteCount; i++)
            {
                hash ^= _memory.ReadDirect(unchecked((ushort)(pc + i)));
                hash *= 16777619u;
            }

            return hash;
        }
        private void CommitTailState(
            byte a,
            byte b,
            byte c,
            byte d,
            byte e,
            byte h,
            byte l,
            byte flags,
            ushort pc,
            ushort sp,
            int writeCount,
            PortWrite write0,
            PortWrite write1,
            PortWrite write2,
            PortWrite write3)
        {
            _cpu.A = a;
            _cpu.B = b;
            _cpu.C = c;
            _cpu.D = d;
            _cpu.E = e;
            _cpu.H = h;
            _cpu.L = l;
            _cpu.SetFlags(flags);
            _cpu.PC = pc;
            _cpu.SP = sp;

            if (writeCount <= 0 || _writePort == null)
            {
                return;
            }

            if (writeCount >= 1)
            {
                _writePort(write0.Port, write0.Value);
            }

            if (writeCount >= 2)
            {
                _writePort(write1.Port, write1.Value);
            }

            if (writeCount >= 3)
            {
                _writePort(write2.Port, write2.Value);
            }

            if (writeCount >= 4)
            {
                _writePort(write3.Port, write3.Value);
            }
        }
        private static void AddUlaWrite(
            ref int writeCount,
            ref PortWrite write0,
            ref PortWrite write1,
            ref PortWrite write2,
            ref PortWrite write3,
            ushort port,
            byte value)
        {
            if ((port & 0x0001) != 0)
            {
                return;
            }

            switch (writeCount)
            {
                case 0:
                    write0 = new PortWrite(port, value);
                    writeCount = 1;
                    break;
                case 1:
                    write1 = new PortWrite(port, value);
                    writeCount = 2;
                    break;
                case 2:
                    write2 = new PortWrite(port, value);
                    writeCount = 3;
                    break;
                default:
                    write3 = new PortWrite(port, value);
                    writeCount = 4;
                    break;
            }
        }
        private static byte GetRegister(int register, byte a, byte b, byte c, byte d, byte e, byte h, byte l)
        {
            return register switch
            {
                0 => b,
                1 => c,
                2 => d,
                3 => e,
                4 => h,
                5 => l,
                7 => a,
                _ => throw new InvalidOperationException("Unsupported memory register operand.")
            };
        }
        private static void SetRegister(int register, byte value, ref byte a, ref byte b, ref byte c, ref byte d, ref byte e, ref byte h, ref byte l)
        {
            switch (register)
            {
                case 0: b = value; break;
                case 1: c = value; break;
                case 2: d = value; break;
                case 3: e = value; break;
                case 4: h = value; break;
                case 5: l = value; break;
                case 7: a = value; break;
                default: throw new InvalidOperationException("Unsupported memory register operand.");
            }
        }
        private static bool TryGetOutCRegisterValue(byte opcode, byte a, byte b, byte c, byte d, byte e, byte h, byte l, out byte value)
        {
            switch (opcode)
            {
                case 0x41: value = b; return true;
                case 0x49: value = c; return true;
                case 0x51: value = d; return true;
                case 0x59: value = e; return true;
                case 0x61: value = h; return true;
                case 0x69: value = l; return true;
                case 0x71: value = 0; return true;
                case 0x79: value = a; return true;
                default:
                    value = 0;
                    return false;
            }
        }
        private static bool ConditionTrue(int condition, byte flags)
        {
            return condition switch
            {
                0 => (flags & 0x40) == 0,
                1 => (flags & 0x40) != 0,
                2 => (flags & 0x01) == 0,
                3 => (flags & 0x01) != 0,
                4 => (flags & 0x04) == 0,
                5 => (flags & 0x04) != 0,
                6 => (flags & 0x80) == 0,
                7 => (flags & 0x80) != 0,
                _ => false
            };
        }
        private static byte LogicFlags(byte value, bool halfCarry)
        {
            byte flags = (byte)(value & 0xA8);
            if (value == 0)
            {
                flags |= 0x40;
            }

            if (EvenParity(value))
            {
                flags |= 0x04;
            }

            if (halfCarry)
            {
                flags |= 0x10;
            }

            return flags;
        }
        private static byte IncFlags(byte original, byte value, bool carry)
        {
            byte flags = (byte)(value & 0xA8);
            if (value == 0) flags |= 0x40;
            if ((original & 0x0F) == 0x0F) flags |= 0x10;
            if (original == 0x7F) flags |= 0x04;
            if (carry) flags |= 0x01;
            return flags;
        }
        private static byte DecFlags(byte original, byte value, bool carry)
        {
            byte flags = (byte)((value & 0xA8) | 0x02);
            if (value == 0) flags |= 0x40;
            if ((original & 0x0F) == 0x00) flags |= 0x10;
            if (original == 0x80) flags |= 0x04;
            if (carry) flags |= 0x01;
            return flags;
        }
        private static byte RotateAccumulatorFlags(byte value, byte oldFlags, bool carry)
        {
            return (byte)((oldFlags & 0xC4) | (value & 0x28) | (carry ? 0x01 : 0x00));
        }
        private static bool EvenParity(byte value)
        {
            value ^= (byte)(value >> 4);
            value ^= (byte)(value >> 2);
            value ^= (byte)(value >> 1);
            return (value & 1) == 0;
        }
        private readonly struct PortWrite(ushort port, byte value)
        {
            public ushort Port { get; } = port;
            public byte Value { get; } = value;
        }
        private readonly struct DetectionCacheEntry(ushort scanStart, uint signature, TapeEdgeAccelerator.AccelerationMode mode, ushort tailStart)
        {
            public ushort ScanStart { get; } = scanStart;
            public uint Signature { get; } = signature;
            public AccelerationMode Mode { get; } = mode;
            public ushort TailStart { get; } = tailStart;
        }
        private enum AccelerationMode
        {
            None,
            Increasing,
            Decreasing
        }
    }
}
