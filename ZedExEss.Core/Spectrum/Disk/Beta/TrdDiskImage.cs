using System;
using System.Collections.Generic;
using System.IO;

namespace ZedExEss.Spectrum.Disk.Beta
{
    /// <summary>
    /// Raw TR-DOS sector image with SCL import/export helpers for Beta 128 compatible drives.
    /// </summary>
    /// <remarks>
    /// SCL is a catalogue-plus-file container rather than a physical disk image. It
    /// is expanded into canonical TRD geometry and remains read-only until explicitly
    /// saved as TRD, avoiding accidental overwrite of the source container.
    /// </remarks>
    public sealed class TrdDiskImage
    {
        public const int SectorSize = 256;
        public const int SectorsPerTrack = 16;
        private const int TrackSize = SectorSize * SectorsPerTrack;
        private const int DirectorySectors = 8;
        private const int DirectoryEntrySize = 16;
        private const int SclHeaderSize = 9;
        private const int SclEntrySize = 14;
        private const int MaximumTracks = 80;
        private const int MaximumSides = 2;
        private const int DefaultTracks = 80;
        private const int DefaultSides = 2;
        private const int DefaultDataStartLogicalTrack = 1;
        private const int MaximumDirectoryEntries = DirectorySectors * SectorSize / DirectoryEntrySize;
        private const int DiskInformationOffset = DirectorySectors * SectorSize;
        private const int DiskTitleOffset = DiskInformationOffset + 0xF5;

        private readonly byte[] _data;
        private TrdDiskImage(string path, byte[] data, int tracks, int sides)
        {
            Path = path;
            _data = data;
            TrackCount = tracks;
            SideCount = sides;
            ReadAddressSectorIdMask = DetectAmdDirectLoaderSectorIds(data) ? (byte)0x40 : (byte)0x00;
        }

        public string Path { get; private set; }
        public int TrackCount { get; }
        public int SideCount { get; }
        public bool SupportsRawWriteback { get; private set; }
        public bool IsWriteProtected { get; set; }

        internal byte ReadAddressSectorIdMask { get; }

        /// <summary>
        /// Returns the sector ID which a rotational READ ADDRESS command sees in
        /// the requested physical slot.
        /// </summary>
        /// <remarks>
        /// TRD stores sector payloads only, so ID fields are reconstructed using the
        /// standard TR-DOS interleave: 1,9,2,10,...,8,16.
        /// </remarks>
        public byte GetPhysicalSectorId(int physicalSlot)
        {
            if ((uint)physicalSlot >= SectorsPerTrack)
            {
                throw new ArgumentOutOfRangeException(nameof(physicalSlot));
            }

            int logicalSector = (physicalSlot & 1) == 0
                ? (physicalSlot >> 1) + 1
                : (physicalSlot >> 1) + 9;
            return (byte)logicalSector;
        }

        private static bool DetectAmdDirectLoaderSectorIds(ReadOnlySpan<byte> data)
        {
            ReadOnlySpan<byte> label = "AMD4ever"u8;
            return data.Length >= DiskTitleOffset + label.Length &&
                   data.Slice(DiskTitleOffset, label.Length).SequenceEqual(label);
        }

        public static TrdDiskImage Load(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length == 0 || data.Length % TrackSize != 0)
            {
                throw new InvalidDataException($"TRD image {path} has invalid size {data.Length}.");
            }

            int trackSides = data.Length / TrackSize;
            int sides;
            int tracks;
            if (trackSides % MaximumSides == 0 && trackSides / MaximumSides <= MaximumTracks)
            {
                sides = MaximumSides;
                tracks = trackSides / MaximumSides;
            }
            else if (trackSides <= MaximumTracks)
            {
                sides = 1;
                tracks = trackSides;
            }
            else
            {
                throw new InvalidDataException($"TRD image {path} has unsupported geometry for size {data.Length}.");
            }

            return new TrdDiskImage(path, data, tracks, sides)
            {
                SupportsRawWriteback = true,
                IsWriteProtected = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0
            };
        }
        public static TrdDiskImage LoadScl(string path)
        {
            byte[] source = File.ReadAllBytes(path);
            if (source.Length < SclHeaderSize || !HasSclSignature(source))
            {
                throw new InvalidDataException($"SCL image {path} does not have a SINCLAIR signature.");
            }

            int fileCount = source[8];
            int headersOffset = SclHeaderSize;
            int dataOffset = headersOffset + (fileCount * SclEntrySize);
            if (fileCount > MaximumDirectoryEntries || dataOffset > source.Length)
            {
                throw new InvalidDataException($"SCL image {path} has an invalid catalogue.");
            }

            byte[] data = new byte[DefaultTracks * DefaultSides * TrackSize];
            int currentSector = 0;
            int currentLogicalTrack = DefaultDataStartLogicalTrack;
            int sourceOffset = dataOffset;
            int deletedFileCount = 0;

            for (int entryIndex = 0; entryIndex < fileCount; entryIndex++)
            {
                int sclEntryOffset = headersOffset + (entryIndex * SclEntrySize);
                int sectors = source[sclEntryOffset + 13];
                int byteCount = sectors * SectorSize;
                if (sourceOffset + byteCount > source.Length)
                {
                    throw new InvalidDataException($"SCL image {path} is truncated in file {entryIndex + 1}.");
                }

                int trdEntryOffset = entryIndex * DirectoryEntrySize;
                Buffer.BlockCopy(source, sclEntryOffset, data, trdEntryOffset, SclEntrySize);
                if (data[trdEntryOffset] == 0x01)
                {
                    deletedFileCount++;
                }

                data[trdEntryOffset + 14] = (byte)currentSector;
                data[trdEntryOffset + 15] = (byte)currentLogicalTrack;

                WriteFileSectors(
                    data,
                    DefaultSides,
                    ref currentLogicalTrack,
                    ref currentSector,
                    source.AsSpan(sourceOffset, byteCount),
                    sectors);
                sourceOffset += byteCount;
            }

            WriteDiskInformation(data, DefaultTracks, DefaultSides, fileCount, deletedFileCount, currentLogicalTrack, currentSector);
            return new TrdDiskImage(path, data, DefaultTracks, DefaultSides)
            {
                SupportsRawWriteback = false,
                IsWriteProtected = true
            };
        }
        public bool TryReadSector(int track, int side, int sectorId, Span<byte> destination)
        {
            if (destination.Length < SectorSize || !TryGetSectorOffset(track, side, sectorId, out int offset))
            {
                return false;
            }

            _data.AsSpan(offset, SectorSize).CopyTo(destination);
            return true;
        }
        public bool TryWriteSector(int track, int side, int sectorId, ReadOnlySpan<byte> source)
        {
            if (IsWriteProtected || !SupportsRawWriteback || source.Length < SectorSize || !TryGetSectorOffset(track, side, sectorId, out int offset))
            {
                return false;
            }

            source[..SectorSize].CopyTo(_data.AsSpan(offset, SectorSize));
            File.WriteAllBytes(Path, _data);
            return true;
        }
        public void SaveAs(string path)
        {
            File.WriteAllBytes(path, _data);
            Path = path;
            SupportsRawWriteback = true;
            IsWriteProtected = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        public void ExportScl(string path)
        {
            List<SclEntry> entries = ReadSclExportEntries();
            using var stream = new MemoryStream();
            WriteAscii(stream, "SINCLAIR");
            stream.WriteByte((byte)entries.Count);

            for (int i = 0; i < entries.Count; i++)
            {
                stream.Write(entries[i].Header);
            }

            Span<byte> sector = stackalloc byte[SectorSize];
            for (int i = 0; i < entries.Count; i++)
            {
                SclEntry entry = entries[i];
                int logicalTrack = entry.StartLogicalTrack;
                int sectorIndex = entry.StartSector;
                for (int sectorNumber = 0; sectorNumber < entry.SectorCount; sectorNumber++)
                {
                    if (!TryReadLogicalSector(logicalTrack, sectorIndex, sector))
                    {
                        throw new InvalidDataException($"TR-DOS file entry {i + 1} points outside the mounted disk image.");
                    }

                    stream.Write(sector);
                    AdvanceLogicalSector(SideCount, ref logicalTrack, ref sectorIndex);
                }
            }

            byte[] output = stream.ToArray();
            uint checksum = 0;
            for (int i = 0; i < output.Length; i++)
            {
                checksum += output[i];
            }

            using var file = File.Create(path);
            file.Write(output);
            file.WriteByte((byte)(checksum & 0xFF));
            file.WriteByte((byte)((checksum >> 8) & 0xFF));
            file.WriteByte((byte)((checksum >> 16) & 0xFF));
            file.WriteByte((byte)((checksum >> 24) & 0xFF));
        }
        private bool TryGetSectorOffset(int track, int side, int sectorId, out int offset)
        {
            offset = 0;
            if (track < 0 || track >= TrackCount ||
                side < 0 || side >= SideCount ||
                sectorId < 1 || sectorId > SectorsPerTrack)
            {
                return false;
            }

            int trackSide = (track * SideCount) + side;
            offset = (trackSide * TrackSize) + ((sectorId - 1) * SectorSize);
            return offset + SectorSize <= _data.Length;
        }
        private bool TryReadLogicalSector(int logicalTrack, int sectorIndex, Span<byte> destination)
        {
            if (destination.Length < SectorSize ||
                logicalTrack < 0 ||
                sectorIndex < 0 ||
                sectorIndex >= SectorsPerTrack)
            {
                return false;
            }

            int track = logicalTrack / SideCount;
            int side = logicalTrack % SideCount;
            return TryReadSector(track, side, sectorIndex + 1, destination);
        }
        private List<SclEntry> ReadSclExportEntries()
        {
            var entries = new List<SclEntry>();
            for (int entryIndex = 0; entryIndex < MaximumDirectoryEntries; entryIndex++)
            {
                int entryOffset = entryIndex * DirectoryEntrySize;
                byte first = _data[entryOffset];
                if (first == 0x00)
                {
                    break;
                }

                if (first == 0x01)
                {
                    continue;
                }

                byte sectors = _data[entryOffset + 13];
                if (sectors == 0)
                {
                    continue;
                }

                byte[] header = new byte[SclEntrySize];
                Buffer.BlockCopy(_data, entryOffset, header, 0, SclEntrySize);
                entries.Add(new SclEntry(header, sectors, _data[entryOffset + 14], _data[entryOffset + 15]));
            }

            if (entries.Count > byte.MaxValue)
            {
                throw new InvalidDataException("Too many TR-DOS files to export as SCL.");
            }

            return entries;
        }
        private static bool HasSclSignature(ReadOnlySpan<byte> data)
        {
            ReadOnlySpan<byte> signature = "SINCLAIR"u8;
            return data.Length >= signature.Length && data[..signature.Length].SequenceEqual(signature);
        }

        private static void WriteFileSectors(
            byte[] disk,
            int sides,
            ref int currentLogicalTrack,
            ref int currentSector,
            ReadOnlySpan<byte> source,
            int sectorCount)
        {
            for (int sector = 0; sector < sectorCount; sector++)
            {
                if (!TryGetLogicalSectorOffset(disk.Length, sides, currentLogicalTrack, currentSector, out int destinationOffset))
                {
                    throw new InvalidDataException("SCL image contents do not fit on a standard 80 track double-sided TR-DOS disk.");
                }

                source.Slice(sector * SectorSize, SectorSize).CopyTo(disk.AsSpan(destinationOffset, SectorSize));
                AdvanceLogicalSector(sides, ref currentLogicalTrack, ref currentSector);
            }
        }
        private static bool TryGetLogicalSectorOffset(int diskLength, int sides, int logicalTrack, int sectorIndex, out int offset)
        {
            offset = 0;
            if (logicalTrack < 0 || sectorIndex < 0 || sectorIndex >= SectorsPerTrack)
            {
                return false;
            }

            int track = logicalTrack / sides;
            int side = logicalTrack % sides;
            int trackSide = (track * sides) + side;
            offset = (trackSide * TrackSize) + (sectorIndex * SectorSize);
            return offset >= 0 && offset + SectorSize <= diskLength;
        }
        private static void AdvanceLogicalSector(int sides, ref int logicalTrack, ref int sectorIndex)
        {
            sectorIndex++;
            if (sectorIndex >= SectorsPerTrack)
            {
                sectorIndex = 0;
                logicalTrack++;
            }
        }
        private static void WriteDiskInformation(byte[] disk, int tracks, int sides, int fileCount, int deletedFileCount, int firstFreeTrack, int firstFreeSector)
        {
            int infoOffset = DiskInformationOffset;
            disk[infoOffset] = 0x00;
            disk[infoOffset + 0xE1] = (byte)firstFreeSector;
            disk[infoOffset + 0xE2] = (byte)firstFreeTrack;
            disk[infoOffset + 0xE3] = GetDiskType(tracks, sides);
            disk[infoOffset + 0xE4] = (byte)fileCount;

            int totalDataSectors = ((tracks * sides) - DefaultDataStartLogicalTrack) * SectorsPerTrack;
            int usedDataSectors = ((firstFreeTrack - DefaultDataStartLogicalTrack) * SectorsPerTrack) + firstFreeSector;
            int freeSectors = Math.Max(0, totalDataSectors - usedDataSectors);
            disk[infoOffset + 0xE5] = (byte)(freeSectors & 0xFF);
            disk[infoOffset + 0xE6] = (byte)((freeSectors >> 8) & 0xFF);
            disk[infoOffset + 0xE7] = 0x10;

            disk.AsSpan(infoOffset + 0xEA, 9).Fill(0x20);
            disk[infoOffset + 0xF4] = (byte)deletedFileCount;
            WriteAscii(disk.AsSpan(infoOffset + 0xF5, 8), "ZEDEXESS");
        }
        private static byte GetDiskType(int tracks, int sides)
        {
            return (tracks, sides) switch
            {
                (80, 2) => 0x16,
                (40, 2) => 0x17,
                (80, 1) => 0x18,
                (40, 1) => 0x19,
                _ => 0x16
            };
        }
        private static void WriteAscii(Stream stream, string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                stream.WriteByte((byte)text[i]);
            }
        }
        private static void WriteAscii(Span<byte> destination, string text)
        {
            destination.Clear();
            int count = Math.Min(destination.Length, text.Length);
            for (int i = 0; i < count; i++)
            {
                destination[i] = (byte)text[i];
            }
        }
        private readonly struct SclEntry(byte[] header, byte sectorCount, byte startSector, byte startLogicalTrack)
        {
            public byte[] Header { get; } = header;
            public byte SectorCount { get; } = sectorCount;
            public byte StartSector { get; } = startSector;
            public byte StartLogicalTrack { get; } = startLogicalTrack;
        }
    }
}
