using ZedExEss.Spectrum.Interface1;

namespace ZedExEss.Spectrum.Core;

/// <summary>One mounted Microdrive slot in an Interface 1 session snapshot.</summary>
public sealed record SpectrumInterface1MediaSlotState(
    string? BackingPath,
    MicrodriveCartridgeState? Cartridge);

/// <summary>Deep-copied media state for all eight persistent Microdrive slots.</summary>
public sealed class SpectrumInterface1MediaSnapshot
{
    private readonly SpectrumInterface1MediaSlotState[] _slots;

    public SpectrumInterface1MediaSnapshot(IReadOnlyList<SpectrumInterface1MediaSlotState> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count != SpectrumInterface1Device.DriveCount)
        {
            throw new ArgumentException(
                $"Interface 1 media state must contain {SpectrumInterface1Device.DriveCount} slots.",
                nameof(slots));
        }

        _slots = new SpectrumInterface1MediaSlotState[slots.Count];
        for (int i = 0; i < slots.Count; i++)
        {
            _slots[i] = slots[i] ?? throw new ArgumentException("A media slot cannot be null.", nameof(slots));
        }

        Slots = Array.AsReadOnly(_slots);
    }

    public IReadOnlyList<SpectrumInterface1MediaSlotState> Slots { get; }
    internal SpectrumInterface1MediaSlotState GetSlot(int index) => _slots[index];
}

/// <summary>
/// Complete portable Interface 1 state: persistent cartridges plus the currently
/// connected device's transient latches and rotating-head positions.
/// </summary>
public sealed record SpectrumInterface1Snapshot(
    SpectrumInterface1MediaSnapshot Media,
    SpectrumInterface1DeviceState? Device);
