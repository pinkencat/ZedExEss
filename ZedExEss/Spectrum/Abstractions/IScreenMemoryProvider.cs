namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Read-only screen-memory view used by the ULA renderer and floating bus.
    /// </summary>
    public interface IScreenMemoryProvider
    {
        byte ReadScreen(ushort address);
    }
}
