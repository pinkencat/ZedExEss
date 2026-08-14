/*
 * === File overview ===
 * Headless TR-DOS / Beta 128 hang diagnostic. Boots a Beta-equipped machine,
 * drives the startup menu, then samples a PC histogram so a wedged firmware
 * poll loop can be identified by address rather than inferred from a black
 * screen. Pairs with the FD1793 trace emitted by the disk controller.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Disk.Beta;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Diagnostics
{
    // Type responsibility: Coordinates TR-DOS diagnostic options as part of the ZedExEss application.
    public sealed class TrDosDiagnosticOptions
    {
        public SpectrumModel Model { get; init; } = SpectrumModel.Scorpion256;
        public string? DiskPath { get; init; }
        public string? OutputPath { get; init; }

        /// <summary>Cursor-down presses before ENTER. 0 selects the default Scorpion menu item (128 TR-DOS).</summary>
        public int MenuDownPresses { get; init; }

        /// <summary>Letters typed after the menu selection, e.g. "RUN" followed by ENTER.</summary>
        public string? TypeAfterBoot { get; init; }

        /// <summary>Frames to run after the menu selection before pressing ENTER again. 0 disables.</summary>
        public int EnterAfterFrames { get; init; }

        public int BootFrames { get; init; } = 200;
        public int RunFrames { get; init; } = 1_200;
        public int SampleFrames { get; init; } = 200;
        public bool FdcTrace { get; init; }
    }

    /// <summary>
    /// Reports whether a Beta 128 machine is still making forward progress after a
    /// disk access, and if not, exactly which addresses it is spinning between.
    /// </summary>
    public static class TrDosDiagnosticRunner
    {
        // Method role: Implements run as part of the ZedExEss application.
        public static int Run(TrDosDiagnosticOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            string outputPath = string.IsNullOrWhiteSpace(options.OutputPath)
                ? Path.Combine(AppContext.BaseDirectory, "trdos-diagnostic.log")
                : Path.GetFullPath(options.OutputPath);
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
            try
            {
                return RunCore(writer, options, outputPath);
            }
            catch (Exception ex)
            {
                writer.WriteLine($"ERROR {ex}");
                return 2;
            }
        }

        // Method role: Boots the machine, drives the menu and samples the resulting steady state.
        private static int RunCore(StreamWriter writer, TrDosDiagnosticOptions options, string outputPath)
        {
            string root = FindRepositoryRoot() ?? Directory.GetCurrentDirectory();
            RomSet roms = LoadRoms(options.Model, root);
            byte[] trdosRom = LoadTrDosRom(options.Model, root, roms);

            writer.WriteLine($"TR-DOS diagnostic model={options.Model}");
            writer.WriteLine($"disk={(options.DiskPath ?? "<none>")}");
            writer.WriteLine($"menuDown={options.MenuDownPresses} type=\"{options.TypeAfterBoot ?? string.Empty}\"");
            writer.WriteLine();

            SpectrumBeta128Device? beta = null;
            SpectrumBeta128DiskController? controller = null;
            SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
            {
                Model = options.Model,
                Roms = roms,
                ConfigureDevices = context =>
                {
                    beta = new SpectrumBeta128Device(trdosRom);
                    context.Memory.ConfigureBeta128(beta);
                    controller = new SpectrumBeta128DiskController(beta);
                    context.Ports.AddDevice(controller);
                }
            });
            machine.AttachTape(null);

            if (beta == null || controller == null)
            {
                writer.WriteLine("ERROR Beta 128 device was not configured.");
                return 2;
            }

            if (options.FdcTrace)
            {
                string tracePath = Path.ChangeExtension(outputPath, ".fdc.log");
                controller.ConfigureTracing(true, tracePath);
                writer.WriteLine($"fdcTrace={tracePath}");
            }

            if (!string.IsNullOrWhiteSpace(options.DiskPath))
            {
                controller.InsertDisk(0, TrdDiskImage.Load(Path.GetFullPath(options.DiskPath)));
            }

            RunFrames(machine, options.BootFrames);
            writer.WriteLine(DescribeState("after-boot", machine, beta, controller));

            for (int i = 0; i < options.MenuDownPresses; i++)
            {
                PressChord(machine, [SpectrumKey.CapsShift, SpectrumKey.D6]);
            }

            PressChord(machine, [SpectrumKey.Enter]);
            writer.WriteLine(DescribeState("after-enter", machine, beta, controller));

            if (!string.IsNullOrWhiteSpace(options.TypeAfterBoot))
            {
                // Wait long enough for the firmware to finish its boot probe and put
                // up a prompt; typing into a busy ROM drops the keystrokes.
                RunFrames(machine, options.EnterAfterFrames > 0 ? options.EnterAfterFrames : 300);
                writer.WriteLine(DescribeState("before-type", machine, beta, controller));
                foreach (char character in options.TypeAfterBoot)
                {
                    if (TryMapKey(character, out SpectrumKey key))
                    {
                        PressChord(machine, [key]);
                    }
                }

                PressChord(machine, [SpectrumKey.Enter]);
                writer.WriteLine(DescribeState("after-type", machine, beta, controller));
            }

            if (options.EnterAfterFrames > 0)
            {
                RunFrames(machine, options.EnterAfterFrames);
                writer.WriteLine(DescribeState("before-second-enter", machine, beta, controller));
                uint beforeEnter = HashRam(machine.Memory);
                PressChord(machine, [SpectrumKey.Enter]);
                RunFrames(machine, 50);
                writer.WriteLine(DescribeState("after-second-enter", machine, beta, controller));
                writer.WriteLine($"ramChangedAcrossEnter={HashRam(machine.Memory) != beforeEnter}");
            }

            // Progress probe: run in slices and watch whether RAM or the FDC command
            // count still change. A wedged loop keeps executing, so CPU cycles alone
            // never prove liveness.
            const int SliceCount = 6;
            int sliceFrames = Math.Max(1, options.RunFrames / SliceCount);
            uint previousRam = HashRam(machine.Memory);
            long previousCommands = controller.CommandCount;
            for (int slice = 0; slice < SliceCount; slice++)
            {
                RunFrames(machine, sliceFrames);
                uint ram = HashRam(machine.Memory);
                long commands = controller.CommandCount;
                writer.WriteLine(
                    $"SLICE {slice} frames={(slice + 1) * sliceFrames} pc={machine.Cpu.PC:X4} " +
                    $"ramChanged={(ram != previousRam)} fdcCommands=+{commands - previousCommands} " +
                    $"{DescribeController(controller, beta, machine.Memory)}");
                previousRam = ram;
                previousCommands = commands;
            }

            writer.WriteLine();
            writer.WriteLine(DescribeState("steady", machine, beta, controller));

            // PC histogram over the final window identifies the spin loop.
            var histogram = new Dictionary<ushort, long>();
            long samples = 0;
            machine.Emulator.ConfigureBeforeCpuStep(() =>
            {
                ushort pc = machine.Cpu.PC;
                histogram.TryGetValue(pc, out long count);
                histogram[pc] = count + 1;
                samples++;
                return false;
            });

            uint ramBeforeSample = HashRam(machine.Memory);
            long commandsBeforeSample = controller.CommandCount;
            RunFrames(machine, Math.Max(1, options.SampleFrames));
            machine.Emulator.ConfigureBeforeCpuStep(null);

            uint ramAfterSample = HashRam(machine.Memory);
            bool ramChanged = ramAfterSample != ramBeforeSample;
            long commandsDuringSample = controller.CommandCount - commandsBeforeSample;

            writer.WriteLine();
            writer.WriteLine($"=== PC histogram over {options.SampleFrames} frames ({samples} instructions, {histogram.Count} distinct PCs) ===");
            foreach (KeyValuePair<ushort, long> entry in histogram.OrderByDescending(e => e.Value).Take(16))
            {
                double percent = samples == 0 ? 0 : 100.0 * entry.Value / samples;
                writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  pc={entry.Key:X4} count={entry.Value} ({percent:F1}%) rom={DescribeAddressSpace(entry.Key, beta, machine.Memory)}"));
            }

            writer.WriteLine();
            writer.WriteLine($"ramChangedDuringSample={ramChanged} fdcCommandsDuringSample={commandsDuringSample}");

            // Raw bytes around the hottest address let the spin loop be decoded
            // without attaching a debugger to a wedged machine.
            if (histogram.Count > 0)
            {
                ushort hot = histogram.OrderByDescending(e => e.Value).First().Key;
                ushort start = unchecked((ushort)(hot - 8));
                var bytes = new StringBuilder();
                for (int offset = 0; offset < 24; offset++)
                {
                    bytes.Append(CultureInfo.InvariantCulture, $"{machine.Memory.ReadDirect(unchecked((ushort)(start + offset))):X2} ");
                }

                writer.WriteLine($"hotPc={hot:X4} bytes[{start:X4}..]= {bytes.ToString().TrimEnd()}");
            }

            string screen = PentagonVerificationRunner.DecodeScreenText(machine.Memory, roms);
            writer.WriteLine("=== screen ===");
            foreach (string line in screen.Split('\n'))
            {
                if (line.Trim().Length > 0)
                {
                    writer.WriteLine($"  |{line.TrimEnd()}");
                }
            }

            // A wedged machine still services interrupts, so RAM changes and cycles
            // advance regardless. The reliable signal is concentration: a spin loop
            // puts almost every executed instruction on a handful of addresses.
            long topFour = histogram.OrderByDescending(e => e.Value).Take(4).Sum(e => e.Value);
            double concentration = samples == 0 ? 0 : (double)topFour / samples;
            writer.WriteLine($"topFourPcShare={concentration:P1}");

            bool looksHung = concentration >= 0.90;
            writer.WriteLine();
            writer.WriteLine(looksHung
                ? $"VERDICT HUNG - {concentration:P1} of instructions execute on only four addresses over {options.SampleFrames} frames"
                : $"VERDICT LIVE - {histogram.Count} distinct PCs, top-four share {concentration:P1}, ramChanged={ramChanged}");
            return looksHung ? 1 : 0;
        }

        // Method role: Summarises machine state for one report line.
        private static string DescribeState(
            string tag,
            SpectrumMachine machine,
            SpectrumBeta128Device beta,
            SpectrumBeta128DiskController controller)
        {
            return $"STATE {tag} pc={machine.Cpu.PC:X4} sp={machine.Cpu.SP:X4} cyc={machine.Cpu.Cyc} " +
                $"{DescribeController(controller, beta, machine.Memory)}";
        }

        // Method role: Summarises Beta/FDC state shared by several report lines.
        private static string DescribeController(
            SpectrumBeta128DiskController controller,
            SpectrumBeta128Device beta,
            SpectrumMemory memory)
        {
            return $"betaPaged={beta.IsPaged} romBank={memory.CurrentRomBank} " +
                $"lastCmd={controller.LastCommand:X2} cmds={controller.CommandCount} transfer={controller.TransferActive}";
        }

        // Method role: Names the memory space an address is executing from.
        private static string DescribeAddressSpace(ushort pc, SpectrumBeta128Device beta, SpectrumMemory memory)
        {
            if (pc >= 0x4000)
            {
                return "ram";
            }

            return beta.IsPaged ? "trdos" : $"rom{memory.CurrentRomBank}";
        }

        // Method role: Implements hash ram as part of the ZedExEss application.
        private static uint HashRam(SpectrumMemory memory)
        {
            uint hash = 2166136261u;
            for (int address = 0x4000; address <= 0xFFFF; address++)
            {
                hash ^= memory.ReadDirect((ushort)address);
                hash *= 16777619u;
            }

            return hash;
        }

        // Method role: Implements run frames as part of the ZedExEss application.
        private static void RunFrames(SpectrumMachine machine, int frameCount)
        {
            for (int frame = 0; frame < frameCount; frame++)
            {
                machine.Emulator.RunFrame(presentFrame: false);
            }
        }

        // Method role: Implements press chord as part of the ZedExEss application.
        private static void PressChord(SpectrumMachine machine, ReadOnlySpan<SpectrumKey> keys)
        {
            foreach (SpectrumKey key in keys)
            {
                machine.Keyboard.SetKeyState(key, pressed: true);
            }

            RunFrames(machine, 10);

            foreach (SpectrumKey key in keys)
            {
                machine.Keyboard.SetKeyState(key, pressed: false);
            }

            RunFrames(machine, 10);
        }

        // Method role: Maps a typed character onto its Spectrum matrix key.
        private static bool TryMapKey(char character, out SpectrumKey key)
        {
            switch (char.ToUpperInvariant(character))
            {
                case 'A': key = SpectrumKey.A; return true;
                case 'B': key = SpectrumKey.B; return true;
                case 'C': key = SpectrumKey.C; return true;
                case 'D': key = SpectrumKey.D; return true;
                case 'E': key = SpectrumKey.E; return true;
                case 'F': key = SpectrumKey.F; return true;
                case 'G': key = SpectrumKey.G; return true;
                case 'H': key = SpectrumKey.H; return true;
                case 'I': key = SpectrumKey.I; return true;
                case 'J': key = SpectrumKey.J; return true;
                case 'K': key = SpectrumKey.K; return true;
                case 'L': key = SpectrumKey.L; return true;
                case 'M': key = SpectrumKey.M; return true;
                case 'N': key = SpectrumKey.N; return true;
                case 'O': key = SpectrumKey.O; return true;
                case 'P': key = SpectrumKey.P; return true;
                case 'Q': key = SpectrumKey.Q; return true;
                case 'R': key = SpectrumKey.R; return true;
                case 'S': key = SpectrumKey.S; return true;
                case 'T': key = SpectrumKey.T; return true;
                case 'U': key = SpectrumKey.U; return true;
                case 'V': key = SpectrumKey.V; return true;
                case 'W': key = SpectrumKey.W; return true;
                case 'X': key = SpectrumKey.X; return true;
                case 'Y': key = SpectrumKey.Y; return true;
                case 'Z': key = SpectrumKey.Z; return true;
                default: key = default; return false;
            }
        }

        // Method role: Loads roms into the relevant emulated state within the ZedExEss application.
        private static RomSet LoadRoms(SpectrumModel model, string root)
        {
            string romRoot = Path.Combine(root, "ROMs");
            return model switch
            {
                SpectrumModel.Pentagon128 => RomSet.LoadFromCombinedFile(
                    Path.Combine(romRoot, "pentagon.rom"), SpectrumModelTraits.RomBankCount(model)),
                SpectrumModel.Scorpion256 => RomSet.LoadFromCombinedFile(
                    Path.Combine(romRoot, "scorpion.rom"), SpectrumModelTraits.RomBankCount(model)),
                _ => throw new NotSupportedException($"TR-DOS diagnostic does not support {model}.")
            };
        }

        // Method role: Loads tr dos rom into the relevant emulated state within the ZedExEss application.
        private static byte[] LoadTrDosRom(SpectrumModel model, string root, RomSet modelRoms)
        {
            if (model == SpectrumModel.Scorpion256)
            {
                // The Scorpion's own TR-DOS lives in bank 3 of its combined ROM.
                return modelRoms.GetBank(3).Span.ToArray();
            }

            string trdosPath = Path.Combine(root, "ROMs", "trdos.rom");
            if (File.Exists(trdosPath))
            {
                byte[] candidate = File.ReadAllBytes(trdosPath);
                if (candidate.Length == 16 * 1024)
                {
                    return candidate;
                }
            }

            string scorpionPath = Path.Combine(root, "ROMs", "scorpion.rom");
            if (File.Exists(scorpionPath))
            {
                byte[] combined = File.ReadAllBytes(scorpionPath);
                if (combined.Length >= 4 * 16 * 1024)
                {
                    byte[] trdos = new byte[16 * 1024];
                    Buffer.BlockCopy(combined, 3 * 16 * 1024, trdos, 0, trdos.Length);
                    return trdos;
                }
            }

            throw new FileNotFoundException("Could not find a usable TR-DOS ROM.");
        }

        // Method role: Implements find repository root as part of the ZedExEss application.
        private static string? FindRepositoryRoot()
        {
            foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                if (Directory.Exists(Path.Combine(start, "ROMs")))
                {
                    return start;
                }

                var directory = new DirectoryInfo(start);
                while (directory != null)
                {
                    string candidate = Path.Combine(directory.FullName, "ZedExEss", "ROMs");
                    if (Directory.Exists(candidate))
                    {
                        return Path.Combine(directory.FullName, "ZedExEss");
                    }

                    directory = directory.Parent;
                }
            }

            return null;
        }
    }
}
