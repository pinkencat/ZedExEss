using System.Collections.Generic;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ZedExEss.FileHandlers;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Video;
using ZedExEss.Zx8x.Core;
using ZedExEss.Zx8x.Input;

namespace ZedExEss.AvaloniaHost;

public sealed partial class MainWindow : Window
{
    private const int AudioBufferSamples = 512;
    private const int AudioBufferCount = 4;
    private static readonly IReadOnlyDictionary<Key, SpectrumKey[]> KeyMap = BuildKeyMap();
    private static readonly IReadOnlyDictionary<Key, SpectrumJoystickButton> JoystickKeyMap = BuildJoystickKeyMap();
    private static readonly ModelChoice[] ModelChoices =
    [
        new("Spectrum 16K", SpectrumModel.Spectrum16K),
        new("Spectrum 48K", SpectrumModel.Spectrum48K),
        new("Spectrum 128K", SpectrumModel.Spectrum128K),
        new("Spectrum +2", SpectrumModel.SpectrumPlus2),
        new("Spectrum +2A", SpectrumModel.SpectrumPlus2A),
        new("Spectrum +3", SpectrumModel.SpectrumPlus3),
        new("Pentagon 128", SpectrumModel.Pentagon128),
        new("Scorpion 256", SpectrumModel.Scorpion256),
        new("Sinclair ZX80", null, Zx8xModel.Zx80),
        new("Sinclair ZX81", null, Zx8xModel.Zx81)
    ];

    private readonly HashSet<Key> _pressedHostKeys = [];
    private readonly Dictionary<SpectrumKey, int> _spectrumKeyPressCounts = [];
    private readonly Dictionary<Zx8xKey, int> _zx8xKeyPressCounts = [];
    private readonly SpectrumSessionController _session = new();
    private TzxLoader? ActiveTape => _zx8xMachine?.Tape.Loader ?? _session.Tape;
    private string? ActiveTapePath => _zx8xMachine?.Tape.Path ?? _session.TapePath;
    private readonly IFileDialogService _fileDialogs;
    private readonly Image _screenImage;
    private readonly TextBlock _statusText;
    private readonly TextBlock _tapeStatusText;
    private readonly ComboBox _modelComboBox;
    private readonly Button _pauseButton;
    private readonly Button _playTapeButton;
    private readonly Button _stopTapeButton;
    private readonly Button _rewindTapeButton;
    private readonly Button _ejectTapeButton;
    private SpectrumMachine? _machine;
    private Zx8xMachine? _zx8xMachine;
    private Zx8xModel? _zx8xModel;
    private IAudioOutput? _audioOutput;
    private RealtimeFrameRunner? _runner;
    private AvaloniaFramePresenter? _presenter;
    private int[]? _frameBuffer;
    private int[]? _dirtyLines;
    private GigascreenFrameBlender? _gigascreenBlender;
    private int _renderPending;
    private bool _closing;
    private bool _replacingMachine;
    private bool _suppressModelSelection;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _screenImage = FindRequiredControl<Image>("ScreenImage");
        _statusText = FindRequiredControl<TextBlock>("StatusText");
        _tapeStatusText = FindRequiredControl<TextBlock>("TapeStatusText");
        _modelComboBox = FindRequiredControl<ComboBox>("ModelComboBox");
        _pauseButton = FindRequiredControl<Button>("QuickPauseButton");
        _playTapeButton = FindRequiredControl<Button>("PlayTapeButton");
        _stopTapeButton = FindRequiredControl<Button>("StopTapeButton");
        _rewindTapeButton = FindRequiredControl<Button>("RewindTapeButton");
        _ejectTapeButton = FindRequiredControl<Button>("EjectTapeButton");
        _fileDialogs = new AvaloniaFileDialogService(this);
        _settingsStore = CreateSettingsStore();
        ApplyHostSettings(_settingsStore.Load());
        InitializeCommandUi();
        InitializeSizingUi();
        InitializeMediaUi();
        InitializeTooling();

        _modelComboBox.ItemsSource = ModelChoices;
        _suppressModelSelection = true;
        _modelComboBox.SelectedItem = ModelChoices.First(choice => choice.Model == SpectrumModel.Spectrum128K);
        _suppressModelSelection = false;

        _playTapeButton.Click += OnPlayTapeClicked;
        _stopTapeButton.Click += OnStopTapeClicked;
        _rewindTapeButton.Click += OnRewindTapeClicked;
        _ejectTapeButton.Click += OnEjectTapeClicked;
        _modelComboBox.SelectionChanged += OnModelSelectionChanged;
        _session.TapePlaybackStopped += OnTapePlaybackStopped;

        Opened += OnOpened;
        Closed += OnClosed;
        Deactivated += OnDeactivated;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Focus();
        ReplaceMachine(SpectrumModel.Spectrum128K, preserveTape: false, rewindTape: false);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        SaveHostSettings();
        _session.TapePlaybackStopped -= OnTapePlaybackStopped;
        ShutdownTooling();
        ClearPressedKeys();
        StopRunnerAndDetachMachine();
        try
        {
            // StopRunnerAndDetachMachine has ended CPU access, so the persisted MDR
            // cannot contain a mixture of bytes from two stages of a record write.
            _session.Interface1.FlushAll();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Unable to save Microdrive media during shutdown: {ex}");
        }

        _session.Interface1.ConnectDevice(null);
        // Stop all CPU access before flushing/discarding an attached SD card.
        _session.DivMmc.Dispose();
        _presenter?.Dispose();
        _presenter = null;
    }

    private void ReplaceMachine(
        SpectrumModel model,
        bool preserveTape,
        bool rewindTape,
        Action<SpectrumMachine>? initialize = null,
        Action<SpectrumMachine>? beforeStart = null)
    {
        if (_closing || _replacingMachine)
        {
            return;
        }

        _replacingMachine = true;
        _statusText.Text = $"Starting {FormatModel(model)}…";
        try
        {
            // Construct first so a missing ROM cannot destroy the currently running session.
            SpectrumMachine replacement = AvaloniaMachineBootstrap.CreateMachine(
                model,
                _session.Disks,
                _session.DivMmc,
                _divExpansionMode,
                _interface1Enabled,
                _interface1RomRevision,
                out AvaloniaMachineDevices replacementDevices);
            ApplyMachinePreferences(replacement);
            initialize?.Invoke(replacement);
            ClearPressedKeys();
            StopRunnerAndDetachMachine();
            _zx8xMachine = null;
            _zx8xModel = null;
            _session.Interface1.ConnectDevice(replacementDevices.Interface1Device);
            _autoLoadInjector = null;
            _session.ReplaceMachine(replacement, preserveTape, rewindTape);
            _machine = replacement;
            _machineDevices = replacementDevices;
            ObserveInterface1Device(replacementDevices.Interface1Device);
            beforeStart?.Invoke(replacement);
            ResetDiskActivityTracking();
            AttachDebuggerToMachine(replacement);
            _oscilloscopeWindow?.AttachAudioRenderer(replacement.Audio);

            SpectrumUlaTiming timing = SpectrumUlaTiming.ForModel(replacement.Model);
            _presenter?.Dispose();
            _presenter = new AvaloniaFramePresenter(timing.FrameWidth, timing.FrameHeight);
            _frameBuffer = new int[timing.FrameWidth * timing.FrameHeight];
            _dirtyLines = new int[timing.FrameHeight];
            ConfigureGigascreenPresentation();
            _screenImage.Source = _presenter.Bitmap;
            ApplyScreenZoom();
            QueueResizeWindowToScreenZoom();

            replacement.Emulator.FrameCompleted += OnFrameCompleted;
            replacement.EarInput.AutoPlayRequested += OnTapeAutoPlayRequested;
            string executionMode = StartSelectedExecution(replacement);
            UpdatePauseCommandState(paused: false);
            _statusText.Text = $"{FormatModel(model)} — {executionMode} — keyboard active";
            UpdateTapeControls();
            UpdateDiskControls();
            UpdateInterface1MenuState();
            SelectModelWithoutReplacing(model);
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Unable to start {FormatModel(model)}: {ex.Message}";
            SelectModelWithoutReplacing(_machine?.Model ?? SpectrumModel.Spectrum128K);
        }
        finally
        {
            _replacingMachine = false;
            Focus();
        }
    }

    private void StopRunnerAndDetachMachine()
    {
        StopExecutionOwner();

        if (_machine != null)
        {
            _machine.Emulator.FrameCompleted -= OnFrameCompleted;
            _machine.EarInput.AutoPlayRequested -= OnTapeAutoPlayRequested;
        }

        if (_zx8xMachine != null)
        {
            _zx8xMachine.Tape.PlaybackStopped -= OnTapePlaybackStopped;
            _zx8xMachine.FrameCompleted -= OnFrameCompleted;
        }

        ObserveInterface1Device(null);
        _machineDevices = null;

        Interlocked.Exchange(ref _renderPending, 0);
    }

    /// <summary>Stops only the host clock, retaining the current machine and all device state.</summary>
    private void StopExecutionOwner()
    {
        IAudioOutput? audioOutput = _audioOutput;
        _audioOutput = null;
        if (audioOutput != null)
        {
            audioOutput.Faulted -= OnAudioOutputFaulted;
            audioOutput.Dispose();
        }

        RealtimeFrameRunner? runner = _runner;
        _runner = null;
        if (runner != null)
        {
            runner.Faulted -= OnRunnerFaulted;
            runner.Dispose();
        }

        StopAcceleratedExecutionOwners();

    }

    private string StartRealtimeExecution(SpectrumMachine machine)
    {
        try
        {
            var audioOutput = new SdlAudioOutput(
                machine.Emulator,
                machine.SampleRate,
                AudioBufferSamples,
                AudioBufferCount);
            audioOutput.Faulted += OnAudioOutputFaulted;
            _audioOutput = audioOutput;
            return "SDL audio realtime";
        }
        catch (Exception ex)
        {
            _runner = new RealtimeFrameRunner(machine);
            _runner.Faulted += OnRunnerFaulted;
            return $"silent realtime (audio unavailable: {ex.Message})";
        }
    }

    private void OnModelSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelSelection || _modelComboBox.SelectedItem is not ModelChoice choice)
        {
            return;
        }

        if (choice.Zx8xModel.HasValue)
        {
            if (_zx8xModel != choice.Zx8xModel || _zx8xMachine == null)
            {
                ReplaceZx8xMachine(choice.Zx8xModel.Value);
            }

            return;
        }

        if (choice.Model.HasValue && (_zx8xMachine != null || choice.Model != _machine?.Model))
        {
            ReplaceMachine(choice.Model.Value, preserveTape: true, rewindTape: false);
        }
    }

    private void OnResetClicked(object? sender, RoutedEventArgs e)
    {
        if (_zx8xModel.HasValue)
        {
            ReplaceZx8xMachine(_zx8xModel.Value);
            return;
        }

        if (_machine != null)
        {
            ReplaceMachine(_machine.Model, preserveTape: true, rewindTape: true);
        }
    }

    private void OnPauseClicked(object? sender, RoutedEventArgs e)
    {
        if (_zx8xMachine != null)
        {
            if (_debugger.IsPaused)
            {
                ResumeFromDebugger();
                Focus();
                return;
            }

            bool zxPaused = !_zx8xMachine.IsPaused;
            _zx8xMachine.SetPaused(zxPaused);
            RefreshExecutionOwner();
            UpdatePauseCommandState(zxPaused);
            _statusText.Text = zxPaused
                ? $"{FormatZx8xModel(_zx8xMachine.Model)} — paused"
                : $"{FormatZx8xModel(_zx8xMachine.Model)} — {GetExecutionModeText()} — keyboard active";
            Focus();
            return;
        }

        if (_machine == null)
        {
            return;
        }

        if (_debugger.IsPaused)
        {
            ResumeFromDebugger();
            Focus();
            return;
        }

        bool paused = !_machine.Emulator.IsPaused;
        _machine.Emulator.SetPaused(paused);
        RefreshExecutionOwner();
        UpdatePauseCommandState(paused);
        _statusText.Text = paused
            ? $"{FormatModel(_machine.Model)} — paused"
            : $"{FormatModel(_machine.Model)} — {GetExecutionModeText()} — keyboard active";
        Focus();
    }

    private async void OnOpenTapeClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
            {
                Title = "Attach Spectrum tape",
                Filters =
                [
                    new FileDialogFilter("Spectrum tape images", "*.tap", "*.tzx", "*.csw"),
                    new FileDialogFilter("All files", "*.*")
                ]
            });
            if (path == null)
            {
                return;
            }

            AttachTapePath(path);
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Unable to attach tape: {ex.Message}";
        }
        finally
        {
            Focus();
        }
    }

    private void AttachTapePath(string path)
    {
        if (_zx8xMachine != null)
        {
            if (!string.Equals(Path.GetExtension(path), ".tzx", StringComparison.OrdinalIgnoreCase))
            {
                _statusText.Text = "ZX80/ZX81 cassette playback currently accepts TZX images; use .o/.p/.81 for direct loading.";
                return;
            }

            _session.EjectTape();
            _zx8xMachine.Tape.LoadTzx(path, _zx8xMachine.Cpu.Cyc);
            RefreshTapeBlockList();
            UpdateTapeControls();
            _statusText.Text = $"Attached {Path.GetFileName(path)}";
            return;
        }

        _session.LoadTape(path);
        RefreshTapeBlockList();
        UpdateTapeControls();
        _statusText.Text = $"Attached {Path.GetFileName(path)}";
        TryStartAutoLoadAfterTapeAttach();
    }

    private void OnPlayTapeClicked(object? sender, RoutedEventArgs e)
    {
        if (_zx8xMachine != null)
        {
            _zx8xMachine.CassetteMonitorEnabled = false;
            _zx8xMachine.Tape.Play(_zx8xMachine.Cpu.Cyc);
        }
        else
        {
            _session.Tape?.Play();
        }
        RefreshExecutionOwner();
        UpdateTapeControls();
        Focus();
    }

    private void OnStopTapeClicked(object? sender, RoutedEventArgs e)
    {
        if (_zx8xMachine != null)
        {
            _zx8xMachine.Tape.Stop(_zx8xMachine.Cpu.Cyc);
        }
        else
        {
            _session.Tape?.Stop();
        }
        RefreshExecutionOwner();
        UpdateTapeControls();
        Focus();
    }

    private void OnRewindTapeClicked(object? sender, RoutedEventArgs e)
    {
        if (_zx8xMachine != null)
        {
            _zx8xMachine.Tape.Rewind(_zx8xMachine.Cpu.Cyc);
        }
        else
        {
            _session.Tape?.Reset();
        }
        RefreshExecutionOwner();
        UpdateTapeControls();
        Focus();
    }

    private void OnEjectTapeClicked(object? sender, RoutedEventArgs e)
    {
        if (_zx8xMachine != null)
        {
            _zx8xMachine.Tape.Eject(_zx8xMachine.Cpu.Cyc);
        }
        else
        {
            _session.EjectTape();
        }
        RefreshExecutionOwner();
        RefreshTapeBlockList();
        UpdateTapeControls();
        Focus();
    }

    private void OnTapePlaybackStopped(object? sender, TapeStopReason reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closing)
            {
                if (_turboEnabled)
                {
                    _turboEnabled = false;
                    UpdateRuntimeMenuState();
                }

                UpdateTapeControls();
                RefreshExecutionOwner();
                _statusText.Text = $"Tape stopped: {reason}";
            }
        });
    }

    private void UpdateTapeControls()
    {
        TzxLoader? tape = ActiveTape;
        bool zx8xSelected = _zx8xMachine != null;
        bool attached = tape != null;
        _playTapeButton.IsEnabled = attached && !tape!.IsPlaying;
        _stopTapeButton.IsEnabled = attached && tape!.IsPlaying;
        _rewindTapeButton.IsEnabled = attached;
        _ejectTapeButton.IsEnabled = attached;
        UpdateTapeMenuState(attached, tape?.IsPlaying == true);

        if (!attached)
        {
            _tapeStatusText.Text = zx8xSelected ? "ZX80/ZX81 tape: none" : "Tape: none";
            UpdateTapeBrowser();
            return;
        }

        int block = tape!.CurrentBlockIndex < 0 ? 0 : tape.CurrentBlockIndex + 1;
        string state = tape.IsPlaying ? "playing" : "stopped";
        _tapeStatusText.Text =
            $"Tape: {Path.GetFileName(ActiveTapePath)} — block {block}/{tape.Blocks.Count} — {state}";
        UpdateTapeBrowser();
    }

    private void OnFrameCompleted()
    {
        if (_closing || _replacingMachine || Interlocked.Exchange(ref _renderPending, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(PresentPendingFrame, DispatcherPriority.Render);
    }

    private void PresentPendingFrame()
    {
        try
        {
            if (_zx8xMachine != null)
            {
                AvaloniaFramePresenter? zxPresenter = _presenter;
                int[]? zxFrameBuffer = _frameBuffer;
                if (!_closing && !_replacingMachine && zxPresenter != null && zxFrameBuffer != null
                    && _zx8xMachine.TryCopyFrame(zxFrameBuffer))
                {
                    zxPresenter.Present(zxFrameBuffer);
                    _screenImage.InvalidateVisual();
                }

                UpdateTapeControls();
                return;
            }

            SpectrumMachine? machine = _machine;
            AvaloniaFramePresenter? presenter = _presenter;
            int[]? frameBuffer = _frameBuffer;
            int[]? dirtyLines = _dirtyLines;
            if (_closing || _replacingMachine || machine == null || presenter == null || frameBuffer == null)
            {
                return;
            }

            if (_gigascreenBlender != null)
            {
                if (machine.Emulator.TryCopyFrame(frameBuffer))
                {
                    presenter.Present(_gigascreenBlender.Compose(frameBuffer));
                    _screenImage.InvalidateVisual();
                }
            }
            else if (dirtyLines != null
                && machine.Emulator.TryCopyFrame(frameBuffer, dirtyLines, out int dirtyCount))
            {
                presenter.PresentDirty(frameBuffer, dirtyLines, dirtyCount);
                _screenImage.InvalidateVisual();
            }

            UpdateTapeControls();
            UpdateDiskControls();
        }
        finally
        {
            Interlocked.Exchange(ref _renderPending, 0);
        }
    }

    private void OnRunnerFaulted(Exception exception)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_closing)
            {
                _statusText.Text = $"Emulation stopped: {exception.Message}";
            }
        });
    }

    private void OnAudioOutputFaulted(Exception exception)
    {
        Dispatcher.UIThread.Post(() => StartSilentFallbackAfterAudioFailure(exception));
    }

    private void StartSilentFallbackAfterAudioFailure(Exception exception)
    {
        if (_closing || _replacingMachine || (_machine == null && _zx8xMachine == null) || _audioOutput?.Failure != exception)
        {
            return;
        }

        IAudioOutput failedOutput = _audioOutput;
        _audioOutput = null;
        failedOutput.Faulted -= OnAudioOutputFaulted;
        failedOutput.Dispose();

        if (_zx8xMachine != null)
        {
            _zx8xRealtimeRunner = new Zx8xRealtimeFrameRunner(_zx8xMachine);
            _zx8xRealtimeRunner.Faulted += OnRunnerFaulted;
            _statusText.Text =
                $"{FormatZx8xModel(_zx8xMachine.Model)} — silent realtime; audio stopped: {exception.Message}";
        }
        else if (_machine != null)
        {
            _runner = new RealtimeFrameRunner(_machine);
            _runner.Faulted += OnRunnerFaulted;
            _statusText.Text =
                $"{FormatModel(_machine.Model)} — silent realtime; audio stopped: {exception.Message}";
        }
    }

    private string GetExecutionModeText()
    {
        return GetSelectedExecutionModeText();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (TryHandleCommandKey(e))
        {
            return;
        }

        if (_zx8xMachine != null)
        {
            if (Zx8xKeyMap.TryGetValue(e.Key, out Zx8xKey[]? zxKeys) && _pressedHostKeys.Add(e.Key))
            {
                foreach (Zx8xKey key in zxKeys)
                {
                    _zx8xKeyPressCounts.TryGetValue(key, out int count);
                    _zx8xKeyPressCounts[key] = count + 1;
                    if (count == 0)
                    {
                        _zx8xMachine.Keyboard.SetKeyState(key, pressed: true);
                    }
                }

                e.Handled = true;
            }

            return;
        }

        if (_machine != null && _joystickType != SpectrumJoystickType.None
            && JoystickKeyMap.TryGetValue(e.Key, out SpectrumJoystickButton joystickButton))
        {
            if (_pressedHostKeys.Add(e.Key))
            {
                _machine.Joystick.SetButtonState(joystickButton, pressed: true);
            }

            e.Handled = true;
            return;
        }

        if (_machine == null || !KeyMap.TryGetValue(e.Key, out SpectrumKey[]? mappedKeys)
            || !_pressedHostKeys.Add(e.Key))
        {
            return;
        }

        foreach (SpectrumKey key in mappedKeys)
        {
            _spectrumKeyPressCounts.TryGetValue(key, out int count);
            _spectrumKeyPressCounts[key] = count + 1;
            if (count == 0)
            {
                _machine.Keyboard.SetKeyState(key, pressed: true);
            }
        }

        e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_zx8xMachine != null)
        {
            if (_pressedHostKeys.Remove(e.Key) && Zx8xKeyMap.TryGetValue(e.Key, out Zx8xKey[]? zxKeys))
            {
                foreach (Zx8xKey key in zxKeys)
                {
                    if (!_zx8xKeyPressCounts.TryGetValue(key, out int count))
                    {
                        continue;
                    }

                    if (count <= 1)
                    {
                        _zx8xKeyPressCounts.Remove(key);
                        _zx8xMachine.Keyboard.SetKeyState(key, pressed: false);
                    }
                    else
                    {
                        _zx8xKeyPressCounts[key] = count - 1;
                    }
                }

                e.Handled = true;
            }

            return;
        }

        if (_machine != null && _joystickType != SpectrumJoystickType.None
            && JoystickKeyMap.TryGetValue(e.Key, out SpectrumJoystickButton joystickButton)
            && _pressedHostKeys.Remove(e.Key))
        {
            _machine.Joystick.SetButtonState(joystickButton, pressed: false);
            e.Handled = true;
            return;
        }

        if (_machine == null || !_pressedHostKeys.Remove(e.Key)
            || !KeyMap.TryGetValue(e.Key, out SpectrumKey[]? mappedKeys))
        {
            return;
        }

        foreach (SpectrumKey key in mappedKeys)
        {
            if (!_spectrumKeyPressCounts.TryGetValue(key, out int count))
            {
                continue;
            }

            if (count <= 1)
            {
                _spectrumKeyPressCounts.Remove(key);
                _machine.Keyboard.SetKeyState(key, pressed: false);
            }
            else
            {
                _spectrumKeyPressCounts[key] = count - 1;
            }
        }

        e.Handled = true;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        ClearPressedKeys();
    }

    private void ClearPressedKeys()
    {
        if (_zx8xMachine != null)
        {
            _zx8xMachine.Keyboard.ReleaseAll();
        }

        SpectrumMachine? machine = _machine;
        if (machine != null)
        {
            foreach (Key hostKey in _pressedHostKeys)
            {
                if (JoystickKeyMap.TryGetValue(hostKey, out SpectrumJoystickButton button))
                {
                    machine.Joystick.SetButtonState(button, pressed: false);
                }
            }

            foreach (SpectrumKey key in _spectrumKeyPressCounts.Keys)
            {
                machine.Keyboard.SetKeyState(key, pressed: false);
            }
        }

        _spectrumKeyPressCounts.Clear();
        _zx8xKeyPressCounts.Clear();
        _pressedHostKeys.Clear();
    }

    private void SelectModelWithoutReplacing(SpectrumModel model)
    {
        _suppressModelSelection = true;
        _modelComboBox.SelectedItem = ModelChoices.First(choice => choice.Model == model);
        _suppressModelSelection = false;
        UpdateModelMenuState(model);
    }

    private T FindRequiredControl<T>(string name) where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"{name} was not created by XAML.");
    }

    private static string FormatModel(SpectrumModel model)
    {
        return ModelChoices.First(choice => choice.Model == model).Name;
    }

    private static IReadOnlyDictionary<Key, SpectrumKey[]> BuildKeyMap()
    {
        return new Dictionary<Key, SpectrumKey[]>
        {
            [Key.LeftShift] = [SpectrumKey.CapsShift],
            [Key.RightShift] = [SpectrumKey.CapsShift],
            [Key.LeftCtrl] = [SpectrumKey.SymbolShift],
            [Key.RightCtrl] = [SpectrumKey.SymbolShift],
            [Key.Space] = [SpectrumKey.Space],
            [Key.Enter] = [SpectrumKey.Enter],
            [Key.Back] = [SpectrumKey.CapsShift, SpectrumKey.D0],
            [Key.Delete] = [SpectrumKey.CapsShift, SpectrumKey.D0],
            [Key.Escape] = [SpectrumKey.CapsShift, SpectrumKey.Space],
            [Key.A] = [SpectrumKey.A], [Key.B] = [SpectrumKey.B], [Key.C] = [SpectrumKey.C],
            [Key.D] = [SpectrumKey.D], [Key.E] = [SpectrumKey.E], [Key.F] = [SpectrumKey.F],
            [Key.G] = [SpectrumKey.G], [Key.H] = [SpectrumKey.H], [Key.I] = [SpectrumKey.I],
            [Key.J] = [SpectrumKey.J], [Key.K] = [SpectrumKey.K], [Key.L] = [SpectrumKey.L],
            [Key.M] = [SpectrumKey.M], [Key.N] = [SpectrumKey.N], [Key.O] = [SpectrumKey.O],
            [Key.P] = [SpectrumKey.P], [Key.Q] = [SpectrumKey.Q], [Key.R] = [SpectrumKey.R],
            [Key.S] = [SpectrumKey.S], [Key.T] = [SpectrumKey.T], [Key.U] = [SpectrumKey.U],
            [Key.V] = [SpectrumKey.V], [Key.W] = [SpectrumKey.W], [Key.X] = [SpectrumKey.X],
            [Key.Y] = [SpectrumKey.Y], [Key.Z] = [SpectrumKey.Z],
            [Key.D0] = [SpectrumKey.D0], [Key.D1] = [SpectrumKey.D1], [Key.D2] = [SpectrumKey.D2],
            [Key.D3] = [SpectrumKey.D3], [Key.D4] = [SpectrumKey.D4], [Key.D5] = [SpectrumKey.D5],
            [Key.D6] = [SpectrumKey.D6], [Key.D7] = [SpectrumKey.D7], [Key.D8] = [SpectrumKey.D8],
            [Key.D9] = [SpectrumKey.D9],
            [Key.NumPad0] = [SpectrumKey.D0], [Key.NumPad1] = [SpectrumKey.D1], [Key.NumPad2] = [SpectrumKey.D2],
            [Key.NumPad3] = [SpectrumKey.D3], [Key.NumPad4] = [SpectrumKey.D4], [Key.NumPad5] = [SpectrumKey.D5],
            [Key.NumPad6] = [SpectrumKey.D6], [Key.NumPad7] = [SpectrumKey.D7], [Key.NumPad8] = [SpectrumKey.D8],
            [Key.NumPad9] = [SpectrumKey.D9]
        };
    }

    private static IReadOnlyDictionary<Key, SpectrumJoystickButton> BuildJoystickKeyMap()
    {
        return new Dictionary<Key, SpectrumJoystickButton>
        {
            [Key.Left] = SpectrumJoystickButton.Left,
            [Key.Right] = SpectrumJoystickButton.Right,
            [Key.Up] = SpectrumJoystickButton.Up,
            [Key.Down] = SpectrumJoystickButton.Down,
            [Key.LeftAlt] = SpectrumJoystickButton.Fire,
            [Key.RightAlt] = SpectrumJoystickButton.Fire
        };
    }

    private sealed record ModelChoice(string Name, SpectrumModel? Model, Zx8xModel? Zx8xModel = null)
    {
        public override string ToString() => Name;
    }
}
