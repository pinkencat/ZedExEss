using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ZedExEss.FileHandlers;
using ZedExEss.Spectrum.Input;
using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Dma;
using ZedExEss.ZxNext.Firmware;
using ZedExEss.ZxNext.Memory;
using ZedExEss.ZxNext.Video;

namespace ZedExEss.Diagnostics;

/// <summary>Duration, case selection and output paths for the ZX Spectrum Next benchmark.</summary>
public sealed class ZxNextBenchmarkOptions
{
    public int WarmupFrames { get; init; } = 20;
    public int Frames { get; init; } = 120;
    public int Repetitions { get; init; } = 1;
    public string Scenario { get; init; } = "all";
    public string? OutputPath { get; init; }
    public string? SystemDirectory { get; init; }
    public string? BootRomPath { get; init; }

    /// <summary>
    /// Runs every measured case in its own child process. Scenarios executed one after
    /// another inside a single process inherit each other's JIT, GC and cache state,
    /// which was measured to shift results by up to 17% and can invert the sign of an
    /// A/B comparison, so isolation is the default.
    /// </summary>
    public bool IsolateScenarios { get; init; } = true;

    /// <summary>Idle pause before each measured case, letting clocks recover.</summary>
    public int SettleMilliseconds { get; init; } = 250;

    /// <summary>Set on the spawned child that measures exactly one case.</summary>
    public bool IsolatedChild { get; init; }
}

/// <summary>
/// Measures deterministic, asset-free ZX Spectrum Next workloads on the production machine graph.
/// </summary>
public static class ZxNextBenchmarkRunner
{
    private static readonly byte[] DmaCopy16KCommand =
    [
        0xC3, 0xC7, 0xCB,
        0x7D, 0x00, 0xC0, 0xFF, 0x3F,
        0x54, 0x01,
        0x50, 0x01,
        0xAD, 0x00, 0x00,
        0x82, 0xCF, 0xB3, 0x87
    ];

    private static readonly byte[] DmaFill16KCommand =
    [
        0xC3, 0xC7, 0xCB,
        0x7D, 0x23, 0xC1, 0xFF, 0x3F,
        0x64, 0x01,
        0x50, 0x01,
        0xAD, 0x00, 0x00,
        0x82, 0xCF, 0xB3, 0x87
    ];

    private const string CsvHeader =
        "timestamp_utc,commit,runtime,os,cpu,logical_cpus,scenario,asset_path,asset_sha256," +
        "run_index,warmup_frames,frames," +
        "elapsed_seconds,effective_fps,realtime_multiplier,cpu_tstates,cpu_mtstates_per_second," +
        "master_ticks,master_mticks_per_second,allocated_bytes,gen0,gen1,gen2,published_frames," +
        "rendered_frames,audio_samples,audio_queue_high_water,ula_grants,tilemap_grants,tilemap_stalls," +
        "tilemap_characters,tilemap_pixels,tilemap_deadline_misses,sprite_ticks,sprite_overtime," +
        "dma_grants,dma_master_ticks,frame_hash";

    public static int Run(ZxNextBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.WarmupFrames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Warm-up frame count cannot be negative.");
        }

        if (options.Frames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Measured frame count must be positive.");
        }

        if (options.Repetitions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Repetition count must be positive.");
        }

        if (options.IsolatedChild && options.Repetitions != 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "An isolated child measures exactly one repetition.");
        }

        IReadOnlyList<BenchmarkCase> cases = SelectCases(options);
        string outputPath = ResolveOutputPath(options.OutputPath);
        string csvPath = Path.ChangeExtension(outputPath, ".csv");
        string reportPath = string.Equals(
            Path.GetFullPath(outputPath),
            Path.GetFullPath(csvPath),
            StringComparison.OrdinalIgnoreCase)
                ? Path.ChangeExtension(outputPath, ".txt")
                : outputPath;
        EnsureOutputDirectory(reportPath);
        EnsureOutputDirectory(csvPath);

        try
        {
            using var report = new StreamWriter(reportPath, false, Encoding.UTF8) { AutoFlush = true };
            using var csv = new StreamWriter(csvPath, false, new UTF8Encoding(false)) { AutoFlush = true };
            csv.WriteLine(CsvHeader);

            MachineIdentity identity = GetMachineIdentity();
            bool isolate = options.IsolateScenarios && !options.IsolatedChild;
            WriteLine(report, "ZX Spectrum Next headless benchmark");
            WriteLine(report, $"UTC:             {identity.TimestampUtc}");
            WriteLine(report, $"Commit/build:    {identity.Commit}");
            WriteLine(report, $"Runtime:         {identity.Runtime}");
            WriteLine(report, $"OS:              {identity.Os}");
            WriteLine(report, $"CPU:             {identity.Cpu}");
            WriteLine(report, $"Logical CPUs:    {identity.LogicalCpuCount}");
            WriteLine(report, $"Warm-up frames:  {options.WarmupFrames}");
            WriteLine(report, $"Measured frames: {options.Frames}");
            WriteLine(report, $"Repetitions:     {options.Repetitions}");
            WriteLine(report, $"Scenario filter: {options.Scenario}");
            WriteLine(report, $"Isolation:       {(isolate ? "one child process per case" : "shared process (results not comparable across cases)")}");
            WriteLine(report, $"Settle delay:    {options.SettleMilliseconds} ms");
            WriteLine(report, $"Asset root:      {options.SystemDirectory ?? "not supplied"}");
            foreach (BenchmarkCase assetCase in cases.Where(static item => item.AssetPath is not null))
            {
                WriteLine(report,
                    $"Asset image:     {assetCase.Name} = {assetCase.AssetPath} "
                    + $"(SHA-256 {assetCase.AssetSha256})");
            }
            WriteLine(report, string.Empty);

            var results = new List<BenchmarkResult>(cases.Count * options.Repetitions);
            for (int repetition = 0; repetition < options.Repetitions; repetition++)
            {
                IEnumerable<BenchmarkCase> orderedCases = (repetition & 1) == 0
                    ? cases
                    : cases.Reverse();
                foreach (BenchmarkCase benchmarkCase in orderedCases)
                {
                    Settle(options.SettleMilliseconds);
                    BenchmarkResult result = isolate
                        ? RunCaseIsolated(benchmarkCase, options, repetition + 1)
                        : RunCase(benchmarkCase, options, identity, repetition + 1);
                    results.Add(result);
                    WriteResult(report, result, options.Repetitions);
                    csv.WriteLine(ToCsv(result));
                }
            }

            if (options.Repetitions > 1)
            {
                WriteSummaries(report, cases, results);
            }

            WriteLine(report, $"Text output:      {reportPath}");
            WriteLine(report, $"CSV output:       {csvPath}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Debug.WriteLine(ex.ToString());
            Console.Error.WriteLine(ex.ToString());
            return 3;
        }
    }

    private static void Settle(int milliseconds)
    {
        if (milliseconds > 0)
        {
            Thread.Sleep(milliseconds);
        }
    }

    /// <summary>
    /// Measures one case in a freshly started child process and returns its result.
    /// </summary>
    private static BenchmarkResult RunCaseIsolated(
        BenchmarkCase benchmarkCase,
        ZxNextBenchmarkOptions options,
        int runIndex)
    {
        string childOutput = Path.Combine(
            Path.GetTempPath(),
            $"zxnext-bench-{Guid.NewGuid():N}.txt");
        string childCsv = Path.ChangeExtension(childOutput, ".csv");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ResolveHostExecutable(out string? entryAssembly),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (entryAssembly is not null)
            {
                startInfo.ArgumentList.Add(entryAssembly);
            }

            startInfo.ArgumentList.Add("--benchmark-zxnext");
            startInfo.ArgumentList.Add("--benchmark-zxnext-isolated-child");
            startInfo.ArgumentList.Add("--benchmark-zxnext-scenario");
            startInfo.ArgumentList.Add(benchmarkCase.Name);
            startInfo.ArgumentList.Add("--benchmark-zxnext-warmup");
            startInfo.ArgumentList.Add(options.WarmupFrames.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--benchmark-zxnext-frames");
            startInfo.ArgumentList.Add(options.Frames.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--benchmark-zxnext-repetitions");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("--benchmark-zxnext-output");
            startInfo.ArgumentList.Add(childOutput);
            if (!string.IsNullOrWhiteSpace(options.SystemDirectory))
            {
                startInfo.ArgumentList.Add("--zxnext-system-directory");
                startInfo.ArgumentList.Add(options.SystemDirectory);
            }

            if (!string.IsNullOrWhiteSpace(options.BootRomPath))
            {
                startInfo.ArgumentList.Add("--zxnext-boot-rom");
                startInfo.ArgumentList.Add(options.BootRomPath);
            }

            using Process child = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    $"Could not start an isolated benchmark process for '{benchmarkCase.Name}'.");
            string childError = child.StandardError.ReadToEnd();
            child.StandardOutput.ReadToEnd();
            child.WaitForExit();
            if (child.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The isolated run of '{benchmarkCase.Name}' exited with {child.ExitCode}."
                    + (string.IsNullOrWhiteSpace(childError) ? string.Empty : $" {childError.Trim()}"));
            }

            if (!File.Exists(childCsv))
            {
                throw new InvalidOperationException(
                    $"The isolated run of '{benchmarkCase.Name}' produced no CSV at '{childCsv}'.");
            }

            string[] lines = File.ReadAllLines(childCsv);
            if (lines.Length < 2)
            {
                throw new InvalidOperationException(
                    $"The isolated run of '{benchmarkCase.Name}' produced no result row.");
            }

            return ParseCsv(lines[1]) with { RunIndex = runIndex };
        }
        finally
        {
            TryDelete(childOutput);
            TryDelete(childCsv);
        }
    }

    private static string ResolveHostExecutable(out string? entryAssembly)
    {
        entryAssembly = null;
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            entryAssembly = Assembly.GetEntryAssembly()?.Location;
            return "dotnet";
        }

        // A framework-dependent launch runs under the shared host, so the managed entry
        // assembly has to be handed back to it explicitly.
        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            entryAssembly = Assembly.GetEntryAssembly()?.Location;
        }

        return processPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex.ToString());
        }
    }

    private static BenchmarkResult RunCase(
        BenchmarkCase benchmarkCase,
        ZxNextBenchmarkOptions options,
        MachineIdentity identity,
        int runIndex)
    {
        using BenchmarkMachineContext machineContext = benchmarkCase.CreateMachine?.Invoke()
            ?? new BenchmarkMachineContext(
                benchmarkCase.SystemAssets is null
                    ? ZxNextMachineFactory.CreateFoundationMachine()
                    : ZxNextMachineFactory.CreateFoundationMachine(benchmarkCase.SystemAssets));
        ZxNextMachine machine = machineContext.Machine;
        long publishedFrames = 0;
        machine.FrameCompleted += () => publishedFrames++;
        if (benchmarkCase.InitializeCpuLoop)
        {
            ConfigureCpuLoop(machine, contendedRead: false);
        }

        benchmarkCase.Configure(machine);
        machine.Video.RenderEnabled = benchmarkCase.RenderVideo;
        machine.Video.Reset();

        short[] audioDrain = new short[2048];
        if (benchmarkCase.CaptureAudio)
        {
            machine.Audio.BeginCapture();
        }

        RunFrames(machine, options.WarmupFrames, benchmarkCase, audioDrain, out _);
        if (benchmarkCase.CaptureAudio)
        {
            // Retain generator phase, but keep the queue metric scoped to the
            // measured interval rather than inheriting the warm-up peak.
            machine.Audio.EndCapture();
            machine.Audio.BeginCapture();
        }

        long publishedFramesBefore = publishedFrames;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        CounterSnapshot before = CounterSnapshot.Capture(machine);
        long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);

        Stopwatch stopwatch = Stopwatch.StartNew();
        RunFrames(machine, options.Frames, benchmarkCase, audioDrain, out long audioSamples);
        stopwatch.Stop();

        long allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocationBefore);
        CounterSnapshot after = CounterSnapshot.Capture(machine);
        CounterSnapshot delta = after - before;
        benchmarkCase.Validate?.Invoke(machine, options.Frames, delta);
        double elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.000001);
        double effectiveFps = options.Frames / elapsedSeconds;
        double realtimeFps = (double)ZxNextReferenceBaseline.MasterClockHz
            / machine.UlaTiming.MasterTicksPerFrame;

        return new BenchmarkResult(
            identity,
            benchmarkCase.Name,
            benchmarkCase.AssetPath ?? string.Empty,
            benchmarkCase.AssetSha256 ?? string.Empty,
            runIndex,
            options.WarmupFrames,
            options.Frames,
            elapsedSeconds,
            effectiveFps,
            effectiveFps / realtimeFps,
            delta.CpuTstates,
            delta.CpuTstates / elapsedSeconds / 1_000_000.0,
            delta.MasterTicks,
            delta.MasterTicks / elapsedSeconds / 1_000_000.0,
            allocatedBytes,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            publishedFrames - publishedFramesBefore,
            benchmarkCase.RenderVideo ? options.Frames : 0,
            audioSamples,
            machine.Audio.QueueHighWaterMark,
            delta.UlaGrants,
            delta.TilemapGrants,
            delta.TilemapStalls,
            delta.TilemapCharacters,
            delta.TilemapPixels,
            delta.TilemapDeadlineMisses,
            delta.SpriteTicks,
            delta.SpriteOvertime,
            delta.DmaGrants,
            delta.DmaMasterTicks,
            HashFrame(machine.Video.FrameBuffer));
    }

    private static void RunFrames(
        ZxNextMachine machine,
        int frames,
        BenchmarkCase benchmarkCase,
        short[] audioDrain,
        out long audioSamples)
    {
        audioSamples = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            benchmarkCase.BeforeFrame?.Invoke(machine);
            machine.RunFrame(presentFrame: benchmarkCase.RenderVideo);
            if (!benchmarkCase.CaptureAudio)
            {
                continue;
            }

            while (machine.Audio.PendingSampleCount > 0)
            {
                int requested = Math.Min(audioDrain.Length, machine.Audio.PendingSampleCount);
                audioSamples += machine.Audio.DrainSamples(audioDrain, 0, requested);
            }
        }
    }

    private static IReadOnlyList<BenchmarkCase> SelectCases(ZxNextBenchmarkOptions options)
    {
        List<BenchmarkCase> all = CreateCases(options);
        string[] requested = options.Scenario.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Length == 0
            || requested.Any(static item => item.Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            return all;
        }

        var selected = new List<BenchmarkCase>();
        foreach (string name in requested)
        {
            BenchmarkCase? match = all.FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                throw new ArgumentException(
                    $"Unknown ZXN benchmark scenario '{name}'. Available cases: "
                    + string.Join(", ", all.Select(static item => item.Name)));
            }

            if (!selected.Contains(match))
            {
                selected.Add(match);
            }
        }

        return selected;
    }

    private static List<BenchmarkCase> CreateCases(ZxNextBenchmarkOptions options)
    {
        var cases = new List<BenchmarkCase>
        {
        CpuCase("cpu-beam-3.5", ZxNextCpuSpeed.Mhz3_5),
        CpuCase("cpu-beam-7", ZxNextCpuSpeed.Mhz7),
        CpuCase("cpu-beam-14", ZxNextCpuSpeed.Mhz14),
        CpuCase("cpu-beam-28", ZxNextCpuSpeed.Mhz28),
        new("ula-uncontended-3.5", machine =>
        {
            ConfigureCpuLoop(machine, contendedRead: false);
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz3_5);
        }),
        new("ula-contended-3.5", machine =>
        {
            ConfigureCpuLoop(machine, contendedRead: true);
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz3_5);
        }),
        new("ula-only-28", machine =>
        {
            ConfigureCpuLoop(machine, contendedRead: false);
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
        }),
        new("ula-only-28-frame-copy-reference", machine =>
        {
            ConfigureCpuLoop(machine, contendedRead: false);
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            machine.Video.UseFrameBufferSwap = false;
        }),
        new("ula-only-28-event-reference", machine =>
        {
            ConfigureCpuLoop(machine, contendedRead: false);
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            machine.Video.UseUnobservedSubpixelCoalescing = false;
        }),
        new("ula-only-28-quiet-reference", machine =>
        {
            ConfigureCpuLoop(machine, contendedRead: false);
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            machine.Video.UseQuietRasterSpans = false;
        }),
        new("lores", machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            ConfigureLoresScreen(machine, radastan: false);
        }),
        new("lores-scrolled", machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            ConfigureLoresScreen(machine, radastan: false);
            machine.Registers.Write(0x32, 0x37); // LoRes scroll X.
            machine.Registers.Write(0x33, 0x51); // LoRes scroll Y.
        }),
        new("radastan", machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            ConfigureLoresScreen(machine, radastan: true);
        }),
        new("timex-hires", machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            ConfigureUlaScreen(machine);
            machine.PortBus.WriteUncontended(0x00FF, 0x06); // Timex 512x192 hi-res.
        }),
        new("ula-scrolled-28", machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            ConfigureUlaScreen(machine);
            machine.Registers.Write(0x26, 0x2B); // ULA scroll X.
            machine.Registers.Write(0x27, 0x19); // ULA scroll Y.
        }),
        Layer2Case("layer2-256", resolution: 0, ulaEnabled: false),
        Layer2Case("layer2-320", resolution: 1, ulaEnabled: false),
        new("layer2-320-general", machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            ConfigureLayer2(machine, resolution: 1, ulaEnabled: true);
            machine.Registers.Write(0x14, 0x00);
        }),
        Layer2Case("layer2-640", resolution: 2, ulaEnabled: false),
        Layer2Case("ula-layer2-320", resolution: 1, ulaEnabled: true),
        TilemapCase("tilemap-4bpp-80", textMode: false, renderVideo: true),
        TilemapCase("tilemap-text-80", textMode: true, renderVideo: true),
        TilemapCase(
            "tilemap-text-80-instruction-reference",
            textMode: true,
            renderVideo: true,
            useInstructionBatching: false),
        TilemapCase(
            "tilemap-text-80-raster-reference",
            textMode: true,
            renderVideo: true,
            useTilemapRasterPixelBatching: false),
        TilemapCase(
            "tilemap-text-80-pixel-span-reference",
            textMode: true,
            renderVideo: true,
            useTilemapMultiPixelSpans: false),
        new("tilemap-text-80-general", machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            ConfigureTilemap(machine, textMode: true, disableUla: false);
            machine.Registers.Write(0x14, 0x00);
        }),
        TilemapCase("tilemap-text-80-pipeline", textMode: true, renderVideo: false),
        SpriteCase("sprites-0", 0, overtime: false),
        SpriteCase("sprites-32", 32, overtime: false),
        SpriteCase("sprites-128-overtime", 128, overtime: true),
        new("sprites-128-overtime-general", machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            machine.Registers.Write(0x14, 0x00);
            ConfigureSprites(machine, ZxNextSpriteDevice.SpriteCount, overtime: true);
        }),
        DmaCase("dma-copy-16k", fixedSource: false),
        DmaCase("dma-fill-16k", fixedSource: true),
        new("sprites-128-overtime-dma", machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            machine.Registers.Write(0x68, 0x80);
            ConfigureSprites(machine, ZxNextSpriteDevice.SpriteCount, overtime: true);
            ConfigureDmaMemory(machine, displayLayer2: false);
        }, BeforeFrame: QueueDmaCopy16K, Validate: ValidateSpriteDma),
        AudioCase("audio-beeper", psgCount: 0),
        AudioCase("audio-1psg", psgCount: 1),
        AudioCase("audio-3psg", psgCount: 3),
        new("combined-stress", machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            ConfigureLayer2(machine, resolution: 1, ulaEnabled: true);
            ConfigureTilemap(machine, textMode: true, disableUla: false);
            ConfigureSprites(machine, spriteCount: 32, overtime: false);
            ConfigureAudio(machine, psgCount: 3);
            ConfigureDmaMemory(machine, displayLayer2: true);
        }, CaptureAudio: true, BeforeFrame: QueueDmaCopy16K, Validate: ValidateDmaCopy),
        new("combined-stress-raster-reference", machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            ConfigureLayer2(machine, resolution: 1, ulaEnabled: true);
            ConfigureTilemap(machine, textMode: true, disableUla: false);
            ConfigureSprites(machine, spriteCount: 32, overtime: false);
            ConfigureAudio(machine, psgCount: 3);
            ConfigureDmaMemory(machine, displayLayer2: true);
            machine.Video.UseTilemapRasterPixelBatching = false;
        }, CaptureAudio: true, BeforeFrame: QueueDmaCopy16K, Validate: ValidateDmaCopy)
        };

        if (!string.IsNullOrWhiteSpace(options.SystemDirectory))
        {
            ZxNextSystemAssets assets = ZxNextSystemAssets.LoadFromDirectory(
                options.SystemDirectory,
                options.BootRomPath);
            if (CreateRamTestCase(
                    assets,
                    useInstructionBatching: true,
                    useTilemapRasterPixelBatching: true,
                    useTilemapMultiPixelSpans: true) is { } ramTest)
            {
                cases.Add(ramTest);
                cases.Add(CreateRamTestCase(
                    assets,
                    useInstructionBatching: false,
                    useTilemapRasterPixelBatching: true,
                    useTilemapMultiPixelSpans: true)!);
                cases.Add(CreateRamTestCase(
                    assets,
                    useInstructionBatching: true,
                    useTilemapRasterPixelBatching: false,
                    useTilemapMultiPixelSpans: true)!);
                cases.Add(CreateRamTestCase(
                    assets,
                    useInstructionBatching: true,
                    useTilemapRasterPixelBatching: true,
                    useTilemapMultiPixelSpans: false)!);
            }

            if (CreateTilemapDemoCase(assets, useTilemapRasterPixelBatching: true) is { } demo)
            {
                cases.Add(demo);
                cases.Add(CreateTilemapDemoCase(
                    assets,
                    useTilemapRasterPixelBatching: false)!);
            }

            if (CreateHottCase(assets, useTilemapRasterPixelBatching: true) is { } hott)
            {
                cases.Add(hott);
                cases.Add(CreateHottCase(
                    assets,
                    useTilemapRasterPixelBatching: false)!);
            }
        }

        return cases;
    }

    private static BenchmarkCase? CreateRamTestCase(
        ZxNextSystemAssets assets,
        bool useInstructionBatching,
        bool useTilemapRasterPixelBatching,
        bool useTilemapMultiPixelSpans)
    {
        string path = Path.Combine(
            assets.RootDirectory,
            "extras",
            "ramtest",
            "1MBTEST.NEX");
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] image = File.ReadAllBytes(path);
        ReadOnlyMemory<byte> autoexec = CreatePostWelcomeNexAutoexec(
            assets.RootDirectory,
            "1mbtest.nex");
        return new BenchmarkCase(
            !useInstructionBatching
                ? "asset-ramtest-instruction-reference"
                : !useTilemapRasterPixelBatching
                ? "asset-ramtest-raster-reference"
                : !useTilemapMultiPixelSpans
                ? "asset-ramtest-pixel-span-reference"
                : "asset-ramtest",
            machine =>
            {
                machine.UseAdaptiveInstructionTstateBatching = useInstructionBatching;
                BootBrowserNex(machine, entryPc: 0xBF00);
                int startupFrames = 0;
                while (startupFrames < 600 && !machine.Tilemap.Enabled)
                {
                    machine.RunFrame(presentFrame: false);
                    startupFrames++;
                }

                if (!machine.Tilemap.Enabled)
                {
                    throw new InvalidOperationException(
                        "The supplied RAMTEST image did not enable its tilemap within 600 frames.");
                }

                machine.Video.UseTilemapRasterPixelBatching =
                    useTilemapRasterPixelBatching;
                machine.Video.UseTilemapMultiPixelSpans =
                    useTilemapMultiPixelSpans;
            },
            InitializeCpuLoop: false,
            Validate: ValidateAssetRamTest,
            CreateMachine: () =>
            {
                var bootLoaderRom = assets.BootLoaderRom
                    ?? throw new InvalidOperationException(
                        "The asset-ramtest scenario requires a boot-loader ROM; " +
                        "pass --zxnext-boot-rom <path>.");
                var overlay = new Dictionary<string, ReadOnlyMemory<byte>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["1mbtest.nex"] = image,
                    ["nextzxos/1mbtest.nex"] = image,
                    ["nextzxos/autoexec.1st"] = autoexec
                };
                ZxNextSystemSession session = ZxNextSystemSession.Create(
                    assets.RootDirectory,
                    bootLoaderRom.Path,
                    overlay);
                return new BenchmarkMachineContext(session.Machine, session);
            },
            AssetPath: path,
            AssetSha256: Convert.ToHexString(SHA256.HashData(image)));
    }

    /// <summary>
    /// The bundled tilemap demo: a continuously scrolling 80-column tilemap, which is
    /// the heaviest sustained tilemap fetch load available as an asset.
    /// </summary>
    private static BenchmarkCase? CreateTilemapDemoCase(
        ZxNextSystemAssets assets,
        bool useTilemapRasterPixelBatching)
    {
        string path = Path.Combine(
            assets.RootDirectory,
            "demos",
            "tilemap",
            "tm.nex");
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] image = File.ReadAllBytes(path);
        return new BenchmarkCase(
            useTilemapRasterPixelBatching
                ? "asset-tilemap-demo"
                : "asset-tilemap-demo-raster-reference",
            machine =>
            {
                machine.Video.UseTilemapRasterPixelBatching =
                    useTilemapRasterPixelBatching;
                _ = ZxNextNexLoader.Load(machine, image);
                int startupFrames = 0;
                while (startupFrames < 600 && !machine.Tilemap.Enabled)
                {
                    machine.RunFrame(presentFrame: false);
                    startupFrames++;
                }

                if (!machine.Tilemap.Enabled)
                {
                    throw new InvalidOperationException(
                        "The tilemap demo did not enable its tilemap within 600 frames.");
                }
            },
            InitializeCpuLoop: false,
            Validate: ValidateAssetTilemapDemo,
            SystemAssets: assets,
            AssetPath: path,
            AssetSha256: Convert.ToHexString(SHA256.HashData(image)));
    }

    private static void ValidateAssetTilemapDemo(
        ZxNextMachine machine,
        int frames,
        CounterSnapshot delta)
    {
        if (!machine.Tilemap.Enabled
            || delta.CpuTstates == 0
            || delta.TilemapGrants == 0
            || delta.TilemapCharacters == 0)
        {
            throw new InvalidOperationException(
                "The tilemap demo did not remain in its active tilemap workload.");
        }
    }

    private static BenchmarkCase? CreateHottCase(
        ZxNextSystemAssets assets,
        bool useTilemapRasterPixelBatching)
    {
        string path = Path.Combine(
            assets.RootDirectory,
            "games",
            "Next",
            "Halls of The Things",
            "hott.nex");
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] image = File.ReadAllBytes(path);
        return new BenchmarkCase(
            useTilemapRasterPixelBatching
                ? "asset-hott"
                : "asset-hott-raster-reference",
            machine =>
            {
                machine.Video.UseTilemapRasterPixelBatching =
                    useTilemapRasterPixelBatching;
                _ = ZxNextNexLoader.Load(machine, image);
                for (int frame = 0; frame < 120; frame++)
                {
                    machine.RunFrame(presentFrame: false);
                }

                machine.Keyboard.SetKeyState(SpectrumKey.D1, pressed: true);
                try
                {
                    for (int frame = 0; frame < 3; frame++)
                    {
                        machine.RunFrame(presentFrame: false);
                    }
                }
                finally
                {
                    machine.Keyboard.SetKeyState(SpectrumKey.D1, pressed: false);
                }

                int startupFrames = 0;
                while (startupFrames < 180 && !machine.Tilemap.Enabled)
                {
                    machine.RunFrame(presentFrame: false);
                    startupFrames++;
                }

                if (!machine.Tilemap.Enabled)
                {
                    throw new InvalidOperationException(
                        "The supplied Halls image did not enter its tilemap game screen.");
                }
            },
            InitializeCpuLoop: false,
            Validate: ValidateAssetHott,
            SystemAssets: assets,
            AssetPath: path,
            AssetSha256: Convert.ToHexString(SHA256.HashData(image)));
    }

    private static void ValidateAssetRamTest(
        ZxNextMachine machine,
        int frames,
        CounterSnapshot delta)
    {
        if (!machine.Tilemap.Enabled
            || delta.CpuTstates == 0
            || delta.TilemapGrants == 0
            || delta.TilemapCharacters == 0)
        {
            throw new InvalidOperationException(
                "RAMTEST did not remain in its active CPU/tilemap workload during measurement.");
        }
    }

    private static void ValidateAssetHott(
        ZxNextMachine machine,
        int frames,
        CounterSnapshot delta)
    {
        if (!machine.Tilemap.Enabled
            || delta.CpuTstates == 0
            || delta.TilemapGrants == 0
            || delta.TilemapStalls == 0
            || delta.TilemapDeadlineMisses != 0)
        {
            throw new InvalidOperationException(
                "Halls did not remain in its deadline-clean tilemap workload during measurement.");
        }
    }

    private static void BootBrowserNex(ZxNextMachine machine, ushort entryPc)
    {
        const int maximumStartupInstructions = 50_000_000;
        int startupInstructions = 0;
        var startupPcs = new Dictionary<ushort, int>();
        while (startupInstructions < maximumStartupInstructions
            && machine.Cpu.PC != entryPc
            && !(machine.Cpu.IsHalted && machine.Cpu.PC is 0x0C8F or 0x0C90))
        {
            if ((startupInstructions & 0x3FFF) == 0)
            {
                startupPcs[machine.Cpu.PC] = startupPcs.GetValueOrDefault(machine.Cpu.PC) + 1;
            }

            machine.StepInstruction();
            startupInstructions++;
        }

        if (machine.Cpu.PC == entryPc)
        {
            return;
        }

        if (!(machine.Cpu.IsHalted && machine.Cpu.PC is 0x0C8F or 0x0C90)
            || !PressGuestChord(machine, [SpectrumKey.Enter]))
        {
            string commonPcs = string.Join(
                ", ",
                startupPcs
                    .OrderByDescending(static pair => pair.Value)
                    .Take(8)
                    .Select(static pair => $"${pair.Key:X4}:{pair.Value}"));
            throw new InvalidOperationException(
                $"NextZXOS did not accept its bootstrap AUTOEXEC Enter "
                + $"(PC=${machine.Cpu.PC:X4}, HALT={(machine.Cpu.IsHalted ? 1 : 0)}, "
                + $"ROM={machine.MemoryBus.CurrentRomBank}, boot={(machine.MemoryBus.BootRomEnabled ? 1 : 0)}, "
                + $"reset={machine.ResetSequence}/{machine.LastResetKind}, "
                + $"frames={machine.UlaTiming.FrameNumber}, line IRQs={machine.UlaTiming.LineInterruptCount}, "
                + $"last NextReg=${machine.Registers.LastWrittenRegister:X2}/"
                + $"${machine.Registers.LastWrittenValue:X2}, instructions={startupInstructions}, "
                + $"sampled PCs [{commonPcs}]).");
        }

        for (int bootstrapCommand = 1;
            bootstrapCommand < 3 && ReadGuestWord(machine, 0x5C45) != 120;
            bootstrapCommand++)
        {
            int commandInstructions = 0;
            while (commandInstructions < 3_000_000
                && machine.Cpu.PC != entryPc
                && ReadGuestWord(machine, 0x5C45) != 120
                && !(machine.Cpu.IsHalted && machine.Cpu.PC is 0x0C8F or 0x0C90))
            {
                machine.StepInstruction();
                commandInstructions++;
            }

            if (machine.Cpu.PC == entryPc)
            {
                return;
            }

            if (ReadGuestWord(machine, 0x5C45) != 120
                && machine.Cpu.IsHalted
                && machine.Cpu.PC is 0x0C8F or 0x0C90)
            {
                _ = PressGuestChord(machine, [SpectrumKey.Enter]);
            }
        }

        int welcomeInstructions = 0;
        while (welcomeInstructions < maximumStartupInstructions
            && machine.Cpu.PC != entryPc
            && !(ReadGuestWord(machine, 0x5C45) == 120
                && machine.Cpu.IsHalted
                && machine.Cpu.PC is 0x0C8F or 0x0C90))
        {
            machine.StepInstruction();
            welcomeInstructions++;
        }

        if (machine.Cpu.PC == entryPc)
        {
            return;
        }

        if (!(ReadGuestWord(machine, 0x5C45) == 120
                && machine.Cpu.IsHalted
                && machine.Cpu.PC is 0x0C8F or 0x0C90)
            || !PressGuestChord(machine, [SpectrumKey.Space]))
        {
            throw new InvalidOperationException(
                $"NextZXOS did not accept the Welcome Space key "
                + $"(PC=${machine.Cpu.PC:X4}, startup instructions={startupInstructions}, "
                + $"welcome instructions={welcomeInstructions}).");
        }

        const int maximumLoadInstructions = 30_000_000;
        int loadInstructions = 0;
        while (loadInstructions < maximumLoadInstructions && machine.Cpu.PC != entryPc)
        {
            machine.StepInstruction();
            loadInstructions++;
        }

        if (machine.Cpu.PC != entryPc)
        {
            throw new InvalidOperationException(
                $"NextZXOS did not enter the benchmark NEX at ${entryPc:X4} within "
                + $"{maximumLoadInstructions} instructions (PC=${machine.Cpu.PC:X4}).");
        }
    }

    private static bool PressGuestChord(
        ZxNextMachine machine,
        ReadOnlySpan<SpectrumKey> keys,
        int maximumInstructions = 1_000_000)
    {
        foreach (SpectrumKey key in keys)
        {
            machine.Keyboard.SetKeyState(key, pressed: true);
        }

        bool accepted = false;
        try
        {
            for (int instructions = 0; instructions < maximumInstructions && !accepted; instructions++)
            {
                ushort previousPc = machine.Cpu.PC;
                machine.StepInstruction();
                accepted = previousPc == 0x0C95
                    && machine.Cpu.PC == 0x0C97
                    && machine.MemoryBus.CurrentRomBank == 0
                    && !machine.DivMmc.IsMapped;
            }
        }
        finally
        {
            foreach (SpectrumKey key in keys)
            {
                machine.Keyboard.SetKeyState(key, pressed: false);
            }
        }

        return accepted;
    }

    private static ushort ReadGuestWord(ZxNextMachine machine, ushort address)
    {
        byte low = machine.MemoryBus.Read(address);
        byte high = machine.MemoryBus.Read((ushort)(address + 1));
        return (ushort)(low | (high << 8));
    }

    private static byte[] CreatePostWelcomeNexAutoexec(
        string systemRoot,
        string nexFileName)
    {
        // A tree whose welcome screen has been disabled keeps the same program under its
        // original name, so it still serves as the file to patch.
        string path = Path.Combine(systemRoot, "nextzxos", "autoexec.1st");
        if (!File.Exists(path))
        {
            path = Path.Combine(systemRoot, "nextzxos", "welcome.bas");
        }

        byte[] original = File.ReadAllBytes(path);
        if (original.Length < 128
            || !original.AsSpan(0, 9).SequenceEqual("PLUS3DOS\x1A"u8))
        {
            throw new InvalidDataException("The supplied AUTOEXEC.1ST is not a +3DOS BASIC file.");
        }

        int insertOffset = -1;
        int position = 128;
        while (position + 4 <= original.Length)
        {
            int lineNumber = (original[position] << 8) | original[position + 1];
            int lineLength = original[position + 2] | (original[position + 3] << 8);
            if (lineNumber >= 200)
            {
                insertOffset = position;
                break;
            }

            position += 4 + lineLength;
        }

        if (insertOffset < 0)
        {
            throw new InvalidDataException("The supplied AUTOEXEC.1ST has no line 200 insertion point.");
        }

        byte[] body = Encoding.ASCII.GetBytes($".nexload {nexFileName}\r");
        var line = new byte[4 + body.Length];
        line[0] = 0x00;
        line[1] = 0xC3;
        BinaryPrimitives.WriteUInt16LittleEndian(line.AsSpan(2, 2), (ushort)body.Length);
        body.CopyTo(line, 4);

        var result = new byte[original.Length + line.Length];
        original.AsSpan(0, insertOffset).CopyTo(result);
        line.CopyTo(result, insertOffset);
        original.AsSpan(insertOffset).CopyTo(result.AsSpan(insertOffset + line.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(11, 4), (uint)result.Length);
        ushort programLength = checked((ushort)(result.Length - 128));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(16, 2), programLength);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20, 2), programLength);
        byte checksum = 0;
        for (int index = 0; index < 127; index++)
        {
            checksum ^= result[index];
        }

        result[127] = checksum;
        return result;
    }

    private static BenchmarkCase CpuCase(string name, ZxNextCpuSpeed speed) =>
        new(name, machine => SetCpuSpeed(machine, speed), RenderVideo: false);

    private static BenchmarkCase Layer2Case(
        string name,
        int resolution,
        bool ulaEnabled) =>
        new(name, machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            ConfigureLayer2(machine, resolution, ulaEnabled);
        });

    private static BenchmarkCase TilemapCase(
        string name,
        bool textMode,
        bool renderVideo,
        bool useInstructionBatching = true,
        bool useTilemapRasterPixelBatching = true,
        bool useTilemapMultiPixelSpans = true) =>
        new(name, machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            ConfigureTilemap(machine, textMode, disableUla: true);
            machine.UseAdaptiveInstructionTstateBatching = useInstructionBatching;
            machine.Video.UseTilemapRasterPixelBatching = useTilemapRasterPixelBatching;
            machine.Video.UseTilemapMultiPixelSpans = useTilemapMultiPixelSpans;
        }, RenderVideo: renderVideo);

    private static BenchmarkCase SpriteCase(string name, int spriteCount, bool overtime) =>
        new(name, machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            machine.Registers.Write(0x68, 0x80);
            ConfigureSprites(machine, spriteCount, overtime);
        });

    private static BenchmarkCase AudioCase(string name, int psgCount) =>
        new(name, machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            ConfigureAudio(machine, psgCount);
        }, RenderVideo: false, CaptureAudio: true);

    private static BenchmarkCase DmaCase(string name, bool fixedSource) =>
        new(name, machine =>
        {
            SetCpuSpeed(machine, ZxNextCpuSpeed.Mhz28);
            machine.Registers.Write(0x68, 0x80);
            ConfigureDmaMemory(machine, displayLayer2: false);
        },
        RenderVideo: false,
        BeforeFrame: fixedSource ? QueueDmaFill16K : QueueDmaCopy16K,
        Validate: fixedSource ? ValidateDmaFill : ValidateDmaCopy);

    private static void ConfigureCpuLoop(ZxNextMachine machine, bool contendedRead)
    {
        ushort readAddress = contendedRead ? (ushort)0x4000 : (ushort)0x8000;
        byte[] program =
        [
            0x3A, (byte)readAddress, (byte)(readAddress >> 8), // LD A,(nn)
            0xC3, 0x00, 0x80                              // JP $8000
        ];
        for (int index = 0; index < program.Length; index++)
        {
            machine.MemoryBus.WriteDirect((ushort)(0x8000 + index), program[index]);
        }

        machine.Cpu.PC = 0x8000;
    }

    private static void SetCpuSpeed(ZxNextMachine machine, ZxNextCpuSpeed speed) =>
        machine.Registers.Write(0x07, (byte)speed);

    /// <summary>Fills bank 5 with a deterministic pattern the ULA/LoRes generators read.</summary>
    private static void ConfigureUlaScreen(ZxNextMachine machine)
    {
        for (int address = 0; address < 0x4000; address++)
        {
            int page = 10 + (address / ZxNextPhysicalMemory.PageSize);
            byte value = (byte)((address * 31 + (address >> 5) + 1) & 0xFF);
            machine.MemoryBus.WritePhysicalRam(
                page,
                address & (ZxNextPhysicalMemory.PageSize - 1),
                value == 0 ? (byte)0x5A : value);
        }
    }

    private static void ConfigureLoresScreen(ZxNextMachine machine, bool radastan)
    {
        ConfigureUlaScreen(machine);
        machine.Registers.Write(0x6A, radastan ? (byte)0x27 : (byte)0x07);
        machine.Registers.Write(0x15, 0x80); // Enable the LoRes generator.
    }

    private static void ConfigureLayer2(ZxNextMachine machine, int resolution, bool ulaEnabled)
    {
        machine.Registers.Write(0x70, (byte)(resolution << 4));
        machine.Layer2.WriteControlPort(0x02);
        if (!ulaEnabled)
        {
            machine.Registers.Write(0x68, 0x80);
        }

        int byteCount = resolution == 0 ? 256 * 192 : 320 * 256;
        int firstPage = machine.Layer2.ActiveBank * 2;
        for (int address = 0; address < byteCount; address++)
        {
            int page = firstPage + (address / ZxNextPhysicalMemory.PageSize);
            byte value = (byte)((address * 13 + (address >> 8) + 1) & 0x7F);
            machine.MemoryBus.WritePhysicalRam(
                page,
                address & (ZxNextPhysicalMemory.PageSize - 1),
                value == 0 ? (byte)1 : value);
        }
    }

    private static void ConfigureTilemap(
        ZxNextMachine machine,
        bool textMode,
        bool disableUla)
    {
        machine.Registers.Write(0x6E, 0x20); // map at bank-5 offset $2000
        machine.Registers.Write(0x6F, 0x00); // definitions at bank-5 offset $0000
        machine.Registers.Write(0x6C, 0x2E);
        machine.Registers.Write(0x6B, textMode ? (byte)0xE8 : (byte)0xE0);
        if (disableUla)
        {
            machine.Registers.Write(0x68, 0x80);
        }

        for (int tile = 0; tile < 256; tile++)
        {
            int bytesPerTile = textMode ? 8 : 32;
            for (int offset = 0; offset < bytesPerTile; offset++)
            {
                WriteBank5(machine, (tile * bytesPerTile) + offset,
                    textMode
                        ? (byte)(((tile + offset) & 1) == 0 ? 0xAA : 0x55)
                        : (byte)((tile + offset) & 0xEE));
            }
        }

        for (int entry = 0; entry < 80 * 32; entry++)
        {
            WriteBank5(machine, 0x2000 + entry, (byte)(entry * 17));
        }
    }

    private static void WriteBank5(ZxNextMachine machine, int address, byte value)
    {
        int page = 10 + (address / ZxNextPhysicalMemory.PageSize);
        machine.MemoryBus.WritePhysicalRam(
            page,
            address & (ZxNextPhysicalMemory.PageSize - 1),
            value);
    }

    private static void ConfigureSprites(ZxNextMachine machine, int spriteCount, bool overtime)
    {
        machine.PortBus.WriteUncontended(ZxNextSpriteDevice.StatusAndSlotPort, 0);
        for (int index = 0; index < ZxNextSpriteDevice.PatternSize; index++)
        {
            machine.PortBus.WriteUncontended(ZxNextSpriteDevice.PatternPort, 0x01);
        }

        for (int sprite = 0; sprite < spriteCount; sprite++)
        {
            machine.Registers.Write(0x34, (byte)sprite);
            machine.Registers.Write(0x35, (byte)((sprite * 19) & 0xFF));
            machine.Registers.Write(0x36, overtime ? (byte)0 : (byte)((sprite * 7) % 240));
            machine.Registers.Write(0x37, (byte)((sprite >> 8) & 0x01));
            machine.Registers.Write(0x38, overtime ? (byte)0xC0 : (byte)0x80);
            machine.Registers.Write(0x39, overtime ? (byte)0x18 : (byte)0x00);
        }

        machine.Registers.Write(0x15, 0x03);
    }

    private static void ConfigureAudio(ZxNextMachine machine, int psgCount)
    {
        machine.PortBus.WriteUncontended(0x00FE, 0x10);
        if (psgCount == 0)
        {
            return;
        }

        if (psgCount > 1)
        {
            machine.Registers.Write(0x08, 0x12);
        }

        byte[] selectors = [0xFF, 0xFE, 0xFD];
        for (int chip = 0; chip < psgCount; chip++)
        {
            if (psgCount > 1)
            {
                machine.PortBus.WriteUncontended(0xFFFD, selectors[chip]);
            }

            WriteAyRegister(machine, 0, (byte)(0x20 + chip));
            WriteAyRegister(machine, 1, 0);
            WriteAyRegister(machine, 7, 0x3E);
            WriteAyRegister(machine, 8, 0x0F);
        }
    }

    private static void ConfigureDmaMemory(ZxNextMachine machine, bool displayLayer2)
    {
        for (int address = 0; address < 0x4000; address++)
        {
            machine.MemoryBus.WritePhysicalRam(
                address >> 13,
                address & (ZxNextPhysicalMemory.PageSize - 1),
                DmaSourceValue(address));
        }

        machine.MemoryBus.WritePhysicalRam(0, 0x0123, 0xE3);
        machine.Layer2.WriteControlPort(displayLayer2 ? (byte)0x03 : (byte)0x01);
    }

    private static byte DmaSourceValue(int address) =>
        (byte)((address * 37) ^ (address >> 5) ^ 0xA5);

    private static void QueueDmaCopy16K(ZxNextMachine machine) =>
        WriteDmaCommand(machine, DmaCopy16KCommand);

    private static void QueueDmaFill16K(ZxNextMachine machine) =>
        WriteDmaCommand(machine, DmaFill16KCommand);

    private static void WriteDmaCommand(ZxNextMachine machine, ReadOnlySpan<byte> command)
    {
        for (int index = 0; index < command.Length; index++)
        {
            ushort port = (ushort)(((command.Length - index) << 8)
                | ZxNextDmaDevice.ZxnControlPort);
            machine.PortBus.WriteUncontended(port, command[index]);
        }
    }

    private static void ValidateDmaCopy(
        ZxNextMachine machine,
        int frames,
        CounterSnapshot counters)
    {
        ValidateDmaCounters(machine, frames, counters);
        int[] probes = [0, 0x0123, 0x1FFF, 0x2000, 0x3FFF];
        foreach (int address in probes)
        {
            byte actual = machine.MemoryBus.ReadPhysicalRam(
                16 + (address >> 13),
                address & (ZxNextPhysicalMemory.PageSize - 1));
            byte expected = address == 0x0123 ? (byte)0xE3 : DmaSourceValue(address);
            if (actual != expected)
            {
                throw new InvalidOperationException(
                    $"DMA benchmark copy diverged at ${address:X4}: expected ${expected:X2}, found ${actual:X2}.");
            }
        }
    }

    private static void ValidateDmaFill(
        ZxNextMachine machine,
        int frames,
        CounterSnapshot counters)
    {
        ValidateDmaCounters(machine, frames, counters);
        int[] probes = [0, 0x0123, 0x1FFF, 0x2000, 0x3FFF];
        foreach (int address in probes)
        {
            byte actual = machine.MemoryBus.ReadPhysicalRam(
                16 + (address >> 13),
                address & (ZxNextPhysicalMemory.PageSize - 1));
            if (actual != 0xE3)
            {
                throw new InvalidOperationException(
                    $"DMA benchmark fill diverged at ${address:X4}: found ${actual:X2}.");
            }
        }
    }

    private static void ValidateSpriteDma(
        ZxNextMachine machine,
        int frames,
        CounterSnapshot counters)
    {
        ValidateDmaCopy(machine, frames, counters);
        if (counters.SpriteOvertime == 0)
        {
            throw new InvalidOperationException(
                "The sprite/DMA benchmark did not exercise an over-budget sprite line.");
        }
    }

    private static void ValidateDmaCounters(
        ZxNextMachine machine,
        int frames,
        CounterSnapshot counters)
    {
        if (counters.DmaGrants != (ulong)frames
            || counters.DmaMasterTicks == 0
            || machine.Dma.LastTransferLength != 0x4000
            || machine.Dma.IsActive
            || machine.BusArbiter.Owner != ZxNextBusOwner.Cpu)
        {
            throw new InvalidOperationException(
                $"DMA benchmark contract failed: grants={counters.DmaGrants}, "
                + $"ticks={counters.DmaMasterTicks}, length={machine.Dma.LastTransferLength}, "
                + $"active={machine.Dma.IsActive}, owner={machine.BusArbiter.Owner}.");
        }
    }

    private static void WriteAyRegister(ZxNextMachine machine, byte register, byte value)
    {
        machine.PortBus.WriteUncontended(0xFFFD, register);
        machine.PortBus.WriteUncontended(0xBFFD, value);
    }

    private static void WriteResult(TextWriter writer, BenchmarkResult result, int repetitions)
    {
        string runSuffix = repetitions > 1 ? $" run {result.RunIndex}/{repetitions}" : string.Empty;
        WriteLine(writer, $"[{result.Scenario}{runSuffix}]");
        WriteLine(writer, $"  Elapsed:          {result.ElapsedSeconds:F3}s");
        WriteLine(writer, $"  Effective FPS:    {result.EffectiveFps:F1}");
        WriteLine(writer, $"  Realtime speed:   {result.RealtimeMultiplier:F2}x");
        WriteLine(writer, $"  CPU throughput:   {result.CpuMtstatesPerSecond:F1} MT/s");
        WriteLine(writer, $"  Master throughput:{result.MasterMticksPerSecond,8:F1} Mtick/s");
        WriteLine(writer, $"  Allocated:        {result.AllocatedBytes:N0} bytes");
        WriteLine(writer, $"  GC collections:   {result.Gen0}/{result.Gen1}/{result.Gen2}");
        WriteLine(writer, $"  Video grants:     ULA {result.UlaGrants:N0}, tile {result.TilemapGrants:N0}, stalls {result.TilemapStalls:N0}");
        WriteLine(writer, $"  Tile pipeline:    chars {result.TilemapCharacters:N0}, pixels {result.TilemapPixels:N0}, misses {result.TilemapDeadlineMisses:N0}");
        WriteLine(writer, $"  Sprites:          ticks {result.SpriteTicks:N0}, overtime {result.SpriteOvertime:N0}");
        WriteLine(writer, $"  DMA:              grants {result.DmaGrants:N0}, ticks {result.DmaMasterTicks:N0}");
        WriteLine(writer, $"  Audio samples:    {result.AudioSamples:N0}");
        WriteLine(writer, $"  Audio queue peak: {result.AudioQueueHighWater:N0}");
        WriteLine(writer, $"  Frame hash:       {result.FrameHash}");
        WriteLine(writer, string.Empty);
    }

    private static void WriteSummaries(
        TextWriter writer,
        IReadOnlyList<BenchmarkCase> cases,
        IReadOnlyList<BenchmarkResult> results)
    {
        WriteLine(writer, "Medians");
        foreach (BenchmarkCase benchmarkCase in cases)
        {
            BenchmarkResult[] caseResults = results
                .Where(result => result.Scenario == benchmarkCase.Name)
                .OrderBy(result => result.EffectiveFps)
                .ToArray();
            double medianFps = Median(caseResults.Select(static result => result.EffectiveFps));
            double medianRealtime = Median(caseResults.Select(static result => result.RealtimeMultiplier));
            double minimumFps = caseResults[0].EffectiveFps;
            double maximumFps = caseResults[^1].EffectiveFps;
            double spread = medianFps == 0 ? 0 : (maximumFps - minimumFps) / medianFps * 100.0;
            bool deterministic = caseResults.All(result =>
                result.FrameHash == caseResults[0].FrameHash
                && result.CpuTstates == caseResults[0].CpuTstates
                && result.MasterTicks == caseResults[0].MasterTicks
                && result.PublishedFrames == caseResults[0].PublishedFrames
                && result.RenderedFrames == caseResults[0].RenderedFrames
                && result.AudioSamples == caseResults[0].AudioSamples
                && result.AudioQueueHighWater == caseResults[0].AudioQueueHighWater
                && result.UlaGrants == caseResults[0].UlaGrants
                && result.TilemapGrants == caseResults[0].TilemapGrants
                && result.TilemapStalls == caseResults[0].TilemapStalls
                && result.TilemapCharacters == caseResults[0].TilemapCharacters
                && result.TilemapPixels == caseResults[0].TilemapPixels
                && result.TilemapDeadlineMisses == caseResults[0].TilemapDeadlineMisses
                && result.SpriteTicks == caseResults[0].SpriteTicks
                && result.SpriteOvertime == caseResults[0].SpriteOvertime
                && result.DmaGrants == caseResults[0].DmaGrants
                && result.DmaMasterTicks == caseResults[0].DmaMasterTicks);
            double[] ordered = [.. caseResults.Select(static result => result.EffectiveFps)];
            double trimmed = TrimmedMean(ordered);
            string dispersion = spread > HighDispersionPercent
                ? $"  HIGH DISPERSION ({spread:F1}%), treat differences below {spread / 2:F0}% as noise"
                : string.Empty;
            WriteLine(writer,
                $"  {benchmarkCase.Name,-30} {medianFps,7:F1} fps  "
                + $"{medianRealtime,5:F2}x  trimmed {trimmed,7:F1}  "
                + $"range {minimumFps:F1}-{maximumFps:F1} "
                + $"({spread:F1}%)  {(deterministic ? "deterministic" : "COUNTER/HASH MISMATCH")}"
                + dispersion);
        }

        WriteLine(writer, string.Empty);
    }

    private const double HighDispersionPercent = 5.0;

    /// <summary>
    /// Mean with the extreme value at each end dropped, which is far less sensitive to
    /// the occasional scheduling outlier than the raw mean.
    /// </summary>
    private static double TrimmedMean(double[] values)
    {
        if (values.Length < 5)
        {
            return Median(values);
        }

        double[] ordered = [.. values.Order()];
        int trim = ordered.Length >= 9 ? 2 : 1;
        return ordered[trim..^trim].Average();
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] ordered = values.Order().ToArray();
        int middle = ordered.Length / 2;
        return (ordered.Length & 1) != 0
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2.0;
    }

    private static string ToCsv(BenchmarkResult result)
    {
        string[] fields =
        [
            result.Identity.TimestampUtc,
            result.Identity.Commit,
            result.Identity.Runtime,
            result.Identity.Os,
            result.Identity.Cpu,
            result.Identity.LogicalCpuCount.ToString(CultureInfo.InvariantCulture),
            result.Scenario,
            result.AssetPath,
            result.AssetSha256,
            result.RunIndex.ToString(CultureInfo.InvariantCulture),
            result.WarmupFrames.ToString(CultureInfo.InvariantCulture),
            result.Frames.ToString(CultureInfo.InvariantCulture),
            result.ElapsedSeconds.ToString("F6", CultureInfo.InvariantCulture),
            result.EffectiveFps.ToString("F3", CultureInfo.InvariantCulture),
            result.RealtimeMultiplier.ToString("F6", CultureInfo.InvariantCulture),
            result.CpuTstates.ToString(CultureInfo.InvariantCulture),
            result.CpuMtstatesPerSecond.ToString("F6", CultureInfo.InvariantCulture),
            result.MasterTicks.ToString(CultureInfo.InvariantCulture),
            result.MasterMticksPerSecond.ToString("F6", CultureInfo.InvariantCulture),
            result.AllocatedBytes.ToString(CultureInfo.InvariantCulture),
            result.Gen0.ToString(CultureInfo.InvariantCulture),
            result.Gen1.ToString(CultureInfo.InvariantCulture),
            result.Gen2.ToString(CultureInfo.InvariantCulture),
            result.PublishedFrames.ToString(CultureInfo.InvariantCulture),
            result.RenderedFrames.ToString(CultureInfo.InvariantCulture),
            result.AudioSamples.ToString(CultureInfo.InvariantCulture),
            result.AudioQueueHighWater.ToString(CultureInfo.InvariantCulture),
            result.UlaGrants.ToString(CultureInfo.InvariantCulture),
            result.TilemapGrants.ToString(CultureInfo.InvariantCulture),
            result.TilemapStalls.ToString(CultureInfo.InvariantCulture),
            result.TilemapCharacters.ToString(CultureInfo.InvariantCulture),
            result.TilemapPixels.ToString(CultureInfo.InvariantCulture),
            result.TilemapDeadlineMisses.ToString(CultureInfo.InvariantCulture),
            result.SpriteTicks.ToString(CultureInfo.InvariantCulture),
            result.SpriteOvertime.ToString(CultureInfo.InvariantCulture),
            result.DmaGrants.ToString(CultureInfo.InvariantCulture),
            result.DmaMasterTicks.ToString(CultureInfo.InvariantCulture),
            result.FrameHash
        ];
        return string.Join(',', fields.Select(EscapeCsv));
    }

    /// <summary>Reads back a row produced by <see cref="ToCsv"/>.</summary>
    private static BenchmarkResult ParseCsv(string line)
    {
        string[] f = SplitCsv(line);
        const int expected = 38;
        if (f.Length != expected)
        {
            throw new InvalidOperationException(
                $"An isolated benchmark row had {f.Length} fields, expected {expected}.");
        }

        static double D(string value) =>
            double.Parse(value, CultureInfo.InvariantCulture);
        static int I(string value) =>
            int.Parse(value, CultureInfo.InvariantCulture);
        static long L(string value) =>
            long.Parse(value, CultureInfo.InvariantCulture);
        static ulong U(string value) =>
            ulong.Parse(value, CultureInfo.InvariantCulture);

        return new BenchmarkResult(
            new MachineIdentity(f[0], f[1], f[2], f[3], f[4], I(f[5])),
            f[6], f[7], f[8], I(f[9]), I(f[10]), I(f[11]),
            D(f[12]), D(f[13]), D(f[14]), U(f[15]), D(f[16]), U(f[17]), D(f[18]),
            L(f[19]), I(f[20]), I(f[21]), I(f[22]), L(f[23]), I(f[24]), L(f[25]), I(f[26]),
            U(f[27]), U(f[28]), U(f[29]), U(f[30]), U(f[31]), U(f[32]), U(f[33]), U(f[34]),
            U(f[35]), U(f[36]), f[37]);
    }

    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (quoted)
            {
                if (character != '"')
                {
                    current.Append(character);
                }
                else if (index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    fields.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        fields.Add(current.ToString());
        return [.. fields];
    }

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : '"' + value.Replace("\"", "\"\"") + '"';

    private static string HashFrame(int[] frame)
    {
        ulong hash = 14695981039346656037UL;
        foreach (int pixel in frame)
        {
            uint value = unchecked((uint)pixel);
            hash ^= (byte)value;
            hash *= 1099511628211UL;
            hash ^= (byte)(value >> 8);
            hash *= 1099511628211UL;
            hash ^= (byte)(value >> 16);
            hash *= 1099511628211UL;
            hash ^= (byte)(value >> 24);
            hash *= 1099511628211UL;
        }

        return hash.ToString("X16", CultureInfo.InvariantCulture);
    }

    private static MachineIdentity GetMachineIdentity()
    {
        Assembly assembly = typeof(ZxNextBenchmarkRunner).Assembly;
        string build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
        string cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
            ?? RuntimeInformation.ProcessArchitecture.ToString();
        return new MachineIdentity(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            build,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            cpu,
            Environment.ProcessorCount);
    }

    private static string ResolveOutputPath(string? requestedPath)
    {
        if (!string.IsNullOrWhiteSpace(requestedPath))
        {
            return Path.GetFullPath(requestedPath);
        }

        string root = FindRepositoryRoot() ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, "TEST", "zxnext-benchmark-results.txt");
    }

    private static string? FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ZedExEss.sln"))
                && Directory.Exists(Path.Combine(directory.FullName, "ZedExEss.Core")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static void EnsureOutputDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void WriteLine(TextWriter writer, string line)
    {
        writer.WriteLine(line);
        Debug.WriteLine(line);
    }

    private sealed record BenchmarkCase(
        string Name,
        Action<ZxNextMachine> Configure,
        bool RenderVideo = true,
        bool CaptureAudio = false,
        Action<ZxNextMachine>? BeforeFrame = null,
        Action<ZxNextMachine, int, CounterSnapshot>? Validate = null,
        bool InitializeCpuLoop = true,
        ZxNextSystemAssets? SystemAssets = null,
        Func<BenchmarkMachineContext>? CreateMachine = null,
        string? AssetPath = null,
        string? AssetSha256 = null);

    private sealed class BenchmarkMachineContext : IDisposable
    {
        private readonly IDisposable? _owner;

        public BenchmarkMachineContext(ZxNextMachine machine, IDisposable? owner = null)
        {
            Machine = machine ?? throw new ArgumentNullException(nameof(machine));
            _owner = owner;
        }

        public ZxNextMachine Machine { get; }

        public void Dispose() => _owner?.Dispose();
    }

    private readonly record struct MachineIdentity(
        string TimestampUtc,
        string Commit,
        string Runtime,
        string Os,
        string Cpu,
        int LogicalCpuCount);

    private readonly record struct CounterSnapshot(
        ulong CpuTstates,
        ulong MasterTicks,
        ulong UlaGrants,
        ulong TilemapGrants,
        ulong TilemapStalls,
        ulong TilemapCharacters,
        ulong TilemapPixels,
        ulong TilemapDeadlineMisses,
        ulong SpriteTicks,
        ulong SpriteOvertime,
        ulong DmaGrants,
        ulong DmaMasterTicks)
    {
        public static CounterSnapshot Capture(ZxNextMachine machine)
        {
            return new CounterSnapshot(
                machine.Cpu.Cyc,
                machine.MasterTicks,
                machine.VideoMemoryArbiter.UlaGrantCount,
                machine.VideoMemoryArbiter.TilemapGrantCount,
                machine.VideoMemoryArbiter.TilemapStallCount,
                machine.Tilemap.CompletedCharacterFetches,
                machine.Tilemap.FetchedPixelCount,
                machine.Tilemap.TimedPixelDeadlineMisses,
                machine.Sprites.SpritePreparationMasterTicks,
                machine.Sprites.OvertimeCount,
                machine.BusArbiter.DmaGrantCount,
                machine.BusArbiter.DmaOwnedMasterTicks);
        }

        public static CounterSnapshot operator -(CounterSnapshot right, CounterSnapshot left) =>
            new(
                right.CpuTstates - left.CpuTstates,
                right.MasterTicks - left.MasterTicks,
                right.UlaGrants - left.UlaGrants,
                right.TilemapGrants - left.TilemapGrants,
                right.TilemapStalls - left.TilemapStalls,
                right.TilemapCharacters - left.TilemapCharacters,
                right.TilemapPixels - left.TilemapPixels,
                right.TilemapDeadlineMisses - left.TilemapDeadlineMisses,
                right.SpriteTicks - left.SpriteTicks,
                right.SpriteOvertime - left.SpriteOvertime,
                right.DmaGrants - left.DmaGrants,
                right.DmaMasterTicks - left.DmaMasterTicks);
    }

    private readonly record struct BenchmarkResult(
        MachineIdentity Identity,
        string Scenario,
        string AssetPath,
        string AssetSha256,
        int RunIndex,
        int WarmupFrames,
        int Frames,
        double ElapsedSeconds,
        double EffectiveFps,
        double RealtimeMultiplier,
        ulong CpuTstates,
        double CpuMtstatesPerSecond,
        ulong MasterTicks,
        double MasterMticksPerSecond,
        long AllocatedBytes,
        int Gen0,
        int Gen1,
        int Gen2,
        long PublishedFrames,
        int RenderedFrames,
        long AudioSamples,
        int AudioQueueHighWater,
        ulong UlaGrants,
        ulong TilemapGrants,
        ulong TilemapStalls,
        ulong TilemapCharacters,
        ulong TilemapPixels,
        ulong TilemapDeadlineMisses,
        ulong SpriteTicks,
        ulong SpriteOvertime,
        ulong DmaGrants,
        ulong DmaMasterTicks,
        string FrameHash);
}
