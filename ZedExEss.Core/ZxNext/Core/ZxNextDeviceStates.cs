namespace ZedExEss.ZxNext.Core;

/// <summary>A display write the video memory has accepted but not yet mutated.</summary>
/// <remarks>
/// These are in flight at the moment of capture and have to survive it, or a restored
/// machine loses writes the guest has already issued.
/// </remarks>
public readonly record struct ZxNextPendingDisplayWriteState(
    byte Page,
    int Offset,
    byte Value,
    ulong MasterTick);

/// <summary>Everything the memory bus holds that the guest can change.</summary>
/// <remarks>
/// The ROM images are configuration rather than state - a restore puts a machine back,
/// it does not rebuild it - so only the writable system SRAM is carried here.
/// </remarks>
public sealed record ZxNextMemoryBusState(
    byte[] MmuPages,
    byte[] SystemSram,
    int CurrentRomBank,
    bool BootRomEnabled,
    bool ConfigurationMode,
    byte ConfigurationBank,
    byte AlternateRomRegister,
    int[] ShadowedPages,
    byte[][] DisplayShadows,
    ZxNextPendingDisplayWriteState[] PendingDisplayWrites)
{
    /// <summary>
    /// The 24K of SRAM behind banks 5 and 7's block RAM, which only physical-mode DMA
    /// reaches; null from a file written before it was kept, meaning all zero.
    /// </summary>
    public byte[]? SramBehindVideoBram { get; init; }
}

/// <summary>The legacy paging ports, which still drive the Next's mapping.</summary>
public readonly record struct ZxNextPagingState(
    byte Port7Ffd,
    byte PortDFfd,
    byte Port1Ffd,
    byte PortEff7);

/// <summary>
/// The NextReg state the register file keeps for itself.
/// </summary>
/// <remarks>
/// Only what this device owns. Registers that write through to the palette, Layer 2,
/// tilemap, sprite, audio or paging devices are that device's state and are captured
/// there, so nothing is stored twice and nothing is restored in the wrong order.
/// </remarks>
public sealed record ZxNextRegisterFileState(
    byte Peripheral1Settings,
    byte Peripheral2Settings,
    byte Peripheral3Settings,
    byte Peripheral4Settings,
    byte Peripheral5Settings,
    byte UserRegister,
    byte MachineTiming,
    byte MachineType,
    bool ExpansionInterruptEnabled,
    bool UlaInterruptEnabled,
    bool LineInterruptEnabled,
    bool LineInterruptMsb,
    byte LineInterruptLsb,
    int LineInterruptGeneration,
    byte InterruptControl,
    uint InternalPortDecoding,
    ushort NmiReturnAddress,
    bool StacklessNmiReturnPending,
    bool PendingDivMmcNmi,
    bool PendingMultifaceNmi,
    bool DivMmcNmiCause,
    bool MultifaceNmiCause,
    bool DisplayTimingLocked,
    bool PeripheralReset,
    byte[] UlaClip,
    byte UlaClipIndex,
    byte TimexPort,
    byte UlaScrollX,
    byte UlaScrollY,
    byte LoresScrollX,
    byte LoresScrollY,
    byte LoresControl,
    ZxNextResetKind LastResetKind,
    ZxNextResetKind? PendingReset)
{
    /// <summary>
    /// What was added after version 1 of the state format; null in a file from before it.
    /// </summary>
    /// <remarks>
    /// <see cref="PendingDivMmcNmi"/> and <see cref="PendingMultifaceNmi"/> are still written,
    /// as "latched and not yet given to the CPU", but the NMI is restored from here.
    /// </remarks>
    public ZxNextRegisterFileExtendedState? Extended { get; init; }
}

/// <summary>
/// NextReg state added after version 1 of the state format.
/// </summary>
/// <param name="ResetType">NextReg $02's three-bit reset-type shift register.</param>
/// <param name="NmiSource">The NMI latch: which device the active NMI belongs to.</param>
/// <param name="NmiPhase">Where that NMI is: waiting for the CPU, for $0066, or held.</param>
/// <param name="JoystickIoMode">NextReg $0B.</param>
/// <param name="BusPortDecoding">NextRegs $86-$89, $89 in the top byte.</param>
/// <param name="I2sControl">NextReg $A2 as written.</param>
/// <param name="ContentionDisableBefore">
/// NextReg $08 bit 6 as in effect before its last write took effect.
/// </param>
/// <param name="ContentionDisableAppliesAt">
/// The master tick at which that write takes effect: the end of the contention window it was
/// made in.
/// </param>
/// <param name="ScandoublerEffective">
/// NextReg $05 bit 0 as copied at the last frame sync; null in a file from before it, when
/// the written bit is used.
/// </param>
public readonly record struct ZxNextRegisterFileExtendedState(
    byte ResetType,
    ZxNextNmiSource NmiSource,
    ZxNextNmiPhase NmiPhase,
    byte JoystickIoMode,
    uint BusPortDecoding,
    byte I2sControl,
    bool ContentionDisableBefore = false,
    ulong ContentionDisableAppliesAt = 0,
    bool? ScandoublerEffective = null);

/// <summary>
/// The raster cadence and the interrupt line, including where the beam is.
/// </summary>
/// <remarks>
/// Derived profile values are carried rather than recomputed, so a restore cannot be
/// perturbed by a register write landing between putting the profile back and putting the
/// schedule back.
/// </remarks>
public sealed record ZxNextUlaTimingState(
    byte TimingProfile,
    bool SixtyHertz,
    ZxNextUlaTimingProfile Profile,
    int ProfileGeneration,
    int PixelsPerLine,
    int LinesPerFrame,
    ulong MasterTicksPerFrame,
    ulong InterruptOffsetMasterTicks,
    int InterruptPulseCpuTstates,
    ulong FrameNumber,
    ulong FrameStartMasterTick,
    ulong InterruptCount,
    ulong LineInterruptCount,
    ulong LastLineInterruptMasterTick,
    bool InterruptLineActive,
    ulong NextFrameTick,
    ulong NextInterruptTick,
    ulong NextLineInterruptTick,
    ulong InterruptEndTick,
    ulong NextEventTick,
    bool ScheduledLineInterruptEnabled,
    ushort ScheduledLineInterruptValue,
    int LineInterruptGeneration);

/// <summary>
/// Where the beam is, and the picture drawn so far.
/// </summary>
/// <remarks>
/// SNP-002 asks for the "exact master/raster phase", and this is it. Without the phase a
/// restored machine's beam disagrees with its clock and every frame afterwards lands
/// somewhere different; without the buffers the machine resumes showing whatever the
/// previous occupant of the renderer had drawn.
/// </remarks>
public sealed record ZxNextRasterState(
    bool UlaGeneratorIsPlain,
    ZxNextUlaTimingProfile Profile,
    int ProfileGeneration,
    ulong ProfileMasterTicksPerFrame,
    ulong BeamMasterTick,
    ulong AbsoluteMasterTick,
    ulong WorkingFrameNumber,
    ulong CompletedFrameNumber,
    int RasterHorizontal,
    int RasterVertical,
    int RasterSubpixel,
    bool CurrentRasterPixelRendered,
    bool CurrentFetchPhaseProcessed,
    bool CurrentVideoMemoryTickProcessed,
    int[] FrameBuffer,
    int[] WorkingBuffer)
{
    /// <summary>The border colour taken for the current 8-pixel group; default in older files.</summary>
    public ZxNextBorderLatchState BorderLatch { get; init; }
}

/// <summary>
/// The border colour as zxula.vhd loads it with the pixel shift register, once per 8-pixel group.
/// </summary>
/// <param name="Group">Which group it was taken for (line and column); 0 for none.</param>
/// <param name="Index">The $FE border colour then.</param>
/// <param name="Hires">Whether Timex hi-res was on, which gives the border the ink's bright paper.</param>
/// <param name="Ink">The port $FF hi-res ink then.</param>
public readonly record struct ZxNextBorderLatchState(int Group, byte Index, bool Hires, byte Ink);

/// <summary>The eight palettes and the write cursor into them.</summary>
/// <remarks>
/// The ARGB copies are carried rather than recomputed: they are derived, but recomputing
/// them would mean re-running the expansion for two thousand entries on every restore, and
/// a greenzone restores constantly.
/// </remarks>
public sealed record ZxNextPaletteState(
    ushort[][] Palettes,
    int[][] ArgbPalettes,
    byte StoredNineBitValue,
    bool NineBitSecondWrite,
    byte Index,
    byte UlaNextAttributeMask,
    byte Control,
    byte FallbackColor,
    byte UlaControl);

/// <summary>Layer 2's banks, scroll, clip window and mapping.</summary>
public sealed record ZxNextLayer2State(
    byte[] Clip,
    byte ClipIndex,
    byte MapSegment,
    byte MapOffset,
    byte ActiveBank,
    byte ShadowBank,
    byte GlobalTransparencyColor,
    byte SpriteAndLayersControl,
    byte ScrollXLeastSignificant,
    byte ScrollXMostSignificant,
    byte ScrollY,
    byte Control,
    bool DisplayEnabled,
    bool MapWritesEnabled,
    bool MapReadsEnabled,
    bool MapShadowBank);

/// <summary>
/// The tilemap's registers and the state of its fetch pipeline part way along a line.
/// </summary>
/// <remarks>
/// The pipeline is the reason this cannot wait for a frame boundary: a tilemap captured
/// mid-line has a half-filled pixel buffer and a fetch in flight, and a restore that
/// dropped them would show a torn line and mis-time the next group of fetches. Caches
/// keyed on a loaded map index are not carried - they are invalidated instead, and rebuild
/// from the registers.
/// </remarks>
public sealed record ZxNextTilemapState(
    byte[] Clip,
    byte ClipIndex,
    byte ScrollXMostSignificant,
    byte ScrollXLeastSignificant,
    byte ScrollY,
    byte TransparencyIndex,
    byte Control,
    byte DefaultAttribute,
    byte MapBase,
    byte TileBase,
    ushort[] TimedPixels,
    byte FetchState,
    bool RequestInFlight,
    int TimedLine,
    int LatestPrefetchLimit,
    int NextGroupStart,
    int FetchGroupStart,
    int FetchPixel,
    int CurrentMapIndex,
    byte FetchedTile,
    byte FetchedAttribute,
    bool FetchEightyColumns,
    bool FetchStripAttributes,
    bool FetchTextMode,
    bool FetchFiveHundredTwelveTiles,
    bool FetchForceAboveUla,
    byte FetchDefaultAttribute,
    byte FetchTransparency,
    byte FetchMapBase,
    byte FetchTileBase,
    ulong CompletedCharacterFetches,
    ulong FetchedPixelCount,
    ulong TimedPixelDeadlineMisses);

/// <summary>
/// Sprite patterns, attributes, the line being built and the preparation engine's phase.
/// </summary>
/// <remarks>
/// Patterns and attributes are the authority; everything the device resolves from them is
/// a cache keyed on a revision counter, so a restore bumps the revision and lets those
/// rebuild rather than trying to carry them. The line buffers and the preparation phase
/// are not caches - they are work in progress - so they are carried.
/// </remarks>
public sealed record ZxNextSpriteState(
    byte[] Patterns,
    byte[] Attributes,
    byte[] Clip,
    byte ClipIndex,
    byte[] LinePaletteIndices,
    bool[] LineOpaque,
    byte[] DisplayPaletteIndices,
    bool[] DisplayOpaque,
    byte[] BuildPaletteIndices,
    bool[] BuildOpaque,
    byte PortSprite,
    byte PortAttributeIndex,
    byte NextRegisterSprite,
    int PatternAddress,
    byte Status,
    bool IdLockstep,
    int Revision,
    int CachedLine,
    byte CachedControl,
    int PreparationState,
    byte TransparencyIndex,
    ulong SpritePreparationMasterTicks,
    ulong LastPreparationMasterTicks,
    int LastQualifiedSpriteCount,
    ulong OvertimeCount);
