using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Spectrum.Tape;
using ZedExEss.Spectrum.Video;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Core;

/// <summary>Builds the canonical CPU/device/timing graph shared by desktop and headless hosts.</summary>
public static class SpectrumMachineFactory
{
    public static SpectrumMachine Create(SpectrumMachineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Roms);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SampleRate);
        ArgumentOutOfRangeException.ThrowIfNegative(options.AyOutputAmplitude);

        SpectrumModel model = options.Model;
        var memory = new SpectrumMemory(model, options.Roms);
        var ports = new SpectrumPortBus(model, contendedPages: memory);
        var renderer = new SpectrumUlaRenderer(model, memory)
        {
            RenderEnabled = options.RenderEnabled
        };
        var audio = new SpectrumAudioRenderer(SpectrumAudioTiming.CpuClockHz(model), options.SampleRate);
        var keyboard = new SpectrumKeyboard();
        var joystick = new SpectrumJoystickDevice(keyboard)
        {
            Type = options.JoystickType
        };
        var earInput = new SpectrumEarInputDevice(audio);

        SpectrumEmulator? emulator = null;
        var ula = new SpectrumUla(
            model,
            renderer,
            keyboard,
            earInput,
            audio,
            audio,
            () => emulator?.SyncToCpu());
        ports.AddDevice(ula);
        ports.AddDevice(joystick);

        if (SpectrumModelTraits.SupportsPaging(model))
        {
            ports.AddDevice(new SpectrumPagingDevice(memory, SpectrumModelTraits.PagingPortMode(model)));
        }

        AY38912? ayChip = null;
        if (SpectrumAudioTiming.HasAy(model))
        {
            ayChip = new AY38912(
                SpectrumAudioTiming.AyClockHz(model),
                options.SampleRate,
                options.AyOutputAmplitude);
            ports.AddDevice(new SpectrumAyDevice(ayChip));
            audio.AttachAy(ayChip);
        }

        options.ConfigureDevices?.Invoke(new SpectrumMachineConfigurationContext(
            model,
            memory,
            ports,
            renderer,
            audio,
            keyboard,
            joystick,
            earInput));

        var cpu = new Z80(memory, ports);
        cpu.Z80Init();

        SpectrumTimingModel machineTiming = SpectrumTimingModel.ForModel(model);
        IContentionProfile contention = SpectrumContentionProfile.Create(model);
        cpu.ConfigureNoMreqContention(memory, contention);
        cpu.ConfigureIoContention(true, machineTiming.IoWritesLatchAtEndOfCycle);
        memory.ConfigureTiming(cpu, contention);
        ports.ConfigureTiming(cpu, contention, memory);
        ports.ConfigureFloatingBus(new SpectrumFloatingBus(model, memory));
        earInput.ConfigureAcceleration(cpu, memory, ports.WriteUncontended);

        emulator = new SpectrumEmulator(
            cpu,
            memory,
            ports,
            renderer,
            audio,
            options.BeforeCpuStep)
        {
            ForceFullFrameCopy = options.ForceFullFrameCopy
        };

        return new SpectrumMachine(
            model,
            options.SampleRate,
            memory,
            ports,
            renderer,
            audio,
            keyboard,
            joystick,
            earInput,
            ula,
            ayChip,
            cpu,
            emulator);
    }
}
