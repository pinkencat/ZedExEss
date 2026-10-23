using System.Diagnostics;
using System.Threading;

namespace ZedExEss.ZxNext.Core;

/// <summary>Runs a Next machine from its video clock while audio remains unavailable.</summary>
public sealed class ZxNextRealtimeFrameRunner : IDisposable
{
    private const int MaximumCatchUpFrames = 4;
    private readonly ZxNextMachine _machine;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private volatile bool _running = true;
    private bool _disposed;

    public ZxNextRealtimeFrameRunner(ZxNextMachine machine)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ZxNextRealtimeFrameRunner"
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
                double ticksPerFrame = (double)Stopwatch.Frequency
                    * _machine.UlaTiming.MasterTicksPerFrame
                    / ZxNextReferenceBaseline.MasterClockHz;
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
