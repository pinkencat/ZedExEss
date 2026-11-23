using ZedExEss.Z80CPU;
using ZedExEss.ZxNext.DivMmc;
using ZedExEss.ZxNext.Memory;
using ZedExEss.ZxNext.Multiface;
using ZedExEss.ZxNext.Ports;

namespace ZedExEss.ZxNext.Core;

/// <summary>
/// The Next's T80N: the shared Z80 core closed over the Next's buses, following the T80N
/// where it differs from a Zilog Z80, with the Z80N instructions.
/// </summary>
/// <remarks>
/// The instruction behaviour and bus cycles follow the pinned core's <c>t80n.vhd</c>,
/// <c>t80n_mcode.vhd</c> and <c>t80n_alu.vhd</c>; see <c>docs/zx-next/z80n-implementation-notes.md</c>.
/// </remarks>
public sealed class ZxNextCpu : Z80Core<ZxNextMemoryBus, ZxNextPortBus>
{
    private readonly ZxNextRegisterFile _registers;
    private readonly ZxNextDivMmcDevice _divMmc;
    private readonly ZxNextMultifaceDevice _multiface;
    private ZxNextNmiSource _nmiSource;
    private ZxNextInterruptController? _interrupts;

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
        UsesT80nRules = true;

        // z80_int_n is pulse_int_n AND im2_int_n, a level the T80N registers every clock
        // (FIX-INT-2): the 32/36-clock ULA pulse re-interrupts a handler that returns inside it.
        MaskableInterruptIsLevel = true;
    }

    internal void ConfigureInterrupts(ZxNextInterruptController interrupts)
    {
        _interrupts = interrupts ?? throw new ArgumentNullException(nameof(interrupts));
    }

    /// <summary>The hardware IM2 vector, or $FF, chosen at the acknowledge.</summary>
    protected override byte OnMaskableInterruptAcknowledge(byte busData) =>
        _interrupts?.Acknowledge() ?? busData;

    /// <remarks>im2_control.vhd tracks the IM the CPU last executed; INT is qualified by IM 2.</remarks>
    protected override void OnInterruptModeChanged() => _interrupts?.OnInterruptModeChanged();

    /// <remarks>The RETI ends a hardware IM2 service at its opcode fetch.</remarks>
    protected override void OnRetiDecoded()
    {
        if (_interrupts is null)
        {
            return;
        }

        SynchronizeBatchedInstructionTstates();
        _interrupts.OnRetiDecoded();
    }

    /// <summary>Captures which device an NMI given to the CPU and not yet acknowledged belongs to.</summary>
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
        // The devices were told when the NMI was latched; DivMMC pages in at the $0066 fetch.
        _nmiSource = ZxNextNmiSource.None;
        return _registers.BeginNmiAcknowledge(returnAddress);
    }

    protected override bool TryTakeStacklessNmiReturn(out ushort returnAddress) =>
        _registers.TryTakeStacklessNmiReturn(out returnAddress);

    /// <remarks>
    /// The core's RETN decoder (im2_control.vhd) recognises ED 45 alone, at its opcode fetch,
    /// so the other RETN encodings release nothing and ED 45 releases before the stack is
    /// read. DivMMC is released only while the Multiface is inactive, which has to be decided
    /// before the Multiface lets go.
    /// </remarks>
    protected override void OnRetnDecoded(byte opcode)
    {
        if (opcode != 0x45)
        {
            return;
        }

        if (!_multiface.IsActive)
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
            0xB4 or 0xB6 or 0xB7 or 0xBC => 16,
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
                    // t80n_mcode.vhd interleaves the operand reads with the stack writes, and
                    // its sixth M-cycle is a read cycle with nothing read.
                    byte high = NextByte();
                    SP--;
                    WriteByte(SP, high);
                    byte low = NextByte();
                    SP--;
                    WriteByte(SP, low);
                    IdleReadCycle(PC);
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
                    // The write is requested as the fourth M-cycle starts; it and the fifth
                    // are read cycles with nothing read (t80n_mcode.vhd), contended as such.
                    IdleReadCycle(PC);
                    IdleReadCycle(PC);
                }
                break;
            case 0x92: // NEXTREG n,A
                {
                    byte register = NextByte();
                    SynchronizeBatchedInstructionTstates();
                    MachinePorts.WriteNextRegister(register, A);
                    IdleReadCycle(PC);
                    IdleReadCycle(PC);
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
            case 0xB6: // LDIRSCALE: its scaling is commented out of t80n.vhd, leaving LDIRX
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

    /// <remarks>OUTI's microcode without the B decrement: no flag changes.</remarks>
    private void ExecuteOutInB()
    {
        ushort hl = GetHL();
        InternalCycle(1);
        byte value = ReadByte(hl);
        WritePortByte(GetBC(), value);
        SetHL((ushort)(hl + 1));
    }

    /// <remarks>
    /// The flags are INC D's, but the microcode does not keep the carry as INC r does: C is
    /// the carry out of D + 1.
    /// </remarks>
    private void ExecuteLdws()
    {
        byte value = ReadByte(GetHL());
        WriteByte(GetDE(), value);
        L++;
        byte d = D;
        D = Inc(d);
        SetInstructionFlags((byte)((GetFlags() & 0xFE) | (d == 0xFF ? 0x01 : 0x00)));
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
            // With the write strobe suppressed, the destination M-cycle is a read cycle at DE.
            IdleReadCycle(de);
        }

        InternalCycle(de, 2);
        ushort remaining = (ushort)(GetBC() - 1);
        SetBC(remaining);
        SetDE((ushort)(de + 1));
        if (!patternSource)
        {
            SetHL((ushort)(decrementSource ? hl - 1 : hl + 1));
        }

        // LDI's flags. LDPIRX's microcode sets no ALU operation, so the default for its
        // opcode runs: B (as BC's decrement left it) OR the byte, not A + the byte.
        SetT80nBlockCopyFlags(patternSource ? (byte)(B | value) : (byte)(A + value), remaining);
        if (repeat && remaining != 0)
        {
            PC -= 2;
            InternalCycle(de, 5);
            SetBlockRepeatFlags();
            MemPtr = (ushort)(PC + 1);
        }
    }

    /// <summary>
    /// The T80N's block-copy flags (I_BT): bits 3 and 1 of the ALU result as flag bits 3 and 5,
    /// H = N = 0, P/V = BC != 0, S, Z and C kept.
    /// </summary>
    private void SetT80nBlockCopyFlags(byte aluResult, ushort remaining)
    {
        byte flags = (byte)((GetFlags() & 0xC1) | (aluResult & 0x08) | ((aluResult & 0x02) != 0 ? 0x20 : 0));
        if (remaining != 0)
        {
            flags |= 0x04;
        }

        SetInstructionFlags(flags);
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

    /// <remarks>
    /// t80n.vhd adds one to the 8-bit row H4H3 L7L6L5 H2H1H0 and leaves H7-H5 and L4-L0 alone,
    /// so row 255 wraps to row 0 of the same 8K rather than carrying into H5.
    /// </remarks>
    private static ushort PixelDown(ushort address)
    {
        int high = address >> 8;
        int low = address & 0xFF;
        int row = ((high & 0x18) << 3) | ((low & 0xE0) >> 2) | (high & 0x07);
        row = (row + 1) & 0xFF;
        int newHigh = (high & 0xE0) | ((row >> 3) & 0x18) | (row & 0x07);
        int newLow = ((row & 0x38) << 2) | (low & 0x1F);
        return (ushort)((newHigh << 8) | newLow);
    }
}
