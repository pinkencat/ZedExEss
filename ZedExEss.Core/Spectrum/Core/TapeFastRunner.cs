using System;
using System.Diagnostics;
using System.Threading;

namespace ZedExEss.Spectrum.Core
{
    /// <summary>
    /// Runs tape loading without realtime pacing or continuous frame construction.
    /// A frame is rendered only at a wall-clock cadence so the UI can show loading
    /// progress without presentation work scaling with emulated speed.
    /// </summary>
    public sealed class TapeFastRunner : IDisposable
    {
        private static readonly long PresentationIntervalTicks = Math.Max(1, Stopwatch.Frequency / 10);
        private readonly SpectrumEmulator _emulator;
        private readonly Thread _thread;
        private volatile bool _running;

        public TapeFastRunner(SpectrumEmulator emulator)
        {
            _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
            _running = true;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TapeFastRunner",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }

        public void Dispose()
        {
            _running = false;
            if (Thread.CurrentThread != _thread)
            {
                _thread.Join();
            }

            _emulator.VideoEnabled = true;
        }

        private void Run()
        {
            bool videoEnabled = false;
            long nextPresentation = Stopwatch.GetTimestamp() + PresentationIntervalTicks;
            int spin = 0;
            try
            {
                _emulator.FastTapeCpuBatchingEnabled = true;
                _emulator.VideoEnabled = false;
                while (_running)
                {
                    long now = Stopwatch.GetTimestamp();
                    bool present = now >= nextPresentation;
                    if (present != videoEnabled)
                    {
                        _emulator.VideoEnabled = present;
                        videoEnabled = present;
                    }

                    _emulator.RunFrame(present);
                    if (present)
                    {
                        nextPresentation = Stopwatch.GetTimestamp() + PresentationIntervalTicks;
                    }

                    if (++spin % 64 == 0)
                    {
                        Thread.Yield();
                    }
                }
            }
            finally
            {
                _emulator.FastTapeCpuBatchingEnabled = false;
                _emulator.VideoEnabled = true;
            }
        }
    }
}
