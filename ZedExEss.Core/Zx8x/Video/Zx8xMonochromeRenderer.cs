using ZedExEss.Zx8x.Core;
using ZedExEss.Zx8x.Memory;

namespace ZedExEss.Zx8x.Video;

/// <summary>
/// Builds the 256x192 monochrome picture directly from timed display-file M1
/// fetches. A byte per pixel keeps the portable core independent of host pixel formats.
/// </summary>
public sealed class Zx8xMonochromeRenderer(
    Zx8xMemory memory,
    Zx8xVideoTiming timing,
    Zx8xHighResolutionMode highResolutionMode = Zx8xHighResolutionMode.Sinclair) : IZx8xRasterSink
{
    public const int Width = 256;
    public const int Height = 192;
    private const byte White = 0xFF;
    private const byte Black = 0x00;
    // The ROM seeds R so the first paper character is fetched with R=DFh;
    // the following 31 M1 cycles select the remaining byte columns. Using this
    // hardware column counter avoids accumulating instruction-boundary jitter
    // into a diagonal picture when the same character row is repeated eight times.
    private const int FirstDisplayRefresh = 0x5F;

    private readonly Zx8xMemory _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    private readonly Zx8xVideoTiming _timing = timing;
    private readonly Zx8xHighResolutionMode _highResolutionMode = highResolutionMode;
    private readonly object _frameLock = new();
    private byte[] _frontBuffer = CreateBlankBuffer();
    private byte[] _backBuffer = CreateBlankBuffer();
    private byte[] _cassetteBuffer = CreateBlankBuffer();
    private bool _hasRenderingFrame;
    private bool _cassetteOutputHigh = true;
    private bool _cassetteActivityActive;
    private ulong _cassetteRasterOrigin;
    private ulong _cassetteRasterCursor;
    private ulong _nextCassetteFrameBoundary = (ulong)timing.NominalTstatesPerFrame;

    public long CompletedFrameNumber { get; private set; }
    public long DisplayFetchCount { get; private set; }
    public ReadOnlyMemory<byte> FrameBuffer => _frontBuffer;
    public Zx8xHighResolutionMode HighResolutionMode => _highResolutionMode;

    public void BeginFrame(long frameNumber)
    {
        lock (_frameLock)
        {
            if (_hasRenderingFrame)
            {
                (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);
                CompletedFrameNumber++;
            }

            Array.Fill(_backBuffer, White);
            _hasRenderingFrame = true;
        }
    }

    /// <summary>
    /// Records a transition of the shared video-sync/MIC signal.  LOAD and SAVE
    /// stripes are consequently derived from the ROM's actual I/O timing rather
    /// than from knowledge of a particular firmware routine.
    /// </summary>
    public void OnCassetteOutputLevelChanged(ulong tstate, bool high)
    {
        AdvanceCassetteRasterTo(tstate, commitCurrentInterval: true);
        _cassetteOutputHigh = high;
    }

    /// <summary>Starts or stops presentation of the composite tape raster.</summary>
    public void OnCassetteOutputActivityChanged(ulong tstate, bool active)
    {
        AdvanceCassetteRasterTo(tstate, commitCurrentInterval: true);
        _cassetteActivityActive = active;
    }

    /// <summary>
    /// Advances the free-running television raster.  Work is deferred until an
    /// edge or frame boundary, so normal emulation pays only a comparison here.
    /// </summary>
    public void AdvanceCassetteRasterTo(ulong tstate)
    {
        AdvanceCassetteRasterTo(tstate, commitCurrentInterval: false);
    }

    private void AdvanceCassetteRasterTo(ulong tstate, bool commitCurrentInterval)
    {
        if (tstate < _cassetteRasterCursor)
        {
            Reset(tstate);
            return;
        }

        if (tstate < _nextCassetteFrameBoundary)
        {
            if (commitCurrentInterval)
            {
                FillCassetteInterval(_cassetteRasterCursor, tstate, _cassetteOutputHigh);
            }

            return;
        }

        while (tstate >= _nextCassetteFrameBoundary)
        {
            FillCassetteInterval(_cassetteRasterCursor, _nextCassetteFrameBoundary, _cassetteOutputHigh);
            _cassetteRasterCursor = _nextCassetteFrameBoundary;
            PublishCassetteFrameIfActive();
            Array.Fill(_cassetteBuffer, White);
            _nextCassetteFrameBoundary += (ulong)_timing.NominalTstatesPerFrame;
        }

        if (commitCurrentInterval)
        {
            FillCassetteInterval(_cassetteRasterCursor, tstate, _cassetteOutputHigh);
        }
    }

    public void OnRasterFetch(in Zx8xRasterFetch fetch)
    {
        DisplayFetchCount++;
        if (!_hasRenderingFrame)
        {
            return;
        }

        int y = fetch.RasterLine - _timing.UpperBorderLines;
        if ((uint)y >= Height)
        {
            return;
        }

        Zx8xDisplayFetch displayFetch = fetch.Fetch;
        int x;
        ushort glyphAddress;
        // Installing/enabling WRX does not disable the Sinclair character
        // generator. The modification only supplies refresh-addressed pixel
        // data after software moves I to 20h or above; the ROM's normal 1Eh/
        // 1Fh character pages must continue to render conventionally.
        bool wrxRasterActive = _highResolutionMode == Zx8xHighResolutionMode.Wrx
            && displayFetch.I >= 0x20;
        if (wrxRasterActive)
        {
            // WRX does not use the display byte as a character number. The RAM
            // modification responds during refresh, so IR itself selects one
            // eight-pixel value. Unlike the ROM display loop, R is therefore an
            // address byte rather than a reliable horizontal column counter.
            // LineTstate is measured from the end of horizontal sync, whereas
            // the host surface is cropped to the 256-pixel active picture. The
            // normal character path implicitly removes this lead-in through its
            // R-based column counter; WRX must remove it explicitly.
            x = (fetch.LineTstate - Zx8xVideoTiming.DisplayStartTstate)
                * Zx8xVideoTiming.PixelClocksPerTstate;
            glyphAddress = (ushort)((displayFetch.I << 8) | displayFetch.R);
        }
        else
        {
            int column = (displayFetch.R & 0x7F) - FirstDisplayRefresh;
            if ((uint)column >= Width / 8)
            {
                return;
            }

            x = column * 8;
            // Character code drives A3-A8, so I bit 0 is not part of the ROM
            // page selection. Masking it also matches odd-I pseudo-hires code.
            glyphAddress = (ushort)(((displayFetch.I & 0xFE) << 8)
                | (displayFetch.CharacterCode << 3)
                | fetch.CharacterLine);
        }

        if ((uint)x > Width - 8)
        {
            return;
        }
        byte glyph = _memory.Read(glyphAddress);
        bool inverse = displayFetch.Inverse;
        int destination = y * Width + x;

        for (int pixel = 0; pixel < 8; pixel++)
        {
            bool ink = (glyph & (0x80 >> pixel)) != 0;
            if (inverse)
            {
                ink = !ink;
            }

            _backBuffer[destination + pixel] = ink ? Black : White;
        }
    }

    public void CopyFrame(Span<byte> destination)
    {
        if (destination.Length < _frontBuffer.Length)
        {
            throw new ArgumentException($"Destination requires at least {_frontBuffer.Length} bytes.", nameof(destination));
        }

        lock (_frameLock)
        {
            _frontBuffer.AsSpan().CopyTo(destination);
        }
    }

    /// <summary>Copies the monochrome frame into the BGRA format used by desktop presenters.</summary>
    public void CopyBgraFrame(Span<int> destination)
    {
        if (destination.Length < _frontBuffer.Length)
        {
            throw new ArgumentException($"Destination requires at least {_frontBuffer.Length} pixels.", nameof(destination));
        }

        lock (_frameLock)
        {
            for (int i = 0; i < _frontBuffer.Length; i++)
            {
                destination[i] = _frontBuffer[i] == Black
                    ? unchecked((int)0xFF000000)
                    : unchecked((int)0xFFFFFFFF);
            }
        }
    }

    public void Reset(ulong tstate = 0)
    {
        lock (_frameLock)
        {
            Array.Fill(_frontBuffer, White);
            Array.Fill(_backBuffer, White);
            Array.Fill(_cassetteBuffer, White);
            _hasRenderingFrame = false;
            _cassetteOutputHigh = true;
            _cassetteActivityActive = false;
            _cassetteRasterOrigin = tstate;
            _cassetteRasterCursor = tstate;
            _nextCassetteFrameBoundary = tstate + (ulong)_timing.NominalTstatesPerFrame;
            CompletedFrameNumber = 0;
            DisplayFetchCount = 0;
        }
    }

    private void FillCassetteInterval(ulong start, ulong end, bool high)
    {
        if (high || end <= start)
        {
            _cassetteRasterCursor = end;
            return;
        }

        ulong frameTstates = (ulong)_timing.NominalTstatesPerFrame;
        ulong lineTstates = (ulong)_timing.TstatesPerLine;
        while (start < end)
        {
            ulong frameOffset = (start - _cassetteRasterOrigin) % frameTstates;
            int line = (int)(frameOffset / lineTstates);
            int lineTstate = (int)(frameOffset % lineTstates);
            ulong lineEnd = start + lineTstates - (ulong)lineTstate;
            ulong segmentEnd = Math.Min(end, lineEnd);

            int y = line - _timing.UpperBorderLines;
            if ((uint)y < Height && lineTstate < _timing.DisplayTstates)
            {
                int visibleEndTstate = Math.Min(
                    _timing.DisplayTstates,
                    lineTstate + (int)(segmentEnd - start));
                int x = lineTstate * Zx8xVideoTiming.PixelClocksPerTstate;
                int width = (visibleEndTstate - lineTstate) * Zx8xVideoTiming.PixelClocksPerTstate;
                if (width > 0)
                {
                    Array.Fill(_cassetteBuffer, Black, y * Width + x, width);
                }
            }

            start = segmentEnd;
        }

        _cassetteRasterCursor = end;
    }

    private void PublishCassetteFrameIfActive()
    {
        if (!_cassetteActivityActive)
        {
            return;
        }

        lock (_frameLock)
        {
            (_frontBuffer, _cassetteBuffer) = (_cassetteBuffer, _frontBuffer);
            _hasRenderingFrame = false;
            CompletedFrameNumber++;
        }
    }

    private static byte[] CreateBlankBuffer()
    {
        var buffer = new byte[Width * Height];
        Array.Fill(buffer, White);
        return buffer;
    }
}
