using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Memory;

using System.Runtime.CompilerServices;

namespace ZedExEss.ZxNext.Video;

/// <summary>
/// ZX Spectrum Next tilemap registers and functional pixel address generator.
/// </summary>
/// <remarks>
/// Tilemap memory is always fetched from legacy RAM bank 5 or the lower 8 KiB of
/// bank 7.  The initial renderer is functional rather than fetch-cycle scheduled;
/// its memory reads can therefore be moved behind the shared display arbiter
/// without changing the register or composition contract.
/// </remarks>
public sealed class ZxNextTilemapDevice
{
    private const int Bank5FirstPage = 10;
    private const int Bank7FirstPage = 14;
    private const int Bank5Size = 16 * 1024;
    private const int Bank7Size = 8 * 1024;

    private readonly byte[] _clip = [0x00, 0x9F, 0x00, 0xFF];
    // One entry per 640-wide output column, packed so a fetched pixel touches a single
    // cache line on store and a single one on composition. Four parallel arrays cost
    // four lines per pixel, and the line buffer is rewritten 163,840 times a frame at
    // 80 columns.
    private const int TimedFetchedFlag = 0x0100;
    private const int TimedValidFlag = 0x0200;
    private const int TimedBelowUlaFlag = 0x0400;
    private readonly ushort[] _timedPixels = new ushort[640];
    private byte _clipIndex;
    private TilemapFetchState _fetchState;
    private bool _requestInFlight;
    private int _timedLine = -1;
    private int _latestPrefetchLimit = -1;
    private int _nextGroupStart;
    private int _fetchGroupStart;
    private int _fetchPixel;
    private int _loadedMapIndex = -1;
    private int _currentMapIndex;
    private byte _fetchedTile;
    private byte _fetchedAttribute;
    private bool _fetchEightyColumns;
    private bool _fetchStripAttributes;
    private bool _fetchTextMode;
    private bool _fetchFiveHundredTwelveTiles;
    private bool _fetchForceAboveUla;
    private byte _fetchDefaultAttribute;
    private byte _fetchTransparency;
    private byte _fetchMapBase;
    private byte _fetchTileBase;
    private int _fetchScrollX;
    private byte _fetchScrollY;
    // Per-character-group invariants, latched with the control registers so the ten
    // BRAM transactions of a group no longer re-derive them one clock at a time.
    private int _fetchMapWidth = 40;
    private int _fetchPixelWidth = 320;
    private int _fetchSourceXBase;
    private int _fetchMapRowBase;
    private int _fetchPixelRow;
    private bool _fetchRotate;
    private bool _fetchMirrorX;
    private bool _fetchMirrorY;
    private int _fetchTileIndex;
    private int _fetchTextRowOffset;

    public byte ScrollXMostSignificant { get; private set; }
    public byte ScrollXLeastSignificant { get; private set; }
    public byte ScrollY { get; private set; }
    public byte TransparencyIndex { get; private set; } = 0x0F;
    public byte Control { get; private set; }
    public byte DefaultAttribute { get; private set; }
    public byte MapBase { get; private set; } = 0x2C;
    public byte TileBase { get; private set; } = 0x0C;

    public bool Enabled => (Control & 0x80) != 0;
    public bool EightyColumnMode => (Control & 0x40) != 0;
    public bool StripAttributes => (Control & 0x20) != 0;
    public bool SecondPalette => (Control & 0x10) != 0;
    public bool TextMode => (Control & 0x08) != 0;
    public bool FiveHundredTwelveTileMode => (Control & 0x02) != 0;
    public bool ForceAboveUla => (Control & 0x01) != 0;
    public int ScrollX => (ScrollXMostSignificant << 8) | ScrollXLeastSignificant;
    public byte ClipX1 => _clip[0];
    public byte ClipX2 => _clip[1];
    public byte ClipY1 => _clip[2];
    public byte ClipY2 => _clip[3];
    public byte ClipIndex => _clipIndex;
    public bool MemoryRequestPending => GetMemoryRequest().HasValue;
    public ulong CompletedCharacterFetches { get; private set; }
    public ulong FetchedPixelCount { get; private set; }
    public ulong TimedPixelDeadlineMisses { get; private set; }
    internal Action<bool>? EnabledStateChanged { get; set; }

    public bool HandlesRegister(byte register) => register is
        0x1B or 0x1C or 0x2F or 0x30 or 0x31 or 0x4C or 0x6B or 0x6C or 0x6E or 0x6F;

    public byte Read(byte register) => register switch
    {
        0x1B => _clip[_clipIndex],
        0x1C => (byte)(_clipIndex << 6),
        0x2F => ScrollXMostSignificant,
        0x30 => ScrollXLeastSignificant,
        0x31 => ScrollY,
        0x4C => TransparencyIndex,
        0x6B => Control,
        0x6C => DefaultAttribute,
        0x6E => MapBase,
        0x6F => TileBase,
        _ => 0
    };

    public void Write(byte register, byte value)
    {
        switch (register)
        {
            case 0x1B:
                _clip[_clipIndex] = value;
                _clipIndex = (byte)((_clipIndex + 1) & 0x03);
                break;

            case 0x1C:
                if ((value & 0x08) != 0)
                {
                    _clipIndex = 0;
                }

                break;

            case 0x2F:
                ScrollXMostSignificant = (byte)(value & 0x03);
                break;

            case 0x30:
                ScrollXLeastSignificant = value;
                break;

            case 0x31:
                ScrollY = value;
                break;

            case 0x4C:
                TransparencyIndex = (byte)(value & 0x0F);
                break;

            case 0x6B:
                bool wasEnabled = Enabled;
                Control = (byte)(value & 0xFB);
                if (Enabled != wasEnabled)
                {
                    EnabledStateChanged?.Invoke(Enabled);
                }
                break;

            case 0x6C:
                DefaultAttribute = value;
                break;

            case 0x6E:
                MapBase = (byte)(value & 0xBF);
                break;

            case 0x6F:
                TileBase = (byte)(value & 0xBF);
                break;
        }
    }

    /// <summary>Returns the tilemap palette index and its ULA ordering flag.</summary>
    public bool TryGetPaletteIndex(
        int outputX,
        int logicalY,
        ZxNextPhysicalMemory memory,
        out byte paletteIndex,
        out bool belowUla)
    {
        ArgumentNullException.ThrowIfNull(memory);
        paletteIndex = 0;
        belowUla = false;
        if (!Enabled || (uint)outputX >= 640 || (uint)logicalY >= 256)
        {
            return false;
        }

        // NR $1B X coordinates are doubled internally against the 320-wide
        // 7 MHz counter.  outputX is the stable 640-wide (14 MHz) coordinate.
        int hcounter = outputX >> 1;
        int clipLeft = ClipX1 << 1;
        int clipRight = (ClipX2 << 1) | 0x01;
        if (hcounter < clipLeft || hcounter > clipRight
            || logicalY < ClipY1 || logicalY > ClipY2)
        {
            return false;
        }

        int mapWidth = EightyColumnMode ? 80 : 40;
        int pixelWidth = mapWidth * 8;
        int sourceX = EightyColumnMode ? outputX : outputX >> 1;
        sourceX = Wrap(sourceX + ScrollX, pixelWidth);
        int sourceY = (logicalY + ScrollY) & 0xFF;
        int mapIndex = ((sourceY >> 3) * mapWidth) + (sourceX >> 3);
        int entrySize = StripAttributes ? 1 : 2;

        byte tileNumber = ReadBanked(memory, MapBase, mapIndex * entrySize);
        byte attribute = StripAttributes
            ? DefaultAttribute
            : ReadBanked(memory, MapBase, (mapIndex * entrySize) + 1);

        int tileIndex = tileNumber;
        if (FiveHundredTwelveTileMode && (attribute & 0x01) != 0)
        {
            tileIndex |= 0x100;
        }

        belowUla = !ForceAboveUla
            && ((attribute & 0x01) != 0 || FiveHundredTwelveTileMode);

        int pixelX = sourceX & 0x07;
        int pixelY = sourceY & 0x07;
        if (TextMode)
        {
            byte glyphRow = ReadBanked(memory, TileBase, (tileIndex * 8) + pixelY);
            paletteIndex = (byte)((attribute & 0xFE)
                | ((glyphRow >> (7 - pixelX)) & 0x01));
            // Text mode deliberately ignores the transparency index.
            return true;
        }

        TransformPixel(attribute, ref pixelX, ref pixelY);
        byte packedPixels = ReadBanked(
            memory,
            TileBase,
            (tileIndex * 32) + (pixelY * 4) + (pixelX >> 1));
        byte colour = (byte)((pixelX & 0x01) == 0
            ? packedPixels >> 4
            : packedPixels & 0x0F);
        if (colour == TransparencyIndex)
        {
            return false;
        }

        paletteIndex = (byte)((attribute & 0xF0) | colour);
        return true;
    }

    /// <summary>Returns a pixel produced by the clk28 tilemap fetch pipeline.</summary>
    public bool TryGetTimedPaletteIndex(
        int outputX,
        int logicalY,
        out byte paletteIndex,
        out bool belowUla)
    {
        return TryGetTimedPixel(
                outputX,
                logicalY,
                out bool opaque,
                out paletteIndex,
                out belowUla)
            && opaque;
    }

    /// <summary>Reports whether the timed pipeline has produced this output position.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetTimedPixel(
        int outputX,
        int logicalY,
        out bool opaque,
        out byte paletteIndex,
        out bool belowUla)
    {
        opaque = false;
        paletteIndex = 0;
        belowUla = false;
        int hcounter = outputX >> 1;
        if ((uint)outputX >= 640 || logicalY != _timedLine)
        {
            return false;
        }

        int packed = _timedPixels[outputX];
        if ((packed & TimedFetchedFlag) == 0)
        {
            return false;
        }

        if (hcounter < (ClipX1 << 1)
            || hcounter > ((ClipX2 << 1) | 0x01)
            || logicalY < ClipY1
            || logicalY > ClipY2)
        {
            return true;
        }

        paletteIndex = (byte)packed;
        belowUla = (packed & TimedBelowUlaFlag) != 0;
        opaque = (packed & TimedValidFlag) != 0;
        return true;
    }

    /// <summary>
    /// Services one clk28 video-memory edge: consumes the preceding response, advances
    /// request generation at the current beam coordinate and returns the packed request
    /// this edge wants to issue (-1 for none).
    /// </summary>
    /// <remarks>
    /// The three steps used to be three separate calls from the renderer. They are fused
    /// here because every active tilemap pixel pays them, and because the ULA fetch the
    /// renderer classifies in between cannot observe or disturb any tilemap state.
    /// </remarks>
    internal int ClockMemory(
        bool responseValid,
        byte responseData,
        int logicalX,
        int logicalY,
        int subpixel)
    {
        if (responseValid && _requestInFlight)
        {
            ConsumeMemoryResponse(responseData);
        }

        if (!Enabled || (uint)logicalY >= 256)
        {
            CancelRasterFetch();
            return -1;
        }

        if (_timedLine != logicalY)
        {
            BeginTimedLine(logicalY);
        }

        int hiresX = (logicalX * 2) + (subpixel >= 2 ? 1 : 0);
        _latestPrefetchLimit = hiresX + (EightyColumnMode ? 8 : 16);
        TryStartEligibleGroup();

        if (_requestInFlight)
        {
            return -1;
        }

        // Advancing the tilemap state can consume the preceding response and create the
        // next request, so request presence cannot be tested before this point.
        if (_fetchState == TilemapFetchState.ReadPixels)
        {
            int mapIndex = GetFetchMapIndex(_fetchPixel);
            if (mapIndex != _loadedMapIndex)
            {
                _currentMapIndex = mapIndex;
                _fetchState = TilemapFetchState.ReadTile;
            }
        }

        switch (_fetchState)
        {
            case TilemapFetchState.ReadTile:
                return PackBankedRequest(_fetchMapBase, GetMapEntryOffset(_currentMapIndex));
            case TilemapFetchState.ReadAttribute:
                return PackBankedRequest(
                    _fetchMapBase,
                    GetMapEntryOffset(_currentMapIndex) + (_fetchStripAttributes ? 0 : 1));
            case TilemapFetchState.ReadPixels:
                return PackBankedRequest(_fetchTileBase, GetTilePixelOffset());
            default:
                return -1;
        }
    }

    private void ConsumeMemoryResponse(byte data)
    {
        _requestInFlight = false;
        switch (_fetchState)
        {
            case TilemapFetchState.ReadTile:
                _fetchedTile = data;
                _fetchState = TilemapFetchState.ReadAttribute;
                break;

            case TilemapFetchState.ReadAttribute:
                _fetchedAttribute = _fetchStripAttributes ? _fetchDefaultAttribute : data;
                _loadedMapIndex = _currentMapIndex;
                LatchCharacterFetchConstants();
                _fetchState = TilemapFetchState.ReadPixels;
                break;

            case TilemapFetchState.ReadPixels:
                StoreFetchedPixel(data);
                _fetchPixel++;
                FetchedPixelCount++;
                if (_fetchPixel >= 8)
                {
                    CompletedCharacterFetches++;
                    _fetchState = TilemapFetchState.Idle;
                    _nextGroupStart = _fetchGroupStart + (_fetchEightyColumns ? 8 : 16);
                }

                break;
        }
    }

    internal ZxNextVideoMemoryRequest? GetMemoryRequest()
    {
        if (_requestInFlight)
        {
            return null;
        }

        return _fetchState switch
        {
            TilemapFetchState.ReadTile => CreateBankedRequest(
                _fetchMapBase,
                GetMapEntryOffset(_currentMapIndex)),
            TilemapFetchState.ReadAttribute => CreateBankedRequest(
                _fetchMapBase,
                GetMapEntryOffset(_currentMapIndex) + (_fetchStripAttributes ? 0 : 1)),
            TilemapFetchState.ReadPixels => CreateBankedRequest(
                _fetchTileBase,
                GetTilePixelOffset()),
            _ => null
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PackBankedRequest(byte baseRegister, int offset)
    {
        bool bank7 = (baseRegister & 0x80) != 0;
        int bankSize = bank7 ? Bank7Size : Bank5Size;
        int bankOffset = (((baseRegister & 0x3F) << 8) + offset) & (bankSize - 1);
        return bank7 ? (bankOffset | 0x10000) : bankOffset;
    }

    /// <summary>
    /// True when this clk28 edge cannot change any tilemap state: the fetch pipeline is
    /// idle, nothing is in flight, the line has already begun, and the next character
    /// group is not yet eligible to start at this beam position. The only thing such an
    /// edge would otherwise do is restamp _latestPrefetchLimit, which is recomputed from
    /// the beam on the next edge that is actually serviced.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsMemoryClockInert(int logicalY, int hiresX) =>
        _fetchState == TilemapFetchState.Idle
        && !_requestInFlight
        && _timedLine == logicalY
        && _nextGroupStart > hiresX + (EightyColumnMode ? 8 : 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void NotifyMemoryGrant(bool granted)
    {
        if (granted)
        {
            _requestInFlight = true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool RequiresMemoryClock(int logicalX, int logicalY) =>
        Enabled
        && (uint)logicalY < 256
        && logicalX >= -8
        && logicalX < 320;

    internal void RecordTimedPixelDeadlineMiss() =>
        TimedPixelDeadlineMisses++;

    public void Reset()
    {
        bool wasEnabled = Enabled;
        ScrollXMostSignificant = 0;
        ScrollXLeastSignificant = 0;
        ScrollY = 0;
        TransparencyIndex = 0x0F;
        Control = 0;
        if (wasEnabled)
        {
            EnabledStateChanged?.Invoke(false);
        }

        DefaultAttribute = 0;
        MapBase = 0x2C;
        TileBase = 0x0C;
        _clip[0] = 0;
        _clip[1] = 0x9F;
        _clip[2] = 0;
        _clip[3] = 0xFF;
        _clipIndex = 0;
        ResetRasterTiming();
    }

    internal void ResetRasterTiming()
    {
        _fetchState = TilemapFetchState.Idle;
        _requestInFlight = false;
        _timedLine = -1;
        _latestPrefetchLimit = -1;
        _nextGroupStart = 0;
        _fetchGroupStart = 0;
        _fetchPixel = 0;
        _loadedMapIndex = -1;
        _currentMapIndex = 0;
        _fetchedTile = 0;
        _fetchedAttribute = 0;
        CompletedCharacterFetches = 0;
        FetchedPixelCount = 0;
        TimedPixelDeadlineMisses = 0;
        Array.Clear(_timedPixels);
    }

    private void BeginTimedLine(int logicalY)
    {
        _timedLine = logicalY;
        _fetchState = TilemapFetchState.Idle;
        _requestInFlight = false;
        _latestPrefetchLimit = -1;
        _nextGroupStart = 0;
        _loadedMapIndex = -1;
        // Clearing the whole entry also drops the stale palette and ordering bits,
        // which are only ever read when the fetched flag is set.
        Array.Clear(_timedPixels);
    }

    private void CancelRasterFetch()
    {
        _fetchState = TilemapFetchState.Idle;
        _requestInFlight = false;
        _timedLine = -1;
        _latestPrefetchLimit = -1;
        _loadedMapIndex = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TryStartEligibleGroup()
    {
        // Serviced on every clk28 edge but true only once per character, so the
        // rejection test is kept inlineable and the setup lives out of line.
        if (_fetchState != TilemapFetchState.Idle
            || _nextGroupStart >= 640
            || _nextGroupStart > _latestPrefetchLimit)
        {
            return;
        }

        StartEligibleGroup();
    }

    private void StartEligibleGroup()
    {
        _fetchGroupStart = _nextGroupStart;
        _fetchPixel = 0;
        _fetchEightyColumns = EightyColumnMode;
        _fetchStripAttributes = StripAttributes;
        _fetchTextMode = TextMode;
        _fetchFiveHundredTwelveTiles = FiveHundredTwelveTileMode;
        _fetchForceAboveUla = ForceAboveUla;
        _fetchDefaultAttribute = DefaultAttribute;
        _fetchTransparency = TransparencyIndex;
        _fetchMapBase = MapBase;
        _fetchTileBase = TileBase;
        _fetchScrollX = Wrap(ScrollX, _fetchEightyColumns ? 640 : 320);
        _fetchScrollY = ScrollY;
        _fetchMapWidth = _fetchEightyColumns ? 80 : 40;
        _fetchPixelWidth = _fetchMapWidth * 8;
        _fetchSourceXBase = (_fetchEightyColumns
            ? _fetchGroupStart
            : _fetchGroupStart >> 1) + _fetchScrollX;
        int sourceY = (_timedLine + _fetchScrollY) & 0xFF;
        _fetchMapRowBase = (sourceY >> 3) * _fetchMapWidth;
        _fetchPixelRow = sourceY & 0x07;
        _currentMapIndex = GetFetchMapIndex(0);
        _fetchState = TilemapFetchState.ReadTile;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetFetchMapIndex(int pixel) =>
        _fetchMapRowBase + (GetFetchSourceX(pixel) >> 3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetMapEntryOffset(int mapIndex) =>
        mapIndex * (_fetchStripAttributes ? 1 : 2);

    private int GetTilePixelOffset()
    {
        if (_fetchTextMode)
        {
            return _fetchTextRowOffset;
        }

        int pixelX = GetFetchSourceX(_fetchPixel) & 0x07;
        int pixelY = _fetchPixelRow;
        TransformFetchPixel(ref pixelX, ref pixelY);
        return (_fetchTileIndex * 32) + (pixelY * 4) + (pixelX >> 1);
    }

    private void StoreFetchedPixel(byte data)
    {
        int pixelX = GetFetchSourceX(_fetchPixel) & 0x07;
        byte palette;
        bool valid;
        if (_fetchTextMode)
        {
            palette = (byte)((_fetchedAttribute & 0xFE)
                | ((data >> (7 - pixelX)) & 0x01));
            valid = true;
        }
        else
        {
            int transformedX = pixelX;
            int transformedY = _fetchPixelRow;
            TransformFetchPixel(ref transformedX, ref transformedY);
            byte colour = (byte)((transformedX & 0x01) == 0 ? data >> 4 : data & 0x0F);
            palette = (byte)((_fetchedAttribute & 0xF0) | colour);
            valid = colour != _fetchTransparency;
        }

        bool below = !_fetchForceAboveUla
            && ((_fetchedAttribute & 0x01) != 0 || _fetchFiveHundredTwelveTiles);
        if (_fetchEightyColumns)
        {
            StoreTimedOutput(_fetchGroupStart + _fetchPixel, palette, valid, below);
        }
        else
        {
            int outputX = _fetchGroupStart + (_fetchPixel * 2);
            StoreTimedOutput(outputX, palette, valid, below);
            StoreTimedOutput(outputX + 1, palette, valid, below);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StoreTimedOutput(int outputX, byte palette, bool valid, bool below)
    {
        if ((uint)outputX >= 640)
        {
            return;
        }

        _timedPixels[outputX] = (ushort)(palette
            | TimedFetchedFlag
            | (valid ? TimedValidFlag : 0)
            | (below ? TimedBelowUlaFlag : 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetFetchSourceX(int pixel)
    {
        // _fetchSourceXBase already folds in the group origin and the pre-wrapped
        // scroll, so the wrap can never need more than one subtraction.
        int sourceX = _fetchSourceXBase + pixel;
        return sourceX >= _fetchPixelWidth ? sourceX - _fetchPixelWidth : sourceX;
    }

    /// <summary>
    /// Derives everything the eight pixel transactions of one character share, once
    /// the tile number and attribute for that character are known.
    /// </summary>
    private void LatchCharacterFetchConstants()
    {
        int tileIndex = _fetchedTile;
        if (_fetchFiveHundredTwelveTiles && (_fetchedAttribute & 0x01) != 0)
        {
            tileIndex |= 0x100;
        }

        _fetchTileIndex = tileIndex;
        _fetchRotate = (_fetchedAttribute & 0x02) != 0;
        _fetchMirrorX = ((_fetchedAttribute & 0x08) != 0) ^ _fetchRotate;
        _fetchMirrorY = (_fetchedAttribute & 0x04) != 0;
        _fetchTextRowOffset = (tileIndex * 8) + _fetchPixelRow;
    }

    /// <summary>Applies the latched attribute transform to one pixel coordinate.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TransformFetchPixel(ref int x, ref int y)
    {
        if (_fetchMirrorX)
        {
            x = 7 - x;
        }

        if (_fetchMirrorY)
        {
            y = 7 - y;
        }

        if (_fetchRotate)
        {
            (x, y) = (y, x);
        }
    }

    private static ZxNextVideoMemoryRequest CreateBankedRequest(byte baseRegister, int offset)
    {
        bool bank7 = (baseRegister & 0x80) != 0;
        int bankSize = bank7 ? Bank7Size : Bank5Size;
        int bankOffset = (((baseRegister & 0x3F) << 8) + offset) & (bankSize - 1);
        return new ZxNextVideoMemoryRequest(
            bank7 ? ZxNextVideoMemoryBank.Bank7 : ZxNextVideoMemoryBank.Bank5,
            bankOffset);
    }

    private static byte ReadBanked(ZxNextPhysicalMemory memory, byte baseRegister, int offset)
    {
        bool bank7 = (baseRegister & 0x80) != 0;
        int bankSize = bank7 ? Bank7Size : Bank5Size;
        int bankOffset = ((((baseRegister & 0x3F) << 8) + offset) & (bankSize - 1));
        int page = (bank7 ? Bank7FirstPage : Bank5FirstPage)
            + (bankOffset / ZxNextPhysicalMemory.PageSize);
        return memory.ReadMasked(page, bankOffset & (ZxNextPhysicalMemory.PageSize - 1));
    }

    private static void TransformPixel(byte attribute, ref int x, ref int y)
    {
        bool rotate = (attribute & 0x02) != 0;
        bool mirrorX = (attribute & 0x08) != 0 ^ rotate;
        bool mirrorY = (attribute & 0x04) != 0;
        if (mirrorX)
        {
            x = 7 - x;
        }

        if (mirrorY)
        {
            y = 7 - y;
        }

        if (rotate)
        {
            (x, y) = (y, x);
        }
    }

    private static int Wrap(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private enum TilemapFetchState : byte
    {
        Idle,
        ReadTile,
        ReadAttribute,
        ReadPixels
    }

    /// <summary>Reads the registers and the fetch pipeline, for a snapshot.</summary>
    public ZxNextTilemapState CaptureState() => new(
        _clip.ToArray(),
        _clipIndex,
        ScrollXMostSignificant,
        ScrollXLeastSignificant,
        ScrollY,
        TransparencyIndex,
        Control,
        DefaultAttribute,
        MapBase,
        TileBase,
        _timedPixels.ToArray(),
        (byte)_fetchState,
        _requestInFlight,
        _timedLine,
        _latestPrefetchLimit,
        _nextGroupStart,
        _fetchGroupStart,
        _fetchPixel,
        _currentMapIndex,
        _fetchedTile,
        _fetchedAttribute,
        _fetchEightyColumns,
        _fetchStripAttributes,
        _fetchTextMode,
        _fetchFiveHundredTwelveTiles,
        _fetchForceAboveUla,
        _fetchDefaultAttribute,
        _fetchTransparency,
        _fetchMapBase,
        _fetchTileBase,
        CompletedCharacterFetches,
        FetchedPixelCount,
        TimedPixelDeadlineMisses);

    /// <summary>
    /// Puts the registers and the pipeline back, and invalidates what is merely cached.
    /// </summary>
    /// <remarks>
    /// The loaded map index is deliberately cleared rather than restored: it names a tile
    /// this device has already read out of video memory, and after a restore that memory
    /// may hold something else entirely. Clearing it costs one re-read and cannot be wrong.
    /// </remarks>
    public void RestoreState(ZxNextTilemapState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        bool wasEnabled = Enabled;

        state.Clip.CopyTo(_clip, 0);
        _clipIndex = state.ClipIndex;
        ScrollXMostSignificant = state.ScrollXMostSignificant;
        ScrollXLeastSignificant = state.ScrollXLeastSignificant;
        ScrollY = state.ScrollY;
        TransparencyIndex = state.TransparencyIndex;
        Control = state.Control;
        DefaultAttribute = state.DefaultAttribute;
        MapBase = state.MapBase;
        TileBase = state.TileBase;

        state.TimedPixels.CopyTo(_timedPixels, 0);
        _fetchState = (TilemapFetchState)state.FetchState;
        _requestInFlight = state.RequestInFlight;
        _timedLine = state.TimedLine;
        _latestPrefetchLimit = state.LatestPrefetchLimit;
        _nextGroupStart = state.NextGroupStart;
        _fetchGroupStart = state.FetchGroupStart;
        _fetchPixel = state.FetchPixel;
        _currentMapIndex = state.CurrentMapIndex;
        _fetchedTile = state.FetchedTile;
        _fetchedAttribute = state.FetchedAttribute;
        _fetchEightyColumns = state.FetchEightyColumns;
        _fetchStripAttributes = state.FetchStripAttributes;
        _fetchTextMode = state.FetchTextMode;
        _fetchFiveHundredTwelveTiles = state.FetchFiveHundredTwelveTiles;
        _fetchForceAboveUla = state.FetchForceAboveUla;
        _fetchDefaultAttribute = state.FetchDefaultAttribute;
        _fetchTransparency = state.FetchTransparency;
        _fetchMapBase = state.FetchMapBase;
        _fetchTileBase = state.FetchTileBase;
        CompletedCharacterFetches = state.CompletedCharacterFetches;
        FetchedPixelCount = state.FetchedPixelCount;
        TimedPixelDeadlineMisses = state.TimedPixelDeadlineMisses;

        _loadedMapIndex = -1;

        // The machine gates instruction batching on whether the tilemap is on, so a restore
        // that changes that has to say so.
        if (Enabled != wasEnabled)
        {
            EnabledStateChanged?.Invoke(Enabled);
        }
    }
}
