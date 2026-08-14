using System.IO;
using System.Windows;
using System.Windows.Controls;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.Spectrum.Interface1;

namespace ZedExEss;

/// <summary>WPF commands for Interface 1 firmware selection and eight persistent Microdrives.</summary>
public partial class MainWindow
{
    private readonly SpectrumInterface1Rs232StreamEndpoint _interface1Rs232Endpoint = new();
    private SpectrumInterface1Rs232ConnectionManager _interface1Rs232Connection = null!;
    private SpectrumInterface1NetworkBridge _interface1NetworkBridge = null!;
    private readonly MenuItem[] _microdriveMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private readonly MenuItem[] _microdriveSaveMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private readonly MenuItem[] _microdriveEjectMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private readonly MenuItem[] _microdriveWriteProtectMenus = new MenuItem[SpectrumInterface1Device.DriveCount];

    private void InitializeInterface1Ui()
    {
        _interface1Rs232Connection = new SpectrumInterface1Rs232ConnectionManager(_interface1Rs232Endpoint);
        _interface1Rs232Connection.StatusChanged += OnInterface1Rs232ConnectionStatusChanged;
        _interface1Rs232Endpoint.Faulted += OnInterface1Rs232Faulted;
        _interface1NetworkBridge = new SpectrumInterface1NetworkBridge(
            _session.Interface1.NetworkBus,
            GetCurrentCpuCycles);
        _interface1NetworkBridge.StatusChanged += OnInterface1NetworkStatusChanged;
        for (int drive = 0; drive < SpectrumInterface1Device.DriveCount; drive++)
        {
            int number = drive + 1;
            _microdriveMenus[drive] = (MenuItem)FindName($"Microdrive{number}Menu");
            _microdriveSaveMenus[drive] = (MenuItem)FindName($"Microdrive{number}SaveMenu");
            _microdriveEjectMenus[drive] = (MenuItem)FindName($"Microdrive{number}EjectMenu");
            _microdriveWriteProtectMenus[drive] = (MenuItem)FindName($"Microdrive{number}WriteProtectMenu");
        }

        UpdateInterface1MenuState();
        UpdateInterface1ActivityStatus();
    }

    private void OnInterface1NetworkListen(object sender, RoutedEventArgs e)
    {
        var dialog = new Rs232EndpointDialog(
            this,
            "Listen for ZX Net peer",
            "Enter the TCP port on which this emulator should wait for another ZedExEss instance.",
            SpectrumInterface1NetworkBridge.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (dialog.ShowDialog() != true || dialog.EnteredValue is not string value)
        {
            return;
        }

        if (!int.TryParse(value, out int port) || (uint)(port - 1) >= 65_535u)
        {
            MessageBox.Show("TCP port must be between 1 and 65535.", "ZX Net", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _interface1NetworkBridge.Listen(port);
        UpdateInterface1MenuState();
    }

    private void OnInterface1NetworkConnect(object sender, RoutedEventArgs e)
    {
        var dialog = new Rs232EndpointDialog(
            this,
            "Connect to ZX Net peer",
            "Enter the peer as host:port. Start Listen in the other ZedExEss instance first.",
            $"127.0.0.1:{SpectrumInterface1NetworkBridge.DefaultPort}");
        if (dialog.ShowDialog() != true || dialog.EnteredValue is not string value ||
            !TryParseInterface1NetworkEndpoint(value, out string host, out int port))
        {
            return;
        }

        _interface1NetworkBridge.Connect(host, port);
        UpdateInterface1MenuState();
    }

    private void OnInterface1NetworkDisconnect(object sender, RoutedEventArgs e)
    {
        _interface1NetworkBridge.Disconnect();
        UpdateInterface1MenuState();
    }

    private void OnInterface1Rs232ConnectPipe(object sender, RoutedEventArgs e)
    {
        var dialog = new Rs232EndpointDialog(
            this,
            "Connect Interface 1 RS232 named pipe",
            "Enter the local named-pipe name. The emulator will reconnect automatically if the server is not yet available or disconnects.",
            _interface1Rs232Connection.Kind == SpectrumInterface1Rs232ConnectionKind.NamedPipe
                ? _interface1Rs232Connection.Target ?? string.Empty
                : string.Empty);
        if (dialog.ShowDialog() == true && dialog.EnteredValue is string pipeName)
        {
            _interface1Rs232Connection.ConnectNamedPipe(pipeName);
            UpdateInterface1MenuState();
        }
    }

    private void OnInterface1Rs232ConnectDevice(object sender, RoutedEventArgs e)
    {
        var dialog = new Rs232EndpointDialog(
            this,
            "Connect Interface 1 RS232 device",
            "Enter a duplex device or pseudo-terminal path, for example /dev/pts/3 or /dev/ttyUSB0. The emulator will reconnect automatically.",
            _interface1Rs232Connection.Kind == SpectrumInterface1Rs232ConnectionKind.Device
                ? _interface1Rs232Connection.Target ?? string.Empty
                : string.Empty);
        if (dialog.ShowDialog() == true && dialog.EnteredValue is string path)
        {
            _interface1Rs232Connection.ConnectDevice(path);
            UpdateInterface1MenuState();
        }
    }

    private void OnInterface1Rs232DisconnectLive(object sender, RoutedEventArgs e)
    {
        _interface1Rs232Connection.Disconnect();
        UpdateInterface1MenuState();
    }

    private async void OnInterface1Rs232AttachReceive(object sender, RoutedEventArgs e)
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
            UpdateInterface1MenuState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Interface 1 RS232 Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnInterface1Rs232AttachTransmit(object sender, RoutedEventArgs e)
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
            UpdateInterface1MenuState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Interface 1 RS232 Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnInterface1Rs232DetachReceive(object sender, RoutedEventArgs e)
    {
        _interface1Rs232Endpoint.DetachReceive();
        UpdateInterface1MenuState();
    }

    private void OnInterface1Rs232DetachTransmit(object sender, RoutedEventArgs e)
    {
        _interface1Rs232Endpoint.DetachTransmit();
        UpdateInterface1MenuState();
    }

    private void OnInterface1EnabledClick(object sender, RoutedEventArgs e)
    {
        _interface1Enabled = Interface1EnabledMenu.IsChecked;
        if (_interface1Enabled)
        {
            // Both devices assert ROMCS and decode overlapping ports; exposing both would create
            // an electrical configuration which neither firmware supports.
            _divExpansionMode = SpectrumDivExpansionMode.Disabled;
        }

        RebuildForInterface1Change();
    }

    private void OnInterface1RevisionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item
            || item.Tag is not string tag
            || !Enum.TryParse(tag, out SpectrumInterface1RomRevision revision))
        {
            return;
        }

        _interface1RomRevision = revision;
        if (_interface1Enabled)
        {
            RebuildForInterface1Change();
        }
        else
        {
            UpdateInterface1MenuState();
        }
    }

    private async void OnMicrodriveInsert(object sender, RoutedEventArgs e)
    {
        if (!TryGetDrive(sender, out int drive))
        {
            return;
        }

        string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
        {
            DefaultExtension = ".mdr",
            Filters =
            [
                new FileDialogFilter("Microdrive Cartridges", "*.mdr"),
                new FileDialogFilter("All Files", "*.*")
            ]
        });
        if (path != null)
        {
            AttachMicrodrive(drive, path);
        }
    }

    private async void OnMicrodriveCreate(object sender, RoutedEventArgs e)
    {
        if (!TryGetDrive(sender, out int drive))
        {
            return;
        }

        string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
        {
            DefaultExtension = ".mdr",
            SuggestedFileName = $"microdrive-{drive + 1}.mdr",
            ConfirmOverwrite = true,
            Filters =
            [
                new FileDialogFilter("Microdrive Cartridges", "*.mdr"),
                new FileDialogFilter("All Files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        try
        {
            _session.Interface1.Create(drive, cartridgeName: Path.GetFileNameWithoutExtension(path));
            _session.Interface1.SaveAs(drive, path);
            UpdateInterface1MenuState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Microdrive Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnMicrodriveSaveAs(object sender, RoutedEventArgs e)
    {
        if (!TryGetDrive(sender, out int drive) || !_session.Interface1.IsAttached(drive))
        {
            return;
        }

        string? existing = _session.Interface1.GetPath(drive);
        string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
        {
            DefaultExtension = ".mdr",
            SuggestedFileName = Path.GetFileName(existing) ?? $"microdrive-{drive + 1}.mdr",
            InitialDirectory = Path.GetDirectoryName(existing),
            ConfirmOverwrite = true,
            Filters =
            [
                new FileDialogFilter("Microdrive Cartridges", "*.mdr"),
                new FileDialogFilter("All Files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        try
        {
            _session.Interface1.SaveAs(drive, path);
            UpdateInterface1MenuState();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Microdrive Save Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnMicrodriveEject(object sender, RoutedEventArgs e)
    {
        if (!TryGetDrive(sender, out int drive))
        {
            return;
        }

        try
        {
            _session.Interface1.Eject(drive);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Microdrive Eject Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        UpdateInterface1MenuState();
    }

    private void OnMicrodriveWriteProtect(object sender, RoutedEventArgs e)
    {
        if (!TryGetDrive(sender, out int drive) || sender is not MenuItem item)
        {
            return;
        }

        _session.Interface1.SetWriteProtected(drive, item.IsChecked);
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
        UpdateInterface1MenuState();
    }

    private void RebuildForInterface1Change()
    {
        if (!TryLoadRoms(_model, out RomSet roms, out string error))
        {
            MessageBox.Show(error, "ROM Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateInterface1MenuState();
            return;
        }

        InitializeMachine(_model, roms, null, preserveTape: true);
    }

    private void UpdateInterface1MenuState()
    {
        if (Interface1EnabledMenu == null)
        {
            return;
        }

        bool compatible = SpectrumInterface1Compatibility.IsSupported(_model);
        Interface1EnabledMenu.IsChecked = _interface1Enabled;
        Interface1EnabledMenu.IsEnabled = compatible;
        Interface1Revision1Menu.IsChecked = _interface1RomRevision == SpectrumInterface1RomRevision.Revision1;
        Interface1Revision2Menu.IsChecked = _interface1RomRevision == SpectrumInterface1RomRevision.Revision2;
        UpdateInterface1Rs232MenuState();
        UpdateInterface1NetworkMenuState();

        for (int drive = 0; drive < _microdriveMenus.Length; drive++)
        {
            if (_microdriveMenus[drive] == null)
            {
                continue;
            }

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
            Interface1Rs232Menu.Header = $"RS232: {state} {target}{error}";
            Interface1Rs232DisconnectLiveMenu.IsEnabled = true;
            Interface1Rs232DetachReceiveMenu.IsEnabled = false;
            Interface1Rs232DetachTransmitMenu.IsEnabled = false;
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

        Interface1Rs232Menu.Header = $"RS232: {status}";
        Interface1Rs232DisconnectLiveMenu.IsEnabled = false;
        Interface1Rs232DetachReceiveMenu.IsEnabled = receive != null;
        Interface1Rs232DetachTransmitMenu.IsEnabled = transmit != null;
    }

    private void OnInterface1Rs232Faulted(Exception exception)
    {
        if (_interface1Rs232Connection.IsActive)
        {
            return;
        }

        _uiDispatcher.TryPost(() =>
        {
            UpdateInterface1MenuState();
            MessageBox.Show(
                exception.Message,
                "Interface 1 RS232 Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }, UiDispatchPriority.Background);
    }

    private void OnInterface1Rs232ConnectionStatusChanged()
    {
        _uiDispatcher.TryPost(UpdateInterface1MenuState, UiDispatchPriority.Background);
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
        Interface1NetworkMenu.Header = $"ZX Net: {state}{target}{error}";
        Interface1NetworkDisconnectMenu.IsEnabled = _interface1NetworkBridge.IsActive;
    }

    private void OnInterface1NetworkStatusChanged()
    {
        _uiDispatcher.TryPost(UpdateInterface1MenuState, UiDispatchPriority.Background);
    }

    private static bool TryParseInterface1NetworkEndpoint(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (!Uri.TryCreate($"tcp://{value.Trim()}", UriKind.Absolute, out Uri? endpoint) ||
            string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port is < 1 or > 65_535)
        {
            MessageBox.Show(
                "Enter a TCP endpoint in host:port form, for example 192.168.1.20:33501.",
                "ZX Net",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        host = endpoint.Host;
        port = endpoint.Port;
        return true;
    }

    /// <summary>
    /// Moves the host's status subscription when a machine graph is replaced. The core
    /// emits only drive/activity transitions, so this does not introduce per-frame polling.
    /// </summary>
    private void ObserveInterface1Device(SpectrumInterface1Device? device)
    {
        if (ReferenceEquals(_interface1Device, device))
        {
            return;
        }

        if (_interface1Device != null)
        {
            _interface1Device.StatusChanged -= OnInterface1StatusChanged;
        }

        _interface1Device = device;
        if (_interface1Device != null)
        {
            _interface1Device.StatusChanged += OnInterface1StatusChanged;
        }

        UpdateInterface1ActivityStatus();
    }

    private void OnInterface1StatusChanged()
    {
        _uiDispatcher.TryPost(UpdateInterface1ActivityStatus, UiDispatchPriority.Background);
    }

    private void UpdateInterface1ActivityStatus()
    {
        if (MicrodriveStatusText == null)
        {
            return;
        }

        SpectrumInterface1Device? device = _interface1Device;
        if (device == null)
        {
            MicrodriveStatusText.Text = "Microdrive: disabled";
            return;
        }

        int driveNumber = device.SelectedDriveNumber;
        if (driveNumber == 0)
        {
            MicrodriveStatusText.Text = "Microdrive: idle";
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
        MicrodriveStatusText.Text = $"Microdrive {driveNumber}: {name} — {activity}";
    }

    private static bool TryGetDrive(object sender, out int drive)
    {
        drive = -1;
        return sender is MenuItem { Tag: string tag }
            && int.TryParse(tag, out drive)
            && (uint)drive < SpectrumInterface1Device.DriveCount;
    }
}
