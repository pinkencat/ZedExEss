namespace ZedExEss.Spectrum.Audio
{
    /// <summary>An audio block that can publish source-separated samples to the oscilloscope.</summary>
    /// <remarks>
    /// Lets one oscilloscope window follow whichever machine family is running without
    /// knowing how that family generates or mixes its audio.
    /// </remarks>
    public interface IAudioScopeSource
    {
        /// <summary>Installs the capture sink, or removes it with null.</summary>
        void SetScopeCapture(AudioScopeCapture? capture);

        /// <summary>The channel set this source publishes.</summary>
        AudioScopeLayout ScopeLayout { get; }
    }
}
