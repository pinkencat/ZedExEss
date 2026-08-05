using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ZedExEss.Spectrum.Audio;

namespace ZedExEss.AvaloniaHost;

/// <summary>Live beeper and independent AY channel waveforms over the optional audio capture sink.</summary>
internal sealed partial class AudioOscilloscopeWindow : Window
{
    private const int VisibleSamples = 1024;
    private readonly AudioScopeCapture _capture = new(SpectrumAudioTiming.DefaultSampleRate * 2);
    private readonly short[] _beeper = new short[VisibleSamples];
    private readonly short[] _ayA = new short[VisibleSamples];
    private readonly short[] _ayB = new short[VisibleSamples];
    private readonly short[] _ayC = new short[VisibleSamples];
    private readonly DispatcherTimer _timer;
    private readonly OscilloscopeTraceControl _beeperTrace;
    private readonly OscilloscopeTraceControl _ayATrace;
    private readonly OscilloscopeTraceControl _ayBTrace;
    private readonly OscilloscopeTraceControl _ayCTrace;
    private SpectrumAudioRenderer? _renderer;

    public AudioOscilloscopeWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _beeperTrace = FindRequiredControl<OscilloscopeTraceControl>("BeeperTrace");
        _ayATrace = FindRequiredControl<OscilloscopeTraceControl>("AyATrace");
        _ayBTrace = FindRequiredControl<OscilloscopeTraceControl>("AyBTrace");
        _ayCTrace = FindRequiredControl<OscilloscopeTraceControl>("AyCTrace");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTimerTick;
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => Detach();
    }

    /// <summary>Moves the opt-in capture sink to the renderer belonging to the current machine.</summary>
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

    private void Detach()
    {
        _timer.Stop();
        AttachAudioRenderer(null);
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _capture.CopyLatest(_beeper, _ayA, _ayB, _ayC, VisibleSamples);
        _beeperTrace.SetSamples(_beeper);
        _ayATrace.SetSamples(_ayA);
        _ayBTrace.SetSamples(_ayB);
        _ayCTrace.SetSamples(_ayC);
    }

    private T FindRequiredControl<T>(string name) where T : Control =>
        this.FindControl<T>(name)
        ?? throw new InvalidOperationException($"{name} was not created by XAML.");
}

/// <summary>Lightweight waveform control; the timer only invalidates it while its window is open.</summary>
internal sealed class OscilloscopeTraceControl : Control
{
    public static readonly StyledProperty<Color> WaveColorProperty =
        AvaloniaProperty.Register<OscilloscopeTraceControl, Color>(nameof(WaveColor), Colors.White);

    private static readonly Pen CenterPen = new(new SolidColorBrush(Color.Parse("#202020")), 1);
    private short[]? _samples;
    private Color _wavePenColor;
    private Pen? _wavePen;

    public Color WaveColor
    {
        get => GetValue(WaveColorProperty);
        set => SetValue(WaveColorProperty, value);
    }

    public void SetSamples(short[] samples)
    {
        _samples = samples;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double width = Bounds.Width;
        double height = Bounds.Height;
        double centerY = height * 0.5;
        context.DrawLine(CenterPen, new Point(0, centerY), new Point(width, centerY));

        short[]? samples = _samples;
        if (samples == null || samples.Length < 2 || width <= 2 || height <= 2)
        {
            return;
        }

        int peak = 512;
        for (int i = 0; i < samples.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs((int)samples[i]));
        }

        if (_wavePen == null || _wavePenColor != WaveColor)
        {
            _wavePenColor = WaveColor;
            _wavePen = new Pen(new SolidColorBrush(_wavePenColor), 1.4);
        }

        double xScale = width / (samples.Length - 1);
        double yScale = (height * 0.45) / peak;
        Point previous = new(0, centerY - (samples[0] * yScale));
        for (int i = 1; i < samples.Length; i++)
        {
            Point next = new(i * xScale, centerY - (samples[i] * yScale));
            context.DrawLine(_wavePen, previous, next);
            previous = next;
        }
    }
}
