using ZedExEss.FileHandlers;
using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Zx8x.Tape;

/// <summary>
/// Owns TZX media attached to a ZX80/ZX81 and advances its EAR waveform from
/// the ZX8x CPU clock without changing the TZX file's 3.5 MHz time base.
/// </summary>
/// <remarks>
/// TZX pulse lengths are defined in 3.5 MHz reference T-states, whereas both
/// Sinclair ZX8x machines run at 3.25 MHz. The clocks have an exact 14:13
/// ratio. Carrying the division remainder between calls prevents the gradual
/// phase and pause-length drift caused by rounding every instruction separately.
/// </remarks>
public sealed class Zx8xTapeSession
{
    public const int TzxReferenceClockHz = 3_500_000;
    private const int MachineClockRatio = 13;
    private const int TzxClockRatio = 14;

    private readonly Zx8xCassetteDevice _cassette;
    private TzxLoader? _loader;
    private ulong _lastMachineTstate;
    private int _conversionRemainder;

    public Zx8xTapeSession(Zx8xCassetteDevice cassette)
    {
        _cassette = cassette ?? throw new ArgumentNullException(nameof(cassette));
    }

    public TzxLoader? Loader => Volatile.Read(ref _loader);
    public string? Path { get; private set; }
    public bool IsAttached => Loader != null;

    public event EventHandler<int>? BlockIndexChanged;
    public event EventHandler<TapeStopReason>? PlaybackStopped;

    /// <summary>Decodes and attaches a TZX without starting its motor.</summary>
    public void LoadTzx(string path, ulong currentMachineTstate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(System.IO.Path.GetExtension(path), ".tzx", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("ZX80/ZX81 cassette playback currently accepts TZX images only.");
        }

        var loader = new TzxLoader(new EarSink(_cassette), TzxReferenceClockHz);
        loader.LoadTape(path);
        loader.BlockIndexChanged += OnBlockIndexChanged;
        loader.PlaybackStopped += OnPlaybackStopped;

        TzxLoader? previous = Interlocked.Exchange(ref _loader, loader);
        DetachEvents(previous);
        Path = System.IO.Path.GetFullPath(path);
        Rebase(currentMachineTstate);
        _cassette.InputHigh = false;
    }

    public void Play(ulong currentMachineTstate)
    {
        Rebase(currentMachineTstate);
        Loader?.Play();
    }

    public void Stop(ulong currentMachineTstate)
    {
        AdvanceTo(currentMachineTstate);
        Loader?.Stop();
        Rebase(currentMachineTstate);
    }

    public void Rewind(ulong currentMachineTstate)
    {
        Loader?.Reset();
        Rebase(currentMachineTstate);
    }

    public void JumpToBlock(int blockIndex, ulong currentMachineTstate)
    {
        Rebase(currentMachineTstate);
        Loader?.JumpToBlock(blockIndex);
    }

    public void Eject(ulong currentMachineTstate)
    {
        TzxLoader? previous = Interlocked.Exchange(ref _loader, null);
        previous?.Stop();
        DetachEvents(previous);
        Path = null;
        Rebase(currentMachineTstate);
        _cassette.InputHigh = false;
    }

    /// <summary>
    /// Advances the tape to an absolute ZX8x CPU timestamp. Calls are cheap when
    /// no tape is attached and are valid at both instruction and I/O boundaries.
    /// </summary>
    public void AdvanceTo(ulong machineTstate)
    {
        if (machineTstate < _lastMachineTstate)
        {
            Rebase(machineTstate);
            return;
        }

        ulong elapsed = machineTstate - _lastMachineTstate;
        _lastMachineTstate = machineTstate;
        if (elapsed == 0)
        {
            return;
        }

        TzxLoader? loader = Loader;
        if (loader == null)
        {
            _conversionRemainder = 0;
            return;
        }

        // Normal calls span one instruction, but chunking makes the conversion
        // safe even when a diagnostic advances a machine by a very large slice.
        while (elapsed > 0)
        {
            uint chunk = (uint)Math.Min(elapsed, (ulong)(int.MaxValue / TzxClockRatio));
            long scaled = (long)chunk * TzxClockRatio + _conversionRemainder;
            int referenceTstates = (int)(scaled / MachineClockRatio);
            _conversionRemainder = (int)(scaled % MachineClockRatio);
            if (referenceTstates > 0)
            {
                loader.Step(referenceTstates);
            }

            elapsed -= chunk;
        }
    }

    /// <summary>Reanchors tape time after reset, media navigation or host suspension.</summary>
    public void Rebase(ulong machineTstate)
    {
        _lastMachineTstate = machineTstate;
        _conversionRemainder = 0;
    }

    private void OnBlockIndexChanged(object? sender, int blockIndex) =>
        BlockIndexChanged?.Invoke(this, blockIndex);

    private void OnPlaybackStopped(object? sender, TapeStopReason reason) =>
        PlaybackStopped?.Invoke(this, reason);

    private void DetachEvents(TzxLoader? loader)
    {
        if (loader == null)
        {
            return;
        }

        loader.BlockIndexChanged -= OnBlockIndexChanged;
        loader.PlaybackStopped -= OnPlaybackStopped;
    }

    private sealed class EarSink(Zx8xCassetteDevice cassette) : IEarInputSink
    {
        public void SetEarLevel(bool high) => cassette.InputHigh = high;
    }
}
