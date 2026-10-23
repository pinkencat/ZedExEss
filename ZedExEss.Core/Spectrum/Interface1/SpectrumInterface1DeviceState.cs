namespace ZedExEss.Spectrum.Interface1;

/// <summary>Transient rotating-head state for one Microdrive mechanism.</summary>
public readonly record struct MicrodriveTransportState(
    int HeadPosition,
    int Transferred,
    int MaximumTransfer,
    int Gap,
    int Sync,
    byte LastByte);

/// <summary>Bit-framing state for an in-flight Interface 1 RS232 byte.</summary>
public readonly record struct SpectrumInterface1Rs232TransportState(
    int InputPhase,
    int OutputPhase,
    byte InputShiftRegister,
    byte OutputShiftRegister,
    bool InputLine,
    bool OutputLine);

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
        : this(
            paged,
            control,
            networkOutput,
            motorMask,
            activity,
            default,
            drives)
    {
    }

    public SpectrumInterface1DeviceState(
        bool paged,
        byte control,
        byte networkOutput,
        byte motorMask,
        MicrodriveActivityState activity,
        SpectrumInterface1Rs232TransportState rs232,
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

        if (rs232.InputPhase is < 0 or > 13 || rs232.OutputPhase is < 0 or > 13)
        {
            throw new ArgumentOutOfRangeException(nameof(rs232), "RS232 framing phase is invalid.");
        }

        IsPaged = paged;
        Control = control;
        NetworkOutput = networkOutput;
        MotorMask = motorMask;
        Activity = activity;
        Rs232 = rs232;
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
    public SpectrumInterface1Rs232TransportState Rs232 { get; }
    public IReadOnlyList<MicrodriveTransportState> Drives { get; }

    internal MicrodriveTransportState GetDrive(int index) => _drives[index];
}
