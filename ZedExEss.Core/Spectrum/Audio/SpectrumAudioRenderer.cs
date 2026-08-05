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
    public sealed class SpectrumAudioRenderer : IBeeperSink, ITapeSink, IEarInputSink
    {
        private const short DefaultAmplitude = 3000;
        private const short DefaultTapeAmplitude = 1500;
        private readonly int _sampleRate;
        private readonly int _cpuHz;
        private readonly short _beeperAmplitude;
        private readonly short _tapeAmplitude;
        private long _sampleRemainder;
        private long _ulaAudioSum;
        private int _ulaAudioWeight;
        private long _scopeBeeperSum;
        private int _scopeBeeperWeight;
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

        public SpectrumAudioRenderer(int cpuHz, int sampleRate, short amplitude = DefaultAmplitude, short tapeAmplitude = DefaultTapeAmplitude)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cpuHz);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

            _cpuHz = cpuHz;
            _sampleRate = sampleRate;
            _beeperAmplitude = amplitude;
            _tapeAmplitude = tapeAmplitude;
        }
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
            short sampleValue = MixSample();
            ResetUlaAudioAccumulator();
            ResetScopeBeeperAccumulator();

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

                scope?.WriteSamples(scopeBeeper, ReadOnlySpan<short>.Empty, ReadOnlySpan<short>.Empty, ReadOnlySpan<short>.Empty, samples);
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
                scope.WriteSamples(scopeBeeper, _ayScopeA.AsSpan(0, samples), _ayScopeB.AsSpan(0, samples), _ayScopeC.AsSpan(0, samples), samples);
            }

            return CopySamples(_ayMixBuffer, 0, samples, buffer, offset, count);
        }
        private short MixSample()
        {
            int value = GetAveragedUlaAudio();
            value += _tapeInHigh ? _tapeAmplitude : -_tapeAmplitude;

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
        private short GetAveragedScopeBeeperAudio()
        {
            if (_scopeBeeperWeight <= 0)
            {
                return (short)GetCurrentBeeperAudio();
            }

            return (short)(_scopeBeeperSum / _scopeBeeperWeight);
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
