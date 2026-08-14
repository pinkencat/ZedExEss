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
