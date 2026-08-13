using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Debugging;
using ZedExEss.Spectrum.Disk.Beta;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.Spectrum.Interface1;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Memory
{
    /// <summary>
    /// Model-aware memory map, including ROM/RAM paging, expansion overlays and delayed screen-write visibility.
    /// </summary>
    public sealed class SpectrumMemory : IMemoryBus, IContendedPageProvider, IScreenMemoryProvider, IScreenWriteSynchronizer
    {
        private const int PageSize = 16 * 1024;
        private const int ScreenSize = 0x1B00;
        private static readonly int[,] Plus3SpecialPagingMap =
        {
            // 1FFD bits 1-2 select one of the four all-RAM maps documented for +2A/+3.
            { 0, 1, 2, 3 },
            { 4, 5, 6, 7 },
            { 4, 5, 6, 3 },
            { 4, 7, 6, 3 }
        };

        private readonly SpectrumModel _model;
        private Z80? _cpu;
        private IContentionProfile? _contention;
        private readonly byte[][] _mappedPages = new byte[4][];
        private readonly bool[] _pageReadOnly = new bool[4];
        private readonly bool[] _pageContended = new bool[4];
        private readonly int[] _pageBankIndex = new int[4];
        private readonly byte[][] _ramBanks;
        private readonly byte[] _openBusPage;
        private readonly byte[][] _screenShadowBanks;
        // Screen writes become visible to the renderer after the CPU-side write
        // has reached the ULA-visible point for the active timing model.
        private readonly Queue<PendingScreenWrite> _pendingScreenWrites = new();

        private SpectrumDivMmcDevice? _divExpansion;
        private SpectrumBeta128Device? _beta128;
        private SpectrumInterface1Device? _interface1;
        private RomSet _roms;
        private byte _port7ffd;
        private byte _port1ffd;
        private bool _pagingLocked;
        private bool _specialPaging;
        private int _screenBank;
        private int _currentRomBank;
        private readonly struct PendingScreenWrite(ulong applyAt, int bankIndex, int offset, byte value)
        {
            public ulong ApplyAt { get; } = applyAt;
            public int BankIndex { get; } = bankIndex;
            public int Offset { get; } = offset;
            public byte Value { get; } = value;
        }

        public SpectrumMemory(
            SpectrumModel model,
            RomSet? roms = null,
            Z80? cpu = null,
            IContentionProfile? contention = null)
        {
            _model = model;
            _cpu = cpu;
            _contention = contention;
            _ramBanks = CreateRamBanks(model);
            _screenShadowBanks = CreateScreenShadowBanks(_ramBanks.Length);
            _roms = roms ?? RomSet.CreateBlank(GetRomBankCount(model));

            _openBusPage = new byte[PageSize];
            Array.Fill(_openBusPage, (byte)0xFF);

            Reset();
        }

        public SpectrumModel Model => _model;

        public int CurrentRomBank => _currentRomBank;
        public SpectrumMemoryMapping GetMapping(ushort address)
        {
            // This is a debugger description only. It must not perform a bus read,
            // trigger automapping, or consume contention time.
            int page = address >> 14;
            int offset = address & 0x3FFF;
            int bank = _pageBankIndex[page];
            bool isOpenBus = ReferenceEquals(_mappedPages[page], _openBusPage);
            bool isRom = _pageReadOnly[page] && bank < 0 && !isOpenBus;
            return new SpectrumMemoryMapping(
                address,
                page,
                bank >= 0,
                isRom,
                isOpenBus,
                _pageReadOnly[page],
                _pageContended[page],
                bank,
                offset,
                isRom && page == 0 ? _currentRomBank : -1,
                _model);
        }
        public bool CanWriteDirect(ushort address)
        {
            return !_pageReadOnly[address >> 14];
        }
        public void ConfigureDivExpansion(SpectrumDivMmcDevice? divExpansion)
        {
            _divExpansion = divExpansion;
        }
        public void ConfigureBeta128(SpectrumBeta128Device? beta128)
        {
            _beta128 = beta128;
        }
        public void ConfigureInterface1(SpectrumInterface1Device? interface1)
        {
            _interface1 = interface1;
        }
        public void ConfigureTiming(Z80? cpu, IContentionProfile? contention)
        {
            _cpu = cpu;
            _contention = contention;
        }
        public void LoadRoms(RomSet roms)
        {
            ArgumentNullException.ThrowIfNull(roms);

            // Treat ROM count mismatches as configuration errors. Silent truncation here
            // makes paging bugs look like CPU or disk-controller faults later.
            int expected = GetRomBankCount(_model);
            if (roms.BankCount != expected)
            {
                throw new InvalidOperationException($"Model {_model} expects {expected} ROM banks, got {roms.BankCount}.");
            }

            _roms = roms;
            ApplyPaging();
            _pendingScreenWrites.Clear();
        }
        public void LoadRamBank(int bankIndex, ReadOnlySpan<byte> data)
        {
            if (bankIndex < 0 || bankIndex >= _ramBanks.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(bankIndex));
            }

            if (data.Length != PageSize)
            {
                throw new ArgumentException($"RAM bank data must be {PageSize} bytes.", nameof(data));
            }

            data.CopyTo(_ramBanks[bankIndex]);
            // Snapshot loading bypasses normal CPU writes, so refresh the ULA-visible
            // screen shadow immediately.
            CopyScreenShadow(bankIndex);
            _pendingScreenWrites.Clear();
        }
        public void Reset()
        {
            // Hardware reset returns paging registers to their power-on state but leaves RAM content intact.
            _port7ffd = 0;
            _port1ffd = 0;
            _pagingLocked = false;
            _specialPaging = false;
            _screenBank = _model switch
            {
                SpectrumModel.Spectrum16K => 0,
                SpectrumModel.Spectrum48K => 0,
                _ => 5
            };
            ApplyPaging();
            ResetScreenShadow();
            _pendingScreenWrites.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte Read(ushort address)
        {
            SpectrumBeta128Device? beta128 = _beta128;
            if (beta128 != null && address < 0x4000 && beta128.IsPaged)
            {
                // TR-DOS ROM overlays the normal low 16 KB only while the Beta automap latch is active.
                ApplyMemoryContention(address);
                return beta128.ReadMemory(address);
            }

            SpectrumDivMmcDevice? divExpansion = _divExpansion;
            if (divExpansion != null && address < 0x4000 && divExpansion.IsActive)
            {
                // DivMMC has priority over normal ROM/RAM while CONMEM or automap is active.
                return divExpansion.ReadMemory(address);
            }

            SpectrumInterface1Device? interface1 = _interface1;
            if (interface1 != null && address < 0x4000 && interface1.IsPaged)
            {
                ApplyMemoryContention(address);
                return interface1.ReadMemory(address);
            }

            ApplyMemoryContention(address);
            return _mappedPages[address >> 14][address & 0x3FFF];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadDirect(ushort address)
        {
            // Direct reads are used by diagnostics/rendering and must not add contention.
            SpectrumBeta128Device? beta128 = _beta128;
            if (beta128 != null && address < 0x4000 && beta128.IsPaged)
            {
                return beta128.ReadMemory(address);
            }

            SpectrumDivMmcDevice? divExpansion = _divExpansion;
            if (divExpansion != null && address < 0x4000 && divExpansion.IsActive)
            {
                return divExpansion.ReadMemory(address);
            }

            SpectrumInterface1Device? interface1 = _interface1;
            if (interface1 != null && address < 0x4000 && interface1.IsPaged)
            {
                return interface1.ReadMemory(address);
            }

            return _mappedPages[address >> 14][address & 0x3FFF];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte FetchOpcode(ushort address)
        {
            SpectrumBeta128Device? beta128 = _beta128;
            // Automap hooks fire only on opcode fetches, not on data reads. This distinction
            // matters for both TR-DOS and DivMMC firmware entry traps.
            beta128?.BeforeOpcodeFetch(address, AllowsBeta128RomTrap());

            SpectrumDivMmcDevice? divExpansion = _divExpansion;
            divExpansion?.BeforeOpcodeFetch(address);

            SpectrumInterface1Device? interface1 = _interface1;
            interface1?.BeforeOpcodeFetch(address);

            byte value = Read(address);
            divExpansion?.AfterOpcodeFetch(address);
            interface1?.AfterOpcodeFetch(address);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ushort address, byte value)
        {
            SpectrumBeta128Device? beta128 = _beta128;
            if (beta128 != null && address < 0x4000 && beta128.IsPaged)
            {
                // Writes to ROM overlays are ignored after paying the normal bus cost.
                ApplyMemoryContention(address);
                return;
            }

            SpectrumDivMmcDevice? divExpansion = _divExpansion;
            if (divExpansion != null && address < 0x4000 && divExpansion.TryWriteMemory(address, value))
            {
                return;
            }

            SpectrumInterface1Device? interface1 = _interface1;
            if (interface1 != null && address < 0x4000 && interface1.IsPaged)
            {
                ApplyMemoryContention(address);
                return;
            }

            ApplyMemoryContention(address);
            int pageIndex = address >> 14;
            if (_pageReadOnly[pageIndex])
            {
                return;
            }

            int offset = address & 0x3FFF;
            _mappedPages[pageIndex][offset] = value;
            int bankIndex = _pageBankIndex[pageIndex];
            if (bankIndex >= 0 && offset < ScreenSize)
            {
                // Non-CPU writes, such as pokes or loaders, become visible immediately.
                _screenShadowBanks[bankIndex][offset] = value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteDirect(ushort address, byte value)
        {
            // Direct writes are intentionally immediate and uncontended; use WriteCpu for instruction writes.
            SpectrumBeta128Device? beta128 = _beta128;
            if (beta128 != null && address < 0x4000 && beta128.IsPaged)
            {
                return;
            }

            SpectrumDivMmcDevice? divExpansion = _divExpansion;
            if (divExpansion != null && address < 0x4000 && divExpansion.TryWriteMemory(address, value))
            {
                return;
            }

            SpectrumInterface1Device? interface1 = _interface1;
            if (interface1 != null && address < 0x4000 && interface1.IsPaged)
            {
                return;
            }

            int pageIndex = address >> 14;
            if (_pageReadOnly[pageIndex])
            {
                return;
            }

            int offset = address & 0x3FFF;
            _mappedPages[pageIndex][offset] = value;
            int bankIndex = _pageBankIndex[pageIndex];
            if (bankIndex >= 0 && offset < ScreenSize)
            {
                _screenShadowBanks[bankIndex][offset] = value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteCpu(ushort address, byte value)
        {
            SpectrumBeta128Device? beta128 = _beta128;
            if (beta128 != null && address < 0x4000 && beta128.IsPaged)
            {
                ApplyMemoryContention(address);
                return;
            }

            SpectrumDivMmcDevice? divExpansion = _divExpansion;
            if (divExpansion != null && address < 0x4000 && divExpansion.TryWriteMemory(address, value))
            {
                return;
            }

            SpectrumInterface1Device? interface1 = _interface1;
            if (interface1 != null && address < 0x4000 && interface1.IsPaged)
            {
                ApplyMemoryContention(address);
                return;
            }

            Z80? cpu = _cpu;
            ulong start = cpu?.Cyc ?? 0;
            int pageIndex = address >> 14;
            if (_pageReadOnly[pageIndex])
            {
                return;
            }

            int delay = 0;
            if (cpu != null && _contention != null && _pageContended[pageIndex])
            {
                delay = _contention.GetMemoryDelay(start);
                if (delay > 0)
                {
                    cpu.AddWaitStates(delay);
                }
            }

            int offset = address & 0x3FFF;
            _mappedPages[pageIndex][offset] = value;

            int bankIndex = _pageBankIndex[pageIndex];
            if (bankIndex < 0 || offset >= ScreenSize)
            {
                return;
            }

            if (cpu == null)
            {
                _screenShadowBanks[bankIndex][offset] = value;
                return;
            }

            // The CPU has committed the RAM byte, but the ULA does not see it until the
            // correct bus/beam-relative point. Queueing this is what keeps multicolour stable.
            ulong applyAt = start + (ulong)delay + 3;
            _pendingScreenWrites.Enqueue(new PendingScreenWrite(applyAt, bankIndex, offset, value));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsContendedPage(int pageIndex)
        {
            return _pageContended[pageIndex & 0x3];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadScreen(ushort address)
        {
            if (address < 0x4000 || address >= 0x5B00)
            {
                return 0xFF;
            }

            int offset = address - 0x4000;
            int bank = GetScreenBankIndex();
            if (bank < 0 || bank >= _screenShadowBanks.Length)
            {
                return 0xFF;
            }

            return _screenShadowBanks[bank][offset];
        }
        public void WritePort7FFD(byte value)
        {
            if (!SupportsPaging())
            {
                return;
            }

            if (_pagingLocked)
            {
                return;
            }

            _port7ffd = value;
            if ((value & 0x20) != 0)
            {
                // 128K paging lock is one-way until reset.
                _pagingLocked = true;
            }

            ApplyPaging();
        }
        public void WritePort1FFD(byte value)
        {
            // +2A/+3 and Scorpion use the secondary paging port for ROM and special RAM layouts.
            if (!SupportsSecondaryPagingPort())
            {
                return;
            }

            if (_pagingLocked)
            {
                return;
            }

            _port1ffd = value;
            ApplyPaging();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ApplyMemoryContention(ushort address)
        {
            Z80? cpu = _cpu;
            if (cpu == null || _contention == null)
            {
                return 0;
            }

            if (!_pageContended[address >> 14])
            {
                return 0;
            }

            int delay = _contention.GetMemoryDelay(cpu.Cyc);
            if (delay > 0)
            {
                cpu.AddWaitStates(delay);
            }

            return delay;
        }
        public void FlushPendingScreenWrites(ulong tstates)
        {
            // Called from the central scheduler before rendering/floating-bus observations.
            while (_pendingScreenWrites.Count > 0 && _pendingScreenWrites.Peek().ApplyAt <= tstates)
            {
                PendingScreenWrite write = _pendingScreenWrites.Dequeue();
                _screenShadowBanks[write.BankIndex][write.Offset] = write.Value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryPeekPendingScreenWrite(out ulong tstate)
        {
            if (_pendingScreenWrites.Count == 0)
            {
                tstate = 0;
                return false;
            }

            tstate = _pendingScreenWrites.Peek().ApplyAt;
            return true;
        }
        private void ApplyPaging()
        {
            // Rebuild all four 16 KB page mappings from model state. Keeping this central
            // prevents 128K, +3 and clone paging rules from drifting apart.
            switch (_model)
            {
                case SpectrumModel.Spectrum16K:
                    MapRom(0, 0);
                    MapRam(1, 0, IsContendedBank(0));
                    MapOpenBus(2);
                    MapOpenBus(3);
                    _screenBank = 0;
                    break;

                case SpectrumModel.Spectrum48K:
                    MapRom(0, 0);
                    MapRam(1, 0, IsContendedBank(0));
                    MapRam(2, 1, false);
                    MapRam(3, 2, false);
                    _screenBank = 0;
                    break;

                case SpectrumModel.Spectrum128K:
                case SpectrumModel.SpectrumPlus2:
                case SpectrumModel.Pentagon128:
                    Apply128KPaging();
                    break;

                case SpectrumModel.Scorpion256:
                    ApplyScorpionPaging();
                    break;

                case SpectrumModel.SpectrumPlus2A:
                case SpectrumModel.SpectrumPlus3:
                    ApplyPlus3Paging();
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(_model), _model, "Unsupported Spectrum model.");
            }
        }
        private void Apply128KPaging()
        {
            _screenBank = ((_port7ffd & 0x08) != 0) ? 7 : 5;
            int pagedBank = _port7ffd & 0x07;
            int rom = (_port7ffd >> 4) & 0x01;

            MapRom(0, rom);
            MapRam(1, 5, IsContendedBank(5));
            MapRam(2, 2, IsContendedBank(2));
            MapRam(3, pagedBank, IsContendedBank(pagedBank));
        }
        private void ApplyPlus3Paging()
        {
            _screenBank = ((_port7ffd & 0x08) != 0) ? 7 : 5;
            _specialPaging = (_port1ffd & 0x01) != 0;

            if (_specialPaging)
            {
                // +2A/+3 special paging replaces the entire 64 KB address space with RAM banks.
                int mode = (_port1ffd >> 1) & 0x03;
                MapRam(0, Plus3SpecialPagingMap[mode, 0], IsContendedBank(Plus3SpecialPagingMap[mode, 0]));
                MapRam(1, Plus3SpecialPagingMap[mode, 1], IsContendedBank(Plus3SpecialPagingMap[mode, 1]));
                MapRam(2, Plus3SpecialPagingMap[mode, 2], IsContendedBank(Plus3SpecialPagingMap[mode, 2]));
                MapRam(3, Plus3SpecialPagingMap[mode, 3], IsContendedBank(Plus3SpecialPagingMap[mode, 3]));
                return;
            }

            int rom = ((_port1ffd >> 1) & 0x02) | ((_port7ffd >> 4) & 0x01);
            int pagedBank = _port7ffd & 0x07;

            MapRom(0, rom);
            MapRam(1, 5, IsContendedBank(5));
            MapRam(2, 2, IsContendedBank(2));
            MapRam(3, pagedBank, IsContendedBank(pagedBank));
        }
        private void ApplyScorpionPaging()
        {
            _screenBank = ((_port7ffd & 0x08) != 0) ? 7 : 5;

            // Scorpion keeps +3-style port decoding but uses its own ROM/RAM bank interpretation.
            int rom = (_port1ffd & 0x02) != 0
                ? 2
                : ((_port7ffd >> 4) & 0x01);
            _currentRomBank = rom;

            _specialPaging = (_port1ffd & 0x01) != 0;
            if (_specialPaging)
            {
                MapRam(0, 0, IsContendedBank(0));
            }
            else
            {
                MapRom(0, rom);
            }

            int pagedBank = ((_port1ffd & 0x10) >> 1) | (_port7ffd & 0x07);
            MapRam(1, 5, IsContendedBank(5));
            MapRam(2, 2, IsContendedBank(2));
            MapRam(3, pagedBank, IsContendedBank(pagedBank));
        }
        private void MapRom(int pageIndex, int romIndex)
        {
            if (romIndex < 0 || romIndex >= _roms.BankCount)
            {
                MapOpenBus(pageIndex);
                return;
            }

            if (pageIndex == 0)
            {
                _currentRomBank = romIndex;
            }

            _mappedPages[pageIndex] = _roms.GetBankBytes(romIndex);
            _pageReadOnly[pageIndex] = true;
            _pageContended[pageIndex] = false;
            _pageBankIndex[pageIndex] = -1;
        }
        private void MapRam(int pageIndex, int bankIndex, bool contended)
        {
            if (bankIndex < 0 || bankIndex >= _ramBanks.Length)
            {
                MapOpenBus(pageIndex);
                return;
            }

            _mappedPages[pageIndex] = _ramBanks[bankIndex];
            _pageReadOnly[pageIndex] = false;
            _pageContended[pageIndex] = contended;
            _pageBankIndex[pageIndex] = bankIndex;
        }
        private void MapOpenBus(int pageIndex)
        {
            _mappedPages[pageIndex] = _openBusPage;
            _pageReadOnly[pageIndex] = true;
            _pageContended[pageIndex] = false;
            _pageBankIndex[pageIndex] = -1;
        }
        private bool SupportsPaging()
        {
            return SpectrumModelTraits.SupportsPaging(_model);
        }
        private bool SupportsSecondaryPagingPort()
        {
            return SpectrumModelTraits.SupportsSecondaryPagingPort(_model);
        }
        private bool AllowsBeta128RomTrap()
        {
            // On 128K-style machines the editor ROM can legitimately fetch in the 3Dxx range;
            // TR-DOS automap should only trigger from the 48K ROM side.
            return !SpectrumModelTraits.Supports128Paging(_model) || _currentRomBank != 0;
        }
        private bool IsContendedBank(int bankIndex)
        {
            return SpectrumModelTraits.IsContendedRamBank(_model, bankIndex);
        }
        private int GetScreenBankIndex()
        {
            return _model switch
            {
                SpectrumModel.Spectrum16K => 0,
                SpectrumModel.Spectrum48K => 0,
                _ => _screenBank
            };
        }
        private static int GetRomBankCount(SpectrumModel model)
        {
            return SpectrumModelTraits.RomBankCount(model);
        }
        private static byte[][] CreateRamBanks(SpectrumModel model)
        {
            int bankCount = SpectrumModelTraits.RamBankCount(model);

            var banks = new byte[bankCount][];
            for (int i = 0; i < bankCount; i++)
            {
                banks[i] = new byte[PageSize];
            }

            return banks;
        }
        private static byte[][] CreateScreenShadowBanks(int bankCount)
        {
            var banks = new byte[bankCount][];
            for (int i = 0; i < bankCount; i++)
            {
                banks[i] = new byte[ScreenSize];
            }

            return banks;
        }
        private void ResetScreenShadow()
        {
            for (int i = 0; i < _ramBanks.Length; i++)
            {
                CopyScreenShadow(i);
            }
        }
        private void CopyScreenShadow(int bankIndex)
        {
            if (bankIndex < 0 || bankIndex >= _ramBanks.Length)
            {
                return;
            }

            Array.Copy(_ramBanks[bankIndex], 0, _screenShadowBanks[bankIndex], 0, ScreenSize);
        }
    }
}
