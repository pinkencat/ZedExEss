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

    // Set when a DMA that never lets go was cut at a frame edge inside an instruction:
    // the rest of that instruction's machine cycles leave the DMA alone.
    private bool _dmaDeferredToBoundary;

    // Set while the DMA runs from inside an instruction (between two of its machine cycles),
    // where a reset or an NMI must wait for the instruction to end.
    private bool _dmaInsideInstruction;

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
        ZxNextMouseDevice mouse,
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
        Mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));
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
        Registers.Ctc.ZeroCount = (channel, tick) =>
            Registers.Interrupts.Request((ZxNextInterruptSource)((int)ZxNextInterruptSource.Ctc0 + channel), tick);
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

    /// <summary>
    /// The Kempston mouse. Its counters survive soft and hard resets, as on the hardware,
    /// where only a power-on clears them.
    /// </summary>
    public ZxNextMouseDevice Mouse { get; }
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

    /// <summary>
    /// Puts the CPU at <paramref name="speed"/> at once, as if it had long been in effect: for
    /// loaders and diagnostics setting a machine up between runs. A guest's NextReg $07 write
    /// instead waits for the next idle CPU edge and the clock handover.
    /// </summary>
    public void ForceCpuSpeed(ZxNextCpuSpeed speed)
    {
        Cpu.SynchronizeBatchedInstructionTstates();
        Clock.ForceCpuSpeed(speed, UlaTiming.FrameMasterTick);
        Cpu.ResynchronizeBatchDeadline();
    }

    public void StepInstruction()
    {
        if (IsPaused)
        {
            return;
        }

        _dmaDeferredToBoundary = false;

        if (DmaGrantDue)
        {
            DrainDmaBusRequest();
            return;
        }

        // A NextReg $07 write that landed between instructions (the Copper's, a host's) is
        // latched on the instruction's first edge, which has no bus cycle under way.
        if (Clock.SpeedLatchPending)
        {
            Clock.LatchRequestedSpeed();
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
            if (DmaGrantDue)
            {
                DrainDmaBusRequest();
                if (Dma.HoldsBusIndefinitely)
                {
                    // The CPU is locked out for good; no T-state of its will come.
                    break;
                }

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

    /// <summary>
    /// Host channels: mono by default; the desktop hosts ask for stereo, the two sides of
    /// audio_mixer.vhd interleaved.
    /// </summary>
    public int Channels => Audio.OutputChannels;

    /// <summary>Whether <see cref="ReadSamples"/> gives interleaved stereo rather than mono.</summary>
    public bool StereoAudio
    {
        get => Audio.OutputChannels == 2;
        set => Audio.SetOutputChannels(value ? 2 : 1);
    }

    /// <summary>Audio-driven execution on the fixed master timeline.</summary>
    /// <remarks>In stereo <paramref name="count"/> counts samples, two to a frame, so it must be even.</remarks>
    public int ReadSamples(short[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count % Channels != 0)
        {
            throw new ArgumentException("A stereo read must ask for whole frames.", nameof(count));
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
        // cpu_speed latches on a CPU edge with MREQ, IORQ and M1 high and the DMA off the bus:
        // the edge that ends a machine cycle. So an OUT to $07 runs its whole I/O cycle at the
        // old speed, and NEXTREG $07 switches after its fourth M-cycle.
        if (Clock.SpeedLatchPending)
        {
            Clock.LatchRequestedSpeed();
        }

        // A burst-mode prescaler wait ends by the clock, so the CPU's time has to be
        // published before asking whether the DMA wants the bus back.
        if (_dmaDeferredToBoundary)
        {
            return;
        }

        if (Dma.HasPendingBusWork)
        {
            Cpu.SynchronizeBatchedInstructionTstates();
        }

        _dmaInsideInstruction = true;
        DrainDmaBusRequest();
        _dmaInsideInstruction = false;
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
    ///
    /// A running Copper adds the earliest tick one of its writes could land: they change
    /// NextRegs under the CPU, so batched time must reach them by the instruction boundary.
    /// </remarks>
    ulong IZ80TstateConsumer.GetNextCpuBoundarySyncTstate()
    {
        // Through a clock handover a T-state is not a fixed number of ticks, so time is
        // published at the next boundary and the deadline rebuilt on the new clock.
        if (Clock.ClockHandoverPending)
        {
            return Cpu.Cyc;
        }

        ulong nextEventTick = UlaTiming.NextEventTick;
        ZxNextCopperDevice copper = Registers.Copper;
        if (copper.IsActive)
        {
            nextEventTick = Math.Min(nextEventTick, copper.NextWriteLowerBound());
        }

        // A CTC channel with its interrupt enabled can change INT at its next zero count.
        if (Registers.Ctc.InterruptEnables != 0)
        {
            nextEventTick = Math.Min(nextEventTick, Registers.Ctc.NextEventEdge);
        }

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
        if (Clock.ClockHandoverPending && cpuTstates > 0)
        {
            CompleteClockHandover(ZxNextBusOwner.Cpu);
            cpuTstates--;
        }

        ulong masterTicks = checked(
            (ulong)cpuTstates * (ulong)Clock.MasterTicksPerCpuTstate);
        AdvanceMasterTicks(masterTicks, ZxNextBusOwner.Cpu);
    }

    /// <summary>
    /// The clock handover after cpu_speed has latched: one T-state still on the old clock, then
    /// the glitch-free BUFGMUX_1s wait for the new clock's next edge, strictly after.
    /// </summary>
    /// <remarks>
    /// Klive matched this to the simulated core at every phase of 3.5 &lt;-&gt; 28 MHz (its
    /// fidelity plan's finding F3-5, probe group S). Every clock's edges lie on the 3.5 MHz grid,
    /// which is phase-locked to hc, so the pause is measured from the frame's start.
    /// </remarks>
    private void CompleteClockHandover(ZxNextBusOwner owner)
    {
        AdvanceMasterTicks((ulong)Clock.MasterTicksPerCpuTstate, owner);
        ulong pause = Clock.CompleteClockHandover(UlaTiming.FrameMasterTick);
        AdvanceMasterTicks(pause, owner);
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

    /// <remarks>
    /// The advance stops at two kinds of point on the way:
    /// <list type="bullet">
    /// <item>A NextReg $03 timing or $05 50/60 Hz change waits for the frame sync (zxnext.vhd's
    /// eff_nr_03_machine_timing, eff_nr_05_5060), so the advance stops there to switch.</item>
    /// <item>Each write of the Copper's lands at its own tick, with every device brought up to
    /// it first. The Copper runs ahead to its next write against the beam, which is fixed
    /// until a write or the frame sync changes it, and those are both stops.</item>
    /// </list>
    /// A write no device advancing with the timeline reads (NextReg $7F, the Copper's own
    /// $60-$63) lands without a stop: the CPU and the DMA read registers only at the end of
    /// a span. Atic Atac Next's delay loop is 245 MOVE $7F,0 a pass, 100,000 a frame.
    /// </remarks>
    private void AdvanceMasterTicks(ulong masterTicks, ZxNextBusOwner owner)
    {
        if (masterTicks == 0)
        {
            return;
        }

        ZxNextCopperDevice copper = Registers.Copper;
        if (!copper.IsActive && !UlaTiming.ProfileChangePending)
        {
            AdvanceTimeline(masterTicks, owner);
            return;
        }

        ulong end = Clock.MasterTicks + masterTicks;
        while (Clock.MasterTicks < end)
        {
            ulong spanEnd = end;
            bool atFrameEdge = false;
            if (UlaTiming.ProfileChangePending && UlaTiming.NextFrameTick <= spanEnd)
            {
                spanEnd = UlaTiming.NextFrameTick;
                atFrameEdge = true;
            }

            ulong writeTick = 0;
            byte register = 0;
            byte value = 0;
            bool copperWrites = false;
            while (copper.IsActive && copper.TryRunToNextWrite(spanEnd, out writeTick, out register, out value))
            {
                if (!ZxNextRegisterFile.CopperWriteLandsWithoutDevices(register))
                {
                    copperWrites = true;
                    break;
                }

                Registers.WriteFromCopper(register, value, writeTick);
            }

            if (copperWrites && writeTick < spanEnd)
            {
                spanEnd = writeTick;
                atFrameEdge = false;
            }

            AdvanceTimeline(spanEnd - Clock.MasterTicks, owner);
            if (atFrameEdge)
            {
                UlaTiming.ApplyPendingProfileAtFrameEdge();
            }

            if (copperWrites)
            {
                Registers.WriteFromCopper(register, value, writeTick);

                // A line interrupt the write moved is rescheduled before time goes on.
                UlaTiming.Advance();
            }
        }
    }

    private void AdvanceTimeline(ulong masterTicks, ZxNextBusOwner owner)
    {
        if (masterTicks == 0)
        {
            return;
        }

        Audio.AdvanceMasterTicks(masterTicks);
        int completedFrames = Video.AdvanceMasterTicks(masterTicks);
        Clock.AdvanceMasterTicks(masterTicks);

        // The CTC's zero counts request interrupts at their own edges.
        ZxNextCtcDevice ctc = Registers.Ctc;
        if (ctc.NextEventEdge <= Clock.MasterTicks)
        {
            ctc.Advance(Clock.MasterTicks);
        }

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

    /// <summary>
    /// True when the DMA holds the bus, or its request is old enough for the CPU to grant it
    /// at the end of the machine cycle that has just finished.
    /// </summary>
    private bool DmaGrantDue => Dma.BusRequestAsserted
        && (Dma.BusGranted || Clock.MasterTicks >= Dma.GrantEligibleTick);

    /// <summary>Advances one granted DMA source or destination bus cycle.</summary>
    /// <remarks>
    /// Grants the bus whenever the DMA asks; the machine's own callers wait for the
    /// request to become due first (<see cref="DmaGrantDue"/>).
    /// </remarks>
    internal bool StepBusArbitration()
    {
        if (!BusArbiter.TryGrantDma(Cpu.Cyc))
        {
            BusArbiter.ReleaseDma();
            return false;
        }

        Dma.OnBusGranted();

        // The DMA is clocked by the CPU clock, which hands over whoever has the bus.
        if (Clock.ClockHandoverPending)
        {
            CompleteClockHandover(ZxNextBusOwner.Dma);
        }

        int cpuClockCycles = Dma.CpuClockCyclesUntilBusEvent;
        ulong masterTicks = checked(
            (ulong)cpuClockCycles * (ulong)Clock.MasterTicksPerCpuTstate);
        AdvanceMasterTicks(masterTicks, ZxNextBusOwner.Dma);
        Dma.CompleteGrantedBusCycle();

        // The bus goes back before anything can make the CPU run: an NMI raised during the
        // DMA's cycles (by the Copper, or a button) is serviced once the DMA lets go.
        if (!Dma.BusRequestAsserted || !Dma.BusGranted)
        {
            BusArbiter.ReleaseDma();
        }

        // Between instructions a reset or an NMI is taken here. Inside one, the CPU finishes
        // the instruction first and StepInstruction applies them after it.
        if (!_dmaInsideInstruction)
        {
            ApplyPendingRequests();
        }

        return true;
    }

    /// <remarks>
    /// A transfer that never gives the bus back (auto restart without burst pacing) is run
    /// one frame at a time, so the frame loop and the host keep going while the CPU stays
    /// locked out. Should that happen inside an instruction, the bus is handed back for the
    /// rest of it, and no more of its cycles drain the DMA until the instruction ends: the
    /// one place the CPU gets time the hardware would not give it.
    /// </remarks>
    private void DrainDmaBusRequest()
    {
        ulong frame = UlaTiming.FrameNumber;
        while (!IsPaused && DmaGrantDue)
        {
            if (!StepBusArbitration())
            {
                break;
            }

            if (UlaTiming.FrameNumber != frame && Dma.HoldsBusIndefinitely)
            {
                BusArbiter.ReleaseDma();
                _dmaDeferredToBoundary = true;
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

        // A latched NMI reaches the CPU at the end of the instruction it is in, or once the
        // DMA gives the bus back; the FPGA holds /NMI low until $0066 is fetched.
        ZxNextNmiController nmi = Registers.Nmi;
        nmi.Settle();
        if (nmi.AwaitingCpu && !Dma.BusRequestAsserted)
        {
            Cpu.RequestNmi(nmi.SignalCpu());
            Cpu.ServicePendingInterruptsAtBoundary();
        }
    }
}
