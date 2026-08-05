namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Pull-based PCM sample source used by the host audio device.
    /// </summary>
    public interface IAudioSource
    {
        int ReadSamples(short[] buffer, int offset, int count);
    }
}
