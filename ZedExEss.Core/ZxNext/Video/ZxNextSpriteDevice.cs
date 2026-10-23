using ZedExEss.ZxNext.Core;
using System.Runtime.CompilerServices;

namespace ZedExEss.ZxNext.Video;

/// <summary>
/// ZX Spectrum Next sprite pattern RAM, attribute RAM, CPU interfaces and line renderer.
/// </summary>
public sealed class ZxNextSpriteDevice
{
    public const ushort StatusAndSlotPort = 0x303B;
    public const byte AttributePort = 0x57;
    public const byte PatternPort = 0x5B;
    public const int SpriteCount = 128;
    public const int PatternCount = 64;
    public const int PatternSize = 256;

    private const int AttributeCount = 5;
    private const int CoordinateMask = 0x1FF;
    private const byte CollisionFlag = 0x01;
    private const byte OvertimeFlag = 0x02;

    private readonly byte[] _patterns = new byte[PatternCount * PatternSize];
    private readonly byte[] _attributes = new byte[SpriteCount * AttributeCount];
    private readonly byte[] _clip = [0x00, 0xFF, 0x00, 0xBF];
    private readonly byte[] _linePaletteIndices = new byte[ZxNextVideoRenderer.LogicalWidth];
    private readonly bool[] _lineOpaque = new bool[ZxNextVideoRenderer.LogicalWidth];
    private readonly ResolvedSprite[] _resolvedSprites = new ResolvedSprite[SpriteCount];
    // Anchor chain state after each sprite of the resolve walk, so the clk28 qualify
    // scan can resume the relative/anchor chain from the cache instead of rebuilding it.
    private readonly AnchorState[] _resolvedAnchors = new AnchorState[SpriteCount];
    private byte[] _displayPaletteIndices = new byte[ZxNextVideoRenderer.LogicalWidth];
    private bool[] _displayOpaque = new bool[ZxNextVideoRenderer.LogicalWidth];
    private byte[] _buildPaletteIndices = new byte[ZxNextVideoRenderer.LogicalWidth];
    private bool[] _buildOpaque = new bool[ZxNextVideoRenderer.LogicalWidth];
    private byte _portSprite;
    private byte _portAttributeIndex;
    private byte _nextRegisterSprite;
    private int _patternAddress;
    private byte _clipIndex;
    private byte _status;
    private bool _idLockstep;
    private int _revision;
    private int _cachedRevision = -1;
    private int _resolvedRevision = -1;
    private int _preparationCacheRevision;
    private bool _preparationCacheValid;
    private int _cachedLine = -1;
    private byte _cachedControl;
    private SpritePreparationState _preparationState;
    private int _preparationSpriteIndex;
    private ResolvedSprite _preparationSprite;
    private AnchorState _preparationAnchor = AnchorState.Initial;
    private int _preparationSourceY;
    private int _preparationDestinationX;
    private int _preparationWidth;
    private int _displayLine = -1;
    private int _buildingLine = -1;
    private ulong _currentPreparationMasterTicks;
    private int _currentQualifiedSpriteCount;

    public byte TransparencyIndex { get; private set; } = 0xE3;
    public byte SelectedPortSprite => _portSprite;
    public byte SelectedNextRegisterSprite => _nextRegisterSprite;
    public byte PortAttributeIndex => _portAttributeIndex;
    public int PatternAddress => _patternAddress;
    public byte ClipX1 => _clip[0];
    public byte ClipX2 => _clip[1];
    public byte ClipY1 => _clip[2];
    public byte ClipY2 => _clip[3];
    public byte ClipIndex => _clipIndex;
    public bool IdLockstep => _idLockstep;
    public bool PreparationBusy => _preparationState != SpritePreparationState.Idle;
    public int PreparedLine => _displayLine;
    public int BuildingLine => _buildingLine;
    public ulong SpritePreparationMasterTicks { get; private set; }
    public ulong LastPreparationMasterTicks { get; private set; }
    public int LastQualifiedSpriteCount { get; private set; }
    public ulong OvertimeCount { get; private set; }

    public bool HandlesRegister(byte register) => register is
        0x19 or 0x1C or 0x34 or 0x35 or 0x36 or 0x37 or 0x38 or 0x39
        or 0x4B or 0x75 or 0x76 or 0x77 or 0x78 or 0x79;

    public byte Read(byte register) => register switch
    {
        0x19 => _clip[_clipIndex],
        0x1C => (byte)(_clipIndex << 2),
        0x34 => _nextRegisterSprite,
        0x4B => TransparencyIndex,
        _ => 0
    };

    public void Write(byte register, byte value)
    {
        switch (register)
        {
            case 0x19:
                _clip[_clipIndex] = value;
                _clipIndex = (byte)((_clipIndex + 1) & 0x03);
                InvalidateLine();
                break;

            case 0x1C:
                if ((value & 0x02) != 0)
                {
                    _clipIndex = 0;
                }

                break;

            case 0x34:
                SelectNextRegisterSprite(value);
                break;

            case >= 0x35 and <= 0x39:
                WriteAttribute(_nextRegisterSprite, register - 0x35, value);
                break;

            case 0x4B:
                TransparencyIndex = value;
                InvalidateLine();
                break;

            case >= 0x75 and <= 0x79:
                WriteAttribute(_nextRegisterSprite, register - 0x75, value);
                IncrementNextRegisterSprite();
                break;
        }
    }

    public bool HandlesReadPort(ushort port) => port == StatusAndSlotPort;

    public bool HandlesWritePort(ushort port) =>
        port == StatusAndSlotPort || (byte)port is AttributePort or PatternPort;

    public byte ReadPort(ushort port)
    {
        if (port != StatusAndSlotPort)
        {
            return 0xFF;
        }

        byte result = _status;
        _status = 0;
        return result;
    }

    public void WritePort(ushort port, byte value)
    {
        if (port == StatusAndSlotPort)
        {
            SelectPortSpriteAndPattern(value);
        }
        else if ((byte)port == AttributePort)
        {
            WritePortAttribute(value);
        }
        else if ((byte)port == PatternPort)
        {
            _patterns[_patternAddress] = value;
            _patternAddress = (_patternAddress + 1) & (_patterns.Length - 1);
            InvalidateLine();
        }
    }

    public void SetIdLockstep(bool enabled) => _idLockstep = enabled;

    /// <summary>Returns one stored byte for deterministic diagnostics.</summary>
    public byte ReadPatternByte(int address) => _patterns[address & (_patterns.Length - 1)];

    /// <summary>Returns one stored sprite attribute for deterministic diagnostics.</summary>
    public byte ReadAttribute(int sprite, int attribute) =>
        _attributes[GetAttributeAddress(sprite, attribute % AttributeCount)];

    /// <summary>
    /// Returns the top sprite palette index at one 320x256 logical display coordinate.
    /// </summary>
    public bool TryGetPaletteIndex(
        int logicalX,
        int logicalY,
        byte spriteAndLayersControl,
        out byte paletteIndex)
    {
        paletteIndex = 0;
        if ((uint)logicalX >= ZxNextVideoRenderer.LogicalWidth
            || (uint)logicalY >= ZxNextVideoRenderer.LogicalHeight
            || (spriteAndLayersControl & 0x01) == 0)
        {
            return false;
        }

        EnsureLine(logicalY, spriteAndLayersControl);
        if (!_lineOpaque[logicalX])
        {
            return false;
        }

        paletteIndex = _linePaletteIndices[logicalX];
        return true;
    }

    /// <summary>
    /// Returns a pixel from the FPGA-style line buffer prepared during the previous raster line.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetPreparedPaletteIndex(
        int logicalX,
        int logicalY,
        byte spriteAndLayersControl,
        out byte paletteIndex)
    {
        paletteIndex = 0;
        if ((uint)logicalX >= ZxNextVideoRenderer.LogicalWidth
            || (uint)logicalY >= ZxNextVideoRenderer.LogicalHeight
            || (spriteAndLayersControl & 0x01) == 0
            || _displayLine != logicalY
            || !_displayOpaque[logicalX]
            || !IsInsideHorizontalClip(logicalX, spriteAndLayersControl)
            || !IsInsideVerticalClip(logicalY, spriteAndLayersControl))
        {
            return false;
        }

        paletteIndex = _displayPaletteIndices[logicalX];
        return true;
    }

    /// <summary>
    /// Swaps the completed sprite line into view and begins preparing the next wide-coordinate line.
    /// </summary>
    internal void BeginRasterLine(int currentWideY)
    {
        if (_preparationState != SpritePreparationState.Idle)
        {
            _status |= OvertimeFlag;
            OvertimeCount++;
        }

        LastPreparationMasterTicks = _currentPreparationMasterTicks;
        LastQualifiedSpriteCount = _currentQualifiedSpriteCount;

        (_displayPaletteIndices, _buildPaletteIndices) =
            (_buildPaletteIndices, _displayPaletteIndices);
        (_displayOpaque, _buildOpaque) = (_buildOpaque, _displayOpaque);
        _displayLine = _buildingLine;
        Array.Clear(_buildOpaque, 0, _buildOpaque.Length);

        _buildingLine = (currentWideY + 1) & CoordinateMask;
        _preparationState = SpritePreparationState.Start;
        _preparationSpriteIndex = 0;
        _preparationAnchor = AnchorState.Initial;
        _preparationCacheRevision = _revision;
        _preparationCacheValid = true;
        _preparationDestinationX = 0;
        _preparationWidth = 0;
        _currentPreparationMasterTicks = 0;
        _currentQualifiedSpriteCount = 0;
    }

    /// <summary>Advances the clk28 sprite preparation state machine.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AdvancePreparationMasterTicks(ulong masterTicks, byte spriteAndLayersControl)
    {
        if (_preparationState == SpritePreparationState.Idle)
        {
            return;
        }

        AdvancePreparationCore(masterTicks, spriteAndLayersControl);
    }

    private void AdvancePreparationCore(ulong masterTicks, byte spriteAndLayersControl)
    {
        while (masterTicks > 0 && _preparationState != SpritePreparationState.Idle)
        {
            masterTicks--;
            SpritePreparationMasterTicks++;
            _currentPreparationMasterTicks++;

            switch (_preparationState)
            {
                case SpritePreparationState.Start:
                    _preparationState = SpritePreparationState.Qualify;
                    break;

                case SpritePreparationState.Qualify:
                    QualifyNextSprite();
                    break;

                case SpritePreparationState.Process:
                    ProcessSpritePixel(spriteAndLayersControl);
                    break;
            }
        }
    }

    /// <summary>Resynchronizes line buffers when the raster origin changes.</summary>
    internal void ResetRasterTiming()
    {
        Array.Clear(_displayOpaque, 0, _displayOpaque.Length);
        Array.Clear(_buildOpaque, 0, _buildOpaque.Length);
        _displayLine = -1;
        _buildingLine = -1;
        _preparationState = SpritePreparationState.Idle;
        _preparationSpriteIndex = 0;
        _preparationAnchor = AnchorState.Initial;
        _preparationCacheValid = false;
        _preparationDestinationX = 0;
        _preparationWidth = 0;
        _currentPreparationMasterTicks = 0;
        _currentQualifiedSpriteCount = 0;
        LastPreparationMasterTicks = 0;
        LastQualifiedSpriteCount = 0;
    }

    /// <summary>Resets interface/register state while retaining the FPGA RAM contents.</summary>
    public void Reset()
    {
        _portSprite = 0;
        _portAttributeIndex = 0;
        _nextRegisterSprite = 0;
        _patternAddress = 0;
        _clip[0] = 0;
        _clip[1] = 0xFF;
        _clip[2] = 0;
        _clip[3] = 0xBF;
        _clipIndex = 0;
        TransparencyIndex = 0xE3;
        _status = 0;
        _idLockstep = false;
        SpritePreparationMasterTicks = 0;
        OvertimeCount = 0;
        ResetRasterTiming();
        InvalidateLine();
    }

    private void SelectPortSpriteAndPattern(byte value)
    {
        _portSprite = (byte)(value & 0x7F);
        _portAttributeIndex = 0;
        _patternAddress = (((value & 0x3F) << 8) | ((value & 0x80) != 0 ? 0x80 : 0))
            & (_patterns.Length - 1);
        if (_idLockstep)
        {
            _nextRegisterSprite = _portSprite;
        }
    }

    private void SelectNextRegisterSprite(byte value)
    {
        _nextRegisterSprite = (byte)(value & 0x7F);
        if (!_idLockstep)
        {
            return;
        }

        _portSprite = _nextRegisterSprite;
        _portAttributeIndex = 0;
        _patternAddress = (((value & 0x3F) << 8) | ((value & 0x80) != 0 ? 0x80 : 0))
            & (_patterns.Length - 1);
    }

    private void WritePortAttribute(byte value)
    {
        int attribute = _portAttributeIndex;
        WriteAttribute(_portSprite, attribute, value);

        bool complete = attribute >= 4 || (attribute == 3 && (value & 0x40) == 0);
        if (complete)
        {
            _portSprite = (byte)((_portSprite + 1) & 0x7F);
            _portAttributeIndex = 0;
            if (_idLockstep)
            {
                _nextRegisterSprite = _portSprite;
            }
        }
        else
        {
            _portAttributeIndex++;
        }
    }

    private void IncrementNextRegisterSprite()
    {
        _nextRegisterSprite = (byte)((_nextRegisterSprite + 1) & 0x7F);
        if (!_idLockstep)
        {
            return;
        }

        int patternHalf = _patternAddress & 0x80;
        _portSprite = _nextRegisterSprite;
        _portAttributeIndex = 0;
        _patternAddress = ((_nextRegisterSprite & 0x3F) << 8) | patternHalf;
    }

    private void WriteAttribute(int sprite, int attribute, byte value)
    {
        _attributes[GetAttributeAddress(sprite, attribute)] = value;
        InvalidateLine();
    }

    private void EnsureLine(int logicalY, byte control)
    {
        if (_cachedLine == logicalY && _cachedRevision == _revision && _cachedControl == control)
        {
            return;
        }

        Array.Clear(_lineOpaque, 0, _lineOpaque.Length);
        _cachedLine = logicalY;
        _cachedRevision = _revision;
        _cachedControl = control;
        EnsureResolvedSprites();

        bool zeroOnTop = (control & 0x40) != 0;
        if (zeroOnTop)
        {
            for (int sprite = SpriteCount - 1; sprite >= 0; sprite--)
            {
                DrawSpriteLine(_resolvedSprites[sprite], logicalY, control);
            }
        }
        else
        {
            for (int sprite = 0; sprite < SpriteCount; sprite++)
            {
                DrawSpriteLine(_resolvedSprites[sprite], logicalY, control);
            }
        }
    }

    private void EnsureResolvedSprites()
    {
        if (_resolvedRevision == _revision)
        {
            return;
        }

        AnchorState anchor = AnchorState.Initial;
        for (int sprite = 0; sprite < SpriteCount; sprite++)
        {
            _resolvedSprites[sprite] = ResolveSprite(sprite, ref anchor);
            _resolvedAnchors[sprite] = anchor;
        }

        _resolvedRevision = _revision;
    }

    private ResolvedSprite ResolveSprite(int sprite, ref AnchorState anchor)
    {
        int attributeAddress = sprite * AttributeCount;
        byte attr3 = _attributes[attributeAddress + 3];
        byte attr4 = _attributes[attributeAddress + 4];
        bool relative = (attr3 & 0x40) != 0 && (attr4 & 0xC0) == 0x40;
        if (relative)
        {
            return ResolveRelativeSprite(attributeAddress, anchor);
        }

        ResolvedSprite resolved = ResolveAnchorSprite(attributeAddress);
        bool unified = (attr3 & 0x40) != 0 && (attr4 & 0x20) != 0;
        int internalPattern = ((attr3 & 0x3F) << 1)
            | (resolved.FourBit ? ((attr4 >> 6) & 0x01) : 0);
        anchor = new AnchorState(
            resolved.Visible,
            unified,
            resolved.FourBit,
            resolved.X,
            resolved.Y,
            internalPattern,
            resolved.PaletteOffset,
            unified && resolved.XMirror,
            unified && resolved.YMirror,
            unified && resolved.Rotate,
            unified ? resolved.XScale : 1,
            unified ? resolved.YScale : 1,
            unified ? resolved.XScaleShift : 0,
            unified ? resolved.YScaleShift : 0);
        return resolved;
    }

    private ResolvedSprite ResolveAnchorSprite(int attributeAddress)
    {
        byte attr2 = _attributes[attributeAddress + 2];
        byte attr3 = _attributes[attributeAddress + 3];
        byte attr4 = _attributes[attributeAddress + 4];
        bool extended = (attr3 & 0x40) != 0;
        bool fourBit = extended && (attr4 & 0x80) != 0;
        int internalPattern = ((attr3 & 0x3F) << 1)
            | (fourBit ? ((attr4 >> 6) & 0x01) : 0);
        int xScaleShift = extended ? (attr4 >> 3) & 0x03 : 0;
        int yScaleShift = extended ? (attr4 >> 1) & 0x03 : 0;
        return new ResolvedSprite(
            (attr3 & 0x80) != 0,
            _attributes[attributeAddress] | ((attr2 & 0x01) << 8),
            _attributes[attributeAddress + 1] | (extended ? (attr4 & 0x01) << 8 : 0),
            fourBit ? internalPattern : internalPattern >> 1,
            fourBit,
            attr2 >> 4,
            (attr2 & 0x08) != 0,
            (attr2 & 0x04) != 0,
            (attr2 & 0x02) != 0,
            1 << xScaleShift,
            1 << yScaleShift,
            xScaleShift,
            yScaleShift);
    }

    private ResolvedSprite ResolveRelativeSprite(int attributeAddress, AnchorState anchor)
    {
        byte attr2 = _attributes[attributeAddress + 2];
        byte attr3 = _attributes[attributeAddress + 3];
        byte attr4 = _attributes[attributeAddress + 4];

        int relativeX = unchecked((sbyte)_attributes[attributeAddress]);
        int relativeY = unchecked((sbyte)_attributes[attributeAddress + 1]);
        if (anchor.Rotate)
        {
            (relativeX, relativeY) = (relativeY, relativeX);
        }

        if (anchor.Rotate ^ anchor.XMirror)
        {
            relativeX = NegateSignedByte(relativeX);
        }

        if (anchor.YMirror)
        {
            relativeY = NegateSignedByte(relativeY);
        }

        int x = (anchor.X + (relativeX * anchor.XScale)) & CoordinateMask;
        int y = (anchor.Y + (relativeY * anchor.YScale)) & CoordinateMask;
        int paletteOffset = (attr2 >> 4)
            + ((attr2 & 0x01) != 0 ? anchor.PaletteOffset : 0);
        paletteOffset &= 0x0F;

        bool localXMirror = (attr2 & 0x08) != 0;
        bool localYMirror = (attr2 & 0x04) != 0;
        bool localRotate = (attr2 & 0x02) != 0;
        bool xMirror;
        bool yMirror;
        bool rotate;
        int xScale;
        int yScale;
        int xScaleShift;
        int yScaleShift;
        if (!anchor.Unified)
        {
            xMirror = localXMirror;
            yMirror = localYMirror;
            rotate = localRotate;
            xScaleShift = (attr4 >> 3) & 0x03;
            yScaleShift = (attr4 >> 1) & 0x03;
            xScale = 1 << xScaleShift;
            yScale = 1 << yScaleShift;
        }
        else
        {
            bool relativeXMirror = !anchor.Rotate
                ? localXMirror
                : localYMirror ^ localRotate;
            bool relativeYMirror = !anchor.Rotate
                ? localYMirror
                : localXMirror ^ localRotate;
            xMirror = anchor.XMirror ^ relativeXMirror;
            yMirror = anchor.YMirror ^ relativeYMirror;
            rotate = anchor.Rotate ^ localRotate;
            xScale = anchor.XScale;
            yScale = anchor.YScale;
            xScaleShift = anchor.XScaleShift;
            yScaleShift = anchor.YScaleShift;
        }

        int internalPattern = ((attr3 & 0x3F) << 1)
            | (anchor.FourBit ? ((attr4 >> 5) & 0x01) : 0);
        if ((attr4 & 0x01) != 0)
        {
            internalPattern = (internalPattern + anchor.InternalPattern) & 0x7F;
        }

        return new ResolvedSprite(
            anchor.Visible && (attr3 & 0x80) != 0,
            x,
            y,
            anchor.FourBit ? internalPattern : internalPattern >> 1,
            anchor.FourBit,
            paletteOffset,
            xMirror,
            yMirror,
            rotate,
            xScale,
            yScale,
            xScaleShift,
            yScaleShift);
    }

    private void DrawSpriteLine(in ResolvedSprite sprite, int logicalY, byte control)
    {
        if (!sprite.Visible)
        {
            return;
        }

        int height = 16 * sprite.YScale;
        int destinationY = (logicalY - sprite.Y) & CoordinateMask;
        if (destinationY >= height || !IsInsideVerticalClip(logicalY, control))
        {
            return;
        }

        int sourceY = destinationY >> sprite.YScaleShift;
        int width = 16 * sprite.XScale;
        for (int destinationX = 0; destinationX < width; destinationX++)
        {
            int screenX = (sprite.X + destinationX) & CoordinateMask;
            if ((uint)screenX >= ZxNextVideoRenderer.LogicalWidth
                || !IsInsideHorizontalClip(screenX, control))
            {
                continue;
            }

            int sourceX = destinationX >> sprite.XScaleShift;
            if (!TryGetSpritePixel(sprite, sourceX, sourceY, out byte finalIndex))
            {
                continue;
            }

            if (_lineOpaque[screenX])
            {
                _status |= CollisionFlag;
            }

            _linePaletteIndices[screenX] = finalIndex;
            _lineOpaque[screenX] = true;
        }
    }

    private void QualifyNextSprite()
    {
        int spriteIndex = _preparationSpriteIndex;
        _preparationSpriteIndex = (_preparationSpriteIndex + 1) & 0x7F;
        _currentQualifiedSpriteCount++;

        // Sprite resolution does not depend on the raster line, so an unchanged
        // attribute set is walked once and reused. A write during the line drops back
        // to live resolution for the rest of it, carrying the anchor accumulated from
        // the pre-write values, which is what the FPGA's in-flight scan would use.
        ResolvedSprite resolved;
        if (_preparationCacheValid && _revision == _preparationCacheRevision)
        {
            EnsureResolvedSprites();
            resolved = _resolvedSprites[spriteIndex];
            _preparationAnchor = _resolvedAnchors[spriteIndex];
        }
        else
        {
            _preparationCacheValid = false;
            AnchorState anchor = _preparationAnchor;
            resolved = ResolveSprite(spriteIndex, ref anchor);
            _preparationAnchor = anchor;
        }

        int height = 16 * resolved.YScale;
        int destinationY = (_buildingLine - resolved.Y) & CoordinateMask;
        if (!resolved.Visible || destinationY >= height)
        {
            if (_preparationSpriteIndex == 0)
            {
                _preparationState = SpritePreparationState.Idle;
            }

            return;
        }

        _preparationSprite = resolved;
        _preparationSourceY = destinationY >> resolved.YScaleShift;
        _preparationDestinationX = 0;
        _preparationWidth = 16 * resolved.XScale;
        _preparationState = SpritePreparationState.Process;
    }

    private void ProcessSpritePixel(byte control)
    {
        int screenX = (_preparationSprite.X + _preparationDestinationX) & CoordinateMask;
        bool validX = screenX < ZxNextVideoRenderer.LogicalWidth;
        if (validX)
        {
            int sourceX = _preparationDestinationX >> _preparationSprite.XScaleShift;
            if (TryGetSpritePixel(
                _preparationSprite,
                sourceX,
                _preparationSourceY,
                out byte finalIndex))
            {
                if (_buildOpaque[screenX])
                {
                    _status |= CollisionFlag;
                }

                bool zeroOnTop = (control & 0x40) != 0;
                if (!zeroOnTop || !_buildOpaque[screenX])
                {
                    _buildPaletteIndices[screenX] = finalIndex;
                    _buildOpaque[screenX] = true;
                }
            }
        }

        _preparationDestinationX++;
        int wrapMask = 32 - _preparationSprite.XScale;
        bool earlyExit = !validX && (((screenX >> 4) & wrapMask) != wrapMask);
        if (_preparationDestinationX < _preparationWidth && !earlyExit)
        {
            return;
        }

        _preparationState = _preparationSpriteIndex == 0
            ? SpritePreparationState.Idle
            : SpritePreparationState.Qualify;
    }

    private bool TryGetSpritePixel(
        in ResolvedSprite sprite,
        int sourceX,
        int sourceY,
        out byte finalIndex)
    {
        int patternX;
        int patternY;
        if (!sprite.Rotate)
        {
            patternX = sprite.XMirror ? 15 - sourceX : sourceX;
            patternY = sprite.YMirror ? 15 - sourceY : sourceY;
        }
        else
        {
            patternX = sprite.YMirror ? 15 - sourceY : sourceY;
            patternY = !sprite.XMirror ? 15 - sourceX : sourceX;
        }

        byte rawPixel;
        if (sprite.FourBit)
        {
            int address = (sprite.Pattern * 128) + (patternY * 8) + (patternX >> 1);
            byte packed = _patterns[address & (_patterns.Length - 1)];
            rawPixel = (byte)((patternX & 1) == 0 ? packed >> 4 : packed & 0x0F);
            if (rawPixel == (TransparencyIndex & 0x0F))
            {
                finalIndex = 0;
                return false;
            }

            finalIndex = (byte)((sprite.PaletteOffset << 4) | rawPixel);
            return true;
        }

        int pixelAddress = (sprite.Pattern * PatternSize) + (patternY * 16) + patternX;
        rawPixel = _patterns[pixelAddress & (_patterns.Length - 1)];
        if (rawPixel == TransparencyIndex)
        {
            finalIndex = 0;
            return false;
        }

        finalIndex = (byte)((((rawPixel >> 4) + sprite.PaletteOffset) & 0x0F) << 4
            | (rawPixel & 0x0F));
        return true;
    }

    private bool IsInsideHorizontalClip(int logicalX, byte control)
    {
        bool overBorder = (control & 0x02) != 0;
        if (overBorder)
        {
            if ((control & 0x20) == 0)
            {
                return true;
            }

            return logicalX >= ClipX1 * 2 && logicalX <= (ClipX2 * 2) + 1;
        }

        return logicalX >= ClipX1 + 32 && logicalX <= ClipX2 + 32;
    }

    private bool IsInsideVerticalClip(int logicalY, byte control)
    {
        bool overBorder = (control & 0x02) != 0;
        if (overBorder)
        {
            return (control & 0x20) == 0
                || (logicalY >= ClipY1 && logicalY <= ClipY2);
        }

        return logicalY < 224 && logicalY >= ClipY1 + 32 && logicalY <= ClipY2 + 32;
    }

    private void InvalidateLine()
    {
        unchecked
        {
            _revision++;
        }

        _cachedRevision = -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetAttributeAddress(int sprite, int attribute) =>
        ((sprite & 0x7F) * AttributeCount) + attribute;

    private static int NegateSignedByte(int value) => unchecked((sbyte)-value);

    private enum SpritePreparationState
    {
        Idle,
        Start,
        Qualify,
        Process
    }

    private readonly record struct ResolvedSprite(
        bool Visible,
        int X,
        int Y,
        int Pattern,
        bool FourBit,
        int PaletteOffset,
        bool XMirror,
        bool YMirror,
        bool Rotate,
        int XScale,
        int YScale,
        int XScaleShift,
        int YScaleShift);

    private readonly record struct AnchorState(
        bool Visible,
        bool Unified,
        bool FourBit,
        int X,
        int Y,
        int InternalPattern,
        int PaletteOffset,
        bool XMirror,
        bool YMirror,
        bool Rotate,
        int XScale,
        int YScale,
        int XScaleShift,
        int YScaleShift)
    {
        public static AnchorState Initial { get; } = new(
            false, false, false, 0, 0, 0, 0, false, false, false, 1, 1, 0, 0);
    }

    /// <summary>Reads patterns, attributes and the line being built, for a snapshot.</summary>
    public ZxNextSpriteState CaptureState() => new(
        _patterns.ToArray(),
        _attributes.ToArray(),
        _clip.ToArray(),
        _clipIndex,
        _linePaletteIndices.ToArray(),
        _lineOpaque.ToArray(),
        _displayPaletteIndices.ToArray(),
        _displayOpaque.ToArray(),
        _buildPaletteIndices.ToArray(),
        _buildOpaque.ToArray(),
        _portSprite,
        _portAttributeIndex,
        _nextRegisterSprite,
        _patternAddress,
        _status,
        _idLockstep,
        _revision,
        _cachedLine,
        _cachedControl,
        (int)_preparationState,
        TransparencyIndex,
        SpritePreparationMasterTicks,
        LastPreparationMasterTicks,
        LastQualifiedSpriteCount,
        OvertimeCount);

    /// <summary>
    /// Puts patterns, attributes and the line in progress back, and drops the caches.
    /// </summary>
    /// <remarks>
    /// Everything resolved from the attributes is keyed on a revision counter, so the
    /// caches are invalidated rather than carried: restoring a resolved sprite list that
    /// disagreed with the attributes it came from would be worse than rebuilding it.
    /// </remarks>
    public void RestoreState(ZxNextSpriteState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.Patterns.CopyTo(_patterns, 0);
        state.Attributes.CopyTo(_attributes, 0);
        state.Clip.CopyTo(_clip, 0);
        _clipIndex = state.ClipIndex;
        state.LinePaletteIndices.CopyTo(_linePaletteIndices, 0);
        state.LineOpaque.CopyTo(_lineOpaque, 0);
        state.DisplayPaletteIndices.CopyTo(_displayPaletteIndices, 0);
        state.DisplayOpaque.CopyTo(_displayOpaque, 0);
        state.BuildPaletteIndices.CopyTo(_buildPaletteIndices, 0);
        state.BuildOpaque.CopyTo(_buildOpaque, 0);
        _portSprite = state.PortSprite;
        _portAttributeIndex = state.PortAttributeIndex;
        _nextRegisterSprite = state.NextRegisterSprite;
        _patternAddress = state.PatternAddress;
        _status = state.Status;
        _idLockstep = state.IdLockstep;
        _revision = state.Revision;
        _cachedLine = state.CachedLine;
        _cachedControl = state.CachedControl;
        _preparationState = (SpritePreparationState)state.PreparationState;
        TransparencyIndex = state.TransparencyIndex;
        SpritePreparationMasterTicks = state.SpritePreparationMasterTicks;
        LastPreparationMasterTicks = state.LastPreparationMasterTicks;
        LastQualifiedSpriteCount = state.LastQualifiedSpriteCount;
        OvertimeCount = state.OvertimeCount;

        _cachedRevision = -1;
        _resolvedRevision = -1;
        _preparationCacheRevision = -1;
        _preparationCacheValid = false;
    }
}
