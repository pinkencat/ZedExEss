using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ZedExEss.Hosting;

namespace ZedExEss
{
    /// <summary>
    /// The controls a run is built with, gathered where they can be reached without the
    /// menu bar.
    /// </summary>
    /// <remarks>
    /// A window rather than a tab in the media browser because the piano roll will need
    /// height that a 300-pixel side panel does not have, and because a run is built with
    /// this open alongside the screen rather than instead of the tape it is loading from.
    ///
    /// It holds no state of its own: everything comes from <see cref="ITasControls"/>, so
    /// the buttons here and the keys in the main window are the same commands and cannot
    /// drift apart.
    /// </remarks>
    public partial class TasWindow : Window
    {
        /// <summary>How often the counters catch up while the machine is running.</summary>
        /// <remarks>
        /// Ten times a second: fast enough that the frame counter looks live, slow enough
        /// that it costs nothing. Commands refresh immediately as well, so anything the
        /// user does appears at once rather than up to a tenth of a second later.
        /// </remarks>
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

        private readonly ITasControls _controls;
        private readonly DispatcherTimer _timer;
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

        private readonly Button[] _saveButtons;
        private readonly Button[] _loadButtons;
        private bool _settingBudget;

        public TasWindow(ITasControls controls)
        {
            _controls = controls ?? throw new ArgumentNullException(nameof(controls));
            InitializeComponent();

            _saveButtons = new Button[_controls.SlotCount];
            _loadButtons = new Button[_controls.SlotCount];
            BuildSlotButtons();
            BuildBudgetChoices();

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
            _timer.Tick += (_, _) => Refresh();
            _controls.StatusChanged += OnStatusChanged;

            Loaded += (_, _) =>
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

        private void BuildSlotButtons()
        {
            for (int slot = 0; slot < _controls.SlotCount; slot++)
            {
                string label = slot.ToString(CultureInfo.InvariantCulture);

                var save = new Button
                {
                    Content = label,
                    Tag = slot,
                    Style = (Style)FindResource("SlotButtonStyle"),
                    ToolTip = $"Save state {slot} (Ctrl+Shift+{slot})"
                };
                save.Click += OnSaveSlot;
                SaveSlotPanel.Children.Add(save);
                _saveButtons[slot] = save;

                var load = new Button
                {
                    Content = label,
                    Tag = slot,
                    Style = (Style)FindResource("SlotButtonStyle"),
                    ToolTip = $"Load state {slot} (Ctrl+{slot})"
                };
                load.Click += OnLoadSlot;
                LoadSlotPanel.Children.Add(load);
                _loadButtons[slot] = load;
            }
        }

        private void BuildBudgetChoices()
        {
            _settingBudget = true;
            try
            {
                foreach (long budget in BudgetChoices)
                {
                    BudgetCombo.Items.Add($"{budget / (1024 * 1024)} MB");
                }

                int current = Array.IndexOf(BudgetChoices, _controls.HistoryBudgetBytes);
                BudgetCombo.SelectedIndex = current >= 0
                    ? current
                    : Array.FindIndex(BudgetChoices, choice => choice >= _controls.HistoryBudgetBytes);
            }
            finally
            {
                _settingBudget = false;
            }
        }

        private void OnBudgetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settingBudget || BudgetCombo.SelectedIndex < 0)
            {
                return;
            }

            _controls.HistoryBudgetBytes = BudgetChoices[BudgetCombo.SelectedIndex];
        }

        private void OnStatusChanged()
        {
            if (Dispatcher.CheckAccess())
            {
                Refresh();
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);
        }

        private void Refresh()
        {
            TasStatus status = _controls.Status;

            FrameText.Text = $"Frame {status.Frame:N0}";
            RecordingText.Text = DescribeRun(status);
            RecordingText.Foreground = status.IsRecording
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A))
                : new SolidColorBrush(Color.FromRgb(0xB8, 0xD7, 0xFF));

            PauseButton.Content = status.IsPaused ? "▶" : "⏸";
            RecordButton.Foreground = status.IsRecording
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x3B))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

            bool canStepBack = status.HistoryStates > 0 && status.Frame > status.OldestFrame;
            BackTenthButton.IsEnabled = canStepBack;
            BackFrameButton.IsEnabled = canStepBack;

            for (int slot = 0; slot < _loadButtons.Length; slot++)
            {
                bool occupied = _controls.IsSlotOccupied(slot);
                _loadButtons[slot].IsEnabled = occupied;
                _loadButtons[slot].ToolTip = occupied
                    ? $"Load state {slot}, saved {_controls.SlotSavedAt(slot)?.ToLocalTime():HH:mm:ss} (Ctrl+{slot})"
                    : $"State {slot} is empty";
                _saveButtons[slot].FontWeight = occupied ? FontWeights.Bold : FontWeights.Normal;
            }

            UpdateHistory(status);
        }

        private void UpdateHistory(TasStatus status)
        {
            if (status.HistoryStates == 0)
            {
                HistoryBar.Value = 0;
                HistoryText.Text = "No history yet";
                return;
            }

            // Against the budget, because how close the history is to its limit is the
            // thing that decides whether the oldest part of the run starts thinning.
            HistoryBar.Value = Math.Clamp(
                status.HistoryBytes / (double)Math.Max(1, status.HistoryBudgetBytes), 0, 1);

            double saved = status.HistoryUnsharedBytes / (double)Math.Max(1, status.HistoryBytes);
            HistoryText.Text =
                $"{status.HistoryStates:N0} states · {DescribeSpan(status.HistorySeconds)} · "
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
            return $"Recording · {status.RecordedFrames:N0} frames · "
                + $"{status.ReRecords:N0} {retakes}";
        }

        private static string DescribeSpan(double seconds) => seconds >= 60
            ? $"{seconds / 60:F1} min"
            : $"{seconds:F1}s";

        private void OnAdvanceFrame(object sender, RoutedEventArgs e) => _controls.AdvanceFrame();

        private void OnStepBackFrame(object sender, RoutedEventArgs e) => _controls.StepBackFrame();

        private void OnStepBackTenth(object sender, RoutedEventArgs e) => _controls.StepBackTenth();

        private void OnTogglePause(object sender, RoutedEventArgs e) => _controls.TogglePause();

        private void OnToggleRecording(object sender, RoutedEventArgs e) => _controls.ToggleRecording();

        private void OnSaveSlot(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: int slot })
            {
                _controls.SaveState(slot);
            }
        }

        private void OnLoadSlot(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: int slot })
            {
                _controls.LoadState(slot);
            }
        }
    }
}
