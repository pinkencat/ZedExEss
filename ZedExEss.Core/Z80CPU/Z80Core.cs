using System.Runtime.CompilerServices;

namespace ZedExEss.Z80CPU
{
    /// <summary>
    /// Cycle-accounted Z80 implementation specialized over concrete machine buses.
    /// </summary>
    /// <remarks>
    /// The generic parameters are closed once per machine family. This shares the
    /// instruction implementation without putting a runtime family branch in every
    /// memory and I/O access made by the Spectrum hot path.
    /// </remarks>
    public partial class Z80Core<TMemory, TPorts> : Spectrum.Debugging.IZ80DebuggerCpu
        where TMemory : class, IZ80MemoryBus
        where TPorts : class, IZ80PortBus
    {
        private readonly TMemory _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        private readonly TPorts _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        private static readonly bool HasRefreshObserver = typeof(IZ80RefreshObserver).IsAssignableFrom(typeof(TMemory));
        private readonly IZ80RefreshObserver? _refreshObserver = memory as IZ80RefreshObserver;

        /// <summary>Concrete machine port bus available to family-specific CPU extensions.</summary>
        protected TPorts MachinePorts => _ports;

        /// <summary>
        /// Follow the ZX Spectrum Next's T80N instead of a Zilog Z80 where the two differ.
        /// </summary>
        /// <remarks>
        /// The Next's CPU is the FPGA core's <c>t80n.vhd</c>, not a Zilog die. Its documented
        /// behaviour is the same, but some undocumented flag bits are not:
        /// <list type="bullet">
        /// <item>SCF and CCF take flag bits 5/3 from A alone (there is no Q latch).</item>
        /// <item>BIT n,(HL) and BIT n,(IX+d) clear them; the undocumented DDCB/FDCB BIT forms take
        /// them from the byte read. MEMPTR never shows.</item>
        /// <item>IN r,(C), LD A,I and LD A,R leave them alone.</item>
        /// <item>CPI and CPD take them from the byte compared.</item>
        /// <item>INI, IND, OUTI and OUTD set every flag from decrementing B.</item>
        /// <item>A repeating block instruction changes no flag in its extra M-cycle.</item>
        /// </list>
        /// Each is pinned by a single-instruction differential against the simulated T80N
        /// (Klive IDE's F1, 115,854 cases) and read again from the pinned VHDL.
        /// </remarks>
        protected bool UsesT80nRules { get; init; }

        /// <summary>
        /// INT is a level, as the T80N samples it (<c>INT_s</c>, registered from <c>INT_n</c> every
        /// clock): taking the interrupt does not clear the request, which stays as long as the
        /// line driven through <see cref="Z80SetINTLine"/> does. A handler that enables
        /// interrupts and returns while its source still holds the line is interrupted again.
        /// </summary>
        /// <remarks>
        /// A Zilog Z80 samples INT as a level too, so this is a statement about the CPU; what
        /// differs between machines is what drives the line. The Spectrum family (the ULA's
        /// 32 or 36 T pulse) and the Next turn it on. It stays off where it would change
        /// nothing: the ZX80/ZX81 bus drives the line from A6 at every refresh, the acknowledge's
        /// own included, and the General Sound card drops its line after one instruction.
        /// <see cref="MaskableInterruptIsOneShot"/> overrides it while a recording is replayed.
        /// </remarks>
        protected bool MaskableInterruptIsLevel { get; init; }

        /// <summary>
        /// Consumes each INT request as it is taken, even on a CPU whose INT is a level.
        /// </summary>
        /// <remarks>
        /// For input-recording playback, which offers each recorded interrupt once, at the
        /// boundary the recording names. A recording made with a level INT has a frame of its
        /// own for every interrupt taken, a re-entry inside the pulse included (Fuse's
        /// retriggered interrupt calls <c>rzx_frame</c> too), and one made with a latched INT has
        /// none; a line held past the first entry would add entries that neither made.
        /// </remarks>
        internal bool MaskableInterruptIsOneShot { get; set; }

        /// <summary>
        /// True while a taken INT stays requested for as long as its line is held, rather than
        /// being consumed by the acknowledge.
        /// </summary>
        public bool MaskableInterruptFollowsLine => MaskableInterruptIsLevel && !MaskableInterruptIsOneShot;

        public ulong Cyc; // Absolute CPU T-state count; do not wrap at frame boundaries.

        /// <summary>T-states spent waiting for the bus, for diagnostics.</summary>
        public ulong WaitStates;

        public ushort PC, SP, IX, IY;
        public ushort MemPtr; // Undocumented WZ/MEMPTR register used by several flag behaviours.
        public byte A, B, C, D, E, H, L;
        public byte A_, B_, C_, D_, E_, H_, L_, F_;
        public byte I, R;

        private const byte CFMask = 0x01;
        private const byte NFMask = 0x02;
        private const byte PFMask = 0x04;
        private const byte XFMask = 0x08;
        private const byte HFMask = 0x10;
        private const byte YFMask = 0x20;
        private const byte ZFMask = 0x40;
        private const byte SFMask = 0x80;
        private byte _f = 0xFF;

        // Q is the undocumented latch used while assembling the next F value.
        // A flag-writing instruction leaves its result here; the next opcode
        // snapshots it into _lastQ and clears Q before it executes.
        private byte _q;
        private byte _lastQ;

        private byte F
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _f;
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                _f = value;
                _q = value;
            }
        }

        byte IffDelay;
        byte InterruptMode;
        byte IntData;
        bool IFF1 = true, IFF2 = true;
        bool Halted = true;
        bool IntPending = true, NmiPending = true;
    }
}
