using System;
using System.Threading;

namespace ZedExEss.Spectrum.Audio
{
    /// <summary>
    /// Thread-safe multi-channel ring buffer used by the oscilloscope window. The audio
    /// thread writes source-separated samples only while the window is open; the UI
    /// thread snapshots the most recent region at display refresh rate.
    /// </summary>
    /// <remarks>
    /// Channels are written a block at a time rather than a sample at a time: a Next
    /// machine separates ten sources, and taking the lock and advancing the ring per
    /// sample per channel cost more than the mixing it was observing.
    /// </remarks>
    public sealed class AudioScopeCapture
    {
        private readonly object _sync = new();
        private readonly short[][] _channels;
        private readonly int _capacity;
        private int _writeIndex;
        private int _available;

        public AudioScopeCapture(int channelCount, int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(channelCount, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

            _capacity = capacity;
            _channels = new short[channelCount][];
            for (int channel = 0; channel < channelCount; channel++)
            {
                _channels[channel] = new short[capacity];
            }
        }

        public int ChannelCount => _channels.Length;

        public int Capacity => _capacity;

        /// <summary>
        /// Reserves the next <paramref name="sampleCount"/> samples across every channel
        /// and holds the lock until the returned block is disposed. The region starts
        /// silent, so a channel the caller does not write reads back as silence rather
        /// than as whatever the ring held a revolution ago.
        /// </summary>
        public AudioScopeBlock BeginBlock(int sampleCount)
        {
            if (sampleCount <= 0)
            {
                return default;
            }

            // A block longer than the ring can only leave its own tail behind.
            int count = Math.Min(sampleCount, _capacity);
            Monitor.Enter(_sync);
            int start = _writeIndex;
            for (int channel = 0; channel < _channels.Length; channel++)
            {
                ClearRun(_channels[channel], start, count);
            }

            return new AudioScopeBlock(this, start, count);
        }

        /// <summary>Copies the most recent <paramref name="destination"/>-length window of one channel.</summary>
        /// <remarks>
        /// A window longer than the buffer has yet accumulated is left-padded with
        /// silence, so a freshly opened window scrolls in rather than showing garbage.
        /// </remarks>
        public int CopyLatest(int channel, Span<short> destination)
        {
            if ((uint)channel >= (uint)_channels.Length || destination.Length == 0)
            {
                return 0;
            }

            lock (_sync)
            {
                int copied = Math.Min(destination.Length, _available);
                int leadingSilence = destination.Length - copied;
                if (leadingSilence > 0)
                {
                    destination[..leadingSilence].Clear();
                }

                if (copied == 0)
                {
                    return 0;
                }

                int start = _writeIndex - copied;
                if (start < 0)
                {
                    start += _capacity;
                }

                short[] source = _channels[channel];
                int firstRun = Math.Min(copied, _capacity - start);
                source.AsSpan(start, firstRun).CopyTo(destination[leadingSilence..]);
                if (firstRun < copied)
                {
                    source.AsSpan(0, copied - firstRun)
                        .CopyTo(destination[(leadingSilence + firstRun)..]);
                }

                return copied;
            }
        }

        /// <summary>Discards everything captured so far, so a re-attach does not show the old machine.</summary>
        public void Clear()
        {
            lock (_sync)
            {
                _writeIndex = 0;
                _available = 0;
                for (int channel = 0; channel < _channels.Length; channel++)
                {
                    Array.Clear(_channels[channel]);
                }
            }
        }

        internal void WriteBlockChannel(int channel, int start, int count, ReadOnlySpan<short> samples)
        {
            if ((uint)channel >= (uint)_channels.Length)
            {
                return;
            }

            int length = Math.Min(count, samples.Length);
            if (length <= 0)
            {
                return;
            }

            short[] target = _channels[channel];
            int firstRun = Math.Min(length, _capacity - start);
            samples[..firstRun].CopyTo(target.AsSpan(start, firstRun));
            if (firstRun < length)
            {
                samples.Slice(firstRun, length - firstRun).CopyTo(target.AsSpan(0, length - firstRun));
            }
        }

        internal void FillBlockChannel(int channel, int start, int count, short value)
        {
            if ((uint)channel >= (uint)_channels.Length || count <= 0)
            {
                return;
            }

            short[] target = _channels[channel];
            int firstRun = Math.Min(count, _capacity - start);
            target.AsSpan(start, firstRun).Fill(value);
            if (firstRun < count)
            {
                target.AsSpan(0, count - firstRun).Fill(value);
            }
        }

        internal void CompleteBlock(int count)
        {
            _writeIndex += count;
            if (_writeIndex >= _capacity)
            {
                _writeIndex -= _capacity;
            }

            _available = Math.Min(_available + count, _capacity);
            Monitor.Exit(_sync);
        }

        private void ClearRun(short[] buffer, int start, int count)
        {
            int firstRun = Math.Min(count, _capacity - start);
            Array.Clear(buffer, start, firstRun);
            if (firstRun < count)
            {
                Array.Clear(buffer, 0, count - firstRun);
            }
        }
    }

    /// <summary>One reserved capture region, written channel by channel and then published.</summary>
    public readonly ref struct AudioScopeBlock
    {
        private readonly AudioScopeCapture? _capture;
        private readonly int _start;
        private readonly int _count;

        internal AudioScopeBlock(AudioScopeCapture capture, int start, int count)
        {
            _capture = capture;
            _start = start;
            _count = count;
        }

        public bool IsEmpty => _capture == null;

        public void Write(int channel, ReadOnlySpan<short> samples) =>
            _capture?.WriteBlockChannel(channel, _start, _count, samples);

        /// <summary>Writes one level across the whole block, for a source held constant over it.</summary>
        public void Fill(int channel, short value) =>
            _capture?.FillBlockChannel(channel, _start, _count, value);

        public void Dispose() => _capture?.CompleteBlock(_count);
    }
}
