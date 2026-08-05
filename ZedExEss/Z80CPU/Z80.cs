using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;
using System.Runtime.CompilerServices;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;

namespace ZedExEss.Z80CPU
{
    /// <summary>
    /// Cycle-accounted Z80 processor used by the Spectrum machine models.
    /// </summary>
    /// <remarks>
    /// The core owns register and interrupt state, but delegates memory, port and
    /// elapsed-T-state effects to the machine. <see cref="Cyc"/> is monotonic and is
    /// the common clock used by contention, the ULA, tape and audio subsystems.
    /// </remarks>
    public partial class Z80
    {
        private readonly SpectrumMemory _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        private readonly SpectrumPortBus _ports = ports ?? throw new ArgumentNullException(nameof(ports));

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
