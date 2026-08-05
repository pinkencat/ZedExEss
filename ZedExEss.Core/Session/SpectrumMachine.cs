using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Spectrum.Tape;
using ZedExEss.Spectrum.Video;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Core;

/// <summary>
/// The portable object graph for one running Spectrum model.
/// </summary>
/// <remarks>
/// Execution drivers and presentation surfaces deliberately live in the host. The same machine
/// can therefore be driven by WinMM, an Avalonia audio backend, turbo execution, or diagnostics
/// without rebuilding its timing-sensitive device graph differently.
/// </remarks>
public sealed class SpectrumMachine
{
    internal SpectrumMachine(
        SpectrumModel model,
        int sampleRate,
        SpectrumMemory memory,
        SpectrumPortBus ports,
        SpectrumUlaRenderer renderer,
        SpectrumAudioRenderer audio,
        SpectrumKeyboard keyboard,
        SpectrumJoystickDevice joystick,
        SpectrumEarInputDevice earInput,
        SpectrumUla ula,
        AY38912? ayChip,
        Z80 cpu,
        SpectrumEmulator emulator)
    {
        Model = model;
        SampleRate = sampleRate;
        Memory = memory;
        Ports = ports;
        Renderer = renderer;
        Audio = audio;
        Keyboard = keyboard;
        Joystick = joystick;
        EarInput = earInput;
        Ula = ula;
        AyChip = ayChip;
        Cpu = cpu;
        Emulator = emulator;
    }

    public SpectrumModel Model { get; }
    public int SampleRate { get; }
    public int CpuClockHz => SpectrumModelTraits.CpuClockHz(Model);
    public int TstatesPerFrame => SpectrumUlaTiming.ForModel(Model).TstatesPerFrame;
    public SpectrumMemory Memory { get; }
    public SpectrumPortBus Ports { get; }
    public SpectrumUlaRenderer Renderer { get; }
    public SpectrumAudioRenderer Audio { get; }
    public SpectrumKeyboard Keyboard { get; }
    public SpectrumJoystickDevice Joystick { get; }
    public SpectrumEarInputDevice EarInput { get; }
    public SpectrumUla Ula { get; }
    public AY38912? AyChip { get; }
    public Z80 Cpu { get; }
    public SpectrumEmulator Emulator { get; }

    /// <summary>
    /// Connects a tape source to both the scheduler and the EAR edge accelerator. Keeping these
    /// links together prevents a host from replacing one side and accidentally playing stale
    /// edges through the other.
    /// </summary>
    public void AttachTape(ITapePlayback? tape)
    {
        Emulator.TapePlayback = tape;
        EarInput.ConfigureEdgeLoading(tape as ITapeEdgeSource);
        if (tape == null)
        {
            EarInput.SetEarLevel(false);
        }
    }
}
