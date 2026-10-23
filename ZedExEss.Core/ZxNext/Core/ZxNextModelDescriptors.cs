using ZedExEss.Machines;

namespace ZedExEss.ZxNext.Core;

/// <summary>Stable host identity for the initial native ZX Spectrum Next target.</summary>
public static class ZxNextModelDescriptors
{
    private static readonly MachineDescriptor NativeDescriptor = new(
        "zx-spectrum-next",
        MachineFamily.ZxNext,
        "ZX Spectrum Next",
        ZxNextReferenceBaseline.BaseCpuClockHz);

    /// <remarks>
    /// The descriptor records the reset CPU clock. A running Next machine exposes
    /// its current turbo clock through <see cref="IEmulatedMachine.CpuClockHz"/>.
    /// </remarks>
    public static MachineDescriptor Native => NativeDescriptor;
}
