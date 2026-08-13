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

    private readonly byte[] _rom;
    private readonly DriveTransport[] _drives = new DriveTransport[DriveCount];
    private bool _paged;
    private byte _control;
    private byte _networkOutput;
    private byte _motorMask;
    private MicrodriveActivityState _activity;

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
    public byte NetworkOutput => _networkOutput;
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

    /// <summary>
    /// Resets Interface 1 latches and transport positions without ejecting media.
    /// </summary>
    public void Reset()
    {
        bool statusChanged = _motorMask != 0 || _activity != MicrodriveActivityState.Idle;
        _paged = false;
        _control = 0;
        _networkOutput = 0;
        _motorMask = 0;
        _activity = MicrodriveActivityState.Idle;
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

        // No attached RS232 endpoint or Sinclair Network peer: DTR and BSY are
        // active-low and therefore clear, matching the disconnected IF1 state.
        value &= 0xE7;
        RestartTransports();
        return value;
    }

    private byte ReadNetwork()
    {
        RestartTransports();
        return 0x7E;
    }

    private void WriteMicrodriveData(byte value)
    {
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
        RestartTransports();

        if (_motorMask != oldMotorMask)
        {
            _activity = MicrodriveActivityState.Idle;
            StatusChanged?.Invoke();
        }
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
