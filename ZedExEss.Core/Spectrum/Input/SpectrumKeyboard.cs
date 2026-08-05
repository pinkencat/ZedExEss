using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System;
using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Input
{
    /// <summary>
    /// Spectrum keyboard matrix device, including row-mask reads for the ULA port.
    /// </summary>
    public sealed class SpectrumKeyboard : IPortDevice
    {
        private readonly byte[] _rows = new byte[8];
        private readonly byte[] _keyboardRows = new byte[8];
        private readonly byte[] _joystickRows = new byte[8];
        private readonly byte[] _rowReadCache = new byte[256];

        public SpectrumKeyboard()
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                _keyboardRows[i] = 0x1F;
                _joystickRows[i] = 0x1F;
                _rows[i] = 0x1F;
            }

            RebuildReadCache();
        }
        public bool HandlesPort(ushort port)
        {
            return (port & 0x0001) == 0;
        }
        public byte Read(ushort port)
        {
            if ((port & 0x0001) != 0)
            {
                return 0xFF;
            }

            return ReadRows((byte)(port >> 8));
        }
        public void Write(ushort port, byte value)
        {
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadRows(byte rowMask) => _rowReadCache[rowMask];
        public void SetKeyState(SpectrumKey key, bool pressed)
        {
            SetKeyState(_keyboardRows, key, pressed);
        }
        public void SetJoystickKeyState(SpectrumKey key, bool pressed)
        {
            SetKeyState(_joystickRows, key, pressed);
        }
        private void SetKeyState(byte[] rows, SpectrumKey key, bool pressed)
        {
            if (!KeyMap.TryGetValue(key, out KeyLocation location))
            {
                return;
            }

            if (pressed)
            {
                rows[location.Row] = (byte)(rows[location.Row] & ~(1 << location.Bit));
            }
            else
            {
                rows[location.Row] = (byte)(rows[location.Row] | (1 << location.Bit));
            }

            rows[location.Row] &= 0x1F;
            _rows[location.Row] = (byte)(_keyboardRows[location.Row] & _joystickRows[location.Row]);
            RebuildReadCache();
        }
        private void RebuildReadCache()
        {
            for (int mask = 0; mask < _rowReadCache.Length; mask++)
            {
                byte value = 0xFF;

                for (int row = 0; row < 8; row++)
                {
                    if ((mask & (1 << row)) == 0)
                    {
                        value &= (byte)(0xE0 | _rows[row]);
                    }
                }

                _rowReadCache[mask] = value;
            }
        }
        private readonly struct KeyLocation(int row, int bit)
        {
            public int Row { get; } = row;
            public int Bit { get; } = bit;
        }
        private static readonly Dictionary<SpectrumKey, KeyLocation> KeyMap = new()
        {
            { SpectrumKey.CapsShift, new KeyLocation(0, 0) },
            { SpectrumKey.Z, new KeyLocation(0, 1) },
            { SpectrumKey.X, new KeyLocation(0, 2) },
            { SpectrumKey.C, new KeyLocation(0, 3) },
            { SpectrumKey.V, new KeyLocation(0, 4) },

            { SpectrumKey.A, new KeyLocation(1, 0) },
            { SpectrumKey.S, new KeyLocation(1, 1) },
            { SpectrumKey.D, new KeyLocation(1, 2) },
            { SpectrumKey.F, new KeyLocation(1, 3) },
            { SpectrumKey.G, new KeyLocation(1, 4) },

            { SpectrumKey.Q, new KeyLocation(2, 0) },
            { SpectrumKey.W, new KeyLocation(2, 1) },
            { SpectrumKey.E, new KeyLocation(2, 2) },
            { SpectrumKey.R, new KeyLocation(2, 3) },
            { SpectrumKey.T, new KeyLocation(2, 4) },

            { SpectrumKey.D1, new KeyLocation(3, 0) },
            { SpectrumKey.D2, new KeyLocation(3, 1) },
            { SpectrumKey.D3, new KeyLocation(3, 2) },
            { SpectrumKey.D4, new KeyLocation(3, 3) },
            { SpectrumKey.D5, new KeyLocation(3, 4) },

            { SpectrumKey.D0, new KeyLocation(4, 0) },
            { SpectrumKey.D9, new KeyLocation(4, 1) },
            { SpectrumKey.D8, new KeyLocation(4, 2) },
            { SpectrumKey.D7, new KeyLocation(4, 3) },
            { SpectrumKey.D6, new KeyLocation(4, 4) },

            { SpectrumKey.P, new KeyLocation(5, 0) },
            { SpectrumKey.O, new KeyLocation(5, 1) },
            { SpectrumKey.I, new KeyLocation(5, 2) },
            { SpectrumKey.U, new KeyLocation(5, 3) },
            { SpectrumKey.Y, new KeyLocation(5, 4) },

            { SpectrumKey.Enter, new KeyLocation(6, 0) },
            { SpectrumKey.L, new KeyLocation(6, 1) },
            { SpectrumKey.K, new KeyLocation(6, 2) },
            { SpectrumKey.J, new KeyLocation(6, 3) },
            { SpectrumKey.H, new KeyLocation(6, 4) },

            { SpectrumKey.Space, new KeyLocation(7, 0) },
            { SpectrumKey.SymbolShift, new KeyLocation(7, 1) },
            { SpectrumKey.M, new KeyLocation(7, 2) },
            { SpectrumKey.N, new KeyLocation(7, 3) },
            { SpectrumKey.B, new KeyLocation(7, 4) }
        };
    }
}
