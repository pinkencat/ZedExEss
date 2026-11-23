namespace ZedExEss.ZxNext.Audio;

/// <summary>
/// One of the Next's three PSGs, as the core's <c>ym2149.vhd</c> implements it: a YM2149 by
/// default and an AY-3-8910 when NextReg $06 bits 1:0 are 01.
/// </summary>
/// <remarks>
/// <para>
/// The core runs it from a 1.75 MHz clock enable. Every 8 enables (<c>ena_div</c>) the tone
/// counters and the envelope counter count; every 16 the noise counter does. Each counter
/// toggles or steps when it reaches its period less one, so periods 0 and 1 both mean 1.
/// The tone, noise and envelope timing is modelled at that eight-enable step; the one-enable
/// register delays inside the core (about half a microsecond) are not.
/// </para>
/// <para>
/// The envelope has 32 steps in both modes. A channel's level is five bits: the envelope, or
/// a fixed volume v as v*2+1 (0 stays 0). The YM looks the level up in a 32-entry table, the
/// AY its top four bits in a 16-entry one. The result is the core's 0-255 channel output.
/// </para>
/// <para>
/// All eight bits of a register are stored. The YM reads them all back; the AY masks the
/// unused bits. Registers 16-31 ignore writes, and read $FF on a YM and alias 0-15 on an AY.
/// R14 and R15 read $FF while their port is an input (R7 bits 6 and 7) and the register
/// otherwise, since the core ties the port inputs high. A reset clears the registers with R7
/// at $FF and leaves the generators running, as the core does.
/// </para>
/// </remarks>
public sealed class ZxNextPsg
{
    /// <summary>The PSG clock enable: 28 MHz / 16.</summary>
    public const int ClockHz = 1_750_000;

    private const int EnablesPerStep = 8;

    private static readonly byte[] VolumeTableAy =
    [
        0x00, 0x03, 0x04, 0x06, 0x0A, 0x0F, 0x15, 0x22,
        0x28, 0x41, 0x5B, 0x72, 0x90, 0xB5, 0xD7, 0xFF
    ];

    private static readonly byte[] VolumeTableYm =
    [
        0x00, 0x01, 0x01, 0x02, 0x02, 0x03, 0x03, 0x04,
        0x06, 0x07, 0x09, 0x0A, 0x0C, 0x0E, 0x11, 0x13,
        0x17, 0x1B, 0x20, 0x25, 0x2C, 0x35, 0x3E, 0x47,
        0x54, 0x66, 0x77, 0x88, 0xA1, 0xC0, 0xE0, 0xFF
    ];

    /// <summary>The bits an AY reads back of each register; the YM reads all eight.</summary>
    private static readonly byte[] AyReadMasks =
    [
        0xFF, 0x0F, 0xFF, 0x0F, 0xFF, 0x0F, 0x1F, 0xFF,
        0x1F, 0x1F, 0x1F, 0xFF, 0xFF, 0x0F, 0xFF, 0xFF
    ];

    private readonly byte[] _registers = new byte[16];
    private readonly int[] _toneCount = new int[3];
    private readonly int _sampleRate;
    private int _toneOutputs;
    private int _enableRemainder;
    private int _enablesToStep;
    private bool _noiseHalf;
    private int _noiseCount;
    private int _poly17;
    private int _envelopeCount;
    private int _envelopeVolume;
    private bool _envelopeIncreasing;
    private bool _envelopeHeld;

    public ZxNextPsg(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _sampleRate = sampleRate;
        PowerOn();
    }

    /// <summary>NextReg $06 bit 0 set: AY-3-8910 behaviour, otherwise YM2149.</summary>
    public bool AyMode { get; set; }

    /// <summary>A write to the selected register (five bits): 16-31 are ignored.</summary>
    public void WriteRegister(int register, byte value)
    {
        if ((uint)register >= 16)
        {
            return;
        }

        _registers[register] = value;
        if (register == 13)
        {
            ResetEnvelope();
        }
    }

    /// <summary>A read of the selected register (five bits).</summary>
    public byte ReadRegister(int register)
    {
        if ((register & 0x10) != 0 && !AyMode)
        {
            return 0xFF;
        }

        int index = register & 0x0F;
        if (index == 14)
        {
            return (_registers[7] & 0x40) == 0 ? (byte)0xFF : _registers[14];
        }

        if (index == 15)
        {
            return (_registers[7] & 0x80) == 0 ? (byte)0xFF : _registers[15];
        }

        return AyMode ? (byte)(_registers[index] & AyReadMasks[index]) : _registers[index];
    }

    /// <summary>The core's reset: registers cleared with R7 at $FF, the generators untouched.</summary>
    public void Reset()
    {
        Array.Clear(_registers);
        _registers[7] = 0xFF;
    }

    /// <summary>The FPGA loaded: the generators back at their initial values too.</summary>
    public void PowerOn()
    {
        Reset();
        Array.Clear(_toneCount);
        _toneOutputs = 0;
        _enableRemainder = 0;
        _enablesToStep = 0;
        _noiseHalf = false;
        _noiseCount = 0;
        _poly17 = 0;
        _envelopeCount = 0;
        _envelopeVolume = 0;
        _envelopeIncreasing = false;
        _envelopeHeld = false;
    }

    /// <summary>Advances by one host sample period and returns the three channel outputs.</summary>
    public void RenderSample(out byte a, out byte b, out byte c)
    {
        AdvanceSample();
        a = ChannelOutput(0);
        b = ChannelOutput(1);
        c = ChannelOutput(2);
    }

    /// <summary>Advances by one host sample period without producing output.</summary>
    public void AdvanceSample()
    {
        _enableRemainder += ClockHz;
        int enables = _enableRemainder / _sampleRate;
        _enableRemainder -= enables * _sampleRate;
        AdvanceEnables(enables);
    }

    private void AdvanceEnables(int enables)
    {
        while (enables > 0)
        {
            if (_enablesToStep > 0)
            {
                int skipped = Math.Min(enables, _enablesToStep);
                _enablesToStep -= skipped;
                enables -= skipped;
                continue;
            }

            enables--;
            _enablesToStep = EnablesPerStep - 1;
            Step();
        }
    }

    /// <summary>One <c>ena_div</c>: the tone and envelope counters, and every other time the noise.</summary>
    private void Step()
    {
        for (int channel = 0; channel < 3; channel++)
        {
            int period = ((_registers[(channel * 2) + 1] & 0x0F) << 8) | _registers[channel * 2];
            int compare = period >= 2 ? period - 1 : 0;
            if (_toneCount[channel] >= compare)
            {
                _toneCount[channel] = 0;
                _toneOutputs ^= 1 << channel;
            }
            else
            {
                _toneCount[channel]++;
            }
        }

        _noiseHalf = !_noiseHalf;
        if (!_noiseHalf)
        {
            int noisePeriod = _registers[6] & 0x1F;
            int noiseCompare = noisePeriod >= 2 ? noisePeriod - 1 : 0;
            if (_noiseCount >= noiseCompare)
            {
                _noiseCount = 0;
                int feedback = (_poly17 & 1) ^ ((_poly17 >> 2) & 1) ^ (_poly17 == 0 ? 1 : 0);
                _poly17 = (feedback << 16) | (_poly17 >> 1);
            }
            else
            {
                _noiseCount++;
            }
        }

        int envelopePeriod = (_registers[12] << 8) | _registers[11];
        int envelopeCompare = envelopePeriod >= 2 ? envelopePeriod - 1 : 0;
        if (_envelopeCount >= envelopeCompare)
        {
            _envelopeCount = 0;
            StepEnvelope();
        }
        else
        {
            _envelopeCount++;
        }
    }

    /// <summary>
    /// An R13 write: the envelope restarts at 31 falling (attack bit clear) or 0 rising, and
    /// steps once at the next clock enable.
    /// </summary>
    private void ResetEnvelope()
    {
        bool attack = (_registers[13] & 0x04) != 0;
        _envelopeVolume = attack ? 0 : 31;
        _envelopeIncreasing = attack;
        _envelopeHeld = false;
        _envelopeCount = 0;
        StepEnvelope();
    }

    /// <summary>One envelope step, transcribed from ym2149.vhd's shape logic.</summary>
    private void StepEnvelope()
    {
        int volume = _envelopeVolume;
        bool zero = (volume >> 1) == 0;
        bool ones = (volume >> 1) == 15;
        bool bottom = zero && (volume & 1) == 0;
        bool bottomPlusOne = zero && (volume & 1) == 1;
        bool topMinusOne = ones && (volume & 1) == 0;
        bool top = ones && (volume & 1) == 1;
        bool held = _envelopeHeld;
        bool increasing = _envelopeIncreasing;

        if (!_envelopeHeld)
        {
            _envelopeVolume = (volume + (increasing ? 1 : 31)) & 0x1F;
        }

        byte shape = _registers[13];
        if ((shape & 0x08) == 0)
        {
            if (increasing ? top : bottomPlusOne)
            {
                held = true;
            }
        }
        else if ((shape & 0x01) != 0)
        {
            bool alternate = (shape & 0x02) != 0;
            if (increasing ? (alternate ? top : topMinusOne) : (alternate ? bottom : bottomPlusOne))
            {
                held = true;
            }
        }
        else if ((shape & 0x02) != 0)
        {
            if (!increasing)
            {
                if (bottomPlusOne)
                {
                    held = true;
                }

                if (bottom)
                {
                    held = false;
                    increasing = true;
                }
            }
            else
            {
                if (topMinusOne)
                {
                    held = true;
                }

                if (top)
                {
                    held = false;
                    increasing = false;
                }
            }
        }

        _envelopeHeld = held;
        _envelopeIncreasing = increasing;
    }

    /// <summary>The 0-255 output of channel 0-2: its mixer gate, then its level through the table.</summary>
    private byte ChannelOutput(int channel)
    {
        byte mixer = _registers[7];
        bool tone = (mixer & (1 << channel)) != 0 || (_toneOutputs & (1 << channel)) != 0;
        bool noise = (mixer & (8 << channel)) != 0 || (_poly17 & 1) != 0;
        if (!tone || !noise)
        {
            return 0;
        }

        byte amplitude = _registers[8 + channel];
        int level;
        if ((amplitude & 0x10) != 0)
        {
            level = _envelopeVolume;
        }
        else
        {
            int fixedVolume = amplitude & 0x0F;
            level = fixedVolume == 0 ? 0 : (fixedVolume << 1) | 1;
        }

        return AyMode ? VolumeTableAy[level >> 1] : VolumeTableYm[level];
    }

    /// <summary>The registers as written, all eight bits.</summary>
    public byte[] CopyRegisters() => _registers.ToArray();

    /// <summary>Captures the registers and every generator.</summary>
    public ZxNextPsgState CaptureState() => new(
        _registers.ToArray(),
        _toneCount.ToArray(),
        (byte)_toneOutputs,
        _enableRemainder,
        _enablesToStep,
        _noiseHalf,
        _noiseCount,
        _poly17,
        _envelopeCount,
        (byte)_envelopeVolume,
        _envelopeIncreasing,
        _envelopeHeld);

    public void RestoreState(ZxNextPsgState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Registers.AsSpan(0, 16).CopyTo(_registers);
        state.ToneCount.AsSpan(0, 3).CopyTo(_toneCount);
        _toneOutputs = state.ToneOutputs & 0x07;
        _enableRemainder = state.EnableRemainder;
        _enablesToStep = state.EnablesToStep;
        _noiseHalf = state.NoiseHalf;
        _noiseCount = state.NoiseCount;
        _poly17 = state.Poly17 & 0x1FFFF;
        _envelopeCount = state.EnvelopeCount;
        _envelopeVolume = state.EnvelopeVolume & 0x1F;
        _envelopeIncreasing = state.EnvelopeIncreasing;
        _envelopeHeld = state.EnvelopeHeld;
    }

    /// <summary>
    /// Puts back only the registers, from a state file saved before the PSG was the core's:
    /// the generators restart.
    /// </summary>
    public void RestoreRegisters(ReadOnlySpan<byte> registers)
    {
        PowerOn();
        registers[..Math.Min(16, registers.Length)].CopyTo(_registers);
    }
}

/// <summary>A <see cref="ZxNextPsg"/>'s registers and generators.</summary>
public sealed record ZxNextPsgState(
    byte[] Registers,
    int[] ToneCount,
    byte ToneOutputs,
    int EnableRemainder,
    int EnablesToStep,
    bool NoiseHalf,
    int NoiseCount,
    int Poly17,
    int EnvelopeCount,
    byte EnvelopeVolume,
    bool EnvelopeIncreasing,
    bool EnvelopeHeld);
