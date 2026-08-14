using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Tape;

namespace ZedExEss.Spectrum.Video
{
    /// <summary>
    /// ULA port device for border, beeper, tape output, EAR input and keyboard reads.
    /// </summary>
    public sealed class SpectrumUla(
        SpectrumModel model,
        SpectrumUlaRenderer renderer,
        SpectrumKeyboard? keyboard = null,
        SpectrumEarInputDevice? earInput = null,
        IBeeperSink? beeper = null,
        ITapeSink? tape = null,
        Action? syncBeforeWrite = null) : IPortDevice
    {
        private readonly SpectrumModel _model = model;
        private readonly SpectrumUlaRenderer _renderer = renderer;
        private readonly IBeeperSink? _beeper = beeper;
        private readonly ITapeSink? _tape = tape;
        private readonly SpectrumKeyboard? _keyboard = keyboard;
        private readonly SpectrumEarInputDevice? _earInput = earInput;
        private readonly Action? _syncBeforeWrite = syncBeforeWrite;
        private byte _lastByte;
        private byte _defaultValue = 0xFF;

        public SpectrumUlaRenderer Renderer => _renderer;
        public byte LastOutputByte => _lastByte;
        public bool HandlesPort(ushort port)
        {
            return SpectrumModelTraits.HasFullyDecodedUlaPort(_model)
                ? (port & 0x00FF) == 0x00FE
                : (port & 0x0001) == 0;
        }
        public byte Read(ushort port)
        {
            byte value = _defaultValue;

            if (_keyboard != null)
            {
                value &= _keyboard.ReadRows((byte)(port >> 8));
            }

            if (_earInput != null)
            {
                if (_earInput.ReadEarBit())
                {
                    value ^= 0x40;
                }
            }

            return value;
        }
        public void Write(ushort port, byte value)
        {
            _syncBeforeWrite?.Invoke();
            ApplyOutputLatch(value);
        }

        /// <summary>Restores the FE latch without advancing emulated time.</summary>
        public void RestoreOutputLatch(byte value)
        {
            ApplyOutputLatch(value);
        }

        private void ApplyOutputLatch(byte value)
        {
            _lastByte = value;
            _renderer.BorderColorIndex = (byte)(value & 0x07);
            _beeper?.SetLevel((value & 0x10) != 0);
            _tape?.SetLevel((value & 0x08) != 0);

            if (_model == SpectrumModel.SpectrumPlus2A || _model == SpectrumModel.SpectrumPlus3)
            {
                _defaultValue = 0xBF;
                return;
            }

            if (_model == SpectrumModel.Spectrum128K || _model == SpectrumModel.SpectrumPlus2)
            {
                _defaultValue = (value & 0x10) != 0 ? (byte)0xFF : (byte)0xBF;
                return;
            }

            _defaultValue = (value & 0x10) != 0 ? (byte)0xFF : (byte)0xBF;
        }
    }
}
