using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System;
using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Disk.Beta
{
    /// <summary>
    /// WD179x/Beta 128 controller used by TR-DOS compatible Pentagon and Scorpion machines.
    /// </summary>
    /// <remarks>
    /// Register accesses expose WD status, DRQ and INTRQ semantics while transfers
    /// are backed by sector queues. Ready and timeout deadlines use emulated CPU
    /// T-states, so host performance cannot alter software-visible disk timing.
    /// </remarks>
    public sealed class SpectrumBeta128DiskController(SpectrumBeta128Device mapper) : IPortDevice
    {
        private const byte StatusCommandPort = 0x1F;
        private const byte TrackPort = 0x3F;
        private const byte SectorPort = 0x5F;
        private const byte DataPort = 0x7F;
        private const byte SystemPort = 0xFF;

        private const byte StatusBusy = 0x01;
        private const byte StatusDrq = 0x02;
        private const byte StatusTrackZero = 0x04;
        private const byte StatusLostData = 0x04;
        private const byte StatusRecordNotFound = 0x10;
        private const byte StatusSpinUp = 0x20;
        private const byte StatusWriteProtect = 0x40;
        private const byte StatusNotReady = 0x80;
        private const int InitialReadGapPolls = 1;
        private const int TypeTwoHeadLoadDelayMs = 30;
        private const int MultiSectorGapDelayMs = 20;
        private const int TransferTimeoutMs = 1000;
        private readonly SpectrumBeta128Device _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        private readonly TrdDiskImage?[] _drives = new TrdDiskImage?[4];
        // Data transfers are byte-streamed through the WD data register. The
        // queues model DRQ-visible transfer state without tying disk images to CPU timing.
        private readonly Queue<byte> _readBuffer = [];
        private readonly List<byte> _writeBuffer = [];
        private byte _track;
        private byte _sector = 1;
        private byte _data;
        private byte _status = StatusTrackZero;
        private byte _system = 0x18;
        private int _writeBytesRemaining;
        private int _writeTrack;
        private int _writeSide;
        private int _writeSector;
        private bool _readCommandActive;
        private bool _readMultiSector;
        private int _readTrack;
        private int _readSide;
        private int _readNextSector;
        private int _readLastSector;
        private int _readGapPolls;
        private ulong _readReadyTstate;
        private ulong _readDataTimeoutTstate;
        private ulong _writeDataTimeoutTstate;
        private ulong _busTstate;
        private int _cpuClockHz = 3_500_000;
        private long _activityCounter;
        private bool _interruptRequest;
        private bool _readAddressTransfer;
        private bool _readAddressPending;
        private byte _readAddressTrack;
        private byte _readAddressSide;
        private byte _readAddressSector;
        private ulong _readAddressReadyTstate;
        private bool _readAddressByteReady;
        private ulong _readAddressNextByteTstate;
        private bool _typeOneStatus = true;
        private bool _headLoaded;
        private bool _traceEnabled;
        private string? _tracePath;
        public long ActivityCounter => Interlocked.Read(ref _activityCounter);
        internal byte LastCommand { get; private set; }
        internal long CommandCount { get; private set; }
        internal bool TransferActive => _readCommandActive || _readAddressPending || _readBuffer.Count > 0 || _writeBytesRemaining > 0;
        public void ConfigureCpuClock(int cpuClockHz)
        {
            if (cpuClockHz > 0)
            {
                _cpuClockHz = cpuClockHz;
            }
        }
        public void SetBusTstate(ulong tstate)
        {
            _busTstate = tstate;
        }
        public void ConfigureTracing(bool enabled, string? path)
        {
            _traceEnabled = enabled;
            _tracePath = enabled ? path : null;

            if (!enabled)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    string? directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(path, $"ZedExEss Beta 128 FDC trace started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Beta 128 FDC trace file unavailable: {ex.Message}");
                    _tracePath = null;
                }
            }

            Trace("TRACE enabled");
        }
        public void InsertDisk(int drive, TrdDiskImage image)
        {
            if ((uint)drive >= _drives.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(drive));
            }

            _drives[drive] = image ?? throw new ArgumentNullException(nameof(image));
            // Media changes abort in-flight transfers but do not reset the whole machine.
            AbortTransfer();
            _typeOneStatus = true;
            _status = 0;
            UpdateTrackZeroStatus();
            Trace($"INSERT drive={drive} path=\"{image.Path}\" tracks={image.TrackCount} sides={image.SideCount} ro={image.IsWriteProtected}");
        }
        public void EjectDisk(int drive)
        {
            if ((uint)drive >= _drives.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(drive));
            }

            _drives[drive] = null;
            AbortTransfer();
            _typeOneStatus = true;
            _status = StatusNotReady;
            UpdateTrackZeroStatus();
            Trace($"EJECT drive={drive}");
        }
        public bool HandlesPort(ushort port)
        {
            if (!_mapper.IsPaged)
            {
                // Beta 128 ports are only visible while TR-DOS ROM is paged in.
                return false;
            }

            return (byte)port is StatusCommandPort or TrackPort or SectorPort or DataPort or SystemPort;
        }
        public byte Read(ushort port)
        {
            MarkActivity();
            return (byte)port switch
            {
                StatusCommandPort => ReadStatus(),
                TrackPort => TraceRead("TR", _track),
                SectorPort => TraceRead("SEC", _sector),
                DataPort => ReadData(),
                SystemPort => ReadSystem(),
                _ => 0xFF
            };
        }
        public void Write(ushort port, byte value)
        {
            MarkActivity();
            switch ((byte)port)
            {
                case StatusCommandPort:
                    ExecuteCommand(value);
                    break;
                case TrackPort:
                    _track = value;
                    UpdateTrackZeroStatus();
                    Trace($"WRITE TR value={value:X2}");
                    break;
                case SectorPort:
                    _sector = value;
                    Trace($"WRITE SEC value={value:X2}");
                    break;
                case DataPort:
                    WriteData(value);
                    break;
                case SystemPort:
                    WriteSystem(value);
                    break;
            }
        }
        private byte ReadStatus()
        {
            AdvanceControllerState();
            _interruptRequest = false;
            // The same status bits have different meanings for type I and type II/III commands.
            // Build the value from current controller state rather than keeping one stale latch.
            byte status = (byte)(_status & unchecked((byte)~(StatusBusy | StatusDrq | StatusNotReady)));
            // On a Beta 128 the WD1793 HLD output is wired back to READY. Media
            // presence therefore does not directly drive NOT READY; a loaded head
            // does. TR-DOS depends on this unusual wiring while probing the drive.
            if (!_headLoaded)
            {
                status |= StatusNotReady;
            }

            bool readDrq = HasReadDataRequest;
            if (_readCommandActive || _readBuffer.Count > 0 || _writeBytesRemaining > 0)
            {
                status |= StatusBusy;
                if (readDrq || _writeBytesRemaining > 0)
                {
                    status |= StatusDrq;
                }
                else
                {
                    status &= unchecked((byte)~StatusDrq);
                }
            }
            else
            {
                status &= unchecked((byte)~(StatusBusy | StatusDrq));
            }

            if (_typeOneStatus && _track == 0)
            {
                status |= StatusTrackZero;
            }

            if (_typeOneStatus)
            {
                if (_headLoaded)
                {
                    status |= StatusSpinUp;
                }

                if (IsIndexPulseActive())
                {
                    status |= StatusDrq;
                }
                else
                {
                    status &= unchecked((byte)~StatusDrq);
                }
            }
            else
            {
                status &= unchecked((byte)~StatusSpinUp);
            }

            return TraceRead("STATUS", status);
        }
        private byte ReadSystem()
        {
            AdvanceControllerState();
            byte value = 0;
            if (HasReadDataRequest || _writeBytesRemaining > 0)
            {
                // TR-DOS watches the system register DRQ mirror as well as the WD status port.
                value |= 0x40;
            }

            if (_interruptRequest)
            {
                value |= 0x80;
            }

            return TraceRead("SYSTEM", value);
        }
        private byte ReadData()
        {
            AdvanceControllerState();
            if (!HasReadDataRequest)
            {
                return TraceRead("DATA idle", _data);
            }

            // Reading the data port consumes exactly one byte from the current WD transfer.
            _data = _readBuffer.Dequeue();
            if (_readAddressTransfer && _readBuffer.Count > 0)
            {
                // The FD1793 drops DRQ after every data-register access and raises
                // it again when the next byte reaches the data separator. FUSE uses
                // a 30 us byte interval; exposing the whole six-byte ID continuously
                // lets direct loaders overrun their polling loop.
                _readAddressByteReady = false;
                _readAddressNextByteTstate = _busTstate + MicrosecondsToTstates(30);
            }

            if (_readBuffer.Count == 0)
            {
                _readDataTimeoutTstate = 0;

                if (_readAddressTransfer)
                {
                    _status = 0;
                    _sector = _readAddressTrack;
                    _readAddressTransfer = false;
                    _readAddressByteReady = false;
                    _readAddressNextByteTstate = 0;
                    _readCommandActive = false;
                    UpdateTrackZeroStatus();
                    _interruptRequest = true;
                    Trace($"DATA transfer complete status={_status:X2} intrq={_interruptRequest}");
                    return _data;
                }

                if (_readMultiSector && _readNextSector <= _readLastSector)
                {
                    // Multi-sector reads pause between sectors. Some loaders poll the system
                    // register during this gap, so the next sector is deferred instead of queued immediately.
                    _sector = (byte)_readNextSector;
                    _status = StatusBusy;
                    _readGapPolls = 0;
                    _readReadyTstate = _busTstate + MillisecondsToTstates(MultiSectorGapDelayMs);
                    UpdateTrackZeroStatus();
                    _interruptRequest = false;
                    Trace($"READ SECTOR gap before drive={SelectedDrive} track={_readTrack} side={_readSide} sector={_readNextSector} ready={_readReadyTstate}");
                    return _data;
                }

                _status = 0;
                _readCommandActive = false;
                _readMultiSector = false;
                UpdateTrackZeroStatus();
                _interruptRequest = true;
                Trace($"DATA transfer complete status={_status:X2} intrq={_interruptRequest}");
            }

            return _data;
        }
        private void WriteData(byte value)
        {
            _data = value;
            if (_writeBytesRemaining <= 0)
            {
                return;
            }

            // The WD write command completes once exactly one 256-byte sector has been supplied.
            _writeBuffer.Add(value);
            _writeBytesRemaining--;
            if (_writeBytesRemaining > 0)
            {
                return;
            }

            _writeDataTimeoutTstate = 0;

            TrdDiskImage? disk = SelectedDisk;
            if (disk == null)
            {
                _status = StatusNotReady | StatusRecordNotFound;
            }
            else if (!disk.TryWriteSector(_writeTrack, _writeSide, _writeSector, CollectionsMarshal.AsSpan(_writeBuffer)))
            {
                _status = disk.IsWriteProtected ? StatusWriteProtect : StatusRecordNotFound;
            }
            else
            {
                _status = 0;
            }

            UpdateTrackZeroStatus();
            _writeBuffer.Clear();
            _interruptRequest = true;
            Trace($"WRITE SECTOR complete drive={SelectedDrive} track={_writeTrack} side={_writeSide} sector={_writeSector} status={_status:X2}");
        }
        private void ExecuteCommand(byte command)
        {
            LastCommand = command;
            CommandCount++;
            Trace($"COMMAND value={command:X2} drive={SelectedDrive} side={SelectedSide} track={_track} sector={_sector} data={_data:X2} system={_system:X2}");
            _interruptRequest = false;
            AbortTransfer();
            byte family = (byte)(command & 0xF0);
            if (family == 0xD0)
            {
                // Force interrupt is used heavily by TR-DOS to cancel current WD activity.
                _typeOneStatus = true;
                _status = 0;
                UpdateTrackZeroStatus();
                _interruptRequest = (command & 0x08) != 0;
                Trace($"FORCE INTERRUPT status={_status:X2} intrq={_interruptRequest}");
                return;
            }

            if ((command & 0x80) == 0)
            {
                ExecuteTypeOne(command);
                return;
            }

            if ((command & 0xE0) == 0x80)
            {
                BeginReadSector(command);
                return;
            }

            if ((command & 0xE0) == 0xA0)
            {
                BeginWriteSector();
                return;
            }

            if ((command & 0xF0) == 0xC0)
            {
                BeginReadAddress();
                return;
            }

            _status = 0;
            UpdateTrackZeroStatus();
            _interruptRequest = true;
            Trace($"COMMAND unsupported status={_status:X2}");
        }
        private void ExecuteTypeOne(byte command)
        {
            _typeOneStatus = true;
            if ((command & 0x08) != 0)
            {
                _headLoaded = true;
            }
            else if ((command & 0x04) == 0)
            {
                _headLoaded = false;
            }

            switch (command & 0xF0)
            {
                case 0x00:
                    _track = 0;
                    break;
                case 0x10:
                    _track = _data;
                    break;
                case 0x40:
                case 0x50:
                    if (_track < byte.MaxValue)
                    {
                        _track++;
                    }
                    break;
                case 0x60:
                case 0x70:
                    if (_track > 0)
                    {
                        _track--;
                    }
                    break;
            }

            _status = 0;
            UpdateTrackZeroStatus();
            _interruptRequest = true;
            Trace($"TYPE I complete command={command:X2} track={_track} headLoaded={_headLoaded} status={_status:X2}");
        }
        private void BeginReadSector(byte command)
        {
            _typeOneStatus = false;
            TrdDiskImage? disk = SelectedDisk;
            if (disk == null)
            {
                _readCommandActive = false;
                _readMultiSector = false;
                _status = StatusNotReady | StatusRecordNotFound;
                UpdateTrackZeroStatus();
                _interruptRequest = true;
                Trace($"READ SECTOR no-disk drive={SelectedDrive} track={_track} side={SelectedSide} sector={_sector} status={_status:X2}");
                return;
            }

            int startSector = _sector;
            // Queueing is deferred until software has had a chance to observe busy/no-DRQ.
            _readCommandActive = true;
            _readMultiSector = (command & 0x10) != 0;
            _readTrack = _track;
            _readSide = SelectedSide;
            _readNextSector = startSector;
            _readLastSector = _readMultiSector ? TrdDiskImage.SectorsPerTrack : startSector;
            _readGapPolls = InitialReadGapPolls;
            _readReadyTstate = (command & 0x04) != 0
                ? _busTstate + MillisecondsToTstates(TypeTwoHeadLoadDelayMs)
                : 0;
            _status = StatusBusy;
            UpdateTrackZeroStatus();
            _interruptRequest = false;

            Trace($"READ SECTOR begin drive={SelectedDrive} track={_readTrack} side={_readSide} sector={startSector} multi={_readMultiSector} last={_readLastSector} ready={_readReadyTstate} status={_status:X2}");
        }
        private void BeginWriteSector()
        {
            _typeOneStatus = false;
            TrdDiskImage? disk = SelectedDisk;
            if (disk == null)
            {
                _status = StatusNotReady | StatusRecordNotFound;
                UpdateTrackZeroStatus();
                _interruptRequest = true;
                Trace($"WRITE SECTOR no-disk drive={SelectedDrive} track={_track} side={SelectedSide} sector={_sector} status={_status:X2}");
                return;
            }

            if (disk.IsWriteProtected)
            {
                _status = StatusWriteProtect;
                UpdateTrackZeroStatus();
                _interruptRequest = true;
                Trace($"WRITE SECTOR protected drive={SelectedDrive} track={_track} side={SelectedSide} sector={_sector} status={_status:X2}");
                return;
            }

            _writeTrack = _track;
            _writeSide = SelectedSide;
            _writeSector = _sector;
            // Beta/TR-DOS uses fixed 256-byte sectors, so the write byte count is known up front.
            _writeBytesRemaining = TrdDiskImage.SectorSize;
            // Fuse gives a type-II transfer five revolutions (one second at
            // 300 RPM) before terminating it as LOST DATA. Some TR-DOS ROMs
            // deliberately issue an unserviced write command while probing the
            // controller and wait for that completion interrupt.
            _writeDataTimeoutTstate = _busTstate + MillisecondsToTstates(TransferTimeoutMs);
            _status = StatusBusy | StatusDrq;
            UpdateTrackZeroStatus();
            _interruptRequest = false;
            Trace($"WRITE SECTOR begin drive={SelectedDrive} track={_writeTrack} side={_writeSide} sector={_writeSector}");
        }
        private void BeginReadAddress()
        {
            _typeOneStatus = false;
            TrdDiskImage? disk = SelectedDisk;
            if (disk == null)
            {
                _status = StatusNotReady | StatusRecordNotFound;
                UpdateTrackZeroStatus();
                _interruptRequest = true;
                Trace($"READ ADDRESS no-disk drive={SelectedDrive} track={_track} side={SelectedSide} status={_status:X2}");
                return;
            }

            Span<byte> scratch = stackalloc byte[TrdDiskImage.SectorSize];
            if (!disk.TryReadSector(_track, SelectedSide, 1, scratch))
            {
                _status = StatusRecordNotFound;
                UpdateTrackZeroStatus();
                _interruptRequest = true;
                Trace($"READ ADDRESS missing drive={SelectedDrive} track={_track} side={SelectedSide} status={_status:X2}");
                return;
            }

            // READ ADDRESS returns the next physical ID field passing under the head,
            // not sector 1 on every command. Direct loaders use this rotating sequence
            // to synchronise with the disk and will wait forever if the ID never moves.
            ulong sectorSlotTstates = Math.Max(1UL, (ulong)_cpuClockHz / (5UL * TrdDiskImage.SectorsPerTrack));
            ulong nextSlot = (_busTstate / sectorSlotTstates) + 1;
            _readAddressTrack = _track;
            _readAddressSide = (byte)SelectedSide;
            // The deadline is the leading edge of nextSlot, so the ID crossing the
            // head at that instant belongs to nextSlot itself. Using nextSlot - 1
            // returned the sector which had just passed while delaying completion
            // until the following ID. Timing-sensitive protection loops then saw a
            // permanently one-sector-late rotational stream.
            int physicalSlot = (int)(nextSlot % TrdDiskImage.SectorsPerTrack);
            // Fuse expands TRD/SCL media using the standard TR-DOS interleave-2
            // order: 1,9,2,10,...,8,16. Raw TRD storage remains logical 1..16;
            // only rotational READ ADDRESS observations use physical ordering.
            _readAddressSector = (byte)(disk.GetPhysicalSectorId(physicalSlot) | disk.ReadAddressSectorIdMask);
            _readAddressReadyTstate = nextSlot * sectorSlotTstates;
            _readAddressPending = true;
            _readCommandActive = true;
            _status = StatusBusy;
            UpdateTrackZeroStatus();
            _interruptRequest = false;
            Trace($"READ ADDRESS begin drive={SelectedDrive} track={_readAddressTrack} side={_readAddressSide} sector={_readAddressSector} ready={_readAddressReadyTstate} status={_status:X2}");
        }
        private void WriteSystem(byte value)
        {
            _system = value;
            // Bit 3 is the external HLT input, not the controller's HLD output.
            // HLD/READY changes as a consequence of WD commands (handled in the
            // type-I/II/III command paths), so merely selecting HLT must not make
            // an otherwise idle controller ready.
            Trace($"WRITE SYSTEM value={value:X2} drive={SelectedDrive} side={SelectedSide} hlt={(value & 0x08) != 0} dden={(value & 0x20) != 0}");
        }

        private bool IsIndexPulseActive()
        {
            if (SelectedDisk == null)
            {
                // FUSE's FDD layer reports INDEX asserted continuously when no
                // medium is loaded. Scorpion's 128 TR-DOS startup probe relies on
                // that state; synthesising a rotating empty spindle leaves it in
                // the status-poll loop for an apparent eternity.
                return true;
            }

            // A 300 RPM drive completes one revolution in 200 ms and presents an
            // approximately 10 ms index pulse while media is mounted.
            ulong revolution = Math.Max(1UL, (ulong)_cpuClockHz / 5UL);
            ulong pulse = Math.Max(1UL, (ulong)_cpuClockHz / 100UL);
            return (_busTstate % revolution) < pulse;
        }
        private void AbortTransfer()
        {
            _readBuffer.Clear();
            _writeBuffer.Clear();
            _writeBytesRemaining = 0;
            _readCommandActive = false;
            _readMultiSector = false;
            _readGapPolls = 0;
            _readReadyTstate = 0;
            _readDataTimeoutTstate = 0;
            _writeDataTimeoutTstate = 0;
            _readAddressTransfer = false;
            _readAddressPending = false;
            _readAddressReadyTstate = 0;
            _readAddressByteReady = false;
            _readAddressNextByteTstate = 0;
            _status &= unchecked((byte)~(StatusBusy | StatusDrq));
        }
        private void AdvanceControllerState()
        {
            if (_writeDataTimeoutTstate != 0 && _busTstate >= _writeDataTimeoutTstate)
            {
                CompleteLostDataTimeout();
                return;
            }

            if (_readDataTimeoutTstate != 0 && _busTstate >= _readDataTimeoutTstate)
            {
                // If software does not drain DRQ, WD179x reports lost data rather than
                // keeping the transfer alive forever.
                CompleteLostDataTimeout();
                return;
            }

            if (_readAddressPending)
            {
                if (_busTstate < _readAddressReadyTstate)
                {
                    return;
                }

                QueueReadAddress();
                return;
            }

            if (_readAddressTransfer && !_readAddressByteReady && _readBuffer.Count > 0)
            {
                if (_busTstate >= _readAddressNextByteTstate)
                {
                    _readAddressByteReady = true;
                    _readAddressNextByteTstate = 0;
                }

                return;
            }

            if (!_readCommandActive || _readBuffer.Count != 0 || _readNextSector > _readLastSector)
            {
                return;
            }

            if (_readReadyTstate != 0 && _busTstate < _readReadyTstate)
            {
                return;
            }

            _readReadyTstate = 0;

            if (_readGapPolls > 0)
            {
                // Give polling code at least one no-DRQ read before data appears.
                _readGapPolls--;
                return;
            }

            TrdDiskImage? disk = SelectedDisk;
            if (disk == null)
            {
                _readCommandActive = false;
                _readMultiSector = false;
                _status = StatusNotReady | StatusRecordNotFound;
                UpdateTrackZeroStatus();
                _interruptRequest = true;
                Trace($"READ SECTOR deferred no-disk drive={SelectedDrive} track={_readTrack} side={_readSide} sector={_readNextSector} status={_status:X2}");
                return;
            }

            int sectorId = _readNextSector;
            if (QueueReadSector(disk, _readTrack, _readSide, sectorId))
            {
                _readNextSector = sectorId + 1;
            }
        }
        private bool QueueReadSector(TrdDiskImage disk, int track, int side, int sectorId)
        {
            Span<byte> sector = stackalloc byte[TrdDiskImage.SectorSize];
            if (!disk.TryReadSector(track, side, sectorId, sector))
            {
                _readCommandActive = false;
                _readMultiSector = false;
                _readBuffer.Clear();
                _status = StatusRecordNotFound;
                UpdateTrackZeroStatus();
                _interruptRequest = true;
                Trace($"READ SECTOR missing drive={SelectedDrive} track={track} side={side} sector={sectorId} status={_status:X2}");
                return false;
            }

            for (int i = 0; i < sector.Length; i++)
            {
                _readBuffer.Enqueue(sector[i]);
            }

            _status = StatusBusy | StatusDrq;
            _readDataTimeoutTstate = _busTstate + MillisecondsToTstates(TransferTimeoutMs);
            UpdateTrackZeroStatus();
            _interruptRequest = false;
            Trace($"READ SECTOR queued drive={SelectedDrive} track={track} side={side} sector={sectorId} bytes={_readBuffer.Count} timeout={_readDataTimeoutTstate} status={_status:X2}");
            return true;
        }
        private void QueueReadAddress()
        {
            _readAddressPending = false;
            _readAddressReadyTstate = 0;
            _readAddressTransfer = true;
            _readAddressByteReady = true;
            _readAddressNextByteTstate = 0;

            _readBuffer.Enqueue(_readAddressTrack);
            _readBuffer.Enqueue(_readAddressSide);
            _readBuffer.Enqueue(_readAddressSector);
            _readBuffer.Enqueue(1); // 256-byte sector length code.

            // A valid ID field leaves the controller CRC accumulator at zero after
            // its two on-disk CRC bytes have been folded in. This is what FUSE's
            // FD1793 data-register path returns for a generated, CRC-valid TRD ID.
            _readBuffer.Enqueue(0);
            _readBuffer.Enqueue(0);

            _status = StatusBusy | StatusDrq;
            _readDataTimeoutTstate = _busTstate + MillisecondsToTstates(TransferTimeoutMs);
            _interruptRequest = false;
            Trace($"READ ADDRESS queued drive={SelectedDrive} track={_readAddressTrack} side={_readAddressSide} sector={_readAddressSector} crc=0000 status={_status:X2}");
        }
        private static ushort ComputeIdCrc(byte track, byte side, byte sector, byte length, bool doubleDensity)
        {
            ushort crc = 0xFFFF;
            if (doubleDensity)
            {
                crc = UpdateCrc16(crc, 0xA1);
                crc = UpdateCrc16(crc, 0xA1);
                crc = UpdateCrc16(crc, 0xA1);
            }

            crc = UpdateCrc16(crc, 0xFE);
            crc = UpdateCrc16(crc, track);
            crc = UpdateCrc16(crc, side);
            crc = UpdateCrc16(crc, sector);
            return UpdateCrc16(crc, length);
        }
        private static ushort UpdateCrc16(ushort crc, byte value)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }

            return crc;
        }
        private void CompleteLostDataTimeout()
        {
            _readBuffer.Clear();
            _writeBuffer.Clear();
            _writeBytesRemaining = 0;
            _readCommandActive = false;
            _readMultiSector = false;
            _readReadyTstate = 0;
            _readAddressPending = false;
            _readAddressReadyTstate = 0;
            _readAddressTransfer = false;
            _readAddressByteReady = false;
            _readAddressNextByteTstate = 0;
            _readDataTimeoutTstate = 0;
            _writeDataTimeoutTstate = 0;
            _status = StatusLostData;
            UpdateTrackZeroStatus();
            _interruptRequest = true;
            Trace($"TRANSFER lost-data timeout drive={SelectedDrive} track={_readTrack} side={_readSide} sector={_sector} status={_status:X2} intrq={_interruptRequest}");
        }
        private ulong MillisecondsToTstates(int milliseconds)
        {
            return (ulong)((long)_cpuClockHz * milliseconds / 1000);
        }

        private ulong MicrosecondsToTstates(int microseconds)
        {
            return Math.Max(1UL, (ulong)((long)_cpuClockHz * microseconds / 1_000_000));
        }

        private bool HasReadDataRequest =>
            _readBuffer.Count > 0 && (!_readAddressTransfer || _readAddressByteReady);
        private void UpdateTrackZeroStatus()
        {
            // Bit 2 is TRACK 0 for type-I status, but LOST DATA for type-II/III.
            // Do not destroy a latched LOST DATA result while updating track state.
            if (!_typeOneStatus)
            {
                return;
            }

            if (_track == 0)
            {
                _status |= StatusTrackZero;
            }
            else
            {
                _status &= unchecked((byte)~StatusTrackZero);
            }
        }
        private void MarkActivity()
        {
            Interlocked.Increment(ref _activityCounter);
        }
        private byte TraceRead(string register, byte value)
        {
            Trace($"READ {register} value={value:X2} drive={SelectedDrive} side={SelectedSide} track={_track} sector={_sector} status={_status:X2} rb={_readBuffer.Count} wb={_writeBytesRemaining} intrq={_interruptRequest}");
            return value;
        }
        private void Trace(string message)
        {
            if (!_traceEnabled)
            {
                return;
            }

            string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
            Debug.WriteLine(line);

            if (string.IsNullOrWhiteSpace(_tracePath))
            {
                return;
            }

            try
            {
                File.AppendAllText(_tracePath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Beta 128 FDC trace write failed: {ex.Message}");
                _tracePath = null;
            }
        }

        private int SelectedDrive => _system & 0x03;
        private int SelectedSide => (_system & 0x10) == 0 ? 1 : 0;
        private TrdDiskImage? SelectedDisk => _drives[SelectedDrive];
    }
}
