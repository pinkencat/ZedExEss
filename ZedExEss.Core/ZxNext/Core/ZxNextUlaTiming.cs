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
    private readonly ZxNextInterruptController _interrupts;
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

    // zxula_timing.vhd's cvc: the copper's line count, reloaded from NextReg $64 at the first
    // display line. The register, the value latched at the last reload, and the next reload.
    private byte _cvcOffsetRegister;
    private byte _cvcActiveOffset;
    private ulong _nextCvcReloadTick;
    private int _copperBeamGeneration;

    internal ZxNextUlaTiming(
        ZxNextClockCoordinator clock,
        ZxNextRegisterFile registers,
        ZxNextCpu cpu,
        ZxNextInterruptController interrupts)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _registers = registers ?? throw new ArgumentNullException(nameof(registers));
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _interrupts = interrupts ?? throw new ArgumentNullException(nameof(interrupts));
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

    /// <summary>The copper line, cvc, now: what NextRegs $1E/$1F read.</summary>
    public ushort CurrentCopperLine
    {
        get
        {
            GetCopperBeam(_clock.MasterTicks, out _, out int cvc);
            return (ushort)cvc;
        }
    }

    /// <summary>NextReg $64, the copper line offset.</summary>
    internal byte CopperLineOffset => _cvcOffsetRegister;

    /// <summary>The $64 value latched at the last cvc reload.</summary>
    internal byte ActiveCopperLineOffset => _cvcActiveOffset;

    /// <summary>The next cvc reload, or an earlier one not yet synchronised.</summary>
    internal ulong NextCopperLineOffsetReloadTick => _nextCvcReloadTick;

    /// <summary>
    /// Changes whenever a beam search could answer differently for the same tick: a $64
    /// write, a timing switch, a reset or a restore. The copper keys its caches on it.
    /// </summary>
    internal int CopperBeamGeneration => _copperBeamGeneration;

    /// <summary>
    /// A NextReg $64 write. zxula_timing.vhd loads it into cvc only at the first display line,
    /// so the lines already counted this frame keep the old offset.
    /// </summary>
    internal void WriteCopperLineOffset(byte value)
    {
        SyncCvcReloads(_clock.MasterTicks);
        _cvcOffsetRegister = value;
        unchecked
        {
            _copperBeamGeneration++;
        }

        _scheduledLineInterruptEnabled = _registers.LineInterruptEnabled;
        _scheduledLineInterruptValue = _registers.LineInterruptValue;
        RescheduleLineInterrupt();
    }

    /// <summary>Latches $64 at every reload up to <paramref name="tick"/>.</summary>
    private void SyncCvcReloads(ulong tick)
    {
        if (_nextCvcReloadTick > tick)
        {
            return;
        }

        // The register cannot have changed since the last sync, so every reload in between
        // latched the same value.
        _cvcActiveOffset = _cvcOffsetRegister;
        ulong frames = ((tick - _nextCvcReloadTick) / MasterTicksPerFrame) + 1;
        _nextCvcReloadTick += frames * MasterTicksPerFrame;
    }

    /// <summary>The offset in force at <paramref name="tick"/>, no later than the next $64 write.</summary>
    private int CvcOffsetAt(ulong tick) => tick < _nextCvcReloadTick ? _cvcActiveOffset : _cvcOffsetRegister;

    /// <summary>
    /// Where a cvc line starts in its frame: hc_ula wraps to 0 there (observable at raw
    /// horizontal active-11, see <see cref="ZxNextUlaTimingProfile"/>) and cvc moves on.
    /// </summary>
    private ulong CvcLineStartOffset(int line) =>
        (ulong)(((long)line * PixelsPerLine + Profile.DisplayStartHorizontal - 11)
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);

    private ulong FrameStartFor(ulong tick)
    {
        ulong phase = FrameStartMasterTick % MasterTicksPerFrame;
        ulong relative = (tick + MasterTicksPerFrame - phase) % MasterTicksPerFrame;
        return tick - relative;
    }

    /// <summary>
    /// hc_ula and cvc during master tick <paramref name="tick"/>: the counters the copper
    /// compares and NextRegs $1E/$1F read.
    /// </summary>
    internal void GetCopperBeam(ulong tick, out int hcUla, out int cvc)
    {
        ulong frameStart = FrameStartFor(tick);
        long pixel = (long)((tick - frameStart) / ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        int rawVertical = (int)(pixel / PixelsPerLine);
        int rawHorizontal = (int)(pixel % PixelsPerLine);
        int wrap = Profile.DisplayStartHorizontal - 11;
        hcUla = (rawHorizontal - wrap + PixelsPerLine) % PixelsPerLine;
        int line = rawHorizontal >= wrap ? rawVertical : rawVertical - 1;
        cvc = (CvcOffsetAt(tick) + line - Profile.FirstDisplayLine + (2 * LinesPerFrame)) % LinesPerFrame;
    }

    /// <summary>
    /// The first master tick at or after <paramref name="fromTick"/> during which cvc is
    /// <paramref name="cvc"/> and hc_ula is in [<paramref name="hcLow"/>, <paramref name="hcHigh"/>],
    /// or <see cref="ulong.MaxValue"/> when that does not happen in the next frames.
    /// </summary>
    /// <remarks>
    /// Each frame holds two runs of cvc lines: those before the reload count on from the
    /// previous frame's offset, those after it from the newly latched one.
    /// </remarks>
    internal ulong FindCopperBeamTick(ulong fromTick, int cvc, int hcLow, int hcHigh)
    {
        if ((uint)cvc >= (uint)LinesPerFrame || hcLow > hcHigh || hcLow >= PixelsPerLine)
        {
            return ulong.MaxValue;
        }

        hcHigh = Math.Min(hcHigh, PixelsPerLine - 1);
        int first = Profile.FirstDisplayLine;
        ulong frameStart = FrameStartFor(fromTick);
        ulong best = ulong.MaxValue;
        for (int frame = 0; frame < 3 && best == ulong.MaxValue; frame++)
        {
            ulong start = frameStart + ((ulong)frame * MasterTicksPerFrame);
            ulong reload = start + CvcLineStartOffset(first);
            int before = CvcOffsetAt(reload - 1);
            int after = CvcOffsetAt(reload);

            // Lines from the reload on: cvc = after + line - first.
            int lineAfter = first + ((cvc - after + LinesPerFrame) % LinesPerFrame);
            // Lines before it (from the previous frame's last line): cvc = before + line - first + LPF.
            int lineBefore = first + ((cvc - before + LinesPerFrame) % LinesPerFrame) - LinesPerFrame;
            for (int run = 0; run < 2; run++)
            {
                int line = run == 0 ? lineBefore : lineAfter;
                if (line < -1 || line >= LinesPerFrame || (line < first) != (run == 0))
                {
                    continue;
                }

                long windowStart = (long)start + (((long)line * PixelsPerLine + Profile.DisplayStartHorizontal - 11)
                    * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
                long low = windowStart + (hcLow * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
                long high = windowStart + ((hcHigh + 1) * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel) - 1;
                long candidate = Math.Max(low, (long)fromTick);
                if (candidate <= high && candidate >= 0 && (ulong)candidate < best)
                {
                    best = (ulong)candidate;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The first line interrupt at or after <paramref name="fromTick"/>: zxula_timing.vhd
    /// registers it when hc_ula is 255 on the cvc line before the selected one (0 selects
    /// the last line, c_max_vc), and the pulse follows a pixel and half a clock later.
    /// </summary>
    private ulong FindLineInterruptTick(ulong fromTick, ushort selectedLine)
    {
        int line = selectedLine & 0x01FF;
        int target = line == 0 ? LinesPerFrame - 1 : line - 1;
        if (target >= LinesPerFrame)
        {
            return ulong.MaxValue;
        }

        const int offsetIntoLine = (256 * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel)
            + ZxNextUlaTimingProfile.FallingEdgeMasterTick;
        ulong search = fromTick > offsetIntoLine ? fromTick - offsetIntoLine : 0;
        while (true)
        {
            ulong hcZero = FindCopperBeamTick(search, target, 0, 0);
            if (hcZero == ulong.MaxValue)
            {
                return ulong.MaxValue;
            }

            ulong windowStart = hcZero - ((hcZero - FrameStartFor(hcZero)) % ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
            ulong interruptTick = windowStart + offsetIntoLine;
            if (interruptTick >= fromTick)
            {
                return interruptTick;
            }

            search = windowStart + ((ulong)PixelsPerLine * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        }
    }

    /// <summary>
    /// The first master tick at or after <paramref name="absoluteMasterTick"/> where the ULA's
    /// horizontal count <c>hc</c> has bit 8 set, that is, is past the 256 positions of the
    /// display's contention window.
    /// </summary>
    /// <remarks>
    /// zxnext.vhd latches NextReg $08's contention disable only then, so a change never cuts
    /// into a line's window. <c>hc</c> is the same count the contention controller uses: zero
    /// where the contention counter starts.
    /// </remarks>
    internal ulong NextContentionWindowEndTick(ulong absoluteMasterTick)
    {
        ZxNextUlaTimingProfile profile = Profile;
        ulong elapsed = absoluteMasterTick >= FrameStartMasterTick
            ? absoluteMasterTick - FrameStartMasterTick
            : absoluteMasterTick;
        ulong inFrame = elapsed % profile.MasterTicksPerFrame;
        int horizontal = (int)((inFrame / ZxNextUlaTimingProfile.MasterTicksPerRasterPixel)
            % (ulong)profile.PixelsPerLine);
        int hc = (horizontal - profile.ContentionCounterStartHorizontal + profile.PixelsPerLine)
            % profile.PixelsPerLine;
        if (hc >= ZxNextUlaTimingProfile.DisplayWidth)
        {
            return absoluteMasterTick;
        }

        ulong intoPixel = inFrame % ZxNextUlaTimingProfile.MasterTicksPerRasterPixel;
        return absoluteMasterTick - intoPixel
            + ((ulong)(ZxNextUlaTimingProfile.DisplayWidth - hc)
                * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
    }

    internal void Reset()
    {
        InterruptLineActive = false;
        _interrupts.OnPulseEnded();
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
        _cvcOffsetRegister = 0;
        _cvcActiveOffset = 0;
        _nextCvcReloadTick = 0;
        ApplyCurrentProfile(0);
    }

    /// <remarks>
    /// A NextReg $03 timing or $05 50/60 Hz change is not applied here: zxnext.vhd takes it at
    /// the next frame sync, which the machine reaches with <see cref="ApplyPendingProfileAtFrameEdge"/>.
    /// </remarks>
    internal void Advance()
    {
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
                InterruptLineActive = false;
                _interrupts.OnPulseEnded();
                _interruptEndTick = ulong.MaxValue;
            }

            if (nextEventTick == _nextFrameTick)
            {
                _registers.OnVideoFrameSync();
                FrameNumber++;
                FrameStartMasterTick = _nextFrameTick;
                _nextFrameTick += MasterTicksPerFrame;
                _nextInterruptTick = FrameStartMasterTick + InterruptOffsetMasterTicks;
                ScheduleLineInterruptForFrame();
            }

            if (nextEventTick == _nextInterruptTick)
            {
                InterruptCount++;
                // zxula_timing.vhd raises the ULA's request only while it is enabled; the
                // interrupt controller decides between the pulse and hardware IM2.
                if (_registers.UlaInterruptEnabled)
                {
                    _interrupts.Request(ZxNextInterruptSource.Ula, nextEventTick);
                }
                _nextInterruptTick = ulong.MaxValue;
            }

            if (nextEventTick == _nextLineInterruptTick)
            {
                LineInterruptCount++;
                LastLineInterruptMasterTick = nextEventTick;
                _interrupts.Request(ZxNextInterruptSource.Line, nextEventTick);
                _nextLineInterruptTick = ulong.MaxValue;
            }

            UpdateNextEventTick();
        }
    }

    /// <summary>
    /// True when NextReg $03's machine timing or $05's 50/60 Hz differs from the timing in
    /// effect, so the next frame sync has to switch.
    /// </summary>
    internal bool ProfileChangePending =>
        _timingProfile != _registers.MachineTiming || _sixtyHertz != _registers.SixtyHertzMode;

    /// <summary>The master tick of the next frame sync.</summary>
    internal ulong NextFrameTick => _nextFrameTick;

    /// <summary>Switches to the written timing at the frame sync the clock has just reached.</summary>
    internal void ApplyPendingProfileAtFrameEdge() => ApplyCurrentProfile(_clock.MasterTicks);

    /// <summary>
    /// Applies the written timing at once, starting a frame here. Reset and restore set the
    /// timing this way, and so can a diagnostic that needs a machine in a given timing now;
    /// running machines switch only at a frame sync.
    /// </summary>
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
        if (MasterTicksPerFrame != 0)
        {
            SyncCvcReloads(frameStartTick);
        }

        InterruptLineActive = false;
        _interrupts.OnPulseEnded();
        _interruptEndTick = ulong.MaxValue;
        _nextLineInterruptTick = ulong.MaxValue;
        _timingProfile = _registers.MachineTiming;
        _sixtyHertz = _registers.SixtyHertzMode && (_timingProfile & 0x04) == 0;
        Profile = ZxNextUlaTimingProfile.Create(_timingProfile, _sixtyHertz);
        unchecked
        {
            ProfileGeneration++;
            _copperBeamGeneration++;
        }

        _sixtyHertz = Profile.SixtyHertz;
        PixelsPerLine = Profile.PixelsPerLine;
        LinesPerFrame = Profile.LinesPerFrame;
        InterruptPulseCpuTstates = Profile.InterruptPulseCpuTstates;
        MasterTicksPerFrame = Profile.MasterTicksPerFrame;
        InterruptOffsetMasterTicks = Profile.InterruptOffsetMasterTicks;
        FrameStartMasterTick = frameStartTick;
        _nextCvcReloadTick = frameStartTick + CvcLineStartOffset(Profile.FirstDisplayLine);
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

    /// <remarks>The first line interrupt in the frame, through cvc and so through NextReg $64.</remarks>
    private void ScheduleLineInterruptForFrame(ulong frameStartTick)
    {
        _nextLineInterruptTick = _scheduledLineInterruptEnabled
            ? FindLineInterruptTick(frameStartTick, _scheduledLineInterruptValue)
            : ulong.MaxValue;
    }

    /// <summary>After $64 changes: this frame's interrupt if it is still to come, else the next.</summary>
    private void RescheduleLineInterrupt()
    {
        ScheduleLineInterruptForFrame();
        if (_nextLineInterruptTick <= _clock.MasterTicks)
        {
            ScheduleLineInterruptForFrame(FrameStartMasterTick + MasterTicksPerFrame);
        }

        UpdateNextEventTick();
    }

    /// <summary>
    /// <c>pulse_int_en</c>: the 32 (48K, +3) or 36 (128K, Pentagon) CPU-clock INT pulse, unless
    /// one is already running.
    /// </summary>
    internal void StartInterruptPulse(ulong assertionTick)
    {
        BeginInterruptPulse(assertionTick);
        UpdateNextEventTick();
    }

    private void BeginInterruptPulse(ulong assertionTick)
    {
        if (InterruptLineActive)
        {
            return;
        }

        InterruptLineActive = true;
        _interrupts.OnPulseStarted();
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

        // A snapshot without the copper's chunk had no $64: the offset is 0.
        RestoreCopperLineOffset(0, 0, FrameStartMasterTick + CvcLineStartOffset(Profile.FirstDisplayLine));

        InterruptLineActive = state.InterruptLineActive;
        _cpu.Z80SetINTLine(state.InterruptLineActive);
    }

    /// <summary>
    /// Puts NextReg $64 back, with the value cvc counts from and the next reload, after
    /// <see cref="RestoreState"/>. The copper's snapshot chunk carries them.
    /// </summary>
    internal void RestoreCopperLineOffset(byte register, byte active, ulong nextReloadTick)
    {
        _cvcOffsetRegister = register;
        _cvcActiveOffset = active;
        _nextCvcReloadTick = nextReloadTick;
        unchecked
        {
            _copperBeamGeneration++;
        }
    }
}
