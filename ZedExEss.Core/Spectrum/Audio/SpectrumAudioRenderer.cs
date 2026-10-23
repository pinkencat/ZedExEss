using System;using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Audio
{
    /// <summary>
    /// Mixes beeper, tape monitor and optional AY output into host PCM samples.
    /// </summary>
    /// <remarks>
    /// ULA square-wave levels are integrated over every emulated T-state covered by
    /// a host sample. This preserves edge position and reduces aliasing without
    /// smoothing the source waveform. AY runs in its own chip-clock domain and is
    /// mixed after sample generation.
    /// </remarks>
    public sealed class SpectrumAudioRenderer : IBeeperSink, ITapeSink, IEarInputSink, IAudioScopeSource
    {
        private const short DefaultAmplitude = 3000;
        private const short DefaultTapeAmplitude = 1500;
        // A Covox is a full-scale DAC driven by sample playback, so it is louder than
        // the square-wave beeper it sits alongside.
        private const short DefaultCovoxAmplitude = 6000;
        private const byte CovoxSilenceLevel = 0x80;
        private readonly int _sampleRate;
        private int _cpuHz;
        private readonly short _beeperAmplitude;
        private readonly short _tapeAmplitude;
        private readonly short _covoxAmplitude;
        private long _sampleRemainder;
        private long _ulaAudioSum;
        private int _ulaAudioWeight;
        private long _scopeBeeperSum;
        private int _scopeBeeperWeight;
        private long _covoxSum;
        private int _covoxWeight;
        private byte _covoxLevel = CovoxSilenceLevel;
        private bool _covoxAttached;
        private GeneralSound.GeneralSoundCard? _generalSound;
        private long _generalSoundSum;
        private int _generalSoundWeight;
        private bool _beeperHigh;
        private bool _tapeOutHigh;
        private bool _tapeInHigh;
        private AY38912? _ay;
        private AudioScopeCapture? _scopeCapture;
        private short[] _ayMixBuffer = [];
        private short[] _ayScopeA = [];
        private short[] _ayScopeB = [];
        private short[] _ayScopeC = [];

        private short[] _spill = new short[64];
        private int _spillOffset;
        private int _spillCount;

        public SpectrumAudioRenderer(
            int cpuHz,
            int sampleRate,
            short amplitude = DefaultAmplitude,
            short tapeAmplitude = DefaultTapeAmplitude,
            short covoxAmplitude = DefaultCovoxAmplitude)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cpuHz);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

            _cpuHz = cpuHz;
            _sampleRate = sampleRate;
            _beeperAmplitude = amplitude;
            _tapeAmplitude = tapeAmplitude;
            _covoxAmplitude = covoxAmplitude;
        }

        /// <summary>
        /// True when a Covox is fitted. While it is not, the DAC contributes nothing and
        /// a machine without one mixes exactly as it did before.
        /// </summary>
        public bool CovoxAttached => _covoxAttached;

        /// <summary>
        /// Declares that a Covox is fitted, before any software has written to it.
        /// </summary>
        /// <remarks>
        /// Kept separate from the first write so the oscilloscope's channel set is
        /// settled when the machine is built rather than appearing partway through.
        /// </remarks>
        public void AttachCovox()
        {
            _covoxAttached = true;
        }

        /// <summary>Latches the DAC level written to the Covox port.</summary>
        public void SetCovoxLevel(byte level)
        {
            _covoxLevel = level;
        }
        /// <summary>
        /// Retunes the renderer when the CPU clock changes, as a turbo clone's does.
        /// </summary>
        /// <remarks>
        /// Sample generation is driven by CPU T-states, so a faster CPU would otherwise
        /// produce proportionally more samples per second and raise the pitch of
        /// everything. The AY keeps its own crystal and is unaffected.
        /// </remarks>
        public void SetCpuClockHz(int cpuHz)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cpuHz);
            if (cpuHz == _cpuHz)
            {
                return;
            }

            _cpuHz = cpuHz;
            _sampleRemainder = 0;
        }

        public int CpuClockHz => _cpuHz;

        public void SetLevel(bool high)
        {
            _beeperHigh = high;
        }

        void ITapeSink.SetLevel(bool high)
        {
            _tapeOutHigh = high;
        }

        void IEarInputSink.SetEarLevel(bool high)
        {
            _tapeInHigh = high;
        }
        public void AttachAy(AY38912? ay)
        {
            _ay = ay;
        }
        public void SetScopeCapture(AudioScopeCapture? capture)
        {
            _scopeCapture = capture;
        }

        public AudioScopeLayout ScopeLayout =>
            AudioScopeLayout.ForSpectrum(_covoxAttached, _generalSound != null);

        /// <summary>Mixes a General Sound card's four channels into the host output.</summary>
        /// <remarks>
        /// The card generates on its own clock, so its level is integrated over the
        /// T-states it was held for in the same way as the beeper and the Covox rather
        /// than being sampled once per host sample.
        /// </remarks>
        public void AttachGeneralSound(GeneralSound.GeneralSoundCard? card)
        {
            _generalSound = card;
            _generalSoundSum = 0;
            _generalSoundWeight = 0;
        }
        public int DrainSpill(short[] buffer, int offset, int count)
        {
            if (_spillCount == 0 || count <= 0)
            {
                return 0;
            }

            int toCopy = Math.Min(count, _spillCount);
            Array.Copy(_spill, _spillOffset, buffer, offset, toCopy);
            _spillOffset += toCopy;
            _spillCount -= toCopy;
            if (_spillCount == 0)
            {
                _spillOffset = 0;
            }

            return toCopy;
        }
        public int RenderSamples(int tstates, short[] buffer, int offset, int count)
        {
            if (tstates <= 0 || count <= 0)
            {
                return 0;
            }

            AccumulateUlaAudio(tstates);

            _sampleRemainder += (long)tstates * _sampleRate;
            int samples = (int)(_sampleRemainder / _cpuHz);
            _sampleRemainder %= _cpuHz;

            if (samples <= 0)
            {
                return 0;
            }

            AudioScopeCapture? scope = _scopeCapture;
            short scopeBeeper = scope != null ? GetAveragedScopeBeeperAudio() : (short)0;
            AudioScopeLayout scopeLayout = ScopeLayout;
            short scopeCovox = scope != null && _covoxAttached
                ? (short)Math.Clamp(GetAveragedCovoxAudio(), short.MinValue, short.MaxValue)
                : (short)0;
            short sampleValue = MixSample();
            ResetUlaAudioAccumulator();
            ResetScopeBeeperAccumulator();
            ResetCovoxAccumulator();
            ResetGeneralSoundAccumulator();

            if (_ay == null)
            {
                int toWrite = Math.Min(samples, count);
                if (toWrite > 0)
                {
                    Array.Fill(buffer, sampleValue, offset, toWrite);
                }

                int remaining = samples - toWrite;
                if (remaining > 0)
                {
                    EnsureSpillCapacity(remaining);
                    Array.Fill(_spill, sampleValue, _spillOffset + _spillCount, remaining);
                    _spillCount += remaining;
                }

                if (scope != null)
                {
                    using AudioScopeBlock block = scope.BeginBlock(samples);
                    block.Fill(AudioScopeLayout.BeeperChannelIndex, scopeBeeper);
                    PublishExpansionScopeChannels(block, scopeLayout, scopeCovox);
                }

                return toWrite;
            }

            EnsureAyMixBuffer(samples);
            if (scope != null)
            {
                EnsureAyScopeBuffers(samples);
                _ay.RenderSamples(samples, _ayMixBuffer, 0, _ayScopeA, _ayScopeB, _ayScopeC);
            }
            else
            {
                _ay.RenderSamples(samples, _ayMixBuffer, 0);
            }

            for (int i = 0; i < samples; i++)
            {
                int mixed = sampleValue + _ayMixBuffer[i];
                if (mixed > short.MaxValue)
                {
                    mixed = short.MaxValue;
                }
                else if (mixed < short.MinValue)
                {
                    mixed = short.MinValue;
                }

                _ayMixBuffer[i] = (short)mixed;
            }

            if (scope != null)
            {
                using AudioScopeBlock block = scope.BeginBlock(samples);
                block.Fill(AudioScopeLayout.BeeperChannelIndex, scopeBeeper);
                block.Write(AudioScopeLayout.GetPsgChannelIndex(0, 0), _ayScopeA.AsSpan(0, samples));
                block.Write(AudioScopeLayout.GetPsgChannelIndex(0, 1), _ayScopeB.AsSpan(0, samples));
                block.Write(AudioScopeLayout.GetPsgChannelIndex(0, 2), _ayScopeC.AsSpan(0, samples));
                PublishExpansionScopeChannels(block, scopeLayout, scopeCovox);
            }

            return CopySamples(_ayMixBuffer, 0, samples, buffer, offset, count);
        }
        private short MixSample()
        {
            int value = GetAveragedUlaAudio();
            value += _tapeInHigh ? _tapeAmplitude : -_tapeAmplitude;
            if (_covoxAttached)
            {
                value += GetAveragedCovoxAudio();
            }

            if (_generalSound != null)
            {
                value += GetAveragedGeneralSoundAudio();
            }

            if (value > short.MaxValue)
            {
                return short.MaxValue;
            }

            if (value < short.MinValue)
            {
                return short.MinValue;
            }

            return (short)value;
        }
        private void AccumulateUlaAudio(int tstates)
        {
            // Weight by elapsed emulated time rather than sampling only the final
            // level; multiple beeper transitions can occur inside one PCM sample.
            _ulaAudioSum += (long)GetCurrentUlaAudio() * tstates;
            _ulaAudioWeight += tstates;

            if (_covoxAttached)
            {
                _covoxSum += (long)GetCurrentCovoxAudio() * tstates;
                _covoxWeight += tstates;
            }

            GeneralSound.GeneralSoundCard? generalSound = _generalSound;
            if (generalSound != null)
            {
                _generalSoundSum += (long)generalSound.GetMixedOutput() * tstates;
                _generalSoundWeight += tstates;
            }

            if (_scopeCapture != null)
            {
                _scopeBeeperSum += (long)GetCurrentBeeperAudio() * tstates;
                _scopeBeeperWeight += tstates;
            }
        }
        private int GetAveragedUlaAudio()
        {
            if (_ulaAudioWeight <= 0)
            {
                return GetCurrentUlaAudio();
            }

            return (int)(_ulaAudioSum / _ulaAudioWeight);
        }
        private int GetCurrentUlaAudio()
        {
            int value = 0;
            value += GetCurrentBeeperAudio();
            value += _tapeOutHigh ? _tapeAmplitude : -_tapeAmplitude;
            return value;
        }
        private int GetCurrentBeeperAudio()
        {
            return _beeperHigh ? _beeperAmplitude : -_beeperAmplitude;
        }
        private int GetAveragedCovoxAudio()
        {
            return _covoxWeight <= 0
                ? GetCurrentCovoxAudio()
                : (int)(_covoxSum / _covoxWeight);
        }
        private int GetCurrentCovoxAudio()
        {
            // Unsigned eight-bit DAC: $80 is the resting mid-point, so silence stays at
            // zero rather than adding a constant offset to everything else in the mix.
            return ((_covoxLevel - CovoxSilenceLevel) * _covoxAmplitude) / 128;
        }
        /// <summary>Publishes the fitted expansions' traces at the layout's own indices.</summary>
        private void PublishExpansionScopeChannels(
            AudioScopeBlock block,
            AudioScopeLayout layout,
            short scopeCovox)
        {
            if (_covoxAttached && layout.CovoxChannelIndex >= 0)
            {
                block.Fill(layout.CovoxChannelIndex, scopeCovox);
            }

            GeneralSound.GeneralSoundCard? generalSound = _generalSound;
            if (generalSound == null || layout.FirstGeneralSoundChannelIndex < 0)
            {
                return;
            }

            for (int channel = 0; channel < GeneralSound.GeneralSoundCard.ChannelCount; channel++)
            {
                block.Fill(
                    layout.FirstGeneralSoundChannelIndex + channel,
                    generalSound.GetChannelLevel(channel));
            }
        }
        private short GetAveragedScopeBeeperAudio()
        {
            if (_scopeBeeperWeight <= 0)
            {
                return (short)GetCurrentBeeperAudio();
            }

            return (short)(_scopeBeeperSum / _scopeBeeperWeight);
        }
        private void ResetCovoxAccumulator()
        {
            _covoxSum = 0;
            _covoxWeight = 0;
        }
        private int GetAveragedGeneralSoundAudio()
        {
            if (_generalSoundWeight > 0)
            {
                return (int)(_generalSoundSum / _generalSoundWeight);
            }

            return _generalSound?.GetMixedOutput() ?? 0;
        }
        private void ResetGeneralSoundAccumulator()
        {
            _generalSoundSum = 0;
            _generalSoundWeight = 0;
        }
        private void ResetUlaAudioAccumulator()
        {
            _ulaAudioSum = 0;
            _ulaAudioWeight = 0;
        }
        private void ResetScopeBeeperAccumulator()
        {
            _scopeBeeperSum = 0;
            _scopeBeeperWeight = 0;
        }
        private int CopySamples(short[] source, int sourceOffset, int sampleCount, short[] buffer, int offset, int count)
        {
            int toWrite = Math.Min(sampleCount, count);
            if (toWrite > 0)
            {
                Array.Copy(source, sourceOffset, buffer, offset, toWrite);
            }

            int remaining = sampleCount - toWrite;
            if (remaining > 0)
            {
                EnsureSpillCapacity(remaining);
                Array.Copy(source, sourceOffset + toWrite, _spill, _spillOffset + _spillCount, remaining);
                _spillCount += remaining;
            }

            return toWrite;
        }
        private void EnsureAyMixBuffer(int sampleCount)
        {
            if (_ayMixBuffer.Length >= sampleCount)
            {
                return;
            }

            _ayMixBuffer = new short[sampleCount];
        }
        private void EnsureAyScopeBuffers(int sampleCount)
        {
            if (_ayScopeA.Length >= sampleCount)
            {
                return;
            }

            _ayScopeA = new short[sampleCount];
            _ayScopeB = new short[sampleCount];
            _ayScopeC = new short[sampleCount];
        }
        private void EnsureSpillCapacity(int extra)
        {
            int available = _spill.Length - (_spillOffset + _spillCount);
            if (available >= extra)
            {
                return;
            }

            int newSize = _spill.Length;
            while (newSize - _spillCount < extra)
            {
                newSize *= 2;
            }

            var next = new short[newSize];
            if (_spillCount > 0)
            {
                Array.Copy(_spill, _spillOffset, next, 0, _spillCount);
            }

            _spill = next;
            _spillOffset = 0;
        }
    }
}
