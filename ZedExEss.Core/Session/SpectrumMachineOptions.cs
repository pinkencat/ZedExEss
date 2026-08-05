using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Spectrum.Core;

/// <summary>Inputs used to construct one complete, timing-configured Spectrum machine.</summary>
public sealed class SpectrumMachineOptions
{
    public required SpectrumModel Model { get; init; }

    public required RomSet Roms { get; init; }

    public int SampleRate { get; init; } = Audio.SpectrumAudioTiming.DefaultSampleRate;

    public short AyOutputAmplitude { get; init; } = 13_500;

    public SpectrumJoystickType JoystickType { get; init; } = SpectrumJoystickType.None;

    public bool RenderEnabled { get; init; } = true;

    public bool ForceFullFrameCopy { get; init; }

    /// <summary>
    /// Adds optional hardware such as +3, Beta 128, or DivMMC devices before the CPU and bus
    /// timing links are finalised.
    /// </summary>
    public Action<SpectrumMachineConfigurationContext>? ConfigureDevices { get; init; }

    /// <summary>Optional instruction-boundary hook used by flash loading and debugging.</summary>
    public Func<bool>? BeforeCpuStep { get; init; }
}
