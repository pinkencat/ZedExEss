using Avalonia.Input;
using ZedExEss.Spectrum.Video;
using ZedExEss.Zx8x.Core;
using ZedExEss.Zx8x.Input;
using ZedExEss.Zx8x.Media;
using ZedExEss.Zx8x.Memory;

namespace ZedExEss.AvaloniaHost;

/// <summary>Avalonia composition for the portable ZX80/ZX81 machine graph.</summary>
public sealed partial class MainWindow
{
    private Zx8xRealtimeFrameRunner? _zx8xRealtimeRunner;
    private Zx8xTurboRunner? _zx8xTurboRunner;
    private Zx8xTurboRunner? _zx8xFastTapeRunner;

    private static readonly IReadOnlyDictionary<Key, Zx8xKey[]> Zx8xKeyMap =
        new Dictionary<Key, Zx8xKey[]>
        {
            [Key.LeftShift] = [Zx8xKey.Shift], [Key.RightShift] = [Zx8xKey.Shift],
            [Key.Space] = [Zx8xKey.Space], [Key.Enter] = [Zx8xKey.NewLine],
            [Key.OemPeriod] = [Zx8xKey.Period], [Key.Decimal] = [Zx8xKey.Period],
            [Key.Back] = [Zx8xKey.Shift, Zx8xKey.D0],
            [Key.Delete] = [Zx8xKey.Shift, Zx8xKey.D0],
            [Key.Escape] = [Zx8xKey.Shift, Zx8xKey.Space],
            [Key.Left] = [Zx8xKey.Shift, Zx8xKey.D5],
            [Key.Down] = [Zx8xKey.Shift, Zx8xKey.D6],
            [Key.Up] = [Zx8xKey.Shift, Zx8xKey.D7],
            [Key.Right] = [Zx8xKey.Shift, Zx8xKey.D8],
            [Key.A] = [Zx8xKey.A], [Key.B] = [Zx8xKey.B], [Key.C] = [Zx8xKey.C],
            [Key.D] = [Zx8xKey.D], [Key.E] = [Zx8xKey.E], [Key.F] = [Zx8xKey.F],
            [Key.G] = [Zx8xKey.G], [Key.H] = [Zx8xKey.H], [Key.I] = [Zx8xKey.I],
            [Key.J] = [Zx8xKey.J], [Key.K] = [Zx8xKey.K], [Key.L] = [Zx8xKey.L],
            [Key.M] = [Zx8xKey.M], [Key.N] = [Zx8xKey.N], [Key.O] = [Zx8xKey.O],
            [Key.P] = [Zx8xKey.P], [Key.Q] = [Zx8xKey.Q], [Key.R] = [Zx8xKey.R],
            [Key.S] = [Zx8xKey.S], [Key.T] = [Zx8xKey.T], [Key.U] = [Zx8xKey.U],
            [Key.V] = [Zx8xKey.V], [Key.W] = [Zx8xKey.W], [Key.X] = [Zx8xKey.X],
            [Key.Y] = [Zx8xKey.Y], [Key.Z] = [Zx8xKey.Z],
            [Key.D0] = [Zx8xKey.D0], [Key.D1] = [Zx8xKey.D1], [Key.D2] = [Zx8xKey.D2],
            [Key.D3] = [Zx8xKey.D3], [Key.D4] = [Zx8xKey.D4], [Key.D5] = [Zx8xKey.D5],
            [Key.D6] = [Zx8xKey.D6], [Key.D7] = [Zx8xKey.D7], [Key.D8] = [Zx8xKey.D8],
            [Key.D9] = [Zx8xKey.D9],
            [Key.NumPad0] = [Zx8xKey.D0], [Key.NumPad1] = [Zx8xKey.D1],
            [Key.NumPad2] = [Zx8xKey.D2], [Key.NumPad3] = [Zx8xKey.D3],
            [Key.NumPad4] = [Zx8xKey.D4], [Key.NumPad5] = [Zx8xKey.D5],
            [Key.NumPad6] = [Zx8xKey.D6], [Key.NumPad7] = [Zx8xKey.D7],
            [Key.NumPad8] = [Zx8xKey.D8], [Key.NumPad9] = [Zx8xKey.D9]
        };

    private void ReplaceZx8xMachine(Zx8xModel model)
    {
        if (_closing || _replacingMachine)
        {
            return;
        }

        string? preservedTapePath = _zx8xMachine?.Tape.Path;
        int preservedBlock = _zx8xMachine?.Tape.Loader?.CurrentBlockIndex ?? -1;
        int preservedPulse = _zx8xMachine?.Tape.Loader?.CurrentPulseOffset ?? 0;
        bool preservedPlaying = _zx8xMachine?.Tape.Loader?.IsPlaying == true;
        _replacingMachine = true;
        _statusText.Text = $"Starting {FormatZx8xModel(model)}…";
        try
        {
            Zx8xMachine replacement = Zx8xMachineFactory.Create(
                model,
                Path.Combine(AppContext.BaseDirectory, "ROMs"),
                ramConfiguration: _zx8xRamConfiguration);
            ClearPressedKeys();
            StopRunnerAndDetachMachine();
            _machine = null;
            _machineDevices = null;
            _zx8xMachine = replacement;
            _zx8xModel = model;
            _autoLoadInjector = null;
            _session.EjectTape();
            _oscilloscopeWindow?.AttachAudioRenderer(null);
            _debuggerWindow?.Close();
            _debuggerWindow = null;

            replacement.Tape.PlaybackStopped += OnTapePlaybackStopped;
            if (preservedTapePath != null)
            {
                replacement.Tape.LoadTzx(preservedTapePath, replacement.Cpu.Cyc);
                if (preservedBlock >= 0)
                {
                    replacement.Tape.Loader?.JumpToBlockPulse(
                        preservedBlock,
                        preservedPulse,
                        preservedPlaying);
                }
            }

            _presenter?.Dispose();
            _presenter = new AvaloniaFramePresenter(replacement.FrameWidth, replacement.FrameHeight);
            _frameBuffer = new int[replacement.FrameWidth * replacement.FrameHeight];
            _dirtyLines = null;
            _gigascreenBlender = null;
            _screenImage.Source = _presenter.Bitmap;
            ApplyScreenZoom();
            QueueResizeWindowToScreenZoom();

            replacement.FrameCompleted += OnFrameCompleted;
            string executionMode = StartSelectedZx8xExecution(replacement);
            UpdatePauseCommandState(paused: false);
            _statusText.Text = $"{FormatZx8xModel(model)} — {executionMode} — keyboard active";
            UpdateTapeControls();
            UpdateDiskControls();
            SelectZx8xModelWithoutReplacing(model);
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Unable to start {FormatZx8xModel(model)}: {ex.Message}";
            SelectCurrentModelWithoutReplacing();
        }
        finally
        {
            _replacingMachine = false;
            Focus();
        }
    }

    private string StartSelectedZx8xExecution(Zx8xMachine machine)
    {
        // A turbo owner never drains PCM. Rebase before either owner starts so
        // returning to realtime cannot spend seconds replaying a stale queue.
        machine.Audio.DiscardPendingSamples(machine.Cpu.Cyc);
        if (_turboEnabled)
        {
            _zx8xTurboRunner = new Zx8xTurboRunner(machine, presentEveryNFrames: 5);
            return "turbo";
        }

        if (ShouldUseZx8xFastTapeRunner())
        {
            _zx8xFastTapeRunner = new Zx8xTurboRunner(machine, presentEveryNFrames: 5);
            return "fast tape";
        }

        try
        {
            var audioOutput = new SdlAudioOutput(
                machine,
                machine.SampleRate,
                AudioBufferSamples,
                AudioBufferCount);
            audioOutput.Faulted += OnAudioOutputFaulted;
            _audioOutput = audioOutput;
            return "SDL audio realtime";
        }
        catch (Exception ex)
        {
            _zx8xRealtimeRunner = new Zx8xRealtimeFrameRunner(machine);
            _zx8xRealtimeRunner.Faulted += OnRunnerFaulted;
            return $"silent realtime (audio unavailable: {ex.Message})";
        }
    }

    /// <summary>
    /// Restores a ZX80/ZX81 ROM SAVE image while no execution owner can race the
    /// memory copy, then resumes the execution mode selected by the user.
    /// </summary>
    private void LoadZx8xProgramImagePath(string path)
    {
        Zx8xModel requiredModel = Zx8xProgramImageLoader.GetRequiredModel(Path.GetExtension(path));
        if (_zx8xMachine == null || _zx8xModel != requiredModel)
        {
            ReplaceZx8xMachine(requiredModel);
        }

        Zx8xMachine machine = _zx8xMachine != null && _zx8xModel == requiredModel
            ? _zx8xMachine
            : throw new InvalidOperationException(
                $"Unable to start {Zx8xModelDescriptors.ForModel(requiredModel).DisplayName} for this program image.");

        StopExecutionOwner();
        string executionMode = "stopped";
        try
        {
            _ = Zx8xProgramImageLoader.LoadFile(machine, path);
            Interlocked.Exchange(ref _renderPending, 0);
            UpdatePauseCommandState(paused: false);
        }
        finally
        {
            if (!_closing && ReferenceEquals(machine, _zx8xMachine))
            {
                executionMode = StartSelectedZx8xExecution(machine);
            }
        }

        _statusText.Text = $"Loaded {Path.GetFileName(path)} â€” {executionMode} â€” keyboard active";
    }

    private void SelectZx8xModelWithoutReplacing(Zx8xModel model)
    {
        _suppressModelSelection = true;
        _modelComboBox.SelectedItem = ModelChoices.First(choice => choice.Zx8xModel == model);
        _suppressModelSelection = false;
        UpdateModelMenuState(null, model);
    }

    private void SelectCurrentModelWithoutReplacing()
    {
        if (_zx8xModel.HasValue && _zx8xMachine != null)
        {
            SelectZx8xModelWithoutReplacing(_zx8xModel.Value);
        }
        else
        {
            SelectModelWithoutReplacing(_machine?.Model ?? Spectrum.Core.SpectrumModel.Spectrum128K);
        }
    }

    private static string FormatZx8xModel(Zx8xModel model) =>
        ModelChoices.First(choice => choice.Zx8xModel == model).Name;
}
