using ZedExEss.Spectrum.Input;
using ZedExEss.Z80CPU;
using ZedExEss.ZxNext.Audio;
using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Dma;
using ZedExEss.ZxNext.DivMmc;
using ZedExEss.ZxNext.Input;
using ZedExEss.ZxNext.Memory;
using ZedExEss.ZxNext.Multiface;
using ZedExEss.ZxNext.Video;

namespace ZedExEss.ZxNext.Ports;

/// <summary>ZX Spectrum Next port bus with the standard NextReg select/data pair.</summary>
/// <remarks>
/// Decodes as zxnext.vhd does: every device's decode is evaluated on its own. A read is the
/// OR of the data of every device that answers it (<c>port_rd_dat</c>), and a port nothing
/// answers reads $FF; a write reaches every device that decodes it.
/// </remarks>
public sealed class ZxNextPortBus : IZ80PortBus, IZxNextRegisterBus
{
    public const ushort NextRegisterSelectPort = 0x243B;
    public const ushort NextRegisterDataPort = 0x253B;
    public const byte DivMmcControlPort = 0xE3;
    public const byte SpiCardSelectPort = 0xE7;
    public const byte SpiDataPort = 0xEB;
    public const byte UlaPort = 0xFE;
    public const byte ResetSelectedRegister = 0x24;

    private readonly ZxNextRegisterFile _registers;
    private readonly ZxNextPagingController _paging;
    private readonly ZxNextDivMmcDevice _divMmc;
    private readonly ZxNextMultifaceDevice _multiface;
    private readonly SpectrumKeyboard _keyboard;
    private readonly ZxNextJoystickDevice _joystick;
    private readonly ZxNextMouseDevice _mouse;
    private readonly ZxNextAudioDevice _audio;
    private readonly ZxNextLayer2Device _layer2;
    private readonly ZxNextSpriteDevice _sprites;
    private readonly ZxNextDmaDevice _dma;
    private ZxNextCpu? _cpu;
    private ZxNextContentionController? _contention;
    private ZxNextUlaTiming? _ulaTiming;
    private ZxNextFloatingBus? _floatingBus;
    private ZxNextMemoryBus? _memory;

    public ZxNextPortBus(
        ZxNextRegisterFile registers,
        ZxNextPagingController paging,
        ZxNextDivMmcDevice divMmc,
        ZxNextMultifaceDevice multiface,
        SpectrumKeyboard keyboard,
        ZxNextJoystickDevice joystick,
        ZxNextMouseDevice mouse,
        ZxNextAudioDevice audio,
        ZxNextLayer2Device layer2,
        ZxNextSpriteDevice sprites,
        ZxNextDmaDevice dma)
    {
        _registers = registers ?? throw new ArgumentNullException(nameof(registers));
        _paging = paging ?? throw new ArgumentNullException(nameof(paging));
        _divMmc = divMmc ?? throw new ArgumentNullException(nameof(divMmc));
        _multiface = multiface ?? throw new ArgumentNullException(nameof(multiface));
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));
        _joystick = joystick ?? throw new ArgumentNullException(nameof(joystick));
        _mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _layer2 = layer2 ?? throw new ArgumentNullException(nameof(layer2));
        _sprites = sprites ?? throw new ArgumentNullException(nameof(sprites));
        _dma = dma ?? throw new ArgumentNullException(nameof(dma));
    }

    public ushort? LastWrittenPort { get; private set; }
    public byte LastWrittenValue { get; private set; }
    public byte SelectedRegister { get; private set; } = ResetSelectedRegister;
    public byte UlaOutputLatch { get; private set; }

    public byte ReadUncontended(ushort port)
    {
        byte low = (byte)port;
        bool decoded = false;
        int value = 0;

        if (port == NextRegisterSelectPort)
        {
            decoded = true;
            value |= SelectedRegister;
        }
        else if (port == NextRegisterDataPort)
        {
            decoded = true;
            value |= _registers.Read(SelectedRegister);
        }

        if (IsUlaPort(port))
        {
            decoded = true;
            value |= ReadUlaPort(port);
        }

        if (low == 0xFF)
        {
            decoded = true;
            value |= ReadPortFf(port);
        }

        if (InternalPortEnabled(4) && IsPlus3Timing && IsPlus3FloatingBusPort(port))
        {
            decoded = true;
            value |= _floatingBus?.Read(port) ?? 0xFF;
        }

        if (InternalPortEnabled(8) && low == DivMmcControlPort)
        {
            decoded = true;
            value |= _divMmc.ControlRegister;
        }

        if (InternalPortEnabled(9) && _multiface.TryReadPort(
                port,
                _registers.MultifaceType,
                _paging,
                UlaOutputLatch,
                out byte multifaceValue))
        {
            decoded = true;
            value |= multifaceValue;
        }

        if (InternalPortEnabled(11) && low == SpiDataPort)
        {
            decoded = true;
            value |= _divMmc.ReadSpi();
        }

        if (InternalPortEnabled(15) && port == ZxNextLayer2Device.ControlPort)
        {
            decoded = true;
            value |= _layer2.ReadControlPort();
        }

        if (DmaPortEnabled(port) && _dma.HandlesPort(port))
        {
            decoded = true;
            value |= _dma.Read(port);
        }

        if (InternalPortEnabled(16))
        {
            // port_fffd_rd: $FFFD, $BFFD in +3 timing only, and $BFF5 (information).
            if (IsAyInformationPort(port))
            {
                decoded = true;
                value |= _audio.ReadInformation();
            }
            else if (IsAyRegisterSelectPort(port) || (IsAyDataPort(port) && IsPlus3Timing))
            {
                decoded = true;
                value |= _audio.ReadSelectedRegister();
            }
        }

        if (InternalPortEnabled(13) && ZxNextMouseDevice.IsMousePort(port))
        {
            decoded = true;
            value |= _mouse.ReadPort(port, _registers.Peripheral5Settings);
        }

        if (JoystickPortEnabled(port) && _joystick.HandlesPort(port))
        {
            decoded = true;
            value |= _joystick.ReadPort(port);
        }

        if (InternalPortEnabled(14) && _sprites.HandlesReadPort(port))
        {
            decoded = true;
            value |= _sprites.ReadPort(port);
        }

        if (InternalPortEnabled(27) && ZxNextCtcDevice.IsCtcPort(port))
        {
            decoded = true;
            value |= _registers.ReadCtcPort(port);
        }

        return decoded ? (byte)value : (byte)0xFF;
    }

    public void WriteUncontended(ushort port, byte value)
    {
        LastWrittenPort = port;
        LastWrittenValue = value;
        byte low = (byte)port;

        if (port == NextRegisterSelectPort)
        {
            SelectedRegister = value;
        }
        else if (port == NextRegisterDataPort)
        {
            _registers.Write(SelectedRegister, value);
        }

        if (IsUlaPort(port))
        {
            UlaOutputLatch = (byte)(value & 0x1F);
            _audio.SetUlaOutputLatch(UlaOutputLatch);
        }

        if (InternalPortEnabled(0) && low == 0xFF)
        {
            _registers.WriteTimexPort(value);
        }

        // While Soundrive mode 2 holds $F1 and $F9 (port enable bit 18, on after reset), a
        // write there never reaches a paging port (port_fd_conflict_wr).
        if (!(InternalPortEnabled(18) && low is 0xF1 or 0xF9))
        {
            bool port1FfdEnabled = InternalPortEnabled(3);
            if (InternalPortEnabled(1) && IsPort7Ffd(port, IsPlus3Timing, port1FfdEnabled))
            {
                _paging.WritePort7Ffd(value);
            }

            if (InternalPortEnabled(2) && IsPortDFfd(port))
            {
                _paging.WritePortDFfd(value);
            }

            if (port1FfdEnabled && IsPort1Ffd(port))
            {
                _paging.WritePort1Ffd(value);
            }
        }

        if (InternalPortEnabled(26) && IsPortEff7(port))
        {
            _paging.WritePortEff7(value);
        }

        if (InternalPortEnabled(8) && low == DivMmcControlPort)
        {
            _divMmc.WriteControl(value);
        }

        if (InternalPortEnabled(11) && low == SpiCardSelectPort)
        {
            _divMmc.WriteCardSelect(value);
        }

        if (InternalPortEnabled(11) && low == SpiDataPort)
        {
            _divMmc.WriteSpi(value);
        }

        if (InternalPortEnabled(9))
        {
            _multiface.TryWritePort(port, _registers.MultifaceType);
        }

        if (InternalPortEnabled(15) && port == ZxNextLayer2Device.ControlPort)
        {
            _layer2.WriteControlPort(value);
        }

        if (InternalPortEnabled(14) && _sprites.HandlesWritePort(port))
        {
            _sprites.WritePort(port, value);
        }

        if (DmaPortEnabled(port) && _dma.HandlesPort(port))
        {
            _dma.Write(port, value);
        }

        if (InternalPortEnabled(16))
        {
            // port_fffd_wr excludes $DFFD while that port is enabled; port_bffd_wr takes
            // the whole $BFFD decode, $BFF5 included.
            if (IsAyRegisterSelectPort(port) && !(InternalPortEnabled(2) && IsPortDFfd(port)))
            {
                _audio.WriteRegisterSelect(value);
            }

            if (IsAyDataPort(port))
            {
                _audio.WriteSelectedRegister(value);
            }
        }

        int dacChannels = DecodeDacChannels(low);
        if (dacChannels != 0)
        {
            _audio.WriteDacPort(dacChannels, value);
        }

        if (InternalPortEnabled(27) && ZxNextCtcDevice.IsCtcPort(port))
        {
            _registers.WriteCtcPort(port, value);
        }
    }

    /// <summary>
    /// The DAC channels a write to this low byte reaches (bit 0 A, 1 B, 2 C, 3 D), from
    /// zxnext.vhd's <c>port_dac_A</c> to <c>port_dac_D</c> and the port enables in NextReg
    /// $84. The ports decode on A7-A0 alone and are write only: nothing answers a read.
    /// </summary>
    /// <remarks>
    /// <code>
    ///      gs   pentagon  specdrum  soundrive 1  soundrive 2  covox  profi
    ///      (22) (21)      (23)      (17)         (18)         (20)   (19)
    /// A         $FB       $DF       $1F          $F1                 $3F
    /// B    $B3                      $0F          $F3          $0F
    /// C    $B3                      $4F          $F9          $4F
    /// D         $FB       $DF       $5F          $FB                 $5F
    /// </code>
    /// The Pentagon/ATM $FB is off while Soundrive mode 2 is on, which takes $FB for D alone.
    /// </remarks>
    internal int DecodeDacChannels(byte low)
    {
        const int a = 0x01, b = 0x02, c = 0x04, d = 0x08;
        return low switch
        {
            0x1F => InternalPortEnabled(17) ? a : 0,
            0xF1 => InternalPortEnabled(18) ? a : 0,
            0x3F => InternalPortEnabled(19) ? a : 0,
            0x0F or 0x4F => InternalPortEnabled(17) || InternalPortEnabled(20) ? (low == 0x0F ? b : c) : 0,
            0xF3 => InternalPortEnabled(18) ? b : 0,
            0xF9 => InternalPortEnabled(18) ? c : 0,
            0x5F => InternalPortEnabled(17) || InternalPortEnabled(19) ? d : 0,
            0xFB => InternalPortEnabled(18) ? d : InternalPortEnabled(21) ? a | d : 0,
            0xDF => InternalPortEnabled(23) ? a | d : 0,
            0xB3 => InternalPortEnabled(22) ? b | c : 0,
            _ => 0
        };
    }

    /// <summary>
    /// The 48K/128K ULA wait for one T-state of an I/O cycle, phase 0 being T1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// zxula.vhd contends a clock when the address on the bus is in a contended MMU page and
    /// no memory or contended-port cycle was under way on the previous clock, or on the first
    /// clock of IORQ to a contended port (<c>port_contend</c>). So the port's high byte
    /// counts as an address (the 3.02.02 fix), giving the classic patterns:
    /// </para>
    /// <list type="bullet">
    /// <item>a contended port with a contended high byte: C:1, C:3;</item>
    /// <item>a contended port with an uncontended high byte: N:1, C:3;</item>
    /// <item>an uncontended port with a contended high byte: C:1, C:1, C:1, C:1;</item>
    /// <item>an uncontended port with an uncontended high byte: N:4.</item>
    /// </list>
    /// <para>
    /// +3 timing has no I/O contention, which <see cref="ZxNextContentionController.GetNoMreqDelay"/>
    /// already answers.
    /// </para>
    /// </remarks>
    public void ApplyIoContentionBeforeCycle(ushort port, int phase)
    {
        ZxNextCpu? cpu = _cpu;
        ZxNextContentionController? contention = _contention;
        if (cpu == null || contention == null || _memory == null)
        {
            return;
        }

        if (phase == 0)
        {
            cpu.SynchronizeBatchedInstructionTstates();
        }

        if (!contention.Enabled)
        {
            return;
        }

        bool highByte = _memory.IsContendedAddress(port);
        bool contended = phase switch
        {
            0 => highByte,
            1 => highByte || contention.IsPortContended(port),
            _ => highByte && !contention.IsPortContended(port)
        };
        if (!contended)
        {
            return;
        }

        // The wait depends on where the beam is, so batched instruction time has to reach
        // the machine first.
        cpu.SynchronizeBatchedInstructionTstates();
        int delay = contention.GetNoMreqDelay(cpu.Cyc);
        if (delay > 0)
        {
            cpu.AddWaitStates(delay);
        }
    }

    internal void ConfigureTiming(
        ZxNextCpu cpu,
        ZxNextContentionController contention,
        ZxNextUlaTiming ulaTiming,
        ZxNextMemoryBus memory)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _contention = contention ?? throw new ArgumentNullException(nameof(contention));
        _ulaTiming = ulaTiming ?? throw new ArgumentNullException(nameof(ulaTiming));
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    internal void ConfigureFloatingBus(ZxNextFloatingBus floatingBus)
    {
        _floatingBus = floatingBus ?? throw new ArgumentNullException(nameof(floatingBus));
    }

    public void WriteNextRegister(byte register, byte value) => _registers.Write(register, value);

    public void Reset()
    {
        LastWrittenPort = null;
        LastWrittenValue = 0;
        SelectedRegister = ResetSelectedRegister;
        UlaOutputLatch = 0;
        _audio.SetUlaOutputLatch(0);
    }

    /// <summary>True when an access to this port would reach the SPI master's data port.</summary>
    internal bool IsSpiDataPort(ushort port) =>
        InternalPortEnabled(11) && (byte)port == SpiDataPort;

    /// <summary>
    /// $7FFD (zxnext.vhd's port_7ffd): A15 = 0 and A1:0 = 01; A14 = 1 only in +3 timing;
    /// and not an address that $1FFD decodes while it is enabled.
    /// </summary>
    public static bool IsPort7Ffd(ushort port, bool plus3Timing, bool port1FfdEnabled) =>
        (port & 0x8003) == 0x0001
        && ((port & 0x4000) != 0 || !plus3Timing)
        && !(port1FfdEnabled && IsPort1Ffd(port));

    public static bool IsPortDFfd(ushort port) => (port & 0xF003) == 0xD001;

    public static bool IsPort1Ffd(ushort port) => (port & 0xF003) == 0x1001;

    public static bool IsPortEff7(ushort port) => (port & 0xF0FF) == 0xE0F7;

    public static bool IsUlaPort(ushort port) => (port & 0x0001) == 0;

    /// <summary>The AY register select, $FFFD: A15:14 = 11, A2 = 1, A1:0 = 01.</summary>
    public static bool IsAyRegisterSelectPort(ushort port) => (port & 0xC007) == 0xC005;

    /// <summary>The AY data port, $BFFD: A15:14 = 10, A2 = 1, A1:0 = 01.</summary>
    public static bool IsAyDataPort(ushort port) => (port & 0xC007) == 0x8005;

    /// <summary>$BFF5: any $BFFD-decoded address with A3 = 0 reads the chip and register.</summary>
    public static bool IsAyInformationPort(ushort port) => (port & 0xC00F) == 0x8005;

    /// <summary>The +3 floating bus port: A15:12 = 0000 and A1:0 = 01.</summary>
    public static bool IsPlus3FloatingBusPort(ushort port) => (port & 0xF003) == 0x0001;

    public static bool IsKempstonAliasPort(ushort port) => (byte)port == 0xDF;

    /// <summary>Captures the bus's own latches, border and beeper bit included.</summary>
    public ZxNextPortBusState CaptureState() => new(
        LastWrittenPort ?? 0,
        LastWrittenPort.HasValue,
        LastWrittenValue,
        SelectedRegister,
        UlaOutputLatch);

    /// <summary>
    /// Puts the latches back without letting a port write happen.
    /// </summary>
    /// <remarks>
    /// Assigned rather than replayed through <see cref="Write"/>, which would drive the
    /// border, the beeper and whichever device decodes that port all over again. The audio
    /// device holds its own copy of the output latch and restores that itself, so the two
    /// agree without either writing through to the other.
    /// </remarks>
    public void RestoreState(ZxNextPortBusState state)
    {
        LastWrittenPort = state.HasLastWrittenPort ? state.LastWrittenPort : null;
        LastWrittenValue = state.LastWrittenValue;
        SelectedRegister = state.SelectedRegister;
        UlaOutputLatch = state.UlaOutputLatch;
    }

    private bool InternalPortEnabled(int bit) => _registers.IsInternalPortEnabled(bit);

    private bool IsPlus3Timing => _ulaTiming?.Profile.IsPlus3 ?? false;

    /// <summary>
    /// $FF reads: the Timex register when NextReg $08 bit 2 and the $FF port enable allow;
    /// otherwise the ULA's floating bus in 48K and 128K timing, and $FF in +3 and Pentagon.
    /// </summary>
    private byte ReadPortFf(ushort port)
    {
        if (_registers.TimexPortReadEnabled && InternalPortEnabled(0))
        {
            return _registers.ReadTimexPort();
        }

        ZxNextUlaTimingProfile? profile = _ulaTiming?.Profile;
        return profile is { IsPlus3: false, IsPentagon: false } && _floatingBus != null
            ? _floatingBus.Read(port)
            : (byte)0xFF;
    }

    private bool DmaPortEnabled(ushort port) => (byte)port switch
    {
        ZxNextDmaDevice.ZxnControlPort => InternalPortEnabled(5),
        ZxNextDmaDevice.Z80ControlPort => InternalPortEnabled(25),
        _ => false
    };

    /// <remarks>
    /// The $DF alias of Kempston 1 is decoded only while the Kempston mouse is disabled
    /// (NextReg $83 bit 5): the pinned core's <c>port_1f</c> takes $DF only when the mouse
    /// enable is clear, alongside the $1F enable and the $DF DAC enable.
    /// </remarks>
    private bool JoystickPortEnabled(ushort port) => (byte)port switch
    {
        0x1F => InternalPortEnabled(6),
        0x37 => InternalPortEnabled(7),
        0xDF => InternalPortEnabled(6) && InternalPortEnabled(23) && !InternalPortEnabled(13),
        _ => false
    };

    /// <remarks>
    /// Bit 6 is the EAR input ORed with the EAR output bit. With no tape the board's
    /// symmetric relaxation settles the input to "issue 2 keyboard (NextReg $08 bit 0) AND
    /// MIC", so on an issue 2 setting the MIC bit reads back too.
    /// </remarks>
    private byte ReadUlaPort(ushort port)
    {
        bool ear = (UlaOutputLatch & 0x10) != 0
            || (_registers.KeyboardIssue2 && (UlaOutputLatch & 0x08) != 0);
        return (byte)(0xA0 | (ear ? 0x40 : 0) | (_keyboard.ReadRows((byte)(port >> 8)) & 0x1F));
    }
}
