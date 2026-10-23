using System;
using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Audio
{
    /// <summary>
    /// Covox Speech Thing: an eight-bit parallel DAC mixed into the machine's audio.
    /// </summary>
    /// <remarks>
    /// The device holds no timing of its own. A write latches a level and the audio
    /// renderer integrates it over the T-states it was held for, exactly as it does the
    /// beeper, so a player writing several samples inside one host PCM sample keeps its
    /// waveform instead of being point-sampled.
    /// </remarks>
    public sealed class SpectrumCovoxDevice(SpectrumAudioRenderer audio) : IPortDevice
    {
        /// <summary>
        /// Pentagon and Scorpion machines carry the DAC on low byte $DD.
        /// </summary>
        /// <remarks>
        /// The port is decoded on the low byte alone, which is how the hardware is
        /// wired and how software addresses it. $DD is odd, so it cannot collide with
        /// the ULA, and it is outside the Beta 128 register block.
        /// </remarks>
        public const byte PortLowByte = 0xDD;

        private readonly SpectrumAudioRenderer _audio =
            audio ?? throw new ArgumentNullException(nameof(audio));

        public bool HandlesPort(ushort port) => (byte)port == PortLowByte;

        /// <summary>The DAC is write-only; a read leaves the bus alone.</summary>
        public byte Read(ushort port) => 0xFF;

        public void Write(ushort port, byte value) => _audio.SetCovoxLevel(value);
    }
}
