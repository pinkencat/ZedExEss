namespace ZedExEss.Z80CPU;

/// <summary>
/// Compile-time memory contract consumed by the generic Z80 core. Concrete,
/// sealed implementations allow the JIT to specialize and inline bus calls.
/// </summary>
public interface IZ80MemoryBus
{
    byte Read(ushort address);
    void WriteCpu(ushort address, byte value);
    byte FetchOpcode(ushort address);
}

/// <summary>Compile-time I/O contract consumed by the generic Z80 core.</summary>
public interface IZ80PortBus
{
    byte ReadUncontended(ushort port);
    void WriteUncontended(ushort port, byte value);
    void ApplyIoContentionBeforeCycle(ushort port, int phase);
}

/// <summary>
/// Optional machine bus hook for hardware driven by the Z80 refresh address.
/// Generic CPU specializations which do not implement it retain a folded-away
/// false branch in <c>IncR</c>.
/// </summary>
public interface IZ80RefreshObserver
{
    void OnRefreshRegisterLoaded(byte refreshRegister);
    void OnRefresh(byte refreshRegister);
}
