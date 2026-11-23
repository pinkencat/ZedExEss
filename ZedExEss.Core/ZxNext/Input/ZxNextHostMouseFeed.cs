namespace ZedExEss.ZxNext.Input;

/// <summary>
/// The host half of the Kempston mouse: turns pointer movement over the displayed picture
/// and wheel turns into what <see cref="ZxNextMouseDevice"/> takes.
/// </summary>
/// <remarks>
/// Shared so the WPF and Avalonia hosts scale identically. Movement is measured against
/// the size the picture is drawn at, so one count is one 320-column Next pixel across, or
/// one line down, whatever the zoom; the NextReg $0A DPI setting then scales from there as
/// it does on the hardware. Wheel deltas arrive in fractions of a notch on some devices,
/// and the remainder is kept until a whole notch has turned.
/// </remarks>
public sealed class ZxNextHostMouseFeed
{
    /// <summary>The Next display the movement is measured against: 320 columns by 256 lines.</summary>
    public const double DisplayColumns = 320;
    public const double DisplayLines = 256;

    private readonly ZxNextMouseDevice _mouse;
    private double _wheelRemainder;

    public ZxNextHostMouseFeed(ZxNextMouseDevice mouse)
    {
        _mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));
    }

    public ZxNextMouseDevice Mouse => _mouse;

    /// <summary>
    /// Adds pointer movement measured in the same units as the picture's drawn size.
    /// </summary>
    public void Move(double deltaX, double deltaY, double pictureWidth, double pictureHeight)
    {
        if (pictureWidth <= 0 || pictureHeight <= 0)
        {
            return;
        }

        _mouse.MoveHost(
            deltaX * DisplayColumns / pictureWidth,
            deltaY * DisplayLines / pictureHeight);
    }

    /// <summary>Adds wheel movement in notches; positive is away from the user.</summary>
    public void TurnWheel(double notches)
    {
        if (!double.IsFinite(notches))
        {
            return;
        }

        _wheelRemainder += notches;
        int whole = (int)Math.Truncate(_wheelRemainder);
        if (whole != 0)
        {
            _wheelRemainder -= whole;
            _mouse.TurnHostWheel(whole);
        }
    }

    public void SetButton(ZxNextMouseButton button, bool pressed) =>
        _mouse.SetHostButton(button, pressed);

    /// <summary>Lets go of everything, as when the host releases its capture.</summary>
    public void Release()
    {
        _wheelRemainder = 0;
        _mouse.ReleaseHost();
    }
}
