using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Z80CPU;
using ZedExEss.ZxNext.Core;
using ZedExEss.Spectrum.Debugging;
using ZedExEss.ZxNext.DivMmc;
using ZedExEss.ZxNext.Multiface;
using ZedExEss.ZxNext.Video;

using System.Runtime.CompilerServices;

namespace ZedExEss.ZxNext.Memory;

/// <summary>ZX Spectrum Next logical memory bus backed by eight 8 KiB MMU slots.</summary>
public sealed class ZxNextMemoryBus : IZ80MemoryBus, IZ80DebuggerMemory, IContendedPageProvider
{
    public const int MmuSlotCount = 8;
    public const byte RomPageSentinel = 0xFF;
    public const int RomBankSize = 16 * 1024;
    public const int BootRomSize = 8 * 1024;
    public const int SystemSramBytes = 256 * 1024;
    public const int AlternateRomFirstRawBank = 6;
    public const int GuestRamFirstRawBank = 16;
    public const int DivMmcRomRawPage = 8;
    public const int MultifaceRomRawPage = 10;
    public const int MultifaceRamRawPage = 11;
    public const int DivMmcRamFirstRawPage = 16;
    public const int BootRomDebuggerBank = 4;
    public const int AlternateRomDebuggerBank = 5;
    public const int DivMmcRomDebuggerBank = 7;
    public const int MultifaceRomDebuggerBank = 8;
    private static readonly byte[] ResetMmuPages =
    [
        RomPageSentinel, RomPageSentinel, 10, 11, 4, 5, 0, 1
    ];

    private readonly byte[] _mmuPages = new byte[MmuSlotCount];
    private readonly byte[] _systemSram = new byte[SystemSramBytes];
    private readonly byte[] _bootRom = new byte[BootRomSize];
    private readonly byte[][] _displayPageShadows =
    [
        new byte[ZxNextPhysicalMemory.PageSize],
        new byte[ZxNextPhysicalMemory.PageSize],
        new byte[ZxNextPhysicalMemory.PageSize]
    ];
    private readonly byte[]?[] _displayShadowByPage = new byte[]?[16];
    private readonly Queue<PendingDisplayWrite> _pendingDisplayWrites = new();
    private readonly ZxNextDivMmcDevice _divMmc;
    private readonly ZxNextMultifaceDevice _multiface;
    private readonly ZxNextLayer2Device _layer2;
    private ZxNextCpu? _cpu;
    private ZxNextContentionController? _contention;
    private ZxNextFloatingBus? _floatingBus;
    private ZxNextClockCoordinator? _clock;

    public ZxNextMemoryBus(
        ZxNextPhysicalMemory physicalMemory,
        ZxNextDivMmcDevice divMmc,
        ZxNextMultifaceDevice multiface,
        ZxNextLayer2Device layer2)
    {
        PhysicalMemory = physicalMemory ?? throw new ArgumentNullException(nameof(physicalMemory));
        _divMmc = divMmc ?? throw new ArgumentNullException(nameof(divMmc));
        _multiface = multiface ?? throw new ArgumentNullException(nameof(multiface));
        _layer2 = layer2 ?? throw new ArgumentNullException(nameof(layer2));
        _displayShadowByPage[10] = _displayPageShadows[0];
        _displayShadowByPage[11] = _displayPageShadows[1];
        _displayShadowByPage[14] = _displayPageShadows[2];
        ResetMmu();
        HardResetRomState();
    }

    public ZxNextPhysicalMemory PhysicalMemory { get; }
    public int CurrentRomBank { get; private set; }
    public bool BootRomEnabled { get; private set; }
    public bool ConfigurationMode { get; private set; }
    public byte ConfigurationBank { get; private set; }
    public byte AlternateRomRegister { get; private set; }
    public bool AlternateRomEnabled => (AlternateRomRegister & 0x80) != 0;
    public bool AlternateRomWriteOnly => (AlternateRomRegister & 0x40) != 0;

    public int EffectiveRomBank
    {
        get
        {
            int locks = (AlternateRomRegister >> 4) & 0x03;
            return locks != 0 ? locks : CurrentRomBank;
        }
    }

    public int EffectiveAlternateRomBank =>
        ((AlternateRomRegister & 0x30) != 0)
            ? (AlternateRomRegister >> 5) & 0x01
            : CurrentRomBank & 0x01;

    public byte Read(ushort address)
    {
        // Classifying the access is pure paging state, so only a read that can take
        // wait states has to publish the batched CPU time first. Publishing on every
        // read pinned the video advance to one machine cycle at a time.
        bool contended = IsCpuAccessContended(address, write: false);
        if (contended && MayIncurContentionDelay)
        {
            SynchronizeCpuTiming();
            if (ApplyContentionDelay())
            {
                SynchronizeCpuTiming();
            }
        }

        byte value = ReadMapped(address);
        if (contended)
        {
            _floatingBus?.ObserveContendedCpuData(value);
        }

        return value;
    }

    public void WriteCpu(ushort address, byte value)
    {
        bool contended = IsCpuAccessContended(address, write: true);
        byte displayPage = 0;
        int displayOffset = 0;
        bool displayWrite = _clock != null
            && TryResolveWritablePhysicalAddress(address, out displayPage, out displayOffset)
            && TryGetDisplayShadow(displayPage, out _);

        // Publish the batched CPU time before the memory changes, but only where the
        // video side could tell the difference: a write that can take wait states, a
        // display-RAM write (timestamped against the master clock below), or any write
        // at all while Layer 2 is displaying, because Layer 2 reads physical RAM
        // directly and has no write ordering of its own.
        if ((contended && MayIncurContentionDelay) || displayWrite || _layer2.DisplayEnabled)
        {
            SynchronizeCpuTiming();
            if (contended && ApplyContentionDelay())
            {
                SynchronizeCpuTiming();
            }
        }

        WriteMapped(address, value);
        if (displayWrite)
        {
            QueueDisplayWrite(displayPage, displayOffset, value);
        }

        if (contended)
        {
            _floatingBus?.ObserveContendedCpuData(value);
        }
    }

    public byte FetchOpcode(ushort address)
    {
        _multiface.BeforeOpcodeFetch(address);
        // Multiface is above DivMMC in the FPGA's low-memory decode. Its
        // active window drives sram_pre_override to zero, so opcode addresses
        // fetched from Multiface ROM/RAM must not qualify DivMMC automap entry
        // points (notably the ROM-3 $0038 trap).
        bool automapActive = address < 0x4000 && !_multiface.IsMapped;
        bool rom3Active = automapActive && IsRom3Active(address);
        _divMmc.BeforeOpcodeFetch(address, automapActive, rom3Active);
        // As in Read: the Multiface and DivMMC decodes are paging-only, so the batched
        // CPU time only has to be published for a fetch that can take wait states.
        bool contended = IsCpuAccessContended(address, write: false);
        if (contended && MayIncurContentionDelay)
        {
            SynchronizeCpuTiming();
            if (ApplyContentionDelay())
            {
                SynchronizeCpuTiming();
            }
        }

        byte value = ReadMapped(address);
        if (contended)
        {
            _floatingBus?.ObserveContendedCpuData(value);
        }

        _divMmc.AfterOpcodeFetch(address, automapActive, rom3Active);
        return value;
    }

    public byte ReadDirect(ushort address) => ReadMapped(address);

    public void WriteDirect(ushort address, byte value)
    {
        WriteMapped(address, value);
        MirrorImmediateDisplayWrite(address, value);
    }

    public bool CanWriteDirect(ushort address)
    {
        if (address < 0x4000 && _multiface.IsMapped)
        {
            return address >= 0x2000;
        }

        if (address < 0x4000 && _divMmc.IsMapped)
        {
            return !_divMmc.IsMappedPageReadOnly(address);
        }

        if (_layer2.TryResolveMappedAddress(address, write: true, out int layer2Page, out _))
        {
            return PhysicalMemory.IsPageInstalled(layer2Page);
        }

        int slot = address >> 13;
        byte mappedPage = _mmuPages[slot];
        if (PhysicalMemory.IsPageInstalled(mappedPage))
        {
            return true;
        }

        if (slot >= 2 || mappedPage != RomPageSentinel)
        {
            return false;
        }

        if (ConfigurationMode)
        {
            return IsRawSramBankInstalled(ConfigurationBank);
        }

        return AlternateRomEnabled && AlternateRomWriteOnly;
    }

    public DebuggerMemoryMapping GetMapping(ushort address)
    {
        int slot = address >> 13;
        int offset = address & (ZxNextPhysicalMemory.PageSize - 1);
        byte mappedPage = _mmuPages[slot];
        if (address < 0x4000 && BootRomEnabled)
        {
            return new DebuggerMemoryMapping(
                address,
                page: slot,
                isRam: false,
                isRom: true,
                isOpenBus: false,
                isReadOnly: !CanWriteDirect(address),
                isContended: false,
                bankIndex: -1,
                offset: address & (BootRomSize - 1),
                romBank: BootRomDebuggerBank,
                machineName: "ZX Spectrum Next boot ROM");
        }

        if (address < 0x4000 && _multiface.IsMapped)
        {
            bool multifaceIsRom = address < 0x2000;
            return new DebuggerMemoryMapping(
                address,
                page: address >> 13,
                isRam: !multifaceIsRom,
                isRom: multifaceIsRom,
                isOpenBus: false,
                isReadOnly: multifaceIsRom,
                isContended: false,
                bankIndex: multifaceIsRom ? -1 : 0,
                offset: address & (ZxNextPhysicalMemory.PageSize - 1),
                romBank: multifaceIsRom ? MultifaceRomDebuggerBank : -1,
                machineName: "ZX Spectrum Next Multiface");
        }

        if (address < 0x4000 && _divMmc.IsMapped)
        {
            bool divMmcIsRom = _divMmc.MapsRom(address);
            int ramBank = _divMmc.GetMappedRamBank(address);
            return new DebuggerMemoryMapping(
                address,
                page: address >> 13,
                isRam: !divMmcIsRom,
                isRom: divMmcIsRom,
                isOpenBus: false,
                isReadOnly: _divMmc.IsMappedPageReadOnly(address),
                isContended: false,
                bankIndex: divMmcIsRom ? -1 : DivMmcRamFirstRawPage + ramBank,
                offset: address & (ZxNextPhysicalMemory.PageSize - 1),
                romBank: divMmcIsRom ? DivMmcRomDebuggerBank : -1,
                machineName: "ZX Spectrum Next DivMMC");
        }

        if (_layer2.TryResolveMappedAddress(address, write: false, out int layer2Page, out int layer2Offset))
        {
            bool installed = PhysicalMemory.IsPageInstalled(layer2Page);
            return new DebuggerMemoryMapping(
                address,
                page: address >> 13,
                isRam: installed,
                isRom: false,
                isOpenBus: !installed,
                isReadOnly: !CanWriteDirect(address),
                isContended: installed && _contention?.IsPhysicalPageContended((byte)layer2Page) == true,
                bankIndex: installed ? layer2Page : -1,
                offset: layer2Offset,
                romBank: -1,
                machineName: "ZX Spectrum Next Layer 2 aperture");
        }

        bool isRam = PhysicalMemory.IsPageInstalled(mappedPage);
        bool isSentinel = slot < 2 && mappedPage == RomPageSentinel;
        if (isSentinel && ConfigurationMode)
        {
            bool installed = IsRawSramBankInstalled(ConfigurationBank);
            return new DebuggerMemoryMapping(
                address,
                page: slot,
                isRam: installed,
                isRom: false,
                isOpenBus: !installed,
                isReadOnly: !installed,
                isContended: false,
                bankIndex: installed ? ConfigurationBank : -1,
                offset: address & (RomBankSize - 1),
                romBank: -1,
                machineName: "ZX Spectrum Next config SRAM");
        }

        bool isRom = isSentinel;
        int debuggerRomBank = AlternateRomEnabled && !AlternateRomWriteOnly
            ? AlternateRomDebuggerBank + EffectiveAlternateRomBank
            : EffectiveRomBank;

        return new DebuggerMemoryMapping(
            address,
            page: slot,
            isRam,
            isRom,
            isOpenBus: !isRam && !isRom,
            isReadOnly: !CanWriteDirect(address),
            isContended: isRam && _contention?.IsPhysicalPageContended(mappedPage) == true,
            bankIndex: isRam ? mappedPage : -1,
            offset,
            romBank: isRom ? debuggerRomBank : -1,
            machineName: "ZX Spectrum Next");
    }

    public byte GetMmuPage(int slot)
    {
        ValidateMmuSlot(slot);
        return _mmuPages[slot];
    }

    internal void ConfigureTiming(
        ZxNextCpu cpu,
        ZxNextContentionController contention,
        ZxNextClockCoordinator clock)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _contention = contention ?? throw new ArgumentNullException(nameof(contention));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    internal void ConfigureFloatingBus(ZxNextFloatingBus floatingBus)
    {
        _floatingBus = floatingBus ?? throw new ArgumentNullException(nameof(floatingBus));
    }

    public bool IsContendedPage(int pageIndex)
    {
        if (_contention == null)
        {
            return false;
        }

        int firstSlot = (pageIndex & 0x03) * 2;
        return _contention.IsPhysicalPageContended(_mmuPages[firstSlot])
            || _contention.IsPhysicalPageContended(_mmuPages[firstSlot + 1]);
    }

    public void SetMmuPage(int slot, byte page)
    {
        ValidateMmuSlot(slot);
        _mmuPages[slot] = page;
    }

    public void ResetMmu() => ResetMmuPages.CopyTo(_mmuPages, 0);

    public byte ReadPhysicalRam(int page, int offset) => PhysicalMemory.Read(page, offset);

    public bool WritePhysicalRam(int page, int offset, byte value)
    {
        bool written = PhysicalMemory.Write(page, offset, value);
        if (written && TryGetDisplayShadow(page, out byte[] shadow))
        {
            ResolveImmediateDisplayMutation();
            shadow[offset] = value;
        }

        return written;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte ReadDisplayRam(int page, int offset, ulong masterTick)
    {
        byte[]? shadow = (uint)page < (uint)_displayShadowByPage.Length
            ? _displayShadowByPage[page]
            : null;
        if (shadow == null)
        {
            return PhysicalMemory.ReadMasked(page, offset);
        }

        if (_pendingDisplayWrites.Count != 0)
        {
            FlushDisplayWritesBefore(masterTick);
        }

        return shadow[offset];
    }

    internal void ResetDisplayWriteTiming()
    {
        _pendingDisplayWrites.Clear();
        CopyDisplayPageToShadow(10, _displayPageShadows[0]);
        CopyDisplayPageToShadow(11, _displayPageShadows[1]);
        CopyDisplayPageToShadow(14, _displayPageShadows[2]);
    }

    public byte[] CopyPhysicalRamPage(int page) => PhysicalMemory.CopyPage(page);

    public void SelectRomBank(int bank)
    {
        if ((uint)bank >= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(bank));
        }

        CurrentRomBank = bank;
    }

    public void LoadRomBank(int bank, ReadOnlySpan<byte> data)
    {
        if ((uint)bank >= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(bank));
        }

        if (data.Length != RomBankSize)
        {
            throw new ArgumentException($"A Next ROM bank must be exactly {RomBankSize} bytes.", nameof(data));
        }

        data.CopyTo(_systemSram.AsSpan(bank * RomBankSize, RomBankSize));
    }

    public void LoadAlternateRomBank(int bank, ReadOnlySpan<byte> data)
    {
        if ((uint)bank >= 2)
        {
            throw new ArgumentOutOfRangeException(nameof(bank));
        }

        if (data.Length != RomBankSize)
        {
            throw new ArgumentException($"A Next alternate ROM bank must be exactly {RomBankSize} bytes.", nameof(data));
        }

        int rawBank = AlternateRomFirstRawBank + bank;
        data.CopyTo(_systemSram.AsSpan(rawBank * RomBankSize, RomBankSize));
    }

    public void LoadBootRom(ReadOnlySpan<byte> data)
    {
        if (data.Length != BootRomSize)
        {
            throw new ArgumentException($"The Next boot ROM must be exactly {BootRomSize} bytes.", nameof(data));
        }

        data.CopyTo(_bootRom);
    }

    public void LoadDivMmcRom(ReadOnlySpan<byte> data)
    {
        if (data.Length != ZxNextPhysicalMemory.PageSize)
        {
            throw new ArgumentException(
                $"The Next DivMMC ROM must be exactly {ZxNextPhysicalMemory.PageSize} bytes.",
                nameof(data));
        }

        data.CopyTo(_systemSram.AsSpan(
            DivMmcRomRawPage * ZxNextPhysicalMemory.PageSize,
            ZxNextPhysicalMemory.PageSize));
    }

    public void LoadMultifaceRom(ReadOnlySpan<byte> data)
    {
        if (data.Length != ZxNextPhysicalMemory.PageSize)
        {
            throw new ArgumentException(
                $"A Next Multiface ROM must be exactly {ZxNextPhysicalMemory.PageSize} bytes.",
                nameof(data));
        }

        data.CopyTo(_systemSram.AsSpan(
            MultifaceRomRawPage * ZxNextPhysicalMemory.PageSize,
            ZxNextPhysicalMemory.PageSize));
    }

    public void DisableBootRom() => BootRomEnabled = false;

    public void SetConfigurationMode(bool enabled) => ConfigurationMode = enabled;

    public void SetConfigurationBank(byte bank) => ConfigurationBank = bank;

    public void SetAlternateRomRegister(byte value) => AlternateRomRegister = value;

    public void HardResetRomState()
    {
        BootRomEnabled = true;
        ConfigurationMode = true;
        ConfigurationBank = 0;
        AlternateRomRegister = 0;
    }

    public void SoftResetRomState()
    {
        int afterReset = AlternateRomRegister & 0x0F;
        AlternateRomRegister = (byte)(afterReset | (afterReset << 4));
        BootRomEnabled = ConfigurationMode;
    }

    private byte ReadMapped(ushort address)
    {
        if (address < 0x4000 && BootRomEnabled)
        {
            return _bootRom[address & (BootRomSize - 1)];
        }

        if (address < 0x4000 && _multiface.IsMapped)
        {
            int rawPage = address < 0x2000 ? MultifaceRomRawPage : MultifaceRamRawPage;
            return _systemSram[(rawPage * ZxNextPhysicalMemory.PageSize)
                + (address & (ZxNextPhysicalMemory.PageSize - 1))];
        }

        if (address < 0x4000 && _divMmc.IsMapped)
        {
            int rawPage = _divMmc.MapsRom(address)
                ? DivMmcRomRawPage
                : DivMmcRamFirstRawPage + _divMmc.GetMappedRamBank(address);
            return _systemSram[(rawPage * ZxNextPhysicalMemory.PageSize)
                + (address & (ZxNextPhysicalMemory.PageSize - 1))];
        }

        if (_layer2.TryResolveMappedAddress(address, write: false, out int layer2Page, out int layer2Offset))
        {
            return PhysicalMemory.ReadMasked(layer2Page, layer2Offset);
        }

        int slot = address >> 13;
        byte mappedPage = _mmuPages[slot];
        if (slot < 2 && mappedPage == RomPageSentinel)
        {
            int offset = address & (RomBankSize - 1);
            if (ConfigurationMode)
            {
                return ReadRawSram(ConfigurationBank, offset);
            }

            int rawBank = AlternateRomEnabled && !AlternateRomWriteOnly
                ? AlternateRomFirstRawBank + EffectiveAlternateRomBank
                : EffectiveRomBank;
            return _systemSram[(rawBank * RomBankSize) + offset];
        }

        return PhysicalMemory.ReadMasked(mappedPage, address & (ZxNextPhysicalMemory.PageSize - 1));
    }

    /// <summary>
    /// Classifies a CPU access once for both the synthetic ULA wait and the +3
    /// floating-bus latch. Each used to re-run this decode independently.
    /// </summary>
    private bool IsCpuAccessContended(ushort address, bool write)
    {
        ZxNextContentionController? contention = _contention;
        if (contention == null
            || (address < 0x4000 && (_multiface.IsMapped || _divMmc.IsMapped)))
        {
            return false;
        }

        if (!write && address < 0x4000 && BootRomEnabled)
        {
            return false;
        }

        byte physicalPage;
        if (_layer2.TryResolveMappedAddress(address, write, out int layer2Page, out _))
        {
            if (!PhysicalMemory.IsPageInstalled(layer2Page))
            {
                return false;
            }

            physicalPage = (byte)layer2Page;
        }
        else
        {
            physicalPage = _mmuPages[address >> 13];
        }

        return contention.IsPhysicalPageContended(physicalPage);
    }

    /// <summary>
    /// True when a contended access might be delayed. The synthetic legacy-ULA wait
    /// window exists only at 3.5 MHz, so above that the delay is always zero and the
    /// batched CPU time does not have to be published in order to compute it. This
    /// deliberately tests the clock alone rather than the controller's full Enabled
    /// state, because it sits on every CPU access: answering "yes" when contention
    /// happens to be switched off at 3.5 MHz simply keeps the old behaviour. The
    /// floating-bus latch is updated either way; it lives in its own field, which no
    /// video-timed edge writes, and every I/O access publishes before sampling it.
    /// </summary>
    private bool MayIncurContentionDelay =>
        _clock?.CpuSpeed == ZxNextCpuSpeed.Mhz3_5;

    /// <summary>Applies the wait states owed by an already-classified contended access.</summary>
    private bool ApplyContentionDelay()
    {
        ZxNextCpu? cpu = _cpu;
        ZxNextContentionController? contention = _contention;
        if (cpu == null || contention == null)
        {
            return false;
        }

        int delay = contention.GetMemoryDelay(cpu.Cyc);
        if (delay > 0)
        {
            cpu.AddWaitStates(delay);
            return true;
        }

        return false;
    }

    /// <summary>Publishes any batched CPU T-states before a timing-visible change.</summary>
    internal void SynchronizeCpuTiming() =>
        _cpu?.SynchronizeBatchedInstructionTstates();

    private void QueueDisplayWrite(byte page, int offset, byte value)
    {
        // Z80Core enters WriteCpu at the start of the three-T-state memory cycle.
        // T80 asserts WR on the falling edge of T2. The bank-5/7 dual-port BRAM then
        // sees that registered enable on the following CLK28 rising edge. Keep the
        // CPU-facing view current immediately, while publishing each byte to the ULA
        // view on that source-derived edge (PUSH/LD (nn),rr remain separate writes).
        ZxNextClockCoordinator clock = _clock!;
        int ticksPerTstate = clock.MasterTicksPerCpuTstate;
        ulong visibleAt = checked(clock.MasterTicks
            + (ulong)((3 * ticksPerTstate) / 2)
            + 1UL);
        _pendingDisplayWrites.Enqueue(new PendingDisplayWrite(
            visibleAt,
            page,
            (ushort)offset,
            value));
    }

    private void MirrorImmediateDisplayWrite(ushort address, byte value)
    {
        if (!TryResolveWritablePhysicalAddress(address, out byte page, out int offset)
            || !TryGetDisplayShadow(page, out byte[] shadow))
        {
            return;
        }

        ResolveImmediateDisplayMutation();
        shadow[offset] = value;
    }

    private void FlushDisplayWritesBefore(ulong masterTick)
    {
        // CPU and ULA use opposite BRAM clock edges. Treat an emulator read stamped
        // on the CPU commit edge as the old ULA-port value; the next edge sees it.
        while (_pendingDisplayWrites.TryPeek(out PendingDisplayWrite pending)
            && pending.VisibleAtMasterTick < masterTick)
        {
            _pendingDisplayWrites.Dequeue();
            if (TryGetDisplayShadow(pending.Page, out byte[] shadow))
            {
                shadow[pending.Offset] = pending.Value;
            }
        }
    }

    private void ResolveImmediateDisplayMutation()
    {
        if (_pendingDisplayWrites.Count == 0)
        {
            return;
        }

        ResetDisplayWriteTiming();
    }

    private bool TryResolveWritablePhysicalAddress(
        ushort address,
        out byte page,
        out int offset)
    {
        page = 0;
        offset = address & (ZxNextPhysicalMemory.PageSize - 1);
        if (address < 0x4000 && (_multiface.IsMapped || _divMmc.IsMapped))
        {
            return false;
        }


        if (_layer2.TryResolveMappedAddress(address, write: true, out int layer2Page, out int layer2Offset))
        {
            if (!PhysicalMemory.IsPageInstalled(layer2Page))
            {
                return false;
            }

            page = (byte)layer2Page;
            offset = layer2Offset;
            return true;
        }

        int slot = address >> 13;
        byte mappedPage = _mmuPages[slot];
        if (slot < 2 && mappedPage == RomPageSentinel)
        {
            if (!ConfigurationMode || ConfigurationBank < GuestRamFirstRawBank)
            {
                return false;
            }

            int firstPage = (ConfigurationBank - GuestRamFirstRawBank) * 2;
            int physicalPage = firstPage + ((address & (RomBankSize - 1)) >> 13);
            if (!PhysicalMemory.IsPageInstalled(physicalPage))
            {
                return false;
            }

            page = (byte)physicalPage;
            return true;
        }

        page = mappedPage;
        return PhysicalMemory.IsPageInstalled(page);
    }

    private bool TryGetDisplayShadow(int page, out byte[] shadow)
    {
        byte[]? candidate = page switch
        {
            10 => _displayPageShadows[0],
            11 => _displayPageShadows[1],
            14 => _displayPageShadows[2],
            _ => null
        };
        shadow = candidate!;
        return candidate != null;
    }

    private void CopyDisplayPageToShadow(int page, byte[] shadow)
    {
        byte[] source = PhysicalMemory.CopyPage(page);
        source.CopyTo(shadow, 0);
    }

    private void WriteMapped(ushort address, byte value)
    {
        if (address is >= 0x2000 and < 0x4000 && _multiface.IsMapped)
        {
            _systemSram[(MultifaceRamRawPage * ZxNextPhysicalMemory.PageSize)
                + (address & (ZxNextPhysicalMemory.PageSize - 1))] = value;
            return;
        }

        if (address < 0x2000 && _multiface.IsMapped)
        {
            return;
        }

        if (address < 0x4000 && _divMmc.IsMapped)
        {
            if (!_divMmc.IsMappedPageReadOnly(address))
            {
                int rawPage = DivMmcRamFirstRawPage + _divMmc.GetMappedRamBank(address);
                _systemSram[(rawPage * ZxNextPhysicalMemory.PageSize)
                    + (address & (ZxNextPhysicalMemory.PageSize - 1))] = value;
            }

            return;
        }

        if (_layer2.TryResolveMappedAddress(address, write: true, out int layer2Page, out int layer2Offset))
        {
            PhysicalMemory.WriteMasked(layer2Page, layer2Offset, value);
            return;
        }

        int slot = address >> 13;
        byte mappedPage = _mmuPages[slot];
        if (slot < 2 && mappedPage == RomPageSentinel)
        {
            int offset = address & (RomBankSize - 1);
            if (ConfigurationMode)
            {
                WriteRawSram(ConfigurationBank, offset, value);
            }
            else if (AlternateRomEnabled && AlternateRomWriteOnly)
            {
                int rawBank = AlternateRomFirstRawBank + EffectiveAlternateRomBank;
                _systemSram[(rawBank * RomBankSize) + offset] = value;
            }

            return;
        }

        PhysicalMemory.WriteMasked(mappedPage, address & (ZxNextPhysicalMemory.PageSize - 1), value);
    }

    private bool IsRawSramBankInstalled(int bank)
    {
        if ((uint)bank < GuestRamFirstRawBank)
        {
            return true;
        }

        int firstPage = (bank - GuestRamFirstRawBank) * 2;
        return PhysicalMemory.IsPageInstalled(firstPage + 1);
    }

    private byte ReadRawSram(int bank, int offset)
    {
        if ((uint)bank < GuestRamFirstRawBank)
        {
            return _systemSram[(bank * RomBankSize) + offset];
        }

        int firstPage = (bank - GuestRamFirstRawBank) * 2;
        return PhysicalMemory.ReadMasked(firstPage + (offset >> 13), offset & (ZxNextPhysicalMemory.PageSize - 1));
    }

    private void WriteRawSram(int bank, int offset, byte value)
    {
        if ((uint)bank < GuestRamFirstRawBank)
        {
            _systemSram[(bank * RomBankSize) + offset] = value;
            return;
        }

        int firstPage = (bank - GuestRamFirstRawBank) * 2;
        PhysicalMemory.WriteMasked(firstPage + (offset >> 13), offset & (ZxNextPhysicalMemory.PageSize - 1), value);
    }

    private bool IsRom3Active(ushort address)
    {
        if (address >= 0x4000)
        {
            return false;
        }

        int slot = address >> 13;
        if (_mmuPages[slot] != RomPageSentinel || ConfigurationMode)
        {
            return false;
        }

        return AlternateRomEnabled && !AlternateRomWriteOnly
            ? EffectiveAlternateRomBank == 1
            : EffectiveRomBank == 3;
    }

    private readonly record struct PendingDisplayWrite(
        ulong VisibleAtMasterTick,
        byte Page,
        ushort Offset,
        byte Value);

    private static void ValidateMmuSlot(int slot)
    {
        if ((uint)slot >= MmuSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    /// <summary>Reads everything the guest can have changed, for a snapshot.</summary>
    public ZxNextMemoryBusState CaptureState()
    {
        var shadowedPages = new List<int>();
        var shadows = new List<byte[]>();
        for (int page = 0; page < _displayShadowByPage.Length; page++)
        {
            byte[]? shadow = _displayShadowByPage[page];
            if (shadow != null)
            {
                shadowedPages.Add(page);
                shadows.Add(shadow.ToArray());
            }
        }

        var pending = new ZxNextPendingDisplayWriteState[_pendingDisplayWrites.Count];
        int index = 0;
        foreach (PendingDisplayWrite write in _pendingDisplayWrites)
        {
            pending[index++] = new ZxNextPendingDisplayWriteState(
                write.Page,
                write.Offset,
                write.Value,
                write.VisibleAtMasterTick);
        }

        return new ZxNextMemoryBusState(
            _mmuPages.ToArray(),
            _systemSram.ToArray(),
            CurrentRomBank,
            BootRomEnabled,
            ConfigurationMode,
            ConfigurationBank,
            AlternateRomRegister,
            [.. shadowedPages],
            [.. shadows],
            pending);
    }

    /// <summary>Puts the mapping, system SRAM and in-flight display writes back.</summary>
    public void RestoreState(ZxNextMemoryBusState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.SystemSram.CopyTo(_systemSram, 0);
        state.MmuPages.CopyTo(_mmuPages, 0);
        CurrentRomBank = state.CurrentRomBank;
        BootRomEnabled = state.BootRomEnabled;
        ConfigurationMode = state.ConfigurationMode;
        ConfigurationBank = state.ConfigurationBank;
        AlternateRomRegister = state.AlternateRomRegister;

        Array.Clear(_displayShadowByPage);
        for (int slot = 0; slot < _displayPageShadows.Length; slot++)
        {
            if (slot >= state.ShadowedPages.Length)
            {
                break;
            }

            state.DisplayShadows[slot].CopyTo(_displayPageShadows[slot], 0);
            _displayShadowByPage[state.ShadowedPages[slot]] = _displayPageShadows[slot];
        }

        _pendingDisplayWrites.Clear();
        foreach (ZxNextPendingDisplayWriteState write in state.PendingDisplayWrites)
        {
            _pendingDisplayWrites.Enqueue(new PendingDisplayWrite(
                write.MasterTick,
                write.Page,
                checked((ushort)write.Offset),
                write.Value));
        }
    }
}
