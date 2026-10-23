using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ZedExEss.Spectrum.Audio;

namespace ZedExEss
{
    /// <summary>Live beeper and per-channel PSG waveform viewer.</summary>
    /// <remarks>
    /// Attaching installs the source's optional capture sink; closing removes it
    /// completely, so ordinary emulation pays no ring-buffer or drawing cost while the
    /// oscilloscope is hidden. The trace set is built from the attached source's
    /// <see cref="AudioScopeLayout"/>, which is how a Next contributes ten channels
    /// where a Spectrum contributes four.
    /// </remarks>
    public partial class AudioOscilloscopeWindow : Window
    {
        private const int VisibleSamples = 1024;
        private const double TraceHeight = 72;

        private readonly List<OscilloscopeTraceControl> _traces = [];
        private readonly DispatcherTimer _timer;
        private AudioScopeCapture? _capture;
        private IAudioScopeSource? _source;

        public AudioOscilloscopeWindow()
        {
            InitializeComponent();

            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _timer.Tick += OnTimerTick;

            Loaded += (_, _) => _timer.Start();
        }

        /// <summary>Points the scope at the running machine's audio block, or at nothing.</summary>
        public void AttachAudioSource(IAudioScopeSource? source)
        {
            if (ReferenceEquals(_source, source))
            {
                return;
            }

            _source?.SetScopeCapture(null);
            _source = source;
            if (source == null)
            {
                _capture = null;
                BuildTraces(null);
                return;
            }

            AudioScopeLayout layout = source.ScopeLayout;
            _capture = new AudioScopeCapture(
                layout.ChannelCount,
                SpectrumAudioTiming.DefaultSampleRate * 2);
            BuildTraces(layout);
            source.SetScopeCapture(_capture);
        }

        public void OwnerClosing()
        {
            AttachAudioSource(null);
            _timer.Stop();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            AttachAudioSource(null);
            _timer.Stop();
            base.OnClosed(e);
        }

        private void BuildTraces(AudioScopeLayout? layout)
        {
            _traces.Clear();
            GroupPanel.Children.Clear();
            if (layout == null)
            {
                StatusText.Text = "No machine attached.";
                return;
            }

            foreach (AudioScopeGroup group in layout.Groups)
            {
                var channelPanel = new StackPanel();
                foreach (AudioScopeChannel channel in group.Channels)
                {
                    channelPanel.Children.Add(CreateTracePanel(channel));
                }

                GroupPanel.Children.Add(new Expander
                {
                    Header = group.Name,
                    IsExpanded = group.ExpandedByDefault,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
                    Margin = new Thickness(0, 0, 0, 6),
                    Content = channelPanel
                });
            }

            StatusText.Text =
                $"{layout.ChannelCount} channels · {VisibleSamples} samples per trace";
        }

        private UIElement CreateTracePanel(AudioScopeChannel channel)
        {
            var trace = new OscilloscopeTraceControl(VisibleSamples)
            {
                ChannelIndex = channel.Index,
                Height = TraceHeight
            };
            trace.SetWaveColor(channel.ColorHex);
            _traces.Add(trace);

            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(new TextBlock
            {
                Text = channel.Name,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 0, 0, 2)
            });
            panel.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)),
                BorderThickness = new Thickness(1),
                Child = trace
            });
            return panel;
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            AudioScopeCapture? capture = _capture;
            if (capture == null)
            {
                return;
            }

            for (int i = 0; i < _traces.Count; i++)
            {
                OscilloscopeTraceControl trace = _traces[i];
                // A collapsed TurboSound group is not measured, so skip the copy and the
                // reduction rather than doing them for something nobody can see.
                if (trace.IsVisible)
                {
                    trace.Refresh(capture);
                }
            }
        }
    }
}
