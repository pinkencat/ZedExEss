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
    /// esxDOS speaks sectors, not host filesystem calls. On clean disposal, modified
    /// files are exported back through a staging directory so the host folder is not
    /// partially replaced if conversion fails.
    /// </remarks>
    public sealed class SpectrumDivMmcSdCard : IDisposable
    {
        private const int BlockSize = 512;
        private readonly FileStream _stream;
        private readonly string? _backingImagePath;
        private readonly string? _folderBackingPath;
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
        private SpectrumDivMmcSdCard(
            string path,
            FileStream stream,
            bool writeProtected,
            string? backingImagePath = null,
            string? folderBackingPath = null,
            bool deleteBackingImageOnDispose = false)
        {
            Path = path;
            _stream = stream;
            WriteProtected = writeProtected;
            SectorCount = stream.Length / BlockSize;
            _backingImagePath = backingImagePath;
            _folderBackingPath = folderBackingPath;
            _deleteBackingImageOnDispose = deleteBackingImageOnDispose;
        }

        public string Path { get; }
        public bool WriteProtected { get; }
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
        public static SpectrumDivMmcSdCard OpenFolderBacked(string folderPath, bool writeProtected)
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

            int megabytes = EstimateImageMegabytes(fullFolderPath);
            string tempImagePath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ZedExEss-DivMMC-Folder-" + Guid.NewGuid().ToString("N") + ".img");

            try
            {
                SpectrumFatImageBuilder.CreateBlankFat16Image(tempImagePath, megabytes);
                SpectrumFatImageBuilder.ImportDirectoryIntoFat16Image(tempImagePath, fullFolderPath);

                FileAccess access = writeProtected ? FileAccess.Read : FileAccess.ReadWrite;
                var stream = new FileStream(tempImagePath, FileMode.Open, access, FileShare.Read);
                return new SpectrumDivMmcSdCard(
                    fullFolderPath,
                    stream,
                    writeProtected,
                    backingImagePath: tempImagePath,
                    folderBackingPath: fullFolderPath,
                    deleteBackingImageOnDispose: true);
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
            SpectrumFatImageBuilder.ExportFat16ImageToDirectory(_backingImagePath, _folderBackingPath);
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

            _stream.Position = (long)lba * BlockSize;
            _stream.Write(_writeBuffer, 0, BlockSize);
            _stream.Flush();
            _modified = true;
            return true;
        }
        private static int EstimateImageMegabytes(string folderPath)
        {
            long bytes = 0;
            foreach (string file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                bytes += new FileInfo(file).Length;
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
