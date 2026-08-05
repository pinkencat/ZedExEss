using System;using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Input
{
    /// <summary>
    /// Supported joystick adapters exposed through either ports or keyboard-matrix mappings.
    /// </summary>
    public enum SpectrumJoystickType
    {
        None,
        Kempston,
        Sinclair1,
        Sinclair2,
        Cursor
    }

    /// <summary>
    /// Buttons tracked by the emulated joystick device.
    /// </summary>
    public enum SpectrumJoystickButton
    {
        Up,
        Down,
        Left,
        Right,
        Fire
    }

    /// <summary>
    /// Joystick port and keyboard-matrix adapter for Kempston, Sinclair and Cursor/Protek modes.
    /// </summary>
    public sealed class SpectrumJoystickDevice(SpectrumKeyboard keyboard) : IPortDevice
    {
        private readonly SpectrumKeyboard _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        private SpectrumJoystickType _type;
        private byte _state;

        public SpectrumJoystickType Type
        {
            get => _type;
            set
            {
                if (_type == value)
                {
                    return;
                }

                ApplyKeyboardOverlay(pressed: false);
                _type = value;
                ApplyKeyboardOverlay(pressed: true);
            }
        }
        public bool HandlesPort(ushort port)
        {
            return _type == SpectrumJoystickType.Kempston && (port & 0x0020) == 0;
        }
        public byte Read(ushort port)
        {
            return _type == SpectrumJoystickType.Kempston ? _state : (byte)0xFF;
        }
        public void Write(ushort port, byte value)
        {
        }
        public void SetButtonState(SpectrumJoystickButton button, bool pressed)
        {
            byte mask = ButtonMask(button);
            byte previous = _state;
            _state = pressed ? (byte)(_state | mask) : (byte)(_state & ~mask);
            if (previous == _state)
            {
                return;
            }

            if (_type != SpectrumJoystickType.Kempston)
            {
                ApplyKeyboardButton(button, pressed);
            }
        }
        private void ApplyKeyboardOverlay(bool pressed)
        {
            if (_type == SpectrumJoystickType.Kempston || _type == SpectrumJoystickType.None)
            {
                return;
            }

            ApplyKeyboardButton(SpectrumJoystickButton.Left, pressed && (_state & 0x02) != 0);
            ApplyKeyboardButton(SpectrumJoystickButton.Right, pressed && (_state & 0x01) != 0);
            ApplyKeyboardButton(SpectrumJoystickButton.Down, pressed && (_state & 0x04) != 0);
            ApplyKeyboardButton(SpectrumJoystickButton.Up, pressed && (_state & 0x08) != 0);
            ApplyKeyboardButton(SpectrumJoystickButton.Fire, pressed && (_state & 0x10) != 0);
        }
        private void ApplyKeyboardButton(SpectrumJoystickButton button, bool pressed)
        {
            if (!TryGetMappedKey(_type, button, out SpectrumKey key))
            {
                return;
            }

            _keyboard.SetJoystickKeyState(key, pressed);
        }
        private static bool TryGetMappedKey(SpectrumJoystickType type, SpectrumJoystickButton button, out SpectrumKey key)
        {
            key = SpectrumKey.Space;
            switch (type)
            {
                case SpectrumJoystickType.Sinclair1:
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

                case SpectrumJoystickType.Sinclair2:
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

                case SpectrumJoystickType.Cursor:
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

                default:
                    return false;
            }
        }
        private static byte ButtonMask(SpectrumJoystickButton button)
        {
            return button switch
            {
                SpectrumJoystickButton.Right => 0x01,
                SpectrumJoystickButton.Left => 0x02,
                SpectrumJoystickButton.Down => 0x04,
                SpectrumJoystickButton.Up => 0x08,
                SpectrumJoystickButton.Fire => 0x10,
                _ => 0
            };
        }
    }
}
