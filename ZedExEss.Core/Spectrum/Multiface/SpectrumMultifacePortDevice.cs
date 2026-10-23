using System;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Spectrum.Multiface
{
    /// <summary>Routes the Multiface's paging ports on and off the bus.</summary>
    /// <remarks>
    /// Reads and writes decode different ports, so both are claimed. A read of the
    /// enable port pages the Multiface in and, on the 128 and +3, also drives the
    /// paging latch the handler needs to restore what it interrupted.
    /// </remarks>
    public sealed class SpectrumMultifacePortDevice : IPortDevice
    {
        private readonly SpectrumMultifaceDevice _multiface;
        private readonly SpectrumMemory _memory;

        public SpectrumMultifacePortDevice(SpectrumMultifaceDevice multiface, SpectrumMemory memory)
        {
            _multiface = multiface ?? throw new ArgumentNullException(nameof(multiface));
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        }

        public bool HandlesPort(ushort port)
        {
            byte low = (byte)port;
            return low == _multiface.EnablePort || low == _multiface.DisablePort;
        }

        public byte Read(ushort port) =>
            _multiface.TryReadPort(port, _memory.Port7FFD, _memory.Port1FFD, out byte value)
                ? value
                : (byte)0xFF;

        public void Write(ushort port, byte value) => _multiface.TryWritePort(port);
    }
}
