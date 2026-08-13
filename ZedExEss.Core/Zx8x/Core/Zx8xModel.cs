using ZedExEss.Machines;

namespace ZedExEss.Zx8x.Core;

/// <summary>The two hardware models in the Sinclair ZX8x family.</summary>
public enum Zx8xModel
{
    Zx80,
    Zx81
}

/// <summary>Selectable firmware supplied for the ZX81 hardware profile.</summary>
public enum Zx81RomRevision
{
    Standard,
    Improved
}

/// <summary>Required host ROM file and exact size for one ZX8x firmware selection.</summary>
public sealed record Zx8xRomDescriptor(string Id, string FileName, int SizeBytes);

/// <summary>
/// Central ZX80/ZX81 identity and firmware table, intentionally separate from
/// <see cref="Spectrum.Core.SpectrumModel"/> and its ULA/paging capability table.
/// </summary>
public static class Zx8xModelDescriptors
{
    public const int CpuClockHz = 3_250_000;
    public const int BuiltInRamSizeBytes = 1024;

    private static readonly MachineDescriptor Zx80 =
        new("zx80", MachineFamily.Zx8x, "Sinclair ZX80", CpuClockHz);
    private static readonly MachineDescriptor Zx81 =
        new("zx81", MachineFamily.Zx8x, "Sinclair ZX81", CpuClockHz);

    private static readonly Zx8xRomDescriptor Zx80Rom =
        new("zx80-standard", "zx80.rom", 4 * 1024);
    private static readonly Zx8xRomDescriptor Zx81StandardRom =
        new("zx81-standard", "zx81_Standard.rom", 8 * 1024);
    private static readonly Zx8xRomDescriptor Zx81ImprovedRom =
        new("zx81-improved", "zx81_improved.rom", 8 * 1024);

    public static MachineDescriptor ForModel(Zx8xModel model)
    {
        return model switch
        {
            Zx8xModel.Zx80 => Zx80,
            Zx8xModel.Zx81 => Zx81,
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported ZX8x model.")
        };
    }

    public static Zx8xRomDescriptor GetRom(Zx8xModel model, Zx81RomRevision zx81Revision = Zx81RomRevision.Standard)
    {
        return model switch
        {
            Zx8xModel.Zx80 => Zx80Rom,
            Zx8xModel.Zx81 when zx81Revision == Zx81RomRevision.Standard => Zx81StandardRom,
            Zx8xModel.Zx81 when zx81Revision == Zx81RomRevision.Improved => Zx81ImprovedRom,
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported ZX8x firmware selection.")
        };
    }
}
