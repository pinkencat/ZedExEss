using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ZedExEss.Spectrum.DivMmc
{
    /// <summary>
    /// Builds and updates simple FAT16 SD-card images used by DivMMC image and folder-backed storage.
    /// </summary>
    public static class SpectrumFatImageBuilder
    {
        private const int SectorSize = 512;
        private const uint PartitionStartLba = 2048;
        private const ushort ReservedSectors = 1;
        private const byte FatCount = 2;
        private const ushort RootEntryCount = 512;
        private const byte MediaDescriptor = 0xF8;
        private const ushort SectorsPerTrack = 63;
        private const ushort HeadCount = 255;
        private const int DefaultMegabytes = 64;
        public static void CreateBlankFat16Image(string path, int megabytes = DefaultMegabytes)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Image path is empty.", nameof(path));
            }

            if (megabytes < 16 || megabytes > 512)
            {
                throw new ArgumentOutOfRangeException(nameof(megabytes), megabytes, "FAT16 image size must be between 16 MB and 512 MB.");
            }

            uint totalSectors = checked((uint)megabytes * 1024u * 1024u / SectorSize);
            if (totalSectors <= PartitionStartLba + 4096)
            {
                throw new ArgumentOutOfRangeException(nameof(megabytes), megabytes, "Image is too small for the reserved partition layout.");
            }

            uint partitionSectors = totalSectors - PartitionStartLba;
            byte sectorsPerCluster = ChooseSectorsPerCluster(partitionSectors);
            ushort rootDirSectors = (ushort)(((RootEntryCount * 32) + (SectorSize - 1)) / SectorSize);
            ushort sectorsPerFat = CalculateFat16Sectors(partitionSectors, sectorsPerCluster, rootDirSectors);
            uint dataSectors = partitionSectors - ReservedSectors - rootDirSectors - ((uint)FatCount * sectorsPerFat);
            uint clusterCount = dataSectors / sectorsPerCluster;

            if (clusterCount < 4085 || clusterCount >= 65525)
            {
                throw new InvalidOperationException($"Unable to create a valid FAT16 layout for {megabytes} MB.");
            }

            using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength((long)totalSectors * SectorSize);

            byte[] sector = new byte[SectorSize];
            BuildMbr(sector, PartitionStartLba, partitionSectors);
            WriteSector(stream, 0, sector);

            Array.Clear(sector);
            BuildFat16BootSector(sector, partitionSectors, sectorsPerCluster, sectorsPerFat);
            WriteSector(stream, PartitionStartLba, sector);

            Array.Clear(sector);
            sector[0] = MediaDescriptor;
            sector[1] = 0xFF;
            sector[2] = 0xFF;
            sector[3] = 0xFF;

            uint firstFat = PartitionStartLba + ReservedSectors;
            WriteSector(stream, firstFat, sector);
            WriteSector(stream, firstFat + sectorsPerFat, sector);

            Array.Clear(sector);
            WriteVolumeLabelRootEntry(sector);
            uint rootDirStart = firstFat + ((uint)FatCount * sectorsPerFat);
            WriteSector(stream, rootDirStart, sector);
            stream.Flush();
        }
        public static void ImportDirectoryIntoFat16Image(string imagePath, string sourceDirectory)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                throw new ArgumentException("Image path is empty.", nameof(imagePath));
            }

            if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("Source directory does not exist.");
            }

            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Fat16Layout layout = Fat16Layout.Read(stream);
            var writer = new Fat16Writer(stream, layout);
            writer.ImportDirectory(sourceDirectory);
            writer.Flush();
        }
        public static void ExportFat16ImageToDirectory(string imagePath, string targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                throw new ArgumentException("Image path is empty.", nameof(imagePath));
            }

            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                throw new ArgumentException("Target directory path is empty.", nameof(targetDirectory));
            }

            string targetRoot = Path.GetFullPath(targetDirectory);
            DirectoryInfo targetInfo = new(targetRoot);
            if (targetInfo.Parent == null)
            {
                throw new IOException("Refusing to export folder-backed storage to a filesystem root.");
            }

            string exportRoot = Path.Combine(Path.GetTempPath(), "ZedExEss-DivMMC-Export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(exportRoot);

            try
            {
                using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    Fat16Layout layout = Fat16Layout.Read(stream);
                    var reader = new Fat16Reader(stream, layout);
                    reader.ExportToDirectory(exportRoot);
                }

                ReplaceDirectoryContents(exportRoot, targetRoot);
            }
            finally
            {
                if (Directory.Exists(exportRoot))
                {
                    Directory.Delete(exportRoot, recursive: true);
                }
            }
        }
        private static byte ChooseSectorsPerCluster(uint partitionSectors)
        {
            return partitionSectors switch
            {
                <= 64 * 2048 => 4,
                <= 128 * 2048 => 8,
                <= 256 * 2048 => 16,
                _ => 32
            };
        }
        private static ushort CalculateFat16Sectors(uint partitionSectors, byte sectorsPerCluster, ushort rootDirSectors)
        {
            uint sectorsPerFat = 1;
            while (true)
            {
                uint dataSectors = partitionSectors - ReservedSectors - rootDirSectors - ((uint)FatCount * sectorsPerFat);
                uint clusters = dataSectors / sectorsPerCluster;
                uint requiredFatSectors = (((clusters + 2) * 2) + (SectorSize - 1)) / SectorSize;
                if (requiredFatSectors == sectorsPerFat)
                {
                    if (requiredFatSectors > ushort.MaxValue)
                    {
                        throw new InvalidOperationException("FAT is too large for FAT16.");
                    }

                    return (ushort)requiredFatSectors;
                }

                sectorsPerFat = requiredFatSectors;
            }
        }
        private static void BuildMbr(byte[] sector, uint partitionStartLba, uint partitionSectors)
        {
            int entry = 0x1BE;
            sector[entry + 0] = 0x00;
            sector[entry + 1] = 0xFE;
            sector[entry + 2] = 0xFF;
            sector[entry + 3] = 0xFF;
            sector[entry + 4] = 0x0E;
            sector[entry + 5] = 0xFE;
            sector[entry + 6] = 0xFF;
            sector[entry + 7] = 0xFF;
            WriteUInt32(sector, entry + 8, partitionStartLba);
            WriteUInt32(sector, entry + 12, partitionSectors);
            sector[0x1FE] = 0x55;
            sector[0x1FF] = 0xAA;
        }
        private static void BuildFat16BootSector(byte[] sector, uint partitionSectors, byte sectorsPerCluster, ushort sectorsPerFat)
        {
            sector[0] = 0xEB;
            sector[1] = 0x3C;
            sector[2] = 0x90;
            WriteAscii(sector, 0x03, "MSDOS5.0", 8);
            WriteUInt16(sector, 0x0B, SectorSize);
            sector[0x0D] = sectorsPerCluster;
            WriteUInt16(sector, 0x0E, ReservedSectors);
            sector[0x10] = FatCount;
            WriteUInt16(sector, 0x11, RootEntryCount);
            WriteUInt16(sector, 0x13, partitionSectors < 65536 ? (ushort)partitionSectors : (ushort)0);
            sector[0x15] = MediaDescriptor;
            WriteUInt16(sector, 0x16, sectorsPerFat);
            WriteUInt16(sector, 0x18, SectorsPerTrack);
            WriteUInt16(sector, 0x1A, HeadCount);
            WriteUInt32(sector, 0x1C, PartitionStartLba);
            WriteUInt32(sector, 0x20, partitionSectors);
            sector[0x24] = 0x80;
            sector[0x26] = 0x29;
            WriteUInt32(sector, 0x27, 0x5A584553);
            WriteAscii(sector, 0x2B, "ZEDEXESS   ", 11);
            WriteAscii(sector, 0x36, "FAT16   ", 8);
            sector[0x1FE] = 0x55;
            sector[0x1FF] = 0xAA;
        }
        private static void WriteVolumeLabelRootEntry(byte[] sector)
        {
            WriteAscii(sector, 0, "ZEDEXESS   ", 11);
            sector[11] = 0x08;
        }
        private static void WriteSector(FileStream stream, uint lba, byte[] sector)
        {
            stream.Position = (long)lba * SectorSize;
            stream.Write(sector, 0, SectorSize);
        }
        private static void ReplaceDirectoryContents(string sourceDirectory, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);

            foreach (string file in Directory.EnumerateFiles(targetDirectory))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            foreach (string directory in Directory.EnumerateDirectories(targetDirectory))
            {
                Directory.Delete(directory, recursive: true);
            }

            CopyDirectoryContents(sourceDirectory, targetDirectory);
        }
        private static void CopyDirectoryContents(string sourceDirectory, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);
            foreach (string directory in Directory.EnumerateDirectories(sourceDirectory))
            {
                string destination = Path.Combine(targetDirectory, Path.GetFileName(directory));
                CopyDirectoryContents(directory, destination);
            }

            foreach (string file in Directory.EnumerateFiles(sourceDirectory))
            {
                string destination = Path.Combine(targetDirectory, Path.GetFileName(file));
                File.Copy(file, destination, overwrite: true);
            }
        }
        private static void WriteUInt16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }
        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }
        private static void WriteAscii(byte[] buffer, int offset, string text, int length)
        {
            Span<byte> destination = buffer.AsSpan(offset, length);
            destination.Fill((byte)' ');
            Encoding.ASCII.GetBytes(text.AsSpan(0, Math.Min(text.Length, length)), destination);
        }
        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }
        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }
        /// <summary>Allocates FAT chains and writes host files/directories into a blank image.</summary>
        private sealed class Fat16Writer
        {
            private const ushort FreeCluster = 0x0000;
            private const ushort EndOfChain = 0xFFFF;

            private readonly FileStream _stream;
            private readonly Fat16Layout _layout;
            private readonly ushort[] _fat;
            private readonly byte[] _rootDirectory;
            private ushort _nextFreeCluster = 2;

            public Fat16Writer(FileStream stream, Fat16Layout layout)
            {
                _stream = stream;
                _layout = layout;
                _fat = new ushort[layout.ClusterCount + 2];
                _fat[0] = (ushort)(0xFF00 | layout.MediaDescriptor);
                _fat[1] = EndOfChain;
                _rootDirectory = new byte[layout.RootDirectoryBytes];
            }
            public void ImportDirectory(string sourceDirectory)
            {
                var writer = DirectoryWriter.CreateRoot(this, _rootDirectory);
                ImportDirectoryContents(sourceDirectory, writer, currentCluster: 0, parentCluster: 0);
                writer.Finish();
            }
            public void Flush()
            {
                WriteFatCopies();
                WriteBytesAtSector(_layout.RootDirectoryStart, _rootDirectory);
                _stream.Flush();
            }
            private void ImportDirectoryContents(string sourceDirectory, DirectoryWriter directory, ushort currentCluster, ushort parentCluster)
            {
                if (currentCluster != 0)
                {
                    directory.WriteEntry(BuildDirectoryEntry(".", currentCluster, 0, Directory.GetLastWriteTime(sourceDirectory)));
                    directory.WriteEntry(BuildDirectoryEntry("..", parentCluster, 0, Directory.GetLastWriteTime(sourceDirectory)));
                }

                foreach (string childDirectory in Directory.EnumerateDirectories(sourceDirectory).Order(StringComparer.OrdinalIgnoreCase))
                {
                    DirectoryInfo info = new(childDirectory);
                    ushort firstCluster = AllocateCluster();
                    string shortName = directory.AllocateShortName(info.Name, isDirectory: true);
                    directory.WriteEntry(BuildDirectoryEntry(shortName, firstCluster, 0, info.LastWriteTime));

                    var childWriter = DirectoryWriter.CreateClusterChain(this, firstCluster);
                    ImportDirectoryContents(childDirectory, childWriter, firstCluster, currentCluster);
                    childWriter.Finish();
                }

                foreach (string childFile in Directory.EnumerateFiles(sourceDirectory).Order(StringComparer.OrdinalIgnoreCase))
                {
                    FileInfo info = new(childFile);
                    ushort firstCluster = WriteFile(childFile, info.Length);
                    string shortName = directory.AllocateShortName(info.Name, isDirectory: false);
                    directory.WriteEntry(BuildDirectoryEntry(shortName, firstCluster, checked((uint)info.Length), info.LastWriteTime));
                }
            }
            private ushort WriteFile(string path, long length)
            {
                if (length == 0)
                {
                    return 0;
                }

                ushort firstCluster = 0;
                ushort previousCluster = 0;
                byte[] buffer = new byte[_layout.ClusterBytes];

                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                while (file.Position < file.Length)
                {
                    ushort cluster = AllocateCluster();
                    if (firstCluster == 0)
                    {
                        firstCluster = cluster;
                    }

                    if (previousCluster != 0)
                    {
                        _fat[previousCluster] = cluster;
                    }

                    Array.Clear(buffer);
                    int offset = 0;
                    while (offset < buffer.Length)
                    {
                        int read = file.Read(buffer, offset, buffer.Length - offset);
                        if (read == 0)
                        {
                            break;
                        }

                        offset += read;
                    }

                    WriteCluster(cluster, buffer);
                    previousCluster = cluster;
                }

                return firstCluster;
            }
            private ushort AllocateCluster()
            {
                for (ushort cluster = _nextFreeCluster; cluster < _fat.Length; cluster++)
                {
                    if (_fat[cluster] == FreeCluster)
                    {
                        _fat[cluster] = EndOfChain;
                        _nextFreeCluster = (ushort)(cluster + 1);
                        return cluster;
                    }
                }

                throw new IOException("FAT image is full.");
            }
            private void LinkCluster(ushort previousCluster, ushort nextCluster)
            {
                _fat[previousCluster] = nextCluster;
            }
            private void WriteCluster(ushort cluster, byte[] data)
            {
                WriteBytesAtSector(_layout.FirstSectorOfCluster(cluster), data);
            }
            private void WriteFatCopies()
            {
                byte[] fatBytes = new byte[_layout.SectorsPerFat * SectorSize];
                for (int i = 0; i < _fat.Length && (i * 2 + 1) < fatBytes.Length; i++)
                {
                    WriteUInt16(fatBytes, i * 2, _fat[i]);
                }

                for (int i = 0; i < _layout.FatCount; i++)
                {
                    WriteBytesAtSector(_layout.FirstFatSector + ((uint)i * _layout.SectorsPerFat), fatBytes);
                }
            }
            private void WriteBytesAtSector(uint sector, byte[] data)
            {
                _stream.Position = (long)sector * SectorSize;
                _stream.Write(data, 0, data.Length);
            }
            private byte[] BuildDirectoryEntry(string shortName, ushort firstCluster, uint size, DateTime lastWriteTime)
            {
                byte[] entry = new byte[32];
                WriteShortName(entry, shortName);
                bool directory = shortName is "." or ".." || size == 0 && firstCluster != 0;
                entry[11] = directory ? (byte)0x10 : (byte)0x20;
                ushort time = EncodeFatTime(lastWriteTime);
                ushort date = EncodeFatDate(lastWriteTime);
                WriteUInt16(entry, 22, time);
                WriteUInt16(entry, 24, date);
                WriteUInt16(entry, 26, firstCluster);
                WriteUInt32(entry, 28, size);
                return entry;
            }
            private static void WriteShortName(byte[] entry, string shortName)
            {
                Span<byte> destination = entry.AsSpan(0, 11);
                destination.Fill((byte)' ');

                if (shortName == ".")
                {
                    destination[0] = (byte)'.';
                    return;
                }

                if (shortName == "..")
                {
                    destination[0] = (byte)'.';
                    destination[1] = (byte)'.';
                    return;
                }

                Encoding.ASCII.GetBytes(shortName, destination);
            }
            private static ushort EncodeFatTime(DateTime time)
            {
                return (ushort)((time.Hour << 11) | (time.Minute << 5) | (time.Second / 2));
            }
            private static ushort EncodeFatDate(DateTime time)
            {
                int year = Math.Clamp(time.Year, 1980, 2107) - 1980;
                return (ushort)((year << 9) | (time.Month << 5) | time.Day);
            }
            /// <summary>Writes fixed-size root entries or grows a subdirectory cluster chain as needed.</summary>
            private sealed class DirectoryWriter
            {
                private readonly Fat16Writer _owner;
                private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);
                private readonly byte[] _buffer;
                private readonly bool _root;
                private ushort _currentCluster;
                private int _offset;

                private DirectoryWriter(Fat16Writer owner, byte[] buffer, bool root, ushort currentCluster)
                {
                    _owner = owner;
                    _buffer = buffer;
                    _root = root;
                    _currentCluster = currentCluster;
                }
                public static DirectoryWriter CreateRoot(Fat16Writer owner, byte[] rootDirectory)
                {
                    return new DirectoryWriter(owner, rootDirectory, root: true, currentCluster: 0);
                }
                public static DirectoryWriter CreateClusterChain(Fat16Writer owner, ushort firstCluster)
                {
                    return new DirectoryWriter(owner, new byte[owner._layout.ClusterBytes], root: false, currentCluster: firstCluster);
                }
                public string AllocateShortName(string name, bool isDirectory)
                {
                    string shortName = CreateShortName(name, isDirectory, _usedNames);
                    _usedNames.Add(shortName);
                    return shortName;
                }
                public void WriteEntry(byte[] entry)
                {
                    if (_offset + 32 > _buffer.Length)
                    {
                        if (_root)
                        {
                            throw new IOException("Root directory is full.");
                        }

                        _owner.WriteCluster(_currentCluster, _buffer);
                        ushort nextCluster = _owner.AllocateCluster();
                        _owner.LinkCluster(_currentCluster, nextCluster);
                        _currentCluster = nextCluster;
                        Array.Clear(_buffer);
                        _offset = 0;
                    }

                    entry.CopyTo(_buffer.AsSpan(_offset, 32));
                    _offset += 32;
                }
                public void Finish()
                {
                    if (!_root)
                    {
                        _owner.WriteCluster(_currentCluster, _buffer);
                    }
                }
            }
        }
        /// <summary>Walks FAT chains and exports validated 8.3 directory entries to a staging folder.</summary>
        private sealed class Fat16Reader
        {
            private const ushort EndOfChain = 0xFFF8;

            private readonly FileStream _stream;
            private readonly Fat16Layout _layout;
            private readonly ushort[] _fat;

            public Fat16Reader(FileStream stream, Fat16Layout layout)
            {
                _stream = stream;
                _layout = layout;
                _fat = ReadFat();
            }
            public void ExportToDirectory(string targetDirectory)
            {
                Directory.CreateDirectory(targetDirectory);
                byte[] rootDirectory = new byte[_layout.RootDirectoryBytes];
                ReadBytesAtSector(_layout.RootDirectoryStart, rootDirectory);
                ExportDirectoryEntries(rootDirectory, targetDirectory);
            }
            private ushort[] ReadFat()
            {
                byte[] fatBytes = new byte[_layout.SectorsPerFat * SectorSize];
                ReadBytesAtSector(_layout.FirstFatSector, fatBytes);

                ushort[] fat = new ushort[_layout.ClusterCount + 2];
                for (int i = 0; i < fat.Length && (i * 2 + 1) < fatBytes.Length; i++)
                {
                    fat[i] = ReadUInt16(fatBytes, i * 2);
                }

                return fat;
            }
            private void ExportDirectory(ushort firstCluster, string targetDirectory)
            {
                if (!IsValidDataCluster(firstCluster))
                {
                    return;
                }

                byte[] directoryData = ReadClusterChain(firstCluster);
                ExportDirectoryEntries(directoryData, targetDirectory);
            }
            private void ExportDirectoryEntries(byte[] directoryData, string targetDirectory)
            {
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int offset = 0; offset + 32 <= directoryData.Length; offset += 32)
                {
                    byte first = directoryData[offset];
                    if (first == 0x00)
                    {
                        break;
                    }

                    if (first == 0xE5)
                    {
                        continue;
                    }

                    byte attributes = directoryData[offset + 11];
                    if ((attributes & 0x0F) == 0x0F || (attributes & 0x08) != 0)
                    {
                        continue;
                    }

                    string name = ReadShortName(directoryData, offset);
                    if (name == "." || name == ".." || name.Length == 0)
                    {
                        continue;
                    }

                    name = AllocateUniqueHostName(SanitizeHostName(name), usedNames);
                    string targetPath = Path.Combine(targetDirectory, name);
                    ushort firstCluster = ReadUInt16(directoryData, offset + 26);
                    uint size = ReadUInt32(directoryData, offset + 28);

                    if ((attributes & 0x10) != 0)
                    {
                        Directory.CreateDirectory(targetPath);
                        ExportDirectory(firstCluster, targetPath);
                    }
                    else
                    {
                        ExportFile(firstCluster, size, targetPath);
                    }
                }
            }
            private void ExportFile(ushort firstCluster, uint size, string targetPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? ".");
                using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                if (size == 0)
                {
                    return;
                }

                if (!IsValidDataCluster(firstCluster))
                {
                    throw new InvalidDataException($"File {Path.GetFileName(targetPath)} has an invalid first cluster.");
                }

                uint remaining = size;
                byte[] buffer = new byte[_layout.ClusterBytes];
                foreach (ushort cluster in EnumerateClusterChain(firstCluster))
                {
                    ReadCluster(cluster, buffer);
                    int count = checked((int)Math.Min(remaining, (uint)buffer.Length));
                    output.Write(buffer, 0, count);
                    remaining -= (uint)count;
                    if (remaining == 0)
                    {
                        break;
                    }
                }

                if (remaining != 0)
                {
                    throw new EndOfStreamException($"File {Path.GetFileName(targetPath)} ended before its FAT directory size was satisfied.");
                }
            }
            private byte[] ReadClusterChain(ushort firstCluster)
            {
                using var output = new MemoryStream();
                byte[] buffer = new byte[_layout.ClusterBytes];
                foreach (ushort cluster in EnumerateClusterChain(firstCluster))
                {
                    ReadCluster(cluster, buffer);
                    output.Write(buffer, 0, buffer.Length);
                }

                return output.ToArray();
            }
            private IEnumerable<ushort> EnumerateClusterChain(ushort firstCluster)
            {
                ushort cluster = firstCluster;
                var seen = new HashSet<ushort>();
                while (IsValidDataCluster(cluster) && seen.Add(cluster))
                {
                    yield return cluster;
                    ushort next = _fat[cluster];
                    if (next >= EndOfChain)
                    {
                        yield break;
                    }

                    cluster = next;
                }
            }
            private bool IsValidDataCluster(ushort cluster)
            {
                return cluster >= 2 && cluster < _fat.Length;
            }
            private void ReadCluster(ushort cluster, byte[] buffer)
            {
                ReadBytesAtSector(_layout.FirstSectorOfCluster(cluster), buffer);
            }
            private void ReadBytesAtSector(uint sector, byte[] buffer)
            {
                _stream.Position = (long)sector * SectorSize;
                int offset = 0;
                while (offset < buffer.Length)
                {
                    int read = _stream.Read(buffer, offset, buffer.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Unexpected end of image.");
                    }

                    offset += read;
                }
            }
            private static string ReadShortName(byte[] directoryData, int offset)
            {
                string name = Encoding.ASCII.GetString(directoryData, offset, 8).TrimEnd();
                string extension = Encoding.ASCII.GetString(directoryData, offset + 8, 3).TrimEnd();
                return extension.Length == 0 ? name : $"{name}.{extension}";
            }
            private static string SanitizeHostName(string name)
            {
                char[] invalid = Path.GetInvalidFileNameChars();
                var builder = new StringBuilder(name.Length);
                foreach (char c in name)
                {
                    builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
                }

                string sanitized = builder.ToString().Trim();
                return sanitized.Length == 0 ? "FILE" : sanitized;
            }
            private static string AllocateUniqueHostName(string name, HashSet<string> usedNames)
            {
                if (usedNames.Add(name))
                {
                    return name;
                }

                string baseName = Path.GetFileNameWithoutExtension(name);
                string extension = Path.GetExtension(name);
                for (int i = 1; i < 10000; i++)
                {
                    string candidate = $"{baseName}_{i}{extension}";
                    if (usedNames.Add(candidate))
                    {
                        return candidate;
                    }
                }

                throw new IOException($"Unable to allocate a unique host name for {name}.");
            }
        }
        private readonly struct Fat16Layout
        {
            private Fat16Layout(uint partitionStart, byte mediaDescriptor, byte sectorsPerCluster, byte fatCount, ushort sectorsPerFat, uint firstFatSector, uint rootDirectoryStart, uint dataStart, ushort rootDirectoryBytes, ushort clusterBytes, ushort clusterCount)
            {
                PartitionStart = partitionStart;
                MediaDescriptor = mediaDescriptor;
                SectorsPerCluster = sectorsPerCluster;
                FatCount = fatCount;
                SectorsPerFat = sectorsPerFat;
                FirstFatSector = firstFatSector;
                RootDirectoryStart = rootDirectoryStart;
                DataStart = dataStart;
                RootDirectoryBytes = rootDirectoryBytes;
                ClusterBytes = clusterBytes;
                ClusterCount = clusterCount;
            }

            public uint PartitionStart { get; }
            public byte MediaDescriptor { get; }
            public byte SectorsPerCluster { get; }
            public byte FatCount { get; }
            public ushort SectorsPerFat { get; }
            public uint FirstFatSector { get; }
            public uint RootDirectoryStart { get; }
            public uint DataStart { get; }
            public ushort RootDirectoryBytes { get; }
            public ushort ClusterBytes { get; }
            public ushort ClusterCount { get; }
            public static Fat16Layout Read(FileStream stream)
            {
                byte[] sector = new byte[SectorSize];
                ReadSector(stream, 0, sector);
                uint partitionStart = LooksLikeFatBootSector(sector)
                    ? 0
                    : ReadUInt32(sector, 0x1BE + 8);

                ReadSector(stream, partitionStart, sector);
                if (!LooksLikeFatBootSector(sector))
                {
                    throw new InvalidDataException("Image does not contain a recognised FAT16 boot sector.");
                }

                ushort bytesPerSector = ReadUInt16(sector, 0x0B);
                if (bytesPerSector != SectorSize)
                {
                    throw new InvalidDataException("Only 512-byte sector FAT images are supported.");
                }

                byte sectorsPerCluster = sector[0x0D];
                ushort reservedSectors = ReadUInt16(sector, 0x0E);
                byte fatCount = sector[0x10];
                ushort rootEntries = ReadUInt16(sector, 0x11);
                uint totalSectors = ReadUInt16(sector, 0x13);
                if (totalSectors == 0)
                {
                    totalSectors = ReadUInt32(sector, 0x20);
                }

                ushort sectorsPerFat = ReadUInt16(sector, 0x16);
                if (sectorsPerFat == 0)
                {
                    throw new InvalidDataException("FAT32 images are not supported by this importer.");
                }

                ushort rootDirSectors = (ushort)(((rootEntries * 32) + (SectorSize - 1)) / SectorSize);
                uint firstFatSector = partitionStart + reservedSectors;
                uint rootDirectoryStart = firstFatSector + ((uint)fatCount * sectorsPerFat);
                uint dataStart = rootDirectoryStart + rootDirSectors;
                uint dataSectors = totalSectors - reservedSectors - ((uint)fatCount * sectorsPerFat) - rootDirSectors;
                uint clusterCount = dataSectors / sectorsPerCluster;

                if (clusterCount < 4085 || clusterCount >= 65525)
                {
                    throw new InvalidDataException("Image is not a FAT16 volume.");
                }

                return new Fat16Layout(
                    partitionStart,
                    sector[0x15],
                    sectorsPerCluster,
                    fatCount,
                    sectorsPerFat,
                    firstFatSector,
                    rootDirectoryStart,
                    dataStart,
                    checked((ushort)(rootDirSectors * SectorSize)),
                    checked((ushort)(sectorsPerCluster * SectorSize)),
                    checked((ushort)clusterCount));
            }
            public uint FirstSectorOfCluster(ushort cluster)
            {
                if (cluster < 2 || cluster >= ClusterCount + 2)
                {
                    throw new ArgumentOutOfRangeException(nameof(cluster));
                }

                return DataStart + ((uint)(cluster - 2) * SectorsPerCluster);
            }
            private static bool LooksLikeFatBootSector(byte[] sector)
            {
                if (sector[0x1FE] != 0x55 || sector[0x1FF] != 0xAA)
                {
                    return false;
                }

                ushort bytesPerSector = ReadUInt16(sector, 0x0B);
                byte fatCount = sector[0x10];
                ushort rootEntries = ReadUInt16(sector, 0x11);
                ushort sectorsPerFat = ReadUInt16(sector, 0x16);
                return bytesPerSector == SectorSize && fatCount > 0 && rootEntries > 0 && sectorsPerFat > 0;
            }
            private static void ReadSector(FileStream stream, uint sector, byte[] buffer)
            {
                stream.Position = (long)sector * SectorSize;
                int offset = 0;
                while (offset < buffer.Length)
                {
                    int read = stream.Read(buffer, offset, buffer.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Unexpected end of image.");
                    }

                    offset += read;
                }
            }
        }
        private static string CreateShortName(string name, bool isDirectory, HashSet<string> usedNames)
        {
            string baseName = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
            string extension = isDirectory ? "" : Path.GetExtension(name).TrimStart('.');
            string cleanBase = SanitizeShortComponent(baseName);
            string cleanExtension = SanitizeShortComponent(extension);

            if (cleanBase.Length is > 0 and <= 8
                && cleanExtension.Length <= 3
                && IsSimpleShortComponent(baseName)
                && (isDirectory || IsSimpleShortComponent(extension)))
            {
                string candidate = cleanBase.PadRight(8) + cleanExtension.PadRight(3);
                if (!usedNames.Contains(candidate))
                {
                    return candidate;
                }
            }

            cleanBase = cleanBase.Length == 0 ? "FILE" : cleanBase;
            cleanExtension = cleanExtension.Length > 3 ? cleanExtension[..3] : cleanExtension;

            for (int i = 1; i < 10000; i++)
            {
                string suffix = "~" + i.ToString(CultureInfo.InvariantCulture);
                int baseLength = Math.Min(8 - suffix.Length, cleanBase.Length);
                string candidateBase = cleanBase[..baseLength] + suffix;
                string candidate = candidateBase.PadRight(8) + cleanExtension.PadRight(3);
                if (!usedNames.Contains(candidate))
                {
                    return candidate;
                }
            }

            throw new IOException($"Unable to allocate a FAT short name for {name}.");
        }
        private static string SanitizeShortComponent(string component)
        {
            component = component.Trim().ToUpperInvariant();
            var builder = new StringBuilder(component.Length);
            foreach (char c in component)
            {
                if (IsValidShortNameCharacter(c))
                {
                    builder.Append(c);
                }
                else if (c != '.')
                {
                    builder.Append('_');
                }
            }

            return builder.ToString();
        }
        private static bool IsSimpleShortComponent(string component)
        {
            if (component.Length == 0)
            {
                return true;
            }

            if (component != component.Trim() || component.Contains('.') || component.Contains(' '))
            {
                return false;
            }

            foreach (char c in component.ToUpperInvariant())
            {
                if (!IsValidShortNameCharacter(c))
                {
                    return false;
                }
            }

            return true;
        }
        private static bool IsValidShortNameCharacter(char c)
        {
            return c is >= 'A' and <= 'Z'
                || c is >= '0' and <= '9'
                || c is '$' or '%' or '\'' or '-' or '_' or '@' or '~' or '`' or '!' or '(' or ')' or '{' or '}' or '^' or '#' or '&';
        }
    }
}
