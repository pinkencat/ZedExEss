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
    private readonly ZxNextAudioDevice _audio;
    private readonly ZxNextLayer2Device _layer2;
    private readonly ZxNextSpriteDevice _sprites;
    private readonly ZxNextDmaDevice _dma;
    private ZxNextCpu? _cpu;
    private ZxNextContentionController? _contention;
    private ZxNextFloatingBus? _floatingBus;

    public ZxNextPortBus(
        ZxNextRegisterFile registers,
        ZxNextPagingController paging,
        ZxNextDivMmcDevice divMmc,
        ZxNextMultifaceDevice multiface,
        SpectrumKeyboard keyboard,
        ZxNextJoystickDevice joystick,
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
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _layer2 = layer2 ?? throw new ArgumentNullException(nameof(layer2));
        _sprites = sprites ?? throw new ArgumentNullException(nameof(sprites));
        _dma = dma ?? throw new ArgumentNullException(nameof(dma));
    }

    public ushort? LastWrittenPort { get; private set; }
    public byte LastWrittenValue { get; private set; }
    public byte SelectedRegister { get; private set; } = ResetSelectedRegister;
    public byte UlaOutputLatch { get; private set; }

    public byte ReadUncontended(ushort port) => port switch
    {
        NextRegisterSelectPort => SelectedRegister,
        NextRegisterDataPort => _registers.Read(SelectedRegister),
        _ when (byte)port == 0xFF && _registers.TimexPortReadEnabled
            => _registers.ReadTimexPort(),
        ZxNextLayer2Device.ControlPort when InternalPortEnabled(15) => _layer2.ReadControlPort(),
        _ when InternalPortEnabled(14) && _sprites.HandlesReadPort(port) => _sprites.ReadPort(port),
        _ when DmaPortEnabled(port) && _dma.HandlesPort(port) => _dma.Read(port),
        _ when InternalPortEnabled(8) && (byte)port == DivMmcControlPort => _divMmc.ControlRegister,
        _ when InternalPortEnabled(11) && (byte)port == SpiDataPort => _divMmc.ReadSpi(),
        _ when InternalPortEnabled(9) && _multiface.TryReadPort(
            port,
            _registers.MultifaceType,
            _paging,
            UlaOutputLatch,
            out byte multifaceValue) => multifaceValue,
        _ when InternalPortEnabled(16) && _audio.HandlesPort(port) && !IsPortDFfd(port) => _audio.ReadPort(port),
        _ when JoystickPortEnabled(port) && _joystick.HandlesPort(port) => _joystick.ReadPort(port),
        _ when IsUlaPort(port) => ReadUlaPort(port),
        _ when InternalPortEnabled(4) && _floatingBus?.HandlesPort(port) == true => _floatingBus.Read(port),
        _ => 0xFF
    };

    public void WriteUncontended(ushort port, byte value)
    {
        LastWrittenPort = port;
        LastWrittenValue = value;

        if (port == NextRegisterSelectPort)
        {
            SelectedRegister = value;
        }
        else if (port == NextRegisterDataPort)
        {
            _registers.Write(SelectedRegister, value);
        }
        else if ((byte)port == 0xFF)
        {
            _registers.WriteTimexPort(value);
        }
        else if (port == ZxNextLayer2Device.ControlPort && InternalPortEnabled(15))
        {
            _layer2.WriteControlPort(value);
        }
        else if (InternalPortEnabled(14) && _sprites.HandlesWritePort(port))
        {
            _sprites.WritePort(port, value);
        }
        else if (DmaPortEnabled(port) && _dma.HandlesPort(port))
        {
            _dma.Write(port, value);
        }
        else if (InternalPortEnabled(8) && (byte)port == DivMmcControlPort)
        {
            _divMmc.WriteControl(value);
        }
        else if (InternalPortEnabled(11) && (byte)port == SpiCardSelectPort)
        {
            _divMmc.WriteCardSelect(value);
        }
        else if (InternalPortEnabled(11) && (byte)port == SpiDataPort)
        {
            _divMmc.WriteSpi(value);
        }
        else if (InternalPortEnabled(9) && _multiface.TryWritePort(port, _registers.MultifaceType))
        {
        }
        else if (IsUlaPort(port))
        {
            UlaOutputLatch = (byte)(value & 0x1F);
            _audio.SetUlaOutputLatch(UlaOutputLatch);
        }
        else if (InternalPortEnabled(1) && IsPort7Ffd(port))
        {
            _paging.WritePort7Ffd(value);
        }
        else if (InternalPortEnabled(2) && IsPortDFfd(port))
        {
            _paging.WritePortDFfd(value);
        }
        else if (InternalPortEnabled(3) && IsPort1Ffd(port))
        {
            _paging.WritePort1Ffd(value);
        }
        else if (InternalPortEnabled(26) && IsPortEff7(port))
        {
            _paging.WritePortEff7(value);
        }
        else if (InternalPortEnabled(16) && _audio.HandlesPort(port))
        {
            _audio.WritePort(port, value);
        }
    }

    public void ApplyIoContentionBeforeCycle(ushort port, int phase)
    {
        if (phase == 0)
        {
            _cpu?.SynchronizeBatchedInstructionTstates();
        }

        // The FPGA qualifies its synthetic wait with the decoded port and samples it
        // during the active I/O machine cycle. Phase one is the shared CPU core's
        // pre-sample no-MREQ point for ordinary Spectrum-compatible port timing.
        if (phase != 1 || _cpu == null || _contention == null
            || !_contention.IsPortContended(port))
        {
            return;
        }

        int delay = _contention.GetNoMreqDelay(_cpu.Cyc);
        if (delay > 0)
        {
            _cpu.AddWaitStates(delay);
        }
    }

    internal void ConfigureTiming(ZxNextCpu cpu, ZxNextContentionController contention)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _contention = contention ?? throw new ArgumentNullException(nameof(contention));
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

    public static bool IsPort7Ffd(ushort port) => (port & 0xC003) == 0x4001;

    public static bool IsPortDFfd(ushort port) => (port & 0xF003) == 0xD001;

    public static bool IsPort1Ffd(ushort port) => (port & 0xF003) == 0x1001;

    public static bool IsPortEff7(ushort port) => (port & 0xF0FF) == 0xE0F7;

    public static bool IsUlaPort(ushort port) => (port & 0x0001) == 0;

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

    private bool DmaPortEnabled(ushort port) => (byte)port switch
    {
        ZxNextDmaDevice.ZxnControlPort => InternalPortEnabled(5),
        ZxNextDmaDevice.Z80ControlPort => InternalPortEnabled(25),
        _ => false
    };

    private bool JoystickPortEnabled(ushort port) => (byte)port switch
    {
        0x1F => InternalPortEnabled(6),
        0x37 => InternalPortEnabled(7),
        0xDF => InternalPortEnabled(23),
        _ => false
    };

    private byte ReadUlaPort(ushort port)
    {
        byte ear = (UlaOutputLatch & 0x10) != 0 ? (byte)0x40 : (byte)0;
        return (byte)(0xA0 | ear | (_keyboard.ReadRows((byte)(port >> 8)) & 0x1F));
    }
}
