using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Memory;

using System.Runtime.CompilerServices;

namespace ZedExEss.ZxNext.Video;

/// <summary>ZX Spectrum Next Layer 2 registers, CPU aperture and bitmap address generator.</summary>
public sealed class ZxNextLayer2Device
{
    public const ushort ControlPort = 0x123B;

    private readonly byte[] _clip = [0x00, 0xFF, 0x00, 0xBF];
    private byte _clipIndex;
    private byte _mapSegment;
    private byte _mapOffset;

    public byte ActiveBank { get; private set; } = 8;
    public byte ShadowBank { get; private set; } = 11;
    public byte GlobalTransparencyColor { get; private set; } = 0xE3;
    public byte SpriteAndLayersControl { get; private set; }
    public byte ScrollXLeastSignificant { get; private set; }
    public byte ScrollY { get; private set; }
    public byte Control { get; private set; }
    public byte ScrollXMostSignificant { get; private set; }
    public bool DisplayEnabled { get; private set; }
    public bool MapWritesEnabled { get; private set; }
    public bool MapReadsEnabled { get; private set; }
    public bool MapShadowBank { get; private set; }
    public int LayerPriority => (SpriteAndLayersControl >> 2) & 0x07;
    public int Resolution => (Control >> 4) & 0x03;
    public int PaletteOffset => Control & 0x0F;
    public int ScrollX => (ScrollXMostSignificant << 8) | ScrollXLeastSignificant;
    public byte ClipX1 => _clip[0];
    public byte ClipX2 => _clip[1];
    public byte ClipY1 => _clip[2];
    public byte ClipY2 => _clip[3];
    public byte ClipIndex => _clipIndex;
    public byte MapSegment => _mapSegment;
    public byte MapOffset => _mapOffset;

    public bool HandlesRegister(byte register) => register is
        0x12 or 0x13 or 0x14 or 0x15 or 0x16 or 0x17 or 0x18 or 0x1C or 0x70 or 0x71;

    public byte Read(byte register) => register switch
    {
        0x12 => ActiveBank,
        0x13 => ShadowBank,
        0x14 => GlobalTransparencyColor,
        0x15 => SpriteAndLayersControl,
        0x16 => ScrollXLeastSignificant,
        0x17 => ScrollY,
        0x18 => _clip[_clipIndex],
        0x1C => _clipIndex,
        0x70 => Control,
        0x71 => ScrollXMostSignificant,
        _ => 0
    };

    public void Write(byte register, byte value)
    {
        switch (register)
        {
            case 0x12:
                ActiveBank = (byte)(value & 0x7F);
                break;
            case 0x13:
                ShadowBank = (byte)(value & 0x7F);
                break;
            case 0x14:
                GlobalTransparencyColor = value;
                break;
            case 0x15:
                SpriteAndLayersControl = value;
                break;
            case 0x16:
                ScrollXLeastSignificant = value;
                break;
            case 0x17:
                ScrollY = value;
                break;
            case 0x18:
                _clip[_clipIndex] = value;
                _clipIndex = (byte)((_clipIndex + 1) & 0x03);
                break;
            case 0x1C:
                if ((value & 0x01) != 0)
                {
                    _clipIndex = 0;
                }

                break;
            case 0x70:
                Control = (byte)(value & 0x3F);
                break;
            case 0x71:
                ScrollXMostSignificant = (byte)(value & 0x01);
                break;
        }
    }

    public byte ReadControlPort() => (byte)((_mapSegment << 6)
        | (MapShadowBank ? 0x08 : 0)
        | (MapReadsEnabled ? 0x04 : 0)
        | (DisplayEnabled ? 0x02 : 0)
        | (MapWritesEnabled ? 0x01 : 0));

    public void WriteControlPort(byte value)
    {
        if ((value & 0x10) != 0)
        {
            _mapOffset = (byte)(value & 0x07);
            return;
        }

        _mapSegment = (byte)(value >> 6);
        MapShadowBank = (value & 0x08) != 0;
        MapReadsEnabled = (value & 0x04) != 0;
        DisplayEnabled = (value & 0x02) != 0;
        MapWritesEnabled = (value & 0x01) != 0;
    }

    public void SetDisplayEnabled(bool enabled) => DisplayEnabled = enabled;

    /// <summary>Resolves a CPU access overlaid by the Layer 2 aperture to an 8 KiB RAM page.</summary>
    public bool TryResolveMappedAddress(
        ushort address,
        bool write,
        out int page,
        out int offset)
    {
        page = 0;
        offset = address & (ZxNextPhysicalMemory.PageSize - 1);
        if (write ? !MapWritesEnabled : !MapReadsEnabled)
        {
            return false;
        }

        int segment;
        if (_mapSegment == 3)
        {
            if (address >= 0xC000)
            {
                return false;
            }

            segment = address >> 14;
        }
        else
        {
            if (address >= 0x4000)
            {
                return false;
            }

            segment = _mapSegment;
        }

        int firstBank = MapShadowBank ? ShadowBank : ActiveBank;
        int bank = (firstBank + segment + _mapOffset) & 0xFF;
        page = (bank * 2) + ((address >> 13) & 0x01);
        return true;
    }

    /// <summary>Returns the palette index generated for one pixel on the stable 640x512 surface.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetPaletteIndex(
        int outputX,
        int logicalY,
        ZxNextPhysicalMemory memory,
        out byte paletteIndex)
    {
        paletteIndex = 0;
        // Called twice for every composed logical pixel, and rejected outright whenever
        // Layer 2 is off or the beam is outside it, so the test is kept inlineable.
        if (!DisplayEnabled || (uint)outputX >= 640 || (uint)logicalY >= 256)
        {
            return false;
        }

        return TryGetEnabledPaletteIndex(outputX, logicalY, memory, out paletteIndex);
    }

    /// <summary>
    /// Resolves both 640-wide half-pixels of one logical pixel together.
    /// </summary>
    /// <remarks>
    /// Every mode derives its address from <c>outputX >> 1</c>, so the two halves share
    /// the clip tests, the scroll wrap (a real division) and the RAM read. Only the
    /// 640-wide modes then split the fetched byte into two nibbles. Resolving them
    /// separately did all of that twice.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGetPaletteIndexPair(
        int outputX,
        int logicalY,
        ZxNextPhysicalMemory memory,
        out byte firstIndex,
        out byte secondIndex)
    {
        firstIndex = 0;
        secondIndex = 0;
        if (!DisplayEnabled || (uint)outputX >= 640 || (uint)logicalY >= 256)
        {
            return false;
        }

        return TryGetEnabledPaletteIndexPair(outputX, logicalY, memory, out firstIndex, out secondIndex);
    }

    private bool TryGetEnabledPaletteIndexPair(
        int outputX,
        int logicalY,
        ZxNextPhysicalMemory memory,
        out byte firstIndex,
        out byte secondIndex)
    {
        firstIndex = 0;
        secondIndex = 0;
        if (!TryResolvePixelByte(outputX, logicalY, memory, out byte pixel, out bool highResolution))
        {
            return false;
        }

        int offset = PaletteOffset << 4;
        if (!highResolution)
        {
            firstIndex = (byte)((pixel + offset) & 0xFF);
            secondIndex = firstIndex;
            return true;
        }

        // outputX is always even here, so the pair is the high nibble then the low one.
        firstIndex = (byte)(((pixel >> 4) + offset) & 0xFF);
        secondIndex = (byte)(((pixel & 0x0F) + offset) & 0xFF);
        return true;
    }

    private bool TryGetEnabledPaletteIndex(
        int outputX,
        int logicalY,
        ZxNextPhysicalMemory memory,
        out byte paletteIndex)
    {
        paletteIndex = 0;
        if (!TryResolvePixelByte(outputX, logicalY, memory, out byte pixelByte, out bool hires))
        {
            return false;
        }

        if (hires)
        {
            pixelByte = (byte)((outputX & 0x01) == 0 ? pixelByte >> 4 : pixelByte & 0x0F);
        }

        paletteIndex = (byte)((pixelByte + (PaletteOffset << 4)) & 0xFF);
        return true;
    }

    /// <summary>Shared address generation and RAM read for one 640-wide column pair.</summary>
    private bool TryResolvePixelByte(
        int outputX,
        int logicalY,
        ZxNextPhysicalMemory memory,
        out byte pixel,
        out bool highResolution)
    {
        pixel = 0;
        int x;
        int y;
        int address;
        highResolution = (Resolution & 0x02) != 0;
        if (Resolution == 0)
        {
            x = (outputX >> 1) - 32;
            y = logicalY - 32;
            if ((uint)x >= 256 || (uint)y >= 192
                || x < ClipX1 || x > ClipX2 || y < ClipY1 || y > ClipY2)
            {
                return false;
            }

            x = (x + ScrollX) & 0xFF;
            y = Wrap(y + ScrollY, 192);
            address = (y << 8) | x;
        }
        else
        {
            int byteX = outputX >> 1;
            int clipX1 = ClipX1 << 1;
            int clipX2 = (ClipX2 << 1) | 0x01;
            if ((uint)byteX >= 320 || byteX < clipX1 || byteX > clipX2
                || logicalY < ClipY1 || logicalY > ClipY2)
            {
                return false;
            }

            x = Wrap(byteX + ScrollX, 320);
            y = (logicalY + ScrollY) & 0xFF;
            address = (x << 8) | y;
        }

        int page = (ActiveBank * 2) + (address / ZxNextPhysicalMemory.PageSize);
        if (!memory.IsPageInstalled(page))
        {
            return false;
        }

        pixel = memory.ReadMasked(page, address & (ZxNextPhysicalMemory.PageSize - 1));
        return true;
    }

    public void Reset()
    {
        ActiveBank = 8;
        ShadowBank = 11;
        GlobalTransparencyColor = 0xE3;
        SpriteAndLayersControl = 0;
        ScrollXLeastSignificant = 0;
        ScrollY = 0;
        Control = 0;
        ScrollXMostSignificant = 0;
        _clip[0] = 0;
        _clip[1] = 0xFF;
        _clip[2] = 0;
        _clip[3] = 0xBF;
        _clipIndex = 0;
        _mapSegment = 0;
        _mapOffset = 0;
        DisplayEnabled = false;
        MapWritesEnabled = false;
        MapReadsEnabled = false;
        MapShadowBank = false;
    }

    private static int Wrap(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    /// <summary>Reads Layer 2's banks, scroll and clip window, for a snapshot.</summary>
    public ZxNextLayer2State CaptureState() => new(
        _clip.ToArray(),
        _clipIndex,
        _mapSegment,
        _mapOffset,
        ActiveBank,
        ShadowBank,
        GlobalTransparencyColor,
        SpriteAndLayersControl,
        ScrollXLeastSignificant,
        ScrollXMostSignificant,
        ScrollY,
        Control,
        DisplayEnabled,
        MapWritesEnabled,
        MapReadsEnabled,
        MapShadowBank);

    /// <summary>Puts Layer 2's configuration back.</summary>
    public void RestoreState(ZxNextLayer2State state)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.Clip.CopyTo(_clip, 0);
        _clipIndex = state.ClipIndex;
        _mapSegment = state.MapSegment;
        _mapOffset = state.MapOffset;
        ActiveBank = state.ActiveBank;
        ShadowBank = state.ShadowBank;
        GlobalTransparencyColor = state.GlobalTransparencyColor;
        SpriteAndLayersControl = state.SpriteAndLayersControl;
        ScrollXLeastSignificant = state.ScrollXLeastSignificant;
        ScrollXMostSignificant = state.ScrollXMostSignificant;
        ScrollY = state.ScrollY;
        Control = state.Control;
        DisplayEnabled = state.DisplayEnabled;
        MapWritesEnabled = state.MapWritesEnabled;
        MapReadsEnabled = state.MapReadsEnabled;
        MapShadowBank = state.MapShadowBank;
    }
}
