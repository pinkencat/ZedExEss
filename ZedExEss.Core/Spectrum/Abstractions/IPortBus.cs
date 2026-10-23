namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// CPU-facing I/O bus abstraction.
    /// </summary>
    public interface IPortBus
    {
        byte Read(ushort port);
        void Write(ushort port, byte value);
        void AddDevice(IPortDevice device);
    }
}
