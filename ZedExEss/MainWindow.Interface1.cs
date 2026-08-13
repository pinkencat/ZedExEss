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
    private readonly MenuItem[] _microdriveMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private readonly MenuItem[] _microdriveSaveMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private readonly MenuItem[] _microdriveEjectMenus = new MenuItem[SpectrumInterface1Device.DriveCount];
    private readonly MenuItem[] _microdriveWriteProtectMenus = new MenuItem[SpectrumInterface1Device.DriveCount];

    private void InitializeInterface1Ui()
    {
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
