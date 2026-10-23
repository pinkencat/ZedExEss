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
    /// <summary>What a folder projection put into an image, so changes can be merged back.</summary>
    /// <remarks>
    /// Merging needs two things the image cannot tell you on its own. Which files were
    /// there at the start, so a file now missing can be recognised as one the guest
    /// deleted rather than one the projection never carried; and which files were
    /// overlaid by the host, so emulator-injected configuration is never written into
    /// the user's folder as though they had put it there.
    ///
    /// Paths are relative to the projected folder, separated with '/', and compared
    /// without regard to case.
    /// </remarks>
    public sealed class SpectrumFatProjectionManifest
    {
        internal SpectrumFatProjectionManifest(
            IReadOnlySet<string> importedFiles,
            IReadOnlySet<string> overlayFiles)
        {
            ImportedFiles = importedFiles;
            OverlayFiles = overlayFiles;
        }

        /// <summary>Files read out of the host folder when the image was built.</summary>
        public IReadOnlySet<string> ImportedFiles { get; }

        /// <summary>Files the host injected, which must never be written back.</summary>
        public IReadOnlySet<string> OverlayFiles { get; }
    }

    /// <summary>What a merge did, for reporting it.</summary>
    public readonly record struct SpectrumFatMergeResult(int Written, int Deleted, int Unchanged)
    {
        public bool ChangedAnything => Written > 0 || Deleted > 0;
    }

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
        /// <summary>
        /// Writes a host folder into a blank image, and reports what it put there.
        /// </summary>
        public static SpectrumFatProjectionManifest ImportDirectoryIntoFat16Image(
            string imagePath,
            string sourceDirectory,
            IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? overlayFiles = null)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                throw new ArgumentException("Image path is empty.", nameof(imagePath));
            }

            if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("Source directory does not exist.");
            }

            IReadOnlyDictionary<string, ReadOnlyMemory<byte>> normalizedOverlayFiles =
                NormalizeOverlayFiles(sourceDirectory, overlayFiles);
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Fat16Layout layout = Fat16Layout.Read(stream);
            var writer = new Fat16Writer(stream, layout);
            writer.ImportDirectory(sourceDirectory, normalizedOverlayFiles);
            writer.Flush();
            return new SpectrumFatProjectionManifest(
                writer.ImportedFiles,
                new HashSet<string>(normalizedOverlayFiles.Keys, StringComparer.OrdinalIgnoreCase));
        }

        private static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> NormalizeOverlayFiles(
            string sourceDirectory,
            IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? overlayFiles)
        {
            var normalized = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
            if (overlayFiles == null)
            {
                return normalized;
            }

            string sourceRoot = Path.GetFullPath(sourceDirectory);
            foreach (KeyValuePair<string, ReadOnlyMemory<byte>> overlay in overlayFiles)
            {
                if (string.IsNullOrWhiteSpace(overlay.Key) || Path.IsPathRooted(overlay.Key))
                {
                    throw new ArgumentException("Overlay file paths must be non-empty relative paths.", nameof(overlayFiles));
                }

                string[] segments = overlay.Key
                    .Replace('\\', '/')
                    .Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0
                    || segments.Any(segment => segment is "." or ".."
                        || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                {
                    throw new ArgumentException(
                        $"Overlay file path '{overlay.Key}' is not a valid relative file path.",
                        nameof(overlayFiles));
                }

                string relativePath = string.Join('/', segments);
                if (!normalized.TryAdd(relativePath, overlay.Value))
                {
                    throw new ArgumentException(
                        $"Overlay file path '{overlay.Key}' is duplicated.",
                        nameof(overlayFiles));
                }

                string parentRelativePath = GetRelativeParent(relativePath);
                string parentPath = parentRelativePath.Length == 0
                    ? sourceRoot
                    : Path.Combine(sourceRoot, parentRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(parentPath))
                {
                    throw new DirectoryNotFoundException(
                        $"Overlay parent directory '{parentRelativePath}' does not exist in the projected folder.");
                }

                string targetPath = Path.Combine(parentPath, segments[^1]);
                if (Directory.Exists(targetPath))
                {
                    throw new IOException($"Overlay file '{relativePath}' conflicts with a source directory.");
                }
            }

            return normalized;
        }

        private static string GetRelativeParent(string relativePath)
        {
            int separator = relativePath.LastIndexOf('/');
            return separator < 0 ? string.Empty : relativePath[..separator];
        }

        private static string CombineRelative(string directory, string name) =>
            directory.Length == 0 ? name : $"{directory}/{name}";
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
        /// <summary>
        /// Writes the guest's changes back into the folder the image was projected from,
        /// touching only what actually changed.
        /// </summary>
        /// <remarks>
        /// Deliberately not the wholesale replacement
        /// <see cref="ExportFat16ImageToDirectory(string, string)"/> performs. That is
        /// right for exporting into a fresh directory and wrong for a folder the user
        /// owns: a projected NextZXOS install is mostly a large tree of games and
        /// documentation that the guest never touched, and rewriting all of it to save
        /// one file would be slow, would reset every timestamp, and would turn any fault
        /// in the projection into data loss.
        ///
        /// So files identical to what is already on disk are left completely alone,
        /// files the host overlaid are never written, and the only files deleted are ones
        /// this projection itself imported and the guest then removed.
        /// </remarks>
        public static SpectrumFatMergeResult MergeFat16ImageIntoDirectory(
            string imagePath,
            string targetDirectory,
            SpectrumFatProjectionManifest manifest)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                throw new ArgumentException("Image path is empty.", nameof(imagePath));
            }

            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                throw new ArgumentException("Target directory path is empty.", nameof(targetDirectory));
            }

            string targetRoot = Path.GetFullPath(targetDirectory);
            if (new DirectoryInfo(targetRoot).Parent == null)
            {
                throw new IOException("Refusing to merge folder-backed storage into a filesystem root.");
            }

            string exportRoot = Path.Combine(
                Path.GetTempPath(),
                "ZedExEss-DivMMC-Merge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(exportRoot);

            try
            {
                using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    Fat16Layout layout = Fat16Layout.Read(stream);
                    var reader = new Fat16Reader(stream, layout);
                    reader.ExportToDirectory(exportRoot);
                }

                int written = 0;
                int unchanged = 0;
                int deleted = 0;
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string file in Directory.EnumerateFiles(exportRoot, "*", SearchOption.AllDirectories))
                {
                    string relativePath = Path.GetRelativePath(exportRoot, file).Replace('\\', '/');
                    present.Add(relativePath);
                    if (manifest.OverlayFiles.Contains(relativePath))
                    {
                        continue;
                    }

                    string destination = Path.Combine(
                        targetRoot,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(destination) && FileContentsMatch(file, destination))
                    {
                        unchanged++;
                        continue;
                    }

                    string? parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    if (File.Exists(destination))
                    {
                        File.SetAttributes(destination, FileAttributes.Normal);
                    }

                    File.Copy(file, destination, overwrite: true);
                    written++;
                }

                foreach (string relativePath in manifest.ImportedFiles)
                {
                    if (present.Contains(relativePath) || manifest.OverlayFiles.Contains(relativePath))
                    {
                        continue;
                    }

                    string destination = Path.Combine(
                        targetRoot,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(destination))
                    {
                        continue;
                    }

                    File.SetAttributes(destination, FileAttributes.Normal);
                    File.Delete(destination);
                    deleted++;
                }

                return new SpectrumFatMergeResult(written, deleted, unchanged);
            }
            finally
            {
                if (Directory.Exists(exportRoot))
                {
                    Directory.Delete(exportRoot, recursive: true);
                }
            }
        }

        /// <summary>Compares two files byte for byte, cheaply rejecting on length first.</summary>
        private static bool FileContentsMatch(string left, string right)
        {
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length)
            {
                return false;
            }

            using FileStream leftStream = File.OpenRead(left);
            using FileStream rightStream = File.OpenRead(right);
            byte[] leftChunk = new byte[64 * 1024];
            byte[] rightChunk = new byte[64 * 1024];
            while (true)
            {
                int leftRead = leftStream.ReadAtLeast(leftChunk, leftChunk.Length, throwOnEndOfStream: false);
                int rightRead = rightStream.ReadAtLeast(rightChunk, rightChunk.Length, throwOnEndOfStream: false);
                if (leftRead != rightRead)
                {
                    return false;
                }

                if (leftRead == 0)
                {
                    return true;
                }

                if (!leftChunk.AsSpan(0, leftRead).SequenceEqual(rightChunk.AsSpan(0, rightRead)))
                {
                    return false;
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
            private readonly HashSet<string> _importedFiles = new(StringComparer.OrdinalIgnoreCase);
            private ushort _nextFreeCluster = 2;

            /// <summary>The host files this image was built from.</summary>
            public IReadOnlySet<string> ImportedFiles => _importedFiles;

            public Fat16Writer(FileStream stream, Fat16Layout layout)
            {
                _stream = stream;
                _layout = layout;
                _fat = new ushort[layout.ClusterCount + 2];
                _fat[0] = (ushort)(0xFF00 | layout.MediaDescriptor);
                _fat[1] = EndOfChain;
                _rootDirectory = new byte[layout.RootDirectoryBytes];
            }
            public void ImportDirectory(
                string sourceDirectory,
                IReadOnlyDictionary<string, ReadOnlyMemory<byte>> overlayFiles)
            {
                var writer = DirectoryWriter.CreateRoot(this, _rootDirectory);
                ImportDirectoryContents(
                    sourceDirectory,
                    writer,
                    currentCluster: 0,
                    parentCluster: 0,
                    relativeDirectory: string.Empty,
                    overlayFiles);
                writer.Finish();
            }
            public void Flush()
            {
                WriteFatCopies();
                WriteBytesAtSector(_layout.RootDirectoryStart, _rootDirectory);
                _stream.Flush();
            }
            private void ImportDirectoryContents(
                string sourceDirectory,
                DirectoryWriter directory,
                ushort currentCluster,
                ushort parentCluster,
                string relativeDirectory,
                IReadOnlyDictionary<string, ReadOnlyMemory<byte>> overlayFiles)
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
                    directory.WriteNamedEntry(
                        info.Name,
                        shortName,
                        BuildDirectoryEntry(shortName, firstCluster, 0, info.LastWriteTime));

                    var childWriter = DirectoryWriter.CreateClusterChain(this, firstCluster);
                    ImportDirectoryContents(
                        childDirectory,
                        childWriter,
                        firstCluster,
                        currentCluster,
                        CombineRelative(relativeDirectory, info.Name),
                        overlayFiles);
                    childWriter.Finish();
                }

                foreach (string childFile in Directory.EnumerateFiles(sourceDirectory).Order(StringComparer.OrdinalIgnoreCase))
                {
                    FileInfo info = new(childFile);
                    string relativePath = CombineRelative(relativeDirectory, info.Name);
                    if (overlayFiles.ContainsKey(relativePath))
                    {
                        continue;
                    }

                    _importedFiles.Add(relativePath);
                    ushort firstCluster = WriteFile(childFile, info.Length);
                    string shortName = directory.AllocateShortName(info.Name, isDirectory: false);
                    directory.WriteNamedEntry(
                        info.Name,
                        shortName,
                        BuildDirectoryEntry(shortName, firstCluster, checked((uint)info.Length), info.LastWriteTime));
                }

                foreach (KeyValuePair<string, ReadOnlyMemory<byte>> overlay in overlayFiles
                    .Where(overlay => string.Equals(
                        GetRelativeParent(overlay.Key),
                        relativeDirectory,
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(overlay => overlay.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string name = overlay.Key[(overlay.Key.LastIndexOf('/') + 1)..];
                    ushort firstCluster = WriteFile(overlay.Value);
                    string shortName = directory.AllocateShortName(name, isDirectory: false);
                    directory.WriteNamedEntry(
                        name,
                        shortName,
                        BuildDirectoryEntry(
                            shortName,
                            firstCluster,
                            checked((uint)overlay.Value.Length),
                            new DateTime(2000, 1, 1)));
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

            private ushort WriteFile(ReadOnlyMemory<byte> data)
            {
                if (data.IsEmpty)
                {
                    return 0;
                }

                ushort firstCluster = 0;
                ushort previousCluster = 0;
                byte[] buffer = new byte[_layout.ClusterBytes];
                int offset = 0;
                while (offset < data.Length)
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
                    int count = Math.Min(buffer.Length, data.Length - offset);
                    data.Span.Slice(offset, count).CopyTo(buffer);
                    WriteCluster(cluster, buffer);
                    offset += count;
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
                public void WriteNamedEntry(string name, string shortName, byte[] entry)
                {
                    ArgumentException.ThrowIfNullOrEmpty(name);
                    ArgumentNullException.ThrowIfNull(entry);
                    if (entry.Length != 32)
                    {
                        throw new ArgumentException("A FAT directory entry must be exactly 32 bytes.", nameof(entry));
                    }

                    string shortDisplayName = GetShortDisplayName(shortName);
                    if (!string.Equals(name, shortDisplayName, StringComparison.Ordinal))
                    {
                        foreach (byte[] longEntry in BuildLongNameEntries(name, shortName))
                        {
                            WriteEntry(longEntry);
                        }
                    }

                    WriteEntry(entry);
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
                var longNameEntries = new List<byte[]>();
                for (int offset = 0; offset + 32 <= directoryData.Length; offset += 32)
                {
                    byte first = directoryData[offset];
                    if (first == 0x00)
                    {
                        break;
                    }

                    if (first == 0xE5)
                    {
                        longNameEntries.Clear();
                        continue;
                    }

                    byte attributes = directoryData[offset + 11];
                    if ((attributes & 0x0F) == 0x0F)
                    {
                        longNameEntries.Add(directoryData.AsSpan(offset, 32).ToArray());
                        continue;
                    }

                    if ((attributes & 0x08) != 0)
                    {
                        longNameEntries.Clear();
                        continue;
                    }

                    string name = TryReadLongName(longNameEntries, directoryData, offset, out string longName)
                        ? longName
                        : ReadShortName(directoryData, offset);
                    longNameEntries.Clear();
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
            private static bool TryReadLongName(
                List<byte[]> entries,
                byte[] shortEntryData,
                int shortEntryOffset,
                out string name)
            {
                name = string.Empty;
                if (entries.Count == 0 || entries.Count > 20)
                {
                    return false;
                }

                int entryCount = entries[0][0] & 0x1F;
                if ((entries[0][0] & 0x40) == 0 || entryCount != entries.Count)
                {
                    return false;
                }

                byte checksum = ComputeShortNameChecksum(shortEntryData, shortEntryOffset);
                for (int index = 0; index < entries.Count; index++)
                {
                    byte[] entry = entries[index];
                    int expectedOrdinal = entryCount - index;
                    if ((entry[0] & 0x1F) != expectedOrdinal
                        || (index != 0 && (entry[0] & 0x40) != 0)
                        || entry[11] != 0x0F
                        || entry[12] != 0
                        || entry[13] != checksum
                        || ReadUInt16(entry, 26) != 0)
                    {
                        return false;
                    }
                }

                int[] characterOffsets = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
                var builder = new StringBuilder(entries.Count * characterOffsets.Length);
                for (int entryIndex = entries.Count - 1; entryIndex >= 0; entryIndex--)
                {
                    byte[] entry = entries[entryIndex];
                    foreach (int characterOffset in characterOffsets)
                    {
                        ushort value = ReadUInt16(entry, characterOffset);
                        if (value == 0)
                        {
                            name = builder.ToString();
                            return name.Length != 0;
                        }

                        if (value != 0xFFFF)
                        {
                            builder.Append((char)value);
                        }
                    }
                }

                name = builder.ToString();
                return name.Length != 0;
            }
            private static string SanitizeHostName(string name)
            {
                char[] invalid = Path.GetInvalidFileNameChars();
                var builder = new StringBuilder(name.Length);
                foreach (char c in name)
                {
                    bool unsafeCharacter = c is '/' or '\\' or '\0'
                        || char.IsControl(c)
                        || Array.IndexOf(invalid, c) >= 0;
                    builder.Append(unsafeCharacter ? '_' : c);
                }

                string sanitized = builder.ToString().Trim().TrimEnd('.');
                return sanitized is "" or "." or ".." ? "FILE" : sanitized;
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
        private static IEnumerable<byte[]> BuildLongNameEntries(string name, string shortName)
        {
            const int CharactersPerEntry = 13;
            int[] characterOffsets = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
            if (name.Length > 255)
            {
                throw new IOException($"FAT long name '{name}' exceeds 255 UTF-16 code units.");
            }

            int entryCount = (name.Length + CharactersPerEntry - 1) / CharactersPerEntry;
            byte checksum = ComputeShortNameChecksum(shortName);
            for (int ordinal = entryCount; ordinal >= 1; ordinal--)
            {
                byte[] entry = new byte[32];
                Array.Fill(entry, (byte)0xFF);
                entry[0] = (byte)(ordinal | (ordinal == entryCount ? 0x40 : 0));
                entry[11] = 0x0F;
                entry[12] = 0;
                entry[13] = checksum;
                WriteUInt16(entry, 26, 0);

                int sourceOffset = (ordinal - 1) * CharactersPerEntry;
                bool wroteTerminator = false;
                for (int index = 0; index < characterOffsets.Length; index++)
                {
                    int sourceIndex = sourceOffset + index;
                    if (sourceIndex < name.Length)
                    {
                        WriteUInt16(entry, characterOffsets[index], name[sourceIndex]);
                    }
                    else if (!wroteTerminator)
                    {
                        WriteUInt16(entry, characterOffsets[index], 0);
                        wroteTerminator = true;
                    }
                }

                yield return entry;
            }
        }
        private static byte ComputeShortNameChecksum(string shortName)
        {
            if (shortName.Length != 11)
            {
                throw new ArgumentException("A FAT short name must contain exactly 11 bytes.", nameof(shortName));
            }

            byte checksum = 0;
            foreach (char value in shortName)
            {
                checksum = (byte)(((checksum & 1) << 7) + (checksum >> 1) + (byte)value);
            }

            return checksum;
        }
        private static byte ComputeShortNameChecksum(byte[] directoryData, int offset)
        {
            byte checksum = 0;
            for (int index = 0; index < 11; index++)
            {
                checksum = (byte)(((checksum & 1) << 7)
                    + (checksum >> 1)
                    + directoryData[offset + index]);
            }

            return checksum;
        }
        private static string GetShortDisplayName(string shortName)
        {
            string baseName = shortName[..8].TrimEnd();
            string extension = shortName[8..].TrimEnd();
            return extension.Length == 0 ? baseName : $"{baseName}.{extension}";
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
