using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows;
using System;
using ZedExEss.FileHandlers;
using ZedExEss.Hosting;
using ZedExEss.Hosting.Wpf;
using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Basic;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Debugging;
using ZedExEss.Spectrum.Disk.Beta;
using ZedExEss.Spectrum.Disk.Plus3;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.Spectrum.Multiface;
using ZedExEss.Spectrum.Replay;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Interface1;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Spectrum.Tape;
using ZedExEss.Spectrum.Video;
using ZedExEss.Z80CPU;
using ZedExEss.Zx8x.Memory;
using ZedExEss.Zx8x.Video;
using ZedExEss.ZxNext.Core;

namespace ZedExEss
{
    /// <summary>
    /// WPF composition root for the emulated machine, media devices and execution runners.
    /// </summary>
    /// <remarks>
    /// Machine objects are rebuilt as one graph when the model changes. Exactly one execution
    /// driver normally owns that graph: realtime audio, full turbo, or accelerated-tape turbo.
    /// Frame and debugger callbacks originate off the UI thread and must be marshalled through
    /// the dispatcher before touching controls.
    /// </remarks>
    public partial class MainWindow : Window
    {
        private SpectrumModel _model;
        private SpectrumMachine _machine = null!;
        private WpfSpectrumDisplay _display = null!;
        // Vertical stretch applied at presentation time. The Next surface stores one
        // buffer row per logical raster line and relies on this to restore its aspect.
        private int _displayHeightScale = 1;
        private SpectrumEmulator _emulator = null!;
        private SpectrumMemory _memory = null!;
        private Z80 _cpu = null!;
        private WaveOutAudioPlayer? _audioPlayer;
        private SpectrumAudioRenderer? _audioRenderer;
        private TurboRunner? _turboRunner;
        private TapeFastRunner? _fastTapeRunner;
        private int[] _presentBuffer = null!;
        private int[]? _gigascreenPreviousBuffer;
        private int[]? _gigascreenBlendBuffer;
        private int[] _dirtyLines = null!;
        private SpectrumKeyboard _keyboard = null!;
        private SpectrumJoystickDevice _joystick = null!;
        private SpectrumJoystickType _joystickType = SpectrumJoystickType.None;
        private SpectrumDivExpansionMode _divExpansionMode = SpectrumDivExpansionMode.Disabled;
        private bool _covoxEnabled;
        private bool _generalSoundEnabled;
        private SpectrumMultifaceModel _multifaceModel = SpectrumMultifaceModel.None;
        private bool _interface1Enabled;
        private SpectrumInterface1RomRevision _interface1RomRevision = SpectrumInterface1RomRevision.Revision2;
        private SpectrumInterface1Device? _interface1Device;
        private SpectrumDivMmcDevice? _divDevice;
        private SpectrumBeta128Device? _beta128Device;
        private SpectrumBeta128DiskController? _betaDiskController;
        private SpectrumDivMmcSdCard? _divStorageCard;
        private string? _divStoragePath;
        private bool _divStorageFolderBacked;
        private bool _divStorageWriteProtected;
        private SpectrumPlus3DiskController? _plus3DiskController;
        private Plus3DiskImage? _diskImage => _session.Disks.GetPlus3Image(0);
        private string? _diskPath => _session.Disks.GetPlus3Path(0);
        private Plus3DiskImage? _diskImageB => _session.Disks.GetPlus3Image(1);
        private string? _diskPathB => _session.Disks.GetPlus3Path(1);
        private TrdDiskImage? _trdDiskImage => _session.Disks.GetTrdImage(0);
        private string? _trdDiskPath => _session.Disks.GetTrdPath(0);
        private TrdDiskImage? _trdDiskImageB => _session.Disks.GetTrdImage(1);
        private string? _trdDiskPathB => _session.Disks.GetTrdPath(1);
        private bool _fdcTraceEnabled;
        private SpectrumEarInputDevice _earInput = null!;
        private bool _turboEnabled;
        private bool _fastForwardEnabled;
        private int _fastForwardSpeed = 4;
        private bool _flashLoadEnabled;
        private bool _flashSaveEnabled;
        private SpectrumContentionTiming _contentionTiming = SpectrumContentionTiming.Early;

        // Owned by the window, not the machine, so a model change or a newly fitted
        // expansion does not throw away a recording in progress.
        private readonly SpectrumTapeRecorder _tapeRecorder = new();

        // Also window-owned: rewind history and a recording in progress have to survive a
        // machine rebuild for the same reason a tape recording does.
        private readonly SpectrumReplaySession _replaySession = new();
        private bool _replayEventsHooked;
        private bool _edgeLoadEnabled;
        private bool _semanticEdgeLoadEnabled;
        private bool _runTapeAccelerationAtMaximumSpeed;
        private bool _autoLoadTapeOnAttach;
        private bool _autoTapePlayStopEnabled;
        private bool _gigascreenBlendEnabled;
        private bool _gigascreenHasPreviousFrame;
        private int _renderPending;
        private const string BaseTitle = "ZedExEss";
        private const int RomBankSize = 16 * 1024;
        private const double DiskActivityHoldSeconds = 0.16;
        private static bool UseDirtyLinePresentation = true;
        private const int AudioBufferSamples = 1024;
        private const int AudioBufferCount = 6;
        private readonly DispatcherTimer _titleTimer;
        private readonly Stopwatch _speedStopwatch = new();
        private readonly SpectrumDebuggerController _debugger = new();
        private readonly Z80Disassembler _debuggerDisassembler = new();
        private readonly Z80Disassembler _z80nDisassembler = new(enableZ80N: true);
        private readonly Z80InlineAssembler _debuggerAssembler = new();
        private readonly IFileDialogService _fileDialogs;
        private readonly IClipboardService _clipboard;
        private readonly IUiDispatcher _uiDispatcher;
        private readonly ISettingsStore _settingsStore;
        private ulong _lastSpeedTstates;
        private double _lastSpeedSeconds;
        private double _cpuHz;
        private int _tstatesPerFrame;
        private long _lastDiskActivityCounter;
        private long _lastDiskActivityTimestamp;

        private readonly SpectrumSessionController _session = new();
        private TzxLoader? _tapeLoader => _zx8xMachine?.Tape.Loader ?? _session.Tape;
        private AutoLoadKeyboardInjector? _autoLoadInjector;
        private DebuggerWindow? _debuggerWindow;
        private AudioOscilloscopeWindow? _oscilloscopeWindow;
        private EmulationRunState? _debuggerSuspendedRunState;
        private EmulationRunState? _quickPauseRunState;
        private string? _tapePath => _zx8xMachine?.Tape.Path ?? _session.TapePath;
        private Zx8xRamConfiguration _zx8xRamConfiguration = Zx8xRamConfiguration.Expansion16K;
        private Zx8xHighResolutionMode _zx8xHighResolutionMode = Zx8xHighResolutionMode.Sinclair;
        private readonly ObservableCollection<BlockInfo> _tapeBlocks = [];
        private bool _tapeBrowserVisible = true;
        private const double DefaultScreenZoom = 2.0;
        private const double MinScreenZoom = 0.5;
        private const double MaxScreenZoom = 4.0;
        private const double ScreenZoomStep = 0.5;
        private double _screenZoom = DefaultScreenZoom;
        private bool _resizingWindowToScreenZoom;
        private int _windowFitZoomQueued;
        private int _lastTapeBlock = -1;
        private const ushort AutoLoad48KReadyPc = 0x10B0;
        private const ushort AutoLoad128ReadyPc = 0x3683;
        private const ushort AutoLoadPlus2ReadyPc = 0x36A9;
        private const ushort AutoLoadPlus3ReadyPc = 0x1875;
        private const int AutoLoadDefaultInitialDelayFrames = 4;
        private const int AutoLoadDefaultKeySpacingFrames = 5;
        private const int AutoLoadPentagonInitialDelayFrames = 40;
        private const int AutoLoadPlus3InitialDelayFrames = 40;
        private static readonly byte[] AutoLoadBasic48Command = [0xEF, 0x22, 0x22, 0x0D];
        private static readonly byte[] AutoLoadCode48Command = [0xEF, 0x22, 0x22, 0xAF, 0x0D];
        private static readonly byte[] AutoLoadEnterCommand = [0x0D];
        private static readonly byte[] AutoLoadCode128Command = [0x0A, 0x0D, 0x6C, 0x6F, 0x61, 0x64, 0x20, 0x22, 0x22, 0x20, 0x63, 0x6F, 0x64, 0x65, 0x0D];
        private static readonly byte[] LdBytesPrefix = [0x08, 0x15, 0xF3, 0x3E, 0x0F, 0xD3, 0xFE, 0x21];
        private static readonly byte[] LdBytesSuffix = [0xE5, 0xDB, 0xFE, 0x1F, 0xE6, 0x20, 0xF6, 0x02];

        private readonly Dictionary<Key, SpectrumKey[]> _keyMap;
        private readonly Dictionary<Key, SpectrumJoystickButton> _joystickKeyMap;

        public MainWindow()
        {
            InitializeComponent();

            _fileDialogs = new WpfFileDialogService();
            _clipboard = new WpfClipboardService();
            _uiDispatcher = new WpfUiDispatcher(Dispatcher);
            _settingsStore = new JsonFileSettingsStore(GetSettingsPath());
            ApplyHostSettings(_settingsStore.Load());
            InitializeInterface1Ui();
            BuildStateSlotMenus();
            BuildZxNextStateMenus();

            TapeBlocksList.ItemsSource = _tapeBlocks;
            _keyMap = BuildKeyMap();
            _joystickKeyMap = BuildJoystickKeyMap();

            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += OnPreviewKeyUp;
            Deactivated += OnWindowDeactivated;
            Loaded += (_, _) => Focus();
            _debugger.BreakHit += OnDebuggerBreakHit;
            _debugger.HooksChanged += OnDebuggerHooksChanged;
            _session.TapePlaybackStopped += OnTapePlaybackStopped;

            _titleTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(0.5)
            };
            _titleTimer.Tick += (_, _) =>
            {
                UpdateWindowTitle();
                UpdateTapeUi();
                UpdateQuickAccessState();
                UpdateReplayStatusText();
            };
            _titleTimer.Start();

            var defaultModel = SpectrumModel.Spectrum128K;
            if (!TryLoadRoms(defaultModel, out RomSet roms, out string error))
            {
                MessageBox.Show(error, "ROM Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                roms = RomSet.CreateBlank(GetRomBankCount(defaultModel));
            }

            InitializeMachine(defaultModel, roms, null, preserveTape: false);
            SetTapeBrowserVisible(_tapeBrowserVisible, resizeWindow: false);
            GigascreenBlendMenu.IsChecked = _gigascreenBlendEnabled;
            UpdateQuickAccessState();
            _uiDispatcher.TryPost(ResizeWindowToScreenZoom, UiDispatchPriority.Loaded);
        }
        protected override void OnClosed(EventArgs e)
        {
            SaveHostSettings();
            _titleTimer.Stop();
            _debuggerWindow?.OwnerClosing();
            _oscilloscopeWindow?.OwnerClosing();
            _audioPlayer?.Dispose();
            _turboRunner?.Dispose();
            _fastTapeRunner?.Dispose();
            StopZxNextHostMachine();
            StopZx8xHostMachine();
            try
            {
                // The runners must be stopped before copying mutable cartridge bytes.
                _session.Interface1.FlushAll();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.Message,
                    "Microdrive Save Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            _session.Interface1.ConnectDevice(null);
            ObserveInterface1Device(null);
            _interface1NetworkBridge.StatusChanged -= OnInterface1NetworkStatusChanged;
            _interface1NetworkBridge.Dispose();
            _interface1Rs232Connection.StatusChanged -= OnInterface1Rs232ConnectionStatusChanged;
            _interface1Rs232Connection.Dispose();
            _interface1Rs232Endpoint.Faulted -= OnInterface1Rs232Faulted;
            _interface1Rs232Endpoint.Dispose();
            CloseDivStorage(showErrors: false);
            base.OnClosed(e);
        }
        /// <summary>Applies only durable host preferences; transient machine state is excluded.</summary>
        private void ApplyHostSettings(EmulatorHostSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            _screenZoom = double.IsFinite(settings.ScreenZoom)
                ? Math.Clamp(settings.ScreenZoom, MinScreenZoom, MaxScreenZoom)
                : DefaultScreenZoom;
            _tapeBrowserVisible = settings.TapeBrowserVisible;
            _joystickType = Enum.IsDefined(typeof(SpectrumJoystickType), settings.JoystickType)
                ? settings.JoystickType
                : SpectrumJoystickType.None;
            _flashLoadEnabled = settings.FlashLoadEnabled;
            _flashSaveEnabled = settings.FlashSaveEnabled;
            _contentionTiming = settings.LateContentionTimings
                ? SpectrumContentionTiming.Late
                : SpectrumContentionTiming.Early;
            _edgeLoadEnabled = settings.PollingLoaderAccelerationEnabled;
            _semanticEdgeLoadEnabled = settings.SemanticLoaderAccelerationEnabled;
            _runTapeAccelerationAtMaximumSpeed = settings.RunTapeAccelerationAtMaximumSpeed;
            _autoLoadTapeOnAttach = settings.AutoLoadTapeOnAttach;
            _autoTapePlayStopEnabled = settings.AutoTapePlayStopEnabled;
            UseDirtyLinePresentation = settings.DirtyLinePresentationEnabled;
            _gigascreenBlendEnabled = settings.GigascreenBlendEnabled;
            _fastForwardSpeed = Math.Clamp(
                settings.FastForwardSpeed,
                TimeStretchAudioSource.MinimumSpeedMultiplier,
                TimeStretchAudioSource.MaximumSpeedMultiplier);
            _interface1Enabled = settings.Interface1Enabled;
            _interface1RomRevision = Enum.IsDefined(typeof(SpectrumInterface1RomRevision), settings.Interface1RomRevision)
                ? settings.Interface1RomRevision
                : SpectrumInterface1RomRevision.Revision2;
            _zx8xRamConfiguration = Enum.IsDefined(typeof(Zx8xRamConfiguration), settings.Zx8xRamConfiguration)
                ? settings.Zx8xRamConfiguration
                : Zx8xRamConfiguration.Expansion16K;
            _zx8xHighResolutionMode = Enum.IsDefined(typeof(Zx8xHighResolutionMode), settings.Zx8xHighResolutionMode)
                ? settings.Zx8xHighResolutionMode
                : Zx8xHighResolutionMode.Sinclair;
            _zxNextSystemDirectory = settings.ZxNextSystemDirectory;
            _zxNextBootRomPath = settings.ZxNextBootRomPath;
        }
        private void SaveHostSettings()
        {
            var settings = new EmulatorHostSettings
            {
                ScreenZoom = _screenZoom,
                TapeBrowserVisible = _tapeBrowserVisible,
                JoystickType = _joystickType,
                FlashLoadEnabled = _flashLoadEnabled,
                FlashSaveEnabled = _flashSaveEnabled,
                LateContentionTimings = _contentionTiming == SpectrumContentionTiming.Late,
                PollingLoaderAccelerationEnabled = _edgeLoadEnabled,
                SemanticLoaderAccelerationEnabled = _semanticEdgeLoadEnabled,
                RunTapeAccelerationAtMaximumSpeed = _runTapeAccelerationAtMaximumSpeed,
                AutoLoadTapeOnAttach = _autoLoadTapeOnAttach,
                AutoTapePlayStopEnabled = _autoTapePlayStopEnabled,
                DirtyLinePresentationEnabled = UseDirtyLinePresentation,
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
                _settingsStore.Save(settings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"Unable to save host settings: {ex}");
            }
        }
        private static string GetSettingsPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZedExEss",
                "settings.json");
        }
        /// <summary>Stops the old runner and atomically rebuilds every model-dependent component.</summary>
        /// <remarks>
        /// The optional initializer is invoked after CPU/device wiring but before a new runner is
        /// started, allowing snapshot state to be restored without racing the emulation thread.
        /// Tape position can be preserved independently of the machine being replaced.
        /// </remarks>
        private void InitializeMachine(SpectrumModel model, RomSet roms, Action<Z80, SpectrumMemory, SpectrumUlaRenderer>? initializer, bool preserveTape, bool rewindTape = false)
        {
            StopZxNextHostMachine();
            StopZx8xHostMachine();
            _autoLoadInjector = null;
            if(_emulator != null)
                _emulator.FrameCompleted -= OnFrameCompleted;
            _oscilloscopeWindow?.AttachAudioSource(null);
            _audioRenderer = null;
            _turboRunner?.Dispose();
            _fastTapeRunner?.Dispose();
            _audioPlayer?.Dispose();
            _fastTapeRunner = null;
            _quickPauseRunState = null;
            CloseDivStorage(showErrors: false);

            _model = model;
            int sampleRate = SpectrumAudioTiming.DefaultSampleRate;
            SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
            {
                Model = model,
                Roms = roms,
                SampleRate = sampleRate,
                AyOutputAmplitude = 13_500,
                JoystickType = _joystickType,
                ForceFullFrameCopy = _gigascreenBlendEnabled || !UseDirtyLinePresentation,
                CovoxEnabled = _covoxEnabled,
                GeneralSoundFirmware = _generalSoundEnabled ? LoadGeneralSoundFirmware() : default,
                MultifaceModel = _multifaceModel,
                MultifaceRom = LoadMultifaceRom(_multifaceModel),
                TapeRecorder = _tapeRecorder,
                ContentionTiming = _contentionTiming,
                BeforeCpuStep = _flashLoadEnabled || _flashSaveEnabled ? TryTapeTraps : null,
                ConfigureDevices = ConfigureOptionalMachineDevices
            });

            _machine = machine;
            if (!_replayEventsHooked)
            {
                _replayEventsHooked = true;
                _replaySession.PlaybackEnded += reason => _uiDispatcher.TryPost(() =>
                {
                    StatusText.Text = reason;
                    UpdateReplayMenuState();
                });

                // Cartridges are written back as the Interface 1 records them, on the
                // thread running the machine. A file it cannot write must be said out
                // loud rather than discovered at the end of the session.
                _session.Interface1.AutoSaveFailed += message =>
                    _uiDispatcher.TryPost(() => StatusText.Text = message);
            }

            StatusText.Text = $"{model} running";
            _replaySession.AttachMachine(machine);
            ClearStateSlots();
            _memory = machine.Memory;
            _cpu = machine.Cpu;
            _emulator = machine.Emulator;
            _audioRenderer = machine.Audio;
            _keyboard = machine.Keyboard;
            _joystick = machine.Joystick;
            _earInput = machine.EarInput;
            _cpuHz = machine.CpuClockHz;
            _tstatesPerFrame = machine.TstatesPerFrame;
            _session.ReplaceMachine(machine, preserveTape, rewindTape);

            _oscilloscopeWindow?.AttachAudioSource(machine.Audio);
            _debugger.Attach(machine.Cpu, machine.Memory, machine.Ports, model);
            _earInput.AutoPlayRequested += OnTapeAutoPlayRequested;

            var timing = SpectrumUlaTiming.ForModel(model);
            _display = new WpfSpectrumDisplay(timing.FrameWidth, timing.FrameHeight);
            _displayHeightScale = 1;
            _presentBuffer = new int[timing.FrameWidth * timing.FrameHeight];
            _gigascreenPreviousBuffer = new int[_presentBuffer.Length];
            _gigascreenBlendBuffer = new int[_presentBuffer.Length];
            _gigascreenHasPreviousFrame = false;
            _dirtyLines = new int[timing.FrameHeight];
            ScreenImage.Source = _display.Bitmap;
            ApplyScreenZoom();
            UpdateZoomMenuChecks();

            _earInput.EdgeLoadingEnabled = _edgeLoadEnabled;
            _earInput.SemanticAccelerationEnabled = _semanticEdgeLoadEnabled;
            _earInput.AutoPlayEnabled = _autoTapePlayStopEnabled;

            _emulator.FrameCompleted += OnFrameCompleted;
            UpdateCpuStepHooks();

            initializer?.Invoke(machine.Cpu, machine.Memory, machine.Renderer);
            _debuggerWindow?.RefreshAll(followPc: true);

            if (_turboEnabled)
            {
                _turboRunner = new TurboRunner(_emulator, presentEveryNFrames: 5);
            }
            else
            {
                _audioPlayer = CreateWaveOutPlayer(_emulator, sampleRate);
            }

            if (!_speedStopwatch.IsRunning)
            {
                _speedStopwatch.Start();
            }
            _lastSpeedSeconds = _speedStopwatch.Elapsed.TotalSeconds;
            _lastSpeedTstates = _cpu.Cyc;
            UpdateWindowTitle();

            UpdateModelMenuChecks();
            UpdateJoystickMenuChecks();
            UpdateDivExpansionMenuChecks();
            UpdateMultifaceMenuChecks();
            UpdateCovoxMenuChecks();
            UpdateGeneralSoundMenuChecks();
            UpdateReplayMenuState();
            UpdateScorpionTurboMenuChecks();
            LateContentionMenu.IsChecked = _contentionTiming == SpectrumContentionTiming.Late;
            UpdateInterface1MenuState();
            UpdateDivStorageMenuState();
            UpdateDiskMenuState();
            UpdateDiskUi();
            UpdateTapeMenuChecks();
            UpdateQuickAccessState();
            TurboMenu.IsChecked = _turboEnabled;
            DirtyLinePresentationMenu.IsChecked = UseDirtyLinePresentation;

            RefreshTapeAttachmentUi();

            RefreshTapeFastRunMode();
            _uiDispatcher.TryPost(ResizeWindowToScreenZoom, UiDispatchPriority.Loaded);
        }
        /// <summary>
        /// Attaches media-backed expansion hardware while the portable machine graph is being
        /// built. Error presentation remains a host concern; successful devices are handed back
        /// to the core through its port and memory configuration APIs.
        /// </summary>
        private void ConfigureOptionalMachineDevices(SpectrumMachineConfigurationContext context)
        {
            SpectrumModel model = context.Model;
            SpectrumMemory memory = context.Memory;
            SpectrumPortBus ports = context.Ports;

            _divDevice = null;
            ObserveInterface1Device(null);
            _session.Interface1.ConnectDevice(null);
            _beta128Device = null;
            _betaDiskController = null;
            if (_interface1Enabled && SpectrumInterface1Compatibility.IsSupported(model))
            {
                string romPath = Path.Combine(
                    AppContext.BaseDirectory,
                    "ROMs",
                    SpectrumInterface1Compatibility.GetRomFileName(_interface1RomRevision));
                try
                {
                    var interface1Device = new SpectrumInterface1Device(File.ReadAllBytes(romPath));
                    interface1Device.AttachRs232Endpoint(_interface1Rs232Endpoint);
                    _session.Interface1.ConnectDevice(interface1Device);
                    ObserveInterface1Device(interface1Device);
                    memory.ConfigureInterface1(interface1Device);
                    ports.AddDevice(interface1Device);
                }
                catch (Exception ex)
                {
                    _interface1Enabled = false;
                    _session.Interface1.ConnectDevice(null);
                    MessageBox.Show(ex.Message, "Interface 1 ROM Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            if (SpectrumModelTraits.HasBeta128Disk(model))
            {
                if (TryCreateBeta128(model, out SpectrumBeta128Device? beta128Device, out string betaError)
                    && beta128Device != null)
                {
                    _beta128Device = beta128Device;
                    memory.ConfigureBeta128(beta128Device);
                    _betaDiskController = new SpectrumBeta128DiskController(beta128Device);
                    _betaDiskController.ConfigureForModel(model, memory);
                    if (_trdDiskImage != null)
                    {
                        _betaDiskController.InsertDisk(0, _trdDiskImage);
                    }

                    if (_trdDiskImageB != null)
                    {
                        _betaDiskController.InsertDisk(1, _trdDiskImageB);
                    }

                    ports.AddDevice(_betaDiskController);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(betaError))
                    {
                        betaError = "Unable to initialise Beta 128/TR-DOS ROM.";
                    }

                    MessageBox.Show(betaError, "TR-DOS ROM Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            if (_divExpansionMode != SpectrumDivExpansionMode.Disabled)
            {
                if (TryCreateDivExpansion(_divExpansionMode, out SpectrumDivMmcDevice? divDevice, out string divError)
                    && divDevice != null)
                {
                    _divDevice = divDevice;
                    divDevice.AutomapTrDosEntryEnabled = !SpectrumModelTraits.HasBeta128Disk(model);
                    TryAttachDivStorage(divDevice);
                    memory.ConfigureDivExpansion(divDevice);
                    ports.AddDevice(divDevice);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(divError))
                    {
                        divError = $"Unable to initialise {_divExpansionMode}.";
                    }

                    MessageBox.Show(divError, "DivMMC Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    _divExpansionMode = SpectrumDivExpansionMode.Disabled;
                }
            }

            _plus3DiskController = null;
            if (SpectrumModelTraits.HasPlus3Disk(model))
            {
                _plus3DiskController = new SpectrumPlus3DiskController();
                ApplyFdcTraceConfiguration();
                if (_diskImage != null)
                {
                    _plus3DiskController.InsertDisk(0, _diskImage);
                }

                if (_diskImageB != null)
                {
                    _plus3DiskController.InsertDisk(1, _diskImageB);
                }

                ports.AddDevice(_plus3DiskController);
            }
        }
        private void OnFrameCompleted()
        {
            // The audio/turbo producer raises this event. Coalesce frames while the dispatcher is
            // busy so emulation never blocks behind an accumulating queue of stale presentations.
            SpectrumEmulator source = _emulator;
            if (Interlocked.Exchange(ref _renderPending, 1) == 1)
            {
                return;
            }

            _uiDispatcher.TryPost(() =>
            {
                try
                {
                    if (_zx8xMachine != null || _zxNextMachine != null || !ReferenceEquals(source, _emulator))
                    {
                        return;
                    }

                    if (_gigascreenBlendEnabled)
                    {
                        if (_emulator.TryCopyFrame(_presentBuffer))
                        {
                            PresentGigascreenFrame();
                        }
                    }
                    else if (UseDirtyLinePresentation)
                    {
                        if (_emulator.TryCopyFrame(_presentBuffer, _dirtyLines, out int dirtyCount))
                        {
                            _display.PresentDirty(_presentBuffer, _dirtyLines, dirtyCount);
                        }
                    }
                    else if (_emulator.TryCopyFrame(_presentBuffer))
                    {
                        _display.Present(_presentBuffer);
                    }

                    UpdateTapeUi();
                    UpdateDiskUi();
                }
                finally
                {
                    Interlocked.Exchange(ref _renderPending, 0);
                }
            }, UiDispatchPriority.Render);
        }
        private void PresentGigascreenFrame()
        {
            int[]? previous = _gigascreenPreviousBuffer;
            int[]? blended = _gigascreenBlendBuffer;
            if (previous == null || blended == null || previous.Length != _presentBuffer.Length || blended.Length != _presentBuffer.Length)
            {
                _display.Present(_presentBuffer);
                return;
            }

            if (!_gigascreenHasPreviousFrame)
            {
                Array.Copy(_presentBuffer, previous, _presentBuffer.Length);
                _gigascreenHasPreviousFrame = true;
                _display.Present(_presentBuffer);
                return;
            }

            BlendGigascreenFrames(_presentBuffer, previous, blended);
            _display.Present(blended);
            Array.Copy(_presentBuffer, previous, _presentBuffer.Length);
        }
        private static void BlendGigascreenFrames(int[] current, int[] previous, int[] destination)
        {
            int length = Math.Min(Math.Min(current.Length, previous.Length), destination.Length);
            for (int i = 0; i < length; i++)
            {
                int a = current[i];
                int b = previous[i];
                int rgb = (a & b & 0x00FFFFFF) + (((a ^ b) & 0x00FEFEFE) >> 1);
                destination[i] = unchecked((int)0xFF000000) | rgb;
            }
        }
        private async void OnOpenFile(object sender, RoutedEventArgs e)
        {
            string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
            {
                DefaultExtension = ".z80",
                Filters =
                [
                    new FileDialogFilter("Supported Files", "*.z80", "*.sna", "*.szx", "*.o", "*.p", "*.81", "*.tap", "*.tzx", "*.csw", "*.dsk", "*.trd", "*.scl", "*.mdr", "*.img", "*.hdf", "*.sd", "*.bin"),
                    new FileDialogFilter("Snapshots", "*.z80", "*.sna", "*.szx"),
                    new FileDialogFilter("ZX80/ZX81 Program Images", "*.o", "*.p", "*.81"),
                    new FileDialogFilter("Tape Files", "*.tap", "*.tzx", "*.csw"),
                    new FileDialogFilter("Disk Images", "*.dsk", "*.trd", "*.scl"),
                    new FileDialogFilter("Microdrive Cartridges", "*.mdr"),
                    new FileDialogFilter("DivMMC Storage Images", "*.img", "*.hdf", "*.sd", "*.bin"),
                    new FileDialogFilter("All Files", "*.*")
                ]
            });

            if (path == null)
            {
                Focus();
                return;
            }

            try
            {
                OpenFilePath(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Open File Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Focus();
        }
        private void OpenFilePath(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();

            switch (ext)
            {
                case ".z80":
                    LoadSnapshot(path, isZ80: true);
                    break;
                case ".sna":
                    LoadSnapshot(path, isZ80: false);
                    break;
                case ".szx":
                    LoadSzxSnapshot(path);
                    break;
                case ".o":
                case ".p":
                case ".81":
                    LoadZx8xProgramImagePath(path);
                    break;
                case ".tap":
                case ".tzx":
                case ".csw":
                    LoadTapeFile(path);
                    break;
                case ".dsk":
                case ".trd":
                case ".scl":
                    LoadDiskFile(path);
                    break;
                case ".img":
                case ".hdf":
                case ".sd":
                case ".bin":
                    AttachDivStorage(path, folderBacked: false, showDeferredMessage: true);
                    break;
                case ".mdr":
                    AttachMicrodriveToFirstEmptyDrive(path);
                    break;
                default:
                    MessageBox.Show($"Unsupported file type: {ext}", "Open File", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        }
        private void OnWindowPreviewDragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasSupportedDropData(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        private void OnWindowPreviewDrop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            {
                return;
            }

            var errors = new List<string>();
            var unsupported = new List<string>();
            int nextPlus3Drive = GetInitialPlus3DropDrive();
            int nextBetaDrive = GetInitialBetaDropDrive();
            int plus3DiskCount = 0;
            int betaDiskCount = 0;
            bool handledAny = false;

            foreach (string path in paths)
            {
                try
                {
                    if (TryOpenDroppedPath(path, ref nextPlus3Drive, ref nextBetaDrive, ref plus3DiskCount, ref betaDiskCount))
                    {
                        handledAny = true;
                    }
                    else
                    {
                        unsupported.Add(Path.GetFileName(path));
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }

            ShowDropMessages(unsupported, errors);

            if (handledAny)
            {
                Focus();
            }
        }
        private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_resizingWindowToScreenZoom)
            {
                return;
            }

            QueueFitScreenZoomToWindow();
        }
        private bool TryOpenDroppedPath(string path, ref int nextPlus3Drive, ref int nextBetaDrive, ref int plus3DiskCount, ref int betaDiskCount)
        {
            if (Directory.Exists(path))
            {
                AttachDivStorage(path, folderBacked: true, showDeferredMessage: true);
                return true;
            }

            if (!File.Exists(path))
            {
                return false;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".z80":
                    LoadSnapshot(path, isZ80: true);
                    return true;
                case ".sna":
                    LoadSnapshot(path, isZ80: false);
                    return true;
                case ".szx":
                    LoadSzxSnapshot(path);
                    return true;
                case ".o":
                case ".p":
                case ".81":
                    LoadZx8xProgramImagePath(path);
                    return true;
                case ".tap":
                case ".tzx":
                case ".csw":
                    LoadTapeFile(path);
                    return true;
                case ".dsk":
                    if (plus3DiskCount >= 2)
                    {
                        throw new InvalidOperationException("Only two +3 disk drives are available.");
                    }

                    LoadDiskFile(path, nextPlus3Drive);
                    plus3DiskCount++;
                    nextPlus3Drive = ToggleDrive(nextPlus3Drive);
                    return true;
                case ".trd":
                case ".scl":
                    if (betaDiskCount >= 2)
                    {
                        throw new InvalidOperationException("Only two Beta/TR-DOS drives are currently exposed in the UI.");
                    }

                    LoadDiskFile(path, nextBetaDrive);
                    betaDiskCount++;
                    nextBetaDrive = ToggleDrive(nextBetaDrive);
                    return true;
                case ".img":
                case ".hdf":
                case ".sd":
                case ".bin":
                    AttachDivStorage(path, folderBacked: false, showDeferredMessage: true);
                    return true;
                case ".mdr":
                    AttachMicrodriveToFirstEmptyDrive(path);
                    return true;
                default:
                    return false;
            }
        }
        private static bool HasSupportedDropData(IDataObject data)
        {
            if (data.GetData(DataFormats.FileDrop) is not string[] paths)
            {
                return false;
            }

            foreach (string path in paths)
            {
                if (IsSupportedDropPath(path))
                {
                    return true;
                }
            }

            return false;
        }
        private static bool IsSupportedDropPath(string path)
        {
            if (Directory.Exists(path))
            {
                return true;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext is ".z80" or ".sna" or ".szx" or ".o" or ".p" or ".81"
                or ".tap" or ".tzx" or ".csw" or ".dsk" or ".trd" or ".scl"
                or ".mdr" or ".img" or ".hdf" or ".sd" or ".bin";
        }
        private int GetInitialPlus3DropDrive()
        {
            return _diskImage == null ? 0 : _diskImageB == null ? 1 : 0;
        }
        private int GetInitialBetaDropDrive()
        {
            return _trdDiskImage == null ? 0 : _trdDiskImageB == null ? 1 : 0;
        }
        private static int ToggleDrive(int drive)
        {
            return drive == 0 ? 1 : 0;
        }
        private static void ShowDropMessages(IReadOnlyList<string> unsupported, IReadOnlyList<string> errors)
        {
            if (unsupported.Count == 0 && errors.Count == 0)
            {
                return;
            }

            var lines = new List<string>();
            if (unsupported.Count > 0)
            {
                lines.Add("Unsupported files:");
                for (int i = 0; i < unsupported.Count; i++)
                {
                    lines.Add($"  {unsupported[i]}");
                }
            }

            if (errors.Count > 0)
            {
                if (lines.Count > 0)
                {
                    lines.Add(string.Empty);
                }

                lines.Add("Files that could not be loaded:");
                for (int i = 0; i < errors.Count; i++)
                {
                    lines.Add($"  {errors[i]}");
                }
            }

            MessageBox.Show(string.Join(Environment.NewLine, lines), "Drag and Drop", MessageBoxButton.OK, errors.Count > 0 ? MessageBoxImage.Error : MessageBoxImage.Warning);
        }
        private void LoadSnapshot(string path, bool isZ80)
        {
            SpectrumModel snapshotModel = isZ80
                ? Z80Loader.DetectModel(path)
                : SnapshotLoader.DetectModel(path);

            if (!TryLoadRoms(snapshotModel, out RomSet roms, out string error))
            {
                MessageBox.Show(error, "ROM Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            InitializeMachine(snapshotModel, roms, (cpu, memory, renderer) =>
            {
                if (isZ80)
                {
                    cpu.LoadZ80(memory, renderer, path);
                }
                else
                {
                    SnapshotLoader.LoadSna(cpu, memory, renderer, path);
                }
            }, preserveTape: false);
        }
        private void LoadSzxSnapshot(string path)
        {
            SpectrumMachineSnapshot snapshot = SzxSnapshotCodec.Load(path);
            if (snapshot.Interface1 != null
                && !SpectrumInterface1Compatibility.IsSupported(snapshot.Model))
            {
                throw new InvalidDataException(
                    $"The snapshot contains Interface 1 state, which cannot be attached to {snapshot.Model}.");
            }

            if (!TryLoadRoms(snapshot.Model, out RomSet roms, out string error))
            {
                throw new InvalidOperationException(error);
            }

            // SZX describes the attached expansion graph. Do not retain an unrelated
            // Interface 1 or DivMMC merely because it was enabled in the previous session.
            _interface1Enabled = snapshot.Interface1 != null;
            _divExpansionMode = SpectrumDivExpansionMode.Disabled;
            InitializeMachine(snapshot.Model, roms, (_, _, _) =>
            {
                SpectrumMachine machine = _machine
                    ?? throw new InvalidOperationException("The replacement machine was not created.");
                SpectrumMachineSnapshotService.Restore(
                    machine,
                    snapshot,
                    snapshot.Interface1 != null ? _session.Interface1 : null);
            }, preserveTape: false);
        }
        private async void OnSaveSnapshot(object sender, RoutedEventArgs e)
        {
            SpectrumMachine? machine = _machine;
            if (machine == null || _zx8xMachine != null || _zxNextMachine != null)
            {
                MessageBox.Show(
                    "SZX snapshots are available for classic Spectrum models only.",
                    "Save Snapshot",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
            {
                Title = "Save SZX Snapshot",
                DefaultExtension = ".szx",
                SuggestedFileName = $"{machine.Model}.szx",
                Filters =
                [
                    new FileDialogFilter("ZX-State snapshots", "*.szx"),
                    new FileDialogFilter("All Files", "*.*")
                ]
            });
            if (path == null)
            {
                Focus();
                return;
            }

            EmulationRunState runState = SuspendEmulationForModal();
            try
            {
                SpectrumMachineSnapshot snapshot = SpectrumMachineSnapshotService.Capture(
                    machine,
                    _interface1Enabled && _interface1Device != null
                        ? _session.Interface1
                        : null);
                SzxSnapshotCodec.Save(path, snapshot);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Save Snapshot Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RestoreEmulationAfterModal(runState);
                Focus();
            }
        }
        private void OnResetMachine(object sender, RoutedEventArgs e)
        {
            if (_zxNextMachine != null)
            {
                ResetZxNextHostMachine(ZxNextResetKind.Hard);
                UpdateQuickAccessState();
                Focus();
                return;
            }

            if (_zx8xModel.HasValue)
            {
                InitializeZx8xMachine(_zx8xModel.Value);
                Focus();
                return;
            }

            if (!TryLoadRoms(_model, out RomSet roms, out string error))
            {
                MessageBox.Show(error, "ROM Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            InitializeMachine(_model, roms, null, preserveTape: true, rewindTape: true);
            Focus();
        }

        private void OnSoftResetZxNext(object sender, RoutedEventArgs e)
        {
            if (_zxNextMachine == null)
            {
                return;
            }

            ResetZxNextHostMachine(ZxNextResetKind.Soft);
            UpdateQuickAccessState();
            Focus();
        }
        private void OnExit(object sender, RoutedEventArgs e)
        {
            Close();
        }
        private void OnTurboModeToggle(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item)
            {
                return;
            }

            SetTurboMode(item.IsChecked);
        }
        private void OnFastForwardToggle(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem item)
            {
                SetFastForwardMode(item.IsChecked);
            }
        }
        private void OnQuickFastForward(object sender, RoutedEventArgs e)
        {
            SetFastForwardMode(!_fastForwardEnabled);
            Focus();
        }
        private void OnFastForwardSpeedClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item
                || item.Tag is not string value
                || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int speed))
            {
                UpdateFastForwardUi();
                return;
            }

            speed = Math.Clamp(
                speed,
                TimeStretchAudioSource.MinimumSpeedMultiplier,
                TimeStretchAudioSource.MaximumSpeedMultiplier);
            if (_fastForwardSpeed == speed)
            {
                UpdateFastForwardUi();
                return;
            }

            _fastForwardSpeed = speed;
            if (_fastForwardEnabled)
            {
                // Analysis history is tied to the old ratio, so replace the
                // audio owner cleanly at a host-buffer boundary.
                SetFastForwardMode(true);
            }
            else
            {
                UpdateFastForwardUi();
            }

            Focus();
        }
        private void OnModelMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
            {
                return;
            }

            if (!Enum.TryParse(tag, out SpectrumModel model))
            {
                return;
            }

            if (_zx8xMachine == null && _zxNextMachine == null && model == _model)
            {
                return;
            }

            if (!TryLoadRoms(model, out RomSet roms, out string error))
            {
                MessageBox.Show(error, "ROM Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateModelMenuChecks();
                return;
            }

            InitializeMachine(model, roms, null, preserveTape: true);
        }
        private void OnJoystickMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
            {
                return;
            }

            if (!Enum.TryParse(tag, out SpectrumJoystickType type))
            {
                return;
            }

            _joystickType = type;
            if (_joystick != null)
            {
                _joystick.Type = type;
            }
            if (_zxNextMachine != null)
            {
                _zxNextMachine.SetHostJoystickType(type);
            }

            UpdateJoystickMenuChecks();
            Focus();
        }
        /// <summary>
        /// Switches the ULA contention phase.
        /// </summary>
        /// <remarks>
        /// The tables are swapped on the running machine rather than rebuilding it: the
        /// point of the setting is to compare the two against the same software.
        /// </remarks>
        private void OnLateContentionToggle(object sender, RoutedEventArgs e)
        {
            _contentionTiming = LateContentionMenu.IsChecked
                ? SpectrumContentionTiming.Late
                : SpectrumContentionTiming.Early;
            if (_machine?.Contention != null)
            {
                _machine.Contention.Timing = _contentionTiming;
            }

            StatusText.Text = $"{_contentionTiming} contention timings";
            Focus();
        }
        private void OnScorpionTurboClick(object sender, RoutedEventArgs e)
        {
            SpectrumMachine? machine = _machine;
            if (machine == null || !machine.SupportsTurbo)
            {
                UpdateScorpionTurboMenuChecks();
                return;
            }

            // This is the front-panel toggle: a hardware latch the running program does
            // not have to be interrupted for.
            machine.TurboEnabled = !machine.TurboEnabled;
            UpdateScorpionTurboMenuChecks();
        }
        private void UpdateScorpionTurboMenuChecks()
        {
            SpectrumMachine? machine = _machine;
            bool supported = machine != null
                && machine.SupportsTurbo
                && _zxNextMachine == null
                && _zx8xMachine == null;
            ScorpionTurboMenu.IsEnabled = supported;
            ScorpionTurboMenu.IsChecked = supported && machine!.TurboEnabled;
        }
        /// <summary>Reads the General Sound firmware, or nothing when it is not installed.</summary>
        private static ReadOnlyMemory<byte> LoadGeneralSoundFirmware()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "ROMs", "gs.rom");
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : default;
            }
            catch (IOException)
            {
                return default;
            }
        }
        /// <summary>Reads the ROM for one Multiface variant, or nothing when absent.</summary>
        private static ReadOnlyMemory<byte> LoadMultifaceRom(SpectrumMultifaceModel model)
        {
            string file = SpectrumModelTraits.GetMultifaceRomFileName(model);
            if (string.IsNullOrEmpty(file))
            {
                return default;
            }

            string path = Path.Combine(AppContext.BaseDirectory, "ROMs", file);
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : default;
            }
            catch (IOException)
            {
                return default;
            }
        }
        private void OnMultifaceMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item
                || item.Tag is not string tag
                || !Enum.TryParse(tag, out SpectrumMultifaceModel model))
            {
                return;
            }

            if (model == _multifaceModel)
            {
                UpdateMultifaceMenuChecks();
                return;
            }

            if (model != SpectrumMultifaceModel.None
                && model != SpectrumModelTraits.SupportedMultiface(_model))
            {
                UpdateMultifaceMenuChecks();
                return;
            }

            if (model != SpectrumMultifaceModel.None && LoadMultifaceRom(model).IsEmpty)
            {
                MessageBox.Show(
                    $"This Multiface needs {SpectrumModelTraits.GetMultifaceRomFileName(model)} "
                        + "in the ROMs folder.",
                    "Multiface",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                UpdateMultifaceMenuChecks();
                return;
            }

            if (!TryLoadRoms(_model, out RomSet roms, out string error))
            {
                MessageBox.Show(error, "ROM Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateMultifaceMenuChecks();
                return;
            }

            _multifaceModel = model;
            if (model != SpectrumMultifaceModel.None)
            {
                // A Multiface and DivMMC overlay the same low memory and their control
                // ports overlap, so fitting one unfits the other.
                _divExpansionMode = SpectrumDivExpansionMode.Disabled;
            }

            InitializeMachine(_model, roms, null, preserveTape: true);
        }
        private void OnMultifaceNmi(object sender, RoutedEventArgs e)
        {
            _machine?.PressMultifaceButton();
            Focus();
        }
        private void UpdateMultifaceMenuChecks()
        {
            SpectrumMultifaceModel supported = SpectrumModelTraits.SupportedMultiface(_model);
            bool available = _zxNextMachine == null && _zx8xMachine == null;
            MultifaceDisabledMenu.IsChecked = _multifaceModel == SpectrumMultifaceModel.None;
            MultifaceDisabledMenu.IsEnabled = available;

            // Only the variant built for this machine is offered; the others stay visible
            // but disabled so it is clear which one this model takes.
            SetMultifaceItem(MultifaceOneMenu, SpectrumMultifaceModel.One, supported, available);
            SetMultifaceItem(Multiface128Menu, SpectrumMultifaceModel.OneTwentyEight, supported, available);
            SetMultifaceItem(MultifaceThreeMenu, SpectrumMultifaceModel.Three, supported, available);
        }
        private void SetMultifaceItem(
            MenuItem item,
            SpectrumMultifaceModel model,
            SpectrumMultifaceModel supported,
            bool available)
        {
            item.IsChecked = _multifaceModel == model;
            item.IsEnabled = available && model == supported;
        }
        private void OnGeneralSoundEnabledClick(object sender, RoutedEventArgs e)
        {
            if (!TryLoadRoms(_model, out RomSet roms, out string error))
            {
                MessageBox.Show(error, "ROM Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateGeneralSoundMenuChecks();
                return;
            }

            if (!_generalSoundEnabled && LoadGeneralSoundFirmware().IsEmpty)
            {
                MessageBox.Show(
                    "General Sound needs a gs.rom in the ROMs folder. Without firmware the card would be "
                        + "detected but never respond, so it is left unfitted.",
                    "General Sound",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                UpdateGeneralSoundMenuChecks();
                return;
            }

            // Fitting the card changes the port map, so the machine is rebuilt.
            _generalSoundEnabled = !_generalSoundEnabled;
            InitializeMachine(_model, roms, null, preserveTape: true);
        }
        private void UpdateGeneralSoundMenuChecks()
        {
            GeneralSoundEnabledMenu.IsChecked = _generalSoundEnabled;
            GeneralSoundEnabledMenu.IsEnabled = _zxNextMachine == null && _zx8xMachine == null;
        }
        private void OnCovoxEnabledClick(object sender, RoutedEventArgs e)
        {
            if (!TryLoadRoms(_model, out RomSet roms, out string error))
            {
                MessageBox.Show(error, "ROM Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateCovoxMenuChecks();
                return;
            }

            // Fitting the DAC changes the port map, so the machine is rebuilt the same
            // way a DivMMC or Interface 1 change rebuilds it.
            _covoxEnabled = !_covoxEnabled;
            InitializeMachine(_model, roms, null, preserveTape: true);
        }
        private void UpdateCovoxMenuChecks()
        {
            CovoxEnabledMenu.IsChecked = _covoxEnabled;
            CovoxEnabledMenu.IsEnabled = _zxNextMachine == null && _zx8xMachine == null;
        }
        private void OnDivExpansionMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag)
            {
                return;
            }

            if (!Enum.TryParse(tag, out SpectrumDivExpansionMode mode))
            {
                return;
            }

            if (mode == _divExpansionMode)
            {
                UpdateDivExpansionMenuChecks();
                return;
            }

            if (!TryLoadRoms(_model, out RomSet roms, out string error))
            {
                MessageBox.Show(error, "ROM Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateDivExpansionMenuChecks();
                return;
            }

            _divExpansionMode = mode;
            if (mode != SpectrumDivExpansionMode.Disabled)
            {
                _interface1Enabled = false;

                // DivMMC and a Multiface overlay the same low memory, so they cannot
                // both be fitted.
                _multifaceModel = SpectrumMultifaceModel.None;
            }
            InitializeMachine(_model, roms, null, preserveTape: true);
        }
        /// <summary>
        /// The toolbar's NMI button, which belongs to whichever interface is fitted.
        /// </summary>
        private void OnQuickNmi(object sender, RoutedEventArgs e)
        {
            if (_machine?.Multiface != null)
            {
                _machine.PressMultifaceButton();
                Focus();
                return;
            }

            TriggerDivNmi();
        }
        private void OnDivNmi(object sender, RoutedEventArgs e)
        {
            TriggerDivNmi();
        }
        private void OnQuickPausePlay(object sender, RoutedEventArgs e)
        {
            ToggleQuickPause();
            Focus();
        }
        private void OnToggleTapeBrowser(object sender, RoutedEventArgs e)
        {
            SetTapeBrowserVisible(!_tapeBrowserVisible, resizeWindow: true);
            if (_tapeBrowserVisible)
            {
                UpdateTapeUi();
            }

            UpdateQuickAccessState();
            Focus();
        }
        private void OnZoomMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item || item.Tag is not string tag || !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
            {
                UpdateZoomMenuChecks();
                return;
            }

            SetScreenZoom(zoom, resizeWindow: true);
            Focus();
        }
        private void SetTapeBrowserVisible(bool visible, bool resizeWindow)
        {
            _tapeBrowserVisible = visible;
            TapeBrowserPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            TapeBrowserColumn.Width = visible ? new GridLength(300) : new GridLength(0);

            if (visible)
            {
                SyncTapeBlockSelection(_tapeLoader?.CurrentBlockIndex ?? -1, scrollIntoView: true);
            }

            if (resizeWindow)
            {
                _uiDispatcher.TryPost(ResizeWindowToScreenZoom, UiDispatchPriority.Loaded);
            }
        }
        private void SetScreenZoom(double zoom, bool resizeWindow)
        {
            _screenZoom = Math.Clamp(zoom, MinScreenZoom, MaxScreenZoom);
            ApplyScreenZoom();
            UpdateZoomMenuChecks();

            if (resizeWindow)
            {
                _uiDispatcher.TryPost(ResizeWindowToScreenZoom, UiDispatchPriority.Loaded);
            }
        }
        private void ApplyScreenZoom()
        {
            if (_display == null || ScreenImage == null)
            {
                return;
            }

            ScreenImage.Width = _display.Width * _screenZoom;
            ScreenImage.Height = _display.Height * _displayHeightScale * _screenZoom;
        }
        private void ResizeWindowToScreenZoom()
        {
            if (!IsLoaded || WindowState != WindowState.Normal || _display == null)
            {
                return;
            }

            _resizingWindowToScreenZoom = true;
            try
            {
                SizeToContent = SizeToContent.Manual;

                double screenWidth = _display.Width * _screenZoom;
                double screenHeight = _display.Height * _displayHeightScale * _screenZoom;
                Thickness screenMargin = ScreenHost.Margin;

                double mainWidth = screenWidth + screenMargin.Left + screenMargin.Right;
                if (_tapeBrowserVisible)
                {
                    mainWidth += TapeBrowserColumn.Width.Value;
                }

                double mainHeight = screenHeight + screenMargin.Top + screenMargin.Bottom;
                double topChromeHeight = MainMenu.ActualHeight + QuickAccessToolBar.ActualHeight;
                double clientWidth = mainWidth;
                double clientHeight = topChromeHeight + mainHeight;

                double windowChromeWidth = RootDockPanel.ActualWidth > 0
                    ? Math.Max(0, ActualWidth - RootDockPanel.ActualWidth)
                    : 0;
                double windowChromeHeight = RootDockPanel.ActualHeight > 0
                    ? Math.Max(0, ActualHeight - RootDockPanel.ActualHeight)
                    : 0;

                Width = Math.Ceiling(clientWidth + windowChromeWidth);
                Height = Math.Ceiling(clientHeight + windowChromeHeight);
            }
            catch
            {
                _resizingWindowToScreenZoom = false;
                throw;
            }

            // WPF raises the resulting SizeChanged after layout has applied Width/Height.
            // Keep the guard set through that layout pass so fit-to-window cannot undo
            // an explicit integer zoom selection on the Next's larger 640x512 surface.
            if (!_uiDispatcher.TryPost(
                    () => _resizingWindowToScreenZoom = false,
                    UiDispatchPriority.Loaded))
            {
                _resizingWindowToScreenZoom = false;
            }
        }
        private void QueueFitScreenZoomToWindow()
        {
            if (Interlocked.Exchange(ref _windowFitZoomQueued, 1) == 1)
            {
                return;
            }

            _uiDispatcher.TryPost(() =>
            {
                Interlocked.Exchange(ref _windowFitZoomQueued, 0);
                if (_resizingWindowToScreenZoom)
                {
                    return;
                }

                FitScreenZoomToWindow();
            }, UiDispatchPriority.Loaded);
        }
        private void FitScreenZoomToWindow()
        {
            if (!IsLoaded || _display == null || ScreenHost == null)
            {
                return;
            }

            double availableWidth = ScreenHost.ActualWidth;
            double availableHeight = ScreenHost.ActualHeight;
            if (availableWidth <= 0 || availableHeight <= 0)
            {
                return;
            }

            double rawFit = Math.Min(availableWidth / _display.Width, availableHeight / _display.Height);
            double fitZoom = Math.Floor(rawFit / ScreenZoomStep) * ScreenZoomStep;
            fitZoom = Math.Clamp(fitZoom, MinScreenZoom, MaxScreenZoom);
            if (Math.Abs(fitZoom - _screenZoom) < 0.001)
            {
                return;
            }

            _screenZoom = fitZoom;
            ApplyScreenZoom();
            UpdateZoomMenuChecks();
        }
        private void UpdateZoomMenuChecks()
        {
            if (Zoom1Menu == null)
            {
                return;
            }

            Zoom1Menu.IsChecked = Math.Abs(_screenZoom - 1.0) < 0.001;
            Zoom2Menu.IsChecked = Math.Abs(_screenZoom - 2.0) < 0.001;
            Zoom3Menu.IsChecked = Math.Abs(_screenZoom - 3.0) < 0.001;
            Zoom4Menu.IsChecked = Math.Abs(_screenZoom - 4.0) < 0.001;
        }
        private bool TriggerDivNmi()
        {
            if (_divExpansionMode == SpectrumDivExpansionMode.Disabled)
            {
                return false;
            }

            _cpu?.Z80GenNMI();
            Focus();
            return true;
        }
        private async void OnDivStorageInsert(object sender, RoutedEventArgs e)
        {
            string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
            {
                DefaultExtension = ".img",
                Filters =
                [
                    new FileDialogFilter("Raw Storage Images", "*.img", "*.hdf", "*.sd", "*.bin"),
                    new FileDialogFilter("All Files", "*.*")
                ]
            });

            if (path == null)
            {
                return;
            }

            AttachDivStorage(path, folderBacked: false, showDeferredMessage: true);
            Focus();
        }
        private async void OnDivStorageFolder(object sender, RoutedEventArgs e)
        {
            string? folder = await _fileDialogs.OpenFolderAsync("Select the folder to use as DivMMC storage");
            if (folder == null)
            {
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                "The selected folder will be projected as a FAT16 DivMMC card. Files changed by the emulated machine will be written back to this folder when storage is ejected, changed, or the emulator closes.",
                "Use Folder as DivMMC Storage",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);
            if (result != MessageBoxResult.OK)
            {
                return;
            }

            AttachDivStorage(folder, folderBacked: true, showDeferredMessage: true);
            Focus();
        }
        private void AttachDivStorage(string path, bool folderBacked, bool showDeferredMessage)
        {
            _divStoragePath = path;
            _divStorageFolderBacked = folderBacked;
            ReopenDivStorage(showDeferredMessage);
            UpdateDivStorageMenuState();
        }
        private async void OnDivStorageNew(object sender, RoutedEventArgs e)
        {
            string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
            {
                DefaultExtension = ".img",
                SuggestedFileName = "divmmc-fat16.img",
                Filters =
                [
                    new FileDialogFilter("Raw SD Images", "*.img"),
                    new FileDialogFilter("All Files", "*.*")
                ]
            });

            if (path == null)
            {
                return;
            }

            try
            {
                SpectrumFatImageBuilder.CreateBlankFat16Image(path);
                _divStoragePath = path;
                _divStorageFolderBacked = false;
                _divStorageWriteProtected = false;
                ReopenDivStorage(showDeferredMessage: true);
                UpdateDivStorageMenuState();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "DivMMC Storage Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Focus();
        }
        private async void OnDivStorageImportFolder(object sender, RoutedEventArgs e)
        {
            if (_divStoragePath == null || _divStorageFolderBacked)
            {
                return;
            }

            if (_divStorageWriteProtected)
            {
                MessageBox.Show("The DivMMC storage image is write protected.", "DivMMC Storage", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string? folder = await _fileDialogs.OpenFolderAsync("Select the folder to copy into the DivMMC image");
            if (folder == null)
            {
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                "This will replace the FAT directory contents of the current DivMMC image with the selected folder tree. Continue?",
                "Import Folder to DivMMC Image",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.OK)
            {
                return;
            }

            try
            {
                CloseDivStorage(showErrors: true);

                SpectrumFatImageBuilder.ImportDirectoryIntoFat16Image(_divStoragePath, folder);
                ReopenDivStorage(showDeferredMessage: false);
                UpdateDivStorageMenuState();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "DivMMC Storage Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Focus();
        }
        private void OnDivStorageEject(object sender, RoutedEventArgs e)
        {
            CloseDivStorage(showErrors: true);
            _divStoragePath = null;
            _divStorageFolderBacked = false;
            UpdateDivStorageMenuState();
            Focus();
        }
        private void OnDivStorageWriteProtectToggle(object sender, RoutedEventArgs e)
        {
            _divStorageWriteProtected = DivStorageWriteProtectMenu.IsChecked;
            ReopenDivStorage(showDeferredMessage: false);
            UpdateDivStorageMenuState();
            Focus();
        }
        private async void OnDiskInsert(object sender, RoutedEventArgs e)
        {
            await InsertPlus3DiskFromDialog(0);
        }
        private async void OnDiskBInsert(object sender, RoutedEventArgs e)
        {
            await InsertPlus3DiskFromDialog(1);
        }
        private async Task InsertPlus3DiskFromDialog(int drive)
        {
            string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
            {
                DefaultExtension = ".dsk",
                Filters =
                [
                    new FileDialogFilter("+3 Disk Images", "*.dsk"),
                    new FileDialogFilter("All Files", "*.*")
                ]
            });

            if (path == null)
            {
                return;
            }

            try
            {
                LoadDiskFile(path, drive);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Disk Image Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private async void OnBetaDiskInsert(object sender, RoutedEventArgs e)
        {
            await InsertTrdDiskFromDialog(0);
        }
        private async void OnBetaDiskBInsert(object sender, RoutedEventArgs e)
        {
            await InsertTrdDiskFromDialog(1);
        }
        private async Task InsertTrdDiskFromDialog(int drive)
        {
            string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
            {
                DefaultExtension = ".trd",
                Filters =
                [
                    new FileDialogFilter("TR-DOS Images", "*.trd", "*.scl"),
                    new FileDialogFilter("TRD Raw Images", "*.trd"),
                    new FileDialogFilter("SCL Compact Images", "*.scl"),
                    new FileDialogFilter("All Files", "*.*")
                ]
            });

            if (path == null)
            {
                return;
            }

            try
            {
                LoadDiskFile(path, drive);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "TR-DOS Disk Image Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private async void OnDiskNewBlank(object sender, RoutedEventArgs e)
        {
            await CreateBlankDiskFromDialog(0);
        }
        private async void OnDiskBNewBlank(object sender, RoutedEventArgs e)
        {
            await CreateBlankDiskFromDialog(1);
        }
        private async Task CreateBlankDiskFromDialog(int drive)
        {
            string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
            {
                DefaultExtension = ".dsk",
                SuggestedFileName = drive == 0 ? "blank-plus3-a.dsk" : "blank-plus3-b.dsk",
                ConfirmOverwrite = true,
                Filters =
                [
                    new FileDialogFilter("Disk Images", "*.dsk"),
                    new FileDialogFilter("All Files", "*.*")
                ]
            });

            if (path == null)
            {
                return;
            }

            try
            {
                Plus3DiskImage image = Plus3DiskImage.CreateBlankPlus3DataDisk(path);
                InsertDiskImage(image, path, drive, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "New Disk Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private async void OnDiskSaveAs(object sender, RoutedEventArgs e)
        {
            await SavePlus3DiskAs(0);
        }
        private async void OnDiskBSaveAs(object sender, RoutedEventArgs e)
        {
            await SavePlus3DiskAs(1);
        }
        private async void OnBetaDiskSaveAs(object sender, RoutedEventArgs e)
        {
            await SaveTrdDiskAs(0);
        }
        private async void OnBetaDiskBSaveAs(object sender, RoutedEventArgs e)
        {
            await SaveTrdDiskAs(1);
        }
        private async Task SavePlus3DiskAs(int drive)
        {
            Plus3DiskImage? diskImage = GetDiskImage(drive);
            string? diskPath = GetDiskPath(drive);
            if (diskImage == null)
            {
                return;
            }

            string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
            {
                DefaultExtension = ".dsk",
                SuggestedFileName = Path.GetFileName(diskPath) ?? (drive == 0 ? "drive-a.dsk" : "drive-b.dsk"),
                InitialDirectory = Path.GetDirectoryName(diskPath),
                ConfirmOverwrite = true,
                Filters =
                [
                    new FileDialogFilter("Disk Images", "*.dsk"),
                    new FileDialogFilter("All Files", "*.*")
                ]
            });

            if (path == null)
            {
                return;
            }

            try
            {
                diskImage.SaveAs(path);
                SetDiskImage(drive, diskImage, path);
                _plus3DiskController?.SetDriveWriteProtected(drive, diskImage.IsWriteProtected);

                UpdateDiskMenuState();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Save Disk Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private async Task SaveTrdDiskAs(int drive)
        {
            TrdDiskImage? trdImage = GetTrdDiskImage(drive);
            string? diskPath = GetTrdDiskPath(drive);
            if (trdImage == null)
            {
                return;
            }

            string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
            {
                DefaultExtension = ".trd",
                SuggestedFileName = Path.GetFileName(diskPath) ?? (drive == 0 ? "drive-a.trd" : "drive-b.trd"),
                InitialDirectory = Path.GetDirectoryName(diskPath),
                ConfirmOverwrite = true,
                Filters =
                [
                    new FileDialogFilter("TR-DOS Images", "*.trd", "*.scl"),
                    new FileDialogFilter("TRD Raw Images", "*.trd"),
                    new FileDialogFilter("SCL Compact Images", "*.scl"),
                    new FileDialogFilter("All Files", "*.*")
                ]
            });

            if (path == null)
            {
                return;
            }

            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".scl")
                {
                    trdImage.ExportScl(path);
                }
                else
                {
                    trdImage.SaveAs(path);
                    SetTrdDiskImage(drive, trdImage, path);
                }

                UpdateDiskMenuState();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Save Disk Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private void OnDiskEject(object sender, RoutedEventArgs e)
        {
            EjectPlus3Disk(0);
        }
        private void OnDiskBEject(object sender, RoutedEventArgs e)
        {
            EjectPlus3Disk(1);
        }
        private void EjectPlus3Disk(int drive)
        {
            SetDiskImage(drive, null, null);
            _plus3DiskController?.EjectDisk(drive);
            UpdateDiskMenuState();
        }
        private void EjectPlus3DisksForTapeAutoLoad()
        {
            if (_diskImage == null && _diskImageB == null)
            {
                return;
            }

            SetDiskImage(0, null, null);
            SetDiskImage(1, null, null);
            _plus3DiskController?.EjectDisk(0);
            _plus3DiskController?.EjectDisk(1);
            UpdateDiskMenuState();
        }
        private void OnBetaDiskEject(object sender, RoutedEventArgs e)
        {
            EjectTrdDisk(0);
        }
        private void OnBetaDiskBEject(object sender, RoutedEventArgs e)
        {
            EjectTrdDisk(1);
        }
        private void EjectTrdDisk(int drive)
        {
            SetTrdDiskImage(drive, null, null);
            _betaDiskController?.EjectDisk(drive);
            UpdateDiskMenuState();
        }
        private void OnDiskWriteProtectToggle(object sender, RoutedEventArgs e)
        {
            TogglePlus3DiskWriteProtect(0, WriteProtectRequested(sender, DiskWriteProtectMenu));
        }
        private void OnDiskBWriteProtectToggle(object sender, RoutedEventArgs e)
        {
            TogglePlus3DiskWriteProtect(1, WriteProtectRequested(sender, DiskBWriteProtectMenu));
        }
        private void OnBetaDiskWriteProtectToggle(object sender, RoutedEventArgs e)
        {
            ToggleTrdDiskWriteProtect(0, WriteProtectRequested(sender, BetaDiskWriteProtectMenu));
        }

        /// <summary>
        /// The new state, taken from whichever control the user clicked.
        /// </summary>
        /// <remarks>
        /// The menu item and the media browser's checkbox share these handlers, and only
        /// the one that was clicked has moved by the time they run.
        /// </remarks>
        private static bool WriteProtectRequested(object sender, MenuItem menu) =>
            sender is CheckBox box ? box.IsChecked == true : menu.IsChecked;
        private void OnBetaDiskBWriteProtectToggle(object sender, RoutedEventArgs e)
        {
            ToggleTrdDiskWriteProtect(1, WriteProtectRequested(sender, BetaDiskBWriteProtectMenu));
        }
        private void OnDiskTraceToggle(object sender, RoutedEventArgs e)
        {
            _fdcTraceEnabled = DiskTraceMenu.IsChecked;
            ApplyFdcTraceConfiguration();
        }
        private void TogglePlus3DiskWriteProtect(int drive, bool writeProtected)
        {
            Plus3DiskImage? diskImage = GetDiskImage(drive);
            if (diskImage == null)
            {
                UpdateDiskMenuState();
                return;
            }

            diskImage.IsWriteProtected = writeProtected;
            _plus3DiskController?.SetDriveWriteProtected(drive, writeProtected);

            UpdateDiskMenuState();
        }
        private void ToggleTrdDiskWriteProtect(int drive, bool writeProtected)
        {
            TrdDiskImage? trdImage = GetTrdDiskImage(drive);
            if (trdImage == null)
            {
                UpdateDiskMenuState();
                return;
            }

            if (!trdImage.SupportsRawWriteback && !writeProtected)
            {
                // An image this format cannot write back stays protected, and both
                // controls have to go back to showing that.
                trdImage.IsWriteProtected = true;
                UpdateDiskMenuState();
                return;
            }

            trdImage.IsWriteProtected = writeProtected;
            UpdateDiskMenuState();
        }
        private void ApplyFdcTraceConfiguration()
        {
            _plus3DiskController?.ConfigureTracing(_fdcTraceEnabled, _fdcTraceEnabled ? GetFdcTracePath() : null);
            _betaDiskController?.ConfigureTracing(_fdcTraceEnabled, _fdcTraceEnabled ? GetBetaFdcTracePath() : null);
        }
        private void OnTapePlay(object sender, RoutedEventArgs e)
        {
            if (_zx8xMachine != null)
            {
                _zx8xMachine.CassetteMonitorEnabled = false;
                _zx8xMachine.Tape.Play(_zx8xMachine.Cpu.Cyc);
            }
            else
            {
                _tapeLoader?.Play();
            }
            RefreshTapeFastRunMode();
            UpdateTapeUi();
        }
        private void OnTapeStop(object sender, RoutedEventArgs e)
        {
            if (_zx8xMachine != null)
            {
                _zx8xMachine.Tape.Stop(_zx8xMachine.Cpu.Cyc);
            }
            else
            {
                _tapeLoader?.Stop();
            }
            RefreshTapeFastRunMode();
            UpdateTapeUi();
        }
        private void OnTapeRewind(object sender, RoutedEventArgs e)
        {
            RewindTapePlayback(updateUi: true);
        }
        private void RewindTapePlayback(bool updateUi)
        {
            if (_tapeLoader == null)
            {
                return;
            }

            if (_zx8xMachine != null)
            {
                _zx8xMachine.Tape.Rewind(_zx8xMachine.Cpu.Cyc);
            }
            else
            {
                _tapeLoader.Reset();
            }
            RefreshTapeFastRunMode();
            if (updateUi)
            {
                UpdateTapeUi();
            }
        }
        private void OnTapeEject(object sender, RoutedEventArgs e)
        {
            ClearTape();
        }
        private void OnTapeBlockDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_tapeLoader == null)
            {
                return;
            }

            if (TapeBlocksList.SelectedItem is not BlockInfo info)
            {
                return;
            }

            if (_zx8xMachine != null)
            {
                _zx8xMachine.Tape.SeekToBlock(info.Index, _zx8xMachine.Cpu.Cyc);
            }
            else
            {
                _tapeLoader.SeekToBlock(info.Index);
            }
            RefreshTapeFastRunMode();
            UpdateTapeUi();
        }
        private void LoadTapeFile(string path, bool allowAutoLoad = true)
        {
            if (_zxNextMachine != null)
            {
                MessageBox.Show(
                    "Tape media is not connected to the experimental ZX Spectrum Next host yet.",
                    "ZX Spectrum Next Tape",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_zx8xMachine != null)
            {
                if (!string.Equals(Path.GetExtension(path), ".tzx", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        "ZX80/ZX81 cassette playback currently accepts TZX images. Use .o, .p or .81 for direct program loading.",
                        "ZX80/ZX81 Tape",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                _session.EjectTape();
                _zx8xMachine.Tape.LoadTzx(path, _zx8xMachine.Cpu.Cyc);
                RefreshTapeAttachmentUi();
                return;
            }

            _session.LoadTape(path);
            RefreshTapeAttachmentUi();
            RefreshTapeFastRunMode();
            if (allowAutoLoad)
            {
                TryStartAutoLoadAfterTapeAttach();
            }
        }
        /// <summary>Rebuilds only the WPF tape-browser projection from portable session state.</summary>
        private void RefreshTapeAttachmentUi()
        {
            _lastTapeBlock = -1;
            _tapeBlocks.Clear();

            TzxLoader? loader = _tapeLoader;
            string? path = _tapePath;
            if (loader == null || path == null)
            {
                ClearTapeUi();
                return;
            }

            for (int i = 0; i < loader.Blocks.Count; i++)
            {
                _tapeBlocks.Add(new BlockInfo(i, loader.Blocks[i]));
            }

            TapeFileText.Text = $"Tape: {Path.GetFileName(path)}";
            TapeStatusText.Text = $"Tape: {Path.GetFileName(path)}";
            UpdateTapeUi();
            SetTapeControlsEnabled(true);
        }
        private void TryStartAutoLoadAfterTapeAttach()
        {
            if (!_autoLoadTapeOnAttach || _tapeLoader == null || _tapePath == null)
            {
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                return;
            }

            if (_divExpansionMode != SpectrumDivExpansionMode.Disabled)
            {
                return;
            }

            if (!TryCreateAutoLoadProfile(
                    out byte[] command,
                    out ushort readyPc,
                    out int? expectedRomBank,
                    out int initialDelayFrames,
                    out int keySpacingFrames,
                    out bool ejectPlus3Disks))
            {
                return;
            }

            if (!TryLoadRoms(_model, out RomSet roms, out string error))
            {
                MessageBox.Show(error, "ROM Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (ejectPlus3Disks)
            {
                EjectPlus3DisksForTapeAutoLoad();
            }

            // Reset through the real ROM, then feed its LAST_K protocol only after the selected
            // model reaches a known keyboard-reading PC. This preserves each ROM's boot process.
            InitializeMachine(_model, roms, null, preserveTape: true, rewindTape: true);
            _autoLoadInjector = new AutoLoadKeyboardInjector(
                _cpu,
                _memory,
                readyPc,
                expectedRomBank,
                command,
                initialDelayFrames * _tstatesPerFrame,
                keySpacingFrames * _tstatesPerFrame);
            UpdateCpuStepHooks();
        }
        private bool TryCreateAutoLoadProfile(
            out byte[] command,
            out ushort readyPc,
            out int? expectedRomBank,
            out int initialDelayFrames,
            out int keySpacingFrames,
            out bool ejectPlus3Disks)
        {
            bool codeHeader = TryGetFirstStandardTapeBlock(out TapeStandardBlock block) && IsCodeHeader(block.Data);
            readyPc = AutoLoad48KReadyPc;
            expectedRomBank = 0;
            command = AutoLoadBasic48Command;
            initialDelayFrames = AutoLoadDefaultInitialDelayFrames;
            keySpacingFrames = AutoLoadDefaultKeySpacingFrames;
            ejectPlus3Disks = false;

            switch (_model)
            {
                case SpectrumModel.Spectrum16K:
                case SpectrumModel.Spectrum48K:
                    command = codeHeader ? AutoLoadCode48Command : AutoLoadBasic48Command;
                    return true;

                case SpectrumModel.Spectrum128K:
                case SpectrumModel.Pentagon128:
                    readyPc = AutoLoad128ReadyPc;
                    command = codeHeader ? AutoLoadCode128Command : AutoLoadEnterCommand;
                    initialDelayFrames = _model == SpectrumModel.Pentagon128
                        ? AutoLoadPentagonInitialDelayFrames
                        : AutoLoadDefaultInitialDelayFrames;
                    return true;

                case SpectrumModel.Scorpion256:
                    expectedRomBank = null;
                    initialDelayFrames = 0;
                    keySpacingFrames = 0;
                    ejectPlus3Disks = false;
                    return false;

                case SpectrumModel.SpectrumPlus2:
                    readyPc = AutoLoadPlus2ReadyPc;
                    command = codeHeader ? AutoLoadCode128Command : AutoLoadEnterCommand;
                    return true;

                case SpectrumModel.SpectrumPlus2A:
                case SpectrumModel.SpectrumPlus3:
                    readyPc = AutoLoadPlus3ReadyPc;
                    command = AutoLoadEnterCommand;
                    initialDelayFrames = AutoLoadPlus3InitialDelayFrames;
                    ejectPlus3Disks = true;
                    return true;

                default:
                    expectedRomBank = null;
                    initialDelayFrames = 0;
                    keySpacingFrames = 0;
                    ejectPlus3Disks = false;
                    return false;
            }
        }
        private bool TryGetFirstStandardTapeBlock(out TapeStandardBlock block)
        {
            block = default;
            if (_tapeLoader == null)
            {
                return false;
            }

            for (int i = 0; i < _tapeLoader.Blocks.Count; i++)
            {
                ITzxBlock tzxBlock = _tapeLoader.Blocks[i];
                switch (tzxBlock)
                {
                    case StdData std:
                        block = new TapeStandardBlock(i, std.Data);
                        return true;
                    case TapBlock tap:
                        block = new TapeStandardBlock(i, tap.Data);
                        return true;
                }
            }

            return false;
        }
        private static bool IsCodeHeader(ReadOnlySpan<byte> data)
        {
            return data.Length == 19 && data[0] == 0x00 && data[1] == 0x03;
        }
        private void LoadDiskFile(string path)
        {
            LoadDiskFile(path, 0);
        }
        private void LoadDiskFile(string path, int drive)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".trd")
            {
                TrdDiskImage trdImage = TrdDiskImage.Load(path);
                InsertTrdDiskImage(trdImage, path, drive, true);
                return;
            }

            if (ext == ".scl")
            {
                TrdDiskImage trdImage = TrdDiskImage.LoadScl(path);
                InsertTrdDiskImage(trdImage, path, drive, true);
                return;
            }

            Plus3DiskImage plus3Image = Plus3DiskImage.Load(path);
            InsertDiskImage(plus3Image, path, drive, true);
        }
        private void InsertDiskImage(Plus3DiskImage image, string path, int drive, bool showDeferredMessage)
        {
            SetDiskImage(drive, image, path);

            _plus3DiskController?.InsertDisk(drive, image);

            UpdateDiskMenuState();

            if (showDeferredMessage && _model != SpectrumModel.SpectrumPlus3)
            {
                MessageBox.Show(
                    "Disk image loaded. It will be inserted when the Spectrum +3 model is selected.",
                    "Disk Image",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        private void InsertTrdDiskImage(TrdDiskImage image, string path, int drive, bool showDeferredMessage)
        {
            SetTrdDiskImage(drive, image, path);

            _betaDiskController?.InsertDisk(drive, image);

            UpdateDiskMenuState();

            if (showDeferredMessage && !SpectrumModelTraits.HasBeta128Disk(_model))
            {
                MessageBox.Show(
                    "TR-DOS disk image loaded. It will be inserted when Pentagon or Scorpion mode is selected.",
                    "Disk Image",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        private Plus3DiskImage? GetDiskImage(int drive)
        {
            return drive == 0 ? _diskImage : _diskImageB;
        }
        private string? GetDiskPath(int drive)
        {
            return drive == 0 ? _diskPath : _diskPathB;
        }
        private TrdDiskImage? GetTrdDiskImage(int drive)
        {
            return drive == 0 ? _trdDiskImage : _trdDiskImageB;
        }
        private string? GetTrdDiskPath(int drive)
        {
            return drive == 0 ? _trdDiskPath : _trdDiskPathB;
        }
        private void SetDiskImage(int drive, Plus3DiskImage? image, string? path)
        {
            _session.Disks.SetPlus3(drive, image, path);
        }
        private void SetTrdDiskImage(int drive, TrdDiskImage? image, string? path)
        {
            _session.Disks.SetTrd(drive, image, path);
        }
        private void ClearTape()
        {
            _autoLoadInjector = null;
            if (_zx8xMachine != null)
            {
                _zx8xMachine.Tape.Eject(_zx8xMachine.Cpu.Cyc);
            }
            else
            {
                _session.EjectTape();
            }
            ClearTapeUi();
            RefreshTapeFastRunMode();
        }
        private void ClearTapeUi()
        {
            _lastTapeBlock = -1;
            _tapeBlocks.Clear();

            TapeFileText.Text = "Tape: (none)";
            TapeStatusText.Text = "Tape: none";
            TapeBlockText.Text = "No tape loaded";
            TapeBlockProgress.Value = 0;
            SyncTapeBlockSelection(-1, scrollIntoView: false);
            SetTapeControlsEnabled(false);
        }
        private void UpdateTapeUi()
        {
            RefreshTapeFastRunMode();

            if (_tapeLoader == null || _tapeBlocks.Count == 0)
            {
                TapeBlockProgress.Value = 0;
                TapeBlockText.Text = "No tape loaded";
                return;
            }

            int blockIndex = _tapeLoader.CurrentBlockIndex;
            int totalBlocks = _tapeBlocks.Count;

            if (blockIndex < 0 || blockIndex >= totalBlocks)
            {
                TapeBlockProgress.Value = 0;
                TapeBlockText.Text = $"Block --/{totalBlocks}";
                SyncTapeBlockSelection(-1, scrollIntoView: false);
                return;
            }

            double elapsedSeconds = _tapeLoader.CurrentBlockElapsedSeconds;
            double durationSeconds = _tapeLoader.CurrentBlockDurationSeconds;
            double progress = durationSeconds > 0 ? Math.Clamp(elapsedSeconds / durationSeconds, 0, 1) : _tapeLoader.CurrentBlockProgress;
            int percent = (int)Math.Round(progress * 100);

            TapeBlockProgress.Value = progress;

            BlockInfo info = _tapeBlocks[blockIndex];
            string label = string.IsNullOrWhiteSpace(info.FileName) ? info.Type : info.FileName;
            TapeBlockText.Text = $"Block {blockIndex + 1}/{totalBlocks}: {label} ({elapsedSeconds:0.00}s / {durationSeconds:0.00}s, {percent}%)";

            if (blockIndex != _lastTapeBlock)
            {
                _lastTapeBlock = blockIndex;
                SyncTapeBlockSelection(blockIndex, scrollIntoView: true);
            }
        }
        private void SyncTapeBlockSelection(int blockIndex, bool scrollIntoView)
        {
            if (!_tapeBrowserVisible || TapeBlocksList == null)
            {
                return;
            }

            if (blockIndex < 0 || blockIndex >= _tapeBlocks.Count)
            {
                TapeBlocksList.SelectedIndex = -1;
                return;
            }

            TapeBlocksList.SelectedIndex = blockIndex;
            if (scrollIntoView && TapeBlocksList.SelectedItem != null)
            {
                TapeBlocksList.ScrollIntoView(TapeBlocksList.SelectedItem);
            }
        }
        private void OnTapePlaybackStopped(object? sender, TapeStopReason reason)
        {
            if (!_turboEnabled)
            {
                StopTapeFastRunner();
            }

            _uiDispatcher.TryPost(() =>
            {
                if (_turboEnabled)
                {
                    SetTurboMode(false);
                }

                if (reason == TapeStopReason.EndOfTape)
                {
                    RewindTapePlayback(updateUi: false);
                }

                RefreshTapeFastRunMode();
                UpdateTapeUi();
            });
        }
        private void OnFlashLoadToggle(object sender, RoutedEventArgs e)
        {
            _flashLoadEnabled = FlashLoadMenu.IsChecked;
            UpdateCpuStepHooks();
        }
        private void OnFlashSaveToggle(object sender, RoutedEventArgs e)
        {
            _flashSaveEnabled = FlashSaveMenu.IsChecked;
            UpdateCpuStepHooks();
        }

        /// <summary>
        /// Runs whichever tape traps are armed. Saving is only taken over while the
        /// recorder is running, so with recording off the machine saves for real.
        /// </summary>
        private bool TryTapeTraps()
        {
            // A replay reproduces a recorded run; a trap that injects a tape block or
            // completes a save would make it a different run.
            if (_replaySession.Mode != SpectrumReplayMode.Idle)
            {
                return false;
            }

            if (_flashLoadEnabled && TryFlashLoad())
            {
                return true;
            }

            return _flashSaveEnabled
                && _tapeRecorder.IsRecording
                && _machine != null
                && SpectrumRomTapeSaver.TryFastSave(_machine, _tapeRecorder);
        }
        private void OnTapeRecordToggle(object sender, RoutedEventArgs e)
        {
            if (TapeRecordMenu.IsChecked)
            {
                _tapeRecorder.Start();
                StatusText.Text = "Recording tape output";
            }
            else
            {
                _tapeRecorder.Stop();
            }

            UpdateTapeMenuChecks();
            Focus();
        }
        private async void OnTapeSaveRecording(object sender, RoutedEventArgs e)
        {
            _tapeRecorder.Stop();
            TapeRecordMenu.IsChecked = false;

            if (!_tapeRecorder.HasRecording)
            {
                MessageBox.Show(
                    "Nothing has been recorded yet. Switch on tape recording, then save from the machine.",
                    "Tape Recording",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Focus();
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
                    new FileDialogFilter("All Files", "*.*")
                ]
            });
            if (path == null)
            {
                Focus();
                return;
            }

            try
            {
                bool tzx = string.Equals(System.IO.Path.GetExtension(path), ".tzx", StringComparison.OrdinalIgnoreCase);
                byte[] image = tzx ? _tapeRecorder.BuildTzx() : _tapeRecorder.BuildTap();
                System.IO.File.WriteAllBytes(path, image);
                StatusText.Text = DescribeRecording(tzx);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Tape Recording", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            UpdateTapeMenuChecks();
            Focus();
        }

        /// <summary>
        /// Says what was written, and what a TAP could not hold.
        /// </summary>
        private string DescribeRecording(bool tzx)
        {
            int standard = _tapeRecorder.StandardBlockCount;
            int raw = _tapeRecorder.Blocks.Count - standard;
            string written = $"Saved {standard} block{(standard == 1 ? string.Empty : "s")}";
            if (raw == 0)
            {
                return written;
            }

            return tzx
                ? $"{written} plus {raw} raw pulse run{(raw == 1 ? string.Empty : "s")}"
                : $"{written}; {raw} run{(raw == 1 ? string.Empty : "s")} of undecoded pulses need a TZX";
        }
        private void OnReplayRecordToggle(object sender, RoutedEventArgs e)
        {
            if (ReplayRecordMenu.IsChecked)
            {
                _replaySession.StartRecording();
                StatusText.Text = "Recording input";
            }
            else
            {
                _replaySession.Stop();
                StatusText.Text = $"Recorded {_replaySession.FrameCount} frames";
            }

            UpdateReplayMenuState();
            Focus();
        }
        private async void OnReplaySaveRecording(object sender, RoutedEventArgs e)
        {
            _replaySession.Stop();
            ReplayRecordMenu.IsChecked = false;
            if (!_replaySession.HasRecording)
            {
                MessageBox.Show(
                    "Nothing has been recorded yet.",
                    "Replay",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Focus();
                return;
            }

            string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
            {
                Title = "Save Input Recording",
                DefaultExtension = ".rzx",
                SuggestedFileName = $"{_model}.rzx",
                Filters =
                [
                    new FileDialogFilter("RZX input recordings", "*.rzx"),
                    new FileDialogFilter("All Files", "*.*")
                ]
            });
            if (path == null)
            {
                Focus();
                return;
            }

            try
            {
                RzxFile.Write(path, _replaySession.BuildRecording());
                StatusText.Text = $"Saved {_replaySession.FrameCount} recorded frames";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Replay", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            UpdateReplayMenuState();
            Focus();
        }
        private async void OnReplayPlay(object sender, RoutedEventArgs e)
        {
            string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
            {
                Title = "Play Input Recording",
                DefaultExtension = ".rzx",
                Filters =
                [
                    new FileDialogFilter("RZX input recordings", "*.rzx"),
                    new FileDialogFilter("All Files", "*.*")
                ]
            });
            if (path == null)
            {
                Focus();
                return;
            }

            EmulationRunState runState = SuspendEmulationForModal();

            // Hold the machine still for the whole of this. Reading the file, decoding
            // its snapshot and rebuilding the machine all write state the processor is
            // otherwise free to be executing against, and a recording that turns out to
            // be unloadable would leave it half-changed.
            bool wasPaused = _emulator?.IsPaused == true;
            SetEmulationPaused(true);
            try
            {
                RzxRecording recording = RzxFile.Read(path);

                // The snapshot inside decides the model, so the machine may have to be
                // rebuilt before the recording can start.
                SpectrumModel required = SpectrumReplaySession.DetectModel(recording);
                if (required != _model)
                {
                    if (!TryLoadRoms(required, out RomSet roms, out string romError))
                    {
                        MessageBox.Show(romError, "ROM Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    InitializeMachine(required, roms, null, preserveTape: false);

                    // A different machine: the one just built has to be held still too.
                    SetEmulationPaused(true);
                }

                _replaySession.StartPlayback(recording);
                StatusText.Text = $"Replaying {_replaySession.FrameCount} frames";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Replay", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetEmulationPaused(wasPaused);
                RestoreEmulationAfterModal(runState);
            }

            UpdateReplayMenuState();
            Focus();
        }
        private void OnReplayStop(object sender, RoutedEventArgs e)
        {
            _replaySession.Stop();
            ReplayRecordMenu.IsChecked = false;
            StatusText.Text = "Replay stopped";
            UpdateReplayMenuState();
            Focus();
        }
        private void OnReplayStepBack(object sender, RoutedEventArgs e)
        {
            StepReplayBack();
            Focus();
        }

        /// <summary>
        /// Steps the machine a tenth of a second into the past.
        /// </summary>
        /// <remarks>
        /// Most useful with emulation paused, which is how a run is built up a step at a
        /// time: pause, step back, try the input again.
        /// </remarks>
        private bool StepReplayBack()
        {
            if (!_replaySession.StepBack())
            {
                StatusText.Text = "No rewind history to step back through";
                return false;
            }

            StatusText.Text = _replaySession.Mode == SpectrumReplayMode.Recording
                ? $"Stepped back - {_replaySession.FrameCount} frames recorded, {_replaySession.RewindSecondsAvailable:F1}s of history left"
                : $"Stepped back - {_replaySession.RewindSecondsAvailable:F1}s of history left";
            UpdateReplayMenuState();
            return true;
        }
        private void OnReplayRewindEnabledToggle(object sender, RoutedEventArgs e)
        {
            _replaySession.RewindEnabled = ReplayRewindEnabledMenu.IsChecked;
            UpdateReplayMenuState();
            Focus();
        }
        /// <summary>
        /// Shows how far a recording has got and how much of it is left.
        /// </summary>
        /// <remarks>
        /// A recording is a list of frames, so its length is a frame count divided by the
        /// machine's frame rate - which is not quite 50 Hz, and differs between a 48K and
        /// a +3, so it is taken from the running machine rather than assumed.
        /// </remarks>
        private void UpdateReplayStatusText()
        {
            switch (_replaySession.Mode)
            {
                case SpectrumReplayMode.Playing:
                    int played = _replaySession.PlaybackFrame;
                    int total = _replaySession.FrameCount;
                    ReplayStatusText.Text =
                        $"Replay {FormatFrameCount(played)} / {FormatFrameCount(total)}"
                            + $"  ({FormatFrameCount(total - played)} left)";
                    return;

                case SpectrumReplayMode.Recording:
                    ReplayStatusText.Text = $"Recording {FormatFrameCount(_replaySession.FrameCount)}";
                    return;

                default:
                    ReplayStatusText.Text = _replaySession.HasRecording
                        ? $"Recorded {FormatFrameCount(_replaySession.FrameCount)}"
                        : string.Empty;
                    return;
            }
        }

        /// <summary>Frames as minutes and seconds at the running machine's frame rate.</summary>
        private string FormatFrameCount(int frames)
        {
            int tstatesPerFrame = SpectrumUlaTiming.ForModel(_model).TstatesPerFrame;
            double seconds = frames * (double)tstatesPerFrame / SpectrumCpuHz;
            var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes}:{span.Seconds:00}";
        }

        /// <summary>The Spectrum's CPU clock, which every supported model shares.</summary>
        private const double SpectrumCpuHz = 3_500_000.0;

        private void UpdateReplayMenuState()
        {
            ReplayRecordMenu.IsChecked = _replaySession.Mode == SpectrumReplayMode.Recording
                || _replaySession.IsWaitingToRecord;
            ReplaySaveMenu.IsEnabled = _replaySession.HasRecording;
            ReplayStopMenu.IsEnabled = _replaySession.Mode != SpectrumReplayMode.Idle;
            ReplayStepBackMenu.IsEnabled = _replaySession.RewindDepth > 0;
            ReplayRewindEnabledMenu.IsChecked = _replaySession.RewindEnabled;
            if (_replaySession.DesyncReason != null)
            {
                StatusText.Text = $"Replay stopped: {_replaySession.DesyncReason}";
            }
        }
        private void OnTapeDiscardRecording(object sender, RoutedEventArgs e)
        {
            _tapeRecorder.Stop();
            _tapeRecorder.Clear();
            TapeRecordMenu.IsChecked = false;
            UpdateTapeMenuChecks();
            Focus();
        }
        private void OnEdgeLoadToggle(object sender, RoutedEventArgs e)
        {
            _edgeLoadEnabled = EdgeLoadMenu.IsChecked;
            if (_earInput != null)
            {
                _earInput.EdgeLoadingEnabled = _edgeLoadEnabled;
            }

            RefreshTapeFastRunMode();
        }
        private void OnSemanticEdgeLoadToggle(object sender, RoutedEventArgs e)
        {
            _semanticEdgeLoadEnabled = SemanticEdgeLoadMenu.IsChecked;
            if (_earInput != null)
            {
                _earInput.SemanticAccelerationEnabled = _semanticEdgeLoadEnabled;
                RefreshTapeFastRunMode();
            }
        }
        private void OnTapeAccelerationMaxSpeedToggle(object sender, RoutedEventArgs e)
        {
            _runTapeAccelerationAtMaximumSpeed = TapeAccelerationMaxSpeedMenu.IsChecked;
            RefreshTapeFastRunMode();
        }
        private void OnAutoLoadTapeToggle(object sender, RoutedEventArgs e)
        {
            _autoLoadTapeOnAttach = AutoLoadTapeMenu.IsChecked;
            UpdateTapeMenuChecks();
        }
        private void OnAutoTapePlayToggle(object sender, RoutedEventArgs e)
        {
            _autoTapePlayStopEnabled = AutoTapePlayMenu.IsChecked;
            if (_earInput != null)
            {
                _earInput.AutoPlayEnabled = _autoTapePlayStopEnabled;
            }

            UpdateCpuStepHooks();
            UpdateTapeMenuChecks();
        }
        private void OnDirtyLinePresentationToggle(object sender, RoutedEventArgs e)
        {
            UseDirtyLinePresentation = DirtyLinePresentationMenu.IsChecked;
            ApplyPresentationCopyMode();
        }
        private void OnGigascreenBlendToggle(object sender, RoutedEventArgs e)
        {
            _gigascreenBlendEnabled = GigascreenBlendMenu.IsChecked;
            _gigascreenHasPreviousFrame = false;
            ApplyPresentationCopyMode();
        }
        private void ApplyPresentationCopyMode()
        {
            if (_emulator != null)
            {
                _emulator.ForceFullFrameCopy = _gigascreenBlendEnabled || !UseDirtyLinePresentation;
            }
        }
        private void OnTapeAutoPlayRequested()
        {
            if (!_autoTapePlayStopEnabled)
            {
                return;
            }

            TryStartTapeMotorFromAutoPlay();
        }
        private bool TryStartTapeForRomLoader()
        {
            if (!_autoTapePlayStopEnabled || !IsLdBytesEntry())
            {
                return false;
            }

            return TryStartTapeMotorFromAutoPlay();
        }
        private bool TryStartTapeMotorFromAutoPlay()
        {
            TzxLoader? loader = _tapeLoader;
            if (loader == null || loader.IsPlaying)
            {
                return false;
            }

            loader.Play();
            QueueTapeRuntimeRefresh();
            return true;
        }
        private void StopTapeMotorFromAutoPlay()
        {
            TzxLoader? loader = _tapeLoader;
            if (loader == null || !loader.IsPlaying)
            {
                return;
            }

            loader.Stop();
            QueueTapeRuntimeRefresh();
        }
        private void QueueTapeRuntimeRefresh()
        {
            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.TryPost(QueueTapeRuntimeRefresh);
                return;
            }

            RefreshTapeFastRunMode();
            UpdateTapeUi();
        }
        private bool TryFlashLoad()
        {
            if (!_flashLoadEnabled || _tapeLoader == null)
            {
                return false;
            }

            if (!IsLdBytesEntry())
            {
                return false;
            }

            int blockIndex = _tapeLoader.CurrentBlockIndex;
            if (!TryGetStandardBlockAt(blockIndex, out TapeStandardBlock block))
            {
                return false;
            }

            ushort de = (ushort)((_cpu.D << 8) | _cpu.E);
            if (block.Data.Length != de + 2)
            {
                return false;
            }

            if (_tapeLoader.IsPlaying)
            {
                _tapeLoader.Stop();
            }

            ExecuteFlashLoad(block.Data, de);
            _cpu.PC = 0x05E2;
            _cpu.SetHalted(false);

            int nextBlock = block.Index + 1;
            if (nextBlock < _tapeLoader.Blocks.Count)
            {
                _tapeLoader.JumpToNextPlayableBlock(nextBlock, play: true);
            }

            _uiDispatcher.TryPost(UpdateTapeUi, UiDispatchPriority.Background);
            return true;
        }
        private bool TryGetStandardBlockAt(int index, out TapeStandardBlock block)
        {
            block = default;
            if (_tapeLoader == null)
            {
                return false;
            }

            if (_tapeLoader.Blocks.Count == 0)
            {
                return false;
            }

            int start = Math.Clamp(index, 0, _tapeLoader.Blocks.Count - 1);
            for (int i = start; i < _tapeLoader.Blocks.Count; i++)
            {
                ITzxBlock blk = _tapeLoader.Blocks[i];
                switch (blk)
                {
                    case StdData std:
                        return ReturnStandardBlock(i, std.Data, out block);
                    case TapBlock tap:
                        return ReturnStandardBlock(i, tap.Data, out block);
                }
            }

            return false;
        }
        private static bool ReturnStandardBlock(int index, byte[] data, out TapeStandardBlock block)
        {
            block = new TapeStandardBlock(index, data);
            return true;
        }
        private void ExecuteFlashLoad(ReadOnlySpan<byte> data, ushort de)
        {
            int length = data.Length;
            int read = length - 1;
            if (read > de)
            {
                read = de;
            }

            if (length == 0)
            {
                _cpu.L = 1;
                _cpu.F_ = 1;
                ClearCarry();
                return;
            }

            bool verify = (_cpu.F_ & 0x01) == 0;
            byte flag = _cpu.A_;

            _cpu.A = 0;
            byte parity = data[0];
            _cpu.L = parity;

            if (de == 0)
            {
                _cpu.B = 0xB0;
                _cpu.A = parity;
                _cpu.SetFlags(CpFlags(_cpu.A, 1));
                _cpu.C = 1;
                _cpu.H = parity;
                return;
            }

            _cpu.A_ = 0x01;
            _cpu.F_ = 0x45;

            bool error = false;
            int bytesRead = 0;

            if (parity != flag)
            {
                error = true;
            }
            else
            {
                if (read > 0)
                {
                    _cpu.L = data[read];
                }

                if (verify)
                {
                    for (int i = 0; i < read; i++)
                    {
                        byte value = data[i + 1];
                        parity ^= value;
                        if (value != _memory.ReadDirect((ushort)(_cpu.IX + i)))
                        {
                            _cpu.L = value;
                            error = true;
                            break;
                        }

                        bytesRead = i + 1;
                    }
                }
                else
                {
                    for (int i = 0; i < read; i++)
                    {
                        byte value = data[i + 1];
                        parity ^= value;
                        _memory.WriteDirect((ushort)(_cpu.IX + i), value);
                        bytesRead = i + 1;
                    }
                }

                if (!error)
                {
                    if (de == bytesRead && read + 1 < length)
                    {
                        parity ^= data[read + 1];
                        _cpu.A = parity;
                        _cpu.SetFlags(CpFlags(_cpu.A, 1));
                        _cpu.B = 0xB0;
                    }
                    else
                    {
                        _cpu.B = 0xFF;
                        _cpu.L = 1;
                        _cpu.B++;
                        error = true;
                    }
                }
            }

            if (error)
            {
                ClearCarry();
            }

            _cpu.C = 1;
            _cpu.H = parity;
            ushort newDe = (ushort)(de - bytesRead);
            _cpu.D = (byte)(newDe >> 8);
            _cpu.E = (byte)newDe;
            _cpu.IX = (ushort)(_cpu.IX + bytesRead);
        }
        private void ClearCarry()
        {
            byte flags = _cpu.GetFlags();
            _cpu.SetFlags((byte)(flags & 0xFE));
        }
        private static byte CpFlags(byte a, byte value)
        {
            int result = a - value;
            byte r = (byte)result;
            byte f = 0;
            if ((r & 0x80) != 0) f |= 0x80;
            if (r == 0) f |= 0x40;
            if (((a ^ value ^ r) & 0x10) != 0) f |= 0x10;
            if (((a ^ value) & (a ^ r) & 0x80) != 0) f |= 0x04;
            f |= 0x02;
            if (result < 0) f |= 0x01;
            f |= (byte)(r & 0x28);
            return f;
        }
        private bool IsLdBytesEntry()
        {
            ushort pc = _cpu.PC;
            if (pc < 0x0558 || pc > 0x0567)
            {
                return false;
            }

            ushort start = 0x0557;
            for (int i = 0; i < LdBytesPrefix.Length; i++)
            {
                if (_memory.ReadDirect((ushort)(start + i)) != LdBytesPrefix[i])
                {
                    return false;
                }
            }

            for (int i = 0; i < LdBytesSuffix.Length; i++)
            {
                if (_memory.ReadDirect((ushort)(start + 10 + i)) != LdBytesSuffix[i])
                {
                    return false;
                }
            }

            return true;
        }
        private void SetTapeControlsEnabled(bool enabled)
        {
            TapePlayButton.IsEnabled = enabled;
            TapeStopButton.IsEnabled = enabled;
            TapeRewindButton.IsEnabled = enabled;
            TapeEjectButton.IsEnabled = enabled;

            TapePlayMenu.IsEnabled = enabled;
            TapeStopMenu.IsEnabled = enabled;
            TapeRewindMenu.IsEnabled = enabled;
            TapeEjectMenu.IsEnabled = enabled;
        }
        private void UpdateModelMenuChecks()
        {
            bool spectrumSelected = _zx8xMachine == null && _zxNextMachine == null;
            Model16KMenu.IsChecked = spectrumSelected && _model == SpectrumModel.Spectrum16K;
            Model48KMenu.IsChecked = spectrumSelected && _model == SpectrumModel.Spectrum48K;
            Model128KMenu.IsChecked = spectrumSelected && _model == SpectrumModel.Spectrum128K;
            ModelPlus2Menu.IsChecked = spectrumSelected && _model == SpectrumModel.SpectrumPlus2;
            ModelPlus2AMenu.IsChecked = spectrumSelected && _model == SpectrumModel.SpectrumPlus2A;
            ModelPlus3Menu.IsChecked = spectrumSelected && _model == SpectrumModel.SpectrumPlus3;
            ModelPentagon128Menu.IsChecked = spectrumSelected && _model == SpectrumModel.Pentagon128;
            ModelScorpion256Menu.IsChecked = spectrumSelected && _model == SpectrumModel.Scorpion256;
            ModelZx80Menu.IsChecked = _zx8xModel == Zx8x.Core.Zx8xModel.Zx80;
            ModelZx81Menu.IsChecked = _zx8xModel == Zx8x.Core.Zx8xModel.Zx81;
            ModelZxNextMenu.IsChecked = _zxNextMachine != null;
            Zx8xRam1KMenu.IsChecked = _zx8xRamConfiguration == Zx8xRamConfiguration.Internal1K;
            Zx8xRam16KMenu.IsChecked = _zx8xRamConfiguration == Zx8xRamConfiguration.Expansion16K;
            Zx8xWrxMenu.IsChecked = _zx8xHighResolutionMode == Zx8xHighResolutionMode.Wrx;
        }
        private void UpdateJoystickMenuChecks()
        {
            JoystickNoneMenu.IsChecked = _joystickType == SpectrumJoystickType.None;
            JoystickKempstonMenu.IsChecked = _joystickType == SpectrumJoystickType.Kempston;
            JoystickSinclair1Menu.IsChecked = _joystickType == SpectrumJoystickType.Sinclair1;
            JoystickSinclair2Menu.IsChecked = _joystickType == SpectrumJoystickType.Sinclair2;
            JoystickCursorMenu.IsChecked = _joystickType == SpectrumJoystickType.Cursor;
        }
        private void UpdateDivExpansionMenuChecks()
        {
            DivDisabledMenu.IsChecked = _divExpansionMode == SpectrumDivExpansionMode.Disabled;
            DivMmcMenu.IsChecked = _divExpansionMode == SpectrumDivExpansionMode.DivMmc;
            DivNmiMenu.IsEnabled = _divExpansionMode != SpectrumDivExpansionMode.Disabled;
            UpdateQuickAccessState();
        }
        private void UpdateDivStorageMenuState()
        {
            bool divMmcEnabled = _divExpansionMode == SpectrumDivExpansionMode.DivMmc;
            DivStorageNewMenu.IsEnabled = divMmcEnabled;
            DivStorageInsertMenu.IsEnabled = divMmcEnabled;
            DivStorageFolderMenu.IsEnabled = divMmcEnabled;
            DivStorageImportFolderMenu.IsEnabled = divMmcEnabled && _divStoragePath != null && !_divStorageFolderBacked && !_divStorageWriteProtected;
            DivStorageEjectMenu.IsEnabled = divMmcEnabled && _divStoragePath != null;
            DivStorageWriteProtectMenu.IsEnabled = divMmcEnabled;
            DivStorageWriteProtectMenu.IsChecked = _divStorageWriteProtected;
            DivStorageInsertMenu.Header = _divStoragePath == null
                ? "_Insert Storage Image..."
                : _divStorageFolderBacked
                    ? "_Insert Storage Image..."
                    : $"_Insert Storage Image... ({Path.GetFileName(_divStoragePath)})";
            DivStorageFolderMenu.Header = _divStoragePath != null && _divStorageFolderBacked
                ? $"Use _Folder as Storage... ({Path.GetFileName(_divStoragePath)})"
                : "Use _Folder as Storage...";
            DivStorageEjectMenu.Header = _divStorageFolderBacked
                ? "_Eject Folder Storage"
                : "_Eject Storage Image";
        }
        private void UpdateDiskMenuState()
        {
            DiskTraceMenu.IsChecked = _fdcTraceEnabled;
            DiskSaveAsMenu.IsEnabled = _diskImage != null;
            DiskEjectMenu.IsEnabled = _diskImage != null;
            DiskWriteProtectMenu.IsEnabled = _diskImage != null;
            DiskWriteProtectMenu.IsChecked = _diskImage?.IsWriteProtected == true;
            DiskInsertMenu.Header = _diskPath == null
                ? "_Insert DSK..."
                : $"_Insert DSK... ({Path.GetFileName(_diskPath)})";

            DiskBSaveAsMenu.IsEnabled = _diskImageB != null;
            DiskBEjectMenu.IsEnabled = _diskImageB != null;
            DiskBWriteProtectMenu.IsEnabled = _diskImageB != null;
            DiskBWriteProtectMenu.IsChecked = _diskImageB?.IsWriteProtected == true;
            DiskBInsertMenu.Header = _diskPathB == null
                ? "_Insert DSK..."
                : $"_Insert DSK... ({Path.GetFileName(_diskPathB)})";

            BetaDiskSaveAsMenu.IsEnabled = _trdDiskImage != null;
            BetaDiskEjectMenu.IsEnabled = _trdDiskImage != null;
            BetaDiskWriteProtectMenu.IsEnabled = _trdDiskImage != null;
            BetaDiskWriteProtectMenu.IsChecked = _trdDiskImage?.IsWriteProtected == true;
            BetaDiskInsertMenu.Header = _trdDiskPath == null
                ? "_Insert TRD/SCL..."
                : $"_Insert TRD/SCL... ({Path.GetFileName(_trdDiskPath)})";

            BetaDiskBSaveAsMenu.IsEnabled = _trdDiskImageB != null;
            BetaDiskBEjectMenu.IsEnabled = _trdDiskImageB != null;
            BetaDiskBWriteProtectMenu.IsEnabled = _trdDiskImageB != null;
            BetaDiskBWriteProtectMenu.IsChecked = _trdDiskImageB?.IsWriteProtected == true;
            BetaDiskBInsertMenu.Header = _trdDiskPathB == null
                ? "_Insert TRD/SCL..."
                : $"_Insert TRD/SCL... ({Path.GetFileName(_trdDiskPathB)})";

            UpdateDriveBrowserState();
            UpdateDiskUi();
        }

        /// <summary>
        /// Mirrors the disk menus into the media browser's drive tabs.
        /// </summary>
        private void UpdateDriveBrowserState()
        {
            SetDriveTab(
                Plus3APathText, Plus3ASaveButton, Plus3AEjectButton, Plus3AWriteProtectCheck,
                _diskPath, _diskImage != null, _diskImage?.IsWriteProtected == true);
            SetDriveTab(
                Plus3BPathText, Plus3BSaveButton, Plus3BEjectButton, Plus3BWriteProtectCheck,
                _diskPathB, _diskImageB != null, _diskImageB?.IsWriteProtected == true);
            SetDriveTab(
                BetaAPathText, BetaASaveButton, BetaAEjectButton, BetaAWriteProtectCheck,
                _trdDiskPath, _trdDiskImage != null, _trdDiskImage?.IsWriteProtected == true);
            SetDriveTab(
                BetaBPathText, BetaBSaveButton, BetaBEjectButton, BetaBWriteProtectCheck,
                _trdDiskPathB, _trdDiskImageB != null, _trdDiskImageB?.IsWriteProtected == true);
        }

        private static void SetDriveTab(
            TextBlock pathText,
            Button saveButton,
            Button ejectButton,
            CheckBox writeProtect,
            string? path,
            bool inserted,
            bool writeProtected)
        {
            pathText.Text = inserted
                ? (path == null ? "(unsaved image)" : Path.GetFileName(path))
                : "(none)";
            saveButton.IsEnabled = inserted;
            ejectButton.IsEnabled = inserted;
            writeProtect.IsEnabled = inserted;
            writeProtect.IsChecked = writeProtected;
        }
        private void UpdateDiskUi()
        {
            string plus3A = FormatPlus3DiskStatus(_diskImage, _diskPath);
            string plus3B = FormatPlus3DiskStatus(_diskImageB, _diskPathB);
            string betaA = FormatTrdDiskStatus(_trdDiskImage, _trdDiskPath);
            string betaB = FormatTrdDiskStatus(_trdDiskImageB, _trdDiskPathB);

            DiskStatusText.Text = FormatDiskStatusText(plus3A, plus3B, betaA, betaB);

            SpectrumPlus3DiskController? controller = _plus3DiskController;
            SpectrumBeta128DiskController? betaController = _betaDiskController;
            if (controller == null && betaController == null)
            {
                DiskActivityLight.Fill = Brushes.Transparent;
                return;
            }

            long activity = controller?.ActivityCounter ?? betaController!.ActivityCounter;
            long now = Stopwatch.GetTimestamp();
            if (activity != _lastDiskActivityCounter)
            {
                _lastDiskActivityCounter = activity;
                _lastDiskActivityTimestamp = now;
            }

            double elapsedSeconds = _lastDiskActivityTimestamp == 0
                ? double.MaxValue
                : (now - _lastDiskActivityTimestamp) / (double)Stopwatch.Frequency;

            DiskActivityLight.Fill = elapsedSeconds <= DiskActivityHoldSeconds
                ? Brushes.LimeGreen
                : Brushes.DarkGreen;
        }
        private static string FormatDiskStatus(Plus3DiskImage image, string? path)
        {
            string writeProtect = image.IsWriteProtected ? " [RO]" : "";
            return $"{Path.GetFileName(path)}{writeProtect}";
        }
        private static string FormatPlus3DiskStatus(Plus3DiskImage? image, string? path)
        {
            return image == null ? "(none)" : FormatDiskStatus(image, path);
        }
        private static string FormatDiskStatus(TrdDiskImage image, string? path)
        {
            string writeProtect = image.IsWriteProtected ? " [RO]" : "";
            return $"{Path.GetFileName(path)}{writeProtect}";
        }
        private static string FormatTrdDiskStatus(TrdDiskImage? image, string? path)
        {
            return image == null ? "(none)" : FormatDiskStatus(image, path);
        }
        private static string FormatDiskStatusText(string plus3A, string plus3B, string betaA, string betaB)
        {
            bool hasPlus3 = plus3A != "(none)" || plus3B != "(none)";
            bool hasBeta = betaA != "(none)" || betaB != "(none)";
            if (!hasPlus3 && !hasBeta)
            {
                return "Disk: (none)";
            }

            if (hasPlus3 && hasBeta)
            {
                return $"+3 A: {plus3A} | B: {plus3B}{Environment.NewLine}Beta A: {betaA} | B: {betaB}";
            }

            return hasPlus3
                ? $"+3 A: {plus3A} | B: {plus3B}"
                : $"Beta A: {betaA} | B: {betaB}";
        }
        private static string GetFdcTracePath()
        {
            string? root = FindProjectRoot(AppContext.BaseDirectory);
            if (root != null)
            {
                return Path.Combine(root, "TEST", "plus3-fdc-trace.log");
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZedExEss",
                "plus3-fdc-trace.log");
        }
        private static string GetBetaFdcTracePath()
        {
            string? root = FindProjectRoot(AppContext.BaseDirectory);
            if (root != null)
            {
                return Path.Combine(root, "TEST", "beta128-fdc-trace.log");
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZedExEss",
                "beta128-fdc-trace.log");
        }
        private static string? FindProjectRoot(string startDirectory)
        {
            DirectoryInfo? directory = new(startDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ZedExEss.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return null;
        }
        private void UpdateTapeMenuChecks()
        {
            AutoLoadTapeMenu.IsChecked = _autoLoadTapeOnAttach;
            AutoTapePlayMenu.IsChecked = _autoTapePlayStopEnabled;
            FlashLoadMenu.IsChecked = _flashLoadEnabled;
            FlashSaveMenu.IsChecked = _flashSaveEnabled;
            TapeRecordMenu.IsChecked = _tapeRecorder.IsRecording;
            TapeSaveRecordingMenu.IsEnabled = _tapeRecorder.HasRecording;
            TapeDiscardRecordingMenu.IsEnabled = _tapeRecorder.HasRecording;
            EdgeLoadMenu.IsChecked = _edgeLoadEnabled;
            SemanticEdgeLoadMenu.IsChecked = _semanticEdgeLoadEnabled;
            TapeAccelerationMaxSpeedMenu.IsChecked = _runTapeAccelerationAtMaximumSpeed;
            if (_earInput != null)
            {
                _earInput.AutoPlayEnabled = _autoTapePlayStopEnabled;
                _earInput.EdgeLoadingEnabled = _edgeLoadEnabled;
                _earInput.SemanticAccelerationEnabled = _semanticEdgeLoadEnabled;
            }
        }
        private static bool TryLoadRoms(SpectrumModel model, out RomSet roms, out string error)
        {
            try
            {
                roms = model switch
                {
                    SpectrumModel.Pentagon128 => RomSet.LoadFromCombinedFile("ROMs\\pentagon.rom", SpectrumModelTraits.RomBankCount(model)),
                    SpectrumModel.Scorpion256 => RomSet.LoadFromCombinedFile("ROMs\\scorpion.rom", SpectrumModelTraits.RomBankCount(model)),
                    _ => RomSet.LoadFromFiles(GetRomPaths(model))
                };
                error = "";
                return true;
            }
            catch (Exception ex)
            {
                roms = RomSet.CreateBlank(GetRomBankCount(model));
                error = ex.Message;
                return false;
            }
        }
        private static bool TryCreateDivExpansion(SpectrumDivExpansionMode mode, out SpectrumDivMmcDevice? device, out string error)
        {
            device = null;
            error = "";

            if (mode == SpectrumDivExpansionMode.Disabled)
            {
                return true;
            }

            if (mode != SpectrumDivExpansionMode.DivMmc)
            {
                error = "Only DivMMC expansion mode is currently available.";
                return false;
            }

            try
            {
                byte[] firmware = File.ReadAllBytes("ROMs\\divmmc.rom");
                const int ramBanks = 16;
                device = new SpectrumDivMmcDevice(mode, firmware, ramBanks);
                device.PowerOn();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        private static bool TryCreateBeta128(SpectrumModel model, out SpectrumBeta128Device? device, out string error)
        {
            device = null;
            error = "";

            try
            {
                if (model == SpectrumModel.Scorpion256 && TryLoadScorpionTrDosRom(out byte[] trdosRom))
                {
                }
                else if (TryLoadStandaloneTrDosRom(model, "ROMs\\trdos.rom", out trdosRom))
                {
                }
                else if (!TryLoadScorpionTrDosRom(out trdosRom))
                {
                    throw new FileNotFoundException("TR-DOS ROM not found. Expected a valid ROMs\\trdos.rom or TR-DOS bank in ROMs\\scorpion.rom.");
                }

                device = new SpectrumBeta128Device(trdosRom);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
        private static bool TryLoadStandaloneTrDosRom(SpectrumModel model, string path, out byte[] rom)
        {
            rom = [];

            if (!File.Exists(path))
            {
                return false;
            }

            byte[] candidate = File.ReadAllBytes(path);
            if (candidate.Length != RomBankSize || IsSameAsModelRomBank(model, candidate, 0))
            {
                return false;
            }

            rom = candidate;
            return true;
        }
        private static bool IsSameAsModelRomBank(SpectrumModel model, ReadOnlySpan<byte> candidate, int bank)
        {
            string[] paths = GetRomPaths(model);
            if (paths.Length == 0)
            {
                return false;
            }

            try
            {
                if (model == SpectrumModel.Pentagon128 || model == SpectrumModel.Scorpion256)
                {
                    string path = paths[0];
                    if (!File.Exists(path))
                    {
                        return false;
                    }

                    byte[] combined = File.ReadAllBytes(path);
                    int offset = bank * RomBankSize;
                    return offset >= 0
                        && offset + RomBankSize <= combined.Length
                        && candidate.SequenceEqual(combined.AsSpan(offset, RomBankSize));
                }

                if (bank >= paths.Length || !File.Exists(paths[bank]))
                {
                    return false;
                }

                byte[] rom = File.ReadAllBytes(paths[bank]);
                return rom.Length == RomBankSize && candidate.SequenceEqual(rom);
            }
            catch
            {
                return false;
            }
        }
        private static bool TryLoadScorpionTrDosRom(out byte[] rom)
        {
            rom = [];

            const string path = "ROMs\\scorpion.rom";
            if (!File.Exists(path))
            {
                return false;
            }

            byte[] combined = File.ReadAllBytes(path);
            if (combined.Length < RomBankSize * 4)
            {
                return false;
            }

            rom = new byte[RomBankSize];
            Buffer.BlockCopy(combined, RomBankSize * 3, rom, 0, RomBankSize);
            return LooksLikeTrDosRom(rom);
        }
        private static bool LooksLikeTrDosRom(ReadOnlySpan<byte> rom)
        {
            return rom.Length == RomBankSize
                && (ContainsAscii(rom, "TR-DOS") || ContainsAscii(rom, "BETA 128"));
        }
        private static bool ContainsAscii(ReadOnlySpan<byte> data, string text)
        {
            ReadOnlySpan<char> needle = text.AsSpan();
            if (needle.Length == 0 || data.Length < needle.Length)
            {
                return false;
            }

            for (int i = 0; i <= data.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (data[i + j] != (byte)needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return true;
                }
            }

            return false;
        }
        private void TryAttachDivStorage(SpectrumDivMmcDevice divDevice)
        {
            divDevice.AttachSdCard(null);
            if (_divExpansionMode != SpectrumDivExpansionMode.DivMmc || _divStoragePath == null)
            {
                return;
            }

            try
            {
                _divStorageCard = OpenDivStorageCard();
                divDevice.AttachSdCard(_divStorageCard);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "DivMMC Storage Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _divStorageCard?.Dispose();
                _divStorageCard = null;
            }
        }
        private void ReopenDivStorage(bool showDeferredMessage)
        {
            CloseDivStorage(showErrors: true);

            if (_divStoragePath == null)
            {
                return;
            }

            if (_divExpansionMode != SpectrumDivExpansionMode.DivMmc || _divDevice == null)
            {
                if (showDeferredMessage)
                {
                    MessageBox.Show(
                        "DivMMC storage selected. It will be attached when DivMMC mode is enabled.",
                        "DivMMC Storage",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            }

            try
            {
                _divStorageCard = OpenDivStorageCard();
                _divDevice.AttachSdCard(_divStorageCard);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "DivMMC Storage Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private SpectrumDivMmcSdCard OpenDivStorageCard()
        {
            if (_divStoragePath == null)
            {
                throw new InvalidOperationException("No DivMMC storage path has been selected.");
            }

            return _divStorageFolderBacked
                ? SpectrumDivMmcSdCard.OpenFolderBacked(_divStoragePath, _divStorageWriteProtected)
                : SpectrumDivMmcSdCard.Open(_divStoragePath, _divStorageWriteProtected);
        }
        private void CloseDivStorage(bool showErrors)
        {
            _divDevice?.AttachSdCard(null);
            SpectrumDivMmcSdCard? storageCard = _divStorageCard;
            _divStorageCard = null;
            if (storageCard == null)
            {
                return;
            }

            try
            {
                storageCard.FlushFolderBacking();
            }
            catch (Exception ex)
            {
                if (showErrors)
                {
                    MessageBox.Show(ex.Message, "DivMMC Folder Storage Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            finally
            {
                storageCard.Dispose();
            }
        }
        private static string[] GetRomPaths(SpectrumModel model)
        {
            return model switch
            {
                SpectrumModel.Spectrum16K => ["ROMs\\48.rom"],
                SpectrumModel.Spectrum48K => ["ROMs\\48.rom"],
                SpectrumModel.Spectrum128K => ["ROMs\\128_0.rom", "ROMs\\128_1.rom"],
                SpectrumModel.SpectrumPlus2 => ["ROMs\\plus2_0.rom", "ROMs\\plus2_1.rom"],
                SpectrumModel.SpectrumPlus2A => ["ROMs\\plus3-0.rom", "ROMs\\plus3-1.rom", "ROMs\\plus3-2.rom", "ROMs\\plus3-3.rom"],
                SpectrumModel.SpectrumPlus3 => ["ROMs\\plus3-0.rom", "ROMs\\plus3-1.rom", "ROMs\\plus3-2.rom", "ROMs\\plus3-3.rom"],
                SpectrumModel.Pentagon128 => ["ROMs\\pentagon.rom"],
                SpectrumModel.Scorpion256 => ["ROMs\\scorpion.rom"],
                _ => ["ROMs\\48.rom"]
            };
        }
        private static int GetRomBankCount(SpectrumModel model)
        {
            return SpectrumModelTraits.RomBankCount(model);
        }
        private void ToggleQuickPause()
        {
            if (_zxNextMachine != null)
            {
                _zxNextMachine.SetPaused(!_zxNextMachine.IsPaused);
                UpdateQuickAccessState();
                return;
            }

            if (_zx8xMachine != null)
            {
                if (_debugger.IsPaused)
                {
                    ResumeFromDebugger();
                    UpdateQuickAccessState();
                    return;
                }

                _zx8xMachine.SetPaused(!_zx8xMachine.IsPaused);
                UpdateQuickAccessState();
                return;
            }

            if (_emulator == null)
            {
                return;
            }

            if (_emulator.IsPaused)
            {
                if (_debuggerSuspendedRunState != null || _debugger.IsPaused)
                {
                    ResumeFromDebugger();
                }
                else
                {
                    ResumeFromQuickPause();
                }

                UpdateQuickAccessState();
                return;
            }

            PauseFromQuickAccess();
            UpdateQuickAccessState();
        }
        private void PauseFromQuickAccess()
        {
            if (_emulator == null)
            {
                return;
            }

            _quickPauseRunState = new EmulationRunState(_turboEnabled, _audioPlayer != null || _fastTapeRunner != null);
            _turboRunner?.Dispose();
            _turboRunner = null;
            _fastTapeRunner?.Dispose();
            _fastTapeRunner = null;
            _audioPlayer?.Dispose();
            _audioPlayer = null;
            _emulator.VideoEnabled = true;
            _emulator.SetPaused(true);
        }
        private void ResumeFromQuickPause()
        {
            if (_emulator == null)
            {
                return;
            }

            EmulationRunState state = _quickPauseRunState ?? new EmulationRunState(_turboEnabled, true);
            _quickPauseRunState = null;
            _emulator.SetPaused(false);
            RestoreEmulationAfterModal(state);
        }
        private void UpdateQuickAccessState()
        {
            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.TryPost(UpdateQuickAccessState);
                return;
            }

            if (QuickPausePlayButton != null)
            {
                bool paused = _zxNextMachine?.IsPaused
                    ?? _zx8xMachine?.IsPaused
                    ?? (_emulator?.IsPaused == true);
                QuickPausePlayButton.ToolTip = paused ? "Resume emulation" : "Pause emulation";
                QuickPauseIcon.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
                QuickPlayIcon.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
            }

            if (QuickNmiButton != null)
            {
                QuickNmiButton.IsEnabled = _zx8xMachine == null
                    && _zxNextMachine == null
                    && (_divExpansionMode != SpectrumDivExpansionMode.Disabled
                        || _machine?.Multiface != null);
                QuickNmiButton.ToolTip = _machine?.Multiface != null
                    ? "Press the Multiface magic button (F6)"
                    : "Trigger DivMMC NMI (F5)";
            }

            if (QuickTapeBrowserButton != null)
            {
                QuickTapeBrowserButton.ToolTip = _tapeBrowserVisible ? "Hide tape browser" : "Show tape browser";
                QuickTapeHideIcon.Visibility = _tapeBrowserVisible ? Visibility.Visible : Visibility.Collapsed;
                QuickTapeShowIcon.Visibility = _tapeBrowserVisible ? Visibility.Collapsed : Visibility.Visible;
            }

            if (ZxNextSoftResetMenu != null)
            {
                ZxNextSoftResetMenu.IsEnabled = _zxNextMachine != null;
            }

            UpdateFastForwardUi();
        }

        /// <summary>
        /// Replaces the current execution owner with a realtime audio puller which advances
        /// the machine by the selected multiple and compresses that PCM back to host time.
        /// </summary>
        private void SetFastForwardMode(bool enabled)
        {
            if (_zxNextMachine != null)
            {
                _fastForwardEnabled = false;
                UpdateQuickAccessState();
                return;
            }

            _fastForwardEnabled = enabled;
            if (enabled)
            {
                // Turbo is an unbounded, silent owner and cannot coexist with paced
                // fast-forward audio. Selecting fast-forward therefore supersedes it.
                _turboEnabled = false;
                TurboMenu.IsChecked = false;
            }

            _turboRunner?.Dispose();
            _turboRunner = null;
            _fastTapeRunner?.Dispose();
            _fastTapeRunner = null;
            _zx8xTurboRunner?.Dispose();
            _zx8xTurboRunner = null;
            _zx8xFastTapeRunner?.Dispose();
            _zx8xFastTapeRunner = null;
            _audioPlayer?.Dispose();
            _audioPlayer = null;

            if (_zx8xMachine != null)
            {
                _zx8xMachine.Audio.DiscardPendingSamples(_zx8xMachine.Cpu.Cyc);
                RefreshZx8xTapeFastRunMode();
            }
            else if (_emulator != null)
            {
                _emulator.VideoEnabled = true;
                RefreshTapeFastRunMode();
            }

            UpdateQuickAccessState();
        }

        private WaveOutAudioPlayer CreateWaveOutPlayer(IAudioSource source, int sampleRate)
        {
            IAudioSource hostSource = _fastForwardEnabled
                ? new TimeStretchAudioSource(source, sampleRate, _fastForwardSpeed)
                : source;
            return new WaveOutAudioPlayer(hostSource, sampleRate, AudioBufferSamples, AudioBufferCount);
        }

        private void UpdateFastForwardUi()
        {
            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.TryPost(UpdateFastForwardUi);
                return;
            }

            FastForwardMenu.IsChecked = _fastForwardEnabled;
            FastForwardMenu.IsEnabled = _zxNextMachine == null;
            MenuItem[] speeds =
            [
                FastForward2xMenu, FastForward3xMenu, FastForward4xMenu,
                FastForward5xMenu, FastForward6xMenu, FastForward7xMenu,
                FastForward8xMenu, FastForward9xMenu, FastForward10xMenu
            ];
            for (int index = 0; index < speeds.Length; index++)
            {
                speeds[index].IsChecked = _fastForwardSpeed == index + 2;
            }

            QuickFastForwardButton.ToolTip = _fastForwardEnabled
                ? $"Disable fast-forward ({_fastForwardSpeed}x) (`)"
                : $"Fast-forward at {_fastForwardSpeed}x (`)";
            QuickFastForwardButton.IsEnabled = _zxNextMachine == null;
            if (_fastForwardEnabled)
            {
                QuickFastForwardButton.Background = Brushes.SteelBlue;
            }
            else
            {
                QuickFastForwardButton.ClearValue(Button.BackgroundProperty);
            }
        }

        private void SetTurboMode(bool enabled)
        {
            // Runner replacement is the mode switch: CPU execution must never be owned by both
            // the waveOut producer and a TurboRunner at the same time.
            _turboEnabled = enabled;
            if (enabled)
            {
                _fastForwardEnabled = false;
            }
            TurboMenu.IsChecked = enabled;

            if (_zxNextMachine != null)
            {
                StopZxNextExecutionOwner();
                StartZxNextExecutionOwner();
                UpdateQuickAccessState();
                return;
            }

            if (_zx8xMachine != null)
            {
                _zx8xTurboRunner?.Dispose();
                _zx8xTurboRunner = null;
                _zx8xFastTapeRunner?.Dispose();
                _zx8xFastTapeRunner = null;
                _audioPlayer?.Dispose();
                _audioPlayer = null;
                _zx8xMachine.Audio.DiscardPendingSamples(_zx8xMachine.Cpu.Cyc);
                if (enabled)
                {
                    _zx8xTurboRunner = new Zx8x.Core.Zx8xTurboRunner(_zx8xMachine, presentEveryNFrames: 5);
                }
                else
                {
                    _audioPlayer = CreateWaveOutPlayer(_zx8xMachine, _zx8xMachine.SampleRate);
                }

                UpdateQuickAccessState();
                return;
            }

            if (_emulator == null)
            {
                return;
            }

            _turboRunner?.Dispose();
            _turboRunner = null;
            _fastTapeRunner?.Dispose();
            _fastTapeRunner = null;
            _audioPlayer?.Dispose();
            _audioPlayer = null;

            if (enabled)
            {
                _turboRunner = new TurboRunner(_emulator, presentEveryNFrames: 5);
            }
            else
            {
                RefreshTapeFastRunMode();
            }

            UpdateQuickAccessState();
        }
        private bool ShouldRunTapeFastMode()
        {
            return !_turboEnabled
                && !_fastForwardEnabled
                && _runTapeAccelerationAtMaximumSpeed
                && _earInput?.LoaderAccelerationEnabled == true
                && _emulator != null
                && !_emulator.IsPaused
                && _tapeLoader?.IsPlaying == true;
        }
        private void RefreshTapeFastRunMode()
        {
            if (_zx8xMachine != null)
            {
                RefreshZx8xTapeFastRunMode();
                return;
            }

            // Both acceleration engines share this one execution owner. A sparse
            // presentation cadence preserves loader border feedback without letting
            // rendering dominate the unthrottled tape workload.
            if (_emulator == null || _turboEnabled)
            {
                StopTapeFastRunner();
                return;
            }

            if (ShouldRunTapeFastMode())
            {
                if (_fastTapeRunner != null)
                {
                    UpdateQuickAccessState();
                    return;
                }

                _audioPlayer?.Dispose();
                _audioPlayer = null;
                _fastTapeRunner = new TapeFastRunner(_emulator);
                UpdateQuickAccessState();
                return;
            }

            bool hadFastTapeRunner = _fastTapeRunner != null;
            StopTapeFastRunner();

            if (_audioPlayer == null)
            {
                _emulator.VideoEnabled = true;
                int sampleRate = SpectrumAudioTiming.DefaultSampleRate;
                _audioPlayer = CreateWaveOutPlayer(_emulator, sampleRate);
            }

            if (hadFastTapeRunner)
            {
                }

            UpdateQuickAccessState();
        }
        private void StopTapeFastRunner()
        {
            if (_zx8xFastTapeRunner != null)
            {
                _zx8xFastTapeRunner.Dispose();
                _zx8xFastTapeRunner = null;
            }

            if (_fastTapeRunner == null)
            {
                return;
            }

            _fastTapeRunner.Dispose();
            _fastTapeRunner = null;
            if (_emulator != null)
            {
                _emulator.VideoEnabled = true;
            }

            UpdateQuickAccessState();
        }

        private bool ShouldRunZx8xTapeFastMode()
        {
            return _zx8xMachine != null
                && !_turboEnabled
                && !_fastForwardEnabled
                && !_zx8xMachine.IsPaused
                && _edgeLoadEnabled
                && _runTapeAccelerationAtMaximumSpeed
                && _zx8xMachine.Tape.Loader?.IsPlaying == true;
        }

        private void RefreshZx8xTapeFastRunMode()
        {
            Zx8x.Core.Zx8xMachine? machine = _zx8xMachine;
            if (machine == null || _turboEnabled)
            {
                return;
            }

            if (ShouldRunZx8xTapeFastMode())
            {
                if (_zx8xFastTapeRunner != null)
                {
                    return;
                }

                _audioPlayer?.Dispose();
                _audioPlayer = null;
                machine.Audio.DiscardPendingSamples(machine.Cpu.Cyc);
                _zx8xFastTapeRunner = new Zx8x.Core.Zx8xTurboRunner(machine, presentEveryNFrames: 5);
                return;
            }

            bool wasFast = _zx8xFastTapeRunner != null;
            _zx8xFastTapeRunner?.Dispose();
            _zx8xFastTapeRunner = null;
            if (_audioPlayer == null)
            {
                machine.Audio.DiscardPendingSamples(machine.Cpu.Cyc);
                _audioPlayer = CreateWaveOutPlayer(machine, machine.SampleRate);
            }

            if (wasFast)
            {
                }
        }
        /// <summary>Holds the Spectrum still, whichever machine is currently loaded.</summary>
        /// <summary>
        /// Ctrl and a digit loads that state slot; adding Shift saves it.
        /// </summary>
        /// <remarks>
        /// The bare digits belong to the Spectrum keyboard, so a modifier is not optional
        /// here. Ctrl is otherwise unused by the emulator, which is what makes it safe to
        /// take ten of them at once.
        /// </remarks>
        private bool TryHandleStateSlotKey(Key key)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                return false;
            }

            int slot = key switch
            {
                >= Key.D0 and <= Key.D9 => key - Key.D0,
                >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
                _ => -1
            };

            if (slot < 0)
            {
                return false;
            }

            return (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                ? SaveStateSlot(slot)
                : LoadStateSlot(slot);
        }

        private void SetEmulationPaused(bool paused)
        {
            if (_emulator != null)
            {
                _emulator.SetPaused(paused);
            }
        }

        private EmulationRunState SuspendEmulationForModal()
        {
            // Capture ownership, not just the turbo flag: normal playback and accelerated tape
            // both use non-turbo state but need different restoration decisions.
            var state = new EmulationRunState(_turboEnabled, _audioPlayer != null || _fastTapeRunner != null);
            _turboRunner?.Dispose();
            _turboRunner = null;
            _fastTapeRunner?.Dispose();
            _fastTapeRunner = null;
            _audioPlayer?.Dispose();
            _audioPlayer = null;
            if (_emulator != null)
            {
                _emulator.VideoEnabled = true;
            }

            return state;
        }
        private void RestoreEmulationAfterModal(EmulationRunState state)
        {
            if (_emulator == null)
            {
                return;
            }

            _turboRunner?.Dispose();
            _turboRunner = null;
            _fastTapeRunner?.Dispose();
            _fastTapeRunner = null;
            _audioPlayer?.Dispose();
            _audioPlayer = null;

            if (state.WasTurboEnabled)
            {
                _turboRunner = new TurboRunner(_emulator, presentEveryNFrames: 5);
                return;
            }

            if (state.HadAudioPlayer)
            {
                RefreshTapeFastRunMode();
                if (_audioPlayer == null && _fastTapeRunner == null)
                {
                    int sampleRate = SpectrumAudioTiming.DefaultSampleRate;
                    _audioPlayer = CreateWaveOutPlayer(_emulator, sampleRate);
                }
            }
        }
        private void UpdateWindowTitle()
        {
            if (_zxNextMachine != null)
            {
                UpdateZxNextWindowTitle();
                return;
            }

            if ((_zx8xMachine == null && _cpu == null) || _cpuHz <= 0 || _tstatesPerFrame <= 0)
            {
                Title = BaseTitle;
                return;
            }

            if (!_speedStopwatch.IsRunning)
            {
                _speedStopwatch.Start();
                _lastSpeedSeconds = _speedStopwatch.Elapsed.TotalSeconds;
                _lastSpeedTstates = GetCurrentCpuCycles();
                Title = BaseTitle;
                return;
            }

            double nowSeconds = _speedStopwatch.Elapsed.TotalSeconds;
            double dt = nowSeconds - _lastSpeedSeconds;
            if (dt <= 0.0)
            {
                return;
            }

            ulong nowTstates = GetCurrentCpuCycles();
            ulong deltaTstates = nowTstates - _lastSpeedTstates;
            _lastSpeedSeconds = nowSeconds;
            _lastSpeedTstates = nowTstates;

            double emuSeconds = deltaTstates / _cpuHz;
            double speed = emuSeconds / dt;
            double percent = speed * 100.0;
            double emuFps = (deltaTstates / (double)_tstatesPerFrame) / dt;

            Title = $"{BaseTitle} - {percent:0}% ({emuFps:0.0} fps)";
        }
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F12)
            {
                ShowDebuggerWindow();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Oem3)
            {
                if (!e.IsRepeat)
                {
                    SetFastForwardMode(!_fastForwardEnabled);
                }

                e.Handled = true;
                return;
            }

            if (e.IsRepeat)
            {
                return;
            }

            if (HandleKeyEvent(e, true))
            {
                e.Handled = true;
            }
        }
        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (HandleKeyEvent(e, false))
            {
                e.Handled = true;
            }
        }
        private void OnWindowDeactivated(object? sender, EventArgs e)
        {
            if (_zxNextMachine != null)
            {
                _zxNextMachine.Joystick.ReleaseAll();
                _zxNextMachine.Keyboard.ReleaseAll();
                return;
            }

            if (_zx8xMachine != null)
            {
                _zx8xMachine.Keyboard.ReleaseAll();
                return;
            }

            _joystick?.ReleaseAll();
            _keyboard?.ReleaseAll();
        }
        private void OnDebugger(object sender, RoutedEventArgs e)
        {
            ShowDebuggerWindow();
        }
        private void OnAudioOscilloscope(object sender, RoutedEventArgs e)
        {
            ShowAudioOscilloscopeWindow();
        }
        private void ShowAudioOscilloscopeWindow()
        {
            if (_zx8xMachine != null)
            {
                MessageBox.Show(
                    "The current oscilloscope displays Spectrum beeper and AY channels. ZX80/ZX81 cassette monitoring will be added with the ZX8x media tools.",
                    "Oscilloscope",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_oscilloscopeWindow != null)
            {
                _oscilloscopeWindow.Activate();
                return;
            }

            var window = new AudioOscilloscopeWindow
            {
                Owner = this
            };
            _oscilloscopeWindow = window;
            window.AttachAudioSource(GetAudioScopeSource());
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_oscilloscopeWindow, window))
                {
                    _oscilloscopeWindow = null;
                }
            };
            window.Show();
        }
        /// <summary>The audio block of whichever machine family is currently running.</summary>
        private IAudioScopeSource? GetAudioScopeSource() =>
            _zxNextMachine != null ? _zxNextMachine.Audio : _audioRenderer;

        private void ShowDebuggerWindow()
        {
            if (_emulator == null && _zx8xMachine == null && _zxNextMachine == null)
            {
                return;
            }

            if (_debuggerWindow != null)
            {
                _debuggerWindow.Activate();
                _debuggerWindow.RefreshAll(followPc: true);
                return;
            }

            // The Next needs the Z80N table and contributes its own MMU and NextReg views.
            var window = new DebuggerWindow(
                _debugger,
                _zxNextMachine != null ? _z80nDisassembler : _debuggerDisassembler,
                _debuggerAssembler,
                _fileDialogs,
                _clipboard,
                _uiDispatcher,
                _zxNextMachine != null ? new ZxNextDebuggerExtensions(_zxNextMachine) : null)
            {
                Owner = this
            };
            window.RunRequested += ResumeFromDebugger;
            window.PauseRequested += () => PauseForDebugger("Paused");
            window.StepIntoRequested += StepDebuggerInto;
            window.StepOverRequested += StepDebuggerOver;
            window.StepSourceLineRequested += StepDebuggerSourceLine;
            window.StepSourceLineOverRequested += StepDebuggerSourceLineOver;
            window.RunToAddressRequested += RunDebuggerToAddress;
            _debuggerWindow = window;
            window.Closed += (_, _) =>
            {
                _debuggerWindow = null;
                UpdateCpuStepHooks();
            };
            UpdateCpuStepHooks();
            window.Show();
        }
        private bool BeforeCpuStep()
        {
            // Ordering is intentional. A debugger break must observe the unexecuted instruction;
            // autoload and flash traps may then mutate state in place of ordinary execution.
            if (_debugger.Enabled && _debugger.BeforeCpuStep())
            {
                _emulator?.SetPaused(true);
                RequestDebuggerPauseOnUiThread();
                return true;
            }

            AdvanceAutoLoadInjector();

            if (TryTapeTraps())
            {
                return true;
            }

            TryStartTapeForRomLoader();
            return false;
        }
        private bool BeforeZxNextDebuggerCpuStep()
        {
            if (!_debugger.Enabled || !_debugger.BeforeCpuStep())
            {
                return false;
            }

            _zxNextMachine?.SetPaused(true);
            RequestDebuggerPauseOnUiThread();
            return true;
        }
        private void AfterZxNextDebuggerCpuStep()
        {
            _debugger.AfterCpuStep();
            if (_debugger.IsPaused)
            {
                _zxNextMachine?.SetPaused(true);
                RequestDebuggerPauseOnUiThread();
            }
        }
        private bool BeforeZx8xDebuggerCpuStep()
        {
            if (!_debugger.Enabled || !_debugger.BeforeCpuStep())
            {
                return false;
            }

            _zx8xMachine?.SetPaused(true);
            RequestDebuggerPauseOnUiThread();
            return true;
        }
        private void AdvanceAutoLoadInjector()
        {
            AutoLoadKeyboardInjector? injector = _autoLoadInjector;
            if (injector == null)
            {
                return;
            }

            injector.Tick();
            if (!injector.IsComplete)
            {
                return;
            }

            _autoLoadInjector = null;
            _uiDispatcher.TryPost(UpdateCpuStepHooks, UiDispatchPriority.Background);
        }
        private void AfterCpuStep()
        {
            if (_autoTapePlayStopEnabled && _cpu?.IsHalted == true)
            {
                StopTapeMotorFromAutoPlay();
            }

            if (!_debugger.Enabled)
            {
                return;
            }

            _debugger.AfterCpuStep();
            if (_debugger.IsPaused)
            {
                _emulator?.SetPaused(true);
                RequestDebuggerPauseOnUiThread();
            }
        }
        private void AfterZx8xDebuggerCpuStep()
        {
            if (!_debugger.Enabled)
            {
                return;
            }

            _debugger.AfterCpuStep();
            if (_debugger.IsPaused)
            {
                _zx8xMachine?.SetPaused(true);
                RequestDebuggerPauseOnUiThread();
            }
        }
        private void UpdateCpuStepHooks()
        {
            if (_zxNextMachine != null)
            {
                _zxNextMachine.ConfigureCpuStepHooks(
                    _debugger.Enabled ? BeforeZxNextDebuggerCpuStep : null,
                    _debugger.Enabled ? AfterZxNextDebuggerCpuStep : null);
                _zxNextMachine.Cpu.ConfigureDebugHook(
                    _debugger.AccessWatchpointsEnabled ? _debugger : null);
                return;
            }

            if (_zx8xMachine != null)
            {
                _zx8xMachine.ConfigureCpuStepHooks(
                    _debugger.Enabled ? BeforeZx8xDebuggerCpuStep : null,
                    _debugger.Enabled ? AfterZx8xDebuggerCpuStep : null);
                _zx8xMachine.Cpu.ConfigureDebugHook(
                    _debugger.AccessWatchpointsEnabled ? _debugger : null);
                return;
            }

            if (_emulator == null)
            {
                return;
            }

            // Remove delegates completely when dormant; this method is called for every Z80
            // instruction, so even an empty callback is measurable in turbo mode.
            bool before = _flashLoadEnabled || _flashSaveEnabled || _autoTapePlayStopEnabled || _autoLoadInjector != null || _debugger.Enabled || _debuggerWindow != null;
            bool after = _autoTapePlayStopEnabled || _debugger.Enabled;
            _emulator.ConfigureCpuStepHooks(before ? BeforeCpuStep : null, after ? AfterCpuStep : null);
            _cpu?.ConfigureDebugHook(_debugger.AccessWatchpointsEnabled ? _debugger : null);
        }
        private void OnDebuggerHooksChanged()
        {
            if (!_uiDispatcher.CheckAccess())
            {
                _uiDispatcher.TryPost(UpdateCpuStepHooks);
                return;
            }

            UpdateCpuStepHooks();
        }
        private void OnDebuggerBreakHit(DebuggerBreakHit hit)
        {
            RequestDebuggerPauseOnUiThread();
        }
        private void RequestDebuggerPauseOnUiThread()
        {
            _uiDispatcher.TryPost(() =>
            {
                PauseForDebugger(_debugger.LastHit?.Reason ?? "Debugger break", notifyController: false);
            });
        }
        private void PauseForDebugger(string reason, bool notifyController = true)
        {
            if (_zxNextMachine != null)
            {
                // The Next owns its own execution thread, so stop that before pausing:
                // the runner must not still be inside an instruction when we refresh.
                StopZxNextExecutionOwner();
                _zxNextMachine.SetPaused(true);
                if (notifyController)
                {
                    _debugger.Pause(reason);
                }

                UpdateCpuStepHooks();
                _debuggerWindow?.RefreshAll(followPc: true);
                UpdateQuickAccessState();
                return;
            }

            if (_zx8xMachine != null)
            {
                _debuggerSuspendedRunState ??= new EmulationRunState(
                    _turboEnabled,
                    _audioPlayer != null || _zx8xFastTapeRunner != null);
                _zx8xTurboRunner?.Dispose();
                _zx8xTurboRunner = null;
                _zx8xFastTapeRunner?.Dispose();
                _zx8xFastTapeRunner = null;
                _audioPlayer?.Dispose();
                _audioPlayer = null;
                _zx8xMachine.SetPaused(true);
                if (notifyController)
                {
                    _debugger.Pause(reason);
                }

                UpdateCpuStepHooks();
                _debuggerWindow?.RefreshAll(followPc: true);
                UpdateQuickAccessState();
                return;
            }

            if (_emulator == null)
            {
                return;
            }

            _debuggerSuspendedRunState ??= new EmulationRunState(_turboEnabled, _audioPlayer != null || _fastTapeRunner != null);

            _turboRunner?.Dispose();
            _turboRunner = null;
            _fastTapeRunner?.Dispose();
            _fastTapeRunner = null;
            _audioPlayer?.Dispose();
            _audioPlayer = null;
            _emulator.VideoEnabled = true;
            _emulator.SetPaused(true);

            if (notifyController)
            {
                _debugger.Pause(reason);
            }

            UpdateCpuStepHooks();
            _debuggerWindow?.RefreshAll(followPc: true);
            UpdateQuickAccessState();
        }
        private void ResumeFromDebugger()
        {
            if (_zxNextMachine != null)
            {
                _debugger.Run();
                _zxNextMachine.SetPaused(false);
                UpdateCpuStepHooks();
                StartZxNextExecutionOwner();
                _debuggerWindow?.RefreshAll(followPc: true);
                UpdateQuickAccessState();
                return;
            }

            if (_zx8xMachine != null)
            {
                _debugger.Run();
                _zx8xMachine.SetPaused(false);
                UpdateCpuStepHooks();
                EmulationRunState zxState = _debuggerSuspendedRunState
                    ?? new EmulationRunState(_turboEnabled, true);
                _debuggerSuspendedRunState = null;
                if (zxState.WasTurboEnabled)
                {
                    SetTurboMode(true);
                }
                else if (zxState.HadAudioPlayer)
                {
                    SetTurboMode(false);
                }

                _debuggerWindow?.RefreshAll(followPc: true);
                UpdateQuickAccessState();
                return;
            }

            if (_emulator == null)
            {
                return;
            }

            _debugger.Run();
            _emulator.SetPaused(false);
            UpdateCpuStepHooks();

            EmulationRunState state = _debuggerSuspendedRunState ?? new EmulationRunState(_turboEnabled, _audioPlayer != null || _fastTapeRunner != null);
            _debuggerSuspendedRunState = null;
            RestoreEmulationAfterModal(state);
            _debuggerWindow?.RefreshAll(followPc: true);
            UpdateQuickAccessState();
        }

        /// <summary>
        /// Runs until execution reaches a different line of the loaded source.
        /// </summary>
        /// <remarks>
        /// Built on the single-instruction step rather than beside it, so it inherits the
        /// per-machine handling the instruction step already does. The refresh is left until
        /// the end: a source line can be hundreds of instructions, and redrawing the whole
        /// debugger for each of them would cost far more than the stepping.
        /// </remarks>
        private void StepDebuggerSourceLine() => RunSourceStep(stepOverCalls: false);

        /// <summary>
        /// The same, but calls made along the way are run to completion rather than entered.
        /// </summary>
        /// <remarks>
        /// The only difference is which primitive the loop is given, which is why both share
        /// one implementation: stepping over is stepping, with a different idea of one step.
        /// </remarks>
        private void StepDebuggerSourceLineOver() => RunSourceStep(stepOverCalls: true);

        private void RunSourceStep(bool stepOverCalls)
        {
            DebuggerWindow? window = _debuggerWindow;
            if (window == null)
            {
                return;
            }

            Func<bool> step = stepOverCalls
                ? () => SpectrumSourceStepper.StepOverInstruction(
                    ReadDebuggerPc,
                    ReadDebuggerStackPointer,
                    MeasureDebuggerInstruction,
                    StepDebuggerOneInstruction)
                : StepDebuggerOneInstruction;

            SourceStepOutcome outcome = SpectrumSourceStepper.StepLine(
                window.Symbols,
                ReadDebuggerPc,
                step);

            window.RefreshAll(followPc: true);
            string what = stepOverCalls ? "Source step over" : "Source step";
            StatusText.Text = outcome.ReachedNewLine
                ? $"Stepped to {outcome.Summary}"
                : $"{what}: {outcome.Summary}";
        }

        private ushort ReadDebuggerStackPointer() => _debugger.Cpu?.SP ?? 0;

        /// <summary>
        /// How wide the instruction at an address is, and whether it calls somewhere.
        /// </summary>
        /// <remarks>
        /// A Next has to be measured with the extended decoder, because Z80N instructions are
        /// longer than the plain table reports and a step over one would otherwise resume in
        /// the middle of it.
        /// </remarks>
        private SpectrumInstructionShape MeasureDebuggerInstruction(ushort address)
        {
            IZ80DebuggerMemory? memory = _debugger.Memory;
            if (memory == null)
            {
                return new SpectrumInstructionShape(1, IsCallLike: false);
            }

            Z80Disassembler disassembler = _zxNextMachine != null ? _z80nDisassembler : _debuggerDisassembler;
            Z80DisassembledInstruction instruction = disassembler.Disassemble(memory, address);
            return new SpectrumInstructionShape(instruction.Length, instruction.IsCallLike);
        }

        private ushort ReadDebuggerPc() => _debugger.Cpu?.PC ?? 0;

        /// <summary>One instruction, without redrawing anything.</summary>
        /// <remarks>
        /// The same sequence the instruction step performs, minus the refresh, so a loop over
        /// it behaves identically to pressing step repeatedly.
        /// </remarks>
        private bool StepDebuggerOneInstruction()
        {
            if (_zxNextMachine != null)
            {
                _zxNextMachine.SetPaused(false);
                _zxNextMachine.StepInstruction();
                _zxNextMachine.SetPaused(true);
                return true;
            }

            if (_zx8xMachine != null)
            {
                _zx8xMachine.SetPaused(false);
                _zx8xMachine.StepInstruction();
                _zx8xMachine.SetPaused(true);
                return true;
            }

            if (_emulator == null)
            {
                return false;
            }

            _emulator.SetPaused(false);
            _emulator.StepInstruction();
            _emulator.SetPaused(true);
            return true;
        }
        private void StepDebuggerInto()
        {
            if (_zxNextMachine != null)
            {
                PauseForDebugger("Step", notifyController: false);
                _debugger.PrepareStepInto();
                UpdateCpuStepHooks();
                _zxNextMachine.SetPaused(false);
                _zxNextMachine.StepInstruction();
                _zxNextMachine.SetPaused(true);
                UpdateCpuStepHooks();
                _debuggerWindow?.RefreshAll(followPc: true);
                return;
            }

            if (_zx8xMachine != null)
            {
                PauseForDebugger("Step", notifyController: false);
                _debugger.PrepareStepInto();
                UpdateCpuStepHooks();
                _zx8xMachine.SetPaused(false);
                _zx8xMachine.StepInstruction();
                _zx8xMachine.SetPaused(true);
                UpdateCpuStepHooks();
                _debuggerWindow?.RefreshAll(followPc: true);
                return;
            }

            if (_emulator == null)
            {
                return;
            }

            PauseForDebugger("Step", notifyController: false);
            _debugger.PrepareStepInto();
            UpdateCpuStepHooks();
            _emulator.SetPaused(false);
            _emulator.StepInstruction();
            _emulator.SetPaused(true);
            UpdateCpuStepHooks();
            _debuggerWindow?.RefreshAll(followPc: true);
        }
        private void StepDebuggerOver()
        {
            if (_zxNextMachine != null)
            {
                PauseForDebugger("Step over", notifyController: false);
                // Z80N instructions are longer than the plain table would report, so a
                // step over one has to be measured with the extended decoder.
                _debugger.PrepareStepOver(_z80nDisassembler);
                UpdateCpuStepHooks();
                if (_debugger.Mode == DebuggerRunMode.StepInto)
                {
                    _zxNextMachine.SetPaused(false);
                    _zxNextMachine.StepInstruction();
                    _zxNextMachine.SetPaused(true);
                    UpdateCpuStepHooks();
                    _debuggerWindow?.RefreshAll(followPc: true);
                    return;
                }

                ResumeFromDebugger();
                return;
            }

            if (_zx8xMachine != null)
            {
                PauseForDebugger("Step over", notifyController: false);
                _debugger.PrepareStepOver(_debuggerDisassembler);
                UpdateCpuStepHooks();
                if (_debugger.Mode == DebuggerRunMode.StepInto)
                {
                    _zx8xMachine.SetPaused(false);
                    _zx8xMachine.StepInstruction();
                    _zx8xMachine.SetPaused(true);
                    UpdateCpuStepHooks();
                    _debuggerWindow?.RefreshAll(followPc: true);
                    return;
                }

                ResumeFromDebugger();
                return;
            }

            if (_emulator == null)
            {
                return;
            }

            PauseForDebugger("Step over", notifyController: false);
            _debugger.PrepareStepOver(_debuggerDisassembler);
            UpdateCpuStepHooks();
            if (_debugger.Mode == DebuggerRunMode.StepInto)
            {
                _emulator.SetPaused(false);
                _emulator.StepInstruction();
                _emulator.SetPaused(true);
                UpdateCpuStepHooks();
                _debuggerWindow?.RefreshAll(followPc: true);
                return;
            }

            ResumeFromDebugger();
        }
        private void RunDebuggerToAddress(ushort address)
        {
            _debugger.PrepareRunTo(address);
            ResumeFromDebugger();
        }
        private void OnBasicProgram(object sender, RoutedEventArgs e)
        {
            if (_zxNextMachine != null)
            {
                MessageBox.Show(
                    "The BASIC editor does not yet support ZX Spectrum Next memory and ROM state.",
                    "BASIC Program",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_zx8xMachine != null)
            {
                MessageBox.Show(
                    "The BASIC editor currently supports Sinclair Spectrum BASIC only.",
                    "BASIC Program",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_memory == null)
            {
                MessageBox.Show("No memory available for BASIC editing.", "BASIC Program", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            EmulationRunState runState = SuspendEmulationForModal();
            try
            {
                var dialog = new BasicProgramDialog(new SpectrumBasicMemoryService(_memory, _model), _model)
                {
                    Owner = this
                };
                dialog.ShowDialog();
            }
            finally
            {
                RestoreEmulationAfterModal(runState);
                Focus();
            }
        }
        private void OnPokes(object sender, RoutedEventArgs e)
        {
            if (_zxNextMachine != null)
            {
                MessageBox.Show(
                    "ZX Spectrum Next physical/MMU-aware memory editing is not implemented yet.",
                    "Pokes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (_zx8xMachine != null)
            {
                MessageBox.Show(
                    "ZX80/ZX81 memory editing will be exposed through the portable debugger path.",
                    "Pokes",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dialog = new PokeDialog
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            if (_memory == null)
            {
                MessageBox.Show("No memory available for pokes.", "Pokes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TryParsePokes(dialog.PokeText, out List<PokeEntry> pokes, out string error))
            {
                MessageBox.Show(error, "Pokes", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ApplyPokes(pokes);
        }
        private void ApplyPokes(List<PokeEntry> pokes)
        {
            for (int i = 0; i < pokes.Count; i++)
            {
                PokeEntry entry = pokes[i];
                for (int offset = 0; offset < entry.Count; offset++)
                {
                    ushort address = (ushort)(entry.Address + offset);
                    _memory.Write(address, entry.Value);
                }
            }
        }
        private static bool TryParsePokes(string text, out List<PokeEntry> pokes, out string error)
        {
            pokes = [];
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                error = "No poke entries were provided.";
                return false;
            }

            string[] lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                string rawLine = lines[i].Trim();
                if (rawLine.Length == 0)
                {
                    continue;
                }

                int commentIndex = rawLine.IndexOf(';');
                if (commentIndex >= 0)
                {
                    rawLine = rawLine[..commentIndex].Trim();
                }

                int slashIndex = rawLine.IndexOf("//", StringComparison.Ordinal);
                if (slashIndex >= 0)
                {
                    rawLine = rawLine[..slashIndex].Trim();
                }

                if (rawLine.Length == 0)
                {
                    continue;
                }

                string cleaned = rawLine.Replace(",", " ").Replace("=", " ").Replace(":", " ");
                string[] parts = cleaned.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                int partIndex = 0;
                if (string.Equals(parts[0], "poke", StringComparison.OrdinalIgnoreCase))
                {
                    partIndex = 1;
                }

                int remaining = parts.Length - partIndex;
                if (remaining < 2 || remaining > 3)
                {
                    error = $"Invalid poke format on line {i + 1}. Use: address value [count].";
                    return false;
                }

                if (!TryParseNumber(parts[partIndex], 0xFFFF, out int address))
                {
                    error = $"Invalid address on line {i + 1}.";
                    return false;
                }

                if (!TryParseNumber(parts[partIndex + 1], 0xFF, out int value))
                {
                    error = $"Invalid value on line {i + 1}.";
                    return false;
                }

                int count = 1;
                if (remaining == 3)
                {
                    if (!TryParseNumber(parts[partIndex + 2], 0xFFFF, out int parsedCount) || parsedCount <= 0)
                    {
                        error = $"Invalid count on line {i + 1}.";
                        return false;
                    }
                    count = parsedCount;
                }

                if (address + count - 1 > 0xFFFF)
                {
                    error = $"Poke range overruns memory on line {i + 1}.";
                    return false;
                }

                pokes.Add(new PokeEntry((ushort)address, (byte)value, count));
            }

            if (pokes.Count == 0)
            {
                error = "No valid pokes were found.";
                return false;
            }

            return true;
        }
        private static bool TryParseNumber(string token, int maxValue, out int value)
        {
            value = 0;
            string text = token.Trim();
            if (text.Length == 0)
            {
                return false;
            }

            int numberBase = 10;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                numberBase = 16;
                text = text[2..];
            }
            else if (text.StartsWith('$') || text.StartsWith('#'))
            {
                numberBase = 16;
                text = text[1..];
            }

            if (numberBase == 16)
            {
                if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                {
                    return false;
                }
            }
            else
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    return false;
                }
            }

            return value >= 0 && value <= maxValue;
        }
        /// <summary>
        /// Feeds a ROM command through LAST_K once that model reaches its stable input loop.
        /// </summary>
        /// <remarks>
        /// Timing is measured in emulated T-states, so autoload behaves identically under realtime
        /// and turbo execution. Waiting for bit 5 of FLAGS to clear prevents overwriting a key the
        /// ROM has not consumed yet.
        /// </remarks>
        private sealed class AutoLoadKeyboardInjector(
            Z80 cpu,
            SpectrumMemory memory,
            ushort readyPc,
            int? expectedRomBank,
            byte[] command,
            int initialDelayTstates,
            int keySpacingTstates)
        {
            private const ushort LastKAddress = 0x5C08;
            private const ushort FlagsAddress = 0x5C3B;
            private const byte KeyAvailableMask = 0x20;
            private readonly byte[] _command = command;
            private readonly ulong _minimumWriteCycle = cpu.Cyc + (ulong)Math.Max(initialDelayTstates, 0);
            private readonly ulong _keySpacingTstates = (ulong)Math.Max(keySpacingTstates, 1);
            private int _offset;
            private ulong _nextWriteCycle;
            private bool _readySeen;

            public bool IsComplete { get; private set; }
            public void Tick()
            {
                if (IsComplete)
                {
                    return;
                }

                if (!_readySeen)
                {
                    if (cpu.PC != readyPc)
                    {
                        return;
                    }

                    if (expectedRomBank.HasValue && memory.CurrentRomBank != expectedRomBank.Value)
                    {
                        return;
                    }

                    _readySeen = true;
                    _nextWriteCycle = Math.Max(cpu.Cyc, _minimumWriteCycle);
                    return;
                }

                if (cpu.Cyc < _nextWriteCycle)
                {
                    return;
                }

                byte flags = memory.ReadDirect(FlagsAddress);
                if ((flags & KeyAvailableMask) != 0)
                {
                    return;
                }

                memory.WriteDirect(LastKAddress, _command[_offset++]);
                memory.WriteDirect(FlagsAddress, (byte)(flags | KeyAvailableMask));
                _nextWriteCycle = cpu.Cyc + _keySpacingTstates;
                IsComplete = _offset >= _command.Length;
            }
        }
        private readonly struct PokeEntry(ushort address, byte value, int count)
        {
            public ushort Address { get; } = address;
            public byte Value { get; } = value;
            public int Count { get; } = count;
        }
        /// <summary>Minimal runner ownership snapshot used while modal tools suspend execution.</summary>
        private readonly struct EmulationRunState(bool wasTurboEnabled, bool hadAudioPlayer)
        {
            public bool WasTurboEnabled { get; } = wasTurboEnabled;
            public bool HadAudioPlayer { get; } = hadAudioPlayer;
        }
        private bool HandleKeyEvent(KeyEventArgs e, bool pressed)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (_zxNextMachine != null)
            {
                return HandleZxNextKeyEvent(key, pressed);
            }

            if (_zx8xMachine != null)
            {
                return HandleZx8xKeyEvent(key, pressed);
            }

            if (pressed && key == Key.F5)
            {
                return TriggerDivNmi();
            }

            if (pressed && key == Key.F6)
            {
                return _machine?.PressMultifaceButton() == true;
            }

            if (pressed && key == Key.F7)
            {
                return StepReplayBack();
            }

            if (pressed && key == Key.F8)
            {
                return (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                    ? StepReplayBackFrames(1)
                    : AdvanceOneFrame();
            }

            if (pressed && key == Key.F9)
            {
                ShowTasWindow();
                return true;
            }

            if (pressed && TryHandleStateSlotKey(key))
            {
                return true;
            }

            if (_joystickType != SpectrumJoystickType.None
                && _joystickKeyMap.TryGetValue(key, out SpectrumJoystickButton button))
            {
                _joystick.SetButtonState(button, pressed);
                return true;
            }

            if (!_keyMap.TryGetValue(key, out SpectrumKey[]? keys))
            {
                return false;
            }

            for (int i = 0; i < keys.Length; i++)
            {
                _keyboard.SetKeyState(keys[i], pressed);
            }

            return true;
        }
        private static Dictionary<Key, SpectrumJoystickButton> BuildJoystickKeyMap()
        {
            return new Dictionary<Key, SpectrumJoystickButton>
            {
                { Key.Left, SpectrumJoystickButton.Left },
                { Key.Right, SpectrumJoystickButton.Right },
                { Key.Up, SpectrumJoystickButton.Up },
                { Key.Down, SpectrumJoystickButton.Down },
                { Key.LeftAlt, SpectrumJoystickButton.Fire },
                { Key.RightAlt, SpectrumJoystickButton.Fire }
            };
        }
        private static Dictionary<Key, SpectrumKey[]> BuildKeyMap()
        {
            return new Dictionary<Key, SpectrumKey[]>
            {
                { Key.LeftShift, new[] { SpectrumKey.CapsShift } },
                { Key.RightShift, new[] { SpectrumKey.CapsShift } },
                { Key.LeftCtrl, new[] { SpectrumKey.SymbolShift } },
                { Key.RightCtrl, new[] { SpectrumKey.SymbolShift } },
                { Key.Left, new[] { SpectrumKey.CapsShift, SpectrumKey.D5 } },
                { Key.Down, new[] { SpectrumKey.CapsShift, SpectrumKey.D6 } },
                { Key.Up, new[] { SpectrumKey.CapsShift, SpectrumKey.D7 } },
                { Key.Right, new[] { SpectrumKey.CapsShift, SpectrumKey.D8 } },
                { Key.Space, new[] { SpectrumKey.Space } },
                { Key.Enter, new[] { SpectrumKey.Enter } },
                { Key.A, new[] { SpectrumKey.A } },
                { Key.B, new[] { SpectrumKey.B } },
                { Key.C, new[] { SpectrumKey.C } },
                { Key.D, new[] { SpectrumKey.D } },
                { Key.E, new[] { SpectrumKey.E } },
                { Key.F, new[] { SpectrumKey.F } },
                { Key.G, new[] { SpectrumKey.G } },
                { Key.H, new[] { SpectrumKey.H } },
                { Key.I, new[] { SpectrumKey.I } },
                { Key.J, new[] { SpectrumKey.J } },
                { Key.K, new[] { SpectrumKey.K } },
                { Key.L, new[] { SpectrumKey.L } },
                { Key.M, new[] { SpectrumKey.M } },
                { Key.N, new[] { SpectrumKey.N } },
                { Key.O, new[] { SpectrumKey.O } },
                { Key.P, new[] { SpectrumKey.P } },
                { Key.Q, new[] { SpectrumKey.Q } },
                { Key.R, new[] { SpectrumKey.R } },
                { Key.S, new[] { SpectrumKey.S } },
                { Key.T, new[] { SpectrumKey.T } },
                { Key.U, new[] { SpectrumKey.U } },
                { Key.V, new[] { SpectrumKey.V } },
                { Key.W, new[] { SpectrumKey.W } },
                { Key.X, new[] { SpectrumKey.X } },
                { Key.Y, new[] { SpectrumKey.Y } },
                { Key.Z, new[] { SpectrumKey.Z } },
                { Key.D1, new[] { SpectrumKey.D1 } },
                { Key.D2, new[] { SpectrumKey.D2 } },
                { Key.D3, new[] { SpectrumKey.D3 } },
                { Key.D4, new[] { SpectrumKey.D4 } },
                { Key.D5, new[] { SpectrumKey.D5 } },
                { Key.D6, new[] { SpectrumKey.D6 } },
                { Key.D7, new[] { SpectrumKey.D7 } },
                { Key.D8, new[] { SpectrumKey.D8 } },
                { Key.D9, new[] { SpectrumKey.D9 } },
                { Key.D0, new[] { SpectrumKey.D0 } },
                { Key.NumPad1, new[] { SpectrumKey.D1 } },
                { Key.NumPad2, new[] { SpectrumKey.D2 } },
                { Key.NumPad3, new[] { SpectrumKey.D3 } },
                { Key.NumPad4, new[] { SpectrumKey.D4 } },
                { Key.NumPad5, new[] { SpectrumKey.D5 } },
                { Key.NumPad6, new[] { SpectrumKey.D6 } },
                { Key.NumPad7, new[] { SpectrumKey.D7 } },
                { Key.NumPad8, new[] { SpectrumKey.D8 } },
                { Key.NumPad9, new[] { SpectrumKey.D9 } },
                { Key.NumPad0, new[] { SpectrumKey.D0 } },
                { Key.Back, new[] { SpectrumKey.CapsShift, SpectrumKey.D0 } },
                { Key.Delete, new[] { SpectrumKey.CapsShift, SpectrumKey.D0 } },
                { Key.Escape, new[] { SpectrumKey.CapsShift, SpectrumKey.Space } }
            };
        }
    }
}
