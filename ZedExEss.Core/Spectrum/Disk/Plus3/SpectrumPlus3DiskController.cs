using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System;
using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Disk.Plus3
{
    /// <summary>
    /// uPD765A-style +3 disk controller with sector, format, scan and multi-drive state.
    /// </summary>
    /// <remarks>
    /// Commands are collected through the data register, executed once their full
    /// parameter count is present, then expose execution/result bytes through the
    /// main-status protocol. Disk images remain sector-oriented; this class owns all
    /// controller phases, DRQ-style byte streaming and interrupt status.
    /// </remarks>
    public sealed class SpectrumPlus3DiskController : IPortDevice
    {
        private const ushort PortMask = 0xF002;
        private const ushort MotorPort = 0x1000;
        private const ushort StatusPort = 0x2000;
        private const ushort DataPort = 0x3000;
        private const int PhysicalDriveCount = 2;
        private const int FdcDriveCount = 4;
        private const byte Status2ControlMark = 0x40;

        private readonly List<byte> _command = [];
        private readonly Queue<byte> _output = new();
        private readonly List<byte> _writeBuffer = [];
        private readonly List<Plus3DiskSector> _sectorRun = [];
        private readonly Plus3DiskImage?[] _drives = new Plus3DiskImage?[PhysicalDriveCount];
        private readonly byte[] _currentTracks = new byte[FdcDriveCount];
        private readonly bool[] _interruptPendingByDrive = new bool[FdcDriveCount];
        private readonly byte[] _pendingInterruptStatusByDrive = new byte[FdcDriveCount];

        private Plus3DiskSector? _writeSector;
        private Plus3DiskSector? _scanSector;
        private int _expectedCommandLength;
        private int _writeBytesExpected;
        private int _readDataBytesRemaining;
        private bool _motorOn;
        private bool _interruptPending;
        private bool _formatInProgress;
        private bool _writeDeletedData;
        private byte _formatTrack;
        private byte _formatSide;
        private byte _formatSizeCode;
        private byte _formatGapLength;
        private byte _formatFiller;
        private byte _specifyStepRateHeadUnload;
        private byte _specifyHeadLoadNonDma;
        private ScanMode _scanMode;
        private byte _activeDriveHead;
        private int _readIdDrive = -1;
        private int _readIdTrack = -1;
        private int _readIdSide = -1;
        private int _readIdIndex;
        private long _activityCounter;
        private bool _traceEnabled;
        private string? _tracePath;
        private byte _driveBusyMask;

        public bool MotorOn => _motorOn;
        public bool HasDisk => _drives[0] != null || _drives[1] != null;
        public bool IsWriteProtected
        {
            get => IsDriveWriteProtected(0);
            set
            {
                SetDriveWriteProtected(0, value);
            }
        }
        public long ActivityCounter => Interlocked.Read(ref _activityCounter);

        public bool TraceEnabled => _traceEnabled;
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

                    File.WriteAllText(path, $"ZedExEss +3 FDC trace started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}{Environment.NewLine}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FDC trace file unavailable: {ex.Message}");
                    _tracePath = null;
                }
            }

            Trace("TRACE enabled");
        }
        public static bool IsMotorPort(ushort port)
        {
            return (port & PortMask) == MotorPort;
        }
        public void SetMotorControl(byte value)
        {
            // +3DOS controls the internal drive motor through port 1FFD rather than a FDC command.
            _motorOn = (value & 0x08) != 0;
            Trace($"MOTOR value={value:X2} on={_motorOn}");
            MarkActivity();
        }
        public void InsertDisk(Plus3DiskImage image)
        {
            InsertDisk(0, image);
        }
        public void InsertDisk(int drive, Plus3DiskImage image)
        {
            if (!IsValidDrive(drive))
            {
                throw new ArgumentOutOfRangeException(nameof(drive));
            }

            _drives[drive] = image ?? throw new ArgumentNullException(nameof(image));
            // Media changes cancel active transfers but preserve the configured FDC state.
            AbortActiveTransfer();
            ClearPendingInterrupt(drive);
            ResetReadIdRotation(drive);
            Trace($"INSERT drive={drive} path=\"{image.Path}\" tracks={image.TrackCount} sides={image.SideCount} ro={image.IsWriteProtected}");
        }
        public void EjectDisk()
        {
            EjectDisk(0);
        }
        public void EjectDisk(int drive)
        {
            if (!IsValidDrive(drive))
            {
                throw new ArgumentOutOfRangeException(nameof(drive));
            }

            _drives[drive] = null;
            AbortActiveTransfer();
            ClearPendingInterrupt(drive);
            ResetReadIdRotation(drive);
            Trace($"EJECT drive={drive}");
        }
        public bool HasDriveDisk(int drive)
        {
            return IsValidDrive(drive) && _drives[drive] != null;
        }
        public bool IsDriveWriteProtected(int drive)
        {
            return IsValidDrive(drive) ? _drives[drive]?.IsWriteProtected ?? true : true;
        }
        public void SetDriveWriteProtected(int drive, bool writeProtected)
        {
            if (IsValidDrive(drive) && _drives[drive] != null)
            {
                _drives[drive]!.IsWriteProtected = writeProtected;
                Trace($"WRITE PROTECT drive={drive} ro={writeProtected}");
            }
        }
        public bool HandlesPort(ushort port)
        {
            ushort masked = (ushort)(port & PortMask);
            return masked == StatusPort
                || masked == DataPort;
        }
        public byte Read(ushort port)
        {
            ushort masked = (ushort)(port & PortMask);
            if (masked == StatusPort)
            {
                MarkActivity();
                return ReadStatus();
            }

            if (masked == DataPort)
            {
                MarkActivity();
                return ReadDataRegister();
            }

            return 0xFF;
        }
        public void Write(ushort port, byte value)
        {
            ushort masked = (ushort)(port & PortMask);
            if (masked == DataPort)
            {
                MarkActivity();
                WriteDataRegister(value);
            }
        }
        private void MarkActivity()
        {
            Interlocked.Increment(ref _activityCounter);
        }
        private byte ReadStatus()
        {
            // Main status combines phase, result availability, command input availability and drive-busy bits.
            byte driveBusy = (byte)(_driveBusyMask & 0x0F);
            if (_output.Count > 0)
            {
                return _readDataBytesRemaining > 0 ? (byte)(0xF0 | driveBusy) : (byte)(0xD0 | driveBusy);
            }

            if (_writeBytesExpected > 0)
            {
                return (byte)(0xB0 | driveBusy);
            }

            if (_command.Count > 0 && _command.Count < _expectedCommandLength)
            {
                return (byte)(0x90 | driveBusy);
            }

            return (byte)(0x80 | driveBusy);
        }
        private byte ReadDataRegister()
        {
            if (_output.Count == 0)
            {
                return 0xFF;
            }

            byte value = _output.Dequeue();
            if (_readDataBytesRemaining > 0)
            {
                _readDataBytesRemaining--;
            }

            return value;
        }
        private void WriteDataRegister(byte value)
        {
            if (_output.Count > 0)
            {
                // Preserve result phase. The ROM should drain pending bytes before starting a new command.
                Trace($"WRITE ignored during output phase value={value:X2}");
                return;
            }

            if (_writeBytesExpected > 0)
            {
                _writeBuffer.Add(value);
                if (_writeBuffer.Count >= _writeBytesExpected)
                {
                    CompleteWriteData();
                }

                return;
            }

            if (_command.Count == 0)
            {
                _output.Clear();
                _readDataBytesRemaining = 0;
                _expectedCommandLength = GetCommandLength(value);
            }

            _command.Add(value);
            if (_command.Count >= _expectedCommandLength)
            {
                // uPD765 commands execute only once all parameter bytes have been written.
                ExecuteCommand();
            }
        }
        private void ExecuteCommand()
        {
            byte command = _command[0];
            // The low five bits select the command; the high bits carry options such as MFM/SK.
            Trace($"CMD {CommandName(command)} ({command:X2}) bytes={BytesToHex(_command)}");
            switch (command & 0x1F)
            {
                case 0x02:
                    ReadTrack();
                    break;
                case 0x03:
                    Specify();
                    break;
                case 0x04:
                    SenseDriveStatus();
                    break;
                case 0x05:
                    BeginWriteData(deletedData: false);
                    break;
                case 0x06:
                    ReadData(deletedDataCommand: false);
                    break;
                case 0x07:
                    Recalibrate();
                    break;
                case 0x08:
                    SenseInterruptStatus();
                    break;
                case 0x09:
                    BeginWriteData(deletedData: true);
                    break;
                case 0x0A:
                    ReadId();
                    break;
                case 0x0C:
                    ReadData(deletedDataCommand: true);
                    break;
                case 0x0D:
                    FormatTrack();
                    break;
                case 0x0F:
                    Seek();
                    break;
                case 0x11:
                    BeginScanData(ScanMode.Equal);
                    break;
                case 0x19:
                    BeginScanData(ScanMode.LowOrEqual);
                    break;
                case 0x1D:
                    BeginScanData(ScanMode.HighOrEqual);
                    break;
                default:
                    InvalidCommand();
                    break;
            }
        }
        private void ReadData(bool deletedDataCommand)
        {
            // Build the sector run first so multi-sector reads honour EOT and image-specific ordering.
            SelectDriveHead();
            int track = _command[2];
            int physicalTrack = CurrentTrack;
            int side = _command[3];
            int physicalSide = SelectedHead;
            int firstSector = _command[4];
            int sizeCode = _command[5];
            int lastSector = _command[6];

            if (!IsSelectedDriveReady())
            {
                Trace($"READ DATA not-ready drive={SelectedDrive} C={track} PC={physicalTrack} H={side} R={firstSector:X2} N={sizeCode} deletedCommand={deletedDataCommand}");
                QueueReadWriteResult(0x48, 0x00, 0x00, (byte)track, (byte)side, (byte)firstSector, (byte)sizeCode);
                EndCommand();
                return;
            }

            if (!TryGetSector(physicalTrack, physicalSide, track, side, firstSector, sizeCode, out Plus3DiskSector? first))
            {
                Trace($"READ DATA missing-sector drive={SelectedDrive} C={track} PC={physicalTrack} H={side} PH={physicalSide} R={firstSector:X2} N={sizeCode} deletedCommand={deletedDataCommand}");
                QueueReadWriteResult(0x40, 0x04, 0x00, (byte)track, (byte)side, (byte)firstSector, (byte)sizeCode);
                EndCommand();
                return;
            }

            Plus3DiskImage? disk = SelectedDisk;
            if (disk == null || !disk.TryGetSectorRun(physicalTrack, physicalSide, track, side, firstSector, lastSector, sizeCode, _sectorRun))
            {
                Trace($"READ DATA missing-run drive={SelectedDrive} C={track} PC={physicalTrack} H={side} PH={physicalSide} R={firstSector:X2} EOT={lastSector:X2} N={sizeCode} deletedCommand={deletedDataCommand}");
                QueueReadWriteResult(0x40, 0x04, 0x00, (byte)track, (byte)side, (byte)firstSector, (byte)sizeCode);
                EndCommand();
                return;
            }

            Plus3DiskSector firstData = first!;
            byte lastRead = firstData.SectorId;
            Plus3DiskSector resultSector = firstData;
            byte resultStatus1 = firstData.Status1;
            byte resultStatus2 = BuildReadDataStatus2(firstData, deletedDataCommand);
            bool foundAddressMarkMismatch = IsDeletedDataSector(firstData) != deletedDataCommand;
            for (int sectorIndex = 0; sectorIndex < _sectorRun.Count; sectorIndex++)
            {
                Plus3DiskSector current = _sectorRun[sectorIndex];
                lastRead = current.SectorId;
                if (!foundAddressMarkMismatch && IsDeletedDataSector(current) != deletedDataCommand)
                {
                    resultSector = current;
                    resultStatus1 = current.Status1;
                    resultStatus2 = BuildReadDataStatus2(current, deletedDataCommand);
                    foundAddressMarkMismatch = true;
                }

                _readDataBytesRemaining += current.Data.Length;
                for (int i = 0; i < current.Data.Length; i++)
                {
                    _output.Enqueue(current.Data[i]);
                }
            }

            if (!foundAddressMarkMismatch)
            {
                resultSector = _sectorRun[^1];
                resultStatus1 = resultSector.Status1;
                resultStatus2 = BuildReadDataStatus2(resultSector, deletedDataCommand);
            }

            Trace($"READ DATA drive={SelectedDrive} C={track} PC={physicalTrack} H={side} PH={physicalSide} R={firstSector:X2}-{lastRead:X2} N={sizeCode} sectors={_sectorRun.Count} bytes={_readDataBytesRemaining} deletedCommand={deletedDataCommand} controlMark={(resultStatus2 & Status2ControlMark) != 0}");
            QueueReadWriteResult(0x00, resultStatus1, resultStatus2, resultSector.Track, resultSector.Side, resultSector.SectorId, resultSector.SizeCode);
            EndCommand();
        }
        private void Specify()
        {
            _specifyStepRateHeadUnload = _command[1];
            _specifyHeadLoadNonDma = _command[2];
            Trace($"SPECIFY SRT/HUT={_specifyStepRateHeadUnload:X2} HLT/ND={_specifyHeadLoadNonDma:X2}");
            EndCommand();
        }
        private void ReadTrack()
        {
            SelectDriveHead();
            int track = _command[2];
            int physicalTrack = CurrentTrack;
            int side = _command[3];
            int physicalSide = SelectedHead;
            if (!IsSelectedDriveReady())
            {
                Trace($"READ TRACK not-ready drive={SelectedDrive} C={track} PC={physicalTrack} H={side} PH={physicalSide}");
                QueueReadWriteResult(0x48, 0x00, 0x00, (byte)track, (byte)side, 0, _command[5]);
                EndCommand();
                return;
            }

            Plus3DiskTrack? diskTrack = SelectedDisk?.FindTrack(physicalTrack, physicalSide);
            if (diskTrack == null || diskTrack.Sectors.Count == 0)
            {
                Trace($"READ TRACK missing-track drive={SelectedDrive} C={track} PC={physicalTrack} H={side} PH={physicalSide}");
                QueueReadWriteResult(0x40, 0x04, 0x00, (byte)track, (byte)side, 0, _command[5]);
                EndCommand();
                return;
            }

            Plus3DiskSector last = diskTrack.Sectors[0];
            for (int i = 0; i < diskTrack.Sectors.Count; i++)
            {
                last = diskTrack.Sectors[i];
                _readDataBytesRemaining += last.Data.Length;
                for (int j = 0; j < last.Data.Length; j++)
                {
                    _output.Enqueue(last.Data[j]);
                }
            }

            Trace($"READ TRACK drive={SelectedDrive} C={track} PC={physicalTrack} H={side} PH={physicalSide} sectors={diskTrack.Sectors.Count} bytes={_readDataBytesRemaining}");
            QueueReadWriteResult(0x00, 0x00, 0x00, last.Track, last.Side, last.SectorId, last.SizeCode);
            EndCommand();
        }
        private void BeginWriteData(bool deletedData)
        {
            // Writes enter a data phase: bytes are collected here and committed in CompleteWriteData.
            SelectDriveHead();
            int track = _command[2];
            int physicalTrack = CurrentTrack;
            int side = _command[3];
            int physicalSide = SelectedHead;
            int sectorId = _command[4];
            int sizeCode = _command[5];
            int lastSectorId = _command[6];
            _sectorRun.Clear();

            if (!IsSelectedDriveReady())
            {
                Trace($"WRITE DATA not-ready drive={SelectedDrive} C={track} PC={physicalTrack} H={side} R={sectorId:X2} N={sizeCode} deleted={deletedData}");
                QueueReadWriteResult(0x48, 0x00, 0x00, (byte)track, (byte)side, (byte)sectorId, (byte)sizeCode);
                EndCommand();
                return;
            }

            if (SelectedDisk?.IsWriteProtected == true)
            {
                Trace($"WRITE DATA protected drive={SelectedDrive} C={track} PC={physicalTrack} H={side} R={sectorId:X2} N={sizeCode} deleted={deletedData}");
                QueueReadWriteResult(0x40, 0x02, 0x00, (byte)track, (byte)side, (byte)sectorId, (byte)sizeCode);
                EndCommand();
                return;
            }

            Plus3DiskImage? disk = SelectedDisk;
            if (disk == null || !disk.TryGetSectorRun(physicalTrack, physicalSide, track, side, sectorId, lastSectorId, sizeCode, _sectorRun))
            {
                Trace($"WRITE DATA missing-run drive={SelectedDrive} C={track} PC={physicalTrack} H={side} PH={physicalSide} R={sectorId:X2} EOT={lastSectorId:X2} N={sizeCode} deleted={deletedData}");
                QueueReadWriteResult(0x40, 0x04, 0x00, (byte)track, (byte)side, (byte)sectorId, (byte)sizeCode);
                EndCommand();
                return;
            }

            Plus3DiskSector writeSector = _sectorRun[0];
            _writeSector = writeSector;
            _writeBuffer.Clear();
            _writeBytesExpected = 0;
            for (int i = 0; i < _sectorRun.Count; i++)
            {
                _writeBytesExpected += _sectorRun[i].Data.Length;
            }

            _writeDeletedData = deletedData;
            _command.Clear();
            _expectedCommandLength = 0;
            Trace($"WRITE DATA begin drive={SelectedDrive} C={track} PC={physicalTrack} H={side} PH={physicalSide} R={sectorId:X2}-{_sectorRun[^1].SectorId:X2} N={sizeCode} sectors={_sectorRun.Count} bytes={_writeBytesExpected} deleted={deletedData}");
        }
        private void BeginScanData(ScanMode mode)
        {
            // SCAN has the same data-in phase shape as WRITE DATA, but the bytes are compared
            // against disk contents instead of persisted.
            SelectDriveHead();
            int track = _command[2];
            int physicalTrack = CurrentTrack;
            int side = _command[3];
            int physicalSide = SelectedHead;
            int sectorId = _command[4];
            int sizeCode = _command[5];
            int lastSectorId = _command[6];
            _sectorRun.Clear();

            if (!IsSelectedDriveReady())
            {
                Trace($"SCAN not-ready mode={mode} drive={SelectedDrive} C={track} PC={physicalTrack} H={side} R={sectorId:X2} N={sizeCode}");
                QueueReadWriteResult(0x48, 0x00, 0x00, (byte)track, (byte)side, (byte)sectorId, (byte)sizeCode);
                EndCommand();
                return;
            }

            Plus3DiskImage? disk = SelectedDisk;
            if (disk == null || !disk.TryGetSectorRun(physicalTrack, physicalSide, track, side, sectorId, lastSectorId, sizeCode, _sectorRun))
            {
                Trace($"SCAN missing-run mode={mode} drive={SelectedDrive} C={track} PC={physicalTrack} H={side} PH={physicalSide} R={sectorId:X2} EOT={lastSectorId:X2} N={sizeCode}");
                QueueReadWriteResult(0x40, 0x04, 0x00, (byte)track, (byte)side, (byte)sectorId, (byte)sizeCode);
                EndCommand();
                return;
            }

            _scanMode = mode;
            _scanSector = _sectorRun[0];
            _writeBuffer.Clear();
            _writeBytesExpected = 0;
            for (int i = 0; i < _sectorRun.Count; i++)
            {
                _writeBytesExpected += _sectorRun[i].Data.Length;
            }

            _command.Clear();
            _expectedCommandLength = 0;
            Trace($"SCAN begin mode={mode} drive={SelectedDrive} C={track} PC={physicalTrack} H={side} PH={physicalSide} R={sectorId:X2}-{_sectorRun[^1].SectorId:X2} N={sizeCode} sectors={_sectorRun.Count} bytes={_writeBytesExpected}");
        }
        private void CompleteWriteData()
        {
            if (_writeSector != null)
            {
                // Persist the full EOT-bounded sector run as one logical command completion.
                Plus3DiskImage? disk = SelectedDisk;
                Plus3DiskSector firstSector = _writeSector;
                Plus3DiskSector lastSector = _sectorRun.Count > 0 ? _sectorRun[^1] : firstSector;
                if (disk == null || disk.IsWriteProtected)
                {
                    Trace($"WRITE DATA complete failed protected/no-disk drive={SelectedDrive} C={firstSector.Track} H={firstSector.Side} R={firstSector.SectorId:X2}");
                    QueueReadWriteResult(0x40, 0x02, 0x00, firstSector.Track, firstSector.Side, firstSector.SectorId, firstSector.SizeCode);
                }
                else if (!TryWriteSectorRun(disk))
                {
                    Trace($"WRITE DATA complete failed write drive={SelectedDrive} C={firstSector.Track} H={firstSector.Side} R={firstSector.SectorId:X2}");
                    QueueReadWriteResult(0x40, 0x02, 0x00, firstSector.Track, firstSector.Side, firstSector.SectorId, firstSector.SizeCode);
                }
                else
                {
                    try
                    {
                        disk.Save();
                        Trace($"WRITE DATA complete ok drive={SelectedDrive} C={firstSector.Track} H={firstSector.Side} R={firstSector.SectorId:X2}-{lastSector.SectorId:X2} sectors={_sectorRun.Count} bytes={_writeBuffer.Count} deleted={_writeDeletedData}");
                        QueueReadWriteResult(0x00, 0x00, 0x00, lastSector.Track, lastSector.Side, lastSector.SectorId, lastSector.SizeCode);
                    }
                    catch (Exception)
                    {
                        Trace($"WRITE DATA complete failed save drive={SelectedDrive} C={firstSector.Track} H={firstSector.Side} R={firstSector.SectorId:X2}");
                        QueueReadWriteResult(0x40, 0x02, 0x00, firstSector.Track, firstSector.Side, firstSector.SectorId, firstSector.SizeCode);
                    }
                }
            }
            else if (_scanSector != null)
            {
                CompleteScanData();
            }
            else if (_formatInProgress)
            {
                // FORMAT TRACK receives C/H/R/N tuples through the data phase and then rebuilds
                // the whole track with the supplied filler byte.
                Plus3DiskImage? disk = SelectedDisk;
                if (disk == null)
                {
                    Trace($"FORMAT complete failed no-disk drive={SelectedDrive} C={_formatTrack} H={_formatSide}");
                    QueueReadWriteResult(0x48, 0x04, 0x00, _formatTrack, _formatSide, 0, _formatSizeCode);
                }
                else if (disk.IsWriteProtected)
                {
                    Trace($"FORMAT complete failed protected drive={SelectedDrive} C={_formatTrack} H={_formatSide}");
                    QueueReadWriteResult(0x40, 0x02, 0x00, _formatTrack, _formatSide, 0, _formatSizeCode);
                }
                else if (!disk.TryFormatTrack(_formatTrack, _formatSide, _writeBuffer, _formatGapLength, _formatFiller))
                {
                    Trace($"FORMAT complete failed format drive={SelectedDrive} C={_formatTrack} H={_formatSide} bytes={_writeBuffer.Count}");
                    QueueReadWriteResult(0x40, 0x04, 0x00, _formatTrack, _formatSide, 0, _formatSizeCode);
                }
                else
                {
                    try
                    {
                        disk.Save();
                        ResetReadIdRotation();
                        Trace($"FORMAT complete ok drive={SelectedDrive} C={_formatTrack} H={_formatSide} ids={_writeBuffer.Count / 4} filler={_formatFiller:X2}");
                        QueueReadWriteResult(0x00, 0x00, 0x00, _formatTrack, _formatSide, 0, _formatSizeCode);
                    }
                    catch (Exception)
                    {
                        Trace($"FORMAT complete failed save drive={SelectedDrive} C={_formatTrack} H={_formatSide}");
                        QueueReadWriteResult(0x40, 0x02, 0x00, _formatTrack, _formatSide, 0, _formatSizeCode);
                    }
                }
            }

            _writeSector = null;
            _scanSector = null;
            _formatInProgress = false;
            _writeDeletedData = false;
            _scanMode = ScanMode.None;
            _sectorRun.Clear();
            _writeBuffer.Clear();
            _writeBytesExpected = 0;
        }
        private bool TryWriteSectorRun(Plus3DiskImage disk)
        {
            int offset = 0;
            if (_sectorRun.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < _sectorRun.Count; i++)
            {
                Plus3DiskSector sector = _sectorRun[i];
                byte[] sectorData = new byte[sector.Data.Length];
                int available = Math.Min(sectorData.Length, _writeBuffer.Count - offset);
                if (available > 0)
                {
                    for (int j = 0; j < available; j++)
                    {
                        sectorData[j] = _writeBuffer[offset + j];
                    }
                }

                if (!disk.TryWriteSector(sector, sectorData, _writeDeletedData))
                {
                    return false;
                }

                offset += sector.Data.Length;
            }

            return true;
        }
        private static byte BuildReadDataStatus2(Plus3DiskSector sector, bool deletedDataCommand)
        {
            byte status2 = (byte)(sector.Status2 & ~Status2ControlMark);
            if (IsDeletedDataSector(sector) != deletedDataCommand)
            {
                status2 |= Status2ControlMark;
            }

            return status2;
        }
        private static bool IsDeletedDataSector(Plus3DiskSector sector)
        {
            return (sector.Status2 & Status2ControlMark) != 0;
        }
        private void CompleteScanData()
        {
            bool satisfied = IsScanRunSatisfied(out Plus3DiskSector sector);
            byte st2 = sector.Status2;
            st2 |= satisfied ? (byte)0x08 : (byte)0x04;
            Trace($"SCAN complete mode={_scanMode} drive={SelectedDrive} C={sector.Track} H={sector.Side} R={sector.SectorId:X2} sectors={_sectorRun.Count} satisfied={satisfied}");
            QueueReadWriteResult(0x00, sector.Status1, st2, sector.Track, sector.Side, sector.SectorId, sector.SizeCode);
        }
        private bool IsScanRunSatisfied(out Plus3DiskSector resultSector)
        {
            resultSector = _scanSector!;
            int offset = 0;
            if (_sectorRun.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < _sectorRun.Count; i++)
            {
                Plus3DiskSector sector = _sectorRun[i];
                resultSector = sector;
                if (!IsScanSatisfied(sector.Data, _writeBuffer, offset, _scanMode))
                {
                    return false;
                }

                offset += sector.Data.Length;
            }

            return true;
        }
        private static bool IsScanSatisfied(byte[] diskData, IReadOnlyList<byte> source, int sourceOffset, ScanMode mode)
        {
            for (int i = 0; i < diskData.Length; i++)
            {
                byte left = diskData[i];
                int sourceIndex = sourceOffset + i;
                byte right = sourceIndex < source.Count ? source[sourceIndex] : (byte)0;
                bool condition = mode switch
                {
                    ScanMode.Equal => left == right,
                    ScanMode.LowOrEqual => left <= right,
                    ScanMode.HighOrEqual => left >= right,
                    _ => false
                };

                if (!condition)
                {
                    return false;
                }
            }

            return true;
        }
        private void FormatTrack()
        {
            // The sector count tells us exactly how many four-byte ID tuples the data phase expects.
            SelectDriveHead();
            _formatTrack = CurrentTrack;
            _formatSide = (byte)((_command[1] >> 2) & 0x01);
            _formatSizeCode = _command[2];
            int formatSectorCount = _command[3];
            _formatGapLength = _command[4];
            _formatFiller = _command[5];
            if (!IsSelectedDriveReady())
            {
                Trace($"FORMAT not-ready drive={SelectedDrive} C={_formatTrack} H={_formatSide}");
                QueueReadWriteResult(0x48, 0x00, 0x00, _formatTrack, _formatSide, 0, _formatSizeCode);
                EndCommand();
                return;
            }

            _writeBytesExpected = Math.Max(0, formatSectorCount * 4);
            _writeSector = null;
            _formatInProgress = true;
            _writeBuffer.Clear();
            _command.Clear();
            _expectedCommandLength = 0;
            Trace($"FORMAT begin drive={SelectedDrive} C={_formatTrack} H={_formatSide} N={_formatSizeCode} ids={formatSectorCount} expectedBytes={_writeBytesExpected}");
            if (_writeBytesExpected == 0)
            {
                ResetReadIdRotation();
                Trace($"FORMAT empty drive={SelectedDrive} C={_formatTrack} H={_formatSide}");
                QueueReadWriteResult(0x00, 0x00, 0x00, _formatTrack, _formatSide, 0, _formatSizeCode);
                _formatInProgress = false;
            }
        }
        private void ReadId()
        {
            // Rotate through sector IDs to approximate disk rotation for firmware that probes geometry.
            SelectDriveHead();
            int side = (_command[1] >> 2) & 0x01;
            if (!IsSelectedDriveReady())
            {
                Trace($"READ ID not-ready drive={SelectedDrive} C={CurrentTrack} H={side}");
                QueueReadWriteResult(0x48, 0x00, 0x00, CurrentTrack, (byte)side, 0, 0);
                EndCommand();
                return;
            }

            byte currentTrack = CurrentTrack;
            Plus3DiskTrack? track = SelectedDisk?.FindTrack(currentTrack, side);
            if (track == null || track.Sectors.Count == 0)
            {
                Trace($"READ ID missing-track drive={SelectedDrive} C={currentTrack} H={side}");
                QueueReadWriteResult(0x40, 0x04, 0x00, currentTrack, (byte)side, 0, 0);
                EndCommand();
                return;
            }

            int drive = SelectedDrive;
            if (_readIdDrive != drive || _readIdTrack != currentTrack || _readIdSide != side)
            {
                _readIdDrive = drive;
                _readIdTrack = currentTrack;
                _readIdSide = side;
                _readIdIndex = 0;
            }

            Plus3DiskSector sector = track.Sectors[_readIdIndex % track.Sectors.Count];
            _readIdIndex = (_readIdIndex + 1) % track.Sectors.Count;
            Trace($"READ ID drive={SelectedDrive} C={currentTrack} H={side} R={sector.SectorId:X2} nextIndex={_readIdIndex}");
            QueueReadWriteResult(0x00, sector.Status1, sector.Status2, sector.Track, sector.Side, sector.SectorId, sector.SizeCode);
            EndCommand();
        }
        private void SenseDriveStatus()
        {
            int driveHead = _command[1];
            int side = (driveHead >> 2) & 0x01;
            _activeDriveHead = (byte)(driveHead & 0x07);
            byte status = 0x00;
            if (IsSelectedDriveReady())
            {
                status |= 0x20;
            }

            Plus3DiskImage? disk = SelectedDisk;
            if (disk?.IsWriteProtected == true)
            {
                status |= 0x40;
            }

            if (CurrentTrack == 0)
            {
                status |= 0x10;
            }

            if (disk?.SideCount > 1)
            {
                status |= 0x08;
            }

            if (side != 0)
            {
                status |= 0x04;
            }

            status |= (byte)(driveHead & 0x03);
            _output.Enqueue(status);
            Trace($"SENSE DRIVE drive={SelectedDrive} H={side} status={status:X2}");
            EndCommand();
        }
        private void Recalibrate()
        {
            SelectDriveHead();
            int drive = SelectedDrive;
            if (IsSelectedDriveReady())
            {
                CurrentTrack = 0;
                ResetReadIdRotation();
                SetPendingInterrupt(drive, (byte)(0x20 | _activeDriveHead));
                Trace($"RECALIBRATE ok drive={SelectedDrive}");
            }
            else
            {
                SetPendingInterrupt(drive, (byte)(0x48 | _activeDriveHead));
                Trace($"RECALIBRATE not-ready drive={SelectedDrive}");
            }

            _interruptPending = true;
            EndCommand();
        }
        private void Seek()
        {
            SelectDriveHead();
            int drive = SelectedDrive;
            if (IsSelectedDriveReady())
            {
                CurrentTrack = _command[2];
                ResetReadIdRotation();
                SetPendingInterrupt(drive, (byte)(0x20 | _activeDriveHead));
                Trace($"SEEK ok drive={SelectedDrive} C={CurrentTrack}");
            }
            else
            {
                SetPendingInterrupt(drive, (byte)(0x48 | _activeDriveHead));
                Trace($"SEEK not-ready drive={SelectedDrive} C={_command[2]}");
            }

            _interruptPending = true;
            EndCommand();
        }
        private void SenseInterruptStatus()
        {
            // SEEK/RECALIBRATE complete asynchronously from the command stream; this returns one
            // pending completion at a time.
            int pendingDrive = FindPendingInterruptDrive();
            if (pendingDrive >= 0)
            {
                byte status = _pendingInterruptStatusByDrive[pendingDrive];
                byte pcn = _currentTracks[pendingDrive];
                _output.Enqueue(status);
                _output.Enqueue(pcn);
                _interruptPendingByDrive[pendingDrive] = false;
                _driveBusyMask = (byte)(_driveBusyMask & ~(1 << pendingDrive));
                _interruptPending = HasPendingInterrupt();
                Trace($"SENSE INTERRUPT pending drive={pendingDrive} ST0={status:X2} PCN={pcn}");
            }
            else
            {
                _output.Enqueue(0x80);
                _output.Enqueue(0);
                _interruptPending = false;
                Trace("SENSE INTERRUPT none");
            }

            EndCommand();
        }
        private void InvalidCommand()
        {
            Trace($"INVALID command={_command[0]:X2}");
            _output.Enqueue(0x80);
            EndCommand();
        }
        private bool TryGetSector(int physicalTrack, int physicalSide, int sectorTrackId, int sectorSideId, int sectorId, int sizeCode, out Plus3DiskSector? sector)
        {
            sector = null;
            if (SelectedDisk == null)
            {
                return false;
            }

            sector = SelectedDisk.FindSector(physicalTrack, physicalSide, sectorTrackId, sectorSideId, sectorId, sizeCode);
            return sector != null;
        }
        private void SelectDriveHead()
        {
            _activeDriveHead = (byte)(_command[1] & 0x07);
        }
        private bool IsSelectedDriveReady()
        {
            int drive = SelectedDrive;
            return IsValidDrive(drive) && _drives[drive] != null;
        }

        private int SelectedDrive => _activeDriveHead & 0x03;

        private int SelectedHead => (_activeDriveHead >> 2) & 0x01;

        private Plus3DiskImage? SelectedDisk
        {
            get
            {
                int drive = SelectedDrive;
                return IsValidDrive(drive) ? _drives[drive] : null;
            }
        }

        private byte CurrentTrack
        {
            get
            {
                int drive = SelectedDrive;
                return IsValidDrive(drive) ? _currentTracks[drive] : (byte)0;
            }
            set
            {
                int drive = SelectedDrive;
                if (IsValidDrive(drive))
                {
                    _currentTracks[drive] = value;
                }
            }
        }
        private static bool IsValidDrive(int drive)
        {
            return drive is >= 0 and < PhysicalDriveCount;
        }
        private static bool IsValidFdcDrive(int drive)
        {
            return drive is >= 0 and < FdcDriveCount;
        }
        private void SetPendingInterrupt(int drive, byte status)
        {
            if (!IsValidFdcDrive(drive))
            {
                return;
            }

            _pendingInterruptStatusByDrive[drive] = status;
            _interruptPendingByDrive[drive] = true;
            _driveBusyMask = (byte)(_driveBusyMask | (1 << drive));
            _interruptPending = true;
        }
        private void ClearPendingInterrupt(int drive)
        {
            if (!IsValidFdcDrive(drive))
            {
                return;
            }

            _interruptPendingByDrive[drive] = false;
            _pendingInterruptStatusByDrive[drive] = 0;
            _driveBusyMask = (byte)(_driveBusyMask & ~(1 << drive));
            _interruptPending = HasPendingInterrupt();
        }
        private int FindPendingInterruptDrive()
        {
            for (int i = 0; i < _interruptPendingByDrive.Length; i++)
            {
                if (_interruptPendingByDrive[i])
                {
                    return i;
                }
            }

            return -1;
        }
        private bool HasPendingInterrupt()
        {
            return FindPendingInterruptDrive() >= 0;
        }
        private void QueueReadWriteResult(byte st0, byte st1, byte st2, byte c, byte h, byte r, byte n)
        {
            byte resultSt0 = (byte)(st0 | _activeDriveHead);
            Trace($"RESULT ST0={resultSt0:X2} ST1={st1:X2} ST2={st2:X2} C={c} H={h} R={r:X2} N={n}");
            _output.Enqueue(resultSt0);
            _output.Enqueue(st1);
            _output.Enqueue(st2);
            _output.Enqueue(c);
            _output.Enqueue(h);
            _output.Enqueue(r);
            _output.Enqueue(n);
        }
        private void EndCommand()
        {
            _command.Clear();
            _expectedCommandLength = 0;
        }
        private void AbortActiveTransfer()
        {
            _command.Clear();
            _output.Clear();
            _writeBuffer.Clear();
            _sectorRun.Clear();
            _writeSector = null;
            _scanSector = null;
            _formatInProgress = false;
            _writeDeletedData = false;
            _scanMode = ScanMode.None;
            _formatTrack = 0;
            _formatSide = 0;
            _formatSizeCode = 0;
            _formatGapLength = 0;
            _formatFiller = 0;
            _expectedCommandLength = 0;
            _writeBytesExpected = 0;
            _readDataBytesRemaining = 0;
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
                Debug.WriteLine($"FDC trace write failed: {ex.Message}");
                _tracePath = null;
            }
        }
        private static string BytesToHex(IReadOnlyList<byte> bytes)
        {
            if (bytes.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(bytes.Count * 3);
            for (int i = 0; i < bytes.Count; i++)
            {
                if (i != 0)
                {
                    builder.Append(' ');
                }

                builder.Append(bytes[i].ToString("X2"));
            }

            return builder.ToString();
        }
        private static string CommandName(byte command)
        {
            return (command & 0x1F) switch
            {
                0x02 => "READ TRACK",
                0x03 => "SPECIFY",
                0x04 => "SENSE DRIVE STATUS",
                0x05 => "WRITE DATA",
                0x06 => "READ DATA",
                0x07 => "RECALIBRATE",
                0x08 => "SENSE INTERRUPT",
                0x09 => "WRITE DELETED DATA",
                0x0A => "READ ID",
                0x0C => "READ DELETED DATA",
                0x0D => "FORMAT TRACK",
                0x0F => "SEEK",
                0x11 => "SCAN EQUAL",
                0x19 => "SCAN LOW/EQUAL",
                0x1D => "SCAN HIGH/EQUAL",
                _ => "INVALID"
            };
        }
        private void ResetReadIdRotation()
        {
            _readIdTrack = -1;
            _readIdSide = -1;
            _readIdDrive = -1;
            _readIdIndex = 0;
        }
        private void ResetReadIdRotation(int drive)
        {
            if (_readIdDrive == drive)
            {
                ResetReadIdRotation();
            }
        }
        private static int GetCommandLength(byte command)
        {
            return (command & 0x1F) switch
            {
                0x02 => 9,
                0x03 => 3,
                0x04 => 2,
                0x05 => 9,
                0x06 => 9,
                0x07 => 2,
                0x08 => 1,
                0x09 => 9,
                0x0A => 2,
                0x0C => 9,
                0x0D => 6,
                0x0F => 3,
                0x11 => 9,
                0x19 => 9,
                0x1D => 9,
                _ => 1
            };
        }
        private enum ScanMode
        {
            None,
            Equal,
            LowOrEqual,
            HighOrEqual
        }
    }
}
