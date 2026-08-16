using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Audio;

/// <summary>
/// Compresses a mono PCM stream in time while retaining its approximate pitch.
/// Pulling one host sample advances the wrapped emulator by <see cref="SpeedMultiplier"/>
/// source samples, making it suitable as the audio clock for fast-forward execution.
/// </summary>
/// <remarks>
/// The implementation uses waveform-similarity overlap/add (WSOLA). Consecutive
/// half-window grains are selected around the requested analysis position and
/// cross-faded at the closest waveform match. This deliberately favours a small,
/// allocation-free streaming implementation over a heavyweight FFT dependency.
/// At the highest multipliers some musical detail is necessarily skipped, but
/// pitches remain recognisable and transitions do not become simple high-pitched
/// resampling artefacts.
/// </remarks>
public sealed class TimeStretchAudioSource : IAudioSource
{
    public const int MinimumSpeedMultiplier = 2;
    public const int MaximumSpeedMultiplier = 10;

    private const int MinimumWindowLength = 256;
    private const int MaximumWindowLength = 2048;
    private const int CoarseCorrelationStride = 4;
    private const int CoarseCandidateStride = 4;

    private readonly IAudioSource _source;
    private readonly int _windowLength;
    private readonly int _hopLength;
    private readonly int _searchRadius;
    private readonly short[] _previousTail;
    private readonly short[] _sourceReadBuffer;

    private short[] _input = new short[16_384];
    private long _inputStart;
    private int _inputCount;

    private short[] _pendingOutput = new short[4_096];
    private int _pendingOffset;
    private int _pendingCount;

    private long _nextAnalysisStart;
    private bool _started;

    public TimeStretchAudioSource(IAudioSource source, int sampleRate, int speedMultiplier)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (speedMultiplier is < MinimumSpeedMultiplier or > MaximumSpeedMultiplier)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speedMultiplier),
                speedMultiplier,
                $"Fast-forward speed must be between {MinimumSpeedMultiplier}x and {MaximumSpeedMultiplier}x.");
        }

        SpeedMultiplier = speedMultiplier;
        _windowLength = SelectWindowLength(sampleRate);
        _hopLength = _windowLength / 2;
        _searchRadius = Math.Max(16, _hopLength / 4);
        _previousTail = new short[_hopLength];
        _sourceReadBuffer = new short[Math.Max(_windowLength, 4_096)];
    }

    public int SpeedMultiplier { get; }

    public int ReadSamples(short[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        while (_pendingCount < count)
        {
            if (!_started)
            {
                StartStream();
            }
            else
            {
                ProduceGrain();
            }
        }

        if (count > 0)
        {
            Array.Copy(_pendingOutput, _pendingOffset, buffer, offset, count);
            _pendingOffset += count;
            _pendingCount -= count;
            if (_pendingCount == 0)
            {
                _pendingOffset = 0;
            }
        }

        return count;
    }

    private void StartStream()
    {
        EnsureInputThrough(_windowLength);
        AppendOutput(_input.AsSpan(0, _hopLength));
        _input.AsSpan(_hopLength, _hopLength).CopyTo(_previousTail);
        _nextAnalysisStart = (long)SpeedMultiplier * _hopLength;
        _started = true;
    }

    private void ProduceGrain()
    {
        long minimumCandidate = Math.Max(_inputStart, _nextAnalysisStart - _searchRadius);
        long maximumCandidate = _nextAnalysisStart + _searchRadius;
        EnsureInputThrough(maximumCandidate + _windowLength);

        long candidate = FindBestCandidate(minimumCandidate, maximumCandidate);
        int candidateIndex = checked((int)(candidate - _inputStart));
        EnsureOutputCapacity(_hopLength);
        int outputIndex = _pendingOffset + _pendingCount;
        int denominator = Math.Max(1, _hopLength - 1);
        for (int i = 0; i < _hopLength; i++)
        {
            // Linear complementary ramps keep unity gain when the two grains match.
            int previousWeight = denominator - i;
            int candidateWeight = i;
            int mixed = (_previousTail[i] * previousWeight)
                + (_input[candidateIndex + i] * candidateWeight);
            _pendingOutput[outputIndex + i] = (short)(mixed / denominator);
        }

        _pendingCount += _hopLength;
        _input.AsSpan(candidateIndex + _hopLength, _hopLength).CopyTo(_previousTail);

        // Advance the ideal analysis cursor, rather than the matched candidate,
        // so correlation corrections cannot gradually alter the requested speed.
        _nextAnalysisStart += (long)SpeedMultiplier * _hopLength;
        DiscardInputBefore(Math.Max(0, _nextAnalysisStart - _searchRadius));
    }

    private long FindBestCandidate(long minimumCandidate, long maximumCandidate)
    {
        long bestCandidate = minimumCandidate;
        long bestScore = long.MaxValue;

        for (long candidate = minimumCandidate; candidate <= maximumCandidate; candidate += CoarseCandidateStride)
        {
            long score = ScoreCandidate(candidate, CoarseCorrelationStride);
            if (score < bestScore)
            {
                bestScore = score;
                bestCandidate = candidate;
            }
        }

        long fineMinimum = Math.Max(minimumCandidate, bestCandidate - CoarseCandidateStride);
        long fineMaximum = Math.Min(maximumCandidate, bestCandidate + CoarseCandidateStride);
        for (long candidate = fineMinimum; candidate <= fineMaximum; candidate++)
        {
            long score = ScoreCandidate(candidate, 1);
            if (score < bestScore)
            {
                bestScore = score;
                bestCandidate = candidate;
            }
        }

        return bestCandidate;
    }

    private long ScoreCandidate(long candidate, int stride)
    {
        int inputIndex = checked((int)(candidate - _inputStart));
        long score = 0;
        for (int i = 0; i < _hopLength; i += stride)
        {
            int difference = _previousTail[i] - _input[inputIndex + i];
            score += (long)difference * difference;
        }

        return score;
    }

    private void EnsureInputThrough(long endExclusive)
    {
        long availableEnd = _inputStart + _inputCount;
        while (availableEnd < endExclusive)
        {
            int requested = (int)Math.Min(_sourceReadBuffer.Length, endExclusive - availableEnd);
            int read = _source.ReadSamples(_sourceReadBuffer, 0, requested);
            if (read <= 0)
            {
                Array.Clear(_sourceReadBuffer, 0, requested);
                read = requested;
            }

            EnsureInputCapacity(read);
            Array.Copy(_sourceReadBuffer, 0, _input, _inputCount, read);
            _inputCount += read;
            availableEnd += read;
        }
    }

    private void EnsureInputCapacity(int additional)
    {
        if (_input.Length - _inputCount >= additional)
        {
            return;
        }

        int size = _input.Length;
        while (size - _inputCount < additional)
        {
            size *= 2;
        }

        Array.Resize(ref _input, size);
    }

    private void DiscardInputBefore(long position)
    {
        if (position <= _inputStart)
        {
            return;
        }

        int discard = (int)Math.Min(_inputCount, position - _inputStart);
        if (discard <= 0)
        {
            return;
        }

        int remaining = _inputCount - discard;
        if (remaining > 0)
        {
            Array.Copy(_input, discard, _input, 0, remaining);
        }

        _inputStart += discard;
        _inputCount = remaining;
    }

    private void AppendOutput(ReadOnlySpan<short> samples)
    {
        EnsureOutputCapacity(samples.Length);
        samples.CopyTo(_pendingOutput.AsSpan(_pendingOffset + _pendingCount));
        _pendingCount += samples.Length;
    }

    private void EnsureOutputCapacity(int additional)
    {
        int tail = _pendingOffset + _pendingCount;
        if (_pendingOutput.Length - tail >= additional)
        {
            return;
        }

        if (_pendingOutput.Length - _pendingCount >= additional)
        {
            Array.Copy(_pendingOutput, _pendingOffset, _pendingOutput, 0, _pendingCount);
            _pendingOffset = 0;
            return;
        }

        int size = _pendingOutput.Length;
        while (size - _pendingCount < additional)
        {
            size *= 2;
        }

        var replacement = new short[size];
        if (_pendingCount > 0)
        {
            Array.Copy(_pendingOutput, _pendingOffset, replacement, 0, _pendingCount);
        }

        _pendingOutput = replacement;
        _pendingOffset = 0;
    }

    private static int SelectWindowLength(int sampleRate)
    {
        // Approximately 20 ms gives stable pitch matching for AY and beeper tones
        // without adding the long latency of speech-oriented time stretchers.
        int target = Math.Max(MinimumWindowLength, sampleRate / 50);
        int length = 1;
        while (length < target && length < MaximumWindowLength)
        {
            length <<= 1;
        }

        return Math.Clamp(length, MinimumWindowLength, MaximumWindowLength);
    }
}
