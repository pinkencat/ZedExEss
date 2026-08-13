using System.Runtime.CompilerServices;
using ZedExEss.Z80CPU;
using ZedExEss.Zx8x.Input;
using ZedExEss.Zx8x.Tape;

namespace ZedExEss.Zx8x.Core;

/// <summary>Uncontended ZX80/ZX81 I/O bus specialization for the shared Z80 core.</summary>
public sealed class Zx8xCpuPortBus(Zx8xIoDevice io) : IZ80PortBus
{
    private readonly Zx8xIoDevice _io = io ?? throw new ArgumentNullException(nameof(io));
    private Zx8xCpu? _cpu;
    private IZx8xIoCycleObserver? _primaryObserver;
    private IZx8xIoCycleObserver? _secondaryObserver;
    private Zx8xTapeSession? _tape;

    internal void AttachCpu(Zx8xCpu cpu)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
    }

    public void ConfigureObserver(IZx8xIoCycleObserver? observer)
    {
        ConfigureObservers(observer, null);
    }

    /// <summary>
    /// Connects the two timing-sensitive consumers of ZX8x I/O cycles without
    /// allocating an observer collection or enumerating one on every port access.
    /// </summary>
    public void ConfigureObservers(
        IZx8xIoCycleObserver? primaryObserver,
        IZx8xIoCycleObserver? secondaryObserver)
    {
        _primaryObserver = primaryObserver;
        _secondaryObserver = secondaryObserver;
    }

    /// <summary>
    /// Connects the cassette clock which must be caught up before an EAR sample
    /// is read, rather than only after the containing instruction completes.
    /// </summary>
    public void ConfigureTapeSession(Zx8xTapeSession? tape)
    {
        _tape = tape;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadUncontended(ushort port)
    {
        ulong tstate = _cpu?.Cyc ?? 0;
        _tape?.AdvanceTo(tstate);
        byte value = _io.ReadPort(port);
        _primaryObserver?.OnIoRead(tstate, port, value);
        _secondaryObserver?.OnIoRead(tstate, port, value);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUncontended(ushort port, byte value)
    {
        _io.WritePort(port, value);
        ulong tstate = _cpu?.Cyc ?? 0;
        _primaryObserver?.OnIoWrite(tstate, port, value);
        _secondaryObserver?.OnIoWrite(tstate, port, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ApplyIoContentionBeforeCycle(ushort port, int phase)
    {
        // ZX8x timing is driven by display/NMI events rather than Spectrum-style
        // contended I/O wait-state tables.
    }
}

/// <summary>Observes the exact sample/latch point of ZX8x I/O cycles.</summary>
public interface IZx8xIoCycleObserver
{
    void OnIoRead(ulong tstate, ushort port, byte value);
    void OnIoWrite(ulong tstate, ushort port, byte value);
}
