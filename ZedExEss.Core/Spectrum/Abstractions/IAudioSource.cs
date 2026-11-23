namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Pull-based PCM sample source used by the host audio device.
    /// </summary>
    /// <remarks>
    /// Samples are signed 16-bit. A source with more than one channel interleaves them a frame
    /// at a time (left then right), and <see cref="ReadSamples"/> counts samples, not frames:
    /// a stereo source is asked for, and returns, an even number.
    /// </remarks>
    public interface IAudioSource
    {
        /// <summary>Interleaved channels per frame: 1 (mono, the default) or 2 (stereo).</summary>
        int Channels => 1;

        int ReadSamples(short[] buffer, int offset, int count);
    }
}
