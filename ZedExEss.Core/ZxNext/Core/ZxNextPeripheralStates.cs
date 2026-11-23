using ZedExEss.Spectrum.Audio;
using ZedExEss.ZxNext.Audio;
using ZedExEss.ZxNext.Input;

namespace ZedExEss.ZxNext.Core;

/// <summary>
/// The DMA controller, including a transfer caught between its two halves.
/// </summary>
/// <remarks>
/// The reason this cannot be reduced to "which transfer is loaded" is the phase. A Next
/// DMA byte is a source read on one granted bus cycle and a destination write on the next,
/// so a capture taken between them holds a byte that has been read and not yet written. A
/// restore that dropped it would write a stale byte, or skip one, and a screen blit would
/// come out one byte short with everything after it shifted.
///
/// The multi-byte register writes are in the same position: the guest has sent a header
/// saying four bytes follow and may have sent two of them. The pending-write cursor and
/// the header it belongs to are carried so the remaining bytes still land in the right
/// fields. Enums are stored as their numeric values because they are the device's own
/// private vocabulary, which the device translates back on restore.
/// </remarks>
public sealed record ZxNextDmaState(
    bool IsActive,
    bool Loaded,
    bool Z80CompatibleMode,
    bool DirectionAtoB,
    bool EndOfBlockNot,
    bool TransferredAtLeastOne,
    bool PortAIsIo,
    bool PortBIsIo,
    byte PendingWrite,
    byte PendingHeader,
    byte ReadField,
    byte ReadMask,
    byte TransferPhase,
    byte PortAAddressMode,
    byte PortBAddressMode,
    byte PortATiming,
    byte PortBTiming,
    byte PortBPrescaler,
    byte TransferByte,
    ushort PortAStart,
    ushort PortBStart,
    ushort BlockLength,
    ushort SourceAddress,
    ushort DestinationAddress,
    ushort Counter,
    int CurrentTransferLength,
    int LastTransferLength)
{
    /// <summary>The WR4/WR5 modes and the zxnDDMA, kept since snapshot format 1 shipped.</summary>
    public ZxNextDmaExtendedState Extended { get; init; }
}

/// <summary>
/// DMA state added after snapshot format 1: the WR4 transfer mode, the WR5 bits, the
/// zxnDDMA (core 3.03) registers and row position, and a prescaler wait in progress.
/// </summary>
/// <remarks>
/// A file without it loads as all zero: byte transfer mode (which acts as continuous), no
/// auto restart and a zxnDDMA that was never programmed, which is what such a file's
/// machine could have been running.
/// </remarks>
public readonly record struct ZxNextDmaExtendedState(
    byte TransferMode,
    bool AutoRestart,
    bool CeWait,
    byte DimensionalFlags,
    byte PortAExtended,
    byte PortBExtended,
    ushort RowLength,
    ushort RowCount,
    ushort PortAStride,
    ushort PortBStride,
    byte TransparentValue,
    byte FillValue,
    byte Layer2XLow,
    byte Layer2XHigh,
    byte SourceHigh,
    byte DestinationHigh,
    int SourceRowStart,
    int DestinationRowStart,
    ushort RowPosition,
    ushort RowIndex,
    int WaitClocks,
    ulong ResumeTick,
    bool BusGranted,
    ulong RequestTick);

/// <summary>Who owns the shared bus, and the tallies the diagnostics read.</summary>
/// <remarks>
/// Ownership is the part that matters to correctness: restoring a machine whose bus was
/// granted to DMA while leaving the arbiter saying CPU makes the next CPU cycle throw,
/// because advancing the CPU without the grant is a contradiction the arbiter checks for.
/// The tallies are carried with it so a restored machine's DMA statistics continue rather
/// than counting from zero mid-session.
/// </remarks>
public readonly record struct ZxNextBusArbiterState(
    ZxNextBusOwner Owner,
    ulong CpuOwnedMasterTicks,
    ulong DmaOwnedMasterTicks,
    ulong DmaGrantCount,
    ulong CpuTstatesAtLastDmaGrant);

/// <summary>DivMMC's paging latches and where its automapper had got to.</summary>
/// <remarks>
/// <c>AutomapHeld</c> and <c>NmiActive</c> are the live ones: the first says the 8 KiB
/// window is mapped right now because an opcode fetch entered a trap, and the second says
/// the machine is inside a DivMMC NMI and the next RETN belongs to it. A restore that lost
/// them resumes executing the service routine with the ROM unmapped underneath it.
///
/// The cards themselves are not here. Mounted media is SNP-005 and is the host's business,
/// and a snapshot that silently rewound a card's write pointer would corrupt a real file.
/// </remarks>
public readonly record struct ZxNextDivMmcState(
    byte ControlRegister,
    byte CardSelectRegister,
    bool SdCardSwap,
    bool Enabled,
    bool AutomapEnabled,
    bool AutomapHeld,
    bool NmiActive,
    byte EntryPoints0,
    byte EntryPointsValid0,
    byte EntryPointsTiming0,
    byte EntryPoints1);

/// <summary>The Multiface NMI and paging latches.</summary>
/// <remarks>
/// Invisibility is carried as well as the mapping: it decides whether the next read of the
/// enable port pages the unit in, so a machine restored without it answers the guest's
/// next probe differently to the one that was captured.
/// </remarks>
public readonly record struct ZxNextMultifaceState(
    bool Enabled,
    bool NmiActive,
    bool IsMapped,
    bool Invisible);

/// <summary>Which device an asserted NMI came from.</summary>
/// <remarks>
/// The Z80 itself carries only "an NMI is pending"; on a Next it also matters who raised
/// it, because acknowledging a DivMMC NMI maps DivMMC's window and acknowledging
/// Multiface's does not. Captured separately from the CPU state, which is the shared Z80
/// record and has no business knowing about Next peripherals, and held as the numeric value
/// because the source itself is the machine's own internal vocabulary.
/// </remarks>
public readonly record struct ZxNextNmiRoutingState(byte PendingNmiSource);

/// <summary>
/// The beeper/MIC level, the PSGs and the partially accumulated sample.
/// </summary>
/// <remarks>
/// The weighted level sum and the sample remainder are mid-sample arithmetic: audio is
/// accumulated per machine cycle and emitted when the remainder crosses a sample boundary,
/// so a capture lands inside a sample more often than not. Dropping them would make the
/// first sample after a restore an average over the wrong number of ticks.
///
/// What is deliberately <b>not</b> carried is the queue of finished samples waiting to be
/// drained. Those are output, not machine state - the host has either played them or is
/// about to - and restoring them would play the same milliseconds twice.
/// </remarks>
public sealed record ZxNextAudioState(
    AySnapshotState[] Chips,
    byte[] SelectedRegisters,
    byte[] Pan,
    int ActiveChip,
    byte UlaOutputLatch,
    byte Peripheral2Settings,
    byte Peripheral3Settings,
    byte Peripheral4Settings,
    int CurrentUlaLevel,
    ulong UlaLevelRunTicks,
    long UlaLevelSum,
    ulong UlaLevelWeight,
    long SampleRemainder)
{
    /// <summary>
    /// The PSGs' own state, which <see cref="Chips"/> (an AY-style record with only the
    /// registers meaningful) cannot hold; null in a file from before the PSGs were the core's,
    /// when the registers are restored and the generators restart.
    /// </summary>
    public ZxNextPsgState[]? Psgs { get; init; }

    /// <summary>
    /// The four DAC channels and their part-accumulated sample; null in a file from before
    /// the DACs, when they restore to their reset value, $80.
    /// </summary>
    public ZxNextDacState? Dac { get; init; }
}

/// <summary>
/// soundrive.vhd's four channels (A, B, C, D) and the level sums of the sample being
/// accumulated, taken with the run up to the capture folded in.
/// </summary>
public sealed record ZxNextDacState(
    byte[] Channels,
    long LeftLevelSum,
    long RightLevelSum);

/// <summary>
/// Which keys and joystick directions are held, and how the connectors are routed.
/// </summary>
/// <remarks>
/// Carried because a held key is state the guest can read: a machine restored with an
/// empty matrix while the player is still holding a direction reads it as released until
/// the host next sends an event. The host's physical input device is not restored - the
/// chosen joystick type is a host setting, not something a snapshot gets to change - but
/// the connector modes are, because the guest programmed those through NextReg.
///
/// Keyboard and joystick rows are kept apart exactly as the device keeps them, so a
/// restore cannot leave a joystick-pressed key stuck down after the joystick is released.
/// </remarks>
public sealed record ZxNextInputState(
    byte[] KeyboardRows,
    byte[] JoystickRows,
    byte[] ConnectorStates,
    ZxNextJoystickMode[] ConnectorModes);

/// <summary>The port bus's own latches.</summary>
/// <remarks>
/// <c>UlaOutputLatch</c> is the border colour and the beeper bit, which is why this is not
/// bookkeeping: a machine restored without it shows the wrong border until the guest next
/// writes port $FE, which for a static screen can be never. <c>SelectedRegister</c> is the
/// $243B NextReg selection, so a guest that selected a register before the capture writes
/// to the right one after the restore.
/// </remarks>
public readonly record struct ZxNextPortBusState(
    ushort LastWrittenPort,
    bool HasLastWrittenPort,
    byte LastWrittenValue,
    byte SelectedRegister,
    byte UlaOutputLatch);

/// <summary>The value the floating bus would answer with.</summary>
/// <remarks>
/// Small, but readable by the guest: a program timed against the floating bus reads it
/// immediately, before anything has refreshed these latches, so they are part of the state
/// rather than a cache that rebuilds itself.
/// </remarks>
public readonly record struct ZxNextFloatingBusState(
    byte UlaFetchValue,
    byte Plus3CpuData,
    bool UlaDriving);

/// <summary>The Kempston mouse's counters and the buttons its last packet latched.</summary>
/// <remarks>
/// Guest-readable state: a pointer drawn from these counters jumps if a restore puts them
/// anywhere else. Movement the host had sent and the guest had not yet read is not part of
/// it - that belongs to the host, which is still holding the mouse.
/// </remarks>
public readonly record struct ZxNextMouseState(byte X, byte Y, byte Wheel, byte LatchedButtons);
