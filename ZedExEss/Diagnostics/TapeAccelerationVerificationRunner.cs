using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Spectrum.Tape;
using ZedExEss.Spectrum.Video;
using ZedExEss.Z80CPU;

namespace ZedExEss.Diagnostics
{
    /// <summary>Output settings for deterministic tape-accelerator unit scenarios.</summary>
    public sealed class TapeAccelerationVerificationOptions
    {
        public string? OutputPath { get; init; }
    }
    /// <summary>
    /// Verifies semantic edge-loader recognition, polarity, fallback and simulated-return behaviour.
    /// </summary>
    /// <remarks>
    /// Synthetic polling routines isolate accelerator contracts from TZX parsing and ROM startup;
    /// complete-loader compatibility belongs to <see cref="TapeGameVerificationRunner"/>.
    /// </remarks>
    public static class TapeAccelerationVerificationRunner
    {
        private const ushort ScanStart = 0x8001;
        private const ushort OpcodePc = 0x8005;
        private const ushort PcAfterIn = 0x8007;
        public static int Run(TapeAccelerationVerificationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            string outputPath = string.IsNullOrWhiteSpace(options.OutputPath)
                ? Path.Combine(AppContext.BaseDirectory, "tape-acceleration-verification.log")
                : Path.GetFullPath(options.OutputPath);

            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
            int failed = 0;
            Check(writer, ref failed, "Semantic signature claims recognised ROM-style loop", VerifyRecognisedSignature);
            Check(writer, ref failed, "Semantic result uses the current pulse length", VerifyCurrentPulseClassification);
            Check(writer, ref failed, "Semantic semantic acceleration returns and advances one edge", VerifySemanticAcceleration);
            Check(writer, ref failed, "Semantic semantic result preserves inverse EAR polarity in C bit 5", VerifySemanticEarPolarity);
            Check(writer, ref failed, "Semantic semantic result leaves C bit 6 untouched", VerifySemanticPreservesCBit6);
            Check(writer, ref failed, "Atomic semantic edge reports one complete transition", VerifyAtomicEdgeContract);
            Check(writer, ref failed, "Recognised hot path uses one snapshot and one atomic advance", VerifySemanticHotPathCallCount);
            Check(writer, ref failed, "Ordinary edge between claim and sample forces a seed read", VerifyClaimSampleEdgeRace);
            Check(writer, ref failed, "Active semantic signature is invalidated after self-modification", VerifyActiveSignatureInvalidation);
            Check(writer, ref failed, "Polling skips do not impersonate semantic edges", VerifyPollingSkipEdgeOrigin);
            Check(writer, ref failed, "Non-data phase is handed to polling fallback", VerifyNonDataPhaseFallsThrough);
            Check(writer, ref failed, "Unknown loop remains available to polling fallback", VerifyUnknownSignatureFallsThrough);
            Check(writer, ref failed, "Fast tape CPU batching preserves machine timing and state", VerifyInstructionTstateBatching);
            Check(writer, ref failed, "Fast tape CPU batching preserves frame interrupts", VerifyInstructionTstateBatchingWithInterrupts);
            writer.WriteLine(failed == 0 ? "Result: PASS" : $"Result: FAIL ({failed} failed checks)");
            return failed == 0 ? 0 : 1;
        }
        private static void VerifyRecognisedSignature()
        {
            TestMachine machine = CreateMachine();
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Known Semantic signature was not claimed.");
            Require(!machine.Accelerator.TryAcceleratePreparedRead(), "First read must seed edge state, not skip blindly.");
            Require(machine.Accelerator.MatchedReads == 1, "Matched-read counter did not advance.");
        }
        private static void VerifySemanticAcceleration()
        {
            TestMachine machine = CreateMachine();

            // First read establishes the recognised routine.
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Known signature was not claimed.");
            machine.Accelerator.TryAcceleratePreparedRead();

            // A normally reached data edge seeds the delayed short/long classifier.
            machine.EdgeSource.ObserveNormalDataEdge(isLong: false);
            machine.Cpu.PC = PcAfterIn;
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Known signature was lost after normal edge.");
            Require(!machine.Accelerator.TryAcceleratePreparedRead(), "Normal edge must defer acceleration for one read.");

            const ushort returnPc = 0x9123;
            machine.Cpu.SP = 0xA000;
            machine.Memory.WriteDirect(0xA000, (byte)(returnPc & 0xFF));
            machine.Memory.WriteDirect(0xA001, (byte)(returnPc >> 8));
            machine.Cpu.PC = PcAfterIn;
            machine.EdgeSource.NextPulseIsData = false;
            ulong before = machine.Cpu.Cyc;

            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Known signature was not claimed for accelerated read.");
            Require(machine.Accelerator.TryAcceleratePreparedRead(), "Prepared data read was not accelerated.");
            Require(machine.Cpu.PC == returnPc, "Simulated RET did not restore the caller PC.");
            Require(machine.Cpu.SP == 0xA002, "Simulated RET did not consume the return address.");
            Require(machine.EdgeSource.AcceleratedAdvances == 1, "Tape did not advance exactly one edge.");
            Require(machine.Cpu.Cyc == before, "Semantic compression unexpectedly advanced emulated CPU time.");
            Require((machine.Cpu.GetFlags() & 0x01) != 0, "Accelerated routine did not set carry.");
            Require((machine.Cpu.C & 0x20) == 0,
                "Accelerated routine did not store inverse EAR-high polarity in C bit 5.");

            machine.Cpu.PC = PcAfterIn;
            Require(!machine.Accelerator.TryClaimRead(OpcodePc, 0xFE),
                "Data-to-pause boundary retained stale classification and claimed another edge.");
        }
        private static void VerifySemanticEarPolarity()
        {
            TestMachine machine = CreateMachine();

            // Seed the delayed classification and accelerate with EAR low.
            // Stores the inverse input level, so C bit 5 must become set.
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Known signature was not claimed.");
            machine.Accelerator.TryAcceleratePreparedRead();
            machine.Cpu.SP = 0xA000;
            machine.Memory.WriteDirect(0xA000, 0x00);
            machine.Memory.WriteDirect(0xA001, 0x90);
            machine.Cpu.PC = PcAfterIn;
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Known signature was not claimed for EAR-low read.");
            Require(machine.Accelerator.TryAcceleratePreparedRead(), "EAR-low read was not accelerated.");
            Require((machine.Cpu.C & 0x20) != 0,
                "Accelerated routine did not store inverse EAR-low polarity in C bit 5.");

            // The advance above changes EAR to high. The following accelerated
            // result must clear bit 5 again.
            machine.Cpu.SP = 0xA000;
            machine.Cpu.PC = PcAfterIn;
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Known signature was not claimed for EAR-high read.");
            Require(machine.Accelerator.TryAcceleratePreparedRead(), "EAR-high read was not accelerated.");
            Require((machine.Cpu.C & 0x20) == 0,
                "Accelerated routine did not store inverse EAR-high polarity in C bit 5.");
        }
        private static void VerifyCurrentPulseClassification()
        {
            TestMachine machine = CreateMachine();
            machine.EdgeSource.CurrentPulseIsLong = true;

            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Known signature was not claimed.");
            Require(!machine.Accelerator.TryAcceleratePreparedRead(), "First read should seed the current interval.");

            machine.Cpu.SP = 0xA000;
            machine.Memory.WriteDirect(0xA000, 0x00);
            machine.Memory.WriteDirect(0xA001, 0x90);
            machine.Cpu.PC = PcAfterIn;
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Second read was not claimed.");
            Require(machine.Accelerator.TryAcceleratePreparedRead(), "Current long pulse was not accelerated.");
            Require(machine.Cpu.B == 0xFE, "Increasing loop did not receive the current long-pulse B value.");

            // The accelerated edge enters a short pulse. The next result must use
            // that new interval, not repeat the long interval which just ended.
            machine.Cpu.SP = 0xA000;
            machine.Cpu.PC = PcAfterIn;
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Following short pulse was not claimed.");
            Require(machine.Accelerator.TryAcceleratePreparedRead(), "Following short pulse was not accelerated.");
            Require(machine.Cpu.B == 0x00, "Pulse classifier remained one interval behind after a long-to-short edge.");
        }
        private static void VerifySemanticPreservesCBit6()
        {
            TestMachine machine = CreateMachine();
            machine.Cpu.C = 0x40;

            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Known signature was not claimed.");
            machine.Accelerator.TryAcceleratePreparedRead();
            machine.Cpu.SP = 0xA000;
            machine.Memory.WriteDirect(0xA000, 0x00);
            machine.Memory.WriteDirect(0xA001, 0x90);
            machine.Cpu.PC = PcAfterIn;
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Known signature was not claimed for bit-6 check.");
            Require(machine.Accelerator.TryAcceleratePreparedRead(), "Bit-6 check did not accelerate.");
            Require((machine.Cpu.C & 0x40) != 0, "Semantic acceleration modified C bit 6.");
        }
        private static void VerifyAtomicEdgeContract()
        {
            TestMachine machine = CreateMachine();
            machine.EdgeSource.CurrentPulseIsLong = true;
            machine.EdgeSource.NextPulseIsLong = false;

            Require(machine.EdgeSource.TryGetSemanticReadState(out TapeSemanticReadState state),
                "Current semantic state was unavailable.");
            Require(state.Flags == TapeAccelerationPulseFlags.LengthLong,
                "Semantic state did not classify the source interval as long.");
            Require(machine.EdgeSource.TryAdvanceSemanticEdge(state, out TapeSemanticEdgeResult result),
                "Atomic semantic transition failed.");
            Require(result.SourcePulseIndex == 0 && result.DestinationPulseIndex == 1,
                "Atomic transition did not report its exact pulse range.");
            Require(result.SourceFlags == TapeAccelerationPulseFlags.LengthLong
                && result.DestinationFlags == TapeAccelerationPulseFlags.LengthShort,
                "Atomic transition returned incorrect source/destination classifications.");
            Require(result.EarHighBefore != result.EarHighAfter,
                "Atomic transition did not report the EAR level change.");
            Require(machine.EdgeSource.SemanticMarks == 1 && machine.EdgeSource.AcceleratedAdvances == 1,
                "Atomic transition did not mark and advance exactly one edge.");
            Require(!machine.EdgeSource.TryAdvanceSemanticEdge(state, out _),
                "A stale prepared pulse index advanced a second edge.");
        }
        private static void VerifyClaimSampleEdgeRace()
        {
            TestMachine machine = CreateMachine();
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Known signature was not claimed.");

            // This models an ordinarily scheduled edge landing during the IN's
            // contention window, after decode but before the ULA samples EAR.
            machine.EdgeSource.ObserveNormalDataEdge(isLong: true);
            Require(!machine.Accelerator.TryAcceleratePreparedRead(),
                "A normally reached mid-instruction edge was compressed instead of seeded.");
            Require(machine.EdgeSource.AcceleratedAdvances == 0,
                "The stale claim advanced the tape despite the intervening normal edge.");
        }
        private static void VerifySemanticHotPathCallCount()
        {
            TestMachine machine = CreateMachine();
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Seed read was not claimed.");
            Require(!machine.Accelerator.TryAcceleratePreparedRead(), "Seed read unexpectedly advanced an edge.");

            machine.Cpu.SP = 0xA000;
            machine.Memory.WriteDirect(0xA000, 0x00);
            machine.Memory.WriteDirect(0xA001, 0x90);
            machine.Cpu.PC = PcAfterIn;
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Accelerated read was not claimed.");
            Require(machine.Accelerator.TryAcceleratePreparedRead(), "Recognised read did not advance atomically.");
            Require(machine.EdgeSource.SemanticStateReads == 2,
                "The sample point redundantly queried semantic pulse state.");
            Require(machine.EdgeSource.SemanticAdvanceAttempts == 1,
                "The recognised edge used more than one atomic advance attempt.");
        }
        private static void VerifyActiveSignatureInvalidation()
        {
            TestMachine machine = CreateMachine();
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE), "Known signature was not claimed.");

            // Destroy the INC B at the start of the active signature. The active
            // profile is deliberately revalidated periodically rather than hashing
            // memory on every tape edge.
            machine.Memory.WriteDirect(ScanStart, 0x00);
            bool stillClaimed = true;
            for (int i = 0; i < 80 && stillClaimed; i++)
            {
                machine.Cpu.PC = PcAfterIn;
                stillClaimed = machine.Accelerator.TryClaimRead(OpcodePc, 0xFE);
            }

            Require(!stillClaimed, "Modified loader signature remained active indefinitely.");
        }
        private static void VerifyUnknownSignatureFallsThrough()
        {
            TestMachine machine = CreateMachine();
            machine.Cpu.PC = 0x8202;
            machine.Memory.WriteDirect(0x8200, 0xDB);
            machine.Memory.WriteDirect(0x8201, 0xFE);
            Require(!machine.Accelerator.TryClaimRead(0x8200, 0xFE), "Unknown loop was claimed instead of falling through.");
        }
        private static void VerifyPollingSkipEdgeOrigin()
        {
            TestMachine machine = CreateMachine();
            var polling = new TapePollingLoopDetector(machine.Cpu, machine.Memory);
            polling.Configure(machine.EdgeSource);
            polling.Enabled = true;
            polling.SkippingEnabled = true;

            machine.Cpu.A = 0xBF;
            machine.Cpu.B = 0;
            for (int i = 0; i < 10; i++)
            {
                machine.Cpu.Cyc += 54;
                machine.Cpu.B++;
                polling.NotifyAndOperand(0x20);
                polling.BeforeInAImmediate(0x8200, 0xFE);
            }

            Require(polling.SkipEvents > 0, "Polling detector did not exercise its skip path.");
            Require(machine.EdgeSource.SemanticMarks == 0,
                "Polling skip marked an ordinary scheduled edge as semantic acceleration.");
        }
        private static void VerifyNonDataPhaseFallsThrough()
        {
            TestMachine machine = CreateMachine();
            machine.EdgeSource.CurrentPulseIsData = false;
            Require(!machine.Accelerator.TryClaimRead(OpcodePc, 0xFE),
                "Recognised routine claimed a pilot/pause/tail read instead of yielding to polling.");

            machine.EdgeSource.CurrentPulseIsData = true;
            Require(machine.Accelerator.TryClaimRead(OpcodePc, 0xFE),
                "Semantic matching did not resume when a real data pulse began.");
        }
        private static void VerifyInstructionTstateBatching()
        {
            TimingBatchMachine normal = CreateTimingBatchMachine();
            TimingBatchMachine batched = CreateTimingBatchMachine();
            batched.Emulator.FastTapeCpuBatchingEnabled = true;

            const int instructions = 50_000;
            for (int i = 0; i < instructions; i++)
            {
                normal.Emulator.StepInstruction();
                batched.Emulator.StepInstruction();
            }

            batched.Emulator.FastTapeCpuBatchingEnabled = false;
            Require(normal.Cpu.Cyc == batched.Cpu.Cyc, "Batched execution changed elapsed CPU T-states.");
            Require(normal.Cpu.PC == batched.Cpu.PC
                && normal.Cpu.SP == batched.Cpu.SP
                && normal.Cpu.A == batched.Cpu.A
                && normal.Cpu.B == batched.Cpu.B
                && normal.Cpu.C == batched.Cpu.C
                && normal.Cpu.D == batched.Cpu.D
                && normal.Cpu.E == batched.Cpu.E
                && normal.Cpu.H == batched.Cpu.H
                && normal.Cpu.L == batched.Cpu.L
                && normal.Cpu.R == batched.Cpu.R
                && normal.Cpu.GetFlags() == batched.Cpu.GetFlags(),
                "Batched execution changed CPU state.");
            Require(normal.Memory.ReadDirect(0x4000) == batched.Memory.ReadDirect(0x4000)
                && normal.Memory.ReadScreen(0x4000) == batched.Memory.ReadScreen(0x4000),
                "Batched execution changed CPU or ULA-visible RAM.");
            Require(normal.Renderer.TstatesUntilFrameEnd == batched.Renderer.TstatesUntilFrameEnd
                && normal.Renderer.BorderColorIndex == batched.Renderer.BorderColorIndex,
                "Batched execution changed ULA beam or border state.");
        }
        private static TimingBatchMachine CreateTimingBatchMachine()
        {
            const SpectrumModel model = SpectrumModel.Spectrum48K;
            var memory = new SpectrumMemory(model, RomSet.CreateBlank(1));
            var ports = new SpectrumPortBus(model, contendedPages: memory);
            var renderer = new SpectrumUlaRenderer(model, memory) { RenderEnabled = false };
            var audio = new SpectrumAudioRenderer(
                SpectrumAudioTiming.CpuClockHz(model),
                SpectrumAudioTiming.DefaultSampleRate);
            var keyboard = new SpectrumKeyboard();
            var earInput = new SpectrumEarInputDevice(audio);
            SpectrumEmulator? emulator = null;
            ports.AddDevice(new SpectrumUla(model, renderer, keyboard, earInput, audio, audio, () => emulator?.SyncToCpu()));

            var cpu = new Z80(memory, ports);
            cpu.Z80Init();
            var contention = SpectrumContentionProfile.Create(model);
            cpu.ConfigureNoMreqContention(memory, contention);
            cpu.ConfigureIoContention(true, SpectrumTimingModel.ForModel(model).IoWritesLatchAtEndOfCycle);
            memory.ConfigureTiming(cpu, contention);
            ports.ConfigureTiming(cpu, contention, memory);
            ports.ConfigureFloatingBus(new SpectrumFloatingBus(model, memory));
            emulator = new SpectrumEmulator(cpu, memory, ports, renderer, audio);

            // Exercises contended screen reads/writes, an exact ULA write latch,
            // an input sample point and a relative branch on every loop.
            byte[] program =
            [
                0xF3,                   // DI
                0x21, 0x00, 0x40,       // LD HL,4000
                0x34,                   // INC (HL)
                0x7E,                   // LD A,(HL)
                0xD3, 0xFE,             // OUT (FE),A
                0xDB, 0xFE,             // IN A,(FE)
                0x18, 0xF8              // JR 8004
            ];
            for (int i = 0; i < program.Length; i++)
            {
                memory.WriteDirect(unchecked((ushort)(0x8000 + i)), program[i]);
            }

            cpu.PC = 0x8000;
            cpu.SP = 0xFF00;
            return new TimingBatchMachine(cpu, memory, renderer, emulator);
        }
        private static void VerifyInstructionTstateBatchingWithInterrupts()
        {
            TimingBatchMachine normal = CreateInterruptBatchMachine();
            TimingBatchMachine batched = CreateInterruptBatchMachine();
            batched.Emulator.FastTapeCpuBatchingEnabled = true;

            const int frames = 200;
            for (int i = 0; i < frames; i++)
            {
                normal.Emulator.RunFrame(presentFrame: false);
                batched.Emulator.RunFrame(presentFrame: false);
            }

            batched.Emulator.FastTapeCpuBatchingEnabled = false;
            Require(normal.Cpu.Cyc == batched.Cpu.Cyc
                && normal.Cpu.PC == batched.Cpu.PC
                && normal.Cpu.SP == batched.Cpu.SP
                && normal.Cpu.R == batched.Cpu.R
                && normal.Cpu.GetFlags() == batched.Cpu.GetFlags(),
                "Batched execution changed frame-interrupt CPU state.");
            Require(normal.Renderer.TstatesUntilFrameEnd == batched.Renderer.TstatesUntilFrameEnd,
                "Batched execution changed the beam position across interrupts.");
        }
        private static TimingBatchMachine CreateInterruptBatchMachine()
        {
            TimingBatchMachine machine = CreateTimingBatchMachine();
            byte[] loop = [0x00, 0xC3, 0x00, 0x80]; // NOP; JP 8000
            for (int i = 0; i < loop.Length; i++)
            {
                machine.Memory.WriteDirect(unchecked((ushort)(0x8000 + i)), loop[i]);
            }

            // IM 2 vector 80FF -> 9000. The ISR re-enables interrupts and returns.
            machine.Memory.WriteDirect(0x80FF, 0x00);
            machine.Memory.WriteDirect(0x8100, 0x90);
            machine.Memory.WriteDirect(0x9000, 0xFB); // EI
            machine.Memory.WriteDirect(0x9001, 0xED);
            machine.Memory.WriteDirect(0x9002, 0x4D); // RETI
            machine.Cpu.PC = 0x8000;
            machine.Cpu.SP = 0xFF00;
            machine.Cpu.I = 0x80;
            machine.Cpu.SetInterruptState(2, iff1: true, iff2: true);
            return machine;
        }
        private static TestMachine CreateMachine()
        {
            var memory = new SpectrumMemory(SpectrumModel.Spectrum48K, RomSet.CreateBlank(1));
            var ports = new SpectrumPortBus(SpectrumModel.Spectrum48K, contendedPages: memory);
            var cpu = new Z80(memory, ports);
            cpu.Z80Init();
            cpu.PC = PcAfterIn;

            // Common increasing-counter signature, scanned from PC-6.
            byte[] routine = [0x04, 0xC8, 0x3E, 0x7F, 0xDB, 0xFE, 0x1F, 0xD0, 0xA9, 0xE6, 0x20, 0x28, 0xF3];
            for (int i = 0; i < routine.Length; i++)
            {
                memory.WriteDirect(unchecked((ushort)(ScanStart + i)), routine[i]);
            }

            var edgeSource = new VerificationEdgeSource();
            var accelerator = new SemanticTapeEdgeAccelerator(cpu, memory);
            accelerator.Configure(edgeSource, () => edgeSource.EarHigh);
            return new TestMachine(cpu, memory, edgeSource, accelerator);
        }
        private static void Check(StreamWriter writer, ref int failed, string name, Action action)
        {
            try
            {
                action();
                writer.WriteLine($"PASS {name}");
                Debug.WriteLine($"PASS {name}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                failed++;
                writer.WriteLine($"FAIL {name}: {ex.Message}");
                Debug.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
        private readonly record struct TestMachine(
            Z80 Cpu,
            SpectrumMemory Memory,
            VerificationEdgeSource EdgeSource,
            SemanticTapeEdgeAccelerator Accelerator);
        private readonly record struct TimingBatchMachine(
            Z80 Cpu,
            SpectrumMemory Memory,
            SpectrumUlaRenderer Renderer,
            SpectrumEmulator Emulator);
        private sealed class VerificationEdgeSource : ITapeEdgeSource
        {
            private bool _hasLastEdge;
            private bool _lastLong;
            private bool _lastAccelerated;
            private bool _nextAccelerated;

            public bool IsPlaying => true;
            public bool EdgeSeen => false;
            public int CurrentBlockIndex => 0;
            public int CurrentPulseIndex { get; private set; }
            public bool EarHigh { get; private set; }
            public int AcceleratedAdvances { get; private set; }
            public int SemanticMarks { get; private set; }
            public int SemanticStateReads { get; private set; }
            public int SemanticAdvanceAttempts { get; private set; }
            public bool CurrentPulseIsData { get; set; } = true;
            public bool CurrentPulseIsLong { get; set; }
            public bool NextPulseIsData { get; set; } = true;
            public bool NextPulseIsLong { get; set; }
            public int PeekNextEdgeDelta() => 855;
            public int AdvanceToNextEdge(bool skipTime)
            {
                CurrentPulseIndex++;
                EarHigh = !EarHigh;
                _hasLastEdge = true;
                _lastAccelerated = _nextAccelerated;
                _nextAccelerated = false;
                CurrentPulseIsData = NextPulseIsData;
                CurrentPulseIsLong = NextPulseIsLong;
                AcceleratedAdvances++;
                return 855;
            }
            public void ObserveNormalDataEdge(bool isLong)
            {
                CurrentPulseIndex++;
                EarHigh = !EarHigh;
                _hasLastEdge = true;
                _lastLong = isLong;
                _lastAccelerated = false;
                CurrentPulseIsData = true;
                CurrentPulseIsLong = isLong;
            }
            public void ClearEdgeSeen() { }
            public bool TryGetDataPulseTimings(out int shortPulse, out int longPulse)
            {
                shortPulse = 855;
                longPulse = 1710;
                return true;
            }
            public bool TryGetCurrentPulseInfo(out int tstates, out bool isData, out bool isLong)
            {
                tstates = CurrentPulseIsLong ? 1710 : 855;
                isData = CurrentPulseIsData;
                isLong = CurrentPulseIsLong;
                return true;
            }
            public bool TryGetCurrentAccelerationFlags(out TapeAccelerationPulseFlags flags)
            {
                flags = !CurrentPulseIsData
                    ? TapeAccelerationPulseFlags.None
                    : CurrentPulseIsLong
                        ? TapeAccelerationPulseFlags.LengthLong
                        : TapeAccelerationPulseFlags.LengthShort;
                return true;
            }
            public bool TryGetSemanticReadState(out TapeSemanticReadState state)
            {
                SemanticStateReads++;
                TryGetCurrentAccelerationFlags(out TapeAccelerationPulseFlags flags);
                state = new TapeSemanticReadState(CurrentPulseIndex, flags, EarHigh, PeekNextEdgeDelta());
                return true;
            }
            public bool TryAdvanceSemanticEdge(TapeSemanticReadState expectedState, out TapeSemanticEdgeResult result)
            {
                SemanticAdvanceAttempts++;
                result = default;
                if (expectedState.PulseIndex != CurrentPulseIndex || !CurrentPulseIsData)
                {
                    return false;
                }

                int sourcePulse = CurrentPulseIndex;
                bool earBefore = EarHigh;
                TryGetCurrentAccelerationFlags(out TapeAccelerationPulseFlags sourceFlags);
                _lastLong = CurrentPulseIsLong;
                MarkNextEdgeSemanticallyAccelerated();
                int elapsed = AdvanceToNextEdge(skipTime: true);
                TryGetCurrentAccelerationFlags(out TapeAccelerationPulseFlags destinationFlags);
                result = new TapeSemanticEdgeResult(
                    elapsed,
                    sourcePulse,
                    CurrentPulseIndex,
                    sourceFlags,
                    destinationFlags,
                    earBefore,
                    EarHigh,
                    IsPlaying);
                return elapsed > 0;
            }
            public bool TryGetPreviousPulseInfo(out int tstates, out bool isData)
            {
                tstates = _lastLong ? 1710 : 855;
                isData = _hasLastEdge;
                return _hasLastEdge;
            }
            public bool TryGetLastEdgeInfo(out int tstates, out bool isData, out bool isLong, out bool fromSemanticAcceleration)
            {
                tstates = _lastLong ? 1710 : 855;
                isData = _hasLastEdge;
                isLong = _lastLong;
                fromSemanticAcceleration = _lastAccelerated;
                return _hasLastEdge;
            }
            public void MarkNextEdgeSemanticallyAccelerated()
            {
                _nextAccelerated = true;
                SemanticMarks++;
            }
        }
    }
}
