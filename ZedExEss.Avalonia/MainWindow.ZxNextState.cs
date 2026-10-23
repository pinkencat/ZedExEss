using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using ZedExEss.Hosting;
using ZedExEss.ZxNext.Core;

namespace ZedExEss.AvaloniaHost;

/// <summary>
/// Avalonia save states for the ZX Spectrum Next: ten in memory and files on disk.
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
        var saveParent = FindRequiredControl<MenuItem>("ZxNextSaveSlotMenuItem");
        var loadParent = FindRequiredControl<MenuItem>("ZxNextLoadSlotMenuItem");

        FindRequiredControl<MenuItem>("ZxNextSaveStateMenuItem").Click +=
            async (_, _) => await SaveZxNextStateAsync();
        FindRequiredControl<MenuItem>("ZxNextLoadStateMenuItem").Click +=
            async (_, _) => await LoadZxNextStateAsync();

        for (int index = 0; index < ZxNextStateSlots.SlotCount; index++)
        {
            int slot = index;

            var save = new MenuItem { Header = $"Slot _{slot}" };
            save.Click += (_, _) => SaveZxNextStateSlot(slot);
            saveParent.Items.Add(save);

            var load = new MenuItem { Header = $"Slot _{slot}" };
            load.Click += (_, _) => LoadZxNextStateSlot(slot);
            loadParent.Items.Add(load);
            _zxNextLoadSlotMenus[slot] = load;
        }

        UpdateZxNextStateMenuState();
    }

    private void UpdateZxNextStateMenuState()
    {
        FindRequiredControl<MenuItem>("ZxNextStateMenuItem").IsEnabled = _zxNextMachine != null;
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

    private async Task SaveZxNextStateAsync()
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
                new FileDialogFilter("ZX Next snapshots", "*" + ZxNextSnapshotFile.Extension),
                new FileDialogFilter("All files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        _statusText.Text = WithMachineStill(machine, () => ZxNextStateSlots.SaveToFile(path, machine));
    }

    private async Task LoadZxNextStateAsync()
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
                new FileDialogFilter("ZX Next snapshots", "*" + ZxNextSnapshotFile.Extension),
                new FileDialogFilter("All files", "*.*")
            ]
        });
        if (path == null)
        {
            return;
        }

        _statusText.Text = WithMachineStill(machine, () => ZxNextStateSlots.LoadFromFile(path, machine));
        machine.PublishFrame();
    }

    private void SaveZxNextStateSlot(int slot)
    {
        ZxNextMachine? machine = _zxNextMachine;
        if (machine == null)
        {
            return;
        }

        _statusText.Text = WithMachineStill(machine, () =>
        {
            _zxNextStates.Save(slot, machine);
            return $"Saved ZX Next state {slot.ToString(CultureInfo.InvariantCulture)}";
        });

        UpdateZxNextStateMenuState();
    }

    private void LoadZxNextStateSlot(int slot)
    {
        ZxNextMachine? machine = _zxNextMachine;
        if (machine == null)
        {
            return;
        }

        _statusText.Text = WithMachineStill(machine, () =>
            _zxNextStates.TryLoad(slot, machine, out ZxNextMediaRestoreReport media)
                ? $"Loaded ZX Next state {slot.ToString(CultureInfo.InvariantCulture)}"
                    + ZxNextStateSlots.DescribeMediaNotes(media)
                : $"ZX Next state {slot.ToString(CultureInfo.InvariantCulture)} is empty");

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
}
