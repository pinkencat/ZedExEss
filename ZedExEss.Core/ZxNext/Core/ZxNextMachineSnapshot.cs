using System.Collections.Generic;
using ZedExEss.Z80CPU;
using ZedExEss.ZxNext.Video;

namespace ZedExEss.ZxNext.Core;

/// <summary>
/// A whole ZX Spectrum Next, captured well enough to carry on from.
/// </summary>
/// <remarks>
/// Kept as one record of per-device states rather than a byte stream, because the file
/// container and the in-memory capture are different problems: save states, rewind and a
/// TAS greenzone all want the capture and never want a file. A container can be written
/// over this later without the devices learning about it.
///
/// Nothing here is optional. A Next with the right RAM and registers but the wrong beam
/// phase resumes into a different frame, and one with the right beam and the wrong palette
/// resumes looking wrong, so the parts are captured together and restored together.
/// </remarks>
public sealed record ZxNextMachineSnapshot(
    Z80SnapshotState Cpu,
    ZxNextClockState Clock,
    byte[] PhysicalRam,
    ZxNextMemoryBusState MemoryBus,
    ZxNextPagingState Paging,
    ZxNextRegisterFileState Registers,
    ZxNextUlaTimingState UlaTiming,
    ZxNextRasterState Raster,
    ZxNextPaletteState Palettes,
    ZxNextLayer2State Layer2,
    ZxNextTilemapState Tilemap,
    ZxNextSpriteState Sprites,
    ZxNextDmaState Dma,
    ZxNextBusArbiterState BusArbiter,
    ZxNextDivMmcState DivMmc,
    ZxNextMultifaceState Multiface,
    ZxNextNmiRoutingState NmiRouting,
    ZxNextAudioState Audio,
    ZxNextInputState Input,
    ZxNextPortBusState PortBus,
    ZxNextFloatingBusState FloatingBus,
    ZxNextMediaState Media)
{
    /// <summary>
    /// Chunks a newer build wrote that this one does not understand.
    /// </summary>
    /// <remarks>
    /// Carried so that loading a file from a newer build and saving it again does not quietly
    /// discard what that build recorded. Empty on a snapshot captured from a running machine,
    /// because there was never a file to preserve anything from.
    /// </remarks>
    public IReadOnlyList<ZxNextSnapshotUnknownChunk> UnknownChunks { get; init; } = [];

    /// <summary>The Kempston mouse's counters and latched buttons.</summary>
    /// <remarks>
    /// Kept out of the positional list only because files written before the mouse existed
    /// have no record of it, and those load as a mouse that has not moved since power-on.
    /// A capture always sets it.
    /// </remarks>
    public ZxNextMouseState Mouse { get; init; }

    /// <summary>The maskable interrupt chain: hardware IM2's status, pending and in-service bits.</summary>
    /// <remarks>
    /// Null in a file written before hardware IM2 existed, which restores as a chain with
    /// nothing pending and the $C5, $C6 and $CC-$CE enables clear. A capture always sets it.
    /// </remarks>
    public ZxNextInterruptControllerState? Interrupts { get; init; }

    /// <summary>The Copper's list, registers and position, and NextReg $64's cvc offset.</summary>
    /// <remarks>
    /// Null in a file written before the Copper existed, which restores as a stopped Copper
    /// with an empty list and no offset. A capture always sets it.
    /// </remarks>
    public ZxNextCopperState? Copper { get; init; }

    /// <summary>
    /// The CTC; null in a file written before it existed, which restores every channel reset,
    /// with the interrupt enables of the IM2X chunk.
    /// </summary>
    public ZxNextCtcState? Ctc { get; init; }
}

/// <summary>Where the 28 MHz timeline and the CPU's rate had got to.</summary>
/// <remarks>
/// <see cref="CpuSpeed"/> is the speed in effect (<c>cpu_speed</c>). The request and the clock
/// differ from it between a NextReg $07 write and the latch, and through the clock handover;
/// null in a file from before they were kept, when all three are equal.
/// </remarks>
public readonly record struct ZxNextClockState(ulong MasterTicks, ZxNextCpuSpeed CpuSpeed)
{
    /// <summary><c>nr_07_cpu_speed</c>, the speed last written to NextReg $07.</summary>
    public ZxNextCpuSpeed? RequestedCpuSpeed { get; init; }

    /// <summary>The clock the CPU was on.</summary>
    public ZxNextCpuSpeed? ClockSpeed { get; init; }
}
