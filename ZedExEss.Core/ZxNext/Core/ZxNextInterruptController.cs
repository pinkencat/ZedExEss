namespace ZedExEss.ZxNext.Core;

/// <summary>
/// The Next's 14 interrupting peripherals in priority order (<c>zxnext.vhd</c>'s
/// <c>im2_int_req</c>): the index is the hardware IM2 vector's bits 4-1.
/// </summary>
public enum ZxNextInterruptSource : byte
{
    Line = 0,
    Uart0Receive = 1,
    Uart1Receive = 2,
    Ctc0 = 3,
    Ctc1 = 4,
    Ctc2 = 5,
    Ctc3 = 6,
    Ctc4 = 7,
    Ctc5 = 8,
    Ctc6 = 9,
    Ctc7 = 10,
    Ula = 11,
    Uart0Transmit = 12,
    Uart1Transmit = 13
}

/// <summary>
/// The Next's maskable interrupts: the pulse that every Spectrum has, and the hardware IM2
/// mode of NextReg $C0 bit 0, with its prioritised daisy chain of peripherals.
/// </summary>
/// <remarks>
/// <para>
/// A transcription of <c>peripherals.vhd</c>, <c>im2_peripheral.vhd</c>, <c>im2_device.vhd</c>
/// and the interrupt lines of <c>zxnext.vhd</c> (core 3.02.03, <c>b047011</c>):
/// </para>
/// <list type="bullet">
/// <item>A source's request edge sets its status bit (in any mode, enabled or not, so it can
/// be polled). In pulse mode, when enabled, it starts the 32/36-clock INT pulse. In hardware
/// IM2 mode, when enabled, it latches a pending request, which only the RETI that ends its
/// service clears.</item>
/// <item>A pending device requests (S_REQ). INT is asserted for the highest-priority one while
/// no device above it is requesting or in service, and only while the CPU is in IM 2.</item>
/// <item>The acknowledge puts <c>($C0 &amp; $E0) | index * 2</c> on the bus and the device is in
/// service (S_ACK, S_ISR) until a RETI decoded while the CPU is in IM 2 reaches it: the
/// highest-priority device in service, as devices that are only requesting pass the chain
/// while a RETI is being decoded. RETN ends no service.</item>
/// <item>Any acknowledge while a device is requesting or in service reads that base vector;
/// otherwise the bus holds $FF.</item>
/// <item>The ULA is the one exception: in hardware IM2 mode it still pulses while the CPU is
/// not in IM 2, so an IM 1 program keeps its frame interrupt.</item>
/// <item>NextReg $20 writes are unqualified requests (bit 7 line, 6 ULA, 3-0 CTC 0-3): they
/// ignore the enables.</item>
/// <item>A requesting or in-service device whose $CC-$CE bit is set holds the DMA off, so the
/// CPU can take the interrupt between two of its bytes; so does an active NMI with $CC bit 7.
/// The hold lasts into the RETI's first stack read (<c>im2_control.vhd</c>'s dma_delay).</item>
/// </list>
/// <para>
/// The register file keeps NextReg $C0 (vector bits 7-5, the mode in bit 0); this controller
/// keeps $C6 and $CC-$CE, the status, pending and in-service bits. The CTC's channels request
/// on their zero counts, and their interrupt enables (NextReg $C5) are the CTC's control bit 7.
/// There is no UART yet, so its sources only ever request through NextReg $20.
/// </para>
/// </remarks>
public sealed class ZxNextInterruptController
{
    private const int SourceCount = 14;
    private const ushort AllSources = (1 << SourceCount) - 1;
    private const ushort LineBit = 1 << (int)ZxNextInterruptSource.Line;
    private const ushort UlaBit = 1 << (int)ZxNextInterruptSource.Ula;

    private readonly ZxNextRegisterFile _registers;
    private ZxNextCpu? _cpu;
    private ZxNextUlaTiming? _ulaTiming;
    private ZxNextClockCoordinator? _clock;

    private ushort _status;
    private ushort _pending;
    private ushort _inService;
    private bool _im2Line;
    private byte _uartEnables;
    private byte _dmaEnables0;
    private byte _dmaEnablesCtc;
    private byte _dmaEnablesUart;
    private ulong _retiHoldOffEndTick;

    internal ZxNextInterruptController(ZxNextRegisterFile registers)
    {
        _registers = registers ?? throw new ArgumentNullException(nameof(registers));
    }

    internal void Configure(ZxNextCpu cpu, ZxNextUlaTiming ulaTiming, ZxNextClockCoordinator clock)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _ulaTiming = ulaTiming ?? throw new ArgumentNullException(nameof(ulaTiming));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>NextReg $C0 bit 0: hardware IM2 mode rather than the pulse.</summary>
    public bool HardwareMode => _registers.HardwareInterruptMode;

    /// <summary>The status bits as NextRegs $20 and $C8-$CA read them: status OR pending.</summary>
    public ushort Status => (ushort)(_status | _pending);

    /// <summary>Hardware IM2 requests latched and not yet serviced, one bit per source.</summary>
    public ushort Pending => _pending;

    /// <summary>Sources acknowledged and not yet ended by a RETI, one bit per source.</summary>
    public ushort InService => _inService;

    /// <summary>True while a hardware IM2 request is driving INT.</summary>
    public bool Im2InterruptAsserted => _im2Line;

    private bool CpuInIm2 => (_cpu?.InterruptModeValue ?? 0) == 2;

    private ushort Requesting => (ushort)(_pending & ~_inService);

    // --- Sources --------------------------------------------------------------------------

    /// <summary>
    /// A request edge from a source. The ULA and line interrupts arrive only while enabled,
    /// as <c>zxula_timing.vhd</c> gates them; the other sources' status latches either way.
    /// </summary>
    internal void Request(ZxNextInterruptSource source, ulong tick)
    {
        ushort bit = (ushort)(1 << (int)source);
        _status |= bit;
        if (!IsEnabled(source))
        {
            return;
        }

        if (HardwareMode)
        {
            _pending |= bit;
            if (bit == UlaBit && !CpuInIm2)
            {
                StartPulse(tick);
            }

            RefreshCpuLine();
        }
        else
        {
            StartPulse(tick);
        }
    }

    private bool IsEnabled(ZxNextInterruptSource source) => source switch
    {
        ZxNextInterruptSource.Line => _registers.LineInterruptEnabled,
        ZxNextInterruptSource.Ula => _registers.UlaInterruptEnabled,
        >= ZxNextInterruptSource.Ctc0 and <= ZxNextInterruptSource.Ctc3 =>
            _registers.Ctc.InterruptEnabled(source - ZxNextInterruptSource.Ctc0),
        ZxNextInterruptSource.Uart0Receive => (_uartEnables & 0x03) != 0,
        ZxNextInterruptSource.Uart1Receive => (_uartEnables & 0x30) != 0,
        ZxNextInterruptSource.Uart0Transmit => (_uartEnables & 0x04) != 0,
        ZxNextInterruptSource.Uart1Transmit => (_uartEnables & 0x40) != 0,
        _ => false
    };

    /// <summary>NextReg $20 write: unqualified requests, which ignore the enables.</summary>
    internal void WriteUnqualified(byte value)
    {
        ushort mask = (ushort)(((value & 0x80) != 0 ? LineBit : 0)
            | ((value & 0x40) != 0 ? UlaBit : 0)
            | ((value & 0x0F) << (int)ZxNextInterruptSource.Ctc0));
        if (mask == 0)
        {
            return;
        }

        ulong tick = _clock?.MasterTicks ?? 0;
        _status |= mask;
        if (HardwareMode)
        {
            _pending |= mask;
            if ((mask & UlaBit) != 0 && !CpuInIm2)
            {
                StartPulse(tick);
            }

            RefreshCpuLine();
        }
        else
        {
            StartPulse(tick);
        }
    }

    private void StartPulse(ulong tick) => _ulaTiming?.StartInterruptPulse(tick);

    // --- The CPU --------------------------------------------------------------------------

    /// <summary>
    /// INT as the CPU sees it: zxnext.vhd's <c>pulse_int_n AND im2_int_n</c>, low while the pulse
    /// runs or a hardware IM2 request is up. The CPU's request is a level (FIX-INT-2): it
    /// follows the line, and taking the interrupt does not clear it, so a handler that returns
    /// within the pulse with interrupts enabled is interrupted again, as on the board.
    /// </summary>
    private void RefreshCpuLine()
    {
        if (_cpu is null)
        {
            return;
        }

        bool im2 = ComputeIm2Line();
        bool wasIm2 = _im2Line;
        _im2Line = im2;
        if (im2 && !wasIm2)
        {
            _cpu.Z80SetINTLine(true, 0xFF);
        }
        else if (!im2 && wasIm2 && !(_ulaTiming?.InterruptLineActive ?? false))
        {
            _cpu.Z80SetINTLine(false);
        }
    }

    /// <summary>
    /// The highest-priority device that is not idle drives INT if it is requesting, the CPU is
    /// in IM 2 and hardware IM2 mode is on; a device in service blocks everything below it.
    /// </summary>
    private bool ComputeIm2Line()
    {
        if (!HardwareMode || _pending == 0 || !CpuInIm2)
        {
            return false;
        }

        ushort first = (ushort)(_pending & -_pending);
        return (first & _inService) == 0;
    }

    /// <summary>The pulse began: INT is asserted as it always was.</summary>
    internal void OnPulseStarted() => _cpu?.Z80SetINTLine(true, 0xFF);

    /// <summary>The pulse ended: INT goes high unless a hardware IM2 request holds it.</summary>
    internal void OnPulseEnded()
    {
        if (!_im2Line)
        {
            _cpu?.Z80SetINTLine(false);
        }
    }

    /// <summary>
    /// The CPU's interrupt acknowledge cycle: the byte on the bus, and the chain moving on.
    /// </summary>
    internal byte Acknowledge()
    {
        if (_pending == 0)
        {
            // im2_ieo is high: nothing drives the bus.
            return 0xFF;
        }

        int vector = 0;
        int first = System.Numerics.BitOperations.TrailingZeroCount(_pending);
        ushort firstBit = (ushort)(1 << first);
        if (CpuInIm2 && (firstBit & _inService) == 0)
        {
            _inService |= firstBit;
            vector = first;
        }

        RefreshCpuLine();
        return (byte)(_registers.InterruptVectorBase | (vector << 1));
    }

    /// <summary>
    /// A RETI (ED 4D) has been fetched. With the CPU in IM 2, it ends the service of the
    /// highest-priority device in service (devices only requesting pass the chain while a
    /// RETI is decoded), whose pending request is then cleared.
    /// </summary>
    internal void OnRetiDecoded()
    {
        if (!HardwareMode || !CpuInIm2 || _inService == 0)
        {
            return;
        }

        bool heldOff = DevicesHoldDmaOff;
        ushort serviced = (ushort)(_inService & -_inService);
        _inService &= (ushort)~serviced;
        _pending &= (ushort)~serviced;
        if (heldOff && _clock is not null)
        {
            // dma_delay keeps the DMA off through the opcode's T4 and the first stack read's
            // T1 and T2; the registered hold drops at the end of that read.
            _retiHoldOffEndTick = _clock.MasterTicks + (3UL * (ulong)_clock.MasterTicksPerCpuTstate);
        }

        RefreshCpuLine();
    }

    /// <summary>The CPU executed an IM instruction: INT follows the IM 2 qualification.</summary>
    internal void OnInterruptModeChanged() => RefreshCpuLine();

    /// <summary>
    /// NextReg $C0 was written. Pulse mode holds every device in reset, dropping requests and
    /// services; the status bits stay.
    /// </summary>
    internal void OnModeWritten()
    {
        if (!HardwareMode)
        {
            _pending = 0;
            _inService = 0;
        }

        RefreshCpuLine();
    }

    // --- The DMA --------------------------------------------------------------------------

    private ushort DmaEnableMask => (ushort)(
        ((_dmaEnables0 & 0x02) != 0 ? LineBit : 0)
        | ((_dmaEnables0 & 0x01) != 0 ? UlaBit : 0)
        | (_dmaEnablesCtc << (int)ZxNextInterruptSource.Ctc0)
        | ((_dmaEnablesUart & 0x03) != 0 ? 1 << (int)ZxNextInterruptSource.Uart0Receive : 0)
        | ((_dmaEnablesUart & 0x30) != 0 ? 1 << (int)ZxNextInterruptSource.Uart1Receive : 0)
        | ((_dmaEnablesUart & 0x04) != 0 ? 1 << (int)ZxNextInterruptSource.Uart0Transmit : 0)
        | ((_dmaEnablesUart & 0x40) != 0 ? 1 << (int)ZxNextInterruptSource.Uart1Transmit : 0));

    private bool DevicesHoldDmaOff => HardwareMode && (_pending & DmaEnableMask) != 0;

    /// <summary>
    /// <c>im2_dma_delay</c>: the DMA may not start another byte while a requesting or
    /// in-service device has its DMA interrupt enable, while an NMI is active with $CC bit 7, or
    /// until the end of a RETI's first stack read.
    /// </summary>
    internal bool DmaHeldOff(ulong masterTick) =>
        DevicesHoldDmaOff
        || ((_dmaEnables0 & 0x80) != 0 && _registers.Nmi.Activated)
        || masterTick < _retiHoldOffEndTick;

    /// <summary>When a RETI's hold on the DMA ends, or zero if none is in progress.</summary>
    internal ulong RetiHoldOffEndTick => _retiHoldOffEndTick;

    // --- NextRegs -------------------------------------------------------------------------

    /// <summary>Reads NextReg $20, $C5, $C6, $C8-$CA or $CC-$CE.</summary>
    internal byte Read(byte register)
    {
        ushort status = Status;
        return register switch
        {
            0x20 => (byte)(((status & LineBit) != 0 ? 0x80 : 0)
                | ((status & UlaBit) != 0 ? 0x40 : 0)
                | ((status >> (int)ZxNextInterruptSource.Ctc0) & 0x0F)),
            0xC5 => _registers.Ctc.InterruptEnables,
            0xC6 => _uartEnables,
            0xC8 => (byte)(((status & LineBit) != 0 ? 0x02 : 0) | ((status & UlaBit) != 0 ? 0x01 : 0)),
            0xC9 => (byte)(status >> (int)ZxNextInterruptSource.Ctc0),
            0xCA => (byte)((StatusBit(status, ZxNextInterruptSource.Uart1Transmit) << 6)
                | (StatusBit(status, ZxNextInterruptSource.Uart1Receive) * 0x30)
                | (StatusBit(status, ZxNextInterruptSource.Uart0Transmit) << 2)
                | (StatusBit(status, ZxNextInterruptSource.Uart0Receive) * 0x03)),
            0xCC => _dmaEnables0,
            0xCD => _dmaEnablesCtc,
            0xCE => _dmaEnablesUart,
            _ => 0x00
        };
    }

    private static int StatusBit(ushort status, ZxNextInterruptSource source) =>
        (status >> (int)source) & 1;

    /// <summary>Writes NextReg $20, $C5, $C6, $C8-$CA or $CC-$CE.</summary>
    internal void Write(byte register, byte value)
    {
        switch (register)
        {
            case 0x20:
                WriteUnqualified(value);
                return;
            case 0xC5:
                // The CTC's control bit 7; it has four channels, so the enables of 4-7 are tied low.
                _registers.WriteCtcInterruptEnables(value);
                return;
            case 0xC6:
                _uartEnables = (byte)(value & 0x77);
                return;
            case 0xC8:
                ClearStatus((ushort)(((value & 0x02) != 0 ? LineBit : 0) | ((value & 0x01) != 0 ? UlaBit : 0)));
                return;
            case 0xC9:
                ClearStatus((ushort)(value << (int)ZxNextInterruptSource.Ctc0));
                return;
            case 0xCA:
                ClearStatus((ushort)(
                    ((value & 0x40) != 0 ? 1 << (int)ZxNextInterruptSource.Uart1Transmit : 0)
                    | ((value & 0x30) != 0 ? 1 << (int)ZxNextInterruptSource.Uart1Receive : 0)
                    | ((value & 0x04) != 0 ? 1 << (int)ZxNextInterruptSource.Uart0Transmit : 0)
                    | ((value & 0x03) != 0 ? 1 << (int)ZxNextInterruptSource.Uart0Receive : 0)));
                return;
            case 0xCC:
                _dmaEnables0 = (byte)(value & 0x83);
                return;
            case 0xCD:
                _dmaEnablesCtc = value;
                return;
            case 0xCE:
                _dmaEnablesUart = (byte)(value & 0x77);
                return;
        }
    }

    /// <summary>
    /// A status write clears the status bit only; in hardware IM2 mode a pending or
    /// in-service source still reads set.
    /// </summary>
    private void ClearStatus(ushort mask) => _status &= (ushort)~mask;

    // --- Reset and state ------------------------------------------------------------------

    internal void Reset()
    {
        _status = 0;
        _pending = 0;
        _inService = 0;
        _uartEnables = 0;
        _dmaEnables0 = 0;
        _dmaEnablesCtc = 0;
        _dmaEnablesUart = 0;
        _retiHoldOffEndTick = 0;
        bool wasIm2 = _im2Line;
        _im2Line = false;
        if (wasIm2 && !(_ulaTiming?.InterruptLineActive ?? false))
        {
            _cpu?.Z80SetINTLine(false);
        }
    }

    public ZxNextInterruptControllerState CaptureState() => new(
        _status,
        _pending,
        _inService,
        _im2Line,
        _registers.Ctc.InterruptEnables,
        _uartEnables,
        _dmaEnables0,
        _dmaEnablesCtc,
        _dmaEnablesUart,
        _retiHoldOffEndTick);

    /// <summary>
    /// Puts the chain back. The CPU's own INT latch is restored with the CPU, so this does not
    /// touch it.
    /// </summary>
    public void RestoreState(ZxNextInterruptControllerState state)
    {
        _status = (ushort)(state.Status & AllSources);
        _pending = (ushort)(state.Pending & AllSources);
        _inService = (ushort)(state.InService & _pending);
        _im2Line = state.Im2Line;
        // The CTC's own state, restored after this, carries the enables; a file without it
        // takes them from here.
        _registers.Ctc.WriteInterruptEnables((byte)(state.CtcEnables & 0x0F));
        _uartEnables = (byte)(state.UartEnables & 0x77);
        _dmaEnables0 = (byte)(state.DmaEnables0 & 0x83);
        _dmaEnablesCtc = state.DmaEnablesCtc;
        _dmaEnablesUart = (byte)(state.DmaEnablesUart & 0x77);
        _retiHoldOffEndTick = state.RetiHoldOffEndTick;
    }
}

/// <summary>The maskable interrupt chain: status, pending and in-service bits and their registers.</summary>
public sealed record ZxNextInterruptControllerState(
    ushort Status,
    ushort Pending,
    ushort InService,
    bool Im2Line,
    byte CtcEnables,
    byte UartEnables,
    byte DmaEnables0,
    byte DmaEnablesCtc,
    byte DmaEnablesUart,
    ulong RetiHoldOffEndTick);
