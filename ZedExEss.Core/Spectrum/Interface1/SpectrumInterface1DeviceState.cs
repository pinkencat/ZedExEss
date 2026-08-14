namespace ZedExEss.Spectrum.Interface1;

/// <summary>Transient rotating-head state for one Microdrive mechanism.</summary>
public readonly record struct MicrodriveTransportState(
    int HeadPosition,
    int Transferred,
    int MaximumTransfer,
    int Gap,
    int Sync,
    byte LastByte);

/// <summary>
/// Snapshot of Interface 1 latches and all eight rotating transports.
/// </summary>
/// <remarks>
/// Cartridge bytes are deliberately excluded. They are owned by
/// <c>SpectrumInterface1MediaState</c>, which restores mounted media before this
/// state is applied to the replacement machine's Interface 1 device.
/// </remarks>
public sealed class SpectrumInterface1DeviceState
{
    private readonly MicrodriveTransportState[] _drives;

    public SpectrumInterface1DeviceState(
        bool paged,
        byte control,
        byte networkOutput,
        byte motorMask,
        MicrodriveActivityState activity,
        IReadOnlyList<MicrodriveTransportState> drives)
    {
        ArgumentNullException.ThrowIfNull(drives);
        if (drives.Count != SpectrumInterface1Device.DriveCount)
        {
            throw new ArgumentException(
                $"Interface 1 state must contain {SpectrumInterface1Device.DriveCount} drive transports.",
                nameof(drives));
        }

        if (!Enum.IsDefined(activity))
        {
            throw new ArgumentOutOfRangeException(nameof(activity));
        }

        IsPaged = paged;
        Control = control;
        NetworkOutput = networkOutput;
        MotorMask = motorMask;
        Activity = activity;
        _drives = new MicrodriveTransportState[drives.Count];
        for (int i = 0; i < drives.Count; i++)
        {
            _drives[i] = drives[i];
        }

        Drives = Array.AsReadOnly(_drives);
    }

    public bool IsPaged { get; }
    public byte Control { get; }
    public byte NetworkOutput { get; }
    public byte MotorMask { get; }
    public MicrodriveActivityState Activity { get; }
    public IReadOnlyList<MicrodriveTransportState> Drives { get; }

    internal MicrodriveTransportState GetDrive(int index) => _drives[index];
}
