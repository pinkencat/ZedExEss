using System;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Audio.GeneralSound
{
    /// <summary>
    /// General Sound: a sample-playback card built around its own Z80, RAM and ROM.
    /// </summary>
    /// <remarks>
    /// The card is a complete second machine rather than a register block, so it reuses
    /// the shared <see cref="Z80Core{TMemory,TPorts}"/> closed over its own buses. It
    /// runs on its own crystal and is advanced by a ratio against host T-states, in the
    /// same way the turbo clock decouples the Scorpion CPU from its ULA.
    ///
    /// The card is only present when firmware has been supplied: without a ROM its Z80
    /// has nothing to execute, and a card that answers its ports while executing nothing
    /// is worse than an absent one, because software would detect it and then wait.
    /// </remarks>
    public sealed class GeneralSoundCard
    {
        /// <summary>The card's own clock. The host's is unrelated and much slower.</summary>
        public const int ClockHz = 12_000_000;

        /// <summary>Sample channels mixed to the card's output.</summary>
        public const int ChannelCount = 4;

        /// <summary>
        /// The card's mix loop is driven by a maskable interrupt at this rate.
        /// </summary>
        /// <remarks>
        /// Without it the firmware initialises and then waits forever: the interrupt is
        /// what makes it fetch the next sample for each channel.
        /// </remarks>
        public const int InterruptHz = 37_500;

        /// <summary>
        /// Card T-states between interrupts: 12 MHz divided by 37.5 kHz, exactly.
        /// </summary>
        /// <remarks>
        /// The mix loop advances one sample per interrupt, so this is the card's sample
        /// rate and any error in it is heard directly as pitch. ZEsarUX uses 334 here,
        /// but it also runs the card at 750 T-states per host scanline where a true
        /// 12 MHz gives 768 - about 2.5% slow - so its period looks like a compensation
        /// for that rather than a property of the hardware. This card is clocked exactly,
        /// so it wants the exact divisor.
        /// </remarks>
        public const int TstatesPerInterrupt = ClockHz / InterruptHz;

        /// <summary>Peak amplitude one channel contributes at full volume.</summary>
        public const int ChannelGain = 48;

        /// <summary>The card's RAM, as 16 KB banks.</summary>
        public int BankCount => _memory.BankCount;


        private readonly GeneralSoundMemory _memory;
        private readonly GeneralSoundPorts _ports;
        private readonly Z80Core<GeneralSoundMemory, GeneralSoundPorts> _cpu;
        private readonly int _hostClockHz;
        private long _pendingHostTstates;
        private ulong _nextInterruptCyc = TstatesPerInterrupt;
        private ulong _targetCyc;

        private GeneralSoundCard(ReadOnlySpan<byte> firmware, int hostClockHz, int bankCount)
        {
            _hostClockHz = hostClockHz;
            // The DAC window is fed by memory reads, so the memory bus needs a way to
            // reach the ports that own the channels.
            GeneralSoundPorts? ports = null;
            _memory = new GeneralSoundMemory(
                firmware,
                bankCount,
                (channel, sample) => ports!.FeedDac(channel, sample));
            _ports = ports = new GeneralSoundPorts(_memory);
            _cpu = new Z80Core<GeneralSoundMemory, GeneralSoundPorts>(_memory, _ports);
            _cpu.Z80Init();

            // A host command does not interrupt the card. Its idle loop polls the status
            // port and picks the command up from there; forcing an NMI instead drops it
            // into whatever ROM happens to sit at $0066, which is ordinary code reached
            // by CALL rather than a vector.
        }

        /// <summary>
        /// Creates a card, or returns null when no firmware is available.
        /// </summary>
        public static GeneralSoundCard? TryCreate(
            ReadOnlySpan<byte> firmware,
            int hostClockHz,
            int bankCount = GeneralSoundMemory.StandardBankCount)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hostClockHz);
            return firmware.IsEmpty ? null : new GeneralSoundCard(firmware, hostClockHz, bankCount);
        }

        /// <summary>The host-facing latches and handshake flags.</summary>
        public GeneralSoundHostInterface Host => _ports.Host;

        /// <summary>Total card T-states executed, for diagnostics and verification.</summary>
        public ulong Cyc => _cpu.Cyc;

        /// <summary>
        /// Interrupts raised so far. The mix loop advances one sample per interrupt, so
        /// this rate is the card's sample rate and any error in it is heard as pitch.
        /// </summary>
        public long InterruptCount { get; private set; }

        /// <summary>
        /// Runs the card for the emulated time represented by <paramref name="hostTstates"/>.
        /// </summary>
        /// <remarks>
        /// The ratio is not a whole number, so the remainder is banked rather than
        /// rounded away; dropping it would make the card drift slow by several percent
        /// over a frame.
        /// </remarks>
        public void AdvanceHostTstates(int hostTstates)
        {
            if (hostTstates <= 0)
            {
                return;
            }

            _pendingHostTstates += (long)hostTstates * ClockHz;
            long budget = _pendingHostTstates / _hostClockHz;
            if (budget <= 0)
            {
                return;
            }

            _pendingHostTstates -= budget * _hostClockHz;

            // Accumulate the target rather than measuring from the current position. The
            // last instruction of a run almost always overshoots, and recomputing from
            // where it landed silently grants that overshoot every time, so the card
            // creeps ahead of the host.
            _targetCyc += (ulong)budget;
            while (_cpu.Cyc < _targetCyc)
            {
                if (_cpu.Cyc >= _nextInterruptCyc)
                {
                    // Re-assert rather than hold the line: the firmware acknowledges by
                    // servicing, and a level held across the whole period would retrigger.
                    _cpu.Z80SetINTLine(true, 0xFF);
                    InterruptCount++;

                    // Advance the schedule by exactly one period. Setting it relative to
                    // the current position instead would add however long the current
                    // instruction happened to be to every period - which both slows the
                    // sample rate and makes it jitter, so pitch wanders.
                    _nextInterruptCyc += TstatesPerInterrupt;
                    if (_nextInterruptCyc <= _cpu.Cyc)
                    {
                        // Genuinely behind rather than merely late: resynchronise instead
                        // of building a backlog that would fire on every instruction.
                        _nextInterruptCyc = _cpu.Cyc + TstatesPerInterrupt;
                    }
                }

                _cpu.Z80Step();
                _cpu.Z80SetINTLine(false);
            }
        }

        /// <summary>Mixes the card's channels into one host sample.</summary>
        public int GetMixedOutput() => _ports.GetMixedOutput();

        /// <summary>Returns one channel's current level, for the oscilloscope.</summary>
        public short GetChannelLevel(int channel) => _ports.GetChannelLevel(channel);

        /// <summary>DAC writes so far, which is how a test sees the firmware producing audio.</summary>
        public long DacWriteCount => _ports.DacWriteCount;

        /// <summary>The card's program counter, for diagnostics.</summary>
        public ushort ProgramCounter => _cpu.PC;

        /// <summary>True while the card's high window is showing ROM rather than RAM.</summary>
        public bool HighWindowIsRom => _memory.HighWindowIsRom;

        /// <summary>The page selected into the card's 32 KB high window.</summary>
        public int MappedPage => _memory.MappedPage;


        public void Reset()
        {
            _cpu.Z80Init();
            _memory.Reset();
            _ports.Reset();
            _pendingHostTstates = 0;
            _nextInterruptCyc = TstatesPerInterrupt;
            _targetCyc = 0;
            InterruptCount = 0;
        }
    }
}
