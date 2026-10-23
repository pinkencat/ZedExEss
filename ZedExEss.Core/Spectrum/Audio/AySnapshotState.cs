namespace ZedExEss.Spectrum.Audio
{
    /// <summary>
    /// Everything an <see cref="AY38912"/> is part way through.
    /// </summary>
    /// <remarks>
    /// The sixteen registers alone are not the chip's state. A PSG restored from registers
    /// only starts every tone at the beginning of its square wave, resets the noise shift
    /// register to its seed and puts the envelope back at step zero, so a sustained note
    /// audibly clicks and a noise channel repeats a pattern it had already played. The
    /// divider counters, square-wave phases, shift register and envelope position are
    /// carried here as well, which is what makes a restore continue the sound rather than
    /// restart it.
    ///
    /// The clock, sample rate and volume table are construction, not state: they describe
    /// which chip this is, and a restore puts a chip back rather than building a different
    /// one.
    /// </remarks>
    public sealed record AySnapshotState(
        byte[] Registers,
        double ClockAccumulator,
        int ToneDivCounter,
        int NoiseDivCounter,
        int EnvelopeDivCounter,
        int ToneCounterA,
        int ToneCounterB,
        int ToneCounterC,
        int PeriodA,
        int PeriodB,
        int PeriodC,
        int ToneOutputA,
        int ToneOutputB,
        int ToneOutputC,
        int NoiseCounter,
        int NoisePeriod,
        int NoiseRng,
        int NoiseOutput,
        int EnvelopeCounter,
        int EnvelopePeriod,
        int EnvelopeVolume,
        bool EnvelopeHold,
        bool EnvelopeAlternate,
        bool EnvelopeAttack,
        bool EnvelopeContinue,
        int EnvelopeStep,
        bool EnvelopeFirst,
        bool EnvelopeReverse);
}
