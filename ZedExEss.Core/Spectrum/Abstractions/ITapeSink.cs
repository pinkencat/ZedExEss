namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Receives tape audio level changes for monitoring/loading sound.
    /// </summary>
    public interface ITapeSink
    {
        void SetLevel(bool high);
    }
}
