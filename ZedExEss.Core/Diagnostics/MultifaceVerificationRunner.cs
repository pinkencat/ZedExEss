using System;
using System.IO;
using System.Text;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Multiface;

namespace ZedExEss.Diagnostics;

/// <summary>Settings for the Multiface foundation checks.</summary>
public sealed class MultifaceVerificationOptions
{
    public string? OutputPath { get; init; }

    /// <summary>Directory holding mf1.rom, mf128.rom and mf3.rom.</summary>
    public string? RomDirectory { get; init; }
}

/// <summary>
/// Verifies Multiface model gating, the NMI-then-page sequence, the low-memory
/// overlay and the per-variant port decode.
/// </summary>
public static class MultifaceVerificationRunner
{
    public static int Run(MultifaceVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string outputPath = Path.GetFullPath(options.OutputPath ?? "multiface-verification.log");
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        int failed = 0;

        writer.WriteLine("Multiface verification");
        writer.WriteLine($"Output: {outputPath}");
        writer.WriteLine();

        Check("Each model accepts only its own variant", VerifyModelGating, ref failed);
        Check("Port decode per variant", VerifyPortDecode, ref failed);
        Check("Button asserts NMI and pages at the vector", VerifyNmiSequence, ref failed);
        Check("ROM and RAM overlay the low 16 KiB", VerifyMemoryOverlay, ref failed);
        Check("Multiface and DivMMC are mutually exclusive", VerifyDivMmcExclusion, ref failed);

        string romDirectory = options.RomDirectory ?? FindRomDirectory();
        Check("Supplied Multiface ROMs load", () => VerifyRoms(romDirectory), ref failed);
        Check("The firmware brings up its menu on a real machine", () => VerifyLiveMenu(romDirectory), ref failed);

        writer.WriteLine();
        writer.WriteLine(failed == 0 ? "Result: PASS" : $"Result: FAIL ({failed} failed checks)");
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
            }
        }
    }

    private static void VerifyModelGating()
    {
        Require(
            SpectrumModelTraits.SupportedMultiface(SpectrumModel.Spectrum16K) == SpectrumMultifaceModel.One,
            "16K should take a Multiface One.");
        Require(
            SpectrumModelTraits.SupportedMultiface(SpectrumModel.Spectrum48K) == SpectrumMultifaceModel.One,
            "48K should take a Multiface One.");
        Require(
            SpectrumModelTraits.SupportedMultiface(SpectrumModel.Spectrum128K) == SpectrumMultifaceModel.OneTwentyEight,
            "128K should take a Multiface 128.");
        Require(
            SpectrumModelTraits.SupportedMultiface(SpectrumModel.SpectrumPlus2) == SpectrumMultifaceModel.OneTwentyEight,
            "+2 should take a Multiface 128.");
        Require(
            SpectrumModelTraits.SupportedMultiface(SpectrumModel.SpectrumPlus2A) == SpectrumMultifaceModel.Three,
            "+2A should take a Multiface 3.");
        Require(
            SpectrumModelTraits.SupportedMultiface(SpectrumModel.SpectrumPlus3) == SpectrumMultifaceModel.Three,
            "+3 should take a Multiface 3.");
        Require(
            SpectrumModelTraits.SupportedMultiface(SpectrumModel.Pentagon128) == SpectrumMultifaceModel.None,
            "Pentagon takes no Multiface.");
        Require(
            SpectrumModelTraits.SupportedMultiface(SpectrumModel.Scorpion256) == SpectrumMultifaceModel.None,
            "Scorpion takes no Multiface.");

        // The factory must refuse a variant the model does not take, rather than fitting
        // hardware whose ports would collide with the machine's own.
        SpectrumMachine mismatched = CreateMachine(
            SpectrumModel.Spectrum48K,
            SpectrumMultifaceModel.Three,
            new byte[SpectrumMultifaceDevice.RomSize]);
        Require(mismatched.Multiface == null, "A 48K must refuse a Multiface 3.");

        SpectrumMachine matched = CreateMachine(
            SpectrumModel.Spectrum48K,
            SpectrumMultifaceModel.One,
            new byte[SpectrumMultifaceDevice.RomSize]);
        Require(matched.Multiface != null, "A 48K should accept a Multiface One.");

        // A ROM of the wrong size leaves it unfitted rather than half-configured.
        SpectrumMachine shortRom = CreateMachine(
            SpectrumModel.Spectrum48K,
            SpectrumMultifaceModel.One,
            new byte[1024]);
        Require(shortRom.Multiface == null, "An undersized ROM must not fit a Multiface.");
    }

    private static void VerifyPortDecode()
    {
        (SpectrumMultifaceModel Model, byte Enable, byte Disable)[] expected =
        [
            (SpectrumMultifaceModel.One, 0x9F, 0x1F),
            (SpectrumMultifaceModel.OneTwentyEight, 0xBF, 0x3F),
            (SpectrumMultifaceModel.Three, 0x3F, 0xBF)
        ];

        foreach ((SpectrumMultifaceModel model, byte enable, byte disable) in expected)
        {
            var device = new SpectrumMultifaceDevice(model, new byte[SpectrumMultifaceDevice.RomSize]);
            Require(device.EnablePort == enable, $"{model} should page in from ${enable:X2}.");
            Require(device.DisablePort == disable, $"{model} should page out to ${disable:X2}.");
        }

        // Multiface One pages in on a read and out on a write, which is exactly why it
        // can share $1F with a Kempston joystick: the joystick only ever reads it.
        var mf1 = new SpectrumMultifaceDevice(
            SpectrumMultifaceModel.One, new byte[SpectrumMultifaceDevice.RomSize]);
        mf1.RequestNmi();
        Require(
            !mf1.TryReadPort(0x009F, 0, 0, out _),
            "Multiface One never drives the bus.");
        Require(mf1.IsPaged, "Reading the enable port should page Multiface One in.");
        Require(!mf1.TryWritePort(0x001E), "An unrelated write must not be claimed.");
        Require(mf1.TryWritePort(0x001F), "Writing the disable port should be claimed.");
        Require(
            mf1.IsPaged,
            "Writing the disable port is the visibility lock, not the paging control: the "
                + "firmware writes it mid-session and carries on running from its own ROM.");
        Require(!mf1.TryReadPort(0x001F, 0, 0, out _), "Multiface One never drives the bus.");
        Require(!mf1.IsPaged, "Reading the disable port should page Multiface One out.");

        // Now that it is out of the way, the lock hides it from a detection probe.
        mf1.TryWritePort(0x001F);
        Require(!mf1.TryReadPort(0x009F, 0, 0, out _), "Multiface One never drives the bus.");
        Require(!mf1.IsPaged, "A locked Multiface must stay hidden from the enable port.");

        // The 128 reports the screen-page bit so its handler can restore paging.
        var mf128 = new SpectrumMultifaceDevice(
            SpectrumMultifaceModel.OneTwentyEight, new byte[SpectrumMultifaceDevice.RomSize]);
        mf128.RequestNmi();
        Require(
            mf128.TryReadPort(0x00BF, 0x08, 0, out byte screenHigh) && (screenHigh & 0x80) != 0,
            "Multiface 128 should report screen page 7 in bit 7.");
        Require(
            mf128.TryReadPort(0x00BF, 0x00, 0, out byte screenLow) && (screenLow & 0x80) == 0,
            "Multiface 128 should report screen page 5 as a clear bit 7.");

        // Multiface 3 answers with whichever paging latch the high byte selects.
        var mf3 = new SpectrumMultifaceDevice(
            SpectrumMultifaceModel.Three, new byte[SpectrumMultifaceDevice.RomSize]);
        Require(
            mf3.TryReadPort(0x7F3F, 0x12, 0x34, out byte latch7Ffd) && latch7Ffd == 0x12,
            "Multiface 3 should return the 7FFD latch.");
        Require(
            mf3.TryReadPort(0x1F3F, 0x12, 0x34, out byte latch1Ffd) && latch1Ffd == 0x34,
            "Multiface 3 should return the 1FFD latch.");
    }

    private static void VerifyNmiSequence()
    {
        var device = new SpectrumMultifaceDevice(
            SpectrumMultifaceModel.One, new byte[SpectrumMultifaceDevice.RomSize]);

        Require(!device.NmiActive && !device.IsPaged, "A Multiface starts idle and unpaged.");
        Require(device.RequestNmi(), "The first button press should assert NMI.");
        Require(device.NmiActive, "The button should leave NMI asserted.");
        Require(
            !device.IsPaged,
            "Nothing may page in before the vector fetch, or the machine ROM could not service the NMI.");
        device.BeforeOpcodeFetch(0x0038);
        Require(!device.IsPaged, "Only the NMI vector pages a Multiface in.");
        device.BeforeOpcodeFetch(0x0066);
        Require(device.IsPaged, "Fetching $0066 should page the Multiface in.");
        Require(!device.RequestNmi(), "A press while the overlay is up should do nothing.");

        // Reading the disable port is how the firmware hands control back to the machine
        // ROM mid-session, and reading the enable port takes it back again.
        Require(!device.TryReadPort(0x001F, 0, 0, out _), "Multiface One never drives the bus.");
        Require(!device.IsPaged, "Reading the disable port should page the overlay out.");
        Require(!device.TryReadPort(0x009F, 0, 0, out _), "Multiface One never drives the bus.");
        Require(device.IsPaged, "Reading the enable port should page the overlay back in.");

        device.OnRetnExecuted();
        Require(!device.IsPaged && !device.NmiActive, "RETN should release the overlay.");

        // The button has to work a second time. The firmware's return-to-BASIC path
        // leaves through the machine ROM rather than a RETN, so the press that follows
        // it must still assert NMI.
        Require(device.RequestNmi(), "The button should re-arm once the overlay is down.");
        device.BeforeOpcodeFetch(0x0066);
        device.TryReadPort(0x001F, 0, 0, out _);
        Require(device.RequestNmi(), "A press after a page-out should assert NMI again.");
    }

    private static void VerifyMemoryOverlay()
    {
        byte[] rom = new byte[SpectrumMultifaceDevice.RomSize];
        rom[0x0000] = 0xA5;
        rom[0x1FFF] = 0x5A;

        SpectrumMachine machine = CreateMachine(
            SpectrumModel.Spectrum48K, SpectrumMultifaceModel.One, rom);
        SpectrumMultifaceDevice device = machine.Multiface
            ?? throw new InvalidOperationException("The Multiface was not fitted.");

        byte beforeRom = machine.Memory.ReadDirect(0x0000);
        Require(beforeRom != 0xA5, "The Multiface must be invisible until it pages in.");

        device.RequestNmi();
        device.BeforeOpcodeFetch(0x0066);

        Require(machine.Memory.ReadDirect(0x0000) == 0xA5, "The ROM half should overlay $0000.");
        Require(machine.Memory.ReadDirect(0x1FFF) == 0x5A, "The ROM half should reach $1FFF.");

        // The RAM half is writable and the ROM half is not.
        machine.Memory.WriteDirect(0x0000, 0x11);
        Require(machine.Memory.ReadDirect(0x0000) == 0xA5, "The ROM half must ignore writes.");
        machine.Memory.WriteDirect(0x2000, 0x22);
        Require(machine.Memory.ReadDirect(0x2000) == 0x22, "The RAM half should accept writes.");

        device.OnRetnExecuted();
        Require(
            machine.Memory.ReadDirect(0x0000) == beforeRom,
            "Releasing the overlay should restore the machine ROM.");
    }

    private static void VerifyDivMmcExclusion()
    {
        // Both overlay the same low memory, so fitting one alongside the other would be
        // ambiguous. The hosts offer them as one exclusive choice; this records that the
        // core still resolves a mis-configured pair predictably rather than by accident.
        byte[] rom = new byte[SpectrumMultifaceDevice.RomSize];
        rom[0] = 0xC7;

        var memory = new SpectrumMemory(
            SpectrumModel.Spectrum48K,
            RomSet.CreateBlank(SpectrumModelTraits.RomBankCount(SpectrumModel.Spectrum48K)));
        var multiface = new SpectrumMultifaceDevice(SpectrumMultifaceModel.One, rom);
        var divMmc = new SpectrumDivMmcDevice(
            SpectrumDivExpansionMode.DivMmc,
            new byte[8 * 1024],
            128);

        memory.ConfigureMultiface(multiface);
        memory.ConfigureDivExpansion(divMmc);

        multiface.RequestNmi();
        multiface.BeforeOpcodeFetch(0x0066);
        Require(
            memory.ReadDirect(0x0000) == 0xC7,
            "With both configured, the Multiface overlay takes priority.");
    }

    /// <summary>
    /// Runs the real firmware on a real machine: presses the button, and checks the
    /// menu actually comes up.
    /// </summary>
    /// <remarks>
    /// The unit-level checks above all passed while the interface was unusable, because
    /// they only ever exercised the device in isolation. The firmware pages itself out
    /// with an IN before it calls the machine ROM to print, and waits on a HALT for the
    /// ROM's interrupt handler to scan the keyboard; with paging out bound to a port
    /// write instead, it hung there forever with a blank screen.
    /// </remarks>
    private static void VerifyLiveMenu(string romDirectory)
    {
        string machineRom = Path.Combine(romDirectory, "48.rom");
        string firmware = Path.Combine(romDirectory, "mf1.rom");
        if (!File.Exists(machineRom) || !File.Exists(firmware))
        {
            return;
        }

        SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum48K,
            Roms = RomSet.LoadFromFiles([machineRom]),
            RenderEnabled = false,
            MultifaceModel = SpectrumMultifaceModel.One,
            MultifaceRom = File.ReadAllBytes(firmware)
        });
        SpectrumMultifaceDevice device = machine.Multiface
            ?? throw new InvalidOperationException("The Multiface was not fitted.");

        // Let the machine ROM reach its main loop before interrupting it.
        for (int i = 0; i < 400000; i++)
        {
            machine.Emulator.StepInstruction();
        }

        byte[] screen = ReadScreen(machine);
        Require(machine.PressMultifaceButton(), "The button should assert NMI.");

        bool paged = false;
        for (int i = 0; i < 2000 && !paged; i++)
        {
            machine.Emulator.StepInstruction();
            paged = device.IsPaged;
        }

        Require(paged, "The firmware should page in at the NMI vector.");
        Require(
            machine.Cpu.PC < 0x4000,
            $"The firmware should be executing in the overlay, not at ${machine.Cpu.PC:X4}.");

        for (int i = 0; i < 400000; i++)
        {
            machine.Emulator.StepInstruction();
        }

        // The menu is printed through the machine ROM, which is only reachable if the
        // firmware managed to page itself back out.
        Require(!device.IsPaged, "The firmware should have paged out to use the machine ROM.");
        Require(ScreenDifference(machine, screen) > 64, "The Multiface menu was never drawn.");

        // Walk into the save flow. This is where the firmware writes its disable port
        // while still running from its own ROM, so a write that lifted the overlay
        // dropped the CPU into the machine ROM at an address only the firmware has
        // anything at, and wedged there.
        screen = ReadScreen(machine);
        TapKey(machine, SpectrumKey.S);
        TapKey(machine, SpectrumKey.Enter);
        TapKey(machine, SpectrumKey.Y);

        Require(ScreenDifference(machine, screen) > 64, "The save menu was never drawn.");
        Require(CountLivePcs(machine) > 8, "The firmware wedged in the save menu.");

        // A second press has to work: the menu's exit paths do not all end in a RETN.
        Require(machine.PressMultifaceButton(), "The button should work a second time.");
    }

    /// <summary>Holds a key long enough for the firmware to see it, then lets go.</summary>
    private static void TapKey(SpectrumMachine machine, SpectrumKey key)
    {
        machine.Keyboard.SetKeyState(key, true);
        for (int i = 0; i < 120000; i++)
        {
            machine.Emulator.StepInstruction();
        }

        machine.Keyboard.SetKeyState(key, false);
        for (int i = 0; i < 400000; i++)
        {
            machine.Emulator.StepInstruction();
        }
    }

    /// <summary>
    /// Counts the distinct addresses the CPU visits, which separates a firmware waiting
    /// on a key from one stuck on a single instruction.
    /// </summary>
    private static int CountLivePcs(SpectrumMachine machine)
    {
        var seen = new System.Collections.Generic.HashSet<ushort>();
        for (int i = 0; i < 200000; i++)
        {
            machine.Emulator.StepInstruction();
            seen.Add(machine.Cpu.PC);
        }

        return seen.Count;
    }

    private static byte[] ReadScreen(SpectrumMachine machine)
    {
        byte[] screen = new byte[6912];
        for (int i = 0; i < screen.Length; i++)
        {
            screen[i] = machine.Memory.ReadDirect((ushort)(0x4000 + i));
        }

        return screen;
    }

    private static int ScreenDifference(SpectrumMachine machine, byte[] before)
    {
        int changed = 0;
        for (int i = 0; i < before.Length; i++)
        {
            if (machine.Memory.ReadDirect((ushort)(0x4000 + i)) != before[i])
            {
                changed++;
            }
        }

        return changed;
    }

    private static void VerifyRoms(string romDirectory)
    {
        (SpectrumMultifaceModel Model, string File)[] roms =
        [
            (SpectrumMultifaceModel.One, "mf1.rom"),
            (SpectrumMultifaceModel.OneTwentyEight, "mf128.rom"),
            (SpectrumMultifaceModel.Three, "mf3.rom")
        ];

        foreach ((SpectrumMultifaceModel model, string file) in roms)
        {
            Require(
                SpectrumModelTraits.GetMultifaceRomFileName(model) == file,
                $"{model} should map to {file}.");

            string path = Path.Combine(romDirectory, file);
            if (!File.Exists(path))
            {
                continue;
            }

            byte[] image = File.ReadAllBytes(path);
            Require(
                image.Length == SpectrumMultifaceDevice.RomSize,
                $"{file} should be {SpectrumMultifaceDevice.RomSize} bytes, not {image.Length}.");

            // A real Multiface ROM begins with executable code, not blank media.
            Require(
                image[0] != 0x00 && image[0] != 0xFF,
                $"{file} does not look like firmware (first byte ${image[0]:X2}).");
        }
    }

    private static SpectrumMachine CreateMachine(
        SpectrumModel model,
        SpectrumMultifaceModel multiface,
        byte[] rom)
    {
        return SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = model,
            Roms = RomSet.CreateBlank(SpectrumModelTraits.RomBankCount(model)),
            MultifaceModel = multiface,
            MultifaceRom = rom
        });
    }

    private static string FindRomDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "ROMs");

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
