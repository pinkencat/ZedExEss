using System.Diagnostics;
using System.Threading;

namespace ZedExEss.Spectrum.Core;

/// <summary>
/// Drives a machine from its video frame clock when no host audio backend owns execution.
/// </summary>
/// <remarks>
/// This is the silent fallback used by early cross-platform hosts. It advances complete frames
/// through the normal scheduler, so CPU, ULA, contention and media timing remain intact. Once an
/// audio backend is active it must replace this runner rather than execute alongside it.
/// </remarks>
public sealed class RealtimeFrameRunner : IDisposable
{
    private const int MaximumCatchUpFrames = 4;
    private readonly SpectrumMachine _machine;
    private readonly ManualResetEventSlim _stop = new(initialState: false);
    private readonly Thread _thread;
    private volatile bool _running;
    private bool _disposed;

    public RealtimeFrameRunner(SpectrumMachine machine)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SpectrumRealtimeFrameRunner"
        };
        _running = true;
        _thread.Start();
    }

    public Exception? Failure { get; private set; }
    public bool IsRunning => _running && _thread.IsAlive;

    /// <summary>Raised on the runner thread if emulation terminates unexpectedly.</summary>
    public event Action<Exception>? Faulted;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running = false;
        _stop.Set();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join();
        }

        _stop.Dispose();
    }

    private void Run()
    {
        double ticksPerFrame = (double)Stopwatch.Frequency * _machine.TstatesPerFrame / _machine.CpuClockHz;
        double nextDeadline = Stopwatch.GetTimestamp();

        try
        {
            while (!_stop.IsSet)
            {
                _machine.Emulator.RunFrame(presentFrame: true);
                nextDeadline += ticksPerFrame;

                long now = Stopwatch.GetTimestamp();
                if (now - nextDeadline > ticksPerFrame * MaximumCatchUpFrames)
                {
                    // Do not spend seconds replaying stale frames after a debugger stop, process
                    // suspension or heavily delayed UI operation.
                    nextDeadline = now;
                }

                WaitUntil(nextDeadline);
            }
        }
        catch (Exception ex)
        {
            Failure = ex;
            Faulted?.Invoke(ex);
        }
        finally
        {
            _running = false;
        }
    }

    private void WaitUntil(double deadline)
    {
        while (!_stop.IsSet)
        {
            double remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return;
            }

            double remainingMilliseconds = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMilliseconds > 1.5)
            {
                if (_stop.Wait(TimeSpan.FromMilliseconds(remainingMilliseconds - 0.75)))
                {
                    return;
                }
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }
}
