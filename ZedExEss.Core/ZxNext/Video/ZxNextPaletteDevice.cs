using ZedExEss.ZxNext.Core;
using System.Runtime.CompilerServices;

namespace ZedExEss.ZxNext.Video;

/// <summary>Next palette RAM and the ULA-facing palette NextRegs.</summary>
public sealed class ZxNextPaletteDevice
{
    private readonly ushort[][] _palettes = new ushort[8][];
    private readonly int[][] _argbPalettes = new int[8][];
    private byte _storedNineBitValue;
    private bool _nineBitSecondWrite;

    public ZxNextPaletteDevice()
    {
        for (int palette = 0; palette < _palettes.Length; palette++)
        {
            _palettes[palette] = new ushort[256];
            _argbPalettes[palette] = new int[256];
            for (int index = 0; index < 256; index++)
            {
                _palettes[palette][index] = ExpandEightBitColor((byte)index);
            }
        }

        InitializeStandardUlaPalette(0);
        InitializeStandardUlaPalette(4);
        RebuildArgbPalettes();
        ResetRegisters();
    }

    public byte Index { get; private set; }
    public byte UlaNextAttributeMask { get; private set; } = 0x07;
    public byte Control { get; private set; }
    public byte FallbackColor { get; private set; } = 0xE3;
    public byte UlaControl { get; private set; }
    public bool UlaNextEnabled => (Control & 0x01) != 0;
    public int ActiveUlaPalette => (Control & 0x02) != 0 ? 4 : 0;
    public int ActiveLayer2Palette => (Control & 0x04) != 0 ? 5 : 1;
    public int ActiveSpritePalette => (Control & 0x08) != 0 ? 6 : 2;
    public bool UlaOutputEnabled => (UlaControl & 0x80) == 0;
    public bool UlaFineScrollX => (UlaControl & 0x04) != 0;
    public bool UlaPlusEnabled => (UlaControl & 0x08) != 0;

    public bool HandlesRegister(byte register) => register is
        0x40 or 0x41 or 0x42 or 0x43 or 0x44 or 0x4A or 0x68;

    public byte Read(byte register) => register switch
    {
        0x40 => Index,
        0x41 => (byte)(GetSelectedColor() >> 1),
        0x42 => UlaNextAttributeMask,
        0x43 => Control,
        0x44 => (byte)((GetSelectedColor() & 0x01)
            | ((GetSelectedColor() >> 3) & 0xC0)),
        0x4A => FallbackColor,
        0x68 => UlaControl,
        _ => 0
    };

    public void Write(byte register, byte value)
    {
        switch (register)
        {
            case 0x40:
                Index = value;
                _nineBitSecondWrite = false;
                break;

            case 0x41:
                SetSelectedColor(ExpandEightBitColor(value));
                AdvanceIndex();
                _nineBitSecondWrite = false;
                break;

            case 0x42:
                UlaNextAttributeMask = value;
                break;

            case 0x43:
                Control = value;
                _nineBitSecondWrite = false;
                break;

            case 0x44:
                if (!_nineBitSecondWrite)
                {
                    _storedNineBitValue = value;
                    _nineBitSecondWrite = true;
                }
                else
                {
                    ushort color = (ushort)((_storedNineBitValue << 1) | (value & 0x01));
                    color |= (ushort)((value & 0xC0) << 3);

                    SetSelectedColor(color);
                    AdvanceIndex();
                    _nineBitSecondWrite = false;
                }
                break;

            case 0x4A:
                FallbackColor = value;
                break;

            case 0x68:
                UlaControl = (byte)(value & 0xFD);
                break;
        }
    }

    public int GetUlaInkColor(byte attribute)
        => ToArgb(GetUlaInkPaletteColor(attribute));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetUlaInkPaletteColor(byte attribute)
    {
        if (!UlaOutputEnabled)
        {
            return GetFallbackPaletteColor();
        }

        int index = UlaNextEnabled
            ? attribute & UlaNextAttributeMask
            : (attribute & 0x07) | ((attribute & 0x40) != 0 ? 0x08 : 0);
        return GetColorValue(ActiveUlaPalette, index);
    }

    public int GetUlaPaperColor(byte attribute)
        => ToArgb(GetUlaPaperPaletteColor(attribute));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetUlaPaperPaletteColor(byte attribute)
    {
        if (!UlaOutputEnabled)
        {
            return GetFallbackPaletteColor();
        }

        int index;
        if (!UlaNextEnabled)
        {
            index = 0x10 | ((attribute >> 3) & 0x07)
                | ((attribute & 0x40) != 0 ? 0x08 : 0);
        }
        else if (TryGetInkBitCount(UlaNextAttributeMask, out int inkBits))
        {
            if (inkBits == 8)
            {
                return GetFallbackPaletteColor();
            }

            index = 0x80 | (attribute >> inkBits);
        }
        else
        {
            return GetFallbackPaletteColor();
        }

        return GetColorValue(ActiveUlaPalette, index);
    }

    public int GetBorderColor(byte borderColor)
        => ToArgb(GetBorderPaletteColor(borderColor));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetBorderPaletteColor(byte borderColor)
    {
        if (!UlaOutputEnabled)
        {
            return GetFallbackPaletteColor();
        }

        if (UlaNextEnabled && UlaNextAttributeMask == 0xFF)
        {
            return GetFallbackPaletteColor();
        }

        int index = UlaNextEnabled ? 0x80 | (borderColor & 0x07) : 0x10 | (borderColor & 0x07);
        return GetColorValue(ActiveUlaPalette, index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetFallbackArgb() => ToArgb(ExpandEightBitColor(FallbackColor));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetFallbackPaletteColor() => ExpandEightBitColor(FallbackColor);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetLayer2PaletteColor(byte index) =>
        GetColorValue(ActiveLayer2Palette, index);

    /// <summary>Returns a direct index from the active ULA palette (LoRes output).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetUlaPaletteColor(byte index) =>
        GetColorValue(ActiveUlaPalette, index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetLayer2Argb(byte index) =>
        _argbPalettes[ActiveLayer2Palette][index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetSpritePaletteColor(byte index) =>
        GetColorValue(ActiveSpritePalette, index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetSpriteArgb(byte index) =>
        _argbPalettes[ActiveSpritePalette][index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetTilemapPaletteColor(byte index, bool secondPalette) =>
        GetColorValue(secondPalette ? 7 : 3, index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetTilemapArgb(byte index, bool secondPalette) =>
        _argbPalettes[secondPalette ? 7 : 3][index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsLayer2PriorityColor(byte index) =>
        (_palettes[ActiveLayer2Palette][index] & 0x0400) != 0;

    /// <summary>Resets palette registers while retaining palette RAM, as the core does.</summary>
    public void ResetRegisters()
    {
        Index = 0;
        UlaNextAttributeMask = 0x07;
        Control = 0;
        FallbackColor = 0xE3;
        UlaControl = 0;
        _storedNineBitValue = 0;
        _nineBitSecondWrite = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToArgb(ushort color)
    {
        int red = ExpandThreeBits((color >> 6) & 0x07);
        int green = ExpandThreeBits((color >> 3) & 0x07);
        int blue = ExpandThreeBits(color & 0x07);
        return unchecked((int)(0xFF000000u
            | ((uint)red << 16)
            | ((uint)green << 8)
            | (uint)blue));
    }

    private ushort GetSelectedColor() => _palettes[WritePalette][Index];

    private void SetSelectedColor(ushort value)
    {
        int palette = WritePalette;
        ushort stored = (ushort)(value & 0x07FF);
        _palettes[palette][Index] = stored;
        _argbPalettes[palette][Index] = ToArgb((ushort)(stored & 0x01FF));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort GetColorValue(int palette, int index) =>
        (ushort)(_palettes[palette][index] & 0x01FF);

    private int WritePalette => (Control >> 4) & 0x07;

    private void AdvanceIndex()
    {
        if ((Control & 0x80) == 0)
        {
            Index++;
        }
    }

    private void InitializeStandardUlaPalette(int palette)
    {
        for (int bright = 0; bright < 2; bright++)
        {
            int level = bright == 0 ? 5 : 7;
            for (int color = 0; color < 8; color++)
            {
                ushort value = MakeColor(
                    (color & 0x02) != 0 ? level : 0,
                    (color & 0x04) != 0 ? level : 0,
                    (color & 0x01) != 0 ? level : 0);
                int index = color | (bright << 3);
                _palettes[palette][index] = value;
                _palettes[palette][0x10 | index] = value;
            }
        }
    }

    private void RebuildArgbPalettes()
    {
        for (int palette = 0; palette < _palettes.Length; palette++)
        {
            for (int index = 0; index < _palettes[palette].Length; index++)
            {
                _argbPalettes[palette][index] = ToArgb(
                    (ushort)(_palettes[palette][index] & 0x01FF));
            }
        }
    }

    private static ushort ExpandEightBitColor(byte value) =>
        (ushort)((value << 1) | (((value & 0x03) != 0) ? 1 : 0));

    private static ushort MakeColor(int red, int green, int blue) =>
        (ushort)((red << 6) | (green << 3) | blue);

    private static int ExpandThreeBits(int value) => (value << 5) | (value << 2) | (value >> 1);

    private static bool TryGetInkBitCount(byte mask, out int bits)
    {
        bits = mask switch
        {
            0x01 => 1,
            0x03 => 2,
            0x07 => 3,
            0x0F => 4,
            0x1F => 5,
            0x3F => 6,
            0x7F => 7,
            0xFF => 8,
            _ => 0
        };
        return bits != 0;
    }

    /// <summary>Reads every palette and the write cursor, for a snapshot.</summary>
    public ZxNextPaletteState CaptureState()
    {
        var palettes = new ushort[_palettes.Length][];
        var argb = new int[_argbPalettes.Length][];
        for (int palette = 0; palette < palettes.Length; palette++)
        {
            palettes[palette] = _palettes[palette].ToArray();
            argb[palette] = _argbPalettes[palette].ToArray();
        }

        return new ZxNextPaletteState(
            palettes,
            argb,
            _storedNineBitValue,
            _nineBitSecondWrite,
            Index,
            UlaNextAttributeMask,
            Control,
            FallbackColor,
            UlaControl);
    }

    /// <summary>Puts every palette back.</summary>
    public void RestoreState(ZxNextPaletteState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        for (int palette = 0; palette < _palettes.Length; palette++)
        {
            state.Palettes[palette].CopyTo(_palettes[palette], 0);
            state.ArgbPalettes[palette].CopyTo(_argbPalettes[palette], 0);
        }

        _storedNineBitValue = state.StoredNineBitValue;
        _nineBitSecondWrite = state.NineBitSecondWrite;
        Index = state.Index;
        UlaNextAttributeMask = state.UlaNextAttributeMask;
        Control = state.Control;
        FallbackColor = state.FallbackColor;
        UlaControl = state.UlaControl;
    }
}
