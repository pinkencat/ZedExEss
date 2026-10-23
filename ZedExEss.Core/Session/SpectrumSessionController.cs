using ZedExEss.FileHandlers;

namespace ZedExEss.Spectrum.Core;

/// <summary>
/// Owns a portable machine and media whose attachment must remain coherent with that machine.
/// </summary>
/// <remarks>
/// A tape loader is constructed with the current machine's EAR sink. It therefore cannot simply
/// be retained when switching model: the controller snapshots its logical position, rebuilds it
/// against the new EAR input, and atomically reconnects both scheduler and edge acceleration.
/// </remarks>
public sealed class SpectrumSessionController
{
    private SpectrumMachine? _machine;
    private TzxLoader? _tape;
    private string? _tapePath;

    public SpectrumMachine Machine => _machine
        ?? throw new InvalidOperationException("No Spectrum machine is attached to the session.");

    public TzxLoader? Tape => _tape;
    public string? TapePath => _tapePath;
    public SpectrumDiskMediaState Disks { get; } = new();
    public SpectrumDivMmcMediaState DivMmc { get; } = new();
    public SpectrumInterface1MediaState Interface1 { get; } = new();

    public event EventHandler<TapeStopReason>? TapePlaybackStopped;

    public SpectrumTapeSessionState? CaptureTapeState()
    {
        if (_tape == null || _tapePath == null)
        {
            return null;
        }

        return new SpectrumTapeSessionState(
            _tapePath,
            _tape.CurrentBlockIndex,
            _tape.CurrentPulseOffset,
            _tape.IsPlaying);
    }

    /// <summary>
    /// Replaces the machine, optionally reconstructing the attached tape at the same logical
    /// position or at its beginning.
    /// </summary>
    public void ReplaceMachine(SpectrumMachine machine, bool preserveTape, bool rewindTape = false)
    {
        ArgumentNullException.ThrowIfNull(machine);

        SpectrumTapeSessionState? savedTape = preserveTape ? CaptureTapeState() : null;
        DetachTape(clearPath: true);
        _machine = machine;

        if (savedTape == null)
        {
            machine.AttachTape(null);
            return;
        }

        TzxLoader loader = LoadTape(savedTape.Path);
        if (rewindTape)
        {
            loader.Reset();
        }
        else
        {
            loader.JumpToBlockPulse(savedTape.BlockIndex, savedTape.PulseOffset, savedTape.WasPlaying);
        }
    }

    public TzxLoader LoadTape(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SpectrumMachine machine = Machine;

        DetachTape(clearPath: true);
        var loader = new TzxLoader(
            machine.EarInput,
            machine.CpuClockHz,
            machine.Model is SpectrumModel.Spectrum16K or SpectrumModel.Spectrum48K);

        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".tzx":
                    loader.LoadTape(path);
                    break;
                case ".csw":
                    loader.LoadCswFile(path);
                    break;
                default:
                    loader.LoadTapFile(path);
                    break;
            }

            loader.Stop();
            _tape = loader;
            _tapePath = path;
            loader.PlaybackStopped += OnTapePlaybackStopped;
            machine.AttachTape(loader);
            return loader;
        }
        catch
        {
            machine.AttachTape(null);
            _tape = null;
            _tapePath = null;
            throw;
        }
    }

    public void EjectTape()
    {
        DetachTape(clearPath: true);
        _machine?.AttachTape(null);
    }

    private void DetachTape(bool clearPath)
    {
        _machine?.AttachTape(null);
        if (_tape != null)
        {
            _tape.PlaybackStopped -= OnTapePlaybackStopped;
            _tape.Stop();
            _tape = null;
        }

        if (clearPath)
        {
            _tapePath = null;
        }
    }

    private void OnTapePlaybackStopped(object? sender, TapeStopReason reason)
    {
        TapePlaybackStopped?.Invoke(this, reason);
    }
}
