namespace ZedExEss.Z80CPU;

/// <summary>
/// Portable machine-time contract called by the shared Z80 core as CPU time advances.
/// </summary>
/// <remarks>
/// Implementations own the conversion from CPU T-states to their machine timeline.
/// The callback is on the CPU hot path and must not allocate.
/// </remarks>
public interface IZ80TstateConsumer
{
    /// <summary>Advances ordinary machine devices after executed CPU T-states.</summary>
    void OnTstatesConsumed(int tstates);

    /// <summary>
    /// Advances machine time skipped by an accelerated tape-load path without
    /// treating the interval as ordinarily executed CPU cycles.
    /// </summary>
    void OnTapeLoadTstatesSkipped(int tstates);

    /// <summary>
    /// Notifies the machine after a complete opcode, memory or I/O bus cycle, when
    /// the Z80 may acknowledge an asserted BUSREQ before continuing the instruction.
    /// </summary>
    void OnMachineCycleCompleted()
    {
    }

    /// <summary>
    /// Returns the next absolute CPU T-state at which instruction-boundary state
    /// may change. Returning <see cref="ulong.MaxValue"/> declares no deadline.
    /// </summary>
    ulong GetNextCpuBoundarySyncTstate();
}
