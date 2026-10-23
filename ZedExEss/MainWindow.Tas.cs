using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Replay;

namespace ZedExEss;

/// <summary>
/// WPF controls for building a run a frame at a time: frame advance and state slots.
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
        _emulator?.IsPaused == true,
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
        ReplayRecordMenu.IsChecked = !ReplayRecordMenu.IsChecked;
        OnReplayRecordToggle(ReplayRecordMenu, new RoutedEventArgs());
        StatusChanged?.Invoke();
    }

    /// <inheritdoc />
    public void TogglePause()
    {
        ToggleQuickPause();
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

        _tasWindow = new TasWindow(this) { Owner = this };
        _tasWindow.Closed += (_, _) => _tasWindow = null;
        _tasWindow.Show();
    }

    private void OnShowTasWindow(object sender, RoutedEventArgs e)
    {
        ShowTasWindow();
        Focus();
    }

    /// <summary>Goes back the given number of frames, reporting where it landed.</summary>
    private bool StepReplayBackFrames(int frames)
    {
        if (!_replaySession.StepBackFrames(frames))
        {
            StatusText.Text = "No history to step back through";
            return false;
        }

        _emulator?.PresentCurrentFrame();
        StatusText.Text = DescribeTasPosition(frames == 1 ? "Back one frame" : $"Back {frames} frames");
        UpdateReplayMenuState();
        StatusChanged?.Invoke();
        return true;
    }
    private readonly MenuItem?[] _stateLoadMenus = new MenuItem?[SpectrumStateSlots.SlotCount];

    /// <summary>
    /// Builds the two slot submenus, which are ten near-identical items each.
    /// </summary>
    private void BuildStateSlotMenus()
    {
        for (int slot = 0; slot < SpectrumStateSlots.SlotCount; slot++)
        {
            string tag = slot.ToString(CultureInfo.InvariantCulture);

            var save = new MenuItem
            {
                Header = $"Slot _{slot}",
                Tag = tag,
                InputGestureText = $"Ctrl+Shift+{slot}"
            };
            save.Click += OnSaveStateSlot;
            StateSaveMenu.Items.Add(save);

            var load = new MenuItem
            {
                Header = $"Slot _{slot}",
                Tag = tag,
                InputGestureText = $"Ctrl+{slot}"
            };
            load.Click += OnLoadStateSlot;
            StateLoadMenu.Items.Add(load);
            _stateLoadMenus[slot] = load;
        }

        UpdateStateSlotMenuState();
    }

    private void UpdateStateSlotMenuState()
    {
        for (int slot = 0; slot < SpectrumStateSlots.SlotCount; slot++)
        {
            if (_stateLoadMenus[slot] == null)
            {
                continue;
            }

            SpectrumStateSlotEntry? entry = _stateSlots.Peek(slot);
            _stateLoadMenus[slot]!.IsEnabled = entry != null;
            _stateLoadMenus[slot]!.Header = entry == null
                ? $"Slot _{slot} (empty)"
                : $"Slot _{slot} - {entry.SavedAt.ToLocalTime():HH:mm:ss}";
        }
    }

    private void OnFrameAdvance(object sender, RoutedEventArgs e)
    {
        AdvanceOneFrame();
        Focus();
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
        if (_emulator == null)
        {
            return false;
        }

        if (!_emulator.IsPaused)
        {
            PauseFromQuickAccess();
            UpdateQuickAccessState();
        }

        bool complete = _emulator.StepFrame();
        StatusText.Text = complete
            ? DescribeTasPosition("Frame advance")
            : "Stopped inside the frame - a breakpoint was hit";
        UpdateQuickAccessState();
        StatusChanged?.Invoke();
        return complete;
    }


    private void OnStepBackOneFrame(object sender, RoutedEventArgs e)
    {
        StepReplayBackFrames(1);
        Focus();
    }
    private void OnSaveStateSlot(object sender, RoutedEventArgs e)
    {
        if (TryGetStateSlot(sender, out int slot))
        {
            SaveStateSlot(slot);
        }

        Focus();
    }

    private void OnLoadStateSlot(object sender, RoutedEventArgs e)
    {
        if (TryGetStateSlot(sender, out int slot))
        {
            LoadStateSlot(slot);
        }

        Focus();
    }

    private bool SaveStateSlot(int slot)
    {
        if (_machine == null || _emulator == null || !SpectrumStateSlots.IsValidSlot(slot))
        {
            return false;
        }

        try
        {
            // Reading the machine while another thread is running it would capture a
            // state from the middle of an instruction.
            _emulator.RunExclusive(() =>
                _stateSlots.Save(slot, _machine, _session.Interface1, _replaySession.Position));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"State {slot} not saved: {ex.Message}";
            return false;
        }

        UpdateStateSlotMenuState();
        StatusText.Text = DescribeTasPosition($"Saved state {slot}");
        StatusChanged?.Invoke();
        return true;
    }

    private bool LoadStateSlot(int slot)
    {
        if (_machine == null || _emulator == null)
        {
            return false;
        }

        SpectrumStateSlotEntry? entry = _stateSlots.Peek(slot);
        if (entry == null)
        {
            StatusText.Text = $"State {slot} is empty";
            return false;
        }

        bool loaded = false;
        SpectrumReplayPosition position = default;
        _emulator.RunExclusive(() =>
            loaded = _stateSlots.TryLoad(slot, _machine, _session.Interface1, out position));

        if (!loaded)
        {
            StatusText.Text = $"State {slot} holds a {entry.Snapshot.Model} machine, not a {_model}";
            return false;
        }

        // The recording has to come back to the same point, or it would describe frames
        // the machine is no longer going to play.
        _replaySession.OnMachineMovedBack(_machine, position);

        // Nothing has produced a frame edge, so the screen would otherwise still show
        // where the machine was before the load.
        _emulator.PresentCurrentFrame();

        UpdateReplayMenuState();
        StatusText.Text = DescribeTasPosition($"Loaded state {slot}");
        StatusChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Says where the run has got to: the frame, and what the recording holds.
    /// </summary>
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

    private static bool TryGetStateSlot(object sender, out int slot)
    {
        slot = -1;
        return sender is FrameworkElement { Tag: string tag }
            && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out slot)
            && SpectrumStateSlots.IsValidSlot(slot);
    }
}
