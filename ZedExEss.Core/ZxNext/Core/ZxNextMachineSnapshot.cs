using System.Collections.Generic;
using ZedExEss.Z80CPU;

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
}

/// <summary>Where the 28 MHz timeline and the CPU's rate had got to.</summary>
public readonly record struct ZxNextClockState(ulong MasterTicks, ZxNextCpuSpeed CpuSpeed);
