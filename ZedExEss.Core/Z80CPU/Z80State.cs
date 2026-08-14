namespace ZedExEss.Z80CPU
{
    // Deliberately small public state surface for snapshot loading and debugger UI.
    // Instruction execution uses the private/inlined accessors in Z80Methods.
    public partial class Z80Core<TMemory, TPorts>
        where TMemory : class, IZ80MemoryBus
        where TPorts : class, IZ80PortBus
    {
        ulong Spectrum.Debugging.IZ80DebuggerCpu.Cyc => Cyc;
        ushort Spectrum.Debugging.IZ80DebuggerCpu.PC => PC;
        ushort Spectrum.Debugging.IZ80DebuggerCpu.SP => SP;
        ushort Spectrum.Debugging.IZ80DebuggerCpu.IX => IX;
        ushort Spectrum.Debugging.IZ80DebuggerCpu.IY => IY;
        byte Spectrum.Debugging.IZ80DebuggerCpu.I => I;
        byte Spectrum.Debugging.IZ80DebuggerCpu.R => R;

        public byte GetFlags()
        {
            return GetF();
        }
        public ushort AF => (ushort)((A << 8) | GetF());
        public ushort BC => GetBC();
        public ushort DE => GetDE();
        public ushort HL => GetHL();

        public ushort AF_ => (ushort)((A_ << 8) | F_);

        public ushort BC_ => (ushort)((B_ << 8) | C_);

        public ushort DE_ => (ushort)((D_ << 8) | E_);

        public ushort HL_ => (ushort)((H_ << 8) | L_);

        public byte InterruptModeValue => InterruptMode;

        public bool Iff1 => IFF1;

        public bool Iff2 => IFF2;

        public bool IsHalted => Halted;

        /// <summary>Captures CPU-owned state at the current instruction boundary.</summary>
        public Z80SnapshotState CaptureSnapshotState()
        {
            FlushBatchedInstructionTstates();
            return new Z80SnapshotState(
                Cyc,
                PC,
                SP,
                IX,
                IY,
                MemPtr,
                A,
                _f,
                B,
                C,
                D,
                E,
                H,
                L,
                A_,
                F_,
                B_,
                C_,
                D_,
                E_,
                H_,
                L_,
                I,
                R,
                InterruptMode,
                IFF1,
                IFF2,
                Halted,
                IffDelay,
                IntData,
                IntPending,
                NmiPending,
                _q,
                _lastQ);
        }

        /// <summary>Restores CPU-owned state without performing bus cycles.</summary>
        public void RestoreSnapshotState(Z80SnapshotState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            FlushBatchedInstructionTstates();

            Cyc = state.Cycles;
            PC = state.PC;
            SP = state.SP;
            IX = state.IX;
            IY = state.IY;
            MemPtr = state.MemPtr;
            A = state.A;
            _f = state.F;
            B = state.B;
            C = state.C;
            D = state.D;
            E = state.E;
            H = state.H;
            L = state.L;
            A_ = state.AlternateA;
            F_ = state.AlternateF;
            B_ = state.AlternateB;
            C_ = state.AlternateC;
            D_ = state.AlternateD;
            E_ = state.AlternateE;
            H_ = state.AlternateH;
            L_ = state.AlternateL;
            I = state.I;
            R = state.R;
            InterruptMode = (byte)(state.InterruptMode & 0x03);
            IFF1 = state.Iff1;
            IFF2 = state.Iff2;
            Halted = state.Halted;
            IffDelay = state.IffDelay;
            IntData = state.InterruptData;
            IntPending = state.IntPending;
            NmiPending = state.NmiPending;
            _q = state.Q;
            _lastQ = state.LastQ;
            _remainingCycles = 0;
            _batchedInstructionTstates = 0;
        }
        public void SetFlags(byte flags)
        {
            SetF(flags);
        }
        public void SetCarry(bool carry)
        {
            if (carry)
            {
                _f |= CFMask;
            }
            else
            {
                _f &= unchecked((byte)~CFMask);
            }
        }
        public void SetInterruptState(byte interruptMode, bool iff1, bool iff2)
        {
            InterruptMode = (byte)(interruptMode & 0x03);
            IFF1 = iff1;
            IFF2 = iff2;
            IffDelay = 0;
            IntPending = false;
            NmiPending = false;
            IntData = 0;
        }
        public void SetHalted(bool halted)
        {
            Halted = halted;
        }
    }
}
