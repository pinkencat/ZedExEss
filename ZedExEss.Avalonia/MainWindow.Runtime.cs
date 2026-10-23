using Avalonia.Controls;
using Avalonia.Interactivity;
using ZedExEss.FileHandlers;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Interface1;
using ZedExEss.Zx8x.Core;
using ZedExEss.Zx8x.Memory;
using ZedExEss.Zx8x.Video;
using ZedExEss.ZxNext.Core;

using ZedExEss.Spectrum.Multiface;

namespace ZedExEss.AvaloniaHost;

/// <summary>
/// Owns durable frontend preferences and selects the single execution driver appropriate for
/// realtime, turbo, or accelerated-tape operation.
/// </summary>
public sealed partial class MainWindow
{
    private readonly ISettingsStore _settingsStore;
    private EmulatorHostSettings _hostSettings = new();
    private SpectrumJoystickType _joystickType = SpectrumJoystickType.None;
    private TurboRunner? _turboRunner;
    private TapeFastRunner? _fastTapeRunner;
    private bool _turboEnabled;
    private bool _fastForwardEnabled;
    private int _fastForwardSpeed = 4;
    private bool _edgeLoadEnabled = true;
    private bool _semanticEdgeLoadEnabled;
    private bool _runTapeAccelerationAtMaximumSpeed = true;
    private bool _flashLoadEnabled = true;
    private bool _flashSaveEnabled = true;
    private Spectrum.Memory.SpectrumContentionTiming _contentionTiming
        = Spectrum.Memory.SpectrumContentionTiming.Early;

    // Owned by the window so a machine rebuild does not discard a recording.
    private readonly Spectrum.Tape.SpectrumTapeRecorder _tapeRecorder = new();
    private readonly Spectrum.Replay.SpectrumReplaySession _replaySession = new();
    private bool _replayEventsHooked;
    private bool _autoLoadTapeOnAttach;
    private bool _autoTapePlayStopEnabled = true;
    private bool _gigascreenBlendEnabled;
    private double _screenZoom = DefaultScreenZoom;
    private bool _interface1Enabled;
    private bool _covoxEnabled;
    private MenuItem? _covoxEnabledMenuItem;
    private MenuItem? _scorpionTurboMenuItem;
    private bool _generalSoundEnabled;
    private MenuItem? _generalSoundEnabledMenuItem;
    private SpectrumMultifaceModel _multifaceModel
        = SpectrumMultifaceModel.None;
    private MenuItem? _multifaceDisabledMenuItem;
    private MenuItem? _multifaceOneMenuItem;
    private MenuItem? _multiface128MenuItem;
    private MenuItem? _multifaceThreeMenuItem;
    private MenuItem? _multifaceNmiMenuItem;
    private SpectrumInterface1RomRevision _interface1RomRevision = SpectrumInterface1RomRevision.Revision2;
    private Zx8xRamConfiguration _zx8xRamConfiguration = Zx8xRamConfiguration.Expansion16K;
    private Zx8xHighResolutionMode _zx8xHighResolutionMode = Zx8xHighResolutionMode.Sinclair;
    private string? _zxNextSystemDirectory;
    private string? _zxNextBootRomPath;
    private ZxNextRealtimeFrameRunner? _zxNextRealtimeRunner;
    private ZxNextTurboRunner? _zxNextTurboRunner;

    private static ISettingsStore CreateSettingsStore()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZedExEss",
            "settings.json");
        return new JsonFileSettingsStore(path);
    }

    private void ApplyHostSettings(EmulatorHostSettings settings)
    {
        _hostSettings = settings ?? new EmulatorHostSettings();
        _screenZoom = double.IsFinite(_hostSettings.ScreenZoom)
            ? Math.Clamp(_hostSettings.ScreenZoom, MinScreenZoom, MaxScreenZoom)
            : DefaultScreenZoom;
        _mediaBrowserVisible = _hostSettings.TapeBrowserVisible;
        _joystickType = Enum.IsDefined(typeof(SpectrumJoystickType), _hostSettings.JoystickType)
            ? _hostSettings.JoystickType
            : SpectrumJoystickType.None;
        _edgeLoadEnabled = _hostSettings.PollingLoaderAccelerationEnabled;
        _semanticEdgeLoadEnabled = _hostSettings.SemanticLoaderAccelerationEnabled;
        _runTapeAccelerationAtMaximumSpeed = _hostSettings.RunTapeAccelerationAtMaximumSpeed;
        _flashLoadEnabled = _hostSettings.FlashLoadEnabled;
        _flashSaveEnabled = _hostSettings.FlashSaveEnabled;
        _contentionTiming = _hostSettings.LateContentionTimings
            ? Spectrum.Memory.SpectrumContentionTiming.Late
            : Spectrum.Memory.SpectrumContentionTiming.Early;
        _autoLoadTapeOnAttach = _hostSettings.AutoLoadTapeOnAttach;
        _autoTapePlayStopEnabled = _hostSettings.AutoTapePlayStopEnabled;
        _gigascreenBlendEnabled = _hostSettings.GigascreenBlendEnabled;
        _fastForwardSpeed = Math.Clamp(
            _hostSettings.FastForwardSpeed,
            TimeStretchAudioSource.MinimumSpeedMultiplier,
            TimeStretchAudioSource.MaximumSpeedMultiplier);
        _interface1Enabled = _hostSettings.Interface1Enabled;
        _interface1RomRevision = Enum.IsDefined(typeof(SpectrumInterface1RomRevision), _hostSettings.Interface1RomRevision)
            ? _hostSettings.Interface1RomRevision
            : SpectrumInterface1RomRevision.Revision2;
        _zx8xRamConfiguration = Enum.IsDefined(typeof(Zx8xRamConfiguration), _hostSettings.Zx8xRamConfiguration)
            ? _hostSettings.Zx8xRamConfiguration
            : Zx8xRamConfiguration.Expansion16K;
        _zx8xHighResolutionMode = Enum.IsDefined(typeof(Zx8xHighResolutionMode), _hostSettings.Zx8xHighResolutionMode)
            ? _hostSettings.Zx8xHighResolutionMode
            : Zx8xHighResolutionMode.Sinclair;
        _zxNextSystemDirectory = _hostSettings.ZxNextSystemDirectory;
        _zxNextBootRomPath = _hostSettings.ZxNextBootRomPath;
    }

    private void SaveHostSettings()
    {
        _hostSettings = _hostSettings with
        {
            ScreenZoom = _screenZoom,
            TapeBrowserVisible = _mediaBrowserVisible,
            JoystickType = _joystickType,
            PollingLoaderAccelerationEnabled = _edgeLoadEnabled,
            SemanticLoaderAccelerationEnabled = _semanticEdgeLoadEnabled,
            RunTapeAccelerationAtMaximumSpeed = _runTapeAccelerationAtMaximumSpeed,
            FlashLoadEnabled = _flashLoadEnabled,
            FlashSaveEnabled = _flashSaveEnabled,
            LateContentionTimings = _contentionTiming == Spectrum.Memory.SpectrumContentionTiming.Late,
            AutoLoadTapeOnAttach = _autoLoadTapeOnAttach,
            AutoTapePlayStopEnabled = _autoTapePlayStopEnabled,
            GigascreenBlendEnabled = _gigascreenBlendEnabled,
            FastForwardSpeed = _fastForwardSpeed,
            Interface1Enabled = _interface1Enabled,
            Interface1RomRevision = _interface1RomRevision,
            Zx8xRamConfiguration = _zx8xRamConfiguration,
            Zx8xHighResolutionMode = _zx8xHighResolutionMode,
            ZxNextSystemDirectory = _zxNextSystemDirectory,
            ZxNextBootRomPath = _zxNextBootRomPath
        };

        try
        {
            _settingsStore.Save(_hostSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _statusText.Text = $"Unable to save settings: {ex.Message}";
        }
    }

    private void ApplyMachinePreferences(SpectrumMachine machine)
    {
        machine.Joystick.Type = _joystickType;
        machine.EarInput.EdgeLoadingEnabled = _edgeLoadEnabled;
        machine.EarInput.SemanticAccelerationEnabled = _semanticEdgeLoadEnabled;
        machine.EarInput.AutoPlayEnabled = _autoTapePlayStopEnabled;
        machine.Emulator.ForceFullFrameCopy = _gigascreenBlendEnabled;
    }

    private string StartSelectedExecution(SpectrumMachine machine)
    {
        if (_debugger.IsPaused)
        {
            return "debugger paused";
        }

        if (_turboEnabled)
        {
            _turboRunner = new TurboRunner(machine.Emulator, presentEveryNFrames: 5);
            return "turbo";
        }

        if (ShouldUseFastTapeRunner())
        {
            _fastTapeRunner = new TapeFastRunner(machine.Emulator);
            return "fast tape";
        }

        return StartRealtimeExecution(machine);
    }

    private bool ShouldUseFastTapeRunner()
    {
        return !_turboEnabled
            && !_fastForwardEnabled
            && _runTapeAccelerationAtMaximumSpeed
            && _machine?.EarInput.LoaderAccelerationEnabled == true
            && _machine?.Emulator.IsPaused == false
            && _session.Tape?.IsPlaying == true;
    }

    private bool ShouldUseZx8xFastTapeRunner()
    {
        return !_turboEnabled
            && !_fastForwardEnabled
            && _edgeLoadEnabled
            && _runTapeAccelerationAtMaximumSpeed
            && _zx8xMachine?.IsPaused == false
            && _zx8xMachine.Tape.Loader?.IsPlaying == true;
    }

    /// <summary>
    /// Re-evaluates execution ownership after a mode, pause, or tape-playback transition.
    /// Existing owners are retained when they already represent the requested mode.
    /// </summary>
    private void RefreshExecutionOwner(bool forceRestart = false)
    {
        if (_zxNextMachine != null)
        {
            if (_closing || _replacingMachine)
            {
                return;
            }

            ExecutionOwnerKind nextDesired = _turboEnabled
                ? ExecutionOwnerKind.Turbo
                : ExecutionOwnerKind.Realtime;
            if (!forceRestart && GetExecutionOwnerKind() == nextDesired)
            {
                return;
            }

            StopExecutionOwner();
            _ = StartSelectedZxNextExecution(_zxNextMachine);
            UpdateRuntimeMenuState();
            return;
        }

        if (_zx8xMachine != null)
        {
            if (_closing || _replacingMachine || _debugger.IsPaused)
            {
                return;
            }

            ExecutionOwnerKind zxDesired = _turboEnabled
                ? ExecutionOwnerKind.Turbo
                : ShouldUseZx8xFastTapeRunner()
                    ? ExecutionOwnerKind.FastTape
                    : ExecutionOwnerKind.Realtime;
            if (!forceRestart && GetExecutionOwnerKind() == zxDesired)
            {
                return;
            }

            StopExecutionOwner();
            _ = StartSelectedZx8xExecution(_zx8xMachine);
            UpdateRuntimeMenuState();
            return;
        }

        SpectrumMachine? machine = _machine;
        if (machine == null || _closing || _replacingMachine || _debugger.IsPaused)
        {
            return;
        }

        ExecutionOwnerKind desired = _turboEnabled
            ? ExecutionOwnerKind.Turbo
            : ShouldUseFastTapeRunner()
                ? ExecutionOwnerKind.FastTape
                : ExecutionOwnerKind.Realtime;
        if (!forceRestart && GetExecutionOwnerKind() == desired)
        {
            return;
        }

        StopExecutionOwner();
        _ = StartSelectedExecution(machine);
        UpdateRuntimeMenuState();
    }

    private void StopAcceleratedExecutionOwners()
    {
        TurboRunner? turbo = _turboRunner;
        _turboRunner = null;
        turbo?.Dispose();

        TapeFastRunner? fastTape = _fastTapeRunner;
        _fastTapeRunner = null;
        fastTape?.Dispose();

        Zx8xTurboRunner? zxTurbo = _zx8xTurboRunner;
        _zx8xTurboRunner = null;
        zxTurbo?.Dispose();

        Zx8xTurboRunner? zxFastTape = _zx8xFastTapeRunner;
        _zx8xFastTapeRunner = null;
        zxFastTape?.Dispose();

        Zx8xRealtimeFrameRunner? zxRealtime = _zx8xRealtimeRunner;
        _zx8xRealtimeRunner = null;
        if (zxRealtime != null)
        {
            zxRealtime.Faulted -= OnRunnerFaulted;
            zxRealtime.Dispose();
        }

        ZxNextTurboRunner? nextTurbo = _zxNextTurboRunner;
        _zxNextTurboRunner = null;
        nextTurbo?.Dispose();

        ZxNextRealtimeFrameRunner? nextRealtime = _zxNextRealtimeRunner;
        _zxNextRealtimeRunner = null;
        if (nextRealtime != null)
        {
            nextRealtime.Faulted -= OnRunnerFaulted;
            nextRealtime.Dispose();
        }
    }

    private bool HasExecutionOwner()
    {
        return _audioOutput != null || _runner != null || _turboRunner != null || _fastTapeRunner != null
            || _zx8xTurboRunner != null || _zx8xFastTapeRunner != null || _zx8xRealtimeRunner != null
            || _zxNextTurboRunner != null || _zxNextRealtimeRunner != null;
    }

    private ExecutionOwnerKind GetExecutionOwnerKind()
    {
        if (_turboRunner != null || _zx8xTurboRunner != null || _zxNextTurboRunner != null)
        {
            return ExecutionOwnerKind.Turbo;
        }

        if (_fastTapeRunner != null || _zx8xFastTapeRunner != null)
        {
            return ExecutionOwnerKind.FastTape;
        }

        if (_audioOutput != null || _runner != null || _zx8xRealtimeRunner != null
            || _zxNextRealtimeRunner != null)
        {
            return ExecutionOwnerKind.Realtime;
        }

        return ExecutionOwnerKind.None;
    }

    private string GetSelectedExecutionModeText()
    {
        return GetExecutionOwnerKind() switch
        {
            ExecutionOwnerKind.Turbo => "turbo",
            ExecutionOwnerKind.FastTape => "fast tape",
            ExecutionOwnerKind.Realtime when _fastForwardEnabled && _audioOutput?.IsRunning == true =>
                $"fast-forward {_fastForwardSpeed}x",
            ExecutionOwnerKind.Realtime when _audioOutput?.IsRunning == true => "SDL audio realtime",
            ExecutionOwnerKind.Realtime => "silent realtime",
            _ => _debugger.IsPaused ? "debugger paused" : "stopped"
        };
    }

    private void OnTurboMenuClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _turboEnabled = _turboMenuItem.IsChecked;
        if (_turboEnabled)
        {
            _fastForwardEnabled = false;
        }
        RefreshExecutionOwner();
        UpdateRuntimeMenuState();
        if (_machine != null)
        {
            _statusText.Text = $"{FormatModel(_machine.Model)} — {GetExecutionModeText()} — keyboard active";
        }
        else if (_zx8xMachine != null)
        {
            _statusText.Text = $"{FormatZx8xModel(_zx8xMachine.Model)} — {GetExecutionModeText()} — keyboard active";
        }
        else if (_zxNextMachine != null)
        {
            _statusText.Text = $"ZX Spectrum Next (experimental) — {GetExecutionModeText()} — keyboard active";
        }

        Focus();
    }

    private void OnFastForwardMenuClicked(object? sender, RoutedEventArgs e)
    {
        if (!_updatingCommandChecks)
        {
            SetFastForwardMode(_fastForwardMenuItem.IsChecked);
        }
    }

    private void OnQuickFastForwardClicked(object? sender, RoutedEventArgs e)
    {
        SetFastForwardMode(!_fastForwardEnabled);
    }

    private void OnFastForwardSpeedClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks
            || sender is not MenuItem item
            || !int.TryParse(item.Tag?.ToString(), out int speed))
        {
            UpdateRuntimeMenuState();
            return;
        }

        speed = Math.Clamp(
            speed,
            TimeStretchAudioSource.MinimumSpeedMultiplier,
            TimeStretchAudioSource.MaximumSpeedMultiplier);
        if (speed == _fastForwardSpeed)
        {
            UpdateRuntimeMenuState();
            return;
        }

        _fastForwardSpeed = speed;
        if (_fastForwardEnabled)
        {
            RefreshExecutionOwner(forceRestart: true);
        }

        UpdateRuntimeMenuState();
        Focus();
    }

    private void SetFastForwardMode(bool enabled)
    {
        if (_zxNextMachine != null)
        {
            _fastForwardEnabled = false;
            UpdateRuntimeMenuState();
            _statusText.Text = "ZX Spectrum Next fast-forward audio is unavailable; use Turbo instead.";
            return;
        }

        _fastForwardEnabled = enabled;
        if (enabled)
        {
            _turboEnabled = false;
        }

        RefreshExecutionOwner(forceRestart: true);
        UpdateRuntimeMenuState();
        if (_machine != null)
        {
            _statusText.Text = $"{FormatModel(_machine.Model)} — {GetExecutionModeText()} — keyboard active";
        }
        else if (_zx8xMachine != null)
        {
            _statusText.Text = $"{FormatZx8xModel(_zx8xMachine.Model)} — {GetExecutionModeText()} — keyboard active";
        }

        Focus();
    }

    private void OnPollingAccelerationClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _edgeLoadEnabled = _pollingAccelerationMenuItem.IsChecked;
        if (_machine != null)
        {
            _machine.EarInput.EdgeLoadingEnabled = _edgeLoadEnabled;
        }

        RefreshExecutionOwner();
        UpdateRuntimeMenuState();
    }

    private void OnSemanticAccelerationClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _semanticEdgeLoadEnabled = _semanticAccelerationMenuItem.IsChecked;
        if (_machine != null)
        {
            _machine.EarInput.SemanticAccelerationEnabled = _semanticEdgeLoadEnabled;
        }

        RefreshExecutionOwner();
        UpdateRuntimeMenuState();
    }

    private void OnMaximumTapeSpeedClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _runTapeAccelerationAtMaximumSpeed = _maximumTapeSpeedMenuItem.IsChecked;
        RefreshExecutionOwner();
        UpdateRuntimeMenuState();
    }

    private void OnFlashSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _flashSaveEnabled = _flashSaveMenuItem.IsChecked;
        UpdateDebuggerHooks();
        UpdateRuntimeMenuState();
    }

    private void OnTapeRecordClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        if (_tapeRecordMenuItem.IsChecked)
        {
            _tapeRecorder.Start();
            _statusText.Text = "Recording tape output";
        }
        else
        {
            _tapeRecorder.Stop();
        }

        UpdateDebuggerHooks();
        UpdateRuntimeMenuState();
    }

    private async void OnSaveTapeRecordingClicked(object? sender, RoutedEventArgs e)
    {
        _tapeRecorder.Stop();
        if (!_tapeRecorder.HasRecording)
        {
            _statusText.Text = "Nothing recorded yet - switch on tape recording, then save from the machine.";
            UpdateRuntimeMenuState();
            return;
        }

        string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
        {
            Title = "Save Tape Recording",
            DefaultExtension = ".tap",
            SuggestedFileName = "recording.tap",
            Filters =
            [
                new FileDialogFilter("Tape images", "*.tap"),
                new FileDialogFilter("TZX tape images", "*.tzx"),
                new FileDialogFilter("All files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        try
        {
            bool tzx = string.Equals(Path.GetExtension(path), ".tzx", StringComparison.OrdinalIgnoreCase);
            File.WriteAllBytes(path, tzx ? _tapeRecorder.BuildTzx() : _tapeRecorder.BuildTap());

            int standard = _tapeRecorder.StandardBlockCount;
            int raw = _tapeRecorder.Blocks.Count - standard;
            string written = $"Saved {standard} block{(standard == 1 ? string.Empty : "s")} to {Path.GetFileName(path)}";
            _statusText.Text = raw == 0
                ? written
                : tzx
                    ? $"{written} plus {raw} raw pulse run{(raw == 1 ? string.Empty : "s")}"
                    : $"{written}; {raw} run{(raw == 1 ? string.Empty : "s")} of undecoded pulses need a TZX";
        }
        catch (IOException ex)
        {
            _statusText.Text = $"Unable to save recording: {ex.Message}";
        }

        UpdateRuntimeMenuState();
    }

    /// <summary>
    /// Switches the ULA contention phase.
    /// </summary>
    /// <remarks>
    /// The tables are swapped on the running machine rather than rebuilding it: the point
    /// of the setting is to compare the two against the same software.
    /// </remarks>
    private void OnLateContentionClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _contentionTiming = _lateContentionMenuItem.IsChecked
            ? Spectrum.Memory.SpectrumContentionTiming.Late
            : Spectrum.Memory.SpectrumContentionTiming.Early;
        if (_machine?.Contention != null)
        {
            _machine.Contention.Timing = _contentionTiming;
        }

        _statusText.Text = $"{_contentionTiming} contention timings";
        UpdateRuntimeMenuState();
    }

    private void OnReplayRecordClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        if (_replayRecordMenuItem.IsChecked)
        {
            _replaySession.StartRecording();
            _statusText.Text = "Recording input";
        }
        else
        {
            _replaySession.Stop();
            _statusText.Text = $"Recorded {_replaySession.FrameCount} frames";
        }

        UpdateRuntimeMenuState();
    }

    private async void OnSaveReplayClicked(object? sender, RoutedEventArgs e)
    {
        _replaySession.Stop();
        if (!_replaySession.HasRecording)
        {
            _statusText.Text = "Nothing has been recorded yet.";
            UpdateRuntimeMenuState();
            return;
        }

        string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
        {
            Title = "Save Input Recording",
            DefaultExtension = ".rzx",
            SuggestedFileName = $"{_machine?.Model}.rzx",
            Filters =
            [
                new FileDialogFilter("RZX input recordings", "*.rzx"),
                new FileDialogFilter("All files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        try
        {
            RzxFile.Write(path, _replaySession.BuildRecording());
            _statusText.Text = $"Saved {_replaySession.FrameCount} recorded frames";
        }
        catch (IOException ex)
        {
            _statusText.Text = $"Unable to save recording: {ex.Message}";
        }

        UpdateRuntimeMenuState();
    }

    private async void OnPlayReplayClicked(object? sender, RoutedEventArgs e)
    {
        string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
        {
            Title = "Play Input Recording",
            DefaultExtension = ".rzx",
            Filters =
            [
                new FileDialogFilter("RZX input recordings", "*.rzx"),
                new FileDialogFilter("All files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        // Hold the machine still for the whole of this. Reading the file, decoding its
        // snapshot and rebuilding the machine all write state the processor is otherwise
        // free to be executing against, and a recording that turns out to be unloadable
        // would leave it half-changed.
        bool wasPaused = _machine?.Emulator.IsPaused == true;
        SetEmulationPaused(true);
        try
        {
            RzxRecording recording = RzxFile.Read(path);

            // The snapshot inside decides the model, so the machine may have to be
            // rebuilt before the recording can start.
            SpectrumModel required = Spectrum.Replay.SpectrumReplaySession.DetectModel(recording);
            if (_machine == null || required != _machine.Model)
            {
                ReplaceMachine(required, preserveTape: false, rewindTape: false);

                // A different machine: the one just built has to be held still too.
                SetEmulationPaused(true);
            }

            _replaySession.StartPlayback(recording);
            _statusText.Text = $"Replaying {_replaySession.FrameCount} frames";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            _statusText.Text = $"Unable to play recording: {ex.Message}";
        }
        finally
        {
            SetEmulationPaused(wasPaused);
        }

        UpdateRuntimeMenuState();
    }

    /// <summary>Holds the Spectrum still, whichever machine is currently loaded.</summary>
    private void SetEmulationPaused(bool paused)
    {
        if (_machine != null)
        {
            _machine.Emulator.SetPaused(paused);
        }
    }

    private void OnStopReplayClicked(object? sender, RoutedEventArgs e)
    {
        _replaySession.Stop();
        _statusText.Text = "Replay stopped";
        UpdateRuntimeMenuState();
    }

    private void OnReplayRewindEnabledClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _replaySession.RewindEnabled = _replayRewindEnabledMenuItem.IsChecked;
        UpdateRuntimeMenuState();
    }

    /// <summary>
    /// Steps the machine a tenth of a second into the past.
    /// </summary>
    /// <remarks>
    /// Most useful with emulation paused, which is how a run is built up a step at a
    /// time: pause, step back, try the input again.
    /// </remarks>
    private void StepReplayBack()
    {
        if (!_replaySession.StepBack())
        {
            _statusText.Text = "No rewind history to step back through";
            return;
        }

        _statusText.Text = _replaySession.Mode == Spectrum.Replay.SpectrumReplayMode.Recording
            ? $"Stepped back - {_replaySession.FrameCount} frames recorded, {_replaySession.RewindSecondsAvailable:F1}s of history left"
            : $"Stepped back - {_replaySession.RewindSecondsAvailable:F1}s of history left";
        UpdateRuntimeMenuState();
    }

    private void OnDiscardTapeRecordingClicked(object? sender, RoutedEventArgs e)
    {
        _tapeRecorder.Stop();
        _tapeRecorder.Clear();
        _statusText.Text = "Tape recording discarded";
        UpdateRuntimeMenuState();
    }

    private void OnFlashLoadClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _flashLoadEnabled = _flashLoadMenuItem.IsChecked;
        UpdateDebuggerHooks();
        UpdateRuntimeMenuState();
    }

    private void OnAutoLoadTapeClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _autoLoadTapeOnAttach = _autoLoadTapeMenuItem.IsChecked;
        UpdateRuntimeMenuState();
    }

    private void OnAutoTapePlayClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _autoTapePlayStopEnabled = _autoTapePlayMenuItem.IsChecked;
        if (_machine != null)
        {
            _machine.EarInput.AutoPlayEnabled = _autoTapePlayStopEnabled;
        }

        UpdateDebuggerHooks();
        UpdateRuntimeMenuState();
    }

    private void OnGigascreenBlendClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _gigascreenBlendEnabled = _gigascreenBlendMenuItem.IsChecked;
        ConfigureGigascreenPresentation();
        UpdateRuntimeMenuState();
    }

    private enum ExecutionOwnerKind
    {
        None,
        Realtime,
        Turbo,
        FastTape
    }
}
