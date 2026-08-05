namespace ZedExEss.Hosting;

/// <summary>
/// Host-owned audio device that consumes emulator PCM and therefore owns realtime execution.
/// </summary>
/// <remarks>
/// Exactly one execution owner may be active for a machine. An audio output replaces the silent
/// frame runner; it must never run alongside it or the CPU and device clocks will advance twice.
/// </remarks>
public interface IAudioOutput : IDisposable
{
    bool IsRunning { get; }

    Exception? Failure { get; }

    event Action<Exception>? Faulted;
}
