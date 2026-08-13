using ZedExEss.Zx8x.Core;

namespace ZedExEss.Zx8x.Memory;

/// <summary>Selects the RAM fitted to the first ZX8x machine implementation.</summary>
public enum Zx8xRamConfiguration
{
    /// <summary>The 1 KiB static RAM fitted inside an unexpanded ZX80 or ZX81.</summary>
    Internal1K,

    /// <summary>A 16 KiB expansion which disables the internal 1 KiB RAM.</summary>
    Expansion16K
}

/// <summary>
/// Decodes ordinary ZX80/ZX81 memory reads and writes.
/// </summary>
/// <remarks>
/// The original machines decode A14 to select ROM or RAM but do not fully decode
/// the remaining address lines. Consequently ROM occupies 0000-3FFF (repeating at
/// its physical 4/8 KiB size), RAM occupies 4000-7FFF (repeating at its fitted
/// size), and A15 mirrors those regions at 8000-FFFF. M1 opcode fetches in the
/// upper half also drive display generation; that bus behaviour deliberately does
/// not live here because an ordinary data read must still return memory unchanged.
/// </remarks>
public sealed class Zx8xMemory
{
    private const int AddressHalfMask = 0x7FFF;
    private const int RamWindowStart = 0x4000;
    private readonly Zx8xRomImage _rom;
    private readonly byte[] _ram;
    private readonly int _ramMask;

    public Zx8xMemory(
        Zx8xModel model,
        Zx8xRomImage rom,
        Zx8xRamConfiguration ramConfiguration = Zx8xRamConfiguration.Internal1K)
    {
        ArgumentNullException.ThrowIfNull(rom);

        Zx8xRomDescriptor expectedRom = Zx8xModelDescriptors.GetRom(model,
            rom.Descriptor.Id == "zx81-improved" ? Zx81RomRevision.Improved : Zx81RomRevision.Standard);
        if (rom.Descriptor.SizeBytes != expectedRom.SizeBytes ||
            !string.Equals(rom.Descriptor.Id, expectedRom.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"ROM {rom.Descriptor.Id} is not valid for the {model} memory profile.", nameof(rom));
        }

        Model = model;
        RamConfiguration = ramConfiguration;
        _rom = rom;
        _ram = new byte[GetRamSize(ramConfiguration)];
        _ramMask = _ram.Length - 1;
    }

    public Zx8xModel Model { get; }
    public Zx8xRamConfiguration RamConfiguration { get; }
    public int RamSizeBytes => _ram.Length;
    public ReadOnlyMemory<byte> Ram => _ram;

    /// <summary>Reads memory without applying opcode-fetch display substitution.</summary>
    public byte Read(ushort address)
    {
        int decodedAddress = address & AddressHalfMask;
        return decodedAddress < RamWindowStart
            ? _rom.ReadMirrored((ushort)decodedAddress)
            : _ram[(decodedAddress - RamWindowStart) & _ramMask];
    }

    /// <summary>Writes RAM through either its lower or A15-mirrored window.</summary>
    public void Write(ushort address, byte value)
    {
        int decodedAddress = address & AddressHalfMask;
        if (decodedAddress >= RamWindowStart)
        {
            _ram[(decodedAddress - RamWindowStart) & _ramMask] = value;
        }
    }

    /// <summary>Clears physical RAM while leaving the immutable ROM untouched.</summary>
    public void ClearRam(byte value = 0)
    {
        Array.Fill(_ram, value);
    }

    private static int GetRamSize(Zx8xRamConfiguration configuration)
    {
        return configuration switch
        {
            Zx8xRamConfiguration.Internal1K => 1024,
            Zx8xRamConfiguration.Expansion16K => 16 * 1024,
            _ => throw new ArgumentOutOfRangeException(nameof(configuration), configuration,
                "Unsupported ZX8x RAM configuration.")
        };
    }
}
