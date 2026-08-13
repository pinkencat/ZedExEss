namespace ZedExEss.Machines;

/// <summary>Broad hardware family used to route host actions to the correct machine graph.</summary>
public enum MachineFamily
{
    Spectrum,
    Zx8x
}

/// <summary>
/// Stable, host-facing identity and clock information for an emulated computer.
/// </summary>
/// <remarks>
/// The descriptor deliberately contains no Spectrum ULA, paging or media concepts. A host can
/// use it to label and pace either family without introducing virtual calls into CPU bus cycles.
/// </remarks>
public sealed class MachineDescriptor
{
    public MachineDescriptor(string id, MachineFamily family, string displayName, int cpuClockHz)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cpuClockHz);

        Id = id;
        Family = family;
        DisplayName = displayName;
        CpuClockHz = cpuClockHz;
    }

    public string Id { get; }
    public MachineFamily Family { get; }
    public string DisplayName { get; }
    public int CpuClockHz { get; }
}

/// <summary>
/// Minimal common surface implemented by portable machine graphs.
/// Timing-sensitive components remain concrete properties on the family-specific machine type.
/// </summary>
public interface IEmulatedMachine
{
    MachineDescriptor Descriptor { get; }
    int SampleRate { get; }
    int CpuClockHz { get; }
}
