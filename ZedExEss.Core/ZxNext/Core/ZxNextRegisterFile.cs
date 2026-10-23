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
    private byte _machineTiming = 0x03;
    private byte _machineType = 0x03;
    private bool _expansionInterruptEnabled = true;
    private bool _ulaInterruptEnabled = true;
    private bool _lineInterruptEnabled;
    private bool _lineInterruptMsb;
    private byte _lineInterruptLsb;
    private int _lineInterruptGeneration;
    private byte _interruptControl;
    private uint _internalPortDecoding = uint.MaxValue;
    private ushort _nmiReturnAddress;
    private bool _stacklessNmiReturnPending;
    private bool _pendingDivMmcNmi;
    private bool _pendingMultifaceNmi;
    private bool _divMmcNmiCause;
    private bool _multifaceNmiCause;
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
    }

    public byte? LastWrittenRegister { get; private set; }
    public byte LastWrittenValue { get; private set; }
    public ZxNextResetKind LastResetKind => _lastResetKind;
    public bool PeripheralResetAsserted => _peripheralReset;
    public bool HasPendingReset => _pendingReset.HasValue;
    public byte MachineTiming => _machineTiming;
    public bool SixtyHertzMode => (_peripheral1Settings & 0x04) != 0 && (_machineTiming & 0x04) == 0;
    public bool ContentionDisabled => (_peripheral3Settings & 0x40) != 0;
    public bool UlaInterruptEnabled => _ulaInterruptEnabled && (_timexPort & 0x40) == 0;
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
            0x01 => 0x32, // Core 3.02; sub-minor is exposed by 0x0E.
            0x02 => (byte)((_peripheralReset ? 0x80 : 0)
                | (_multifaceNmiCause ? 0x08 : 0)
                | (_divMmcNmiCause ? 0x04 : 0)
                | (_lastResetKind == ZxNextResetKind.Hard ? 0x02 : 0x01)),
            0x03 => (byte)((_machineTiming << 4) | (_displayTimingLocked ? 0x08 : 0) | _machineType),
            0x05 => _peripheral1Settings,
            0x06 => _peripheral2Settings,
            0x07 => (byte)(((byte)_clock.CpuSpeed << 4) | (byte)_clock.CpuSpeed),
            0x08 => (byte)((_paging.PagingLocked ? 0 : 0x80) | _peripheral3Settings),
            0x09 => (byte)(_peripheral4Settings & ~0x08),
            0x0A => _peripheral5Settings,
            0x0E => 0x03,
            0x1E => (byte)((_ulaTiming?.CurrentCopperLine ?? 0) >> 8),
            0x1F => (byte)(_ulaTiming?.CurrentCopperLine ?? 0),
            0x1A => _ulaClip[_ulaClipIndex],
            0x1C => (byte)(_layer2.Read(0x1C)
                | _sprites.Read(0x1C)
                | (_ulaClipIndex << 4)
                | _tilemap.Read(0x1C)),
            0x22 => (byte)((_ulaTiming?.InterruptLineActive == true ? 0x80 : 0)
                | (_ulaInterruptEnabled ? 0 : 0x04)
                | (_lineInterruptEnabled ? 0x02 : 0)
                | (_lineInterruptMsb ? 0x01 : 0)),
            0x23 => _lineInterruptLsb,
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
                | (_ulaInterruptEnabled ? 0x01 : 0)),
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

        if (register == 0x02)
        {
            _peripheralReset = (value & 0x80) != 0;
            _multifaceNmiCause = (value & 0x08) != 0;
            _divMmcNmiCause = (value & 0x04) != 0;
            if (_multifaceNmiCause)
            {
                _pendingMultifaceNmi = true;
            }
            else
            {
                _pendingMultifaceNmi = false;
            }

            if (_divMmcNmiCause)
            {
                _pendingDivMmcNmi = true;
            }
            else
            {
                _pendingDivMmcNmi = false;
            }

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
            _clock.SetCpuSpeed((ZxNextCpuSpeed)(value & 0x03));
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
            _ulaInterruptEnabled = (value & 0x04) == 0;
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
            int shift = (register - 0x82) * 8;
            uint mask = 0xFFu << shift;
            _internalPortDecoding = (_internalPortDecoding & ~mask) | ((uint)value << shift);
            ApplyInternalPortEnables();
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
        else if (register == 0xC0)
        {
            _interruptControl = (byte)(value & 0xE9);
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
            _ulaInterruptEnabled = (value & 0x01) != 0;
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
        _clock.SetCpuSpeed(ZxNextCpuSpeed.Mhz3_5);
        _paging.Reset();
        _divMmc.Reset(kind);
        _pendingReset = null;
        _lastResetKind = kind;
        _expansionInterruptEnabled = true;
        _ulaInterruptEnabled = true;
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
        _pendingDivMmcNmi = false;
        _pendingMultifaceNmi = false;
        _divMmcNmiCause = false;
        _multifaceNmiCause = false;
        _userRegister = 0xFF;
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
            _internalPortDecoding = uint.MaxValue;
            _peripheral3Settings = 0x10;
            _peripheral1Settings = 0x41;
            _peripheral2Settings = 0xA0;
            _peripheral4Settings = 0;
            _peripheral5Settings = 0x01;
            _machineTiming = 0x03;
            _machineType = 0x03;
            _displayTimingLocked = false;
            _peripheralReset = false;
            _memory.HardResetRomState();
        }
        else
        {
            if ((_internalPortDecoding & 0x8000_0000u) != 0)
            {
                _internalPortDecoding = uint.MaxValue;
            }

            _peripheral2Settings |= 0xA0;
            _peripheral3Settings = (byte)(_peripheral3Settings & ~0x40);
            _peripheral4Settings = (byte)(_peripheral4Settings & ~0x10);
            _memory.SoftResetRomState();
        }

        _joystick.ConfigureModes(_peripheral1Settings);
        ApplyInternalPortEnables();
        _audio.Reset();
        ConfigureAudio();
        _palettes.ResetRegisters();
        _layer2.Reset();
        _tilemap.Reset();
        _sprites.Reset();
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

    internal void ConfigureCpu(ZxNextCpu cpu) =>
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));

    internal void ConfigureUlaTiming(ZxNextUlaTiming ulaTiming) =>
        _ulaTiming = ulaTiming ?? throw new ArgumentNullException(nameof(ulaTiming));

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

    internal bool TryTakeDivMmcNmiRequest()
    {
        bool requested = _pendingDivMmcNmi;
        _pendingDivMmcNmi = false;
        return requested;
    }

    internal bool TryTakeMultifaceNmiRequest()
    {
        bool requested = _pendingMultifaceNmi;
        _pendingMultifaceNmi = false;
        return requested;
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
        }

        if (requestedType == 7)
        {
            _memory.SetConfigurationMode(true);
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
        _ulaInterruptEnabled,
        _lineInterruptEnabled,
        _lineInterruptMsb,
        _lineInterruptLsb,
        _lineInterruptGeneration,
        _interruptControl,
        _internalPortDecoding,
        _nmiReturnAddress,
        _stacklessNmiReturnPending,
        _pendingDivMmcNmi,
        _pendingMultifaceNmi,
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
        _pendingReset);

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
        _expansionInterruptEnabled = state.ExpansionInterruptEnabled;
        _ulaInterruptEnabled = state.UlaInterruptEnabled;
        _lineInterruptEnabled = state.LineInterruptEnabled;
        _lineInterruptMsb = state.LineInterruptMsb;
        _lineInterruptLsb = state.LineInterruptLsb;
        _lineInterruptGeneration = state.LineInterruptGeneration;
        _interruptControl = state.InterruptControl;
        _internalPortDecoding = state.InternalPortDecoding;
        _nmiReturnAddress = state.NmiReturnAddress;
        _stacklessNmiReturnPending = state.StacklessNmiReturnPending;
        _pendingDivMmcNmi = state.PendingDivMmcNmi;
        _pendingMultifaceNmi = state.PendingMultifaceNmi;
        _divMmcNmiCause = state.DivMmcNmiCause;
        _multifaceNmiCause = state.MultifaceNmiCause;
        _displayTimingLocked = state.DisplayTimingLocked;
        _peripheralReset = state.PeripheralReset;
        state.UlaClip.CopyTo(_ulaClip, 0);
        _ulaClipIndex = state.UlaClipIndex;
        _timexPort = state.TimexPort;
        _ulaScrollX = state.UlaScrollX;
        _ulaScrollY = state.UlaScrollY;
        _loresScrollX = state.LoresScrollX;
        _loresScrollY = state.LoresScrollY;
        _loresControl = state.LoresControl;
        _lastResetKind = state.LastResetKind;
        _pendingReset = state.PendingReset;

        // The audio device reads its routing out of the peripheral registers, so it has to
        // be told they have moved rather than left on the previous machine's settings.
        ConfigureAudio();
        ApplyInternalPortEnables();
    }
}
