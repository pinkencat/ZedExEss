using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Debugging;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Spectrum.Video;
using ZedExEss.Z80CPU;
using ZedExEss.Zx8x.Core;
using ZedExEss.Zx8x.Memory;

namespace ZedExEss.Diagnostics
{
    /// <summary>Output settings for the headless debugger verification.</summary>
    public sealed class DebuggerVerificationOptions
    {
        public string? OutputPath { get; init; }
    }
    /// <summary>
    /// Exercises assembler/disassembler symmetry and debugger stop semantics on a minimal machine.
    /// </summary>
    /// <remarks>
    /// Step and watchpoint checks still pass through <see cref="SpectrumEmulator"/> so they verify
    /// the production instruction-boundary timing path rather than calling the CPU in isolation.
    /// </remarks>
    public static class DebuggerVerificationRunner
    {
        private const ushort TestAddress = 0x8000;
        public static int Run(DebuggerVerificationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            string outputPath = ResolveOutputPath(options.OutputPath);
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
            var log = new DebuggerVerificationLog(writer);
            try
            {
                log.WriteLine("Debugger headless verification");
                log.WriteLine($"Output: {outputPath}");
                log.WriteLine(string.Empty);

                log.Check("Disassemble representative Z80 opcodes", VerifyDisassembler);
                log.Check("Assemble representative Z80 snippets", VerifyAssembler);
                log.Check("Assembler bitwise and shift expressions", VerifyAssemblerExpressions);
                log.Check("Assembler storage and text directives", VerifyAssemblerDirectives);
                log.Check("Assembler local label scoping", VerifyAssemblerLocalLabels);
                log.Check("Assembler reports every error with a position", VerifyAssemblerDiagnostics);
                log.Check("Imported symbols name addresses", VerifySymbolImport);
                log.Check("Symbol loading state drives both tools", VerifySymbolLoadState);
                log.Check("Source-level debugging files map addresses to lines", VerifySourceLevelImport);
                log.Check("Source view follows the processor", VerifySourceWindow);
                log.Check("Source-level stepping stops on the next line", VerifySourceStepping);
                log.Check("Stepping over a call returns to the caller", VerifyStepOverInstruction);
                log.Check("INCBIN emits a file through the host", VerifyIncludeBinary);
                log.Check("Macros, repetition and conditionals expand", VerifyAssemblerMacros);
                log.Check("Expansion errors point at the user's line", VerifyMacroDiagnostics);
                log.Check("Execution breakpoint stops before instruction", VerifyExecutionBreakpoint);
                log.Check("Single-step advances through emulator timing path", VerifyStepInto);
                log.Check("Step-over uses temporary breakpoint after CALL", VerifyStepOver);
                log.Check("Memory and port watchpoints pause after instruction", VerifyWatchpoints);
                log.Check("Project and patch debugger state through the portable view service", VerifyPortableViewService);
                log.Check("Debug ZX80/ZX81 execution and memory through the shared debugger", VerifyZx8xDebugger);

                log.WriteLine(string.Empty);
                log.WriteLine(log.Failed == 0 ? "Result: PASS" : $"Result: FAIL ({log.Failed} failed checks)");
                return log.Failed == 0 ? 0 : 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.WriteLine($"Error: {ex.Message}");
                Debug.WriteLine(ex.ToString());
                return 3;
            }
        }
        private static void VerifyDisassembler()
        {
            var disassembler = new Z80Disassembler();
            SpectrumMemory memory = CreateMemory();
            Write(memory, TestAddress, 0x21, 0x34, 0x12);
            Require(disassembler.Disassemble(memory, TestAddress).Text == "LD HL,1234", "LD HL,nn disassembly failed.");

            Write(memory, TestAddress, 0x18, 0x02);
            Z80DisassembledInstruction jr = disassembler.Disassemble(memory, TestAddress);
            Require(jr.Text == "JR 8004" && jr.Length == 2, "JR disassembly failed.");

            Write(memory, TestAddress, 0xCD, 0x00, 0x90);
            Z80DisassembledInstruction call = disassembler.Disassemble(memory, TestAddress);
            Require(call.Text == "CALL 9000" && call.IsCallLike, "CALL disassembly failed.");

            Write(memory, TestAddress, 0xCB, 0x46);
            Require(disassembler.Disassemble(memory, TestAddress).Text == "BIT 0,(HL)", "CB disassembly failed.");

            Write(memory, TestAddress, 0xED, 0xB0);
            Require(disassembler.Disassemble(memory, TestAddress).Text == "LDIR", "ED disassembly failed.");

            Write(memory, TestAddress, 0xDD, 0xCB, 0xFE, 0x46);
            Require(disassembler.Disassemble(memory, TestAddress).Text == "BIT 0,(IX-02)", "DDCB disassembly failed.");
        }

        /// <summary>
        /// Masks and shifts, which are the commonest expressions in Z80 source and had no
        /// syntax at all.
        /// </summary>
        /// <remarks>
        /// Precedence is asserted as well as arithmetic, because an assembler that computes
        /// <c>1 | 2 &lt;&lt; 3</c> as <c>(1 | 2) &lt;&lt; 3</c> is worse than one that refuses
        /// the expression: it silently assembles the wrong byte.
        /// </remarks>
        private static void VerifyAssemblerExpressions()
        {
            var assembler = new Z80InlineAssembler();

            RequireBytes(assembler.Assemble(TestAddress, "LD A,0F0H & 3CH"), [0x3E, 0x30],
                "Bitwise AND was not evaluated.");
            RequireBytes(assembler.Assemble(TestAddress, "LD A,0C0H | 03H"), [0x3E, 0xC3],
                "Bitwise OR was not evaluated.");
            RequireBytes(assembler.Assemble(TestAddress, "LD A,0FFH ^ 0F0H"), [0x3E, 0x0F],
                "Bitwise XOR was not evaluated.");
            RequireBytes(assembler.Assemble(TestAddress, "LD A,1 << 6"), [0x3E, 0x40],
                "Left shift was not evaluated.");
            RequireBytes(assembler.Assemble(TestAddress, "LD A,80H >> 3"), [0x3E, 0x10],
                "Right shift was not evaluated.");

            // Shift binds tighter than OR, which binds loosest of the three bitwise levels.
            RequireBytes(assembler.Assemble(TestAddress, "LD A,1 | 2 << 3"), [0x3E, 0x11],
                "Shift and OR precedence is wrong.");
            RequireBytes(assembler.Assemble(TestAddress, "LD A,(1 | 2) << 3"), [0x3E, 0x18],
                "Parentheses did not override bitwise precedence.");
            RequireBytes(assembler.Assemble(TestAddress, "LD A,0FH & 3 | 30H"), [0x3E, 0x33],
                "AND should bind tighter than OR.");
            RequireBytes(assembler.Assemble(TestAddress, "LD A,~0F0H & 0FFH"), [0x3E, 0x0F],
                "Complement did not combine with a mask.");

            // And the pieces that already worked must still work alongside them.
            RequireBytes(assembler.Assemble(TestAddress, "LD A,'A' | 20H"), [0x3E, 0x61],
                "A character literal did not combine with a mask.");
            RequireBytes(assembler.Assemble(TestAddress, "LD A,2 + 3 * 4"), [0x3E, 0x0E],
                "Arithmetic precedence regressed.");
        }

        /// <summary>Reserving space, laying down text, and padding to a boundary.</summary>
        private static void VerifyAssemblerDirectives()
        {
            var assembler = new Z80InlineAssembler();

            RequireBytes(assembler.Assemble(TestAddress, "DS 4"), [0x00, 0x00, 0x00, 0x00],
                "DS did not reserve zeroed space.");
            RequireBytes(assembler.Assemble(TestAddress, "DEFS 3,0FFH"), [0xFF, 0xFF, 0xFF],
                "DS with a fill value did not repeat it.");
            RequireBytes(assembler.Assemble(TestAddress, "DEFM \"Hi\",0"), [0x48, 0x69, 0x00],
                "DEFM did not emit text.");
            RequireBytes(assembler.Assemble(TestAddress, "DM \"A\""), [0x41], "DM is not a DEFM alias.");

            // DS has to move the assembler on, or every label after it is wrong.
            Z80AssemblyResult reserved = assembler.Assemble(0x8000, """
                DS 5
                here: DB 0
                LD HL,here
                """);
            Require(reserved.Success, reserved.Error ?? "DS with a following label failed.");
            Require(reserved.Bytes.AsSpan()[6..].SequenceEqual(new byte[] { 0x21, 0x05, 0x80 }),
                "A label after DS did not account for the reserved bytes.");

            // ALIGN pads from wherever the code happens to have reached.
            Z80AssemblyResult aligned = assembler.Assemble(0x8000, """
                DB 1,2,3
                ALIGN 4
                DB 9
                """);
            Require(aligned.Success, aligned.Error ?? "ALIGN failed.");
            Require(aligned.Bytes.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 0, 9 }),
                "ALIGN did not pad to the next boundary.");

            Z80AssemblyResult alignedAlready = assembler.Assemble(0x8000, "ALIGN 4\nDB 9");
            Require(alignedAlready.Success && alignedAlready.Bytes.Length == 1,
                "ALIGN padded an address that was already aligned.");

            // A count that cannot be known in the first pass must be refused, not guessed:
            // guessing puts every later label in the wrong place.
            Z80AssemblyResult forward = assembler.Assemble(0x8000, "DS later\nlater EQU 4");
            Require(!forward.Success, "DS accepted a forward reference for its count.");
            Require(assembler.Assemble(0x8000, "ALIGN 3").Success == false,
                "ALIGN accepted a boundary that is not a power of two.");
        }

        /// <summary>
        /// A local label belongs to the label above it, so the same name can be used again.
        /// </summary>
        /// <remarks>
        /// This is the point of the feature: two routines in one patch both wanting
        /// <c>.loop</c>. Without scoping the second definition is a duplicate-symbol error,
        /// which is why scratch patches end up full of names like <c>loop2</c>.
        /// </remarks>
        private static void VerifyAssemblerLocalLabels()
        {
            var assembler = new Z80InlineAssembler();

            Z80AssemblyResult twice = assembler.Assemble(0x8000, """
                first:
                .loop: DJNZ .loop
                second:
                .loop: DJNZ .loop
                """);
            Require(twice.Success, twice.Error ?? "The same local label could not be reused.");
            Require(twice.Bytes.AsSpan().SequenceEqual(new byte[] { 0x10, 0xFE, 0x10, 0xFE }),
                "Each local label should have resolved inside its own scope.");

            // A local label must not be reachable from another scope.
            Z80AssemblyResult leaked = assembler.Assemble(0x8000, """
                first:
                .inner: NOP
                second:
                JP .inner
                """);
            Require(!leaked.Success, "A local label leaked out of the label it belongs to.");

            // Duplicates inside one scope are still an error.
            Z80AssemblyResult duplicate = assembler.Assemble(0x8000, """
                only:
                .twice: NOP
                .twice: NOP
                """);
            Require(!duplicate.Success, "A duplicated local label was accepted.");

            // And a local label with nothing above it has no scope to belong to.
            Require(!assembler.Assemble(0x8000, ".orphan: NOP").Success,
                "A local label was accepted with no preceding label.");

            // Global labels still work, and still reach across scopes.
            Z80AssemblyResult mixed = assembler.Assemble(0x8000, """
                start:
                .skip: NOP
                JR start
                """);
            Require(mixed.Success, mixed.Error ?? "Mixing global and local labels failed.");
            Require(mixed.Bytes.AsSpan().SequenceEqual(new byte[] { 0x00, 0x18, 0xFD }),
                "A global label stopped resolving once a local one existed.");
        }
        /// <summary>
        /// Every mistake in one go, each with a line, a column and the line itself.
        /// </summary>
        /// <remarks>
        /// Stopping at the first error makes fixing a patch a sequence of one-error
        /// round trips. The column is best-effort - it is found by locating the token the
        /// message names - so what is asserted is that it points *into* the offending
        /// token, not that it is exact to the character.
        /// </remarks>
        private static void VerifyAssemblerDiagnostics()
        {
            var assembler = new Z80InlineAssembler();

            Z80AssemblyResult result = assembler.Assemble(0x8000, """
                LD A,1
                LD BOGUS,2
                NOP
                LD HL,(((
                """);

            Require(!result.Success, "Broken source assembled successfully.");
            Require(result.Diagnostics.Count >= 2,
                $"Only {result.Diagnostics.Count} diagnostic(s) were reported; both bad lines should be.");

            Z80AssemblyDiagnostic first = result.Diagnostics[0];
            Require(first.Line == 2, $"The first diagnostic blamed line {first.Line}, not 2.");
            Require(first.SourceLine.Contains("BOGUS", StringComparison.OrdinalIgnoreCase),
                "The diagnostic did not carry the line it came from.");
            Require(first.Column >= 1 && first.Column <= first.SourceLine.Length,
                $"The column {first.Column} is outside the line.");

            Require(result.Diagnostics.Any(diagnostic => diagnostic.Line == 4),
                "The assembler stopped before reaching the second bad line.");

            // The formatted form is what a user actually reads.
            string formatted = first.Format();
            Require(formatted.Contains("Line 2,", StringComparison.Ordinal),
                $"The formatted diagnostic lost its position: {formatted}");
            Require(formatted.Contains('^'), "The formatted diagnostic has no caret.");

            // Error keeps working for callers that only ever showed a string, and now
            // carries all of them rather than the first.
            Require(result.Error != null && result.Error.Contains("Line 4,", StringComparison.Ordinal),
                "The joined error text does not mention every diagnostic.");

            // Good source still reports nothing at all.
            Z80AssemblyResult good = assembler.Assemble(0x8000, "LD A,1\nNOP");
            Require(good.Success && good.Diagnostics.Count == 0 && good.Error == null,
                "Valid source produced diagnostics.");
        }

        /// <summary>
        /// Names imported from an assembler reach both the disassembler and the assembler.
        /// </summary>
        /// <remarks>
        /// This is the difference between debugging your own code and debugging a hex dump.
        /// The reader is asserted against the shapes assemblers actually emit rather than
        /// one canonical format, and the odd unparseable line is required to be skipped
        /// rather than to fail the import: losing three thousand good names to one bad line
        /// would make the feature useless in exactly the situation it is wanted.
        /// </remarks>
        private static void VerifySymbolImport()
        {
            Z80SymbolTable symbols = Z80SymbolTable.Parse(
                "init_screen: EQU $8034\n"
                + "main_loop = $8040\n"
                + "buffer EQU 9000h\n"
                + "8050 helper          ; address first, as .lbl files write it\n"
                + "decimal_name 32768\n"
                + "// a comment line\n"
                + "\n"
                + "this line makes no sense at all\n");

            Require(symbols.Count == 5, $"Imported {symbols.Count} symbols, expected five.");
            Require(symbols.SkippedLines == 1,
                $"Skipped {symbols.SkippedLines} lines, expected exactly the nonsense one.");

            Require(symbols.TryGetAddress("init_screen", out int init) && init == 0x8034,
                "A 'name: EQU $hex' line did not import.");
            Require(symbols.TryGetAddress("main_loop", out int loop) && loop == 0x8040,
                "A 'name = $hex' line did not import.");
            Require(symbols.TryGetAddress("buffer", out int buffer) && buffer == 0x9000,
                "A 'name EQU hexh' line did not import.");
            Require(symbols.TryGetAddress("helper", out int helper) && helper == 0x8050,
                "An address-first line did not import.");
            Require(symbols.TryGetAddress("decimal_name", out int decimalName) && decimalName == 32768,
                "A decimal address did not import.");

            Require(symbols.TryGetName(0x8034, out string named) && named == "init_screen",
                "The reverse lookup does not name an imported address.");

            // The disassembler shows the name instead of the number.
            SpectrumMemory memory = CreateMemory();
            Write(memory, TestAddress, 0xCD, 0x34, 0x80);   // CALL $8034

            var plain = new Z80Disassembler();
            Require(plain.Disassemble(memory, TestAddress).Text == "CALL 8034",
                "The unadorned disassembly changed shape.");

            var named2 = new Z80Disassembler { Symbols = symbols };
            Require(named2.Disassemble(memory, TestAddress).Text == "CALL init_screen",
                $"Symbols were not applied: got '{named2.Disassemble(memory, TestAddress).Text}'.");

            // A byte operand must not be mistaken for an address.
            Write(memory, TestAddress, 0x3E, 0x34);         // LD A,$34
            Require(named2.Disassemble(memory, TestAddress).Text == "LD A,34",
                "A byte operand was rewritten as a symbol.");

            // And the assembler can use the names in operands.
            var assembler = new Z80InlineAssembler { Symbols = symbols };
            RequireBytes(assembler.Assemble(TestAddress, "CALL init_screen"), [0xCD, 0x34, 0x80],
                "An imported symbol could not be used as an operand.");
            RequireBytes(assembler.Assemble(TestAddress, "LD HL,buffer + 2"), [0x21, 0x02, 0x90],
                "An imported symbol could not be used in an expression.");

            // A label in the source shadows an imported name rather than colliding with it.
            Z80AssemblyResult shadowed = assembler.Assemble(0x9100, """
                buffer: NOP
                LD HL,buffer
                """);
            Require(shadowed.Success, shadowed.Error ?? "A source label collided with an import.");
            Require(shadowed.Bytes.AsSpan().SequenceEqual(new byte[] { 0x00, 0x21, 0x00, 0x91 }),
                "The source label did not shadow the imported symbol.");

            // Without an import the assembler is unchanged.
            Require(!new Z80InlineAssembler().Assemble(TestAddress, "CALL init_screen").Success,
                "An unknown symbol assembled without any import.");
        }

        /// <summary>
        /// Macros with arguments, repetition, conditionals and includes.
        /// </summary>
        /// <remarks>
        /// The assertion that matters most is that the same macro can be used twice. A macro
        /// whose body defines a label is only reusable if expansion renames it, and that is
        /// the part naive implementations get wrong - they assemble the first invocation and
        /// fail the second with a duplicate symbol.
        /// </remarks>
        private static void VerifyAssemblerMacros()
        {
            var assembler = new Z80InlineAssembler();

            // Arguments substitute as whole tokens, so a parameter named 'a' must not be
            // found inside another name.
            Z80AssemblyResult simple = assembler.Assemble(0x8000, """
                MACRO loadpair a, b
                LD A,a
                LD B,b
                ENDM
                loadpair 1, 2
                loadpair 3, 4
                """);
            Require(simple.Success, simple.Error ?? "A macro with arguments failed.");
            Require(simple.Bytes.AsSpan().SequenceEqual(new byte[] { 0x3E, 0x01, 0x06, 0x02, 0x3E, 0x03, 0x06, 0x04 }),
                "Macro arguments were not substituted per invocation.");

            // The whole point: a body label, used twice.
            Z80AssemblyResult reused = assembler.Assemble(0x8000, """
                MACRO spin
                .wait: DJNZ .wait
                ENDM
                outer:
                spin
                spin
                """);
            Require(reused.Success, reused.Error ?? "A macro defining a label could not be used twice.");
            Require(reused.Bytes.AsSpan().SequenceEqual(new byte[] { 0x10, 0xFE, 0x10, 0xFE }),
                "Each expansion's label should have been made unique.");

            // REPT unrolls.
            Z80AssemblyResult repeated = assembler.Assemble(0x8000, """
                REPT 3
                NOP
                ENDR
                """);
            Require(repeated.Success, repeated.Error ?? "REPT failed.");
            Require(repeated.Bytes.Length == 3, $"REPT emitted {repeated.Bytes.Length} bytes, not three.");

            // A count may be a constant defined above it.
            Z80AssemblyResult countedRepeat = assembler.Assemble(0x8000, """
                COUNT EQU 4
                REPT COUNT
                NOP
                ENDR
                """);
            Require(countedRepeat.Success, countedRepeat.Error ?? "REPT with a constant count failed.");
            Require(countedRepeat.Bytes.Length == 4,
                $"REPT COUNT emitted {countedRepeat.Bytes.Length} bytes, not four.");

            // Conditionals choose a branch, and the branch not taken emits nothing.
            Z80AssemblyResult conditional = assembler.Assemble(0x8000, """
                DEBUG EQU 1
                IF DEBUG
                LD A,1
                ELSE
                LD A,2
                ENDIF
                """);
            Require(conditional.Success, conditional.Error ?? "IF/ELSE/ENDIF failed.");
            Require(conditional.Bytes.AsSpan().SequenceEqual(new byte[] { 0x3E, 0x01 }),
                "The wrong branch of a conditional was assembled.");

            Z80AssemblyResult negated = assembler.Assemble(0x8000, """
                DEBUG EQU 0
                IF DEBUG
                LD A,1
                ELSE
                LD A,2
                ENDIF
                """);
            Require(negated.Bytes.AsSpan().SequenceEqual(new byte[] { 0x3E, 0x02 }),
                "A false condition did not take the ELSE branch.");

            Z80AssemblyResult defined = assembler.Assemble(0x8000, """
                THING EQU 5
                IFDEF THING
                NOP
                ENDIF
                IFNDEF THING
                HALT
                ENDIF
                """);
            Require(defined.Bytes.AsSpan().SequenceEqual(new byte[] { 0x00 }),
                "IFDEF/IFNDEF did not test whether a constant exists.");

            // A macro may use another macro.
            Z80AssemblyResult nested = assembler.Assemble(0x8000, """
                MACRO twice
                NOP
                NOP
                ENDM
                MACRO fourtimes
                twice
                twice
                ENDM
                fourtimes
                """);
            Require(nested.Success, nested.Error ?? "A macro calling a macro failed.");
            Require(nested.Bytes.Length == 4, $"Nested macros emitted {nested.Bytes.Length} bytes, not four.");

            // INCLUDE is only available when the host supplies the text.
            Require(!assembler.Assemble(0x8000, "INCLUDE \"other.asm\"").Success,
                "INCLUDE worked with no resolver to read it.");

            var withInclude = new Z80InlineAssembler
            {
                IncludeResolver = name => name == "other.asm" ? "LD A,7" : null
            };
            RequireBytes(withInclude.Assemble(0x8000, "INCLUDE \"other.asm\""), [0x3E, 0x07],
                "An INCLUDE supplied by the host did not assemble.");
            Require(!withInclude.Assemble(0x8000, "INCLUDE \"missing.asm\"").Success,
                "A missing INCLUDE was accepted.");

            // Source with none of this in it must go through untouched.
            RequireBytes(assembler.Assemble(0x8000, "LD A,1\nNOP"), [0x3E, 0x01, 0x00],
                "Plain source changed behaviour once expansion existed.");
        }

        /// <summary>
        /// An error inside an expansion names a line the user can actually see.
        /// </summary>
        /// <remarks>
        /// This is the difference between a usable macro assembler and an infuriating one.
        /// The reported line is the invocation, not a position inside a body the user never
        /// wrote out, and the message says which macro it came from.
        /// </remarks>
        private static void VerifyMacroDiagnostics()
        {
            var assembler = new Z80InlineAssembler();

            Z80AssemblyResult result = assembler.Assemble(0x8000, """
                MACRO bad
                LD BOGUS,1
                ENDM
                NOP
                bad
                """);

            Require(!result.Success, "A macro containing a bad instruction assembled.");
            Require(result.Diagnostics.Count >= 1, "No diagnostic was reported for a bad macro body.");

            Z80AssemblyDiagnostic diagnostic = result.Diagnostics[0];
            Require(diagnostic.Line == 5,
                $"The diagnostic blamed line {diagnostic.Line}; the invocation is on line 5.");
            Require(diagnostic.Message.Contains("macro bad", StringComparison.OrdinalIgnoreCase),
                $"The diagnostic does not say which macro it came from: {diagnostic.Message}");

            // Structural mistakes are reported where they start.
            Z80AssemblyResult unterminated = assembler.Assemble(0x8000, "MACRO never\nNOP");
            Require(!unterminated.Success, "A macro with no ENDM assembled.");
            Require(unterminated.Error != null
                    && unterminated.Error.Contains("ENDM", StringComparison.OrdinalIgnoreCase),
                $"A missing ENDM was not named: {unterminated.Error}");

            Z80AssemblyResult openIf = assembler.Assemble(0x8000, "IF 1\nNOP");
            Require(!openIf.Success && openIf.Error != null
                    && openIf.Error.Contains("ENDIF", StringComparison.OrdinalIgnoreCase),
                "A missing ENDIF was not reported.");

            // Wrong argument counts are caught at the invocation.
            Z80AssemblyResult wrongArity = assembler.Assemble(0x8000, """
                MACRO pair a, b
                LD A,a
                ENDM
                pair 1
                """);
            Require(!wrongArity.Success, "A macro accepted the wrong number of arguments.");
            Require(wrongArity.Error != null && wrongArity.Error.Contains("argument", StringComparison.OrdinalIgnoreCase),
                $"The arity error does not mention arguments: {wrongArity.Error}");

            // A condition that cannot be decided here says so rather than guessing.
            Z80AssemblyResult lateConstant = assembler.Assemble(0x8000, """
                IF LATER
                NOP
                ENDIF
                LATER EQU 1
                """);
            Require(!lateConstant.Success,
                "A condition depending on a later constant was silently decided.");
        }

        /// <summary>
        /// The state object both debugger windows use to load, apply and forget symbols.
        /// </summary>
        /// <remarks>
        /// The hosts contribute a button and a label each; everything that decides what
        /// happens lives here, so this is where it is checked. The empty-file case is the
        /// one worth being deliberate about: a file that parses to nothing almost always
        /// means the wrong file was picked, and reporting success would leave the user
        /// staring at an unchanged disassembly with no explanation.
        /// </remarks>
        private static void VerifySymbolLoadState()
        {
            var disassembler = new Z80Disassembler();
            var assembler = new Z80InlineAssembler();
            var state = new SpectrumDebuggerSymbolState(disassembler, assembler);

            Require(!state.HasSymbols, "A fresh symbol state claimed to hold symbols.");
            Require(state.Description == "No symbols loaded",
                $"The empty description reads '{state.Description}'.");
            Require(disassembler.Symbols == null && assembler.Symbols == null,
                "A fresh symbol state had already pointed the tools somewhere.");

            string directory = Path.Combine(Path.GetTempPath(), $"ZedExEss-symbols-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                string good = Path.Combine(directory, "game.sym");
                File.WriteAllText(good, "init_screen: EQU $8034\nnonsense line here\n");

                SpectrumSymbolLoadResult loaded = state.Load(good);
                Require(loaded.Loaded, $"A valid symbol file did not load: {loaded.Summary}");
                Require(loaded.Count == 1, $"Loaded {loaded.Count} symbols, expected one.");
                Require(loaded.SkippedLines == 1,
                    $"Reported {loaded.SkippedLines} skipped lines, expected one.");
                Require(loaded.Summary.Contains("game.sym", StringComparison.Ordinal),
                    $"The summary does not name the file: {loaded.Summary}");
                Require(loaded.Summary.Contains("skipped", StringComparison.OrdinalIgnoreCase),
                    $"The summary hides that a line was skipped: {loaded.Summary}");

                // Both tools must now be pointed at the same table.
                Require(state.HasSymbols, "The state does not report the symbols it loaded.");
                Require(ReferenceEquals(disassembler.Symbols, state.Symbols)
                        && ReferenceEquals(assembler.Symbols, state.Symbols),
                    "Loading did not point both the disassembler and the assembler at the table.");

                SpectrumMemory memory = CreateMemory();
                Write(memory, TestAddress, 0xCD, 0x34, 0x80);
                Require(disassembler.Disassemble(memory, TestAddress).Text == "CALL init_screen",
                    "The disassembler is not using the loaded symbols.");
                RequireBytes(assembler.Assemble(TestAddress, "CALL init_screen"), [0xCD, 0x34, 0x80],
                    "The assembler is not using the loaded symbols.");

                // A file with nothing recognisable in it is a failure, and changes nothing.
                string empty = Path.Combine(directory, "empty.sym");
                File.WriteAllText(empty, "this is not a symbol file at all\n");
                SpectrumSymbolLoadResult rejected = state.Load(empty);
                Require(!rejected.Loaded, "A file with no symbols reported success.");
                Require(rejected.Summary.Contains("No symbols", StringComparison.OrdinalIgnoreCase),
                    $"The rejection does not say why: {rejected.Summary}");
                Require(disassembler.Disassemble(memory, TestAddress).Text == "CALL init_screen",
                    "A failed load discarded the symbols that were already working.");

                // A missing file is reported, not thrown.
                SpectrumSymbolLoadResult missing = state.Load(Path.Combine(directory, "nope.sym"));
                Require(!missing.Loaded, "A missing file reported success.");
                Require(missing.Summary.Length > 0, "A missing file produced no explanation.");

                // Clearing goes back to plain addresses.
                state.Clear();
                Require(!state.HasSymbols, "Clearing left symbols behind.");
                Require(disassembler.Symbols == null && assembler.Symbols == null,
                    "Clearing did not unhook the tools.");
                Require(disassembler.Disassemble(memory, TestAddress).Text == "CALL 8034",
                    "The disassembly still shows a name after clearing.");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        /// <summary>
        /// A source-level debugging file gives both names and the line each address came from.
        /// </summary>
        /// <remarks>
        /// The reader was written against the documented record shape - fields separated by
        /// pipes, metadata lines beginning with an empty field, <c>T</c> for emitted code and
        /// <c>L</c>/<c>D</c> for names - and not against output from a real toolchain, because
        /// there is no sample in the tree. So what is asserted here is that shape, plus the
        /// tolerance that makes a wrong assumption visible: unrecognised records are counted,
        /// and a file yielding nothing is a reported failure rather than a silent success.
        /// </remarks>
        private static void VerifySourceLevelImport()
        {
            const string Sld = """
                |SLD.data.version|1
                ||K|KEYWORDS|WPMEM,LOGPOINT,ASSERTION
                main.asm|10|main.asm|10|0|32768|T|
                main.asm|11|main.asm|11|0|32769|T|
                main.asm|10|main.asm|10|0|32770|T|
                draw.asm|5|draw.asm|5|0|33000|T|
                main.asm|3|main.asm|3|0|32768|L|,init_screen,
                main.asm|4|main.asm|4|0|1234|D|,SCREEN_SIZE,
                this is not a record
                main.asm|9|main.asm|9|0|notanumber|T|
                """;

            Z80SldImport import = Z80SldReader.Parse(Sld);

            Require(import.Version == "1", $"The version marker read as '{import.Version}'.");
            Require(import.TraceRecords == 4,
                $"Understood {import.TraceRecords} trace records, expected four.");
            Require(import.LabelRecords == 2,
                $"Understood {import.LabelRecords} name records, expected two.");
            Require(import.SkippedLines == 2,
                $"Skipped {import.SkippedLines} records, expected the two malformed ones.");
            Require(import.IsUsable, "A file with trace records and names was called unusable.");

            // Addresses map forward to the line that produced them.
            Require(import.SourceMap.TryGetLocation(32768, out Z80SourceLocation first)
                    && first.File == "main.asm" && first.Line == 10,
                "An address did not map back to its source line.");
            Require(import.SourceMap.TryGetLocation(33000, out Z80SourceLocation other)
                    && other.File == "draw.asm" && other.Line == 5,
                "A second source file did not map.");
            Require(!import.SourceMap.TryGetLocation(40000, out _),
                "An address with no record produced a location.");

            // And lines map back to where they start. Line 10 emitted at 32768 and again at
            // 32770; a breakpoint on it belongs at the first.
            Require(import.SourceMap.TryGetAddress("main.asm", 10, out int lineStart) && lineStart == 32768,
                $"Line 10 mapped to {lineStart}, not the first address it emitted.");
            Require(!import.SourceMap.TryGetAddress("main.asm", 99, out _),
                "A line that emitted nothing produced an address.");

            Require(import.SourceMap.Files.Count == 2,
                $"Recorded {import.SourceMap.Files.Count} source files, expected two.");

            // Names come through as an ordinary symbol table, usable by both tools.
            Require(import.Symbols.TryGetAddress("init_screen", out int label) && label == 32768,
                "A label record did not import.");
            Require(import.Symbols.TryGetAddress("SCREEN_SIZE", out int constant) && constant == 1234,
                "A constant record did not import.");

            // Through the state object the debugger windows use.
            string directory = Path.Combine(Path.GetTempPath(), $"ZedExEss-sld-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "game.sld");
                File.WriteAllText(path, Sld);

                var disassembler = new Z80Disassembler();
                var assembler = new Z80InlineAssembler();
                var state = new SpectrumDebuggerSymbolState(disassembler, assembler);

                SpectrumSymbolLoadResult loaded = state.LoadSourceLevel(path);
                Require(loaded.Loaded, $"A .sld did not load: {loaded.Summary}");
                Require(state.HasSourceMap, "The state does not report a source map.");
                Require(state.HasSymbols, "The state did not take the names from the .sld.");
                Require(loaded.Summary.Contains("source line", StringComparison.OrdinalIgnoreCase),
                    $"The summary does not mention source lines: {loaded.Summary}");

                Require(state.TryGetLocation(32769, out Z80SourceLocation located)
                        && located.Line == 11,
                    "The state does not resolve an address to its line.");

                // The names reach the tools exactly as a .sym would.
                SpectrumMemory memory = CreateMemory();
                Write(memory, TestAddress, 0xCD, 0x00, 0x80);
                Require(disassembler.Disassemble(memory, TestAddress).Text == "CALL init_screen",
                    "A .sld's labels did not reach the disassembler.");

                // Loading a plain symbol file afterwards must drop the map: it describes
                // code the new file says nothing about.
                string plain = Path.Combine(directory, "other.sym");
                File.WriteAllText(plain, "somewhere: EQU $9000\n");
                Require(state.Load(plain).Loaded, "A plain symbol file failed after a .sld.");
                Require(!state.HasSourceMap,
                    "A plain symbol file left the previous file's source map in place.");

                // A file with nothing usable is refused and changes nothing.
                string junk = Path.Combine(directory, "junk.sld");
                File.WriteAllText(junk, "not a source level file at all\n");
                SpectrumSymbolLoadResult rejected = state.LoadSourceLevel(junk);
                Require(!rejected.Loaded, "An unusable .sld reported success.");
                Require(state.HasSymbols, "A failed .sld load discarded working symbols.");

                state.Clear();
                Require(!state.HasSourceMap && !state.HasSymbols,
                    "Clearing left the source map or the names behind.");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        /// <summary>
        /// The source window both debugger windows display for wherever the processor is.
        /// </summary>
        /// <remarks>
        /// The hosts only put this text in a box, so the text is what gets checked: the right
        /// lines, the current one marked, and a source file found beside the debugging file
        /// even though the record names it relatively - which is how an assembler writes it,
        /// because it was given a relative path on the command line.
        /// </remarks>
        private static void VerifySourceWindow()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"ZedExEss-srcview-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                // Twelve numbered lines, so which ones appear is unambiguous.
                var source = new System.Text.StringBuilder();
                for (int line = 1; line <= 12; line++)
                {
                    source.AppendLine($"    NOP ; line {line}");
                }

                File.WriteAllText(Path.Combine(directory, "prog.asm"), source.ToString());

                // The record names the file relatively, as a build would.
                string sld = "|SLD.data.version|1\n"
                    + "prog.asm|6|prog.asm|6|0|32768|T|\n"
                    + "prog.asm|7|prog.asm|7|0|32769|T|\n";
                string sldPath = Path.Combine(directory, "prog.sld");
                File.WriteAllText(sldPath, sld);

                var state = new SpectrumDebuggerSymbolState(new Z80Disassembler(), new Z80InlineAssembler());
                Require(state.LoadSourceLevel(sldPath).Loaded, "The .sld did not load.");

                Require(state.TryGetSourceWindow(32768, radius: 2, out string header, out string text),
                    "No source window was produced for a mapped address.");
                Require(header == "prog.asm:6", $"The header reads '{header}', not 'prog.asm:6'.");

                string[] shown = text.TrimEnd().Split('\n');
                Require(shown.Length == 5,
                    $"A radius of two showed {shown.Length} lines, expected five.");
                Require(shown[0].Contains("line 4", StringComparison.Ordinal),
                    $"The window starts at the wrong line: {shown[0]}");
                Require(shown[^1].Contains("line 8", StringComparison.Ordinal),
                    $"The window ends at the wrong line: {shown[^1]}");

                // The current line is the marked one, and only it.
                Require(shown[2].StartsWith(">", StringComparison.Ordinal)
                        && shown[2].Contains("line 6", StringComparison.Ordinal),
                    $"The current line is not marked: {shown[2]}");
                Require(shown.Count(entry => entry.StartsWith(">", StringComparison.Ordinal)) == 1,
                    "More than one line was marked as current.");

                // Following the processor moves the window with it.
                Require(state.TryGetSourceWindow(32769, radius: 1, out string nextHeader, out _)
                        && nextHeader == "prog.asm:7",
                    $"The window did not follow to the next address; header was '{nextHeader}'.");

                // Clamped at the start of the file rather than asking for line zero.
                File.WriteAllText(
                    Path.Combine(directory, "top.sld"),
                    "|SLD.data.version|1\nprog.asm|1|prog.asm|1|0|40000|T|\n");
                var atTop = new SpectrumDebuggerSymbolState(new Z80Disassembler(), new Z80InlineAssembler());
                Require(atTop.LoadSourceLevel(Path.Combine(directory, "top.sld")).Loaded,
                    "The second .sld did not load.");
                Require(atTop.TryGetSourceWindow(40000, radius: 5, out _, out string topText),
                    "No window was produced at the top of a file.");
                Require(topText.TrimEnd().Split('\n').Length == 6,
                    "The window was not clamped to the start of the file.");

                // An address with no record produces nothing, which is what the UI reports.
                Require(!state.TryGetSourceWindow(50000, radius: 2, out _, out _),
                    "An unmapped address produced a source window.");

                // A file that cannot be found says so instead of pretending.
                File.WriteAllText(
                    Path.Combine(directory, "missing.sld"),
                    "|SLD.data.version|1\nabsent.asm|3|absent.asm|3|0|41000|T|\n");
                var missing = new SpectrumDebuggerSymbolState(new Z80Disassembler(), new Z80InlineAssembler());
                Require(missing.LoadSourceLevel(Path.Combine(directory, "missing.sld")).Loaded,
                    "The third .sld did not load.");
                Require(missing.TryGetSourceWindow(41000, radius: 2, out string absentHeader, out string absentText),
                    "A mapped address with a missing file should still report its location.");
                Require(absentHeader == "absent.asm:3", $"The header reads '{absentHeader}'.");
                Require(absentText.Contains("could not be found", StringComparison.OrdinalIgnoreCase),
                    $"A missing source file was not explained: {absentText}");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        /// <summary>
        /// A source step runs instructions until the source line changes, and not before.
        /// </summary>
        /// <remarks>
        /// The rules are the feature, so they are checked directly with a scripted machine
        /// rather than through a host: several instructions on one line are passed over,
        /// unmapped code in between is passed over, a line that loops back to itself does not
        /// count as arriving, and a runaway stops with a count instead of hanging.
        /// </remarks>
        private static void VerifySourceStepping()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"ZedExEss-srcstep-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                // Line 10 emits three instructions, then line 11 one, then unmapped code,
                // then line 12.
                string sld = "|SLD.data.version|1\n"
                    + "prog.asm|10|prog.asm|10|0|32768|T|\n"
                    + "prog.asm|10|prog.asm|10|0|32769|T|\n"
                    + "prog.asm|10|prog.asm|10|0|32770|T|\n"
                    + "prog.asm|11|prog.asm|11|0|32771|T|\n"
                    + "prog.asm|12|prog.asm|12|0|32780|T|\n";
                string path = Path.Combine(directory, "prog.sld");
                File.WriteAllText(path, sld);

                var symbols = new SpectrumDebuggerSymbolState(new Z80Disassembler(), new Z80InlineAssembler());
                Require(symbols.LoadSourceLevel(path).Loaded, "The .sld did not load.");

                // A scripted processor: each step moves to the next address in this list.
                ushort[] trace =
                [
                    32768, 32769, 32770, 32771, 32775, 32776, 32780
                ];
                int position = 0;
                ushort ReadPc() => trace[position];
                bool StepOnce()
                {
                    if (position + 1 >= trace.Length)
                    {
                        return false;
                    }

                    position++;
                    return true;
                }

                // From the first instruction of line 10, three steps reach line 11.
                SourceStepOutcome toEleven = SpectrumSourceStepper.StepLine(symbols, ReadPc, StepOnce);
                Require(toEleven.ReachedNewLine, $"The step did not reach a new line: {toEleven.Summary}");
                Require(toEleven.Instructions == 3,
                    $"Reaching line 11 took {toEleven.Instructions} instruction(s), expected three.");
                Require(ReadPc() == 32771, $"The machine stopped at {ReadPc()}, not 32771.");
                Require(toEleven.Summary.Contains("prog.asm:11", StringComparison.Ordinal),
                    $"The summary does not name where it stopped: {toEleven.Summary}");

                // From line 11 the next mapped line is 12, with two unmapped instructions
                // in between that must be stepped through rather than stopped at.
                SourceStepOutcome toTwelve = SpectrumSourceStepper.StepLine(symbols, ReadPc, StepOnce);
                Require(toTwelve.ReachedNewLine, $"The second step failed: {toTwelve.Summary}");
                Require(toTwelve.Instructions == 3,
                    $"Reaching line 12 took {toTwelve.Instructions} instruction(s), expected three.");
                Require(ReadPc() == 32780, $"The machine stopped at {ReadPc()}, not 32780.");

                // Running out of machine is reported, not thrown.
                SourceStepOutcome exhausted = SpectrumSourceStepper.StepLine(symbols, ReadPc, StepOnce);
                Require(!exhausted.ReachedNewLine, "A machine that cannot step reported success.");
                Require(exhausted.Summary.Contains("would not step", StringComparison.OrdinalIgnoreCase),
                    $"The stall was not explained: {exhausted.Summary}");

                // A line that jumps back to itself is not arrival: the cap stops it.
                int spins = 0;
                SourceStepOutcome looping = SpectrumSourceStepper.StepLine(
                    symbols,
                    () => 32768,
                    () => { spins++; return true; },
                    maximumInstructions: 50);
                Require(!looping.ReachedNewLine, "A line looping to itself counted as a new line.");
                Require(spins == 50, $"The cap ran {spins} instructions, not the 50 it was given.");
                Require(looping.Summary.Contains("Gave up", StringComparison.OrdinalIgnoreCase),
                    $"Hitting the cap was not explained: {looping.Summary}");

                // With no map there is nothing to step to, and the machine is left alone.
                var bare = new SpectrumDebuggerSymbolState(new Z80Disassembler(), new Z80InlineAssembler());
                int untouched = 0;
                SourceStepOutcome none = SpectrumSourceStepper.StepLine(
                    bare,
                    () => 32768,
                    () => { untouched++; return true; });
                Require(!none.ReachedNewLine && untouched == 0,
                    "Stepping without a source map still ran the machine.");
                Require(none.Summary.Contains("No source map", StringComparison.OrdinalIgnoreCase),
                    $"The missing map was not explained: {none.Summary}");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        /// <summary>
        /// The synchronous step-over a source-level step-over is built from.
        /// </summary>
        /// <remarks>
        /// Checked with a scripted processor because the rules are what matter, and one of
        /// them is easy to get wrong: a recursive callee returns several times, and only the
        /// return that unwinds the stack to where the call started is the one that belongs to
        /// this invocation. Without the stack half of the test, stepping over a recursive call
        /// stops early and lands the user inside the routine they asked to skip.
        /// </remarks>
        private static void VerifyStepOverInstruction()
        {
            // A three-byte CALL at 32768, so execution should resume at 32771.
            SpectrumInstructionShape Measure(ushort address) => address == 32768
                ? new SpectrumInstructionShape(3, IsCallLike: true)
                : new SpectrumInstructionShape(1, IsCallLike: false);

            // The callee calls itself once: the inner return passes through 32771 with the
            // stack still deeper than it was, which must not be mistaken for the outer one.
            (ushort Pc, ushort Sp)[] trace =
            [
                (32768, 0x7F00),    // the CALL itself
                (40000, 0x7EFE),    // inside the callee
                (32771, 0x7EFC),    // an inner return passing the address, stack still deep
                (40010, 0x7EFE),
                (32771, 0x7F00)     // the real return
            ];

            int position = 0;
            ushort ReadPc() => trace[position].Pc;
            ushort ReadSp() => trace[position].Sp;
            bool StepOnce()
            {
                if (position + 1 >= trace.Length)
                {
                    return false;
                }

                position++;
                return true;
            }

            Require(
                SpectrumSourceStepper.StepOverInstruction(ReadPc, ReadSp, Measure, StepOnce),
                "Stepping over a call reported that the machine would not step.");
            Require(position == trace.Length - 1,
                $"The step over stopped at trace position {position}, not the real return.");
            Require(ReadPc() == 32771 && ReadSp() == 0x7F00,
                "The step over did not land on the instruction after the call.");

            // A plain instruction is one step and nothing more.
            position = 0;
            (ushort Pc, ushort Sp)[] plain = [(1000, 0x7F00), (1001, 0x7F00), (1002, 0x7F00)];
            int plainPosition = 0;
            Require(
                SpectrumSourceStepper.StepOverInstruction(
                    () => plain[plainPosition].Pc,
                    () => plain[plainPosition].Sp,
                    static _ => new SpectrumInstructionShape(1, IsCallLike: false),
                    () => { plainPosition++; return true; }),
                "Stepping over a plain instruction failed.");
            Require(plainPosition == 1,
                $"A plain instruction took {plainPosition} steps, expected one.");

            // A conditional call that was not taken is already past itself.
            int notTakenPosition = 0;
            (ushort Pc, ushort Sp)[] notTaken = [(32768, 0x7F00), (32771, 0x7F00), (32772, 0x7F00)];
            Require(
                SpectrumSourceStepper.StepOverInstruction(
                    () => notTaken[notTakenPosition].Pc,
                    () => notTaken[notTakenPosition].Sp,
                    Measure,
                    () => { notTakenPosition++; return true; }),
                "Stepping over an untaken conditional call failed.");
            Require(notTakenPosition == 1,
                $"An untaken call took {notTakenPosition} steps, expected one.");

            // A callee that never returns stops with the allowance spent rather than hanging,
            // and leaves the machine where it got to.
            int spins = 0;
            Require(
                SpectrumSourceStepper.StepOverInstruction(
                    () => (ushort)(spins == 0 ? 32768 : 40000),
                    static () => (ushort)0x7EFE,
                    Measure,
                    () => { spins++; return true; },
                    maximumInstructions: 40),
                "A call that never returns should still report that it stepped.");
            Require(spins == 41,
                $"The allowance ran {spins} steps, expected the one call plus forty.");

            // And a machine that will not step at all is reported.
            Require(
                !SpectrumSourceStepper.StepOverInstruction(
                    static () => (ushort)32768,
                    static () => (ushort)0x7F00,
                    Measure,
                    static () => false),
                "A machine that would not step reported success.");
        }

        /// <summary>
        /// Laying a binary file into the output, with an optional slice of it.
        /// </summary>
        /// <remarks>
        /// The property worth asserting hardest is that the file is read once per assembly.
        /// Both passes have to agree about how many bytes the line occupies, so a resolver
        /// that answered differently the second time would misplace every label after it -
        /// which is why the count of resolver calls is checked, not just the bytes.
        /// </remarks>
        private static void VerifyIncludeBinary()
        {
            byte[] content = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];
            int resolverCalls = 0;

            var assembler = new Z80InlineAssembler
            {
                BinaryResolver = name =>
                {
                    resolverCalls++;
                    return name == "sprite.bin" ? content : null;
                }
            };

            RequireBytes(assembler.Assemble(0x8000, "INCBIN \"sprite.bin\""), content,
                "INCBIN did not emit the file.");
            Require(resolverCalls == 1,
                $"The file was read {resolverCalls} times; both passes must share one reading.");

            RequireBytes(assembler.Assemble(0x8000, "INCBIN \"sprite.bin\",2"), [0x33, 0x44, 0x55, 0x66],
                "INCBIN did not honour a skip.");
            RequireBytes(assembler.Assemble(0x8000, "INCBIN \"sprite.bin\",1,3"), [0x22, 0x33, 0x44],
                "INCBIN did not honour a skip and length.");
            RequireBytes(assembler.Assemble(0x8000, "INCBIN \"sprite.bin\",6"), [],
                "A skip to the very end should emit nothing rather than fail.");

            // The bytes have to move the assembler on, or labels after them are wrong.
            Z80AssemblyResult withLabel = assembler.Assemble(0x8000, """
                INCBIN "sprite.bin"
                here: DB 0
                LD HL,here
                """);
            Require(withLabel.Success, withLabel.Error ?? "INCBIN with a following label failed.");
            Require(withLabel.Bytes.AsSpan()[7..].SequenceEqual(new byte[] { 0x21, 0x06, 0x80 }),
                "A label after INCBIN did not account for the included bytes.");

            // Reading past the end is refused rather than truncated.
            Require(!assembler.Assemble(0x8000, "INCBIN \"sprite.bin\",4,9").Success,
                "INCBIN accepted a length past the end of the file.");
            Require(!assembler.Assemble(0x8000, "INCBIN \"sprite.bin\",99").Success,
                "INCBIN accepted a skip past the end of the file.");

            Z80AssemblyResult missing = assembler.Assemble(0x8000, "INCBIN \"absent.bin\"");
            Require(!missing.Success, "A missing INCBIN file assembled.");
            Require(missing.Error != null
                    && missing.Error.Contains("could not be read", StringComparison.OrdinalIgnoreCase),
                $"A missing file was not explained: {missing.Error}");

            // A size that cannot be known in the first pass is refused, as DS does.
            Require(!assembler.Assemble(0x8000, "INCBIN \"sprite.bin\",later\nlater EQU 2").Success,
                "INCBIN accepted a forward reference for its skip.");

            // With no resolver it says so rather than half-working.
            Z80AssemblyResult unavailable =
                new Z80InlineAssembler().Assemble(0x8000, "INCBIN \"sprite.bin\"");
            Require(!unavailable.Success, "INCBIN worked with no resolver to read it.");
            Require(unavailable.Error != null
                    && unavailable.Error.Contains("not available", StringComparison.OrdinalIgnoreCase),
                $"The unavailable case was not explained: {unavailable.Error}");

            // And through the state object both hosts wire, resolving beside the symbol file.
            string directory = Path.Combine(Path.GetTempPath(), $"ZedExEss-incbin-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(Path.Combine(directory, "game.sym"), "start: EQU $8000\n");
                File.WriteAllBytes(Path.Combine(directory, "tiles.bin"), [0xAA, 0xBB]);
                File.WriteAllText(Path.Combine(directory, "extra.asm"), "LD A,9");

                var hosted = new Z80InlineAssembler();
                var state = new SpectrumDebuggerSymbolState(new Z80Disassembler(), hosted);
                hosted.BinaryResolver = state.ReadProjectBytes;
                hosted.IncludeResolver = state.ReadProjectText;

                // Before a symbol file is loaded there is no project root to measure from.
                Require(!hosted.Assemble(0x8000, "INCBIN \"tiles.bin\"").Success,
                    "A relative INCBIN resolved with no symbol file loaded.");

                Require(state.Load(Path.Combine(directory, "game.sym")).Loaded,
                    "The symbol file did not load.");

                RequireBytes(hosted.Assemble(0x8000, "INCBIN \"tiles.bin\""), [0xAA, 0xBB],
                    "INCBIN did not resolve beside the loaded symbol file.");
                RequireBytes(hosted.Assemble(0x8000, "INCLUDE \"extra.asm\""), [0x3E, 0x09],
                    "INCLUDE did not resolve beside the loaded symbol file.");
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }

        private static void VerifyAssembler()
        {
            var assembler = new Z80InlineAssembler();
            RequireBytes(assembler.Assemble(TestAddress, "LD HL,1234H"), [0x21, 0x34, 0x12], "LD HL,nn assembly failed.");
            RequireBytes(assembler.Assemble(TestAddress, "JR 8004H"), [0x18, 0x02], "JR assembly failed.");
            RequireBytes(assembler.Assemble(TestAddress, "CALL 9000H"), [0xCD, 0x00, 0x90], "CALL assembly failed.");
            RequireBytes(assembler.Assemble(TestAddress, "BIT 0,(HL)"), [0xCB, 0x46], "BIT assembly failed.");
            RequireBytes(assembler.Assemble(TestAddress, "LDIR"), [0xED, 0xB0], "LDIR assembly failed.");
            RequireBytes(assembler.Assemble(TestAddress, "DB \"A\",0DH"), [0x41, 0x0D], "DB assembly failed.");
            RequireBytes(assembler.Assemble(TestAddress, "loop: NOP\nJR loop"), [0x00, 0x18, 0xFD], "Label relative assembly failed.");
            RequireBytes(assembler.Assemble(TestAddress, "PUSH IX\nPOP IX\nPUSH IY\nPOP IY"), [0xDD, 0xE5, 0xDD, 0xE1, 0xFD, 0xE5, 0xFD, 0xE1], "Indexed stack assembly failed.");

            Z80AssemblyResult orgResult = assembler.Assemble(TestAddress, """
                ORG 9000H
                start: LD A,value
                value EQU 42H
                JR start
                """);
            Require(orgResult.Success, orgResult.Error ?? "ORG/EQU/label assembly failed.");
            Require(orgResult.Patches.Count == 1 && orgResult.Patches[0].Address == 0x9000, "ORG did not move output patch to 9000H.");
            Require(orgResult.Bytes.AsSpan().SequenceEqual(new byte[] { 0x3E, 0x42, 0x18, 0xFC }), "ORG/EQU/label bytes did not match expected output.");

            Z80AssemblyResult multiOrgResult = assembler.Assemble(TestAddress, """
                DB 1
                ORG 9010H
                DB 2
                """);
            Require(multiOrgResult.Success, multiOrgResult.Error ?? "Multiple ORG assembly failed.");
            Require(multiOrgResult.Patches.Count == 2
                && multiOrgResult.Patches[0].Address == TestAddress
                && multiOrgResult.Patches[1].Address == 0x9010,
                "Multiple ORG directives did not create address-specific patches.");
        }
        private static void VerifyExecutionBreakpoint()
        {
            DebugMachine machine = CreateDebugMachine();
            Write(machine.Memory, TestAddress, 0x00, 0x00);
            machine.Cpu.PC = TestAddress;
            machine.Debugger.AddExecuteBreakpoint(TestAddress);
            machine.Debugger.Run();
            machine.Emulator.SetPaused(false);
            machine.Emulator.StepInstruction();
            Require(machine.Cpu.PC == TestAddress, "PC advanced despite execute breakpoint.");
            Require(machine.Debugger.IsPaused, "Debugger did not pause on execute breakpoint.");
        }
        private static void VerifyStepInto()
        {
            DebugMachine machine = CreateDebugMachine();
            Write(machine.Memory, TestAddress, 0x00, 0x00);
            machine.Cpu.PC = TestAddress;
            machine.Emulator.SetPaused(true);
            machine.Debugger.PrepareStepInto();
            machine.Emulator.StepInstruction();
            Require(machine.Cpu.PC == TestAddress + 1, "Step did not execute one NOP.");
            Require(machine.Debugger.IsPaused, "Debugger did not pause after step.");
            Require(machine.Cpu.Cyc > 0, "Step did not advance CPU time.");
        }
        private static void VerifyStepOver()
        {
            DebugMachine machine = CreateDebugMachine();
            Write(machine.Memory, TestAddress, 0xCD, 0x10, 0x80, 0x00);
            Write(machine.Memory, 0x8010, 0xC9);
            machine.Cpu.PC = TestAddress;
            machine.Cpu.SP = 0xFF00;
            machine.Debugger.PrepareStepOver(new Z80Disassembler());
            machine.Emulator.SetPaused(false);
            int guard = 0;
            while (!machine.Debugger.IsPaused && guard++ < 20)
            {
                machine.Emulator.StepInstruction();
            }

            Require(machine.Cpu.PC == TestAddress + 3, $"Step over stopped at {machine.Cpu.PC:X4}.");
        }
        private static void VerifyWatchpoints()
        {
            DebugMachine machine = CreateDebugMachine();
            Write(machine.Memory, TestAddress, 0x3E, 0x55, 0x32, 0x00, 0x90);
            machine.Cpu.PC = TestAddress;
            machine.Debugger.AddMemoryBreakpoint(DebuggerBreakType.MemoryWrite, 0x9000, 0x9000);
            machine.Debugger.Run();
            machine.Cpu.ConfigureDebugHook(machine.Debugger);
            machine.Emulator.SetPaused(false);
            int guard = 0;
            while (!machine.Debugger.IsPaused && guard++ < 10)
            {
                machine.Emulator.StepInstruction();
            }

            Require(machine.Memory.ReadDirect(0x9000) == 0x55, "Watchpoint test write did not happen.");
            Require(machine.Debugger.LastHit?.Type == DebuggerBreakType.MemoryWrite, "Memory write watchpoint did not trigger.");

            machine = CreateDebugMachine();
            Write(machine.Memory, TestAddress, 0xDB, 0xFE);
            machine.Cpu.PC = TestAddress;
            machine.Debugger.AddPortBreakpoint(DebuggerBreakType.PortRead, 0x00FE, 0x00FF);
            machine.Debugger.Run();
            machine.Cpu.ConfigureDebugHook(machine.Debugger);
            machine.Emulator.SetPaused(false);
            machine.Emulator.StepInstruction();
            Require(machine.Debugger.LastHit?.Type == DebuggerBreakType.PortRead, "Port read watchpoint did not trigger.");
        }
        private static void VerifyPortableViewService()
        {
            DebugMachine machine = CreateDebugMachine();
            Write(machine.Memory, TestAddress, 0x21, 0x34, 0x12, 0x00);
            machine.Cpu.PC = TestAddress;
            machine.Cpu.SP = 0x9000;

            var view = new SpectrumDebuggerViewService(
                machine.Debugger,
                new Z80Disassembler(),
                new Z80InlineAssembler());
            Require(view.GetRegistersText().Contains("PC 8000", StringComparison.Ordinal), "Portable register projection omitted PC.");
            machine.Debugger.AddExecuteBreakpoint(TestAddress);
            IReadOnlyList<Z80DisassemblyLine> lines = view.GetDisassembly(TestAddress, 2);
            Require(lines.Count == 2 && lines[0].Mnemonic == "LD HL,1234", "Portable disassembly projection was incorrect.");
            Require(lines[0].IsCurrent && lines[0].HasBreakpoint,
                "Portable disassembly projection omitted its current-PC or breakpoint marker state.");
            Require(view.GetMemoryText(TestAddress, 1).StartsWith("8000: 21 34 12 00", StringComparison.Ordinal), "Portable memory projection was incorrect.");
            Require(view.TryPatchBytes(TestAddress, "3E 2A", out string error), error);
            Require(machine.Memory.ReadDirect(TestAddress) == 0x3E && machine.Memory.ReadDirect(TestAddress + 1) == 0x2A,
                "Portable byte patch did not update RAM.");
            Require(view.TryApplyAssembly(TestAddress, "LD BC,5678H", out Z80AssemblyResult result, out error), error);
            Require(result.Success && machine.Memory.ReadDirect(TestAddress) == 0x01,
                "Portable assembler patch did not update RAM.");
            Require(view.TryBuildDisassemblyExport(TestAddress, TestAddress + 3, out string export, out error), error);
            Require(export.Contains("8000: 01 78 56", StringComparison.Ordinal)
                && export.Contains("LD BC,5678", StringComparison.Ordinal),
                "Portable disassembly export was incorrect.");
        }
        private static void VerifyZx8xDebugger()
        {
            Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81);
            Zx8xMachine machine = Zx8xMachineFactory.Create(
                Zx8xModel.Zx81,
                Zx8xRomImage.Load(new byte[descriptor.SizeBytes], descriptor),
                ramConfiguration: Zx8xRamConfiguration.Expansion16K);
            Write(machine.Memory, 0x4000, 0x00, 0x00);
            machine.Cpu.PC = 0x4000;
            machine.Cpu.SetInterruptState(0, false, false);
            machine.Cpu.SetHalted(false);

            var debugger = new SpectrumDebuggerController();
            debugger.Attach(
                machine.Cpu,
                machine.Memory,
                machine.TstatesPerFrame,
                machine.VideoTiming.Timing.TstatesPerLine);
            machine.ConfigureCpuStepHooks(debugger.BeforeCpuStep, debugger.AfterCpuStep);
            debugger.AddExecuteBreakpoint(0x4000);
            debugger.Run();
            machine.StepInstruction();
            Require(debugger.IsPaused && machine.Cpu.PC == 0x4000,
                "ZX8x execute breakpoint did not stop before the opcode fetch.");

            debugger.RemoveBreakpoint(debugger.Breakpoints.Single());
            debugger.PrepareStepInto();
            machine.SetPaused(false);
            machine.StepInstruction();
            Require(debugger.IsPaused && machine.Cpu.PC == 0x4001,
                "ZX8x single-step did not execute exactly one instruction.");

            Write(machine.Memory, 0x4001, 0x3E, 0x55, 0x32, 0x00, 0x41);
            machine.Cpu.PC = 0x4001;
            debugger.AddMemoryBreakpoint(DebuggerBreakType.MemoryWrite, 0x4100, 0x4100);
            debugger.Run();
            machine.Cpu.ConfigureDebugHook(debugger);
            machine.SetPaused(false);
            for (int guard = 0; !debugger.IsPaused && guard < 4; guard++)
            {
                machine.StepInstruction();
            }

            Require(machine.Memory.ReadDirect(0x4100) == 0x55
                && debugger.LastHit?.Type == DebuggerBreakType.MemoryWrite,
                "ZX8x memory watchpoint did not stop after the write instruction.");

            var view = new SpectrumDebuggerViewService(
                debugger,
                new Z80Disassembler(),
                new Z80InlineAssembler());
            Require(view.GetMemoryText(0x4000, 1).StartsWith("4000: 00 3E 55 32", StringComparison.Ordinal),
                "ZX8x debugger memory projection was incorrect.");
            Require(view.TryPatchBytes(0x4000, "C9", out string error), error);
            Require(!view.TryPatchBytes(0x0000, "00", out _),
                "ZX8x debugger allowed a ROM patch through the RAM-only editor.");
            Require(machine.Memory.GetMapping(0x4000).DisplayName.StartsWith("RAM", StringComparison.Ordinal),
                "ZX8x RAM mapping was not exposed to the disassembly view.");
        }
        private static DebugMachine CreateDebugMachine()
        {
            SpectrumModel model = SpectrumModel.Spectrum48K;
            SpectrumMemory memory = CreateMemory();
            var ports = new SpectrumPortBus(model, contendedPages: memory);
            var cpu = new Z80(memory, ports);
            cpu.Z80Init();
            cpu.SetInterruptState(0, false, false);
            cpu.SetHalted(false);

            var renderer = new SpectrumUlaRenderer(model, memory);
            var audio = new SpectrumAudioRenderer(SpectrumAudioTiming.CpuClockHz(model), SpectrumAudioTiming.DefaultSampleRate);
            var contention = SpectrumContentionProfile.Create(model);
            cpu.ConfigureNoMreqContention(memory, contention);
            cpu.ConfigureIoContention(true);
            memory.ConfigureTiming(cpu, contention);
            ports.ConfigureTiming(cpu, contention, memory);

            var emulator = new SpectrumEmulator(cpu, memory, ports, renderer, audio);
            var debugger = new SpectrumDebuggerController();
            debugger.Attach(cpu, memory, ports, model);
            emulator.ConfigureCpuStepHooks(debugger.BeforeCpuStep, debugger.AfterCpuStep);
            return new DebugMachine(cpu, memory, ports, emulator, debugger);
        }
        private static SpectrumMemory CreateMemory()
        {
            return new SpectrumMemory(SpectrumModel.Spectrum48K, RomSet.CreateBlank(1));
        }
        private static void Write(SpectrumMemory memory, ushort address, params byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                memory.WriteDirect(unchecked((ushort)(address + i)), bytes[i]);
            }
        }
        private static void Write(Zx8xMemory memory, ushort address, params byte[] bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                memory.WriteDirect(unchecked((ushort)(address + i)), bytes[i]);
            }
        }
        private static void RequireBytes(Z80AssemblyResult result, byte[] expected, string message)
        {
            Require(result.Success, result.Error ?? message);
            Require(result.Bytes.AsSpan().SequenceEqual(expected), message);
        }
        private static string ResolveOutputPath(string? outputPath)
        {
            return string.IsNullOrWhiteSpace(outputPath)
                ? Path.GetFullPath(Path.Combine("TEST", "debugger-verification-results.txt"))
                : Path.GetFullPath(outputPath);
        }
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
        private readonly struct DebugMachine(
            Z80 cpu,
            SpectrumMemory memory,
            SpectrumPortBus ports,
            SpectrumEmulator emulator,
            SpectrumDebuggerController debugger)
        {
            public Z80 Cpu { get; } = cpu;
            public SpectrumMemory Memory { get; } = memory;
            public SpectrumPortBus Ports { get; } = ports;
            public SpectrumEmulator Emulator { get; } = emulator;
            public SpectrumDebuggerController Debugger { get; } = debugger;
        }
        private sealed class DebuggerVerificationLog(StreamWriter writer)
        {
            public int Failed { get; private set; }
            public void WriteLine(string line)
            {
                writer.WriteLine(line);
                Debug.WriteLine(line);
            }
            public void Check(string name, Action action)
            {
                try
                {
                    action();
                    WriteLine($"PASS {name}");
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    Failed++;
                    WriteLine($"FAIL {name}: {ex.Message}");
                }
            }
        }
    }
}
