using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ZedExEss.Spectrum.Disk.Plus3
{
    /// <summary>
    /// CPC/+3 DSK image model with sector metadata and writable extended-track support.
    /// </summary>
    public sealed class Plus3DiskImage
    {
        private const int DiskHeaderSize = 256;
        private const int TrackHeaderSize = 256;
        private const int Plus3DataDiskTracks = 40;
        private const int Plus3DataDiskSides = 1;
        private const int Plus3DataDiskSectorsPerTrack = 9;
        private const int Plus3DataDiskSectorSizeCode = 2;
        private const int Plus3DataDiskFirstSectorId = 0xC1;
        private const int Plus3DataDiskSectorLength = 512;
        private const int Plus3DataDiskTrackSize = TrackHeaderSize + (Plus3DataDiskSectorsPerTrack * Plus3DataDiskSectorLength);

        private byte[] _imageData;
        private readonly List<Plus3DiskTrack> _tracks;
        private bool _dirty;

        private Plus3DiskImage(string path, byte[] imageData, bool extended, int trackCount, int sideCount, List<Plus3DiskTrack> tracks)
        {
            Path = path;
            _imageData = imageData;
            IsExtended = extended;
            TrackCount = trackCount;
            SideCount = sideCount;
            _tracks = tracks;
            IsWriteProtected = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }

        public string Path { get; private set; }
        public bool IsExtended { get; }
        public int TrackCount { get; }
        public int SideCount { get; }
        public bool IsDirty => _dirty;
        public bool IsWriteProtected { get; set; }
        public IReadOnlyList<Plus3DiskTrack> Tracks => _tracks;
        public static Plus3DiskImage Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Disk image path is empty.", nameof(path));
            }

            byte[] data = File.ReadAllBytes(path);
            if (data.Length < DiskHeaderSize)
            {
                throw new InvalidDataException("DSK image is too short to contain a disk header.");
            }

            string signature = Encoding.ASCII.GetString(data, 0, Math.Min(34, data.Length));
            bool extended = signature.StartsWith("EXTENDED CPC DSK File\r\nDisk-Info\r\n", StringComparison.Ordinal);
            bool standard = signature.StartsWith("MV - CPCEMU Disk-File\r\nDisk-Info\r\n", StringComparison.Ordinal);
            if (!extended && !standard)
            {
                throw new InvalidDataException("Unsupported disk image. Expected standard or extended CPC DSK format.");
            }

            int trackCount = data[0x30];
            int sideCount = data[0x31];
            if (trackCount <= 0 || sideCount <= 0)
            {
                throw new InvalidDataException("DSK image has no tracks or sides.");
            }

            var tracks = new List<Plus3DiskTrack>(trackCount * sideCount);
            int offset = DiskHeaderSize;
            int standardTrackSize = standard ? ReadUInt16(data, 0x32) : 0;

            for (int track = 0; track < trackCount; track++)
            {
                for (int side = 0; side < sideCount; side++)
                {
                    int trackIndex = (track * sideCount) + side;
                    int trackSize = extended ? data[0x34 + trackIndex] * 256 : standardTrackSize;
                    if (trackSize == 0)
                    {
                        continue;
                    }

                    if (offset + trackSize > data.Length)
                    {
                        throw new InvalidDataException($"DSK track {track}, side {side} overruns the image.");
                    }

                    tracks.Add(ParseTrack(data, offset, trackSize, track, side, extended));
                    offset += trackSize;
                }
            }

            return new Plus3DiskImage(path, data, extended, trackCount, sideCount, tracks);
        }
        public static Plus3DiskImage CreateBlankPlus3DataDisk(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Disk image path is empty.", nameof(path));
            }

            byte[] data = new byte[DiskHeaderSize + (Plus3DataDiskTracks * Plus3DataDiskSides * Plus3DataDiskTrackSize)];
            WriteAscii(data, 0x00, "EXTENDED CPC DSK File\r\nDisk-Info\r\n");
            WriteAscii(data, 0x22, "ZedExEss");
            data[0x30] = Plus3DataDiskTracks;
            data[0x31] = Plus3DataDiskSides;

            for (int track = 0; track < Plus3DataDiskTracks; track++)
            {
                data[0x34 + track] = Plus3DataDiskTrackSize / 256;
            }

            int offset = DiskHeaderSize;
            for (int track = 0; track < Plus3DataDiskTracks; track++)
            {
                WriteAscii(data, offset, "Track-Info\r\n");
                data[offset + 0x10] = (byte)track;
                data[offset + 0x11] = 0;
                data[offset + 0x14] = Plus3DataDiskSectorSizeCode;
                data[offset + 0x15] = Plus3DataDiskSectorsPerTrack;
                data[offset + 0x16] = 0x4E;
                data[offset + 0x17] = 0xE5;

                int sectorDataOffset = offset + TrackHeaderSize;
                for (int sector = 0; sector < Plus3DataDiskSectorsPerTrack; sector++)
                {
                    int infoOffset = offset + 0x18 + (sector * 8);
                    data[infoOffset] = (byte)track;
                    data[infoOffset + 1] = 0;
                    data[infoOffset + 2] = (byte)(Plus3DataDiskFirstSectorId + sector);
                    data[infoOffset + 3] = Plus3DataDiskSectorSizeCode;
                    data[infoOffset + 4] = 0;
                    data[infoOffset + 5] = 0;
                    data[infoOffset + 6] = Plus3DataDiskSectorLength & 0xFF;
                    data[infoOffset + 7] = Plus3DataDiskSectorLength >> 8;

                    Array.Fill(data, (byte)0xE5, sectorDataOffset + (sector * Plus3DataDiskSectorLength), Plus3DataDiskSectorLength);
                }

                offset += Plus3DataDiskTrackSize;
            }

            File.WriteAllBytes(path, data);
            return Load(path);
        }
        public Plus3DiskTrack? FindTrack(int track, int side)
        {
            for (int i = 0; i < _tracks.Count; i++)
            {
                Plus3DiskTrack candidate = _tracks[i];
                if (candidate.Track == track && candidate.Side == side)
                {
                    return candidate;
                }
            }

            if (SideCount == 1 && side != 0)
            {
                return FindTrack(track, 0);
            }

            return null;
        }
        public Plus3DiskSector? FindSector(int track, int side, int sectorId)
        {
            return FindTrack(track, side)?.FindSector(sectorId);
        }
        public Plus3DiskSector? FindSector(int physicalTrack, int physicalSide, int sectorTrackId, int sectorSideId, int sectorId, int sizeCode)
        {
            Plus3DiskTrack? diskTrack = FindTrack(physicalTrack, physicalSide);
            if (diskTrack == null)
            {
                return null;
            }

            return diskTrack.FindSector(sectorTrackId, sectorSideId, sectorId, sizeCode)
                ?? diskTrack.FindSector(sectorId);
        }
        public bool TryGetSectorRun(int track, int side, int firstSectorId, int lastSectorId, List<Plus3DiskSector> sectors)
        {
            ArgumentNullException.ThrowIfNull(sectors);
            sectors.Clear();

            Plus3DiskTrack? diskTrack = FindTrack(track, side);
            if (diskTrack == null)
            {
                return false;
            }

            if (firstSectorId <= lastSectorId)
            {
                bool foundLogicalRun = true;
                for (int sectorId = firstSectorId; sectorId <= lastSectorId; sectorId++)
                {
                    Plus3DiskSector? sector = diskTrack.FindSector(sectorId);
                    if (sector == null)
                    {
                        foundLogicalRun = false;
                        break;
                    }

                    sectors.Add(sector);
                }

                if (foundLogicalRun && sectors.Count > 0)
                {
                    return true;
                }

                sectors.Clear();
            }

            int firstIndex = diskTrack.FindSectorIndex(firstSectorId);
            if (firstIndex < 0)
            {
                return false;
            }

            for (int i = firstIndex; i < diskTrack.Sectors.Count; i++)
            {
                Plus3DiskSector sector = diskTrack.Sectors[i];
                sectors.Add(sector);
                if (sector.SectorId == (byte)lastSectorId)
                {
                    break;
                }
            }

            return sectors.Count > 0;
        }
        public bool TryGetSectorRun(int physicalTrack, int physicalSide, int sectorTrackId, int sectorSideId, int firstSectorId, int lastSectorId, int sizeCode, List<Plus3DiskSector> sectors)
        {
            ArgumentNullException.ThrowIfNull(sectors);
            sectors.Clear();

            Plus3DiskTrack? diskTrack = FindTrack(physicalTrack, physicalSide);
            if (diskTrack == null)
            {
                return false;
            }

            if (firstSectorId <= lastSectorId)
            {
                bool foundLogicalRun = true;
                for (int sectorId = firstSectorId; sectorId <= lastSectorId; sectorId++)
                {
                    Plus3DiskSector? sector = diskTrack.FindSector(sectorTrackId, sectorSideId, sectorId, sizeCode)
                        ?? diskTrack.FindSector(sectorId);
                    if (sector == null)
                    {
                        foundLogicalRun = false;
                        break;
                    }

                    sectors.Add(sector);
                }

                if (foundLogicalRun && sectors.Count > 0)
                {
                    return true;
                }

                sectors.Clear();
            }

            int firstIndex = diskTrack.FindSectorIndex(sectorTrackId, sectorSideId, firstSectorId, sizeCode);
            if (firstIndex < 0)
            {
                firstIndex = diskTrack.FindSectorIndex(firstSectorId);
            }

            if (firstIndex < 0)
            {
                return false;
            }

            for (int i = firstIndex; i < diskTrack.Sectors.Count; i++)
            {
                Plus3DiskSector sector = diskTrack.Sectors[i];
                sectors.Add(sector);
                if (sector.SectorId == (byte)lastSectorId)
                {
                    break;
                }
            }

            return sectors.Count > 0;
        }
        public bool TryWriteSector(Plus3DiskSector sector, IReadOnlyList<byte> source, bool deletedData = false)
        {
            ArgumentNullException.ThrowIfNull(sector);

            ArgumentNullException.ThrowIfNull(source);

            if (IsWriteProtected)
            {
                return false;
            }

            int count = Math.Min(sector.Data.Length, source.Count);
            for (int i = 0; i < count; i++)
            {
                byte value = source[i];
                sector.Data[i] = value;
                _imageData[sector.ImageOffset + i] = value;
            }

            if (count < sector.Data.Length)
            {
                Array.Clear(sector.Data, count, sector.Data.Length - count);
                Array.Clear(_imageData, sector.ImageOffset + count, sector.Data.Length - count);
            }

            sector.Status1 = 0;
            sector.Status2 = deletedData ? (byte)0x40 : (byte)0x00;
            _imageData[sector.InfoOffset + 4] = sector.Status1;
            _imageData[sector.InfoOffset + 5] = sector.Status2;

            _dirty = true;
            return true;
        }
        public bool TryFormatTrack(int track, int side, IReadOnlyList<byte> sectorIds, byte gapLength, byte filler)
        {
            ArgumentNullException.ThrowIfNull(sectorIds);

            if (IsWriteProtected)
            {
                return false;
            }

            int sectorCount = sectorIds.Count / 4;
            if (sectorCount <= 0 || sectorCount > 29)
            {
                return false;
            }

            int totalDataLength = 0;
            for (int i = 0; i < sectorCount; i++)
            {
                int length = SectorLengthFromSizeCode(sectorIds[(i * 4) + 3]);
                if (length <= 0)
                {
                    return false;
                }

                totalDataLength += length;
            }

            int requiredTrackSize = AlignTrackSize(TrackHeaderSize + totalDataLength);
            if (!TryPrepareFormatTrack(track, side, requiredTrackSize, out int offset, out int trackSize))
            {
                return false;
            }

            if (totalDataLength > trackSize - TrackHeaderSize)
            {
                return false;
            }

            WriteAscii(_imageData, offset, "Track-Info\r\n");
            _imageData[offset + 0x10] = (byte)track;
            _imageData[offset + 0x11] = (byte)NormalizePhysicalSide(side);
            _imageData[offset + 0x14] = sectorIds[3];
            _imageData[offset + 0x15] = (byte)sectorCount;
            _imageData[offset + 0x16] = gapLength;
            _imageData[offset + 0x17] = filler;

            int tableOffset = offset + 0x18;
            Array.Clear(_imageData, tableOffset, TrackHeaderSize - 0x18);

            int dataOffset = offset + TrackHeaderSize;
            for (int i = 0; i < sectorCount; i++)
            {
                int idOffset = i * 4;
                byte c = sectorIds[idOffset];
                byte h = sectorIds[idOffset + 1];
                byte r = sectorIds[idOffset + 2];
                byte n = sectorIds[idOffset + 3];
                int length = SectorLengthFromSizeCode(n);

                int infoOffset = tableOffset + (i * 8);
                _imageData[infoOffset] = c;
                _imageData[infoOffset + 1] = h;
                _imageData[infoOffset + 2] = r;
                _imageData[infoOffset + 3] = n;
                _imageData[infoOffset + 4] = 0;
                _imageData[infoOffset + 5] = 0;
                _imageData[infoOffset + 6] = (byte)(length & 0xFF);
                _imageData[infoOffset + 7] = (byte)(length >> 8);

                Array.Fill(_imageData, filler, dataOffset, length);
                dataOffset += length;
            }

            int remaining = (offset + trackSize) - dataOffset;
            if (remaining > 0)
            {
                Array.Fill(_imageData, filler, dataOffset, remaining);
            }

            RebuildTrackList();
            _dirty = true;
            return true;
        }
        private bool TryPrepareFormatTrack(int track, int side, int requiredTrackSize, out int offset, out int trackSize)
        {
            offset = 0;
            trackSize = 0;

            if (track < 0 || track >= TrackCount)
            {
                return false;
            }

            int physicalSide = NormalizePhysicalSide(side);
            if (physicalSide < 0 || physicalSide >= SideCount)
            {
                return false;
            }

            int trackIndex = (track * SideCount) + physicalSide;
            if (IsExtended)
            {
                offset = DiskHeaderSize;
                for (int i = 0; i < trackIndex; i++)
                {
                    offset += _imageData[0x34 + i] * 256;
                }

                int existingTrackSize = _imageData[0x34 + trackIndex] * 256;
                int newTrackSize = Math.Max(existingTrackSize, requiredTrackSize);
                if (newTrackSize > 0xFF00)
                {
                    return false;
                }

                if (newTrackSize != existingTrackSize)
                {
                    byte[] resized = new byte[_imageData.Length - existingTrackSize + newTrackSize];
                    Array.Copy(_imageData, 0, resized, 0, offset);
                    Array.Copy(
                        _imageData,
                        offset + existingTrackSize,
                        resized,
                        offset + newTrackSize,
                        _imageData.Length - offset - existingTrackSize);
                    _imageData = resized;
                    _imageData[0x34 + trackIndex] = (byte)(newTrackSize / 256);
                    Array.Clear(_imageData, offset, newTrackSize);
                }

                trackSize = newTrackSize;
                return trackSize >= TrackHeaderSize;
            }

            int standardTrackSize = ReadUInt16(_imageData, 0x32);
            offset = DiskHeaderSize + (trackIndex * standardTrackSize);
            trackSize = standardTrackSize;
            return offset >= DiskHeaderSize
                && trackSize >= TrackHeaderSize
                && offset + trackSize <= _imageData.Length
                && requiredTrackSize <= trackSize;
        }
        private void RebuildTrackList()
        {
            _tracks.Clear();
            int offset = DiskHeaderSize;
            int standardTrackSize = IsExtended ? 0 : ReadUInt16(_imageData, 0x32);

            for (int track = 0; track < TrackCount; track++)
            {
                for (int side = 0; side < SideCount; side++)
                {
                    int trackIndex = (track * SideCount) + side;
                    int trackSize = IsExtended ? _imageData[0x34 + trackIndex] * 256 : standardTrackSize;
                    if (trackSize == 0)
                    {
                        continue;
                    }

                    _tracks.Add(ParseTrack(_imageData, offset, trackSize, track, side, IsExtended));
                    offset += trackSize;
                }
            }
        }
        private int NormalizePhysicalSide(int side)
        {
            return SideCount == 1 ? 0 : side;
        }
        private static int AlignTrackSize(int size)
        {
            return (size + 0xFF) & ~0xFF;
        }
        public void Save()
        {
            if (!_dirty)
            {
                return;
            }

            if (IsWriteProtected)
            {
                throw new IOException("Disk image is write protected.");
            }

            File.WriteAllBytes(Path, _imageData);
            _dirty = false;
        }
        public void SaveAs(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Disk image path is empty.", nameof(path));
            }

            File.WriteAllBytes(path, _imageData);
            Path = path;
            _dirty = false;
            IsWriteProtected = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        private static Plus3DiskTrack ParseTrack(byte[] data, int offset, int trackSize, int fallbackTrack, int fallbackSide, bool extended)
        {
            string signature = Encoding.ASCII.GetString(data, offset, Math.Min(12, trackSize));
            if (!signature.StartsWith("Track-Info", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"DSK track at offset {offset} does not contain a valid track header.");
            }

            int track = data[offset + 0x10];
            int side = data[offset + 0x11];
            if (track == 0xFF)
            {
                track = fallbackTrack;
            }

            if (side == 0xFF)
            {
                side = fallbackSide;
            }

            int sectorCount = data[offset + 0x15];
            var sectors = new List<Plus3DiskSector>(sectorCount);
            int dataOffset = offset + TrackHeaderSize;
            int trackEnd = offset + trackSize;

            for (int sectorIndex = 0; sectorIndex < sectorCount; sectorIndex++)
            {
                int infoOffset = offset + 0x18 + (sectorIndex * 8);
                if (infoOffset + 8 > offset + TrackHeaderSize)
                {
                    throw new InvalidDataException($"DSK track {track}, side {side} has an invalid sector table.");
                }

                byte c = data[infoOffset];
                byte h = data[infoOffset + 1];
                byte r = data[infoOffset + 2];
                byte n = data[infoOffset + 3];
                byte st1 = data[infoOffset + 4];
                byte st2 = data[infoOffset + 5];
                int length = extended ? ReadUInt16(data, infoOffset + 6) : SectorLengthFromSizeCode(n);
                if (length <= 0)
                {
                    length = SectorLengthFromSizeCode(n);
                }

                if (dataOffset + length > trackEnd)
                {
                    throw new InvalidDataException($"DSK sector {r:X2} on track {track}, side {side} overruns its track.");
                }

                int sectorImageOffset = dataOffset;
                byte[] sectorData = new byte[length];
                Array.Copy(data, dataOffset, sectorData, 0, length);
                dataOffset += length;

                sectors.Add(new Plus3DiskSector(c, h, r, n, st1, st2, infoOffset, sectorImageOffset, sectorData));
            }

            return new Plus3DiskTrack(track, side, offset, trackSize, sectors);
        }
        private static int SectorLengthFromSizeCode(byte sizeCode)
        {
            if (sizeCode > 7)
            {
                return 0;
            }

            return 128 << sizeCode;
        }
        private static int ReadUInt16(byte[] data, int offset)
        {
            if (offset + 1 >= data.Length)
            {
                return 0;
            }

            return data[offset] | (data[offset + 1] << 8);
        }
        private static void WriteAscii(byte[] data, int offset, string value)
        {
            Encoding.ASCII.GetBytes(value, 0, value.Length, data, offset);
        }
    }
    /// <summary>Parsed DSK track header and its physical sector descriptors.</summary>
    public sealed class Plus3DiskTrack(int track, int side, int imageOffset, int trackSize, List<Plus3DiskSector> sectors)
    {
        private readonly List<Plus3DiskSector> _sectors = sectors;

        public int Track { get; } = track;
        public int Side { get; } = side;
        public int ImageOffset { get; } = imageOffset;
        public int TrackSize { get; } = trackSize;
        public IReadOnlyList<Plus3DiskSector> Sectors => _sectors;
        public Plus3DiskSector? FindSector(int sectorId)
        {
            byte id = (byte)sectorId;
            for (int i = 0; i < _sectors.Count; i++)
            {
                if (_sectors[i].SectorId == id)
                {
                    return _sectors[i];
                }
            }

            return null;
        }
        public Plus3DiskSector? FindSector(int trackId, int sideId, int sectorId, int sizeCode)
        {
            byte c = (byte)trackId;
            byte h = (byte)sideId;
            byte r = (byte)sectorId;
            byte n = (byte)sizeCode;
            for (int i = 0; i < _sectors.Count; i++)
            {
                Plus3DiskSector sector = _sectors[i];
                if (sector.Track == c
                    && sector.Side == h
                    && sector.SectorId == r
                    && sector.SizeCode == n)
                {
                    return sector;
                }
            }

            return null;
        }
        public int FindSectorIndex(int sectorId)
        {
            byte id = (byte)sectorId;
            for (int i = 0; i < _sectors.Count; i++)
            {
                if (_sectors[i].SectorId == id)
                {
                    return i;
                }
            }

            return -1;
        }
        public int FindSectorIndex(int trackId, int sideId, int sectorId, int sizeCode)
        {
            byte c = (byte)trackId;
            byte h = (byte)sideId;
            byte r = (byte)sectorId;
            byte n = (byte)sizeCode;
            for (int i = 0; i < _sectors.Count; i++)
            {
                Plus3DiskSector sector = _sectors[i];
                if (sector.Track == c
                    && sector.Side == h
                    && sector.SectorId == r
                    && sector.SizeCode == n)
                {
                    return i;
                }
            }

            return -1;
        }
        internal void ReplaceSectors(List<Plus3DiskSector> sectors)
        {
            _sectors.Clear();
            _sectors.AddRange(sectors);
        }
    }
    /// <summary>
    /// One DSK sector, including the CHRN identity and uPD765 status bytes stored in its track header.
    /// </summary>
    public sealed class Plus3DiskSector(byte track, byte side, byte sectorId, byte sizeCode, byte status1, byte status2, int infoOffset, int imageOffset, byte[] data)
    {
        public byte Track { get; } = track;
        public byte Side { get; } = side;
        public byte SectorId { get; } = sectorId;
        public byte SizeCode { get; } = sizeCode;
        public byte Status1 { get; internal set; } = status1;
        public byte Status2 { get; internal set; } = status2;
        public int InfoOffset { get; } = infoOffset;
        public int ImageOffset { get; } = imageOffset;
        public byte[] Data { get; } = data ?? throw new ArgumentNullException(nameof(data));
    }
}
