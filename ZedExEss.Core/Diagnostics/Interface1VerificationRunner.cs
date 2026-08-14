using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Interface1;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Z80CPU;

namespace ZedExEss.Diagnostics;

/// <summary>Settings for the portable Interface 1 foundation checks.</summary>
public sealed class Interface1VerificationOptions
{
    public string? OutputPath { get; init; }
    public string? RomPath { get; init; }
}

/// <summary>
/// Verifies Interface 1 ROMCS timing, mirrored mapping and partially decoded ports
/// without booting a desktop host.
/// </summary>
public static class Interface1VerificationRunner
{
    public static int Run(Interface1VerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string outputPath = Path.GetFullPath(options.OutputPath ?? "interface1-verification.log");
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        int failed = 0;

        writer.WriteLine("Interface 1 verification");
        writer.WriteLine($"Output: {outputPath}");
        writer.WriteLine();

        Check("ROM size validation", VerifyRomSize, ref failed);
        Check("Opcode-fetch ROMCS mapping", VerifyRomMapping, ref failed);
        Check("Partially decoded IF1 ports", VerifyPortDecode, ref failed);
        Check("Eight-drive motor shift register", VerifyMotorShiftRegister, ref failed);
        Check("MDR image validation and round trip", VerifyMdrImage, ref failed);
        Check("Microdrive GAP/SYNC and byte transport", VerifyMicrodriveReadTransport, ref failed);
        Check("Microdrive write/erase gates and write protection", VerifyMicrodriveWriteTransport, ref failed);
        Check("Persistent Microdrive session state", VerifyPersistentMediaState, ref failed);
        Check("Interface 1 snapshot capture and exact restore", VerifySnapshotPersistence, ref failed);
        Check("Dirty MDR shutdown flush and reload", VerifyDirtyMediaFlush, ref failed);

        if (!string.IsNullOrWhiteSpace(options.RomPath))
        {
            Check("Supplied Interface 1 ROM", () => VerifySuppliedRom(options.RomPath!), ref failed);
            Check("Interface 1 ROM cartridge-presence probe", () => VerifyRomPresenceProbe(options.RomPath!), ref failed);
        }

        writer.WriteLine();
        writer.WriteLine(failed == 0
            ? "Result: PASS"
            : $"Result: FAIL ({failed.ToString(CultureInfo.InvariantCulture)} failed checks)");
        return failed == 0 ? 0 : 1;

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
    }

    private static void VerifyRomSize()
    {
        _ = new SpectrumInterface1Device(new byte[SpectrumInterface1Device.RomSize]);

        bool rejected = false;
        try
        {
            _ = new SpectrumInterface1Device(new byte[SpectrumInterface1Device.RomSize - 1]);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Require(rejected, "An incorrectly sized Interface 1 ROM was accepted.");
    }

    private static void VerifyRomMapping()
    {
        byte[] firmware = CreatePatternedRom();
        var device = new SpectrumInterface1Device(firmware);
        var memory = new SpectrumMemory(
            SpectrumModel.Spectrum48K,
            RomSet.CreateBlank(1));
        memory.ConfigureInterface1(device);

        Require(!device.IsPaged, "Interface 1 ROM must start unpaged.");
        Require(memory.ReadDirect(0x0008) == 0x00, "Machine ROM should be visible before an IF1 entry fetch.");

        byte entry = memory.FetchOpcode(0x0008);
        Require(device.IsPaged, "Fetch at 0008h did not page Interface 1 ROM before the read.");
        Require(entry == firmware[0x0008], "Entry opcode was not read from Interface 1 ROM.");
        Require(memory.ReadDirect(0x2008) == firmware[0x0008], "Interface 1 ROM is not mirrored at 2000h.");

        byte exit = memory.FetchOpcode(0x0700);
        Require(exit == firmware[0x0700], "Exit opcode was not fetched from Interface 1 ROM.");
        Require(!device.IsPaged, "Fetch at 0700h did not release ROMCS after the read.");
        Require(memory.ReadDirect(0x0700) == 0x00, "Machine ROM was not restored after IF1 unpaging.");

        _ = memory.FetchOpcode(0x1708);
        Require(device.IsPaged, "Fetch at 1708h did not page Interface 1 ROM.");
        Require(memory.ReadDirect(0x3708) == firmware[0x1708], "Upper mirror did not wrap to the 8 KiB ROM.");
    }

    private static void VerifyPortDecode()
    {
        var device = new SpectrumInterface1Device(CreatePatternedRom());

        Require(device.HandlesPort(0x00E7), "Nominal Microdrive data port was not decoded.");
        Require(device.HandlesPort(0xBFEF), "Aliased control port was not decoded.");
        Require(device.HandlesPort(0x7FF7), "Aliased network port was not decoded.");
        Require(!device.HandlesPort(0x0018), "Unknown A3/A4 port combination was decoded.");
        Require(device.Read(0x00E7) == 0xFF, "Idle Microdrive data bus should read FFh.");
        Require(device.Read(0x00EF) == 0xE7, "Idle control/status value should read E7h.");
        Require(device.Read(0x00F7) == 0x7E, "Disconnected communications value should read 7Eh.");

        device.Write(0x00F7, 0x01);
        Require(device.NetworkOutput == 1, "Network output bit was not latched.");
    }

    private static void VerifyMotorShiftRegister()
    {
        var device = new SpectrumInterface1Device(CreatePatternedRom());

        // Clock high then low with active-low DATA starts drive 1.
        device.Write(0x00EF, 0xEE);
        device.Write(0x00EF, 0xEC);
        Require(device.MotorMask == 0x01 && device.IsMotorRunning(1), "Drive 1 did not start on a falling clock edge.");
        Require(device.SelectedDriveNumber == 1, "Selected-drive status did not identify drive 1.");
        Require(device.Activity == MicrodriveActivityState.Idle, "Selecting a drive incorrectly reported data activity.");

        // Shift drive 1 to drive 2 while inserting an off state for drive 1.
        device.Write(0x00EF, 0xEF);
        device.Write(0x00EF, 0xED);
        Require(device.MotorMask == 0x02, "Motor state did not shift from drive 1 to drive 2.");
        Require(!device.IsMotorRunning(1) && device.IsMotorRunning(2), "Shifted motor selection is incorrect.");
        Require(device.SelectedDriveNumber == 2, "Selected-drive status did not follow the motor shift register.");
    }

    private static void VerifyMdrImage()
    {
        byte[] image = CreatePatternedMdr(writeProtected: true);
        MicrodriveCartridge cartridge = MicrodriveCartridge.Load(image);

        Require(cartridge.SectorCount == MicrodriveCartridge.MinimumSectorCount, "MDR sector count was decoded incorrectly.");
        Require(cartridge.WriteProtected, "MDR trailing write-protect byte was ignored.");
        Require(cartridge.ToMdrBytes().AsSpan().SequenceEqual(image), "MDR round trip changed image bytes.");

        bool rejected = false;
        try
        {
            _ = MicrodriveCartridge.Load(new byte[(MicrodriveCartridge.MinimumSectorCount * MicrodriveCartridge.SectorLength) - 1]);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Require(rejected, "An invalid MDR length was accepted.");

        MicrodriveCartridge formatted = MicrodriveCartridge.CreateFormatted("Success", 179);
        byte[] expectedFirstHeader =
        [
            0x01, 0x4B, 0x00, 0x00,
            (byte)'S', (byte)'u', (byte)'c', (byte)'c', (byte)'e', (byte)'s', (byte)'s',
            0x20, 0x20, 0x20, 0x88
        ];
        for (int i = 0; i < expectedFirstHeader.Length; i++)
        {
            Require(formatted.ReadByte(i) == expectedFirstHeader[i],
                $"Formatted MDR header byte {i} is {formatted.ReadByte(i):X2}; expected {expectedFirstHeader[i]:X2}.");
        }

        Require(formatted.GetPreambleState(0) == byte.MaxValue,
            "Formatted cartridge did not expose a valid sector-header preamble.");
        Require(formatted.GetPreambleState(formatted.SectorCount) == byte.MaxValue,
            "Formatted cartridge did not expose a valid record-header preamble.");
    }

    private static void VerifyMicrodriveReadTransport()
    {
        byte[] image = CreatePatternedMdr(writeProtected: false);
        var cartridge = MicrodriveCartridge.Load(image);
        var device = new SpectrumInterface1Device(CreatePatternedRom());
        device.InsertCartridge(1, cartridge);
        SelectDriveOne(device);

        for (int i = 0; i < 15; i++)
        {
            Require(device.Read(0x00EF) == 0xE7, $"GAP ended too early at status read {i}.");
        }

        Require(device.Read(0x00EF) == 0xE1, "GAP/SYNC active-low status was not exposed after the gap.");
        Require(device.Activity == MicrodriveActivityState.Reading, "Status polling did not report Microdrive read activity.");

        for (int i = 0; i < MicrodriveCartridge.HeaderLength; i++)
        {
            Require(device.Read(0x00E7) == image[i], $"Header transport byte {i} is incorrect.");
        }

        // A status poll restarts the transfer at the record-header boundary.
        _ = device.Read(0x00EF);
        Require(device.Read(0x00E7) == image[MicrodriveCartridge.HeaderLength],
            "Record-header transport did not follow the sector header.");

        Require(ReferenceEquals(device.EjectCartridge(1), cartridge), "Eject did not return the inserted cartridge.");
        Require(device.GetCartridge(1) == null, "Drive retained an ejected cartridge.");
    }

    private static void VerifyMicrodriveWriteTransport()
    {
        var cartridge = MicrodriveCartridge.CreateBlank(MicrodriveCartridge.MinimumSectorCount);
        var device = new SpectrumInterface1Device(CreatePatternedRom());
        device.InsertCartridge(1, cartridge);
        SelectDriveOne(device);

        Require(!device.MicrodriveWriteEnabled && !device.MicrodriveEraseEnabled,
            "Drive selection did not leave the IF1 in its EEh read state.");

        // The data register is electrically disconnected from the write head
        // in read mode. An E7h output must therefore leave the cartridge and
        // transport untouched.
        byte originalFirstByte = cartridge.ReadByte(0);
        WritePreamble(device);
        device.Write(0x00E7, 0x35);
        Require(cartridge.ReadByte(0) == originalFirstByte,
            "A data-port write modified the cartridge while R/W selected read mode.");

        // E6h starts the leading erase head, but does not yet enable the data
        // write head. MDR images contain logical sectors rather than raw flux,
        // so this phase is represented by its gate/activity state only.
        device.Write(0x00EF, 0xE6);
        Require(!device.MicrodriveWriteEnabled && device.MicrodriveEraseEnabled,
            "E6h did not select the erase-only lead-in state.");
        device.Write(0x00E7, 0x35);
        Require(cartridge.ReadByte(0) == originalFirstByte,
            "Erase-only mode incorrectly routed a data byte to the write head.");

        device.Write(0x00EF, 0xE2);
        Require(device.MicrodriveWriteEnabled && device.MicrodriveEraseEnabled,
            "E2h did not enable the Microdrive write and erase heads.");

        byte[] header = Enumerable.Range(0, MicrodriveCartridge.HeaderLength)
            .Select(static i => (byte)(0x80 + i))
            .ToArray();
        WritePreamble(device);
        foreach (byte value in header)
        {
            device.Write(0x00E7, value);
        }

        for (int i = 0; i < header.Length; i++)
        {
            Require(cartridge.ReadByte(i) == header[i], $"Written header byte {i} was not stored.");
        }

        device.Write(0x00EF, 0xEE);
        Require(!device.MicrodriveWriteEnabled && !device.MicrodriveEraseEnabled,
            "EEh did not return the Microdrive to read mode.");

        cartridge.SetWriteProtected(true);
        _ = device.Read(0x00EF); // Move transport to the record-header half.
        Require(device.Read(0x00EF) == 0xE6, "Write-protect status bit is not active-low.");

        int recordOffset = MicrodriveCartridge.HeaderLength;
        byte before = cartridge.ReadByte(recordOffset);
        device.Write(0x00EF, 0xE2);
        WritePreamble(device);
        device.Write(0x00E7, 0x35);
        Require(cartridge.ReadByte(recordOffset) == before, "Write-protected media was modified.");
    }

    private static void VerifySuppliedRom(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] firmware = File.ReadAllBytes(fullPath);
        _ = new SpectrumInterface1Device(firmware);
        Require(firmware.Any(static value => value != 0x00), "Supplied Interface 1 ROM is blank.");
        Require(firmware.Any(static value => value != 0xFF), "Supplied Interface 1 ROM contains only FFh.");
    }

    /// <summary>
    /// Executes the firmware's own SEL-DRIVE routine. This covers the real
    /// eight-pulse motor sequence, settling delay and six-consecutive-GAP presence
    /// test instead of merely duplicating those assumptions in a diagnostic helper.
    /// </summary>
    private static void VerifyRomPresenceProbe(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] firmware = File.ReadAllBytes(fullPath);
        string machineRomPath = Path.Combine(Path.GetDirectoryName(fullPath)!, "48.rom");
        Require(File.Exists(machineRomPath), $"48K ROM not found beside Interface 1 ROM: {machineRomPath}");

        SpectrumInterface1Device? device = null;
        SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum48K,
            Roms = RomSet.LoadFromFiles([machineRomPath]),
            RenderEnabled = false,
            ConfigureDevices = context =>
            {
                device = new SpectrumInterface1Device(firmware);
                context.Memory.ConfigureInterface1(device);
                context.Ports.AddDevice(device);
            }
        });
        SpectrumInterface1Device attachedDevice = device
            ?? throw new InvalidOperationException("Interface 1 device was not attached to the full machine graph.");

        attachedDevice.InsertCartridge(1, MicrodriveCartridge.CreateFormatted("Verifier", MicrodriveCartridge.MinimumSectorCount));

        Z80 cpu = machine.Cpu;
        cpu.A = 1;
        cpu.SP = 0x9000;
        machine.Memory.WriteDirect(0x9000, 0x00);
        machine.Memory.WriteDirect(0x9001, 0x80);

        // An M1 fetch at 0008h asserts ROMCS. Continue directly at the matching
        // firmware revision's SEL-DRIVE entry and stop at the synthetic RAM return.
        _ = machine.Memory.FetchOpcode(0x0008);
        cpu.PC = FindSelectDriveEntry(firmware);
        const int maximumInstructions = 250_000;
        for (int i = 0; i < maximumInstructions && cpu.PC != 0x8000; i++)
        {
            machine.Emulator.StepInstruction();
        }

        Require(cpu.PC == 0x8000,
            $"Firmware presence probe did not return (PC={cpu.PC:X4}, motor={attachedDevice.MotorMask:X2}).");
        Require(attachedDevice.MotorMask == 0x01 && attachedDevice.IsMotorRunning(1),
            $"Firmware selected the wrong Microdrive motor (mask={attachedDevice.MotorMask:X2}).");
    }

    private static ushort FindSelectDriveEntry(ReadOnlySpan<byte> firmware)
    {
        // Both Sinclair revisions begin SEL-DRIVE with PUSH HL / CP 0 / JR NZ and
        // contain the distinctive 1388h settling counter shortly afterwards.
        ReadOnlySpan<byte> prefix = [0xE5, 0xFE, 0x00, 0x20];
        for (int offset = 0; offset <= firmware.Length - 24; offset++)
        {
            if (!firmware.Slice(offset, prefix.Length).SequenceEqual(prefix))
            {
                continue;
            }

            ReadOnlySpan<byte> window = firmware.Slice(offset, 24);
            for (int i = 0; i <= window.Length - 3; i++)
            {
                if (window[i] == 0x21 && window[i + 1] == 0x88 && window[i + 2] == 0x13)
                {
                    return checked((ushort)offset);
                }
            }
        }

        throw new InvalidDataException("Could not locate SEL-DRIVE in the supplied Interface 1 ROM.");
    }

    private static void VerifyPersistentMediaState()
    {
        var media = new SpectrumInterface1MediaState();
        MicrodriveCartridge cartridge = media.Create(3, MicrodriveCartridge.MinimumSectorCount);
        cartridge.SetWriteProtected(true);

        var firstDevice = new SpectrumInterface1Device(CreatePatternedRom());
        media.ConnectDevice(firstDevice);
        Require(ReferenceEquals(firstDevice.GetCartridge(4), cartridge), "Persistent media was not connected to its drive.");

        var replacement = new SpectrumInterface1Device(CreatePatternedRom());
        media.ConnectDevice(replacement);
        Require(firstDevice.GetCartridge(4) == null, "The replaced device retained session media.");
        Require(ReferenceEquals(replacement.GetCartridge(4), cartridge), "Media did not survive device replacement.");
        Require(media.GetPath(3) == null && media.GetCartridge(3)?.WriteProtected == true,
            "Unsaved media state changed during replacement.");

        media.ConnectDevice(null);
        Require(replacement.GetCartridge(4) == null, "Disconnect did not detach media from the old device.");
        Require(ReferenceEquals(media.GetCartridge(3), cartridge), "Disconnect discarded persistent media.");
    }

    private static void VerifyDirtyMediaFlush()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"zedexess-if1-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "flush-test.mdr");
        Directory.CreateDirectory(directory);
        try
        {
            var media = new SpectrumInterface1MediaState();
            MicrodriveCartridge cartridge = media.Create(0, MicrodriveCartridge.MinimumSectorCount, "FlushTest");
            media.SaveAs(0, path);

            byte original = cartridge.ReadByte(30);
            byte replacement = (byte)(original ^ 0x5A);
            Require(cartridge.TryWriteByte(30, replacement), "Writable cartridge rejected a verification byte.");
            Require(cartridge.Modified, "Changing a cartridge byte did not mark the image dirty.");

            media.FlushAll();
            Require(!cartridge.Modified, "Flushing a cartridge did not clear its dirty state.");

            MicrodriveCartridge reloaded = MicrodriveCartridge.Load(path);
            Require(reloaded.ReadByte(30) == replacement,
                "A dirty cartridge byte was lost when the saved MDR was reloaded.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifySnapshotPersistence()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"zedexess-if1-state-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "snapshot-test.mdr");
        Directory.CreateDirectory(directory);
        try
        {
            var media = new SpectrumInterface1MediaState();
            MicrodriveCartridge cartridge = media.Create(
                0,
                MicrodriveCartridge.MinimumSectorCount,
                "Snapshot");
            media.SaveAs(0, path);

            var device = new SpectrumInterface1Device(CreatePatternedRom());
            media.ConnectDevice(device);
            SelectDriveOne(device);
            device.BeforeOpcodeFetch(0x0008);
            device.Write(0x00F7, 0x01);
            device.Write(0x00EF, 0xE2);
            WritePreamble(device);
            device.Write(0x00E7, 0x91);
            device.Write(0x00E7, 0x92);
            device.Write(0x00E7, 0x93);
            Require(cartridge.TryWriteByte(20, 0x42), "Could not prepare a cartridge byte for snapshot verification.");
            cartridge.SetWriteProtected(true);

            SpectrumInterface1Snapshot captured = media.CaptureSnapshot();
            SpectrumInterface1MediaSlotState capturedSlot = captured.Media.Slots[0];
            MicrodriveCartridgeState capturedCartridge = capturedSlot.Cartridge
                ?? throw new InvalidOperationException("Captured snapshot omitted the mounted cartridge.");
            byte[] capturedData = capturedCartridge.CopyData();
            byte[] capturedPreambles = capturedCartridge.CopyPreambleState();

            // Exercise reconstruction of preamble state which MDR files do not
            // contain. This is the state a native snapshot serializer will carry.
            int recordPreamble = capturedCartridge.SectorCount;
            capturedPreambles[recordPreamble] = 5;
            var replacementCartridgeState = new MicrodriveCartridgeState(
                capturedCartridge.SectorCount,
                capturedData,
                capturedPreambles,
                capturedCartridge.WriteProtected,
                capturedCartridge.Modified);
            SpectrumInterface1MediaSlotState[] slots = captured.Media.Slots.ToArray();
            slots[0] = new SpectrumInterface1MediaSlotState(capturedSlot.BackingPath, replacementCartridgeState);
            captured = new SpectrumInterface1Snapshot(
                new SpectrumInterface1MediaSnapshot(slots),
                captured.Device);

            SpectrumInterface1DeviceState expectedDevice = captured.Device
                ?? throw new InvalidOperationException("Connected device state was not captured.");

            // Mutate every ownership layer after capture. None of these changes
            // may leak into the saved state, and discarded dirty media must not be
            // flushed merely because the snapshot is restored.
            cartridge.SetWriteProtected(false);
            Require(cartridge.TryWriteByte(20, 0x99), "Could not mutate live cartridge after capture.");
            device.Write(0x00F7, 0x00);
            device.Reset();
            _ = media.Eject(0, saveDirtyImage: false);
            Require(replacementCartridgeState.CopyData()[20] == 0x42,
                "Live cartridge writes changed the deep-copied snapshot.");

            media.RestoreSnapshot(captured);

            MicrodriveCartridge restored = media.GetCartridge(0)
                ?? throw new InvalidOperationException("Snapshot restore did not remount drive 1.");
            Require(media.GetPath(0) == Path.GetFullPath(path), "Snapshot restore lost the MDR backing path.");
            Require(restored.ReadByte(20) == 0x42, "Snapshot restore returned mutated future cartridge data.");
            Require(restored.WriteProtected, "Snapshot restore lost cartridge write protection.");
            Require(restored.Modified, "Snapshot restore lost the cartridge dirty flag.");
            Require(restored.GetPreambleState(recordPreamble) == 5,
                "Snapshot restore lost an in-progress record preamble.");
            Require(ReferenceEquals(device.GetCartridge(1), restored),
                "Restored media was not reconnected to the active Interface 1 device.");

            SpectrumInterface1Snapshot roundTrip = media.CaptureSnapshot();
            SpectrumInterface1DeviceState actualDevice = roundTrip.Device
                ?? throw new InvalidOperationException("Restored device state could not be recaptured.");
            Require(actualDevice.IsPaged == expectedDevice.IsPaged, "ROMCS paging state changed during restore.");
            Require(actualDevice.Control == expectedDevice.Control, "Control latch changed during restore.");
            Require(actualDevice.NetworkOutput == expectedDevice.NetworkOutput, "Network latch changed during restore.");
            Require(actualDevice.MotorMask == expectedDevice.MotorMask, "Motor selection changed during restore.");
            Require(actualDevice.Activity == expectedDevice.Activity, "Activity state changed during restore.");
            for (int drive = 0; drive < SpectrumInterface1Device.DriveCount; drive++)
            {
                Require(actualDevice.Drives[drive] == expectedDevice.Drives[drive],
                    $"Drive {drive + 1} transport state changed during restore.");
            }

            byte[] hostImage = File.ReadAllBytes(path);
            Require(hostImage[20] != 0x99,
                "Restoring a snapshot flushed discarded future cartridge data to the MDR file.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreatePatternedRom()
    {
        byte[] firmware = new byte[SpectrumInterface1Device.RomSize];
        for (int i = 0; i < firmware.Length; i++)
        {
            firmware[i] = (byte)((i * 37 + (i >> 8) + 0x41) & 0xFF);
        }

        return firmware;
    }

    private static byte[] CreatePatternedMdr(bool writeProtected)
    {
        int dataLength = MicrodriveCartridge.MinimumSectorCount * MicrodriveCartridge.SectorLength;
        byte[] image = new byte[dataLength + 1];
        for (int i = 0; i < dataLength; i++)
        {
            image[i] = (byte)((i * 19 + 0x23) & 0xFF);
        }

        image[^1] = writeProtected ? (byte)1 : (byte)0;
        return image;
    }

    private static void SelectDriveOne(SpectrumInterface1Device device)
    {
        // Preserve the ROM's idle/read gate state while clocking an active-low
        // COMMS DATA bit into drive 1's motor-selection shift register.
        device.Write(0x00EF, 0xEE);
        device.Write(0x00EF, 0xEC);
        Require(device.IsMotorRunning(1), "Drive 1 was not selected.");
    }

    private static void WritePreamble(SpectrumInterface1Device device)
    {
        for (int i = 0; i < 10; i++)
        {
            device.Write(0x00E7, 0x00);
        }

        device.Write(0x00E7, 0xFF);
        device.Write(0x00E7, 0xFF);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
