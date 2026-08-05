namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Receives the current tape EAR input level for audio mixing.
    /// </summary>
    public interface IEarInputSink
    {
        void SetEarLevel(bool high);
    }
}
