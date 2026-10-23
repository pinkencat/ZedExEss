using System.Runtime.CompilerServices;

namespace ZedExEss.ZxNext.Core;

/// <summary>Converts variable-rate CPU T-states onto the absolute clk28 timeline.</summary>
public sealed class ZxNextClockCoordinator
{
    public ulong MasterTicks { get; private set; }
    public ZxNextCpuSpeed CpuSpeed { get; private set; }
    public int MasterTicksPerCpuTstate =>
        ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate >> (int)CpuSpeed;
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

    /// <summary>Runs just before the rate changes, while the old one still applies.</summary>
    /// <remarks>
    /// Batched CPU time has to be published here. A T-state accumulated at 3.5 MHz is
    /// worth eight master ticks and one accumulated at 28 MHz is worth one, so time still
    /// held when the rate changes would be converted at the wrong rate - which is exactly
    /// what a NEXTREG that switches speed mid-instruction would do.
    /// </remarks>
    public Action? CpuSpeedChanging { get; set; }

    /// <summary>Runs after the rate has changed, so deadlines can be rebuilt on it.</summary>
    public Action? CpuSpeedChanged { get; set; }

    public void SetCpuSpeed(ZxNextCpuSpeed speed)
    {
        if ((byte)speed > (byte)ZxNextCpuSpeed.Mhz28)
        {
            throw new ArgumentOutOfRangeException(nameof(speed));
        }

        CpuSpeedChanging?.Invoke();
        CpuSpeed = speed;
        CpuSpeedChanged?.Invoke();
    }

    /// <summary>Reads the timeline position and rate, for a snapshot.</summary>
    public ZxNextClockState CaptureState() => new(MasterTicks, CpuSpeed);

    /// <summary>
    /// Puts the timeline back without announcing a speed change.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="SetCpuSpeed"/>: its hooks publish batched CPU time and
    /// rebuild deadlines, which is right for a guest changing speed and wrong for a
    /// restore, where the CPU's batched state is being replaced wholesale anyway.
    /// </remarks>
    public void RestoreState(ZxNextClockState state)
    {
        MasterTicks = state.MasterTicks;
        CpuSpeed = state.CpuSpeed;
    }

    public void Reset()
    {
        MasterTicks = 0;
        CpuSpeed = ZxNextCpuSpeed.Mhz3_5;
    }
}
