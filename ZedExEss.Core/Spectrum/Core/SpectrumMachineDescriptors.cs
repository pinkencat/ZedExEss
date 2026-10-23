using ZedExEss.Machines;

namespace ZedExEss.Spectrum.Core;

/// <summary>Stable host descriptors for the Spectrum family and supported clones.</summary>
public static class SpectrumMachineDescriptors
{
    private static readonly MachineDescriptor Spectrum16K = Create("spectrum-16k", "ZX Spectrum 16K", SpectrumModel.Spectrum16K);
    private static readonly MachineDescriptor Spectrum48K = Create("spectrum-48k", "ZX Spectrum 48K", SpectrumModel.Spectrum48K);
    private static readonly MachineDescriptor Spectrum128K = Create("spectrum-128k", "ZX Spectrum 128K", SpectrumModel.Spectrum128K);
    private static readonly MachineDescriptor SpectrumPlus2 = Create("spectrum-plus2", "ZX Spectrum +2", SpectrumModel.SpectrumPlus2);
    private static readonly MachineDescriptor SpectrumPlus2A = Create("spectrum-plus2a", "ZX Spectrum +2A", SpectrumModel.SpectrumPlus2A);
    private static readonly MachineDescriptor SpectrumPlus3 = Create("spectrum-plus3", "ZX Spectrum +3", SpectrumModel.SpectrumPlus3);
    private static readonly MachineDescriptor Pentagon128 = Create("pentagon-128", "Pentagon 128", SpectrumModel.Pentagon128);
    private static readonly MachineDescriptor Scorpion256 = Create("scorpion-256", "Scorpion 256", SpectrumModel.Scorpion256);

    public static MachineDescriptor ForModel(SpectrumModel model)
    {
        return model switch
        {
            SpectrumModel.Spectrum16K => Spectrum16K,
            SpectrumModel.Spectrum48K => Spectrum48K,
            SpectrumModel.Spectrum128K => Spectrum128K,
            SpectrumModel.SpectrumPlus2 => SpectrumPlus2,
            SpectrumModel.SpectrumPlus2A => SpectrumPlus2A,
            SpectrumModel.SpectrumPlus3 => SpectrumPlus3,
            SpectrumModel.Pentagon128 => Pentagon128,
            SpectrumModel.Scorpion256 => Scorpion256,
            _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported Spectrum model.")
        };
    }

    private static MachineDescriptor Create(string id, string displayName, SpectrumModel model)
    {
        return new MachineDescriptor(id, MachineFamily.Spectrum, displayName, SpectrumModelTraits.CpuClockHz(model));
    }
}
