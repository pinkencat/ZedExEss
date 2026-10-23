using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Audio.GeneralSound;
using ZedExEss.Spectrum.Multiface;
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
        var joystick = new SpectrumJoystickDevice(
            keyboard,
            useDedicatedKempstonPort: SpectrumModelTraits.HasBeta128Disk(model))
        {
            Type = options.JoystickType
        };
        var earInput = new SpectrumEarInputDevice(audio);
        SpectrumTapeRecorder tapeRecorder = options.TapeRecorder ?? new SpectrumTapeRecorder();

        SpectrumEmulator? emulator = null;
        var ula = new SpectrumUla(
            model,
            renderer,
            keyboard,
            earInput,
            audio,
            audio,
            () => emulator?.SyncToCpu(),
            tapeRecorder);
        ports.AddDevice(ula);
        ports.AddDevice(joystick);

        if (SpectrumModelTraits.SupportsPaging(model))
        {
            ports.AddDevice(new SpectrumPagingDevice(memory, SpectrumModelTraits.PagingPortMode(model)));
        }

        AY38912? ayChip = null;
        SpectrumAyDevice? ayDevice = null;
        if (SpectrumAudioTiming.HasAy(model))
        {
            ayChip = new AY38912(
                SpectrumAudioTiming.AyClockHz(model),
                options.SampleRate,
                options.AyOutputAmplitude);
            ayDevice = new SpectrumAyDevice(ayChip);
            ports.AddDevice(ayDevice);
            audio.AttachAy(ayChip);
        }

        if (options.CovoxEnabled)
        {
            audio.AttachCovox();
            ports.AddDevice(new SpectrumCovoxDevice(audio));
        }

        GeneralSoundCard? generalSound = GeneralSoundCard.TryCreate(
            options.GeneralSoundFirmware.Span,
            SpectrumAudioTiming.CpuClockHz(model));
        if (generalSound != null)
        {
            ports.AddDevice(new GeneralSoundDevice(generalSound));
            audio.AttachGeneralSound(generalSound);
        }

        // A Multiface is only fitted when the requested variant is the one this model
        // takes and its ROM is the right size. Anything else leaves it unfitted rather
        // than half-configured.
        SpectrumMultifaceDevice? multiface = null;
        if (options.MultifaceModel != SpectrumMultifaceModel.None
            && options.MultifaceModel == SpectrumModelTraits.SupportedMultiface(model)
            && options.MultifaceRom.Length == SpectrumMultifaceDevice.RomSize)
        {
            multiface = new SpectrumMultifaceDevice(options.MultifaceModel, options.MultifaceRom.Span);
            memory.ConfigureMultiface(multiface);
            ports.AddDevice(new SpectrumMultifacePortDevice(multiface, memory));
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
        if (multiface != null)
        {
            // The overlay is released when the handler returns, which only the CPU sees.
            cpu.RetnObserver = multiface.OnRetnExecuted;
        }
        cpu.Z80Init();

        SpectrumTimingModel machineTiming = SpectrumTimingModel.ForModel(model);
        SpectrumContentionProfile contention = SpectrumContentionProfile.Create(model);
        contention.Timing = options.ContentionTiming;
        cpu.ConfigureNoMreqContention(memory, contention);
        cpu.ConfigureIoContention(true, machineTiming.IoWritesLatchAtEndOfCycle);
        cpu.ConfigureM1Alignment(SpectrumModelTraits.AlignsM1ToEvenTstates(model));
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

        // The card keeps its own crystal, so the emulator advances it in ULA time.
        emulator.AttachGeneralSound(generalSound);

        var machine = new SpectrumMachine(
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
            ayDevice,
            cpu,
            emulator);

        machine.Multiface = multiface;
        machine.TapeRecorder = tapeRecorder;
        machine.Contention = contention;
        tapeRecorder.Attach(() => cpu.Cyc, () => emulator.CpuTstatesPerUlaTstate);
        return machine;
    }
}
