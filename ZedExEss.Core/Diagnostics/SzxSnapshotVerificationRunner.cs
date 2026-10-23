using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZedExEss.FileHandlers;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Interface1;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Z80CPU;

namespace ZedExEss.Diagnostics;

public sealed class SzxSnapshotVerificationOptions
{
    public string? OutputPath { get; init; }
}

/// <summary>Exercises SZX serialization and restoration without a desktop host.</summary>
public static class SzxSnapshotVerificationRunner
{
    public static int Run(SzxSnapshotVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string outputPath = Path.GetFullPath(options.OutputPath ?? "szx-verification.log");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"zedexess-szx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        int failed = 0;
        try
        {
            writer.WriteLine("SZX snapshot verification");
            writer.WriteLine($"Output: {outputPath}");
            writer.WriteLine();

            foreach (SpectrumModel model in Enum.GetValues<SpectrumModel>())
            {
                Check($"{model} standard state round trip", () => VerifyModelRoundTrip(model), ref failed);
            }

            Check("Interface 1 embedded media and transport round trip", VerifyInterface1RoundTrip, ref failed);
            Check("Malformed snapshot is rejected before restore", VerifyMalformedSnapshot, ref failed);

            writer.WriteLine();
            writer.WriteLine(failed == 0
                ? "Result: PASS"
                : $"Result: FAIL ({failed.ToString(CultureInfo.InvariantCulture)} failed checks)");
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        void Check(string name, Action action, ref int failureCount)
        {
            try
            {
                action();
                writer.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failureCount++;
                writer.WriteLine($"FAIL {name}: {ex.Message}");
                Debug.WriteLine(ex.ToString());
            }
        }

        void VerifyModelRoundTrip(SpectrumModel model)
        {
            SpectrumMachine source = CreateMachine(model, out _);
            PopulateMachine(source, model);
            SpectrumMachineSnapshot expected = SpectrumMachineSnapshotService.Capture(source);

            string path = Path.Combine(temporaryDirectory, $"{model}.szx");
            SzxSnapshotCodec.Save(path, expected);
            Require(SzxSnapshotCodec.DetectModel(path) == model, "Header model detection changed the machine type.");
            SpectrumMachineSnapshot decoded = SzxSnapshotCodec.Load(path);
            CompareSnapshots(expected, decoded, compareInterface1: false);

            SpectrumMachine target = CreateMachine(model, out _);
            SpectrumMachineSnapshotService.Restore(target, decoded);
            SpectrumMachineSnapshot restored = SpectrumMachineSnapshotService.Capture(target);
            CompareSnapshots(expected, restored, compareInterface1: false);
        }

        void VerifyInterface1RoundTrip()
        {
            var sourceMedia = new SpectrumInterface1MediaState();
            SpectrumMachine source = CreateMachine(SpectrumModel.Spectrum48K, out SpectrumInterface1Device? sourceDevice);
            sourceMedia.ConnectDevice(sourceDevice);
            MicrodriveCartridge cartridge = sourceMedia.Create(0, MicrodriveCartridge.MinimumSectorCount, "Snapshot");
            cartridge.TryWriteByte(12, 0xA5);
            cartridge.SetWriteProtected(true);

            var drives = new MicrodriveTransportState[SpectrumInterface1Device.DriveCount];
            for (int i = 0; i < drives.Length; i++)
            {
                drives[i] = new MicrodriveTransportState(
                    i == 0 ? 27 : 0,
                    i == 0 ? 3 : 0,
                    MicrodriveCartridge.HeaderLength,
                    15 - i,
                    15,
                    (byte)(0x80 + i));
            }

            sourceDevice!.RestoreState(new SpectrumInterface1DeviceState(
                paged: true,
                control: 0xE2,
                networkOutput: 1,
                motorMask: 1,
                activity: MicrodriveActivityState.Writing,
                rs232: new SpectrumInterface1Rs232TransportState(
                    InputPhase: 7,
                    OutputPhase: 5,
                    InputShiftRegister: 0x2A,
                    OutputShiftRegister: 0xC0,
                    InputLine: true,
                    OutputLine: false),
                drives));
            PopulateMachine(source, SpectrumModel.Spectrum48K);
            SpectrumMachineSnapshot expected = SpectrumMachineSnapshotService.Capture(source, sourceMedia);

            string path = Path.Combine(temporaryDirectory, "interface1.szx");
            SzxSnapshotCodec.Save(path, expected);
            SpectrumMachineSnapshot decoded = SzxSnapshotCodec.Load(path);
            CompareSnapshots(expected, decoded, compareInterface1: true);

            var targetMedia = new SpectrumInterface1MediaState();
            SpectrumMachine target = CreateMachine(SpectrumModel.Spectrum48K, out SpectrumInterface1Device? targetDevice);
            targetMedia.ConnectDevice(targetDevice);
            SpectrumMachineSnapshotService.Restore(target, decoded, targetMedia);
            SpectrumMachineSnapshot restored = SpectrumMachineSnapshotService.Capture(target, targetMedia);
            CompareSnapshots(expected, restored, compareInterface1: true);
        }

        void VerifyMalformedSnapshot()
        {
            string path = Path.Combine(temporaryDirectory, "invalid.szx");
            File.WriteAllBytes(path,
            [
                (byte)'Z', (byte)'X', (byte)'S', (byte)'T', 1, 5, 1, 0,
                (byte)'R', (byte)'A', (byte)'M', (byte)'P', 0xFF, 0xFF, 0xFF, 0x7F
            ]);

            bool rejected = false;
            try
            {
                _ = SzxSnapshotCodec.Load(path);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            Require(rejected, "An oversized/truncated chunk was accepted.");
        }
    }

    private static SpectrumMachine CreateMachine(SpectrumModel model, out SpectrumInterface1Device? interface1)
    {
        SpectrumInterface1Device? created = null;
        SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = model,
            Roms = RomSet.CreateBlank(SpectrumModelTraits.RomBankCount(model)),
            RenderEnabled = false,
            ConfigureDevices = model is SpectrumModel.Spectrum16K or SpectrumModel.Spectrum48K
                ? context =>
                {
                    created = new SpectrumInterface1Device(new byte[SpectrumInterface1Device.RomSize]);
                    context.Memory.ConfigureInterface1(created);
                    context.Ports.AddDevice(created);
                }
                : null
        });
        interface1 = created;
        return machine;
    }

    private static void PopulateMachine(SpectrumMachine machine, SpectrumModel model)
    {
        for (int bank = 0; bank < machine.Memory.RamBankCount; bank++)
        {
            var data = new byte[0x4000];
            for (int offset = 0; offset < data.Length; offset++)
            {
                data[offset] = (byte)((bank * 37 + offset * 13 + 0x5A) & 0xFF);
            }

            machine.Memory.LoadRamBank(bank, data);
        }

        byte port7ffd = SpectrumModelTraits.SupportsPaging(model) ? (byte)0x17 : (byte)0;
        byte port1ffd = SpectrumModelTraits.SupportsSecondaryPagingPort(model) ? (byte)0x10 : (byte)0;
        machine.Memory.RestorePagingState(port7ffd, port1ffd);
        machine.Ula.RestoreOutputLatch(0x1D);

        var cpu = new Z80SnapshotState(
            123_456_789,
            0x8123,
            0xDFFE,
            0x1357,
            0x2468,
            0xA55A,
            0x91,
            0xA5,
            0x12,
            0x34,
            0x56,
            0x78,
            0x9A,
            0xBC,
            0x11,
            0x22,
            0x33,
            0x44,
            0x55,
            0x66,
            0x77,
            0x88,
            0x3C,
            0xB7,
            2,
            true,
            false,
            true,
            1,
            0xFF,
            true,
            false,
            0xA5,
            0x5A);
        machine.Cpu.RestoreSnapshotState(cpu);
        machine.Renderer.RestoreTiming(12_345 % machine.TstatesPerFrame, 37);
        machine.Emulator.ResetSynchronizationAfterSnapshotRestore();

        if (machine.AyChip != null)
        {
            byte[] registers = Enumerable.Range(0, 16).Select(static i => (byte)(i * 11)).ToArray();
            machine.AyChip.RestoreRegisters(registers);
            machine.AyDevice!.SelectedRegister = 9;
        }
    }

    private static void CompareSnapshots(
        SpectrumMachineSnapshot expected,
        SpectrumMachineSnapshot actual,
        bool compareInterface1)
    {
        Require(expected.Model == actual.Model, "Model differs.");
        Require(expected.Cpu == actual.Cpu, "CPU state differs.");
        Require(expected.Port7FFD == actual.Port7FFD && expected.Port1FFD == actual.Port1FFD, "Paging latches differ.");
        Require(expected.UlaOutput == actual.UlaOutput, "ULA output latch differs.");
        Require(expected.FrameTstate == actual.FrameTstate && expected.FrameCounter == actual.FrameCounter, "ULA timing differs.");
        Require(expected.RamBankCount == actual.RamBankCount, "RAM bank count differs.");
        for (int bank = 0; bank < expected.RamBankCount; bank++)
        {
            Require(expected.CopyRamBank(bank).AsSpan().SequenceEqual(actual.CopyRamBank(bank)), $"RAM bank {bank} differs.");
        }

        if (expected.Ay == null || actual.Ay == null)
        {
            Require(expected.Ay == null && actual.Ay == null, "AY presence differs.");
        }
        else
        {
            Require(expected.Ay.SelectedRegister == actual.Ay.SelectedRegister, "AY selected register differs.");
            Require(expected.Ay.Registers.AsSpan().SequenceEqual(actual.Ay.Registers), "AY registers differ.");
        }

        if (compareInterface1)
        {
            CompareInterface1(expected.Interface1, actual.Interface1);
        }
    }

    private static void CompareInterface1(SpectrumInterface1Snapshot? expected, SpectrumInterface1Snapshot? actual)
    {
        if (expected?.Device == null || actual?.Device == null)
        {
            throw new InvalidOperationException("Interface 1 state is missing.");
        }

        Require(expected.Device.IsPaged == actual.Device.IsPaged &&
                expected.Device.Control == actual.Device.Control &&
                expected.Device.NetworkOutput == actual.Device.NetworkOutput &&
                expected.Device.MotorMask == actual.Device.MotorMask &&
                expected.Device.Activity == actual.Device.Activity &&
                expected.Device.Rs232 == actual.Device.Rs232,
            "Interface 1 latches differ.");
        Require(expected.Device.Drives.SequenceEqual(actual.Device.Drives), "Microdrive transport state differs.");
        for (int slot = 0; slot < SpectrumInterface1Device.DriveCount; slot++)
        {
            SpectrumInterface1MediaSlotState left = expected.Media.Slots[slot];
            SpectrumInterface1MediaSlotState right = actual.Media.Slots[slot];
            Require(left.BackingPath == right.BackingPath, $"Microdrive {slot + 1} path differs.");
            if (left.Cartridge == null || right.Cartridge == null)
            {
                Require(left.Cartridge == null && right.Cartridge == null, $"Microdrive {slot + 1} presence differs.");
                continue;
            }

            Require(left.Cartridge.SectorCount == right.Cartridge.SectorCount &&
                    left.Cartridge.WriteProtected == right.Cartridge.WriteProtected &&
                    left.Cartridge.Modified == right.Cartridge.Modified,
                $"Microdrive {slot + 1} metadata differs.");
            Require(left.Cartridge.CopyData().AsSpan().SequenceEqual(right.Cartridge.CopyData()),
                $"Microdrive {slot + 1} data differs.");
            Require(left.Cartridge.CopyPreambleState().AsSpan().SequenceEqual(right.Cartridge.CopyPreambleState()),
                $"Microdrive {slot + 1} preamble state differs.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
