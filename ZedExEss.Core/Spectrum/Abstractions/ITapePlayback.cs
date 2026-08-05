namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// T-state stepped tape playback source.
    /// </summary>
    public interface ITapePlayback
    {
        void Step(int tstates);
    }
}
