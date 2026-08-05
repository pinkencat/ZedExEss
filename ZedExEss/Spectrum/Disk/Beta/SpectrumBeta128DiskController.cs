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
        private const int ReadDataTimeoutMs = 1000;
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
        private ulong _busTstate;
        private int _cpuClockHz = 3_500_000;
        private long _activityCounter;
        private bool _interruptRequest;
        private bool _readAddressTransfer;
        private byte _readAddressTrack;
        private bool _typeOneStatus = true;
        private bool _headLoaded;
        private int _indexReadCounter;
        private bool _traceEnabled;
        private string? _tracePath;
        public long ActivityCounter => Interlocked.Read(ref _activityCounter);
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
            AdvanceDeferredReadSector();
            _interruptRequest = false;
            // The same status bits have different meanings for type I and type II/III commands.
            // Build the value from current controller state rather than keeping one stale latch.
            byte status = (byte)(_status & unchecked((byte)~(StatusBusy | StatusDrq | StatusNotReady)));
            if (SelectedDisk == null)
            {
                status |= StatusNotReady;
            }

            if (_readCommandActive || _readBuffer.Count > 0 || _writeBytesRemaining > 0)
            {
                status |= StatusBusy;
                if (_readBuffer.Count > 0 || _writeBytesRemaining > 0)
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
            else if (!_typeOneStatus)
            {
                status &= unchecked((byte)~StatusTrackZero);
            }

            if (_typeOneStatus)
            {
                if (_headLoaded)
                {
                    status |= StatusSpinUp;
                }

                if (SelectedDisk == null || ((_indexReadCounter++ & 0x0F) == 0))
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
            AdvanceDeferredReadSector();
            byte value = 0;
            if (_readBuffer.Count > 0 || _writeBytesRemaining > 0)
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
            if (_readBuffer.Count == 0)
            {
                return TraceRead("DATA idle", _data);
            }

            // Reading the data port consumes exactly one byte from the current WD transfer.
            _data = _readBuffer.Dequeue();
            if (_readBuffer.Count == 0)
            {
                _readDataTimeoutTstate = 0;

                if (_readAddressTransfer)
                {
                    _status = 0;
                    _sector = _readAddressTrack;
                    _readAddressTransfer = false;
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

            _readBuffer.Enqueue(_track);
            _readBuffer.Enqueue((byte)SelectedSide);
            _readBuffer.Enqueue(1);
            _readBuffer.Enqueue(1);
            _readBuffer.Enqueue(0);
            _readBuffer.Enqueue(0);
            // The emulated ID field is enough for TR-DOS and many direct WD loaders.
            _readAddressTransfer = true;
            _readAddressTrack = _track;
            _readCommandActive = true;
            _status = StatusBusy | StatusDrq;
            UpdateTrackZeroStatus();
            _interruptRequest = false;
            Trace($"READ ADDRESS queued drive={SelectedDrive} track={_track} side={SelectedSide} status={_status:X2}");
        }
        private void WriteSystem(byte value)
        {
            _system = value;
            if ((value & 0x08) != 0)
            {
                _headLoaded = true;
            }

            Trace($"WRITE SYSTEM value={value:X2} drive={SelectedDrive} side={SelectedSide} hlt={(value & 0x08) != 0} dden={(value & 0x20) != 0}");
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
            _readAddressTransfer = false;
            _status &= unchecked((byte)~(StatusBusy | StatusDrq));
        }
        private void AdvanceDeferredReadSector()
        {
            if (_readDataTimeoutTstate != 0 && _busTstate >= _readDataTimeoutTstate)
            {
                // If software does not drain DRQ, WD179x reports lost data rather than
                // keeping the transfer alive forever.
                CompleteLostDataTimeout();
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
            _readDataTimeoutTstate = _busTstate + MillisecondsToTstates(ReadDataTimeoutMs);
            UpdateTrackZeroStatus();
            _interruptRequest = false;
            Trace($"READ SECTOR queued drive={SelectedDrive} track={track} side={side} sector={sectorId} bytes={_readBuffer.Count} timeout={_readDataTimeoutTstate} status={_status:X2}");
            return true;
        }
        private void CompleteLostDataTimeout()
        {
            _readBuffer.Clear();
            _readCommandActive = false;
            _readMultiSector = false;
            _readReadyTstate = 0;
            _readDataTimeoutTstate = 0;
            _status = StatusLostData;
            UpdateTrackZeroStatus();
            _interruptRequest = true;
            Trace($"READ SECTOR lost-data timeout drive={SelectedDrive} track={_readTrack} side={_readSide} sector={_sector} status={_status:X2} intrq={_interruptRequest}");
        }
        private ulong MillisecondsToTstates(int milliseconds)
        {
            return (ulong)((long)_cpuClockHz * milliseconds / 1000);
        }
        private void UpdateTrackZeroStatus()
        {
            if (_track == 0)
            {
                if (_typeOneStatus)
                {
                    _status |= StatusTrackZero;
                }
                else
                {
                    _status &= unchecked((byte)~StatusTrackZero);
                }
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
