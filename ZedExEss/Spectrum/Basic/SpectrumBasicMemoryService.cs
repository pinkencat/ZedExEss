using System;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Spectrum.Basic
{
    /// <summary>
    /// Reads and replaces the BASIC program area using the current machine's system variables.
    /// </summary>
    /// <remarks>
    /// Injection is intentionally a clean replacement: variables, edit line and calculator stack
    /// are discarded, then the ROM system-variable pointers are rebuilt around the new program.
    /// Direct memory access is safe because callers suspend emulation while this service runs.
    /// </remarks>
    public sealed class SpectrumBasicMemoryService(SpectrumMemory memory, SpectrumModel model)
    {
        private const ushort VarsAddress = 0x5C4B;
        private const ushort ProgAddress = 0x5C53;
        private const ushort NxtlinAddress = 0x5C55;
        private const ushort DataddAddress = 0x5C57;
        private const ushort ELineAddress = 0x5C59;
        private const ushort KCurAddress = 0x5C5B;
        private const ushort ChAddAddress = 0x5C5D;
        private const ushort XPtrAddress = 0x5C5F;
        private const ushort WorkspAddress = 0x5C61;
        private const ushort StkbotAddress = 0x5C63;
        private const ushort StkendAddress = 0x5C65;
        private const ushort RamtopAddress = 0x5CB2;
        private const ushort MinimumBasicAddress = 0x5CCB;
        private readonly SpectrumMemory _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        private readonly SpectrumModel _model = model;

        public SpectrumModel Model => _model;
        public bool Allow128BasicTokens => Is128BasicModel(_model)
            && _memory.GetMapping(0).IsRom
            && _memory.CurrentRomBank == 0;
        public bool TryReadProgram(out SpectrumBasicProgramSnapshot snapshot, out string error)
        {
            snapshot = default;
            if (!TryReadLayout(out SpectrumBasicLayout layout, out error))
            {
                return false;
            }

            int size = layout.Vars - layout.Prog;
            byte[] program = new byte[size];
            for (int i = 0; i < size; i++)
            {
                program[i] = _memory.ReadDirect((ushort)(layout.Prog + i));
            }

            if (!SpectrumBasicDetokenizer.TryDetokenizeProgram(program, Allow128BasicTokens, out string source, out error))
            {
                return false;
            }

            snapshot = new SpectrumBasicProgramSnapshot(layout.Prog, layout.Vars, layout.Ramtop, program.Length, source);
            return true;
        }
        public bool TryValidateSource(string source, out int tokenizedSize, out string error)
        {
            tokenizedSize = 0;
            if (!SpectrumBasicSyntaxChecker.TryValidateSource(source, Allow128BasicTokens, out error))
            {
                return false;
            }

            if (!SpectrumBasicTokenizer.TryTokenizeProgram(source, Allow128BasicTokens, out byte[] program, out error))
            {
                return false;
            }

            tokenizedSize = program.Length;
            return true;
        }
        public bool TryInjectProgram(string source, out SpectrumBasicProgramSnapshot snapshot, out string error)
        {
            snapshot = default;
            if (!SpectrumBasicSyntaxChecker.TryValidateSource(source, Allow128BasicTokens, out error))
            {
                return false;
            }

            if (!SpectrumBasicTokenizer.TryTokenizeProgram(source, Allow128BasicTokens, out byte[] program, out error))
            {
                return false;
            }

            if (!TryReadLayout(out SpectrumBasicLayout layout, out error))
            {
                return false;
            }

            int vars = layout.Prog + program.Length;
            int eLine = vars + 1;
            int worksp = eLine + 1;
            if (worksp >= layout.Ramtop)
            {
                error = $"Tokenised BASIC program is too large. It would end at {worksp:X4}, beyond RAMTOP {layout.Ramtop:X4}.";
                return false;
            }

            for (int i = 0; i < program.Length; i++)
            {
                _memory.WriteDirect((ushort)(layout.Prog + i), program[i]);
            }

            // Empty variables and edit line form the smallest valid workspace accepted by the ROM.
            _memory.WriteDirect((ushort)vars, 0x80);
            _memory.WriteDirect((ushort)eLine, 0x0D);

            WriteWord(VarsAddress, (ushort)vars);
            WriteWord(NxtlinAddress, (ushort)layout.Prog);
            WriteWord(DataddAddress, (ushort)vars);
            WriteWord(ELineAddress, (ushort)eLine);
            WriteWord(KCurAddress, (ushort)eLine);
            WriteWord(ChAddAddress, (ushort)eLine);
            WriteWord(XPtrAddress, 0);
            WriteWord(WorkspAddress, (ushort)worksp);
            WriteWord(StkbotAddress, (ushort)worksp);
            WriteWord(StkendAddress, (ushort)worksp);

            snapshot = new SpectrumBasicProgramSnapshot((ushort)layout.Prog, (ushort)vars, (ushort)layout.Ramtop, program.Length, source);
            error = string.Empty;
            return true;
        }
        private bool TryReadLayout(out SpectrumBasicLayout layout, out string error)
        {
            int prog = ReadWord(ProgAddress);
            int vars = ReadWord(VarsAddress);
            int ramtop = ReadWord(RamtopAddress);

            if (prog < MinimumBasicAddress || prog >= 0xFF00)
            {
                layout = default;
                error = $"Current BASIC PROG pointer {prog:X4} is not valid.";
                return false;
            }

            if (vars < prog || vars >= 0xFF00)
            {
                layout = default;
                error = $"Current BASIC VARS pointer {vars:X4} is not valid for PROG {prog:X4}.";
                return false;
            }

            if (ramtop <= prog || ramtop > 0xFFFF)
            {
                layout = default;
                error = $"Current BASIC RAMTOP pointer {ramtop:X4} is not valid for PROG {prog:X4}.";
                return false;
            }

            if (vars >= ramtop)
            {
                layout = default;
                error = $"Current BASIC program area overruns RAMTOP {ramtop:X4}.";
                return false;
            }

            layout = new SpectrumBasicLayout((ushort)prog, (ushort)vars, (ushort)ramtop);
            error = string.Empty;
            return true;
        }
        private int ReadWord(ushort address)
        {
            return _memory.ReadDirect(address) | (_memory.ReadDirect((ushort)(address + 1)) << 8);
        }
        private void WriteWord(ushort address, ushort value)
        {
            _memory.WriteDirect(address, (byte)(value & 0xFF));
            _memory.WriteDirect((ushort)(address + 1), (byte)(value >> 8));
        }
        private static bool Is128BasicModel(SpectrumModel model)
        {
            return model is SpectrumModel.Spectrum128K
                or SpectrumModel.SpectrumPlus2
                or SpectrumModel.SpectrumPlus2A
                or SpectrumModel.SpectrumPlus3
                or SpectrumModel.Pentagon128
                or SpectrumModel.Scorpion256;
        }
        private readonly struct SpectrumBasicLayout(ushort prog, ushort vars, ushort ramtop)
        {
            public ushort Prog { get; } = prog;
            public ushort Vars { get; } = vars;
            public ushort Ramtop { get; } = ramtop;
        }
    }
    /// <summary>Editable BASIC source plus the validated ROM workspace bounds it came from.</summary>
    public readonly struct SpectrumBasicProgramSnapshot(ushort prog, ushort vars, ushort ramtop, int programSize, string source)
    {
        public ushort Prog { get; } = prog;
        public ushort Vars { get; } = vars;
        public ushort Ramtop { get; } = ramtop;
        public int ProgramSize { get; } = programSize;
        public string Source { get; } = source;
    }
}
