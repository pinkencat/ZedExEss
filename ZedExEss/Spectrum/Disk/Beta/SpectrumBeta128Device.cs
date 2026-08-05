using System;

namespace ZedExEss.Spectrum.Disk.Beta
{
    /// <summary>
    /// Beta 128/TR-DOS ROM automap latch used by Pentagon and Scorpion compatible machines.
    /// </summary>
    /// <remarks>
    /// The TR-DOS ROM pages when an opcode is fetched from 3D00-3DFF while the
    /// machine ROM is visible, and unpages on the first opcode fetch from RAM.
    /// Data reads alone must never alter the latch.
    /// </remarks>
    public sealed class SpectrumBeta128Device
    {
        private const int RomSize = 16 * 1024;
        private readonly byte[] _trdosRom;
        private bool _paged;

        public SpectrumBeta128Device(ReadOnlySpan<byte> trdosRom)
        {
            if (trdosRom.Length != RomSize)
            {
                throw new ArgumentException($"TR-DOS ROM must be {RomSize} bytes.", nameof(trdosRom));
            }

            _trdosRom = trdosRom.ToArray();
        }

        public bool IsPaged => _paged;
        public void Reset()
        {
            _paged = false;
        }
        public void BeforeOpcodeFetch(ushort pc, bool allowRomTrap)
        {
            if (_paged)
            {
                if (allowRomTrap && pc >= 0x4000)
                {
                    _paged = false;
                }

                return;
            }

            if (allowRomTrap && (pc & 0xFF00) == 0x3D00)
            {
                _paged = true;
            }
        }
        public byte ReadMemory(ushort address)
        {
            return address < RomSize ? _trdosRom[address] : (byte)0xFF;
        }
    }
}
