using ZedExEss.ZxNext.Core;
namespace ZedExEss.ZxNext.Memory;

/// <summary>
/// Native +3-style legacy paging state synchronized with the eight Next MMU slots.
/// </summary>
public sealed class ZxNextPagingController
{
    private static readonly byte[][] AllRamBanks =
    [
        [0, 1, 2, 3],
        [4, 5, 6, 7],
        [5, 5, 6, 3],
        [4, 7, 6, 3]
    ];

    private readonly ZxNextMemoryBus _memory;

    public ZxNextPagingController(ZxNextMemoryBus memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public byte Port7Ffd { get; private set; }
    public byte PortDFfd { get; private set; }
    public byte Port1Ffd { get; private set; }
    public byte PortEff7 { get; private set; }

    public bool PagingLocked => (Port7Ffd & 0x20) != 0;
    public bool IsAllRamMode => (Port1Ffd & 0x01) != 0;
    public int SelectedRamBank => ((PortDFfd & 0x0F) << 3) | (Port7Ffd & 0x07);
    public int SelectedRomBank => ((Port1Ffd & 0x04) >> 1) | ((Port7Ffd & 0x10) >> 4);

    public void WritePort7Ffd(byte value)
    {
        if (PagingLocked)
        {
            return;
        }

        bool wasAllRam = IsAllRamMode;
        Port7Ffd = value;
        ApplyMapping(wasAllRam, updateTopBank: true);
    }

    public void WritePortDFfd(byte value)
    {
        if (PagingLocked)
        {
            return;
        }

        bool wasAllRam = IsAllRamMode;
        PortDFfd = (byte)(value & 0x0F);
        ApplyMapping(wasAllRam, updateTopBank: true);
    }

    public void WritePort1Ffd(byte value)
    {
        if (PagingLocked)
        {
            return;
        }

        bool wasAllRam = IsAllRamMode;
        Port1Ffd = (byte)(value & 0x07);
        ApplyMapping(wasAllRam, updateTopBank: true);
    }

    public void WritePortEff7(byte value)
    {
        bool wasAllRam = IsAllRamMode;
        PortEff7 = (byte)(value & 0x0C);
        ApplyMapping(wasAllRam, updateTopBank: true);
    }

    public void UnlockPaging() => Port7Ffd = (byte)(Port7Ffd & ~0x20);

    /// <summary>Applies the NextReg $69 shadow-display alias independently of the paging lock.</summary>
    public void SetShadowScreen(bool enabled) => Port7Ffd = (byte)((Port7Ffd & ~0x08)
        | (enabled ? 0x08 : 0));

    public byte ReadNextRegister8E()
    {
        int finalBit = IsAllRamMode ? (Port1Ffd >> 1) & 0x01 : (Port7Ffd >> 4) & 0x01;
        return (byte)(
            ((PortDFfd & 0x01) << 7)
            | ((Port7Ffd & 0x07) << 4)
            | 0x08
            | ((Port1Ffd & 0x01) << 2)
            | ((Port1Ffd & 0x04) >> 1)
            | finalBit);
    }

    public void WriteNextRegister8E(byte value)
    {
        bool wasAllRam = IsAllRamMode;
        bool updateTopBank = (value & 0x08) != 0;
        if (updateTopBank)
        {
            Port7Ffd = (byte)((Port7Ffd & ~0x07) | ((value >> 4) & 0x07));
            PortDFfd = (byte)((value >> 7) & 0x01);
        }

        Port1Ffd = (byte)(((value & 0x02) << 1) | ((value & 0x01) << 1) | ((value & 0x04) >> 2));
        if (!IsAllRamMode)
        {
            Port7Ffd = (byte)((Port7Ffd & ~0x10) | ((value & 0x01) << 4));
        }

        ApplyMapping(wasAllRam, updateTopBank);
    }

    public void Reset()
    {
        Port7Ffd = 0;
        PortDFfd = 0;
        Port1Ffd = 0;
        PortEff7 = 0;
        _memory.ResetMmu();
        _memory.SelectRomBank(0);
    }

    private void ApplyMapping(bool wasAllRam, bool updateTopBank)
    {
        if (IsAllRamMode)
        {
            byte[] banks = AllRamBanks[(Port1Ffd >> 1) & 0x03];
            for (int slotPair = 0; slotPair < banks.Length; slotPair++)
            {
                int page = banks[slotPair] * 2;
                _memory.SetMmuPage(slotPair * 2, (byte)page);
                _memory.SetMmuPage((slotPair * 2) + 1, (byte)(page + 1));
            }

            return;
        }

        _memory.SelectRomBank(SelectedRomBank);
        if ((PortEff7 & 0x08) != 0)
        {
            _memory.SetMmuPage(0, 0);
            _memory.SetMmuPage(1, 1);
        }
        else
        {
            _memory.SetMmuPage(0, ZxNextMemoryBus.RomPageSentinel);
            _memory.SetMmuPage(1, ZxNextMemoryBus.RomPageSentinel);
        }

        if (wasAllRam)
        {
            _memory.SetMmuPage(2, 10);
            _memory.SetMmuPage(3, 11);
            _memory.SetMmuPage(4, 4);
            _memory.SetMmuPage(5, 5);
        }

        if (wasAllRam || updateTopBank)
        {
            int topPage = SelectedRamBank * 2;
            _memory.SetMmuPage(6, (byte)topPage);
            _memory.SetMmuPage(7, (byte)(topPage + 1));
        }
    }

    /// <summary>Reads the legacy paging ports, for a snapshot.</summary>
    public ZxNextPagingState CaptureState() =>
        new(Port7Ffd, PortDFfd, Port1Ffd, PortEff7);

    /// <summary>
    /// Puts the paging ports back and re-applies the mapping they describe.
    /// </summary>
    /// <remarks>
    /// The ports are written through the normal paths so the memory bus ends up with the
    /// mapping they imply rather than a separately restored copy of it, which is one fewer
    /// thing that can disagree. The MMU pages are restored afterwards, because on a Next
    /// they override what the legacy ports selected.
    /// </remarks>
    public void RestoreState(ZxNextPagingState state)
    {
        WritePortEff7(state.PortEff7);
        WritePortDFfd(state.PortDFfd);
        WritePort1Ffd(state.Port1Ffd);
        WritePort7Ffd(state.Port7Ffd);
    }
}
