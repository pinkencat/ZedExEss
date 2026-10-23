using System;
using System.Collections.Generic;
using System.IO;

namespace ZedExEss.Spectrum.DivMmc
{
    /// <summary>
    /// SD/MMC SPI block-device facade used by DivMMC firmware, backed by either an image or projected host folder.
    /// </summary>
    /// <remarks>
    /// Folder-backed media is materialised as a temporary FAT16 image because
    /// esxDOS speaks sectors, not host filesystem calls. Persistent writable folders
    /// are exported back through a staging directory so the host folder is not
    /// partially replaced if conversion fails. A writable snapshot can instead
    /// discard all guest changes with its temporary image.
    /// </remarks>
    public sealed class SpectrumDivMmcSdCard : IDisposable
    {
        private const int BlockSize = 512;

        /// <summary>
        /// How many written sectors a card will journal before giving up on rewinding.
        /// </summary>
        /// <remarks>
        /// Each journalled sector costs 512 bytes of pristine content held by the card, plus
        /// 512 bytes in every snapshot that carries it. Sixteen thousand sectors is eight
        /// megabytes: comfortably more than a game writing a save file or NextZXOS writing its
        /// configuration, and far less than a guest formatting the card, which is exactly the
        /// case where rewinding the medium is neither affordable nor expected.
        /// </remarks>
        public const int SectorJournalLimit = 16384;

        private readonly FileStream _stream;
        private readonly string? _backingImagePath;
        private readonly string? _folderBackingPath;
        private readonly SpectrumFatProjectionManifest? _projectionManifest;
        private readonly bool _deleteBackingImageOnDispose;
        private readonly Queue<byte> _response = new();
        private readonly byte[] _command = new byte[6];
        private readonly byte[] _writeBuffer = new byte[BlockSize + 2];
        private int _commandLength;
        private bool _idle = true;
        private bool _appCommand;
        private bool _multipleRead;
        private uint _multipleReadLba;
        private WriteState _writeState;
        private uint _writeLba;
        private int _writeIndex;
        private bool _modified;
        private bool _folderBackingFlushed;

        // The sectors the guest has written since the card was mounted, and what each held
        // before it first wrote there. The first set says what a capture has to carry; the
        // second is what lets a restore undo a write the capture happened before.
        private readonly Dictionary<uint, byte[]> _pristineSectors = [];
        private bool _journalOverflowed;
        private SpectrumDivMmcSdCard(
            string path,
            FileStream stream,
            bool writeProtected,
            string? backingImagePath = null,
            string? folderBackingPath = null,
            bool deleteBackingImageOnDispose = false,
            SpectrumFatProjectionManifest? projectionManifest = null)
        {
            Path = path;
            _stream = stream;
            WriteProtected = writeProtected;
            SectorCount = stream.Length / BlockSize;
            _backingImagePath = backingImagePath;
            _folderBackingPath = folderBackingPath;
            _deleteBackingImageOnDispose = deleteBackingImageOnDispose;
            _projectionManifest = projectionManifest;
        }

        public string Path { get; }
        public bool WriteProtected { get; }
        public bool WritesBackToFolder => _folderBackingPath != null;

        /// <summary>True when the guest has written and the folder has not caught up.</summary>
        public bool HasUnflushedChanges => _folderBackingPath != null && _modified && !WriteProtected;

        /// <summary>Blocks the guest has written, for telling "has written" from "still writing".</summary>
        public ulong WriteCount { get; private set; }

        /// <summary>What the last write-back did, for reporting it.</summary>
        public SpectrumFatMergeResult LastFlushResult { get; private set; }
        public long SectorCount { get; }
        public static SpectrumDivMmcSdCard Open(string path, bool writeProtected)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("SD image path is empty.", nameof(path));
            }

            FileAccess access = writeProtected ? FileAccess.Read : FileAccess.ReadWrite;
            var stream = new FileStream(path, FileMode.Open, access, FileShare.Read);
            if (stream.Length < BlockSize || (stream.Length % BlockSize) != 0)
            {
                stream.Dispose();
                throw new InvalidDataException("SD image size must be a non-zero multiple of 512 bytes.");
            }

            return new SpectrumDivMmcSdCard(path, stream, writeProtected);
        }
        public static SpectrumDivMmcSdCard OpenFolderBacked(
            string folderPath,
            bool writeProtected,
            IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? overlayFiles = null,
            bool writeBackChanges = true)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                throw new DirectoryNotFoundException("Folder-backed storage directory does not exist.");
            }

            string fullFolderPath = System.IO.Path.GetFullPath(folderPath);
            if (new DirectoryInfo(fullFolderPath).Parent == null)
            {
                throw new IOException("Refusing to use a filesystem root as folder-backed DivMMC storage.");
            }

            int megabytes = EstimateImageMegabytes(fullFolderPath, overlayFiles);
            string tempImagePath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ZedExEss-DivMMC-Folder-" + Guid.NewGuid().ToString("N") + ".img");

            try
            {
                SpectrumFatImageBuilder.CreateBlankFat16Image(tempImagePath, megabytes);
                SpectrumFatProjectionManifest manifest =
                    SpectrumFatImageBuilder.ImportDirectoryIntoFat16Image(
                        tempImagePath,
                        fullFolderPath,
                        overlayFiles);

                FileAccess access = writeProtected ? FileAccess.Read : FileAccess.ReadWrite;
                var stream = new FileStream(tempImagePath, FileMode.Open, access, FileShare.Read);
                return new SpectrumDivMmcSdCard(
                    fullFolderPath,
                    stream,
                    writeProtected,
                    backingImagePath: tempImagePath,
                    folderBackingPath: !writeProtected && writeBackChanges ? fullFolderPath : null,
                    deleteBackingImageOnDispose: true,
                    projectionManifest: manifest);
            }
            catch
            {
                if (File.Exists(tempImagePath))
                {
                    File.Delete(tempImagePath);
                }

                throw;
            }
        }
        public static SpectrumDivMmcSdCard OpenWritableFolderSnapshot(
            string folderPath,
            IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? overlayFiles = null) =>
            OpenFolderBacked(
                folderPath,
                writeProtected: false,
                overlayFiles: overlayFiles,
                writeBackChanges: false);
        /// <summary>
        /// Writes the guest's changes back into the projected folder.
        /// </summary>
        /// <remarks>
        /// Safe to call repeatedly: it does nothing until the guest has written something
        /// new, so a host can flush at whatever moments suit it - ejecting the card,
        /// swapping the machine, closing down - without paying for a merge each time.
        /// </remarks>
        public void FlushFolderBacking()
        {
            if (_folderBackingPath == null || _backingImagePath == null || _folderBackingFlushed)
            {
                return;
            }

            if (WriteProtected || !_modified)
            {
                _folderBackingFlushed = true;
                return;
            }

            _stream.Flush(flushToDisk: true);
            if (_projectionManifest != null)
            {
                LastFlushResult = SpectrumFatImageBuilder.MergeFat16ImageIntoDirectory(
                    _backingImagePath,
                    _folderBackingPath,
                    _projectionManifest);
            }
            else
            {
                SpectrumFatImageBuilder.ExportFat16ImageToDirectory(_backingImagePath, _folderBackingPath);
            }

            _folderBackingFlushed = true;
            _modified = false;
        }
        public byte Transfer(byte mosi)
        {
            if (_response.Count > 0)
            {
                // SPI is full-duplex, but for command responses the card mostly clocks out
                // queued bytes while ignoring MOSI.
                return _response.Dequeue();
            }

            if (HandleWriteTransfer(mosi, out byte writeResponse))
            {
                return writeResponse;
            }

            if (_commandLength > 0 || (mosi & 0xC0) == 0x40)
            {
                // Commands are six bytes: start/command, 32-bit argument and CRC/dummy byte.
                _command[_commandLength++] = mosi;
                if (_commandLength == _command.Length)
                {
                    ExecuteCommand();
                    _commandLength = 0;
                }

                return 0xFF;
            }

            if (_multipleRead)
            {
                // CMD18 keeps streaming blocks until CMD12 stops it.
                EnqueueReadBlock(_multipleReadLba++, includeR1: false);
                return _response.Count == 0 ? (byte)0xFF : _response.Dequeue();
            }

            return 0xFF;
        }
        public void Reset()
        {
            // Power-on reset leaves the card in idle and clears partial SPI framing.
            _response.Clear();
            _commandLength = 0;
            _idle = true;
            _appCommand = false;
            _multipleRead = false;
            _writeState = WriteState.None;
            _writeIndex = 0;
        }
        public void Deselect()
        {
            // Chip-select high cancels partial commands/transfers but does not clear app-command
            // state; esxDOS toggles CS between CMD55 and ACMD41 during initialisation.
            _response.Clear();
            _commandLength = 0;
            _multipleRead = false;
            _writeState = WriteState.None;
            _writeIndex = 0;
        }
        public void Dispose()
        {
            try
            {
                FlushFolderBacking();
            }
            catch
            {
                // User-initiated detach paths flush explicitly and report errors. Dispose must not
                // bring down the emulator during application shutdown.
            }
            finally
            {
                _stream.Dispose();
                if (_deleteBackingImageOnDispose && _backingImagePath != null)
                {
                    try
                    {
                        File.Delete(_backingImagePath);
                    }
                    catch
                    {
                    }
                }
            }
        }
        private bool HandleWriteTransfer(byte mosi, out byte response)
        {
            response = 0xFF;

            switch (_writeState)
            {
                case WriteState.AwaitSingleToken:
                    // Single-block writes start with a 0xFE data token.
                    if (mosi == 0xFE)
                    {
                        _writeIndex = 0;
                        _writeState = WriteState.ReceiveSingleBlock;
                    }

                    return true;

                case WriteState.AwaitMultipleToken:
                    // Multi-block writes use 0xFC for each block and 0xFD as stop token.
                    if (mosi == 0xFC)
                    {
                        _writeIndex = 0;
                        _writeState = WriteState.ReceiveMultipleBlock;
                    }
                    else if (mosi == 0xFD)
                    {
                        _writeState = WriteState.None;
                        _response.Enqueue(0xFF);
                    }

                    return true;

                case WriteState.ReceiveSingleBlock:
                case WriteState.ReceiveMultipleBlock:
                    _writeBuffer[_writeIndex++] = mosi;
                    if (_writeIndex == _writeBuffer.Length)
                    {
                        // Buffer includes the 512-byte payload plus two CRC bytes, which are ignored.
                        bool accepted = TryWriteBlock(_writeLba);
                        _response.Enqueue((byte)(accepted ? 0x05 : 0x0D));
                        _response.Enqueue(0x00);
                        _response.Enqueue(0xFF);

                        if (_writeState == WriteState.ReceiveMultipleBlock)
                        {
                            _writeLba++;
                            _writeState = WriteState.AwaitMultipleToken;
                        }
                        else
                        {
                            _writeState = WriteState.None;
                        }
                    }

                    return true;

                default:
                    return false;
            }
        }
        private void ExecuteCommand()
        {
            byte command = (byte)(_command[0] & 0x3F);
            uint argument = ((uint)_command[1] << 24)
                | ((uint)_command[2] << 16)
                | ((uint)_command[3] << 8)
                | _command[4];
            bool appCommand = _appCommand;
            _appCommand = false;

            // ACMD commands are represented as CMD55 followed by a normal command number.
            if (appCommand && command == 41)
            {
                _idle = false;
                EnqueueR1();
                return;
            }

            if (appCommand && command == 23)
            {
                EnqueueR1();
                return;
            }

            switch (command)
            {
                case 0:
                    _idle = true;
                    _multipleRead = false;
                    _writeState = WriteState.None;
                    EnqueueR1();
                    break;

                case 1:
                    _idle = false;
                    EnqueueR1();
                    break;

                case 8:
                    EnqueueR1();
                    _response.Enqueue(0x00);
                    _response.Enqueue(0x00);
                    _response.Enqueue((byte)((argument >> 8) & 0xFF));
                    _response.Enqueue((byte)(argument & 0xFF));
                    break;

                case 9:
                    EnqueueRegisterBlock(BuildCsd());
                    break;

                case 10:
                    EnqueueRegisterBlock(BuildCid());
                    break;

                case 12:
                    _multipleRead = false;
                    _response.Enqueue(0xFF);
                    EnqueueR1();
                    break;

                case 13:
                    EnqueueR1();
                    _response.Enqueue(0x00);
                    break;

                case 16:
                    EnqueueR1(argument == BlockSize ? (byte)0x00 : (byte)0x04);
                    break;

                case 23:
                    EnqueueR1();
                    break;

                case 17:
                    EnqueueReadBlock(argument, includeR1: true);
                    break;

                case 18:
                    _multipleRead = true;
                    _multipleReadLba = argument + 1;
                    EnqueueReadBlock(argument, includeR1: true);
                    break;

                case 24:
                    EnqueueR1();
                    _writeLba = argument;
                    _writeState = WriteState.AwaitSingleToken;
                    break;

                case 25:
                    EnqueueR1();
                    _writeLba = argument;
                    _writeState = WriteState.AwaitMultipleToken;
                    break;

                case 55:
                    _appCommand = true;
                    EnqueueR1();
                    break;

                case 58:
                    EnqueueR1();
                    _response.Enqueue(0xC0);
                    _response.Enqueue(0x00);
                    _response.Enqueue(0x00);
                    _response.Enqueue(0x00);
                    break;

                case 59:
                    EnqueueR1();
                    break;

                default:
                    EnqueueR1(0x04);
                    break;
            }
        }
        private void EnqueueR1(byte extraBits = 0)
        {
            _response.Enqueue((byte)((_idle ? 0x01 : 0x00) | extraBits));
        }
        private byte CurrentR1(byte extraBits = 0)
        {
            return (byte)((_idle ? 0x01 : 0x00) | extraBits);
        }
        private void EnqueueReadBlock(uint lba, bool includeR1)
        {
            if (!TryReadBlock(lba, out byte[] data))
            {
                if (includeR1)
                {
                    EnqueueR1(0x20);
                }

                _multipleRead = false;
                return;
            }

            if (includeR1)
            {
                EnqueueR1();
            }

            // Data response format: optional R1, busy byte, 0xFE token, payload, dummy CRC.
            _response.Enqueue(0xFF);
            _response.Enqueue(0xFE);
            for (int i = 0; i < data.Length; i++)
            {
                _response.Enqueue(data[i]);
            }

            _response.Enqueue(0xFF);
            _response.Enqueue(0xFF);
        }
        private void EnqueueRegisterBlock(byte[] data)
        {
            EnqueueR1();
            _response.Enqueue(0xFF);
            _response.Enqueue(0xFE);
            for (int i = 0; i < data.Length; i++)
            {
                _response.Enqueue(data[i]);
            }

            _response.Enqueue(0xFF);
            _response.Enqueue(0xFF);
        }
        private bool TryReadBlock(uint lba, out byte[] data)
        {
            data = new byte[BlockSize];
            if (lba >= SectorCount)
            {
                return false;
            }

            _stream.Position = (long)lba * BlockSize;
            int read = 0;
            while (read < BlockSize)
            {
                int count = _stream.Read(data, read, BlockSize - read);
                if (count == 0)
                {
                    return false;
                }

                read += count;
            }

            return true;
        }
        private bool TryWriteBlock(uint lba)
        {
            if (WriteProtected || lba >= SectorCount)
            {
                return false;
            }

            JournalSectorBeforeWrite(lba);
            _stream.Position = (long)lba * BlockSize;
            _stream.Write(_writeBuffer, 0, BlockSize);
            _stream.Flush();
            _modified = true;
            WriteCount++;

            // A later write makes the folder stale again, so the one-shot latch that
            // stops a flush repeating itself has to reopen.
            _folderBackingFlushed = false;
            return true;
        }
        /// <summary>Keeps what a sector held before the guest first wrote to it.</summary>
        /// <remarks>
        /// Once past the limit the journal is abandoned and its memory released rather than
        /// kept half-complete: a capture that carried some of a format would restore a card
        /// into a state the guest never saw, which is worse than admitting the card cannot be
        /// put back.
        /// </remarks>
        private void JournalSectorBeforeWrite(uint lba)
        {
            if (_journalOverflowed || _pristineSectors.ContainsKey(lba))
            {
                return;
            }

            if (_pristineSectors.Count >= SectorJournalLimit)
            {
                _journalOverflowed = true;
                _pristineSectors.Clear();
                return;
            }

            if (TryReadBlock(lba, out byte[] before))
            {
                _pristineSectors[lba] = before;
            }
        }

        /// <summary>True when every sector the guest has written can still be put back.</summary>
        public bool SectorJournalComplete => !_journalOverflowed;

        /// <summary>How many distinct sectors the guest has written since the card was mounted.</summary>
        public int JournalledSectorCount => _pristineSectors.Count;

        /// <summary>Describes the medium, for a restore to compare against.</summary>
        public SpectrumSdCardIdentity CaptureIdentity() => new(
            Path,
            SectorCount,
            WriteProtected,
            _folderBackingPath != null || _backingImagePath != null,
            WritesBackToFolder);

        /// <summary>
        /// Captures the SPI link and the sectors the guest has changed.
        /// </summary>
        /// <remarks>
        /// The sector contents are read back out of the backing file rather than kept in
        /// memory as they are written, because the file is the authority and the host's page
        /// cache has them anyway. The journal is sorted so two captures of the same card
        /// produce the same bytes, which is what lets a check compare files.
        /// </remarks>
        public SpectrumSdCardState CaptureState()
        {
            uint[] lbas = _journalOverflowed ? [] : [.. _pristineSectors.Keys];
            Array.Sort(lbas);

            var contents = new byte[lbas.Length * BlockSize];
            int written = 0;
            foreach (uint lba in lbas)
            {
                if (TryReadBlock(lba, out byte[] sector))
                {
                    sector.CopyTo(contents, written * BlockSize);
                }

                written++;
            }

            return new SpectrumSdCardState(
                CaptureIdentity(),
                _command.ToArray(),
                _commandLength,
                [.. _response],
                _idle,
                _appCommand,
                _multipleRead,
                _multipleReadLba,
                (byte)_writeState,
                _writeLba,
                _writeIndex,
                _writeBuffer.ToArray(),
                _modified,
                _folderBackingFlushed,
                WriteCount,
                lbas,
                contents,
                !_journalOverflowed);
        }

        /// <summary>
        /// Puts the SPI link back, and the sectors with it when that can be done honestly.
        /// </summary>
        /// <remarks>
        /// Three things can stop the sectors going back, and each is reported rather than
        /// hidden. The medium may not be the one that was captured, in which case nothing is
        /// written - a state file does not get to choose which file on the host it writes to.
        /// The journal may have overflowed, in which case the card is left as it is. Or the
        /// card may be write-protected, in which case the guest never changed it and there is
        /// nothing to undo.
        ///
        /// When the sectors do go back, the card is put back to the captured content
        /// <em>exactly</em>: sectors the guest has written since the capture are rewound to
        /// what the capture holds, and sectors it wrote after the capture that the capture
        /// does not mention are rewound to what they held when the card was mounted. Writing
        /// only the captured ones would leave the card ahead of the machine, which is the
        /// state in which a filesystem driver corrupts a card.
        /// </remarks>
        public SpectrumSdCardRestoreResult RestoreState(SpectrumSdCardState state)
        {
            ArgumentNullException.ThrowIfNull(state);

            SpectrumSdCardIdentity mounted = CaptureIdentity();
            if (state.Identity != mounted)
            {
                return SpectrumSdCardRestoreResult.Refused(
                    $"the card in this slot is {DescribeMedium(mounted)}, not "
                        + $"{DescribeMedium(state.Identity)}");
            }

            if (state.Command.Length != _command.Length
                || state.WriteBuffer.Length != _writeBuffer.Length)
            {
                return SpectrumSdCardRestoreResult.Refused("its SPI buffers are the wrong size");
            }

            state.Command.CopyTo(_command, 0);
            _commandLength = state.CommandLength;
            _response.Clear();
            foreach (byte value in state.Response)
            {
                _response.Enqueue(value);
            }

            _idle = state.Idle;
            _appCommand = state.AppCommand;
            _multipleRead = state.MultipleRead;
            _multipleReadLba = state.MultipleReadLba;
            _writeState = (WriteState)state.WriteState;
            _writeLba = state.WriteLba;
            _writeIndex = state.WriteIndex;
            state.WriteBuffer.CopyTo(_writeBuffer, 0);
            WriteCount = state.WriteCount;
            _folderBackingFlushed = state.FolderBackingFlushed;

            if (WriteProtected)
            {
                _modified = state.Modified;
                return new SpectrumSdCardRestoreResult(true, true, 0, null);
            }

            if (!state.SectorsComplete || !SectorJournalComplete)
            {
                _modified = state.Modified;
                return new SpectrumSdCardRestoreResult(
                    true,
                    false,
                    0,
                    $"{System.IO.Path.GetFileName(Path)} was written to too heavily to be put back");
            }

            int sectorsWritten = RewindSectors(state);
            _modified = state.Modified || sectorsWritten > 0;
            return new SpectrumSdCardRestoreResult(true, true, sectorsWritten, null);
        }

        /// <summary>Writes the captured sectors back, and undoes the ones written since.</summary>
        private int RewindSectors(SpectrumSdCardState state)
        {
            var captured = new Dictionary<uint, int>(state.DirtySectorLbas.Length);
            for (int index = 0; index < state.DirtySectorLbas.Length; index++)
            {
                captured[state.DirtySectorLbas[index]] = index;
            }

            int sectorsWritten = 0;
            foreach (uint lba in _pristineSectors.Keys)
            {
                if (captured.ContainsKey(lba))
                {
                    continue;
                }

                if (WriteSectorDirect(lba, _pristineSectors[lba], 0))
                {
                    sectorsWritten++;
                }
            }

            foreach ((uint lba, int index) in captured)
            {
                if (WriteSectorDirect(lba, state.DirtySectorData, index * BlockSize))
                {
                    sectorsWritten++;
                }
            }

            if (sectorsWritten > 0)
            {
                _stream.Flush();

                // A restore is a change like any other as far as the folder is concerned, so
                // the one-shot latch that stops a flush repeating itself has to reopen.
                _folderBackingFlushed = false;
            }

            return sectorsWritten;
        }

        private bool WriteSectorDirect(uint lba, byte[] source, int offset)
        {
            if (WriteProtected || lba >= SectorCount || offset + BlockSize > source.Length)
            {
                return false;
            }

            _stream.Position = (long)lba * BlockSize;
            _stream.Write(source, offset, BlockSize);
            return true;
        }

        private static string DescribeMedium(SpectrumSdCardIdentity identity) =>
            identity.FolderBacked
                ? $"the folder {System.IO.Path.GetFileName(identity.Path)}"
                : System.IO.Path.GetFileName(identity.Path);

        private static int EstimateImageMegabytes(
            string folderPath,
            IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? overlayFiles)
        {
            long bytes = 0;
            foreach (string file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                bytes += new FileInfo(file).Length;
            }

            if (overlayFiles != null)
            {
                foreach (ReadOnlyMemory<byte> data in overlayFiles.Values)
                {
                    bytes += data.Length;
                }
            }

            long required = Math.Max(16L * 1024 * 1024, bytes + (bytes / 2) + (8L * 1024 * 1024));
            int megabytes = (int)Math.Ceiling(required / (1024.0 * 1024.0));
            megabytes = Math.Max(64, megabytes);
            megabytes = ((megabytes + 15) / 16) * 16;
            return Math.Min(512, megabytes);
        }
        private byte[] BuildCsd()
        {
            uint cSize = (uint)Math.Max(0, Math.Min(0x3FFFFF, (SectorCount / 1024) - 1));
            byte[] csd = new byte[16];
            csd[0] = 0x40;
            csd[1] = 0x0E;
            csd[2] = 0x00;
            csd[3] = 0x32;
            csd[4] = 0x5B;
            csd[5] = 0x59;
            csd[6] = 0x00;
            csd[7] = (byte)((cSize >> 16) & 0x3F);
            csd[8] = (byte)(cSize >> 8);
            csd[9] = (byte)cSize;
            csd[10] = 0x7F;
            csd[11] = 0x80;
            csd[12] = 0x0A;
            csd[13] = 0x40;
            csd[14] = 0x00;
            csd[15] = 0x01;
            return csd;
        }
        private static byte[] BuildCid()
        {
            byte[] cid = new byte[16];
            cid[0] = 0x03;
            cid[1] = (byte)'Z';
            cid[2] = (byte)'E';
            cid[3] = (byte)'D';
            cid[4] = (byte)'X';
            cid[5] = (byte)'S';
            cid[6] = (byte)'D';
            cid[7] = 0x00;
            cid[8] = 0x10;
            cid[9] = 0x00;
            cid[10] = 0x00;
            cid[11] = 0x00;
            cid[12] = 0x01;
            cid[13] = 0x01;
            cid[14] = 0x01;
            cid[15] = 0x01;
            return cid;
        }
        private enum WriteState
        {
            None,
            AwaitSingleToken,
            AwaitMultipleToken,
            ReceiveSingleBlock,
            ReceiveMultipleBlock
        }
    }
}
