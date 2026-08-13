using System;
using System.Collections.Generic;
using System.Globalization;
using ZedExEss.Spectrum.Core;

namespace ZedExEss.Diagnostics
{
    /// <summary>
    /// Parses and runs emulator diagnostics without depending on a desktop host.
    /// </summary>
    /// <remarks>
    /// Both the WPF application and the portable console host call this entry
    /// point, ensuring their switches execute identical core code and defaults.
    /// Unknown switches are ignored here so the WPF host can continue processing
    /// its own future arguments; the console host reports when no diagnostic was
    /// selected.
    /// </remarks>
    public static class DiagnosticCommandLine
    {
        public const string HelpText =
            "ZedExEss headless diagnostics\n" +
            "\n" +
            "Tests:\n" +
            "  --verify-basic\n" +
            "  --verify-debugger\n" +
            "  --verify-interface1 [--interface1-rom <path>]\n" +
            "  --verify-pentagon\n" +
            "  --verify-session\n" +
            "  --verify-settings\n" +
            "  --verify-tape-acceleration\n" +
            "  --verify-tape-game <path>\n" +
            "  --raxoft-z80full | --raxoft-z80memptr\n" +
            "  --raxoft-z80flags | --raxoft-z80ccf\n" +
            "  --benchmark\n" +
            "\n" +
            "Common output options:\n" +
            "  --verify-output <path>\n" +
            "  --raxoft-program <path> --raxoft-output <path>\n" +
            "  --benchmark-output <path>\n";

        public static bool TryRun(string[] args, out int exitCode)
        {
            ArgumentNullException.ThrowIfNull(args);

            exitCode = 0;
            if (args.Length == 0)
            {
                return false;
            }

            var options = new Options();
            Parse(args, options);

            if (options.RunRaxoft)
            {
                exitCode = RaxoftZ80FullRunner.Run(new RaxoftZ80FullOptions
                {
                    ProgramPath = options.ProgramPath,
                    OutputPath = options.OutputPath,
                    MaxInstructions = options.MaxInstructions
                });
                return true;
            }

            if (options.RunRaxoftMemptr)
            {
                exitCode = RaxoftZ80MemptrRunner.Run(new RaxoftZ80MemptrOptions
                {
                    ProgramPath = options.ProgramPath,
                    OutputPath = options.OutputPath,
                    MaxInstructions = options.MaxInstructions
                });
                return true;
            }

            if (options.RunRaxoftFlags)
            {
                exitCode = RaxoftZ80FlagsRunner.Run(new RaxoftZ80FlagsOptions
                {
                    ProgramPath = options.ProgramPath,
                    OutputPath = options.OutputPath,
                    MaxInstructions = options.MaxInstructions
                });
                return true;
            }

            if (options.RunRaxoftCcf)
            {
                exitCode = RaxoftZ80CcfRunner.Run(new RaxoftZ80CcfOptions
                {
                    ProgramPath = options.ProgramPath,
                    OutputPath = options.OutputPath,
                    MaxInstructions = options.MaxInstructions
                });
                return true;
            }

            if (options.RunPentagonVerification)
            {
                exitCode = PentagonVerificationRunner.Run(new PentagonVerificationOptions
                {
                    OutputPath = options.OutputPath
                });
                return true;
            }

            if (options.RunBasicVerification)
            {
                exitCode = BasicProgramVerificationRunner.Run(new BasicProgramVerificationOptions
                {
                    OutputPath = options.OutputPath
                });
                return true;
            }

            if (options.RunDebuggerVerification)
            {
                exitCode = DebuggerVerificationRunner.Run(new DebuggerVerificationOptions
                {
                    OutputPath = options.OutputPath
                });
                return true;
            }

            if (options.RunInterface1Verification)
            {
                exitCode = Interface1VerificationRunner.Run(new Interface1VerificationOptions
                {
                    OutputPath = options.OutputPath,
                    RomPath = options.Interface1RomPath
                });
                return true;
            }

            if (options.RunTapeAccelerationVerification)
            {
                exitCode = TapeAccelerationVerificationRunner.Run(new TapeAccelerationVerificationOptions
                {
                    OutputPath = options.OutputPath
                });
                return true;
            }

            if (options.RunSessionVerification)
            {
                exitCode = SessionVerificationRunner.Run(new SessionVerificationOptions
                {
                    OutputPath = options.OutputPath
                });
                return true;
            }

            if (options.RunSettingsVerification)
            {
                exitCode = SettingsVerificationRunner.Run(new SettingsVerificationOptions
                {
                    OutputPath = options.OutputPath
                });
                return true;
            }

            if (options.TapeGamePath != null)
            {
                exitCode = TapeGameVerificationRunner.Run(new TapeGameVerificationOptions
                {
                    TzxPath = options.TapeGamePath,
                    OutputPath = options.OutputPath,
                    Model = options.TapeGameModel,
                    MaxFrames = options.TapeGameMaxFrames,
                    UseFastTapeCpuBatching = options.TapeGameFastBatching,
                    DumpRamAtPulses = options.TapeGameDumpPulses,
                    TraceFromPulse = options.TapeGameTracePulse
                });
                return true;
            }

            if (options.RunBenchmark)
            {
                exitCode = SpectrumBenchmarkRunner.Run(new SpectrumBenchmarkOptions
                {
                    Model = options.BenchmarkModel,
                    Frames = options.BenchmarkFrames,
                    PresentEveryNFrames = options.BenchmarkPresentEvery,
                    UseFastTapeCpuBatching = options.BenchmarkFastTapeBatching,
                    OutputPath = options.BenchmarkOutputPath
                });
                return true;
            }

            return false;
        }

        private static void Parse(string[] args, Options options)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg.ToLowerInvariant())
                {
                    case "--raxoft-z80full":
                        options.RunRaxoft = true;
                        break;
                    case "--raxoft-z80memptr":
                        options.RunRaxoftMemptr = true;
                        break;
                    case "--raxoft-z80flags":
                        options.RunRaxoftFlags = true;
                        break;
                    case "--raxoft-z80ccf":
                        options.RunRaxoftCcf = true;
                        break;
                    case "--verify-pentagon":
                        options.RunPentagonVerification = true;
                        break;
                    case "--verify-basic":
                        options.RunBasicVerification = true;
                        break;
                    case "--verify-debugger":
                        options.RunDebuggerVerification = true;
                        break;
                    case "--verify-interface1":
                        options.RunInterface1Verification = true;
                        break;
                    case "--interface1-rom":
                        options.Interface1RomPath = RequireValue(args, ref i, arg);
                        break;
                    case "--verify-tape-acceleration":
                        options.RunTapeAccelerationVerification = true;
                        break;
                    case "--verify-session":
                        options.RunSessionVerification = true;
                        break;
                    case "--verify-settings":
                        options.RunSettingsVerification = true;
                        break;
                    case "--verify-tape-game":
                        options.TapeGamePath = RequireValue(args, ref i, arg);
                        break;
                    case "--tape-game-model":
                        string tapeModel = RequireValue(args, ref i, arg);
                        if (!SpectrumBenchmarkRunner.TryParseModel(tapeModel, out SpectrumModel parsedTapeModel))
                        {
                            throw new ArgumentException($"Invalid {arg} value '{tapeModel}'.");
                        }

                        options.TapeGameModel = parsedTapeModel;
                        break;
                    case "--tape-game-max-frames":
                        options.TapeGameMaxFrames = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--tape-game-trace-pulse":
                        options.TapeGameTracePulse = ParseNonNegativeInt(RequireValue(args, ref i, arg), arg);
                        break;
                    case "--tape-game-dump-pulse":
                        string pulseList = RequireValue(args, ref i, arg);
                        foreach (string part in pulseList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            options.TapeGameDumpPulses.Add(ParsePositiveInt(part, arg));
                        }
                        break;
                    case "--tape-game-fast-batching":
                        options.TapeGameFastBatching = true;
                        break;
                    case "--raxoft-program":
                        options.ProgramPath = RequireValue(args, ref i, arg);
                        break;
                    case "--verify-output":
                    case "--raxoft-output":
                        options.OutputPath = RequireValue(args, ref i, arg);
                        break;
                    case "--raxoft-max-instructions":
                        string instructionCount = RequireValue(args, ref i, arg);
                        if (!long.TryParse(instructionCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedInstructions)
                            || parsedInstructions <= 0)
                        {
                            throw new ArgumentException($"Invalid {arg} value '{instructionCount}'.");
                        }

                        options.MaxInstructions = parsedInstructions;
                        break;
                    case "--benchmark":
                        options.RunBenchmark = true;
                        break;
                    case "--benchmark-model":
                        string benchmarkModel = RequireValue(args, ref i, arg);
                        if (!SpectrumBenchmarkRunner.TryParseModel(benchmarkModel, out SpectrumModel parsedBenchmarkModel))
                        {
                            throw new ArgumentException($"Invalid {arg} value '{benchmarkModel}'.");
                        }

                        options.BenchmarkModel = parsedBenchmarkModel;
                        options.RunBenchmark = true;
                        break;
                    case "--benchmark-frames":
                        options.BenchmarkFrames = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                        options.RunBenchmark = true;
                        break;
                    case "--benchmark-present-every":
                        options.BenchmarkPresentEvery = ParseNonNegativeInt(RequireValue(args, ref i, arg), arg);
                        options.RunBenchmark = true;
                        break;
                    case "--benchmark-fast-tape-batching":
                        options.BenchmarkFastTapeBatching = true;
                        options.RunBenchmark = true;
                        break;
                    case "--benchmark-output":
                        options.BenchmarkOutputPath = RequireValue(args, ref i, arg);
                        options.RunBenchmark = true;
                        break;
                }
            }
        }

        private static int ParsePositiveInt(string value, string option)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) || result <= 0)
            {
                throw new ArgumentException($"Invalid {option} value '{value}'.");
            }

            return result;
        }

        private static int ParseNonNegativeInt(string value, string option)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) || result < 0)
            {
                throw new ArgumentException($"Invalid {option} value '{value}'.");
            }

            return result;
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            int valueIndex = index + 1;
            if (valueIndex >= args.Length)
            {
                throw new ArgumentException($"{option} requires a value.");
            }

            index = valueIndex;
            return args[valueIndex];
        }

        private sealed class Options
        {
            public bool RunRaxoft { get; set; }
            public bool RunRaxoftMemptr { get; set; }
            public bool RunRaxoftFlags { get; set; }
            public bool RunRaxoftCcf { get; set; }
            public bool RunBenchmark { get; set; }
            public bool RunPentagonVerification { get; set; }
            public bool RunBasicVerification { get; set; }
            public bool RunDebuggerVerification { get; set; }
            public bool RunInterface1Verification { get; set; }
            public bool RunTapeAccelerationVerification { get; set; }
            public bool RunSessionVerification { get; set; }
            public bool RunSettingsVerification { get; set; }
            public string? TapeGamePath { get; set; }
            public string? Interface1RomPath { get; set; }
            public SpectrumModel TapeGameModel { get; set; } = SpectrumModel.Spectrum48K;
            public int TapeGameMaxFrames { get; set; } = 90_000;
            public bool TapeGameFastBatching { get; set; }
            public List<int> TapeGameDumpPulses { get; } = [];
            public int TapeGameTracePulse { get; set; }
            public string? ProgramPath { get; set; }
            public string? OutputPath { get; set; }
            public long MaxInstructions { get; set; } = 5_000_000_000;
            public SpectrumModel BenchmarkModel { get; set; } = SpectrumModel.Spectrum128K;
            public int BenchmarkFrames { get; set; } = 2000;
            public int BenchmarkPresentEvery { get; set; } = 5;
            public bool BenchmarkFastTapeBatching { get; set; }
            public string? BenchmarkOutputPath { get; set; }
        }
    }
}
