using ZedExEss.ZxNext.DivMmc;
using ZedExEss.ZxNext.Multiface;

namespace ZedExEss.ZxNext.Core;

/// <summary>The device an NMI belongs to: zxnext.vhd's <c>nmi_mf</c> and <c>nmi_divmmc</c> latches.</summary>
public enum ZxNextNmiSource : byte
{
    None,
    DivMmc,
    Multiface
}

/// <summary>Where a latched NMI is in zxnext.vhd's <c>nmi_state</c> machine.</summary>
public enum ZxNextNmiPhase : byte
{
    /// <summary>
    /// <c>S_NMI_IDLE</c>. With a source latched, the NMI is waiting to reach the CPU (which
    /// can be held off by the DMA owning the bus).
    /// </summary>
    Idle,

    /// <summary><c>S_NMI_FETCH</c>: the CPU has the NMI and has not yet fetched $0066.</summary>
    Fetch,

    /// <summary>
    /// <c>S_NMI_HOLD</c>: $0066 has been fetched, and the NMI stays active until the device
    /// that owns it lets go.
    /// </summary>
    Hold
}

/// <summary>
/// The Next's NMI arbitration, as zxnext.vhd (3.02.03) does it.
/// </summary>
/// <remarks>
/// <para>
/// An NMI request - NextReg $02 bit 3 or bit 2, from the CPU or the Copper - is a one-clock
/// pulse. It is latched for one device or lost: nothing is queued. Only one NMI is active
/// at a time, from the pulse until the owning device releases it after the $0066 fetch.
/// </para>
/// <para>
/// A Multiface request wins when DivMMC's CONMEM is off and DivMMC is not holding an NMI of
/// its own (automap or its button latch); otherwise a DivMMC request wins when the Multiface
/// is not active (paged in or holding its NMI). The NextReg $06 enables and configuration
/// mode are applied by the caller.
/// </para>
/// <para>
/// Latching presses the device's button at once (<c>nmi_mf_button</c>,
/// <c>nmi_divmmc_button</c>). A disabled Multiface ignores its button, so the CPU runs
/// whatever is at $0066 and the NMI ends with that fetch; DivMMC with automap off does the
/// same.
/// </para>
/// <para>
/// The release is evaluated at the next instruction boundary or NMI request after it
/// happens (<see cref="Settle"/>), which is as soon as anything can observe it.
/// </para>
/// </remarks>
public sealed class ZxNextNmiController
{
    private readonly ZxNextDivMmcDevice _divMmc;
    private readonly ZxNextMultifaceDevice _multiface;

    public ZxNextNmiController(ZxNextDivMmcDevice divMmc, ZxNextMultifaceDevice multiface)
    {
        _divMmc = divMmc ?? throw new ArgumentNullException(nameof(divMmc));
        _multiface = multiface ?? throw new ArgumentNullException(nameof(multiface));
    }

    /// <summary>The device the active NMI belongs to, or none.</summary>
    public ZxNextNmiSource Source { get; private set; }

    public ZxNextNmiPhase Phase { get; private set; }

    /// <summary><c>nmi_activated</c>: an NMI is latched, from its request until its release.</summary>
    public bool Activated
    {
        get
        {
            Settle();
            return Source != ZxNextNmiSource.None;
        }
    }

    /// <summary>
    /// <c>nmi_accept_cause</c>: NextReg $02 records a request's cause only before $0066 has
    /// been fetched, so a request made inside an NMI routine leaves no trace.
    /// </summary>
    public bool AcceptsCause
    {
        get
        {
            Settle();
            return Phase != ZxNextNmiPhase.Hold;
        }
    }

    /// <summary>True when a latched NMI has not yet been given to the CPU.</summary>
    internal bool AwaitingCpu => Source != ZxNextNmiSource.None && Phase == ZxNextNmiPhase.Idle;

    /// <summary>One request pulse, already gated by the NextReg $06 enables.</summary>
    internal void Request(bool multiface, bool divMmc)
    {
        Settle();
        if (Source != ZxNextNmiSource.None)
        {
            return;
        }

        if (multiface && !_divMmc.Conmem && !_divMmc.NmiHold)
        {
            Source = ZxNextNmiSource.Multiface;
            _multiface.PressButton();
        }
        else if (divMmc && !_multiface.IsActive)
        {
            Source = ZxNextNmiSource.DivMmc;
            _divMmc.PressButton();
        }
    }

    /// <summary>Gives the latched NMI to the CPU; the FPGA holds /NMI low until $0066 is fetched.</summary>
    internal ZxNextNmiSource SignalCpu()
    {
        Phase = ZxNextNmiPhase.Fetch;
        return Source;
    }

    /// <summary>An opcode fetch from $0066.</summary>
    internal void OnVectorFetch()
    {
        if (Phase == ZxNextNmiPhase.Fetch)
        {
            Phase = ZxNextNmiPhase.Hold;
        }
    }

    /// <summary>Ends a held NMI whose device has let go (<c>S_NMI_END</c>).</summary>
    /// <remarks>Called at every instruction boundary, so the common case is one comparison.</remarks>
    internal void Settle()
    {
        if (Phase == ZxNextNmiPhase.Hold)
        {
            EndIfReleased();
        }
    }

    private void EndIfReleased()
    {
        bool held = Source switch
        {
            ZxNextNmiSource.Multiface => _multiface.NmiActive,
            ZxNextNmiSource.DivMmc => _divMmc.NmiHold,
            _ => false
        };
        if (!held)
        {
            Reset();
        }
    }

    /// <summary>Reset, and configuration mode, clear the latches.</summary>
    public void Reset()
    {
        Source = ZxNextNmiSource.None;
        Phase = ZxNextNmiPhase.Idle;
    }

    internal void Restore(ZxNextNmiSource source, ZxNextNmiPhase phase)
    {
        Source = source;
        Phase = source == ZxNextNmiSource.None ? ZxNextNmiPhase.Idle : phase;
    }

    /// <summary>
    /// Rebuilds the latch for a state saved before it was stored, from the restored devices.
    /// </summary>
    /// <remarks>
    /// Those states come from a model that pressed a device's button when the CPU took the
    /// NMI, and paged DivMMC in at that moment. So a device with its NMI active was inside
    /// one, and a request still pending had not reached its device yet.
    /// </remarks>
    internal void RestoreLegacy(bool pendingMultiface, bool pendingDivMmc)
    {
        Reset();
        if (_multiface.NmiActive)
        {
            Restore(ZxNextNmiSource.Multiface, _multiface.IsMapped ? ZxNextNmiPhase.Hold : ZxNextNmiPhase.Fetch);
        }
        else if (_divMmc.NmiActive)
        {
            Restore(ZxNextNmiSource.DivMmc, ZxNextNmiPhase.Hold);
        }
        else if (pendingMultiface || pendingDivMmc)
        {
            Request(pendingMultiface, pendingDivMmc);
        }
    }
}
