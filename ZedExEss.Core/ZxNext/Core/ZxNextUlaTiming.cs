namespace ZedExEss.ZxNext.Core;

/// <summary>
/// Tracks the pinned core's ULA raster cadence on the absolute 28 MHz timeline
/// and drives the conventional maskable interrupt line.
/// </summary>
public sealed class ZxNextUlaTiming
{
    private readonly ZxNextClockCoordinator _clock;
    private readonly ZxNextRegisterFile _registers;
    private readonly ZxNextCpu _cpu;
    private byte _timingProfile = byte.MaxValue;
    private bool _sixtyHertz;
    private ulong _nextFrameTick;
    private ulong _nextInterruptTick;
    private ulong _nextLineInterruptTick = ulong.MaxValue;
    private ulong _interruptEndTick = ulong.MaxValue;
    private ulong _nextEventTick;
    private bool _scheduledLineInterruptEnabled;
    private ushort _scheduledLineInterruptValue;
    private int _lineInterruptGeneration;

    internal ZxNextUlaTiming(
        ZxNextClockCoordinator clock,
        ZxNextRegisterFile registers,
        ZxNextCpu cpu)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _registers = registers ?? throw new ArgumentNullException(nameof(registers));
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        Reset();
    }

    public int PixelsPerLine { get; private set; }
    public int LinesPerFrame { get; private set; }
    public ulong MasterTicksPerFrame { get; private set; }
    public ulong InterruptOffsetMasterTicks { get; private set; }
    public int InterruptPulseCpuTstates { get; private set; }
    public ulong FrameNumber { get; private set; }
    public ulong FrameStartMasterTick { get; private set; }
    public ulong InterruptCount { get; private set; }
    public ulong LineInterruptCount { get; private set; }
    public ulong LastLineInterruptMasterTick { get; private set; }
    public bool InterruptLineActive { get; private set; }
    public ZxNextUlaTimingProfile Profile { get; private set; }
    internal int ProfileGeneration { get; private set; }

    public ulong FrameMasterTick => _clock.MasterTicks - FrameStartMasterTick;

    /// <summary>The current copper-offset video line (offset zero until Copper is implemented).</summary>
    public ushort CurrentCopperLine
    {
        get
        {
            ulong rasterPosition = (FrameMasterTick % MasterTicksPerFrame)
                / ZxNextUlaTimingProfile.MasterTicksPerRasterPixel;
            int rawVertical = (int)(rasterPosition / (ulong)PixelsPerLine);
            int rawHorizontal = (int)(rasterPosition % (ulong)PixelsPerLine);
            int counterUpdateHorizontal = Profile.DisplayStartHorizontal - 12;
            int effectiveVertical = rawHorizontal >= counterUpdateHorizontal
                ? rawVertical
                : (rawVertical + LinesPerFrame - 1) % LinesPerFrame;
            return (ushort)((effectiveVertical - Profile.FirstDisplayLine + LinesPerFrame)
                % LinesPerFrame);
        }
    }

    internal void Reset()
    {
        _cpu.Z80SetINTLine(false);
        InterruptLineActive = false;
        FrameNumber = 0;
        InterruptCount = 0;
        LineInterruptCount = 0;
        LastLineInterruptMasterTick = 0;
        FrameStartMasterTick = 0;
        _scheduledLineInterruptEnabled = false;
        _scheduledLineInterruptValue = 0;
        _nextLineInterruptTick = ulong.MaxValue;
        _timingProfile = byte.MaxValue;
        _sixtyHertz = false;
        ApplyCurrentProfile(0);
    }

    internal void Advance()
    {
        SynchronizeProfile();
        SynchronizeLineInterruptConfiguration();

        ulong currentTick = _clock.MasterTicks;
        if (_nextEventTick > currentTick)
        {
            return;
        }

        while (true)
        {
            ulong nextEventTick = _nextEventTick;
            if (nextEventTick > currentTick)
            {
                break;
            }

            if (nextEventTick == _interruptEndTick)
            {
                _cpu.Z80SetINTLine(false);
                InterruptLineActive = false;
                _interruptEndTick = ulong.MaxValue;
            }

            if (nextEventTick == _nextFrameTick)
            {
                FrameNumber++;
                FrameStartMasterTick = _nextFrameTick;
                _nextFrameTick += MasterTicksPerFrame;
                _nextInterruptTick = FrameStartMasterTick + InterruptOffsetMasterTicks;
                ScheduleLineInterruptForFrame();
            }

            if (nextEventTick == _nextInterruptTick)
            {
                InterruptCount++;
                if (_registers.UlaInterruptEnabled)
                {
                    BeginInterruptPulse(nextEventTick);
                }
                _nextInterruptTick = ulong.MaxValue;
            }

            if (nextEventTick == _nextLineInterruptTick)
            {
                LineInterruptCount++;
                LastLineInterruptMasterTick = nextEventTick;
                BeginInterruptPulse(nextEventTick);
                _nextLineInterruptTick = ulong.MaxValue;
            }

            UpdateNextEventTick();
        }
    }

    internal bool SynchronizeProfile()
    {
        if (_timingProfile == _registers.MachineTiming
            && _sixtyHertz == _registers.SixtyHertzMode)
        {
            return false;
        }

        ApplyCurrentProfile(_clock.MasterTicks);
        return true;
    }

    private void ApplyCurrentProfile(ulong frameStartTick)
    {
        _cpu.Z80SetINTLine(false);
        InterruptLineActive = false;
        _interruptEndTick = ulong.MaxValue;
        _nextLineInterruptTick = ulong.MaxValue;
        _timingProfile = _registers.MachineTiming;
        _sixtyHertz = _registers.SixtyHertzMode && (_timingProfile & 0x04) == 0;
        Profile = ZxNextUlaTimingProfile.Create(_timingProfile, _sixtyHertz);
        unchecked
        {
            ProfileGeneration++;
        }

        _sixtyHertz = Profile.SixtyHertz;
        PixelsPerLine = Profile.PixelsPerLine;
        LinesPerFrame = Profile.LinesPerFrame;
        InterruptPulseCpuTstates = Profile.InterruptPulseCpuTstates;
        MasterTicksPerFrame = Profile.MasterTicksPerFrame;
        InterruptOffsetMasterTicks = Profile.InterruptOffsetMasterTicks;
        FrameStartMasterTick = frameStartTick;
        _nextFrameTick = frameStartTick + MasterTicksPerFrame;
        _nextInterruptTick = frameStartTick + InterruptOffsetMasterTicks;
        _scheduledLineInterruptEnabled = _registers.LineInterruptEnabled;
        _scheduledLineInterruptValue = _registers.LineInterruptValue;
        _lineInterruptGeneration = _registers.LineInterruptGeneration;
        ScheduleLineInterruptForFrame();
        UpdateNextEventTick();
    }

    private void SynchronizeLineInterruptConfiguration()
    {
        int generation = _registers.LineInterruptGeneration;
        if (generation == _lineInterruptGeneration)
        {
            return;
        }

        _lineInterruptGeneration = generation;
        bool enabled = _registers.LineInterruptEnabled;
        ushort value = _registers.LineInterruptValue;
        _scheduledLineInterruptEnabled = enabled;
        _scheduledLineInterruptValue = value;
        ScheduleLineInterruptForFrame();
        if (_nextLineInterruptTick <= _clock.MasterTicks)
        {
            ScheduleLineInterruptForFrame(FrameStartMasterTick + MasterTicksPerFrame);
        }

        UpdateNextEventTick();
    }

    private void ScheduleLineInterruptForFrame() =>
        ScheduleLineInterruptForFrame(FrameStartMasterTick);

    private void ScheduleLineInterruptForFrame(ulong frameStartTick)
    {
        ulong? offset = _scheduledLineInterruptEnabled
            ? Profile.GetLineInterruptOffsetMasterTicks(_scheduledLineInterruptValue)
            : null;
        _nextLineInterruptTick = offset.HasValue
            ? checked(frameStartTick + offset.Value)
            : ulong.MaxValue;
    }

    private void BeginInterruptPulse(ulong assertionTick)
    {
        if (InterruptLineActive)
        {
            return;
        }

        _cpu.Z80SetINTLine(true, 0xFF);
        InterruptLineActive = true;
        _interruptEndTick = checked(assertionTick
            + ((ulong)InterruptPulseCpuTstates
                * (ulong)_clock.MasterTicksPerCpuTstate));
    }

    private ulong CalculateNextEventTick() => Math.Min(
        _nextFrameTick,
        Math.Min(_nextInterruptTick,
            Math.Min(_nextLineInterruptTick, _interruptEndTick)));

    /// <summary>The next clk28 tick at which instruction-boundary state changes.</summary>
    internal ulong NextEventTick => _nextEventTick;

    private void UpdateNextEventTick()
    {
        _nextEventTick = CalculateNextEventTick();

        // The CPU batches instruction time against this, so an event moving earlier has
        // to reach it: otherwise the interrupt that was just scheduled would be published
        // only once the old, later deadline came around.
        _cpu.ResynchronizeBatchDeadline();
    }

    /// <summary>Reads the raster cadence and interrupt schedule, for a snapshot.</summary>
    public ZxNextUlaTimingState CaptureState() => new(
        _timingProfile,
        _sixtyHertz,
        Profile,
        ProfileGeneration,
        PixelsPerLine,
        LinesPerFrame,
        MasterTicksPerFrame,
        InterruptOffsetMasterTicks,
        InterruptPulseCpuTstates,
        FrameNumber,
        FrameStartMasterTick,
        InterruptCount,
        LineInterruptCount,
        LastLineInterruptMasterTick,
        InterruptLineActive,
        _nextFrameTick,
        _nextInterruptTick,
        _nextLineInterruptTick,
        _interruptEndTick,
        _nextEventTick,
        _scheduledLineInterruptEnabled,
        _scheduledLineInterruptValue,
        _lineInterruptGeneration);

    /// <summary>
    /// Puts the cadence, the schedule and the beam back.
    /// </summary>
    /// <remarks>
    /// The interrupt line is driven rather than merely recorded, because the CPU has its
    /// own copy of it: a restore that only set the flag here would leave a machine whose
    /// ULA thinks the line is low and whose Z80 thinks it is high.
    /// </remarks>
    public void RestoreState(ZxNextUlaTimingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _timingProfile = state.TimingProfile;
        _sixtyHertz = state.SixtyHertz;
        Profile = state.Profile;
        ProfileGeneration = state.ProfileGeneration;
        PixelsPerLine = state.PixelsPerLine;
        LinesPerFrame = state.LinesPerFrame;
        MasterTicksPerFrame = state.MasterTicksPerFrame;
        InterruptOffsetMasterTicks = state.InterruptOffsetMasterTicks;
        InterruptPulseCpuTstates = state.InterruptPulseCpuTstates;
        FrameNumber = state.FrameNumber;
        FrameStartMasterTick = state.FrameStartMasterTick;
        InterruptCount = state.InterruptCount;
        LineInterruptCount = state.LineInterruptCount;
        LastLineInterruptMasterTick = state.LastLineInterruptMasterTick;
        _nextFrameTick = state.NextFrameTick;
        _nextInterruptTick = state.NextInterruptTick;
        _nextLineInterruptTick = state.NextLineInterruptTick;
        _interruptEndTick = state.InterruptEndTick;
        _nextEventTick = state.NextEventTick;
        _scheduledLineInterruptEnabled = state.ScheduledLineInterruptEnabled;
        _scheduledLineInterruptValue = state.ScheduledLineInterruptValue;
        _lineInterruptGeneration = state.LineInterruptGeneration;

        InterruptLineActive = state.InterruptLineActive;
        _cpu.Z80SetINTLine(state.InterruptLineActive);
    }
}
