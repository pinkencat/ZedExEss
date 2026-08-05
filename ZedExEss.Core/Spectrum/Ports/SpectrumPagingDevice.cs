using System;using ZedExEss.Spectrum.Abstractions; using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Spectrum.Ports
{
    /// <summary>
    /// Paging port decode style used by 128K, +2A/+3 and Scorpion-class machines.
    /// </summary>
    public enum SpectrumPagingPortMode
    {
        Standard128,
        Plus3,
        Scorpion
    }

    /// <summary>
    /// Routes model-specific paging port writes into the memory mapper.
    /// </summary>
    public sealed class SpectrumPagingDevice : IPortDevice
    {
        private readonly SpectrumMemory _memory;
        private readonly SpectrumPagingPortMode _mode;

        public SpectrumPagingDevice(SpectrumMemory memory, bool supportsPlus3)
            : this(memory, supportsPlus3 ? SpectrumPagingPortMode.Plus3 : SpectrumPagingPortMode.Standard128)
        {
        }

        public SpectrumPagingDevice(SpectrumMemory memory, SpectrumPagingPortMode mode)
        {
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _mode = mode;
        }
        public bool HandlesPort(ushort port)
        {
            if (_mode is SpectrumPagingPortMode.Plus3 or SpectrumPagingPortMode.Scorpion)
            {
                if ((port & 0xC002) == 0x4000)
                {
                    return true;
                }

                if ((port & 0xF002) == 0x1000)
                {
                    return true;
                }

                return false;
            }

            if ((port & 0x8002) == 0x0000)
            {
                return true;
            }

            return false;
        }
        public byte Read(ushort port)
        {
            return 0xFF;
        }
        public void Write(ushort port, byte value)
        {
            if ((_mode is SpectrumPagingPortMode.Plus3 or SpectrumPagingPortMode.Scorpion) && (port & 0xF002) == 0x1000)
            {
                _memory.WritePort1FFD(value);
                return;
            }

            if (_mode is SpectrumPagingPortMode.Plus3 or SpectrumPagingPortMode.Scorpion)
            {
                if ((port & 0xC002) == 0x4000)
                {
                    _memory.WritePort7FFD(value);
                }

                return;
            }

            if ((port & 0x8002) == 0x0000)
            {
                _memory.WritePort7FFD(value);
                return;
            }
        }
    }
}
