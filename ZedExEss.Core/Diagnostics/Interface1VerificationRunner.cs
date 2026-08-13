using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Interface1;
using ZedExEss.Spectrum.Memory;

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

        if (!string.IsNullOrWhiteSpace(options.RomPath))
        {
            Check("Supplied Interface 1 ROM", () => VerifySuppliedRom(options.RomPath!), ref failed);
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
        device.Write(0x00EF, 0x02);
        device.Write(0x00EF, 0x00);
        Require(device.MotorMask == 0x01 && device.IsMotorRunning(1), "Drive 1 did not start on a falling clock edge.");

        // Shift drive 1 to drive 2 while inserting an off state for drive 1.
        device.Write(0x00EF, 0x03);
        device.Write(0x00EF, 0x01);
        Require(device.MotorMask == 0x02, "Motor state did not shift from drive 1 to drive 2.");
        Require(!device.IsMotorRunning(1) && device.IsMotorRunning(2), "Shifted motor selection is incorrect.");
    }

    private static void VerifySuppliedRom(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] firmware = File.ReadAllBytes(fullPath);
        _ = new SpectrumInterface1Device(firmware);
        Require(firmware.Any(static value => value != 0x00), "Supplied Interface 1 ROM is blank.");
        Require(firmware.Any(static value => value != 0xFF), "Supplied Interface 1 ROM contains only FFh.");
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
