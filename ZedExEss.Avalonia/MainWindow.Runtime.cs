using Avalonia.Interactivity;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Interface1;
using ZedExEss.Zx8x.Core;
using ZedExEss.Zx8x.Memory;
using ZedExEss.Zx8x.Video;

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
    private bool _edgeLoadEnabled = true;
    private bool _semanticEdgeLoadEnabled;
    private bool _runTapeAccelerationAtMaximumSpeed = true;
    private bool _flashLoadEnabled = true;
    private bool _autoLoadTapeOnAttach;
    private bool _autoTapePlayStopEnabled = true;
    private bool _gigascreenBlendEnabled;
    private double _screenZoom = DefaultScreenZoom;
    private bool _interface1Enabled;
    private SpectrumInterface1RomRevision _interface1RomRevision = SpectrumInterface1RomRevision.Revision2;
    private Zx8xRamConfiguration _zx8xRamConfiguration = Zx8xRamConfiguration.Expansion16K;
    private Zx8xHighResolutionMode _zx8xHighResolutionMode = Zx8xHighResolutionMode.Sinclair;

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
        _autoLoadTapeOnAttach = _hostSettings.AutoLoadTapeOnAttach;
        _autoTapePlayStopEnabled = _hostSettings.AutoTapePlayStopEnabled;
        _gigascreenBlendEnabled = _hostSettings.GigascreenBlendEnabled;
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
            AutoLoadTapeOnAttach = _autoLoadTapeOnAttach,
            AutoTapePlayStopEnabled = _autoTapePlayStopEnabled,
            GigascreenBlendEnabled = _gigascreenBlendEnabled,
            Interface1Enabled = _interface1Enabled,
            Interface1RomRevision = _interface1RomRevision,
            Zx8xRamConfiguration = _zx8xRamConfiguration,
            Zx8xHighResolutionMode = _zx8xHighResolutionMode
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
            && _runTapeAccelerationAtMaximumSpeed
            && _machine?.EarInput.LoaderAccelerationEnabled == true
            && _machine?.Emulator.IsPaused == false
            && _session.Tape?.IsPlaying == true;
    }

    private bool ShouldUseZx8xFastTapeRunner()
    {
        return !_turboEnabled
            && _edgeLoadEnabled
            && _runTapeAccelerationAtMaximumSpeed
            && _zx8xMachine?.IsPaused == false
            && _zx8xMachine.Tape.Loader?.IsPlaying == true;
    }

    /// <summary>
    /// Re-evaluates execution ownership after a mode, pause, or tape-playback transition.
    /// Existing owners are retained when they already represent the requested mode.
    /// </summary>
    private void RefreshExecutionOwner()
    {
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
            if (GetExecutionOwnerKind() == zxDesired)
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
        if (GetExecutionOwnerKind() == desired)
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
    }

    private bool HasExecutionOwner()
    {
        return _audioOutput != null || _runner != null || _turboRunner != null || _fastTapeRunner != null
            || _zx8xTurboRunner != null || _zx8xFastTapeRunner != null || _zx8xRealtimeRunner != null;
    }

    private ExecutionOwnerKind GetExecutionOwnerKind()
    {
        if (_turboRunner != null || _zx8xTurboRunner != null)
        {
            return ExecutionOwnerKind.Turbo;
        }

        if (_fastTapeRunner != null || _zx8xFastTapeRunner != null)
        {
            return ExecutionOwnerKind.FastTape;
        }

        if (_audioOutput != null || _runner != null || _zx8xRealtimeRunner != null)
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
