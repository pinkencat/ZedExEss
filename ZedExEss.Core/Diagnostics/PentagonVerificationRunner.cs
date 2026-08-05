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
                log.Check("Scorpion boot executes frames", () => VerifyBootRuns(SpectrumModel.Scorpion256, scorpionRoms ?? throw new InvalidOperationException("Scorpion ROM must be loaded before boot verification."), scorpionTrdosRom ?? throw new InvalidOperationException("Scorpion TR-DOS ROM must be loaded before boot verification."), options.BootFrames));

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
            for (int bank = 0; bank < 16; bank++)
            {
                byte[] data = new byte[RamBankSize];
                Array.Fill(data, (byte)(0xB0 + bank));
                memory.LoadRamBank(bank, data);
            }

            memory.WritePort7FFD(0x07);
            Require(memory.ReadDirect(0xC000) == 0xB7, "Scorpion should page RAM bank 7 at C000h via #7FFD.");

            memory.WritePort1FFD(0x10);
            Require(memory.ReadDirect(0xC000) == 0xBF, "Scorpion #1FFD bit 4 should select RAM banks 8-15.");

            memory.WritePort7FFD(0x08);
            Require(memory.ReadScreen(0x4000) == 0xB7, "Scorpion alternate screen should read from RAM bank 7.");

            int rom2Probe = FindFirstDifference(roms.GetBank(0).Span, roms.GetBank(2).Span);
            Require(rom2Probe >= 0, "Scorpion ROM bank 2 must differ from ROM bank 0 for paging verification.");
            memory.WritePort1FFD(0x02);
            Require(memory.CurrentRomBank == 2, "Scorpion #1FFD bit 1 should select ROM bank 2.");
            Require(memory.ReadDirect((ushort)rom2Probe) == roms.GetBank(2).Span[rom2Probe], "Scorpion ROM bank 2 should be visible at 0000h.");

            memory.WritePort1FFD(0x01);
            Require(memory.ReadDirect(0x0000) == 0xB0, "Scorpion #1FFD bit 0 should map RAM bank 0 at 0000h.");
            memory.WriteDirect(0x0000, 0x5A);
            Require(memory.ReadDirect(0x0000) == 0x5A, "Scorpion low-memory RAM mapping should be writable.");
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
            int frameCount = Math.Max(1, frames);
            for (int frame = 0; frame < frameCount; frame++)
            {
                machine.Emulator.RunFrame(presentFrame: false);
            }

            Require(machine.Cpu.Cyc > 0, "CPU should advance while running the boot ROM.");
        }
        private static HeadlessMachine CreateMachine(SpectrumModel model, RomSet roms, byte[] trdosRom)
        {
            SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
            {
                Model = model,
                Roms = roms,
                ConfigureDevices = context =>
                {
                    var beta = new SpectrumBeta128Device(trdosRom);
                    context.Memory.ConfigureBeta128(beta);
                    context.Ports.AddDevice(new SpectrumBeta128DiskController(beta));
                }
            });
            machine.AttachTape(null);
            return new HeadlessMachine(machine.Cpu, machine.Emulator);
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
        private sealed class HeadlessMachine(Z80 cpu, SpectrumEmulator emulator) : IDisposable
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
