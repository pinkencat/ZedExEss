namespace ZedExEss.Zx8x.Core;

/// <summary>
/// One M1 opcode fetch from the A15-mirrored display-file address space.
/// </summary>
/// <param name="TState">Absolute T-state at the beginning of the M1 cycle.</param>
/// <param name="Address">CPU address whose A15-high read drove display generation.</param>
/// <param name="DisplayByte">Unmodified byte read from mirrored RAM.</param>
/// <param name="I">CPU I register used by character-generator addressing.</param>
/// <param name="R">CPU refresh register at the start of the fetch.</param>
public readonly record struct Zx8xDisplayFetch(
    ulong TState,
    ushort Address,
    byte DisplayByte,
    byte I,
    byte R)
{
    public byte CharacterCode => (byte)(DisplayByte & 0x3F);
    public bool Inverse => (DisplayByte & 0x80) != 0;
}

/// <summary>Receives display-file fetches only while a ZX8x renderer is attached.</summary>
public interface IZx8xDisplayFetchSink
{
    void OnDisplayFetch(in Zx8xDisplayFetch fetch);
}
