using System;using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Ports
{
    /// <summary>
    /// Adapter between the Z80 core's high-byte port convention and the emulator port bus.
    /// </summary>
    public sealed class Z80PortAdapter(IPortBus bus, Func<byte> highByteProvider)
    {
        private readonly IPortBus _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        private readonly Func<byte> _highByteProvider = highByteProvider ?? throw new ArgumentNullException(nameof(highByteProvider));
        public byte Read(byte portLow)
        {
            ushort port = (ushort)((_highByteProvider() << 8) | portLow);
            return _bus.Read(port);
        }
        public void Write(byte portLow, byte value)
        {
            ushort port = (ushort)((_highByteProvider() << 8) | portLow);
            _bus.Write(port, value);
        }
    }
}
