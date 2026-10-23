using System.Collections.Generic;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.ZxNext.Core;

namespace ZedExEss.ZxNext.DivMmc;

/// <summary>
/// ZX Spectrum Next DivMMC paging control and programmable opcode-fetch automapper.
/// </summary>
/// <remarks>
/// The Next stores the DivMMC ROM and RAM in its system SRAM. This device owns only
/// the paging and automap latches; <see cref="Memory.ZxNextMemoryBus"/> resolves the
/// resulting SRAM pages so configuration-mode access observes the same bytes.
/// </remarks>
public sealed class ZxNextDivMmcDevice
{
    public const int SdCardCount = 2;
    public const byte ResetEntryPoints0 = 0x83;
    public const byte ResetEntryPointsValid0 = 0x01;
    public const byte ResetEntryPointsTiming0 = 0x00;
    public const byte ResetEntryPoints1 = 0xCD;

    private byte _controlRegister;
    private readonly object _sdLock = new();
    private readonly SpectrumDivMmcSdCard?[] _sdCards = new SpectrumDivMmcSdCard?[SdCardCount];
    private byte _cardSelectRegister = 0xFF;
    private bool _sdCardSwap;

    public bool Enabled { get; private set; } = true;
    public bool AutomapEnabled { get; private set; }
    public bool AutomapHeld { get; private set; }
    public bool NmiActive { get; private set; }
    public bool IsMapped => Enabled && (Conmem || AutomapHeld);
    public bool Conmem => (_controlRegister & 0x80) != 0;
    public bool Mapram => (_controlRegister & 0x40) != 0;
    public int SelectedRamBank => _controlRegister & 0x0F;
    public byte ControlRegister => (byte)(_controlRegister & 0xCF);
    public byte CardSelectRegister => _cardSelectRegister;
    public bool SdCardSwap => _sdCardSwap;
    public int SelectedSdCard => GetSelectedSdCard(_cardSelectRegister);
    public byte EntryPoints0 { get; private set; } = ResetEntryPoints0;
    public byte EntryPointsValid0 { get; private set; } = ResetEntryPointsValid0;
    public byte EntryPointsTiming0 { get; private set; } = ResetEntryPointsTiming0;
    public byte EntryPoints1 { get; private set; } = ResetEntryPoints1;

    public void WriteControl(byte value)
    {
        byte mapram = (byte)(_controlRegister & 0x40);
        _controlRegister = (byte)((value & 0x8F) | mapram | (value & 0x40));
    }

    public void ResetMapram() => _controlRegister = (byte)(_controlRegister & ~0x40);

    public void SetAutomapEnabled(bool enabled)
    {
        AutomapEnabled = enabled;
        if (!enabled && !NmiActive)
        {
            AutomapHeld = false;
        }
    }

    public void SetSdCardSwap(bool enabled) => _sdCardSwap = enabled;

    public void SetEnabled(bool enabled)
    {
        Enabled = enabled;
        if (!enabled)
        {
            AutomapHeld = false;
            NmiActive = false;
        }
    }

    /// <summary>Connects media to physical SD slot 0.</summary>
    public void AttachSdCard(SpectrumDivMmcSdCard? sdCard) => AttachSdCard(0, sdCard);

    /// <summary>Connects media to one of the two physical Next SD slots.</summary>
    public void AttachSdCard(int slot, SpectrumDivMmcSdCard? sdCard)
    {
        ValidateSdCardSlot(slot);
        lock (_sdLock)
        {
            if (ReferenceEquals(_sdCards[slot], sdCard))
            {
                return;
            }

            if (SelectedSdCard == slot)
            {
                _sdCards[slot]?.Deselect();
            }

            _sdCards[slot] = sdCard;
            _sdCards[slot]?.Reset();
        }
    }

    public void WriteCardSelect(byte value)
    {
        byte normalized = (value & 0x03) switch
        {
            0x02 => _sdCardSwap ? (byte)0xFD : (byte)0xFE,
            0x01 => _sdCardSwap ? (byte)0xFE : (byte)0xFD,
            _ when value == 0xFB => 0xFB,
            _ when value == 0xF7 => 0xF7,
            _ when value == 0x7F => 0x7F,
            _ => 0xFF
        };

        lock (_sdLock)
        {
            int previousCard = GetSelectedSdCard(_cardSelectRegister);
            int nextCard = GetSelectedSdCard(normalized);
            if (previousCard >= 0 && previousCard != nextCard)
            {
                _sdCards[previousCard]?.Deselect();
            }

            _cardSelectRegister = normalized;
        }
    }

    public byte ReadSpi() => TransferSpi(0xFF);

    public void WriteSpi(byte value) => _ = TransferSpi(value);

    public void WriteEntryPoints0(byte value) => EntryPoints0 = value;

    public void WriteEntryPointsValid0(byte value) => EntryPointsValid0 = value;

    public void WriteEntryPointsTiming0(byte value) => EntryPointsTiming0 = value;

    public void WriteEntryPoints1(byte value) => EntryPoints1 = value;

    /// <summary>Maps the DivMMC NMI service window before the CPU acknowledges NMI.</summary>
    public bool BeginNmi()
    {
        if (!Enabled || NmiActive)
        {
            return false;
        }

        NmiActive = true;
        AutomapHeld = true;
        return true;
    }

    /// <summary>
    /// Releases the hardware-owned DivMMC mapping after RETN.
    /// </summary>
    /// <remarks>
    /// The FPGA clears the automap hold on every RETN seen while Multiface is
    /// inactive, not only after a DivMMC-generated NMI. This also terminates
    /// ordinary automapped service routines entered through an opcode trap.
    /// </remarks>
    public void OnRetnExecuted()
    {
        NmiActive = false;
        AutomapHeld = false;
    }

    /// <summary>Applies instant entry points before the opcode byte is read.</summary>
    public void BeforeOpcodeFetch(ushort address, bool automapActive, bool rom3Active)
    {
        if (!Enabled || !AutomapEnabled)
        {
            return;
        }

        if (TryGetResetEntryPoint(address, out int entryPoint))
        {
            byte mask = (byte)(1 << entryPoint);
            bool enabled = (EntryPoints0 & mask) != 0;
            bool valid = (EntryPointsValid0 & mask) != 0 ? automapActive : rom3Active;
            bool instant = (EntryPointsTiming0 & mask) != 0;
            if (enabled && valid && instant)
            {
                AutomapHeld = true;
            }
        }

        if ((EntryPoints1 & 0x80) != 0 && (address & 0xFF00) == 0x3D00 && rom3Active)
        {
            AutomapHeld = true;
        }
    }

    /// <summary>Applies delayed entry and exit points after the opcode byte is read.</summary>
    public void AfterOpcodeFetch(ushort address, bool automapActive, bool rom3Active)
    {
        if (NmiActive)
        {
            return;
        }

        if (!Enabled || !AutomapEnabled)
        {
            AutomapHeld = false;
            return;
        }

        bool delayedEntry = false;
        if (TryGetResetEntryPoint(address, out int entryPoint))
        {
            byte mask = (byte)(1 << entryPoint);
            bool enabled = (EntryPoints0 & mask) != 0;
            bool valid = (EntryPointsValid0 & mask) != 0 ? automapActive : rom3Active;
            bool delayed = (EntryPointsTiming0 & mask) == 0;
            delayedEntry = enabled && valid && delayed;
        }

        delayedEntry |= rom3Active && IsFixedDelayedRom3Entry(address);
        if (delayedEntry)
        {
            AutomapHeld = true;
        }

        if ((EntryPoints1 & 0x40) != 0
            && address is >= 0x1FF8 and <= 0x1FFF
            && automapActive)
        {
            AutomapHeld = false;
        }
    }

    public bool MapsRom(ushort address) => IsMapped && address < 0x2000 && !Mapram;

    public bool MapsRam(ushort address) =>
        IsMapped && address < 0x4000 && (address >= 0x2000 || Mapram);

    public int GetMappedRamBank(ushort address) => address < 0x2000 ? 3 : SelectedRamBank;

    public bool IsMappedPageReadOnly(ushort address) =>
        address < 0x2000 || (Mapram && SelectedRamBank == 3);

    public void Reset(ZxNextResetKind kind)
    {
        lock (_sdLock)
        {
            int selectedCard = GetSelectedSdCard(_cardSelectRegister);
            if (selectedCard >= 0)
            {
                _sdCards[selectedCard]?.Deselect();
            }

            _cardSelectRegister = 0xFF;
        }

        _controlRegister = 0;
        AutomapHeld = false;
        NmiActive = false;
        EntryPoints0 = ResetEntryPoints0;
        EntryPointsValid0 = ResetEntryPointsValid0;
        EntryPointsTiming0 = ResetEntryPointsTiming0;
        EntryPoints1 = ResetEntryPoints1;

        if (kind == ZxNextResetKind.Hard)
        {
            AutomapEnabled = false;
            Enabled = true;
            _sdCardSwap = false;
        }
    }

    /// <summary>
    /// Captures what is in the two SD slots, under the same lock the SPI path uses.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CaptureState"/> because the two are different kinds of thing:
    /// the latches are a handful of bytes that always exist, and the media is optional, can be
    /// large, and may not be capturable at all. Keeping them apart means a machine with no
    /// card pays nothing and a failure to capture a card does not take the machine with it.
    /// </remarks>
    public ZxNextMediaState CaptureMediaState()
    {
        lock (_sdLock)
        {
            var slots = new SpectrumSdCardState?[SdCardCount];
            for (int slot = 0; slot < SdCardCount; slot++)
            {
                slots[slot] = _sdCards[slot]?.CaptureState();
            }

            return new ZxNextMediaState(slots);
        }
    }

    /// <summary>
    /// Puts the slots back, saying what it could not do rather than doing it anyway.
    /// </summary>
    /// <remarks>
    /// A slot that was empty at capture and holds a card now is left alone: ejecting the
    /// user's card because a state file does not mention it would be the snapshot deciding
    /// what is plugged into the machine. The same the other way round - a card that was
    /// captured and is no longer mounted is reported, not remounted.
    /// </remarks>
    public ZxNextMediaRestoreReport RestoreMediaState(ZxNextMediaState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var notes = new List<string>();
        lock (_sdLock)
        {
            for (int slot = 0; slot < SdCardCount && slot < state.Slots.Length; slot++)
            {
                SpectrumSdCardState? captured = state.Slots[slot];
                SpectrumDivMmcSdCard? card = _sdCards[slot];
                if (captured == null)
                {
                    if (card != null)
                    {
                        notes.Add($"SD{slot} holds a card the state does not describe; it was left alone");
                    }

                    continue;
                }

                if (card == null)
                {
                    notes.Add($"SD{slot} was mounted when the state was saved and is empty now");
                    continue;
                }

                SpectrumSdCardRestoreResult result = card.RestoreState(captured);
                if (result.Note != null)
                {
                    notes.Add($"SD{slot}: {result.Note}");
                }
            }
        }

        return notes.Count == 0 ? ZxNextMediaRestoreReport.Silent : new ZxNextMediaRestoreReport(notes);
    }

    /// <summary>Captures the paging and automap latches, but not the cards.</summary>
    public ZxNextDivMmcState CaptureState() => new(
        _controlRegister,
        _cardSelectRegister,
        _sdCardSwap,
        Enabled,
        AutomapEnabled,
        AutomapHeld,
        NmiActive,
        EntryPoints0,
        EntryPointsValid0,
        EntryPointsTiming0,
        EntryPoints1);

    /// <summary>
    /// Puts the latches back, leaving whatever media is mounted alone.
    /// </summary>
    /// <remarks>
    /// The card selection is assigned rather than written through
    /// <see cref="WriteCardSelect"/>, because that deselects the previously selected card -
    /// a side effect a guest wants and a restore does not, since it would abort a command
    /// the captured machine was part way through issuing.
    /// </remarks>
    public void RestoreState(ZxNextDivMmcState state)
    {
        lock (_sdLock)
        {
            _cardSelectRegister = state.CardSelectRegister;
        }

        _controlRegister = state.ControlRegister;
        _sdCardSwap = state.SdCardSwap;
        Enabled = state.Enabled;
        AutomapEnabled = state.AutomapEnabled;
        AutomapHeld = state.AutomapHeld;
        NmiActive = state.NmiActive;
        EntryPoints0 = state.EntryPoints0;
        EntryPointsValid0 = state.EntryPointsValid0;
        EntryPointsTiming0 = state.EntryPointsTiming0;
        EntryPoints1 = state.EntryPoints1;
    }

    private byte TransferSpi(byte value)
    {
        lock (_sdLock)
        {
            int selectedCard = GetSelectedSdCard(_cardSelectRegister);
            SpectrumDivMmcSdCard? card = selectedCard >= 0 ? _sdCards[selectedCard] : null;
            return card?.Transfer(value) ?? 0xFF;
        }
    }

    private static int GetSelectedSdCard(byte cardSelect)
    {
        return (cardSelect & 0x03) switch
        {
            0x02 => 0,
            0x01 => 1,
            _ => -1
        };
    }

    private static void ValidateSdCardSlot(int slot)
    {
        if ((uint)slot >= SdCardCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }
    }

    private bool IsFixedDelayedRom3Entry(ushort address)
    {
        return address switch
        {
            0x04C6 => (EntryPoints1 & 0x04) != 0,
            0x0562 => (EntryPoints1 & 0x08) != 0,
            0x04D7 => (EntryPoints1 & 0x10) != 0,
            0x056A => (EntryPoints1 & 0x20) != 0,
            _ => false
        };
    }

    private static bool TryGetResetEntryPoint(ushort address, out int entryPoint)
    {
        if (address <= 0x003F && (address & 0x0007) == 0)
        {
            entryPoint = address >> 3;
            return true;
        }

        entryPoint = 0;
        return false;
    }
}
