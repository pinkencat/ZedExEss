using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Memory;

namespace ZedExEss.ZxNext.Firmware;

/// <summary>Metadata for one user-supplied System/Next binary.</summary>
public sealed record ZxNextSystemAssetInfo(
    string Role,
    string Path,
    int Length,
    string Sha256);

/// <summary>One entry in the 512-byte <c>TBBLUE.FW</c> module map.</summary>
public readonly record struct ZxNextFirmwareBlock(
    int Index,
    string Name,
    int StartBlock,
    int BlockCount)
{
    public long FileOffset => (StartBlock + 1L) * ZxNextSystemAssets.FirmwareBlockSize;
    public int ByteLength => checked(BlockCount * ZxNextSystemAssets.FirmwareBlockSize);
}

/// <summary>
/// Validated, user-supplied assets from an official System/Next directory.
/// </summary>
/// <remarks>
/// The distribution contains several different kinds of binary. In particular,
/// <c>TBBLUE.FW</c> is a block-mapped firmware package and <c>TBBLUE.TBU</c> is an
/// FPGA update package; neither is the 8 KiB loader ROM compiled into the core.
/// </remarks>
public sealed class ZxNextSystemAssets
{
    public const string MainRomFileName = "enNextZX.rom";
    public const string AlternateRomFileName = "enAltZX.rom";
    public const string DivMmcRomFileName = "enNxtmmc.rom";
    public const string MultifaceRomFileName = "enNextMf.rom";
    public const string FirmwareFileName = "TBBLUE.FW";
    public const string CoreUpdateFileName = "TBBLUE.TBU";
    public const string CompiledLoaderFileName = "loader.bin";
    public const int MainRomSize = 4 * ZxNextMemoryBus.RomBankSize;
    public const int AlternateRomSize = 2 * ZxNextMemoryBus.RomBankSize;
    public const int ExpansionRomSize = ZxNextPhysicalMemory.PageSize;
    public const int FirmwareBlockSize = 512;
    public const int FirmwareBlockMapEntries = 9;

    private static readonly string[] FirmwareBlockNames =
    [
        "boot",
        "legacy editor alias",
        "updater",
        "legacy cores alias",
        "editor",
        "cores",
        "video test",
        "reset",
        "screens"
    ];

    private readonly byte[] _mainRom;
    private readonly byte[] _alternateRom;
    private readonly byte[] _divMmcRom;
    private readonly byte[] _multifaceRom;
    private readonly byte[] _firmwarePackage;
    private readonly byte[]? _bootLoaderRom;
    private readonly ZxNextFirmwareBlock[] _firmwareBlocks;

    private ZxNextSystemAssets(
        string rootDirectory,
        string machinesNextDirectory,
        LoadedAsset mainRom,
        LoadedAsset alternateRom,
        LoadedAsset divMmcRom,
        LoadedAsset multifaceRom,
        LoadedAsset firmwarePackage,
        LoadedAsset? bootLoaderRom,
        ZxNextSystemAssetInfo? coreUpdatePackage,
        ZxNextFirmwareBlock[] firmwareBlocks)
    {
        RootDirectory = rootDirectory;
        MachinesNextDirectory = machinesNextDirectory;
        MainRom = mainRom.Info;
        AlternateRom = alternateRom.Info;
        DivMmcRom = divMmcRom.Info;
        MultifaceRom = multifaceRom.Info;
        FirmwarePackage = firmwarePackage.Info;
        BootLoaderRom = bootLoaderRom?.Info;
        CoreUpdatePackage = coreUpdatePackage;
        _mainRom = mainRom.Data;
        _alternateRom = alternateRom.Data;
        _divMmcRom = divMmcRom.Data;
        _multifaceRom = multifaceRom.Data;
        _firmwarePackage = firmwarePackage.Data;
        _bootLoaderRom = bootLoaderRom?.Data;
        _firmwareBlocks = firmwareBlocks;
    }

    public string RootDirectory { get; }
    public string MachinesNextDirectory { get; }
    public ZxNextSystemAssetInfo MainRom { get; }
    public ZxNextSystemAssetInfo AlternateRom { get; }
    public ZxNextSystemAssetInfo DivMmcRom { get; }
    public ZxNextSystemAssetInfo MultifaceRom { get; }
    public ZxNextSystemAssetInfo FirmwarePackage { get; }
    public ZxNextSystemAssetInfo? CoreUpdatePackage { get; }
    public ZxNextSystemAssetInfo? BootLoaderRom { get; }
    public bool HasBootLoaderRom => _bootLoaderRom is not null;
    public IReadOnlyList<ZxNextFirmwareBlock> FirmwareBlocks => _firmwareBlocks;
    public ReadOnlyMemory<byte> MainRomData => _mainRom;
    public ReadOnlyMemory<byte> AlternateRomData => _alternateRom;
    public ReadOnlyMemory<byte> DivMmcRomData => _divMmcRom;
    public ReadOnlyMemory<byte> MultifaceRomData => _multifaceRom;
    public ReadOnlyMemory<byte> FirmwarePackageData => _firmwarePackage;
    public ReadOnlyMemory<byte> BootLoaderRomData => _bootLoaderRom ?? ReadOnlyMemory<byte>.Empty;

    public ReadOnlyMemory<byte> GetFirmwareBlockData(int index)
    {
        if ((uint)index >= _firmwareBlocks.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        ZxNextFirmwareBlock block = _firmwareBlocks[index];
        return _firmwarePackage.AsMemory(checked((int)block.FileOffset), block.ByteLength);
    }

    /// <summary>Discovers and validates assets without modifying the distribution.</summary>
    public static ZxNextSystemAssets LoadFromDirectory(
        string systemDirectory,
        string? bootRomPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDirectory);
        string rootDirectory = Path.GetFullPath(systemDirectory);
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The System/Next directory does not exist: '{rootDirectory}'.");
        }

        string machinesDirectory = ResolveRequiredDirectory(rootDirectory, "machines");
        string machinesNextDirectory = ResolveRequiredDirectory(machinesDirectory, "next");

        LoadedAsset mainRom = LoadRequiredAsset(
            machinesNextDirectory, MainRomFileName, "four-bank NextZXOS system ROM", MainRomSize);
        LoadedAsset alternateRom = LoadRequiredAsset(
            machinesNextDirectory, AlternateRomFileName, "two-bank alternate ROM", AlternateRomSize);
        LoadedAsset divMmcRom = LoadRequiredAsset(
            machinesNextDirectory, DivMmcRomFileName, "DivMMC ROM", ExpansionRomSize);
        LoadedAsset multifaceRom = LoadRequiredAsset(
            machinesNextDirectory, MultifaceRomFileName, "Multiface ROM", ExpansionRomSize);
        LoadedAsset firmwarePackage = LoadRequiredAsset(
            rootDirectory, FirmwareFileName, "firmware module package", expectedLength: null);

        ZxNextFirmwareBlock[] firmwareBlocks = ParseFirmwareBlockMap(firmwarePackage.Data);
        LoadedAsset? bootLoaderRom = LoadBootLoader(rootDirectory, bootRomPath);
        ZxNextSystemAssetInfo? coreUpdatePackage = LoadOptionalInfo(
            rootDirectory, CoreUpdateFileName, "FPGA core update package");

        return new ZxNextSystemAssets(
            rootDirectory,
            machinesNextDirectory,
            mainRom,
            alternateRom,
            divMmcRom,
            multifaceRom,
            firmwarePackage,
            bootLoaderRom,
            coreUpdatePackage,
            firmwareBlocks);
    }

    /// <summary>
    /// Loads assets represented by current hardware seams into a machine.
    /// </summary>
    /// <remarks>
    /// The validated system, alternate, DivMMC and Multiface ROM payloads are
    /// installed here. The firmware-package/FAT boot handoff remains separate.
    /// </remarks>
    public void ApplyTo(ZxNextMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        for (int bank = 0; bank < 4; bank++)
        {
            machine.MemoryBus.LoadRomBank(
                bank,
                _mainRom.AsSpan(bank * ZxNextMemoryBus.RomBankSize, ZxNextMemoryBus.RomBankSize));
        }

        for (int bank = 0; bank < 2; bank++)
        {
            machine.MemoryBus.LoadAlternateRomBank(
                bank,
                _alternateRom.AsSpan(bank * ZxNextMemoryBus.RomBankSize, ZxNextMemoryBus.RomBankSize));
        }

        machine.MemoryBus.LoadDivMmcRom(_divMmcRom);
        machine.MemoryBus.LoadMultifaceRom(_multifaceRom);
        if (_bootLoaderRom is not null)
        {
            machine.MemoryBus.LoadBootRom(_bootLoaderRom);
        }
    }

    private static ZxNextFirmwareBlock[] ParseFirmwareBlockMap(byte[] data)
    {
        if (data.Length < FirmwareBlockSize || data.Length % FirmwareBlockSize != 0)
        {
            throw new InvalidDataException(
                $"{FirmwareFileName} must contain a 512-byte map followed by whole 512-byte blocks.");
        }

        int payloadBlockCount = (data.Length / FirmwareBlockSize) - 1;
        var blocks = new ZxNextFirmwareBlock[FirmwareBlockMapEntries];
        for (int index = 0; index < blocks.Length; index++)
        {
            int mapOffset = index * 4;
            int startBlock = data[mapOffset] | (data[mapOffset + 1] << 8);
            int blockCount = data[mapOffset + 2] | (data[mapOffset + 3] << 8);
            if (blockCount == 0 || startBlock + blockCount > payloadBlockCount)
            {
                throw new InvalidDataException(
                    $"{FirmwareFileName} map entry {index} ({FirmwareBlockNames[index]}) " +
                    $"addresses blocks {startBlock}..{startBlock + blockCount - 1}, " +
                    $"outside its {payloadBlockCount}-block payload.");
            }

            blocks[index] = new ZxNextFirmwareBlock(
                index, FirmwareBlockNames[index], startBlock, blockCount);
        }

        return blocks;
    }

    private static LoadedAsset? LoadBootLoader(string rootDirectory, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string fullPath = Path.GetFullPath(explicitPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    $"The explicit ZX Spectrum Next boot ROM does not exist: '{fullPath}'.",
                    fullPath);
            }

            return string.Equals(Path.GetExtension(fullPath), ".vhd", StringComparison.OrdinalIgnoreCase)
                ? LoadBootLoaderVhdl(fullPath)
                : LoadAsset(fullPath, "FPGA boot-loader ROM", ZxNextMemoryBus.BootRomSize);
        }

        string loaderDirectory = Path.Combine(rootDirectory, "src", "firmware", "loader");
        string? path = ResolveOptionalFile(loaderDirectory, CompiledLoaderFileName);
        return path is null
            ? null
            : LoadAsset(path, "FPGA boot-loader ROM", ZxNextMemoryBus.BootRomSize);
    }

    private static LoadedAsset LoadBootLoaderVhdl(string path)
    {
        string text = File.ReadAllText(path);
        int constantStart = text.IndexOf("constant ROM", StringComparison.OrdinalIgnoreCase);
        int assignment = constantStart < 0
            ? -1
            : text.IndexOf(":=", constantStart, StringComparison.Ordinal);
        int dataStart = assignment < 0 ? -1 : text.IndexOf('(', assignment);
        int dataEnd = dataStart < 0
            ? -1
            : text.IndexOf(");", dataStart, StringComparison.Ordinal);
        if (constantStart < 0 || assignment < 0 || dataStart < 0 || dataEnd < 0)
        {
            throw new InvalidDataException(
                $"Boot ROM VHDL '{path}' does not contain a constant ROM array.");
        }

        MatchCollection matches = Regex.Matches(
            text.Substring(dataStart, dataEnd - dataStart),
            "x\\s*\"([0-9A-Fa-f]{2})\"",
            RegexOptions.CultureInvariant);
        var data = new byte[matches.Count];
        for (int index = 0; index < data.Length; index++)
        {
            data[index] = Convert.ToByte(matches[index].Groups[1].Value, 16);
        }

        return CreateLoadedAsset(
            path,
            "FPGA boot-loader ROM extracted from VHDL",
            data,
            ZxNextMemoryBus.BootRomSize);
    }

    private static LoadedAsset LoadRequiredAsset(
        string directory,
        string fileName,
        string role,
        int? expectedLength)
    {
        string? path = ResolveOptionalFile(directory, fileName);
        if (path is null)
        {
            throw new FileNotFoundException(
                $"Required System/Next {role} '{fileName}' was not found in '{directory}'.",
                Path.Combine(directory, fileName));
        }

        return LoadAsset(path, role, expectedLength);
    }

    private static LoadedAsset LoadAsset(string path, string role, int? expectedLength)
    {
        byte[] data = File.ReadAllBytes(path);
        return CreateLoadedAsset(path, role, data, expectedLength);
    }

    private static LoadedAsset CreateLoadedAsset(
        string path,
        string role,
        byte[] data,
        int? expectedLength)
    {
        if (expectedLength is int length && data.Length != length)
        {
            throw new InvalidDataException(
                $"System/Next {role} '{path}' is {data.Length} bytes; expected exactly {length} bytes.");
        }

        return new LoadedAsset(
            new ZxNextSystemAssetInfo(
                role,
                Path.GetFullPath(path),
                data.Length,
                Convert.ToHexString(SHA256.HashData(data))),
            data);
    }

    private static ZxNextSystemAssetInfo? LoadOptionalInfo(
        string directory,
        string fileName,
        string role)
    {
        string? path = ResolveOptionalFile(directory, fileName);
        return path is null ? null : LoadAsset(path, role, expectedLength: null).Info;
    }

    private static string ResolveRequiredDirectory(string parentDirectory, string name)
    {
        string path = Path.Combine(parentDirectory, name);
        if (Directory.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        if (!Directory.Exists(parentDirectory))
        {
            throw new DirectoryNotFoundException($"Directory does not exist: '{parentDirectory}'.");
        }

        string? match = Directory.EnumerateDirectories(parentDirectory)
            .FirstOrDefault(candidate =>
                string.Equals(Path.GetFileName(candidate), name, StringComparison.OrdinalIgnoreCase));
        return match is not null
            ? Path.GetFullPath(match)
            : throw new DirectoryNotFoundException(
                $"Required System/Next directory '{name}' was not found in '{parentDirectory}'.");
    }

    private static string? ResolveOptionalFile(string directory, string fileName)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string path = Path.Combine(directory, fileName);
        if (File.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        return Directory.EnumerateFiles(directory)
            .FirstOrDefault(candidate =>
                string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record LoadedAsset(ZxNextSystemAssetInfo Info, byte[] Data);
}
