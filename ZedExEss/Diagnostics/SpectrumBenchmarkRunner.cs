using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System;
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
    /// <summary>Machine model, duration and presentation cadence for a headless benchmark.</summary>
    public sealed class SpectrumBenchmarkOptions
    {
        public SpectrumModel Model { get; init; } = SpectrumModel.Spectrum128K;
        public int Frames { get; init; } = 2000;
        public int PresentEveryNFrames { get; init; } = 5;
        public bool UseFastTapeCpuBatching { get; init; }
        public string? OutputPath { get; init; }
    }
    /// <summary>Measures production emulation throughput without WPF or realtime audio throttling.</summary>
    /// <remarks>
    /// Video is enabled only on requested presentation frames, matching turbo frameskip while
    /// leaving CPU, ULA timing, contention and peripheral advancement active on every frame.
    /// </remarks>
    public static class SpectrumBenchmarkRunner
    {
        public static int Run(SpectrumBenchmarkOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (options.Frames <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Frame count must be positive.");
            }

            if (options.PresentEveryNFrames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "Present interval cannot be negative.");
            }

            string outputPath = ResolveOutputPath(options.OutputPath);
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };

            try
            {
                using var machine = CreateMachine(options.Model);
                int frames = options.Frames;
                int presentEvery = options.PresentEveryNFrames;
                bool videoEnabled = true;
                machine.Emulator.FastTapeCpuBatchingEnabled = options.UseFastTapeCpuBatching;

                Stopwatch stopwatch = Stopwatch.StartNew();
                for (int frame = 0; frame < frames; frame++)
                {
                    bool presentFrame = presentEvery > 0 && frame % presentEvery == 0;
                    if (presentFrame != videoEnabled)
                    {
                        machine.Emulator.VideoEnabled = presentFrame;
                        videoEnabled = presentFrame;
                    }

                    machine.Emulator.RunFrame(presentFrame);
                }

                stopwatch.Stop();
                machine.Emulator.FastTapeCpuBatchingEnabled = false;
                machine.Emulator.VideoEnabled = true;

                double elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.000001);
                double effectiveFps = frames / elapsedSeconds;
                int tstatesPerFrame = SpectrumUlaTiming.ForModel(options.Model).TstatesPerFrame;
                int cpuHz = SpectrumAudioTiming.CpuClockHz(options.Model);
                double realtimeFps = (double)cpuHz / tstatesPerFrame;
                double realtimePercent = effectiveFps / realtimeFps * 100.0;
                double mtstatesPerSecond = machine.Cpu.Cyc / elapsedSeconds / 1_000_000.0;

                WriteLine(writer, "Spectrum headless benchmark");
                WriteLine(writer, $"Model:          {options.Model}");
                WriteLine(writer, $"Frames:         {frames.ToString("N0", CultureInfo.InvariantCulture)}");
                WriteLine(writer, $"Present every:  {(presentEvery == 0 ? "never" : presentEvery.ToString(CultureInfo.InvariantCulture))}");
                WriteLine(writer, $"CPU batching:   {(options.UseFastTapeCpuBatching ? "fast tape" : "normal")}");
                WriteLine(writer, $"Elapsed:        {elapsedSeconds:F3}s");
                WriteLine(writer, $"Effective FPS:  {effectiveFps:F1}");
                WriteLine(writer, $"Realtime FPS:   {realtimeFps:F2}");
                WriteLine(writer, $"Realtime speed: {realtimePercent:F0}%");
                WriteLine(writer, $"CPU T-states:   {machine.Cpu.Cyc.ToString("N0", CultureInfo.InvariantCulture)}");
                WriteLine(writer, $"Throughput:     {mtstatesPerSecond:F1} MT/s");
                WriteLine(writer, $"Output:         {outputPath}");

                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                WriteLine(writer, "Spectrum headless benchmark");
                WriteLine(writer, $"Output: {outputPath}");
                WriteLine(writer, $"Error:  {ex.Message}");
                Debug.WriteLine(ex.ToString());
                return 3;
            }
        }
        public static bool TryParseModel(string value, out SpectrumModel model)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "16":
                case "16k":
                case "spectrum16k":
                    model = SpectrumModel.Spectrum16K;
                    return true;
                case "48":
                case "48k":
                case "spectrum48k":
                    model = SpectrumModel.Spectrum48K;
                    return true;
                case "128":
                case "128k":
                case "spectrum128k":
                    model = SpectrumModel.Spectrum128K;
                    return true;
                case "+2":
                case "plus2":
                case "spectrumplus2":
                    model = SpectrumModel.SpectrumPlus2;
                    return true;
                case "+2a":
                case "plus2a":
                case "spectrumplus2a":
                    model = SpectrumModel.SpectrumPlus2A;
                    return true;
                case "+3":
                case "plus3":
                case "spectrumplus3":
                    model = SpectrumModel.SpectrumPlus3;
                    return true;
                default:
                    return Enum.TryParse(value, ignoreCase: true, out model);
            }
        }
        private static BenchmarkMachine CreateMachine(SpectrumModel model)
        {
            SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
            {
                Model = model,
                Roms = RomSet.CreateBlank(GetRomBankCount(model))
            });
            machine.AttachTape(null);
            return new BenchmarkMachine(machine.Cpu, machine.Emulator);
        }
        private static string ResolveOutputPath(string? requestedPath)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                return Path.GetFullPath(requestedPath);
            }

            string root = FindRepositoryRoot() ?? Directory.GetCurrentDirectory();
            return Path.Combine(root, "TEST", "benchmark-results.txt");
        }
        private static string? FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory != null)
            {
                string projectPath = Path.Combine(directory.FullName, "ZedExEss.csproj");
                if (File.Exists(projectPath))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return null;
        }
        private static void WriteLine(TextWriter writer, string line)
        {
            writer.WriteLine(line);
            Debug.WriteLine(line);
        }
        private static int GetRomBankCount(SpectrumModel model)
        {
            return SpectrumModelTraits.RomBankCount(model);
        }
        private sealed class BenchmarkMachine(Z80 cpu, SpectrumEmulator emulator) : IDisposable
        {
            public Z80 Cpu { get; } = cpu;
            public SpectrumEmulator Emulator { get; } = emulator;
            public void Dispose()
            {
                Emulator.VideoEnabled = true;
            }
        }
    }
}
