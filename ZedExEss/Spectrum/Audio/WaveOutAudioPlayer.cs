using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System;
using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Audio
{
    /// <summary>
    /// Windows waveOut audio sink that pulls samples from the emulator on a background buffer cadence.
    /// </summary>
    /// <remarks>
    /// The producer thread advances emulation by pulling PCM into a small ring. The
    /// waveOut thread owns native headers and drains that ring when callbacks return
    /// completed buffers. Keeping native callbacks free of emulation work avoids
    /// callback re-entry and long audio stalls during UI activity.
    /// </remarks>
    public sealed class WaveOutAudioPlayer : IDisposable
    {
        private const int WaveMapper = -1;
        private const int CallbackFunction = 0x00030000;
        private const int WomDone = 0x3BD;

        private readonly IAudioSource _source;
        private readonly WaveOutProc _callback;
        private readonly AutoResetEvent _bufferEvent = new(false);
        private readonly Queue<int> _doneQueue = new();
        private readonly Lock _doneLock = new();
        private readonly Lock _waveOutLock = new();
        private readonly Thread _thread;
        private readonly Thread _producerThread;
        private readonly WaveOutBuffer[] _buffers;
        private readonly object _ringLock = new();
        private readonly short[] _ringBuffer;
        private readonly short[] _producerBuffer;
        private int _ringReadIndex;
        private int _ringWriteIndex;
        private int _ringCount;
        private IntPtr _waveOutHandle;
        private volatile bool _running;

        public WaveOutAudioPlayer(IAudioSource source, int sampleRate, int bufferSamples, int bufferCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSamples);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferCount);

            _source = source ?? throw new ArgumentNullException(nameof(source));
            _callback = OnWaveOutCallback;
            _buffers = CreateBuffers(bufferSamples, bufferCount);
            _producerBuffer = new short[bufferSamples];
            _ringBuffer = new short[bufferSamples];

            var format = new WaveFormatEx
            {
                wFormatTag = 1,
                nChannels = 1,
                nSamplesPerSec = sampleRate,
                wBitsPerSample = 16
            };
            format.nBlockAlign = (short)(format.nChannels * format.wBitsPerSample / 8);
            format.nAvgBytesPerSec = format.nSamplesPerSec * format.nBlockAlign;
            format.cbSize = 0;

            int result = waveOutOpen(out _waveOutHandle, WaveMapper, ref format, _callback, IntPtr.Zero, CallbackFunction);
            if (result != 0)
            {
                throw new InvalidOperationException($"waveOutOpen failed with code {result}.");
            }

            PrepareHeaders();
            waveOutPause(_waveOutHandle);

            _running = true;
            _producerThread = new Thread(ProducerThread)
            {
                IsBackground = true,
                Name = "AudioProducer",
                Priority = ThreadPriority.AboveNormal
            };
            _thread = new Thread(AudioThread)
            {
                IsBackground = true,
                Name = "WaveOutAudio",
                Priority = ThreadPriority.AboveNormal
            };
            _producerThread.Start();
            _thread.Start();
        }
        public void Dispose()
        {
            _running = false;

            lock (_waveOutLock)
            {
                if (_waveOutHandle != IntPtr.Zero)
                {
                    waveOutReset(_waveOutHandle);
                }
            }

            _bufferEvent.Set();
            lock (_ringLock)
            {
                _ringReadIndex = 0;
                _ringWriteIndex = 0;
                _ringCount = 0;
                Monitor.PulseAll(_ringLock);
            }

            lock (_doneLock)
            {
                _doneQueue.Clear();
            }

            if (Thread.CurrentThread != _producerThread)
            {
                _producerThread.Join();
            }

            if (Thread.CurrentThread != _thread)
            {
                _thread.Join();
            }

            lock (_waveOutLock)
            {
                if (_waveOutHandle != IntPtr.Zero)
                {
                    UnprepareHeaders();
                    waveOutClose(_waveOutHandle);
                    _waveOutHandle = IntPtr.Zero;
                }
            }

            foreach (WaveOutBuffer buffer in _buffers)
            {
                buffer.Dispose();
            }

            _bufferEvent.Dispose();
        }
        private void ProducerThread()
        {
            while (_running)
            {
                int read = _source.ReadSamples(_producerBuffer, 0, _producerBuffer.Length);
                if (read <= 0)
                {
                    continue;
                }

                WriteRing(_producerBuffer, 0, read);
            }
        }
        private void AudioThread()
        {
            for (int i = 0; i < _buffers.Length; i++)
            {
                if (!FillAndWrite(i))
                {
                    return;
                }
            }

            waveOutRestart(_waveOutHandle);

            while (_running)
            {
                _bufferEvent.WaitOne();

                while (true)
                {
                    int index;
                    lock (_doneLock)
                    {
                        if (_doneQueue.Count == 0)
                        {
                            break;
                        }

                        index = _doneQueue.Dequeue();
                    }

                    if (!_running)
                    {
                        break;
                    }

                    if (!FillAndWrite(index))
                    {
                        break;
                    }
                }
            }
        }
        private bool FillAndWrite(int index)
        {
            WaveOutBuffer buffer = _buffers[index];
            int read = ReadRing(buffer.Samples, 0, buffer.Samples.Length);
            if (!_running && read == 0)
            {
                return false;
            }

            if (read < buffer.Samples.Length)
            {
                Array.Clear(buffer.Samples, read, buffer.Samples.Length - read);
            }

            lock (_waveOutLock)
            {
                if (!_running || _waveOutHandle == IntPtr.Zero)
                {
                    return false;
                }

                int result = waveOutWrite(_waveOutHandle, buffer.HeaderPtr, Marshal.SizeOf<WaveHeader>());
                if (result != 0)
                {
                    throw new InvalidOperationException($"waveOutWrite failed with code {result}.");
                }
            }

            return true;
        }
        private void WriteRing(short[] source, int offset, int count)
        {
            int written = 0;
            lock (_ringLock)
            {
                while (_running && written < count)
                {
                    while (_running && _ringCount == _ringBuffer.Length)
                    {
                        Monitor.Wait(_ringLock);
                    }

                    if (!_running)
                    {
                        break;
                    }

                    int free = _ringBuffer.Length - _ringCount;
                    int toCopy = Math.Min(count - written, free);
                    int first = Math.Min(toCopy, _ringBuffer.Length - _ringWriteIndex);
                    Array.Copy(source, offset + written, _ringBuffer, _ringWriteIndex, first);

                    int second = toCopy - first;
                    if (second > 0)
                    {
                        Array.Copy(source, offset + written + first, _ringBuffer, 0, second);
                    }

                    _ringWriteIndex = (_ringWriteIndex + toCopy) % _ringBuffer.Length;
                    _ringCount += toCopy;
                    written += toCopy;
                    Monitor.PulseAll(_ringLock);
                }
            }
        }
        private int ReadRing(short[] destination, int offset, int count)
        {
            int read = 0;
            lock (_ringLock)
            {
                while (read < count)
                {
                    while (_running && _ringCount == 0)
                    {
                        Monitor.Wait(_ringLock);
                    }

                    if (_ringCount == 0)
                    {
                        break;
                    }

                    int toCopy = Math.Min(count - read, _ringCount);
                    int first = Math.Min(toCopy, _ringBuffer.Length - _ringReadIndex);
                    Array.Copy(_ringBuffer, _ringReadIndex, destination, offset + read, first);

                    int second = toCopy - first;
                    if (second > 0)
                    {
                        Array.Copy(_ringBuffer, 0, destination, offset + read + first, second);
                    }

                    _ringReadIndex = (_ringReadIndex + toCopy) % _ringBuffer.Length;
                    _ringCount -= toCopy;
                    read += toCopy;
                    Monitor.PulseAll(_ringLock);
                }
            }

            return read;
        }
        private void OnWaveOutCallback(IntPtr hwo, int msg, IntPtr instance, IntPtr param1, IntPtr param2)
        {
            if (msg != WomDone || !_running)
            {
                return;
            }

            WaveHeader header = Marshal.PtrToStructure<WaveHeader>(param1);
            int index = header.dwUser.ToInt32();

            lock (_doneLock)
            {
                _doneQueue.Enqueue(index);
            }

            _bufferEvent.Set();
        }
        private void PrepareHeaders()
        {
            for (int i = 0; i < _buffers.Length; i++)
            {
                WaveOutBuffer buffer = _buffers[i];
                int result = waveOutPrepareHeader(_waveOutHandle, buffer.HeaderPtr, Marshal.SizeOf<WaveHeader>());
                if (result != 0)
                {
                    throw new InvalidOperationException($"waveOutPrepareHeader failed with code {result}.");
                }
            }
        }
        private void UnprepareHeaders()
        {
            for (int i = 0; i < _buffers.Length; i++)
            {
                WaveOutBuffer buffer = _buffers[i];
                waveOutUnprepareHeader(_waveOutHandle, buffer.HeaderPtr, Marshal.SizeOf<WaveHeader>());
            }
        }
        private static WaveOutBuffer[] CreateBuffers(int bufferSamples, int bufferCount)
        {
            var buffers = new WaveOutBuffer[bufferCount];
            for (int i = 0; i < bufferCount; i++)
            {
                buffers[i] = new WaveOutBuffer(bufferSamples, i);
            }

            return buffers;
        }
        /// <summary>Managed sample array plus its pinned native WAVEHDR descriptor.</summary>
        private sealed class WaveOutBuffer : IDisposable
        {
            public WaveOutBuffer(int sampleCount, int index)
            {
                Samples = new short[sampleCount];
                _samplesHandle = GCHandle.Alloc(Samples, GCHandleType.Pinned);

                var header = new WaveHeader
                {
                    lpData = _samplesHandle.AddrOfPinnedObject(),
                    dwBufferLength = sampleCount * sizeof(short),
                    dwUser = (IntPtr)index
                };

                HeaderPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
                Marshal.StructureToPtr(header, HeaderPtr, false);
            }

            public short[] Samples { get; }
            public IntPtr HeaderPtr { get; }

            private readonly GCHandle _samplesHandle;
            public void Dispose()
            {
                Marshal.FreeHGlobal(HeaderPtr);
                if (_samplesHandle.IsAllocated)
                {
                    _samplesHandle.Free();
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveFormatEx
        {
            public short wFormatTag;
            public short nChannels;
            public int nSamplesPerSec;
            public int nAvgBytesPerSec;
            public short nBlockAlign;
            public short wBitsPerSample;
            public short cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public int dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }
        private delegate void WaveOutProc(IntPtr hwo, int msg, IntPtr instance, IntPtr param1, IntPtr param2);

        [DllImport("winmm.dll")]
        private static extern int waveOutOpen(out IntPtr hWaveOut, int deviceId, ref WaveFormatEx format, WaveOutProc callback, IntPtr instance, int flags);

        [DllImport("winmm.dll")]
        private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr header, int size);

        [DllImport("winmm.dll")]
        private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr header, int size);

        [DllImport("winmm.dll")]
        private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr header, int size);

        [DllImport("winmm.dll")]
        private static extern int waveOutPause(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutRestart(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutReset(IntPtr hWaveOut);

        [DllImport("winmm.dll")]
        private static extern int waveOutClose(IntPtr hWaveOut);
    }
}
