using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Interface1;
using ZedExEss.Zx8x.Memory;

namespace ZedExEss.Hosting;

/// <summary>
/// Portable desktop preferences that are safe to restore before constructing an emulated
/// machine. Mounted media, turbo state and debugger state are deliberately not persisted.
/// </summary>
public sealed record EmulatorHostSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public double ScreenZoom { get; init; } = 2.0;
    public bool TapeBrowserVisible { get; init; } = true;
    public SpectrumJoystickType JoystickType { get; init; } = SpectrumJoystickType.None;
    public bool FlashLoadEnabled { get; init; } = true;
    public bool PollingLoaderAccelerationEnabled { get; init; } = true;
    public bool SemanticLoaderAccelerationEnabled { get; init; }
    public bool RunTapeAccelerationAtMaximumSpeed { get; init; } = true;
    public bool AutoLoadTapeOnAttach { get; init; }
    public bool AutoTapePlayStopEnabled { get; init; } = true;
    public bool DirtyLinePresentationEnabled { get; init; } = true;
    public bool GigascreenBlendEnabled { get; init; }
    public bool Interface1Enabled { get; init; }
    public SpectrumInterface1RomRevision Interface1RomRevision { get; init; } = SpectrumInterface1RomRevision.Revision2;
    public Zx8xRamConfiguration Zx8xRamConfiguration { get; init; } = Zx8xRamConfiguration.Expansion16K;
}
