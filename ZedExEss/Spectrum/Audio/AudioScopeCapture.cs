using System;

namespace ZedExEss.Spectrum.Audio
{
    /// <summary>
    /// Thread-safe ring buffer used by the oscilloscope window. The audio thread writes
    /// source-separated samples only while the window is open; the UI thread snapshots
    /// the most recent region at display refresh rate.
    /// </summary>
    public sealed class AudioScopeCapture
    {
        private readonly object _sync = new();
        private readonly short[] _beeper;
        private readonly short[] _ayA;
        private readonly short[] _ayB;
        private readonly short[] _ayC;
        private int _writeIndex;
        private int _available;

        public AudioScopeCapture(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

            _beeper = new short[capacity];
            _ayA = new short[capacity];
            _ayB = new short[capacity];
            _ayC = new short[capacity];
        }

        public int Capacity => _beeper.Length;
        public void WriteSamples(short beeper, ReadOnlySpan<short> ayA, ReadOnlySpan<short> ayB, ReadOnlySpan<short> ayC, int sampleCount)
        {
            if (sampleCount <= 0)
            {
                return;
            }

            lock (_sync)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    _beeper[_writeIndex] = beeper;
                    _ayA[_writeIndex] = i < ayA.Length ? ayA[i] : (short)0;
                    _ayB[_writeIndex] = i < ayB.Length ? ayB[i] : (short)0;
                    _ayC[_writeIndex] = i < ayC.Length ? ayC[i] : (short)0;

                    _writeIndex++;
                    if (_writeIndex == _beeper.Length)
                    {
                        _writeIndex = 0;
                    }

                    if (_available < _beeper.Length)
                    {
                        _available++;
                    }
                }
            }
        }
        public int CopyLatest(short[] beeper, short[] ayA, short[] ayB, short[] ayC, int sampleCount)
        {
            ArgumentNullException.ThrowIfNull(beeper);
            ArgumentNullException.ThrowIfNull(ayA);
            ArgumentNullException.ThrowIfNull(ayB);
            ArgumentNullException.ThrowIfNull(ayC);

            int count = Math.Min(sampleCount, Math.Min(Math.Min(beeper.Length, ayA.Length), Math.Min(ayB.Length, ayC.Length)));
            if (count <= 0)
            {
                return 0;
            }

            lock (_sync)
            {
                int copied = Math.Min(count, _available);
                int leadingZeros = count - copied;
                if (leadingZeros > 0)
                {
                    Array.Clear(beeper, 0, leadingZeros);
                    Array.Clear(ayA, 0, leadingZeros);
                    Array.Clear(ayB, 0, leadingZeros);
                    Array.Clear(ayC, 0, leadingZeros);
                }

                int start = _writeIndex - copied;
                if (start < 0)
                {
                    start += _beeper.Length;
                }

                for (int i = 0; i < copied; i++)
                {
                    int source = start + i;
                    if (source >= _beeper.Length)
                    {
                        source -= _beeper.Length;
                    }

                    int target = leadingZeros + i;
                    beeper[target] = _beeper[source];
                    ayA[target] = _ayA[source];
                    ayB[target] = _ayB[source];
                    ayC[target] = _ayC[source];
                }

                return copied;
            }
        }
    }
}
