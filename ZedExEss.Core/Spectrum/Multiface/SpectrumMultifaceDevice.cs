using System;

namespace ZedExEss.Spectrum.Multiface
{
    /// <summary>
    /// A Multiface: 8 KiB of ROM and 8 KiB of RAM paged over the machine ROM on demand.
    /// </summary>
    /// <remarks>
    /// Pressing the button asserts NMI, but nothing is paged until the CPU fetches the
    /// NMI vector at $0066 - so the interrupt is taken through the machine's own ROM and
    /// only then does the Multiface appear underneath it.
    ///
    /// Both paging in and paging out are triggered by port READS: the firmware pages
    /// itself out with an IN before every call into the machine ROM (to print, or to
    /// let the interrupt handler scan the keyboard) and pages back in with an IN
    /// afterwards. This is exactly why a Multiface One cannot share a machine with a
    /// Kempston joystick - the joystick reads the same $1F that pages the interface out.
    /// The writes are the visibility latch, not the paging.
    /// </remarks>
    public sealed class SpectrumMultifaceDevice
    {
        /// <summary>The ROM occupies the bottom 8 KiB, the RAM the 8 KiB above it.</summary>
        public const int RomSize = 8 * 1024;

        /// <summary>Battery-backed RAM, which is where a saved snapshot is built.</summary>
        public const int RamSize = 8 * 1024;

        private readonly byte[] _rom = new byte[RomSize];
        private readonly byte[] _ram = new byte[RamSize];

        /// <summary>
        /// True while the Multiface is hidden from its own enable port.
        /// </summary>
        /// <remarks>
        /// The 128 hides itself again after paging out so that software probing the port
        /// cannot find it. Multiface 3 always answers, which is how its software detects
        /// it.
        /// </remarks>
        private bool _invisible = true;

        public SpectrumMultifaceDevice(SpectrumMultifaceModel model, ReadOnlySpan<byte> rom)
        {
            if (model == SpectrumMultifaceModel.None)
            {
                throw new ArgumentOutOfRangeException(nameof(model), model, "No Multiface to create.");
            }

            if (rom.Length != RomSize)
            {
                throw new ArgumentException($"A Multiface ROM must be {RomSize} bytes.", nameof(rom));
            }

            Model = model;
            rom.CopyTo(_rom);
        }

        public SpectrumMultifaceModel Model { get; }

        /// <summary>True once the button has been pressed and before the handler returns.</summary>
        public bool NmiActive { get; private set; }

        /// <summary>True while the ROM and RAM overlay the machine's low 16 KiB.</summary>
        public bool IsPaged { get; private set; }

        /// <summary>The port whose read pages the Multiface in.</summary>
        public byte EnablePort => Model switch
        {
            SpectrumMultifaceModel.One => 0x9F,
            SpectrumMultifaceModel.OneTwentyEight => 0xBF,
            _ => 0x3F
        };

        /// <summary>The port whose write pages the Multiface out.</summary>
        public byte DisablePort => Model switch
        {
            SpectrumMultifaceModel.One => 0x1F,
            SpectrumMultifaceModel.OneTwentyEight => 0x3F,
            _ => 0xBF
        };

        /// <summary>Presses the button. Returns false when it would do nothing.</summary>
        public bool RequestNmi()
        {
            // Only a press while the overlay is already up is ignored. The firmware can
            // leave through the machine ROM without a RETN, so refusing on NmiActive
            // alone would make the button work exactly once per session.
            if (IsPaged)
            {
                return false;
            }

            NmiActive = true;
            _invisible = false;
            return true;
        }

        /// <summary>Pages in when the CPU fetches the NMI vector, not before.</summary>
        public void BeforeOpcodeFetch(ushort address)
        {
            if (NmiActive && address == 0x0066)
            {
                IsPaged = true;
            }
        }

        /// <summary>RETN from the handler releases the overlay.</summary>
        public void OnRetnExecuted()
        {
            NmiActive = false;
            IsPaged = false;
        }

        public byte ReadMemory(ushort address) => address < RomSize
            ? _rom[address]
            : _ram[(address - RomSize) & (RamSize - 1)];

        /// <summary>Writes reach the RAM half only; the ROM half is masked.</summary>
        public void WriteMemory(ushort address, byte value)
        {
            if (address < RomSize)
            {
                return;
            }

            _ram[(address - RomSize) & (RamSize - 1)] = value;
        }

        /// <summary>
        /// Applies a port read. Returns true when the Multiface drives the bus.
        /// </summary>
        public bool TryReadPort(ushort port, byte port7Ffd, byte port1Ffd, out byte value)
        {
            value = 0xFF;
            if ((byte)port == DisablePort)
            {
                // The firmware pages itself out around every call into the machine ROM,
                // so this is the common case, not the end of the session. The visibility
                // latch is left alone: an IN on the enable port must page it back in.
                PageOut();
                return false;
            }

            if ((byte)port != EnablePort)
            {
                return false;
            }

            bool hidden = _invisible && Model != SpectrumMultifaceModel.Three;
            if (!hidden)
            {
                IsPaged = true;
            }

            switch (Model)
            {
                case SpectrumMultifaceModel.One:
                    // Multiface One drives nothing; the read only pages it in.
                    return false;

                case SpectrumMultifaceModel.OneTwentyEight:
                    if (hidden)
                    {
                        return false;
                    }

                    // Bit 7 mirrors the screen-page bit so the handler can restore paging.
                    value = (byte)(((port7Ffd & 0x08) != 0 ? 0x80 : 0x00) | 0x7F);
                    return true;

                default:
                    // Multiface 3 answers with whichever paging latch the high byte
                    // selects, which is how its handler recovers the state it interrupted.
                    value = (port >> 12) switch
                    {
                        0x01 => port1Ffd,
                        0x07 => port7Ffd,
                        _ => 0xFF
                    };
                    return true;
            }
        }

        /// <summary>Applies a port write. Returns true when the Multiface consumed it.</summary>
        public bool TryWritePort(ushort port)
        {
            byte low = (byte)port;
            bool enable = low == EnablePort;
            bool disable = low == DisablePort;
            if (!enable && !disable)
            {
                return false;
            }

            // Writing the disable port is the visibility lock, not the paging control.
            // The firmware writes it in the middle of a session and carries straight on
            // running from its own ROM, so a write must never lift the overlay - it only
            // hides an interface that is already out of the way, which is what stops
            // software detecting one that is not in use.
            if (disable && !IsPaged)
            {
                _invisible = true;
            }

            return true;
        }

        /// <summary>
        /// Lifts the overlay and releases the NMI.
        /// </summary>
        /// <remarks>
        /// The NMI is released here rather than only on RETN because the firmware's
        /// "return to BASIC" path leaves by jumping into the machine ROM instead.
        /// </remarks>
        private void PageOut()
        {
            IsPaged = false;
            NmiActive = false;
        }

        public void Reset()
        {
            NmiActive = false;
            IsPaged = false;
            _invisible = true;
        }
    }
}
