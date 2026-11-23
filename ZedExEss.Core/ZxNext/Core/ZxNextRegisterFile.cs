using ZedExEss.ZxNext.Audio;
using ZedExEss.ZxNext.DivMmc;
using ZedExEss.ZxNext.Input;
using ZedExEss.ZxNext.Memory;
using ZedExEss.ZxNext.Multiface;
using ZedExEss.ZxNext.Video;

namespace ZedExEss.ZxNext.Core;

/// <summary>Implemented NextReg groups and their subsystem side effects.</summary>
public sealed class ZxNextRegisterFile
{
    private readonly ZxNextClockCoordinator _clock;
    private readonly ZxNextMemoryBus _memory;
    private readonly ZxNextPagingController _paging;
    private readonly ZxNextDivMmcDevice _divMmc;
    private readonly ZxNextMultifaceDevice _multiface;
    private readonly ZxNextJoystickDevice _joystick;
    private readonly ZxNextAudioDevice _audio;
    private readonly ZxNextPaletteDevice _palettes;
    private readonly ZxNextLayer2Device _layer2;
    private readonly ZxNextTilemapDevice _tilemap;
    private readonly ZxNextSpriteDevice _sprites;
    private ZxNextCpu? _cpu;
    private ZxNextUlaTiming? _ulaTiming;
    private byte _peripheral3Settings = 0x10;
    private byte _peripheral1Settings = 0x41;
    private byte _peripheral2Settings = 0xA0;
    private byte _peripheral4Settings;
    private byte _peripheral5Settings = 0x01;
    private byte _userRegister = 0xFF;
    private byte _joystickIoMode = 0x01;
    private byte _i2sControl;
    private byte _machineTiming = 0x03;
    private byte _machineType = 0x03;
    private bool _expansionInterruptEnabled = true;
    private bool _lineInterruptEnabled;
    private bool _lineInterruptMsb;
    private byte _lineInterruptLsb;
    private int _lineInterruptGeneration;
    private byte _interruptControl;
    private uint _internalPortDecoding = PortDecodingPowerOn;
    private uint _busPortDecoding = PortDecodingPowerOn;
    private ushort _nmiReturnAddress;
    private bool _stacklessNmiReturnPending;
    private bool _divMmcNmiCause;
    private bool _multifaceNmiCause;
    private byte _resetType = PowerOnResetType;
    private bool _contentionDisableBefore;
    private bool _scandoublerEffective = true;
    private ulong _contentionDisableAppliesAt;
    private bool _displayTimingLocked;
    private bool _peripheralReset;
    private readonly byte[] _ulaClip = [0x00, 0xFF, 0x00, 0xBF];
    private byte _ulaClipIndex;
    private byte _timexPort;
    private byte _ulaScrollX;
    private byte _ulaScrollY;
    private byte _loresScrollX;
    private byte _loresScrollY;
    private byte _loresControl;
    private ZxNextResetKind _lastResetKind = ZxNextResetKind.Hard;
    private ZxNextResetKind? _pendingReset;

    /// <summary>
    /// NextRegs $82-$85 and $86-$89 as zxnext.vhd stores them: every enable set, and the
    /// reset-type bit (bit 31, NextReg $85/$89 bit 7) set; bits 30:28 do not exist.
    /// </summary>
    private const uint PortDecodingPowerOn = 0x8FFF_FFFF;
    private const uint PortDecodingEnables = 0x0FFF_FFFF;
    private const uint PortDecodingResetType = 0x8000_0000;

    /// <summary>
    /// <c>nr_02_reset_type</c> at power on, which is also what a hard reset gives: NextReg $02
    /// bit 1 reloads the FPGA.
    /// </summary>
    private const byte PowerOnResetType = 0b100;

    public ZxNextRegisterFile(
        ZxNextClockCoordinator clock,
        ZxNextMemoryBus memory,
        ZxNextPagingController paging,
        ZxNextDivMmcDevice divMmc,
        ZxNextMultifaceDevice multiface,
        ZxNextJoystickDevice joystick,
        ZxNextAudioDevice audio,
        ZxNextPaletteDevice palettes,
        ZxNextLayer2Device layer2,
        ZxNextTilemapDevice tilemap,
        ZxNextSpriteDevice sprites)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _paging = paging ?? throw new ArgumentNullException(nameof(paging));
        _divMmc = divMmc ?? throw new ArgumentNullException(nameof(divMmc));
        _multiface = multiface ?? throw new ArgumentNullException(nameof(multiface));
        _joystick = joystick ?? throw new ArgumentNullException(nameof(joystick));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _palettes = palettes ?? throw new ArgumentNullException(nameof(palettes));
        _layer2 = layer2 ?? throw new ArgumentNullException(nameof(layer2));
        _tilemap = tilemap ?? throw new ArgumentNullException(nameof(tilemap));
        _sprites = sprites ?? throw new ArgumentNullException(nameof(sprites));
        Nmi = new ZxNextNmiController(divMmc, multiface);
        Interrupts = new ZxNextInterruptController(this);
        Copper = new ZxNextCopperDevice();
        Ctc = new ZxNextCtcDevice();
    }

    /// <summary>The Copper, which NextRegs $60-$63 program and which writes NextRegs itself.</summary>
    public ZxNextCopperDevice Copper { get; }

    /// <summary>The Z80 CTC on ports $183B-$1B3B; NextReg $C5 holds its interrupt enables.</summary>
    public ZxNextCtcDevice Ctc { get; }

    /// <summary>A CTC port read, as of now.</summary>
    internal byte ReadCtcPort(ushort port) => Ctc.Read(port, _clock.MasterTicks);

    /// <summary>A CTC port write, seen on the next 28 MHz edge.</summary>
    /// <remarks>
    /// Batched CPU time has to reach the channel's next zero count: a pulse-mode interrupt
    /// raised inside a batch would start and end unseen.
    /// </remarks>
    internal void WriteCtcPort(ushort port, byte value)
    {
        Ctc.Write(port, value, _clock.MasterTicks);
        _cpu?.ResynchronizeBatchDeadline();
    }

    /// <summary>NextReg $C5: brings the CTC to now first, so a zero count already due keeps the old enable.</summary>
    internal void WriteCtcInterruptEnables(byte value)
    {
        Ctc.Advance(_clock.MasterTicks);
        Ctc.WriteInterruptEnables(value);
        _cpu?.ResynchronizeBatchDeadline();
    }

    /// <summary>The NMI arbitration NextReg $02 requests go through.</summary>
    public ZxNextNmiController Nmi { get; }

    /// <summary>The maskable interrupt chain: the pulse, hardware IM2 and its NextRegs.</summary>
    public ZxNextInterruptController Interrupts { get; }

    /// <summary>NextReg $C0 bit 0: hardware IM2 mode.</summary>
    internal bool HardwareInterruptMode => (_interruptControl & 0x01) != 0;

    /// <summary>NextReg $C0 bits 7-5, the hardware IM2 vector's top three bits.</summary>
    internal byte InterruptVectorBase => (byte)(_interruptControl & 0xE0);

    public byte? LastWrittenRegister { get; private set; }
    public byte LastWrittenValue { get; private set; }
    public ZxNextResetKind LastResetKind => _lastResetKind;
    public bool PeripheralResetAsserted => _peripheralReset;
    public bool HasPendingReset => _pendingReset.HasValue;
    public byte MachineTiming => _machineTiming;
    public bool SixtyHertzMode => (_peripheral1Settings & 0x04) != 0 && (_machineTiming & 0x04) == 0;
    /// <summary>
    /// NextReg $08 bit 6 as it is in effect (<c>eff_nr_08_contention_disable</c>), which is also
    /// what $08 reads back.
    /// </summary>
    /// <remarks>
    /// zxnext.vhd copies the written bit across only while the ULA's <c>hc</c> is 256 or more,
    /// past the line's contention window, so a write inside the window takes effect when it
    /// ends (<see cref="ZxNextUlaTiming.NextContentionWindowEndTick"/>).
    /// </remarks>
    public bool ContentionDisabled => _clock.MasterTicks >= _contentionDisableAppliesAt
        ? (_peripheral3Settings & 0x40) != 0
        : _contentionDisableBefore;
    /// <remarks>
    /// The ULA frame interrupt's disable is one flip-flop, <c>port_ff_reg(6)</c> in
    /// zxnext.vhd: port $FF writes it with bit 6, NextReg $22 with bit 2, and NextReg $C4 with
    /// the inverse of bit 0, whichever came last; all three read it back.
    /// </remarks>
    public bool UlaInterruptEnabled => (_timexPort & 0x40) == 0;

    /// <summary>NextReg $08 bit 0: the issue 2 keyboard, which makes $FE bit 6 follow MIC.</summary>
    public bool KeyboardIssue2 => (_peripheral3Settings & 0x01) != 0;
    public bool TimexPortReadEnabled => (_peripheral3Settings & 0x04) != 0;
    public byte TimexPort => _timexPort;
    public byte TimexVideoMode => (byte)(_timexPort & 0x07);
    public byte UlaScrollX => _ulaScrollX;
    public byte UlaScrollY => _ulaScrollY;
    public byte LoresScrollX => _loresScrollX;
    public byte LoresScrollY => _loresScrollY;
    public byte LoresControl => _loresControl;
    public bool LoresEnabled => (_layer2.SpriteAndLayersControl & 0x80) != 0;
    public bool LoresRadastanMode => (_loresControl & 0x20) != 0;
    public bool LoresDisplayFileXor => (_loresControl & 0x10) != 0;
    public byte LoresPaletteOffset => (byte)(_loresControl & 0x0F);
    public byte UlaClipX1 => _ulaClip[0];
    public byte UlaClipX2 => _ulaClip[1];
    public byte UlaClipY1 => _ulaClip[2];
    public byte UlaClipY2 => _ulaClip[3];
    public byte UlaClipIndex => _ulaClipIndex;
    public bool LineInterruptEnabled => _lineInterruptEnabled;
    public ushort LineInterruptValue => (ushort)((_lineInterruptMsb ? 0x100 : 0) | _lineInterruptLsb);
    internal int LineInterruptGeneration => _lineInterruptGeneration;
    public byte MultifaceType => (byte)(_peripheral5Settings >> 6);

    /// <summary>NextReg $0A, whose bit 3 swaps the mouse buttons and bits 1:0 set its DPI.</summary>
    public byte Peripheral5Settings => _peripheral5Settings;
    public bool IsInternalPortEnabled(int bit)
    {
        if ((uint)bit >= 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bit));
        }

        return (_internalPortDecoding & (1u << bit)) != 0;
    }

    public byte Read(byte register)
    {
        return register switch
        {
            0x00 => ZxNextReferenceBaseline.MachineId,
            0x01 => ZxNextReferenceBaseline.CoreVersionMajorMinor,
            0x02 => (byte)((_peripheralReset ? 0x80 : 0)
                | (_multifaceNmiCause ? 0x08 : 0)
                | (_divMmcNmiCause ? 0x04 : 0)
                | (_resetType & 0x03)),
            0x03 => (byte)((_palettes.NineBitSecondWritePending ? 0x80 : 0)
                | (_machineTiming << 4)
                | (_displayTimingLocked ? 0x08 : 0)
                | _machineType),

            // The 50/60 Hz and scandoubler bits read back as in effect: zxnext.vhd copies them at
            // the frame sync (eff_nr_05_5060, eff_nr_05_scandouble_en).
            0x05 => (byte)((_peripheral1Settings & 0xFA)
                | ((_ulaTiming?.Profile.SixtyHertz ?? (_peripheral1Settings & 0x04) != 0) ? 0x04 : 0)
                | (_scandoublerEffective ? 0x01 : 0)),
            0x06 => _peripheral2Settings,
            // cpu_speed (in effect) in bits 5:4, nr_07_cpu_speed (written) in bits 1:0.
            0x07 => (byte)(((byte)_clock.CpuSpeed << 4) | (byte)_clock.RequestedCpuSpeed),
            0x08 => (byte)((_paging.PagingLocked ? 0 : 0x80)
                | (_peripheral3Settings & 0x3F)
                | (ContentionDisabled ? 0x40 : 0)),
            0x09 => (byte)(_peripheral4Settings & ~0x08),
            0x0A => _peripheral5Settings,
            0x0B => _joystickIoMode,
            0x0E => ZxNextReferenceBaseline.CoreVersionSubMinor,
            0x0F => ZxNextReferenceBaseline.BoardIssue,

            // The core id, then the DRIVE and M1 buttons, which the host does not have.
            0x10 => (byte)(ZxNextReferenceBaseline.CoreId << 2),
            0x61 or 0x62 => Copper.ReadRegister(register),
            0x64 => _ulaTiming?.CopperLineOffset ?? 0,
            0x1E => (byte)((_ulaTiming?.CurrentCopperLine ?? 0) >> 8),
            0x1F => (byte)(_ulaTiming?.CurrentCopperLine ?? 0),
            0x1A => _ulaClip[_ulaClipIndex],
            0x1C => (byte)(_layer2.Read(0x1C)
                | _sprites.Read(0x1C)
                | (_ulaClipIndex << 4)
                | _tilemap.Read(0x1C)),
            0x22 => (byte)((_ulaTiming?.InterruptLineActive == true ? 0x80 : 0)
                | ((_timexPort & 0x40) != 0 ? 0x04 : 0)
                | (_lineInterruptEnabled ? 0x02 : 0)
                | (_lineInterruptMsb ? 0x01 : 0)),
            0x23 => _lineInterruptLsb,
            0x28 => _palettes.StoredNineBitValue,

            // The Pi's I2S input, which is not emulated: the 10-bit midpoint, $200, whose two
            // low bits $2D would latch are zero.
            0x2C or 0x2E => 0x80,
            0x2D => 0x00,
            0x26 => _ulaScrollX,
            0x27 => _ulaScrollY,
            0x32 => _loresScrollX,
            0x33 => _loresScrollY,
            0x69 => (byte)((_layer2.DisplayEnabled ? 0x80 : 0)
                | ((_paging.Port7Ffd & 0x08) != 0 ? 0x40 : 0)
                | (_timexPort & 0x3F)),
            0x6A => _loresControl,
            0x7F => _userRegister,
            _ when _sprites.HandlesRegister(register) => _sprites.Read(register),
            _ when _layer2.HandlesRegister(register) => _layer2.Read(register),
            _ when _tilemap.HandlesRegister(register) => _tilemap.Read(register),
            _ when _palettes.HandlesRegister(register) => _palettes.Read(register),
            >= 0x50 and <= 0x57 => _memory.GetMmuPage(register - 0x50),
            0x8C => _memory.AlternateRomRegister,
            0x8E => _paging.ReadNextRegister8E(),
            >= 0x82 and <= 0x85 => (byte)(_internalPortDecoding >> ((register - 0x82) * 8)),
            >= 0x86 and <= 0x89 => (byte)(_busPortDecoding >> ((register - 0x86) * 8)),
            0xA2 => (byte)((_i2sControl & 0xDD) | 0x02),
            0xB8 => _divMmc.EntryPoints0,
            0xB9 => _divMmc.EntryPointsValid0,
            0xBA => _divMmc.EntryPointsTiming0,
            0xBB => _divMmc.EntryPoints1,
            0xC0 => (byte)(_interruptControl
                | (((_cpu?.InterruptModeValue ?? 0) & 0x03) << 1)),
            0xC2 => (byte)_nmiReturnAddress,
            0xC3 => (byte)(_nmiReturnAddress >> 8),
            0xC4 => (byte)((_expansionInterruptEnabled ? 0x80 : 0)
                | (_lineInterruptEnabled ? 0x02 : 0)
                | ((_timexPort & 0x40) == 0 ? 0x01 : 0)),
            0x20 or 0xC5 or 0xC6 or 0xC8 or 0xC9 or 0xCA or 0xCC or 0xCD or 0xCE => Interrupts.Read(register),
            _ => 0x00
        };
    }

    /// <summary>
    /// Notified after every NextReg write, so the debugger can break on one. Null while
    /// no debugger is attached, which is the only cost this adds to a NextReg write.
    /// </summary>
    public Action<byte, byte>? RegisterWritten { get; set; }

    public void Write(byte register, byte value)
    {
        LastWrittenRegister = register;
        LastWrittenValue = value;
        RegisterWritten?.Invoke(register, value);
        WriteRegister(register, value);
    }

    /// <summary>
    /// A write of the Copper's, at the tick it lands. zxnext.vhd sends it down the same
    /// <c>nr_wr_en</c> path as the CPU's, so it does everything a CPU write does (a $02 write
    /// raises NMIs and resets), but it is not the CPU's: the debugger's NextReg watch and
    /// <see cref="LastWrittenRegister"/> do not see it.
    /// </summary>
    internal void WriteFromCopper(byte register, byte value, ulong tick)
    {
        WriteRegister(register, value);
        CopperWritten?.Invoke(register, value, tick);
    }

    /// <summary>
    /// True for a register nothing advancing with the timeline reads: NextReg $7F and the
    /// Copper's own $60-$63. A Copper write to one need not wait for the devices to reach it.
    /// </summary>
    internal static bool CopperWriteLandsWithoutDevices(byte register) =>
        register is 0x7F or (>= 0x60 and <= 0x63);

    /// <summary>Notified after each of the Copper's writes, with the tick it landed at. Diagnostics only.</summary>
    internal Action<byte, byte, ulong>? CopperWritten { get; set; }

    private void WriteRegister(byte register, byte value)
    {
        if (register == 0x02)
        {
            _peripheralReset = (value & 0x80) != 0;
            WriteNmiRequest(value);
            _pendingReset = (value & 0x02) != 0
                ? ZxNextResetKind.Hard
                : (value & 0x01) != 0
                    ? ZxNextResetKind.Soft
                    : null;
        }
        else if (register == 0x03)
        {
            WriteMachineType(value);
        }
        else if (register == 0x04)
        {
            _memory.SetConfigurationBank(value);
        }
        else if (register == 0x05)
        {
            _peripheral1Settings = (_machineTiming & 0x04) != 0
                ? (byte)(value & ~0x04)
                : value;
            _joystick.ConfigureModes(_peripheral1Settings);
        }
        else if (register == 0x07)
        {
            // Takes effect at the CPU's next idle clock edge (ZxNextMachine).
            _clock.RequestCpuSpeed((ZxNextCpuSpeed)(value & 0x03));
        }
        else if (register == 0x06)
        {
            byte ps2Mode = _memory.ConfigurationMode
                ? (byte)(value & 0x04)
                : (byte)(_peripheral2Settings & 0x04);
            _peripheral2Settings = (byte)((value & ~0x04) | ps2Mode);
            ConfigureAudio();
        }
        else if (register == 0x08)
        {
            _contentionDisableBefore = ContentionDisabled;
            _contentionDisableAppliesAt = _ulaTiming?.NextContentionWindowEndTick(_clock.MasterTicks) ?? 0;
            _peripheral3Settings = (byte)(value & 0x7F);
            if ((value & 0x80) != 0)
            {
                _paging.UnlockPaging();
            }
            ConfigureAudio();
        }
        else if (register == 0x09)
        {
            _peripheral4Settings = (byte)(value & ~0x08);
            _sprites.SetIdLockstep((_peripheral4Settings & 0x10) != 0);
            if ((value & 0x08) != 0)
            {
                _divMmc.ResetMapram();
            }
            ConfigureAudio();
        }
        else if (register == 0x0B)
        {
            _joystickIoMode = (byte)(value & 0xB1);
        }
        else if (register == 0x0A)
        {
            byte configurationFields = _memory.ConfigurationMode
                ? (byte)(value & 0xE0)
                : (byte)(_peripheral5Settings & 0xE0);
            _peripheral5Settings = (byte)(configurationFields | (value & 0x1B));
            _divMmc.SetSdCardSwap((_peripheral5Settings & 0x20) != 0);
            _divMmc.SetAutomapEnabled((_peripheral5Settings & 0x10) != 0);
        }
        else if (_palettes.HandlesRegister(register))
        {
            _palettes.Write(register, value);
        }
        else if (register == 0x1A)
        {
            _ulaClip[_ulaClipIndex] = value;
            _ulaClipIndex = (byte)((_ulaClipIndex + 1) & 0x03);
        }
        else if (register == 0x1C)
        {
            _layer2.Write(register, value);
            _tilemap.Write(register, value);
            _sprites.Write(register, value);
            if ((value & 0x04) != 0)
            {
                _ulaClipIndex = 0;
            }
        }
        else if (register == 0x22)
        {
            bool previousEnabled = _lineInterruptEnabled;
            bool previousMsb = _lineInterruptMsb;
            SetUlaInterruptDisable((value & 0x04) != 0);
            _lineInterruptEnabled = (value & 0x02) != 0;
            _lineInterruptMsb = (value & 0x01) != 0;
            AdvanceLineInterruptGenerationIfChanged(
                previousEnabled,
                previousMsb,
                _lineInterruptLsb);
        }
        else if (register == 0x23)
        {
            byte previousLsb = _lineInterruptLsb;
            _lineInterruptLsb = value;
            AdvanceLineInterruptGenerationIfChanged(
                _lineInterruptEnabled,
                _lineInterruptMsb,
                previousLsb);
        }
        else if (register is >= 0x2C and <= 0x2E)
        {
            // The DAC mirrors write; their reads are the Pi's I2S input.
            _audio.WriteDacMirror(register, value);
        }
        else if (register == 0x26)
        {
            _ulaScrollX = value;
        }
        else if (register == 0x27)
        {
            _ulaScrollY = value;
        }
        else if (register == 0x32)
        {
            _loresScrollX = value;
        }
        else if (register == 0x33)
        {
            _loresScrollY = value;
        }
        else if (_sprites.HandlesRegister(register))
        {
            _sprites.Write(register, value);
        }
        else if (_layer2.HandlesRegister(register))
        {
            _layer2.Write(register, value);
        }
        else if (_tilemap.HandlesRegister(register))
        {
            _tilemap.Write(register, value);
        }
        else if (register == 0x69)
        {
            _layer2.SetDisplayEnabled((value & 0x80) != 0);
            _paging.SetShadowScreen((value & 0x40) != 0);
            _timexPort = (byte)((_timexPort & 0xC0) | (value & 0x3F));
        }
        else if (register == 0x6A)
        {
            _loresControl = (byte)(value & 0x3F);
        }
        else if (register == 0x7F)
        {
            _userRegister = value;
        }
        else if (register is >= 0x60 and <= 0x63)
        {
            // A stopped Copper's clock is brought here first, so a start begins after it.
            Copper.Synchronize(_clock.MasterTicks);
            Copper.WriteRegister(register, value);

            // Batched CPU time has to reach the Copper's first write.
            _cpu?.ResynchronizeBatchDeadline();
        }
        else if (register == 0x64)
        {
            _ulaTiming?.WriteCopperLineOffset(value);
        }
        else if (register is >= 0x50 and <= 0x57)
        {
            _memory.SetMmuPage(register - 0x50, value);
        }
        else if (register == 0x8C)
        {
            _memory.SetAlternateRomRegister(value);
        }
        else if (register is >= 0x82 and <= 0x85)
        {
            _internalPortDecoding = WritePortDecodingByte(_internalPortDecoding, register - 0x82, value);
            ApplyInternalPortEnables();
        }
        else if (register is >= 0x86 and <= 0x89)
        {
            _busPortDecoding = WritePortDecodingByte(_busPortDecoding, register - 0x86, value);
        }
        else if (register == 0x8E)
        {
            _paging.WriteNextRegister8E(value);
        }
        else if (register == 0xB8)
        {
            _divMmc.WriteEntryPoints0(value);
        }
        else if (register == 0xB9)
        {
            _divMmc.WriteEntryPointsValid0(value);
        }
        else if (register == 0xBA)
        {
            _divMmc.WriteEntryPointsTiming0(value);
        }
        else if (register == 0xBB)
        {
            _divMmc.WriteEntryPoints1(value);
        }
        else if (register == 0xA2)
        {
            _i2sControl = value;
        }
        else if (register == 0xC0)
        {
            _interruptControl = (byte)(value & 0xE9);

            // z80_stackless_retn_en is held clear while the stackless response is off, so
            // clearing bit 3 inside an NMI routine sends its RETN back to the stack.
            if ((value & 0x08) == 0)
            {
                _stacklessNmiReturnPending = false;
            }

            Interrupts.OnModeWritten();
        }
        else if (register is 0x20 or 0xC5 or 0xC6 or 0xC8 or 0xC9 or 0xCA or 0xCC or 0xCD or 0xCE)
        {
            Interrupts.Write(register, value);
        }
        else if (register == 0xC2)
        {
            _nmiReturnAddress = (ushort)((_nmiReturnAddress & 0xFF00) | value);
        }
        else if (register == 0xC3)
        {
            _nmiReturnAddress = (ushort)((_nmiReturnAddress & 0x00FF) | (value << 8));
        }
        else if (register == 0xC4)
        {
            bool previousEnabled = _lineInterruptEnabled;
            _expansionInterruptEnabled = (value & 0x80) != 0;
            _lineInterruptEnabled = (value & 0x02) != 0;
            SetUlaInterruptDisable((value & 0x01) == 0);
            AdvanceLineInterruptGenerationIfChanged(
                previousEnabled,
                _lineInterruptMsb,
                _lineInterruptLsb);
        }
    }

    public void Reset() => Reset(ZxNextResetKind.Hard);

    public void Reset(ZxNextResetKind kind)
    {
        bool previousLineInterruptEnabled = _lineInterruptEnabled;
        bool previousLineInterruptMsb = _lineInterruptMsb;
        byte previousLineInterruptLsb = _lineInterruptLsb;
        LastWrittenRegister = null;
        LastWrittenValue = 0;
        _clock.ResetSpeed();
        _paging.Reset();
        _divMmc.Reset(kind);
        Nmi.Reset();
        _pendingReset = null;
        _lastResetKind = kind;

        // nr_02_reset_type: a soft reset shifts it, reading "hard" once after power on.
        _resetType = kind == ZxNextResetKind.Hard
            ? PowerOnResetType
            : (byte)((_resetType >> 1) | ((_resetType & 0x03) != 0 ? 1 : 0));
        _expansionInterruptEnabled = true;
        _lineInterruptEnabled = false;
        _lineInterruptMsb = false;
        _lineInterruptLsb = 0;
        AdvanceLineInterruptGenerationIfChanged(
            previousLineInterruptEnabled,
            previousLineInterruptMsb,
            previousLineInterruptLsb);
        _interruptControl = 0;
        _nmiReturnAddress = 0;
        _stacklessNmiReturnPending = false;
        _divMmcNmiCause = false;
        _multifaceNmiCause = false;
        _joystickIoMode = 0x01;
        _i2sControl = 0;
        _contentionDisableBefore = false;
        _contentionDisableAppliesAt = 0;
        _ulaClip[0] = 0x00;
        _ulaClip[1] = 0xFF;
        _ulaClip[2] = 0x00;
        _ulaClip[3] = 0xBF;
        _ulaClipIndex = 0;
        _timexPort = 0;
        _ulaScrollX = 0;
        _ulaScrollY = 0;
        _loresScrollX = 0;
        _loresScrollY = 0;
        _loresControl = 0;

        if (kind == ZxNextResetKind.Hard)
        {
            // A hard reset reloads the FPGA, so these come back at their power-on values.
            _internalPortDecoding = PortDecodingPowerOn;
            _busPortDecoding = PortDecodingPowerOn;
            _userRegister = 0xFF;
            _peripheral3Settings = 0x10;
            _peripheral1Settings = 0x41;
            _peripheral2Settings = 0xA0;
            _peripheral4Settings = 0;
            _peripheral5Settings = 0x01;
            _machineTiming = 0x03;
            _machineType = 0x03;
            _memory.SetMachineType(_machineType);
            _displayTimingLocked = false;
            _peripheralReset = false;
            _memory.HardResetRomState();
        }
        else
        {
            // The reset-type bits survive a soft reset and decide whether the enables do:
            // $85 bit 7 set resets the internal ones, $89 bit 7 clear the expansion bus ones.
            // NextReg $7F is not reset at all.
            if ((_internalPortDecoding & PortDecodingResetType) != 0)
            {
                _internalPortDecoding |= PortDecodingEnables;
            }

            if ((_busPortDecoding & PortDecodingResetType) == 0)
            {
                _busPortDecoding |= PortDecodingEnables;
            }

            _peripheral2Settings |= 0xA0;
            _peripheral3Settings = (byte)(_peripheral3Settings & ~0x40);
            _peripheral4Settings = (byte)(_peripheral4Settings & ~0x10);
            _memory.SoftResetRomState();
        }

        _scandoublerEffective = (_peripheral1Settings & 0x01) != 0;
        _joystick.ConfigureModes(_peripheral1Settings);
        ApplyInternalPortEnables();
        _audio.Reset(powerOn: kind == ZxNextResetKind.Hard);
        Interrupts.Reset();
        ConfigureAudio();
        _palettes.ResetRegisters();
        _layer2.Reset();
        _tilemap.Reset();
        _sprites.Reset();

        // A hard reset loads the FPGA again, which clears the list RAM; a soft one keeps it.
        if (kind == ZxNextResetKind.Hard)
        {
            Copper.PowerOn();
        }

        Copper.Reset(_clock.MasterTicks);

        // zxnext.vhd resets the CTC on every reset, soft or hard.
        Ctc.Reset(_clock.MasterTicks);
    }

    internal void ConfigureHostJoystickMode(ZxNextJoystickMode mode)
    {
        byte encoded = (byte)mode;
        _peripheral1Settings = (byte)((_peripheral1Settings & ~0xC8)
            | ((encoded & 0x03) << 6)
            | ((encoded & 0x04) << 1));
        _joystick.ConfigureModes(_peripheral1Settings);
    }

    public byte ReadTimexPort() => _timexPort;

    public void WriteTimexPort(byte value) => _timexPort = value;

    private void SetUlaInterruptDisable(bool disabled) =>
        _timexPort = (byte)((_timexPort & ~0x40) | (disabled ? 0x40 : 0));

    private void ConfigureAudio() =>
        _audio.Configure(_peripheral2Settings, _peripheral3Settings, _peripheral4Settings);

    private void AdvanceLineInterruptGenerationIfChanged(
        bool previousEnabled,
        bool previousMsb,
        byte previousLsb)
    {
        if (previousEnabled == _lineInterruptEnabled
            && previousMsb == _lineInterruptMsb
            && previousLsb == _lineInterruptLsb)
        {
            return;
        }

        unchecked
        {
            _lineInterruptGeneration++;
        }
    }

    private void ApplyInternalPortEnables()
    {
        _divMmc.SetEnabled(IsInternalPortEnabled(8));
        _multiface.SetEnabled(IsInternalPortEnabled(9));
    }

    /// <summary>
    /// The video frame sync (zxnext.vhd's <c>video_frame_sync</c>), where the effective copies
    /// of the video-timing registers are taken. The ULA timing applies $03's timing and $05's
    /// 50/60 Hz there itself.
    /// </summary>
    internal void OnVideoFrameSync() => _scandoublerEffective = (_peripheral1Settings & 0x01) != 0;

    internal void ConfigureCpu(ZxNextCpu cpu) =>
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));

    internal void ConfigureUlaTiming(ZxNextUlaTiming ulaTiming)
    {
        _ulaTiming = ulaTiming ?? throw new ArgumentNullException(nameof(ulaTiming));
        Copper.ConfigureBeam(ulaTiming);
    }

    internal bool BeginNmiAcknowledge(ushort returnAddress)
    {
        _nmiReturnAddress = returnAddress;
        _stacklessNmiReturnPending = (_interruptControl & 0x08) != 0;
        return _stacklessNmiReturnPending;
    }

    internal bool TryTakeStacklessNmiReturn(out ushort returnAddress)
    {
        if (_stacklessNmiReturnPending)
        {
            _stacklessNmiReturnPending = false;
            returnAddress = _nmiReturnAddress;
            return true;
        }

        returnAddress = 0;
        return false;
    }

    /// <summary>
    /// NextReg $02 bits 3 (Multiface) and 2 (DivMMC), from the CPU or the Copper: a one-clock
    /// NMI request each, and the cause bits that read back.
    /// </summary>
    /// <remarks>
    /// A cause bit is set by a request made before the current NMI's $0066 fetch, and
    /// cleared by writing it as 0. The request itself needs its NextReg $06 enable (bit 3,
    /// bit 4), and is ignored in configuration mode.
    /// </remarks>
    private void WriteNmiRequest(byte value)
    {
        bool multiface = (value & 0x08) != 0;
        bool divMmc = (value & 0x04) != 0;
        bool accepts = Nmi.AcceptsCause;
        _multifaceNmiCause = multiface && (_multifaceNmiCause || accepts);
        _divMmcNmiCause = divMmc && (_divMmcNmiCause || accepts);
        if (!_memory.ConfigurationMode)
        {
            Nmi.Request(
                multiface && (_peripheral2Settings & 0x08) != 0,
                divMmc && (_peripheral2Settings & 0x10) != 0);
        }
    }

    /// <summary>Writes one byte of a port-decode word; its top byte keeps bits 7 and 3:0.</summary>
    private static uint WritePortDecodingByte(uint decoding, int index, byte value)
    {
        int shift = index * 8;
        if (index == 3)
        {
            value &= 0x8F;
        }

        return (decoding & ~(0xFFu << shift)) | ((uint)value << shift);
    }

    internal bool TryTakeResetRequest(out ZxNextResetKind kind)
    {
        if (_pendingReset is ZxNextResetKind requested)
        {
            _pendingReset = null;
            kind = requested;
            return true;
        }

        kind = default;
        return false;
    }

    private void WriteMachineType(byte value)
    {
        _memory.DisableBootRom();

        if ((value & 0x80) != 0 && !_displayTimingLocked && (value & 0x08) == 0)
        {
            _machineTiming = ((value >> 4) & 0x07) switch
            {
                0 or 1 => 1,
                2 => 2,
                3 => 3,
                4 => 4,
                _ => 3
            };
            if ((_machineTiming & 0x04) != 0)
            {
                _peripheral1Settings = (byte)(_peripheral1Settings & ~0x04);
            }
        }

        if ((value & 0x08) != 0)
        {
            _displayTimingLocked = !_displayTimingLocked;
        }

        int requestedType = value & 0x07;
        if (_memory.ConfigurationMode && requestedType is >= 1 and <= 4)
        {
            _machineType = (byte)requestedType;
            _memory.SetMachineType(_machineType);
        }

        if (requestedType == 7)
        {
            _memory.SetConfigurationMode(true);
            Nmi.Reset();
        }
        else if (requestedType != 0)
        {
            _memory.SetConfigurationMode(false);
        }
    }

    /// <summary>The palettes these registers write through to.</summary>
    /// <remarks>
    /// Exposed because the palettes are guest-visible machine state that nothing else
    /// reaches: the renderer holds them privately, so a snapshot had no way to them.
    /// </remarks>
    public ZxNextPaletteDevice Palettes => _palettes;

    /// <summary>Reads the NextReg state this device owns, for a snapshot.</summary>
    public ZxNextRegisterFileState CaptureState() => new(
        _peripheral1Settings,
        _peripheral2Settings,
        _peripheral3Settings,
        _peripheral4Settings,
        _peripheral5Settings,
        _userRegister,
        _machineTiming,
        _machineType,
        _expansionInterruptEnabled,
        (_timexPort & 0x40) == 0,
        _lineInterruptEnabled,
        _lineInterruptMsb,
        _lineInterruptLsb,
        _lineInterruptGeneration,
        _interruptControl,
        _internalPortDecoding,
        _nmiReturnAddress,
        _stacklessNmiReturnPending,
        Nmi.AwaitingCpu && Nmi.Source == ZxNextNmiSource.DivMmc,
        Nmi.AwaitingCpu && Nmi.Source == ZxNextNmiSource.Multiface,
        _divMmcNmiCause,
        _multifaceNmiCause,
        _displayTimingLocked,
        _peripheralReset,
        _ulaClip.ToArray(),
        _ulaClipIndex,
        _timexPort,
        _ulaScrollX,
        _ulaScrollY,
        _loresScrollX,
        _loresScrollY,
        _loresControl,
        _lastResetKind,
        _pendingReset)
    {
        Extended = new ZxNextRegisterFileExtendedState(
            _resetType,
            Nmi.Source,
            Nmi.Phase,
            _joystickIoMode,
            _busPortDecoding,
            _i2sControl,
            _contentionDisableBefore,
            _contentionDisableAppliesAt,
            _scandoublerEffective)
    };

    /// <summary>
    /// Puts the NextReg state back by assignment rather than by replaying writes.
    /// </summary>
    /// <remarks>
    /// Replaying writes would re-run their side effects - reconfiguring audio, advancing
    /// the line-interrupt generation, requesting resets - which is what a guest programming
    /// the machine wants and the opposite of what restoring one wants.
    /// </remarks>
    public void RestoreState(ZxNextRegisterFileState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _peripheral1Settings = state.Peripheral1Settings;
        _peripheral2Settings = state.Peripheral2Settings;
        _peripheral3Settings = state.Peripheral3Settings;
        _peripheral4Settings = state.Peripheral4Settings;
        _peripheral5Settings = state.Peripheral5Settings;
        _userRegister = state.UserRegister;
        _machineTiming = state.MachineTiming;
        _machineType = state.MachineType;
        _memory.SetMachineType(_machineType);
        _expansionInterruptEnabled = state.ExpansionInterruptEnabled;
        _lineInterruptEnabled = state.LineInterruptEnabled;
        _lineInterruptMsb = state.LineInterruptMsb;
        _lineInterruptLsb = state.LineInterruptLsb;
        _lineInterruptGeneration = state.LineInterruptGeneration;
        _interruptControl = state.InterruptControl;
        _internalPortDecoding = state.InternalPortDecoding;
        _nmiReturnAddress = state.NmiReturnAddress;
        _stacklessNmiReturnPending = state.StacklessNmiReturnPending;
        _divMmcNmiCause = state.DivMmcNmiCause;
        _multifaceNmiCause = state.MultifaceNmiCause;
        _displayTimingLocked = state.DisplayTimingLocked;
        _peripheralReset = state.PeripheralReset;
        state.UlaClip.CopyTo(_ulaClip, 0);
        _ulaClipIndex = state.UlaClipIndex;
        _timexPort = state.TimexPort;

        // Files from before the flip-flop was shared kept a second latch for $22/$C4; either
        // one disabling the interrupt meant it was disabled.
        if (!state.UlaInterruptEnabled)
        {
            _timexPort |= 0x40;
        }
        _ulaScrollX = state.UlaScrollX;
        _ulaScrollY = state.UlaScrollY;
        _loresScrollX = state.LoresScrollX;
        _loresScrollY = state.LoresScrollY;
        _loresControl = state.LoresControl;
        _lastResetKind = state.LastResetKind;
        _pendingReset = state.PendingReset;
        if (state.Extended is ZxNextRegisterFileExtendedState extended)
        {
            _resetType = (byte)(extended.ResetType & 0x07);
            Nmi.Restore(extended.NmiSource, extended.NmiPhase);
            _joystickIoMode = extended.JoystickIoMode;
            _busPortDecoding = extended.BusPortDecoding;
            _i2sControl = extended.I2sControl;
            _contentionDisableBefore = extended.ContentionDisableBefore;
            _contentionDisableAppliesAt = extended.ContentionDisableAppliesAt;
            _scandoublerEffective = extended.ScandoublerEffective ?? (_peripheral1Settings & 0x01) != 0;
        }
        else
        {
            // Saved before these were stored: $02 reads back what it did then, and the rest
            // are at their reset values. The NMI latch is rebuilt from the devices once they
            // have been restored (ZxNextNmiController.RestoreLegacy).
            _resetType = state.LastResetKind == ZxNextResetKind.Hard ? (byte)0b010 : (byte)0b001;
            Nmi.Reset();
            _joystickIoMode = 0x01;
            _busPortDecoding = PortDecodingPowerOn;
            _i2sControl = 0;
            _contentionDisableBefore = false;
            _contentionDisableAppliesAt = 0;
            _scandoublerEffective = (_peripheral1Settings & 0x01) != 0;
        }

        // The audio device reads its routing out of the peripheral registers, so it has to
        // be told they have moved rather than left on the previous machine's settings.
        ConfigureAudio();
        ApplyInternalPortEnables();
    }
}
