namespace ZedExEss.Spectrum.Memory
{
    /// <summary>
    /// Which of the two ULA contention phases a machine models.
    /// </summary>
    /// <remarks>
    /// Real Spectrums differ by one T-state in when the ULA begins stealing the bus, and
    /// emulators differ in which they implement. <see cref="Early"/> is this emulator's
    /// own model, the one its timing tests are written against; <see cref="Late"/> exists
    /// to match software - or recordings - built around the other.
    /// </remarks>
    public enum SpectrumContentionTiming
    {
        /// <summary>The verified default.</summary>
        Early,

        /// <summary>The whole contention window one T-state later.</summary>
        Late
    }
}
