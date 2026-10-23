using System.Runtime.CompilerServices;
using ZedExEss.Spectrum.Audio;
using ZedExEss.ZxNext.Core;

namespace ZedExEss.ZxNext.Audio;

/// <summary>
/// Next beeper and three-chip PSG block clocked from the fixed 28 MHz timeline.
/// </summary>
/// <remarks>
/// The desktop audio contract is currently mono. The device nevertheless applies
/// TurboSound chip selection, per-chip pan enables, ABC/ACB ordering and mono flags
/// before downmixing so guest-visible routing state is not bypassed.
/// </remarks>
public sealed class ZxNextAudioDevice : IAudioScopeSource
{
    public const int AyClockHz = ZxNextReferenceBaseline.MasterClockHz / 16;

    private const int BeeperAmplitude = 7000;
    private const int MicAmplitude = 1750;
    private const int PsgChannelAmplitude = 4500;
    private const int LimiterKnee = 24576;
    private const int LimiterHeadroom = short.MaxValue - LimiterKnee;
    private const int InitialQueueCapacity = 2048;

    private readonly int _sampleRate;
    private readonly AY38912[] _chips;
    private readonly byte[] _selectedRegisters = new byte[3];
    private readonly byte[] _pan = [0x03, 0x03, 0x03];
    private short[][] _chipMix = [[], [], []];
    private short[][] _chipA = [[], [], []];
    private short[][] _chipB = [[], [], []];
    private short[][] _chipC = [[], [], []];
    private short[] _queue = new short[InitialQueueCapacity];
    private int _queueRead;
    private int _queueCount;
    private long _sampleRemainder;
    private long _ulaLevelSum;
    private ulong _ulaLevelWeight;
    // The beeper/MIC level only moves on a port or NextReg write, so the weighted sum
    // is accumulated as (level x run length) at those edges instead of once per
    // machine cycle. _ulaLevelRunTicks is the run not yet folded into _ulaLevelSum.
    private int _currentUlaLevel;
    private ulong _ulaLevelRunTicks;
    private byte _ulaOutputLatch;
    private byte _peripheral2Settings = 0xA0;
    private byte _peripheral3Settings = 0x10;
    private byte _peripheral4Settings;
    private int _activeChip;
    private bool _captureEnabled;
    private AudioScopeCapture? _scopeCapture;

    public ZxNextAudioDevice(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _sampleRate = sampleRate;
        _chips =
        [
            new AY38912(AyClockHz, sampleRate, outputAmplitude: PsgChannelAmplitude),
            new AY38912(AyClockHz, sampleRate, outputAmplitude: PsgChannelAmplitude),
            new AY38912(AyClockHz, sampleRate, outputAmplitude: PsgChannelAmplitude)
        ];
    }

    public int SampleRate => _sampleRate;
    public int ActiveChip => _activeChip;
    public byte SelectedRegister => _selectedRegisters[_activeChip];
    public int PendingSampleCount => _queueCount;
    public int QueueHighWaterMark { get; private set; }
    public bool TurboSoundEnabled => (_peripheral3Settings & 0x02) != 0;
    public byte PsgMode => (byte)(_peripheral2Settings & 0x03);

    public AY38912 GetChip(int index)
    {
        if ((uint)index >= (uint)_chips.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return _chips[index];
    }

    public void Configure(byte peripheral2Settings, byte peripheral3Settings, byte peripheral4Settings)
    {
        byte previousMode = PsgMode;
        FlushUlaLevelRun();
        _peripheral2Settings = peripheral2Settings;
        _peripheral3Settings = peripheral3Settings;
        _peripheral4Settings = peripheral4Settings;
        _currentUlaLevel = GetCurrentUlaLevel();

        if (PsgMode == 0x03 && previousMode != 0x03)
        {
            ResetPsgState();
        }
    }

    public void SetUlaOutputLatch(byte value)
    {
        byte latched = (byte)(value & 0x1F);
        if (latched == _ulaOutputLatch)
        {
            return;
        }

        FlushUlaLevelRun();
        _ulaOutputLatch = latched;
        _currentUlaLevel = GetCurrentUlaLevel();
    }

    /// <summary>Folds the ticks elapsed at the present level into the weighted sum.</summary>
    private void FlushUlaLevelRun()
    {
        if (_ulaLevelRunTicks == 0)
        {
            return;
        }

        _ulaLevelSum += (long)_currentUlaLevel * checked((long)_ulaLevelRunTicks);
        _ulaLevelRunTicks = 0;
    }

    public void BeginCapture()
    {
        if (_captureEnabled)
        {
            return;
        }

        ClearQueue();
        QueueHighWaterMark = 0;
        _captureEnabled = true;
    }

    /// <summary>
    /// Installs the oscilloscope's capture sink, or removes it with null.
    /// </summary>
    /// <remarks>
    /// The sink is independent of audio capture: the scope separates all ten sources
    /// even when the host is not draining samples for playback, and while it is absent
    /// the chips are advanced rather than rendered so nothing is paid for it.
    /// </remarks>
    public void SetScopeCapture(AudioScopeCapture? capture)
    {
        _scopeCapture = capture;
    }

    public AudioScopeLayout ScopeLayout => AudioScopeLayout.ZxNext;

    public void EndCapture()
    {
        _captureEnabled = false;
        ClearQueue();
        QueueHighWaterMark = 0;
    }

    /// <summary>Advances audio by fixed master-clock ticks, regardless of CPU speed.</summary>
    /// <remarks>
    /// This runs once per CPU machine cycle, and at 44.1 kHz only about one call in
    /// six hundred crosses a sample boundary. The accumulate-and-test path is kept
    /// inlineable so the overwhelmingly common case costs no call at all; everything
    /// that only happens on a real sample boundary lives in EmitPendingSamples.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AdvanceMasterTicks(ulong masterTicks)
    {
        if (masterTicks == 0)
        {
            return;
        }

        _ulaLevelRunTicks += masterTicks;
        _ulaLevelWeight += masterTicks;
        _sampleRemainder += checked((long)masterTicks * _sampleRate);
        if (_sampleRemainder < ZxNextReferenceBaseline.MasterClockHz)
        {
            return;
        }

        EmitPendingSamples();
    }

    private void EmitPendingSamples()
    {
        int samples = (int)(_sampleRemainder / ZxNextReferenceBaseline.MasterClockHz);
        _sampleRemainder %= ZxNextReferenceBaseline.MasterClockHz;

        FlushUlaLevelRun();
        int averagedUlaLevel = _ulaLevelWeight == 0
            ? _currentUlaLevel
            : (int)(_ulaLevelSum / (long)_ulaLevelWeight);
        _ulaLevelSum = 0;
        _ulaLevelWeight = 0;

        bool psgHeldInReset = PsgMode == 0x03;
        AudioScopeCapture? scope = _scopeCapture;
        if (!_captureEnabled && scope == null)
        {
            if (!psgHeldInReset)
            {
                for (int chip = 0; chip < _chips.Length; chip++)
                {
                    _chips[chip].AdvanceSamples(samples);
                }
            }

            return;
        }

        EnsureChipBuffers(samples);
        for (int chip = 0; chip < _chips.Length; chip++)
        {
            if (psgHeldInReset)
            {
                Array.Clear(_chipMix[chip], 0, samples);
                Array.Clear(_chipA[chip], 0, samples);
                Array.Clear(_chipB[chip], 0, samples);
                Array.Clear(_chipC[chip], 0, samples);
            }
            else
            {
                _chips[chip].RenderSamples(
                    samples,
                    _chipMix[chip],
                    0,
                    _chipA[chip],
                    _chipB[chip],
                    _chipC[chip]);
            }
        }

        if (scope != null)
        {
            // Every chip is published, including one TurboSound is currently muting:
            // the point of the scope is to show what each generator is doing.
            using AudioScopeBlock block = scope.BeginBlock(samples);
            block.Fill(
                AudioScopeLayout.BeeperChannelIndex,
                (short)Math.Clamp(averagedUlaLevel, short.MinValue, short.MaxValue));
            for (int chip = 0; chip < _chips.Length; chip++)
            {
                block.Write(
                    AudioScopeLayout.GetPsgChannelIndex(chip, 0), _chipA[chip].AsSpan(0, samples));
                block.Write(
                    AudioScopeLayout.GetPsgChannelIndex(chip, 1), _chipB[chip].AsSpan(0, samples));
                block.Write(
                    AudioScopeLayout.GetPsgChannelIndex(chip, 2), _chipC[chip].AsSpan(0, samples));
            }
        }

        if (!_captureEnabled)
        {
            return;
        }

        EnsureQueueCapacity(_queueCount + samples);
        for (int sample = 0; sample < samples; sample++)
        {
            int mixed = averagedUlaLevel;
            for (int chip = 0; chip < _chips.Length; chip++)
            {
                if (!TurboSoundEnabled && chip != _activeChip)
                {
                    continue;
                }

                mixed += DownmixChip(chip, sample);
            }

            Enqueue(ApplyOutputLimiter(mixed));
        }

        QueueHighWaterMark = Math.Max(QueueHighWaterMark, _queueCount);
    }

    public int DrainSamples(short[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        int copied = Math.Min(count, _queueCount);
        for (int index = 0; index < copied; index++)
        {
            buffer[offset + index] = _queue[_queueRead];
            _queueRead = (_queueRead + 1) % _queue.Length;
        }

        _queueCount -= copied;
        if (_queueCount == 0)
        {
            _queueRead = 0;
        }

        return copied;
    }

    public bool HandlesPort(ushort port) =>
        IsAyInformationPort(port) || IsAyControlPort(port) || IsAyDataPort(port);

    public byte ReadPort(ushort port)
    {
        if (IsAyInformationPort(port))
        {
            return (byte)(ActiveChipCode(_activeChip) | SelectedRegister);
        }

        if (IsAyControlPort(port) || IsAyDataPort(port))
        {
            return _chips[_activeChip].ReadRegister(SelectedRegister);
        }

        return 0xFF;
    }

    public void WritePort(ushort port, byte value)
    {
        if (IsAyControlPort(port))
        {
            WriteControl(value);
            return;
        }

        // $BFF5 is read-only information but remains inside the broad $BFFD write decode.
        if (IsAyDataPort(port) && PsgMode != 0x03)
        {
            _chips[_activeChip].WriteRegister(SelectedRegister, value);
        }
    }

    public void Reset()
    {
        bool captureEnabled = _captureEnabled;
        ResetPsgState();
        _ulaOutputLatch = 0;
        _currentUlaLevel = 0;
        _ulaLevelRunTicks = 0;
        _sampleRemainder = 0;
        _ulaLevelSum = 0;
        _ulaLevelWeight = 0;
        ClearQueue();
        QueueHighWaterMark = 0;
        _captureEnabled = captureEnabled;
    }

    /// <summary>Captures the PSGs and the part-accumulated sample.</summary>
    /// <remarks>
    /// The level run is folded into the weighted sum first, so the capture holds one number
    /// rather than a number and an unapplied run length. That makes the captured state
    /// independent of when the capture was taken relative to the last port write.
    /// </remarks>
    public ZxNextAudioState CaptureState()
    {
        FlushUlaLevelRun();
        var chips = new AySnapshotState[_chips.Length];
        for (int chip = 0; chip < _chips.Length; chip++)
        {
            chips[chip] = _chips[chip].CaptureState();
        }

        return new ZxNextAudioState(
            chips,
            _selectedRegisters.ToArray(),
            _pan.ToArray(),
            _activeChip,
            _ulaOutputLatch,
            _peripheral2Settings,
            _peripheral3Settings,
            _peripheral4Settings,
            _currentUlaLevel,
            _ulaLevelRunTicks,
            _ulaLevelSum,
            _ulaLevelWeight,
            _sampleRemainder);
    }

    /// <summary>
    /// Puts the PSGs and the accumulator back, leaving the output queue alone.
    /// </summary>
    /// <remarks>
    /// Capture is not symmetric here and that is deliberate: finished samples belong to the
    /// host, which has either played them or is about to, so a restore neither carries them
    /// in nor throws away what is already queued. Only what the machine is part way through
    /// producing is restored. The routing registers are assigned rather than pushed through
    /// <see cref="Configure"/>, which resets the PSGs when the mode changes - right for a
    /// guest reprogramming the machine, wrong for putting one back.
    /// </remarks>
    public void RestoreState(ZxNextAudioState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Chips.Length != _chips.Length
            || state.SelectedRegisters.Length != _selectedRegisters.Length
            || state.Pan.Length != _pan.Length)
        {
            throw new ArgumentException("A Next has three PSGs.", nameof(state));
        }

        for (int chip = 0; chip < _chips.Length; chip++)
        {
            _chips[chip].RestoreState(state.Chips[chip]);
        }

        state.SelectedRegisters.CopyTo(_selectedRegisters, 0);
        state.Pan.CopyTo(_pan, 0);
        _activeChip = state.ActiveChip;
        _ulaOutputLatch = state.UlaOutputLatch;
        _peripheral2Settings = state.Peripheral2Settings;
        _peripheral3Settings = state.Peripheral3Settings;
        _peripheral4Settings = state.Peripheral4Settings;
        _currentUlaLevel = state.CurrentUlaLevel;
        _ulaLevelRunTicks = state.UlaLevelRunTicks;
        _ulaLevelSum = state.UlaLevelSum;
        _ulaLevelWeight = state.UlaLevelWeight;
        _sampleRemainder = state.SampleRemainder;
    }

    public static bool IsAyControlPort(ushort port) => (port & 0xC002) == 0xC000;

    public static bool IsAyDataPort(ushort port) => (port & 0xC002) == 0x8000;

    public static bool IsAyInformationPort(ushort port) => (port & 0xC0FF) == 0x80F5;

    private void WriteControl(byte value)
    {
        if ((value & 0xE0) == 0)
        {
            _selectedRegisters[_activeChip] = (byte)(value & 0x0F);
            return;
        }

        if (!TurboSoundEnabled || (value & 0x80) == 0 || (value & 0x1C) != 0x1C)
        {
            return;
        }

        int chip = (value & 0x03) switch
        {
            0x02 => 1,
            0x01 => 2,
            _ => 0
        };
        _activeChip = chip;
        _pan[chip] = (byte)((value >> 5) & 0x03);
    }

    private int DownmixChip(int chip, int sample)
    {
        int a = _chipA[chip][sample];
        int b = _chipB[chip][sample];
        int c = _chipC[chip][sample];
        bool leftEnabled = (_pan[chip] & 0x02) != 0;
        bool rightEnabled = (_pan[chip] & 0x01) != 0;
        if (!leftEnabled && !rightEnabled)
        {
            return 0;
        }

        bool mono = (_peripheral4Settings & (0x20 << chip)) != 0;
        if (mono)
        {
            return a + b + c;
        }

        // Collapse the configured stereo route without halving hard-panned channels.
        // A source contributes once if it is audible on either retained side. Sparse
        // one-channel/one-chip mixes therefore keep useful level; only dense mixes
        // consume the limiter headroom below.
        int mixed = 0;
        bool acbOrder = (_peripheral3Settings & 0x20) != 0;
        if (leftEnabled)
        {
            mixed += a;
        }

        if (rightEnabled)
        {
            mixed += acbOrder ? b : c;
        }

        mixed += acbOrder ? c : b;
        return mixed;
    }

    private static short ApplyOutputLimiter(int value)
    {
        long magnitude = Math.Abs((long)value);
        if (magnitude <= LimiterKnee)
        {
            return (short)value;
        }

        long excess = magnitude - LimiterKnee;
        long compressed = LimiterKnee
            + ((long)LimiterHeadroom * excess / (excess + LimiterHeadroom));
        return (short)(value < 0 ? -compressed : compressed);
    }

    private int GetCurrentUlaLevel()
    {
        bool beeperExclusive = (_peripheral2Settings & 0x40) != 0
            && (_peripheral3Settings & 0x10) != 0;
        if (beeperExclusive)
        {
            return 0;
        }

        int value = (_ulaOutputLatch & 0x10) != 0 ? BeeperAmplitude : 0;
        value += (_ulaOutputLatch & 0x08) != 0 ? MicAmplitude : 0;
        return value;
    }

    private void ResetPsgState()
    {
        for (int chip = 0; chip < _chips.Length; chip++)
        {
            _chips[chip].Reset();
            _selectedRegisters[chip] = 0;
            _pan[chip] = 0x03;
        }

        _activeChip = 0;
    }

    private void EnsureChipBuffers(int samples)
    {
        for (int chip = 0; chip < _chips.Length; chip++)
        {
            if (_chipMix[chip].Length < samples)
            {
                _chipMix[chip] = new short[samples];
                _chipA[chip] = new short[samples];
                _chipB[chip] = new short[samples];
                _chipC[chip] = new short[samples];
            }
        }
    }

    private void EnsureQueueCapacity(int required)
    {
        if (required <= _queue.Length)
        {
            return;
        }

        int capacity = _queue.Length;
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        var replacement = new short[capacity];
        for (int index = 0; index < _queueCount; index++)
        {
            replacement[index] = _queue[(_queueRead + index) % _queue.Length];
        }

        _queue = replacement;
        _queueRead = 0;
    }

    private void Enqueue(short sample)
    {
        int write = (_queueRead + _queueCount) % _queue.Length;
        _queue[write] = sample;
        _queueCount++;
    }

    private void ClearQueue()
    {
        _queueRead = 0;
        _queueCount = 0;
    }

    private static byte ActiveChipCode(int chip) => chip switch
    {
        1 => 0x80,
        2 => 0x40,
        _ => 0xC0
    };

}
