using ZedExEss.Spectrum.Input;

namespace ZedExEss.ZxNext.Input;

/// <summary>Physical joystick connector selected by NextReg $05.</summary>
public enum ZxNextJoystickConnector
{
    Joystick1,
    Joystick2
}

/// <summary>Pinned core 3.02.03 joystick mode encoding from NextReg $05.</summary>
public enum ZxNextJoystickMode : byte
{
    Sinclair2 = 0,
    Kempston1 = 1,
    Cursor = 2,
    Sinclair1 = 3,
    Kempston2 = 4,
    MegaDrive1 = 5,
    MegaDrive2 = 6,
    UserDefined = 7
}

/// <summary>
/// Two-connector Next joystick block for standard directions/fire and keyboard overlays.
/// </summary>
public sealed class ZxNextJoystickDevice
{
    private readonly SpectrumKeyboard _keyboard;
    private readonly byte[] _states = new byte[2];
    private readonly ZxNextJoystickMode[] _modes =
    [
        ZxNextJoystickMode.Kempston1,
        ZxNextJoystickMode.Sinclair2
    ];
    private SpectrumJoystickType _hostType;

    public ZxNextJoystickDevice(SpectrumKeyboard keyboard)
    {
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
    }

    /// <summary>Desktop attachment type; None detaches host joystick input.</summary>
    public SpectrumJoystickType HostType => _hostType;

    public ZxNextJoystickMode GetMode(ZxNextJoystickConnector connector) =>
        _modes[ValidateConnector(connector)];

    public byte GetState(ZxNextJoystickConnector connector) =>
        _states[ValidateConnector(connector)];

    public void SetHostType(SpectrumJoystickType type)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        ReleaseAll();
        _hostType = type;
    }

    /// <summary>Applies the two three-bit mode fields encoded by NextReg $05.</summary>
    public void ConfigureModes(byte peripheral1Settings)
    {
        _modes[0] = (ZxNextJoystickMode)(((peripheral1Settings >> 6) & 0x03)
            | ((peripheral1Settings >> 1) & 0x04));
        _modes[1] = (ZxNextJoystickMode)(((peripheral1Settings >> 4) & 0x03)
            | ((peripheral1Settings << 1) & 0x04));
        RebuildKeyboardOverlay();
    }

    /// <summary>Updates connector 1 from the desktop keyboard joystick mapping.</summary>
    public void SetButtonState(SpectrumJoystickButton button, bool pressed)
    {
        if (_hostType != SpectrumJoystickType.None)
        {
            SetConnectorButtonState(ZxNextJoystickConnector.Joystick1, button, pressed);
        }
    }

    public void SetConnectorButtonState(
        ZxNextJoystickConnector connector,
        SpectrumJoystickButton button,
        bool pressed)
    {
        int index = ValidateConnector(connector);
        byte mask = ButtonMask(button);
        byte next = pressed
            ? (byte)(_states[index] | mask)
            : (byte)(_states[index] & ~mask);
        if (next == _states[index])
        {
            return;
        }

        _states[index] = next;
        RebuildKeyboardOverlay();
    }

    public bool HandlesPort(ushort port)
    {
        byte low = (byte)port;
        return low switch
        {
            0x1F or 0xDF => RoutesToKempston1(_modes[0]) || RoutesToKempston1(_modes[1]),
            0x37 => RoutesToKempston2(_modes[0]) || RoutesToKempston2(_modes[1]),
            _ => false
        };
    }

    public byte ReadPort(ushort port)
    {
        byte low = (byte)port;
        if (low is 0x1F or 0xDF)
        {
            return CombinePortStates(RoutesToKempston1);
        }

        if (low == 0x37)
        {
            return CombinePortStates(RoutesToKempston2);
        }

        return 0xFF;
    }

    public void ReleaseAll()
    {
        _states[0] = 0;
        _states[1] = 0;
        RebuildKeyboardOverlay();
    }

    /// <summary>Captures what is held on each connector, and how each is routed.</summary>
    public (byte[] States, ZxNextJoystickMode[] Modes) CaptureState() =>
        (_states.ToArray(), _modes.ToArray());

    /// <summary>
    /// Puts the held directions and the connector routing back.
    /// </summary>
    /// <remarks>
    /// The keyboard overlay is rebuilt rather than carried, because it is this state
    /// projected onto the matrix: a joystick mode that maps a direction to a key writes
    /// that key's bit, and recomputing it is what keeps the two from disagreeing.
    /// </remarks>
    public void RestoreState(byte[] states, ZxNextJoystickMode[] modes)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(modes);
        if (states.Length != _states.Length || modes.Length != _modes.Length)
        {
            throw new ArgumentException("A Next has two joystick connectors.", nameof(states));
        }

        states.CopyTo(_states, 0);
        modes.CopyTo(_modes, 0);
        RebuildKeyboardOverlay();
    }

    public static ZxNextJoystickMode FromHostType(SpectrumJoystickType type) => type switch
    {
        SpectrumJoystickType.Kempston => ZxNextJoystickMode.Kempston1,
        SpectrumJoystickType.Sinclair1 => ZxNextJoystickMode.Sinclair1,
        SpectrumJoystickType.Sinclair2 => ZxNextJoystickMode.Sinclair2,
        SpectrumJoystickType.Cursor => ZxNextJoystickMode.Cursor,
        _ => ZxNextJoystickMode.Kempston1
    };

    private byte CombinePortStates(Func<ZxNextJoystickMode, bool> routed)
    {
        byte value = 0;
        for (int connector = 0; connector < _states.Length; connector++)
        {
            if (routed(_modes[connector]))
            {
                value |= _states[connector];
            }
        }

        return value;
    }

    private void RebuildKeyboardOverlay()
    {
        _keyboard.ReleaseAllJoystickKeys();
        for (int connector = 0; connector < _states.Length; connector++)
        {
            byte state = _states[connector];
            foreach (SpectrumJoystickButton button in Enum.GetValues<SpectrumJoystickButton>())
            {
                if ((state & ButtonMask(button)) != 0
                    && TryGetMappedKey(_modes[connector], button, out SpectrumKey key))
                {
                    _keyboard.SetJoystickKeyState(key, pressed: true);
                }
            }
        }
    }

    private static bool TryGetMappedKey(
        ZxNextJoystickMode mode,
        SpectrumJoystickButton button,
        out SpectrumKey key)
    {
        key = SpectrumKey.Space;
        if (mode == ZxNextJoystickMode.Sinclair1)
        {
            key = button switch
            {
                SpectrumJoystickButton.Left => SpectrumKey.D6,
                SpectrumJoystickButton.Right => SpectrumKey.D7,
                SpectrumJoystickButton.Down => SpectrumKey.D8,
                SpectrumJoystickButton.Up => SpectrumKey.D9,
                SpectrumJoystickButton.Fire => SpectrumKey.D0,
                _ => SpectrumKey.Space
            };
            return true;
        }

        if (mode == ZxNextJoystickMode.Sinclair2)
        {
            key = button switch
            {
                SpectrumJoystickButton.Left => SpectrumKey.D1,
                SpectrumJoystickButton.Right => SpectrumKey.D2,
                SpectrumJoystickButton.Down => SpectrumKey.D3,
                SpectrumJoystickButton.Up => SpectrumKey.D4,
                SpectrumJoystickButton.Fire => SpectrumKey.D5,
                _ => SpectrumKey.Space
            };
            return true;
        }

        if (mode == ZxNextJoystickMode.Cursor)
        {
            key = button switch
            {
                SpectrumJoystickButton.Left => SpectrumKey.D5,
                SpectrumJoystickButton.Down => SpectrumKey.D6,
                SpectrumJoystickButton.Up => SpectrumKey.D7,
                SpectrumJoystickButton.Right => SpectrumKey.D8,
                SpectrumJoystickButton.Fire => SpectrumKey.D0,
                _ => SpectrumKey.Space
            };
            return true;
        }

        return false;
    }

    private static bool RoutesToKempston1(ZxNextJoystickMode mode) =>
        mode is ZxNextJoystickMode.Kempston1 or ZxNextJoystickMode.MegaDrive1;

    private static bool RoutesToKempston2(ZxNextJoystickMode mode) =>
        mode is ZxNextJoystickMode.Kempston2 or ZxNextJoystickMode.MegaDrive2;

    private static int ValidateConnector(ZxNextJoystickConnector connector)
    {
        int index = (int)connector;
        if ((uint)index >= 2)
        {
            throw new ArgumentOutOfRangeException(nameof(connector));
        }

        return index;
    }

    private static byte ButtonMask(SpectrumJoystickButton button) => button switch
    {
        SpectrumJoystickButton.Right => 0x01,
        SpectrumJoystickButton.Left => 0x02,
        SpectrumJoystickButton.Down => 0x04,
        SpectrumJoystickButton.Up => 0x08,
        SpectrumJoystickButton.Fire => 0x10,
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };
}
