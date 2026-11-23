using System.Runtime.CompilerServices;

namespace ZedExEss.ZxNext.Core;

/// <summary>Converts variable-rate CPU T-states onto the absolute clk28 timeline.</summary>
/// <remarks>
/// <para>
/// The CPU speed has three stages, as in the core:
/// </para>
/// <list type="bullet">
/// <item><see cref="RequestedCpuSpeed"/>, <c>nr_07_cpu_speed</c>: what NextReg $07 was last
/// written with (read back in bits 1:0).</item>
/// <item><see cref="CpuSpeed"/>, <c>cpu_speed</c>: the speed in effect, latched from the request on
/// a CPU clock edge with MREQ, IORQ and M1 high and the DMA off the bus (zxnext.vhd ~5796-5826).
/// The machine latches it at the end of the CPU's next machine cycle, or before an instruction.
/// It gates contention, clocks the DMA and reads back in bits 5:4.</item>
/// <item><see cref="ClockSpeed"/>: the clock the CPU actually runs on. The board's glitch-free
/// <c>BUFGMUX_1</c>s (<c>zxnext_top_issue4.vhd</c> ~992-1030) run one more T-state on the old
/// clock after the latch, then hand over on the new clock's next edge, strictly after. Every
/// clock's edges sit on the 3.5 MHz grid, which the 3.5 MHz clock (<c>CLK_3M5_CONT</c>) takes from
/// hc, so after any turbo period the CPU is back in step with the beam.</item>
/// </list>
/// </remarks>
public sealed class ZxNextClockCoordinator
{
    public ulong MasterTicks { get; private set; }

    /// <summary><c>cpu_speed</c>: the speed in effect.</summary>
    public ZxNextCpuSpeed CpuSpeed { get; private set; }

    /// <summary><c>nr_07_cpu_speed</c>: the speed last written to NextReg $07.</summary>
    public ZxNextCpuSpeed RequestedCpuSpeed { get; private set; }

    /// <summary>The speed of the clock the CPU is on; it trails <see cref="CpuSpeed"/> through the handover.</summary>
    public ZxNextCpuSpeed ClockSpeed { get; private set; }

    /// <summary>A NextReg $07 write is waiting for the CPU's next idle clock edge.</summary>
    public bool SpeedLatchPending => RequestedCpuSpeed != CpuSpeed;

    /// <summary>The speed in effect has changed and the clock has not handed over yet.</summary>
    public bool ClockHandoverPending => ClockSpeed != CpuSpeed;

    /// <summary>
    /// The ULA's contention reaches the CPU only through <c>CLK_3M5_CONT</c>: with 3.5 MHz in effect
    /// (<c>i_contention_en</c>) and the CPU on that clock.
    /// </summary>
    public bool CpuOnContendedClock =>
        CpuSpeed == ZxNextCpuSpeed.Mhz3_5 && ClockSpeed == ZxNextCpuSpeed.Mhz3_5;

    /// <summary>Master ticks in one T-state of the clock the CPU is on.</summary>
    public int MasterTicksPerCpuTstate =>
        ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate >> (int)ClockSpeed;

    public int CpuClockHz => ZxNextReferenceBaseline.BaseCpuClockHz << (int)CpuSpeed;

    public void AdvanceCpuTstates(int cpuTstates)
    {
        if (cpuTstates > 0)
        {
            AdvanceMasterTicks(
                checked((ulong)cpuTstates * (ulong)MasterTicksPerCpuTstate));
        }
    }

    /// <summary>Advances a non-CPU bus owner directly on the clk28 timeline.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceMasterTicks(ulong masterTicks)
    {
        MasterTicks = checked(MasterTicks + masterTicks);
    }

    /// <summary>Runs just before the speed in effect changes, while the old one still applies.</summary>
    /// <remarks>
    /// Batched CPU time has to be published here: the T-states after the latch begin with the
    /// clock handover, so time still held when it happens would be converted as if it came
    /// after it.
    /// </remarks>
    public Action? CpuSpeedChanging { get; set; }

    /// <summary>Runs after the speed in effect has changed, so deadlines can be rebuilt on it.</summary>
    public Action? CpuSpeedChanged { get; set; }

    /// <summary>A NextReg $07 write: <c>nr_07_cpu_speed</c>. It takes effect at the next idle edge.</summary>
    public void RequestCpuSpeed(ZxNextCpuSpeed speed)
    {
        if ((byte)speed > (byte)ZxNextCpuSpeed.Mhz28)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }

        RequestedCpuSpeed = speed;
    }

    /// <summary>At an idle CPU clock edge: <c>cpu_speed</c> takes the requested speed.</summary>
    public void LatchRequestedSpeed()
    {
        if (!SpeedLatchPending)
        {
            return;
        }

        CpuSpeedChanging?.Invoke();
        CpuSpeed = RequestedCpuSpeed;
        CpuSpeedChanged?.Invoke();
    }

    /// <summary>
    /// The clock hands over to the speed in effect; returns the ticks it pauses for, to the new
    /// clock's next edge strictly after <paramref name="frameMasterTick"/> (1 to one period).
    /// </summary>
    /// <remarks>The caller has already run the one T-state on the old clock.</remarks>
    public ulong CompleteClockHandover(ulong frameMasterTick)
    {
        ClockSpeed = CpuSpeed;
        ulong period = (ulong)MasterTicksPerCpuTstate;
        return period - (frameMasterTick % period);
    }

    /// <summary>
    /// Puts all three stages at <paramref name="speed"/> at once, with no latch and no handover:
    /// for loaders and diagnostics that set a machine up between runs. The timeline has to be on
    /// the new clock's grid (<paramref name="frameMasterTick"/> a whole number of its periods
    /// into the frame), or the CPU would run out of step with the beam.
    /// </summary>
    public void ForceCpuSpeed(ZxNextCpuSpeed speed, ulong frameMasterTick)
    {
        if ((byte)speed > (byte)ZxNextCpuSpeed.Mhz28)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }

        int period = ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate >> (int)speed;
        if (frameMasterTick % (ulong)period != 0)
        {
            throw new InvalidOperationException(
                $"Tick {frameMasterTick} of the frame is not on the {speed} clock's grid.");
        }

        RequestedCpuSpeed = speed;
        CpuSpeed = speed;
        ClockSpeed = speed;
    }

    /// <summary>Reads the timeline position and the three speeds, for a snapshot.</summary>
    public ZxNextClockState CaptureState() => new(MasterTicks, CpuSpeed)
    {
        RequestedCpuSpeed = RequestedCpuSpeed,
        ClockSpeed = ClockSpeed
    };

    /// <summary>
    /// Puts the timeline back without announcing a speed change.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="LatchRequestedSpeed"/>: its hooks publish batched CPU time and
    /// rebuild deadlines, which is right for a guest changing speed and wrong for a restore,
    /// where the CPU's batched state is being replaced wholesale anyway. A state from before
    /// the three stages were kept has them all equal.
    /// </remarks>
    public void RestoreState(ZxNextClockState state)
    {
        MasterTicks = state.MasterTicks;
        CpuSpeed = state.CpuSpeed;
        RequestedCpuSpeed = state.RequestedCpuSpeed ?? state.CpuSpeed;
        ClockSpeed = state.ClockSpeed ?? state.CpuSpeed;
    }

    /// <summary>A reset: <c>nr_07_cpu_speed</c> and <c>cpu_speed</c> to 3.5 MHz, the CPU on that clock.</summary>
    /// <remarks>The machine restarts the timeline at tick 0 on a reset, so no handover is due.</remarks>
    public void ResetSpeed()
    {
        RequestedCpuSpeed = ZxNextCpuSpeed.Mhz3_5;
        CpuSpeed = ZxNextCpuSpeed.Mhz3_5;
        ClockSpeed = ZxNextCpuSpeed.Mhz3_5;
    }

    public void Reset()
    {
        MasterTicks = 0;
        ResetSpeed();
    }
}
