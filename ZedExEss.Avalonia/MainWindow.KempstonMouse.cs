using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZedExEss.ZxNext.Input;

namespace ZedExEss.AvaloniaHost;

/// <summary>
/// Host capture for the ZX Spectrum Next's Kempston mouse.
/// </summary>
/// <remarks>
/// With the option on, a click on the picture captures the pointer: it is hidden, and its
/// movement goes to the Next as mouse counts. F11, the window losing focus, or leaving the
/// Next releases it. The click that captures is not passed on, so it cannot press a button
/// the guest then sees. Matches the WPF host.
///
/// On Windows the pointer is held at the picture's centre and confined to it, so movement
/// never runs out. Avalonia has no portable pointer lock, so elsewhere movement is measured
/// from the last position and stops at the edge of the screen.
/// </remarks>
public partial class MainWindow
{
    private const Key KempstonMouseReleaseKey = Key.F11;
    private const string KempstonMouseCapturedStatus = "Mouse captured - F11 releases";

    private MenuItem _kempstonMouseMenuItem = null!;
    private bool _kempstonMouseEnabled;
    private bool _kempstonMouseCaptured;
    private ZxNextHostMouseFeed? _kempstonMouseFeed;
    private Point _kempstonMouseReference;
    private IPointer? _kempstonPointer;

    private static bool CanWarpKempstonMouse => OperatingSystem.IsWindows();

    private void InitializeKempstonMouse()
    {
        _kempstonMouseMenuItem = FindRequiredControl<MenuItem>("KempstonMouseMenuItem");
        _kempstonMouseMenuItem.IsChecked = _kempstonMouseEnabled;
        _kempstonMouseMenuItem.Click += OnKempstonMouseClicked;
        _screenImage.AddHandler(PointerPressedEvent, OnKempstonPointerPressed, RoutingStrategies.Tunnel);
        _screenImage.AddHandler(PointerReleasedEvent, OnKempstonPointerReleased, RoutingStrategies.Tunnel);
        _screenImage.PointerMoved += OnKempstonPointerMoved;
        _screenImage.PointerWheelChanged += OnKempstonPointerWheelChanged;
        _screenImage.PointerCaptureLost += (_, _) => EndKempstonMouseCapture();
    }

    private void OnKempstonMouseClicked(object? sender, RoutedEventArgs e)
    {
        _kempstonMouseEnabled = _kempstonMouseMenuItem.IsChecked;
        if (!_kempstonMouseEnabled)
        {
            EndKempstonMouseCapture();
        }

        Focus();
    }

    /// <summary>Releases a captured mouse for the release key; false when there was none.</summary>
    private bool TryReleaseKempstonMouseForKey(KeyEventArgs e, bool pressed)
    {
        if (e.Key != KempstonMouseReleaseKey || !_kempstonMouseCaptured)
        {
            return false;
        }

        if (pressed)
        {
            EndKempstonMouseCapture();
        }

        e.Handled = true;
        return true;
    }

    private ZxNextHostMouseFeed? GetKempstonMouseFeed()
    {
        if (_zxNextMachine == null)
        {
            _kempstonMouseFeed = null;
            return null;
        }

        if (!ReferenceEquals(_kempstonMouseFeed?.Mouse, _zxNextMachine.Mouse))
        {
            _kempstonMouseFeed = new ZxNextHostMouseFeed(_zxNextMachine.Mouse);
        }

        return _kempstonMouseFeed;
    }

    private void OnKempstonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_kempstonMouseEnabled || GetKempstonMouseFeed() is not { } feed)
        {
            return;
        }

        if (!_kempstonMouseCaptured)
        {
            BeginKempstonMouseCapture(e);
            e.Handled = true;
            return;
        }

        if (TryMapKempstonButton(e.GetCurrentPoint(_screenImage).Properties.PointerUpdateKind,
                out ZxNextMouseButton button, out bool pressed))
        {
            feed.SetButton(button, pressed);
        }

        e.Handled = true;
    }

    private void OnKempstonPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_kempstonMouseCaptured || GetKempstonMouseFeed() is not { } feed)
        {
            return;
        }

        if (TryMapKempstonButton(e.GetCurrentPoint(_screenImage).Properties.PointerUpdateKind,
                out ZxNextMouseButton button, out bool pressed))
        {
            feed.SetButton(button, pressed);
        }

        e.Handled = true;
    }

    private void OnKempstonPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_kempstonMouseCaptured || GetKempstonMouseFeed() is not { } feed)
        {
            return;
        }

        // A second button pressed or released while another is held arrives as a move.
        if (TryMapKempstonButton(e.GetCurrentPoint(_screenImage).Properties.PointerUpdateKind,
                out ZxNextMouseButton button, out bool pressed))
        {
            feed.SetButton(button, pressed);
        }

        Point position = e.GetPosition(_screenImage);
        Point delta = position - _kempstonMouseReference;
        if (delta.X == 0 && delta.Y == 0)
        {
            return;
        }

        feed.Move(delta.X, delta.Y, _screenImage.Bounds.Width, _screenImage.Bounds.Height);
        if (CanWarpKempstonMouse)
        {
            WarpKempstonMouseToCentre();
        }
        else
        {
            _kempstonMouseReference = position;
        }

        e.Handled = true;
    }

    private void OnKempstonPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!_kempstonMouseCaptured || GetKempstonMouseFeed() is not { } feed)
        {
            return;
        }

        feed.TurnWheel(e.Delta.Y);
        e.Handled = true;
    }

    private void BeginKempstonMouseCapture(PointerPressedEventArgs e)
    {
        double width = _screenImage.Bounds.Width;
        double height = _screenImage.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        e.Pointer.Capture(_screenImage);
        _kempstonPointer = e.Pointer;
        _kempstonMouseCaptured = true;
        _screenImage.Cursor = new Cursor(StandardCursorType.None);
        _kempstonMouseReference = e.GetPosition(_screenImage);
        if (CanWarpKempstonMouse)
        {
            // Keep the pointer inside the picture so a fast flick cannot leave the window
            // between two recentrings.
            PixelPoint topLeft = _screenImage.PointToScreen(new Point(0, 0));
            PixelPoint bottomRight = _screenImage.PointToScreen(new Point(width, height));
            var clip = new NativeRect
            {
                Left = topLeft.X + 1,
                Top = topLeft.Y + 1,
                Right = bottomRight.X - 1,
                Bottom = bottomRight.Y - 1
            };
            ClipCursor(ref clip);
            WarpKempstonMouseToCentre();
        }

        _statusText.Text = KempstonMouseCapturedStatus;
    }

    private void EndKempstonMouseCapture()
    {
        if (!_kempstonMouseCaptured)
        {
            return;
        }

        _kempstonMouseCaptured = false;
        if (CanWarpKempstonMouse)
        {
            ClipCursor(IntPtr.Zero);
        }

        _screenImage.Cursor = null;
        IPointer? pointer = _kempstonPointer;
        _kempstonPointer = null;
        if (ReferenceEquals(pointer?.Captured, _screenImage))
        {
            pointer!.Capture(null);
        }

        _kempstonMouseFeed?.Release();
        if (_statusText.Text == KempstonMouseCapturedStatus)
        {
            _statusText.Text = string.Empty;
        }
    }

    /// <summary>
    /// Puts the pointer back on the picture's centre. The centre is the whole device pixel
    /// the warp lands on, measured back into the picture, so the move the warp itself
    /// causes reads as none rather than as a rounding error that drifts the guest's pointer.
    /// </summary>
    private void WarpKempstonMouseToCentre()
    {
        PixelPoint centre = _screenImage.PointToScreen(
            new Point(_screenImage.Bounds.Width / 2, _screenImage.Bounds.Height / 2));
        _kempstonMouseReference = _screenImage.PointToClient(centre);
        SetCursorPos(centre.X, centre.Y);
    }

    private static bool TryMapKempstonButton(
        PointerUpdateKind kind,
        out ZxNextMouseButton button,
        out bool pressed)
    {
        (button, pressed) = kind switch
        {
            PointerUpdateKind.LeftButtonPressed => (ZxNextMouseButton.Left, true),
            PointerUpdateKind.LeftButtonReleased => (ZxNextMouseButton.Left, false),
            PointerUpdateKind.RightButtonPressed => (ZxNextMouseButton.Right, true),
            PointerUpdateKind.RightButtonReleased => (ZxNextMouseButton.Right, false),
            PointerUpdateKind.MiddleButtonPressed => (ZxNextMouseButton.Middle, true),
            PointerUpdateKind.MiddleButtonReleased => (ZxNextMouseButton.Middle, false),
            _ => (default, false)
        };

        return kind is PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased
            or PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased
            or PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(IntPtr rect);
}
