namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Port-mapped hardware device attached to the Spectrum I/O bus.
    /// </summary>
    public interface IPortDevice
    {
        bool HandlesPort(ushort port);
        byte Read(ushort port);
        void Write(ushort port, byte value);
    }
}
