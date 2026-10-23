using System.Collections.Generic;
using ZedExEss.Spectrum.DivMmc;

namespace ZedExEss.ZxNext.Core;

/// <summary>
/// The cards in the two SD slots, as far as a snapshot can honestly describe them.
/// </summary>
/// <remarks>
/// A slot is null when nothing was mounted, which is different from a slot whose card could
/// not be captured: the first says there was no card, and a restore leaves the slot alone.
/// Mounting is never the snapshot's business - a state file describes a machine, not the
/// host's drives - so what this carries is the SPI conversation, the sectors the guest has
/// changed, and enough identity for a restore to tell whether it is looking at the same
/// medium.
/// </remarks>
public sealed record ZxNextMediaState(SpectrumSdCardState?[] Slots);

/// <summary>What restoring the media did, slot by slot.</summary>
/// <remarks>
/// Collected and handed back rather than written to a log, because the host has a status line
/// and the person loading the state is the one who needs to know that the card was left as it
/// was. Empty when everything went back as captured.
/// </remarks>
public sealed record ZxNextMediaRestoreReport(IReadOnlyList<string> Notes)
{
    public static readonly ZxNextMediaRestoreReport Silent = new([]);
}
