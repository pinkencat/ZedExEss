namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// CPU-facing memory bus abstraction.
    /// </summary>
    public interface IMemoryBus
    {
        byte Read(ushort address);
        void Write(ushort address, byte value);
    }
}
