using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using ZedExEss.FileHandlers;
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
    /// <summary>Media, model and comparison settings for a complete accelerated tape load.</summary>
    public sealed class TapeGameVerificationOptions
    {
        public string? TzxPath { get; init; }
        public string? OutputPath { get; init; }
        public SpectrumModel Model { get; init; } = SpectrumModel.Spectrum48K;
        public int MaxFrames { get; init; } = 90_000;
        public int TailFrames { get; init; } = 250;
        public bool UseFastTapeCpuBatching { get; init; }

        /// <summary>
        /// Each pass dumps RAM to a .bin beside the log as soon as playback reaches
        /// each of these pulse indexes, so passes can be binary-diffed at identical
        /// tape positions to locate the first corrupted byte.
        /// </summary>
        public IReadOnlyList<int> DumpRamAtPulses { get; init; } = [];

        /// <summary>Suppress semantic-accelerator trace lines before this pulse index.</summary>
        public int TraceFromPulse { get; init; }
    }

    /// <summary>
    /// Runs a real machine (real ROM, real tape pulses, no flashload trap) through a
    /// complete tape load in two accelerator configurations and reports whether the
    /// semantic-acceleration run loads the same data the polling-only run does.
    /// </summary>
    public static class TapeGameVerificationRunner
    {
        private const ushort AutoLoad48KReadyPc = 0x10B0;
        private const ushort AutoLoad128ReadyPc = 0x3683;
        private const int AutoLoadInitialDelayFrames = 4;
        private const int AutoLoadKeySpacingFrames = 5;
        private static readonly byte[] AutoLoadBasic48Command = [0xEF, 0x22, 0x22, 0x0D];
        private static readonly byte[] AutoLoadCode48Command = [0xEF, 0x22, 0x22, 0xAF, 0x0D];
        private static readonly byte[] AutoLoadEnterCommand = [0x0D];
        public static int Run(TapeGameVerificationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (string.IsNullOrWhiteSpace(options.TzxPath))
            {
                throw new ArgumentException("--verify-tape-game requires a tape path.");
            }

            string tzxPath = Path.GetFullPath(options.TzxPath);
            if (!File.Exists(tzxPath))
            {
                throw new FileNotFoundException($"Tape file not found: {tzxPath}");
            }

            string outputPath = ResolveOutputPath(options.OutputPath, tzxPath);
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
            writer.WriteLine($"Tape game verification: {tzxPath}");
            writer.WriteLine($"Model={options.Model} maxFrames={options.MaxFrames} tailFrames={options.TailFrames}");
            writer.WriteLine($"Fast tape CPU batching={options.UseFastTapeCpuBatching}");
            writer.WriteLine("Runs use the real ROM, real tape pulses and no flashload trap.");
            writer.WriteLine();

            try
            {
                PassResult baseline = RunPass(writer, "no acceleration", tzxPath, options, edgeLoading: false, semanticAcceleration: false);
                writer.WriteLine();
                PassResult polling = RunPass(writer, "polling only", tzxPath, options, edgeLoading: true, semanticAcceleration: false);
                writer.WriteLine();
                PassResult semantic = RunPass(writer, "semantic + polling", tzxPath, options, edgeLoading: true, semanticAcceleration: true);
                writer.WriteLine();

                int exitCode = WriteVerdict(writer, baseline, polling, semantic);
                writer.WriteLine($"Output: {outputPath}");
                return exitCode;
            }
            catch (Exception ex)
            {
                writer.WriteLine($"ERROR {ex}");
                writer.WriteLine("Result: FAIL (verification run crashed)");
                return 2;
            }
        }
        private static PassResult RunPass(
            StreamWriter writer,
            string label,
            string tzxPath,
            TapeGameVerificationOptions options,
            bool edgeLoading,
            bool semanticAcceleration)
        {
            writer.WriteLine($"=== {label} ===");

            SpectrumModel model = options.Model;
            RomSet roms = LoadRoms(model);
            SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
            {
                Model = model,
                Roms = roms
            });
            SpectrumMemory memory = machine.Memory;
            SpectrumEarInputDevice earInput = machine.EarInput;
            Z80 cpu = machine.Cpu;
            SpectrumEmulator emulator = machine.Emulator;
            var session = new SpectrumSessionController();
            session.ReplaceMachine(machine, preserveTape: false);
            TzxLoader loader = session.LoadTape(tzxPath);
            earInput.EdgeLoadingEnabled = edgeLoading;
            earInput.SemanticAccelerationEnabled = semanticAcceleration;
            earInput.AutoPlayEnabled = true;
            if (semanticAcceleration && earInput.SemanticAccelerator != null)
            {
                earInput.SemanticAccelerator.TraceSink = line => writer.WriteLine($"TRACE {line}");
                earInput.SemanticAccelerator.TraceFromPulse = options.TraceFromPulse;
            }

            emulator.FastTapeCpuBatchingEnabled = options.UseFastTapeCpuBatching;

            var result = new PassResult { Label = label };
            bool endReached = false;
            earInput.AutoPlayRequested += () =>
            {
                if (!endReached && !loader.IsPlaying)
                {
                    loader.Play();
                }
            };

            session.TapePlaybackStopped += (_, reason) =>
            {
                if (reason == TapeStopReason.EndOfTape)
                {
                    endReached = true;
                }
            };

            loader.BlockIndexChanged += (_, blockIndex) =>
            {
                result.Snapshots.Add(CaptureSnapshot($"block={blockIndex}", blockIndex, cpu, memory, loader));
            };

            int tstatesPerFrame = SpectrumTimingModel.ForModel(model).TstatesPerFrame;
            var injector = new AutoLoadInjector(
                cpu,
                memory,
                GetAutoLoadReadyPc(model),
                expectedRomBank: SpectrumModelTraits.SupportsPaging(model) ? 0 : null,
                BuildAutoLoadCommand(model, loader),
                AutoLoadInitialDelayFrames * tstatesPerFrame,
                AutoLoadKeySpacingFrames * tstatesPerFrame);
            bool tapeStarted = false;
            int nextDump = 0;
            List<int> dumpPulses = [.. options.DumpRamAtPulses];
            dumpPulses.Sort();
            emulator.ConfigureBeforeCpuStep(() =>
            {
                injector.Tick();
                if (!tapeStarted && injector.IsComplete)
                {
                    tapeStarted = true;
                    loader.Play();
                }

                while (nextDump < dumpPulses.Count && loader.CurrentPulseIndex >= dumpPulses[nextDump])
                {
                    DumpRam(writer, memory, tzxPath, label, dumpPulses[nextDump]);
                    nextDump++;
                }

                return false;
            });

            var stopwatch = Stopwatch.StartNew();
            int frames = 0;
            int tailRemaining = Math.Max(0, options.TailFrames);
            while (frames < options.MaxFrames)
            {
                emulator.RunFrame(presentFrame: false);
                frames++;

                if (endReached)
                {
                    if (tailRemaining <= 0)
                    {
                        break;
                    }

                    tailRemaining--;
                }
            }

            stopwatch.Stop();
            emulator.FastTapeCpuBatchingEnabled = false;
            emulator.RunFrame(presentFrame: true);
            SaveScreenshot(writer, machine.Renderer, tzxPath, label);
            result.Snapshots.Add(CaptureSnapshot("final", loader.CurrentBlockIndex, cpu, memory, loader));
            result.EndReached = endReached;
            result.Frames = frames;
            result.FinalPc = cpu.PC;
            result.FinalBlockIndex = loader.CurrentBlockIndex;
            result.WallSeconds = stopwatch.Elapsed.TotalSeconds;
            result.UnloadedDataBlocks = FindUnloadedDataBlocks(result.Snapshots, loader);

            foreach (StateSnapshot snapshot in result.Snapshots)
            {
                writer.WriteLine(snapshot.Describe());
            }

            // ERR_NR: 0xFF = "0 OK", 0x1A = report R "Tape loading error".
            writer.WriteLine(
                $"frames={result.Frames} wall={result.WallSeconds:F3}s end={result.EndReached} " +
                $"playing={loader.IsPlaying} block={result.FinalBlockIndex} pulse={loader.CurrentPulseIndex} " +
                $"finalPc={result.FinalPc:X4} errNr={memory.ReadDirect(0x5C3A):X2}");
            writer.WriteLine(earInput.GetAccelerationStatus());
            if (result.UnloadedDataBlocks.Count > 0)
            {
                writer.WriteLine(
                    "STALL data blocks played without any RAM change: " +
                    string.Join(", ", result.UnloadedDataBlocks));
            }

            return result;
        }
        private static int WriteVerdict(StreamWriter writer, PassResult baseline, PassResult polling, PassResult semantic)
        {
            var regressions = new List<string>();
            CompareAgainst(baseline, polling, "polling", regressions);
            CompareAgainst(baseline, semantic, "semantic", regressions);
            CompareAgainst(polling, semantic, "semantic-vs-polling", regressions);

            writer.WriteLine("=== verdict ===");
            writer.WriteLine($"baseline: end={baseline.EndReached} block={baseline.FinalBlockIndex} finalPc={baseline.FinalPc:X4} stalls={baseline.UnloadedDataBlocks.Count}");
            writer.WriteLine($"polling : end={polling.EndReached} block={polling.FinalBlockIndex} finalPc={polling.FinalPc:X4} stalls={polling.UnloadedDataBlocks.Count}");
            writer.WriteLine($"semantic: end={semantic.EndReached} block={semantic.FinalBlockIndex} finalPc={semantic.FinalPc:X4} stalls={semantic.UnloadedDataBlocks.Count}");

            if (regressions.Count == 0)
            {
                writer.WriteLine("Result: PASS (all accelerated runs matched the unaccelerated outcome)");
                return 0;
            }

            foreach (string regression in regressions)
            {
                writer.WriteLine($"REGRESSION {regression}");
            }

            writer.WriteLine("Result: FAIL");
            return 1;
        }
        private static void CompareAgainst(PassResult reference, PassResult candidate, string label, List<string> regressions)
        {
            if (reference.EndReached && !candidate.EndReached)
            {
                regressions.Add($"{label}: did not reach the end of the tape (reference did)");
            }

            if (reference.UnloadedDataBlocks.Count == 0 && candidate.UnloadedDataBlocks.Count > 0)
            {
                regressions.Add(
                    $"{label}: stopped consuming data blocks: " +
                    string.Join(", ", candidate.UnloadedDataBlocks));
            }

            if (reference.FinalPc >= 0x4000 && candidate.FinalPc < 0x4000)
            {
                regressions.Add(
                    $"{label}: ended in ROM at {candidate.FinalPc:X4} (probable loading error) " +
                    $"while the reference ended at {reference.FinalPc:X4}");
            }
        }
        private static void DumpRam(StreamWriter writer, SpectrumMemory memory, string tzxPath, string passLabel, int pulseIndex)
        {
            string name = Path.GetFileNameWithoutExtension(tzxPath);
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            string suffix = passLabel.Replace(' ', '-').Replace('+', 'p');
            string path = Path.Combine(AppContext.BaseDirectory, $"tape-game-{name}-{suffix}-p{pulseIndex}.ram.bin");
            byte[] ram = new byte[0x10000 - 0x4000];
            for (int address = 0x4000; address <= 0xFFFF; address++)
            {
                ram[address - 0x4000] = memory.ReadDirect((ushort)address);
            }

            File.WriteAllBytes(path, ram);
            writer.WriteLine($"ramdump pulse={pulseIndex} path={path}");
        }
        private static void SaveScreenshot(StreamWriter writer, SpectrumUlaRenderer renderer, string tzxPath, string passLabel)
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(tzxPath);
                foreach (char invalid in Path.GetInvalidFileNameChars())
                {
                    name = name.Replace(invalid, '_');
                }

                string suffix = passLabel.Replace(' ', '-').Replace('+', 'p');
                string path = Path.Combine(AppContext.BaseDirectory, $"tape-game-{name}-{suffix}.png");
                int width = renderer.FrameWidth;
                int height = renderer.FrameHeight;
                PortablePngWriter.WriteArgb32(path, renderer.FrameBuffer, width, height);
                writer.WriteLine($"screenshot={path}");
            }
            catch (Exception ex)
            {
                writer.WriteLine($"screenshot failed: {ex.Message}");
            }
        }
        private static StateSnapshot CaptureSnapshot(string tag, int blockIndex, Z80 cpu, SpectrumMemory memory, TzxLoader loader)
        {
            return new StateSnapshot(
                tag,
                blockIndex,
                cpu.Cyc,
                cpu.PC,
                cpu.SP,
                (ushort)((cpu.A << 8) | cpu.GetFlags()),
                (ushort)((cpu.B << 8) | cpu.C),
                (ushort)((cpu.D << 8) | cpu.E),
                (ushort)((cpu.H << 8) | cpu.L),
                cpu.IX,
                cpu.IY,
                ComputeRamFnv(memory),
                loader.CurrentPulseIndex);
        }

        /// <summary>
        /// A data-bearing block that plays start-to-end without a single RAM change means
        /// the CPU stopped consuming the tape — the frozen-RAM signature of a wedged loader.
        /// </summary>
        private static List<int> FindUnloadedDataBlocks(List<StateSnapshot> snapshots, TzxLoader loader)
        {
            var unloaded = new List<int>();
            for (int i = 1; i < snapshots.Count; i++)
            {
                StateSnapshot previous = snapshots[i - 1];
                StateSnapshot current = snapshots[i];
                if (current.RamFnv != previous.RamFnv)
                {
                    continue;
                }

                int playedBlock = previous.BlockIndex;
                if (playedBlock >= 0
                    && playedBlock < loader.Blocks.Count
                    && GetBlockDataLength(loader.Blocks[playedBlock]) > 0)
                {
                    unloaded.Add(playedBlock);
                }
            }

            return unloaded;
        }
        private static int GetBlockDataLength(ITzxBlock block)
        {
            return block switch
            {
                StdData std => std.DataLength,
                TapBlock tap => tap.DataLength,
                Turbo turbo => turbo.DataLength,
                PureData pure => pure.DataLength,
                SpeedlockData speedlock => speedlock.DataLen,
                _ => 0
            };
        }
        private static uint ComputeRamFnv(SpectrumMemory memory)
        {
            uint hash = 2166136261u;
            for (int address = 0x4000; address <= 0xFFFF; address++)
            {
                hash ^= memory.ReadDirect((ushort)address);
                hash *= 16777619u;
            }

            return hash;
        }
        private static ushort GetAutoLoadReadyPc(SpectrumModel model)
        {
            return model switch
            {
                SpectrumModel.Spectrum128K or SpectrumModel.SpectrumPlus2 => AutoLoad128ReadyPc,
                _ => AutoLoad48KReadyPc
            };
        }
        private static byte[] BuildAutoLoadCommand(SpectrumModel model, TzxLoader loader)
        {
            if (model == SpectrumModel.Spectrum128K || model == SpectrumModel.SpectrumPlus2)
            {
                // The 128 menu boots with "Tape Loader" selected; Enter starts it.
                return AutoLoadEnterCommand;
            }

            return FirstBlockIsCodeHeader(loader) ? AutoLoadCode48Command : AutoLoadBasic48Command;
        }
        private static bool FirstBlockIsCodeHeader(TzxLoader loader)
        {
            foreach (ITzxBlock block in loader.Blocks)
            {
                if (block is StdData std && std.DataLength > 0)
                {
                    return std.IsHeader && std.FileType == 3;
                }
            }

            return false;
        }
        private static RomSet LoadRoms(SpectrumModel model)
        {
            string[] relativePaths = model switch
            {
                SpectrumModel.Spectrum16K or SpectrumModel.Spectrum48K => ["48.rom"],
                SpectrumModel.Spectrum128K => ["128_0.rom", "128_1.rom"],
                SpectrumModel.SpectrumPlus2 => ["plus2_0.rom", "plus2_1.rom"],
                SpectrumModel.SpectrumPlus2A or SpectrumModel.SpectrumPlus3 =>
                    ["plus3-0.rom", "plus3-1.rom", "plus3-2.rom", "plus3-3.rom"],
                _ => throw new NotSupportedException($"Tape game verification does not support {model}.")
            };

            string romRoot = FindRomRoot()
                ?? throw new FileNotFoundException("Could not locate a ROMs directory next to the executable or repository root.");
            string[] paths = new string[relativePaths.Length];
            for (int i = 0; i < relativePaths.Length; i++)
            {
                paths[i] = Path.Combine(romRoot, relativePaths[i]);
            }

            return RomSet.LoadFromFiles(paths);
        }
        private static string? FindRomRoot()
        {
            string baseDirectory = Path.Combine(AppContext.BaseDirectory, "ROMs");
            if (Directory.Exists(baseDirectory))
            {
                return baseDirectory;
            }

            string cwd = Path.Combine(Directory.GetCurrentDirectory(), "ROMs");
            if (Directory.Exists(cwd))
            {
                return cwd;
            }

            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "ZedExEss", "ROMs");
                if (File.Exists(Path.Combine(directory.FullName, "ZedExEss", "ZedExEss.csproj")) && Directory.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            return null;
        }
        private static string ResolveOutputPath(string? requestedPath, string tzxPath)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                return Path.GetFullPath(requestedPath);
            }

            string name = Path.GetFileNameWithoutExtension(tzxPath);
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            return Path.Combine(AppContext.BaseDirectory, $"tape-game-{name}.log");
        }
        private sealed record StateSnapshot(
            string Tag,
            int BlockIndex,
            ulong Cyc,
            ushort Pc,
            ushort Sp,
            ushort Af,
            ushort Bc,
            ushort De,
            ushort Hl,
            ushort Ix,
            ushort Iy,
            uint RamFnv,
            int PulseIndex)
        {
            public string Describe()
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"STATE {Tag} cyc={Cyc} pc={Pc:X4} sp={Sp:X4} af={Af:X4} bc={Bc:X4} de={De:X4} hl={Hl:X4} ix={Ix:X4} iy={Iy:X4} ramfnv={RamFnv:X8} pulse={PulseIndex}");
            }
        }
        private sealed class PassResult
        {
            public required string Label { get; init; }
            public List<StateSnapshot> Snapshots { get; } = [];
            public bool EndReached { get; set; }
            public int Frames { get; set; }
            public ushort FinalPc { get; set; }
            public int FinalBlockIndex { get; set; }
            public double WallSeconds { get; set; }
            public List<int> UnloadedDataBlocks { get; set; } = [];
        }

        /// <summary>
        /// Types the auto-load command through LAST-K exactly like the interactive shell's
        /// injector: wait for the editor's key-wait PC, then feed one key per spacing interval.
        /// </summary>
        private sealed class AutoLoadInjector(
            Z80 cpu,
            SpectrumMemory memory,
            ushort readyPc,
            int? expectedRomBank,
            byte[] command,
            int initialDelayTstates,
            int keySpacingTstates)
        {
            private const ushort LastKAddress = 0x5C08;
            private const ushort FlagsAddress = 0x5C3B;
            private const byte KeyAvailableMask = 0x20;
            private readonly byte[] _command = command;
            private readonly ulong _minimumWriteCycle = cpu.Cyc + (ulong)Math.Max(initialDelayTstates, 0);
            private readonly ulong _keySpacingTstates = (ulong)Math.Max(keySpacingTstates, 1);
            private int _offset;
            private ulong _nextWriteCycle;
            private bool _readySeen;

            public bool IsComplete { get; private set; }
            public void Tick()
            {
                if (IsComplete)
                {
                    return;
                }

                if (!_readySeen)
                {
                    if (cpu.PC != readyPc)
                    {
                        return;
                    }

                    if (expectedRomBank.HasValue && memory.CurrentRomBank != expectedRomBank.Value)
                    {
                        return;
                    }

                    _readySeen = true;
                    _nextWriteCycle = Math.Max(cpu.Cyc, _minimumWriteCycle);
                    return;
                }

                if (cpu.Cyc < _nextWriteCycle)
                {
                    return;
                }

                byte flags = memory.ReadDirect(FlagsAddress);
                if ((flags & KeyAvailableMask) != 0)
                {
                    return;
                }

                memory.WriteDirect(LastKAddress, _command[_offset++]);
                memory.WriteDirect(FlagsAddress, (byte)(flags | KeyAvailableMask));
                _nextWriteCycle = cpu.Cyc + _keySpacingTstates;
                IsComplete = _offset >= _command.Length;
            }
        }
    }
}
