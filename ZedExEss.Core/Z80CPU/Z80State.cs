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
