using System;

namespace ZedExEss.Spectrum.DivMmc
{
    /// <summary>
    /// Which medium a captured card was, so a restore can tell it is the same one.
    /// </summary>
    /// <remarks>
    /// A snapshot never mounts anything. This is here to be compared, not acted on: a state
    /// file is a document, and a document that could make the emulator open a path of its
    /// choosing and write sectors into it would be a way of damaging files that have nothing
    /// to do with the machine. So a restore matches this against what the host has already
    /// mounted and refuses the sector data if it is not the same medium.
    /// </remarks>
    public sealed record SpectrumSdCardIdentity(
        string Path,
        long SectorCount,
        bool WriteProtected,
        bool FolderBacked,
        bool WritesBackToFolder);

    /// <summary>
    /// A mounted card: what the SPI link is part way through, and what the guest has changed.
    /// </summary>
    /// <remarks>
    /// <para>The SPI half is small and unconditional. A capture taken between the third and
    /// fourth byte of a command, or in the middle of a 512-byte write payload, has to carry
    /// the half-assembled command, the queued response bytes and the partly filled write
    /// buffer, or the guest's next clock pulse is answered from the wrong place in the
    /// conversation and the driver hangs.</para>
    ///
    /// <para>The sector half is a journal, not a copy. Copying a card would make a save state
    /// as big as the card - hundreds of megabytes for a real NextZXOS install - so only the
    /// sectors the guest has written since the card was mounted are carried, which for the
    /// usual case of a game saving its progress is a handful. <c>SectorsComplete</c> is false
    /// when the guest has written more than the journal is allowed to hold; the capture is
    /// then still a valid machine state, and a restore says the card was left as it is rather
    /// than pretending to have put it back.</para>
    /// </remarks>
    public sealed record SpectrumSdCardState(
        SpectrumSdCardIdentity Identity,
        byte[] Command,
        int CommandLength,
        byte[] Response,
        bool Idle,
        bool AppCommand,
        bool MultipleRead,
        uint MultipleReadLba,
        byte WriteState,
        uint WriteLba,
        int WriteIndex,
        byte[] WriteBuffer,
        bool Modified,
        bool FolderBackingFlushed,
        ulong WriteCount,
        uint[] DirtySectorLbas,
        byte[] DirtySectorData,
        bool SectorsComplete);

    /// <summary>What a restore actually did to a card, in the host's words.</summary>
    /// <remarks>
    /// Returned rather than thrown or logged because the answer is not always "it worked" and
    /// the difference matters to the person loading the state: a machine whose card was not
    /// put back is a machine that may be about to write a directory entry over the wrong
    /// sector. <see cref="Note"/> is null when there is nothing worth saying.
    /// </remarks>
    public readonly record struct SpectrumSdCardRestoreResult(
        bool SpiRestored,
        bool SectorsRestored,
        int SectorsWritten,
        string? Note)
    {
        public static SpectrumSdCardRestoreResult Refused(string note) => new(false, false, 0, note);
    }
}
