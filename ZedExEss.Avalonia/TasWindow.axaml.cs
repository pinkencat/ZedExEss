using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ZedExEss.Hosting;

namespace ZedExEss.AvaloniaHost;

/// <summary>
/// The controls a run is built with, gathered where they can be reached without the menu
/// bar.
/// </summary>
/// <remarks>
/// A window rather than a tab in the media browser because the piano roll will need
/// height that a 300-pixel side panel does not have, and because a run is built with this
/// open alongside the screen rather than instead of the tape it is loading from.
///
/// It holds no state of its own: everything comes from <see cref="ITasControls"/>, so the
/// buttons here and the keys in the main window are the same commands and cannot drift
/// apart.
/// </remarks>
public partial class TasWindow : Window
{
    /// <summary>How often the counters catch up while the machine is running.</summary>
    /// <remarks>
    /// Ten times a second: fast enough that the frame counter looks live, slow enough that
    /// it costs nothing. Commands refresh immediately as well, so anything the user does
    /// appears at once rather than up to a tenth of a second later.
    /// </remarks>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    private readonly ITasControls _controls = null!;
    private readonly DispatcherTimer _timer = null!;
    /// <summary>The sizes the history may be limited to.</summary>
    private static readonly long[] BudgetChoices =
    [
        32L * 1024 * 1024,
        64L * 1024 * 1024,
        128L * 1024 * 1024,
        256L * 1024 * 1024,
        512L * 1024 * 1024,
        1024L * 1024 * 1024
    ];

    private readonly Button[] _saveButtons = [];
    private readonly Button[] _loadButtons = [];
    private bool _settingBudget;

    /// <summary>Design-time constructor; the host always uses the one taking controls.</summary>
    public TasWindow()
    {
        InitializeComponent();
    }

    public TasWindow(ITasControls controls)
    {
        _controls = controls ?? throw new ArgumentNullException(nameof(controls));
        InitializeComponent();

        FindRequired<Button>("AdvanceButton").Click += (_, _) => _controls.AdvanceFrame();
        FindRequired<Button>("BackFrameButton").Click += (_, _) => _controls.StepBackFrame();
        FindRequired<Button>("BackTenthButton").Click += (_, _) => _controls.StepBackTenth();
        FindRequired<Button>("PauseButton").Click += (_, _) => _controls.TogglePause();
        FindRequired<Button>("RecordButton").Click += (_, _) => _controls.ToggleRecording();

        _saveButtons = new Button[_controls.SlotCount];
        _loadButtons = new Button[_controls.SlotCount];
        BuildSlotButtons();
        BuildBudgetChoices();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _timer.Tick += (_, _) => Refresh();
        _controls.StatusChanged += OnStatusChanged;

        Opened += (_, _) =>
        {
            _timer.Start();
            Refresh();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _controls.StatusChanged -= OnStatusChanged;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private T FindRequired<T>(string name) where T : Control =>
        this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"The TAS window is missing '{name}'.");

    private void BuildSlotButtons()
    {
        var savePanel = FindRequired<StackPanel>("SaveSlotPanel");
        var loadPanel = FindRequired<StackPanel>("LoadSlotPanel");

        for (int index = 0; index < _controls.SlotCount; index++)
        {
            int slot = index;
            string label = slot.ToString(CultureInfo.InvariantCulture);

            var save = new Button { Content = label };
            save.Classes.Add("slot");
            ToolTip.SetTip(save, $"Save state {slot} (Ctrl+Shift+{slot})");
            save.Click += (_, _) => _controls.SaveState(slot);
            savePanel.Children.Add(save);
            _saveButtons[slot] = save;

            var load = new Button { Content = label };
            load.Classes.Add("slot");
            load.Click += (_, _) => _controls.LoadState(slot);
            loadPanel.Children.Add(load);
            _loadButtons[slot] = load;
        }
    }

    private void BuildBudgetChoices()
    {
        var combo = FindRequired<ComboBox>("BudgetCombo");
        _settingBudget = true;
        try
        {
            var labels = new List<string>(BudgetChoices.Length);
            foreach (long budget in BudgetChoices)
            {
                labels.Add($"{budget / (1024 * 1024)} MB");
            }

            combo.ItemsSource = labels;

            int current = Array.IndexOf(BudgetChoices, _controls.HistoryBudgetBytes);
            combo.SelectedIndex = current >= 0
                ? current
                : Array.FindIndex(BudgetChoices, choice => choice >= _controls.HistoryBudgetBytes);
        }
        finally
        {
            _settingBudget = false;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (!_settingBudget && combo.SelectedIndex >= 0)
            {
                _controls.HistoryBudgetBytes = BudgetChoices[combo.SelectedIndex];
            }
        };
    }

    private void OnStatusChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Refresh();
            return;
        }

        Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        TasStatus status = _controls.Status;

        FindRequired<TextBlock>("FrameText").Text = $"Frame {status.Frame:N0}";

        var recording = FindRequired<TextBlock>("RecordingText");
        recording.Text = DescribeRun(status);
        recording.Foreground = new SolidColorBrush(status.IsRecording
            ? Color.FromRgb(0xFF, 0x8A, 0x8A)
            : Color.FromRgb(0xB8, 0xD7, 0xFF));

        FindRequired<Button>("PauseButton").Content = status.IsPaused ? "▶" : "⏸";
        FindRequired<Button>("RecordButton").Foreground = new SolidColorBrush(status.IsRecording
            ? Color.FromRgb(0xFF, 0x3B, 0x3B)
            : Color.FromRgb(0xFF, 0x6B, 0x6B));

        bool canStepBack = status.HistoryStates > 0 && status.Frame > status.OldestFrame;
        FindRequired<Button>("BackTenthButton").IsEnabled = canStepBack;
        FindRequired<Button>("BackFrameButton").IsEnabled = canStepBack;

        for (int slot = 0; slot < _loadButtons.Length; slot++)
        {
            bool occupied = _controls.IsSlotOccupied(slot);
            _loadButtons[slot].IsEnabled = occupied;
            ToolTip.SetTip(_loadButtons[slot], occupied
                ? $"Load state {slot}, saved {_controls.SlotSavedAt(slot)?.ToLocalTime():HH:mm:ss} (Ctrl+{slot})"
                : $"State {slot} is empty");
            _saveButtons[slot].FontWeight = occupied ? FontWeight.Bold : FontWeight.Normal;
        }

        UpdateHistory(status);
    }

    private void UpdateHistory(TasStatus status)
    {
        var bar = FindRequired<ProgressBar>("HistoryBar");
        var text = FindRequired<TextBlock>("HistoryText");

        if (status.HistoryStates == 0)
        {
            bar.Value = 0;
            text.Text = "No history yet";
            return;
        }

        // Against the budget, because how close the history is to its limit is the thing
        // that decides whether the oldest part of the run starts thinning.
        bar.Value = Math.Clamp(
            status.HistoryBytes / (double)Math.Max(1, status.HistoryBudgetBytes), 0, 1);

        double saved = status.HistoryUnsharedBytes / (double)Math.Max(1, status.HistoryBytes);
        text.Text = $"{status.HistoryStates:N0} states · {DescribeSpan(status.HistorySeconds)} · "
            + $"{status.HistoryBytes / (1024.0 * 1024.0):F1} of "
            + $"{status.HistoryBudgetBytes / (1024 * 1024)} MB · shared {saved:F0}x";
    }

    private static string DescribeRun(TasStatus status)
    {
        if (status.IsPlaying)
        {
            return $"Replaying {status.RecordedFrames:N0} frames";
        }

        if (!status.IsRecording)
        {
            return status.RecordedFrames > 0
                ? $"Stopped · {status.RecordedFrames:N0} frames recorded"
                : "Not recording";
        }

        string retakes = status.ReRecords == 1 ? "re-record" : "re-records";
        return $"Recording · {status.RecordedFrames:N0} frames · {status.ReRecords:N0} {retakes}";
    }

    private static string DescribeSpan(double seconds) => seconds >= 60
        ? $"{seconds / 60:F1} min"
        : $"{seconds:F1}s";
}
