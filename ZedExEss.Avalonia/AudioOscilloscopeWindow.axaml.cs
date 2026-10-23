using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ZedExEss.Spectrum.Audio;

namespace ZedExEss.AvaloniaHost;

/// <summary>Live beeper and independent PSG channel waveforms over the optional capture sink.</summary>
/// <remarks>
/// The trace set is built from the attached source's <see cref="AudioScopeLayout"/>,
/// which is how a Next contributes ten channels where a Spectrum contributes four.
/// </remarks>
internal sealed partial class AudioOscilloscopeWindow : Window
{
    private const int VisibleSamples = 1024;
    private const double TraceHeight = 72;

    private readonly List<OscilloscopeTraceControl> _traces = [];
    private readonly DispatcherTimer _timer;
    private readonly StackPanel _groupPanel;
    private readonly TextBlock _statusText;
    private AudioScopeCapture? _capture;
    private IAudioScopeSource? _source;

    public AudioOscilloscopeWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _groupPanel = FindRequiredControl<StackPanel>("GroupPanel");
        _statusText = FindRequiredControl<TextBlock>("StatusText");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTimerTick;
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => Detach();
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

    private void Detach()
    {
        _timer.Stop();
        AttachAudioSource(null);
    }

    private void BuildTraces(AudioScopeLayout? layout)
    {
        _traces.Clear();
        _groupPanel.Children.Clear();
        if (layout == null)
        {
            _statusText.Text = "No machine attached.";
            return;
        }

        foreach (AudioScopeGroup group in layout.Groups)
        {
            var channelPanel = new StackPanel();
            foreach (AudioScopeChannel channel in group.Channels)
            {
                channelPanel.Children.Add(CreateTracePanel(channel));
            }

            _groupPanel.Children.Add(new Expander
            {
                Header = group.Name,
                IsExpanded = group.ExpandedByDefault,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = channelPanel
            });
        }

        _statusText.Text =
            $"{layout.ChannelCount} channels · {VisibleSamples} samples per trace";
    }

    private Control CreateTracePanel(AudioScopeChannel channel)
    {
        var trace = new OscilloscopeTraceControl(VisibleSamples)
        {
            ChannelIndex = channel.Index,
            WaveColor = Color.Parse(channel.ColorHex),
            Height = TraceHeight
        };
        _traces.Add(trace);

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(new TextBlock
        {
            Text = channel.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(4, 0, 0, 2)
        });
        panel.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x05)),
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
            // A collapsed TurboSound group is not laid out, so skip the copy and the
            // reduction rather than doing them for something nobody can see.
            if (trace.IsEffectivelyVisible)
            {
                trace.Refresh(capture);
            }
        }
    }

    private T FindRequiredControl<T>(string name) where T : Control =>
        this.FindControl<T>(name)
        ?? throw new InvalidOperationException($"{name} was not created by XAML.");
}

/// <summary>Draws one captured channel as a single stroked envelope.</summary>
/// <remarks>
/// This replaces a per-segment DrawLine loop that issued one call per captured sample:
/// four traces of a thousand samples meant about four thousand draw calls every refresh,
/// and ten Next channels would have meant ten thousand. The trace is now reduced to one
/// min/max pair per pixel column and emitted as a single geometry, so cost follows
/// control width rather than sample rate.
/// </remarks>
internal sealed class OscilloscopeTraceControl : Control
{
    public static readonly StyledProperty<Color> WaveColorProperty =
        AvaloniaProperty.Register<OscilloscopeTraceControl, Color>(nameof(WaveColor), Colors.White);

    private static readonly Pen CenterPen = new(new SolidColorBrush(Color.Parse("#202020")), 1);

    private readonly short[] _samples;
    private short[] _minima = [];
    private short[] _maxima = [];
    private Color _wavePenColor;
    private Pen? _wavePen;
    private int _sampleCount;

    public OscilloscopeTraceControl(int visibleSamples)
    {
        _samples = new short[visibleSamples];
    }

    public Color WaveColor
    {
        get => GetValue(WaveColorProperty);
        set => SetValue(WaveColorProperty, value);
    }

    /// <summary>The channel this trace draws, as an index into the capture buffer.</summary>
    public int ChannelIndex { get; init; }

    /// <summary>Pulls this trace's newest window and schedules a redraw.</summary>
    public void Refresh(AudioScopeCapture capture)
    {
        capture.CopyLatest(ChannelIndex, _samples);
        _sampleCount = _samples.Length;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        double centerY = height * 0.5;
        context.DrawLine(CenterPen, new Point(0, centerY), new Point(width, centerY));
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

        if (_wavePen == null || _wavePenColor != WaveColor)
        {
            _wavePenColor = WaveColor;
            _wavePen = new Pen(new SolidColorBrush(_wavePenColor), 1.4);
        }

        double scale = AudioScopeTrace.GetVerticalScale(_minima, _maxima, produced, height * 0.45);
        double xScale = produced > 1 ? width / (produced - 1) : width;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext figure = geometry.Open())
        {
            figure.BeginFigure(new Point(0, centerY - (_maxima[0] * scale)), false);
            for (int column = 0; column < produced; column++)
            {
                double x = column * xScale;
                // Both extremes of the column, so a transient narrower than a pixel
                // still shows as that column's full height.
                figure.LineTo(new Point(x, centerY - (_maxima[column] * scale)));
                figure.LineTo(new Point(x, centerY - (_minima[column] * scale)));
            }

            figure.EndFigure(false);
        }

        context.DrawGeometry(null, _wavePen, geometry);
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
}
