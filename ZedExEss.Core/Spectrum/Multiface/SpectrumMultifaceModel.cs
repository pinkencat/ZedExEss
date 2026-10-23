namespace ZedExEss.Spectrum.Multiface
{
    /// <summary>
    /// Which Multiface variant is fitted, if any.
    /// </summary>
    /// <remarks>
    /// Each variant was built for a particular machine and decodes different ports, so
    /// they are mutually exclusive with each other, and with DivMMC, which competes for
    /// the same low-memory overlay.
    /// </remarks>
    public enum SpectrumMultifaceModel
    {
        None,

        /// <summary>Multiface One, for the 16K and 48K machines.</summary>
        One,

        /// <summary>Multiface 128, for the 128K and +2.</summary>
        OneTwentyEight,

        /// <summary>Multiface 3, for the +2A and +3.</summary>
        Three
    }
}
