using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Memory;
using ZedExEss.ZxNext.Ports;
using ZedExEss.ZxNext.Video;

namespace ZedExEss.ZxNext.Dma;

/// <summary>
/// The ZX Spectrum Next DMA: the zxnDMA of <c>dma.vhd</c> and, on port <c>$6B</c>, the
/// zxnDDMA (blitter) commands that core 3.03 added.
/// </summary>
/// <remarks>
/// Enabling a transfer asserts BUSREQ; the machine arbiter then advances its separately
/// timed source and destination cycles while the CPU yields the shared bus.
///
/// The prescaler paces bytes as <c>dma.vhd</c> does: its timer is zeroed at each byte's first
/// read clock and gains one per 28 MHz tick, and after the write the DMA waits until
/// timer / 32 reaches the prescaler. In continuous (and byte) mode it keeps the bus through
/// the wait; in burst mode it lets the bus go after the first waiting clock and asks for it
/// again when the wait is over, so the CPU runs between bytes. Auto restart reloads the
/// start addresses and counter at the end of the block and carries on.
///
/// The bus hand-off follows <c>dma.vhd</c> and the T80: a request (START_DMA) pulls BUSRQ low
/// a clock after the DMA decodes the enable; the CPU registers it on the next clock and grants
/// only at the end of a machine cycle after that, so a request made in an OUT's I/O cycle is
/// granted at the end of the next machine cycle. One WAITING_ACK clock follows each grant.
/// Giving the bus back at the end of a block takes FINISH_DMA, IDLE and the two clocks the
/// CPU needs to see BUSRQ go high: the CPU's next T1 comes four clocks after the last write.
/// A DMA access to the SPI data port <c>$EB</c> waits for the SPI byte (<c>spi_wait_n</c>).
///
/// The interrupt hold-off (<c>dma_delay_i</c>, from NextRegs $CC-$CE): after a byte, with more
/// to come and no prescaler wait, START_DMA lets the bus go and asks for it again only once
/// the hold-off has ended, so the CPU can take the interrupt. A continuous-mode prescaler wait
/// goes straight back to WAITING_ACK, so it is not interrupted.
///
/// The zxnDMA follows the pinned 3.02 VHDL. The zxnDDMA's VHDL (<c>new_dma.vhd</c>) is not
/// published, so its commands, modes and reset follow the System/Next 26.11a guide,
/// <c>docs/zxnDDMA/zxnddma.gde</c> part 2. Where the guide is silent: the byte counter
/// counts every byte of a rectangle, the Layer 2 bank is 7 bits, the X,Y mode works without
/// physical mode (its low 16 bits then go through the MMU, as the guide describes), and
/// <c>$D3</c> does not restart a rectangle's rows.
/// </remarks>
public sealed class ZxNextDmaDevice
{
    public const byte Z80ControlPort = 0x0B;
    public const byte ZxnControlPort = 0x6B;

    private const byte PhysicalFlag = 0x01;
    private const byte TwoDimensionalFlag = 0x02;
    private const byte TransparentFlag = 0x04;
    private const byte FillFlag = 0x08;
    private const byte Layer2PositionFlag = 0x10;
    private const byte HiddenScreenFlag = 0x20;

    private readonly ZxNextMemoryBus _memory;
    private ZxNextClockCoordinator? _clock;
    private ZxNextPortBus? _ports;
    private ZxNextLayer2Device? _layer2;
    private ZxNextInterruptController? _interrupts;
    private PendingWrite _pendingWrite;
    private ReadField _readField;
    private byte _pendingHeader;
    private byte _readMask;
    private ushort _portAStart;
    private ushort _portBStart;
    private ushort _blockLength;
    private ushort _sourceAddress;
    private ushort _destinationAddress;
    private ushort _counter;
    private byte _portATiming;
    private byte _portBTiming;
    private byte _portBPrescaler;
    private byte _transferByte;
    private TransferPhase _transferPhase;
    private int _currentTransferLength;
    private AddressMode _portAAddressMode;
    private AddressMode _portBAddressMode;
    private bool _portAIsIo;
    private bool _portBIsIo;
    private bool _directionAtoB;
    private bool _loaded;
    private bool _endOfBlockNot;
    private bool _transferredAtLeastOne;
    private bool _z80CompatibleMode;
    private byte _transferMode;
    private bool _autoRestart;
    private bool _ceWait;
    private bool _active;

    // Prescaler pacing: the clocks of a held wait still to run, and the master tick at
    // which a burst-mode wait ends (the DMA asks for the bus again, restarts or finishes).
    private int _waitClocks;
    private ulong _resumeTick;

    // The bus hand-off: whether the DMA holds the bus, and the master tick at which BUSRQ
    // went low for a request not yet granted.
    private bool _busGranted;
    private ulong _requestTick;

    /// <summary>
    /// A DMA access to the SPI data port waits for the SPI master's byte. Its 16-clock
    /// transfer starts on the access's second falling edge and the DMA samples its wait
    /// through a falling-edge register, so a three- or four-clock cycle ends 18 clocks after
    /// it began. A two-clock cycle is over before the wait reaches it.
    /// </summary>
    private const int SpiAccessClocks = 18;

    // zxnDDMA registers and the working state of a transfer that uses them.
    private byte _dimensionalFlags;
    private byte _portAExtended;
    private byte _portBExtended;
    private ushort _rowLength;
    private ushort _rowCount;
    private ushort _portAStride;
    private ushort _portBStride;
    private byte _transparentValue;
    private byte _fillValue;
    private byte _layer2XLow;
    private byte _layer2XHigh;
    private byte _sourceHigh;
    private byte _destinationHigh;
    private int _sourceRowStart;
    private int _destinationRowStart;
    private ushort _rowPosition;
    private ushort _rowIndex;

    public ZxNextDmaDevice(ZxNextMemoryBus memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _readMask = 0x7F;
        _portAAddressMode = AddressMode.Increment;
        _portBAddressMode = AddressMode.Increment;
        Reset();
    }

    /// <summary>True from an enable until the block ends or the DMA is stopped.</summary>
    public bool IsActive
    {
        get
        {
            ResolveBurstWaitIfDue();
            return _active;
        }
    }

    /// <summary>
    /// True while the DMA wants the bus: always while a transfer is running, except during a
    /// burst-mode prescaler wait, which gives the bus back until its time is up.
    /// </summary>
    public bool BusRequestAsserted => _active && _transferPhase switch
    {
        TransferPhase.BurstWait => ResolveBurstWaitIfDue(),
        TransferPhase.HeldOff => ResolveHoldOffIfClear(),
        _ => true
    };

    /// <summary>
    /// True while a transfer is enabled, waiting or not; the machine keeps the CPU's time
    /// published every machine cycle meanwhile, so a burst wait ends on the right cycle.
    /// </summary>
    public bool HasPendingBusWork => _active;

    /// <summary>True while the DMA holds the bus the machine granted it.</summary>
    public bool BusGranted => _busGranted;

    /// <summary>
    /// The first master tick at which a machine cycle may end with the bus granted: BUSRQ
    /// went low at the request tick, the CPU registers it on the next clock, and it grants at
    /// the end of a machine cycle after that.
    /// </summary>
    public ulong GrantEligibleTick => _requestTick + (2UL * (ulong)TicksPerClock);

    /// <summary>
    /// True for a transfer that will never give the bus back: auto restart without burst
    /// pacing. The hardware locks the CPU out for good; the machine still finishes frames.
    /// </summary>
    public bool HoldsBusIndefinitely =>
        _active && _autoRestart && !(_transferMode == 0x02 && _portBPrescaler > 0);

    /// <summary>
    /// True once a <c>$CF</c> or <c>$D3</c> has been taken since the last reset; a diagnostic
    /// only, as the hardware starts a transfer whether or not one was.
    /// </summary>
    public bool HasLoadedTransfer => _loaded;

    public bool Z80CompatibleMode => _z80CompatibleMode;
    public ushort PortAStartAddress => _portAStart;
    public ushort PortBStartAddress => _portBStart;
    public ushort BlockLength => _blockLength;
    public ushort PortAAddressCounter => _directionAtoB ? _sourceAddress : _destinationAddress;
    public ushort PortBAddressCounter => _directionAtoB ? _destinationAddress : _sourceAddress;
    public ushort ByteCounter => _counter;
    public int CurrentTransferLength => _currentTransferLength;
    public int LastTransferLength { get; private set; }

    /// <summary>WR4 bits 6:5: 00 byte (acts as continuous), 01 continuous, 10 burst.</summary>
    public byte TransferMode => _transferMode;

    /// <summary>WR5 bit 5: the block starts again from the start addresses when it ends.</summary>
    public bool AutoRestart => _autoRestart;

    /// <summary>WR2's prescaler byte: a byte every 32 x P 28 MHz ticks (875 kHz / P).</summary>
    public byte Prescaler => _portBPrescaler;

    /// <summary>The zxnDDMA mode flags last set by <c>$DF</c>.</summary>
    public byte DimensionalFlags => _dimensionalFlags;

    /// <summary>True while a WR4 with bit 4 and no address bytes has hung the decoder.</summary>
    public bool RegisterDecoderHung => _pendingWrite == PendingWrite.Register4InterruptControl;

    public int CpuClockCyclesUntilBusEvent => _transferPhase switch
    {
        TransferPhase.HeldWait or TransferPhase.BurstHold or TransferPhase.AwaitingAck
            or TransferPhase.Releasing or TransferPhase.HoldOffRelease => _waitClocks,
        TransferPhase.SourceRead or TransferPhase.BurstWait when !FillMode => ReadCycles,
        _ => WriteCycles
    };

    private int ReadCycles => EndpointCycles(
        SourceIsPortA ? _portATiming : _portBTiming,
        SourceIsPortA ? _portAIsIo : _portBIsIo,
        _sourceAddress);

    private int WriteCycles => EndpointCycles(
        SourceIsPortA ? _portBTiming : _portATiming,
        SourceIsPortA ? _portBIsIo : _portAIsIo,
        _destinationAddress);

    private int TicksPerClock =>
        _clock?.MasterTicksPerCpuTstate ?? ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate;

    private ulong Now => _clock?.MasterTicks ?? 0;

    private int EndpointCycles(byte timing, bool io, ushort address)
    {
        int cycles = DecodeTimingCycles(timing);
        return io && cycles >= 3 && _ports?.IsSpiDataPort(address) == true
            ? SpiAccessClocks
            : cycles;
    }

    /// <summary>The machine gave the DMA the bus.</summary>
    internal void OnBusGranted() => _busGranted = true;

    public bool HandlesPort(ushort port) => (byte)port is Z80ControlPort or ZxnControlPort;

    internal void ConfigurePorts(ZxNextPortBus ports)
    {
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
    }

    internal void ConfigureLayer2(ZxNextLayer2Device layer2)
    {
        _layer2 = layer2 ?? throw new ArgumentNullException(nameof(layer2));
    }

    internal void ConfigureInterrupts(ZxNextInterruptController interrupts)
    {
        _interrupts = interrupts ?? throw new ArgumentNullException(nameof(interrupts));
    }

    internal void ConfigureClock(ZxNextClockCoordinator clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public byte Read(ushort port)
    {
        ResolveBurstWaitIfDue();
        SelectMode(port);
        byte value = _readField switch
        {
            ReadField.CounterLow => (byte)_counter,
            ReadField.CounterHigh => (byte)(_counter >> 8),
            ReadField.PortALow => (byte)PortAAddressCounter,
            ReadField.PortAHigh => (byte)(PortAAddressCounter >> 8),
            ReadField.PortBLow => (byte)PortBAddressCounter,
            ReadField.PortBHigh => (byte)(PortBAddressCounter >> 8),
            _ => (byte)((_endOfBlockNot ? 0x20 : 0)
                | 0x1A
                | (_transferredAtLeastOne ? 0x01 : 0))
        };
        AdvanceReadField();
        return value;
    }

    public void Write(ushort port, byte value)
    {
        SelectMode(port);
        if (_pendingWrite != PendingWrite.None)
        {
            WritePendingValue(value);
            return;
        }

        if (!_z80CompatibleMode && TryBeginDimensionalCommand(value))
        {
            return;
        }

        if ((value & 0x80) == 0 && (value & 0x03) != 0)
        {
            WriteRegister0(value);
        }
        else if ((value & 0x80) == 0 && (value & 0x07) == 0x04)
        {
            _portAIsIo = (value & 0x08) != 0;
            _portAAddressMode = DecodeAddressMode(value);
            if ((value & 0x40) != 0)
            {
                _pendingHeader = value;
                _pendingWrite = PendingWrite.Register1Timing;
            }
        }
        else if ((value & 0x80) == 0 && (value & 0x07) == 0)
        {
            _portBIsIo = (value & 0x08) != 0;
            _portBAddressMode = DecodeAddressMode(value);
            if ((value & 0x40) != 0)
            {
                _pendingHeader = value;
                _pendingWrite = PendingWrite.Register2Timing;
            }
        }
        else if ((value & 0x80) != 0 && (value & 0x03) == 0)
        {
            WriteRegister3(value);
        }
        else if ((value & 0x80) != 0 && (value & 0x03) == 1)
        {
            WriteRegister4(value);
        }
        else if ((value & 0xC0) == 0x80 && (value & 0x07) == 2)
        {
            _ceWait = (value & 0x10) != 0;
            _autoRestart = (value & 0x20) != 0;
        }
        else if ((value & 0x80) != 0 && (value & 0x03) == 3)
        {
            WriteRegister6(value);
        }
    }

    /// <summary>The hardware reset (<c>reset_i</c>): soft and hard machine resets alike.</summary>
    /// <remarks>
    /// Stops a transfer and clears the decoder, counter, timings, prescaler, mode, WR5, read
    /// mask and status, and the zxnDDMA registers. The start and working addresses, block
    /// length, direction, port types and address modes are not reset.
    /// </remarks>
    public void Reset()
    {
        _pendingWrite = PendingWrite.None;
        _pendingHeader = 0;
        _readMask = 0x7F;
        _readField = ReadField.Status;
        _counter = 0;
        _portATiming = 0x01;
        _portBTiming = 0x01;
        _portBPrescaler = 0;
        _transferMode = 0x01;
        _ceWait = false;
        _autoRestart = false;
        _transferByte = 0;
        _transferPhase = TransferPhase.SourceRead;
        _waitClocks = 0;
        _resumeTick = 0;
        _busGranted = false;
        _requestTick = 0;
        _currentTransferLength = 0;
        _loaded = false;
        _endOfBlockNot = true;
        _transferredAtLeastOne = false;
        _z80CompatibleMode = false;
        ClearDimensionalRegisters();
        _active = false;
        LastTransferLength = 0;
    }

    /// <summary>Captures the controller, mid-transfer and mid-register-write included.</summary>
    public ZxNextDmaState CaptureState() => new(
        _active,
        _loaded,
        _z80CompatibleMode,
        _directionAtoB,
        _endOfBlockNot,
        _transferredAtLeastOne,
        _portAIsIo,
        _portBIsIo,
        (byte)_pendingWrite,
        _pendingHeader,
        (byte)_readField,
        _readMask,
        (byte)_transferPhase,
        (byte)_portAAddressMode,
        (byte)_portBAddressMode,
        _portATiming,
        _portBTiming,
        _portBPrescaler,
        _transferByte,
        _portAStart,
        _portBStart,
        _blockLength,
        _sourceAddress,
        _destinationAddress,
        _counter,
        _currentTransferLength,
        LastTransferLength)
    {
        Extended = new ZxNextDmaExtendedState(
            _transferMode,
            _autoRestart,
            _ceWait,
            _dimensionalFlags,
            _portAExtended,
            _portBExtended,
            _rowLength,
            _rowCount,
            _portAStride,
            _portBStride,
            _transparentValue,
            _fillValue,
            _layer2XLow,
            _layer2XHigh,
            _sourceHigh,
            _destinationHigh,
            _sourceRowStart,
            _destinationRowStart,
            _rowPosition,
            _rowIndex,
            _waitClocks,
            _resumeTick,
            _busGranted,
            _requestTick)
    };

    /// <summary>Puts a captured controller back without replaying any of its writes.</summary>
    public void RestoreState(ZxNextDmaState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _active = state.IsActive;
        _loaded = state.Loaded;
        _z80CompatibleMode = state.Z80CompatibleMode;
        _directionAtoB = state.DirectionAtoB;
        _endOfBlockNot = state.EndOfBlockNot;
        _transferredAtLeastOne = state.TransferredAtLeastOne;
        _portAIsIo = state.PortAIsIo;
        _portBIsIo = state.PortBIsIo;
        _pendingWrite = (PendingWrite)state.PendingWrite;
        _pendingHeader = state.PendingHeader;
        _readField = (ReadField)state.ReadField;
        _readMask = state.ReadMask;
        _transferPhase = (TransferPhase)state.TransferPhase;
        _portAAddressMode = (AddressMode)state.PortAAddressMode;
        _portBAddressMode = (AddressMode)state.PortBAddressMode;
        _portATiming = state.PortATiming;
        _portBTiming = state.PortBTiming;
        _portBPrescaler = state.PortBPrescaler;
        _transferByte = state.TransferByte;
        _portAStart = state.PortAStart;
        _portBStart = state.PortBStart;
        _blockLength = state.BlockLength;
        _sourceAddress = state.SourceAddress;
        _destinationAddress = state.DestinationAddress;
        _counter = state.Counter;
        _currentTransferLength = state.CurrentTransferLength;
        LastTransferLength = state.LastTransferLength;

        ZxNextDmaExtendedState extended = state.Extended;
        _transferMode = extended.TransferMode;
        _autoRestart = extended.AutoRestart;
        _ceWait = extended.CeWait;
        _dimensionalFlags = extended.DimensionalFlags;
        _portAExtended = extended.PortAExtended;
        _portBExtended = extended.PortBExtended;
        _rowLength = extended.RowLength;
        _rowCount = extended.RowCount;
        _portAStride = extended.PortAStride;
        _portBStride = extended.PortBStride;
        _transparentValue = extended.TransparentValue;
        _fillValue = extended.FillValue;
        _layer2XLow = extended.Layer2XLow;
        _layer2XHigh = extended.Layer2XHigh;
        _sourceHigh = extended.SourceHigh;
        _destinationHigh = extended.DestinationHigh;
        _sourceRowStart = extended.SourceRowStart;
        _destinationRowStart = extended.DestinationRowStart;
        _rowPosition = extended.RowPosition;
        _rowIndex = extended.RowIndex;
        _waitClocks = extended.WaitClocks;
        _resumeTick = extended.ResumeTick;
        _busGranted = extended.BusGranted;
        _requestTick = extended.RequestTick;
    }

    private void WriteRegister0(byte value)
    {
        _pendingHeader = value;
        _directionAtoB = (value & 0x04) != 0;
        _pendingWrite = NextRegister0Field(value, PendingWrite.Register0PortALow);
    }

    private void WriteRegister3(byte value)
    {
        _pendingHeader = value;
        _pendingWrite = (value & 0x08) != 0
            ? PendingWrite.Register3Mask
            : (value & 0x10) != 0
                ? PendingWrite.Register3Match
                : PendingWrite.None;
        if ((value & 0x40) != 0)
        {
            Enable();
        }
    }

    private void WriteRegister4(byte value)
    {
        _pendingHeader = value;
        _transferMode = (byte)((value >> 5) & 0x03);

        // Bit 4 asks for an interrupt-control byte. The FPGA's handler for it is commented
        // out, so with no address byte before it the decoder waits there for good: every
        // later write is swallowed until the next reset (dma.vhd's "when others => null").
        _pendingWrite = (value & 0x04) != 0
            ? PendingWrite.Register4PortBLow
            : (value & 0x08) != 0
                ? PendingWrite.Register4PortBHigh
                : (value & 0x10) != 0
                    ? PendingWrite.Register4InterruptControl
                    : PendingWrite.None;
    }

    private void WriteRegister6(byte value)
    {
        switch (value)
        {
            case 0xC3: // Reset.
                _active = false;
                _endOfBlockNot = true;
                _transferredAtLeastOne = false;
                _portATiming = 0x01;
                _portBTiming = 0x01;
                _portBPrescaler = 0;
                _ceWait = false;
                _autoRestart = false;
                ClearDimensionalRegisters();
                _loaded = false;
                _transferPhase = TransferPhase.SourceRead;
                _waitClocks = 0;
                _currentTransferLength = 0;
                LastTransferLength = 0;
                break;
            case 0xC7: // Reset port A timing.
                _portATiming = 0x01;
                break;
            case 0xCB: // Reset port B timing.
                _portBTiming = 0x01;
                break;
            case 0xCF: // Load the start addresses and the byte counter.
                LoadTransfer();
                break;
            case 0xD3: // Continue: reset the byte counter, keep the working addresses.
                _endOfBlockNot = true;
                _counter = _z80CompatibleMode ? ushort.MaxValue : (ushort)0;
                _currentTransferLength = 0;
                _loaded = true;
                break;
            case 0xBF:
                _readField = ReadField.Status;
                break;
            case 0x8B:
                _endOfBlockNot = true;
                _transferredAtLeastOne = false;
                break;
            case 0xA7:
                _readField = FirstReadField();
                break;
            case 0x87: // Enable.
                Enable();
                break;
            case 0x83: // Disable.
                Stop();
                break;
            case 0xBB: // Read-mask byte follows.
                _pendingWrite = PendingWrite.Register6ReadMask;
                break;
        }
    }

    /// <summary>
    /// Takes a zxnDDMA command byte. Only on <c>$6B</c> with the decoder idle: on <c>$0B</c>
    /// these bytes fall through to WR6, which ignores them, and their follow bytes are then
    /// read as ordinary register writes, as on a core without the zxnDDMA.
    /// </summary>
    private bool TryBeginDimensionalCommand(byte value)
    {
        PendingWrite next;
        switch (value)
        {
            case 0xD7: next = PendingWrite.DdmaPortAExtended; break;
            case 0xDB: next = PendingWrite.DdmaPortBExtended; break;
            case 0xDF: next = PendingWrite.DdmaFlags; break;
            case 0xE3: next = PendingWrite.DdmaRowLengthLow; break;
            case 0xE7: next = PendingWrite.DdmaRowCountLow; break;
            case 0xEB: next = PendingWrite.DdmaPortAStrideLow; break;
            case 0xEF: next = PendingWrite.DdmaPortBStrideLow; break;
            case 0xF3: next = PendingWrite.DdmaTransparent; break;
            case 0xF7: next = PendingWrite.DdmaFill; break;
            case 0xFB: next = PendingWrite.None; break; // Commented out in new_dma.vhd.
            default: return false;
        }

        _pendingWrite = next;
        return true;
    }

    private void WritePendingValue(byte value)
    {
        switch (_pendingWrite)
        {
            case PendingWrite.Register0PortALow:
                _portAStart = (ushort)((_portAStart & 0xFF00) | value);
                _pendingWrite = NextRegister0Field(_pendingHeader, PendingWrite.Register0PortAHigh);
                break;
            case PendingWrite.Register0PortAHigh:
                _portAStart = (ushort)((_portAStart & 0x00FF) | (value << 8));
                _pendingWrite = NextRegister0Field(_pendingHeader, PendingWrite.Register0LengthLow);
                break;
            case PendingWrite.Register0LengthLow:
                _blockLength = (ushort)((_blockLength & 0xFF00) | value);
                _pendingWrite = NextRegister0Field(_pendingHeader, PendingWrite.Register0LengthHigh);
                break;
            case PendingWrite.Register0LengthHigh:
                _blockLength = (ushort)((_blockLength & 0x00FF) | (value << 8));
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.Register1Timing:
                _portATiming = (byte)(value & 0x03);
                _pendingWrite = (value & 0x20) != 0
                    ? PendingWrite.Register1Prescaler
                    : PendingWrite.None;
                break;
            case PendingWrite.Register1Prescaler:
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.Register2Timing:
                _portBTiming = (byte)(value & 0x03);
                _pendingWrite = (value & 0x20) != 0
                    ? PendingWrite.Register2Prescaler
                    : PendingWrite.None;
                break;
            case PendingWrite.Register2Prescaler:
                _portBPrescaler = value;
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.Register3Mask:
                _pendingWrite = (_pendingHeader & 0x10) != 0
                    ? PendingWrite.Register3Match
                    : PendingWrite.None;
                break;
            case PendingWrite.Register3Match:
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.Register4PortBLow:
                _portBStart = (ushort)((_portBStart & 0xFF00) | value);
                _pendingWrite = (_pendingHeader & 0x08) != 0
                    ? PendingWrite.Register4PortBHigh
                    : PendingWrite.None;
                break;
            case PendingWrite.Register4PortBHigh:
                _portBStart = (ushort)((_portBStart & 0x00FF) | (value << 8));
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.Register4InterruptControl:
                // Hung: see WriteRegister4.
                break;
            case PendingWrite.Register6ReadMask:
                _readMask = value;
                _readField = FirstReadField();
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.DdmaPortAExtended:
                _portAExtended = value;
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.DdmaPortBExtended:
                _portBExtended = value;
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.DdmaFlags:
                _dimensionalFlags = (byte)(value & 0x3F);
                _pendingWrite = (value & Layer2PositionFlag) != 0
                    ? PendingWrite.DdmaLayer2XLow
                    : PendingWrite.None;
                break;
            case PendingWrite.DdmaLayer2XLow:
                _layer2XLow = value;
                _pendingWrite = PendingWrite.DdmaLayer2XHigh;
                break;
            case PendingWrite.DdmaLayer2XHigh:
                _layer2XHigh = value;
                _pendingWrite = PendingWrite.DdmaLayer2Y;
                break;
            case PendingWrite.DdmaLayer2Y:
                ApplyLayer2Position(value);
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.DdmaRowLengthLow:
                _rowLength = (ushort)((_rowLength & 0xFF00) | value);
                _pendingWrite = PendingWrite.DdmaRowLengthHigh;
                break;
            case PendingWrite.DdmaRowLengthHigh:
                _rowLength = (ushort)((_rowLength & 0x00FF) | (value << 8));
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.DdmaRowCountLow:
                _rowCount = (ushort)((_rowCount & 0xFF00) | value);
                _pendingWrite = PendingWrite.DdmaRowCountHigh;
                break;
            case PendingWrite.DdmaRowCountHigh:
                _rowCount = (ushort)((_rowCount & 0x00FF) | (value << 8));
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.DdmaPortAStrideLow:
                _portAStride = (ushort)((_portAStride & 0xFF00) | value);
                _pendingWrite = PendingWrite.DdmaPortAStrideHigh;
                break;
            case PendingWrite.DdmaPortAStrideHigh:
                _portAStride = (ushort)((_portAStride & 0x00FF) | (value << 8));
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.DdmaPortBStrideLow:
                _portBStride = (ushort)((_portBStride & 0xFF00) | value);
                _pendingWrite = PendingWrite.DdmaPortBStrideHigh;
                break;
            case PendingWrite.DdmaPortBStrideHigh:
                _portBStride = (ushort)((_portBStride & 0x00FF) | (value << 8));
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.DdmaTransparent:
                _transparentValue = value;
                _pendingWrite = PendingWrite.None;
                break;
            case PendingWrite.DdmaFill:
                _fillValue = value;
                _pendingWrite = PendingWrite.None;
                break;
            default:
                _pendingWrite = PendingWrite.None;
                break;
        }
    }

    /// <summary>
    /// Works the Port B start address out of a Layer 2 X,Y position (<c>$DF</c> bit 4).
    /// </summary>
    /// <remarks>
    /// The bank is NextReg <c>$12</c>, or <c>$13</c> with the hidden-screen flag, and the
    /// screen layout NextReg <c>$70</c> bits 5:4. 256x192 is stored row by row (X_hi is
    /// ignored); the 320 and 640 modes column by column with a 9-bit X. Port B's stride
    /// becomes 256 until the next <c>$EF</c> or <c>$C3</c>.
    /// </remarks>
    private void ApplyLayer2Position(byte y)
    {
        int bank = ((_dimensionalFlags & HiddenScreenFlag) != 0
            ? _layer2?.ShadowBank ?? 0
            : _layer2?.ActiveBank ?? 0) & 0x7F;
        int resolution = _layer2?.Resolution ?? 0;
        int address = 0x40000 + (bank * 0x4000) + (resolution == 0
            ? (y << 8) | _layer2XLow
            : ((((_layer2XHigh & 0x01) << 8) | _layer2XLow) << 8) + y);
        _portBStart = (ushort)address;
        _portBExtended = (byte)(address >> 16);
        _portBStride = 0x100;
    }

    private void ClearDimensionalRegisters()
    {
        _dimensionalFlags = 0;
        _portAExtended = 0;
        _portBExtended = 0;
        _rowLength = 0;
        _rowCount = 0;
        _portAStride = 0;
        _portBStride = 0;
        _transparentValue = 0;
        _fillValue = 0;
        _layer2XLow = 0;
        _layer2XHigh = 0;
        _sourceHigh = 0;
        _destinationHigh = 0;
        _sourceRowStart = 0;
        _destinationRowStart = 0;
        _rowPosition = 0;
        _rowIndex = 0;
    }

    private PendingWrite NextRegister0Field(byte header, PendingWrite after)
    {
        int firstBit = after switch
        {
            PendingWrite.Register0PortALow => 3,
            PendingWrite.Register0PortAHigh => 4,
            PendingWrite.Register0LengthLow => 5,
            PendingWrite.Register0LengthHigh => 6,
            _ => 7
        };
        for (int bit = firstBit; bit <= 6; bit++)
        {
            if ((header & (1 << bit)) != 0)
            {
                return bit switch
                {
                    3 => PendingWrite.Register0PortALow,
                    4 => PendingWrite.Register0PortAHigh,
                    5 => PendingWrite.Register0LengthLow,
                    _ => PendingWrite.Register0LengthHigh
                };
            }
        }

        return PendingWrite.None;
    }

    private void LoadTransfer()
    {
        ReloadAddressesAndCounter();
        _endOfBlockNot = true;
        _loaded = true;
        _currentTransferLength = 0;
        _transferPhase = TransferPhase.SourceRead;
    }

    /// <summary>What <c>$CF</c> and an auto restart share: the start addresses and counter.</summary>
    private void ReloadAddressesAndCounter()
    {
        _sourceAddress = _directionAtoB ? _portAStart : _portBStart;
        _destinationAddress = _directionAtoB ? _portBStart : _portAStart;
        _sourceHigh = _directionAtoB ? _portAExtended : _portBExtended;
        _destinationHigh = _directionAtoB ? _portBExtended : _portAExtended;
        _sourceRowStart = (_sourceHigh << 16) | _sourceAddress;
        _destinationRowStart = (_destinationHigh << 16) | _destinationAddress;
        _rowPosition = 0;
        _rowIndex = 0;
        _counter = _z80CompatibleMode ? ushort.MaxValue : (ushort)0;
    }

    /// <summary>
    /// <c>$87</c> or WR3 bit 6. The hardware goes to START_DMA whatever came before, so a
    /// transfer that was never loaded, or one that already finished, still moves a byte.
    /// </summary>
    private void Enable()
    {
        // A DMA holding the bus cannot be programmed by the CPU; only a host write gets here.
        if (_active && _busGranted)
        {
            return;
        }

        // START_DMA from any state, so an enable also cuts a prescaler wait short. The CPU's
        // write lands at the end of its I/O cycle's T1; the DMA decodes it on the next clock
        // and START_DMA pulls BUSRQ low one clock later.
        _active = true;
        EnterStartDma(Now + (ulong)TicksPerClock);
    }

    /// <summary>
    /// START_DMA, entered at the given tick: BUSRQ goes low a clock later, unless the
    /// interrupt hold-off is on, when the DMA waits here without asking for the bus.
    /// </summary>
    private void EnterStartDma(ulong tick)
    {
        if (_interrupts?.DmaHeldOff(tick) == true)
        {
            _busGranted = false;
            _resumeTick = tick;
            _transferPhase = TransferPhase.HeldOff;
            return;
        }

        RequestBus(tick + (ulong)TicksPerClock);
    }

    /// <summary>
    /// START_DMA waiting out the interrupt hold-off: once it has ended the DMA asks for the bus
    /// again. True when it now wants the bus.
    /// </summary>
    /// <remarks>
    /// START_DMA sees the hold-off gone a clock after it drops and pulls BUSRQ low then. A
    /// RETI's hold ends at a known tick; any other end (a register write, an NMI released) is
    /// taken as now, the machine cycle at which the DMA looked.
    /// </remarks>
    private bool ResolveHoldOffIfClear()
    {
        ulong now = Now;
        if (_interrupts?.DmaHeldOff(now) == true)
        {
            return false;
        }

        ulong retiEnd = _interrupts?.RetiHoldOffEndTick ?? 0;
        ulong cleared = retiEnd > _resumeTick && retiEnd <= now ? retiEnd : now;
        RequestBus(Math.Max(cleared, _resumeTick) + (ulong)TicksPerClock);
        return true;
    }

    /// <summary>START_DMA: BUSRQ goes low at the given tick; WAITING_ACK follows the grant.</summary>
    private void RequestBus(ulong requestTick)
    {
        _requestTick = requestTick;
        _busGranted = false;
        _waitClocks = 1;
        _transferPhase = TransferPhase.AwaitingAck;
    }

    private void Stop()
    {
        _active = false;
        _busGranted = false;
        _transferPhase = TransferPhase.SourceRead;

        // The idle state clears "at least one byte" on every clock.
        _transferredAtLeastOne = false;
    }

    /// <summary>Completes the endpoint cycle currently holding a DMA bus grant.</summary>
    internal void CompleteGrantedBusCycle()
    {
        if (!_active)
        {
            return;
        }

        switch (_transferPhase)
        {
            case TransferPhase.AwaitingAck:
                _waitClocks = 0;
                _transferPhase = TransferPhase.SourceRead;
                return;
            case TransferPhase.Releasing:
                FinishBlock();
                return;
            case TransferPhase.HoldOffRelease:
                // START_DMA's clock and the CPU's two to see BUSRQ high: the bus goes back.
                _busGranted = false;
                _resumeTick = Now;
                _transferPhase = TransferPhase.HeldOff;
                return;
            case TransferPhase.HeldWait:
                EndPrescalerWait(busHeld: true);
                return;
            case TransferPhase.BurstWait:
                EndPrescalerWait(busHeld: false);
                return;
            case TransferPhase.BurstHold:
                // The first waiting clock and the CPU's two clocks to see BUSRQ high have
                // passed; the bus goes back. A request cannot come before this.
                _transferPhase = TransferPhase.BurstWait;
                _busGranted = false;
                _resumeTick = Math.Max(_resumeTick, Now);
                return;
        }

        if (_transferPhase == TransferPhase.SourceRead)
        {
            if (FillMode)
            {
                // Fill has no read cycle: this grant is the write.
                _transferByte = _fillValue;
            }
            else
            {
                _transferByte = ReadEndpoint(
                    _sourceAddress,
                    _sourceHigh,
                    SourceIsPortA ? _portAIsIo : _portBIsIo);
                _transferPhase = TransferPhase.DestinationWrite;
                return;
            }
        }

        // A transparent byte is not written but its write cycle is still taken.
        if ((_dimensionalFlags & TransparentFlag) == 0 || _transferByte != _transparentValue)
        {
            WriteEndpoint(
                _destinationAddress,
                _destinationHigh,
                _transferByte,
                SourceIsPortA ? _portBIsIo : _portAIsIo);
            if (!_active)
            {
                return;
            }
        }

        _counter++;
        _currentTransferLength++;
        _transferredAtLeastOne = true;
        StepAddress(
            ref _sourceAddress,
            ref _sourceHigh,
            SourceIsPortA ? _portAAddressMode : _portBAddressMode);
        StepAddress(
            ref _destinationAddress,
            ref _destinationHigh,
            SourceIsPortA ? _portBAddressMode : _portAAddressMode);
        _transferPhase = TransferPhase.SourceRead;

        bool more = (_dimensionalFlags & TwoDimensionalFlag) != 0
            ? AdvanceRow()
            : _counter < _blockLength;
        if (_portBPrescaler > 0 && TryBeginPrescalerWait(more))
        {
            return;
        }

        if (more)
        {
            if (_interrupts?.DmaHeldOff(Now) == true)
            {
                // TRANSFERING_WRITE_4 goes to START_DMA, which lets the bus go.
                _waitClocks = 3;
                _transferPhase = TransferPhase.HoldOffRelease;
            }

            return;
        }

        if (_autoRestart)
        {
            // FINISH_DMA reloads, then WAITING_ACK: two clocks with the bus held.
            _waitClocks = 2;
            _transferPhase = TransferPhase.HeldWait;
            return;
        }

        BeginRelease();
    }

    /// <summary>
    /// FINISH_DMA, IDLE raising BUSRQ, and the CPU's two clocks to register it and drop BUSAK:
    /// four clocks before the CPU's next T1.
    /// </summary>
    private void BeginRelease()
    {
        _waitClocks = 4;
        _transferPhase = TransferPhase.Releasing;
    }

    /// <summary>
    /// After a byte's last write clock: wait if the prescaler's time is not yet up.
    /// </summary>
    /// <remarks>
    /// <c>dma.vhd</c> zeroes the timer at the byte's first read clock (clock 0) and adds the
    /// number of 28 MHz ticks per DMA clock on every later clock, so clock j sees
    /// (j - 1) x ticks. The last write clock (byteClocks - 1) tests it; WAITING_CYCLES then
    /// holds until the first clock whose timer / 32 reaches the prescaler, after which
    /// WAITING_ACK (a further byte) or FINISH_DMA follows. So a byte starts every
    /// ceil(32P / ticks) + 3 clocks: 875 kHz / P whatever the CPU speed.
    /// </remarks>
    private bool TryBeginPrescalerWait(bool more)
    {
        int ticksPerClock = _clock?.MasterTicksPerCpuTstate ?? ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate;
        int byteClocks = (FillMode ? 0 : ReadCycles) + WriteCycles;
        if (_portBPrescaler <= ((byteClocks - 2) * ticksPerClock) >> 5)
        {
            return false;
        }

        int exitClock = (((32 * _portBPrescaler) + ticksPerClock - 1) / ticksPerClock) + 1;

        // The clock (counted from the read) at which the DMA next does something: asks for
        // the bus for another byte (after WAITING_ACK), restarts (after FINISH_DMA and
        // WAITING_ACK), or finishes (FINISH_DMA).
        int nextClock = more ? exitClock + 2 : _autoRestart ? exitClock + 3 : exitClock + 1;
        if (_transferMode == 0x02)
        {
            // Burst: the first waiting clock still holds the bus, and the CPU takes two more
            // to see BUSRQ go high; then the bus is the CPU's until the wait is over, when
            // START_DMA pulls BUSRQ low again (that is the clock counted here).
            ulong byteStart = Now - ((ulong)byteClocks * (ulong)ticksPerClock);
            _resumeTick = byteStart + ((ulong)nextClock * (ulong)ticksPerClock);
            _waitClocks = 3;
            _transferPhase = TransferPhase.BurstHold;
            return true;
        }

        _waitClocks = nextClock - byteClocks;
        _transferPhase = TransferPhase.HeldWait;
        return true;
    }

    /// <summary>
    /// The end of a prescaler wait or of FINISH_DMA's restart clocks: the next byte, an auto
    /// restart, or the end of the block - decided on the registers as they are now, as the
    /// hardware's WAITING_CYCLES state does.
    /// </summary>
    private void EndPrescalerWait(bool busHeld)
    {
        _waitClocks = 0;
        bool more = (_dimensionalFlags & TwoDimensionalFlag) != 0
            ? _rowIndex < Math.Max((int)_rowCount, 1)
            : _counter < _blockLength;
        if (more || _autoRestart)
        {
            if (!more)
            {
                RestartBlock();
            }

            if (busHeld)
            {
                _transferPhase = TransferPhase.SourceRead;
            }
            else
            {
                // _resumeTick is when START_DMA pulls BUSRQ low: it was entered a clock before.
                EnterStartDma(_resumeTick - (ulong)TicksPerClock);
            }

            return;
        }

        if (busHeld)
        {
            BeginRelease();
        }
        else
        {
            FinishBlock();
        }
    }

    /// <summary>
    /// A burst-mode wait ends by the clock, not by a bus cycle: once its time has come the DMA
    /// asks for the bus again (or restarts, or finishes). True when it now wants the bus.
    /// </summary>
    private bool ResolveBurstWaitIfDue()
    {
        if (_transferPhase != TransferPhase.BurstWait || !_active)
        {
            return false;
        }

        if ((_clock?.MasterTicks ?? ulong.MaxValue) < _resumeTick)
        {
            return false;
        }

        EndPrescalerWait(busHeld: false);
        return _active && _transferPhase != TransferPhase.HeldOff;
    }

    /// <summary>
    /// Auto restart (WR5 bit 5): FINISH_DMA reloads the start addresses and the counter and
    /// carries on. The end of block stays reported.
    /// </summary>
    private void RestartBlock()
    {
        LastTransferLength = _currentTransferLength;
        _currentTransferLength = 0;
        ReloadAddressesAndCounter();
        _endOfBlockNot = false;
        _transferPhase = TransferPhase.SourceRead;
    }

    private void FinishBlock()
    {
        LastTransferLength = _currentTransferLength;
        _endOfBlockNot = false;
        _loaded = false;
        Stop();
    }

    /// <summary>
    /// Moves along a rectangle's row and on to the next row; false once the last row is done.
    /// A row length or number of rows of 0 still moves one byte.
    /// </summary>
    private bool AdvanceRow()
    {
        _rowPosition++;
        if (_rowPosition < Math.Max((int)_rowLength, 1))
        {
            return true;
        }

        _rowPosition = 0;
        _rowIndex++;
        if (_rowIndex >= Math.Max((int)_rowCount, 1))
        {
            return false;
        }

        _sourceRowStart = NextRowStart(_sourceRowStart, SourceIsPortA ? _portAStride : _portBStride);
        _destinationRowStart = NextRowStart(
            _destinationRowStart,
            SourceIsPortA ? _portBStride : _portAStride);
        _sourceAddress = (ushort)_sourceRowStart;
        _sourceHigh = (byte)(_sourceRowStart >> 16);
        _destinationAddress = (ushort)_destinationRowStart;
        _destinationHigh = (byte)(_destinationRowStart >> 16);
        return true;
    }

    /// <summary>
    /// A stride is added to the row's 24-bit start, so a physical-mode row can cross 64K;
    /// otherwise the address wraps within 64K.
    /// </summary>
    private int NextRowStart(int rowStart, ushort stride) => PhysicalMode
        ? (rowStart + stride) & 0xFF_FFFF
        : (rowStart & 0xFF_0000) | ((rowStart + stride) & 0xFFFF);

    /// <summary>
    /// Steps an address by its mode. In physical mode an increment carries from <c>$FFFF</c>
    /// into the extended byte and a decrement borrows at <c>$0000</c>.
    /// </summary>
    private void StepAddress(ref ushort address, ref byte high, AddressMode mode)
    {
        if (mode == AddressMode.Increment)
        {
            address++;
            if (address == 0 && PhysicalMode)
            {
                high++;
            }
        }
        else if (mode == AddressMode.Decrement)
        {
            if (address == 0 && PhysicalMode)
            {
                high--;
            }

            address--;
        }
    }

    private bool SourceIsPortA => _directionAtoB;

    private bool PhysicalMode => (_dimensionalFlags & PhysicalFlag) != 0;

    private bool FillMode => (_dimensionalFlags & FillFlag) != 0;

    /// <summary>
    /// The 22-bit physical address: the extended byte's bits 5:0 above the 16-bit address.
    /// The memory bus drops bit 21 on this 2 MB board.
    /// </summary>
    private static int PhysicalAddress(ushort address, byte high) => ((high & 0x3F) << 16) | address;

    private byte ReadEndpoint(ushort address, byte high, bool io)
    {
        if (io)
        {
            return _ports?.ReadUncontended(address) ?? (byte)0xFF;
        }

        return PhysicalMode
            ? _memory.ReadPhysicalSram(PhysicalAddress(address, high))
            : _memory.ReadDirect(address);
    }

    private void WriteEndpoint(ushort address, byte high, byte value, bool io)
    {
        if (io)
        {
            _ports?.WriteUncontended(address, value);
        }
        else if (PhysicalMode)
        {
            _memory.WritePhysicalSram(PhysicalAddress(address, high), value);
        }
        else
        {
            _memory.WriteDirect(address, value);
        }
    }

    private void SelectMode(ushort port)
    {
        _z80CompatibleMode = (byte)port == Z80ControlPort;
    }

    private static AddressMode DecodeAddressMode(byte value) => ((value >> 4) & 0x03) switch
    {
        0 => AddressMode.Decrement,
        1 => AddressMode.Increment,
        _ => AddressMode.Fixed
    };

    private static int DecodeTimingCycles(byte timing) => (timing & 0x03) switch
    {
        0 => 4,
        1 => 3,
        2 => 2,
        _ => 4
    };

    private ReadField FirstReadField()
    {
        for (int field = 0; field <= 6; field++)
        {
            if ((_readMask & (1 << field)) != 0)
            {
                return (ReadField)field;
            }
        }

        return ReadField.Status;
    }

    private void AdvanceReadField()
    {
        int current = (int)_readField;
        for (int offset = 1; offset <= 7; offset++)
        {
            int field = (current + offset) % 7;
            if ((_readMask & (1 << field)) != 0)
            {
                _readField = (ReadField)field;
                return;
            }
        }

        _readField = ReadField.Status;
    }

    private enum AddressMode : byte
    {
        Decrement,
        Increment,
        Fixed
    }

    // Stored in snapshots by number: append, never renumber.
    private enum PendingWrite : byte
    {
        None,
        Register0PortALow,
        Register0PortAHigh,
        Register0LengthLow,
        Register0LengthHigh,
        Register1Timing,
        Register1Prescaler,
        Register2Timing,
        Register2Prescaler,
        Register3Mask,
        Register3Match,
        Register4PortBLow,
        Register4PortBHigh,
        Register6ReadMask,
        Register4InterruptControl,
        DdmaPortAExtended,
        DdmaPortBExtended,
        DdmaFlags,
        DdmaLayer2XLow,
        DdmaLayer2XHigh,
        DdmaLayer2Y,
        DdmaRowLengthLow,
        DdmaRowLengthHigh,
        DdmaRowCountLow,
        DdmaRowCountHigh,
        DdmaPortAStrideLow,
        DdmaPortAStrideHigh,
        DdmaPortBStrideLow,
        DdmaPortBStrideHigh,
        DdmaTransparent,
        DdmaFill
    }

    private enum ReadField : byte
    {
        Status,
        CounterLow,
        CounterHigh,
        PortALow,
        PortAHigh,
        PortBLow,
        PortBHigh
    }

    // Stored in snapshots by number: append, never renumber.
    private enum TransferPhase : byte
    {
        SourceRead,
        DestinationWrite,
        HeldWait,
        BurstHold,
        BurstWait,
        AwaitingAck,
        Releasing,
        HoldOffRelease,
        HeldOff
    }
}
