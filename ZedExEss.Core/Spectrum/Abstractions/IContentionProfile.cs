namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Supplies model-specific memory and I/O contention delays.
    /// </summary>
    public interface IContentionProfile
    {
        int TstatesPerFrame { get; }
        int GetMemoryDelay(ulong tstate);
        int GetNoMreqDelay(ulong tstate);
        bool IsUlaPort(ushort port);
    }
}
