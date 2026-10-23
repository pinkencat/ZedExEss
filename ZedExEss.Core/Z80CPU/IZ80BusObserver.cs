namespace ZedExEss.Z80CPU;

/// <summary>
/// Reports every bus cycle the CPU performs, in the order the Z80 performs them.
/// </summary>
public interface IZ80BusObserver
{
    /// <summary>The bus is held on <paramref name="address"/> for a number of T-states.</summary>
    void OnContend(ushort address, int cycles);

    void OnRead(ushort address, byte value);

    void OnWrite(ushort address, byte value);

    /// <summary>The I/O bus is held on <paramref name="port"/> for a number of T-states.</summary>
    void OnPortContend(ushort port, int cycles);

    void OnPortRead(ushort port, byte value);

    void OnPortWrite(ushort port, byte value);
}
