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
    private readonly MenuItem[] _microdriveMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private readonly MenuItem[] _microdriveSaveMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private readonly MenuItem[] _microdriveEjectMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private readonly MenuItem[] _microdriveWriteProtectMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private MenuItem _interface1EnabledMenuItem = null!;
    private MenuItem _interface1Revision1MenuItem = null!;
    private MenuItem _interface1Revision2MenuItem = null!;
    private TextBlock _microdriveStatusText = null!;
    private SpectrumInterface1Device? _observedInterface1Device;

    private void InitializeInterface1Ui()
    {
        _interface1EnabledMenuItem = FindRequiredControl<MenuItem>("Interface1EnabledMenuItem");
        _interface1Revision1MenuItem = FindRequiredControl<MenuItem>("Interface1Revision1MenuItem");
        _interface1Revision2MenuItem = FindRequiredControl<MenuItem>("Interface1Revision2MenuItem");
        _microdriveStatusText = FindRequiredControl<TextBlock>("MicrodriveStatusText");
        _interface1EnabledMenuItem.Click += OnInterface1EnabledClicked;
        _interface1Revision1MenuItem.Click += (_, _) => SelectInterface1Revision(SpectrumInterface1RomRevision.Revision1);
        _interface1Revision2MenuItem.Click += (_, _) => SelectInterface1Revision(SpectrumInterface1RomRevision.Revision2);

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
