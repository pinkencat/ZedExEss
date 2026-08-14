using Avalonia.Controls;
using Avalonia.Interactivity;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.Spectrum.Interface1;

namespace ZedExEss.AvaloniaHost;

/// <summary>Avalonia commands for Interface 1 firmware and persistent Microdrive media.</summary>
public sealed partial class MainWindow
{
    private readonly SpectrumInterface1Rs232StreamEndpoint _interface1Rs232Endpoint = new();
    private SpectrumInterface1Rs232ConnectionManager _interface1Rs232Connection = null!;
    private SpectrumInterface1NetworkBridge _interface1NetworkBridge = null!;
    private readonly MenuItem[] _microdriveMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private readonly MenuItem[] _microdriveSaveMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private readonly MenuItem[] _microdriveEjectMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private readonly MenuItem[] _microdriveWriteProtectMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private MenuItem _interface1EnabledMenuItem = null!;
    private MenuItem _interface1Revision1MenuItem = null!;
    private MenuItem _interface1Revision2MenuItem = null!;
    private MenuItem _interface1Rs232MenuItem = null!;
    private MenuItem _interface1Rs232DisconnectLiveMenuItem = null!;
    private MenuItem _interface1Rs232DetachReceiveMenuItem = null!;
    private MenuItem _interface1Rs232DetachTransmitMenuItem = null!;
    private MenuItem _interface1NetworkMenuItem = null!;
    private MenuItem _interface1NetworkDisconnectMenuItem = null!;
    private TextBlock _microdriveStatusText = null!;
    private SpectrumInterface1Device? _observedInterface1Device;

    private void InitializeInterface1Ui()
    {
        _interface1EnabledMenuItem = FindRequiredControl<MenuItem>("Interface1EnabledMenuItem");
        _interface1Revision1MenuItem = FindRequiredControl<MenuItem>("Interface1Revision1MenuItem");
        _interface1Revision2MenuItem = FindRequiredControl<MenuItem>("Interface1Revision2MenuItem");
        _interface1Rs232MenuItem = FindRequiredControl<MenuItem>("Interface1Rs232MenuItem");
        _interface1Rs232DisconnectLiveMenuItem = FindRequiredControl<MenuItem>("Interface1Rs232DisconnectLiveMenuItem");
        _interface1Rs232DetachReceiveMenuItem = FindRequiredControl<MenuItem>("Interface1Rs232DetachReceiveMenuItem");
        _interface1Rs232DetachTransmitMenuItem = FindRequiredControl<MenuItem>("Interface1Rs232DetachTransmitMenuItem");
        _interface1NetworkMenuItem = FindRequiredControl<MenuItem>("Interface1NetworkMenuItem");
        _interface1NetworkDisconnectMenuItem = FindRequiredControl<MenuItem>("Interface1NetworkDisconnectMenuItem");
        _microdriveStatusText = FindRequiredControl<TextBlock>("MicrodriveStatusText");
        _interface1Rs232Connection = new SpectrumInterface1Rs232ConnectionManager(_interface1Rs232Endpoint);
        _interface1Rs232Connection.StatusChanged += OnInterface1Rs232ConnectionStatusChanged;
        _interface1Rs232Endpoint.Faulted += OnInterface1Rs232Faulted;
        _interface1NetworkBridge = new SpectrumInterface1NetworkBridge(
            _session.Interface1.NetworkBus,
            () => _machine?.Cpu.Cyc ?? 0);
        _interface1NetworkBridge.StatusChanged += OnInterface1NetworkStatusChanged;
        _interface1EnabledMenuItem.Click += OnInterface1EnabledClicked;
        _interface1Revision1MenuItem.Click += (_, _) => SelectInterface1Revision(SpectrumInterface1RomRevision.Revision1);
        _interface1Revision2MenuItem.Click += (_, _) => SelectInterface1Revision(SpectrumInterface1RomRevision.Revision2);
        FindRequiredControl<MenuItem>("Interface1Rs232ConnectPipeMenuItem").Click += async (_, _) => await ConnectInterface1Rs232PipeAsync();
        FindRequiredControl<MenuItem>("Interface1Rs232ConnectDeviceMenuItem").Click += async (_, _) => await ConnectInterface1Rs232DeviceAsync();
        _interface1Rs232DisconnectLiveMenuItem.Click += (_, _) => DisconnectInterface1Rs232Live();
        FindRequiredControl<MenuItem>("Interface1Rs232AttachReceiveMenuItem").Click += async (_, _) => await AttachInterface1Rs232ReceiveAsync();
        _interface1Rs232DetachReceiveMenuItem.Click += (_, _) => DetachInterface1Rs232Receive();
        FindRequiredControl<MenuItem>("Interface1Rs232AttachTransmitMenuItem").Click += async (_, _) => await AttachInterface1Rs232TransmitAsync();
        _interface1Rs232DetachTransmitMenuItem.Click += (_, _) => DetachInterface1Rs232Transmit();
        FindRequiredControl<MenuItem>("Interface1NetworkListenMenuItem").Click += async (_, _) => await ListenInterface1NetworkAsync();
        FindRequiredControl<MenuItem>("Interface1NetworkConnectMenuItem").Click += async (_, _) => await ConnectInterface1NetworkAsync();
        _interface1NetworkDisconnectMenuItem.Click += (_, _) => DisconnectInterface1Network();

        for (int drive = 0; drive < SpectrumInterface1Device.DriveCount; drive++)
        {
            int slot = drive;
            int number = drive + 1;
            _microdriveMenus[drive] = FindRequiredControl<MenuItem>($"Microdrive{number}MenuItem");
            _microdriveSaveMenus[drive] = FindRequiredControl<MenuItem>($"Microdrive{number}SaveMenuItem");
            _microdriveEjectMenus[drive] = FindRequiredControl<MenuItem>($"Microdrive{number}EjectMenuItem");
            _microdriveWriteProtectMenus[drive] = FindRequiredControl<MenuItem>($"Microdrive{number}WriteProtectMenuItem");
            FindRequiredControl<MenuItem>($"Microdrive{number}InsertMenuItem").Click += async (_, _) => await InsertMicrodriveAsync(slot);
            FindRequiredControl<MenuItem>($"Microdrive{number}NewMenuItem").Click += async (_, _) => await CreateMicrodriveAsync(slot);
            _microdriveSaveMenus[drive].Click += async (_, _) => await SaveMicrodriveAsAsync(slot);
            _microdriveEjectMenus[drive].Click += (_, _) => EjectMicrodrive(slot);
            _microdriveWriteProtectMenus[drive].Click += (_, _) => ToggleMicrodriveWriteProtection(slot);
        }

        UpdateInterface1MenuState();
        UpdateInterface1ActivityStatus();
    }

    private async Task ListenInterface1NetworkAsync()
    {
        var dialog = new Rs232EndpointDialog(
            "Listen for ZX Net peer",
            "Enter the TCP port on which this emulator should wait for another ZedExEss instance.",
            SpectrumInterface1NetworkBridge.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string? value = await dialog.ShowDialog<string?>(this);
        if (value == null)
        {
            return;
        }

        if (!int.TryParse(value, out int port) || (uint)(port - 1) >= 65_535u)
        {
            _statusText.Text = "ZX Net TCP port must be between 1 and 65535.";
            return;
        }

        _interface1NetworkBridge.Listen(port);
        UpdateInterface1MenuState();
    }

    private async Task ConnectInterface1NetworkAsync()
    {
        var dialog = new Rs232EndpointDialog(
            "Connect to ZX Net peer",
            "Enter the peer as host:port. Start Listen in the other ZedExEss instance first.",
            $"127.0.0.1:{SpectrumInterface1NetworkBridge.DefaultPort}");
        string? value = await dialog.ShowDialog<string?>(this);
        if (value == null || !TryParseInterface1NetworkEndpoint(value, out string host, out int port))
        {
            return;
        }

        _interface1NetworkBridge.Connect(host, port);
        UpdateInterface1MenuState();
    }

    private void DisconnectInterface1Network()
    {
        _interface1NetworkBridge.Disconnect();
        UpdateInterface1MenuState();
    }

    private async Task ConnectInterface1Rs232PipeAsync()
    {
        var dialog = new Rs232EndpointDialog(
            "Connect Interface 1 RS232 named pipe",
            "Enter the local named-pipe name. The emulator will reconnect automatically if the server is not yet available or disconnects.",
            _interface1Rs232Connection.Kind == SpectrumInterface1Rs232ConnectionKind.NamedPipe
                ? _interface1Rs232Connection.Target ?? string.Empty
                : string.Empty);
        string? pipeName = await dialog.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(pipeName))
        {
            _interface1Rs232Connection.ConnectNamedPipe(pipeName);
            UpdateInterface1MenuState();
        }
    }

    private async Task ConnectInterface1Rs232DeviceAsync()
    {
        var dialog = new Rs232EndpointDialog(
            "Connect Interface 1 RS232 device",
            "Enter a duplex device or pseudo-terminal path, for example /dev/pts/3 or /dev/ttyUSB0. The emulator will reconnect automatically.",
            _interface1Rs232Connection.Kind == SpectrumInterface1Rs232ConnectionKind.Device
                ? _interface1Rs232Connection.Target ?? string.Empty
                : string.Empty);
        string? path = await dialog.ShowDialog<string?>(this);
        if (!string.IsNullOrWhiteSpace(path))
        {
            _interface1Rs232Connection.ConnectDevice(path);
            UpdateInterface1MenuState();
        }
    }

    private void DisconnectInterface1Rs232Live()
    {
        _interface1Rs232Connection.Disconnect();
        UpdateInterface1MenuState();
    }

    private async Task AttachInterface1Rs232ReceiveAsync()
    {
        string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
        {
            Title = "Attach Interface 1 RS232 receive file",
            Filters = [new FileDialogFilter("All files", "*.*")]
        });
        if (path == null)
        {
            return;
        }

        try
        {
            _interface1Rs232Connection.Disconnect();
            _interface1Rs232Endpoint.AttachReceiveFile(path);
            _statusText.Text = $"Interface 1 RS232 receive: {Path.GetFileName(path)}";
            UpdateInterface1MenuState();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Unable to attach RS232 receive file: {ex.Message}";
        }
    }

    private async Task AttachInterface1Rs232TransmitAsync()
    {
        string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
        {
            Title = "Attach Interface 1 RS232 transmit file",
            DefaultExtension = ".bin",
            SuggestedFileName = "interface1-rs232-output.bin",
            ConfirmOverwrite = true,
            Filters =
            [
                new FileDialogFilter("Binary files", "*.bin"),
                new FileDialogFilter("All files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        try
        {
            _interface1Rs232Connection.Disconnect();
            _interface1Rs232Endpoint.AttachTransmitFile(path);
            _statusText.Text = $"Interface 1 RS232 transmit: {Path.GetFileName(path)}";
            UpdateInterface1MenuState();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Unable to attach RS232 transmit file: {ex.Message}";
        }
    }

    private void DetachInterface1Rs232Receive()
    {
        _interface1Rs232Endpoint.DetachReceive();
        UpdateInterface1MenuState();
    }

    private void DetachInterface1Rs232Transmit()
    {
        _interface1Rs232Endpoint.DetachTransmit();
        UpdateInterface1MenuState();
    }

    private void OnInterface1EnabledClicked(object? sender, RoutedEventArgs e)
    {
        if (_updatingCommandChecks)
        {
            return;
        }

        _interface1Enabled = _interface1EnabledMenuItem.IsChecked;
        if (_interface1Enabled)
        {
            _divExpansionMode = SpectrumDivExpansionMode.Disabled;
        }

        if (_machine != null)
        {
            ReplaceMachine(_machine.Model, preserveTape: true, rewindTape: false);
        }

        UpdateInterface1MenuState();
        UpdateDivMmcMenuState();
    }

    private void SelectInterface1Revision(SpectrumInterface1RomRevision revision)
    {
        if (_updatingCommandChecks || revision == _interface1RomRevision)
        {
            return;
        }

        _interface1RomRevision = revision;
        if (_interface1Enabled && _machine != null)
        {
            ReplaceMachine(_machine.Model, preserveTape: true, rewindTape: false);
        }

        UpdateInterface1MenuState();
    }

    private async Task InsertMicrodriveAsync(int drive)
    {
        string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
        {
            Title = $"Insert cartridge in Microdrive {drive + 1}",
            DefaultExtension = ".mdr",
            Filters =
            [
                new FileDialogFilter("Microdrive cartridges", "*.mdr"),
                new FileDialogFilter("All files", "*.*")
            ]
        });
        if (path != null)
        {
            AttachMicrodrive(drive, path);
        }
    }

    private async Task CreateMicrodriveAsync(int drive)
    {
        string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
        {
            Title = $"Create cartridge for Microdrive {drive + 1}",
            DefaultExtension = ".mdr",
            SuggestedFileName = $"microdrive-{drive + 1}.mdr",
            ConfirmOverwrite = true,
            Filters =
            [
                new FileDialogFilter("Microdrive cartridges", "*.mdr"),
                new FileDialogFilter("All files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        _session.Interface1.Create(drive, cartridgeName: Path.GetFileNameWithoutExtension(path));
        _session.Interface1.SaveAs(drive, path);
        _statusText.Text = $"Created Microdrive {drive + 1}: {Path.GetFileName(path)}";
        UpdateInterface1MenuState();
    }

    private async Task SaveMicrodriveAsAsync(int drive)
    {
        if (!_session.Interface1.IsAttached(drive))
        {
            return;
        }

        string? existing = _session.Interface1.GetPath(drive);
        string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
        {
            Title = $"Save Microdrive {drive + 1}",
            DefaultExtension = ".mdr",
            SuggestedFileName = Path.GetFileName(existing) ?? $"microdrive-{drive + 1}.mdr",
            InitialDirectory = Path.GetDirectoryName(existing),
            ConfirmOverwrite = true,
            Filters =
            [
                new FileDialogFilter("Microdrive cartridges", "*.mdr"),
                new FileDialogFilter("All files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        _session.Interface1.SaveAs(drive, path);
        _statusText.Text = $"Saved Microdrive {drive + 1}: {Path.GetFileName(path)}";
        UpdateInterface1MenuState();
    }

    private void EjectMicrodrive(int drive)
    {
        _session.Interface1.Eject(drive);
        _statusText.Text = $"Microdrive {drive + 1} ejected";
        UpdateInterface1MenuState();
    }

    private void ToggleMicrodriveWriteProtection(int drive)
    {
        _session.Interface1.SetWriteProtected(drive, _microdriveWriteProtectMenus[drive].IsChecked);
        UpdateInterface1MenuState();
    }

    private void AttachMicrodriveToFirstEmptyDrive(string path)
    {
        int drive = _session.Interface1.GetFirstEmptyDrive();
        if (drive < 0)
        {
            throw new InvalidOperationException("All eight Microdrive slots already contain cartridges.");
        }

        AttachMicrodrive(drive, path);
    }

    private void AttachMicrodrive(int drive, string path)
    {
        _session.Interface1.Attach(drive, path);
        _statusText.Text = $"Microdrive {drive + 1}: {Path.GetFileName(path)}";
        UpdateInterface1MenuState();
    }

    private void UpdateInterface1MenuState()
    {
        if (_interface1EnabledMenuItem == null)
        {
            return;
        }

        bool compatible = SpectrumInterface1Compatibility.IsSupported(_machine?.Model ?? SpectrumModel.Spectrum128K);
        _updatingCommandChecks = true;
        try
        {
            _interface1EnabledMenuItem.IsChecked = _interface1Enabled;
            _interface1EnabledMenuItem.IsEnabled = compatible;
            _interface1Revision1MenuItem.IsChecked = _interface1RomRevision == SpectrumInterface1RomRevision.Revision1;
            _interface1Revision2MenuItem.IsChecked = _interface1RomRevision == SpectrumInterface1RomRevision.Revision2;
            UpdateInterface1Rs232MenuState();
            UpdateInterface1NetworkMenuState();

            for (int drive = 0; drive < _microdriveMenus.Length; drive++)
            {
                MicrodriveCartridge? cartridge = _session.Interface1.GetCartridge(drive);
                string? path = _session.Interface1.GetPath(drive);
                bool attached = cartridge != null;
                _microdriveMenus[drive].Header = attached
                    ? $"Drive {drive + 1}: {Path.GetFileName(path) ?? "(unsaved)"}"
                    : $"Drive {drive + 1}: (empty)";
                _microdriveSaveMenus[drive].IsEnabled = attached;
                _microdriveEjectMenus[drive].IsEnabled = attached;
                _microdriveWriteProtectMenus[drive].IsEnabled = attached;
                _microdriveWriteProtectMenus[drive].IsChecked = cartridge?.WriteProtected == true;
            }
        }
        finally
        {
            _updatingCommandChecks = false;
        }

        UpdateInterface1ActivityStatus();
    }

    private void UpdateInterface1Rs232MenuState()
    {
        if (_interface1Rs232Connection.IsActive)
        {
            string target = _interface1Rs232Connection.Target ?? "endpoint";
            string state = _interface1Rs232Connection.State switch
            {
                SpectrumInterface1Rs232ConnectionState.Connecting => "connecting",
                SpectrumInterface1Rs232ConnectionState.Connected => "connected",
                SpectrumInterface1Rs232ConnectionState.Reconnecting => "reconnecting",
                _ => "disconnected"
            };
            string error = _interface1Rs232Connection.LastError is string message
                ? $" ({message})"
                : string.Empty;
            _interface1Rs232MenuItem.Header = $"RS232: {state} {target}{error}";
            _interface1Rs232DisconnectLiveMenuItem.IsEnabled = true;
            _interface1Rs232DetachReceiveMenuItem.IsEnabled = false;
            _interface1Rs232DetachTransmitMenuItem.IsEnabled = false;
            return;
        }

        string? receive = _interface1Rs232Endpoint.ReceiveName;
        string? transmit = _interface1Rs232Endpoint.TransmitName;
        string status = (receive, transmit) switch
        {
            (null, null) => "disconnected",
            (not null, null) => $"RX {Path.GetFileName(receive)}",
            (null, not null) => $"TX {Path.GetFileName(transmit)}",
            _ => $"RX {Path.GetFileName(receive)}, TX {Path.GetFileName(transmit)}"
        };

        _interface1Rs232MenuItem.Header = $"RS232: {status}";
        _interface1Rs232DisconnectLiveMenuItem.IsEnabled = false;
        _interface1Rs232DetachReceiveMenuItem.IsEnabled = receive != null;
        _interface1Rs232DetachTransmitMenuItem.IsEnabled = transmit != null;
    }

    private void OnInterface1Rs232Faulted(Exception exception)
    {
        if (_interface1Rs232Connection.IsActive)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateInterface1MenuState();
            _statusText.Text = $"Interface 1 RS232 error: {exception.Message}";
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnInterface1Rs232ConnectionStatusChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            UpdateInterface1MenuState,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void UpdateInterface1NetworkMenuState()
    {
        string state = _interface1NetworkBridge.State switch
        {
            SpectrumInterface1NetworkBridgeState.Connecting => "connecting",
            SpectrumInterface1NetworkBridgeState.Listening => "listening",
            SpectrumInterface1NetworkBridgeState.Connected => "connected",
            _ => "disconnected"
        };
        string target = _interface1NetworkBridge.Target is string endpoint
            ? $" {endpoint}"
            : string.Empty;
        string error = _interface1NetworkBridge.LastError is string message
            ? $" ({message})"
            : string.Empty;
        _interface1NetworkMenuItem.Header = $"ZX Net: {state}{target}{error}";
        _interface1NetworkDisconnectMenuItem.IsEnabled = _interface1NetworkBridge.IsActive;
    }

    private void OnInterface1NetworkStatusChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            UpdateInterface1MenuState,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private bool TryParseInterface1NetworkEndpoint(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (!Uri.TryCreate($"tcp://{value.Trim()}", UriKind.Absolute, out Uri? endpoint) ||
            string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port is < 1 or > 65_535)
        {
            _statusText.Text = "Enter a ZX Net endpoint as host:port, for example 192.168.1.20:33501.";
            return false;
        }

        host = endpoint.Host;
        port = endpoint.Port;
        return true;
    }

    private void ObserveInterface1Device(SpectrumInterface1Device? device)
    {
        if (ReferenceEquals(_observedInterface1Device, device))
        {
            return;
        }

        if (_observedInterface1Device != null)
        {
            _observedInterface1Device.StatusChanged -= OnInterface1StatusChanged;
        }

        _observedInterface1Device = device;
        if (_observedInterface1Device != null)
        {
            _observedInterface1Device.StatusChanged += OnInterface1StatusChanged;
        }

        UpdateInterface1ActivityStatus();
    }

    private void OnInterface1StatusChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(
            UpdateInterface1ActivityStatus,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private void UpdateInterface1ActivityStatus()
    {
        if (_microdriveStatusText == null)
        {
            return;
        }

        SpectrumInterface1Device? device = _observedInterface1Device;
        if (device == null)
        {
            _microdriveStatusText.Text = "Microdrive: disabled";
            return;
        }

        int driveNumber = device.SelectedDriveNumber;
        if (driveNumber == 0)
        {
            _microdriveStatusText.Text = "Microdrive: idle";
            return;
        }

        int drive = driveNumber - 1;
        string name = Path.GetFileName(_session.Interface1.GetPath(drive)) ?? "(empty)";
        string activity = device.Activity switch
        {
            MicrodriveActivityState.Reading => "reading",
            MicrodriveActivityState.Writing => "writing",
            _ => "selected"
        };
        _microdriveStatusText.Text = $"Microdrive {driveNumber}: {name} — {activity}";
    }
}
