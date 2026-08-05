using System;

namespace ZedExEss.Spectrum.Audio
{
    /// <summary>
    /// Clocked AY-3-8912 emulation with three tone channels, shared noise and shared envelope generators.
    /// </summary>
    /// <remarks>
    /// Chip clocks are accumulated independently of the host sample rate. Tone
    /// counters tick at clock/8; noise and envelope counters at clock/16. Mixer
    /// disable bits force their gate high, matching the AY's AND-gate topology and
    /// allowing tone-only or noise-only output.
    /// </remarks>
    public class AY38912
    {
        private const short DefaultOutputAmplitude = 4500;
        private const int MaxVolume = 111360;
        private static readonly int[] VolumeTable =
        [
            0, 836, 1212, 1755, 2470, 3564, 5035, 7033,
            10045, 14021, 20042, 28260, 40267, 56354, 80412, 111360
        ];
        private static readonly byte[] RegisterMasks =
        [
            0xFF, 0x0F, 0xFF, 0x0F, 0xFF, 0x0F, 0x1F, 0xFF,
            0x1F, 0x1F, 0x1F, 0xFF, 0xFF, 0x0F, 0xFF, 0xFF
        ];

        // Spectrum 128K AY clock (half the nominal CPU clock).
        public const int ClockFrequency = 1773400;

        // The 8912 exposes one I/O port, but retaining all 16 register slots keeps
        // register addressing compatible with the 8910 family.
        private readonly byte[] _registers = new byte[16];
        private readonly int _clockFrequency;
        private readonly int _sampleRate;
        private readonly double _clocksPerSample;
        private readonly short[] _scaledVolumeTable;
        private double _clockAccumulator;

        private int _toneDivCounter;
        private int _noiseDivCounter;
        private int _envelopeDivCounter;

        // Internal elapsed tone ticks for each channel.
        private int _toneCounterA;
        private int _toneCounterB;
        private int _toneCounterC;

        // Effective 12-bit tone periods; register value zero is normalised to one.
        private int _periodA;
        private int _periodB;
        private int _periodC;

        // Current square-wave phase for each independent tone generator.
        private int _toneOutputA;
        private int _toneOutputB;
        private int _toneOutputC;

        // State shared by the three noise mixer inputs.
        private int _noiseCounter;
        private int _noisePeriod;
        private int _noiseRng = 1;
        private int _noiseOutput;

        // State shared by channels whose amplitude register selects envelope mode.
        private int _envelopeCounter;
        private int _envelopePeriod;
        private int _envelopeVolume; // 0..15
        private bool _envelopeHold;
        private bool _envelopeAlternate;
        private bool _envelopeAttack;
        private bool _envelopeContinue;
        private int _envelopeStep;
        private bool _envelopeFirst;
        private bool _envelopeReverse;

        public AY38912(int clockFrequency = ClockFrequency, int sampleRate = SpectrumAudioTiming.DefaultSampleRate, short outputAmplitude = DefaultOutputAmplitude)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clockFrequency);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

            _clockFrequency = clockFrequency;
            _sampleRate = sampleRate;
            _clocksPerSample = (double)clockFrequency / sampleRate;
            _scaledVolumeTable = BuildScaledVolumeTable(outputAmplitude);
            Reset();
        }

        public void WriteRegister(int register, byte value)
        {
            if (register < 0 || register >= 16) return;
            _registers[register] = (byte)(value & RegisterMasks[register]);
            OnRegisterWritten(register);
        }

        /// <summary>Reads a masked register value, including the 8912's single I/O-port direction.</summary>
        public byte ReadRegister(int register)
        {
            if (register < 0 || register >= 16)
                return 0;

            const byte portInput = 0xBF;

            if (register == 14)
            {
                return (_registers[7] & 0x40) != 0 ? (byte)(portInput & _registers[14]) : portInput;
            }

            if (register == 15 && (_registers[7] & 0x80) == 0)
            {
                return 0xFF;
            }

            return (byte)(_registers[register] & RegisterMasks[register]);
        }

        /// <summary>Generates an interleaved stereo buffer containing the mono AY output.</summary>
        public short[] GenerateSamples(int sampleRate, int numSamples)
        {
            short[] buffer = new short[numSamples * 2];

            for (int i = 0; i < numSamples; i++)
            {
                _clockAccumulator += (double)_clockFrequency / sampleRate;

                int clocks = (int)_clockAccumulator;
                if (clocks > 0)
                {
                    UpdateChip(clocks);
                    _clockAccumulator -= clocks;
                }

                // The 8912 output is mono; duplicate its pin output into the host stereo stream.
                short pcmValue = MixChannels();
                buffer[(i * 2) + 0] = pcmValue;
                buffer[(i * 2) + 1] = pcmValue;
            }

            return buffer;
        }
        public void RenderSamples(int numSamples, short[] buffer, int offset)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            if (offset < 0 || numSamples < 0 || offset + numSamples > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(numSamples));
            }

            for (int i = 0; i < numSamples; i++)
            {
                _clockAccumulator += _clocksPerSample;
                int clocks = (int)_clockAccumulator;
                if (clocks > 0)
                {
                    UpdateChip(clocks);
                    _clockAccumulator -= clocks;
                }

                buffer[offset + i] = MixChannels();
            }
        }
        public void RenderSamples(int numSamples, short[] buffer, int offset, short[] channelA, short[] channelB, short[] channelC)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentNullException.ThrowIfNull(channelA);
            ArgumentNullException.ThrowIfNull(channelB);
            ArgumentNullException.ThrowIfNull(channelC);

            if (offset < 0 ||
                numSamples < 0 ||
                offset + numSamples > buffer.Length ||
                offset + numSamples > channelA.Length ||
                offset + numSamples > channelB.Length ||
                offset + numSamples > channelC.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(numSamples));
            }

            for (int i = 0; i < numSamples; i++)
            {
                _clockAccumulator += _clocksPerSample;
                int clocks = (int)_clockAccumulator;
                if (clocks > 0)
                {
                    UpdateChip(clocks);
                    _clockAccumulator -= clocks;
                }

                buffer[offset + i] = MixChannels(out short a, out short b, out short c);
                channelA[offset + i] = a;
                channelB[offset + i] = b;
                channelC[offset + i] = c;
            }
        }
        public void Reset()
        {
            Array.Clear(_registers, 0, _registers.Length);
            _clockAccumulator = 0.0;
            _toneDivCounter = 0;
            _noiseDivCounter = 0;
            _envelopeDivCounter = 0;
            _toneCounterA = 0;
            _toneCounterB = 0;
            _toneCounterC = 0;
            _toneOutputA = 0;
            _toneOutputB = 0;
            _toneOutputC = 0;
            _noiseCounter = 1;
            _noisePeriod = 1;
            _noiseRng = 1;
            _noiseOutput = 0;
            _envelopeCounter = 1;
            _envelopePeriod = 1;
            _envelopeVolume = 15;
            _envelopeContinue = false;
            _envelopeAttack = false;
            _envelopeAlternate = false;
            _envelopeHold = false;
            _envelopeFirst = true;
            _envelopeReverse = false;
            _envelopeStep = 0;
        }

        private void OnRegisterWritten(int register)
        {
            switch (register)
            {
                case 0: // Channel A Fine Tune
                case 1: // Channel A Coarse Tune
                    _periodA = ((_registers[1] & 0x0F) << 8) | _registers[0];
                    // Hardware treats period zero as one rather than stopping the generator.
                    if (_periodA == 0) _periodA = 1;
                    if (_toneCounterA >= _periodA * 2) _toneCounterA %= _periodA * 2;
                    break;

                case 2: // Channel B Fine Tune
                case 3: // Channel B Coarse Tune
                    _periodB = ((_registers[3] & 0x0F) << 8) | _registers[2];
                    if (_periodB == 0) _periodB = 1;
                    if (_toneCounterB >= _periodB * 2) _toneCounterB %= _periodB * 2;
                    break;

                case 4: // Channel C Fine Tune
                case 5: // Channel C Coarse Tune
                    _periodC = ((_registers[5] & 0x0F) << 8) | _registers[4];
                    if (_periodC == 0) _periodC = 1;
                    if (_toneCounterC >= _periodC * 2) _toneCounterC %= _periodC * 2;
                    break;

                case 6: // Noise Period
                    _noisePeriod = _registers[6] & 0x1F;
                    if (_noisePeriod == 0) _noisePeriod = 1;
                    _noiseDivCounter = 0;
                    _noiseCounter = _noisePeriod;
                    break;

                case 11: // Envelope Fine
                case 12: // Envelope Coarse
                    _envelopePeriod = (_registers[12] << 8) | _registers[11];
                    if (_envelopePeriod == 0) _envelopePeriod = 1;
                    if (_envelopeCounter <= 0 || _envelopeCounter > _envelopePeriod) _envelopeCounter = _envelopePeriod;
                    break;

                case 13: // Envelope Shape
                    UpdateEnvelopeControl();
                    break;

                default:
                    // Mixer and volume registers are sampled directly by MixChannels.
                    break;
            }
        }

        private void UpdateEnvelopeControl()
        {
            // Register 13 is CONT/ATTACK/ALTERNATE/HOLD from bit 3 to bit 0. A write
            // restarts the envelope even when the shape value itself did not change.
            byte shape = _registers[13];
            _envelopeContinue = (shape & 0x08) != 0;
            _envelopeAttack = (shape & 0x04) != 0;
            _envelopeAlternate = (shape & 0x02) != 0;
            _envelopeHold = (shape & 0x01) != 0;

            _envelopeStep = 0;
            _envelopeVolume = _envelopeAttack ? 0 : 15;
            _envelopeFirst = true;
            _envelopeReverse = false;
            _envelopeCounter = _envelopePeriod == 0 ? 1 : _envelopePeriod;
            _envelopeDivCounter = 0;
        }

        private void UpdateChip(int clocks)
        {
            if (clocks <= 0)
            {
                return;
            }

            int toneTicks = AdvanceDivider(ref _toneDivCounter, clocks, 8);
            if (toneTicks > 0)
            {
                AdvanceTone(ref _toneCounterA, ref _toneOutputA, _periodA, toneTicks);
                AdvanceTone(ref _toneCounterB, ref _toneOutputB, _periodB, toneTicks);
                AdvanceTone(ref _toneCounterC, ref _toneOutputC, _periodC, toneTicks);
            }

            int noiseTicks = AdvanceDivider(ref _noiseDivCounter, clocks, 16);
            if (noiseTicks > 0)
            {
                AdvanceNoise(noiseTicks);
            }

            int envelopeTicks = AdvanceDivider(ref _envelopeDivCounter, clocks, 16);
            if (envelopeTicks > 0)
            {
                AdvanceEnvelope(envelopeTicks);
            }
        }
        private static int AdvanceDivider(ref int counter, int clocks, int divisor)
        {
            int total = counter + clocks;
            counter = total % divisor;
            return total / divisor;
        }
        private static void AdvanceTone(ref int toneCounter, ref int toneOutput, int period, int ticks)
        {
            if (ticks <= 0)
            {
                return;
            }

            if (period <= 0)
            {
                period = 1;
            }

            toneCounter += ticks;
            while (toneCounter >= period)
            {
                toneCounter -= period;
                toneOutput ^= 1; // Toggle between 0 and 1
            }
        }
        private void AdvanceNoise(int ticks)
        {
            if (ticks <= 0)
            {
                return;
            }

            int period = _noisePeriod <= 0 ? 1 : _noisePeriod;
            if (_noiseCounter <= 0)
            {
                _noiseCounter = period;
            }

            while (ticks >= _noiseCounter)
            {
                ticks -= _noiseCounter;
                _noiseCounter = period;

                // AY noise uses a 17-bit LFSR. The output transition and feedback
                // are derived from its low bits; 0x24000 represents the x^17+x^14+1
                // feedback taps in this right-shifting representation.
                if (((_noiseRng & 0x01) ^ ((_noiseRng & 0x02) != 0 ? 1 : 0)) != 0)
                {
                    _noiseOutput ^= 1;
                }

                if ((_noiseRng & 0x01) != 0)
                {
                    _noiseRng ^= 0x24000;
                }

                _noiseRng >>= 1;
            }

            _noiseCounter -= ticks;
        }
        private void AdvanceEnvelope(int ticks)
        {
            if (ticks <= 0 || _envelopePeriod <= 0)
            {
                return;
            }

            if (_envelopeCounter <= 0)
            {
                _envelopeCounter = _envelopePeriod;
            }

            while (ticks >= _envelopeCounter)
            {
                ticks -= _envelopeCounter;
                _envelopeCounter = _envelopePeriod;
                AdvanceEnvelope();
            }

            _envelopeCounter -= ticks;
        }
        private void AdvanceEnvelope()
        {
            if (_envelopeFirst || (_envelopeContinue && !_envelopeHold))
            {
                int delta = _envelopeAttack ? 1 : -1;
                _envelopeVolume += _envelopeReverse ? -delta : delta;

                if (_envelopeVolume > 15)
                {
                    _envelopeVolume = 15;
                }
                else if (_envelopeVolume < 0)
                {
                    _envelopeVolume = 0;
                }
            }

            _envelopeStep++;
            if (_envelopeStep < 16)
            {
                return;
            }

            _envelopeStep = 0;

            if (!_envelopeContinue)
            {
                _envelopeVolume = 0;
            }
            else if (_envelopeHold)
            {
                if (_envelopeFirst && _envelopeAlternate)
                {
                    _envelopeVolume = _envelopeVolume != 0 ? 0 : 15;
                }
            }
            else
            {
                if (_envelopeAlternate)
                {
                    _envelopeReverse = !_envelopeReverse;
                }
                else
                {
                    _envelopeVolume = _envelopeAttack ? 0 : 15;
                }
            }

            _envelopeFirst = false;
        }

        private short MixChannels()
        {
            return MixChannels(out _, out _, out _);
        }
        private short MixChannels(out short channelASample, out short channelBSample, out short channelCSample)
        {
            // Register 7 bits 0-2 disable tone A-C; bits 3-5 disable noise A-C.
            byte mixer = _registers[7];

            // Register bit 4 selects the shared envelope in place of fixed volume.
            int volumeA = _registers[8] & 0x0F;
            bool envA = (_registers[8] & 0x10) != 0;
            int volumeB = _registers[9] & 0x0F;
            bool envB = (_registers[9] & 0x10) != 0;
            int volumeC = _registers[10] & 0x0F;
            bool envC = (_registers[10] & 0x10) != 0;

            int envVol = _envelopeVolume;

            // Disable is active-high on the register but forces the corresponding
            // internal mixer gate high; MixChannel therefore only rejects an
            // output when an enabled source is currently low.
            bool toneADisable = ((mixer & 0x01) != 0);
            bool toneBDisable = ((mixer & 0x02) != 0);
            bool toneCDisable = ((mixer & 0x04) != 0);
            bool noiseADisable = ((mixer & 0x08) != 0);
            bool noiseBDisable = ((mixer & 0x10) != 0);
            bool noiseCDisable = ((mixer & 0x20) != 0);

            int channelA = MixChannel(toneADisable, noiseADisable, _toneOutputA, envA ? envVol : volumeA);
            int channelB = MixChannel(toneBDisable, noiseBDisable, _toneOutputB, envB ? envVol : volumeB);
            int channelC = MixChannel(toneCDisable, noiseCDisable, _toneOutputC, envC ? envVol : volumeC);
            channelASample = (short)channelA;
            channelBSample = (short)channelB;
            channelCSample = (short)channelC;

            int sum = channelA + channelB + channelC;
            if (sum > short.MaxValue)
            {
                return short.MaxValue;
            }

            if (sum < short.MinValue)
            {
                return short.MinValue;
            }

            return (short)sum;
        }
        private int MixChannel(bool toneDisabled, bool noiseDisabled, int toneOutput, int volumeIndex)
        {
            int amp = _scaledVolumeTable[volumeIndex & 0x0F];

            if (!toneDisabled && toneOutput == 0)
            {
                return 0;
            }

            if (!noiseDisabled && _noiseOutput != 0)
            {
                return 0;
            }

            return amp;
        }
        private static short[] BuildScaledVolumeTable(short outputAmplitude)
        {
            var table = new short[VolumeTable.Length];
            int divisor = MaxVolume;
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = (short)((VolumeTable[i] * outputAmplitude) / divisor);
            }

            return table;
        }
    }
}

