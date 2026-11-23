using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using ZedExEss.ZxNext.Input;

namespace ZedExEss;

/// <summary>
/// Host capture for the ZX Spectrum Next's Kempston mouse.
/// </summary>
/// <remarks>
/// With the option on, a click on the picture captures the pointer: it is hidden, held at
/// the centre of the picture, and its movement goes to the Next as mouse counts. F11, the
/// window losing focus, or leaving the Next releases it. The click that captures is not
/// passed on, so it cannot press a button the guest then sees. Matches the Avalonia host.
/// </remarks>
public partial class MainWindow
{
    private const Key KempstonMouseReleaseKey = Key.F11;

    private bool _kempstonMouseEnabled;
    private bool _kempstonMouseCaptured;
    private ZxNextHostMouseFeed? _kempstonMouseFeed;
    private Point _kempstonMouseCentre;

    private void InitializeKempstonMouse()
    {
        KempstonMouseMenu.IsChecked = _kempstonMouseEnabled;
        ScreenImage.PreviewMouseDown += OnKempstonMouseDown;
        ScreenImage.PreviewMouseUp += OnKempstonMouseUp;
        ScreenImage.MouseMove += OnKempstonMouseMove;
        ScreenImage.MouseWheel += OnKempstonMouseWheel;
        ScreenImage.LostMouseCapture += (_, _) => EndKempstonMouseCapture();
    }

    private void OnKempstonMouseToggle(object sender, RoutedEventArgs e)
    {
        _kempstonMouseEnabled = KempstonMouseMenu.IsChecked;
        if (!_kempstonMouseEnabled)
        {
            EndKempstonMouseCapture();
        }

        Focus();
    }

    /// <summary>Releases a captured mouse for the release key; false when there was none.</summary>
    private bool TryReleaseKempstonMouseForKey(Key key, bool pressed)
    {
        if (key != KempstonMouseReleaseKey || !_kempstonMouseCaptured)
        {
            return false;
        }

        if (pressed)
        {
            EndKempstonMouseCapture();
        }

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

    private void OnKempstonMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_kempstonMouseEnabled || GetKempstonMouseFeed() is not { } feed)
        {
            return;
        }

        if (!_kempstonMouseCaptured)
        {
            BeginKempstonMouseCapture();
            e.Handled = true;
            return;
        }

        if (TryMapKempstonButton(e.ChangedButton, out ZxNextMouseButton button))
        {
            feed.SetButton(button, pressed: true);
        }

        e.Handled = true;
    }

    private void OnKempstonMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_kempstonMouseCaptured || GetKempstonMouseFeed() is not { } feed)
        {
            return;
        }

        if (TryMapKempstonButton(e.ChangedButton, out ZxNextMouseButton button))
        {
            feed.SetButton(button, pressed: false);
        }

        e.Handled = true;
    }

    private void OnKempstonMouseMove(object sender, MouseEventArgs e)
    {
        if (!_kempstonMouseCaptured || GetKempstonMouseFeed() is not { } feed)
        {
            return;
        }

        Point position = e.GetPosition(ScreenImage);
        Vector delta = position - _kempstonMouseCentre;
        if (delta.X == 0 && delta.Y == 0)
        {
            return;
        }

        feed.Move(delta.X, delta.Y, ScreenImage.ActualWidth, ScreenImage.ActualHeight);
        WarpKempstonMouseToCentre();
    }

    private void OnKempstonMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_kempstonMouseCaptured || GetKempstonMouseFeed() is not { } feed)
        {
            return;
        }

        feed.TurnWheel(e.Delta / (double)Mouse.MouseWheelDeltaForOneLine);
        e.Handled = true;
    }

    private void BeginKempstonMouseCapture()
    {
        if (ScreenImage.ActualWidth <= 0 || ScreenImage.ActualHeight <= 0
            || !ScreenImage.CaptureMouse())
        {
            return;
        }

        _kempstonMouseCaptured = true;
        ScreenImage.Cursor = Cursors.None;

        // Keep the pointer inside the picture so a fast flick cannot leave the window
        // between two recentrings.
        Point topLeft = ScreenImage.PointToScreen(new Point(0, 0));
        Point bottomRight = ScreenImage.PointToScreen(
            new Point(ScreenImage.ActualWidth, ScreenImage.ActualHeight));
        var clip = new NativeRect
        {
            Left = (int)Math.Ceiling(topLeft.X),
            Top = (int)Math.Ceiling(topLeft.Y),
            Right = (int)Math.Floor(bottomRight.X),
            Bottom = (int)Math.Floor(bottomRight.Y)
        };
        ClipCursor(ref clip);
        WarpKempstonMouseToCentre();
    }

    private void EndKempstonMouseCapture()
    {
        if (!_kempstonMouseCaptured)
        {
            return;
        }

        _kempstonMouseCaptured = false;
        ClipCursor(IntPtr.Zero);
        ScreenImage.Cursor = null;
        if (ScreenImage.IsMouseCaptured)
        {
            ScreenImage.ReleaseMouseCapture();
        }

        _kempstonMouseFeed?.Release();
    }

    /// <summary>
    /// Puts the pointer back on the picture's centre. The centre is snapped to a whole
    /// device pixel and measured back, so the move the warp itself causes reads as none
    /// rather than as a rounding error that drifts the guest's pointer.
    /// </summary>
    private void WarpKempstonMouseToCentre()
    {
        Point centre = ScreenImage.PointToScreen(
            new Point(ScreenImage.ActualWidth / 2, ScreenImage.ActualHeight / 2));
        int x = (int)Math.Round(centre.X);
        int y = (int)Math.Round(centre.Y);
        _kempstonMouseCentre = ScreenImage.PointFromScreen(new Point(x, y));
        SetCursorPos(x, y);
    }

    private string KempstonMouseTitleSuffix =>
        _kempstonMouseCaptured ? " - mouse captured (F11 releases)" : string.Empty;

    private static bool TryMapKempstonButton(MouseButton changed, out ZxNextMouseButton button)
    {
        switch (changed)
        {
            case MouseButton.Left:
                button = ZxNextMouseButton.Left;
                return true;
            case MouseButton.Right:
                button = ZxNextMouseButton.Right;
                return true;
            case MouseButton.Middle:
                button = ZxNextMouseButton.Middle;
                return true;
            default:
                button = default;
                return false;
        }
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
