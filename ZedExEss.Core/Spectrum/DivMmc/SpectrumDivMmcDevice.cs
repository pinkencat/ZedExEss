using System;using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.DivMmc
{
    /// <summary>
    /// DivMMC memory mapper and SPI port device, including automap, CONMEM and MAPRAM behaviour.
    /// </summary>
    /// <remarks>
    /// Opcode-fetch notifications control automapping; ordinary memory reads do not.
    /// When active, the expansion overlays two 8K windows at 0000-3FFF while the
    /// Spectrum's underlying mapping remains intact for immediate unpaging.
    /// </remarks>
    public sealed class SpectrumDivMmcDevice : IPortDevice
    {
        private const int RomSize = 8 * 1024;
        private const int RamBankSize = 8 * 1024;
        private const byte ControlPort = 0xE3;
        private const byte DivMmcCardSelectPort = 0xE7;
        private const byte DivMmcSpiPort = 0xEB;

        private readonly byte[] _rom;
        private readonly byte[][] _ramBanks;
        private readonly int _bankMask;
        private readonly object _sdLock = new();

        private bool _conmem;
        private bool _mapram;
        private bool _automap;
        private int _bank;
        private byte _cardSelect = 0x03;
        private SpectrumDivMmcSdCard? _sdCard;

        public SpectrumDivMmcDevice(SpectrumDivExpansionMode mode, ReadOnlySpan<byte> firmwareRom, int ramBankCount)
        {
            if (mode != SpectrumDivExpansionMode.DivMmc)
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Div expansion mode must be DivMMC.");
            }

            if (firmwareRom.Length != RomSize)
            {
                throw new ArgumentException($"DivMMC firmware ROM must be {RomSize} bytes.", nameof(firmwareRom));
            }

            if (ramBankCount < 4 || (ramBankCount & (ramBankCount - 1)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(ramBankCount), ramBankCount, "RAM bank count must be a power of two and at least 4.");
            }

            _rom = firmwareRom.ToArray();
            _ramBanks = new byte[ramBankCount][];
            for (int i = 0; i < _ramBanks.Length; i++)
            {
                _ramBanks[i] = new byte[RamBankSize];
            }

            _bankMask = ramBankCount - 1;
        }

        public SpectrumDivExpansionMode Mode => SpectrumDivExpansionMode.DivMmc;
        public bool IsActive => _conmem || _automap;
        public bool Conmem => _conmem;
        public bool Mapram => _mapram;
        public bool Automap => _automap;
        public bool AutomapTrDosEntryEnabled { get; set; } = true;
        public int Bank => _bank & _bankMask;
        public byte CardSelect => (byte)(_cardSelect & 0x03);
        public void AttachSdCard(SpectrumDivMmcSdCard? sdCard)
        {
            lock (_sdLock)
            {
                // Reset the SPI card state on media changes so firmware sees a freshly inserted device.
                _sdCard = sdCard;
                _sdCard?.Reset();
            }
        }
        public void PowerOn()
        {
            // DivMMC starts with its ROM unmapped; firmware enters through automap traps or CONMEM.
            _conmem = false;
            _mapram = false;
            _automap = false;
            _bank = 0;
            _cardSelect = 0x03;
        }
        public bool HandlesPort(ushort port)
        {
            byte lowPort = (byte)port;
            if (lowPort == ControlPort)
            {
                return true;
            }

            return lowPort is DivMmcCardSelectPort or DivMmcSpiPort;
        }
        public byte Read(ushort port)
        {
            byte lowPort = (byte)port;
            if (lowPort == DivMmcSpiPort)
            {
                return TransferSpi(0xFF);
            }

            return 0xFF;
        }
        public void Write(ushort port, byte value)
        {
            byte lowPort = (byte)port;
            if (lowPort == ControlPort)
            {
                // Bit 6 latches MAPRAM permanently until reset. This protects bank 3 firmware RAM
                // after esxDOS has copied itself there.
                _conmem = (value & 0x80) != 0;
                _mapram |= (value & 0x40) != 0;
                _bank = value & _bankMask;
                return;
            }

            if (lowPort == DivMmcCardSelectPort)
            {
                // Card select is active-low. Deselecting resets command framing but not app-command state.
                _cardSelect = (byte)(value & 0x03);
                if (_cardSelect == 0x03)
                {
                    lock (_sdLock)
                    {
                        _sdCard?.Deselect();
                    }
                }
            }
            else if (lowPort == DivMmcSpiPort)
            {
                _ = TransferSpi(value);
            }
        }
        public byte ReadMemory(ushort address)
        {
            if (!IsActive || address >= 0x4000)
            {
                return 0xFF;
            }

            int offset = address & 0x1FFF;
            if (address < 0x2000)
            {
                // Lower 8 KB is ROM normally, but MAPRAM exposes RAM bank 3 after firmware takeover.
                return !_conmem && _mapram
                    ? _ramBanks[3][offset]
                    : _rom[offset];
            }

            return _ramBanks[Bank][offset];
        }
        public bool TryWriteMemory(ushort address, byte value)
        {
            if (!IsActive || address >= 0x4000)
            {
                return false;
            }

            if (address < 0x2000)
            {
                // Low page is ROM or protected MAPRAM bank 3; either way CPU writes disappear.
                return true;
            }

            int bank = Bank;
            if (!_conmem && _mapram && bank == 3)
            {
                // MAPRAM protects the resident firmware bank from accidental overwrites.
                return true;
            }

            _ramBanks[bank][address & 0x1FFF] = value;
            return true;
        }
        public void BeforeOpcodeFetch(ushort pc)
        {
            if (AutomapTrDosEntryEnabled && (pc & 0xFF00) == 0x3D00)
            {
                // DivMMC-compatible firmware can hook TR-DOS entry fetches when enabled.
                _automap = true;
            }
        }
        public void AfterOpcodeFetch(ushort pc)
        {
            if (IsEntryTrap(pc))
            {
                // These ROM entry points are trapped after the opcode fetch so the fetched byte
                // still comes from the original ROM, matching DivMMC automap behaviour.
                _automap = true;
            }

            if (pc >= 0x1FF8 && pc <= 0x1FFF)
            {
                // Firmware exits by executing from the documented unmap window.
                _automap = false;
            }
        }
        private static bool IsEntryTrap(ushort pc)
        {
            return pc is 0x0000 or 0x0008 or 0x0038 or 0x0066 or 0x04C6 or 0x0562;
        }
        private byte TransferSpi(byte value)
        {
            if ((_cardSelect & 0x03) == 0x03)
            {
                // No selected card leaves MISO high.
                return 0xFF;
            }

            lock (_sdLock)
            {
                if (_sdCard == null)
                {
                    return 0xFF;
                }

                return _sdCard.Transfer(value);
            }
        }
    }
}
