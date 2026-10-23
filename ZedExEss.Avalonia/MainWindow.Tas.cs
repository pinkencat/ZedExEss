using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Replay;

namespace ZedExEss.AvaloniaHost;

/// <summary>
/// Avalonia controls for building a run a frame at a time: frame advance and state slots.
/// </summary>
/// <remarks>
/// These are the two things a recording is actually made with. Neither goes anywhere near
/// a file dialog, because both are used hundreds of times in a sitting; states live in
/// memory for the life of the session and a snapshot file is the way to keep one.
/// </remarks>
public partial class MainWindow : ITasControls
{
    private readonly SpectrumStateSlots _stateSlots = new();
    private TasWindow? _tasWindow;

    /// <inheritdoc />
    public event Action? StatusChanged;

    /// <inheritdoc />
    public int SlotCount => SpectrumStateSlots.SlotCount;

    /// <inheritdoc />
    public TasStatus Status => new(
        _replaySession.CurrentFrame,
        _replaySession.FrameCount,
        _replaySession.ReRecordCount,
        _replaySession.Mode == SpectrumReplayMode.Recording,
        _replaySession.Mode == SpectrumReplayMode.Playing,
        _machine?.Emulator.IsPaused == true,
        _replaySession.RewindDepth,
        _replaySession.RewindSecondsAvailable,
        _replaySession.History.BytesUsed,
        _replaySession.History.BudgetBytes,
        _replaySession.History.UnsharedBytes,
        _replaySession.OldestFrameAvailable);

    /// <inheritdoc />
    public long HistoryBudgetBytes
    {
        get => _replaySession.History.BudgetBytes;
        set
        {
            _replaySession.History.BudgetBytes = value;
            StatusChanged?.Invoke();
        }
    }

    /// <inheritdoc />
    public bool IsSlotOccupied(int slot) => _stateSlots.IsOccupied(slot);

    /// <inheritdoc />
    public DateTimeOffset? SlotSavedAt(int slot) => _stateSlots.Peek(slot)?.SavedAt;

    /// <inheritdoc />
    public void AdvanceFrame() => AdvanceOneFrame();

    /// <inheritdoc />
    public void StepBackFrame() => StepReplayBackFrames(1);

    /// <inheritdoc />
    public void StepBackTenth() => StepReplayBack();

    /// <inheritdoc />
    public void SaveState(int slot) => SaveStateSlot(slot);

    /// <inheritdoc />
    public void LoadState(int slot) => LoadStateSlot(slot);

    /// <inheritdoc />
    public void ToggleRecording()
    {
        _replayRecordMenuItem.IsChecked = !_replayRecordMenuItem.IsChecked;
        OnReplayRecordClicked(this, new global::Avalonia.Interactivity.RoutedEventArgs());
        StatusChanged?.Invoke();
    }

    /// <inheritdoc />
    public void TogglePause()
    {
        OnPauseClicked(this, new global::Avalonia.Interactivity.RoutedEventArgs());
        StatusChanged?.Invoke();
    }

    /// <summary>Opens the TAS window, or brings the open one forward.</summary>
    private void ShowTasWindow()
    {
        if (_tasWindow != null)
        {
            _tasWindow.Activate();
            return;
        }

        _tasWindow = new TasWindow(this);
        _tasWindow.Closed += (_, _) => _tasWindow = null;
        _tasWindow.Show(this);
    }

    /// <summary>Goes back the given number of frames, reporting where it landed.</summary>
    private bool StepReplayBackFrames(int frames)
    {
        if (!_replaySession.StepBackFrames(frames))
        {
            _statusText.Text = "No history to step back through";
            return false;
        }

        _machine?.Emulator.PresentCurrentFrame();
        _statusText.Text = DescribeTasPosition(frames == 1 ? "Back one frame" : $"Back {frames} frames");
        UpdateRuntimeMenuState();
        StatusChanged?.Invoke();
        return true;
    }
    private readonly MenuItem?[] _stateLoadMenuItems = new MenuItem?[SpectrumStateSlots.SlotCount];

    /// <summary>Builds the two slot submenus, which are ten near-identical items each.</summary>
    private void BuildStateSlotMenus()
    {
        var save = FindRequiredControl<MenuItem>("StateSaveMenuItem");
        var load = FindRequiredControl<MenuItem>("StateLoadMenuItem");

        for (int index = 0; index < SpectrumStateSlots.SlotCount; index++)
        {
            int slot = index;

            var saveItem = new MenuItem
            {
                Header = $"Slot _{slot}",
                InputGesture = new KeyGesture(
                    Key.D0 + slot,
                    KeyModifiers.Control | KeyModifiers.Shift)
            };
            saveItem.Click += (_, _) => SaveStateSlot(slot);
            save.Items.Add(saveItem);

            var loadItem = new MenuItem
            {
                Header = $"Slot _{slot}",
                InputGesture = new KeyGesture(Key.D0 + slot, KeyModifiers.Control)
            };
            loadItem.Click += (_, _) => LoadStateSlot(slot);
            load.Items.Add(loadItem);
            _stateLoadMenuItems[slot] = loadItem;
        }

        UpdateStateSlotMenuState();
    }

    private void UpdateStateSlotMenuState()
    {
        for (int slot = 0; slot < SpectrumStateSlots.SlotCount; slot++)
        {
            if (_stateLoadMenuItems[slot] == null)
            {
                continue;
            }

            SpectrumStateSlotEntry? entry = _stateSlots.Peek(slot);
            _stateLoadMenuItems[slot]!.IsEnabled = entry != null;
            _stateLoadMenuItems[slot]!.Header = entry == null
                ? $"Slot _{slot} (empty)"
                : $"Slot _{slot} - {entry.SavedAt.ToLocalTime():HH:mm:ss}";
        }
    }

    /// <summary>
    /// Runs exactly one frame, pausing the machine first if it was running.
    /// </summary>
    /// <remarks>
    /// Pausing on the way in is what makes this usable as a single key: the first press
    /// stops the machine and the ones after it walk forward, which is how every run is
    /// built. Presentation is the emulator's job, so the picture keeps up even though the
    /// audio driver that normally produces frames has been shut down by the pause.
    /// </remarks>
    private bool AdvanceOneFrame()
    {
        if (_machine == null)
        {
            return false;
        }

        if (!_machine.Emulator.IsPaused)
        {
            _machine.Emulator.SetPaused(true);
            RefreshExecutionOwner();
            UpdatePauseCommandState(paused: true);
        }

        bool complete = _machine.Emulator.StepFrame();
        _statusText.Text = complete
            ? DescribeTasPosition("Frame advance")
            : "Stopped inside the frame - a breakpoint was hit";
        UpdateRuntimeMenuState();
        StatusChanged?.Invoke();
        return complete;
    }

    private bool SaveStateSlot(int slot)
    {
        if (_machine == null || !SpectrumStateSlots.IsValidSlot(slot))
        {
            return false;
        }

        try
        {
            // Reading the machine while another thread is running it would capture a
            // state from the middle of an instruction.
            _machine.Emulator.RunExclusive(() =>
                _stateSlots.Save(slot, _machine, _session.Interface1, _replaySession.Position));
        }
        catch (Exception ex)
        {
            _statusText.Text = $"State {slot} not saved: {ex.Message}";
            return false;
        }

        UpdateStateSlotMenuState();
        _statusText.Text = DescribeTasPosition($"Saved state {slot}");
        StatusChanged?.Invoke();
        return true;
    }

    private bool LoadStateSlot(int slot)
    {
        if (_machine == null)
        {
            return false;
        }

        SpectrumStateSlotEntry? entry = _stateSlots.Peek(slot);
        if (entry == null)
        {
            _statusText.Text = $"State {slot} is empty";
            return false;
        }

        bool loaded = false;
        SpectrumReplayPosition position = default;
        _machine.Emulator.RunExclusive(() =>
            loaded = _stateSlots.TryLoad(slot, _machine, _session.Interface1, out position));

        if (!loaded)
        {
            _statusText.Text =
                $"State {slot} holds a {entry.Snapshot.Model} machine, not a {_machine.Model}";
            return false;
        }

        // The recording has to come back to the same point, or it would describe frames
        // the machine is no longer going to play.
        _replaySession.OnMachineMovedBack(_machine, position);

        // Nothing has produced a frame edge, so the screen would otherwise still show
        // where the machine was before the load.
        _machine.Emulator.PresentCurrentFrame();

        UpdateRuntimeMenuState();
        _statusText.Text = DescribeTasPosition($"Loaded state {slot}");
        StatusChanged?.Invoke();
        return true;
    }

    /// <summary>Says where the run has got to: the frame, and what the recording holds.</summary>
    private string DescribeTasPosition(string prefix)
    {
        int frame = _machine?.Renderer.FrameCounter ?? 0;
        if (_replaySession.Mode != SpectrumReplayMode.Recording)
        {
            return $"{prefix} - frame {frame}";
        }

        string retakes = _replaySession.ReRecordCount == 1 ? "re-record" : "re-records";
        return $"{prefix} - frame {frame}, {_replaySession.FrameCount} recorded, "
            + $"{_replaySession.ReRecordCount} {retakes}";
    }

    /// <summary>Forgets every state, for when the machine they describe has been replaced.</summary>
    private void ClearStateSlots()
    {
        _stateSlots.Clear();
        UpdateStateSlotMenuState();
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// Ctrl and a digit loads that state slot; adding Shift saves it.
    /// </summary>
    /// <remarks>
    /// The bare digits belong to the Spectrum keyboard, so a modifier is not optional
    /// here. Ctrl is otherwise unused by the emulator, which is what makes it safe to
    /// take ten of them at once.
    /// </remarks>
    private bool TryHandleStateSlotKey(KeyEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Control) == 0)
        {
            return false;
        }

        int slot = e.Key switch
        {
            >= Key.D0 and <= Key.D9 => e.Key - Key.D0,
            >= Key.NumPad0 and <= Key.NumPad9 => e.Key - Key.NumPad0,
            _ => -1
        };

        if (slot < 0)
        {
            return false;
        }

        _ = (e.KeyModifiers & KeyModifiers.Shift) != 0
            ? SaveStateSlot(slot)
            : LoadStateSlot(slot);
        e.Handled = true;
        return true;
    }

    private void OnFrameAdvanceClicked(object? sender, RoutedEventArgs e)
    {
        AdvanceOneFrame();
        Focus();
    }
}
