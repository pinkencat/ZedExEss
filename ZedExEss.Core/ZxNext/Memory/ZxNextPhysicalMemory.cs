using ZedExEss.ZxNext.Core;

using System.Runtime.CompilerServices;

namespace ZedExEss.ZxNext.Memory;

/// <summary>Installed ZX Spectrum Next RAM addressed as 8 KiB MMU pages.</summary>
public sealed class ZxNextPhysicalMemory
{
    public const int PageSize = 8 * 1024;
    public const int ReservedSystemBytes = 256 * 1024;

    private readonly byte[] _ram;

    public ZxNextPhysicalMemory(int configuredMemoryBytes = ZxNextReferenceBaseline.InitialRamBytes)
    {
        if (configuredMemoryBytes is not (1024 * 1024) and not (2 * 1024 * 1024))
        {
            throw new ArgumentOutOfRangeException(nameof(configuredMemoryBytes),
                "ZX Spectrum Next memory must be configured as 1 MiB or 2 MiB.");
        }

        ConfiguredMemoryBytes = configuredMemoryBytes;
        InstalledRamBytes = configuredMemoryBytes - ReservedSystemBytes;
        InstalledRamPageCount = InstalledRamBytes / PageSize;
        _ram = new byte[InstalledRamBytes];
    }

    /// <summary>
    /// Total fitted memory, including the 256 KiB address space reserved for ROMs,
    /// firmware and DivMMC resources.
    /// </summary>
    public int ConfiguredMemoryBytes { get; }

    public int InstalledRamBytes { get; }
    public int InstalledRamPageCount { get; }

    public bool IsPageInstalled(int page) => (uint)page < (uint)InstalledRamPageCount;

    public byte Read(int page, int offset)
    {
        ValidateOffset(offset);
        return IsPageInstalled(page) ? _ram[(page * PageSize) + offset] : (byte)0xFF;
    }

    public bool Write(int page, int offset, byte value)
    {
        ValidateOffset(offset);
        if (!IsPageInstalled(page))
        {
            return false;
        }

        _ram[(page * PageSize) + offset] = value;
        return true;
    }

    /// <summary>
    /// Page-checked read for callers that have already masked the offset to a page.
    /// The validated <see cref="Read(int,int)"/> remains the public contract.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadMasked(int page, int offset) =>
        IsPageInstalled(page) ? _ram[(page * PageSize) + offset] : (byte)0xFF;

    /// <summary>Page-checked write for callers that have already masked the offset.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool WriteMasked(int page, int offset, byte value)
    {
        if (!IsPageInstalled(page))
        {
            return false;
        }

        _ram[(page * PageSize) + offset] = value;
        return true;
    }

    public byte[] CopyPage(int page)
    {
        if (!IsPageInstalled(page))
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        byte[] result = new byte[PageSize];
        Array.Copy(_ram, page * PageSize, result, 0, PageSize);
        return result;
    }

    private static void ValidateOffset(int offset)
    {
        if ((uint)offset >= PageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    /// <summary>Copies all installed RAM, for a snapshot.</summary>
    public byte[] CopyAll() => _ram.ToArray();

    /// <summary>Puts a snapshot's RAM back, which must be the same size as this machine's.</summary>
    public void LoadAll(ReadOnlySpan<byte> ram)
    {
        if (ram.Length != _ram.Length)
        {
            throw new ArgumentException(
                $"This machine has {_ram.Length} bytes of RAM, not {ram.Length}.",
                nameof(ram));
        }

        ram.CopyTo(_ram);
    }
}
