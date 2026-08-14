using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Interface1;

/// <summary>Current host-visible operation of the selected Microdrive.</summary>
public enum MicrodriveActivityState
{
    Idle,
    Reading,
    Writing
}

/// <summary>
/// Interface 1 ROMCS mapper, ULA ports and eight-drive Microdrive transport.
/// </summary>
/// <remarks>
/// The 8 KiB Interface 1 ROM is mirrored across the Spectrum's low 16 KiB.
/// ROMCS entry and exit are driven only by opcode fetches. Microdrive media is
/// represented separately from each drive's rotating head state, allowing an MDR
/// image to be removed and reinserted without serialising transient GAP/SYNC state.
/// </remarks>
public sealed class SpectrumInterface1Device : IPortDevice
{
    public const int RomSize = 8 * 1024;
    public const int DriveCount = 8;

    // The IF1 ULA inverts these two control-register outputs. A clear R/W
    // bit therefore selects the write head, while a clear ERASE bit enables
    // the erase head mounted slightly ahead of it. The Sinclair ROM uses
    // EEh (read), E6h (erase lead-in), E2h (erase + write), then E6h/EEh.
    private const byte ReadWriteMask = 0x04;
    private const byte EraseMask = 0x08;

    private readonly byte[] _rom;
    private readonly DriveTransport[] _drives = new DriveTransport[DriveCount];
    private bool _paged;
    private byte _control;
    private byte _networkOutput;
    private byte _motorMask;
    private MicrodriveActivityState _activity;
    private ISpectrumInterface1Rs232Endpoint? _rs232Endpoint;
    private SpectrumInterface1NetworkStation? _networkStation;
    private ulong _busTstate;
    private bool _networkWaitPending;
    private int _rs232InputPhase;
    private int _rs232OutputPhase;
    private byte _rs232InputShift;
    private byte _rs232OutputShift;
    private bool _rs232InputLine;
    private bool _rs232OutputLine;

    public SpectrumInterface1Device(ReadOnlySpan<byte> firmwareRom)
    {
        if (firmwareRom.Length != RomSize)
        {
            throw new ArgumentException(
                $"Interface 1 firmware ROM must be {RomSize} bytes.",
                nameof(firmwareRom));
        }

        _rom = firmwareRom.ToArray();
        for (int i = 0; i < _drives.Length; i++)
        {
            _drives[i] = new DriveTransport();
        }

        Reset();
    }

    public bool IsPaged => _paged;
    public byte MotorMask => _motorMask;
    public byte Control => _control;
    public bool MicrodriveWriteEnabled => (_control & ReadWriteMask) == 0;
    public bool MicrodriveEraseEnabled => (_control & EraseMask) == 0;
    public byte NetworkOutput => _networkOutput;
    public bool Rs232Attached => _rs232Endpoint != null;
    public bool NetworkAttached => _networkStation?.IsAttached == true;
    public bool NetworkWaitEnabled => (_control & 0x21) == 0;
    public MicrodriveActivityState Activity => _activity;

    /// <summary>
    /// One-based number of the first selected drive, or zero while all motors are off.
    /// The shift register can briefly contain more than one bit while the ROM moves the
    /// selection along the daisy chain; exposing the first bit keeps host status stable.
    /// </summary>
    public int SelectedDriveNumber
    {
        get
        {
            for (int i = 0; i < DriveCount; i++)
            {
                if ((_motorMask & (1 << i)) != 0)
                {
                    return i + 1;
                }
            }

            return 0;
        }
    }

    /// <summary>
    /// Raised only when the selected drive or read/write state changes. The event is
    /// intentionally not raised per byte, so an open UI cannot burden the transport path.
    /// </summary>
    public event Action? StatusChanged;

    /// <summary>Captures latches and exact byte-stream position for all drives.</summary>
    public SpectrumInterface1DeviceState CaptureState()
    {
        var drives = new MicrodriveTransportState[_drives.Length];
        for (int i = 0; i < _drives.Length; i++)
        {
            drives[i] = _drives[i].CaptureState();
        }

        return new SpectrumInterface1DeviceState(
            _paged,
            _control,
            _networkOutput,
            _motorMask,
            _activity,
            new SpectrumInterface1Rs232TransportState(
                _rs232InputPhase,
                _rs232OutputPhase,
                _rs232InputShift,
                _rs232OutputShift,
                _rs232InputLine,
                _rs232OutputLine),
            drives);
    }

    /// <summary>
    /// Restores transient device state after the matching cartridges have been
    /// inserted. No emulated port accesses are generated during restoration.
    /// </summary>
    public void RestoreState(SpectrumInterface1DeviceState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        for (int i = 0; i < _drives.Length; i++)
        {
            bool motorOn = (state.MotorMask & (1 << i)) != 0;
            _drives[i].ValidateState(state.GetDrive(i), motorOn);
        }

        _paged = state.IsPaged;
        _control = state.Control;
        _networkOutput = state.NetworkOutput;
        _motorMask = state.MotorMask;
        _activity = state.Activity;
        _networkWaitPending = false;
        _rs232InputPhase = state.Rs232.InputPhase;
        _rs232OutputPhase = state.Rs232.OutputPhase;
        _rs232InputShift = state.Rs232.InputShiftRegister;
        _rs232OutputShift = state.Rs232.OutputShiftRegister;
        _rs232InputLine = state.Rs232.InputLine;
        _rs232OutputLine = state.Rs232.OutputLine;
        for (int i = 0; i < _drives.Length; i++)
        {
            bool motorOn = (_motorMask & (1 << i)) != 0;
            _drives[i].RestoreState(state.GetDrive(i), motorOn);
        }

        UpdateNetworkOutput();

        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Resets Interface 1 latches and transport positions without ejecting media.
    /// </summary>
    public void Reset()
    {
        bool statusChanged = _motorMask != 0 || _activity != MicrodriveActivityState.Idle;
        _paged = false;
        _control = 0;
        // A high ULA output is inverted by the Interface 1 transistor stage and
        // therefore releases the resting-low network wire.
        _networkOutput = 1;
        _motorMask = 0;
        _activity = MicrodriveActivityState.Idle;
        _networkWaitPending = false;
        ResetRs232Framing();
        UpdateNetworkOutput();
        _rs232Endpoint?.SetClearToSend(false);
        for (int i = 0; i < _drives.Length; i++)
        {
            _drives[i].Reset();
        }

        if (statusChanged)
        {
            StatusChanged?.Invoke();
        }
    }

    public void InsertCartridge(int driveNumber, MicrodriveCartridge cartridge)
    {
        ArgumentNullException.ThrowIfNull(cartridge);
        GetDrive(driveNumber).Insert(cartridge);
        if (IsMotorRunning(driveNumber))
        {
            StatusChanged?.Invoke();
        }
    }

    /// <summary>
    /// Connects a byte-oriented host endpoint. Disconnecting resets only an incomplete
    /// serial frame; Microdrive and ROMCS state are left untouched.
    /// </summary>
    public void AttachRs232Endpoint(ISpectrumInterface1Rs232Endpoint? endpoint)
    {
        _rs232Endpoint = endpoint;
        ResetRs232Framing();
        endpoint?.SetClearToSend((_control & 0x10) != 0);
    }

    /// <summary>
    /// Connects this Interface 1 to one station on a shared ZX Net wire. The station
    /// lifetime is owned by the session which created it, not by this device.
    /// </summary>
    public void AttachNetworkStation(SpectrumInterface1NetworkStation? station)
    {
        if (ReferenceEquals(_networkStation, station))
        {
            return;
        }

        _networkStation?.SetOutput(
            ulaOutputHigh: true,
            networkSelected: false,
            tstate: _busTstate);
        _networkStation = station;
        UpdateNetworkOutput();
    }

    /// <summary>Sets the T-state at which the current port access reaches the IF1 ULA.</summary>
    public void SetBusTstate(ulong tstate)
    {
        _busTstate = tstate;
    }

    /// <summary>
    /// Reports a pending IF1 ULA processor WAIT request. The ROM writes EFh with bit
    /// 5 clear once per incoming byte while the network pulse is active. That write
    /// holds the next CPU cycle until the wire rests; later marker transitions must
    /// remain visible to the INPAK polling loop and do not retrigger the same request.
    /// </summary>
    public bool TryGetNetworkWait(ulong tstate, out ulong releaseTstate)
    {
        SpectrumInterface1NetworkStation? station = _networkStation;
        if (!_networkWaitPending || !NetworkWaitEnabled || station?.Sample(tstate) != true)
        {
            _networkWaitPending = false;
            releaseTstate = tstate;
            return false;
        }

        if (!station.TryGetNextRestingTstate(tstate, out releaseTstate))
        {
            releaseTstate = 0;
        }

        return true;
    }

    public MicrodriveCartridge? EjectCartridge(int driveNumber)
    {
        MicrodriveCartridge? cartridge = GetDrive(driveNumber).Eject();
        if (IsMotorRunning(driveNumber))
        {
            _activity = MicrodriveActivityState.Idle;
            StatusChanged?.Invoke();
        }

        return cartridge;
    }

    public MicrodriveCartridge? GetCartridge(int driveNumber)
    {
        return GetDrive(driveNumber).Cartridge;
    }

    public bool IsMotorRunning(int driveNumber)
    {
        return GetDrive(driveNumber).MotorOn;
    }

    public void BeforeOpcodeFetch(ushort pc)
    {
        if (pc is 0x0008 or 0x1708)
        {
            _paged = true;
        }
    }

    public void AfterOpcodeFetch(ushort pc)
    {
        if (pc == 0x0700)
        {
            _paged = false;
        }
    }

    public byte ReadMemory(ushort address)
    {
        if (!_paged || address >= 0x4000)
        {
            return 0xFF;
        }

        return _rom[address & 0x1FFF];
    }

    public bool HandlesPort(ushort port)
    {
        // Interface 1 only decodes A3 and A4, giving each nominal port aliases
        // throughout the complete Z80 I/O address space.
        return (port & 0x0018) != 0x0018;
    }

    public byte Read(ushort port)
    {
        return (byte)((port & 0x0018) switch
        {
            0x0000 => ReadMicrodriveData(),
            0x0008 => ReadControlStatus(),
            0x0010 => ReadNetwork(),
            _ => 0xFF
        });
    }

    public void Write(ushort port, byte value)
    {
        switch (port & 0x0018)
        {
            case 0x0000:
                WriteMicrodriveData(value);
                break;

            case 0x0008:
                WriteControl(value);
                break;

            case 0x0010:
                _networkOutput = (byte)(value & 0x01);
                if ((_control & 0x01) != 0)
                {
                    WriteCommunications(value);
                }
                else
                {
                    UpdateNetworkOutput();
                }
                break;
        }
    }

    private byte ReadMicrodriveData()
    {
        if (HasSelectedCartridge())
        {
            SetActivity(MicrodriveActivityState.Reading);
        }

        byte value = 0xFF;
        for (int i = 0; i < _drives.Length; i++)
        {
            value &= _drives[i].ReadData();
        }

        return value;
    }

    private byte ReadControlStatus()
    {
        if (HasSelectedCartridge())
        {
            SetActivity(MicrodriveActivityState.Reading);
        }

        byte value = 0xFF;
        for (int i = 0; i < _drives.Length; i++)
        {
            value &= _drives[i].ReadStatus();
        }

        // DTR is active-low. BUSY follows the physical ZX Net wire, which rests low
        // and rises while any attached output stage asserts it.
        // A connected endpoint raises DTR; an absent endpoint retains the real
        // disconnected value used by the Interface 1 ROM's OPEN checks.
        if (_rs232Endpoint?.DataTerminalReady != true)
        {
            value &= 0xF7;
        }

        if (_networkStation?.Sample(_busTstate) != true)
        {
            value &= 0xEF;
        }
        RestartTransports();
        return value;
    }

    private byte ReadNetwork()
    {
        AdvanceRs232Input();
        RestartTransports();
        byte value = 0x7E; // A disconnected ZX Net wire rests low.
        if (_rs232InputLine)
        {
            value |= 0x80;
        }


        if (_networkStation?.Sample(_busTstate) == true)
        {
            value |= 0x01;
        }

        return value;
    }

    private void AdvanceRs232Input()
    {
        ISpectrumInterface1Rs232Endpoint? endpoint = _rs232Endpoint;
        bool clearToSend = (_control & 0x10) != 0;
        if (endpoint == null || !clearToSend)
        {
            _rs232InputPhase = 0;
            _rs232InputLine = false;
            return;
        }

        if (_rs232InputPhase == 0)
        {
            if (endpoint.TryReadByte(out byte value))
            {
                _rs232InputShift = value;
                _rs232InputPhase = 1;
            }

            _rs232InputLine = false;
        }
        else if (_rs232InputPhase < 5)
        {
            _rs232InputLine = true;
            _rs232InputPhase++;
        }
        else if (_rs232InputPhase < 13)
        {
            // The IF1 level shifter inverts RS232 data at the ULA input.
            _rs232InputLine = (_rs232InputShift & 0x01) == 0;
            _rs232InputShift >>= 1;
            _rs232InputPhase++;
        }
        else
        {
            _rs232InputPhase = 0;
        }
    }

    private void WriteCommunications(byte value)
    {
        _rs232OutputLine = (value & 0x01) != 0;
        if (_rs232Endpoint == null || (_control & 0x01) == 0)
        {
            return;
        }

        bool line = _rs232OutputLine;
        bool framingError = false;
        if (_rs232OutputPhase == 0)
        {
            if (!line)
            {
                _rs232OutputPhase = 1;
            }

            return;
        }

        if (_rs232OutputPhase == 1)
        {
            if ((_control & 0x10) != 0 || !line)
            {
                framingError = true;
            }
            else
            {
                _rs232OutputPhase = 2;
            }
        }
        else if (_rs232OutputPhase <= 9)
        {
            _rs232OutputShift >>= 1;
            if (!line)
            {
                _rs232OutputShift |= 0x80;
            }

            _rs232OutputPhase++;
        }
        else if (_rs232OutputPhase <= 11)
        {
            framingError = line;
            _rs232OutputPhase++;
        }
        else if (_rs232OutputPhase == 12)
        {
            framingError = !line;
            _rs232OutputPhase++;
        }
        else
        {
            framingError = line;
            CompleteRs232Output(framingError ? (byte)'?' : _rs232OutputShift);
            return;
        }

        if (framingError)
        {
            CompleteRs232Output((byte)'?');
        }
    }

    private void CompleteRs232Output(byte value)
    {
        _rs232Endpoint?.WriteByte(value);
        _rs232OutputPhase = 0;
        _rs232OutputShift = 0;
    }

    private void WriteMicrodriveData(byte value)
    {
        // Writes to the data register cannot reach the head while R/W is in
        // read mode. This matters for custom IF1 software which may touch E7h
        // while polling; treating every access as a physical write can damage
        // the logical sector stream and differs from the real ULA.
        if (!MicrodriveWriteEnabled)
        {
            return;
        }

        if (HasSelectedCartridge())
        {
            SetActivity(MicrodriveActivityState.Writing);
        }

        for (int i = 0; i < _drives.Length; i++)
        {
            _drives[i].WriteData(value);
        }
    }

    private void WriteControl(byte value)
    {
        byte oldMotorMask = _motorMask;
        bool oldMediaWriteActive = MicrodriveWriteEnabled || MicrodriveEraseEnabled;
        bool oldClockHigh = (_control & 0x02) != 0;
        bool newClockHigh = (value & 0x02) != 0;
        if (oldClockHigh && !newClockHigh)
        {
            // Falling CLK shifts existing selections towards drive 8. DATA is
            // active-low and supplies the new drive-1 motor state.
            int driveOne = (value & 0x01) == 0 ? 1 : 0;
            _motorMask = (byte)(((_motorMask << 1) | driveOne) & 0xFF);
            for (int i = 0; i < _drives.Length; i++)
            {
                _drives[i].MotorOn = (_motorMask & (1 << i)) != 0;
            }
        }

        _control = value;
        // Each write with WAIT low creates one synchronization request. It is
        // consumed when the currently active network pulse returns to rest; merely
        // leaving bit 5 low must not hide subsequent marker bits from INPAK.
        _networkWaitPending = (value & 0x21) == 0 && _networkStation != null;
        _rs232Endpoint?.SetClearToSend((value & 0x10) != 0);
        UpdateNetworkOutput();
        RestartTransports();

        bool newMediaWriteActive = MicrodriveWriteEnabled || MicrodriveEraseEnabled;
        if (HasSelectedCartridge() && newMediaWriteActive)
        {
            // E6h starts the erase head before the write head is enabled, so
            // the host-visible activity is already a write operation here.
            SetActivity(MicrodriveActivityState.Writing);
        }
        else if (oldMediaWriteActive && !newMediaWriteActive &&
                 _activity == MicrodriveActivityState.Writing)
        {
            SetActivity(MicrodriveActivityState.Idle);
        }

        if (_motorMask != oldMotorMask)
        {
            _activity = MicrodriveActivityState.Idle;
            StatusChanged?.Invoke();
        }
    }

    private void ResetRs232Framing()
    {
        _rs232InputPhase = 0;
        _rs232OutputPhase = 0;
        _rs232InputShift = 0;
        _rs232OutputShift = 0;
        _rs232InputLine = false;
        _rs232OutputLine = false;
    }

    private void UpdateNetworkOutput()
    {
        _networkStation?.SetOutput(
            ulaOutputHigh: (_networkOutput & 0x01) != 0,
            networkSelected: (_control & 0x01) == 0,
            tstate: _busTstate);
    }

    private bool HasSelectedCartridge()
    {
        for (int i = 0; i < _drives.Length; i++)
        {
            if (_drives[i].MotorOn && _drives[i].Cartridge != null)
            {
                return true;
            }
        }

        return false;
    }

    private void SetActivity(MicrodriveActivityState activity)
    {
        if (_activity == activity)
        {
            return;
        }

        _activity = activity;
        StatusChanged?.Invoke();
    }

    private void RestartTransports()
    {
        for (int i = 0; i < _drives.Length; i++)
        {
            _drives[i].RestartTransfer();
        }
    }

    private DriveTransport GetDrive(int driveNumber)
    {
        if (driveNumber is < 1 or > DriveCount)
        {
            throw new ArgumentOutOfRangeException(nameof(driveNumber));
        }

        return _drives[driveNumber - 1];
    }

    /// <summary>
    /// Runtime state of one drive. Byte availability is advanced by IF1 port
    /// accesses, as expected by the ROM's polling loops; no wall-clock state leaks
    /// into cartridge persistence.
    /// </summary>
    private sealed class DriveTransport
    {
        private const int GapLength = 15;
        private const int SyncLength = 15;
        private const int PreambleLength = 12;

        private int _headPosition;
        private int _transferred;
        private int _maximumTransfer;
        private int _gap;
        private int _sync;
        private byte _lastByte;

        public MicrodriveCartridge? Cartridge { get; private set; }
        public bool MotorOn { get; set; }

        public MicrodriveTransportState CaptureState()
        {
            return new MicrodriveTransportState(
                _headPosition,
                _transferred,
                _maximumTransfer,
                _gap,
                _sync,
                _lastByte);
        }

        public void ValidateState(MicrodriveTransportState state, bool motorOn)
        {
            if (state.Transferred < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(state), "Transferred byte count cannot be negative.");
            }

            int recordLength = MicrodriveCartridge.HeaderLength + MicrodriveCartridge.DataLength + 1;
            if (state.MaximumTransfer != MicrodriveCartridge.HeaderLength &&
                state.MaximumTransfer != recordLength)
            {
                throw new ArgumentOutOfRangeException(nameof(state), "Invalid Microdrive transfer length.");
            }

            if (state.Gap is < 0 or > GapLength || state.Sync is < 0 or > SyncLength)
            {
                throw new ArgumentOutOfRangeException(nameof(state), "Invalid GAP/SYNC counter.");
            }

            if (Cartridge == null)
            {
                if (state.HeadPosition != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(state), "An empty drive cannot have a non-zero head position.");
                }

                if (motorOn)
                {
                    // A running empty mechanism is valid; only cartridge-relative
                    // state must remain at its reset position.
                    return;
                }

                return;
            }

            if ((uint)state.HeadPosition >= (uint)Cartridge.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(state), "Microdrive head position lies outside the cartridge.");
            }
        }

        public void RestoreState(MicrodriveTransportState state, bool motorOn)
        {
            _headPosition = state.HeadPosition;
            _transferred = state.Transferred;
            _maximumTransfer = state.MaximumTransfer;
            _gap = state.Gap;
            _sync = state.Sync;
            _lastByte = state.LastByte;
            MotorOn = motorOn;
        }

        public void Reset()
        {
            MotorOn = false;
            _headPosition = 0;
            _transferred = 0;
            _maximumTransfer = MicrodriveCartridge.HeaderLength;
            _gap = GapLength;
            _sync = SyncLength;
            _lastByte = 0xFF;
        }

        public void Insert(MicrodriveCartridge cartridge)
        {
            Cartridge = cartridge;
            _headPosition = 0;
            _transferred = 0;
            _maximumTransfer = MicrodriveCartridge.HeaderLength;
            _gap = GapLength;
            _sync = SyncLength;
            _lastByte = 0xFF;
        }

        public MicrodriveCartridge? Eject()
        {
            MicrodriveCartridge? cartridge = Cartridge;
            Cartridge = null;
            _headPosition = 0;
            _transferred = 0;
            _lastByte = 0xFF;
            return cartridge;
        }

        public byte ReadData()
        {
            MicrodriveCartridge? cartridge = Cartridge;
            if (!MotorOn || cartridge == null)
            {
                return 0xFF;
            }

            if (_transferred < _maximumTransfer)
            {
                _lastByte = cartridge.ReadByte(_headPosition);
                IncrementHead(cartridge);
            }

            _transferred++;
            return _lastByte;
        }

        public byte ReadStatus()
        {
            MicrodriveCartridge? cartridge = Cartridge;
            if (!MotorOn || cartridge == null)
            {
                return 0xFF;
            }

            byte value = 0xFF;
            int section = GetSection(cartridge);
            if (cartridge.GetPreambleState(section) == byte.MaxValue)
            {
                if (_gap > 0)
                {
                    _gap--;
                }
                else
                {
                    // GAP and SYNC are active-low at the control/status port.
                    value &= 0xF9;
                    if (_sync > 0)
                    {
                        _sync--;
                    }
                    else
                    {
                        _gap = GapLength;
                        _sync = SyncLength;
                    }
                }
            }

            if (cartridge.WriteProtected)
            {
                value &= 0xFE;
            }

            return value;
        }

        public void WriteData(byte value)
        {
            MicrodriveCartridge? cartridge = Cartridge;
            if (!MotorOn || cartridge == null)
            {
                return;
            }

            int section = GetSection(cartridge);
            if (_transferred == 0 && value == 0x00)
            {
                cartridge.BeginPreamble(section);
            }
            else if (_transferred is > 0 and < 10 && value == 0x00)
            {
                cartridge.ContinuePreamble(section);
            }
            else if (_transferred is > 9 and < PreambleLength && value == 0xFF)
            {
                cartridge.ContinuePreamble(section);
            }
            else if (_transferred == PreambleLength && cartridge.GetPreambleState(section) == PreambleLength)
            {
                cartridge.CompletePreamble(section);
            }

            if (_transferred >= PreambleLength && _transferred < _maximumTransfer + PreambleLength)
            {
                // The tape continues to move beneath the head even when the
                // cartridge's write-protect tab prevents the byte changing.
                _ = cartridge.TryWriteByte(_headPosition, value);
                IncrementHead(cartridge);
            }

            _transferred++;
        }

        public void RestartTransfer()
        {
            MicrodriveCartridge? cartridge = Cartridge;
            if (cartridge == null)
            {
                _transferred = 0;
                return;
            }

            int sectorOffset = _headPosition % MicrodriveCartridge.SectorLength;
            while (sectorOffset != 0 && sectorOffset != MicrodriveCartridge.HeaderLength)
            {
                IncrementHead(cartridge);
                sectorOffset = _headPosition % MicrodriveCartridge.SectorLength;
            }

            _transferred = 0;
            _maximumTransfer = sectorOffset == 0
                ? MicrodriveCartridge.HeaderLength
                : MicrodriveCartridge.HeaderLength + MicrodriveCartridge.DataLength + 1;
        }

        private int GetSection(MicrodriveCartridge cartridge)
        {
            int block = _headPosition / MicrodriveCartridge.SectorLength;
            return _maximumTransfer == MicrodriveCartridge.HeaderLength
                ? block
                : cartridge.SectorCount + block;
        }

        private void IncrementHead(MicrodriveCartridge cartridge)
        {
            _headPosition++;
            if (_headPosition >= cartridge.Length)
            {
                _headPosition = 0;
            }
        }
    }
}
