using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System;
using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Disk.Beta;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Spectrum.Tape;
using ZedExEss.Spectrum.Video;
using ZedExEss.Z80CPU;

namespace ZedExEss.Diagnostics
{
    /// <summary>ROM location and boot duration settings for clone/disk verification.</summary>
    public sealed class PentagonVerificationOptions
    {
        public string? OutputPath { get; init; }
        public int BootFrames { get; init; } = 10;
    }
    /// <summary>
    /// Integration checks for Pentagon/Scorpion paging, Beta 128 automapping and TR-DOS media.
    /// </summary>
    /// <remarks>
    /// This intentionally builds the same memory/port/device graph as the UI because most clone
    /// failures arise from interactions between paging and disk-ROM automapping.
    /// </remarks>
    public static class PentagonVerificationRunner
    {
        private const int RamBankSize = 16 * 1024;
        public static int Run(PentagonVerificationOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            string outputPath = ResolveOutputPath(options.OutputPath);
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
            var log = new VerificationLog(writer);

            try
            {
                string root = FindRepositoryRoot() ?? Directory.GetCurrentDirectory();
                RomSet? pentagonRoms = null;
                RomSet? scorpionRoms = null;
                byte[]? trdosRom = null;
                byte[]? scorpionTrdosRom = null;

                log.WriteLine("Pentagon/TR-DOS headless verification");
                log.WriteLine($"Repository: {root}");
                log.WriteLine($"Output:     {outputPath}");
                log.WriteLine(string.Empty);

                log.Check("Load Pentagon ROM banks", () =>
                {
                    pentagonRoms = RomSet.LoadFromCombinedFile(
                        Path.Combine(root, "ROMs", "pentagon.rom"),
                        SpectrumModelTraits.RomBankCount(SpectrumModel.Pentagon128));
                    Require(pentagonRoms.BankCount == 2, "Pentagon must expose two 16 KB ROM banks.");
                });

                log.Check("Load Scorpion ROM banks", () =>
                {
                    scorpionRoms = RomSet.LoadFromCombinedFile(
                        Path.Combine(root, "ROMs", "scorpion.rom"),
                        SpectrumModelTraits.RomBankCount(SpectrumModel.Scorpion256));
                    Require(scorpionRoms.BankCount == 4, "Scorpion must expose four 16 KB ROM banks.");
                });

                log.Check("Load TR-DOS ROM", () =>
                {
                    trdosRom = LoadTrDosRom(root, pentagonRoms ?? throw new InvalidOperationException("Pentagon ROM must be loaded before TR-DOS ROM selection."));
                    Require(trdosRom.Length == 16 * 1024, "TR-DOS ROM must be exactly 16 KB.");
                });

                if (pentagonRoms == null || trdosRom == null)
                {
                    return 1;
                }

                RomSet roms = pentagonRoms;
                byte[] betaRom = trdosRom;
                scorpionTrdosRom = scorpionRoms?.GetBank(3).ToArray();

                log.Check("Pentagon timing profile", VerifyPentagonTiming);
                log.Check("Pentagon I/O write phase", () => VerifyPentagonIoWritePhase(roms));
                log.Check("#7FFD RAM and screen-bank paging", () => Verify128Paging(roms));
                log.Check("AY register port path", VerifyAyPorts);
                log.Check("TR-DOS automap ROM path", () => VerifyTrDosAutomap(roms, betaRom));
                log.Check("Beta 128 sector read/write path", () => VerifyBetaDiskController(betaRom, outputDirectory ?? root));
                log.Check("TR-DOS catalogue writeback path", () => VerifyTrDosCatalogueWriteback(betaRom, outputDirectory ?? root));
                log.Check("Pentagon boot executes frames", () => VerifyBootRuns(SpectrumModel.Pentagon128, roms, betaRom, options.BootFrames));
                log.Check("Scorpion #7FFD/#1FFD paging", () => VerifyScorpionPaging(scorpionRoms ?? throw new InvalidOperationException("Scorpion ROM must be loaded before paging verification.")));
                log.Check("Scorpion built-in TR-DOS ROMCS", () => VerifyScorpionBuiltInTrDos(
                    scorpionRoms ?? throw new InvalidOperationException("Scorpion ROM must be loaded before ROMCS verification.")));
                log.Check("Scorpion Beta/Kempston port ownership", () => VerifyScorpionBetaKempstonConflict(
                    scorpionRoms ?? throw new InvalidOperationException("Scorpion ROM must be loaded before port verification.")));
                log.Check("Pentagon/Scorpion full ULA port decode", VerifyCloneUlaPortDecode);
                log.Check("Scorpion frame timing and open bus", VerifyScorpionTimingAndOpenBus);
                log.Check("Scorpion even-T-state M1 alignment", VerifyScorpionM1Alignment);
                log.Check("Scorpion four-T-state border latch", VerifyScorpionBorderLatch);
                log.Check("Scorpion AY clock, ports and mono routing", VerifyScorpionAyRouting);
                log.Check("Scorpion boot executes frames", () => VerifyBootRuns(SpectrumModel.Scorpion256, scorpionRoms ?? throw new InvalidOperationException("Scorpion ROM must be loaded before boot verification."), scorpionTrdosRom ?? throw new InvalidOperationException("Scorpion TR-DOS ROM must be loaded before boot verification."), options.BootFrames));
                log.Check("Scorpion ROM menu enters 128 BASIC", () => VerifyScorpion128BasicBoot(
                    scorpionRoms ?? throw new InvalidOperationException("Scorpion ROM must be loaded before 128 BASIC verification."),
                    scorpionTrdosRom ?? throw new InvalidOperationException("Scorpion TR-DOS ROM must be loaded before 128 BASIC verification.")));
                log.Check("Scorpion 128 TR-DOS reports no-media loading error", () => VerifyScorpion128TrDosNoDisk(
                    scorpionRoms ?? throw new InvalidOperationException("Scorpion ROM must be loaded before TR-DOS verification."),
                    scorpionTrdosRom ?? throw new InvalidOperationException("Scorpion TR-DOS ROM must be loaded before TR-DOS verification.")));
                log.Check("Scorpion 128 TR-DOS reaches A prompt on a non-boot disk", () => VerifyScorpion128TrDosPrompt(
                    scorpionRoms ?? throw new InvalidOperationException("Scorpion ROM must be loaded before TR-DOS prompt verification."),
                    scorpionTrdosRom ?? throw new InvalidOperationException("Scorpion TR-DOS ROM must be loaded before TR-DOS prompt verification."),
                    outputDirectory ?? root));

                string anamnesiPath = Path.Combine(root, "bin", "Debug", "net9.0-windows", "Games", "ANAMNESI.TRD");
                if (File.Exists(anamnesiPath))
                {
                    log.Check("Scorpion 128 TR-DOS media probe completes", () => VerifyScorpion128TrDosWithDisk(
                        scorpionRoms ?? throw new InvalidOperationException("Scorpion ROM must be loaded before media-probe verification."),
                        scorpionTrdosRom ?? throw new InvalidOperationException("Scorpion TR-DOS ROM must be loaded before media-probe verification."),
                        anamnesiPath));
                    log.Check("Scorpion ANAMNESI direct loader completes", () => VerifyScorpionDirectDiskLoader(
                        scorpionRoms ?? throw new InvalidOperationException("Scorpion ROM must be loaded before direct-loader verification."),
                        scorpionTrdosRom ?? throw new InvalidOperationException("Scorpion TR-DOS ROM must be loaded before direct-loader verification."),
                        anamnesiPath));
                }

                string anamorigPath = Path.Combine(root, "bin", "Debug", "net9.0-windows", "Games", "ANAMORIG.TRD");
                if (File.Exists(anamorigPath))
                {
                    log.Check("Scorpion 128 TR-DOS starts an autoboot disk", () => VerifyScorpion128TrDosAutoboot(
                        scorpionRoms ?? throw new InvalidOperationException("Scorpion ROM must be loaded before autoboot verification."),
                        scorpionTrdosRom ?? throw new InvalidOperationException("Scorpion TR-DOS ROM must be loaded before autoboot verification."),
                        anamorigPath));
                    log.Check("Scorpion ANAMORIG raster demo executes", () => VerifyScorpionRasterDemo(
                        scorpionRoms ?? throw new InvalidOperationException("Scorpion ROM must be loaded before raster-demo verification."),
                        scorpionTrdosRom ?? throw new InvalidOperationException("Scorpion TR-DOS ROM must be loaded before raster-demo verification."),
                        anamorigPath));
                }

                log.WriteLine(string.Empty);
                log.WriteLine(log.Failed == 0
                    ? "Result: PASS"
                    : $"Result: FAIL ({log.Failed.ToString(CultureInfo.InvariantCulture)} failed checks)");

                return log.Failed == 0 ? 0 : 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                log.WriteLine($"Error: {ex.Message}");
                Debug.WriteLine(ex.ToString());
                return 3;
            }
        }
        private static void VerifyPentagonTiming()
        {
            SpectrumTimingModel timing = SpectrumTimingModel.ForModel(SpectrumModel.Pentagon128);
            Require(timing.TstatesPerLine == 224, "Pentagon line length should be 224 T-states.");
            Require(timing.LinesPerFrame == 320, "Pentagon frame should be 320 lines.");
            Require(timing.TstatesPerFrame == 71680, "Pentagon frame should be 71680 T-states.");
            Require(timing.LeftBorderTstates == 36, "Pentagon physical left border should be 36 T-states.");
            Require(timing.DisplayStartTstate == 69, "Pentagon rendered display should start at T-state 69 on each display line.");
            Require(timing.TopLeftPixelTstate == 17989, "Pentagon top-left paper pixel should be 17989 T-states after INT.");
            Require(timing.IoWritesLatchAtEndOfCycle, "Pentagon I/O writes should latch at the end of the four-T-state bus cycle.");
            Require(SpectrumModelTraits.CpuClockHz(SpectrumModel.Pentagon128) == 3584000, "Pentagon CPU clock should be 3.584 MHz.");
            Require(!timing.FloatingBusEnabled, "Pentagon should not expose the Spectrum floating bus model.");

            SpectrumUlaTiming ula = SpectrumUlaTiming.ForModel(SpectrumModel.Pentagon128);
            Require(ula.DisplayStartTstate == 69, "Pentagon ULA display should start at T-state 69 in the rendered frame.");
            Require(ula.DisplayFetchAdvanceTstates == 2, "Pentagon ULA display fetch should use the normal 2 T-state lead.");
            Require(ula.VisibleFirstLine == ula.FirstDisplayLine - 48, "Pentagon output should begin 48 lines before the paper area.");
            Require(ula.FrameHeight == 288, "Rendered output should contain 48 top-border, 192 paper and 48 bottom-border lines.");
            Require(ula.BorderLeftPixels == 32, "Pentagon rendered left border should remain 32 pixels wide.");
            Require(ula.DisplayTstates == 128, "Pentagon paper width should be 128 T-states.");

            VerifyTimingProfile(SpectrumModel.Spectrum16K, 224, 312, 14336, 32, 24);
            VerifyTimingProfile(SpectrumModel.Spectrum48K, 224, 312, 14336, 32, 24);
            VerifyTimingProfile(SpectrumModel.Spectrum128K, 228, 311, 14362, 36, 26);
            VerifyTimingProfile(SpectrumModel.SpectrumPlus2, 228, 311, 14362, 36, 26);
            VerifyTimingProfile(SpectrumModel.SpectrumPlus2A, 228, 311, 14365, 32, 23);
            VerifyTimingProfile(SpectrumModel.SpectrumPlus3, 228, 311, 14365, 32, 23);
            VerifyTimingProfile(SpectrumModel.Pentagon128, 224, 320, 17989, 36, 0);
            VerifyTimingProfile(SpectrumModel.Scorpion256, 224, 312, 14336, 36, 24);
        }
        private static void VerifyPentagonIoWritePhase(RomSet roms)
        {
            var memory = new SpectrumMemory(SpectrumModel.Pentagon128, roms);
            var ports = new SpectrumPortBus(SpectrumModel.Pentagon128, contendedPages: memory);
            var cpu = new Z80(memory, ports);
            var probe = new PortWriteProbe(() => cpu.Cyc);
            ports.AddDevice(probe);
            cpu.Z80Init();

            SpectrumTimingModel timing = SpectrumTimingModel.ForModel(SpectrumModel.Pentagon128);
            var contention = SpectrumContentionProfile.Create(SpectrumModel.Pentagon128);
            cpu.ConfigureIoContention(true, timing.IoWritesLatchAtEndOfCycle);
            memory.ConfigureTiming(cpu, contention);
            ports.ConfigureTiming(cpu, contention, memory);

            memory.WriteDirect(0x8000, 0xD3); // OUT (#FE),A
            memory.WriteDirect(0x8001, 0xFE);
            cpu.PC = 0x8000;
            ulong start = cpu.Cyc;
            cpu.Z80Step();

            Require(cpu.Cyc - start == 11, "OUT (n),A should retain its 11-T-state instruction length.");
            Require(probe.WriteTstate == cpu.Cyc, "Pentagon port write should become visible at the end of the I/O cycle.");
        }
        private static void VerifyTimingProfile(
            SpectrumModel model,
            int tstatesPerLine,
            int linesPerFrame,
            int topLeftPixelTstate,
            int interruptPulseTstates,
            int interruptAssertOffsetTstates)
        {
            SpectrumTimingModel timing = SpectrumTimingModel.ForModel(model);
            SpectrumUlaTiming ula = SpectrumUlaTiming.ForModel(model);

            Require(timing.TstatesPerLine == tstatesPerLine, $"{model} line length differs from its machine profile.");
            Require(timing.LinesPerFrame == linesPerFrame, $"{model} frame height differs from its machine profile.");
            Require(timing.TopLeftPixelTstate == topLeftPixelTstate, $"{model} paper start differs from its machine profile.");
            Require(timing.InterruptPulseTstates == interruptPulseTstates, $"{model} INT pulse differs from its machine profile.");
            Require(timing.InterruptAssertOffsetTstates == interruptAssertOffsetTstates, $"{model} INT position is inconsistent with its physical frame geometry.");
            Require(
                timing.DisplayBorderHeightLines * timing.TstatesPerLine + timing.DisplayStartTstate ==
                timing.InterruptAssertOffsetTstates + timing.TopLeftPixelTstate,
                $"{model} physical paper position is inconsistent with its INT-relative timing.");
            Require(ula.FrameHeight == 288, $"{model} rendered viewport should be 288 lines high.");
            Require(ula.VisibleFirstLine == ula.FirstDisplayLine - SpectrumUlaTiming.VisibleTopBorderLines, $"{model} viewport should begin 48 lines above paper.");
        }
        private static void Verify128Paging(RomSet roms)
        {
            var memory = new SpectrumMemory(SpectrumModel.Pentagon128, roms);
            for (int bank = 0; bank < 8; bank++)
            {
                byte[] data = new byte[RamBankSize];
                Array.Fill(data, (byte)(0xA0 + bank));
                memory.LoadRamBank(bank, data);
            }

            memory.WritePort7FFD(0x00);
            Require(memory.ReadDirect(0xC000) == 0xA0, "Bank 0 should be visible at C000h after reset paging.");
            Require(memory.ReadScreen(0x4000) == 0xA5, "Primary screen should read from RAM bank 5.");

            memory.WritePort7FFD(0x07);
            Require(memory.ReadDirect(0xC000) == 0xA7, "Bank 7 should page into C000h.");

            memory.WritePort7FFD(0x08);
            Require(memory.ReadScreen(0x4000) == 0xA7, "Alternate screen should read from RAM bank 7.");

            int probe = FindFirstDifference(roms.GetBank(0).Span, roms.GetBank(1).Span);
            Require(probe >= 0, "Pentagon ROM banks must differ for ROM-bank paging verification.");
            memory.WritePort7FFD(0x10);
            Require(memory.ReadDirect((ushort)probe) == roms.GetBank(1).Span[probe], "ROM bank 1 should be visible after bit 4 of #7FFD is set.");
        }
        private static void VerifyScorpionPaging(RomSet roms)
        {
            var memory = new SpectrumMemory(SpectrumModel.Scorpion256, roms);
            var paging = new SpectrumPagingDevice(memory, SpectrumPagingPortMode.Scorpion);
            for (int bank = 0; bank < 16; bank++)
            {
                byte[] data = new byte[RamBankSize];
                Array.Fill(data, (byte)(0xB0 + bank));
                memory.LoadRamBank(bank, data);
            }

            Require(paging.HandlesPort(0x7FFD), "Scorpion should decode #7FFD through the +3-style A15/A14/A1 mask.");
            Require(paging.HandlesPort(0x5FFD), "Scorpion should accept the documented #7FFD mirror decode.");
            Require(paging.HandlesPort(0x1FFD), "Scorpion should decode the secondary #1FFD paging port.");
            Require(!paging.HandlesPort(0x3FFD), "#3FFD must not alias the Scorpion #1FFD latch.");

            paging.Write(0x7FFD, 0x07);
            Require(memory.ReadDirect(0xC000) == 0xB7, "Scorpion should page RAM bank 7 at C000h via #7FFD.");

            paging.Write(0x1FFD, 0x10);
            Require(memory.ReadDirect(0xC000) == 0xBF, "Scorpion #1FFD bit 4 should select RAM banks 8-15.");

            paging.Write(0x7FFD, 0x08);
            Require(memory.ReadScreen(0x4000) == 0xB7, "Scorpion alternate screen should read from RAM bank 7.");

            int rom2Probe = FindFirstDifference(roms.GetBank(0).Span, roms.GetBank(2).Span);
            Require(rom2Probe >= 0, "Scorpion ROM bank 2 must differ from ROM bank 0 for paging verification.");
            paging.Write(0x1FFD, 0x02);
            Require(memory.CurrentRomBank == 2, "Scorpion #1FFD bit 1 should select ROM bank 2.");
            Require(memory.ReadDirect((ushort)rom2Probe) == roms.GetBank(2).Span[rom2Probe], "Scorpion ROM bank 2 should be visible at 0000h.");

            paging.Write(0x1FFD, 0x01);
            Require(memory.ReadDirect(0x0000) == 0xB0, "Scorpion #1FFD bit 0 should map RAM bank 0 at 0000h.");
            memory.WriteDirect(0x0000, 0x5A);
            Require(memory.ReadDirect(0x0000) == 0x5A, "Scorpion low-memory RAM mapping should be writable.");

            memory.Reset();
            paging.Write(0x7FFD, 0x23); // Bank 3 plus the one-way paging lock.
            paging.Write(0x1FFD, 0x10);
            paging.Write(0x7FFD, 0x07);
            Require(memory.ReadDirect(0xC000) == 0xB3, "Scorpion paging lock should reject later #7FFD and #1FFD writes until reset.");
        }
        private static void VerifyScorpionBuiltInTrDos(RomSet roms)
        {
            byte[] builtInTrDos = roms.GetBank(3).ToArray();
            var memory = new SpectrumMemory(SpectrumModel.Scorpion256, roms);
            var beta = new SpectrumBeta128Device(builtInTrDos);
            memory.ConfigureBeta128(beta);

            // Scorpion bank 3 is wired to the built-in Beta interface's ROMCS input. The
            // ordinary paging equations can select ROMs 0, 1 and 2, but never bank 3.
            memory.WritePort7FFD(0x10);
            memory.WritePort1FFD(0x02);
            Require(memory.CurrentRomBank == 2, "Scorpion service ROM should be primary ROM bank 2, not the TR-DOS bank.");
            memory.WritePort1FFD(0x00);
            Require(memory.CurrentRomBank == 1, "Scorpion #7FFD bit 4 should restore primary ROM bank 1.");

            int probe = FindFirstDifference(roms.GetBank(1).Span, builtInTrDos);
            Require(probe >= 0, "Scorpion ROM bank 1 and built-in TR-DOS bank 3 must differ.");
            Require(memory.ReadDirect((ushort)probe) == roms.GetBank(1).Span[probe], "TR-DOS bank 3 must not appear through ordinary ROM paging.");

            memory.FetchOpcode(0x3D00);
            Require(beta.IsPaged, "A 3Dxx opcode fetch from Scorpion ROM 1 should assert built-in Beta ROMCS.");
            Require(memory.ReadDirect((ushort)probe) == builtInTrDos[probe], "Built-in Scorpion ROM bank 3 should be visible while Beta ROMCS is asserted.");

            memory.FetchOpcode(0x4000);
            Require(!beta.IsPaged, "The first RAM opcode fetch should release late Scorpion Beta ROMCS.");
            Require(memory.ReadDirect((ushort)probe) == roms.GetBank(1).Span[probe], "Primary ROM bank 1 should return after Beta ROMCS is released.");

            beta.Reset();
            memory.Reset();
            memory.FetchOpcode(0x3D00);
            Require(!beta.IsPaged, "A 3Dxx fetch from Scorpion ROM 0 must not page the Beta ROM.");

            memory.WritePort1FFD(0x02);
            memory.FetchOpcode(0x3D00);
            Require(beta.IsPaged, "Late Beta decode treats Scorpion service ROM 2 as a ROMCS-capable ROM.");
        }
        private static void VerifyScorpionBetaKempstonConflict(RomSet roms)
        {
            var memory = new SpectrumMemory(SpectrumModel.Scorpion256, roms);
            var beta = new SpectrumBeta128Device(roms.GetBank(3).Span);
            var controller = new SpectrumBeta128DiskController(beta);
            var keyboard = new SpectrumKeyboard();
            var joystick = new SpectrumJoystickDevice(keyboard, useDedicatedKempstonPort: true)
            {
                Type = SpectrumJoystickType.Kempston
            };
            joystick.SetButtonState(SpectrumJoystickButton.Fire, pressed: true);

            var ports = new SpectrumPortBus(SpectrumModel.Scorpion256, contendedPages: memory);
            ports.AddDevice(controller);
            ports.AddDevice(joystick);

            Require(ports.ReadUncontended(0x001F) == 0x10, "Kempston should own #1F while Scorpion Beta ROMCS is inactive.");
            Require(!joystick.HandlesPort(0x005F), "Scorpion Kempston must not claim the Beta sector-register port #5F.");
            Require(ports.ReadUncontended(0x005F) == 0xFF, "An inactive Beta interface must leave #5F open instead of returning joystick bits.");

            beta.BeforeOpcodeFetch(0x3D00, allowRomTrap: true);
            Require(controller.HandlesPort(0x001F), "Beta status port should become visible with ROMCS active.");
            Require(ports.ReadUncontended(0x001F) == 0x86, "Active Beta status must take priority over the overlapping Kempston #1F decode.");
        }
        private static void VerifyCloneUlaPortDecode()
        {
            VerifyCloneUlaPortDecode(SpectrumModel.Pentagon128);
            VerifyCloneUlaPortDecode(SpectrumModel.Scorpion256);

            // Preserve the Sinclair partial decode: a non-FE even port must still reach
            // the ULA on a 48K machine.
            var sinclairMemory = new SpectrumMemory(SpectrumModel.Spectrum48K, RomSet.CreateBlank(1));
            var sinclairRenderer = new SpectrumUlaRenderer(SpectrumModel.Spectrum48K, sinclairMemory);
            var sinclairUla = new SpectrumUla(SpectrumModel.Spectrum48K, sinclairRenderer);
            var sinclairPorts = new SpectrumPortBus(SpectrumModel.Spectrum48K, contendedPages: sinclairMemory);
            sinclairPorts.AddDevice(sinclairUla);
            sinclairPorts.WriteUncontended(0x00FC, 0x03);
            Require(sinclairRenderer.BorderColorIndex == 3, "Sinclair ULA should retain its every-even-port partial decode.");
        }
        private static void VerifyCloneUlaPortDecode(SpectrumModel model)
        {
            var memory = new SpectrumMemory(model, RomSet.CreateBlank(SpectrumModelTraits.RomBankCount(model)));
            var renderer = new SpectrumUlaRenderer(model, memory);
            var ula = new SpectrumUla(model, renderer);
            var ports = new SpectrumPortBus(model, contendedPages: memory);
            ports.AddDevice(ula);

            Require(ula.HandlesPort(0x00FE), $"{model} ULA should respond at xxFE.");
            Require(ula.HandlesPort(0x7FFE), $"{model} ULA should ignore the high address byte at xxFE.");
            Require(!ula.HandlesPort(0x00FC), $"{model} ULA should not respond to a different even low byte.");

            ports.WriteUncontended(0x00FC, 0x03);
            Require(renderer.BorderColorIndex == 0, $"{model} non-FE even write must not alter the border.");
            ports.WriteUncontended(0x7FFE, 0x05);
            Require(renderer.BorderColorIndex == 5, $"{model} xxFE write should alter the border.");
        }
        private static void VerifyScorpionTimingAndOpenBus()
        {
            const SpectrumModel Model = SpectrumModel.Scorpion256;
            SpectrumTimingModel timing = SpectrumTimingModel.ForModel(Model);
            SpectrumUlaTiming ula = SpectrumUlaTiming.ForModel(Model);

            // 24+128+32+40 = 224 T-states per line and 48+192+48+24 = 312 lines.
            Require(SpectrumModelTraits.CpuClockHz(Model) == 3500000, "Scorpion CPU clock should be 3.5 MHz.");
            Require(SpectrumAudioTiming.AyClockHz(Model) == 1750000, "Scorpion AY clock should be 1.75 MHz.");
            Require(timing.TstatesPerLine == 224, "Scorpion line should contain 224 T-states.");
            Require(timing.LinesPerFrame == 312, "Scorpion frame should contain 312 lines.");
            Require(timing.TstatesPerFrame == 69888, "Scorpion frame should contain 69888 T-states.");
            Require(timing.LeftBorderTstates == 24, "Scorpion paper should start after 24 left-border T-states.");
            Require(timing.HorizontalScreenTstates == 128, "Scorpion paper should occupy 128 T-states per line.");
            Require(timing.TopLeftPixelTstate == 14336, "Scorpion top-left paper pixel should occur 14336 T-states after INT.");
            Require(timing.InterruptPulseTstates == 36, "Scorpion INT pulse should last 36 T-states.");
            Require(!timing.FloatingBusEnabled, "Scorpion unattached ports should expose an idle FF bus, not ULA fetch data.");
            Require(ula.FirstDisplayLine == 64, "Scorpion first paper line should be physical frame line 64.");
            Require(ula.VisibleFirstLine == 16, "The 48-line host crop should begin at physical frame line 16.");
            Require(ula.DisplayStartTstate == 24, "Scorpion paper should begin at line T-state 24.");

            var contention = SpectrumContentionProfile.Create(Model);
            for (ulong tstate = 0; tstate < (ulong)timing.TstatesPerFrame; tstate++)
            {
                Require(contention.GetMemoryDelay(tstate) == 0, "Scorpion RAM must remain uncontended.");
                Require(contention.GetNoMreqDelay(tstate) == 0, "Scorpion no-MREQ cycles must remain uncontended.");
            }

            var memory = new SpectrumMemory(Model, RomSet.CreateBlank(SpectrumModelTraits.RomBankCount(Model)));
            var floatingBus = new SpectrumFloatingBus(Model, memory);
            Require(floatingBus.Read(0xFFFF, (ulong)timing.TopLeftPixelTstate + 2) == 0xFF,
                "Scorpion open-bus reads must return FF even while the ULA is fetching screen data.");
        }
        private static void VerifyScorpionM1Alignment()
        {
            const SpectrumModel Model = SpectrumModel.Scorpion256;
            Require(SpectrumModelTraits.AlignsM1ToEvenTstates(Model),
                "Scorpion must advertise its even-T-state M1 bus alignment.");
            Require(!SpectrumModelTraits.AlignsM1ToEvenTstates(SpectrumModel.Pentagon128),
                "Pentagon must not inherit the Scorpion-only M1 alignment rule.");

            var memory = new SpectrumMemory(Model, RomSet.CreateBlank(SpectrumModelTraits.RomBankCount(Model)));
            var ports = new SpectrumPortBus(Model, contendedPages: memory);
            var cpu = new Z80(memory, ports);
            cpu.Z80Init();
            cpu.ConfigureM1Alignment(true);

            cpu.PC = 0x0000; // Blank ROM contains NOP.
            cpu.Cyc = 1;
            cpu.Z80Step();
            Require(cpu.Cyc == 5, "Scorpion must not align an M1 fetch in the low 16K ROM window.");

            memory.WriteDirect(0xC000, 0x00);
            cpu.PC = 0xC000;
            cpu.Cyc = 1;
            cpu.Z80Step();
            Require(cpu.Cyc == 6,
                "An odd Scorpion M1 fetch in RAM must receive one wait before the four-T fetch.");

            // Prefix bytes are independent M1 cycles and use the address of each
            // individual fetch when applying the RAM-address qualification.
            memory.WriteDirect(0xC001, 0xDD);
            memory.WriteDirect(0xC002, 0x00); // Ignored DD prefix followed by NOP.
            cpu.PC = 0xC001;
            cpu.Cyc = 7;
            ulong prefixStart = cpu.Cyc;
            cpu.Z80Step();
            Require(cpu.Cyc - prefixStart == 9,
                "An odd first prefix M1 in RAM should add exactly one wait.");
        }

        private static void VerifyScorpionBorderLatch()
        {
            const SpectrumModel Model = SpectrumModel.Scorpion256;
            var memory = new SpectrumMemory(Model, RomSet.CreateBlank(SpectrumModelTraits.RomBankCount(Model)));
            var renderer = new SpectrumUlaRenderer(Model, memory);
            var ula = new SpectrumUla(Model, renderer);
            var ports = new SpectrumPortBus(Model, contendedPages: memory);
            var cpu = new Z80(memory, ports);
            cpu.Z80Init();
            ports.ConfigureTiming(cpu, SpectrumContentionProfile.Create(Model), memory);
            ports.AddDevice(ula);

            cpu.Cyc = 101;
            Require(ports.TryWriteAtStartOfIoCycle(0x00FE, 0x03),
                "The Scorpion bus should accept an FE write at the beginning of its I/O cycle.");

            Require(ports.TryPeekPendingWrite(out ulong latchAt),
                "A Scorpion FE write should remain pending until the gate-array latch edge.");
            Require(latchAt == ((cpu.Cyc + 3UL) & ~3UL),
                "Scorpion FE writes should latch on the next four-T-state edge selected at I/O-cycle start.");
            Require((latchAt & 3UL) == 0,
                "Successive Scorpion border latch groups must remain on the four-T-state hardware grid.");

            ports.FlushPendingWrites(latchAt - 1);
            Require(renderer.BorderColorIndex == 0,
                "The border colour must not become visible before the latch edge.");
            ports.FlushPendingWrites(latchAt);
            Require(renderer.BorderColorIndex == 3,
                "The border colour should become visible exactly on the latch edge.");
        }
        private static void VerifyScorpionAyRouting()
        {
            const SpectrumModel Model = SpectrumModel.Scorpion256;
            var ay = new AY38912(SpectrumAudioTiming.AyClockHz(Model), SpectrumAudioTiming.DefaultSampleRate, outputAmplitude: 9000);
            var device = new SpectrumAyDevice(ay);
            var ports = new SpectrumPortBus(Model);
            ports.AddDevice(device);

            ports.WriteUncontended(0xFFFD, 0x08);
            ports.WriteUncontended(0xBFFD, 0x0F);
            Require(ports.ReadUncontended(0xFFFD) == 0x0F, "Scorpion AY should use the standard FFFD/BFFD register path.");

            // Base Scorpion hardware exposes the AY's mono mix. 
            ports.WriteUncontended(0xFFFD, 0x07);
            ports.WriteUncontended(0xBFFD, 0x3E); // Tone A only; disable noise and channels B/C.
            short[] stereo = ay.GenerateSamples(SpectrumAudioTiming.DefaultSampleRate, 128);
            bool audible = false;
            for (int i = 0; i < stereo.Length; i += 2)
            {
                Require(stereo[i] == stereo[i + 1], "Base Scorpion AY output should be duplicated equally to both host channels.");
                audible |= stereo[i] != 0;
            }

            Require(audible, "Scorpion AY routing test should produce an audible channel-A signal.");
        }
        private static void VerifyAyPorts()
        {
            var ay = new AY38912(SpectrumAudioTiming.AyClockHz(SpectrumModel.Pentagon128), SpectrumAudioTiming.DefaultSampleRate, outputAmplitude: 13500);
            var device = new SpectrumAyDevice(ay);
            Require(device.HandlesPort(0xFFFD), "AY register-select port should be handled.");
            Require(device.HandlesPort(0xBFFD), "AY data-write port should be handled.");

            device.Write(0xFFFD, 8);
            device.Write(0xBFFD, 0x0F);
            Require(device.Read(0xFFFD) == 0x0F, "AY volume register readback should match the written value.");
        }
        private static void VerifyTrDosAutomap(RomSet roms, byte[] trdosRom)
        {
            var memory = new SpectrumMemory(SpectrumModel.Pentagon128, roms);
            var beta = new SpectrumBeta128Device(trdosRom);
            memory.ConfigureBeta128(beta);

            ReadOnlySpan<byte> mainRom = roms.GetBank(0).Span;
            int probe = FindFirstDifference(mainRom, trdosRom);
            Require(probe >= 0, "Main ROM and TR-DOS ROM must differ for automap verification.");

            Require(!beta.IsPaged, "TR-DOS ROM should start unpaged.");
            Require(memory.ReadDirect((ushort)probe) == mainRom[probe], "Main ROM should be visible before TR-DOS automap.");

            beta.BeforeOpcodeFetch(0x3D00, allowRomTrap: false);
            Require(!beta.IsPaged, "Fetching from 3D00h in the 128 editor ROM must not page TR-DOS.");
            Require(memory.ReadDirect((ushort)probe) == mainRom[probe], "Main ROM should remain visible when 128 editor ROM hits 3D00h.");

            memory.WritePort7FFD(0x10);
            Require(memory.CurrentRomBank == 1, "Pentagon ROM bank 1 should be the 48K ROM for TR-DOS automap.");

            beta.BeforeOpcodeFetch(0x3D00, allowRomTrap: true);
            Require(beta.IsPaged, "Fetching from 3D00h should page in TR-DOS.");
            Require(memory.ReadDirect((ushort)probe) == trdosRom[probe], "TR-DOS ROM should be visible after automap.");

            beta.BeforeOpcodeFetch(0x4000, allowRomTrap: true);
            Require(!beta.IsPaged, "Fetching at or above 4000h should unpage TR-DOS.");
            Require(memory.ReadDirect((ushort)probe) == roms.GetBank(1).Span[probe], "48K ROM should be visible after TR-DOS unmaps.");
        }
        private static void VerifyBetaDiskController(byte[] trdosRom, string outputDirectory)
        {
            string tempPath = Path.Combine(outputDirectory, "pentagon-verification-temp.trd");
            try
            {
                byte[] raw = new byte[80 * 2 * TrdDiskImage.SectorsPerTrack * TrdDiskImage.SectorSize];
                for (int i = 0; i < TrdDiskImage.SectorSize; i++)
                {
                    raw[i] = (byte)i;
                }

                File.WriteAllBytes(tempPath, raw);
                TrdDiskImage disk = TrdDiskImage.Load(tempPath);

                var beta = new SpectrumBeta128Device(trdosRom);
                beta.BeforeOpcodeFetch(0x3D00, allowRomTrap: true);
                var fdc = new SpectrumBeta128DiskController(beta);
                fdc.ConfigureCpuClock(SpectrumModelTraits.CpuClockHz(SpectrumModel.Pentagon128));
                fdc.InsertDisk(0, disk);

                fdc.Write(0xFF, 0x3C);
                fdc.Write(0x3F, 0x00);
                fdc.Write(0x5F, 0x01);
                fdc.SetBusTstate(0);
                fdc.Write(0x1F, 0x80);
                fdc.Read(0xFF);
                fdc.Read(0xFF);
                Require((fdc.Read(0xFF) & 0x40) != 0, "Read sector should assert DRQ in the system register.");

                for (int i = 0; i < TrdDiskImage.SectorSize; i++)
                {
                    byte value = fdc.Read(0x7F);
                    Require(value == (byte)i, $"Read sector byte {i} should match the disk image.");
                }

                fdc.Write(0x5F, 0x02);
                fdc.Write(0x1F, 0xA0);
                for (int i = 0; i < TrdDiskImage.SectorSize; i++)
                {
                    fdc.Write(0x7F, (byte)(255 - i));
                }

                Span<byte> sector = stackalloc byte[TrdDiskImage.SectorSize];
                Require(disk.TryReadSector(0, 0, 2, sector), "Written sector should be readable.");
                for (int i = 0; i < sector.Length; i++)
                {
                    Require(sector[i] == (byte)(255 - i), $"Written sector byte {i} should be persisted.");
                }

                // TR-DOS controller probes can deliberately leave a write command
                // unserviced and wait for the FD1793 LOST DATA completion interrupt.
                fdc.Write(0x5F, 0x03);
                fdc.SetBusTstate(0);
                fdc.Write(0x1F, 0xBC);
                Require((fdc.Read(0xFF) & 0x40) != 0, "Unserviced write should initially assert DRQ.");
                fdc.SetBusTstate((ulong)SpectrumModelTraits.CpuClockHz(SpectrumModel.Pentagon128) + 1);
                byte system = fdc.Read(0xFF);
                Require((system & 0x80) != 0, "Unserviced write should eventually assert INTRQ.");
                Require((system & 0x40) == 0, "Timed-out write should release DRQ.");
                byte status = fdc.Read(0x1F);
                Require((status & 0x04) != 0, "Timed-out write should report LOST DATA.");
                Require((status & 0x01) == 0, "Timed-out write should clear BUSY.");
            }
            finally
            {
                DeleteFileIfExists(tempPath);
            }
        }
        private static void VerifyTrDosCatalogueWriteback(byte[] trdosRom, string outputDirectory)
        {
            string tempPath = Path.Combine(outputDirectory, "pentagon-verification-save.trd");
            string tempSclPath = Path.Combine(outputDirectory, "pentagon-verification-save.scl");
            try
            {
                File.WriteAllBytes(tempPath, CreateBlankTrdImage());
                TrdDiskImage disk = TrdDiskImage.Load(tempPath);
                SpectrumBeta128DiskController fdc = CreateBetaDiskController(trdosRom, disk);

                Span<byte> directory = stackalloc byte[TrdDiskImage.SectorSize];
                Span<byte> diskInfo = stackalloc byte[TrdDiskImage.SectorSize];
                Span<byte> payload = stackalloc byte[TrdDiskImage.SectorSize];

                WriteTrDosDirectoryEntry(directory, "VERIFY", (byte)'B', sectorCount: 1, startSector: 0, startLogicalTrack: 1);
                WriteTrDosDiskInfo(diskInfo, fileCount: 1, firstFreeSector: 1, firstFreeLogicalTrack: 1);
                for (int i = 0; i < payload.Length; i++)
                {
                    payload[i] = (byte)(0xA5 ^ i);
                }

                WriteBetaSector(fdc, track: 0, side: 0, sectorId: 1, directory);
                WriteBetaSector(fdc, track: 0, side: 0, sectorId: 9, diskInfo);
                WriteBetaSector(fdc, track: 0, side: 1, sectorId: 1, payload);

                TrdDiskImage reloaded = TrdDiskImage.Load(tempPath);
                Span<byte> reloadedDirectory = stackalloc byte[TrdDiskImage.SectorSize];
                Span<byte> reloadedPayload = stackalloc byte[TrdDiskImage.SectorSize];
                Require(reloaded.TryReadSector(0, 0, 1, reloadedDirectory), "Reloaded TRD directory sector should be readable.");
                Require(reloaded.TryReadSector(0, 1, 1, reloadedPayload), "Reloaded TRD file data sector should be readable.");

                Require(reloadedDirectory[..16].SequenceEqual(directory[..16]), "TR-DOS directory entry should persist exactly.");
                Require(reloadedPayload.SequenceEqual(payload), "TR-DOS saved payload sector should persist exactly.");

                reloaded.ExportScl(tempSclPath);
                byte[] scl = File.ReadAllBytes(tempSclPath);
                Require(scl.Length == 9 + 14 + TrdDiskImage.SectorSize + 4, "Exported SCL should contain one one-sector file plus checksum.");
                Require(scl.AsSpan(0, 8).SequenceEqual("SINCLAIR"u8), "Exported SCL should have the SINCLAIR signature.");
                Require(scl[8] == 1, "Exported SCL should contain one file entry.");
                Require(scl.AsSpan(9, 14).SequenceEqual(directory[..14]), "Exported SCL file header should match the TR-DOS directory entry.");
                Require(scl.AsSpan(23, TrdDiskImage.SectorSize).SequenceEqual(payload), "Exported SCL file payload should match the TR-DOS data sector.");
            }
            finally
            {
                DeleteFileIfExists(tempPath);
                DeleteFileIfExists(tempSclPath);
            }
        }
        private static SpectrumBeta128DiskController CreateBetaDiskController(byte[] trdosRom, TrdDiskImage disk)
        {
            var beta = new SpectrumBeta128Device(trdosRom);
            beta.BeforeOpcodeFetch(0x3D00, allowRomTrap: true);
            var fdc = new SpectrumBeta128DiskController(beta);
            fdc.ConfigureCpuClock(SpectrumModelTraits.CpuClockHz(SpectrumModel.Pentagon128));
            fdc.InsertDisk(0, disk);
            return fdc;
        }
        private static void WriteBetaSector(SpectrumBeta128DiskController fdc, int track, int side, int sectorId, ReadOnlySpan<byte> source)
        {
            Require(source.Length >= TrdDiskImage.SectorSize, "Source sector must contain 256 bytes.");
            fdc.Write(0xFF, side == 0 ? (byte)0x3C : (byte)0x2C);
            fdc.Write(0x3F, (byte)track);
            fdc.Write(0x5F, (byte)sectorId);
            fdc.Write(0x1F, 0xA0);
            Require((fdc.Read(0xFF) & 0x40) != 0, $"Write sector should assert DRQ for track {track}, side {side}, sector {sectorId}.");

            for (int i = 0; i < TrdDiskImage.SectorSize; i++)
            {
                fdc.Write(0x7F, source[i]);
            }
        }
        private static byte[] CreateBlankTrdImage()
        {
            byte[] raw = new byte[80 * 2 * TrdDiskImage.SectorsPerTrack * TrdDiskImage.SectorSize];
            WriteTrDosDiskInfo(raw.AsSpan(8 * TrdDiskImage.SectorSize, TrdDiskImage.SectorSize), fileCount: 0, firstFreeSector: 0, firstFreeLogicalTrack: 1);
            return raw;
        }
        private static void WriteTrDosDirectoryEntry(Span<byte> directorySector, string name, byte type, byte sectorCount, byte startSector, byte startLogicalTrack)
        {
            directorySector.Clear();
            WritePaddedAscii(directorySector[..8], name);
            directorySector[8] = type;
            directorySector[9] = 0x00;
            directorySector[10] = 0x00;
            directorySector[11] = 0x00;
            directorySector[12] = 0x01;
            directorySector[13] = sectorCount;
            directorySector[14] = startSector;
            directorySector[15] = startLogicalTrack;
        }
        private static void WriteTrDosDiskInfo(Span<byte> sector, int fileCount, int firstFreeSector, int firstFreeLogicalTrack)
        {
            sector.Clear();
            sector[0xE1] = (byte)firstFreeSector;
            sector[0xE2] = (byte)firstFreeLogicalTrack;
            sector[0xE3] = 0x16;
            sector[0xE4] = (byte)fileCount;

            int totalDataSectors = ((80 * 2) - 1) * TrdDiskImage.SectorsPerTrack;
            int usedDataSectors = ((firstFreeLogicalTrack - 1) * TrdDiskImage.SectorsPerTrack) + firstFreeSector;
            int freeSectors = Math.Max(0, totalDataSectors - usedDataSectors);
            sector[0xE5] = (byte)(freeSectors & 0xFF);
            sector[0xE6] = (byte)(freeSectors >> 8);
            sector[0xE7] = 0x10;
            sector.Slice(0xEA, 9).Fill(0x20);
            WritePaddedAscii(sector.Slice(0xF5, 8), "ZEDEXESS");
        }
        private static void WritePaddedAscii(Span<byte> destination, string text)
        {
            destination.Fill(0x20);
            int count = Math.Min(destination.Length, text.Length);
            for (int i = 0; i < count; i++)
            {
                destination[i] = (byte)text[i];
            }
        }
        private static void VerifyBootRuns(SpectrumModel model, RomSet roms, byte[] trdosRom, int frames)
        {
            using var machine = CreateMachine(model, roms, trdosRom);
            RunFrames(machine, Math.Max(1, frames));

            Require(machine.Cpu.Cyc > 0, "CPU should advance while running the boot ROM.");
        }

        private static void VerifyScorpion128BasicBoot(RomSet roms, byte[] trdosRom)
        {
            using var machine = CreateMachine(SpectrumModel.Scorpion256, roms, trdosRom, SpectrumJoystickType.Kempston);
            RunFrames(machine, 200);

            uint menuFrame = HashFrame(machine.Machine.Renderer.FrameBuffer);
            Require(!machine.Beta.IsPaged, "Scorpion startup menu should not leave TR-DOS ROMCS asserted.");

            // The Scorpion menu starts on 128 TR-DOS. One cursor-down chord selects
            // 128 BASIC; the deliberately long key/release periods match a real matrix key.
            PressChord(machine, [SpectrumKey.CapsShift, SpectrumKey.D6]);
            PressChord(machine, [SpectrumKey.Enter]);
            RunFrames(machine, 100);

            ushort prog = ReadSystemVariableWord(machine.Machine.Memory, 0x5C53);
            ushort vars = ReadSystemVariableWord(machine.Machine.Memory, 0x5C4B);
            Require(machine.Machine.Memory.CurrentRomBank == 0, "128 BASIC should execute from Scorpion ROM bank 0.");
            Require(!machine.Beta.IsPaged, "128 BASIC must run with TR-DOS ROMCS released.");
            Require(prog is >= 0x5CCB and < 0xC000, "128 BASIC should initialise a sane PROG system variable.");
            Require(vars >= prog && vars < 0xC000, "128 BASIC should initialise VARS at or above PROG.");
            Require(HashFrame(machine.Machine.Renderer.FrameBuffer) != menuFrame, "Selecting 128 BASIC should leave the Scorpion startup menu.");
        }

        private static void VerifyScorpion128TrDosNoDisk(RomSet roms, byte[] trdosRom)
        {
            using var machine = CreateMachine(SpectrumModel.Scorpion256, roms, trdosRom, SpectrumJoystickType.Kempston);
            RunFrames(machine, 200);

            uint menuFrame = HashFrame(machine.Machine.Renderer.FrameBuffer);
            Require(!machine.Beta.IsPaged, "Scorpion startup menu should begin with TR-DOS ROMCS released.");

            // 128 TR-DOS is the default menu item. With no medium its ROM performs
            // the controller probe, returns to 128 BASIC and deliberately reports
            // the same tape-loading error used by the genuine firmware.
            PressChord(machine, [SpectrumKey.Enter]);
            RunFrames(machine, 400);

            Require(!machine.Beta.IsPaged, "The no-media path should release TR-DOS ROMCS before reporting the BASIC error.");
            Require(machine.Machine.Memory.CurrentRomBank == 0, "The no-media error should return to Scorpion 128 BASIC ROM bank 0.");
            Require(!machine.Controller.TransferActive, $"The no-media path should not remain in command {machine.Controller.LastCommand:X2}.");
            string screenText = DecodeScreenText(machine.Machine.Memory, roms);
            Require(screenText.Contains("R Tape loading error, 0:1", StringComparison.OrdinalIgnoreCase),
                $"The terminal no-media result should be 'R Tape loading error, 0:1' (decoded screen: {screenText.Replace('\n', '|')}).");
            Require(HashFrame(machine.Machine.Renderer.FrameBuffer) != menuFrame, "Selecting 128 TR-DOS should leave the Scorpion startup menu.");
        }

        private static void VerifyScorpion128TrDosPrompt(RomSet roms, byte[] trdosRom, string outputDirectory)
        {
            string tempPath = Path.Combine(outputDirectory, "scorpion-128-trdos-prompt.trd");
            try
            {
                File.WriteAllBytes(tempPath, CreateBlankTrdImage());
                using var machine = CreateMachine(SpectrumModel.Scorpion256, roms, trdosRom, SpectrumJoystickType.Kempston);
                machine.Controller.InsertDisk(0, TrdDiskImage.Load(tempPath));
                RunFrames(machine, 200);
                PressChord(machine, [SpectrumKey.Enter]);
                RunFrames(machine, 400);

                Require(!machine.Beta.IsPaged, "A non-boot disk should return from ROMCS to the Scorpion command environment.");
                Require(!machine.Controller.TransferActive, $"The non-boot disk path should not remain in command {machine.Controller.LastCommand:X2}.");
                Require(ScreenContainsText(machine.Machine.Memory, roms, "A>"),
                    "A valid non-boot disk should reach the 128 TR-DOS A> prompt.");
            }
            finally
            {
                DeleteFileIfExists(tempPath);
            }
        }

        private static void VerifyScorpion128TrDosAutoboot(RomSet roms, byte[] trdosRom, string diskPath)
        {
            using var machine = CreateMachine(SpectrumModel.Scorpion256, roms, trdosRom, SpectrumJoystickType.Kempston);
            machine.Controller.InsertDisk(0, TrdDiskImage.Load(diskPath));
            RunFrames(machine, 200);
            PressChord(machine, [SpectrumKey.Enter]);
            RunFrames(machine, 600);

            Require(machine.Controller.CommandCount > 100, "An autoboot disk should execute its disk-loading command sequence.");
            Require(!machine.Controller.TransferActive, $"Autoboot should not remain stuck in command {machine.Controller.LastCommand:X2}.");
            Require(!machine.Beta.IsPaged, "The autoboot program should release TR-DOS ROMCS after loading.");
            Require(machine.Machine.Memory.ReadDirect(0x67A2) == 0x76,
                "ANAMORIG autoboot should reach its loaded HALT-based checkerboard loop.");
            Require(!ScreenContainsText(machine.Machine.Memory, roms, "A>"),
                "An autoboot disk must start its program instead of stopping at the A> prompt.");
        }

        private static void VerifyScorpionDirectDiskLoader(RomSet roms, byte[] trdosRom, string diskPath)
        {
            using var machine = CreateMachine(SpectrumModel.Scorpion256, roms, trdosRom, SpectrumJoystickType.Kempston);
            machine.Controller.InsertDisk(0, TrdDiskImage.Load(diskPath));
            RunFrames(machine, 200);

            // The Scorpion menu order is 128 TR-DOS, 128 BASIC, Calculator,
            // 48 BASIC, 48 TR-DOS.
            PressChord(machine, [SpectrumKey.CapsShift, SpectrumKey.D6]);
            PressChord(machine, [SpectrumKey.CapsShift, SpectrumKey.D6]);
            PressChord(machine, [SpectrumKey.CapsShift, SpectrumKey.D6]);
            PressChord(machine, [SpectrumKey.CapsShift, SpectrumKey.D6]);
            PressChord(machine, [SpectrumKey.Enter]);
            RunFrames(machine, 100);

            PressChord(machine, [SpectrumKey.R]);
            PressChord(machine, [SpectrumKey.U]);
            PressChord(machine, [SpectrumKey.N]);
            PressChord(machine, [SpectrumKey.Enter]);

            RunFrames(machine, 2_000);

            Require(machine.Controller.CommandCount > 1_000, "ANAMNESI should execute its direct FDC loading sequence.");
            Require(!machine.Controller.TransferActive, $"ANAMNESI should not remain stuck in command {machine.Controller.LastCommand:X2}.");
            Require(machine.Controller.LastCommand == 0xD0, "ANAMNESI should finish disk loading by forcing the WD controller idle.");
            Require(!machine.Beta.IsPaged, "The running demo should have released TR-DOS ROMCS.");
            Require(machine.Machine.Memory.ReadDirect(0x67A2) == 0x76, "The loaded demo main loop should contain its HALT instruction.");
        }

        private static void VerifyScorpionRasterDemo(RomSet roms, byte[] trdosRom, string diskPath)
        {
            using var machine = CreateMachine(SpectrumModel.Scorpion256, roms, trdosRom, SpectrumJoystickType.Kempston);
            machine.Controller.InsertDisk(0, TrdDiskImage.Load(diskPath));
            RunFrames(machine, 200);

            // Enter 48 TR-DOS from the Scorpion startup menu and run the disk boot file.
            for (int i = 0; i < 4; i++)
            {
                PressChord(machine, [SpectrumKey.CapsShift, SpectrumKey.D6]);
            }
            PressChord(machine, [SpectrumKey.Enter]);
            RunFrames(machine, 100);
            PressChord(machine, [SpectrumKey.R]);
            PressChord(machine, [SpectrumKey.U]);
            PressChord(machine, [SpectrumKey.N]);
            PressChord(machine, [SpectrumKey.Enter]);
            RunFrames(machine, 2_000);

            Require(machine.Controller.CommandCount > 1_000, "ANAMORIG should execute its direct FDC loading sequence.");
            Require(!machine.Controller.TransferActive, $"ANAMORIG should not remain stuck in command {machine.Controller.LastCommand:X2}.");
            Require(!machine.Beta.IsPaged, "The running ANAMORIG demo should release TR-DOS ROMCS.");

            int firstBorderTransition = FindFirstHorizontalTransition(
                machine.Machine.Renderer.FrameBuffer,
                machine.Machine.Renderer.FrameWidth,
                y: 40);
            int firstPaperTransition = FindFirstHorizontalTransitionAfter(
                machine.Machine.Renderer.FrameBuffer,
                machine.Machine.Renderer.FrameWidth,
                y: 48,
                startX: 33);
            int secondPaperTransition = FindFirstHorizontalTransitionAfter(
                machine.Machine.Renderer.FrameBuffer,
                machine.Machine.Renderer.FrameWidth,
                y: 48,
                startX: firstPaperTransition + 1);
            int paperPeriod = secondPaperTransition - firstPaperTransition;
            Require(firstPaperTransition > 32 && paperPeriod > 0,
                "ANAMORIG's paper checker transitions must be visible before comparing raster phases.");
            int expectedBorderPhase = firstPaperTransition % paperPeriod;
            Require(firstBorderTransition == expectedBorderPhase,
                $"ANAMORIG's border and paper checker phases differ: border={firstBorderTransition}, " +
                $"paper phase={expectedBorderPhase} (transitions {firstPaperTransition}, {secondPaperTransition}).");

            // Follow the same sequence used from the UI: leave the checkerboard,
            // move down once in the demo menu, then start the disk-loaded part.
            long commandsBeforeStart = machine.Controller.CommandCount;
            PressChord(machine, [SpectrumKey.Enter]);
            RunFrames(machine, 300);
            PressChord(machine, [SpectrumKey.CapsShift, SpectrumKey.D6]);
            PressChord(machine, [SpectrumKey.Enter]);
            RunFrames(machine, 1_000);

            Require(machine.Controller.CommandCount > commandsBeforeStart,
                "Starting ANAMORIG from its own menu should issue the next direct-FDC command sequence.");
            Require(!machine.Controller.TransferActive,
                $"ANAMORIG should complete its post-menu disk transfer instead of hanging in command {machine.Controller.LastCommand:X2}.");
        }

        private static void VerifyScorpion128TrDosWithDisk(RomSet roms, byte[] trdosRom, string diskPath)
        {
            using var machine = CreateMachine(SpectrumModel.Scorpion256, roms, trdosRom, SpectrumJoystickType.Kempston);
            machine.Controller.InsertDisk(0, TrdDiskImage.Load(diskPath));
            RunFrames(machine, 200);

            PressChord(machine, [SpectrumKey.Enter]);
            // The genuine Scorpion ROM deliberately leaves a write-sector probe
            // unserviced; allow its one-second FD1793 timeout plus menu overhead.
            RunFrames(machine, 400);

            Require(machine.Controller.CommandCount > 100, "128 TR-DOS should progress beyond its initial attached-media probe.");
            Require(!machine.Controller.TransferActive, $"128 TR-DOS should not remain stuck in command {machine.Controller.LastCommand:X2}.");
        }

        private static void RunFrames(HeadlessMachine machine, int frameCount)
        {
            for (int frame = 0; frame < frameCount; frame++)
            {
                machine.Emulator.RunFrame(presentFrame: false);
            }
        }

        private static void PressChord(HeadlessMachine machine, ReadOnlySpan<SpectrumKey> keys)
        {
            foreach (SpectrumKey key in keys)
            {
                machine.Machine.Keyboard.SetKeyState(key, pressed: true);
            }

            RunFrames(machine, 10);

            foreach (SpectrumKey key in keys)
            {
                machine.Machine.Keyboard.SetKeyState(key, pressed: false);
            }

            RunFrames(machine, 10);
        }

        private static ushort ReadSystemVariableWord(SpectrumMemory memory, ushort address)
        {
            return (ushort)(memory.ReadDirect(address) | (memory.ReadDirect((ushort)(address + 1)) << 8));
        }

        private static uint HashFrame(ReadOnlySpan<int> frame)
        {
            uint hash = 2166136261;
            foreach (int pixel in frame)
            {
                hash ^= unchecked((uint)pixel);
                hash *= 16777619;
            }

            return hash;
        }

        private static int FindFirstHorizontalTransition(ReadOnlySpan<int> frame, int width, int y)
        {
            int rowStart = checked(y * width);
            int first = frame[rowStart];
            for (int x = 1; x < width; x++)
            {
                if (frame[rowStart + x] != first)
                {
                    return x;
                }
            }

            return -1;
        }

        private static int FindFirstHorizontalTransitionAfter(ReadOnlySpan<int> frame, int width, int y, int startX)
        {
            int rowStart = checked(y * width);
            int clampedStart = Math.Clamp(startX, 1, width - 1);
            int previous = frame[rowStart + clampedStart - 1];
            for (int x = clampedStart; x < width; x++)
            {
                int current = frame[rowStart + x];
                if (current != previous)
                {
                    return x;
                }

                previous = current;
            }

            return -1;
        }

        /// <summary>
        /// Decodes the 32x24 Spectrum text grid through the active ROM's CHARS
        /// table. This lets integration checks assert the firmware's terminal
        /// result rather than accepting a transient PC inside TR-DOS.
        /// </summary>
        internal static bool ScreenContainsText(SpectrumMemory memory, RomSet roms, string expected)
        {
            return DecodeScreenText(memory, roms).Contains(expected, StringComparison.OrdinalIgnoreCase);
        }

        internal static string DecodeScreenText(SpectrumMemory memory, RomSet roms)
        {
            ushort chars = ReadSystemVariableWord(memory, 0x5C36);
            if (chars < 0x0100 || chars > 0xFF00)
            {
                return string.Empty;
            }

            var text = new StringBuilder(24 * 33);
            Span<byte> cell = stackalloc byte[8];
            for (int row = 0; row < 24; row++)
            {
                for (int column = 0; column < 32; column++)
                {
                    for (int scanline = 0; scanline < 8; scanline++)
                    {
                        int y = (row * 8) + scanline;
                        ushort address = (ushort)(0x4000 |
                            ((y & 0xC0) << 5) |
                            ((y & 0x07) << 8) |
                            ((y & 0x38) << 2) |
                            column);
                        cell[scanline] = memory.ReadScreen(address);
                    }

                    text.Append(DecodeScreenCharacter(memory, roms, chars, cell));
                }

                text.Append('\n');
            }

            return text.ToString();
        }

        private static char DecodeScreenCharacter(SpectrumMemory memory, RomSet roms, ushort chars, ReadOnlySpan<byte> cell)
        {
            for (int code = 32; code < 128; code++)
            {
                ushort glyph = (ushort)(chars + (code * 8));
                int sourceCount = glyph < 0x4000 ? roms.BankCount : 1;
                for (int source = 0; source < sourceCount; source++)
                {
                    bool normal = true;
                    bool inverse = true;
                    for (int scanline = 0; scanline < 8 && (normal || inverse); scanline++)
                    {
                        ushort address = (ushort)(glyph + scanline);
                        byte expected = glyph < 0x4000
                            ? roms.GetBank(source).Span[address]
                            : memory.ReadDirect(address);
                        normal &= cell[scanline] == expected;
                        inverse &= cell[scanline] == (byte)~expected;
                    }

                    if (normal || inverse)
                    {
                        return (char)code;
                    }
                }
            }

            return '?';
        }

        private static HeadlessMachine CreateMachine(
            SpectrumModel model,
            RomSet roms,
            byte[] trdosRom,
            SpectrumJoystickType joystickType = SpectrumJoystickType.None)
        {
            SpectrumBeta128Device? beta = null;
            SpectrumBeta128DiskController? controller = null;
            SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
            {
                Model = model,
                Roms = roms,
                JoystickType = joystickType,
                ConfigureDevices = context =>
                {
                    beta = new SpectrumBeta128Device(trdosRom);
                    context.Memory.ConfigureBeta128(beta);
                    controller = new SpectrumBeta128DiskController(beta);
                    context.Ports.AddDevice(controller);
                }
            });
            machine.AttachTape(null);
            return new HeadlessMachine(
                machine,
                beta ?? throw new InvalidOperationException("Beta 128 device was not configured."),
                controller ?? throw new InvalidOperationException("Beta 128 disk controller was not configured."));
        }
        private static int FindFirstDifference(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            int length = Math.Min(left.Length, right.Length);
            for (int i = 0; i < length; i++)
            {
                if (left[i] != right[i])
                {
                    return i;
                }
            }

            return -1;
        }
        private static byte[] LoadTrDosRom(string root, RomSet modelRoms)
        {
            string trdosPath = Path.Combine(root, "ROMs", "trdos.rom");
            if (File.Exists(trdosPath))
            {
                byte[] candidate = File.ReadAllBytes(trdosPath);
                if (candidate.Length == 16 * 1024 && !candidate.AsSpan().SequenceEqual(modelRoms.GetBank(0).Span))
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

            throw new FileNotFoundException("Could not find a usable TR-DOS ROM. Expected ROMs\\trdos.rom or bank 3 in ROMs\\scorpion.rom.");
        }
        private static string ResolveOutputPath(string? requestedPath)
        {
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                return Path.GetFullPath(requestedPath);
            }

            string root = FindRepositoryRoot() ?? Directory.GetCurrentDirectory();
            return Path.Combine(root, "TEST", "pentagon-verification-results.txt");
        }
        private static string? FindRepositoryRoot()
        {
            return FindRepositoryRootFrom(Directory.GetCurrentDirectory())
                ?? FindRepositoryRootFrom(AppContext.BaseDirectory);
        }
        private static string? FindRepositoryRootFrom(string startDirectory)
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory != null)
            {
                string projectPath = Path.Combine(directory.FullName, "ZedExEss.csproj");
                if (File.Exists(projectPath))
                {
                    return directory.FullName;
                }

                string sourceDirectory = Path.Combine(directory.FullName, "ZedExEss");
                string nestedProjectPath = Path.Combine(sourceDirectory, "ZedExEss.csproj");
                if (File.Exists(nestedProjectPath))
                {
                    return sourceDirectory;
                }

                directory = directory.Parent;
            }

            return null;
        }
        private static void DeleteFileIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
        private sealed class VerificationLog(TextWriter writer)
        {
            public int Failed { get; private set; }
            public void Check(string name, Action action)
            {
                try
                {
                    action();
                    WriteLine($"PASS: {name}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
                {
                    Failed++;
                    WriteLine($"FAIL: {name}: {ex.Message}");
                    Debug.WriteLine(ex.ToString());
                }
            }
            public void WriteLine(string line)
            {
                writer.WriteLine(line);
                Debug.WriteLine(line);
            }
        }
        private sealed class PortWriteProbe(Func<ulong> tstateProvider) : IPortDevice
        {
            public ulong? WriteTstate { get; private set; }
            public bool HandlesPort(ushort port) => true;
            public byte Read(ushort port) => 0xFF;
            public void Write(ushort port, byte value)
            {
                WriteTstate = tstateProvider();
            }
        }
        private sealed class HeadlessMachine(
            SpectrumMachine machine,
            SpectrumBeta128Device beta,
            SpectrumBeta128DiskController controller) : IDisposable
        {
            public SpectrumMachine Machine { get; } = machine;
            public SpectrumBeta128Device Beta { get; } = beta;
            public SpectrumBeta128DiskController Controller { get; } = controller;
            public Z80 Cpu => Machine.Cpu;
            public SpectrumEmulator Emulator => Machine.Emulator;
            public void Dispose()
            {
                Emulator.VideoEnabled = true;
            }
        }
    }
}
