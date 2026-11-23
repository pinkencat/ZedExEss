using System.Runtime.CompilerServices;
using ZedExEss.Spectrum.Audio;
using ZedExEss.ZxNext.Core;

namespace ZedExEss.ZxNext.Audio;

/// <summary>
/// Next beeper, three-chip PSG block and four DACs, clocked from the fixed 28 MHz timeline.
/// </summary>
/// <remarks>
/// <para>
/// The PSGs are the core's (<see cref="ZxNextPsg"/>), routed as <c>turbosound.vhd</c> routes
/// them: chip selection and pan, ABC (centre B on both sides), ACB (centre C) or mono per
/// chip, and with TurboSound off only the selected chip heard.
/// </para>
/// <para>
/// <c>soundrive.vhd</c>'s four 8-bit DACs are A and B on the left, C and D on the right. They
/// reset to $80 and are held there while NextReg $08 bit 3 is clear; the port bus and the
/// $2C-$2E mirrors write them.
/// </para>
/// <para>
/// <c>audio_mixer.vhd</c> then adds, per side, the beeper (EAR 512, MIC 128), the PSGs (each
/// channel 0-255), the DACs ((A + B) x 4 and (C + D) x 4) and the Pi audio, which is not
/// emulated. The sum is what the HDMI output carries (13 bits, sampled at its 48 kHz); the
/// analogue jack keeps only 12 bits and wraps, which is not modelled. The host stream is the
/// two sides at the HDMI level (each shifted left two), interleaved, or in mono (the default)
/// their mean; neither can clip, so there is no limiter. Both leave out the DACs' resting
/// level (1024 a side at $80), a constant. Like HDMI, the PSG outputs are sampled at the
/// host rate; the beeper and DAC levels, which move only on writes, are averaged over each
/// sample.
/// </para>
/// </remarks>
public sealed class ZxNextAudioDevice : IAudioScopeSource
{
    public const int AyClockHz = ZxNextPsg.ClockHz;

    /// <summary>audio_mixer.vhd's EAR and MIC volumes.</summary>
    private const int EarLevel = 512;
    private const int MicLevel = 128;

    /// <summary>
    /// The HDMI output carries each side's 13-bit sum shifted left two; the mono stream is the
    /// mean of the sides at that level, so (left + right) times two.
    /// </summary>
    private const int MonoScale = 2;
    private const int StereoScale = 4;

    /// <summary>audio_mixer.vhd shifts each side's 9-bit DAC sum left two.</summary>
    private const int DacScale = 4;

    /// <summary>soundrive.vhd's reset value, the midpoint of an unsigned 8-bit sample.</summary>
    private const byte DacResetValue = 0x80;

    /// <summary>Scales that make the scope's traces readable: a full channel, the full beeper.</summary>
    private const int ScopeChannelScale = 64;
    private const int ScopeBeeperScale = 32;
    private const int InitialQueueCapacity = 2048;

    private readonly int _sampleRate;
    private readonly ZxNextPsg[] _chips;
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

    // soundrive.vhd's channels A, B, C and D. Their sums only move on a write, so, like the
    // beeper, each side's level is folded into its sum at the writes, as level x the ticks
    // since _dacRunStart (a position in _ulaLevelWeight, the sample's elapsed ticks).
    private readonly byte[] _dac = [DacResetValue, DacResetValue, DacResetValue, DacResetValue];
    private int _currentDacLeft;
    private int _currentDacRight;
    private long _dacLeftSum;
    private long _dacRightSum;
    private ulong _dacRunStart;
    private bool _captureEnabled;
    private int _outputChannels = 1;
    private AudioScopeCapture? _scopeCapture;

    public ZxNextAudioDevice(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _sampleRate = sampleRate;
        _chips =
        [
            new ZxNextPsg(sampleRate),
            new ZxNextPsg(sampleRate),
            new ZxNextPsg(sampleRate)
        ];
    }

    public int SampleRate => _sampleRate;

    /// <summary>Host channels: 1, the sides' mean, or 2, left and right interleaved.</summary>
    public int OutputChannels => _outputChannels;

    /// <summary>
    /// Chooses mono or interleaved stereo for the samples queued from now on. A change drops
    /// the samples already queued, which are in the other layout.
    /// </summary>
    public void SetOutputChannels(int channels)
    {
        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "The Next's host stream is mono or stereo.");
        }

        if (channels != _outputChannels)
        {
            _outputChannels = channels;
            ClearQueue();
        }
    }
    public int ActiveChip => _activeChip;
    public byte SelectedRegister => _selectedRegisters[_activeChip];
    public int PendingSampleCount => _queueCount;
    public int QueueHighWaterMark { get; private set; }
    public bool TurboSoundEnabled => (_peripheral3Settings & 0x02) != 0;

    /// <summary>NextReg $08 bit 3: the DACs run; clear, soundrive.vhd is held in reset.</summary>
    public bool DacEnabled => (_peripheral3Settings & 0x08) != 0;
    public byte PsgMode => (byte)(_peripheral2Settings & 0x03);

    /// <summary>
    /// NextReg $06 bits 1:0 = 11 holds the PSGs in reset (<c>audio_ay_reset</c>) for as long as
    /// it is set: registers at their reset values, chip 0 selected with both pans on, writes
    /// and selections ignored, outputs silent.
    /// </summary>
    private bool PsgHeldInReset => PsgMode == 0x03;

    public ZxNextPsg GetChip(int index)
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
        ApplyChipMode();

        if (PsgHeldInReset && previousMode != 0x03)
        {
            ResetPsgState();
        }

        // soundrive.vhd's reset is "reset or not nr_08_dac_en".
        if (!DacEnabled)
        {
            ResetDacChannels();
        }
    }

    /// <summary>One DAC channel's value, A to D (0 to 3).</summary>
    public byte GetDacChannel(int channel)
    {
        if ((uint)channel >= (uint)_dac.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }

        return _dac[channel];
    }

    /// <summary>
    /// A port write the bus has decoded for DAC channels (bit 0 A, 1 B, 2 C, 3 D):
    /// zxnext.vhd's <c>port_dac_A_wr</c> to <c>port_dac_D_wr</c>, which need $08 bit 3.
    /// </summary>
    public void WriteDacPort(int channels, byte value)
    {
        if (!DacEnabled || channels == 0)
        {
            return;
        }

        FlushDacRun();
        for (int channel = 0; channel < _dac.Length; channel++)
        {
            if ((channels & (1 << channel)) != 0)
            {
                _dac[channel] = value;
            }
        }

        UpdateDacLevels();
    }

    /// <summary>
    /// The NextReg mirrors: $2C writes B (left), $2D A and D (mono), $2E C (right). They
    /// write through soundrive.vhd too, so they do nothing while it is held in reset.
    /// </summary>
    public void WriteDacMirror(byte register, byte value)
    {
        int channels = register switch
        {
            0x2C => 0x02,
            0x2D => 0x09,
            0x2E => 0x04,
            _ => throw new ArgumentOutOfRangeException(nameof(register))
        };
        WriteDacPort(channels, value);
    }

    /// <summary>Folds the DAC sums' run since the last change into the sample's sums.</summary>
    private void FlushDacRun()
    {
        ulong ticks = _ulaLevelWeight - _dacRunStart;
        if (ticks != 0)
        {
            _dacLeftSum += (long)_currentDacLeft * checked((long)ticks);
            _dacRightSum += (long)_currentDacRight * checked((long)ticks);
        }

        _dacRunStart = _ulaLevelWeight;
    }

    /// <summary>
    /// Each side's DAC level less its resting level, $80 + $80 shifted left two (1024): a
    /// constant in the mixer's sum, which the analogue output's coupling removes and which in
    /// the host stream would only click at every pause.
    /// </summary>
    private void UpdateDacLevels()
    {
        _currentDacLeft = (_dac[0] + _dac[1] - (2 * DacResetValue)) * DacScale;
        _currentDacRight = (_dac[2] + _dac[3] - (2 * DacResetValue)) * DacScale;
    }

    private void ResetDacChannels()
    {
        if (_dac[0] == DacResetValue && _dac[1] == DacResetValue
            && _dac[2] == DacResetValue && _dac[3] == DacResetValue)
        {
            return;
        }

        FlushDacRun();
        Array.Fill(_dac, DacResetValue);
        UpdateDacLevels();
    }

    /// <summary>NextReg $06 bit 0: 1 makes the PSGs AY-3-8910s, 0 YM2149s.</summary>
    private void ApplyChipMode()
    {
        bool ayMode = (PsgMode & 0x01) != 0;
        for (int chip = 0; chip < _chips.Length; chip++)
        {
            _chips[chip].AyMode = ayMode;
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
    /// The sink is independent of audio capture: the scope separates all fourteen sources
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

        EmitPendingSamples(masterTicks);
    }

    /// <summary>Emits the samples whose boundaries the span of <paramref name="masterTicks"/> just crossed.</summary>
    /// <remarks>
    /// The span is split at the boundaries, to the tick. Nothing changes a level inside a
    /// span (a write flushes the runs between spans), so the first sample is the mean of the
    /// levels from its start to its boundary, every later sample lies wholly inside the span
    /// at the levels it ran at, and the ticks after the last boundary begin the next sample.
    /// A long span (batched CPU time, a DMA burst) used to give all its ticks to the samples
    /// it ended, which moved up to a sample's worth of a level edge into the wrong sample.
    /// </remarks>
    private void EmitPendingSamples(ulong masterTicks)
    {
        const long clock = ZxNextReferenceBaseline.MasterClockHz;
        long total = _sampleRemainder;
        long before = total - ((long)masterTicks * _sampleRate);
        int samples = (int)(total / clock);
        _sampleRemainder = total % clock;
        ulong after = (ulong)(_sampleRemainder / _sampleRate);
        ulong toFirst = Math.Min(
            (ulong)((clock - before + _sampleRate - 1) / _sampleRate),
            masterTicks - after);
        ulong pastFirst = masterTicks - toFirst;

        // The current runs cover the whole span, so their part past the first boundary is
        // left out of the first sample and only the ticks after the last one carried on.
        ulong firstWeight = _ulaLevelWeight - pastFirst;
        long ulaSum = _ulaLevelSum + ((long)_currentUlaLevel * (long)(_ulaLevelRunTicks - pastFirst));
        ulong dacRun = _ulaLevelWeight - _dacRunStart - pastFirst;
        long dacLeftSum = _dacLeftSum + ((long)_currentDacLeft * (long)dacRun);
        long dacRightSum = _dacRightSum + ((long)_currentDacRight * (long)dacRun);
        int firstUlaLevel = _currentUlaLevel;
        int firstDacLeft = _currentDacLeft;
        int firstDacRight = _currentDacRight;
        if (firstWeight != 0)
        {
            long weight = (long)firstWeight;
            firstUlaLevel = (int)(ulaSum / weight);
            firstDacLeft = (int)(dacLeftSum / weight);
            firstDacRight = (int)(dacRightSum / weight);
        }

        _ulaLevelSum = 0;
        _ulaLevelRunTicks = after;
        _ulaLevelWeight = after;
        _dacLeftSum = 0;
        _dacRightSum = 0;
        _dacRunStart = 0;

        // The generators run on while the PSGs are held in reset; only the outputs are silent.
        bool psgHeldInReset = PsgHeldInReset;
        AudioScopeCapture? scope = _scopeCapture;
        if (!_captureEnabled && scope == null)
        {
            for (int chip = 0; chip < _chips.Length; chip++)
            {
                for (int sample = 0; sample < samples; sample++)
                {
                    _chips[chip].AdvanceSample();
                }
            }

            return;
        }

        EnsureChipBuffers(samples);
        for (int chip = 0; chip < _chips.Length; chip++)
        {
            short[] channelA = _chipA[chip];
            short[] channelB = _chipB[chip];
            short[] channelC = _chipC[chip];
            ZxNextPsg psg = _chips[chip];
            for (int sample = 0; sample < samples; sample++)
            {
                psg.RenderSample(out byte a, out byte b, out byte c);
                channelA[sample] = psgHeldInReset ? (short)0 : a;
                channelB[sample] = psgHeldInReset ? (short)0 : b;
                channelC[sample] = psgHeldInReset ? (short)0 : c;
            }
        }

        if (scope != null)
        {
            // Every chip is published, including one TurboSound is currently muting:
            // the point of the scope is to show what each generator is doing.
            using AudioScopeBlock block = scope.BeginBlock(samples);
            block.Fill(
                AudioScopeLayout.BeeperChannelIndex,
                (short)Math.Clamp(firstUlaLevel * ScopeBeeperScale, short.MinValue, short.MaxValue));
            for (int chip = 0; chip < _chips.Length; chip++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    short[] levels = channel switch { 0 => _chipA[chip], 1 => _chipB[chip], _ => _chipC[chip] };
                    Span<short> scaled = _chipMix[chip].AsSpan(0, samples);
                    for (int sample = 0; sample < samples; sample++)
                    {
                        scaled[sample] = (short)(levels[sample] * ScopeChannelScale);
                    }

                    block.Write(AudioScopeLayout.GetPsgChannelIndex(chip, channel), scaled);
                }
            }

            for (int channel = 0; channel < _dac.Length; channel++)
            {
                block.Fill(
                    AudioScopeLayout.GetNextDacChannelIndex(channel),
                    (short)(_dac[channel] * ScopeChannelScale));
            }
        }

        if (!_captureEnabled)
        {
            return;
        }

        bool stereo = _outputChannels == 2;
        EnsureQueueCapacity(_queueCount + (samples * _outputChannels));
        for (int sample = 0; sample < samples; sample++)
        {
            int left = sample == 0 ? firstUlaLevel + firstDacLeft : _currentUlaLevel + _currentDacLeft;
            int right = sample == 0 ? firstUlaLevel + firstDacRight : _currentUlaLevel + _currentDacRight;
            for (int chip = 0; chip < _chips.Length; chip++)
            {
                if (!TurboSoundEnabled && chip != _activeChip)
                {
                    continue;
                }

                AddChipStereo(chip, sample, ref left, ref right);
            }

            if (stereo)
            {
                Enqueue((short)(left * StereoScale));
                Enqueue((short)(right * StereoScale));
            }
            else
            {
                Enqueue((short)((left + right) * MonoScale));
            }
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

    /// <summary>The $BFF5 read: the active chip's id, then the five-bit selected register.</summary>
    public byte ReadInformation() => (byte)(ActiveChipCode(_activeChip) | SelectedRegister);

    /// <summary>The $FFFD read (and $BFFD's in +3 timing): the selected register.</summary>
    public byte ReadSelectedRegister() => _chips[_activeChip].ReadRegister(SelectedRegister);

    /// <summary>A $FFFD write: a register select, or a TurboSound chip and pan control.</summary>
    public void WriteRegisterSelect(byte value)
    {
        if (!PsgHeldInReset)
        {
            WriteControl(value);
        }
    }

    /// <summary>A $BFFD write ($BFF5 included): the selected register of the active chip.</summary>
    public void WriteSelectedRegister(byte value)
    {
        if (!PsgHeldInReset)
        {
            _chips[_activeChip].WriteRegister(SelectedRegister, value);
        }
    }

    /// <summary>A soft reset: the core resets the PSGs' registers and leaves their generators running.</summary>
    public void Reset() => Reset(powerOn: false);

    /// <summary>A reset, or with <paramref name="powerOn"/> the FPGA loading, which restarts the generators too.</summary>
    public void Reset(bool powerOn)
    {
        bool captureEnabled = _captureEnabled;
        ResetPsgState();
        if (powerOn)
        {
            for (int chip = 0; chip < _chips.Length; chip++)
            {
                _chips[chip].PowerOn();
            }
        }

        _ulaOutputLatch = 0;
        _currentUlaLevel = 0;
        _ulaLevelRunTicks = 0;
        _sampleRemainder = 0;
        _ulaLevelSum = 0;
        _ulaLevelWeight = 0;
        Array.Fill(_dac, DacResetValue);
        UpdateDacLevels();
        _dacLeftSum = 0;
        _dacRightSum = 0;
        _dacRunStart = 0;
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
        FlushDacRun();

        // The AY-style record is what the state format had before the PSGs were the core's:
        // it still carries the registers, so an older reader keeps working, and the PSGs'
        // own state goes alongside it.
        var chips = new AySnapshotState[_chips.Length];
        var psgs = new ZxNextPsgState[_chips.Length];
        for (int chip = 0; chip < _chips.Length; chip++)
        {
            psgs[chip] = _chips[chip].CaptureState();
            chips[chip] = RegistersOnlyAyState(psgs[chip].Registers);
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
            _sampleRemainder)
        {
            Psgs = psgs,
            Dac = new ZxNextDacState(_dac.ToArray(), _dacLeftSum, _dacRightSum)
        };
    }

    private static AySnapshotState RegistersOnlyAyState(byte[] registers) => new(
        registers.ToArray(), 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 1, 0, 1, 1, 15,
        false, false, false, false, 0, true, false);

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
            if (state.Psgs is { } psgs && chip < psgs.Length)
            {
                _chips[chip].RestoreState(psgs[chip]);
            }
            else
            {
                _chips[chip].RestoreRegisters(state.Chips[chip].Registers);
            }
        }

        state.SelectedRegisters.CopyTo(_selectedRegisters, 0);
        state.Pan.CopyTo(_pan, 0);
        _activeChip = state.ActiveChip;
        _ulaOutputLatch = state.UlaOutputLatch;
        _peripheral2Settings = state.Peripheral2Settings;
        _peripheral3Settings = state.Peripheral3Settings;
        _peripheral4Settings = state.Peripheral4Settings;
        ApplyChipMode();
        _currentUlaLevel = state.CurrentUlaLevel;
        _ulaLevelRunTicks = state.UlaLevelRunTicks;
        _ulaLevelSum = state.UlaLevelSum;
        _ulaLevelWeight = state.UlaLevelWeight;
        _sampleRemainder = state.SampleRemainder;

        // The capture folded the DAC run in, so it starts again at the capture point. A
        // state from before the DACs holds them at $80 for the whole sample so far.
        if (state.Dac is { Channels.Length: 4 } dac)
        {
            dac.Channels.CopyTo(_dac, 0);
            UpdateDacLevels();
            _dacLeftSum = dac.LeftLevelSum;
            _dacRightSum = dac.RightLevelSum;
        }
        else
        {
            Array.Fill(_dac, DacResetValue);
            UpdateDacLevels();
            _dacLeftSum = (long)_currentDacLeft * (long)_ulaLevelWeight;
            _dacRightSum = (long)_currentDacRight * (long)_ulaLevelWeight;
        }

        _dacRunStart = _ulaLevelWeight;
    }

    private void WriteControl(byte value)
    {
        // Five bits of register select: 16-31 are selected, and ignore writes.
        if ((value & 0xE0) == 0)
        {
            _selectedRegisters[_activeChip] = (byte)(value & 0x1F);
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

    /// <summary>
    /// One chip's sides, as turbosound.vhd forms them: ABC puts A left, C right and B on both;
    /// ACB (NextReg $08 bit 5) A left, B right and C on both; mono (NextReg $09 bit 5+chip)
    /// all three on both; then the pan bits keep or drop each side.
    /// </summary>
    private void AddChipStereo(int chip, int sample, ref int left, ref int right)
    {
        int a = _chipA[chip][sample];
        int b = _chipB[chip][sample];
        int c = _chipC[chip][sample];
        int chipLeft;
        int chipRight;
        if ((_peripheral4Settings & (0x20 << chip)) != 0)
        {
            chipLeft = a + b + c;
            chipRight = chipLeft;
        }
        else if ((_peripheral3Settings & 0x20) != 0)
        {
            chipLeft = a + c;
            chipRight = c + b;
        }
        else
        {
            chipLeft = a + b;
            chipRight = c + b;
        }

        if ((_pan[chip] & 0x02) != 0)
        {
            left += chipLeft;
        }

        if ((_pan[chip] & 0x01) != 0)
        {
            right += chipRight;
        }
    }

    /// <summary>
    /// The beeper's level on each side: EAR 512 and MIC 128, MIC cancelled on an issue 2
    /// keyboard (zxnext.vhd's beep_mic_final, with no tape input), and both off while NextReg
    /// $06 bit 6 and $08 bit 4 send the beeper to the internal speaker only.
    /// </summary>
    private int GetCurrentUlaLevel()
    {
        bool beeperExclusive = (_peripheral2Settings & 0x40) != 0
            && (_peripheral3Settings & 0x10) != 0;
        if (beeperExclusive)
        {
            return 0;
        }

        bool issue2 = (_peripheral3Settings & 0x01) != 0;
        int value = (_ulaOutputLatch & 0x10) != 0 ? EarLevel : 0;
        value += (_ulaOutputLatch & 0x08) != 0 && !issue2 ? MicLevel : 0;
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
