using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Spectrum.Core;

/// <summary>Inputs used to construct one complete, timing-configured Spectrum machine.</summary>
public sealed class SpectrumMachineOptions
{
    /// <summary>
    /// A recorder to keep across machine rebuilds, or null for a fresh one.
    /// </summary>
    /// <remarks>
    /// Hosts pass their own so that changing model or fitting an expansion does not
    /// silently throw away a recording in progress.
    /// </remarks>
    public Spectrum.Tape.SpectrumTapeRecorder? TapeRecorder { get; init; }

    /// <summary>Which ULA contention phase to model.</summary>
    public Memory.SpectrumContentionTiming ContentionTiming { get; init; }
        = Memory.SpectrumContentionTiming.Early;

    public required SpectrumModel Model { get; init; }

    public required RomSet Roms { get; init; }

    public int SampleRate { get; init; } = Audio.SpectrumAudioTiming.DefaultSampleRate;

    public short AyOutputAmplitude { get; init; } = 13_500;

    public SpectrumJoystickType JoystickType { get; init; } = SpectrumJoystickType.None;

    /// <summary>
    /// Fits a Covox DAC on port $DD. Optional expansion hardware, so it is off unless
    /// asked for; software that does not know about it is unaffected either way.
    /// </summary>
    public bool CovoxEnabled { get; init; }

    /// <summary>
    /// General Sound firmware. Supplying it fits the card; leaving it empty does not,
    /// because a card whose Z80 has nothing to execute would be detected and then
    /// never answer.
    /// </summary>
    public ReadOnlyMemory<byte> GeneralSoundFirmware { get; init; }

    /// <summary>
    /// The Multiface variant to fit, and its 8 KiB ROM.
    /// </summary>
    /// <remarks>
    /// Only the variant built for this model can be fitted, and a Multiface is mutually
    /// exclusive with DivMMC: both overlay the same low memory and their control ports
    /// overlap. The factory enforces both rather than trusting the caller.
    /// </remarks>
    public Multiface.SpectrumMultifaceModel MultifaceModel { get; init; }
        = Multiface.SpectrumMultifaceModel.None;

    public ReadOnlyMemory<byte> MultifaceRom { get; init; }

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
