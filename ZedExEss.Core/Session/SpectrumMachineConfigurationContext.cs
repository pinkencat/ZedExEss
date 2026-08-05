using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Spectrum.Tape;
using ZedExEss.Spectrum.Video;

namespace ZedExEss.Spectrum.Core;

/// <summary>
/// Core components available while optional port and memory-mapped devices are attached.
/// </summary>
public sealed class SpectrumMachineConfigurationContext
{
    internal SpectrumMachineConfigurationContext(
        SpectrumModel model,
        SpectrumMemory memory,
        SpectrumPortBus ports,
        SpectrumUlaRenderer renderer,
        SpectrumAudioRenderer audio,
        SpectrumKeyboard keyboard,
        SpectrumJoystickDevice joystick,
        SpectrumEarInputDevice earInput)
    {
        Model = model;
        Memory = memory;
        Ports = ports;
        Renderer = renderer;
        Audio = audio;
        Keyboard = keyboard;
        Joystick = joystick;
        EarInput = earInput;
    }

    public SpectrumModel Model { get; }
    public SpectrumMemory Memory { get; }
    public SpectrumPortBus Ports { get; }
    public SpectrumUlaRenderer Renderer { get; }
    public SpectrumAudioRenderer Audio { get; }
    public SpectrumKeyboard Keyboard { get; }
    public SpectrumJoystickDevice Joystick { get; }
    public SpectrumEarInputDevice EarInput { get; }
}
