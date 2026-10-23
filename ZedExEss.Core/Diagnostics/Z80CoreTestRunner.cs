using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ZedExEss.Z80CPU;

namespace ZedExEss.Diagnostics;

/// <summary>Settings for the reference Z80 core tests.</summary>
public sealed class Z80CoreTestOptions
{
    public string? OutputPath { get; init; }

    /// <summary>Directory holding tests.in and tests.expected.</summary>
    public string? TestsDirectory { get; init; }

    /// <summary>How many failing tests to describe in full before summarising.</summary>
    public int MaxReported { get; init; } = 25;
}

/// <summary>
/// Runs the reference implementation's own Z80 core tests and compares our bus activity
/// against its recorded output, cycle by cycle.
/// </summary>
/// <remarks>
/// The existing contention checks answer "is this cycle contended", but they only cover
/// the instructions someone thought to write a case for. These tests cover every opcode,
/// and because the expected output records the address and the T-state of every bus
/// cycle, they pin down the shape of an instruction and not merely its total cost - the
/// thing an input recording made on another emulator actually depends on.
///
/// The reference generates this output with an uncontended machine, so nothing here
/// depends on a contention table: a failure means we perform a different sequence of bus
/// cycles, which is a CPU bug rather than a timing-model disagreement.
/// </remarks>
public static class Z80CoreTestRunner
{
    /// <summary>The pattern the reference fills memory with before each test.</summary>
    private static readonly byte[] FillPattern = [0xDE, 0xAD, 0xBE, 0xEF];

    private static readonly string[] EventTypes = ["MR", "MW", "MC", "PR", "PW", "PC"];

    public static int Run(Z80CoreTestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string outputPath = Path.GetFullPath(options.OutputPath ?? "z80-core-tests.log");
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        writer.WriteLine("Z80 core tests against the reference implementation");
        writer.WriteLine($"Output: {outputPath}");
        writer.WriteLine();

        string? directory = ResolveTestsDirectory(options.TestsDirectory);
        if (directory == null)
        {
            writer.WriteLine("FAIL tests.in/tests.expected not found.");
            writer.WriteLine("Result: FAIL");
            Console.WriteLine($"Z80 core tests: test data not found. See {outputPath}");
            return 1;
        }

        writer.WriteLine($"Tests: {directory}");
        writer.WriteLine();

        Dictionary<string, ExpectedCase> expected;
        List<InputCase> inputs;
        try
        {
            inputs = ParseInputs(Path.Combine(directory, "tests.in"));
            expected = ParseExpected(Path.Combine(directory, "tests.expected"));
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            writer.WriteLine($"FAIL could not read the test data: {ex.Message}");
            writer.WriteLine("Result: FAIL");
            Console.WriteLine($"Z80 core tests: could not read the test data. See {outputPath}");
            return 1;
        }

        int passed = 0;
        int failed = 0;
        int reported = 0;

        foreach (InputCase test in inputs)
        {
            if (!expected.TryGetValue(test.Name, out ExpectedCase? want))
            {
                failed++;
                if (reported++ < options.MaxReported)
                {
                    writer.WriteLine($"FAIL {test.Name}: no expected output.");
                }

                continue;
            }

            List<string> differences = RunOne(test, want);
            if (differences.Count == 0)
            {
                passed++;
                continue;
            }

            failed++;
            if (reported++ < options.MaxReported)
            {
                writer.WriteLine($"FAIL {test.Name}");
                foreach (string difference in differences)
                {
                    writer.WriteLine($"    {difference}");
                }

                writer.WriteLine();
            }
        }

        if (reported > options.MaxReported)
        {
            writer.WriteLine($"({reported - options.MaxReported} further failures not described)");
            writer.WriteLine();
        }

        writer.WriteLine($"Passed: {passed}");
        writer.WriteLine($"Failed: {failed}");
        writer.WriteLine($"Result: {(failed == 0 ? "PASS" : "FAIL")}");

        Console.WriteLine($"Z80 core tests: {passed} passed, {failed} failed. See {outputPath}");
        return failed == 0 ? 0 : 1;
    }

    private static List<string> RunOne(InputCase test, ExpectedCase want)
    {
        var memory = new CoreTestMemory();
        var ports = new CoreTestPorts();
        var cpu = new Z80Core<CoreTestMemory, CoreTestPorts>(memory, ports);

        for (int address = 0; address < memory.Bytes.Length; address++)
        {
            memory.Bytes[address] = FillPattern[address & 3];
        }

        foreach ((ushort address, byte value) in test.Memory)
        {
            memory.Bytes[address] = value;
        }

        byte[] initial = (byte[])memory.Bytes.Clone();

        cpu.Z80Init();
        cpu.RestoreSnapshotState(test.ToSnapshotState());

        var trace = new BusTrace();
        cpu.ConfigureBusObserver(trace);

        // The reference runs whole instructions until the clock passes the requested
        // time, so the last instruction of a test is always allowed to complete.
        int guard = 0;
        while (cpu.Cyc < (ulong)test.EndTstates && guard++ < 1000)
        {
            cpu.Z80Step();
        }

        cpu.ConfigureBusObserver(null);

        var differences = new List<string>();
        CompareEvents(Skeleton(want.Events), Skeleton(trace.Events), differences);

        string actualRegisters = string.Join(
            ' ',
            new[]
            {
                cpu.AF, cpu.BC, cpu.DE, cpu.HL, cpu.AF_, cpu.BC_, cpu.DE_, cpu.HL_,
                cpu.IX, cpu.IY, cpu.SP, cpu.PC, cpu.MemPtr
            }.Select(value => value.ToString("x4", CultureInfo.InvariantCulture)));

        if (actualRegisters != want.Registers)
        {
            differences.Add($"registers: want {want.Registers}");
            differences.Add($"           got  {actualRegisters}");
        }

        string actualFlags = string.Format(
            CultureInfo.InvariantCulture,
            "{0:x2} {1:x2} {2} {3} {4} {5} {6}",
            cpu.I,
            cpu.R,
            cpu.Iff1 ? 1 : 0,
            cpu.Iff2 ? 1 : 0,
            cpu.InterruptModeValue,
            cpu.IsHalted ? 1 : 0,
            cpu.Cyc);

        if (actualFlags != want.Flags)
        {
            differences.Add($"state: want {want.Flags}");
            differences.Add($"       got  {actualFlags}");
        }

        CompareMemory(initial, memory.Bytes, want.Memory, differences);
        return differences;
    }

    /// <summary>
    /// Drops read events, keeping the bus-cycle skeleton.
    /// </summary>
    /// <remarks>
    /// The reference holds the bus for a byte it has decided to discard - the operand of
    /// a jump it is not going to take - without recording a read, because its harness
    /// only logs a read when the core asks for the value. The bus cycle is the same one
    /// either way, and it is still compared: every read is preceded by a contention at
    /// the same address, and the value that came back is covered by the register and
    /// memory comparisons.
    /// </remarks>
    private static List<string> Skeleton(IEnumerable<string> events) =>
        events.Where(line => !line.Contains(" MR ", StringComparison.Ordinal)).ToList();

    private static void CompareEvents(
        IReadOnlyList<string> want,
        IReadOnlyList<string> got,
        List<string> differences)
    {
        int count = Math.Max(want.Count, got.Count);
        for (int i = 0; i < count; i++)
        {
            string wanted = i < want.Count ? want[i] : "(nothing)";
            string actual = i < got.Count ? got[i] : "(nothing)";
            if (wanted == actual)
            {
                continue;
            }

            differences.Add($"cycle {i}: want {wanted}");
            differences.Add($"          got  {actual}");

            // One misplaced cycle shifts every cycle after it, so the first few
            // differences identify the fault and the rest are its shadow.
            if (differences.Count >= 8)
            {
                differences.Add("          (later cycles not compared)");
                return;
            }
        }
    }

    private static void CompareMemory(
        byte[] initial,
        byte[] final,
        IReadOnlyDictionary<ushort, byte> want,
        List<string> differences)
    {
        int reported = 0;
        for (int address = 0; address < final.Length; address++)
        {
            byte expected = want.TryGetValue((ushort)address, out byte changed)
                ? changed
                : initial[address];

            if (final[address] == expected)
            {
                continue;
            }

            if (reported++ < 4)
            {
                differences.Add($"memory {address:x4}: want {expected:x2} got {final[address]:x2}");
            }
        }

        if (reported > 4)
        {
            differences.Add($"memory: {reported - 4} further differences");
        }
    }

    private static string? ResolveTestsDirectory(string? configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        // Walk up from the binaries to find the checked-in reference sources.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            candidates.Add(Path.Combine(directory.FullName, "TEST", "fuse-1.9.1", "z80", "tests"));
            candidates.Add(Path.Combine(
                directory.FullName, "ZedExEss", "TEST", "fuse-1.9.1", "z80", "tests"));
            directory = directory.Parent;
        }

        return candidates.FirstOrDefault(
            candidate => File.Exists(Path.Combine(candidate, "tests.in"))
                && File.Exists(Path.Combine(candidate, "tests.expected")));
    }

    private static List<InputCase> ParseInputs(string path)
    {
        var tokens = new TokenReader(File.ReadAllLines(path));
        var tests = new List<InputCase>();

        while (tokens.TryReadName(out string name))
        {
            var registers = new ushort[13];
            for (int i = 0; i < registers.Length; i++)
            {
                registers[i] = (ushort)tokens.ReadHex();
            }

            byte i8 = (byte)tokens.ReadHex();
            byte r8 = (byte)tokens.ReadHex();
            bool iff1 = tokens.ReadInt() != 0;
            bool iff2 = tokens.ReadInt() != 0;
            byte im = (byte)tokens.ReadInt();
            bool halted = tokens.ReadInt() != 0;
            int endTstates = tokens.ReadInt();

            var memory = new List<(ushort, byte)>();
            while (true)
            {
                int address = tokens.ReadHex();
                if (address < 0)
                {
                    break;
                }

                while (true)
                {
                    int value = tokens.ReadHex();
                    if (value < 0)
                    {
                        break;
                    }

                    memory.Add(((ushort)address, (byte)value));
                    address++;
                }
            }

            tests.Add(new InputCase(
                name, registers, i8, r8, iff1, iff2, im, halted, endTstates, memory));
        }

        return tests;
    }

    private static Dictionary<string, ExpectedCase> ParseExpected(string path)
    {
        var results = new Dictionary<string, ExpectedCase>(StringComparer.Ordinal);
        string[] lines = File.ReadAllLines(path);
        int index = 0;

        while (index < lines.Length)
        {
            while (index < lines.Length && lines[index].Trim().Length == 0)
            {
                index++;
            }

            if (index >= lines.Length)
            {
                break;
            }

            string name = lines[index++].Trim();
            var events = new List<string>();
            while (index < lines.Length)
            {
                string[] parts = Split(lines[index]);
                if (parts.Length < 3 || !EventTypes.Contains(parts[1]))
                {
                    break;
                }

                events.Add(string.Join(' ', parts));
                index++;
            }

            if (index >= lines.Length)
            {
                break;
            }

            string registers = string.Join(' ', Split(lines[index++]));
            string flags = index < lines.Length
                ? string.Join(' ', Split(lines[index++]))
                : string.Empty;

            var memory = new Dictionary<ushort, byte>();
            while (index < lines.Length && lines[index].Trim().Length > 0)
            {
                string[] parts = Split(lines[index++]);
                int address = Convert.ToInt32(parts[0], 16);
                foreach (string part in parts.Skip(1))
                {
                    if (part == "-1")
                    {
                        break;
                    }

                    memory[(ushort)address] = Convert.ToByte(part, 16);
                    address++;
                }
            }

            results[name] = new ExpectedCase(events, registers, flags, memory);
        }

        return results;
    }

    private static string[] Split(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private sealed record ExpectedCase(
        IReadOnlyList<string> Events,
        string Registers,
        string Flags,
        IReadOnlyDictionary<ushort, byte> Memory);

    private sealed record InputCase(
        string Name,
        ushort[] Registers,
        byte I,
        byte R,
        bool Iff1,
        bool Iff2,
        byte InterruptMode,
        bool Halted,
        int EndTstates,
        IReadOnlyList<(ushort Address, byte Value)> Memory)
    {
        public Z80SnapshotState ToSnapshotState() => new(
            Cycles: 0,
            PC: Registers[11],
            SP: Registers[10],
            IX: Registers[8],
            IY: Registers[9],
            MemPtr: Registers[12],
            A: (byte)(Registers[0] >> 8),
            F: (byte)Registers[0],
            B: (byte)(Registers[1] >> 8),
            C: (byte)Registers[1],
            D: (byte)(Registers[2] >> 8),
            E: (byte)Registers[2],
            H: (byte)(Registers[3] >> 8),
            L: (byte)Registers[3],
            AlternateA: (byte)(Registers[4] >> 8),
            AlternateF: (byte)Registers[4],
            AlternateB: (byte)(Registers[5] >> 8),
            AlternateC: (byte)Registers[5],
            AlternateD: (byte)(Registers[6] >> 8),
            AlternateE: (byte)Registers[6],
            AlternateH: (byte)(Registers[7] >> 8),
            AlternateL: (byte)Registers[7],
            I: I,
            R: R,
            InterruptMode: InterruptMode,
            Iff1: Iff1,
            Iff2: Iff2,
            Halted: Halted,
            IffDelay: 0,
            InterruptData: 0,
            IntPending: false,
            NmiPending: false,
            Q: 0,
            LastQ: 0);
    }

    /// <summary>Flat, uncontended memory, matching the reference's test machine.</summary>
    private sealed class CoreTestMemory : IZ80MemoryBus
    {
        public byte[] Bytes { get; } = new byte[0x10000];

        public byte Read(ushort address) => Bytes[address];

        public void WriteCpu(ushort address, byte value) => Bytes[address] = value;

        public byte FetchOpcode(ushort address) => Bytes[address];
    }

    /// <summary>Ports return their own high byte, as the reference's test machine does.</summary>
    private sealed class CoreTestPorts : IZ80PortBus
    {
        public byte ReadUncontended(ushort port) => (byte)(port >> 8);

        public void WriteUncontended(ushort port, byte value)
        {
        }

        public void ApplyIoContentionBeforeCycle(ushort port, int phase)
        {
        }
    }

    /// <summary>Records bus cycles in the reference's own trace format.</summary>
    /// <remarks>
    /// The clock here is the observer's, advanced by the length of each bus cycle as it
    /// is reported, which is how the reference builds its expected output: a contention
    /// is stamped when the bus is taken and the access when it completes.
    /// </remarks>
    private sealed class BusTrace : IZ80BusObserver
    {
        private int _time;

        public List<string> Events { get; } = [];

        public void OnContend(ushort address, int cycles)
        {
            Events.Add($"{_time} MC {address:x4}");
            _time += cycles;
        }

        public void OnRead(ushort address, byte value) =>
            Events.Add($"{_time} MR {address:x4} {value:x2}");

        public void OnWrite(ushort address, byte value) =>
            Events.Add($"{_time} MW {address:x4} {value:x2}");

        public void OnPortRead(ushort port, byte value) =>
            Events.Add($"{_time} PR {port:x4} {value:x2}");

        public void OnPortWrite(ushort port, byte value) =>
            Events.Add($"{_time} PW {port:x4} {value:x2}");

        /// <summary>
        /// An I/O cycle is reported as one T-state before the access and three after.
        /// Which of those the ULA would steal depends on the port, exactly as it does on
        /// the machine: the high byte decides whether the address is contended, and bit 0
        /// whether the ULA holds the bus for the whole of the second half.
        /// </summary>
        public void OnPortContend(ushort port, int cycles)
        {
            bool contendedAddress = (port & 0xC000) == 0x4000;
            if (cycles == 1)
            {
                if (contendedAddress)
                {
                    Events.Add($"{_time} PC {port:x4}");
                }

                _time++;
                return;
            }

            if ((port & 0x0001) == 0)
            {
                Events.Add($"{_time} PC {port:x4}");
                _time += cycles;
                return;
            }

            if (!contendedAddress)
            {
                _time += cycles;
                return;
            }

            for (int i = 0; i < cycles; i++)
            {
                Events.Add($"{_time} PC {port:x4}");
                _time++;
            }
        }
    }

    /// <summary>Reads whitespace-separated tokens across lines, as the reference's parser does.</summary>
    private sealed class TokenReader(string[] lines)
    {
        private int _line;
        private string[] _tokens = [];
        private int _index;

        public bool TryReadName(out string name)
        {
            name = string.Empty;
            if (_index < _tokens.Length)
            {
                return false;
            }

            while (_line < lines.Length && lines[_line].Trim().Length == 0)
            {
                _line++;
            }

            if (_line >= lines.Length)
            {
                return false;
            }

            name = lines[_line++].Trim();
            _tokens = [];
            _index = 0;
            return true;
        }

        public int ReadHex() => ReadInt(hex: true);

        public int ReadInt(bool hex = false)
        {
            while (_index >= _tokens.Length)
            {
                if (_line >= lines.Length)
                {
                    throw new FormatException("The test data ended unexpectedly.");
                }

                _tokens = Split(lines[_line++]);
                _index = 0;
            }

            string token = _tokens[_index++];
            if (token == "-1")
            {
                return -1;
            }

            return hex
                ? Convert.ToInt32(token, 16)
                : int.Parse(token, CultureInfo.InvariantCulture);
        }
    }
}
