using System.Diagnostics;
using System.Threading;

namespace ZedExEss.Zx8x.Core;

/// <summary>Silent frame-clock fallback used when a host audio device is unavailable.</summary>
public sealed class Zx8xRealtimeFrameRunner : IDisposable
{
    private const int MaximumCatchUpFrames = 4;
    private readonly Zx8xMachine _machine;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private volatile bool _running = true;
    private bool _disposed;

    public Zx8xRealtimeFrameRunner(Zx8xMachine machine)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ZX8xRealtimeFrameRunner"
        };
        _thread.Start();
    }

    public Exception? Failure { get; private set; }
    public bool IsRunning => _running && _thread.IsAlive;
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
                if (_machine.IsPaused)
                {
                    _stop.Wait(2);
                    nextDeadline = Stopwatch.GetTimestamp();
                    continue;
                }

                _machine.RunFrame(presentFrame: true);
                nextDeadline += ticksPerFrame;
                long now = Stopwatch.GetTimestamp();
                if (now - nextDeadline > ticksPerFrame * MaximumCatchUpFrames)
                {
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
            double remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            double milliseconds = remaining * 1000.0 / Stopwatch.Frequency;
            if (milliseconds > 1.5)
            {
                if (_stop.Wait(TimeSpan.FromMilliseconds(milliseconds - 0.75)))
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
