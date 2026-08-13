using System.Runtime.CompilerServices;

namespace ZedExEss.Zx8x.Input;

/// <summary>Active-low eight-row by five-column ZX80/ZX81 keyboard matrix.</summary>
public sealed class Zx8xKeyboard
{
    private readonly byte[] _rows = new byte[8];
    private readonly byte[] _rowReadCache = new byte[256];

    public Zx8xKeyboard()
    {
        Array.Fill(_rows, (byte)0x1F);
        RebuildReadCache();
    }

    /// <summary>
    /// Reads all rows selected by zero bits in A8-A15. Multiple selected rows
    /// are electrically ANDed, just as they are on the physical keyboard bus.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadRows(byte rowMask) => _rowReadCache[rowMask];

    public void SetKeyState(Zx8xKey key, bool pressed)
    {
        int keyIndex = (int)key;
        if ((uint)keyIndex >= 40)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, "Unsupported ZX8x key.");
        }

        int row = keyIndex / 5;
        int bit = keyIndex % 5;
        byte bitMask = (byte)(1 << bit);
        _rows[row] = pressed
            ? (byte)(_rows[row] & ~bitMask)
            : (byte)(_rows[row] | bitMask);
        _rows[row] &= 0x1F;
        RebuildReadCache();
    }

    public void ReleaseAll()
    {
        Array.Fill(_rows, (byte)0x1F);
        RebuildReadCache();
    }

    private void RebuildReadCache()
    {
        for (int mask = 0; mask < _rowReadCache.Length; mask++)
        {
            byte columns = 0x1F;
            for (int row = 0; row < 8; row++)
            {
                if ((mask & (1 << row)) == 0)
                {
                    columns &= _rows[row];
                }
            }

            _rowReadCache[mask] = columns;
        }
    }
}
