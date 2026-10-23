using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Spectrum.Tape;
using ZedExEss.Spectrum.Video;
using ZedExEss.Z80CPU;
using ZedExEss.Machines;

namespace ZedExEss.Spectrum.Core;

/// <summary>
/// The portable object graph for one running Spectrum model.
/// </summary>
/// <remarks>
/// Execution drivers and presentation surfaces deliberately live in the host. The same machine
/// can therefore be driven by WinMM, an Avalonia audio backend, turbo execution, or diagnostics
/// without rebuilding its timing-sensitive device graph differently.
/// </remarks>
public sealed class SpectrumMachine : IEmulatedMachine
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
        SpectrumAyDevice? ayDevice,
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
        AyDevice = ayDevice;
        Cpu = cpu;
        Emulator = emulator;
    }

    public SpectrumModel Model { get; }
    public MachineDescriptor Descriptor => SpectrumMachineDescriptors.ForModel(Model);
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
    public SpectrumAyDevice? AyDevice { get; }
    public Z80 Cpu { get; }
    public SpectrumEmulator Emulator { get; }

    /// <summary>The fitted Multiface, or null when none is.</summary>
    public Multiface.SpectrumMultifaceDevice? Multiface { get; internal set; }

    /// <summary>Presses the Multiface button. False when there is nothing to press.</summary>
    /// <remarks>
    /// The NMI is asserted here but nothing is paged until the CPU fetches the vector,
    /// so the machine's own ROM still services the interrupt first.
    /// </remarks>
    public bool PressMultifaceButton()
    {
        Multiface.SpectrumMultifaceDevice? multiface = Multiface;
        if (multiface == null || !multiface.RequestNmi())
        {
            return false;
        }

        Cpu.Z80GenNMI();
        return true;
    }

    /// <summary>
    /// The contention tables in use, which can be switched between the early and late
    /// models while the machine runs.
    /// </summary>
    public Memory.SpectrumContentionProfile? Contention { get; internal set; }

    /// <summary>Captures what the machine writes to its MIC line, when armed.</summary>
    public Tape.SpectrumTapeRecorder TapeRecorder { get; internal set; } = new();

    /// <summary>True when this machine can switch to its 7 MHz clock.</summary>
    public bool SupportsTurbo => SpectrumModelTraits.SupportsTurboClock(Model);

    /// <summary>
    /// The 7 MHz CPU clock. On a real Scorpion this is a hardware latch driven by the
    /// front-panel button or by the service ROM, not a writable I/O bit.
    /// </summary>
    public bool TurboEnabled
    {
        get => Emulator.CpuTstatesPerUlaTstate > 1;
        set
        {
            if (!SupportsTurbo)
            {
                return;
            }

            Emulator.CpuTstatesPerUlaTstate = value ? 2 : 1;
        }
    }

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
