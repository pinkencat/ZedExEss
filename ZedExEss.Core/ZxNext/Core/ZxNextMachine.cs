using ZedExEss.Machines;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Input;
using ZedExEss.Z80CPU;
using ZedExEss.ZxNext.Audio;
using ZedExEss.ZxNext.Dma;
using ZedExEss.ZxNext.DivMmc;
using ZedExEss.ZxNext.Input;
using ZedExEss.ZxNext.Memory;
using ZedExEss.ZxNext.Multiface;
using ZedExEss.ZxNext.Ports;
using ZedExEss.ZxNext.Video;

namespace ZedExEss.ZxNext.Core;

/// <summary>Portable ZX Spectrum Next machine graph and master-clock owner.</summary>
public sealed class ZxNextMachine : IEmulatedMachine, IAudioSource, IZ80TstateConsumer
{
    private bool _publishCompletedFrames = true;
    private bool _instructionTstateBatchingEnabled;
    private bool _useAdaptiveInstructionTstateBatching = true;
    private Func<bool>? _beforeCpuStep;
    private Action? _afterCpuStep;
    private bool _hasBeforeCpuStep;
    private bool _hasAfterCpuStep;

    internal ZxNextMachine(
        int sampleRate,
        ZxNextClockCoordinator clock,
        ZxNextRegisterFile registers,
        ZxNextMemoryBus memoryBus,
        ZxNextPagingController paging,
        ZxNextDivMmcDevice divMmc,
        ZxNextMultifaceDevice multiface,
        SpectrumKeyboard keyboard,
        ZxNextJoystickDevice joystick,
        ZxNextAudioDevice audio,
        ZxNextPortBus portBus,
        ZxNextUlaTiming ulaTiming,
        ZxNextContentionController contention,
        ZxNextFloatingBus floatingBus,
        ZxNextLayer2Device layer2,
        ZxNextTilemapDevice tilemap,
        ZxNextSpriteDevice sprites,
        ZxNextDmaDevice dma,
        ZxNextBusArbiter busArbiter,
        ZxNextVideoMemoryArbiter videoMemoryArbiter,
        ZxNextVideoRenderer video,
        ZxNextCpu cpu)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        SampleRate = sampleRate;
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Registers = registers ?? throw new ArgumentNullException(nameof(registers));
        MemoryBus = memoryBus ?? throw new ArgumentNullException(nameof(memoryBus));
        Paging = paging ?? throw new ArgumentNullException(nameof(paging));
        DivMmc = divMmc ?? throw new ArgumentNullException(nameof(divMmc));
        Multiface = multiface ?? throw new ArgumentNullException(nameof(multiface));
        Keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        Joystick = joystick ?? throw new ArgumentNullException(nameof(joystick));
        Audio = audio ?? throw new ArgumentNullException(nameof(audio));
        PortBus = portBus ?? throw new ArgumentNullException(nameof(portBus));
        UlaTiming = ulaTiming ?? throw new ArgumentNullException(nameof(ulaTiming));
        Contention = contention ?? throw new ArgumentNullException(nameof(contention));
        FloatingBus = floatingBus ?? throw new ArgumentNullException(nameof(floatingBus));
        Layer2 = layer2 ?? throw new ArgumentNullException(nameof(layer2));
        Tilemap = tilemap ?? throw new ArgumentNullException(nameof(tilemap));
        Sprites = sprites ?? throw new ArgumentNullException(nameof(sprites));
        Dma = dma ?? throw new ArgumentNullException(nameof(dma));
        BusArbiter = busArbiter ?? throw new ArgumentNullException(nameof(busArbiter));
        VideoMemoryArbiter = videoMemoryArbiter
            ?? throw new ArgumentNullException(nameof(videoMemoryArbiter));
        Video = video ?? throw new ArgumentNullException(nameof(video));
        Cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));

        // A speed change revalues every batched T-state, so time is published at the old
        // rate before the change and the deadline is rebuilt on the new one after it.
        Clock.CpuSpeedChanging = Cpu.SynchronizeBatchedInstructionTstates;
        Clock.CpuSpeedChanged = Cpu.ResynchronizeBatchDeadline;
    }

    public MachineDescriptor Descriptor => ZxNextModelDescriptors.Native;
    public int SampleRate { get; }
    public int CpuClockHz => Clock.CpuClockHz;
    public ZxNextClockCoordinator Clock { get; }
    public ZxNextRegisterFile Registers { get; }
    public ZxNextMemoryBus MemoryBus { get; }
    public ZxNextPagingController Paging { get; }
    public ZxNextDivMmcDevice DivMmc { get; }
    public ZxNextMultifaceDevice Multiface { get; }
    public SpectrumKeyboard Keyboard { get; }
    public ZxNextJoystickDevice Joystick { get; }
    public ZxNextAudioDevice Audio { get; }
    public ZxNextPortBus PortBus { get; }
    public ZxNextUlaTiming UlaTiming { get; }
    public ZxNextContentionController Contention { get; }
    public ZxNextFloatingBus FloatingBus { get; }
    public ZxNextLayer2Device Layer2 { get; }
    public ZxNextTilemapDevice Tilemap { get; }
    public ZxNextSpriteDevice Sprites { get; }
    public ZxNextDmaDevice Dma { get; }
    public ZxNextBusArbiter BusArbiter { get; }
    public ZxNextVideoMemoryArbiter VideoMemoryArbiter { get; }
    public ZxNextVideoRenderer Video { get; }
    public ZxNextCpu Cpu { get; }
    public int FrameWidth => ZxNextVideoRenderer.FrameWidth;
    public int FrameHeight => ZxNextVideoRenderer.FrameHeight;
    public ulong MasterTicks => Clock.MasterTicks;
    public ZxNextCpuSpeed CpuSpeed => Clock.CpuSpeed;
    public bool IsPaused { get; private set; }
    internal bool UseAdaptiveInstructionTstateBatching
    {
        get => _useAdaptiveInstructionTstateBatching;
        set
        {
            if (value == _useAdaptiveInstructionTstateBatching)
            {
                return;
            }

            _useAdaptiveInstructionTstateBatching = value;
            SetInstructionTstateBatching(value && Tilemap.Enabled);
        }
    }
    public ulong ResetSequence { get; private set; }
    public ZxNextResetKind LastResetKind { get; private set; } = ZxNextResetKind.Hard;

    public event Action? FrameCompleted;

    /// <summary>
    /// Installs per-instruction hooks, matching the Spectrum contract: returning true
    /// from <paramref name="beforeCpuStep"/> means the hook has taken this slot and the
    /// instruction must not execute.
    /// </summary>
    /// <remarks>
    /// Pass null for both when nothing is watching. This runs for every instruction, so
    /// the delegates are removed rather than left as empty callbacks.
    /// </remarks>
    public void ConfigureCpuStepHooks(Func<bool>? beforeCpuStep, Action? afterCpuStep)
    {
        _beforeCpuStep = beforeCpuStep;
        _hasBeforeCpuStep = beforeCpuStep != null;
        _afterCpuStep = afterCpuStep;
        _hasAfterCpuStep = afterCpuStep != null;
    }

    /// <summary>Executes one Z80 or Z80N instruction.</summary>
    /// <summary>
    /// Publishes any instruction time the CPU is still holding.
    /// </summary>
    /// <remarks>
    /// Batched time has elapsed but has not yet reached the clk28 timeline, so anything
    /// that reads <see cref="MasterTicks"/> and expects it to account for the instruction
    /// just executed has to ask for it first. Emulation never needs this - devices are
    /// given their time before they can observe it - but a debugger, a check or a
    /// diagnostic reading the clock between instructions does.
    /// </remarks>
    public void SynchronizeTime() => Cpu.SynchronizeBatchedInstructionTstates();

    public void StepInstruction()
    {
        if (IsPaused)
        {
            return;
        }

        if (Dma.BusRequestAsserted)
        {
            DrainDmaBusRequest();
            return;
        }

        if (_hasBeforeCpuStep)
        {
            Func<bool>? beforeCpuStep = _beforeCpuStep;
            if (beforeCpuStep != null && beforeCpuStep())
            {
                return;
            }
        }

        Cpu.Z80Step();
        if (_hasAfterCpuStep)
        {
            _afterCpuStep?.Invoke();
        }

        ApplyPendingRequests();
        DrainDmaBusRequest();
    }

    public void RunForTstates(int minimumTstates)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumTstates);
        ulong consumed = 0;
        while (!IsPaused && consumed < (ulong)minimumTstates)
        {
            if (Dma.BusRequestAsserted)
            {
                DrainDmaBusRequest();
                continue;
            }

            ulong before = Cpu.Cyc;
            StepInstruction();
            consumed += Cpu.Cyc - before;
            if (Cpu.Cyc == before)
            {
                // A hook took the slot without executing anything; leave rather than
                // spin waiting for a T-state that is never going to be consumed.
                break;
            }
        }
    }

    /// <summary>Audio-driven execution on the fixed master timeline.</summary>
    public int ReadSamples(short[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (IsPaused)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        Audio.BeginCapture();
        int total = Audio.DrainSamples(buffer, offset, count);
        while (total < count && !IsPaused)
        {
            StepInstruction();
            total += Audio.DrainSamples(buffer, offset + total, count - total);
        }

        if (total < count)
        {
            Array.Clear(buffer, offset + total, count - total);
        }

        return count;
    }

    /// <summary>Runs until the current video frame completes.</summary>
    public void RunFrame(bool presentFrame = true)
    {
        if (IsPaused)
        {
            return;
        }

        ulong initialFrame = UlaTiming.FrameNumber;
        bool previousPublishState = _publishCompletedFrames;
        _publishCompletedFrames = presentFrame;
        try
        {
            while (!IsPaused && UlaTiming.FrameNumber == initialFrame)
            {
                StepInstruction();
            }
        }
        finally
        {
            _publishCompletedFrames = previousPublishState;
        }
    }

    /// <summary>
    /// Tells whatever is drawing to redraw, without running the machine.
    /// </summary>
    /// <remarks>
    /// Restoring a state replaces the picture without producing a frame edge, so nothing
    /// would reach the screen until emulation was resumed - which is exactly when the user
    /// needs to see where they have landed.
    /// </remarks>
    public void PublishFrame() => FrameCompleted?.Invoke();

    public bool TryCopyFrame(int[] destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Video.CopyFrame(destination);
        return Video.CompletedFrameNumber > 0;
    }

    public void SetPaused(bool paused) => IsPaused = paused;

    /// <summary>Attaches the desktop joystick mapping to connector 1.</summary>
    public void SetHostJoystickType(SpectrumJoystickType type)
    {
        Joystick.SetHostType(type);
        if (type != SpectrumJoystickType.None)
        {
            Registers.ConfigureHostJoystickMode(ZxNextJoystickDevice.FromHostType(type));
        }
    }

    /// <summary>Performs a hard reset.</summary>
    public void Reset() => Reset(ZxNextResetKind.Hard);

    public void Reset(ZxNextResetKind kind)
    {
        Cpu.Z80Init();
        Clock.Reset();
        Registers.Reset(kind);
        Multiface.Reset();
        Dma.Reset();
        BusArbiter.Reset();
        VideoMemoryArbiter.Reset();
        UlaTiming.Reset();
        PortBus.Reset();
        Video.Reset();
        LastResetKind = kind;
        ResetSequence++;
        IsPaused = false;
    }

    void IZ80TstateConsumer.OnTstatesConsumed(int tstates) => AdvanceMasterTime(tstates);

    void IZ80TstateConsumer.OnTapeLoadTstatesSkipped(int tstates) => AdvanceMasterTime(tstates);

    void IZ80TstateConsumer.OnMachineCycleCompleted()
    {
        if (Dma.BusRequestAsserted)
        {
            Cpu.SynchronizeBatchedInstructionTstates();
        }

        DrainDmaBusRequest();
    }

    /// <summary>
    /// The next CPU T-state at which batched instruction time has to be published.
    /// </summary>
    /// <remarks>
    /// Only instruction-boundary state needs a deadline here: the frame edge and the
    /// interrupt line, which is what <see cref="ZxNextUlaTiming.NextEventTick"/> tracks.
    /// Memory and I/O devices add their own, earlier, barriers at their exact sample and
    /// latch points, and the video needs none at all because it subdivides whatever span
    /// it is finally handed.
    ///
    /// This used to return <c>Cpu.Cyc</c>, which made every instruction boundary a
    /// barrier and accounted for most of the remaining flushes. The division floors, so a
    /// deadline is never later than the event it is protecting.
    /// </remarks>
    ulong IZ80TstateConsumer.GetNextCpuBoundarySyncTstate()
    {
        ulong nextEventTick = UlaTiming.NextEventTick;
        ulong publishedTick = Clock.MasterTicks;
        if (nextEventTick <= publishedTick)
        {
            return Cpu.Cyc;
        }

        ulong remainingTstates =
            (nextEventTick - publishedTick) / (ulong)Clock.MasterTicksPerCpuTstate;
        return Cpu.Cyc + remainingTstates;
    }

    private void AdvanceMasterTime(int cpuTstates)
    {
        ulong masterTicks = checked(
            (ulong)cpuTstates * (ulong)Clock.MasterTicksPerCpuTstate);
        AdvanceMasterTicks(masterTicks, ZxNextBusOwner.Cpu);
    }

    internal void OnTilemapEnabledStateChanged(bool enabled)
    {
        if (_useAdaptiveInstructionTstateBatching)
        {
            SetInstructionTstateBatching(enabled);
        }
    }

    private void SetInstructionTstateBatching(bool enabled)
    {
        if (enabled == _instructionTstateBatchingEnabled)
        {
            return;
        }

        Cpu.ConfigureInstructionTstateBatching(enabled);
        _instructionTstateBatchingEnabled = enabled;
    }

    private void AdvanceMasterTicks(ulong masterTicks, ZxNextBusOwner owner)
    {
        if (masterTicks == 0)
        {
            return;
        }

        UlaTiming.SynchronizeProfile();
        Audio.AdvanceMasterTicks(masterTicks);
        int completedFrames = Video.AdvanceMasterTicks(masterTicks);
        Clock.AdvanceMasterTicks(masterTicks);
        if (owner == ZxNextBusOwner.Cpu)
        {
            BusArbiter.RecordCpuMasterTicks(masterTicks);
        }
        else
        {
            BusArbiter.RecordDmaMasterTicks(masterTicks);
        }

        UlaTiming.Advance();
        if (_publishCompletedFrames)
        {
            for (int frame = 0; frame < completedFrames; frame++)
            {
                FrameCompleted?.Invoke();
            }
        }
    }

    /// <summary>Advances one granted DMA source or destination bus cycle.</summary>
    internal bool StepBusArbitration()
    {
        if (!BusArbiter.TryGrantDma(Cpu.Cyc))
        {
            BusArbiter.ReleaseDma();
            return false;
        }

        int cpuClockCycles = Dma.CpuClockCyclesUntilBusEvent;
        ulong masterTicks = checked(
            (ulong)cpuClockCycles * (ulong)Clock.MasterTicksPerCpuTstate);
        AdvanceMasterTicks(masterTicks, ZxNextBusOwner.Dma);
        Dma.CompleteGrantedBusCycle();
        ApplyPendingRequests();
        if (!Dma.BusRequestAsserted)
        {
            BusArbiter.ReleaseDma();
        }

        return true;
    }

    private void DrainDmaBusRequest()
    {
        while (!IsPaused && Dma.BusRequestAsserted)
        {
            if (!StepBusArbitration())
            {
                break;
            }
        }
    }

    private void ApplyPendingRequests()
    {
        if (Registers.TryTakeResetRequest(out ZxNextResetKind kind))
        {
            Reset(kind);
            return;
        }

        if (Dma.BusRequestAsserted || DivMmc.NmiActive || Multiface.NmiActive)
        {
            return;
        }

        if (Registers.TryTakeMultifaceNmiRequest() && Multiface.BeginNmi())
        {
            Cpu.RequestNmi(ZxNextNmiSource.Multiface);
            Cpu.ServicePendingInterruptsAtBoundary();
        }
        else if (Registers.TryTakeDivMmcNmiRequest())
        {
            Cpu.RequestNmi(ZxNextNmiSource.DivMmc);
            Cpu.ServicePendingInterruptsAtBoundary();
        }
    }
}
