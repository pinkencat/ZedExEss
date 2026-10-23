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
    /// <summary>One thing wrong with the source, and where.</summary>
    /// <remarks>
    /// The column is best-effort: the assembler's parsers report the token they choked on
    /// rather than an offset, so it is found by locating that token in the line. When it
    /// cannot be located the column is the start of the line, which is still better than
    /// no position at all. A caret is worth more than exactness here - it points at the
    /// thing the reader has to look at.
    /// </remarks>
    public readonly record struct Z80AssemblyDiagnostic(
        int Line,
        int Column,
        string Message,
        string SourceLine)
    {
        /// <summary>The message, the offending line, and a caret under the column.</summary>
        public string Format()
        {
            string caret = new string(' ', Math.Max(0, Column - 1)) + "^";
            return $"Line {Line},{Column}: {Message}\n  {SourceLine}\n  {caret}";
        }

        public override string ToString() => Format();
    }

    /// <summary>The patches produced by an assembly operation, or everything wrong with it.</summary>
    public sealed class Z80AssemblyResult
    {
        public Z80AssemblyResult(IReadOnlyList<Z80AssemblyPatch> patches, string? error)
            : this(
                patches,
                error == null
                    ? []
                    : new[] { new Z80AssemblyDiagnostic(0, 1, error, string.Empty) })
        {
        }

        public Z80AssemblyResult(
            IReadOnlyList<Z80AssemblyPatch> patches,
            IReadOnlyList<Z80AssemblyDiagnostic> diagnostics)
        {
            Patches = patches;
            Bytes = patches.SelectMany(static patch => patch.Bytes).ToArray();
            Diagnostics = diagnostics;
            Error = diagnostics.Count == 0
                ? null
                : string.Join("\n", diagnostics.Select(static diagnostic => diagnostic.Format()));
        }

        public byte[] Bytes { get; }
        public IReadOnlyList<Z80AssemblyPatch> Patches { get; }

        /// <summary>Everything wrong with the source, not only the first thing.</summary>
        public IReadOnlyList<Z80AssemblyDiagnostic> Diagnostics { get; }

        /// <summary>
        /// Every diagnostic, formatted and joined.
        /// </summary>
        /// <remarks>
        /// Kept as a string so callers that only ever showed a message keep working, and
        /// get the whole list rather than the first line of it for free.
        /// </remarks>
        public string? Error { get; }

        public bool Success => Diagnostics.Count == 0;
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
        private readonly bool _enableZ80N;
        /// <summary>The label a leading-dot name belongs to, or empty outside any.</summary>
        /// <remarks>
        /// Local labels exist so a scratch patch can say <c>.loop</c> twice without the two
        /// colliding. They are stored qualified - <c>copy.loop</c> - so the symbol table
        /// stays a flat dictionary, and references are qualified the same way at lookup.
        /// </remarks>
        [ThreadStatic]
        private static string? _expressionLocalScope;

        /// <summary>
        /// Names imported from an assembler, consulted only when the source defines none.
        /// </summary>
        /// <remarks>
        /// Kept separate from the source's own symbols rather than merged into them, so a
        /// patch that defines a label already present in the imported list shadows it
        /// instead of colliding with it. The thing in front of you wins.
        /// </remarks>
        /// <summary>Supplies the bytes an <c>INCBIN</c> asks for.</summary>
        /// <remarks>
        /// Separate from the text resolver because the two want different things back, and
        /// both are callbacks for the same reason: where a host keeps its files, and whether
        /// reading arbitrary paths is acceptable, is not this assembler's decision.
        /// </remarks>
        [ThreadStatic]
        private static Func<string, byte[]?>? _binaryResolver;

        /// <summary>
        /// Bytes already fetched during this assembly, so both passes see the same file.
        /// </summary>
        /// <remarks>
        /// Not an optimisation. The two passes have to agree about how many bytes a line
        /// occupies, and a file that changed on disk between them would move every label
        /// after it. Reading once per assembly makes that impossible rather than unlikely.
        /// </remarks>
        [ThreadStatic]
        private static Dictionary<string, byte[]?>? _binaryCache;

        [ThreadStatic]
        private static IReadOnlyDictionary<string, int>? _expressionImports;

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

        /// <summary>
        /// How many diagnostics one assembly reports before giving up.
        /// </summary>
        /// <remarks>
        /// A line that failed emits nothing, so every label after it is at the wrong
        /// address and later lines can fail for that reason alone. Reporting the first
        /// twenty keeps the real errors visible without burying them in consequences.
        /// </remarks>
        public const int MaximumDiagnostics = 20;

        public Z80InlineAssembler(bool enableZ80N = false)
        {
            _enableZ80N = enableZ80N;
        }

        /// <summary>Names an assembler exported, usable in expressions and operands.</summary>
        public Z80SymbolTable? Symbols { get; set; }

        /// <summary>
        /// Supplies the contents of a file named by <c>INCBIN</c>, or null when it is missing.
        /// </summary>
        /// <remarks>
        /// Left null, <c>INCBIN</c> reports that it is unavailable rather than half-working.
        /// </remarks>
        public Func<string, byte[]?>? BinaryResolver { get; set; }

        /// <summary>
        /// Supplies the text of an <c>INCLUDE</c>, or null when it cannot be found.
        /// </summary>
        /// <remarks>
        /// A callback rather than file access of its own: this assembler lives in the core
        /// and has no business deciding where a host keeps its source, or whether reading
        /// arbitrary paths is acceptable. Left null, <c>INCLUDE</c> reports that it is
        /// unavailable instead of half-working.
        /// </remarks>
        public Func<string, string?>? IncludeResolver { get; set; }

        /// <summary>
        /// Evaluates an expression against a fixed set of constants and nothing else.
        /// </summary>
        /// <remarks>
        /// For the preprocessor, whose conditions and repeat counts are decided before any
        /// address is known. Sharing the parser means <c>IF DEBUG &amp; 1</c> means the same
        /// thing there as it would in an operand.
        /// </remarks>
        internal static bool TryEvaluateConstant(
            string text,
            IReadOnlyDictionary<string, int> constants,
            out int value) =>
            new ExpressionParser(text, constants, allowUnresolved: false, currentPc: 0)
                .TryParse(out value);

        /// <summary>Records one diagnostic against the line it came from.</summary>
        private static void Record(
            List<Z80AssemblyDiagnostic> diagnostics,
            ParsedAssemblerLine parsed,
            string message)
        {
            diagnostics.Add(new Z80AssemblyDiagnostic(
                parsed.LineNumber,
                FindColumn(parsed.Text, message),
                parsed.Origin == null ? message : $"{message} (in {parsed.Origin})",
                parsed.Text));
        }

        /// <summary>Finds the column of the token a message complains about.</summary>
        private static int FindColumn(string sourceLine, string message)
        {
            int open = message.IndexOf('\'');
            if (open < 0)
            {
                return 1;
            }

            int close = message.IndexOf('\'', open + 1);
            if (close <= open + 1)
            {
                return 1;
            }

            string token = message[(open + 1)..close];
            int at = sourceLine.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? 1 : at + 1;
        }

        public Z80AssemblyResult Assemble(ushort address, string source)
        {
            ArgumentNullException.ThrowIfNull(source);

            // Instruction widths must be known before forward references can be evaluated.
            var diagnostics = new List<Z80AssemblyDiagnostic>();

            // Macros, repetition, conditionals and includes are resolved before addresses
            // are fixed, because expansion changes how many instructions there are.
            ParsedAssemblerLine[] lines = Z80AssemblerPreprocessor.NeedsExpansion(source)
                ? PreprocessSource(source, IncludeResolver, diagnostics)
                : ParseSource(source);
            if (diagnostics.Count > 0)
            {
                return new Z80AssemblyResult([], diagnostics);
            }

            _expressionImports = Symbols?.ByName;
            _binaryResolver = BinaryResolver;
            _binaryCache = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            var symbols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var equDefinitions = new List<EquDefinition>();

            // The first pass sees every line, so it is where a whole source's worth of
            // syntax errors can be collected. Emitting is not attempted afterwards: a line
            // that failed produced no bytes, so every label past it is at the wrong address
            // and the second pass would only report consequences.
            FirstPass(address, lines, symbols, equDefinitions, _enableZ80N, diagnostics);
            if (diagnostics.Count > 0)
            {
                return new Z80AssemblyResult([], diagnostics);
            }

            if (!ResolveEquDefinitions(equDefinitions, symbols, out string error))
            {
                return Error(error);
            }

            if (!SecondPass(address, lines, symbols, _enableZ80N, out List<Z80AssemblyPatch> patches, out error))
            {
                return Error(error);
            }

            return new Z80AssemblyResult(patches, (string?)null);
        }
        /// <summary>Expands the source, then parses what came out of it.</summary>
        private static ParsedAssemblerLine[] PreprocessSource(
            string source,
            Func<string, string?>? includeResolver,
            List<Z80AssemblyDiagnostic> diagnostics)
        {
            List<Z80PreprocessedLine> expanded =
                Z80AssemblerPreprocessor.Expand(source, includeResolver, diagnostics);

            var lines = new List<ParsedAssemblerLine>(expanded.Count);
            foreach (Z80PreprocessedLine line in expanded)
            {
                string text = StripComment(line.Text).Trim();
                if (text.Length > 0)
                {
                    lines.Add(new ParsedAssemblerLine(line.LineNumber, text, line.Origin));
                }
            }

            return lines.ToArray();
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
        /// <summary>
        /// Fixes addresses, collects symbols, and records everything wrong with the source.
        /// </summary>
        /// <remarks>
        /// Carries on past a bad line rather than stopping, so one sitting reports every
        /// mistake. The line's own bytes are skipped, which is why the second pass is not
        /// attempted when anything was reported.
        /// </remarks>
        private static void FirstPass(
            ushort address,
            IReadOnlyList<ParsedAssemblerLine> lines,
            Dictionary<string, int> symbols,
            List<EquDefinition> equDefinitions,
            bool enableZ80N,
            List<Z80AssemblyDiagnostic> diagnostics)
        {
            string error = string.Empty;
            ushort pc = address;
            _expressionSymbols = symbols;
            _expressionLocalScope = null;
            _allowUnresolvedExpressions = true;
            _suppressRelativeRangeErrors = true;

            foreach (ParsedAssemblerLine parsed in lines)
            {
                string line = parsed.Text;
                if (diagnostics.Count >= MaximumDiagnostics)
                {
                    return;
                }

                if (!ExtractLabels(ref line, parsed.LineNumber, pc, symbols, out error))
                {
                    Record(diagnostics, parsed, error);
                    continue;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                if (TryParseEqu(line, out string name, out string expression))
                {
                    if (!IsValidIdentifier(name))
                    {
                        Record(diagnostics, parsed, $"Invalid EQU name '{name}'.");
                        continue;
                    }

                    if (symbols.ContainsKey(name) || equDefinitions.Any(definition => definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        Record(diagnostics, parsed, $"Symbol '{name}' is already defined.");
                        continue;
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
                        Record(diagnostics, parsed, $"Invalid ORG expression '{orgExpression}'.");
                        continue;
                    }

                    _allowUnresolvedExpressions = previousAllowUnresolved;
                    pc = (ushort)newPc;
                    continue;
                }

                var scratch = new List<byte>();
                _expressionPc = pc;
                if (!TryAssembleLine(pc, line, scratch, enableZ80N, out error))
                {
                    Record(diagnostics, parsed, error);
                    continue;
                }

                pc = unchecked((ushort)(pc + scratch.Count));
            }
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
            bool enableZ80N,
            out List<Z80AssemblyPatch> patches,
            out string error)
        {
            patches = [];
            error = string.Empty;
            ushort pc = address;
            ushort segmentStart = pc;
            var segment = new List<byte>();
            _expressionSymbols = symbols;
            _expressionLocalScope = null;
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
                if (!TryAssembleLine(pc, line, emitted, enableZ80N, out error))
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
        private static bool TryAssembleLine(ushort pc, string line, List<byte> output, bool enableZ80N, out string error)
        {
            try
            {
                return TryAssembleLineCore(pc, line, output, enableZ80N, out error);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                error = ex.Message;
                return false;
            }
        }
        private static bool TryAssembleLineCore(ushort pc, string line, List<byte> output, bool enableZ80N, out string error)
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

            // DEFM is DEFB by another name; both already take quoted strings.
            if (op is "DM" or "DEFM")
            {
                return EmitBytes(operands, output, out error);
            }

            if (op is "DS" or "DEFS")
            {
                return EmitStorage(operands, output, out error);
            }

            if (op is "ALIGN")
            {
                return EmitAlignment(pc, operands, output, out error);
            }

            if (op is "INCBIN")
            {
                return EmitBinary(operands, output, out error);
            }

            if (enableZ80N)
            {
                if (TryEmitZ80N(op, operands, output, out bool handled, out error))
                {
                    return true;
                }

                if (handled)
                {
                    return false;
                }
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

        private static bool TryEmitZ80N(
            string op,
            string[] operands,
            List<byte> output,
            out bool handled,
            out string error)
        {
            handled = true;
            error = string.Empty;

            byte fixedOpcode = op switch
            {
                "SWAPNIB" => 0x23,
                "OUTINB" => 0x90,
                "PIXELDN" => 0x93,
                "PIXELAD" => 0x94,
                "SETAE" => 0x95,
                "LDIX" => 0xA4,
                "LDWS" => 0xA5,
                "LDDX" => 0xAC,
                "LDIRX" => 0xB4,
                "LDPIRX" => 0xB7,
                "LDDRX" => 0xBC,
                _ => 0
            };
            if (fixedOpcode != 0)
            {
                if (operands.Length != 0)
                {
                    error = $"{op} takes no operands.";
                    return false;
                }

                EmitEd(output, fixedOpcode);
                return true;
            }

            if (op == "MIRROR")
            {
                return EmitFixedZ80NOperands(op, operands, ["A"], 0x24, output, out error);
            }

            if (op == "MUL")
            {
                return EmitFixedZ80NOperands(op, operands, ["D", "E"], 0x30, output, out error);
            }

            byte barrelOpcode = op switch
            {
                "BSLA" => 0x28,
                "BSRA" => 0x29,
                "BSRL" => 0x2A,
                "BSRF" => 0x2B,
                "BRLC" => 0x2C,
                _ => 0
            };
            if (barrelOpcode != 0)
            {
                return EmitFixedZ80NOperands(op, operands, ["DE", "B"], barrelOpcode, output, out error);
            }

            if (op == "TEST")
            {
                if (operands.Length == 1 && TryNumber(operands[0], out int value))
                {
                    EmitEd(output, 0x27);
                    output.Add(CheckedByte(value));
                    return true;
                }

                error = "TEST requires one byte operand.";
                return false;
            }

            if (op == "NEXTREG")
            {
                if (operands.Length == 2 && TryNumber(operands[0], out int register))
                {
                    byte checkedRegister = CheckedByte(register);
                    if (operands[1].Equals("A", StringComparison.OrdinalIgnoreCase))
                    {
                        EmitEd(output, 0x92);
                        output.Add(checkedRegister);
                        return true;
                    }

                    if (TryNumber(operands[1], out int value))
                    {
                        EmitEd(output, 0x91);
                        output.Add(checkedRegister);
                        output.Add(CheckedByte(value));
                        return true;
                    }
                }

                error = "NEXTREG requires register,value or register,A.";
                return false;
            }

            if (op == "ADD" && operands.Length == 2)
            {
                byte registerOffset = operands[0].ToUpperInvariant() switch
                {
                    "HL" => 0,
                    "DE" => 1,
                    "BC" => 2,
                    _ => byte.MaxValue
                };
                if (registerOffset != byte.MaxValue)
                {
                    if (operands[1].Equals("A", StringComparison.OrdinalIgnoreCase))
                    {
                        EmitEd(output, (byte)(0x31 + registerOffset));
                        return true;
                    }

                    if (TryNumber(operands[1], out int value))
                    {
                        EmitEd(output, (byte)(0x34 + registerOffset));
                        EmitWord(output, value);
                        return true;
                    }
                }

                handled = false;
                return false;
            }

            if (op == "PUSH")
            {
                if (operands.Length == 1 && TryNumber(operands[0], out int value))
                {
                    if (value is < 0 or > 0xFFFF)
                    {
                        error = "PUSH immediate operand is outside 16-bit range.";
                        return false;
                    }

                    EmitEd(output, 0x8A);
                    output.Add((byte)(value >> 8));
                    output.Add((byte)value);
                    return true;
                }

                handled = false;
                return false;
            }

            if (op == "JP" && operands.Length == 1 && operands[0].Equals("(C)", StringComparison.OrdinalIgnoreCase))
            {
                EmitEd(output, 0x98);
                return true;
            }

            handled = false;
            return false;
        }

        private static bool EmitFixedZ80NOperands(
            string op,
            string[] actual,
            string[] expected,
            byte opcode,
            List<byte> output,
            out string error)
        {
            if (actual.Length == expected.Length
                && actual.Zip(expected).All(pair => pair.First.Equals(pair.Second, StringComparison.OrdinalIgnoreCase)))
            {
                EmitEd(output, opcode);
                error = string.Empty;
                return true;
            }

            error = $"Unsupported {op} operand form.";
            return false;
        }

        private static void EmitEd(List<byte> output, byte opcode)
        {
            output.Add(0xED);
            output.Add(opcode);
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
        /// <summary>
        /// Reserves space: <c>DS count</c>, or <c>DS count, fill</c>.
        /// </summary>
        /// <remarks>
        /// The count is resolved without allowing forward references, because the two
        /// passes have to agree about how many bytes this line occupies and a count that
        /// read as zero in the first pass would put every later label in the wrong place.
        /// </remarks>
        private static bool EmitStorage(string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length is 0 or > 2)
            {
                error = "DS takes a byte count and an optional fill value.";
                return false;
            }

            if (!TryResolvedNumber(operands[0], out int count) || count is < 0 or > 0x10000)
            {
                error = $"Invalid DS count '{operands[0]}'; it must not use a forward reference.";
                return false;
            }

            byte fill = 0;
            if (operands.Length == 2)
            {
                if (!TryNumber(operands[1], out int fillValue) || fillValue is < -128 or > 0xFF)
                {
                    error = $"Invalid DS fill value '{operands[1]}'.";
                    return false;
                }

                fill = unchecked((byte)fillValue);
            }

            for (int index = 0; index < count; index++)
            {
                output.Add(fill);
            }

            return true;
        }

        /// <summary>
        /// Emits the contents of a file: <c>INCBIN "name"</c>, optionally with a skip and a
        /// length.
        /// </summary>
        /// <remarks>
        /// Skip and length are resolved without forward references, for the same reason
        /// <c>DS</c> is: they decide how many bytes this line occupies, and a count that read
        /// as zero in the first pass would misplace every label after it.
        /// </remarks>
        private static bool EmitBinary(string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length is 0 or > 3)
            {
                error = "INCBIN takes a file name and an optional skip and length.";
                return false;
            }

            string name = operands[0].Trim().Trim('"');
            if (name.Length == 0)
            {
                error = "INCBIN needs a file name.";
                return false;
            }

            if (!TryGetBinary(name, out byte[] content, out error))
            {
                return false;
            }

            int skip = 0;
            if (operands.Length >= 2)
            {
                if (!TryResolvedNumber(operands[1], out skip) || skip < 0)
                {
                    error = $"Invalid INCBIN skip '{operands[1]}'; it must not use a forward reference.";
                    return false;
                }
            }

            if (skip > content.Length)
            {
                error = $"INCBIN skip {skip} is past the end of '{name}', which is {content.Length} byte(s).";
                return false;
            }

            int length = content.Length - skip;
            if (operands.Length == 3)
            {
                if (!TryResolvedNumber(operands[2], out length) || length < 0)
                {
                    error = $"Invalid INCBIN length '{operands[2]}'; it must not use a forward reference.";
                    return false;
                }

                if (skip + length > content.Length)
                {
                    error = $"INCBIN wants {length} byte(s) from offset {skip} of '{name}', "
                        + $"which holds {content.Length}.";
                    return false;
                }
            }

            output.AddRange(content.AsSpan(skip, length));
            return true;
        }

        /// <summary>Fetches a file once per assembly, through the host's resolver.</summary>
        private static bool TryGetBinary(string name, out byte[] content, out string error)
        {
            content = [];
            error = string.Empty;

            Dictionary<string, byte[]?>? cache = _binaryCache;
            if (cache != null && cache.TryGetValue(name, out byte[]? cached))
            {
                if (cached == null)
                {
                    error = $"INCBIN '{name}' could not be read.";
                    return false;
                }

                content = cached;
                return true;
            }

            Func<string, byte[]?>? resolver = _binaryResolver;
            if (resolver == null)
            {
                error = $"INCBIN '{name}' is not available here; no file can be loaded.";
                return false;
            }

            byte[]? fetched = resolver(name);
            cache?.TryAdd(name, fetched);
            if (fetched == null)
            {
                error = $"INCBIN '{name}' could not be read.";
                return false;
            }

            content = fetched;
            return true;
        }

        /// <summary>Pads with zeroes up to the next multiple of a boundary.</summary>
        private static bool EmitAlignment(ushort pc, string[] operands, List<byte> output, out string error)
        {
            error = string.Empty;
            if (operands.Length != 1)
            {
                error = "ALIGN takes one boundary.";
                return false;
            }

            if (!TryResolvedNumber(operands[0], out int boundary)
                || boundary < 1
                || boundary > 0x4000
                || (boundary & (boundary - 1)) != 0)
            {
                error = $"Invalid ALIGN boundary '{operands[0]}'; it must be a power of two.";
                return false;
            }

            int padding = (boundary - (pc % boundary)) % boundary;
            for (int index = 0; index < padding; index++)
            {
                output.Add(0);
            }

            return true;
        }

        /// <summary>
        /// Evaluates an expression that must not contain a forward reference.
        /// </summary>
        /// <remarks>
        /// For the directives whose value decides how much space a line takes. Everything
        /// else may refer forwards, because its width is known before its value is.
        /// </remarks>
        private static bool TryResolvedNumber(string text, out int value)
        {
            bool previous = _allowUnresolvedExpressions;
            _allowUnresolvedExpressions = false;
            try
            {
                return TryNumber(text, out value);
            }
            finally
            {
                _allowUnresolvedExpressions = previous;
            }
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

                if (IsLocalLabel(label))
                {
                    if (string.IsNullOrEmpty(_expressionLocalScope))
                    {
                        error = $"Line {lineNumber}: local label '{label}' has no preceding label to belong to.";
                        return false;
                    }

                    string qualified = QualifyLocalLabel(label, _expressionLocalScope);
                    if (symbols != null && !symbols.TryAdd(qualified, value))
                    {
                        error = $"Line {lineNumber}: local label '{label}' is already defined in '{_expressionLocalScope}'.";
                        return false;
                    }
                }
                else
                {
                    if (symbols != null && !symbols.TryAdd(label, value))
                    {
                        error = $"Line {lineNumber}: symbol '{label}' is already defined.";
                        return false;
                    }

                    // Everything after this label, until the next one, is its scope.
                    _expressionLocalScope = label;
                }

                line = line.Trim();
            }

            return true;
        }

        /// <summary>A name beginning with a dot belongs to the label above it.</summary>
        private static bool IsLocalLabel(string label) =>
            label.Length > 1 && label[0] == '.';

        private static string QualifyLocalLabel(string label, string scope) => scope + label;
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
        private readonly struct ParsedAssemblerLine(int lineNumber, string text, string? origin = null)
        {
            public int LineNumber { get; } = lineNumber;
            public string Text { get; } = text;

            /// <summary>Where an expanded line came from, so a diagnostic can say.</summary>
            public string? Origin { get; } = origin;
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

                if (!TryParseBitwiseOr(out value))
                {
                    return false;
                }

                SkipWhiteSpace();
                return _index == _text.Length;
            }

            /// <summary>
            /// The bitwise and shift levels, in C's precedence order.
            /// </summary>
            /// <remarks>
            /// Masks are the commonest expression in Z80 source and there was no way to
            /// write one. C's order is used rather than anything assembler-specific because
            /// it is what someone reading `flags & ~mask | bit << 3` will assume.
            /// </remarks>
            private bool TryParseBitwiseOr(out int value)
            {
                if (!TryParseBitwiseXor(out value))
                {
                    return false;
                }

                while (true)
                {
                    SkipWhiteSpace();

                    // Not the start of a '||', which this grammar has no use for.
                    if (_index + 1 < _text.Length && _text[_index] == '|' && _text[_index + 1] == '|')
                    {
                        return true;
                    }

                    if (!TryConsume('|'))
                    {
                        return true;
                    }

                    if (!TryParseBitwiseXor(out int rhs))
                    {
                        return false;
                    }

                    value |= rhs;
                }
            }

            private bool TryParseBitwiseXor(out int value)
            {
                if (!TryParseBitwiseAnd(out value))
                {
                    return false;
                }

                while (true)
                {
                    SkipWhiteSpace();
                    if (!TryConsume('^'))
                    {
                        return true;
                    }

                    if (!TryParseBitwiseAnd(out int rhs))
                    {
                        return false;
                    }

                    value ^= rhs;
                }
            }

            private bool TryParseBitwiseAnd(out int value)
            {
                if (!TryParseShift(out value))
                {
                    return false;
                }

                while (true)
                {
                    SkipWhiteSpace();
                    if (_index + 1 < _text.Length && _text[_index] == '&' && _text[_index + 1] == '&')
                    {
                        return true;
                    }

                    if (!TryConsume('&'))
                    {
                        return true;
                    }

                    if (!TryParseShift(out int rhs))
                    {
                        return false;
                    }

                    value &= rhs;
                }
            }

            private bool TryParseShift(out int value)
            {
                if (!TryParseAdditive(out value))
                {
                    return false;
                }

                while (true)
                {
                    SkipWhiteSpace();
                    if (TryConsumePair('<', '<'))
                    {
                        if (!TryParseAdditive(out int rhs) || rhs is < 0 or > 31)
                        {
                            return false;
                        }

                        value <<= rhs;
                        continue;
                    }

                    if (TryConsumePair('>', '>'))
                    {
                        if (!TryParseAdditive(out int rhs) || rhs is < 0 or > 31)
                        {
                            return false;
                        }

                        value >>= rhs;
                        continue;
                    }

                    return true;
                }
            }

            /// <summary>Consumes a two-character operator, or nothing at all.</summary>
            private bool TryConsumePair(char first, char second)
            {
                if (_index + 1 >= _text.Length || _text[_index] != first || _text[_index + 1] != second)
                {
                    return false;
                }

                _index += 2;
                return true;
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
                    if (!TryParseBitwiseOr(out value))
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
                    if (symbols != null
                        && IsLocalLabel(identifier)
                        && !string.IsNullOrEmpty(_expressionLocalScope)
                        && symbols.TryGetValue(
                            QualifyLocalLabel(identifier, _expressionLocalScope),
                            out value))
                    {
                        return true;
                    }

                    if (symbols != null && symbols.TryGetValue(identifier, out value))
                    {
                        return true;
                    }

                    if (_expressionImports != null && _expressionImports.TryGetValue(identifier, out value))
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
