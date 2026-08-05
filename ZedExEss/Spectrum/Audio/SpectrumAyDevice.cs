using System;using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Audio
{
    /// <summary>
    /// AY register-select/data port adapter for 128K and clone models.
    /// </summary>
    public sealed class SpectrumAyDevice(AY38912 chip) : IPortDevice
    {
        private const ushort AyRegisterSelectMask = 0xC002;
        private const ushort AyRegisterSelectValue = 0xC000; // 0xFFFD
        private const ushort AyDataWriteValue = 0x8000; // 0xBFFD
        private readonly AY38912 _chip = chip ?? throw new ArgumentNullException(nameof(chip));
        private byte _selectedRegister;

        public AY38912 Chip => _chip;
        public bool HandlesPort(ushort port)
        {
            ushort masked = (ushort)(port & AyRegisterSelectMask);
            return masked == AyRegisterSelectValue || masked == AyDataWriteValue;
        }
        public byte Read(ushort port)
        {
            if ((port & AyRegisterSelectMask) == AyRegisterSelectValue)
            {
                return _chip.ReadRegister(_selectedRegister);
            }

            return 0xFF;
        }
        public void Write(ushort port, byte value)
        {
            ushort masked = (ushort)(port & AyRegisterSelectMask);
            if (masked == AyRegisterSelectValue)
            {
                _selectedRegister = (byte)(value & 0x0F);
                return;
            }

            if (masked == AyDataWriteValue)
            {
                _chip.WriteRegister(_selectedRegister, value);
            }
        }
    }
}
