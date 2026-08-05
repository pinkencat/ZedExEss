namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Receives ULA beeper level changes at emulated time.
    /// </summary>
    public interface IBeeperSink
    {
        void SetLevel(bool high);
    }
}
