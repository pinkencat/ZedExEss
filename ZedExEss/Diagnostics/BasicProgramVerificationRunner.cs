using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Basic;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Diagnostics
{
    /// <summary>Output settings for the headless BASIC editor verification.</summary>
    public sealed class BasicProgramVerificationOptions
    {
        public string? OutputPath { get; init; }
    }
    /// <summary>
    /// Verifies token byte compatibility, source round-tripping and ROM workspace injection.
    /// </summary>
    /// <remarks>This runner does not boot a ROM; it constructs representative system-variable layouts directly.</remarks>
    public static class BasicProgramVerificationRunner
    {
        public static int Run(BasicProgramVerificationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            string outputPath = ResolveOutputPath(options.OutputPath);
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
            var log = new BasicVerificationLog(writer);

            try
            {
                log.WriteLine("BASIC editor headless verification");
                log.WriteLine($"Output: {outputPath}");
                log.WriteLine(string.Empty);

                log.Check("Tokenize PRINT with hidden numeric value", VerifyPrintNumericBytes);
                log.Check("Gate 128 BASIC-only tokens by token mode", Verify128BasicTokens);
                log.Check("Preserve BASIC token display spacing", VerifyTokenSpacing);
                log.Check("Tokenize and detokenize common BASIC statements", VerifyTokenRoundTrip);
                log.Check("Reject duplicate line numbers", VerifyDuplicateLineRejection);
                log.Check("Reject structurally invalid BASIC source", VerifySyntaxRejection);
                log.Check("Inject BASIC into all supported model memory maps", VerifyMemoryInjectionForModels);
                log.Check("Drive BASIC editing through the portable session", VerifyPortableEditorSession);

                log.WriteLine(string.Empty);
                log.WriteLine(log.Failed == 0
                    ? "Result: PASS"
                    : $"Result: FAIL ({log.Failed.ToString(CultureInfo.InvariantCulture)} failed checks)");

                return log.Failed == 0 ? 0 : 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.WriteLine($"Error: {ex.Message}");
                Debug.WriteLine(ex.ToString());
                return 3;
            }
        }
        private static void VerifyPrintNumericBytes()
        {
            Require(SpectrumBasicTokenizer.TryTokenizeProgram("10 PRINT 42", out byte[] program, out string error), error);
            byte[] expected =
            [
                0x00, 0x0A, 0x0A, 0x00,
                0xF5, 0x34, 0x32, 0x0E, 0x00, 0x00, 0x2A, 0x00, 0x00, 0x0D
            ];
            Require(program.AsSpan().SequenceEqual(expected), "PRINT numeric token stream did not match expected ZX BASIC bytes.");
            Require(SpectrumBasicDetokenizer.TryDetokenizeProgram(program, out string source, out error), error);
            Require(source == "10 PRINT 42", $"Unexpected detokenized source: {source}");
        }
        private static void VerifyTokenSpacing()
        {
            Require(SpectrumBasicTokenizer.TryTokenizeProgram("10 PRINT AT 10,3; \"A\"", out byte[] program, out string error), error);
            Require(SpectrumBasicDetokenizer.TryDetokenizeProgram(program, out string source, out error), error);
            Require(source == "10 PRINT AT 10,3; \"A\"", $"Unexpected token spacing: {source}");
            Require(!source.Contains("  ", StringComparison.Ordinal), "Detokenized source should not contain doubled display spaces.");

            byte[] compactProgram =
            [
                0x00, 0x0A, 0x05, 0x00,
                0xF5, (byte)'"', (byte)'A', (byte)'"', 0x0D
            ];
            Require(SpectrumBasicDetokenizer.TryDetokenizeProgram(compactProgram, out source, out error), error);
            Require(source == "10 PRINT \"A\"", $"Expected detokenizer to add display spacing after PRINT: {source}");
        }
        private static void Verify128BasicTokens()
        {
            const string source = "10 PLAY \"ABC\"\n20 SPECTRUM";
            Require(!SpectrumBasicSyntaxChecker.TryValidateSource(source, out string error), "128-only tokens should be rejected by the 48K token table.");
            Require(error.Contains("recognised BASIC command", StringComparison.OrdinalIgnoreCase), "128-token rejection should be explicit.");

            Require(SpectrumBasicSyntaxChecker.TryValidateSource(source, allow128Tokens: true, out error), error);
            Require(SpectrumBasicTokenizer.TryTokenizeProgram(source, allow128Tokens: true, out byte[] program, out error), error);
            Require(program.Contains((byte)0xA4), "PLAY was not tokenized as 0xA4.");
            Require(program.Contains((byte)0xA3), "SPECTRUM was not tokenized as 0xA3.");

            Require(SpectrumBasicDetokenizer.TryDetokenizeProgram(program, allow128Tokens: true, out string detokenized, out error), error);
            Require(detokenized.Contains("10 PLAY \"ABC\"", StringComparison.Ordinal), $"PLAY did not detokenize correctly: {detokenized}");
            Require(detokenized.Contains("20 SPECTRUM", StringComparison.Ordinal), $"SPECTRUM did not detokenize correctly: {detokenized}");

            Require(SpectrumBasicDetokenizer.TryDetokenizeProgram(program, out string fallback, out error), error);
            Require(fallback.Contains("{0xA4}", StringComparison.Ordinal) && fallback.Contains("{0xA3}", StringComparison.Ordinal),
                "128-only tokens should remain raw byte escapes outside 128 BASIC mode.");

            var memory = new SpectrumMemory(SpectrumModel.Spectrum128K, RomSet.CreateBlank(SpectrumModelTraits.RomBankCount(SpectrumModel.Spectrum128K)));
            WriteWord(memory, 0x5C53, 0x5CCB);
            WriteWord(memory, 0x5C4B, 0x5CCB);
            WriteWord(memory, 0x5CB2, 0xFF00);
            var service = new SpectrumBasicMemoryService(memory, SpectrumModel.Spectrum128K);
            Require(service.Allow128BasicTokens, "128 BASIC token mode should be enabled when ROM 0 is paged.");
            Require(service.TryInjectProgram(source, out _, out error), error);

            memory.WritePort7FFD(0x10);
            service = new SpectrumBasicMemoryService(memory, SpectrumModel.Spectrum128K);
            Require(!service.Allow128BasicTokens, "128 BASIC token mode should be disabled when the 48K ROM is paged.");
            Require(!service.TryInjectProgram(source, out _, out error), "128-only source should not inject when the 48K ROM is paged.");
        }
        private static void VerifyTokenRoundTrip()
        {
            const string source = """
                30 PRINT "EG";A;N
                10 LET A=42
                20 FOR N=1 TO 3
                40 NEXT N
                50 DATA 1,2,3
                60 REM PRINT stays literal
                70 IF A>=42 THEN GO TO 90
                80 GO SUB 100
                90 PRINT {0x16}
                100 RETURN
                """;

            Require(SpectrumBasicTokenizer.TryTokenizeProgram(source, out byte[] program, out string error), error);
            Require(SpectrumBasicDetokenizer.TryDetokenizeProgram(program, out string detokenized, out error), error);
            Require(detokenized.Contains("10 LET A=42", StringComparison.Ordinal), "LET line missing after detokenize.");
            Require(detokenized.Contains("20 FOR N=1 TO 3", StringComparison.Ordinal), "FOR/NEXT line missing after detokenize.");
            Require(detokenized.Contains("50 DATA 1,2,3", StringComparison.Ordinal), "DATA line missing after detokenize.");
            Require(detokenized.Contains("60 REM PRINT stays literal", StringComparison.Ordinal), "REM line was not preserved.");
            Require(detokenized.Contains("70 IF A>=42 THEN GO TO 90", StringComparison.Ordinal), "IF/THEN/GO TO line missing after detokenize.");
            Require(detokenized.Contains("80 GO SUB 100", StringComparison.Ordinal), "GO SUB line missing after detokenize.");
            Require(detokenized.Contains("90 PRINT {0x16}", StringComparison.Ordinal), "Raw byte escape did not round-trip.");
        }
        private static void VerifyDuplicateLineRejection()
        {
            Require(!SpectrumBasicTokenizer.TryTokenizeProgram("10 PRINT 1\n10 PRINT 2", out _, out string error), "Duplicate line numbers should be rejected.");
            Require(error.Contains("Duplicate", StringComparison.OrdinalIgnoreCase), "Duplicate-line error message should be explicit.");
        }
        private static void VerifySyntaxRejection()
        {
            Require(!SpectrumBasicSyntaxChecker.TryValidateSource("10 FLANGE 1", out string error), "Unknown statement should be rejected.");
            Require(error.Contains("recognised BASIC command", StringComparison.OrdinalIgnoreCase), "Unknown-statement error should be explicit.");

            Require(!SpectrumBasicSyntaxChecker.TryValidateSource("10 IF A=1 PRINT A", out error), "IF without THEN should be rejected.");
            Require(error.Contains("IF without THEN", StringComparison.OrdinalIgnoreCase), "IF error should mention THEN.");

            Require(!SpectrumBasicSyntaxChecker.TryValidateSource("10 PRINT \"BROKEN", out error), "Unterminated strings should be rejected.");
            Require(error.Contains("unterminated string", StringComparison.OrdinalIgnoreCase), "String error should be explicit.");

            Require(!SpectrumBasicSyntaxChecker.TryValidateSource("10 LET A", out error), "LET without '=' should be rejected.");
            Require(error.Contains("LET without", StringComparison.OrdinalIgnoreCase), "LET error should mention '='.");
        }
        private static void VerifyMemoryInjectionForModels()
        {
            SpectrumModel[] models =
            [
                SpectrumModel.Spectrum48K,
                SpectrumModel.Spectrum128K,
                SpectrumModel.SpectrumPlus3,
                SpectrumModel.Pentagon128,
                SpectrumModel.Scorpion256
            ];

            const string source = """
                10 LET A=1
                20 PRINT "MODEL"
                30 GO TO 20
                """;

            for (int i = 0; i < models.Length; i++)
            {
                VerifyMemoryInjection(models[i], source);
            }
        }
        private static void VerifyPortableEditorSession()
        {
            var memory = new SpectrumMemory(SpectrumModel.Spectrum48K, RomSet.CreateBlank(1));
            WriteWord(memory, 0x5C53, 0x5CCB);
            WriteWord(memory, 0x5C4B, 0x5CCB);
            WriteWord(memory, 0x5CB2, 0xFF00);

            var service = new SpectrumBasicMemoryService(memory, SpectrumModel.Spectrum48K);
            Require(service.TryInjectProgram("10 PRINT \"OLD\"", out _, out string error), error);

            var editor = new SpectrumBasicEditorSession(service);
            Require(editor.Reload(), editor.Status);
            Require(editor.Source == "10 PRINT \"OLD\"", "Portable editor did not load the current program.");
            Require(editor.SetSource("10 LET A=42\n20 PRINT A"), editor.Status);
            Require(editor.TokenizedSize > 0, "Portable editor did not expose the validated tokenized size.");
            Require(editor.Inject(out error), error);
            Require(service.TryReadProgram(out SpectrumBasicProgramSnapshot snapshot, out error), error);
            Require(snapshot.Source.Contains("20 PRINT A", StringComparison.Ordinal), "Portable editor did not inject its replacement source.");
        }
        private static void VerifyMemoryInjection(SpectrumModel model, string source)
        {
            var memory = new SpectrumMemory(model, RomSet.CreateBlank(SpectrumModelTraits.RomBankCount(model)));
            ushort ramtop = model == SpectrumModel.Spectrum16K ? (ushort)0x7FFF : (ushort)0xFF00;
            WriteWord(memory, 0x5C53, 0x5CCB);
            WriteWord(memory, 0x5C4B, 0x5CCB);
            WriteWord(memory, 0x5CB2, ramtop);

            var service = new SpectrumBasicMemoryService(memory, model);
            Require(service.TryInjectProgram(source, out SpectrumBasicProgramSnapshot injected, out string error), $"{model}: {error}");
            Require(injected.Prog == 0x5CCB, $"{model}: PROG changed unexpectedly.");
            Require(memory.ReadDirect(injected.Vars) == 0x80, $"{model}: variables terminator missing.");

            ushort eLine = ReadWord(memory, 0x5C59);
            ushort worksp = ReadWord(memory, 0x5C61);
            Require(memory.ReadDirect(eLine) == 0x0D, $"{model}: edit line terminator missing.");
            Require(worksp == eLine + 1, $"{model}: WORKSP was not reset after empty edit line.");
            Require(ReadWord(memory, 0x5C63) == worksp, $"{model}: STKBOT was not reset.");
            Require(ReadWord(memory, 0x5C65) == worksp, $"{model}: STKEND was not reset.");

            Require(service.TryReadProgram(out SpectrumBasicProgramSnapshot readBack, out error), $"{model}: {error}");
            Require(readBack.Source.Contains("20 PRINT \"MODEL\"", StringComparison.Ordinal), $"{model}: injected BASIC did not detokenize correctly.");
        }
        private static ushort ReadWord(SpectrumMemory memory, ushort address)
        {
            return (ushort)(memory.ReadDirect(address) | (memory.ReadDirect((ushort)(address + 1)) << 8));
        }
        private static void WriteWord(SpectrumMemory memory, ushort address, ushort value)
        {
            memory.WriteDirect(address, (byte)(value & 0xFF));
            memory.WriteDirect((ushort)(address + 1), (byte)(value >> 8));
        }
        private static string ResolveOutputPath(string? outputPath)
        {
            return string.IsNullOrWhiteSpace(outputPath)
                ? Path.GetFullPath(Path.Combine("TEST", "basic-editor-verification-results.txt"))
                : Path.GetFullPath(outputPath);
        }
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
        private sealed class BasicVerificationLog(StreamWriter writer)
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
                    WriteLine($"PASS: {name}");
                }
                catch (Exception ex)
                {
                    Failed++;
                    WriteLine($"FAIL: {name} - {ex.Message}");
                    Debug.WriteLine(ex.ToString());
                }
            }
        }
    }
}
