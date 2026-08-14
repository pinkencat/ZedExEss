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

        public ulong Cyc; // Absolute CPU T-state count; do not wrap at frame boundaries.

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
