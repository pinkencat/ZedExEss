using System.Diagnostics;
using System.IO;
using System.Text;
using System;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Z80CPU;

namespace ZedExEss.Diagnostics
{
    /// <summary>Program, log and instruction-limit settings for the Raxoft test run.</summary>
    public sealed class RaxoftZ80FullOptions
    {
        public string? ProgramPath { get; init; }
        public string? OutputPath { get; init; }
        public long MaxInstructions { get; init; } = 5_000_000_000;
    }

    /// <summary>Program, log and instruction-limit settings for the Raxoft MEMPTR test run.</summary>
    public sealed class RaxoftZ80MemptrOptions
    {
        public string? ProgramPath { get; init; }
        public string? OutputPath { get; init; }
        public long MaxInstructions { get; init; } = 5_000_000_000;
    }

    /// <summary>Program, log and instruction-limit settings for the Raxoft flags test run.</summary>
    public sealed class RaxoftZ80FlagsOptions
    {
        public string? ProgramPath { get; init; }
        public string? OutputPath { get; init; }
        public long MaxInstructions { get; init; } = 5_000_000_000;
    }

    /// <summary>Program, log and instruction-limit settings for the Raxoft post-CCF test run.</summary>
    public sealed class RaxoftZ80CcfOptions
    {
        public string? ProgramPath { get; init; }
        public string? OutputPath { get; init; }
        public long MaxInstructions { get; init; } = 5_000_000_000;
    }

    /// <summary>Runs the Spectrum Raxoft z80full binary directly against the production CPU core.</summary>
    /// <remarks>
    /// ROM character-output entry points are trapped only to capture text; instruction execution,
    /// memory behaviour and the FE-port response remain equivalent to the full 48K machine. This
    /// makes CRC failures directly comparable with an interactive run while avoiding ROM boot and tape loading.
    /// </remarks>
    public static class RaxoftZ80FullRunner
    {
        public static int Run(RaxoftZ80FullOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return RaxoftHeadlessRunner.Run("z80full", options.ProgramPath, options.OutputPath, options.MaxInstructions);
        }
    }

    /// <summary>Runs the Spectrum Raxoft z80memptr binary directly against the production CPU core.</summary>
    public static class RaxoftZ80MemptrRunner
    {
        public static int Run(RaxoftZ80MemptrOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return RaxoftHeadlessRunner.Run("z80memptr", options.ProgramPath, options.OutputPath, options.MaxInstructions);
        }
    }

    /// <summary>Runs the Raxoft z80flags binary directly against the production CPU core.</summary>
    public static class RaxoftZ80FlagsRunner
    {
        public static int Run(RaxoftZ80FlagsOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return RaxoftHeadlessRunner.Run("z80flags", options.ProgramPath, options.OutputPath, options.MaxInstructions);
        }
    }

    /// <summary>
    /// Runs the Raxoft z80ccf binary. This variant executes CCF after each test sequence and
    /// therefore validates both normal flag results and the Z80's hidden Q-latch behaviour.
    /// </summary>
    public static class RaxoftZ80CcfRunner
    {
        public static int Run(RaxoftZ80CcfOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return RaxoftHeadlessRunner.Run("z80ccf", options.ProgramPath, options.OutputPath, options.MaxInstructions);
        }
    }

    /// <summary>
    /// Shared execution environment for the Raxoft variants. Keeping memory, ports and ROM traps
    /// identical is essential: otherwise a CRC difference could be caused by the runner rather
    /// than by the CPU behaviour selected by each assembled test program.
    /// </summary>
    internal static class RaxoftHeadlessRunner
    {
        private const ushort ProgramAddress = 0x8000;
        private const ushort PrintCharTrapAddress = 0x0010;
        private const ushort ChannelOpenTrapAddress = 0x1601;
        private const ushort ExitAddress = 0xFFFF;
        private const ushort FinalStackPointer = 0xFFFE;
        private const ushort InitialStackPointer = 0xFFFC;
        public static int Run(string testProgramName, string? requestedProgramPath, string? requestedOutputPath, long maxInstructions)
        {
            string? repositoryRoot = FindRepositoryRoot(testProgramName);
            string outputPath = ResolveOutputPath(repositoryRoot, requestedOutputPath, testProgramName);
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
            var sink = new RaxoftOutputSink(writer);
            TextWriter originalConsoleOut = Console.Out;
            TextWriter originalConsoleError = Console.Error;

            try
            {
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);

                string programPath = ResolveProgramPath(repositoryRoot, requestedProgramPath, testProgramName);
                byte[] program = LoadProgram(programPath);
                if (program.Length == 0)
                {
                    throw new InvalidDataException($"'{programPath}' does not contain any program bytes.");
                }

                if (program.Length > ushort.MaxValue - ProgramAddress + 1)
                {
                    throw new InvalidDataException($"'{programPath}' is {program.Length} bytes; the runner can load at most 32768 bytes at 0x8000.");
                }

                sink.WriteLine($"Raxoft {testProgramName} headless run");
                sink.WriteLine($"Program: {programPath}");
                sink.WriteLine($"Output:  {outputPath}");
                sink.WriteLine($"Loaded:  {program.Length} bytes at 0x{ProgramAddress:X4}");
                sink.WriteLine(string.Empty);

                var memory = new SpectrumMemory(SpectrumModel.Spectrum48K, RomSet.CreateBlank(1));
                var ports = new SpectrumPortBus(SpectrumModel.Spectrum48K, contendedPages: memory);
                ports.AddDevice(new RaxoftPortDevice());

                var cpu = new Z80(memory, ports);
                cpu.Z80Init();
                LoadProgram(memory, program);
                WriteReturnAddress(memory, InitialStackPointer, ExitAddress);

                cpu.PC = ProgramAddress;
                cpu.SP = InitialStackPointer;
                cpu.IX = 0;
                cpu.IY = 0;
                cpu.SetHalted(false);
                cpu.SetInterruptState(0, false, false);

                long instructionCount = 0;
                var stopwatch = Stopwatch.StartNew();

                while (cpu.PC != ExitAddress || cpu.SP != FinalStackPointer)
                {
                    if (instructionCount >= maxInstructions)
                    {
                        sink.WriteLine(string.Empty);
                        sink.WriteLine($"Aborted: instruction limit reached ({maxInstructions:N0}).");
                        sink.WriteLine($"CPU state: PC={cpu.PC:X4} SP={cpu.SP:X4} AF={cpu.AF:X4} BC={cpu.BC:X4} DE={cpu.DE:X4} HL={cpu.HL:X4}");
                        return 2;
                    }

                    if (cpu.PC == PrintCharTrapAddress)
                    {
                        sink.WriteSpectrumByte(cpu.A);
                        ReturnFromTrap(cpu, memory);
                        continue;
                    }

                    if (cpu.PC == ChannelOpenTrapAddress)
                    {
                        ReturnFromTrap(cpu, memory);
                        continue;
                    }

                    cpu.Z80Step();
                    instructionCount++;
                }

                stopwatch.Stop();
                sink.WriteLine(string.Empty);
                sink.WriteLine($"Completed in {stopwatch.Elapsed.TotalSeconds:F3}s");
                sink.WriteLine($"Instructions: {instructionCount:N0}");
                sink.WriteLine($"CPU T-states:  {cpu.Cyc:N0}");

                double millionInstructionsPerSecond = instructionCount / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001) / 1_000_000.0;
                sink.WriteLine($"Throughput:    {millionInstructionsPerSecond:F2} MIPS");

                return sink.HadFailure ? 1 : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                sink.WriteLine($"Raxoft {testProgramName} headless run");
                sink.WriteLine($"Output: {outputPath}");
                sink.WriteLine($"Error:  {ex.Message}");
                Debug.WriteLine(ex.ToString());
                return 3;
            }
            finally
            {
                Console.SetOut(originalConsoleOut);
                Console.SetError(originalConsoleError);
            }
        }
        private static void LoadProgram(SpectrumMemory memory, ReadOnlySpan<byte> program)
        {
            for (int i = 0; i < program.Length; i++)
            {
                memory.WriteDirect((ushort)(ProgramAddress + i), program[i]);
            }
        }
        private static void ReturnFromTrap(Z80 cpu, SpectrumMemory memory)
        {
            ushort returnAddress = ReadWord(memory, cpu.SP);
            cpu.SP = unchecked((ushort)(cpu.SP + 2));
            cpu.PC = returnAddress;
        }
        private static ushort ReadWord(SpectrumMemory memory, ushort address)
        {
            byte lo = memory.ReadDirect(address);
            byte hi = memory.ReadDirect(unchecked((ushort)(address + 1)));
            return (ushort)(lo | (hi << 8));
        }
        private static void WriteReturnAddress(SpectrumMemory memory, ushort address, ushort returnAddress)
        {
            memory.WriteDirect(address, (byte)returnAddress);
            memory.WriteDirect(unchecked((ushort)(address + 1)), (byte)(returnAddress >> 8));
        }
        private static byte[] LoadProgram(string path)
        {
            string extension = Path.GetExtension(path);
            if (extension.Equals(".tap", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractLargestTapDataBlock(path);
            }

            return File.ReadAllBytes(path);
        }
        private static byte[] ExtractLargestTapDataBlock(string path)
        {
            byte[] tape = File.ReadAllBytes(path);
            byte[]? best = null;
            int offset = 0;

            while (offset + 2 <= tape.Length)
            {
                int length = tape[offset] | (tape[offset + 1] << 8);
                offset += 2;

                if (length <= 0 || offset + length > tape.Length)
                {
                    throw new InvalidDataException($"'{path}' contains an invalid TAP block length at offset {offset - 2}.");
                }

                ReadOnlySpan<byte> block = tape.AsSpan(offset, length);
                offset += length;

                if (block.Length < 3 || block[0] != 0xFF)
                {
                    continue;
                }

                byte[] data = block.Slice(1, block.Length - 2).ToArray();
                if (best == null || data.Length > best.Length)
                {
                    best = data;
                }
            }

            return best ?? throw new InvalidDataException($"'{path}' does not contain a TAP data block.");
        }
        private static string ResolveProgramPath(string? repositoryRoot, string? requestedPath, string testProgramName)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                return Path.GetFullPath(requestedPath);
            }

            if (repositoryRoot != null)
            {
                string outPath = Path.Combine(repositoryRoot, "TEST", "raxoft", $"{testProgramName}.out");
                if (IsNonEmptyFile(outPath))
                {
                    return outPath;
                }

                string tapPath = Path.Combine(repositoryRoot, "TEST", "raxoft", $"{testProgramName}.tap");
                if (IsNonEmptyFile(tapPath))
                {
                    return tapPath;
                }

            }

            throw new FileNotFoundException(
                $"Could not find a non-empty TEST\\raxoft\\{testProgramName}.out or TEST\\raxoft\\{testProgramName}.tap. " +
                $"Run .\\TEST\\RunRaxoftZ80Full.ps1 -TestName {testProgramName} -Rebuild so its SjASMPlus compatibility pass is applied, " +
                $"or pass --raxoft-program <path> to a known-good {testProgramName}.out or {testProgramName}.tap.");
        }
        private static string ResolveOutputPath(string? repositoryRoot, string? requestedPath, string testProgramName)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                return Path.GetFullPath(requestedPath);
            }

            string root = repositoryRoot ?? Directory.GetCurrentDirectory();
            return Path.Combine(root, "TEST", "raxoft", $"{testProgramName}-results.txt");
        }
        private static bool IsNonEmptyFile(string path)
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        private static string? FindRepositoryRoot(string testProgramName)
        {
            string? root = FindRepositoryRootFrom(Directory.GetCurrentDirectory(), testProgramName);
            if (root != null)
            {
                return root;
            }

            return FindRepositoryRootFrom(AppContext.BaseDirectory, testProgramName);
        }
        private static string? FindRepositoryRootFrom(string startDirectory, string testProgramName)
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory != null)
            {
                string projectPath = Path.Combine(directory.FullName, "ZedExEss.csproj");
                string testPath = Path.Combine(directory.FullName, "TEST", "raxoft", $"{testProgramName}.asm");
                if (File.Exists(projectPath) && File.Exists(testPath))
                {
                    return directory.FullName;
                }

                string sourceDirectory = Path.Combine(directory.FullName, "ZedExEss");
                string nestedProjectPath = Path.Combine(sourceDirectory, "ZedExEss.csproj");
                string nestedTestPath = Path.Combine(sourceDirectory, "TEST", "raxoft", $"{testProgramName}.asm");
                if (File.Exists(nestedProjectPath) && File.Exists(nestedTestPath))
                {
                    return sourceDirectory;
                }

                directory = directory.Parent;
            }

            return null;
        }
        private sealed class RaxoftPortDevice : IPortDevice
        {
            private byte _ulaDefault = 0xFF;
            public bool HandlesPort(ushort port)
            {
                return (port & 0x0001) == 0;
            }
            public byte Read(ushort port)
            {
                return _ulaDefault;
            }
            public void Write(ushort port, byte value)
            {
                _ulaDefault = (value & 0x10) != 0 ? (byte)0xFF : (byte)0xBF;
            }
        }
        private sealed class RaxoftOutputSink(TextWriter writer)
        {
            private readonly TextWriter _writer = writer;
            private readonly StringBuilder _currentLine = new();

            public bool HadFailure { get; private set; }
            public void WriteSpectrumByte(byte value)
            {
                if (value == 13)
                {
                    EndLine();
                    return;
                }

                if (value < 32 || value == 127)
                {
                    return;
                }

                char ch = (char)value;
                _currentLine.Append(ch);
                _writer.Write(ch);
                Debug.Write(ch.ToString());
            }
            public void WriteLine(string line)
            {
                if (_currentLine.Length > 0)
                {
                    EndLine();
                }

                _writer.WriteLine(line);
                Debug.WriteLine(line);
            }
            private void EndLine()
            {
                string line = _currentLine.ToString();
                if (line.Contains("FAILED", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("tests failed", StringComparison.OrdinalIgnoreCase))
                {
                    HadFailure = true;
                }

                _currentLine.Clear();
                _writer.WriteLine();
                Debug.WriteLine(string.Empty);
            }
        }
    }
}
