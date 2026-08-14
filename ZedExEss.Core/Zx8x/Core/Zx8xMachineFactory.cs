using ZedExEss.Zx8x.Audio;
using ZedExEss.Zx8x.Input;
using ZedExEss.Zx8x.Memory;
using ZedExEss.Zx8x.Tape;
using ZedExEss.Zx8x.Video;

namespace ZedExEss.Zx8x.Core;

/// <summary>Builds a fully connected portable ZX80/ZX81 machine graph.</summary>
public static class Zx8xMachineFactory
{
    public static Zx8xMachine Create(
        Zx8xModel model,
        string romDirectory,
        Zx81RomRevision romRevision = Zx81RomRevision.Standard,
        Zx8xRamConfiguration ramConfiguration = Zx8xRamConfiguration.Expansion16K,
        Zx8xHighResolutionMode highResolutionMode = Zx8xHighResolutionMode.Sinclair,
        bool is50Hz = true,
        int sampleRate = 44_100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(romDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(model, romRevision);
        string romPath = Path.Combine(Path.GetFullPath(romDirectory), descriptor.FileName);
        Zx8xRomImage rom = Zx8xRomImage.Load(romPath, descriptor);
        return Create(model, rom, romRevision, ramConfiguration, highResolutionMode, is50Hz, sampleRate);
    }

    public static Zx8xMachine Create(
        Zx8xModel model,
        Zx8xRomImage rom,
        Zx81RomRevision romRevision = Zx81RomRevision.Standard,
        Zx8xRamConfiguration ramConfiguration = Zx8xRamConfiguration.Expansion16K,
        Zx8xHighResolutionMode highResolutionMode = Zx8xHighResolutionMode.Sinclair,
        bool is50Hz = true,
        int sampleRate = 44_100)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var memory = new Zx8xMemory(model, rom, ramConfiguration);
        var keyboard = new Zx8xKeyboard();
        var io = new Zx8xIoDevice(model, keyboard) { Is50Hz = is50Hz };
        var cassette = new Zx8xCassetteDevice(io);
        var tape = new Zx8xTapeSession(cassette);
        var audio = new Zx8xAudioRenderer(Zx8xModelDescriptors.CpuClockHz, sampleRate);
        var memoryBus = new Zx8xCpuMemoryBus(memory);
        var portBus = new Zx8xCpuPortBus(io);
        var cpu = new Zx8xCpu(memoryBus, portBus);
        Zx8xVideoTiming timing = Zx8xVideoTiming.ForRegion(is50Hz);
        var videoTiming = new Zx8xVideoTimingController(model, io, timing);
        var renderer = new Zx8xMonochromeRenderer(memory, timing, highResolutionMode);

        cassette.ConfigureOutputSink(audio);
        cassette.OutputLevelChanged += renderer.OnCassetteOutputLevelChanged;
        // The host monitor is useful feedback for SAVE, but replaying the ROM's
        // sync waveform while an input tape is already playing is both redundant
        // and extremely harsh. Keep LOAD silent without altering its EAR data or
        // the visual cassette raster.
        cassette.OutputActivityChanged += (tstate, active) =>
            audio.SetMonitorEnabled(active && tape.Loader?.IsPlaying != true, tstate);
        cassette.OutputActivityChanged += renderer.OnCassetteOutputActivityChanged;
        portBus.ConfigureTapeSession(tape);
        portBus.ConfigureObservers(videoTiming, cassette);
        memoryBus.ConfigureDisplaySink(videoTiming);
        videoTiming.ConfigureRasterSink(renderer);
        cpu.Z80Init();
        renderer.Reset(cpu.Cyc);

        return new Zx8xMachine(
            model,
            romRevision,
            sampleRate,
            memory,
            keyboard,
            io,
            cassette,
            tape,
            audio,
            memoryBus,
            portBus,
            cpu,
            videoTiming,
            renderer);
    }
}
