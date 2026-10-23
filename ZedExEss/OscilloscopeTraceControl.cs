using System;
using System.Windows;
using System.Windows.Media;
using ZedExEss.Spectrum.Audio;

namespace ZedExEss
{
    /// <summary>Draws one captured channel as a single stroked envelope.</summary>
    /// <remarks>
    /// This replaces a Polyline whose Points collection was rebuilt from every captured
    /// sample on each refresh. That allocated a thousand Points per trace and made WPF
    /// re-tessellate the whole shape; with ten Next channels it would have been ten
    /// thousand. The trace is now reduced to one min/max pair per pixel column and
    /// emitted as a frozen StreamGeometry, so cost follows control width, not sample
    /// rate, and no per-sample object is created at all.
    /// </remarks>
    internal sealed class OscilloscopeTraceControl : FrameworkElement
    {
        private static readonly Brush BackgroundBrush = CreateFrozenBrush("#050505");
        private static readonly Pen CenterPen = CreateFrozenPen("#202020", 1);

        private readonly short[] _samples;
        private short[] _minima = [];
        private short[] _maxima = [];
        private Pen _wavePen = CreateFrozenPen("#FFFFFF", 1.4);
        private int _sampleCount;

        public OscilloscopeTraceControl(int visibleSamples)
        {
            _samples = new short[visibleSamples];
        }

        /// <summary>The channel this trace draws, as an index into the capture buffer.</summary>
        public int ChannelIndex { get; init; }

        public void SetWaveColor(string colorHex) =>
            _wavePen = CreateFrozenPen(colorHex, 1.4);

        /// <summary>Pulls this trace's newest window and schedules a redraw.</summary>
        public void Refresh(AudioScopeCapture capture)
        {
            capture.CopyLatest(ChannelIndex, _samples);
            _sampleCount = _samples.Length;
            InvalidateVisual();
        }

        public void Clear()
        {
            Array.Clear(_samples);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 2 || height <= 2)
            {
                return;
            }

            drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));
            double centerY = height * 0.5;
            drawingContext.DrawLine(CenterPen, new Point(0, centerY), new Point(width, centerY));
            if (_sampleCount == 0)
            {
                return;
            }

            int columns = Math.Clamp((int)width, 1, AudioScopeTrace.MaximumColumns);
            EnsureEnvelopeCapacity(columns);
            int produced = AudioScopeTrace.Reduce(
                _samples.AsSpan(0, _sampleCount), columns, _minima, _maxima);
            if (produced == 0)
            {
                return;
            }

            double scale = AudioScopeTrace.GetVerticalScale(
                _minima, _maxima, produced, height * 0.45);
            double xScale = produced > 1 ? width / (produced - 1) : width;

            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(
                    new Point(0, centerY - (_maxima[0] * scale)),
                    isFilled: false,
                    isClosed: false);
                for (int column = 0; column < produced; column++)
                {
                    double x = column * xScale;
                    // Both extremes of the column, so a transient narrower than a pixel
                    // still shows as that column's full height.
                    context.LineTo(new Point(x, centerY - (_maxima[column] * scale)), true, false);
                    context.LineTo(new Point(x, centerY - (_minima[column] * scale)), true, false);
                }
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, _wavePen, geometry);
        }

        private void EnsureEnvelopeCapacity(int columns)
        {
            if (_minima.Length >= columns)
            {
                return;
            }

            _minima = new short[columns];
            _maxima = new short[columns];
        }

        private static Pen CreateFrozenPen(string colorHex, double thickness)
        {
            var pen = new Pen(CreateFrozenBrush(colorHex), thickness);
            pen.Freeze();
            return pen;
        }

        private static Brush CreateFrozenBrush(string colorHex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
            brush.Freeze();
            return brush;
        }
    }
}
