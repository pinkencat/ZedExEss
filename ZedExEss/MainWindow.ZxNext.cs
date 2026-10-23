using ZedExEss.Spectrum.DivMmc;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Video;
using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Video;

namespace ZedExEss;

/// <summary>Experimental WPF presentation for the portable ZX Spectrum Next graph.</summary>
public partial class MainWindow
{
    private ZxNextSystemSession? _zxNextSession;
    private ZxNextMachine? _zxNextMachine;
    private ZxNextRealtimeFrameRunner? _zxNextRealtimeRunner;
    private ZxNextTurboRunner? _zxNextTurboRunner;
    private string? _zxNextSystemDirectory;
    private string? _zxNextBootRomPath;
    private ulong _lastNextFrameNumber;
    private double _lastNextFrameSeconds;

    private async void OnZxNextModelMenuClick(object sender, RoutedEventArgs e)
    {
        if (_zxNextMachine != null)
        {
            UpdateModelMenuChecks();
            return;
        }

        if (!await EnsureZxNextAssetPathsAsync())
        {
            UpdateModelMenuChecks();
            Focus();
            return;
        }

        InitializeZxNextMachine();
        Focus();
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
                    new FileDialogFilter("All Files", "*.*")
                ]
            });
            if (string.IsNullOrWhiteSpace(_zxNextBootRomPath))
            {
                return false;
            }
        }

        return true;
    }

    private void InitializeZxNextMachine()
    {
        if (string.IsNullOrWhiteSpace(_zxNextSystemDirectory)
            || string.IsNullOrWhiteSpace(_zxNextBootRomPath))
        {
            return;
        }

        ZxNextSystemSession replacement;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            replacement = ZxNextSystemSession.Create(
                _zxNextSystemDirectory,
                _zxNextBootRomPath,
                persistChanges: true);
            replacement.AutoFlushDue += OnZxNextCardSettled;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "ZX Spectrum Next Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _zxNextSystemDirectory = null;
            _zxNextBootRomPath = null;
            UpdateModelMenuChecks();
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _autoLoadInjector = null;
        if (_emulator != null)
        {
            _emulator.FrameCompleted -= OnFrameCompleted;
        }

        _oscilloscopeWindow?.AttachAudioSource(null);
        _audioRenderer = null;
        _debuggerWindow?.OwnerClosing();
        _debuggerWindow = null;
        _turboRunner?.Dispose();
        _turboRunner = null;
        _fastTapeRunner?.Dispose();
        _fastTapeRunner = null;
        _audioPlayer?.Dispose();
        _audioPlayer = null;
        StopZx8xHostMachine();
        StopZxNextHostMachine();
        _session.EjectTape();

        _machine = null!;
        _memory = null!;
        _cpu = null!;
        _emulator = null!;
        _keyboard = null!;
        _joystick = null!;
        _earInput = null!;

        _zxNextSession = replacement;
        _zxNextMachine = replacement.Machine;
        _zxNextMachine.SetHostJoystickType(_joystickType);
        UpdateZxNextStateMenuState();
        _cpuHz = _zxNextMachine.CpuClockHz;
        _tstatesPerFrame = checked((int)(
            _zxNextMachine.UlaTiming.MasterTicksPerFrame
            / ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate));
        _display = new WpfSpectrumDisplay(
            ZxNextVideoRenderer.FrameWidth,
            ZxNextVideoRenderer.FrameHeight);
        _displayHeightScale = ZxNextVideoRenderer.DisplayHeightScale;
        _presentBuffer = new int[ZxNextVideoRenderer.FrameWidth * ZxNextVideoRenderer.FrameHeight];
        _dirtyLines = new int[ZxNextVideoRenderer.FrameHeight];
        _gigascreenPreviousBuffer = null;
        _gigascreenBlendBuffer = null;
        _gigascreenHasPreviousFrame = false;
        ScreenImage.Source = _display.Bitmap;
        ApplyScreenZoom();
        UpdateZoomMenuChecks();

        _oscilloscopeWindow?.AttachAudioSource(_zxNextMachine.Audio);
        _debugger.Attach(
            _zxNextMachine.Cpu,
            _zxNextMachine.MemoryBus,
            _tstatesPerFrame,
            checked((int)(ZxNextUlaTimingProfile.MasterTicksPerRasterPixel
                * _zxNextMachine.UlaTiming.PixelsPerLine
                / ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate)));
        // NextReg writes have no bus event of their own, so the register file reports
        // them straight to the debugger.
        _zxNextMachine.Registers.RegisterWritten = _debugger.OnDeviceRegisterWrite;
        UpdateCpuStepHooks();
        _debuggerWindow?.RefreshAll(followPc: true);
        _zxNextMachine.FrameCompleted += OnZxNextFrameCompleted;
        _fastForwardEnabled = false;
        StartZxNextExecutionOwner();

        if (!_speedStopwatch.IsRunning)
        {
            _speedStopwatch.Start();
        }

        _lastNextFrameSeconds = _speedStopwatch.Elapsed.TotalSeconds;
        _lastNextFrameNumber = _zxNextMachine.UlaTiming.FrameNumber;
        Interlocked.Exchange(ref _renderPending, 0);
        UpdateModelMenuChecks();
        SetTapeControlsEnabled(false);
        UpdateQuickAccessState();
        TurboMenu.IsChecked = _turboEnabled;
        UpdateWindowTitle();
        _uiDispatcher.TryPost(ResizeWindowToScreenZoom, UiDispatchPriority.Loaded);
    }

    private void StopZxNextHostMachine()
    {
        StopZxNextExecutionOwner();
        if (_zxNextMachine != null)
        {
            _zxNextMachine.Registers.RegisterWritten = null;
            _zxNextMachine.ConfigureCpuStepHooks(null, null);
            _zxNextMachine.FrameCompleted -= OnZxNextFrameCompleted;
            _zxNextMachine.Joystick.ReleaseAll();
            _zxNextMachine.Keyboard.ReleaseAll();
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
    private void OnZxNextCardSettled()
    {
        _uiDispatcher.TryPost(() =>
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
    }

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
                StatusText.Text = $"Saved {result.Written} file(s) to the ZX Next SD folder"
                    + (result.Deleted > 0 ? $", removed {result.Deleted}" : string.Empty);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Changes made on the ZX Next SD card could not be saved: {ex.Message}",
                "ZX Spectrum Next Storage",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void StartZxNextExecutionOwner()
    {
        ZxNextMachine? machine = _zxNextMachine;
        if (machine == null)
        {
            return;
        }

        if (_turboEnabled)
        {
            machine.Audio.EndCapture();
            _zxNextTurboRunner = new ZxNextTurboRunner(machine, presentEveryNFrames: 5);
        }
        else
        {
            try
            {
                _audioPlayer = CreateWaveOutPlayer(machine, machine.SampleRate);
            }
            catch
            {
                machine.Audio.EndCapture();
                _zxNextRealtimeRunner = new ZxNextRealtimeFrameRunner(machine);
                _zxNextRealtimeRunner.Faulted += OnZxNextRunnerFaulted;
            }
        }
    }

    private void StopZxNextExecutionOwner()
    {
        _audioPlayer?.Dispose();
        _audioPlayer = null;
        _zxNextMachine?.Audio.EndCapture();

        if (_zxNextRealtimeRunner != null)
        {
            _zxNextRealtimeRunner.Faulted -= OnZxNextRunnerFaulted;
            _zxNextRealtimeRunner.Dispose();
            _zxNextRealtimeRunner = null;
        }

        _zxNextTurboRunner?.Dispose();
        _zxNextTurboRunner = null;
    }

    private void ResetZxNextHostMachine(ZxNextResetKind kind)
    {
        ZxNextMachine? machine = _zxNextMachine;
        if (machine == null)
        {
            return;
        }

        // Reset owns the machine exclusively: Z80Init and device reset must not race an
        // instruction already executing on the realtime or turbo thread.
        StopZxNextExecutionOwner();
        machine.Joystick.ReleaseAll();
        machine.Keyboard.ReleaseAll();
        machine.Reset(kind);
        machine.SetHostJoystickType(_joystickType);
        Interlocked.Exchange(ref _renderPending, 0);
        _lastNextFrameSeconds = _speedStopwatch.Elapsed.TotalSeconds;
        _lastNextFrameNumber = machine.UlaTiming.FrameNumber;
        StartZxNextExecutionOwner();
    }

    private void OnZxNextFrameCompleted()
    {
        ZxNextMachine? source = _zxNextMachine;
        if (source == null || Interlocked.Exchange(ref _renderPending, 1) == 1)
        {
            return;
        }

        _uiDispatcher.TryPost(() =>
        {
            try
            {
                if (ReferenceEquals(source, _zxNextMachine)
                    && source.TryCopyFrame(_presentBuffer))
                {
                    _display.Present(_presentBuffer);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _renderPending, 0);
            }
        }, UiDispatchPriority.Render);
    }

    private void OnZxNextRunnerFaulted(Exception exception)
    {
        _uiDispatcher.TryPost(() => MessageBox.Show(
            exception.Message,
            "ZX Spectrum Next Execution Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error));
    }

    private bool HandleZxNextKeyEvent(Key key, bool pressed)
    {
        ZxNextMachine? machine = _zxNextMachine;
        if (machine == null)
        {
            return false;
        }

        if (_joystickType != SpectrumJoystickType.None
            && _joystickKeyMap.TryGetValue(key, out SpectrumJoystickButton button))
        {
            machine.Joystick.SetButtonState(button, pressed);
            return true;
        }

        if (!_keyMap.TryGetValue(key, out SpectrumKey[]? keys))
        {
            return false;
        }

        for (int index = 0; index < keys.Length; index++)
        {
            machine.Keyboard.SetKeyState(keys[index], pressed);
        }

        return true;
    }

    private void UpdateZxNextWindowTitle()
    {
        ZxNextMachine? machine = _zxNextMachine;
        if (machine == null)
        {
            return;
        }

        double now = _speedStopwatch.Elapsed.TotalSeconds;
        double elapsed = now - _lastNextFrameSeconds;
        if (elapsed <= 0)
        {
            return;
        }

        ulong frameNumber = machine.UlaTiming.FrameNumber;
        ulong completedFrames = frameNumber >= _lastNextFrameNumber
            ? frameNumber - _lastNextFrameNumber
            : frameNumber;
        double fps = completedFrames / elapsed;
        _lastNextFrameNumber = frameNumber;
        _lastNextFrameSeconds = now;
        Title = $"{BaseTitle} - ZX Spectrum Next (experimental) - "
            + $"{fps:0.0} fps - {machine.CpuClockHz / 1_000_000.0:0.#} MHz";
    }
}
