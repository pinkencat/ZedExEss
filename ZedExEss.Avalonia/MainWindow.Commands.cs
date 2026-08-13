using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Interface1;
using ZedExEss.Zx8x.Core;
using ZedExEss.Zx8x.Memory;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace ZedExEss.AvaloniaHost;

/// <summary>
/// Wires the desktop command surface and routes files from dialogs or drag/drop to the
/// appropriate portable media subsystem.
/// </summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<SpectrumModel, MenuItem> _modelMenuItems = [];
    private readonly Dictionary<Zx8xModel, MenuItem> _zx8xModelMenuItems = [];
    private readonly Dictionary<Zx8xRamConfiguration, MenuItem> _zx8xRamMenuItems = [];
    private readonly Dictionary<SpectrumJoystickType, MenuItem> _joystickMenuItems = [];
    private readonly MenuItem[] _plus3SaveMenuItems = new MenuItem[2];
    private readonly MenuItem[] _plus3EjectMenuItems = new MenuItem[2];
    private readonly MenuItem[] _betaSaveMenuItems = new MenuItem[2];
    private readonly MenuItem[] _betaEjectMenuItems = new MenuItem[2];
    private Grid _mainContentGrid = null!;
    private Border _mediaBrowserPanel = null!;
    private ShapePath _pauseGlyph = null!;
    private ShapePath _playGlyph = null!;
    private Button _quickBrowserButton = null!;
    private MenuItem _showMediaBrowserMenuItem = null!;
    private MenuItem _playTapeMenuItem = null!;
    private MenuItem _stopTapeMenuItem = null!;
    private MenuItem _rewindTapeMenuItem = null!;
    private MenuItem _ejectTapeMenuItem = null!;
    private MenuItem _divMmcEnabledMenuItem = null!;
    private MenuItem _divMmcEjectMenuItem = null!;
    private MenuItem _turboMenuItem = null!;
    private MenuItem _pollingAccelerationMenuItem = null!;
    private MenuItem _semanticAccelerationMenuItem = null!;
    private MenuItem _maximumTapeSpeedMenuItem = null!;
    private MenuItem _flashLoadMenuItem = null!;
    private MenuItem _autoLoadTapeMenuItem = null!;
    private MenuItem _autoTapePlayMenuItem = null!;
    private MenuItem _gigascreenBlendMenuItem = null!;
    private SpectrumDivExpansionMode _divExpansionMode = SpectrumDivExpansionMode.Disabled;
    private bool _mediaBrowserVisible = true;
    private bool _updatingCommandChecks;

    private void InitializeCommandUi()
    {
        _mainContentGrid = FindRequiredControl<Grid>("MainContentGrid");
        _mediaBrowserPanel = FindRequiredControl<Border>("MediaBrowserPanel");
        _pauseGlyph = FindRequiredControl<ShapePath>("PauseGlyph");
        _playGlyph = FindRequiredControl<ShapePath>("PlayGlyph");
        _quickBrowserButton = FindRequiredControl<Button>("QuickBrowserButton");
        _showMediaBrowserMenuItem = FindRequiredControl<MenuItem>("ShowMediaBrowserMenuItem");
        _playTapeMenuItem = FindRequiredControl<MenuItem>("PlayTapeMenuItem");
        _stopTapeMenuItem = FindRequiredControl<MenuItem>("StopTapeMenuItem");
        _rewindTapeMenuItem = FindRequiredControl<MenuItem>("RewindTapeMenuItem");
        _ejectTapeMenuItem = FindRequiredControl<MenuItem>("EjectTapeMenuItem");
        _divMmcEnabledMenuItem = FindRequiredControl<MenuItem>("DivMmcEnabledMenuItem");
        _divMmcEjectMenuItem = FindRequiredControl<MenuItem>("DivMmcEjectMenuItem");
        _turboMenuItem = FindRequiredControl<MenuItem>("TurboMenuItem");
        _pollingAccelerationMenuItem = FindRequiredControl<MenuItem>("PollingAccelerationMenuItem");
        _semanticAccelerationMenuItem = FindRequiredControl<MenuItem>("SemanticAccelerationMenuItem");
        _maximumTapeSpeedMenuItem = FindRequiredControl<MenuItem>("MaximumTapeSpeedMenuItem");
        _flashLoadMenuItem = FindRequiredControl<MenuItem>("FlashLoadMenuItem");
        _autoLoadTapeMenuItem = FindRequiredControl<MenuItem>("AutoLoadTapeMenuItem");
        _autoTapePlayMenuItem = FindRequiredControl<MenuItem>("AutoTapePlayMenuItem");
        _gigascreenBlendMenuItem = FindRequiredControl<MenuItem>("GigascreenBlendMenuItem");

        FindRequiredControl<Button>("QuickOpenButton").Click += OnOpenMediaClicked;
        FindRequiredControl<Button>("QuickResetButton").Click += OnResetClicked;
        _pauseButton.Click += OnPauseClicked;
        FindRequiredControl<Button>("QuickNmiButton").Click += OnNmiClicked;
        _quickBrowserButton.Click += OnToggleMediaBrowserClicked;

        FindRequiredControl<MenuItem>("OpenMediaMenuItem").Click += OnOpenMediaClicked;
        FindRequiredControl<MenuItem>("ResetMenuItem").Click += OnResetClicked;
        _turboMenuItem.Click += OnTurboMenuClicked;
        FindRequiredControl<MenuItem>("ExitMenuItem").Click += (_, _) => Close();
        FindRequiredControl<MenuItem>("OpenTapeMenuItem").Click += OnOpenTapeClicked;
        _playTapeMenuItem.Click += OnPlayTapeClicked;
        _stopTapeMenuItem.Click += OnStopTapeClicked;
        _rewindTapeMenuItem.Click += OnRewindTapeClicked;
        _ejectTapeMenuItem.Click += OnEjectTapeClicked;
        _showMediaBrowserMenuItem.Click += OnToggleMediaBrowserClicked;
        FindRequiredControl<MenuItem>("BasicEditorMenuItem").Click += OnBasicEditorClicked;
        FindRequiredControl<MenuItem>("PokesMenuItem").Click += OnPokesClicked;
        FindRequiredControl<MenuItem>("AudioOscilloscopeMenuItem").Click += OnAudioOscilloscopeClicked;
        FindRequiredControl<MenuItem>("DebuggerMenuItem").Click += OnDebuggerClicked;
        FindRequiredControl<MenuItem>("NmiMenuItem").Click += OnNmiClicked;

        RegisterModelMenuItem("Model16MenuItem", SpectrumModel.Spectrum16K);
        RegisterModelMenuItem("Model48MenuItem", SpectrumModel.Spectrum48K);
        RegisterModelMenuItem("Model128MenuItem", SpectrumModel.Spectrum128K);
        RegisterModelMenuItem("ModelPlus2MenuItem", SpectrumModel.SpectrumPlus2);
        RegisterModelMenuItem("ModelPlus2AMenuItem", SpectrumModel.SpectrumPlus2A);
        RegisterModelMenuItem("ModelPlus3MenuItem", SpectrumModel.SpectrumPlus3);
        RegisterModelMenuItem("ModelPentagonMenuItem", SpectrumModel.Pentagon128);
        RegisterModelMenuItem("ModelScorpionMenuItem", SpectrumModel.Scorpion256);
        RegisterZx8xModelMenuItem("ModelZx80MenuItem", Zx8xModel.Zx80);
        RegisterZx8xModelMenuItem("ModelZx81MenuItem", Zx8xModel.Zx81);
        RegisterZx8xRamMenuItem("Zx8xRam1KMenuItem", Zx8xRamConfiguration.Internal1K);
        RegisterZx8xRamMenuItem("Zx8xRam16KMenuItem", Zx8xRamConfiguration.Expansion16K);
        RegisterJoystickMenuItem("JoystickNoneMenuItem", SpectrumJoystickType.None);
        RegisterJoystickMenuItem("JoystickKempstonMenuItem", SpectrumJoystickType.Kempston);
        RegisterJoystickMenuItem("JoystickSinclair1MenuItem", SpectrumJoystickType.Sinclair1);
        RegisterJoystickMenuItem("JoystickSinclair2MenuItem", SpectrumJoystickType.Sinclair2);
        RegisterJoystickMenuItem("JoystickCursorMenuItem", SpectrumJoystickType.Cursor);

        WireDiskMenus();
        InitializeInterface1Ui();
        _divMmcEnabledMenuItem.Click += OnDivMmcEnabledClicked;
        FindRequiredControl<MenuItem>("DivMmcInsertImageMenuItem").Click += OnInsertDivMmcImageClicked;
        FindRequiredControl<MenuItem>("DivMmcAttachFolderMenuItem").Click += OnAttachDivMmcFolderClicked;
        _divMmcEjectMenuItem.Click += OnEjectDivMmcClicked;
        _pollingAccelerationMenuItem.Click += OnPollingAccelerationClicked;
        _semanticAccelerationMenuItem.Click += OnSemanticAccelerationClicked;
        _maximumTapeSpeedMenuItem.Click += OnMaximumTapeSpeedClicked;
        _flashLoadMenuItem.Click += OnFlashLoadClicked;
        _autoLoadTapeMenuItem.Click += OnAutoLoadTapeClicked;
        _autoTapePlayMenuItem.Click += OnAutoTapePlayClicked;
        _gigascreenBlendMenuItem.Click += OnGigascreenBlendClicked;

        DragDrop.AddDragOverHandler(this, OnWindowDragOver);
        DragDrop.AddDropHandler(this, OnWindowDrop);

        SetMediaBrowserVisible(_mediaBrowserVisible, resizeWindow: false);
        UpdatePauseCommandState(paused: false);
        UpdateModelMenuState(SpectrumModel.Spectrum128K);
        UpdateJoystickMenuState();
        UpdateRuntimeMenuState();
        UpdateTapeMenuState(attached: false, playing: false);
        UpdateDiskMenuState();
        UpdateDivMmcMenuState();
    }

    private void RegisterJoystickMenuItem(string name, SpectrumJoystickType type)
    {
        MenuItem item = FindRequiredControl<MenuItem>(name);
        _joystickMenuItems[type] = item;
        item.Click += (_, _) =>
        {
            if (_updatingCommandChecks || type == _joystickType)
            {
                return;
            }

            ClearPressedKeys();
            _joystickType = type;
            if (_machine != null)
            {
                _machine.Joystick.Type = type;
            }

            UpdateJoystickMenuState();
            Focus();
        };
    }

    private void RegisterModelMenuItem(string name, SpectrumModel model)
    {
        MenuItem item = FindRequiredControl<MenuItem>(name);
        _modelMenuItems[model] = item;
        item.Click += (_, _) =>
        {
            if (!_updatingCommandChecks && (_zx8xMachine != null || model != _machine?.Model))
            {
                ReplaceMachine(model, preserveTape: true, rewindTape: false);
            }
        };
    }

    private void RegisterZx8xModelMenuItem(string name, Zx8xModel model)
    {
        MenuItem item = FindRequiredControl<MenuItem>(name);
        _zx8xModelMenuItems[model] = item;
        item.Click += (_, _) =>
        {
            if (!_updatingCommandChecks && (_zx8xMachine == null || model != _zx8xModel))
            {
                ReplaceZx8xMachine(model);
            }
        };
    }

    private void RegisterZx8xRamMenuItem(string name, Zx8xRamConfiguration configuration)
    {
        MenuItem item = FindRequiredControl<MenuItem>(name);
        _zx8xRamMenuItems[configuration] = item;
        item.Click += (_, _) =>
        {
            if (_updatingCommandChecks || configuration == _zx8xRamConfiguration)
            {
                UpdateZx8xRamMenuState();
                return;
            }

            _zx8xRamConfiguration = configuration;
            if (_zx8xModel.HasValue)
            {
                ReplaceZx8xMachine(_zx8xModel.Value);
            }

            UpdateZx8xRamMenuState();
            Focus();
        };
    }

    private void WireDiskMenus()
    {
        WirePlus3DiskMenu(0, "MenuPlus3AInsert", "MenuPlus3ANew", "MenuPlus3ASave", "MenuPlus3AEject");
        WirePlus3DiskMenu(1, "MenuPlus3BInsert", "MenuPlus3BNew", "MenuPlus3BSave", "MenuPlus3BEject");
        WireBetaDiskMenu(0, "MenuBetaAInsert", "MenuBetaASave", "MenuBetaAEject");
        WireBetaDiskMenu(1, "MenuBetaBInsert", "MenuBetaBSave", "MenuBetaBEject");
    }

    private void WirePlus3DiskMenu(int drive, string insertName, string newName, string saveName, string ejectName)
    {
        FindRequiredControl<MenuItem>(insertName).Click += async (_, _) => await InsertPlus3DiskAsync(drive);
        FindRequiredControl<MenuItem>(newName).Click += async (_, _) => await CreatePlus3DiskAsync(drive);
        _plus3SaveMenuItems[drive] = FindRequiredControl<MenuItem>(saveName);
        _plus3EjectMenuItems[drive] = FindRequiredControl<MenuItem>(ejectName);
        _plus3SaveMenuItems[drive].Click += async (_, _) => await SavePlus3DiskAsAsync(drive);
        _plus3EjectMenuItems[drive].Click += (_, _) => EjectPlus3Disk(drive);
    }

    private void WireBetaDiskMenu(int drive, string insertName, string saveName, string ejectName)
    {
        FindRequiredControl<MenuItem>(insertName).Click += async (_, _) => await InsertBetaDiskAsync(drive);
        _betaSaveMenuItems[drive] = FindRequiredControl<MenuItem>(saveName);
        _betaEjectMenuItems[drive] = FindRequiredControl<MenuItem>(ejectName);
        _betaSaveMenuItems[drive].Click += async (_, _) => await SaveBetaDiskAsAsync(drive);
        _betaEjectMenuItems[drive].Click += (_, _) => EjectBetaDisk(drive);
    }

    private async void OnOpenMediaClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
            {
                Title = "Open Spectrum media",
                Filters =
                [
                    new FileDialogFilter("Supported files", "*.z80", "*.sna", "*.o", "*.p", "*.81", "*.tap", "*.tzx", "*.csw", "*.dsk", "*.trd", "*.scl", "*.mdr", "*.img", "*.hdf", "*.sd", "*.bin"),
                    new FileDialogFilter("Snapshots", "*.z80", "*.sna"),
                    new FileDialogFilter("ZX80/ZX81 program images", "*.o", "*.p", "*.81"),
                    new FileDialogFilter("Tape images", "*.tap", "*.tzx", "*.csw"),
                    new FileDialogFilter("Disk images", "*.dsk", "*.trd", "*.scl"),
                    new FileDialogFilter("Microdrive cartridges", "*.mdr"),
                    new FileDialogFilter("DivMMC images", "*.img", "*.hdf", "*.sd", "*.bin"),
                    new FileDialogFilter("All files", "*.*")
                ]
            });
            if (path != null)
            {
                OpenMediaPath(path);
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Unable to open media: {ex.Message}";
        }
        finally
        {
            Focus();
        }
    }

    private void OpenMediaPath(string path)
    {
        switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
        {
            case ".z80":
                LoadSnapshotPath(path, isZ80: true);
                break;
            case ".sna":
                LoadSnapshotPath(path, isZ80: false);
                break;
            case ".o":
            case ".p":
            case ".81":
                LoadZx8xProgramImagePath(path);
                break;
            case ".tap":
            case ".tzx":
            case ".csw":
                AttachTapePath(path);
                break;
            case ".dsk":
                AttachPlus3DiskPath(path, GetInitialPlus3Drive());
                break;
            case ".trd":
            case ".scl":
                AttachBetaDiskPath(path, GetInitialBetaDrive());
                break;
            case ".img":
            case ".hdf":
            case ".sd":
            case ".bin":
                AttachDivMmcStorage(path, folderBacked: false);
                break;
            case ".mdr":
                AttachMicrodriveToFirstEmptyDrive(path);
                break;
            default:
                throw new NotSupportedException($"Unsupported media type: {System.IO.Path.GetExtension(path)}");
        }
    }

    private void LoadSnapshotPath(string path, bool isZ80)
    {
        SpectrumModel model = isZ80
            ? ZedExEss.FileHandlers.Z80Loader.DetectModel(path)
            : ZedExEss.FileHandlers.SnapshotLoader.DetectModel(path);
        ReplaceMachine(model, preserveTape: false, rewindTape: false, machine =>
        {
            if (isZ80)
            {
                ZedExEss.FileHandlers.Z80Loader.LoadZ80(machine.Cpu, machine.Memory, machine.Renderer, path);
            }
            else
            {
                ZedExEss.FileHandlers.SnapshotLoader.LoadSna(machine.Cpu, machine.Memory, machine.Renderer, path);
            }
        });
        _statusText.Text = $"Loaded {System.IO.Path.GetFileName(path)}";
    }

    private async void OnInsertDivMmcImageClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
            {
                Title = "Attach DivMMC SD image",
                Filters =
                [
                    new FileDialogFilter("DivMMC storage images", "*.img", "*.hdf", "*.sd", "*.bin"),
                    new FileDialogFilter("All files", "*.*")
                ]
            });
            if (path != null)
            {
                AttachDivMmcStorage(path, folderBacked: false);
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Unable to attach DivMMC image: {ex.Message}";
        }
        finally
        {
            Focus();
        }
    }

    private async void OnAttachDivMmcFolderClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            string? path = await _fileDialogs.OpenFolderAsync("Attach host folder as DivMMC storage");
            if (path != null)
            {
                AttachDivMmcStorage(path, folderBacked: true);
            }
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Unable to attach DivMMC folder: {ex.Message}";
        }
        finally
        {
            Focus();
        }
    }

    private void AttachDivMmcStorage(string path, bool folderBacked)
    {
        _session.DivMmc.Attach(path, folderBacked);
        if (_divExpansionMode != SpectrumDivExpansionMode.DivMmc)
        {
            _divExpansionMode = SpectrumDivExpansionMode.DivMmc;
            if (_machine != null)
            {
                ReplaceMachine(_machine.Model, preserveTape: true, rewindTape: false);
            }
        }

        _statusText.Text = $"Attached DivMMC {(folderBacked ? "folder" : "image")}: {System.IO.Path.GetFileName(path)}";
        UpdateDivMmcMenuState();
        UpdateDiskControls();
    }

    private void OnDivMmcEnabledClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _divExpansionMode = _divMmcEnabledMenuItem.IsChecked
            ? SpectrumDivExpansionMode.DivMmc
            : SpectrumDivExpansionMode.Disabled;
        if (_divExpansionMode != SpectrumDivExpansionMode.Disabled)
        {
            _interface1Enabled = false;
        }
        if (_machine != null)
        {
            ReplaceMachine(_machine.Model, preserveTape: true, rewindTape: false);
        }

        UpdateDivMmcMenuState();
        UpdateInterface1MenuState();
    }

    private void OnEjectDivMmcClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            _session.DivMmc.Eject();
            _statusText.Text = "DivMMC storage ejected";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Unable to eject DivMMC storage: {ex.Message}";
        }

        UpdateDivMmcMenuState();
        UpdateDiskControls();
        Focus();
    }

    private void OnNmiClicked(object? sender, RoutedEventArgs e)
    {
        _machine?.Cpu.Z80GenNMI();
        Focus();
    }

    private void OnToggleMediaBrowserClicked(object? sender, RoutedEventArgs e)
    {
        bool visible = sender == _showMediaBrowserMenuItem
            ? _showMediaBrowserMenuItem.IsChecked
            : !_mediaBrowserVisible;
        SetMediaBrowserVisible(visible, resizeWindow: true);
        Focus();
    }

    private void SetMediaBrowserVisible(bool visible, bool resizeWindow)
    {
        _mediaBrowserVisible = visible;
        _mediaBrowserPanel.IsVisible = visible;
        _mainContentGrid.ColumnDefinitions[1].Width = visible
            ? new GridLength(340)
            : new GridLength(0);
        _showMediaBrowserMenuItem.IsChecked = visible;
        _quickBrowserButton.CommandParameter = visible;
        ToolTip.SetTip(_quickBrowserButton, visible ? "Hide tape/disk browser" : "Show tape/disk browser");

        if (resizeWindow)
        {
            QueueResizeWindowToScreenZoom();
        }
    }

    private void UpdatePauseCommandState(bool paused)
    {
        if (_pauseGlyph == null)
        {
            return;
        }

        _pauseGlyph.IsVisible = !paused;
        _playGlyph.IsVisible = paused;
        ToolTip.SetTip(_pauseButton, paused ? "Resume emulation" : "Pause emulation");
    }

    private void UpdateModelMenuState(SpectrumModel? model, Zx8xModel? zx8xModel = null)
    {
        if (_modelMenuItems.Count == 0)
        {
            return;
        }

        _updatingCommandChecks = true;
        try
        {
            foreach ((SpectrumModel itemModel, MenuItem item) in _modelMenuItems)
            {
                item.IsChecked = itemModel == model;
            }

            foreach ((Zx8xModel itemModel, MenuItem item) in _zx8xModelMenuItems)
            {
                item.IsChecked = itemModel == zx8xModel;
            }
        }
        finally
        {
            _updatingCommandChecks = false;
        }

        UpdateZx8xRamMenuState();
    }

    private void UpdateZx8xRamMenuState()
    {
        if (_zx8xRamMenuItems.Count == 0)
        {
            return;
        }

        _updatingCommandChecks = true;
        try
        {
            foreach ((Zx8xRamConfiguration configuration, MenuItem item) in _zx8xRamMenuItems)
            {
                item.IsChecked = configuration == _zx8xRamConfiguration;
            }
        }
        finally
        {
            _updatingCommandChecks = false;
        }
    }

    private void UpdateJoystickMenuState()
    {
        if (_joystickMenuItems.Count == 0)
        {
            return;
        }

        _updatingCommandChecks = true;
        try
        {
            foreach ((SpectrumJoystickType type, MenuItem item) in _joystickMenuItems)
            {
                item.IsChecked = type == _joystickType;
            }
        }
        finally
        {
            _updatingCommandChecks = false;
        }
    }

    private void UpdateRuntimeMenuState()
    {
        if (_turboMenuItem == null)
        {
            return;
        }

        _updatingCommandChecks = true;
        try
        {
            _turboMenuItem.IsChecked = _turboEnabled;
            _pollingAccelerationMenuItem.IsChecked = _edgeLoadEnabled;
            _semanticAccelerationMenuItem.IsChecked = _semanticEdgeLoadEnabled;
            _maximumTapeSpeedMenuItem.IsChecked = _runTapeAccelerationAtMaximumSpeed;
            _flashLoadMenuItem.IsChecked = _flashLoadEnabled;
            _autoLoadTapeMenuItem.IsChecked = _autoLoadTapeOnAttach;
            _autoTapePlayMenuItem.IsChecked = _autoTapePlayStopEnabled;
            _gigascreenBlendMenuItem.IsChecked = _gigascreenBlendEnabled;
        }
        finally
        {
            _updatingCommandChecks = false;
        }
    }

    private void UpdateTapeMenuState(bool attached, bool playing)
    {
        if (_playTapeMenuItem == null)
        {
            return;
        }

        _playTapeMenuItem.IsEnabled = attached && !playing;
        _stopTapeMenuItem.IsEnabled = attached && playing;
        _rewindTapeMenuItem.IsEnabled = attached;
        _ejectTapeMenuItem.IsEnabled = attached;
    }

    private void UpdateDiskMenuState()
    {
        if (_plus3SaveMenuItems[0] == null)
        {
            return;
        }

        for (int drive = 0; drive < 2; drive++)
        {
            bool plus3Attached = _session.Disks.GetPlus3Image(drive) != null;
            bool betaAttached = _session.Disks.GetTrdImage(drive) != null;
            _plus3SaveMenuItems[drive].IsEnabled = plus3Attached;
            _plus3EjectMenuItems[drive].IsEnabled = plus3Attached;
            _betaSaveMenuItems[drive].IsEnabled = betaAttached;
            _betaEjectMenuItems[drive].IsEnabled = betaAttached;
        }
    }

    private void UpdateDivMmcMenuState()
    {
        if (_divMmcEnabledMenuItem == null)
        {
            return;
        }

        _updatingCommandChecks = true;
        try
        {
            _divMmcEnabledMenuItem.IsChecked = _divExpansionMode == SpectrumDivExpansionMode.DivMmc;
            _divMmcEjectMenuItem.IsEnabled = _session.DivMmc.IsAttached;
        }
        finally
        {
            _updatingCommandChecks = false;
        }
    }

    private bool TryHandleCommandKey(KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            OnNmiClicked(this, new RoutedEventArgs());
            e.Handled = true;
            return true;
        }

        if (e.Key == Key.F12)
        {
            OnDebuggerClicked(this, new RoutedEventArgs());
            e.Handled = true;
            return true;
        }

        return false;
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        IReadOnlyList<IStorageItem>? items = e.DataTransfer.TryGetFiles();
        e.DragEffects = items?.Any(IsSupportedStorageItem) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        IReadOnlyList<IStorageItem>? items = e.DataTransfer.TryGetFiles();
        if (items == null || items.Count == 0)
        {
            return;
        }

        int plus3Drive = GetInitialPlus3Drive();
        int betaDrive = GetInitialBetaDrive();
        var failures = new List<string>();
        foreach (IStorageItem item in items)
        {
            string? path = item.TryGetLocalPath();
            if (path == null)
            {
                failures.Add($"{item.Name}: no local filesystem path is available");
                continue;
            }

            try
            {
                if (item is IStorageFolder || Directory.Exists(path))
                {
                    AttachDivMmcStorage(path, folderBacked: true);
                    continue;
                }

                string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".dsk")
                {
                    AttachPlus3DiskPath(path, plus3Drive);
                    plus3Drive ^= 1;
                }
                else if (extension is ".trd" or ".scl")
                {
                    AttachBetaDiskPath(path, betaDrive);
                    betaDrive ^= 1;
                }
                else
                {
                    OpenMediaPath(path);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{item.Name}: {ex.Message}");
            }
        }

        if (failures.Count != 0)
        {
            _statusText.Text = "Some dropped media could not be attached: " + string.Join("; ", failures);
        }

        Focus();
    }

    private static bool IsSupportedStorageItem(IStorageItem item)
    {
        if (item is IStorageFolder)
        {
            return true;
        }

        string extension = System.IO.Path.GetExtension(item.Name).ToLowerInvariant();
        return extension is ".z80" or ".sna" or ".o" or ".p" or ".81" or ".tap" or ".tzx" or ".csw"
            or ".dsk" or ".trd" or ".scl" or ".mdr" or ".img" or ".hdf" or ".sd" or ".bin";
    }

    private int GetInitialPlus3Drive()
    {
        return _session.Disks.GetPlus3Image(0) == null ? 0
            : _session.Disks.GetPlus3Image(1) == null ? 1
            : 0;
    }

    private int GetInitialBetaDrive()
    {
        return _session.Disks.GetTrdImage(0) == null ? 0
            : _session.Disks.GetTrdImage(1) == null ? 1
            : 0;
    }
}
