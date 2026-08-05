using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ZedExEss.FileHandlers;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Disk.Beta;
using ZedExEss.Spectrum.Disk.Plus3;

namespace ZedExEss.AvaloniaHost;

public sealed partial class MainWindow
{
    private const double DiskActivityHoldSeconds = 0.12;

    private readonly ObservableCollection<BlockInfo> _tapeBlocks = [];
    private AvaloniaMachineDevices? _machineDevices;
    private ListBox _tapeBlocksList = null!;
    private TextBlock _tapeFileText = null!;
    private TextBlock _tapeBlockText = null!;
    private ProgressBar _tapeBlockProgress = null!;
    private TextBlock _diskStatusText = null!;
    private Border _diskActivityLight = null!;
    private readonly TextBlock[] _plus3PathTexts = new TextBlock[2];
    private readonly TextBlock[] _betaPathTexts = new TextBlock[2];
    private readonly Button[] _plus3SaveButtons = new Button[2];
    private readonly Button[] _plus3EjectButtons = new Button[2];
    private readonly Button[] _betaSaveButtons = new Button[2];
    private readonly Button[] _betaEjectButtons = new Button[2];
    private readonly CheckBox[] _plus3WriteProtect = new CheckBox[2];
    private readonly CheckBox[] _betaWriteProtect = new CheckBox[2];
    private int _lastTapeBlockIndex = -2;
    private long _lastDiskActivityCounter;
    private long _lastDiskActivityTimestamp;
    private bool _updatingDiskControls;

    private void InitializeMediaUi()
    {
        _tapeBlocksList = FindRequiredControl<ListBox>("TapeBlocksList");
        _tapeFileText = FindRequiredControl<TextBlock>("TapeFileText");
        _tapeBlockText = FindRequiredControl<TextBlock>("TapeBlockText");
        _tapeBlockProgress = FindRequiredControl<ProgressBar>("TapeBlockProgress");
        _diskStatusText = FindRequiredControl<TextBlock>("DiskStatusText");
        _diskActivityLight = FindRequiredControl<Border>("DiskActivityLight");
        _tapeBlocksList.ItemsSource = _tapeBlocks;
        _tapeBlocksList.DoubleTapped += OnTapeBlockDoubleTapped;

        for (int drive = 0; drive < 2; drive++)
        {
            int capturedDrive = drive;
            string suffix = drive == 0 ? "A" : "B";

            _plus3PathTexts[drive] = FindRequiredControl<TextBlock>($"Plus3{suffix}PathText");
            _betaPathTexts[drive] = FindRequiredControl<TextBlock>($"Beta{suffix}PathText");
            _plus3SaveButtons[drive] = FindRequiredControl<Button>($"Plus3{suffix}SaveButton");
            _plus3EjectButtons[drive] = FindRequiredControl<Button>($"Plus3{suffix}EjectButton");
            _betaSaveButtons[drive] = FindRequiredControl<Button>($"Beta{suffix}SaveButton");
            _betaEjectButtons[drive] = FindRequiredControl<Button>($"Beta{suffix}EjectButton");
            _plus3WriteProtect[drive] = FindRequiredControl<CheckBox>($"Plus3{suffix}WriteProtectCheckBox");
            _betaWriteProtect[drive] = FindRequiredControl<CheckBox>($"Beta{suffix}WriteProtectCheckBox");

            FindRequiredControl<Button>($"Plus3{suffix}InsertButton").Click +=
                async (_, _) => await InsertPlus3DiskAsync(capturedDrive);
            FindRequiredControl<Button>($"Plus3{suffix}NewButton").Click +=
                async (_, _) => await CreatePlus3DiskAsync(capturedDrive);
            _plus3SaveButtons[drive].Click += async (_, _) => await SavePlus3DiskAsAsync(capturedDrive);
            _plus3EjectButtons[drive].Click += (_, _) => EjectPlus3Disk(capturedDrive);

            FindRequiredControl<Button>($"Beta{suffix}InsertButton").Click +=
                async (_, _) => await InsertBetaDiskAsync(capturedDrive);
            _betaSaveButtons[drive].Click += async (_, _) => await SaveBetaDiskAsAsync(capturedDrive);
            _betaEjectButtons[drive].Click += (_, _) => EjectBetaDisk(capturedDrive);

            _plus3WriteProtect[drive].IsCheckedChanged += (_, _) => ApplyPlus3WriteProtect(capturedDrive);
            _betaWriteProtect[drive].IsCheckedChanged += (_, _) => ApplyBetaWriteProtect(capturedDrive);
        }

        RefreshTapeBlockList();
        UpdateDiskControls();
    }

    private void RefreshTapeBlockList()
    {
        _lastTapeBlockIndex = -2;
        _tapeBlocks.Clear();
        TzxLoader? tape = _session.Tape;
        if (tape == null)
        {
            return;
        }

        for (int index = 0; index < tape.Blocks.Count; index++)
        {
            _tapeBlocks.Add(new BlockInfo(index, tape.Blocks[index]));
        }
    }

    private void UpdateTapeBrowser()
    {
        TzxLoader? tape = _session.Tape;
        if (tape == null)
        {
            _tapeFileText.Text = "No tape loaded";
            _tapeBlockText.Text = "No tape loaded";
            _tapeBlockProgress.Value = 0;
            _tapeBlocksList.SelectedIndex = -1;
            _lastTapeBlockIndex = -2;
            return;
        }

        _tapeFileText.Text = Path.GetFileName(_session.TapePath);
        int blockIndex = tape.CurrentBlockIndex;
        if (blockIndex < 0 || blockIndex >= tape.Blocks.Count)
        {
            _tapeBlockText.Text = $"Block --/{tape.Blocks.Count}";
            _tapeBlockProgress.Value = 0;
            return;
        }

        double elapsed = tape.CurrentBlockElapsedSeconds;
        double duration = tape.CurrentBlockDurationSeconds;
        double progress = duration > 0
            ? Math.Clamp(elapsed / duration, 0, 1)
            : tape.CurrentBlockProgress;
        BlockInfo info = _tapeBlocks[blockIndex];
        _tapeBlockProgress.Value = progress;
        _tapeBlockText.Text =
            $"Block {blockIndex + 1}/{tape.Blocks.Count}: {info.Type} " +
            $"({elapsed:0.00}s / {duration:0.00}s, {progress * 100:0}%)";

        if (_lastTapeBlockIndex != blockIndex)
        {
            _lastTapeBlockIndex = blockIndex;
            _tapeBlocksList.SelectedIndex = blockIndex;
            _tapeBlocksList.ScrollIntoView(blockIndex);
        }
    }

    private void OnTapeBlockDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_session.Tape == null || _tapeBlocksList.SelectedItem is not BlockInfo block)
        {
            return;
        }

        _session.Tape.JumpToBlock(block.Index);
        UpdateTapeControls();
        Focus();
    }

    private async Task InsertPlus3DiskAsync(int drive)
    {
        await RunMediaOperationAsync(async () =>
        {
            string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
            {
                Title = $"Insert +3 disk in drive {DriveName(drive)}",
                Filters =
                [
                    new FileDialogFilter("+3 disk images", "*.dsk"),
                    new FileDialogFilter("All files", "*.*")
                ]
            });
            if (path == null)
            {
                return;
            }

            AttachPlus3DiskPath(path, drive);
        });
    }

    private async Task CreatePlus3DiskAsync(int drive)
    {
        await RunMediaOperationAsync(async () =>
        {
            string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
            {
                Title = $"Create +3 disk for drive {DriveName(drive)}",
                DefaultExtension = ".dsk",
                SuggestedFileName = $"blank-plus3-{DriveName(drive).ToLowerInvariant()}.dsk",
                ConfirmOverwrite = true,
                Filters = [new FileDialogFilter("+3 disk images", "*.dsk")]
            });
            if (path == null)
            {
                return;
            }

            Plus3DiskImage image = Plus3DiskImage.CreateBlankPlus3DataDisk(path);
            _session.Disks.SetPlus3(drive, image, path);
            _machineDevices?.Plus3DiskController?.InsertDisk(drive, image);
            _statusText.Text = $"Created {Path.GetFileName(path)} in +3 drive {DriveName(drive)}";
        });
    }

    private async Task InsertBetaDiskAsync(int drive)
    {
        await RunMediaOperationAsync(async () =>
        {
            string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
            {
                Title = $"Insert TR-DOS disk in drive {DriveName(drive)}",
                Filters =
                [
                    new FileDialogFilter("TR-DOS images", "*.trd", "*.scl"),
                    new FileDialogFilter("All files", "*.*")
                ]
            });
            if (path == null)
            {
                return;
            }

            AttachBetaDiskPath(path, drive);
        });
    }

    private void AttachPlus3DiskPath(string path, int drive)
    {
        Plus3DiskImage image = _session.Disks.LoadPlus3(drive, path);
        _machineDevices?.Plus3DiskController?.InsertDisk(drive, image);
        _statusText.Text = $"Inserted {Path.GetFileName(path)} in +3 drive {DriveName(drive)}";
        UpdateDiskControls();
    }

    private void AttachBetaDiskPath(string path, int drive)
    {
        TrdDiskImage image = _session.Disks.LoadTrd(drive, path);
        _machineDevices?.BetaDiskController?.InsertDisk(drive, image);
        _statusText.Text = $"Inserted {Path.GetFileName(path)} in Beta drive {DriveName(drive)}";
        UpdateDiskControls();
    }

    private async Task SavePlus3DiskAsAsync(int drive)
    {
        Plus3DiskImage? image = _session.Disks.GetPlus3Image(drive);
        if (image == null)
        {
            return;
        }

        await RunMediaOperationAsync(async () =>
        {
            string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
            {
                Title = $"Save +3 drive {DriveName(drive)} as",
                DefaultExtension = ".dsk",
                SuggestedFileName = Path.GetFileName(_session.Disks.GetPlus3Path(drive)) ?? $"drive-{DriveName(drive)}.dsk",
                ConfirmOverwrite = true,
                Filters = [new FileDialogFilter("+3 disk images", "*.dsk")]
            });
            if (path == null)
            {
                return;
            }

            image.SaveAs(path);
            _session.Disks.SetPlus3(drive, image, path);
            _statusText.Text = $"Saved +3 drive {DriveName(drive)} to {Path.GetFileName(path)}";
        });
    }

    private async Task SaveBetaDiskAsAsync(int drive)
    {
        TrdDiskImage? image = _session.Disks.GetTrdImage(drive);
        if (image == null)
        {
            return;
        }

        await RunMediaOperationAsync(async () =>
        {
            string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
            {
                Title = $"Save Beta drive {DriveName(drive)} as",
                DefaultExtension = ".trd",
                SuggestedFileName = Path.GetFileName(_session.Disks.GetTrdPath(drive)) ?? $"drive-{DriveName(drive)}.trd",
                ConfirmOverwrite = true,
                Filters =
                [
                    new FileDialogFilter("TR-DOS raw image", "*.trd"),
                    new FileDialogFilter("SCL compact image", "*.scl")
                ]
            });
            if (path == null)
            {
                return;
            }

            if (Path.GetExtension(path).Equals(".scl", StringComparison.OrdinalIgnoreCase))
            {
                image.ExportScl(path);
            }
            else
            {
                image.SaveAs(path);
                _session.Disks.SetTrd(drive, image, path);
            }

            _statusText.Text = $"Saved Beta drive {DriveName(drive)} to {Path.GetFileName(path)}";
        });
    }

    private void EjectPlus3Disk(int drive)
    {
        try
        {
            _session.Disks.EjectPlus3(drive);
            _machineDevices?.Plus3DiskController?.EjectDisk(drive);
            _statusText.Text = $"Ejected +3 drive {DriveName(drive)}";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Unable to eject +3 drive {DriveName(drive)}: {ex.Message}";
        }

        UpdateDiskControls();
        Focus();
    }

    private void EjectBetaDisk(int drive)
    {
        _session.Disks.EjectTrd(drive);
        _machineDevices?.BetaDiskController?.EjectDisk(drive);
        _statusText.Text = $"Ejected Beta drive {DriveName(drive)}";
        UpdateDiskControls();
        Focus();
    }

    private void ApplyPlus3WriteProtect(int drive)
    {
        if (_updatingDiskControls)
        {
            return;
        }

        Plus3DiskImage? image = _session.Disks.GetPlus3Image(drive);
        if (image != null)
        {
            image.IsWriteProtected = _plus3WriteProtect[drive].IsChecked == true;
            _machineDevices?.Plus3DiskController?.SetDriveWriteProtected(drive, image.IsWriteProtected);
        }

        UpdateDiskControls();
    }

    private void ApplyBetaWriteProtect(int drive)
    {
        if (_updatingDiskControls)
        {
            return;
        }

        TrdDiskImage? image = _session.Disks.GetTrdImage(drive);
        if (image != null)
        {
            bool writeProtected = _betaWriteProtect[drive].IsChecked == true;
            image.IsWriteProtected = image.SupportsRawWriteback ? writeProtected : true;
        }

        UpdateDiskControls();
    }

    private async Task RunMediaOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Disk operation failed: {ex.Message}";
        }
        finally
        {
            UpdateDiskControls();
            Focus();
        }
    }

    private void UpdateDiskControls()
    {
        if (_diskStatusText == null)
        {
            return;
        }

        _updatingDiskControls = true;
        try
        {
            for (int drive = 0; drive < 2; drive++)
            {
                Plus3DiskImage? plus3 = _session.Disks.GetPlus3Image(drive);
                TrdDiskImage? beta = _session.Disks.GetTrdImage(drive);
                _plus3PathTexts[drive].Text = FormatDiskPath(_session.Disks.GetPlus3Path(drive), plus3?.IsWriteProtected == true);
                _betaPathTexts[drive].Text = FormatDiskPath(_session.Disks.GetTrdPath(drive), beta?.IsWriteProtected == true);
                _plus3SaveButtons[drive].IsEnabled = plus3 != null;
                _plus3EjectButtons[drive].IsEnabled = plus3 != null;
                _betaSaveButtons[drive].IsEnabled = beta != null;
                _betaEjectButtons[drive].IsEnabled = beta != null;
                _plus3WriteProtect[drive].IsEnabled = plus3 != null;
                _plus3WriteProtect[drive].IsChecked = plus3?.IsWriteProtected == true;
                _betaWriteProtect[drive].IsEnabled = beta != null;
                _betaWriteProtect[drive].IsChecked = beta?.IsWriteProtected == true;
            }
        }
        finally
        {
            _updatingDiskControls = false;
        }

        string plus3Summary = FormatDrivePair("+3", true);
        string betaSummary = FormatDrivePair("Beta", false);
        string divMmcSummary = _session.DivMmc.Path == null
            ? ""
            : $"DivMMC: {(_session.DivMmc.IsFolderBacked ? _session.DivMmc.Path : Path.GetFileName(_session.DivMmc.Path))}";
        _diskStatusText.Text = plus3Summary == "" && betaSummary == "" && divMmcSummary == ""
            ? "Disk: none"
            : string.Join(Environment.NewLine, new[] { plus3Summary, betaSummary, divMmcSummary }.Where(text => text.Length != 0));

        long activity = (_machineDevices?.Plus3DiskController?.ActivityCounter ?? 0)
            + (_machineDevices?.BetaDiskController?.ActivityCounter ?? 0);
        long now = Stopwatch.GetTimestamp();
        if (activity != _lastDiskActivityCounter)
        {
            _lastDiskActivityCounter = activity;
            _lastDiskActivityTimestamp = now;
        }

        double elapsed = _lastDiskActivityTimestamp == 0
            ? double.MaxValue
            : (now - _lastDiskActivityTimestamp) / (double)Stopwatch.Frequency;
        _diskActivityLight.Background = elapsed <= DiskActivityHoldSeconds
            ? Brushes.LimeGreen
            : Brushes.DarkGreen;
        UpdateDiskMenuState();
    }

    private void ResetDiskActivityTracking()
    {
        _lastDiskActivityCounter = (_machineDevices?.Plus3DiskController?.ActivityCounter ?? 0)
            + (_machineDevices?.BetaDiskController?.ActivityCounter ?? 0);
        _lastDiskActivityTimestamp = 0;
    }

    private string FormatDrivePair(string label, bool plus3)
    {
        string? pathA = plus3 ? _session.Disks.GetPlus3Path(0) : _session.Disks.GetTrdPath(0);
        string? pathB = plus3 ? _session.Disks.GetPlus3Path(1) : _session.Disks.GetTrdPath(1);
        if (pathA == null && pathB == null)
        {
            return "";
        }

        return $"{label} A: {Path.GetFileName(pathA) ?? "(none)"} | B: {Path.GetFileName(pathB) ?? "(none)"}";
    }

    private static string FormatDiskPath(string? path, bool writeProtected)
    {
        return path == null ? "(none)" : $"{Path.GetFileName(path)}{(writeProtected ? " [RO]" : "")}";
    }

    private static string DriveName(int drive) => drive == 0 ? "A" : "B";
}
