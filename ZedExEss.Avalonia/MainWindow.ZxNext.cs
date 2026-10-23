using ZedExEss.Spectrum.DivMmc;
using Avalonia.Controls;
using ZedExEss.Hosting;
using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Video;

namespace ZedExEss.AvaloniaHost;

/// <summary>Experimental Avalonia presentation for the portable Next machine graph.</summary>
public sealed partial class MainWindow
{
    private async Task ReplaceZxNextMachineAsync()
    {
        if (_closing || _replacingMachine)
        {
            return;
        }

        if (!await EnsureZxNextAssetPathsAsync())
        {
            SelectCurrentModelWithoutReplacing();
            return;
        }

        _replacingMachine = true;
        _statusText.Text = "Starting ZX Spectrum Next (experimental)…";
        try
        {
            ZxNextSystemSession replacement = ZxNextSystemSession.Create(
                _zxNextSystemDirectory!,
                _zxNextBootRomPath!,
                persistChanges: true);
            replacement.AutoFlushDue += OnZxNextCardSettled;
            ClearPressedKeys();
            StopRunnerAndDetachMachine();
            DisposeZxNextSession();
            _machine = null;
            _machineDevices = null;
            _zx8xMachine = null;
            _zx8xModel = null;
            _autoLoadInjector = null;
            _session.EjectTape();
            _oscilloscopeWindow?.AttachAudioSource(null);
            _debuggerWindow?.Close();
            _debuggerWindow = null;

            _zxNextSession = replacement;
            _zxNextMachine = replacement.Machine;
            _zxNextMachine.SetHostJoystickType(_joystickType);
            UpdateZxNextStateMenuState();
            _presenter?.Dispose();
            _presenter = new AvaloniaFramePresenter(
                ZxNextVideoRenderer.FrameWidth,
                ZxNextVideoRenderer.FrameHeight);
            _frameBuffer = new int[ZxNextVideoRenderer.FrameWidth * ZxNextVideoRenderer.FrameHeight];
            _dirtyLines = null;
            _gigascreenBlender = null;
            _screenImage.Source = _presenter.Bitmap;
            ApplyScreenZoom();
            QueueResizeWindowToScreenZoom();

            _debugger.Attach(
                _zxNextMachine.Cpu,
                _zxNextMachine.MemoryBus,
                checked((int)(_zxNextMachine.UlaTiming.MasterTicksPerFrame
                    / ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate)),
                checked((int)(ZxNextUlaTimingProfile.MasterTicksPerRasterPixel
                    * _zxNextMachine.UlaTiming.PixelsPerLine
                    / ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate)));
            // NextReg writes have no bus event of their own, so the register file reports
            // them straight to the debugger.
            _zxNextMachine.Registers.RegisterWritten = _debugger.OnDeviceRegisterWrite;
            UpdateDebuggerHooks();
            _zxNextMachine.FrameCompleted += OnFrameCompleted;
            _fastForwardEnabled = false;
            string executionMode = StartSelectedZxNextExecution(_zxNextMachine);
            UpdatePauseCommandState(paused: false);
            _statusText.Text =
                $"ZX Spectrum Next (experimental) — {executionMode} — keyboard active";
            UpdateTapeControls();
            UpdateDiskControls();
            SelectZxNextWithoutReplacing();
            UpdateRuntimeMenuState();
        }
        catch (Exception ex)
        {
            _zxNextSystemDirectory = null;
            _zxNextBootRomPath = null;
            _statusText.Text = $"Unable to start ZX Spectrum Next: {ex.Message}";
            SelectCurrentModelWithoutReplacing();
        }
        finally
        {
            _replacingMachine = false;
            Focus();
        }
    }

    private async Task<bool> EnsureZxNextAssetPathsAsync()
    {
        if (string.IsNullOrWhiteSpace(_zxNextSystemDirectory)
            || !Directory.Exists(_zxNextSystemDirectory))
        {
            _zxNextSystemDirectory = await _fileDialogs.OpenFolderAsync(
                "Select the extracted System/Next directory");
            if (string.IsNullOrWhiteSpace(_zxNextSystemDirectory))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(_zxNextBootRomPath)
            || !File.Exists(_zxNextBootRomPath))
        {
            _zxNextBootRomPath = await _fileDialogs.OpenFileAsync(new FileDialogOptions
            {
                Title = "Select the ZX Spectrum Next loader ROM or bootrom.vhd",
                Filters =
                [
                    new FileDialogFilter("Next loader ROM", "*.bin", "*.rom", "*.vhd"),
                    new FileDialogFilter("All files", "*.*")
                ]
            });
            if (string.IsNullOrWhiteSpace(_zxNextBootRomPath))
            {
                return false;
            }
        }

        return true;
    }

    private string StartSelectedZxNextExecution(ZxNextMachine machine)
    {
        if (_turboEnabled)
        {
            machine.Audio.EndCapture();
            _zxNextTurboRunner = new ZxNextTurboRunner(machine, presentEveryNFrames: 5);
            return "turbo";
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
            machine.Audio.EndCapture();
            _zxNextRealtimeRunner = new ZxNextRealtimeFrameRunner(machine);
            _zxNextRealtimeRunner.Faulted += OnRunnerFaulted;
            return $"silent realtime (audio unavailable: {ex.Message})";
        }
    }

    private void ResetZxNextHostMachine(ZxNextResetKind kind)
    {
        ZxNextMachine? machine = _zxNextMachine;
        if (machine == null)
        {
            return;
        }

        // Reset mutates the CPU, timeline and every mapped device. Join the current
        // execution owner first so no instruction can overlap that state transition.
        StopExecutionOwner();
        machine.Joystick.ReleaseAll();
        machine.Keyboard.ReleaseAll();
        machine.Reset(kind);
        machine.SetHostJoystickType(_joystickType);
        Interlocked.Exchange(ref _renderPending, 0);
        _ = StartSelectedZxNextExecution(machine);
    }

    private void DisposeZxNextSession()
    {
        if (_zxNextMachine != null)
        {
            _zxNextMachine.Registers.RegisterWritten = null;
            _zxNextMachine.ConfigureCpuStepHooks(null, null);
        }

        SaveZxNextCardChanges();
        _zxNextSession?.Dispose();
        _zxNextSession = null;
        _zxNextMachine = null;

        // The saved states describe a machine that no longer exists. Keeping them would offer
        // to restore a Next snapshot into whatever is running next, which need not even be a
        // Next, so they go with the machine they came from.
        ClearZxNextStates();
    }

    /// <summary>
    /// Merges what the guest wrote back into the projected SD folder.
    /// </summary>
    /// <remarks>
    /// Done here rather than left to the session's disposal because disposal deliberately
    /// swallows failures so shutdown cannot be derailed by one - which would make a folder
    /// that could not be written look exactly like one with nothing to write.
    /// </remarks>
    /// <summary>
    /// The guest has stopped writing, so bring the folder up to date.
    /// </summary>
    /// <remarks>
    /// Raised from the emulation thread, so it is marshalled first; the machine is then
    /// held still for the merge, which reads the whole projected image and would
    /// otherwise race the guest's next write.
    /// </remarks>
    private void OnZxNextCardSettled() => global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        ZxNextMachine? machine = _zxNextMachine;
        if (machine == null)
        {
            return;
        }

        bool wasPaused = machine.IsPaused;
        machine.SetPaused(true);
        try
        {
            SaveZxNextCardChanges();
        }
        finally
        {
            machine.SetPaused(wasPaused);
        }
    });

    private void SaveZxNextCardChanges()
    {
        ZxNextSystemSession? session = _zxNextSession;
        if (session == null || !session.HasUnsavedCardChanges)
        {
            return;
        }

        try
        {
            SpectrumFatMergeResult result = session.FlushSdCard();
            if (result.ChangedAnything)
            {
                _statusText.Text = $"Saved {result.Written} file(s) to the ZX Next SD folder"
                    + (result.Deleted > 0 ? $", removed {result.Deleted}" : string.Empty);
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"ZX Next SD card changes could not be saved: {ex.Message}";
        }
    }

    private void SelectZxNextWithoutReplacing()
    {
        _suppressModelSelection = true;
        _modelComboBox.SelectedItem = ModelChoices.First(choice => choice.IsZxNext);
        _suppressModelSelection = false;
        UpdateModelMenuState(null, null, zxNext: true);
    }
}
