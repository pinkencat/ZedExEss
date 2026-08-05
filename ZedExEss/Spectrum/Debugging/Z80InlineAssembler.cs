using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ZedExEss.Spectrum.Debugging
{
    /// <summary>One contiguous set of bytes emitted at an assembly address.</summary>
    /// <remarks>
    /// A source containing <c>ORG</c> directives can produce several patches rather than one
    /// contiguous byte array.
    /// </remarks>
    public readonly struct Z80AssemblyPatch(ushort address, byte[] bytes)
    {
        public ushort Address { get; } = address;
        public byte[] Bytes { get; } = bytes;
    }
    /// <summary>The patches produced by an assembly operation, or its first diagnostic.</summary>
    public sealed class Z80AssemblyResult
    {
        public Z80AssemblyResult(IReadOnlyList<Z80AssemblyPatch> patches, string? error)
        {
            Patches = patches;
            Bytes = patches.SelectMany(static patch => patch.Bytes).ToArray();
            Error = error;
        }

        public byte[] Bytes { get; }
        public IReadOnlyList<Z80AssemblyPatch> Patches { get; }
        public string? Error { get; }
        public bool Success => Error == null;
    }
    /// <summary>
    /// Small two-pass Z80 assembler used for debugger-side code patching.
    /// </summary>
    /// <remarks>
    /// The first pass fixes instruction addresses and collects labels/EQU definitions; the
    /// second resolves expressions and emits patches. Thread-static expression context keeps
    /// the parser allocation-free without allowing state to leak between debugger threads.
    /// This is deliberately not a project assembler: it accepts the directives useful when
    /// patching a running machine, but does not perform file inclusion or macro expansion.
    /// </remarks>
    public sealed class Z80InlineAssembler
    {
        [ThreadStatic]
        private static IReadOnlyDictionary<string, int>? _expressionSymbols;
        [ThreadStatic]
        private static ushort _expressionPc;
        [ThreadStatic]
        private static bool _allowUnresolvedExpressions;
        [ThreadStatic]
        private static bool _suppressRelativeRangeErrors;
        private static readonly Dictionary<string, byte> Reg8 = new(StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = 0, ["C"] = 1, ["D"] = 2, ["E"] = 3, ["H"] = 4, ["L"] = 5, ["(HL)"] = 6, ["A"] = 7
        };
        private static readonly Dictionary<string, byte> Reg16 = new(StringComparer.OrdinalIgnoreCase)
        {
            ["BC"] = 0, ["DE"] = 1, ["HL"] = 2, ["SP"] = 3
        };
        private static readonly Dictionary<string, byte> Reg16Af = new(StringComparer.OrdinalIgnoreCase)
        {
            ["BC"] = 0, ["DE"] = 1, ["HL"] = 2, ["AF"] = 3
        };
        private static readonly Dictionary<string, byte> Conditions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["NZ"] = 0, ["Z"] = 1, ["NC"] = 2, ["C"] = 3, ["PO"] = 4, ["PE"] = 5, ["P"] = 6, ["M"] = 7
        };
        private static readonly Dictionary<string, byte> Alu = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ADD A"] = 0, ["ADC A"] = 1, ["SUB"] = 2, ["SBC A"] = 3, ["AND"] = 4, ["XOR"] = 5, ["OR"] = 6, ["CP"] = 7
        };
        private static readonly Dictionary<string, byte> BlockEd = new(StringComparer.OrdinalIgnoreCase)
        {
            ["LDI"] = 0xA0, ["CPI"] = 0xA1, ["INI"] = 0xA2, ["OUTI"] = 0xA3,
            ["LDD"] = 0xA8, ["CPD"] = 0xA9, ["IND"] = 0xAA, ["OUTD"] = 0xAB,
            ["LDIR"] = 0xB0, ["CPIR"] = 0xB1, ["INIR"] = 0xB2, ["OTIR"] = 0xB3,
            ["LDDR"] = 0xB8, ["CPDR"] = 0xB9, ["INDR"] = 0xBA, ["OTDR"] = 0xBB,
            ["NEG"] = 0x44, ["RETN"] = 0x45, ["RETI"] = 0x4D, ["RRD"] = 0x67, ["RLD"] = 0x6F
        };
        public Z80AssemblyResult Assemble(ushort address, string source)
        {
            ArgumentNullException.ThrowIfNull(source);

            // Instruction widths must be known before forward references can be evaluated.
            ParsedAssemblerLine[] lines = ParseSource(source);
            var symbols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var equDefinitions = new List<EquDefinition>();
            if (!FirstPass(address, lines, symbols, equDefinitions, out string error))
            {
                return Error(error);
            }

            if (!ResolveEquDefinitions(equDefinitions, symbols, out error))
            {
                return Error(error);
            }

            if (!SecondPass(address, lines, symbols, out List<Z80AssemblyPatch> patches, out error))
            {
                return Error(error);
            }

            return new Z80AssemblyResult(patches, null);
        }
        private static ParsedAssemblerLine[] ParseSource(string source)
        {
            string[] rawLines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var lines = new List<ParsedAssemblerLine>(rawLines.Length);
            for (int i = 0; i < rawLines.Length; i++)
            {
                string text = StripComment(rawLines[i]).Trim();
                if (text.Length > 0)
                {
                    lines.Add(new ParsedAssemblerLine(i + 1, text));
                }
            }

            return lines.ToArray();
        }
        private static bool FirstPass(
            ushort address,
            IReadOnlyList<ParsedAssemblerLine> lines,
            Dictionary<string, int> symbols,
            List<EquDefinition> equDefinitions,
            out string error)
        {
            error = string.Empty;
            ushort pc = address;
            _expressionSymbols = symbols;
            _allowUnresolvedExpressions = true;
            _suppressRelativeRangeErrors = true;

            foreach (ParsedAssemblerLine parsed in lines)
            {
                string line = parsed.Text;
                if (!ExtractLabels(ref line, parsed.LineNumber, pc, symbols, out error))
                {
                    return false;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                if (TryParseEqu(line, out string name, out string expression))
                {
                    if (!IsValidIdentifier(name))
                    {
                        error = $"Line {parsed.LineNumber}: invalid EQU name '{name}'.";
                        return false;
                    }

                    if (symbols.ContainsKey(name) || equDefinitions.Any(definition => definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        error = $"Line {parsed.LineNumber}: symbol '{name}' is already defined.";
                        return false;
                    }

                    equDefinitions.Add(new EquDefinition(name, expression, parsed.LineNumber));
                    continue;
                }

                if (TryParseOrg(line, out string orgExpression))
                {
                    _expressionPc = pc;
                    bool previousAllowUnresolved = _allowUnresolvedExpressions;
                    _allowUnresolvedExpressions = false;
                    if (!TryNumber(orgExpression, out int newPc) || newPc is < 0 or > 0xFFFF)
                    {
                        _allowUnresolvedExpressions = previousAllowUnresolved;
                        error = $"Line {parsed.LineNumber}: invalid ORG expression '{orgExpression}'.";
                        return false;
                    }

                    _allowUnresolvedExpressions = previousAllowUnresolved;
                    pc = (ushort)newPc;
                    continue;
                }

                var scratch = new List<byte>();
                _expressionPc = pc;
                if (!TryAssembleLine(pc, line, scratch, out error))
                {
                    error = $"Line {parsed.LineNumber}: {error}";
                    return false;
                }

                pc = unchecked((ushort)(pc + scratch.Count));
            }

            return true;
        }
        private static bool ResolveEquDefinitions(
            IReadOnlyList<EquDefinition> definitions,
            Dictionary<string, int> symbols,
            out string error)
        {
            error = string.Empty;
            var unresolved = new List<EquDefinition>(definitions);
            _expressionSymbols = symbols;
            _allowUnresolvedExpressions = false;
            _suppressRelativeRangeErrors = false;

            bool resolvedAny;
            do
            {
                resolvedAny = false;
                for (int i = unresolved.Count - 1; i >= 0; i--)
                {
                    EquDefinition definition = unresolved[i];
                    _expressionPc = 0;
                    if (TryNumber(definition.Expression, out int value))
                    {
                        symbols[definition.Name] = value;
                        unresolved.RemoveAt(i);
                        resolvedAny = true;
                    }
                }
            }
            while (resolvedAny && unresolved.Count > 0);

            if (unresolved.Count > 0)
            {
                EquDefinition definition = unresolved[0];
                error = $"Line {definition.LineNumber}: EQU expression for '{definition.Name}' could not be resolved.";
                return false;
            }

            return true;
        }
        private static bool SecondPass(
            ushort address,
            IReadOnlyList<ParsedAssemblerLine> lines,
            IReadOnlyDictionary<string, int> symbols,
            out List<Z80AssemblyPatch> patches,
            out string error)
        {
            patches = [];
            error = string.Empty;
            ushort pc = address;
            ushort segmentStart = pc;
            var segment = new List<byte>();
            _expressionSymbols = symbols;
            _allowUnresolvedExpressions = false;
            _suppressRelativeRangeErrors = false;

            foreach (ParsedAssemblerLine parsed in lines)
            {
                string line = parsed.Text;
                if (!ExtractLabels(ref line, parsed.LineNumber, pc, null, out error))
                {
                    return false;
                }

                if (line.Length == 0 || TryParseEqu(line, out _, out _))
                {
                    continue;
                }

                if (TryParseOrg(line, out string orgExpression))
                {
                    FlushPatch(patches, segmentStart, segment);
                    _expressionPc = pc;
                    if (!TryNumber(orgExpression, out int newPc) || newPc is < 0 or > 0xFFFF)
                    {
                        error = $"Line {parsed.LineNumber}: invalid ORG expression '{orgExpression}'.";
                        return false;
                    }

                    pc = (ushort)newPc;
                    segmentStart = pc;
                    continue;
                }

                var emitted = new List<byte>();
                _expressionPc = pc;
                if (!TryAssembleLine(pc, line, emitted, out error))
                {
                    error = $"Line {parsed.LineNumber}: {error}";
                    return false;
                }

                segment.AddRange(emitted);
                pc = unchecked((ushort)(pc + emitted.Count));
            }

            FlushPatch(patches, segmentStart, segment);
            return true;
        }
        private static bool TryAssembleLine(ushort pc, string line, List<byte> output, out string error)
        {
            try
            {
                return TryAssembleLineCore(pc, line, output, out error);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                error = ex.Message;
                return false;
            }
        }
        private static bool TryAssembleLineCore(ushort pc, string line, List<byte> output, out string error)
        {
            error = string.Empty;
            string normalized = Normalize(line);
            int split = normalized.IndexOf(' ');
            string op = split < 0 ? normalized.ToUpperInvariant() : normalized[..split].ToUpperInvariant();
            string operandText = split < 0 ? string.Empty : normalized[(split + 1)..].Trim();
            string[] operands = SplitOperands(operandText);

            if (op is "DB" or "DEFB")
            {
                return EmitBytes(operands, output, out error);
            }

            if (op is "DW" or "DEFW")
            {
                return EmitWords(operands, output, out error);
            }

            if (BlockEd.TryGetValue(op, out byte ed))
            {
                output.Add(0xED);
                output.Add(ed);
                return true;
            }

            switch (op)
            {
                case "NOP": output.Add(0x00); return true;
                case "HALT": output.Add(0x76); return true;
                case "DI": output.Add(0xF3); return true;
                case "EI": output.Add(0xFB); return true;
                case "EXX": output.Add(0xD9); return true;
                case "DAA": output.Add(0x27); return true;
                case "CPL": output.Add(0x2F); return true;
                case "SCF": output.Add(0x37); return true;
                case "CCF": output.Add(0x3F); return true;
                case "RLCA": output.Add(0x07); return true;
                case "RRCA": output.Add(0x0F); return true;
                case "RLA": output.Add(0x17); return true;
                case "RRA": output.Add(0x1F); return true;
                case "RET":
                    if (operands.Length == 0) { output.Add(0xC9); return true; }
                    if (TryCondition(operands[0], out byte retCc)) { output.Add((byte)(0xC0 + (retCc << 3))); return true; }
                    break;
                case "RST":
                    if (operands.Length == 1 && TryNumber(operands[0], out int rst) && rst % 8 == 0 && rst is >= 0 and <= 0x38)
                    {
                        output.Add((byte)(0xC7 + rst));
                        return true;
                    }
                    break;
                case "JP":
                    return EmitJp(pc, operands, output, out error);
                case "JR":
                    return EmitJr(pc, operands, output, out error);
                case "DJNZ":
                    return EmitRelative(pc, operands, 0x10, output, out error);
                case "CALL":
                    return EmitCall(operands, output, out error);
                case "PUSH":
                case "POP":
                    return EmitPushPop(op, operands, output, out error);
                case "EX":
                    return EmitEx(operands, output, out error);
                case "LD":
                    return EmitLd(operands, output, out error);
                case "INC":
                case "DEC":
                    return EmitIncDec(op, operands, output, out error);
                case "ADD":
                case "ADC":
                case "SBC":
                case "SUB":
                case "AND":
                case "XOR":
                case "OR":
                case "CP":
                    return EmitAlu(op, operands, output, out error);
                case "BIT":
                case "RES":
                case "SET":
                case "RLC":
                case "RRC":
                case "RL":
                case "RR":
                case "SLA":
                case "SRA":
                case "SLL":
                case "SRL":
                    return EmitCb(op, operands, output, out error);
                case "IN":
                    return EmitIn(operands, output, out error);
                case "OUT":
                    return EmitOut(operands, output, out error);
                case "IM":
                    return EmitIm(operands, output, out error);
            }

            error = $"Unsupported instruction '{line}'.";
            return false;
        }
        private static bool EmitLd(string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length != 2)
            {
                error = "LD requires two operands.";
                return false;
            }

            string dst = operands[0];
            string src = operands[1];
            if (TryReg8(dst, out byte rd) && TryReg8(src, out byte rs))
            {
                output.Add((byte)(0x40 | (rd << 3) | rs));
                return true;
            }

            if (dst.Equals("A", StringComparison.OrdinalIgnoreCase) && src.Equals("(BC)", StringComparison.OrdinalIgnoreCase)) { output.Add(0x0A); return true; }
            if (dst.Equals("A", StringComparison.OrdinalIgnoreCase) && src.Equals("(DE)", StringComparison.OrdinalIgnoreCase)) { output.Add(0x1A); return true; }
            if (dst.Equals("(BC)", StringComparison.OrdinalIgnoreCase) && src.Equals("A", StringComparison.OrdinalIgnoreCase)) { output.Add(0x02); return true; }
            if (dst.Equals("(DE)", StringComparison.OrdinalIgnoreCase) && src.Equals("A", StringComparison.OrdinalIgnoreCase)) { output.Add(0x12); return true; }

            if (IsMemoryAddress(dst, out int address) && src.Equals("A", StringComparison.OrdinalIgnoreCase)) { output.Add(0x32); EmitWord(output, address); return true; }
            if (dst.Equals("A", StringComparison.OrdinalIgnoreCase) && IsMemoryAddress(src, out address)) { output.Add(0x3A); EmitWord(output, address); return true; }
            if (IsMemoryAddress(dst, out address) && src.Equals("HL", StringComparison.OrdinalIgnoreCase)) { output.Add(0x22); EmitWord(output, address); return true; }
            if (dst.Equals("HL", StringComparison.OrdinalIgnoreCase) && IsMemoryAddress(src, out address)) { output.Add(0x2A); EmitWord(output, address); return true; }

            if (TryIndexMemory(dst, out byte prefix, out sbyte disp))
            {
                if (TryReg8(src, out rs))
                {
                    output.Add(prefix);
                    output.Add((byte)(0x70 | rs));
                    output.Add(unchecked((byte)disp));
                    return true;
                }

                if (TryNumber(src, out int indexedImmediate))
                {
                    output.Add(prefix);
                    output.Add(0x36);
                    output.Add(unchecked((byte)disp));
                    output.Add(CheckedByte(indexedImmediate));
                    return true;
                }
            }

            if (TryReg8(dst, out rd) && TryIndexMemory(src, out prefix, out disp))
            {
                output.Add(prefix);
                output.Add((byte)(0x46 | (rd << 3)));
                output.Add(unchecked((byte)disp));
                return true;
            }

            if (TryReg8(dst, out rd) && TryNumber(src, out int n))
            {
                output.Add((byte)(0x06 | (rd << 3)));
                output.Add(CheckedByte(n));
                return true;
            }

            if (TryReg16(dst, out byte rp) && TryNumber(src, out int nn))
            {
                output.Add((byte)(0x01 | (rp << 4)));
                EmitWord(output, nn);
                return true;
            }

            if (TryIndexRegister(dst, out prefix, out bool high) && TryNumber(src, out n))
            {
                output.Add(prefix);
                output.Add((byte)(high ? 0x26 : 0x2E));
                output.Add(CheckedByte(n));
                return true;
            }

            error = "Unsupported LD form.";
            return false;
        }
        private static bool EmitIncDec(string op, string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length != 1)
            {
                error = $"{op} requires one operand.";
                return false;
            }

            if (TryReg8(operands[0], out byte r))
            {
                output.Add((byte)((op == "INC" ? 0x04 : 0x05) | (r << 3)));
                return true;
            }

            if (TryReg16(operands[0], out byte rp))
            {
                output.Add((byte)((op == "INC" ? 0x03 : 0x0B) | (rp << 4)));
                return true;
            }

            if (TryIndexMemory(operands[0], out byte prefix, out sbyte disp))
            {
                output.Add(prefix);
                output.Add(op == "INC" ? (byte)0x34 : (byte)0x35);
                output.Add(unchecked((byte)disp));
                return true;
            }

            error = $"Unsupported {op} operand.";
            return false;
        }
        private static bool EmitAlu(string op, string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            string key;
            string operand;
            if (op is "ADD" or "ADC" or "SBC")
            {
                if (operands.Length != 2)
                {
                    error = $"{op} requires two operands.";
                    return false;
                }

                if (operands[0].Equals("HL", StringComparison.OrdinalIgnoreCase) && TryReg16(operands[1], out byte rr))
                {
                    if (op == "ADD")
                    {
                        output.Add((byte)(0x09 | (rr << 4)));
                        return true;
                    }

                    output.Add(0xED);
                    output.Add((byte)((op == "ADC" ? 0x4A : 0x42) | (rr << 4)));
                    return true;
                }

                key = $"{op} A";
                operand = operands[1];
            }
            else
            {
                if (operands.Length != 1)
                {
                    error = $"{op} requires one operand.";
                    return false;
                }

                key = op;
                operand = operands[0];
            }

            if (!Alu.TryGetValue(key, out byte group))
            {
                error = $"Unsupported {op} form.";
                return false;
            }

            if (TryReg8(operand, out byte r))
            {
                output.Add((byte)(0x80 | (group << 3) | r));
                return true;
            }

            if (TryNumber(operand, out int n))
            {
                output.Add((byte)(0xC6 | (group << 3)));
                output.Add(CheckedByte(n));
                return true;
            }

            if (TryIndexMemory(operand, out byte prefix, out sbyte disp))
            {
                output.Add(prefix);
                output.Add((byte)(0x86 | (group << 3)));
                output.Add(unchecked((byte)disp));
                return true;
            }

            error = $"Unsupported {op} operand.";
            return false;
        }
        private static bool EmitCb(string op, string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            bool bitOp = op is "BIT" or "RES" or "SET";
            if ((bitOp && operands.Length != 2) || (!bitOp && operands.Length != 1))
            {
                error = $"{op} has the wrong number of operands.";
                return false;
            }

            int bit = 0;
            string operand = bitOp ? operands[1] : operands[0];
            if (bitOp && (!TryNumber(operands[0], out bit) || bit is < 0 or > 7))
            {
                error = "Bit number must be 0..7.";
                return false;
            }

            int group = op switch
            {
                "RLC" => 0, "RRC" => 1, "RL" => 2, "RR" => 3, "SLA" => 4, "SRA" => 5, "SLL" => 6, "SRL" => 7,
                "BIT" => 8 + bit, "RES" => 16 + bit, "SET" => 24 + bit,
                _ => -1
            };

            if (TryReg8(operand, out byte r))
            {
                output.Add(0xCB);
                output.Add((byte)(((group & 0x18) << 3) | ((group & 0x07) << 3) | r));
                return true;
            }

            if (TryIndexMemory(operand, out byte prefix, out sbyte disp))
            {
                output.Add(prefix);
                output.Add(0xCB);
                output.Add(unchecked((byte)disp));
                output.Add((byte)(((group & 0x18) << 3) | ((group & 0x07) << 3) | 0x06));
                return true;
            }

            error = $"Unsupported {op} operand.";
            return false;
        }
        private static bool EmitJp(ushort pc, string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length == 1)
            {
                if (operands[0].Equals("(HL)", StringComparison.OrdinalIgnoreCase)) { output.Add(0xE9); return true; }
                if (TryNumber(operands[0], out int nn)) { output.Add(0xC3); EmitWord(output, nn); return true; }
            }

            if (operands.Length == 2 && TryCondition(operands[0], out byte cc) && TryNumber(operands[1], out int target))
            {
                output.Add((byte)(0xC2 | (cc << 3)));
                EmitWord(output, target);
                return true;
            }

            error = "Unsupported JP form.";
            return false;
        }
        private static bool EmitJr(ushort pc, string[] operands, List<byte> output, out string error)
        {
            if (operands.Length == 1)
            {
                return EmitRelative(pc, operands, 0x18, output, out error);
            }

            error = string.Empty;
            if (operands.Length == 2 && TryCondition(operands[0], out byte cc) && cc <= 3)
            {
                return EmitRelative(pc, [operands[1]], (byte)(0x20 | (cc << 3)), output, out error);
            }

            error = "JR only supports no condition, NZ, Z, NC or C.";
            return false;
        }
        private static bool EmitRelative(ushort pc, string[] operands, byte opcode, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length != 1 || !TryNumber(operands[0], out int target))
            {
                error = "Relative jump requires a numeric target address.";
                return false;
            }

            int displacement = target - (pc + 2);
            if (displacement is < -128 or > 127)
            {
                if (!_suppressRelativeRangeErrors)
                {
                    error = $"Relative target {target:X4} is out of range.";
                    return false;
                }

                displacement = 0;
            }

            output.Add(opcode);
            output.Add(unchecked((byte)(sbyte)displacement));
            return true;
        }
        private static bool EmitCall(string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length == 1 && TryNumber(operands[0], out int target))
            {
                output.Add(0xCD);
                EmitWord(output, target);
                return true;
            }

            if (operands.Length == 2 && TryCondition(operands[0], out byte cc) && TryNumber(operands[1], out target))
            {
                output.Add((byte)(0xC4 | (cc << 3)));
                EmitWord(output, target);
                return true;
            }

            error = "Unsupported CALL form.";
            return false;
        }
        private static bool EmitPushPop(string op, string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length == 1 && TryReg16Af(operands[0], out byte rr))
            {
                output.Add((byte)((op == "PUSH" ? 0xC5 : 0xC1) | (rr << 4)));
                return true;
            }

            if (operands.Length == 1 && TryIndexPair(operands[0], out byte prefix))
            {
                output.Add(prefix);
                output.Add(op == "PUSH" ? (byte)0xE5 : (byte)0xE1);
                return true;
            }

            error = $"{op} requires BC, DE, HL, AF, IX or IY.";
            return false;
        }
        private static bool EmitEx(string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length != 2)
            {
                error = "EX requires two operands.";
                return false;
            }

            string left = operands[0].Trim();
            string right = operands[1].Trim();
            if (left.Equals("AF", StringComparison.OrdinalIgnoreCase) && right.Equals("AF'", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(0x08);
                return true;
            }

            if (left.Equals("DE", StringComparison.OrdinalIgnoreCase) && right.Equals("HL", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(0xEB);
                return true;
            }

            if (left.Equals("(SP)", StringComparison.OrdinalIgnoreCase))
            {
                if (right.Equals("HL", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add(0xE3);
                    return true;
                }

                if (right.Equals("IX", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add(0xDD);
                    output.Add(0xE3);
                    return true;
                }

                if (right.Equals("IY", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add(0xFD);
                    output.Add(0xE3);
                    return true;
                }
            }

            error = "Unsupported EX form.";
            return false;
        }
        private static bool EmitIn(string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length == 2 && operands[0].Equals("A", StringComparison.OrdinalIgnoreCase) && IsPortImmediate(operands[1], out int port))
            {
                output.Add(0xDB);
                output.Add(CheckedByte(port));
                return true;
            }

            if (operands.Length == 2 && TryReg8OrZero(operands[0], out byte r) && operands[1].Equals("(C)", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(0xED);
                output.Add((byte)(0x40 | (r << 3)));
                return true;
            }

            error = "Unsupported IN form.";
            return false;
        }
        private static bool EmitOut(string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length == 2 && IsPortImmediate(operands[0], out int port) && operands[1].Equals("A", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(0xD3);
                output.Add(CheckedByte(port));
                return true;
            }

            if (operands.Length == 2 && operands[0].Equals("(C)", StringComparison.OrdinalIgnoreCase) && TryReg8OrZero(operands[1], out byte r))
            {
                output.Add(0xED);
                output.Add((byte)(0x41 | (r << 3)));
                return true;
            }

            error = "Unsupported OUT form.";
            return false;
        }
        private static bool EmitIm(string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length != 1 || !TryNumber(operands[0], out int mode) || mode is < 0 or > 2)
            {
                error = "IM requires 0, 1 or 2.";
                return false;
            }

            output.Add(0xED);
            output.Add(mode switch { 0 => (byte)0x46, 1 => (byte)0x56, _ => (byte)0x5E });
            return true;
        }
        private static bool EmitBytes(string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            foreach (string operand in operands)
            {
                if (operand.Length >= 2 && operand[0] == '"' && operand[^1] == '"')
                {
                    output.AddRange(Encoding.ASCII.GetBytes(operand[1..^1]));
                    continue;
                }

                if (!TryNumber(operand, out int value) || value is < -128 or > 0xFF)
                {
                    error = $"Invalid byte '{operand}'.";
                    return false;
                }

                output.Add(unchecked((byte)value));
            }

            return true;
        }
        private static bool EmitWords(string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            foreach (string operand in operands)
            {
                if (!TryNumber(operand, out int value) || value is < 0 or > 0xFFFF)
                {
                    error = $"Invalid word '{operand}'.";
                    return false;
                }

                EmitWord(output, value);
            }

            return true;
        }
        private static string StripComment(string line)
        {
            bool inQuote = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"')
                {
                    inQuote = !inQuote;
                }
                else if (line[i] == ';' && !inQuote)
                {
                    return line[..i];
                }
            }

            return line;
        }
        private static string Normalize(string line)
        {
            return string.Join(' ', line.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        }
        private static string[] SplitOperands(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return [];
            }

            var operands = new List<string>();
            var builder = new StringBuilder();
            bool inQuote = false;
            int parenDepth = 0;
            foreach (char ch in text)
            {
                if (ch == '"')
                {
                    inQuote = !inQuote;
                }
                else if (ch == '(' && !inQuote)
                {
                    parenDepth++;
                }
                else if (ch == ')' && !inQuote)
                {
                    parenDepth--;
                }

                if (ch == ',' && !inQuote && parenDepth == 0)
                {
                    operands.Add(builder.ToString().Trim());
                    builder.Clear();
                    continue;
                }

                builder.Append(ch);
            }

            operands.Add(builder.ToString().Trim());
            return operands.Where(o => o.Length > 0).ToArray();
        }
        private static bool TryNumber(string text, out int value)
        {
            return new ExpressionParser(text, _expressionSymbols, _allowUnresolvedExpressions, _expressionPc).TryParse(out value);
        }
        private static bool TryReg8(string operand, out byte register) => Reg8.TryGetValue(operand.Trim(), out register);
        private static bool TryReg16(string operand, out byte register) => Reg16.TryGetValue(operand.Trim(), out register);
        private static bool TryReg16Af(string operand, out byte register) => Reg16Af.TryGetValue(operand.Trim(), out register);
        private static bool TryCondition(string operand, out byte condition) => Conditions.TryGetValue(operand.Trim(), out condition);
        private static bool TryReg8OrZero(string operand, out byte register)
        {
            if (operand == "0")
            {
                register = 6;
                return true;
            }

            return TryReg8(operand, out register);
        }
        private static bool TryIndexRegister(string operand, out byte prefix, out bool high)
        {
            prefix = 0;
            high = false;
            string text = operand.Trim().ToUpperInvariant();
            if (text is "IXH" or "IXL")
            {
                prefix = 0xDD;
                high = text == "IXH";
                return true;
            }

            if (text is "IYH" or "IYL")
            {
                prefix = 0xFD;
                high = text == "IYH";
                return true;
            }

            return false;
        }
        private static bool TryIndexPair(string operand, out byte prefix)
        {
            prefix = 0;
            string text = operand.Trim();
            if (text.Equals("IX", StringComparison.OrdinalIgnoreCase))
            {
                prefix = 0xDD;
                return true;
            }

            if (text.Equals("IY", StringComparison.OrdinalIgnoreCase))
            {
                prefix = 0xFD;
                return true;
            }

            return false;
        }
        private static bool TryIndexMemory(string operand, out byte prefix, out sbyte displacement)
        {
            prefix = 0;
            displacement = 0;
            string text = operand.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
            if (!text.StartsWith("(IX", StringComparison.Ordinal) && !text.StartsWith("(IY", StringComparison.Ordinal))
            {
                return false;
            }

            if (!text.EndsWith(")", StringComparison.Ordinal))
            {
                return false;
            }

            prefix = text[2] == 'X' ? (byte)0xDD : (byte)0xFD;
            string offset = text[3..^1];
            if (offset.Length == 0)
            {
                displacement = 0;
                return true;
            }

            char sign = offset[0];
            if (sign is not ('+' or '-'))
            {
                return false;
            }

            if (!TryNumber(offset[1..], out int value) || value < 0 || (sign == '+' && value > 127) || (sign == '-' && value > 128))
            {
                return false;
            }

            displacement = (sbyte)(sign == '-' ? -value : value);
            return true;
        }
        private static bool IsMemoryAddress(string operand, out int address)
        {
            address = 0;
            string text = operand.Trim();
            return text.StartsWith("(", StringComparison.Ordinal)
                && text.EndsWith(")", StringComparison.Ordinal)
                && TryNumber(text[1..^1], out address);
        }
        private static bool IsPortImmediate(string operand, out int port) => IsMemoryAddress(operand, out port);
        private static void EmitWord(List<byte> output, int value)
        {
            output.Add((byte)(value & 0xFF));
            output.Add((byte)((value >> 8) & 0xFF));
        }
        private static byte CheckedByte(int value) => value is < 0 or > 0xFF
            ? throw new ArgumentOutOfRangeException(nameof(value), "Byte operand out of range.")
            : (byte)value;
        private static bool StartsWithDirective(string line, string directive)
        {
            return line.StartsWith(directive + " ", StringComparison.OrdinalIgnoreCase)
                || line.Equals(directive, StringComparison.OrdinalIgnoreCase);
        }
        private static bool TryParseOrg(string line, out string expression)
        {
            expression = string.Empty;
            string normalized = Normalize(line);
            int split = normalized.IndexOf(' ');
            string op = split < 0 ? normalized : normalized[..split];
            if (!op.Equals("ORG", StringComparison.OrdinalIgnoreCase) && !op.Equals(".ORG", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            expression = split < 0 ? string.Empty : normalized[(split + 1)..].Trim();
            return expression.Length > 0;
        }
        private static bool TryParseEqu(string line, out string name, out string expression)
        {
            name = string.Empty;
            expression = string.Empty;
            string normalized = Normalize(line);
            int equals = normalized.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                name = normalized[..equals].Trim();
                expression = normalized[(equals + 1)..].Trim();
                return name.Length > 0 && expression.Length > 0;
            }

            int split = normalized.IndexOf(' ');
            if (split <= 0)
            {
                return false;
            }

            string first = normalized[..split];
            string rest = normalized[(split + 1)..].Trim();
            if (rest.StartsWith("EQU ", StringComparison.OrdinalIgnoreCase))
            {
                name = first;
                expression = rest[4..].Trim();
                return expression.Length > 0;
            }

            if (rest.StartsWith("= ", StringComparison.Ordinal) || rest.StartsWith("=", StringComparison.Ordinal))
            {
                name = first;
                expression = rest[1..].Trim();
                return expression.Length > 0;
            }

            return false;
        }
        private static bool ExtractLabels(
            ref string line,
            int lineNumber,
            ushort value,
            Dictionary<string, int>? symbols,
            out string error)
        {
            error = string.Empty;
            line = line.Trim();
            while (TryConsumeLabel(ref line, out string label))
            {
                if (!IsValidIdentifier(label))
                {
                    error = $"Line {lineNumber}: invalid label '{label}'.";
                    return false;
                }

                if (symbols != null && !symbols.TryAdd(label, value))
                {
                    error = $"Line {lineNumber}: symbol '{label}' is already defined.";
                    return false;
                }

                line = line.Trim();
            }

            return true;
        }
        private static bool TryConsumeLabel(ref string line, out string label)
        {
            label = string.Empty;
            int index = 0;
            if (index >= line.Length || !IsIdentifierStart(line[index]))
            {
                return false;
            }

            index++;
            while (index < line.Length && IsIdentifierPart(line[index]))
            {
                index++;
            }

            int afterIdentifier = index;
            while (index < line.Length && char.IsWhiteSpace(line[index]))
            {
                index++;
            }

            if (index >= line.Length || line[index] != ':')
            {
                return false;
            }

            label = line[..afterIdentifier];
            line = line[(index + 1)..];
            return true;
        }
        private static bool IsValidIdentifier(string text)
        {
            if (text.Length == 0 || !IsIdentifierStart(text[0]))
            {
                return false;
            }

            for (int i = 1; i < text.Length; i++)
            {
                if (!IsIdentifierPart(text[i]))
                {
                    return false;
                }
            }

            return true;
        }
        private static bool IsIdentifierStart(char ch)
        {
            return char.IsLetter(ch) || ch is '_' or '.' or '@';
        }
        private static bool IsIdentifierPart(char ch)
        {
            return char.IsLetterOrDigit(ch) || ch is '_' or '.' or '@' or '$';
        }
        private static void FlushPatch(List<Z80AssemblyPatch> patches, ushort address, List<byte> segment)
        {
            if (segment.Count == 0)
            {
                return;
            }

            patches.Add(new Z80AssemblyPatch(address, segment.ToArray()));
            segment.Clear();
        }
        private static Z80AssemblyResult Error(string error) => new([], error);
        private readonly struct ParsedAssemblerLine(int lineNumber, string text)
        {
            public int LineNumber { get; } = lineNumber;
            public string Text { get; } = text;
        }
        private readonly struct EquDefinition(string name, string expression, int lineNumber)
        {
            public string Name { get; } = name;
            public string Expression { get; } = expression;
            public int LineNumber { get; } = lineNumber;
        }
        private sealed class ExpressionParser(
            string text,
            IReadOnlyDictionary<string, int>? symbols,
            bool allowUnresolved,
            ushort currentPc)
        {
            private readonly string _text = text.Trim();
            private int _index;
            public bool TryParse(out int value)
            {
                value = 0;
                if (_text.Length == 0)
                {
                    return false;
                }

                if (!TryParseAdditive(out value))
                {
                    return false;
                }

                SkipWhiteSpace();
                return _index == _text.Length;
            }
            private bool TryParseAdditive(out int value)
            {
                if (!TryParseMultiplicative(out value))
                {
                    return false;
                }

                while (true)
                {
                    SkipWhiteSpace();
                    if (TryConsume('+'))
                    {
                        if (!TryParseMultiplicative(out int rhs))
                        {
                            return false;
                        }

                        value += rhs;
                        continue;
                    }

                    if (TryConsume('-'))
                    {
                        if (!TryParseMultiplicative(out int rhs))
                        {
                            return false;
                        }

                        value -= rhs;
                        continue;
                    }

                    return true;
                }
            }
            private bool TryParseMultiplicative(out int value)
            {
                if (!TryParseUnary(out value))
                {
                    return false;
                }

                while (true)
                {
                    SkipWhiteSpace();
                    if (TryConsume('*'))
                    {
                        if (!TryParseUnary(out int rhs))
                        {
                            return false;
                        }

                        value *= rhs;
                        continue;
                    }

                    if (TryConsume('/'))
                    {
                        if (!TryParseUnary(out int rhs) || rhs == 0)
                        {
                            return false;
                        }

                        value /= rhs;
                        continue;
                    }

                    if (TryConsume('%'))
                    {
                        if (!TryParseUnary(out int rhs) || rhs == 0)
                        {
                            return false;
                        }

                        value %= rhs;
                        continue;
                    }

                    return true;
                }
            }
            private bool TryParseUnary(out int value)
            {
                SkipWhiteSpace();
                if (TryConsume('+'))
                {
                    return TryParseUnary(out value);
                }

                if (TryConsume('-'))
                {
                    if (!TryParseUnary(out value))
                    {
                        return false;
                    }

                    value = -value;
                    return true;
                }

                if (TryConsume('~'))
                {
                    if (!TryParseUnary(out value))
                    {
                        return false;
                    }

                    value = ~value;
                    return true;
                }

                return TryParsePrimary(out value);
            }
            private bool TryParsePrimary(out int value)
            {
                SkipWhiteSpace();
                value = 0;
                if (_index >= _text.Length)
                {
                    return false;
                }

                if (TryConsume('('))
                {
                    if (!TryParseAdditive(out value))
                    {
                        return false;
                    }

                    SkipWhiteSpace();
                    return TryConsume(')');
                }

                if (_text[_index] == '\'')
                {
                    return TryParseCharacter(out value);
                }

                if (_text[_index] == '$')
                {
                    if (_index + 1 < _text.Length && IsHexDigit(_text[_index + 1]))
                    {
                        return TryParsePrefixedHex('$', out value);
                    }

                    _index++;
                    value = currentPc;
                    return true;
                }

                if (_text[_index] == '#')
                {
                    return TryParsePrefixedHex('#', out value);
                }

                if (_text[_index] == '%' && _index + 1 < _text.Length && (_text[_index + 1] is '0' or '1'))
                {
                    return TryParseBinary(out value);
                }

                if (_index + 1 < _text.Length
                    && _text[_index] == '0'
                    && (_text[_index + 1] == 'x' || _text[_index + 1] == 'X'))
                {
                    return TryParse0xHex(out value);
                }

                if (char.IsDigit(_text[_index]))
                {
                    return TryParseNumber(out value);
                }

                if (IsIdentifierStart(_text[_index]))
                {
                    string identifier = ReadIdentifier();
                    if (symbols != null && symbols.TryGetValue(identifier, out value))
                    {
                        return true;
                    }

                    if (allowUnresolved)
                    {
                        value = 0;
                        return true;
                    }
                }

                return false;
            }
            private bool TryParseCharacter(out int value)
            {
                value = 0;
                _index++;
                if (_index >= _text.Length)
                {
                    return false;
                }

                value = _text[_index++];
                if (_index >= _text.Length || _text[_index] != '\'')
                {
                    return false;
                }

                _index++;
                return true;
            }
            private bool TryParsePrefixedHex(char prefix, out int value)
            {
                value = 0;
                if (!TryConsume(prefix))
                {
                    return false;
                }

                int start = _index;
                while (_index < _text.Length && IsHexDigit(_text[_index]))
                {
                    _index++;
                }

                return _index > start && int.TryParse(_text[start.._index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }
            private bool TryParse0xHex(out int value)
            {
                value = 0;
                _index += 2;
                int start = _index;
                while (_index < _text.Length && IsHexDigit(_text[_index]))
                {
                    _index++;
                }

                return _index > start && int.TryParse(_text[start.._index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }
            private bool TryParseBinary(out int value)
            {
                value = 0;
                _index++;
                int start = _index;
                while (_index < _text.Length && (_text[_index] is '0' or '1'))
                {
                    value = (value << 1) | (_text[_index] - '0');
                    _index++;
                }

                return _index > start;
            }
            private bool TryParseNumber(out int value)
            {
                value = 0;
                int start = _index;
                while (_index < _text.Length && char.IsLetterOrDigit(_text[_index]))
                {
                    _index++;
                }

                string number = _text[start.._index];
                if (number.EndsWith("H", StringComparison.OrdinalIgnoreCase))
                {
                    return int.TryParse(number[..^1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
                }

                if (number.EndsWith("B", StringComparison.OrdinalIgnoreCase))
                {
                    string digits = number[..^1];
                    for (int i = 0; i < digits.Length; i++)
                    {
                        if (digits[i] is not ('0' or '1'))
                        {
                            return false;
                        }

                        value = (value << 1) | (digits[i] - '0');
                    }

                    return digits.Length > 0;
                }

                return int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
            }
            private string ReadIdentifier()
            {
                int start = _index;
                _index++;
                while (_index < _text.Length && IsIdentifierPart(_text[_index]))
                {
                    _index++;
                }

                return _text[start.._index];
            }
            private void SkipWhiteSpace()
            {
                while (_index < _text.Length && char.IsWhiteSpace(_text[_index]))
                {
                    _index++;
                }
            }
            private bool TryConsume(char ch)
            {
                if (_index < _text.Length && _text[_index] == ch)
                {
                    _index++;
                    return true;
                }

                return false;
            }
            private static bool IsHexDigit(char ch)
            {
                return (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
            }
        }
    }
}
