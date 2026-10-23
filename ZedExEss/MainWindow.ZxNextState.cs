using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ZedExEss.Hosting;
using ZedExEss.ZxNext.Core;

namespace ZedExEss;

/// <summary>
/// WPF save states for the ZX Spectrum Next: ten in memory and files on disk.
/// </summary>
/// <remarks>
/// Separate from the Spectrum's state slots rather than shared with them, because the two
/// machines have nothing in common to capture: a Next snapshot is its own record and its own
/// file format. What is shared is the shape of the commands, so the menu reads the same.
/// </remarks>
public partial class MainWindow
{
    private readonly ZxNextStateSlots _zxNextStates = new();
    private readonly MenuItem?[] _zxNextLoadSlotMenus = new MenuItem?[ZxNextStateSlots.SlotCount];

    /// <summary>Builds the two slot submenus, which are ten near-identical items each.</summary>
    private void BuildZxNextStateMenus()
    {
        for (int slot = 0; slot < ZxNextStateSlots.SlotCount; slot++)
        {
            string tag = slot.ToString(CultureInfo.InvariantCulture);

            var save = new MenuItem { Header = $"Slot _{slot}", Tag = tag };
            save.Click += OnSaveZxNextStateSlot;
            ZxNextStateSaveMenu.Items.Add(save);

            var load = new MenuItem { Header = $"Slot _{slot}", Tag = tag };
            load.Click += OnLoadZxNextStateSlot;
            ZxNextStateLoadMenu.Items.Add(load);
            _zxNextLoadSlotMenus[slot] = load;
        }

        UpdateZxNextStateMenuState();
    }

    private void UpdateZxNextStateMenuState()
    {
        if (ZxNextStateMenu == null)
        {
            return;
        }

        ZxNextStateMenu.IsEnabled = _zxNextMachine != null;
        for (int slot = 0; slot < _zxNextLoadSlotMenus.Length; slot++)
        {
            if (_zxNextLoadSlotMenus[slot] == null)
            {
                continue;
            }

            ZxNextStateSlotEntry? entry = _zxNextStates.Peek(slot);
            _zxNextLoadSlotMenus[slot]!.IsEnabled = entry != null && _zxNextMachine != null;
            _zxNextLoadSlotMenus[slot]!.Header = entry == null
                ? $"Slot _{slot} (empty)"
                : $"Slot _{slot} - {entry.SavedAt.ToLocalTime():HH:mm:ss}";
        }
    }

    /// <summary>Forgets every state, for when the machine they describe has been replaced.</summary>
    private void ClearZxNextStates()
    {
        _zxNextStates.Clear();
        UpdateZxNextStateMenuState();
    }

    private async void OnSaveZxNextState(object sender, RoutedEventArgs e)
    {
        ZxNextMachine? machine = _zxNextMachine;
        if (machine == null)
        {
            return;
        }

        string? path = await _fileDialogs.SaveFileAsync(new FileDialogOptions
        {
            DefaultExtension = ZxNextSnapshotFile.Extension,
            SuggestedFileName = "state" + ZxNextSnapshotFile.Extension,
            ConfirmOverwrite = true,
            Filters =
            [
                new FileDialogFilter("ZX Next Snapshots", "*" + ZxNextSnapshotFile.Extension),
                new FileDialogFilter("All Files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        StatusText.Text = WithMachineStill(machine, () => ZxNextStateSlots.SaveToFile(path, machine));
    }

    private async void OnLoadZxNextState(object sender, RoutedEventArgs e)
    {
        ZxNextMachine? machine = _zxNextMachine;
        if (machine == null)
        {
            return;
        }

        string? path = await _fileDialogs.OpenFileAsync(new FileDialogOptions
        {
            DefaultExtension = ZxNextSnapshotFile.Extension,
            Filters =
            [
                new FileDialogFilter("ZX Next Snapshots", "*" + ZxNextSnapshotFile.Extension),
                new FileDialogFilter("All Files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        StatusText.Text = WithMachineStill(machine, () => ZxNextStateSlots.LoadFromFile(path, machine));
        machine.PublishFrame();
    }

    private void OnSaveZxNextStateSlot(object sender, RoutedEventArgs e)
    {
        ZxNextMachine? machine = _zxNextMachine;
        if (machine == null || !TryGetZxNextSlot(sender, out int slot))
        {
            return;
        }

        StatusText.Text = WithMachineStill(machine, () =>
        {
            _zxNextStates.Save(slot, machine);
            return $"Saved ZX Next state {slot}";
        });

        UpdateZxNextStateMenuState();
    }

    private void OnLoadZxNextStateSlot(object sender, RoutedEventArgs e)
    {
        ZxNextMachine? machine = _zxNextMachine;
        if (machine == null || !TryGetZxNextSlot(sender, out int slot))
        {
            return;
        }

        StatusText.Text = WithMachineStill(machine, () =>
            _zxNextStates.TryLoad(slot, machine, out ZxNextMediaRestoreReport media)
                ? $"Loaded ZX Next state {slot}{ZxNextStateSlots.DescribeMediaNotes(media)}"
                : $"ZX Next state {slot} is empty");

        machine.PublishFrame();
    }

    /// <summary>
    /// Runs something with the machine held still, then puts it back as it was.
    /// </summary>
    /// <remarks>
    /// Capturing or replacing state while the audio thread is running the CPU would read or
    /// write from the middle of an instruction. Pausing is cheap and the machine is paused for
    /// a few milliseconds, which nobody notices.
    /// </remarks>
    private static string WithMachineStill(ZxNextMachine machine, Func<string> action)
    {
        bool wasPaused = machine.IsPaused;
        machine.SetPaused(true);
        try
        {
            return action();
        }
        finally
        {
            machine.SetPaused(wasPaused);
        }
    }

    private static bool TryGetZxNextSlot(object sender, out int slot)
    {
        slot = -1;
        return sender is FrameworkElement { Tag: string tag }
            && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot)
            && ZxNextStateSlots.IsValidSlot(slot);
    }
}
