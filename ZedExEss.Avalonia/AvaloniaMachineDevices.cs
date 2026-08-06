using ZedExEss.Spectrum.Disk.Beta;
using ZedExEss.Spectrum.Disk.Plus3;
using ZedExEss.Spectrum.DivMmc;

namespace ZedExEss.AvaloniaHost;

/// <summary>
/// Retains host-observable optional devices created as part of a portable machine graph.
/// The host uses these references only for media insertion/ejection and activity display.
/// </summary>
internal sealed class AvaloniaMachineDevices
{
    public SpectrumPlus3DiskController? Plus3DiskController { get; internal set; }
    public SpectrumBeta128Device? Beta128Device { get; internal set; }
    public SpectrumBeta128DiskController? BetaDiskController { get; internal set; }
    public SpectrumDivMmcDevice? DivMmcDevice { get; internal set; }
}
