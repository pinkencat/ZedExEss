using System.Threading;

namespace ZedExEss.Zx8x.Core;

/// <summary>Runs ZX80/ZX81 frames without wall-clock throttling.</summary>
public sealed class Zx8xTurboRunner : IDisposable
{
    private readonly Zx8xMachine _machine;
    private readonly int _presentEveryNFrames;
    private readonly Thread _thread;
    private volatile bool _running = true;

    public Zx8xTurboRunner(Zx8xMachine machine, int presentEveryNFrames = 5)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(presentEveryNFrames);
        _presentEveryNFrames = presentEveryNFrames;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ZX8xTurboRunner",
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
    }

    private void Run()
    {
        int frame = 0;
        while (_running)
        {
            if (_machine.IsPaused)
            {
                Thread.Sleep(1);
                continue;
            }

            _machine.RunFrame((frame % _presentEveryNFrames) == 0);
            frame = frame == int.MaxValue ? 0 : frame + 1;
            if ((frame & 0x3F) == 0)
            {
                Thread.Yield();
            }
        }
    }
}
