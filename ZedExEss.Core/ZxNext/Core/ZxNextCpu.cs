using ZedExEss.Z80CPU;
using ZedExEss.ZxNext.DivMmc;
using ZedExEss.ZxNext.Memory;
using ZedExEss.ZxNext.Multiface;
using ZedExEss.ZxNext.Ports;

namespace ZedExEss.ZxNext.Core;

internal enum ZxNextNmiSource
{
    None,
    DivMmc,
    Multiface
}

/// <summary>
/// Shared Z80 core closed over concrete Next buses. Z80N decoding is added in Phase 2.
/// </summary>
public sealed class ZxNextCpu : Z80Core<ZxNextMemoryBus, ZxNextPortBus>
{
    private readonly ZxNextRegisterFile _registers;
    private readonly ZxNextDivMmcDevice _divMmc;
    private readonly ZxNextMultifaceDevice _multiface;
    private ZxNextNmiSource _nmiSource;

    public ZxNextCpu(
        ZxNextMemoryBus memory,
        ZxNextPortBus ports,
        ZxNextRegisterFile registers,
        ZxNextDivMmcDevice divMmc,
        ZxNextMultifaceDevice multiface)
        : base(memory, ports)
    {
        _registers = registers ?? throw new ArgumentNullException(nameof(registers));
        _divMmc = divMmc ?? throw new ArgumentNullException(nameof(divMmc));
        _multiface = multiface ?? throw new ArgumentNullException(nameof(multiface));
    }

    /// <summary>Captures which device an asserted NMI belongs to.</summary>
    public ZxNextNmiRoutingState CaptureNmiRoutingState() => new((byte)_nmiSource);

    /// <summary>Puts the pending NMI's owner back.</summary>
    public void RestoreNmiRoutingState(ZxNextNmiRoutingState state)
    {
        _nmiSource = (ZxNextNmiSource)state.PendingNmiSource;
    }

    internal void RequestNmi(ZxNextNmiSource source)
    {
        _nmiSource = source;
        Z80GenNMI();
    }

    protected override bool OnNmiAcknowledge(ushort returnAddress)
    {
        if (_nmiSource == ZxNextNmiSource.DivMmc)
        {
            _divMmc.BeginNmi();
        }

        _nmiSource = ZxNextNmiSource.None;
        return _registers.BeginNmiAcknowledge(returnAddress);
    }

    protected override bool TryTakeStacklessNmiReturn(out ushort returnAddress) =>
        _registers.TryTakeStacklessNmiReturn(out returnAddress);

    protected override void OnRetnExecuted()
    {
        // The core suppresses DivMMC's RETN release while Multiface owns the
        // NMI service window. Capture that condition before Multiface unmaps.
        if (!_multiface.IsMapped && !_multiface.NmiActive)
        {
            _divMmc.OnRetnExecuted();
        }

        _multiface.CompleteNmi();
    }

    protected override int GetExtendedEdOpcodeCycles(byte opcode)
    {
        return opcode switch
        {
            0x23 or 0x24 => 8,
            0x27 => 11,
            >= 0x28 and <= 0x2C => 8,
            >= 0x30 and <= 0x33 => 8,
            >= 0x34 and <= 0x36 => 16,
            0x8A => 23,
            0x90 => 16,
            0x91 => 20,
            0x92 => 17,
            0x93 or 0x94 or 0x95 => 8,
            0x98 => 13,
            0xA4 => 16,
            0xA5 => 14,
            0xAC => 16,
            0xB4 or 0xB7 or 0xBC => 16,
            _ => 0
        };
    }

    protected override void ExecuteExtendedEdOpcode(byte opcode)
    {
        switch (opcode)
        {
            case 0x23: // SWAPNIB
                A = (byte)((A << 4) | (A >> 4));
                break;
            case 0x24: // MIRROR A
                A = ReverseBits(A);
                break;
            case 0x27: // TEST n
                ApplyTestFlags(NextByte());
                break;
            case 0x28: // BSLA DE,B
                SetDE((ushort)(GetDE() << (B & 0x1F)));
                break;
            case 0x29: // BSRA DE,B
                SetDE(unchecked((ushort)((short)GetDE() >> (B & 0x1F))));
                break;
            case 0x2A: // BSRL DE,B
                SetDE((ushort)((uint)GetDE() >> (B & 0x1F)));
                break;
            case 0x2B: // BSRF DE,B
                SetDE(unchecked((ushort)~((ushort)~GetDE() >> (B & 0x1F))));
                break;
            case 0x2C: // BRLC DE,B
                SetDE(RotateLeft16(GetDE(), B & 0x0F));
                break;
            case 0x30: // MUL D,E
                SetDE((ushort)(D * E));
                break;
            case 0x31: // ADD HL,A
                SetHL((ushort)(GetHL() + A));
                ClearCarryAsPinnedCoreDoes();
                break;
            case 0x32: // ADD DE,A
                SetDE((ushort)(GetDE() + A));
                ClearCarryAsPinnedCoreDoes();
                break;
            case 0x33: // ADD BC,A
                SetBC((ushort)(GetBC() + A));
                ClearCarryAsPinnedCoreDoes();
                break;
            case 0x34: // ADD HL,nn
                SetHL((ushort)(GetHL() + NextWord()));
                break;
            case 0x35: // ADD DE,nn
                SetDE((ushort)(GetDE() + NextWord()));
                break;
            case 0x36: // ADD BC,nn
                SetBC((ushort)(GetBC() + NextWord()));
                break;
            case 0x8A: // PUSH nn (the immediate is uniquely encoded high, low)
                {
                    byte high = NextByte();
                    byte low = NextByte();
                    PushWord((ushort)((high << 8) | low));
                }
                break;
            case 0x90: // OUTINB
                ExecuteOutInB();
                break;
            case 0x91: // NEXTREG n,n
                {
                    byte register = NextByte();
                    byte value = NextByte();
                    SynchronizeBatchedInstructionTstates();
                    MachinePorts.WriteNextRegister(register, value);
                }
                break;
            case 0x92: // NEXTREG n,A
                {
                    byte register = NextByte();
                    SynchronizeBatchedInstructionTstates();
                    MachinePorts.WriteNextRegister(register, A);
                }
                break;
            case 0x93: // PIXELDN
                SetHL(PixelDown(GetHL()));
                break;
            case 0x94: // PIXELAD
                SetHL((ushort)(0x4000 | ((D & 0xC0) << 5) | ((D & 0x07) << 8)
                    | ((D & 0x38) << 2) | (E >> 3)));
                break;
            case 0x95: // SETAE
                A = (byte)(0x80 >> (E & 0x07));
                break;
            case 0x98: // JP (C)
                PC = (ushort)((PC & 0xC000) | (ReadPortByte(GetBC()) << 6));
                break;
            case 0xA4: // LDIX
                ExecuteExtendedTransfer(decrementSource: false, repeat: false, patternSource: false);
                break;
            case 0xA5: // LDWS
                ExecuteLdws();
                break;
            case 0xAC: // LDDX
                ExecuteExtendedTransfer(decrementSource: true, repeat: false, patternSource: false);
                break;
            case 0xB4: // LDIRX
                ExecuteExtendedTransfer(decrementSource: false, repeat: true, patternSource: false);
                break;
            case 0xB7: // LDPIRX
                ExecuteExtendedTransfer(decrementSource: false, repeat: true, patternSource: true);
                break;
            case 0xBC: // LDDRX
                ExecuteExtendedTransfer(decrementSource: true, repeat: true, patternSource: false);
                break;
            default:
                throw new InvalidOperationException($"Unhandled Z80N ED opcode {opcode:X2}.");
        }
    }

    private void ApplyTestFlags(byte operand)
    {
        byte result = (byte)(A & operand);
        byte flags = (byte)(0x10 | (result & 0xA8));
        if (result == 0)
        {
            flags |= 0x40;
        }

        if (Parity(result))
        {
            flags |= 0x04;
        }

        SetInstructionFlags(flags);
    }

    private void ClearCarryAsPinnedCoreDoes()
    {
        // t80n.vhd assigns a truncated 16-bit sum before reading temporary bit 16,
        // so core 3.02.03 clears C rather than reporting arithmetic carry.
        SetInstructionFlags((byte)(GetFlags() & 0xFE));
    }

    private void ExecuteOutInB()
    {
        ushort hl = GetHL();
        InternalCycle(1);
        byte value = ReadByte(hl);
        WritePortByte(GetBC(), value);
        SetHL((ushort)(hl + 1));

        byte sum = (byte)(value + L);
        SetBlockIoFlags(B, value, sum, sum < value);
    }

    private void ExecuteLdws()
    {
        byte value = ReadByte(GetHL());
        WriteByte(GetDE(), value);
        L++;
        D = Inc(D);
    }

    private void ExecuteExtendedTransfer(bool decrementSource, bool repeat, bool patternSource)
    {
        ushort hl = GetHL();
        ushort de = GetDE();
        ushort source = patternSource ? (ushort)((hl & 0xFFF8) | (de & 0x0007)) : hl;
        byte value = ReadByte(source);

        if (value != A)
        {
            WriteByte(de, value);
        }
        else
        {
            // The destination memory cycle still occupies three T-states when the
            // compare suppresses its write strobe.
            InternalCycle(de, 3);
        }

        InternalCycle(de, 2);
        ushort remaining = (ushort)(GetBC() - 1);
        SetBC(remaining);
        SetDE((ushort)(de + 1));
        if (!patternSource)
        {
            SetHL((ushort)(decrementSource ? hl - 1 : hl + 1));
        }

        SetBlockTransferFlags(value, remaining);
        if (repeat && remaining != 0)
        {
            PC -= 2;
            InternalCycle(de, 5);
            SetBlockRepeatFlags();
            MemPtr = (ushort)(PC + 1);
        }
    }

    private static byte ReverseBits(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        return (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
    }

    private static ushort RotateLeft16(ushort value, int count)
    {
        return count == 0 ? value : (ushort)((value << count) | (value >> (16 - count)));
    }

    private static ushort PixelDown(ushort address)
    {
        if ((address & 0x0700) != 0x0700)
        {
            return (ushort)(address + 0x0100);
        }

        if ((address & 0x00E0) != 0x00E0)
        {
            return (ushort)((address & 0xF8FF) + 0x0020);
        }

        return (ushort)((address & 0xF81F) + 0x0800);
    }
}
