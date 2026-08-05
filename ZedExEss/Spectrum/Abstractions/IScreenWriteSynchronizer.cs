namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Flushes delayed screen writes up to an emulated T-state.
    /// </summary>
    public interface IScreenWriteSynchronizer
    {
        void FlushPendingScreenWrites(ulong tstates);
    }
}
