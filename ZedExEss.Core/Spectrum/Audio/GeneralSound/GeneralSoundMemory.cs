using System;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Audio.GeneralSound
{
    /// <summary>The card's address space, and the memory-mapped DAC window inside it.</summary>
    /// <remarks>
    /// The map is not four independently banked pages. The bottom 16 KB is always ROM,
    /// the next 16 KB is always the first RAM page, and the top 32 KB is a single
    /// switchable page selected by the low nibble of port $00: page 0 maps the whole
    /// 32 KB ROM there, and pages 1 upwards map consecutive pairs of RAM banks.
    ///
    /// Sample output is memory-mapped, and fed by READS rather than writes: when the
    /// card's Z80 reads $6000-$7FFF the byte it fetches is clocked straight into a
    /// channel, the channel coming from bits 8 and 9 of the address. The mixing loop
    /// simply walks its sample buffer with LD A,(HL) and the hardware does the rest.
    /// Feeding the DAC on writes instead produces sound, but from whatever the firmware
    /// happens to store rather than from the sample stream, which is noise.
    /// </remarks>
    public sealed class GeneralSoundMemory : IZ80MemoryBus
    {
        /// <summary>Every ROM half and every RAM bank is 16 KB.</summary>
        public const int PageSize = 0x4000;

        /// <summary>The switchable window is two banks wide.</summary>
        public const int WindowBanks = 2;

        /// <summary>16 KB banks in the 512 KB card.</summary>
        public const int StandardBankCount = 32;

        /// <summary>Port $00 selects the high window with its low nibble.</summary>
        public const int PageMask = 0x0F;

        /// <summary>Reads in this range are clocked into a DAC channel.</summary>
        public const ushort DacWindowStart = 0x6000;
        public const ushort DacWindowEnd = 0x7FFF;

        /// <summary>
        /// The fixed window at $4000 shows the last RAM bank.
        /// </summary>
        /// <remarks>
        /// Not the first: bank 0 is already reachable at $8000 as page 1, so putting it
        /// here as well would waste a bank and alias the firmware's workspace.
        /// </remarks>
        public const int FixedWindowBank = 15;

        private readonly byte[][] _rom = [new byte[PageSize], new byte[PageSize]];
        private readonly byte[][] _ram;
        private readonly int _bankMask;
        private readonly Action<int, byte> _dacFeed;
        private int _mappedPage;

        internal GeneralSoundMemory(ReadOnlySpan<byte> firmware, int bankCount, Action<int, byte> dacFeed)
        {
            _dacFeed = dacFeed ?? throw new ArgumentNullException(nameof(dacFeed));

            // A 32 KB part: the lower half sits at the bottom of the map permanently,
            // and both halves appear together in the high window as page 0.
            firmware[..Math.Min(firmware.Length, PageSize)].CopyTo(_rom[0]);
            if (firmware.Length > PageSize)
            {
                firmware[PageSize..Math.Min(firmware.Length, 2 * PageSize)].CopyTo(_rom[1]);
            }

            _ram = new byte[bankCount][];
            for (int bank = 0; bank < bankCount; bank++)
            {
                _ram[bank] = new byte[PageSize];
            }

            _bankMask = bankCount - 1;
        }

        /// <summary>The page currently in the high window; zero means the ROM.</summary>
        public int MappedPage => _mappedPage;

        /// <summary>The last value written to the mapping port.</summary>
        public byte MappingValue { get; private set; }

        public int BankCount => _ram.Length;

        /// <summary>True while the high window shows ROM rather than RAM.</summary>
        public bool HighWindowIsRom => _mappedPage == 0;

        /// <summary>Port $00: the low nibble selects the 32 KB high window.</summary>
        public void WriteMappingValue(byte value)
        {
            MappingValue = value;
            _mappedPage = value & PageMask;
        }

        public byte Read(ushort address)
        {
            byte value = Resolve(address, out int offset)[offset];
            if (address is >= DacWindowStart and <= DacWindowEnd)
            {
                _dacFeed((address >> 8) & 0x03, value);
            }

            return value;
        }

        /// <summary>An opcode fetch does not clock the DAC, only a data read does.</summary>
        public byte FetchOpcode(ushort address) => Resolve(address, out int offset)[offset];

        public void WriteCpu(ushort address, byte value)
        {
            if (address < PageSize)
            {
                // The bottom of the map is masked ROM and never becomes RAM.
                return;
            }

            if (address >= 2 * PageSize && HighWindowIsRom)
            {
                // The high window is showing ROM; a write there goes nowhere.
                return;
            }

            Resolve(address, out int offset)[offset] = value;
        }

        public void Reset()
        {
            MappingValue = 0;
            _mappedPage = 0;
            for (int bank = 0; bank < _ram.Length; bank++)
            {
                Array.Clear(_ram[bank]);
            }
        }

        private byte[] Resolve(ushort address, out int offset)
        {
            offset = address & (PageSize - 1);
            switch (address >> 14)
            {
                case 0:
                    return _rom[0];
                case 1:
                    // The fixed RAM window, where the DAC read window lives.
                    return _ram[FixedWindowBank & _bankMask];
                default:
                    int half = (address >> 14) - 2;
                    if (HighWindowIsRom)
                    {
                        return _rom[half];
                    }

                    // Page 1 is the first RAM pair, page 2 the next, and so on.
                    return _ram[(((_mappedPage - 1) * WindowBanks) + half) & _bankMask];
            }
        }
    }
}
