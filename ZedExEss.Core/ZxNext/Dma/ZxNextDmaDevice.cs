using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Memory;
using ZedExEss.ZxNext.Ports;

namespace ZedExEss.ZxNext.Dma;

/// <summary>
/// Functional model of the ZX Spectrum Next DMA command interface.
/// </summary>
/// <remarks>
/// The native DMA port is used heavily for screen blits. Enabling a loaded transfer
/// asserts BUSREQ; the machine arbiter then advances its separately timed source and
/// destination cycles while the CPU yields the shared bus.
/// </remarks>
public sealed class ZxNextDmaDevice
{
    public const byte Z80ControlPort = 0x0B;
    public const byte ZxnControlPort = 0x6B;

    private readonly ZxNextMemoryBus _memory;
    private ZxNextPortBus? _ports;
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

    public ZxNextDmaDevice(ZxNextMemoryBus memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public bool IsActive { get; private set; }
    public bool BusRequestAsserted => IsActive;
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
    public int CpuClockCyclesUntilBusEvent => _transferPhase == TransferPhase.SourceRead
        ? DecodeTimingCycles(SourceIsPortA ? _portATiming : _portBTiming)
        : DecodeTimingCycles(SourceIsPortA ? _portBTiming : _portATiming);

    public bool HandlesPort(ushort port) => (byte)port is Z80ControlPort or ZxnControlPort;

    internal void ConfigurePorts(ZxNextPortBus ports)
    {
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
    }

    public byte Read(ushort port)
    {
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
            // WR5 READY/WAIT and auto-restart are retained by the FPGA but do not
            // alter an ordinary immediate memory transfer.
        }
        else if ((value & 0x80) != 0 && (value & 0x03) == 3)
        {
            WriteRegister6(value);
        }
    }

    public void Reset()
    {
        _pendingWrite = PendingWrite.None;
        _pendingHeader = 0;
        _readMask = 0x7F;
        _readField = ReadField.Status;
        _portAStart = 0;
        _portBStart = 0;
        _blockLength = 0;
        _sourceAddress = 0;
        _destinationAddress = 0;
        _counter = 0;
        _portATiming = 0x01;
        _portBTiming = 0x01;
        _portBPrescaler = 0;
        _transferByte = 0;
        _transferPhase = TransferPhase.SourceRead;
        _currentTransferLength = 0;
        _portAAddressMode = AddressMode.Increment;
        _portBAddressMode = AddressMode.Increment;
        _portAIsIo = false;
        _portBIsIo = false;
        _directionAtoB = false;
        _loaded = false;
        _endOfBlockNot = true;
        _transferredAtLeastOne = false;
        _z80CompatibleMode = false;
        IsActive = false;
        LastTransferLength = 0;
    }

    /// <summary>Captures the controller, mid-transfer and mid-register-write included.</summary>
    public ZxNextDmaState CaptureState() => new(
        IsActive,
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
        LastTransferLength);

    /// <summary>Puts a captured controller back without replaying any of its writes.</summary>
    public void RestoreState(ZxNextDmaState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        IsActive = state.IsActive;
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
            StartLoadedTransfer();
        }
    }

    private void WriteRegister4(byte value)
    {
        _pendingHeader = value;
        _pendingWrite = (value & 0x04) != 0
            ? PendingWrite.Register4PortBLow
            : (value & 0x08) != 0
                ? PendingWrite.Register4PortBHigh
                : PendingWrite.None;
    }

    private void WriteRegister6(byte value)
    {
        switch (value)
        {
            case 0xC3: // Reset DMA and status.
                _pendingWrite = PendingWrite.None;
                _loaded = false;
                IsActive = false;
                _portATiming = 0x01;
                _portBTiming = 0x01;
                _portBPrescaler = 0;
                _transferPhase = TransferPhase.SourceRead;
                _currentTransferLength = 0;
                _endOfBlockNot = true;
                _transferredAtLeastOne = false;
                LastTransferLength = 0;
                break;
            case 0xC7: // Reset port A timing.
                _portATiming = 0x01;
                break;
            case 0xCB: // Reset port B timing.
                _portBTiming = 0x01;
                break;
            case 0xCF: // Load programmed addresses and byte counter.
                LoadTransfer();
                break;
            case 0xD3: // Continue with the current address counters.
                _counter = _z80CompatibleMode ? ushort.MaxValue : (ushort)0;
                _currentTransferLength = 0;
                _transferPhase = TransferPhase.SourceRead;
                _endOfBlockNot = true;
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
            case 0x87: // Enable DMA.
                StartLoadedTransfer();
                break;
            case 0x83: // Disable DMA.
                IsActive = false;
                break;
            case 0xBB: // Read-mask byte follows.
                _pendingWrite = PendingWrite.Register6ReadMask;
                break;
        }
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
            case PendingWrite.Register6ReadMask:
                _readMask = value;
                _readField = FirstReadField();
                _pendingWrite = PendingWrite.None;
                break;
            default:
                _pendingWrite = PendingWrite.None;
                break;
        }
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
        _sourceAddress = _directionAtoB ? _portAStart : _portBStart;
        _destinationAddress = _directionAtoB ? _portBStart : _portAStart;
        _counter = _z80CompatibleMode ? ushort.MaxValue : (ushort)0;
        _endOfBlockNot = true;
        _loaded = true;
        _currentTransferLength = 0;
        _transferPhase = TransferPhase.SourceRead;
    }

    private void StartLoadedTransfer()
    {
        if (!_loaded || IsActive)
        {
            return;
        }

        IsActive = true;
    }

    /// <summary>Completes the endpoint cycle currently holding a DMA bus grant.</summary>
    internal void CompleteGrantedBusCycle()
    {
        if (!IsActive)
        {
            return;
        }

        if (_transferPhase == TransferPhase.SourceRead)
        {
            _transferByte = ReadEndpoint(
                _sourceAddress,
                SourceIsPortA ? _portAIsIo : _portBIsIo);
            _transferPhase = TransferPhase.DestinationWrite;
            return;
        }

        WriteEndpoint(
            _destinationAddress,
            _transferByte,
            SourceIsPortA ? _portBIsIo : _portAIsIo);
        if (!IsActive)
        {
            return;
        }

        _counter++;
        _currentTransferLength++;
        _transferredAtLeastOne = true;
        _sourceAddress = AdvanceAddress(
            _sourceAddress,
            SourceIsPortA ? _portAAddressMode : _portBAddressMode);
        _destinationAddress = AdvanceAddress(
            _destinationAddress,
            SourceIsPortA ? _portBAddressMode : _portAAddressMode);
        if (_currentTransferLength <= _blockLength)
        {
            _transferPhase = TransferPhase.SourceRead;
            return;
        }

        LastTransferLength = _currentTransferLength;
        _endOfBlockNot = false;
        _loaded = false;
        IsActive = false;
    }

    private bool SourceIsPortA => _directionAtoB;

    private byte ReadEndpoint(ushort address, bool io)
        => io ? _ports?.ReadUncontended(address) ?? (byte)0xFF : _memory.ReadDirect(address);

    private void WriteEndpoint(ushort address, byte value, bool io)
    {
        if (io)
        {
            _ports?.WriteUncontended(address, value);
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

    private static ushort AdvanceAddress(ushort address, AddressMode mode) => mode switch
    {
        AddressMode.Decrement => (ushort)(address - 1),
        AddressMode.Increment => (ushort)(address + 1),
        _ => address
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
        Register6ReadMask
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

    private enum TransferPhase : byte
    {
        SourceRead,
        DestinationWrite
    }
}
