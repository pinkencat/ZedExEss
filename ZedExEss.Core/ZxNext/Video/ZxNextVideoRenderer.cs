using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Memory;
using ZedExEss.ZxNext.Ports;
using System.Runtime.CompilerServices;

namespace ZedExEss.ZxNext.Video;

/// <summary>Cycle-accounted legacy ULA layer on the stable 640x512 Next surface.</summary>
/// <remarks>
/// The renderer follows the 7 MHz raster on the absolute clk28 timeline. Border state
/// is sampled as the beam enters each raster pixel and bitmap/attribute bytes are
/// latched ahead of their output position, so later CPU writes cannot alter the past.
/// </remarks>
public sealed class ZxNextVideoRenderer
{
    public const int FrameWidth = 640;
    public const int FrameHeight = 256;

    /// <summary>
    /// Vertical stretch a presenter must apply to this surface. Every logical raster
    /// line produces exactly one buffer row; the doubled scanline the hardware shows
    /// carries no extra information, so it is applied at presentation time instead of
    /// being stored twice.
    /// </summary>
    public const int DisplayHeightScale = 2;
    public const int LogicalWidth = 320;
    public const int LogicalHeight = 256;
    public const int UlaWidth = 256;
    public const int UlaHeight = 192;
    public const int UlaLeft = 64;
    public const int UlaTop = 32;

    private const int HorizontalScale = 2;
    private const int VerticalScale = 1;
    private const int PixelBytesPerLine = UlaWidth / 8;
    private const int AttributeOffset = 0x1800;

    private readonly ZxNextMemoryBus _memory;
    private readonly ZxNextPagingController _paging;
    private readonly ZxNextPortBus _ports;
    private readonly ZxNextRegisterFile _registers;
    private readonly ZxNextPaletteDevice _palettes;
    private readonly ZxNextLayer2Device _layer2;
    private readonly ZxNextTilemapDevice _tilemap;
    private readonly ZxNextSpriteDevice _sprites;
    private readonly ZxNextVideoMemoryArbiter _videoMemoryArbiter;
    private readonly ZxNextUlaTiming _timing;
    private readonly ZxNextFloatingBus _floatingBus;
    private readonly object _frameBufferSync = new();
    private int[] _frameBuffer = new int[FrameWidth * FrameHeight];
    private int[] _workingBuffer = new int[FrameWidth * FrameHeight];
    private readonly byte[] _latchedPixels = new byte[UlaHeight * PixelBytesPerLine];
    private readonly byte[] _latchedAttributes = new byte[UlaHeight * PixelBytesPerLine];
    private readonly bool[] _latchedPixelBytes = new bool[UlaHeight * PixelBytesPerLine];
    private readonly bool[] _latchedAttributeBytes = new bool[UlaHeight * PixelBytesPerLine];
    private bool _ulaGeneratorIsPlain;
    private ZxNextUlaTimingProfile _profile;
    private int _profileGeneration;
    // The only profile value the raster loop cannot afford to recompute: the property
    // is a pair of checked 64-bit multiplies and the frame-end test reads it on every
    // iteration. The remaining geometry properties are plain field subtractions.
    private ulong _profileMasterTicksPerFrame;

    /// <summary>
    /// Per-horizontal ULA fetch schedule: -1 where the ULA does not fetch, otherwise the
    /// byte column in the upper bits with bit 0 set for an attribute fetch.
    /// </summary>
    /// <remarks>
    /// The schedule is a pure function of the horizontal counter once the timing profile
    /// is fixed, but it was rebuilt on every subpixel-2 edge by trialling four lead
    /// offsets in turn, three of which miss even on a fetch edge.
    /// </remarks>
    private int[] _ulaFetchClassification = Array.Empty<int>();
    private ulong _beamMasterTick;
    private ulong _absoluteMasterTick;
    private ulong _workingFrameNumber;
    private int _rasterHorizontal;
    private int _rasterVertical;
    private int _rasterSubpixel;
    private bool _currentRasterPixelRendered;
    private bool _currentFetchPhaseProcessed;
    private bool _currentVideoMemoryTickProcessed;

    public ZxNextVideoRenderer(
        ZxNextMemoryBus memory,
        ZxNextPagingController paging,
        ZxNextPortBus ports,
        ZxNextRegisterFile registers,
        ZxNextPaletteDevice palettes,
        ZxNextLayer2Device layer2,
        ZxNextTilemapDevice tilemap,
        ZxNextSpriteDevice sprites,
        ZxNextVideoMemoryArbiter videoMemoryArbiter,
        ZxNextUlaTiming timing,
        ZxNextFloatingBus floatingBus)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _paging = paging ?? throw new ArgumentNullException(nameof(paging));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _registers = registers ?? throw new ArgumentNullException(nameof(registers));
        _palettes = palettes ?? throw new ArgumentNullException(nameof(palettes));
        _layer2 = layer2 ?? throw new ArgumentNullException(nameof(layer2));
        _tilemap = tilemap ?? throw new ArgumentNullException(nameof(tilemap));
        _sprites = sprites ?? throw new ArgumentNullException(nameof(sprites));
        _videoMemoryArbiter = videoMemoryArbiter
            ?? throw new ArgumentNullException(nameof(videoMemoryArbiter));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
        _floatingBus = floatingBus ?? throw new ArgumentNullException(nameof(floatingBus));
        _profile = timing.Profile;
        _profileGeneration = timing.ProfileGeneration;
        _profileMasterTicksPerFrame = _profile.MasterTicksPerFrame;
        RebuildUlaFetchClassification();
        RenderFrame(0);
    }

    public int[] FrameBuffer => Volatile.Read(ref _frameBuffer);
    public ulong CompletedFrameNumber { get; private set; }
    public int SelectedScreenBank => (_paging.Port7Ffd & 0x08) != 0 ? 7 : 5;
    public byte BorderColorIndex => (byte)(_ports.UlaOutputLatch & 0x07);
    public bool RenderEnabled { get; set; } = true;
    internal bool UseFrameBufferSwap { get; set; } = true;
    internal bool UseUnobservedSubpixelCoalescing { get; set; } = true;
    internal bool UseTilemapRasterPixelBatching { get; set; } = true;

    /// <summary>
    /// Whether the raster loop may advance in one step through the parts of a line where
    /// nothing can observe it: horizontal blanking, and the whole of every line outside
    /// the visible window.
    /// </summary>
    /// <remarks>
    /// About two fifths of the raster is blanking, and the loop used to walk it a pixel at
    /// a time, paying the full dispatch for pixels that produce nothing, fetch nothing and
    /// arbitrate nothing. The sprite engine still gets every master tick, the line
    /// interrupt engine keeps its own master-tick count, and the span always stops short of
    /// the first point on a line that anything can notice - see
    /// <see cref="QuietWakeLeadPixels"/>. Kept switchable for the benchmark's reference
    /// cases.
    /// </remarks>
    internal bool UseQuietRasterSpans
    {
        get => _useQuietRasterSpans;
        set
        {
            _useQuietRasterSpans = value;
            _quietUntilBeamTick = 0;
        }
    }

    private bool _useQuietRasterSpans = true;

    /// <summary>
    /// The beam tick at which the current quiet span ends, or zero when there is none.
    /// </summary>
    /// <remarks>
    /// DMA and short CPU runs enter the raster loop a few ticks at a time, and deriving
    /// the span from the raster position on every entry cost more than walking the pixel
    /// did. Nothing inside a quiet span can change where it ends - the position is a
    /// function of the beam tick, a register write between entries is a barrier that
    /// cannot move the wake position earlier than the lead allows, and the frame edge
    /// resets the beam tick and this with it.
    /// </remarks>
    private ulong _quietUntilBeamTick;

    /// <summary>
    /// How many pixels before the visible window a quiet span has to wake the per-pixel
    /// loop on a visible line.
    /// </summary>
    /// <remarks>
    /// The earliest thing that can happen on a line is the tilemap's memory clock, which
    /// starts eight pixels before the visible window. The sprite line swap is one pixel
    /// before it, the ULA contention counter eleven pixels before the display and its first
    /// observable edge one pixel after that, and the first ULA fetch six pixels before the
    /// display - all later. On a line outside the visible window only the sprite swap
    /// exists, so <see cref="QuietWakeLeadPixelsInvisibleLine"/> wakes just for that.
    /// </remarks>
    private const int QuietWakeLeadPixels = 9;

    private const int QuietWakeLeadPixelsInvisibleLine = 1;
    internal bool UseTilemapMultiPixelSpans { get; set; } = true;
    public ulong FrameMasterTick => _beamMasterTick;

    public void Reset()
    {
        CompletedFrameNumber = 0;
        _workingFrameNumber = 0;
        _beamMasterTick = 0;
        _quietUntilBeamTick = 0;
        _absoluteMasterTick = 0;
        _rasterHorizontal = 0;
        _rasterVertical = 0;
        _rasterSubpixel = 0;
        _currentRasterPixelRendered = false;
        _currentFetchPhaseProcessed = false;
        _currentVideoMemoryTickProcessed = false;
        _profile = _timing.Profile;
        _profileGeneration = _timing.ProfileGeneration;
        _profileMasterTicksPerFrame = _profile.MasterTicksPerFrame;
        RebuildUlaFetchClassification();
        ClearDisplayLatches();
        _memory.ResetDisplayWriteTiming();
        _floatingBus.Reset();
        _sprites.ResetRasterTiming();
        _tilemap.ResetRasterTiming();
        _videoMemoryArbiter.Reset();
        RenderFrame(0);
    }

    /// <summary>Advances the beam and returns the number of completed frames crossed.</summary>
    internal int AdvanceMasterTicks(ulong masterTicks)
    {
        SynchronizeProfile();
        if (masterTicks == 0)
        {
            return 0;
        }

        int completedFrames = 0;
        ulong remaining = masterTicks;
        bool tilemapEnabled = _tilemap.Enabled;
        while (remaining > 0)
        {
            bool tilemapClockActive = tilemapEnabled && IsTilemapMemoryClockActive();
            if (UseTilemapRasterPixelBatching && tilemapClockActive)
            {
                AdvanceTilemapRasterSpan(ref remaining);
            }
            else
            {
                bool ulaFallingEdgeRequired = false;
                ulong quietTicks = 0;
                bool quiet = _useQuietRasterSpans && _beamMasterTick < _quietUntilBeamTick;
                if (quiet)
                {
                    quietTicks = _quietUntilBeamTick - _beamMasterTick;
                }
                else
                {
                    ulaFallingEdgeRequired = !UseUnobservedSubpixelCoalescing
                        || (IsUlaDisplayLine() && IsUlaFallingEdgeObservable());
                    if (_useQuietRasterSpans && !tilemapClockActive && !ulaFallingEdgeRequired)
                    {
                        // Visible pixels are the common case and are never quiet, so they
                        // are turned away here with two compares rather than a call.
                        int logicalX = _rasterHorizontal - _profile.VisibleStartHorizontal;
                        int logicalY = _rasterVertical - _profile.VisibleFirstLine;
                        bool visibleLine = (uint)logicalY < LogicalHeight;
                        bool visiblePixel = visibleLine
                            && (uint)(logicalX + QuietWakeLeadPixels) < (uint)(LogicalWidth + QuietWakeLeadPixels);
                        if (!visiblePixel
                            && TryGetQuietSpanMasterTicks(logicalX, logicalY, visibleLine, out quietTicks))
                        {
                            quiet = true;
                            _quietUntilBeamTick = _beamMasterTick + quietTicks;
                        }
                    }
                }

                if (quiet)
                {
                    ulong quietRun = Math.Min(remaining, quietTicks);
                    _sprites.AdvancePreparationMasterTicks(quietRun, _layer2.SpriteAndLayersControl);
                    AdvanceRasterCountersBulk((int)quietRun);
                    _beamMasterTick += quietRun;
                    _absoluteMasterTick += quietRun;
                    remaining -= quietRun;
                    _currentVideoMemoryTickProcessed = false;
                }
                else
                {
                RenderCurrentRasterPixelIfRequired();
                int subPixelTick = _rasterSubpixel;
                if (!_currentVideoMemoryTickProcessed
                    && (tilemapClockActive || (ulaFallingEdgeRequired && subPixelTick == 2)))
                {
                    // Advancing the tilemap state can consume the preceding response and
                    // create the next request, so request presence cannot be tested first.
                    ProcessVideoMemoryTick(subPixelTick == 2);
                }

                ulong untilNextEvent = tilemapClockActive
                    ? 1
                    : ulaFallingEdgeRequired && subPixelTick < 2
                        ? (ulong)(2 - subPixelTick)
                        : (ulong)(ZxNextUlaTimingProfile.MasterTicksPerRasterPixel - subPixelTick);
                ulong run = Math.Min(remaining, untilNextEvent);
                _sprites.AdvancePreparationMasterTicks(run, _layer2.SpriteAndLayersControl);
                AdvanceRasterCounters((int)run);
                _beamMasterTick += run;
                _absoluteMasterTick += run;
                remaining -= run;
                _currentVideoMemoryTickProcessed = false;

                if (_rasterSubpixel == 2
                    && !_currentFetchPhaseProcessed
                    && (tilemapClockActive || ulaFallingEdgeRequired))
                {
                    ProcessVideoMemoryTick(processUlaFetch: true);
                }
                }
            }

            if (_rasterSubpixel == 0)
            {
                _currentRasterPixelRendered = false;
                _currentFetchPhaseProcessed = false;
            }

            if (_beamMasterTick < _profileMasterTicksPerFrame)
            {
                continue;
            }

            _beamMasterTick = 0;
            _quietUntilBeamTick = 0;
            _rasterHorizontal = 0;
            _rasterVertical = 0;
            _rasterSubpixel = 0;
            _currentRasterPixelRendered = false;
            _currentFetchPhaseProcessed = false;
            _currentVideoMemoryTickProcessed = false;
            _workingFrameNumber++;
            CompletedFrameNumber = _workingFrameNumber;
            if (RenderEnabled)
            {
                PublishWorkingFrame();
            }

            ClearDisplayLatches();
            completedFrames++;
        }

        return completedFrames;
    }

    /// <summary>
    /// Services consecutive active tilemap pixels inside the current externally bounded
    /// span. Every clk28 edge and BRAM timestamp remains explicit, while raster-loop
    /// dispatch is paid once for as many complete 7 MHz pixels as the caller supplied.
    /// CPU, I/O, NextReg and DMA barriers can still truncate at any master tick.
    /// </summary>
    private void AdvanceTilemapRasterSpan(ref ulong remaining)
    {
        int logicalY = _rasterVertical - _profile.VisibleFirstLine;
        int activeEndHorizontal = _profile.VisibleStartHorizontal + LogicalWidth;
        while (remaining > 0
            && _rasterVertical - _profile.VisibleFirstLine == logicalY
            && _rasterHorizontal < activeEndHorizontal)
        {
            RenderCurrentRasterPixelIfRequired();
            int startSubpixel = _rasterSubpixel;
            int run = (int)Math.Min(
                remaining,
                (ulong)(ZxNextUlaTimingProfile.MasterTicksPerRasterPixel - startSubpixel));
            int logicalX = _rasterHorizontal - _profile.VisibleStartHorizontal;
            ulong firstMasterTick = _absoluteMasterTick;

            for (int offset = 0; offset < run; offset++)
            {
                int subpixel = startSubpixel + offset;
                if (offset == 0 && _currentVideoMemoryTickProcessed)
                {
                    continue;
                }

                // Roughly a third of the edges inside an active 80-column span fall
                // between character groups and change nothing. Subpixel 2 is never
                // skipped because it carries the ULA fetch and the floating-bus edge.
                if (subpixel != 2
                    && !_videoMemoryArbiter.HasPendingResponse
                    && _tilemap.IsMemoryClockInert(
                        logicalY,
                        (logicalX * 2) + (subpixel >= 2 ? 1 : 0)))
                {
                    continue;
                }

                ProcessVideoMemoryTick(
                    logicalX,
                    logicalY,
                    subpixel,
                    processUlaFetch: subpixel == 2,
                    firstMasterTick + (ulong)offset);
            }

            _sprites.AdvancePreparationMasterTicks(
                (ulong)run,
                _layer2.SpriteAndLayersControl);
            AdvanceRasterCounters(run);
            _beamMasterTick += (ulong)run;
            _absoluteMasterTick += (ulong)run;
            remaining -= (ulong)run;
            _currentVideoMemoryTickProcessed = false;
            if (_rasterSubpixel == 0)
            {
                _currentRasterPixelRendered = false;
                _currentFetchPhaseProcessed = false;
            }

            if (!UseTilemapMultiPixelSpans || _rasterSubpixel != 0)
            {
                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RenderCurrentRasterPixelIfRequired()
    {
        if (_currentRasterPixelRendered)
        {
            return;
        }

        BeginSpriteRasterLineIfRequired();
        // RenderEnabled suppresses host pixels only. ULA/tilemap bus clocks,
        // floating-bus state and sprite preparation remain guest-visible.
        if (RenderEnabled)
        {
            RenderCurrentRasterPixel();
        }

        _currentRasterPixelRendered = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsTilemapMemoryClockActive()
    {
        return _tilemap.RequiresMemoryClock(
            _rasterHorizontal - _profile.VisibleStartHorizontal,
            _rasterVertical - _profile.VisibleFirstLine);
    }

    /// <summary>
    /// Decides whether the subpixel-2 edge of the current raster pixel can change any
    /// guest-visible state, so the raster loop can run a whole pixel in one step when it
    /// cannot. On a display line the edge only matters when it carries a ULA fetch, when
    /// it is the hcount value that releases the latch, when a scheduled read is still
    /// waiting to be published, or when the latch is still driving and has to be
    /// released on leaving the display window.
    /// </summary>
    private bool IsUlaFallingEdgeObservable()
    {
        if (_videoMemoryArbiter.HasPendingResponse)
        {
            return true;
        }

        int ulaHorizontal = _rasterHorizontal - _profile.ContentionCounterStartHorizontal;
        if (ulaHorizontal < 0)
        {
            ulaHorizontal += _profile.PixelsPerLine;
        }

        if (ulaHorizontal >= ZxNextUlaTimingProfile.DisplayWidth)
        {
            return !_floatingBus.IsReleased;
        }

        return (ulaHorizontal & 0x0F) == 0x01
            || TryClassifyUlaFetch(_rasterHorizontal, _rasterVertical, out _, out _, out _);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsUlaDisplayLine() =>
        (uint)(_rasterVertical - _profile.FirstDisplayLine) < UlaHeight;

    /// <summary>
    /// Measures how far the beam can run from here before reaching a pixel that something
    /// can observe, or zero when this pixel is already one of those.
    /// </summary>
    /// <remarks>
    /// Quiet means outside the visible window on this line, and not yet at the wake
    /// position of the next line that has one. The frame edge is a hard stop as well, so
    /// the frame-end bookkeeping in the caller runs on exactly the tick it always did. The
    /// result lands on a pixel boundary, which is what lets the caller's subpixel-zero
    /// housekeeping run unchanged.
    /// </remarks>
    private bool TryGetQuietSpanMasterTicks(int logicalX, int logicalY, bool visibleLine, out ulong masterTicks)
    {
        masterTicks = 0;
        int lead = visibleLine ? QuietWakeLeadPixels : QuietWakeLeadPixelsInvisibleLine;
        if (visibleLine && logicalX >= -lead && logicalX < LogicalWidth)
        {
            return false;
        }

        if (!visibleLine && logicalX == -lead)
        {
            return false;
        }

        int wakeLine;
        int wakeHorizontal;
        if (logicalX < -lead)
        {
            wakeLine = _rasterVertical;
            wakeHorizontal = _profile.VisibleStartHorizontal - lead;
        }
        else
        {
            wakeLine = _rasterVertical + 1;
            bool nextVisible = (uint)(wakeLine - _profile.VisibleFirstLine) < LogicalHeight;
            wakeHorizontal = _profile.VisibleStartHorizontal
                - (nextVisible ? QuietWakeLeadPixels : QuietWakeLeadPixelsInvisibleLine);
        }

        long pixels = ((long)(wakeLine - _rasterVertical) * _profile.PixelsPerLine)
            + (wakeHorizontal - _rasterHorizontal);
        long ticks = (pixels * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel) - _rasterSubpixel;
        if (ticks <= 0)
        {
            return false;
        }

        ulong untilFrameEnd = _profileMasterTicksPerFrame - _beamMasterTick;
        masterTicks = Math.Min((ulong)ticks, untilFrameEnd);
        return masterTicks > 0;
    }

    /// <summary>Advances the raster counters by any number of ticks, crossing lines as needed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AdvanceRasterCountersBulk(int masterTicks)
    {
        // A DMA bus cycle or a short CPU run advances less than a pixel, and that case
        // has to stay as cheap as the single-pixel advance it replaced.
        int total = _rasterSubpixel + masterTicks;
        if (total < ZxNextUlaTimingProfile.MasterTicksPerRasterPixel)
        {
            _rasterSubpixel = total;
            return;
        }

        int pixels = total >> 2;
        _rasterSubpixel = total & 3;
        int horizontal = _rasterHorizontal + pixels;
        while (horizontal >= _profile.PixelsPerLine)
        {
            horizontal -= _profile.PixelsPerLine;
            _rasterVertical++;
        }

        _rasterHorizontal = horizontal;
    }

    private void AdvanceRasterCounters(int masterTicks)
    {
        int subpixel = _rasterSubpixel + masterTicks;
        if (subpixel < ZxNextUlaTimingProfile.MasterTicksPerRasterPixel)
        {
            _rasterSubpixel = subpixel;
            return;
        }

        _rasterSubpixel = subpixel - ZxNextUlaTimingProfile.MasterTicksPerRasterPixel;
        _rasterHorizontal++;
        if (_rasterHorizontal >= _profile.PixelsPerLine)
        {
            _rasterHorizontal = 0;
            _rasterVertical++;
        }
    }

    /// <summary>
    /// Produces an immediate diagnostic snapshot. Normal emulation uses AdvanceMasterTicks.
    /// </summary>
    public void RenderFrame(ulong frameNumber)
    {
        CompletedFrameNumber = frameNumber;
        _workingFrameNumber = frameNumber;
        if (!RenderEnabled)
        {
            return;
        }

        lock (_frameBufferSync)
        {
            RenderSnapshot(_frameBuffer, frameNumber);
            Array.Copy(_frameBuffer, _workingBuffer, _frameBuffer.Length);
        }
    }

    public void CopyFrame(Span<int> destination)
    {
        if (destination.Length != _frameBuffer.Length)
        {
            throw new ArgumentException(
                "Destination length does not match the Next framebuffer.",
                nameof(destination));
        }

        lock (_frameBufferSync)
        {
            _frameBuffer.CopyTo(destination);
        }
    }

    private void PublishWorkingFrame()
    {
        lock (_frameBufferSync)
        {
            if (UseFrameBufferSwap)
            {
                (_frameBuffer, _workingBuffer) = (_workingBuffer, _frameBuffer);
                return;
            }

            Array.Copy(_workingBuffer, _frameBuffer, _frameBuffer.Length);
        }
    }

    private void SynchronizeProfile()
    {
        int generation = _timing.ProfileGeneration;
        if (generation == _profileGeneration)
        {
            return;
        }

        _profileGeneration = generation;
        _profile = _timing.Profile;
        _profileMasterTicksPerFrame = _profile.MasterTicksPerFrame;
        RebuildUlaFetchClassification();
        _beamMasterTick = 0;
        _quietUntilBeamTick = 0;
        _absoluteMasterTick = _timing.FrameStartMasterTick;
        _rasterHorizontal = 0;
        _rasterVertical = 0;
        _rasterSubpixel = 0;
        _currentRasterPixelRendered = false;
        _currentFetchPhaseProcessed = false;
        _currentVideoMemoryTickProcessed = false;
        _workingFrameNumber = _timing.FrameNumber;
        ClearDisplayLatches();
        _floatingBus.Reset();
        _sprites.ResetRasterTiming();
        _tilemap.ResetRasterTiming();
        Array.Fill(_workingBuffer, _palettes.GetBorderColor(BorderColorIndex));
    }

    private void BeginSpriteRasterLineIfRequired()
    {
        // zxula_timing exposes wide hcount=$1ff one 7 MHz pixel before hcount zero.
        // sprites.vhd detects that value to swap the buffers and target vcount+1.
        if (_rasterHorizontal != _profile.VisibleStartHorizontal - 1)
        {
            return;
        }

        int currentWideY = (_rasterVertical - _profile.VisibleFirstLine) & 0x1FF;
        _sprites.BeginRasterLine(currentWideY);
    }

    /// <summary>
    /// True while the ULA generator is in its plain, unscrolled, standard-resolution
    /// configuration, so the per-pixel path can skip the LoRes, scroll and Timex work.
    /// Sampled once per raster pixel row alongside the other composition guards.
    /// </summary>
    private void RefreshUlaGeneratorShape() =>
        _ulaGeneratorIsPlain =
            !_registers.LoresEnabled
            && _registers.UlaScrollX == 0
            && _registers.UlaScrollY == 0
            && !_palettes.UlaFineScrollX
            && (GetEffectiveTimexMode() & 0x04) == 0;

    private void RenderCurrentRasterPixel()
    {
        int logicalX = _rasterHorizontal - _profile.VisibleStartHorizontal;
        int logicalY = _rasterVertical - _profile.VisibleFirstLine;
        if ((uint)logicalX >= LogicalWidth || (uint)logicalY >= LogicalHeight)
        {
            return;
        }

        // Only the ULA-only mix and the general mixer read the ULA colour; the tilemap,
        // Layer 2 and sprite single-layer mixes discard it, so running the generator for
        // them would cost a scroll mux, a clip test and two palette lookups per pixel for
        // a value nothing consumes.
        bool tilemapOnly = IsTilemapOnlyComposition();
        bool ulaOnly = !tilemapOnly && IsUlaOnlyComposition();
        bool layer2Only = !tilemapOnly && !ulaOnly && IsLayer2OnlyComposition();
        bool spriteOnly = !tilemapOnly && !ulaOnly && !layer2Only && IsSpriteOnlyComposition();
        bool needsUlaColor = ulaOnly || !(tilemapOnly || layer2Only || spriteOnly);

        ushort firstUlaColor = 0;
        ushort secondUlaColor = 0;
        bool ulaPixelEnabled = true;
        int displayX = _rasterHorizontal - _profile.DisplayStartHorizontal;
        int displayY = _rasterVertical - _profile.FirstDisplayLine;
        if (!needsUlaColor)
        {
            // Nothing to resolve.
        }
        else if ((uint)displayX >= UlaWidth || (uint)displayY >= UlaHeight)
        {
            firstUlaColor = _palettes.GetBorderPaletteColor(BorderColorIndex);
            secondUlaColor = firstUlaColor;
        }
        else
        {
            RefreshUlaGeneratorShape();
            ulaPixelEnabled = IsInsideUlaClip(displayX, displayY);
            GetUlaDisplayColors(
                displayX,
                displayY,
                (_workingFrameNumber & 0x10) != 0,
                out firstUlaColor,
                out secondUlaColor);
        }

        int outputX = logicalX * HorizontalScale;
        int outputY = logicalY * VerticalScale;
        int firstColor;
        int secondColor;
        if (tilemapOnly)
        {
            firstColor = ComposeTilemapOnlyPixel(outputX, logicalY);
            secondColor = ComposeTilemapOnlyPixel(outputX + 1, logicalY);
        }
        else if (ulaOnly)
        {
            bool ulaOutputEnabled = ulaPixelEnabled && _palettes.UlaOutputEnabled;
            firstColor = ulaOutputEnabled && !IsGloballyTransparent(firstUlaColor)
                ? ZxNextPaletteDevice.ToArgb(firstUlaColor)
                : _palettes.GetFallbackArgb();
            // Both halves carry the same colour unless a 14 MHz mode split them, so the
            // transparency test and the ARGB conversion are worth doing only once.
            secondColor = secondUlaColor == firstUlaColor
                ? firstColor
                : ulaOutputEnabled && !IsGloballyTransparent(secondUlaColor)
                    ? ZxNextPaletteDevice.ToArgb(secondUlaColor)
                    : _palettes.GetFallbackArgb();
        }
        else if (layer2Only)
        {
            ComposeLayer2OnlyPixelPair(outputX, logicalY, out firstColor, out secondColor);
        }
        else if (spriteOnly)
        {
            firstColor = ComposeSpriteOnlyPixel(logicalX, logicalY);
            secondColor = firstColor;
        }
        else
        {
            bool layer2Present = _layer2.TryGetPaletteIndexPair(
                outputX,
                logicalY,
                _memory.PhysicalMemory,
                out byte layer2First,
                out byte layer2Second);
            // The sprite layer is addressed in logical pixels, so both halves resolve
            // the same sprite and the same colour.
            bool spriteOpaque = _sprites.TryGetPreparedPaletteIndex(
                logicalX,
                logicalY,
                _layer2.SpriteAndLayersControl,
                out byte spriteIndex);
            ushort spriteColor = spriteOpaque
                ? _palettes.GetSpritePaletteColor(spriteIndex)
                : (ushort)0;
            firstColor = ComposePixelCore(
                outputX,
                logicalY,
                firstUlaColor,
                ulaPixelEnabled,
                usePreparedSprites: true,
                layer2Present,
                layer2First,
                spriteOpaque,
                spriteColor);
            secondColor = ComposePixelCore(
                outputX + 1,
                logicalY,
                secondUlaColor,
                ulaPixelEnabled,
                usePreparedSprites: true,
                layer2Present,
                layer2Second,
                spriteOpaque,
                spriteColor);
        }

        WritePixel(_workingBuffer, outputX, outputY, firstColor);
        WritePixel(_workingBuffer, outputX + 1, outputY, secondColor);
    }

    private void GetUlaDisplayColors(
        int displayX,
        int displayY,
        bool flashPhase,
        out ushort firstColor,
        out ushort secondColor)
    {
        if (_registers.LoresEnabled)
        {
            byte paletteIndex = GetLoresPaletteIndex(displayX, displayY);
            firstColor = _palettes.GetUlaPaletteColor(paletteIndex);
            secondColor = firstColor;
            return;
        }

        // Unscrolled standard resolution is what almost every program runs, and it is
        // the shape the generator had before LoRes, scrolling and Timex hi-res were
        // added. Resolve it without the nine-bit scroll mux, the 14 MHz half-pixel
        // split or the per-half mode test, all of which fold to identity here.
        if (_ulaGeneratorIsPlain)
        {
            int plainColumn = displayX >> 3;
            int plainIndex = (displayY * PixelBytesPerLine) + plainColumn;
            if (!_latchedPixelBytes[plainIndex])
            {
                LatchPixelByte(displayY, plainColumn, plainIndex);
            }

            if (!_latchedAttributeBytes[plainIndex])
            {
                LatchAttributeByte(displayY, plainColumn, plainIndex);
            }

            GetAttributePaletteColors(
                _latchedAttributes[plainIndex],
                flashPhase,
                out ushort plainInk,
                out ushort plainPaper);
            firstColor = (_latchedPixels[plainIndex] & (0x80 >> (displayX & 0x07))) != 0
                ? plainInk
                : plainPaper;
            secondColor = firstColor;
            return;
        }

        int sourceY = GetScrolledUlaY(displayY);
        int source14X = GetScrolledUla14MhzX(displayX << 1);
        bool hires = (GetEffectiveTimexMode() & 0x04) != 0;
        firstColor = GetLatchedUlaColor(displayY, source14X, sourceY, flashPhase, hires);

        // At standard resolution both 14 MHz half-pixels of one logical pixel select the
        // same byte column, the same attribute and the same bit, so the second half can
        // only differ when hi-res is active or when the fine-scroll bit has made
        // source14X odd. displayX and the coarse scroll both contribute even values, so
        // that parity is exactly the fine-scroll bit.
        secondColor = hires || (source14X & 0x01) != 0
            ? GetLatchedUlaColor(displayY, (source14X + 1) & 0x1FF, sourceY, flashPhase, hires)
            : firstColor;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort GetLatchedUlaColor(
        int displayY,
        int source14X,
        int sourceY,
        bool flashPhase,
        bool hires)
    {
        int byteColumn = source14X >> 4;
        int latchIndex = (displayY * PixelBytesPerLine) + byteColumn;
        if (!_latchedPixelBytes[latchIndex])
        {
            LatchPixelByte(sourceY, byteColumn, latchIndex);
        }

        if (!_latchedAttributeBytes[latchIndex])
        {
            LatchAttributeByte(sourceY, byteColumn, latchIndex);
        }

        byte pixels = _latchedPixels[latchIndex];
        byte attributeOrSecondPixels = _latchedAttributes[latchIndex];
        if (hires)
        {
            byte ink = (byte)((_registers.TimexPort >> 3) & 0x07);
            byte attribute = (byte)(0x40 | ((ink ^ 0x07) << 3) | ink);
            GetAttributePaletteColors(attribute, flashPhase, out ushort inkColor, out ushort paperColor);
            byte selectedPixels = (source14X & 0x08) == 0
                ? pixels
                : attributeOrSecondPixels;
            int mask = 0x80 >> (source14X & 0x07);
            return (selectedPixels & mask) != 0 ? inkColor : paperColor;
        }

        GetAttributePaletteColors(
            attributeOrSecondPixels,
            flashPhase,
            out ushort standardInk,
            out ushort standardPaper);
        int standardMask = 0x80 >> ((source14X >> 1) & 0x07);
        return (pixels & standardMask) != 0 ? standardInk : standardPaper;
    }

    /// <summary>
    /// Mirrors lores.vhd from the pinned core. Coordinates are practical ULA
    /// coordinates, so the scroll registers operate at the documented half-pixel rate.
    /// </summary>
    private byte GetLoresPaletteIndex(int displayX, int displayY)
    {
        int x = (displayX + _registers.LoresScrollX) & 0xFF;
        int yPre = (displayY + _registers.LoresScrollY) & 0x1FF;
        int yHigh = (yPre >> 6) & 0x03;
        if (yPre >= 192)
        {
            yHigh = (yHigh + 1) & 0x03;
        }

        int y = (yHigh << 6) | (yPre & 0x3F);
        int address;
        byte data;
        if (_registers.LoresRadastanMode)
        {
            bool timexDisplayFile = (_registers.TimexPort & 0x01) != 0;
            bool displayFile = timexDisplayFile ^ _registers.LoresDisplayFileXor;
            address = (displayFile ? 0x2000 : 0)
                | ((y >> 1) << 6)
                | (x >> 2);
            data = ReadScreenByte(5, address);
            int pixel = (x & 0x02) == 0 ? data >> 4 : data & 0x0F;
            int paletteHigh = _palettes.UlaPlusEnabled && !_palettes.UlaNextEnabled
                ? 0x0C | (_registers.LoresPaletteOffset & 0x03)
                : _registers.LoresPaletteOffset;
            return (byte)((paletteHigh << 4) | pixel);
        }

        int addressBeforeGap = ((y >> 1) << 7) | (x >> 1);
        address = y >= 96 ? (addressBeforeGap + 0x0800) & 0x3FFF : addressBeforeGap;
        data = ReadScreenByte(5, address);
        int high = ((data >> 4) + _registers.LoresPaletteOffset) & 0x0F;
        return (byte)((high << 4) | (data & 0x0F));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsInsideUlaClip(int displayX, int displayY) =>
        displayX >= _registers.UlaClipX1
        && displayX <= _registers.UlaClipX2
        && displayY >= _registers.UlaClipY1
        && displayY <= _registers.UlaClipY2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetEffectiveTimexMode() => SelectedScreenBank == 7
        ? 0
        : _registers.TimexVideoMode;

    private void ProcessVideoMemoryTick(bool processUlaFetch)
    {
        int logicalX = _rasterHorizontal - _profile.VisibleStartHorizontal;
        int logicalY = _rasterVertical - _profile.VisibleFirstLine;
        ProcessVideoMemoryTick(
            logicalX,
            logicalY,
            _rasterSubpixel,
            processUlaFetch,
            _absoluteMasterTick);
    }

    private void ProcessVideoMemoryTick(
        int logicalX,
        int logicalY,
        int subpixel,
        bool processUlaFetch,
        ulong absoluteMasterTick)
    {

        _videoMemoryArbiter.BeginClock(
            out bool tilemapResponseValid,
            out byte tilemapResponseData);
        int tilemapPacked = _tilemap.ClockMemory(
            tilemapResponseValid,
            tilemapResponseData,
            logicalX,
            logicalY,
            subpixel);

        int displayY = 0;
        int byteColumn = 0;
        bool attributeFetch = false;
        bool classifiedFetch = processUlaFetch
            && TryClassifyUlaFetch(
                _rasterHorizontal,
                _rasterVertical,
                out displayY,
                out byteColumn,
                out attributeFetch);
        // The request address and the latch both need the scrolled source coordinates,
        // and both used to derive them independently.
        int sourceY = 0;
        int sourceByteColumn = 0;
        if (classifiedFetch)
        {
            sourceY = GetScrolledUlaY(displayY);
            sourceByteColumn = GetScrolledUlaByteColumn(byteColumn);
        }

        int ulaPacked = classifiedFetch
            ? PackUlaMemoryRequest(sourceY, sourceByteColumn, attributeFetch)
            : -1;
        _tilemap.NotifyMemoryGrant(_videoMemoryArbiter.ScheduleTrustedPacked(
            absoluteMasterTick,
            ulaPacked,
            tilemapPacked));

        if (processUlaFetch)
        {
            byte? fetchedValue = classifiedFetch
                ? LatchClassifiedDisplayFetch(displayY, sourceY, sourceByteColumn, attributeFetch)
                : null;
            int ulaHorizontal = _rasterHorizontal - _profile.ContentionCounterStartHorizontal;
            if (ulaHorizontal < 0)
            {
                ulaHorizontal += _profile.PixelsPerLine;
            }

            _floatingBus.AdvanceUlaFallingEdge(
                ulaHorizontal,
                displayY,
                fetchedValue);
            _currentFetchPhaseProcessed = true;
        }

        _currentVideoMemoryTickProcessed = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryClassifyUlaFetch(
        int horizontal,
        int vertical,
        out int displayY,
        out int byteColumn,
        out bool attribute)
    {
        displayY = vertical - _profile.FirstDisplayLine;
        byteColumn = 0;
        attribute = false;
        if ((uint)displayY >= UlaHeight)
        {
            return false;
        }

        int[] schedule = _ulaFetchClassification;
        int classification = (uint)horizontal < (uint)schedule.Length
            ? schedule[horizontal]
            : -1;
        if (classification < 0)
        {
            return false;
        }

        byteColumn = classification >> 1;
        attribute = (classification & 0x01) != 0;
        return true;
    }

    private void RebuildUlaFetchClassification()
    {
        int pixelsPerLine = _profile.PixelsPerLine;
        if (_ulaFetchClassification.Length != pixelsPerLine)
        {
            _ulaFetchClassification = new int[pixelsPerLine];
        }

        for (int horizontal = 0; horizontal < pixelsPerLine; horizontal++)
        {
            int classification;
            if (TryGetFetchByteColumn(horizontal, evenByte: true, attribute: false, out int column)
                || TryGetFetchByteColumn(horizontal, evenByte: false, attribute: false, out column))
            {
                classification = column << 1;
            }
            else if (TryGetFetchByteColumn(horizontal, evenByte: true, attribute: true, out column)
                || TryGetFetchByteColumn(horizontal, evenByte: false, attribute: true, out column))
            {
                classification = (column << 1) | 0x01;
            }
            else
            {
                classification = -1;
            }

            _ulaFetchClassification[horizontal] = classification;
        }
    }

    private int PackUlaMemoryRequest(int sourceY, int sourceByteColumn, bool attribute)
    {
        int offset = GetUlaMemoryOffset(sourceY, sourceByteColumn, attribute);
        return SelectedScreenBank == 7 ? (offset | 0x10000) : offset;
    }

    private ZxNextVideoMemoryRequest CreateUlaMemoryRequest(
        int displayY,
        int byteColumn,
        bool attribute)
    {
        int sourceY = GetScrolledUlaY(displayY);
        int sourceByteColumn = GetScrolledUlaByteColumn(byteColumn);
        int offset = GetUlaMemoryOffset(sourceY, sourceByteColumn, attribute);
        return new ZxNextVideoMemoryRequest(
            SelectedScreenBank == 7
                ? ZxNextVideoMemoryBank.Bank7
                : ZxNextVideoMemoryBank.Bank5,
            offset);
    }

    private byte LatchClassifiedDisplayFetch(
        int displayY,
        int sourceY,
        int sourceByteColumn,
        bool attribute)
    {
        int latchIndex = (displayY * PixelBytesPerLine) + sourceByteColumn;
        if (attribute)
        {
            if (!_latchedAttributeBytes[latchIndex])
            {
                LatchAttributeByte(sourceY, sourceByteColumn, latchIndex);
            }

            return _latchedAttributes[latchIndex];
        }

        if (!_latchedPixelBytes[latchIndex])
        {
            LatchPixelByte(sourceY, sourceByteColumn, latchIndex);
        }

        return _latchedPixels[latchIndex];
    }

    private bool TryGetFetchByteColumn(
        int horizontal,
        bool evenByte,
        bool attribute,
        out int byteColumn)
    {
        int lead = attribute
            ? evenByte
                ? ZxNextUlaTimingProfile.EvenAttributeFetchLeadPixels
                : ZxNextUlaTimingProfile.OddAttributeFetchLeadPixels
            : evenByte
                ? ZxNextUlaTimingProfile.EvenPixelFetchLeadPixels
                : ZxNextUlaTimingProfile.OddPixelFetchLeadPixels;
        int firstOutput = _profile.DisplayStartHorizontal + (evenByte ? 0 : 8);
        int fetchDelta = horizontal - (firstOutput - lead);
        if (fetchDelta < 0 || (fetchDelta & 0x0F) != 0)
        {
            byteColumn = 0;
            return false;
        }

        byteColumn = ((fetchDelta >> 4) * 2) + (evenByte ? 0 : 1);
        return (uint)byteColumn < PixelBytesPerLine;
    }

    private void LatchPixelByte(int sourceY, int byteColumn, int latchIndex)
    {
        int screenBank = SelectedScreenBank;
        _latchedPixels[latchIndex] = ReadScreenByte(
            screenBank,
            GetUlaMemoryOffset(sourceY, byteColumn, attribute: false));
        _latchedPixelBytes[latchIndex] = true;
    }

    private void LatchAttributeByte(int sourceY, int byteColumn, int latchIndex)
    {
        int screenBank = SelectedScreenBank;
        _latchedAttributes[latchIndex] = ReadScreenByte(
            screenBank,
            GetUlaMemoryOffset(sourceY, byteColumn, attribute: true));
        _latchedAttributeBytes[latchIndex] = true;
    }

    /// <summary>Mirrors zxula.vhd's Timex display-file and attribute address mux.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetUlaMemoryOffset(int displayY, int byteColumn, bool attribute)
    {
        int mode = GetEffectiveTimexMode();
        int pixelOffset = GetPixelRowOffset(displayY) + byteColumn;
        if (!attribute)
        {
            return ((mode & 0x01) << 13) | pixelOffset;
        }

        if ((mode & 0x02) != 0)
        {
            return 0x2000 | pixelOffset;
        }

        int attributeOffset = AttributeOffset
            + ((displayY >> 3) * PixelBytesPerLine)
            + byteColumn;
        return ((mode & 0x01) << 13) | attributeOffset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetScrolledUla14MhzX(int output14X) =>
        (output14X
            + (_registers.UlaScrollX << 1)
            + (_palettes.UlaFineScrollX ? 1 : 0)) & 0x1FF;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetScrolledUlaByteColumn(int displayByteColumn) =>
        (displayByteColumn + (_registers.UlaScrollX >> 3)) & 0x1F;

    /// <summary>Applies zxula.vhd's nine-bit vertical sum and 192/256-line wrap mux.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetScrolledUlaY(int displayY)
    {
        int y = (displayY + _registers.UlaScrollY) & 0x1FF;
        if ((y & 0x180) == 0x180)
        {
            return ((~y >> 7) & 0x01) << 7 | (y & 0x7F);
        }

        if ((y & 0x100) != 0 || (y & 0xC0) == 0xC0)
        {
            return (((y >> 6) + 1) & 0x03) << 6 | (y & 0x3F);
        }

        return y & 0xFF;
    }

    private void RenderSnapshot(int[] destination, ulong frameNumber)
    {
        Array.Fill(destination, _palettes.GetFallbackArgb());
        bool flashPhase = (frameNumber & 0x10) != 0;
        for (int logicalY = 0; logicalY < LogicalHeight; logicalY++)
        {
            int displayY = logicalY - (UlaTop / VerticalScale);
            int destinationY = logicalY * VerticalScale;
            for (int logicalX = 0; logicalX < LogicalWidth; logicalX++)
            {
                int displayX = logicalX - (UlaLeft / HorizontalScale);
                ushort firstUlaColor;
                ushort secondUlaColor;
                bool ulaPixelEnabled;
                if ((uint)displayX >= UlaWidth || (uint)displayY >= UlaHeight)
                {
                    firstUlaColor = _palettes.GetBorderPaletteColor(BorderColorIndex);
                    secondUlaColor = firstUlaColor;
                    ulaPixelEnabled = true;
                }
                else
                {
                    ulaPixelEnabled = IsInsideUlaClip(displayX, displayY);
                    GetSnapshotUlaDisplayColors(
                        displayX,
                        displayY,
                        flashPhase,
                        out firstUlaColor,
                        out secondUlaColor);
                }

                int outputX = logicalX * HorizontalScale;
                WritePixel(destination, outputX, destinationY,
                    ComposePixel(outputX, logicalY, firstUlaColor, ulaPixelEnabled));
                WritePixel(destination, outputX + 1, destinationY,
                    ComposePixel(outputX + 1, logicalY, secondUlaColor, ulaPixelEnabled));
            }
        }
    }

    private void GetSnapshotUlaDisplayColors(
        int displayX,
        int displayY,
        bool flashPhase,
        out ushort firstColor,
        out ushort secondColor)
    {
        if (_registers.LoresEnabled)
        {
            byte paletteIndex = GetLoresPaletteIndex(displayX, displayY);
            firstColor = _palettes.GetUlaPaletteColor(paletteIndex);
            secondColor = firstColor;
            return;
        }

        int sourceY = GetScrolledUlaY(displayY);
        int source14X = GetScrolledUla14MhzX(displayX << 1);
        firstColor = GetSnapshotUlaColor(source14X, sourceY, flashPhase);
        secondColor = GetSnapshotUlaColor(
            (source14X + 1) & 0x1FF,
            sourceY,
            flashPhase);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort GetSnapshotUlaColor(int source14X, int sourceY, bool flashPhase)
    {
        int mode = GetEffectiveTimexMode();
        bool hires = (mode & 0x04) != 0;
        int byteColumn = source14X >> 4;
        int screenBank = SelectedScreenBank;
        byte pixels = ReadScreenByte(
            screenBank,
            GetUlaMemoryOffset(sourceY, byteColumn, attribute: false));
        byte attributeOrSecondPixels = ReadScreenByte(
            screenBank,
            GetUlaMemoryOffset(sourceY, byteColumn, attribute: true));
        if (hires)
        {
            byte ink = (byte)((_registers.TimexPort >> 3) & 0x07);
            byte attribute = (byte)(0x40 | ((ink ^ 0x07) << 3) | ink);
            GetAttributePaletteColors(attribute, flashPhase, out ushort inkColor, out ushort paperColor);
            byte selectedPixels = (source14X & 0x08) == 0
                ? pixels
                : attributeOrSecondPixels;
            int mask = 0x80 >> (source14X & 0x07);
            return (selectedPixels & mask) != 0 ? inkColor : paperColor;
        }

        GetAttributePaletteColors(
            attributeOrSecondPixels,
            flashPhase,
            out ushort standardInk,
            out ushort standardPaper);
        int standardMask = 0x80 >> ((source14X >> 1) & 0x07);
        return (pixels & standardMask) != 0 ? standardInk : standardPaper;
    }

    private byte ReadScreenByte(int bank, int offset)
    {
        int page = (bank * 2) + (offset / ZxNextPhysicalMemory.PageSize);
        int pageOffset = offset & (ZxNextPhysicalMemory.PageSize - 1);
        return _memory.ReadDisplayRam(page, pageOffset, _absoluteMasterTick);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void GetAttributePaletteColors(
        byte attribute,
        bool flashPhase,
        out ushort ink,
        out ushort paper)
    {
        ink = _palettes.GetUlaInkPaletteColor(attribute);
        paper = _palettes.GetUlaPaperPaletteColor(attribute);

        if (!_palettes.UlaNextEnabled && (attribute & 0x80) != 0 && flashPhase)
        {
            (ink, paper) = (paper, ink);
        }
    }

    private int ComposePixel(
        int outputX,
        int logicalY,
        ushort ulaColor,
        bool ulaPixelEnabled = true,
        bool usePreparedSprites = false)
    {
        bool layer2Present = _layer2.TryGetPaletteIndex(
            outputX,
            logicalY,
            _memory.PhysicalMemory,
            out byte layer2Index);
        bool spriteOpaque = usePreparedSprites
            ? _sprites.TryGetPreparedPaletteIndex(
                outputX >> 1,
                logicalY,
                _layer2.SpriteAndLayersControl,
                out byte spriteIndex)
            : _sprites.TryGetPaletteIndex(
                outputX >> 1,
                logicalY,
                _layer2.SpriteAndLayersControl,
                out spriteIndex);
        return ComposePixelCore(
            outputX,
            logicalY,
            ulaColor,
            ulaPixelEnabled,
            usePreparedSprites,
            layer2Present,
            layer2Index,
            spriteOpaque,
            spriteOpaque ? _palettes.GetSpritePaletteColor(spriteIndex) : (ushort)0);
    }

    /// <summary>
    /// Mixes one half-pixel from an already-resolved Layer 2 index and sprite colour.
    /// </summary>
    /// <remarks>
    /// Only the tilemap is addressed per half-pixel. Layer 2 shares an address between
    /// the two halves and the sprite layer is addressed in logical pixels, so the raster
    /// path resolves both once and calls this twice.
    /// </remarks>
    private int ComposePixelCore(
        int outputX,
        int logicalY,
        ushort ulaColor,
        bool ulaPixelEnabled,
        bool usePreparedSprites,
        bool layer2Present,
        byte layer2Index,
        bool spriteOpaque,
        ushort spriteColor)
    {
        bool ulaOpaque = ulaPixelEnabled
            && _palettes.UlaOutputEnabled
            && !IsGloballyTransparent(ulaColor);
        bool timedTilemapPixel = _tilemap.TryGetTimedPixel(
            outputX,
            logicalY,
            out bool tilemapOpaque,
            out byte tilemapIndex,
            out bool tilemapBelowUla);
        if (!timedTilemapPixel)
        {
            if (usePreparedSprites && _tilemap.Enabled)
            {
                _tilemap.RecordTimedPixelDeadlineMiss();
            }

            tilemapOpaque = _tilemap.TryGetPaletteIndex(
                outputX,
                logicalY,
                _memory.PhysicalMemory,
                out tilemapIndex,
                out tilemapBelowUla);
        }
        ushort tilemapColor = tilemapOpaque
            ? _palettes.GetTilemapPaletteColor(tilemapIndex, _tilemap.SecondPalette)
            : (ushort)0;
        bool ulaTileOpaque = tilemapOpaque && (!tilemapBelowUla || !ulaOpaque)
            || ulaOpaque;
        ushort ulaTileColor = tilemapOpaque && (!tilemapBelowUla || !ulaOpaque)
            ? tilemapColor
            : ulaColor;
        // One palette read serves both the transparency test and the colour.
        ushort layer2Raw = layer2Present
            ? _palettes.GetLayer2PaletteColor(layer2Index)
            : (ushort)0;
        bool layer2Opaque = layer2Present && !IsGloballyTransparent(layer2Raw);
        ushort layer2Color = layer2Opaque ? layer2Raw : (ushort)0;
        bool promoted = layer2Opaque && _palettes.IsLayer2PriorityColor(layer2Index);

        int layerPriority = _layer2.LayerPriority;
        ushort finalColor = layerPriority switch
        {
            0 when promoted => layer2Color,
            0 => spriteOpaque ? spriteColor
                : layer2Opaque ? layer2Color
                : ulaTileOpaque ? ulaTileColor
                : _palettes.GetFallbackPaletteColor(),
            1 or 3 => layer2Opaque ? layer2Color
                : (spriteOpaque && layerPriority == 1) ? spriteColor
                : ulaTileOpaque ? ulaTileColor
                : spriteOpaque ? spriteColor
                : _palettes.GetFallbackPaletteColor(),
            2 when promoted => layer2Color,
            2 => spriteOpaque ? spriteColor
                : ulaTileOpaque ? ulaTileColor
                : layer2Opaque ? layer2Color
                : _palettes.GetFallbackPaletteColor(),
            4 when promoted => layer2Color,
            4 => ulaTileOpaque ? ulaTileColor
                : spriteOpaque ? spriteColor
                : layer2Opaque ? layer2Color
                : _palettes.GetFallbackPaletteColor(),
            5 when promoted => layer2Color,
            5 => ulaTileOpaque ? ulaTileColor
                : layer2Opaque ? layer2Color
                : spriteOpaque ? spriteColor
                : _palettes.GetFallbackPaletteColor(),
            6 or 7 when promoted => BlendLayer2(layer2Color, ulaTileColor, ulaTileOpaque,
                subtractFive: layerPriority == 7),
            6 or 7 => ulaTileOpaque ? ulaTileColor
                : spriteOpaque ? spriteColor
                : layer2Opaque ? layer2Color
                : _palettes.GetFallbackPaletteColor(),
            _ => spriteOpaque ? spriteColor
                : ulaTileOpaque ? ulaTileColor
                : layer2Opaque ? layer2Color
                : _palettes.GetFallbackPaletteColor()
        };
        return ZxNextPaletteDevice.ToArgb(finalColor);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsTilemapOnlyComposition() =>
        _tilemap.Enabled
        && !_palettes.UlaOutputEnabled
        && !_layer2.DisplayEnabled
        && (_layer2.SpriteAndLayersControl & 0x01) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsUlaOnlyComposition() =>
        !_tilemap.Enabled
        && !_layer2.DisplayEnabled
        && (_layer2.SpriteAndLayersControl & 0x01) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsLayer2OnlyComposition() =>
        !_tilemap.Enabled
        && !_palettes.UlaOutputEnabled
        && _layer2.DisplayEnabled
        && (_layer2.SpriteAndLayersControl & 0x01) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ComposeLayer2OnlyPixelPair(
        int outputX,
        int logicalY,
        out int firstColor,
        out int secondColor)
    {
        if (!_layer2.TryGetPaletteIndexPair(
                outputX,
                logicalY,
                _memory.PhysicalMemory,
                out byte firstIndex,
                out byte secondIndex))
        {
            firstColor = _palettes.GetFallbackArgb();
            secondColor = firstColor;
            return;
        }

        firstColor = IsGloballyTransparent(_palettes.GetLayer2PaletteColor(firstIndex))
            ? _palettes.GetFallbackArgb()
            : _palettes.GetLayer2Argb(firstIndex);
        // Only the 640-wide modes give the halves different nibbles.
        secondColor = secondIndex == firstIndex
            ? firstColor
            : IsGloballyTransparent(_palettes.GetLayer2PaletteColor(secondIndex))
                ? _palettes.GetFallbackArgb()
                : _palettes.GetLayer2Argb(secondIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsSpriteOnlyComposition() =>
        !_tilemap.Enabled
        && !_palettes.UlaOutputEnabled
        && !_layer2.DisplayEnabled
        && (_layer2.SpriteAndLayersControl & 0x01) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ComposeSpriteOnlyPixel(int logicalX, int logicalY) =>
        _sprites.TryGetPreparedPaletteIndex(
            logicalX,
            logicalY,
            _layer2.SpriteAndLayersControl,
            out byte paletteIndex)
                ? _palettes.GetSpriteArgb(paletteIndex)
                : _palettes.GetFallbackArgb();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ComposeTilemapOnlyPixel(int outputX, int logicalY)
    {
        bool timedPixel = _tilemap.TryGetTimedPixel(
            outputX,
            logicalY,
            out bool opaque,
            out byte paletteIndex,
            out _);
        if (!timedPixel)
        {
            _tilemap.RecordTimedPixelDeadlineMiss();
            opaque = _tilemap.TryGetPaletteIndex(
                outputX,
                logicalY,
                _memory.PhysicalMemory,
                out paletteIndex,
                out _);
        }

        return opaque
            ? _palettes.GetTilemapArgb(paletteIndex, _tilemap.SecondPalette)
            : _palettes.GetFallbackArgb();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsGloballyTransparent(ushort color) =>
        (byte)(color >> 1) == _layer2.GlobalTransparencyColor;

    private static ushort BlendLayer2(
        ushort layer2Color,
        ushort ulaColor,
        bool ulaOpaque,
        bool subtractFive)
    {
        if (!ulaOpaque)
        {
            return layer2Color;
        }

        int red = BlendComponent((layer2Color >> 6) & 0x07, (ulaColor >> 6) & 0x07, subtractFive);
        int green = BlendComponent((layer2Color >> 3) & 0x07, (ulaColor >> 3) & 0x07, subtractFive);
        int blue = BlendComponent(layer2Color & 0x07, ulaColor & 0x07, subtractFive);
        return (ushort)((red << 6) | (green << 3) | blue);
    }

    private static int BlendComponent(int layer2, int ula, bool subtractFive)
    {
        int value = layer2 + ula - (subtractFive ? 5 : 0);
        return Math.Clamp(value, 0, 7);
    }

    private static int GetPixelRowOffset(int y) => ((y & 0xC0) << 5)
        | ((y & 0x07) << 8)
        | ((y & 0x38) << 2);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WritePixel(int[] destination, int x, int y, int color) =>
        destination[(y * FrameWidth) + x] = color;

    private void ClearDisplayLatches()
    {
        Array.Clear(_latchedPixelBytes, 0, _latchedPixelBytes.Length);
        Array.Clear(_latchedAttributeBytes, 0, _latchedAttributeBytes.Length);
    }

    /// <summary>Reads the beam position and the picture, for a snapshot.</summary>
    public ZxNextRasterState CaptureState()
    {
        lock (_frameBufferSync)
        {
            return new ZxNextRasterState(
                _ulaGeneratorIsPlain,
                _profile,
                _profileGeneration,
                _profileMasterTicksPerFrame,
                _beamMasterTick,
                _absoluteMasterTick,
                _workingFrameNumber,
                CompletedFrameNumber,
                _rasterHorizontal,
                _rasterVertical,
                _rasterSubpixel,
                _currentRasterPixelRendered,
                _currentFetchPhaseProcessed,
                _currentVideoMemoryTickProcessed,
                _frameBuffer.ToArray(),
                _workingBuffer.ToArray());
        }
    }

    /// <summary>Puts the beam and the picture back.</summary>
    public void RestoreState(ZxNextRasterState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_frameBufferSync)
        {
            _ulaGeneratorIsPlain = state.UlaGeneratorIsPlain;
            _profile = state.Profile;
            _profileGeneration = state.ProfileGeneration;
            _profileMasterTicksPerFrame = state.ProfileMasterTicksPerFrame;
            _beamMasterTick = state.BeamMasterTick;
            _quietUntilBeamTick = 0;
            _absoluteMasterTick = state.AbsoluteMasterTick;
            _workingFrameNumber = state.WorkingFrameNumber;
            CompletedFrameNumber = state.CompletedFrameNumber;
            _rasterHorizontal = state.RasterHorizontal;
            _rasterVertical = state.RasterVertical;
            _rasterSubpixel = state.RasterSubpixel;
            _currentRasterPixelRendered = state.CurrentRasterPixelRendered;
            _currentFetchPhaseProcessed = state.CurrentFetchPhaseProcessed;
            _currentVideoMemoryTickProcessed = state.CurrentVideoMemoryTickProcessed;

            if (state.FrameBuffer.Length == _frameBuffer.Length)
            {
                state.FrameBuffer.CopyTo(_frameBuffer, 0);
                state.WorkingBuffer.CopyTo(_workingBuffer, 0);
            }
        }
    }
}
