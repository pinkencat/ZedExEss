using System;
using System.Threading;

namespace ZedExEss.Spectrum.Core
{
    /// <summary>
    /// Runs frames as fast as possible with optional frame presentation skipping for turbo mode.
    /// </summary>
    /// <remarks>
    /// Skipped frames disable pixel construction only; CPU execution, contention and all timed
    /// peripherals continue normally. The runner yields periodically to avoid starving the WPF
    /// dispatcher when emulation saturates a core.
    /// </remarks>
    public sealed class TurboRunner : IDisposable
    {
        private readonly SpectrumEmulator _emulator;
        private readonly int _presentEveryNFrames;
        private readonly Thread _thread;
        private volatile bool _running;

        public TurboRunner(SpectrumEmulator emulator, int presentEveryNFrames = 1)
        {
            _emulator = emulator ?? throw new ArgumentNullException(nameof(emulator));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(presentEveryNFrames);

            _presentEveryNFrames = presentEveryNFrames;
            _running = true;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TurboRunner",
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
            try
            {
                int spin = 0;
                int frame = 0;
                bool videoEnabled = true;
                while (_running)
                {
                    bool present = (frame % _presentEveryNFrames) == 0;
                    if (present != videoEnabled)
                    {
                        _emulator.VideoEnabled = present;
                        videoEnabled = present;
                    }

                    _emulator.RunFrame(present);
                    frame++;
                    if (frame == int.MaxValue)
                    {
                        frame = 0;
                    }

                    spin++;
                    if ((spin & 0x3F) == 0)
                    {
                        Thread.Yield();
                    }
                }
            }
            finally
            {
                _emulator.VideoEnabled = true;
            }
        }
    }
}
