using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Video;
using ZedExEss.Zx8x.Core;
using ZedExEss.Zx8x.Input;
using ZedExEss.Zx8x.Media;
using ZedExEss.Zx8x.Memory;
using ZedExEss.Zx8x.Video;

namespace ZedExEss;

/// <summary>WPF host integration for the portable ZX80/ZX81 machine graph.</summary>
public partial class MainWindow
{
    private Zx8xModel? _zx8xModel;
    private Zx8xMachine? _zx8xMachine;
    private Zx8xTurboRunner? _zx8xTurboRunner;
    private Zx8xTurboRunner? _zx8xFastTapeRunner;

    private static readonly IReadOnlyDictionary<Key, Zx8xKey[]> Zx8xKeyMap =
        new Dictionary<Key, Zx8xKey[]>
        {
            [Key.LeftShift] = [Zx8xKey.Shift],
            [Key.RightShift] = [Zx8xKey.Shift],
            [Key.Space] = [Zx8xKey.Space],
            [Key.Enter] = [Zx8xKey.NewLine],
            [Key.OemPeriod] = [Zx8xKey.Period],
            [Key.Decimal] = [Zx8xKey.Period],
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

    private void OnZx8xModelMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !Enum.TryParse(tag, out Zx8xModel model))
        {
            return;
        }

        if (_zx8xModel == model && _zx8xMachine != null)
        {
            return;
        }

        InitializeZx8xMachine(model);
        Focus();
    }

    private void InitializeZx8xMachine(Zx8xModel model)
    {
        string? preservedTapePath = _zx8xMachine?.Tape.Path;
        int preservedBlock = _zx8xMachine?.Tape.Loader?.CurrentBlockIndex ?? -1;
        int preservedPulse = _zx8xMachine?.Tape.Loader?.CurrentPulseOffset ?? 0;
        bool preservedPlaying = _zx8xMachine?.Tape.Loader?.IsPlaying == true;
        Zx8xMachine replacement;
        try
        {
            replacement = Zx8xMachineFactory.Create(
                model,
                Path.Combine(AppContext.BaseDirectory, "ROMs"),
                ramConfiguration: _zx8xRamConfiguration,
                highResolutionMode: _zx8xHighResolutionMode,
                sampleRate: SpectrumAudioTiming.DefaultSampleRate);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ZX80/ZX81 ROM Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateModelMenuChecks();
            return;
        }

        _autoLoadInjector = null;
        if (_emulator != null)
        {
            _emulator.FrameCompleted -= OnFrameCompleted;
        }

        _oscilloscopeWindow?.AttachAudioRenderer(null);
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
        _session.EjectTape();

        _zx8xModel = model;
        _zx8xMachine = replacement;
        _debugger.Attach(
            replacement.Cpu,
            replacement.Memory,
            replacement.TstatesPerFrame,
            replacement.VideoTiming.Timing.TstatesPerLine);
        UpdateCpuStepHooks();
        replacement.Tape.PlaybackStopped += OnTapePlaybackStopped;
        if (preservedTapePath != null)
        {
            replacement.Tape.LoadTzx(preservedTapePath, replacement.Cpu.Cyc);
            if (preservedBlock >= 0)
            {
                replacement.Tape.Loader?.JumpToBlockPulse(preservedBlock, preservedPulse, preservedPlaying);
            }
        }
        _cpuHz = replacement.CpuClockHz;
        _tstatesPerFrame = replacement.TstatesPerFrame;
        _display = new WpfSpectrumDisplay(replacement.FrameWidth, replacement.FrameHeight);
        _presentBuffer = new int[replacement.FrameWidth * replacement.FrameHeight];
        _dirtyLines = new int[replacement.FrameHeight];
        _gigascreenPreviousBuffer = null;
        _gigascreenBlendBuffer = null;
        _gigascreenHasPreviousFrame = false;
        ScreenImage.Source = _display.Bitmap;
        ApplyScreenZoom();
        UpdateZoomMenuChecks();

        replacement.FrameCompleted += OnZx8xFrameCompleted;
        if (_turboEnabled)
        {
            _zx8xTurboRunner = new Zx8xTurboRunner(replacement, presentEveryNFrames: 5);
        }
        else if (ShouldRunZx8xTapeFastMode())
        {
            _zx8xFastTapeRunner = new Zx8xTurboRunner(replacement, presentEveryNFrames: 5);
        }
        else
        {
            _audioPlayer = new WaveOutAudioPlayer(
                replacement,
                replacement.SampleRate,
                AudioBufferSamples,
                AudioBufferCount);
        }

        if (!_speedStopwatch.IsRunning)
        {
            _speedStopwatch.Start();
        }

        _lastSpeedSeconds = _speedStopwatch.Elapsed.TotalSeconds;
        _lastSpeedTstates = replacement.Cpu.Cyc;
        Interlocked.Exchange(ref _renderPending, 0);
        UpdateModelMenuChecks();
        RefreshTapeAttachmentUi();
        TurboMenu.IsChecked = _turboEnabled;
        UpdateQuickAccessState();
        UpdateWindowTitle();
        _uiDispatcher.TryPost(ResizeWindowToScreenZoom, UiDispatchPriority.Loaded);
    }

    private void StopZx8xHostMachine()
    {
        _zx8xTurboRunner?.Dispose();
        _zx8xTurboRunner = null;
        _zx8xFastTapeRunner?.Dispose();
        _zx8xFastTapeRunner = null;
        if (_zx8xMachine != null)
        {
            _zx8xMachine.Tape.PlaybackStopped -= OnTapePlaybackStopped;
            _zx8xMachine.FrameCompleted -= OnZx8xFrameCompleted;
            _zx8xMachine.Keyboard.ReleaseAll();
        }

        _zx8xMachine = null;
        _zx8xModel = null;
    }

    private void OnZx8xRamMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }
            || !Enum.TryParse(tag, out Zx8xRamConfiguration configuration)
            || configuration == _zx8xRamConfiguration)
        {
            UpdateModelMenuChecks();
            return;
        }

        _zx8xRamConfiguration = configuration;
        if (_zx8xModel.HasValue)
        {
            InitializeZx8xMachine(_zx8xModel.Value);
        }
        else
        {
            UpdateModelMenuChecks();
        }

        Focus();
    }

    private void OnZx8xWrxMenuClick(object sender, RoutedEventArgs e)
    {
        _zx8xHighResolutionMode = Zx8xWrxMenu.IsChecked
            ? Zx8xHighResolutionMode.Wrx
            : Zx8xHighResolutionMode.Sinclair;
        if (_zx8xModel.HasValue)
        {
            InitializeZx8xMachine(_zx8xModel.Value);
        }
        else
        {
            UpdateModelMenuChecks();
        }

        Focus();
    }

    /// <summary>
    /// Loads a ROM SAVE image into its native ZX80 or ZX81 environment, replacing
    /// the current machine family when necessary and suspending the host clock so
    /// the CPU cannot observe a partially restored image.
    /// </summary>
    private void LoadZx8xProgramImagePath(string path)
    {
        Zx8xModel requiredModel = Zx8xProgramImageLoader.GetRequiredModel(Path.GetExtension(path));
        if (_zx8xMachine == null || _zx8xModel != requiredModel)
        {
            InitializeZx8xMachine(requiredModel);
        }

        Zx8xMachine machine = _zx8xMachine != null && _zx8xModel == requiredModel
            ? _zx8xMachine
            : throw new InvalidOperationException(
                $"Unable to start {Zx8xModelDescriptors.ForModel(requiredModel).DisplayName} for this program image.");

        _zx8xTurboRunner?.Dispose();
        _zx8xTurboRunner = null;
        _zx8xFastTapeRunner?.Dispose();
        _zx8xFastTapeRunner = null;
        _audioPlayer?.Dispose();
        _audioPlayer = null;
        try
        {
            _ = Zx8xProgramImageLoader.LoadFile(machine, path);
            _lastSpeedSeconds = _speedStopwatch.Elapsed.TotalSeconds;
            _lastSpeedTstates = machine.Cpu.Cyc;
            Interlocked.Exchange(ref _renderPending, 0);
        }
        finally
        {
            if (ReferenceEquals(machine, _zx8xMachine))
            {
                SetTurboMode(_turboEnabled);
            }
        }

        UpdateQuickAccessState();
        UpdateWindowTitle();
    }

    private void OnZx8xFrameCompleted()
    {
        Zx8xMachine? source = _zx8xMachine;
        if (source == null || Interlocked.Exchange(ref _renderPending, 1) == 1)
        {
            return;
        }

        _uiDispatcher.TryPost(() =>
        {
            try
            {
                if (ReferenceEquals(source, _zx8xMachine) && source.TryCopyFrame(_presentBuffer))
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

    private bool HandleZx8xKeyEvent(Key key, bool pressed)
    {
        if (_zx8xMachine == null || !Zx8xKeyMap.TryGetValue(key, out Zx8xKey[]? keys))
        {
            return false;
        }

        for (int i = 0; i < keys.Length; i++)
        {
            _zx8xMachine.Keyboard.SetKeyState(keys[i], pressed);
        }

        return true;
    }

    private ulong GetCurrentCpuCycles() => _zx8xMachine?.Cpu.Cyc ?? _cpu?.Cyc ?? 0;
}
