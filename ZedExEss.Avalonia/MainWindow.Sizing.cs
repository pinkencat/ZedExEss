using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZedExEss.Spectrum.Video;

namespace ZedExEss.AvaloniaHost;

/// <summary>
/// Keeps the emulated framebuffer at an undistorted half-integer scale and sizes the
/// top-level window around it. The media browser is extra window chrome: showing it must
/// never steal pixels from the Spectrum viewport.
/// </summary>
public sealed partial class MainWindow
{
    private const double DefaultScreenZoom = 2.0;
    private const double MinScreenZoom = 0.5;
    private const double MaxScreenZoom = 4.0;
    private const double ScreenZoomStep = 0.5;
    private const double MediaBrowserWidth = 340.0;

    private Border _screenHost = null!;
    private readonly MenuItem[] _zoomMenuItems = new MenuItem[4];
    private bool _resizingWindowToScreenZoom;
    private bool _sizingUiInitialized;
    private int _windowFitZoomQueued;
    private int _windowResizeQueued;

    private void InitializeSizingUi()
    {
        _screenHost = FindRequiredControl<Border>("ScreenHost");
        RegisterZoomMenuItem(0, "Zoom1MenuItem", 1.0);
        RegisterZoomMenuItem(1, "Zoom2MenuItem", 2.0);
        RegisterZoomMenuItem(2, "Zoom3MenuItem", 3.0);
        RegisterZoomMenuItem(3, "Zoom4MenuItem", 4.0);

        SizeChanged += OnWindowSizeChanged;
        _sizingUiInitialized = true;
        UpdateZoomMenuChecks();
    }

    private void RegisterZoomMenuItem(int index, string name, double zoom)
    {
        MenuItem item = FindRequiredControl<MenuItem>(name);
        item.Tag = zoom.ToString(CultureInfo.InvariantCulture);
        item.Click += OnZoomMenuClicked;
        _zoomMenuItems[index] = item;
    }

    private void OnZoomMenuClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item
            || item.Tag is not string tag
            || !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
        {
            UpdateZoomMenuChecks();
            return;
        }

        SetScreenZoom(zoom, resizeWindow: true);
        Focus();
    }

    private void SetScreenZoom(double zoom, bool resizeWindow)
    {
        _screenZoom = Math.Clamp(zoom, MinScreenZoom, MaxScreenZoom);
        ApplyScreenZoom();
        UpdateZoomMenuChecks();

        if (resizeWindow)
        {
            QueueResizeWindowToScreenZoom();
        }
    }

    private void ApplyScreenZoom()
    {
        if (!TryGetActiveFrameSize(out int frameWidth, out int frameHeight))
        {
            return;
        }

        _screenImage.Width = frameWidth * _screenZoom;
        _screenImage.Height = frameHeight * _screenZoom;
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_sizingUiInitialized || _resizingWindowToScreenZoom)
        {
            return;
        }

        QueueFitScreenZoomToWindow();
    }

    private void QueueResizeWindowToScreenZoom()
    {
        if (Interlocked.Exchange(ref _windowResizeQueued, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _windowResizeQueued, 0);
            ResizeWindowToScreenZoom();
        }, DispatcherPriority.Loaded);
    }

    private void ResizeWindowToScreenZoom()
    {
        if (!_sizingUiInitialized || !IsVisible || WindowState != WindowState.Normal
            || !TryGetActiveFrameSize(out int frameWidth, out int frameHeight))
        {
            return;
        }

        // Bounds are populated only after the first layout pass. Treating an unmeasured main
        // grid as application chrome would add the whole initial client height a second time.
        if (_mainContentGrid.Bounds.Width <= 0 || _mainContentGrid.Bounds.Height <= 0)
        {
            return;
        }

        Thickness margin = _screenHost.Margin;
        double mainChromeWidth = Math.Max(0, ClientSize.Width - _mainContentGrid.Bounds.Width);
        double mainChromeHeight = Math.Max(0, ClientSize.Height - _mainContentGrid.Bounds.Height);
        double desiredClientWidth =
            (frameWidth * _screenZoom)
            + margin.Left + margin.Right
            + (_mediaBrowserVisible ? MediaBrowserWidth : 0)
            + mainChromeWidth;
        double desiredClientHeight =
            (frameHeight * _screenZoom)
            + margin.Top + margin.Bottom
            + mainChromeHeight;

        _resizingWindowToScreenZoom = true;
        try
        {
            Width = Math.Ceiling(desiredClientWidth);
            Height = Math.Ceiling(desiredClientHeight);
        }
        finally
        {
            Dispatcher.UIThread.Post(
                () => _resizingWindowToScreenZoom = false,
                DispatcherPriority.Loaded);
        }
    }

    private void QueueFitScreenZoomToWindow()
    {
        if (Interlocked.Exchange(ref _windowFitZoomQueued, 1) == 1)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _windowFitZoomQueued, 0);
            if (!_resizingWindowToScreenZoom)
            {
                FitScreenZoomToWindow();
            }
        }, DispatcherPriority.Loaded);
    }

    private void FitScreenZoomToWindow()
    {
        if (!_sizingUiInitialized || !TryGetActiveFrameSize(out int frameWidth, out int frameHeight))
        {
            return;
        }

        double availableWidth = _screenHost.Bounds.Width;
        double availableHeight = _screenHost.Bounds.Height;
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        double rawFit = Math.Min(
            availableWidth / frameWidth,
            availableHeight / frameHeight);
        double fitZoom = Math.Floor(rawFit / ScreenZoomStep) * ScreenZoomStep;
        fitZoom = Math.Clamp(fitZoom, MinScreenZoom, MaxScreenZoom);
        if (Math.Abs(fitZoom - _screenZoom) < 0.001)
        {
            return;
        }

        _screenZoom = fitZoom;
        ApplyScreenZoom();
        UpdateZoomMenuChecks();
    }

    private bool TryGetActiveFrameSize(out int width, out int height)
    {
        if (_zx8xMachine != null)
        {
            width = _zx8xMachine.FrameWidth;
            height = _zx8xMachine.FrameHeight;
            return true;
        }

        if (_machine != null)
        {
            SpectrumUlaTiming timing = SpectrumUlaTiming.ForModel(_machine.Model);
            width = timing.FrameWidth;
            height = timing.FrameHeight;
            return true;
        }

        width = 0;
        height = 0;
        return false;
    }

    private void UpdateZoomMenuChecks()
    {
        if (!_sizingUiInitialized)
        {
            return;
        }

        for (int index = 0; index < _zoomMenuItems.Length; index++)
        {
            _zoomMenuItems[index].IsChecked = Math.Abs(_screenZoom - (index + 1)) < 0.001;
        }
    }
}
