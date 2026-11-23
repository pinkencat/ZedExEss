using ZedExEss.Spectrum.Input;
using ZedExEss.ZxNext.Audio;
using ZedExEss.ZxNext.Dma;
using ZedExEss.ZxNext.DivMmc;
using ZedExEss.ZxNext.Firmware;
using ZedExEss.ZxNext.Input;
using ZedExEss.ZxNext.Memory;
using ZedExEss.ZxNext.Multiface;
using ZedExEss.ZxNext.Ports;
using ZedExEss.ZxNext.Video;

namespace ZedExEss.ZxNext.Core;

/// <summary>Owns construction and wiring of the portable Next machine graph.</summary>
public static class ZxNextMachineFactory
{
    public static ZxNextMachine CreateFoundationMachine(int sampleRate = 44_100)
        => CreateFoundationMachineCore(sampleRate);

    public static ZxNextMachine CreateFoundationMachine(
        ZxNextSystemAssets assets,
        int sampleRate = 44_100)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ZxNextMachine machine = CreateFoundationMachineCore(sampleRate);
        assets.ApplyTo(machine);
        return machine;
    }

    private static ZxNextMachine CreateFoundationMachineCore(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var clock = new ZxNextClockCoordinator();
        var physicalMemory = new ZxNextPhysicalMemory();
        var divMmc = new ZxNextDivMmcDevice();
        var multiface = new ZxNextMultifaceDevice();
        var layer2 = new ZxNextLayer2Device();
        var tilemap = new ZxNextTilemapDevice();
        var sprites = new ZxNextSpriteDevice();
        var memoryBus = new ZxNextMemoryBus(physicalMemory, divMmc, multiface, layer2);
        var videoMemoryArbiter = new ZxNextVideoMemoryArbiter(memoryBus);
        var dma = new ZxNextDmaDevice(memoryBus);
        var busArbiter = new ZxNextBusArbiter(dma);
        var paging = new ZxNextPagingController(memoryBus);
        var keyboard = new SpectrumKeyboard();
        var joystick = new ZxNextJoystickDevice(keyboard);
        var mouse = new ZxNextMouseDevice();
        var audio = new ZxNextAudioDevice(sampleRate);
        var palettes = new ZxNextPaletteDevice();
        var registers = new ZxNextRegisterFile(
            clock,
            memoryBus,
            paging,
            divMmc,
            multiface,
            joystick,
            audio,
            palettes,
            layer2,
            tilemap,
            sprites);
        memoryBus.ConfigureNmi(registers.Nmi);
        var portBus = new ZxNextPortBus(
            registers,
            paging,
            divMmc,
            multiface,
            keyboard,
            joystick,
            mouse,
            audio,
            layer2,
            sprites,
            dma);
        tilemap.ConfigureTextModeTransparency(palettes, layer2);
        dma.ConfigurePorts(portBus);
        dma.ConfigureLayer2(layer2);
        dma.ConfigureClock(clock);
        var cpu = new ZxNextCpu(memoryBus, portBus, registers, divMmc, multiface);
        registers.ConfigureCpu(cpu);
        var ulaTiming = new ZxNextUlaTiming(clock, registers, cpu, registers.Interrupts);
        registers.ConfigureUlaTiming(ulaTiming);
        registers.Interrupts.Configure(cpu, ulaTiming, clock);
        cpu.ConfigureInterrupts(registers.Interrupts);
        dma.ConfigureInterrupts(registers.Interrupts);
        var contention = new ZxNextContentionController(clock, registers, ulaTiming);
        var floatingBus = new ZxNextFloatingBus(ulaTiming, paging);
        var video = new ZxNextVideoRenderer(
            memoryBus,
            paging,
            portBus,
            registers,
            palettes,
            layer2,
            tilemap,
            sprites,
            videoMemoryArbiter,
            ulaTiming,
            floatingBus);
        var machine = new ZxNextMachine(
            sampleRate,
            clock,
            registers,
            memoryBus,
            paging,
            divMmc,
            multiface,
            keyboard,
            joystick,
            mouse,
            audio,
            portBus,
            ulaTiming,
            contention,
            floatingBus,
            layer2,
            tilemap,
            sprites,
            dma,
            busArbiter,
            videoMemoryArbiter,
            video,
            cpu);

        tilemap.EnabledStateChanged = machine.OnTilemapEnabledStateChanged;
        cpu.Z80Init();
        cpu.ConfigureTstateConsumer(machine);
        cpu.ConfigureNoMreqContention(memoryBus, contention);
        cpu.ConfigureIoContention(enabled: true);
        memoryBus.ConfigureTiming(cpu, contention, clock);
        memoryBus.ConfigureFloatingBus(floatingBus);
        portBus.ConfigureTiming(cpu, contention, ulaTiming, memoryBus);
        portBus.ConfigureFloatingBus(floatingBus);
        return machine;
    }
}
