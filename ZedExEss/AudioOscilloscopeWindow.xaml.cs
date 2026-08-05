using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ZedExEss.Spectrum.Audio;

namespace ZedExEss
{
    /// <summary>Live beeper and per-channel AY waveform viewer.</summary>
    /// <remarks>
    /// Attaching installs the renderer's optional capture sink; closing removes it completely, so
    /// ordinary emulation pays no ring-buffer or drawing cost while the oscilloscope is hidden.
    /// </remarks>
    public partial class AudioOscilloscopeWindow : Window
    {
        private const int VisibleSamples = 1024;
        private readonly AudioScopeCapture _capture = new(SpectrumAudioTiming.DefaultSampleRate * 2);
        private readonly short[] _beeper = new short[VisibleSamples];
        private readonly short[] _ayA = new short[VisibleSamples];
        private readonly short[] _ayB = new short[VisibleSamples];
        private readonly short[] _ayC = new short[VisibleSamples];
        private readonly DispatcherTimer _timer;
        private SpectrumAudioRenderer? _renderer;

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
        public void AttachAudioRenderer(SpectrumAudioRenderer? renderer)
        {
            if (ReferenceEquals(_renderer, renderer))
            {
                return;
            }

            _renderer?.SetScopeCapture(null);
            _renderer = renderer;
            _renderer?.SetScopeCapture(_capture);
        }
        public void OwnerClosing()
        {
            AttachAudioRenderer(null);
            _timer.Stop();
            Close();
        }
        protected override void OnClosed(EventArgs e)
        {
            AttachAudioRenderer(null);
            _timer.Stop();
            base.OnClosed(e);
        }
        private void OnTimerTick(object? sender, EventArgs e)
        {
            _capture.CopyLatest(_beeper, _ayA, _ayB, _ayC, VisibleSamples);

            DrawWave(BeeperCanvas, BeeperCenterLine, BeeperWave, _beeper);
            DrawWave(AyACanvas, AyACenterLine, AyAWave, _ayA);
            DrawWave(AyBCanvas, AyBCenterLine, AyBWave, _ayB);
            DrawWave(AyCCanvas, AyCCenterLine, AyCWave, _ayC);
        }
        private static void DrawWave(Canvas canvas, Line centerLine, Polyline wave, short[] samples)
        {
            double width = canvas.ActualWidth;
            double height = canvas.ActualHeight;
            if (width <= 2 || height <= 2)
            {
                return;
            }

            double centerY = height * 0.5;
            centerLine.X1 = 0;
            centerLine.X2 = width;
            centerLine.Y1 = centerY;
            centerLine.Y2 = centerY;

            int peak = 1;
            for (int i = 0; i < samples.Length; i++)
            {
                int absolute = Math.Abs((int)samples[i]);
                if (absolute > peak)
                {
                    peak = absolute;
                }
            }

            double verticalScale = (height * 0.45) / Math.Max(peak, 512);
            double xScale = width / (samples.Length - 1);
            var points = new PointCollection(samples.Length);
            for (int i = 0; i < samples.Length; i++)
            {
                points.Add(new Point(i * xScale, centerY - (samples[i] * verticalScale)));
            }

            wave.Points = points;
        }
    }
}
