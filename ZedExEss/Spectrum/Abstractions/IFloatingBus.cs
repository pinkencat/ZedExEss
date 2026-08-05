namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Provides floating-bus values for unhandled port reads at a specific T-state.
    /// </summary>
    public interface IFloatingBus
    {
        byte Read(ushort port, ulong tstate);
    }
}
