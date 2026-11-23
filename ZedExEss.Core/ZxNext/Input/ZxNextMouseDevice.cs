using ZedExEss.ZxNext.Core;

namespace ZedExEss.ZxNext.Input;

/// <summary>A button on the PS/2 mouse the Next presents as a Kempston mouse.</summary>
public enum ZxNextMouseButton
{
    Left,
    Right,
    Middle
}

/// <summary>
/// The PS/2 mouse the Next presents as a Kempston mouse on ports $FBDF (X), $FFDF (Y) and
/// $FADF (wheel and buttons).
/// </summary>
/// <remarks>
/// Modelled on the pinned core's <c>ps2_mouse.v</c> and its read path in <c>zxnext.vhd</c>,
/// which is authoritative where <c>ports.txt</c> disagrees:
/// <list type="bullet">
/// <item>X counts up to the right and Y counts up as the mouse moves <i>up</i>, both
/// wrapping at 8 bits. The wheel is an 8-bit counter of which $FADF shows the low four.</item>
/// <item>The buttons read <b>active low</b> - bit 1 left, bit 0 right, bit 2 middle - with
/// bit 3 always set. <c>ports.txt</c> says "1 for press"; the core inverts them.</item>
/// <item>NextReg $0A bits 1:0 scale each movement packet as it arrives - double, as sent,
/// half or quarter, the last two by arithmetic shift of the 8-bit delta - and bit 3 swaps
/// left and right as the button byte is latched. Neither changes what was counted before.</item>
/// <item>Only a power-on resets the counters; neither soft nor hard reset touches them.</item>
/// </list>
///
/// The host feeds movement from its own thread. It is held as pending PS/2 input and
/// delivered as packets on the emulation thread when the guest next reads a mouse port,
/// which is the only point at which the guest can tell. Packets are sized to the 8-bit
/// delta the core's scaling reads, so a large host movement scales the way a real mouse's
/// stream of reports would.
/// </remarks>
public sealed class ZxNextMouseDevice
{
    public const ushort ButtonsPort = 0xFADF;
    public const ushort XPort = 0xFBDF;
    public const ushort YPort = 0xFFDF;

    /// <summary>The largest movement one packet carries in the core's 8-bit delta.</summary>
    private const int MaximumPacketDelta = 127;

    /// <summary>An IntelliMouse wheel report is a signed four-bit value.</summary>
    private const int MaximumPacketWheel = 7;

    private readonly object _hostLock = new();
    private byte _x;
    private byte _y;
    private byte _wheel;
    private byte _latchedButtons;

    // Host-owned input, written under _hostLock from whichever thread the host uses.
    private double _hostX;
    private double _hostY;
    private int _pendingX;
    private int _pendingY;
    private int _pendingWheel;
    private byte _hostButtons;
    private volatile bool _hostChanged;

    public byte X => _x;
    public byte Y => _y;
    public byte Wheel => _wheel;

    /// <summary>The buttons the last packet latched: bit 0 left, bit 1 right, bit 2 middle.</summary>
    public byte LatchedButtons => _latchedButtons;

    /// <summary>What $FADF answers with, in the core's active-low button layout.</summary>
    public byte ButtonsPortValue => (byte)(((_wheel & 0x0F) << 4)
        | 0x08
        | ((~_latchedButtons & 0x04) != 0 ? 0x04 : 0)
        | ((~_latchedButtons & 0x01) != 0 ? 0x02 : 0)
        | ((~_latchedButtons & 0x02) != 0 ? 0x01 : 0));

    /// <summary>
    /// Whether this port is one of the three mouse ports, before the NextReg $83 enable.
    /// </summary>
    /// <remarks>
    /// The core decodes A11-A8 against $A, $B and $F with the low byte $DF and ignores
    /// A15-A12, so $0ADF and $FADF are the same port.
    /// </remarks>
    public static bool IsMousePort(ushort port) =>
        (byte)port == 0xDF && ((port >> 8) & 0x0F) is 0x0A or 0x0B or 0x0F;

    public byte ReadPort(ushort port, byte peripheral5Settings)
    {
        DeliverHostInput(peripheral5Settings);
        return ((port >> 8) & 0x0F) switch
        {
            0x0B => _x,
            0x0F => _y,
            _ => ButtonsPortValue
        };
    }

    /// <summary>
    /// Adds host movement in Next display pixels: one per 320-column pixel across and one
    /// per line down. Fractions carry over to the next call.
    /// </summary>
    /// <remarks>
    /// Down is positive here, as host pointers report it; the mouse's own Y counts up.
    /// </remarks>
    public void MoveHost(double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
        {
            return;
        }

        lock (_hostLock)
        {
            _hostX += deltaX;
            _hostY += deltaY;
            int wholeX = (int)Math.Truncate(_hostX);
            int wholeY = (int)Math.Truncate(_hostY);
            if (wholeX == 0 && wholeY == 0)
            {
                return;
            }

            _hostX -= wholeX;
            _hostY -= wholeY;
            _pendingX = Math.Clamp(_pendingX + wholeX, -65536, 65536);
            _pendingY = Math.Clamp(_pendingY - wholeY, -65536, 65536);
            _hostChanged = true;
        }
    }

    /// <summary>
    /// Turns the wheel by whole notches. Positive is away from the user ("scroll up"),
    /// which an IntelliMouse reports as a negative Z and the Next counts down.
    /// </summary>
    public void TurnHostWheel(int notches)
    {
        if (notches == 0)
        {
            return;
        }

        lock (_hostLock)
        {
            _pendingWheel = Math.Clamp(_pendingWheel - notches, -4096, 4096);
            _hostChanged = true;
        }
    }

    public void SetHostButton(ZxNextMouseButton button, bool pressed)
    {
        byte mask = button switch
        {
            ZxNextMouseButton.Left => 0x01,
            ZxNextMouseButton.Right => 0x02,
            ZxNextMouseButton.Middle => 0x04,
            _ => throw new ArgumentOutOfRangeException(nameof(button))
        };

        lock (_hostLock)
        {
            byte next = pressed ? (byte)(_hostButtons | mask) : (byte)(_hostButtons & ~mask);
            if (next != _hostButtons)
            {
                _hostButtons = next;
                _hostChanged = true;
            }
        }
    }

    /// <summary>
    /// Lets go of every host button and drops movement not yet delivered, as when the host
    /// releases its capture or loses focus. The release still reaches the guest.
    /// </summary>
    public void ReleaseHost()
    {
        lock (_hostLock)
        {
            _hostX = 0;
            _hostY = 0;
            _pendingX = 0;
            _pendingY = 0;
            _pendingWheel = 0;
            _hostButtons = 0;
            _hostChanged = true;
        }
    }

    /// <summary>Puts the counters back to their power-on state, which only power-on does.</summary>
    public void PowerOn()
    {
        _x = 0;
        _y = 0;
        _wheel = 0;
        _latchedButtons = 0;
        lock (_hostLock)
        {
            _hostX = 0;
            _hostY = 0;
            _pendingX = 0;
            _pendingY = 0;
            _pendingWheel = 0;
            _hostChanged = _hostButtons != 0;
        }
    }

    /// <summary>
    /// Delivers what the host has sent as a stream of PS/2 packets, scaled and swapped by
    /// the NextReg $0A settings in force now.
    /// </summary>
    public void DeliverHostInput(byte peripheral5Settings)
    {
        if (!_hostChanged)
        {
            return;
        }

        int x;
        int y;
        int wheel;
        byte buttons;
        lock (_hostLock)
        {
            x = _pendingX;
            y = _pendingY;
            wheel = _pendingWheel;
            buttons = _hostButtons;
            _pendingX = 0;
            _pendingY = 0;
            _pendingWheel = 0;
            _hostChanged = false;
        }

        // Each packet latches the buttons first, then adds X, Y and the wheel.
        bool reversed = (peripheral5Settings & 0x08) != 0;
        _latchedButtons = reversed
            ? (byte)((buttons & 0x04) | ((buttons & 0x01) << 1) | ((buttons & 0x02) >> 1))
            : buttons;

        int dpi = peripheral5Settings & 0x03;
        while (x != 0 || y != 0)
        {
            int packetX = Math.Clamp(x, -MaximumPacketDelta, MaximumPacketDelta);
            int packetY = Math.Clamp(y, -MaximumPacketDelta, MaximumPacketDelta);
            _x = (byte)(_x + Scale(packetX, dpi));
            _y = (byte)(_y + Scale(packetY, dpi));
            x -= packetX;
            y -= packetY;
        }

        while (wheel != 0)
        {
            int packetWheel = Math.Clamp(wheel, -MaximumPacketWheel - 1, MaximumPacketWheel);
            _wheel = (byte)(_wheel + packetWheel);
            wheel -= packetWheel;
        }
    }

    public ZxNextMouseState CaptureState() => new(_x, _y, _wheel, _latchedButtons);

    /// <summary>
    /// Puts the counters and the latched buttons back. Movement the host sent and the
    /// guest had not read is dropped: it was aimed at the machine being replaced. The
    /// restored buttons stand until the host next reports a change, so a restored machine
    /// carries on exactly as the captured one would have.
    /// </summary>
    public void RestoreState(ZxNextMouseState state)
    {
        _x = state.X;
        _y = state.Y;
        _wheel = state.Wheel;
        _latchedButtons = (byte)(state.LatchedButtons & 0x07);
        lock (_hostLock)
        {
            _hostX = 0;
            _hostY = 0;
            _pendingX = 0;
            _pendingY = 0;
            _pendingWheel = 0;
            _hostChanged = false;
        }
    }

    /// <summary>
    /// The core's <c>xydelta</c>: the packet's 8-bit delta doubled, as sent, or shifted
    /// right arithmetically by one or two.
    /// </summary>
    private static int Scale(int delta, int dpi) => dpi switch
    {
        0 => delta << 1,
        1 => delta,
        2 => delta >> 1,
        _ => delta >> 2
    };
}
