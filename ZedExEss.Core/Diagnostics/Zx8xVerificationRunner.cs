using System.Globalization;
using System.Text;
using ZedExEss.FileHandlers;
using ZedExEss.Machines;
using ZedExEss.Spectrum.Core;
using ZedExEss.Zx8x.Core;
using ZedExEss.Zx8x.Input;
using ZedExEss.Zx8x.Media;
using ZedExEss.Zx8x.Memory;
using ZedExEss.Zx8x.Tape;
using ZedExEss.Zx8x.Video;

namespace ZedExEss.Diagnostics;

public sealed class Zx8xVerificationOptions
{
    public string? OutputPath { get; init; }
    public string? RomDirectory { get; init; }
}

/// <summary>Headless checks for the ZX80/ZX81 family seam and supplied firmware.</summary>
public static class Zx8xVerificationRunner
{
    public static int Run(Zx8xVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string outputPath = Path.GetFullPath(options.OutputPath ?? "zx8x-verification.log");
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        int failed = 0;
        writer.WriteLine("ZX80/ZX81 foundation verification");
        writer.WriteLine($"Output: {outputPath}");
        writer.WriteLine();

        Check("Machine-family descriptors", VerifyMachineDescriptors, ref failed);
        Check("ZX8x ROM descriptors", VerifyRomDescriptors, ref failed);
        Check("ZX8x ROM size rejection", VerifyRomSizeRejection, ref failed);
        Check("ZX80 ordinary memory decoding", VerifyZx80MemoryMap, ref failed);
        Check("ZX81 ordinary memory decoding", VerifyZx81MemoryMap, ref failed);
        Check("ZX8x 16 KiB expansion decoding", VerifyExpansionMemoryMap, ref failed);
        Check("ZX8x keyboard matrix", VerifyKeyboardMatrix, ref failed);
        Check("ZX8x keyboard/cassette port", VerifyKeyboardPort, ref failed);
        Check("ZX8x timestamped MIC output", VerifyCassetteOutputTiming, ref failed);
        Check("ZX8x optional cassette audio", VerifyCassetteAudio, ref failed);
        Check("ZX8x turbo-to-realtime audio handoff", VerifyAudioOwnerHandoff, ref failed);
        Check("ZX8x ROM-driven cassette feedback", VerifyCassetteFeedback, ref failed);
        Check("ZX8x TZX clock conversion and transport", VerifyTzxPlayback, ref failed);
        Check("ZX81 retrace and NMI controls", VerifyZx81IoControls, ref failed);
        Check("ZX8x specialized CPU execution", VerifyCpuExecution, ref failed);
        Check("ZX8x display-file M1 substitution", VerifyDisplayFetch, ref failed);
        Check("ZX8x refresh-address display interrupt", VerifyRefreshInterrupt, ref failed);
        Check("ZX81 207-T line and SLOW-mode NMI scheduling", VerifySlowModeTiming, ref failed);
        Check("ZX81 FAST mode and vertical-retrace hold", VerifyFastModeTiming, ref failed);
        Check("ZX8x frame and short-retrace classification", VerifyRetraceClassification, ref failed);
        Check("ZX8x character raster generation", VerifyCharacterRaster, ref failed);
        Check("ZX8x host frame surface", VerifyHostFrameSurface, ref failed);
        Check("ZX80 O and ZX81 P/81 program images", VerifyProgramImages, ref failed);
        if (!string.IsNullOrWhiteSpace(options.RomDirectory))
        {
            Check("Supplied ZX80/ZX81 ROM files", () => VerifyRomFiles(options.RomDirectory!), ref failed);
            Check("Supplied ZX80/ZX81 ROM boot execution", () => VerifyRomBoots(options.RomDirectory!), ref failed);
            Check("Supplied ZX80/ZX81 ROM boot display", () => VerifyRomBootDisplays(options.RomDirectory!), ref failed);
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
            }
        }
    }

    private static void VerifyMachineDescriptors()
    {
        MachineDescriptor zx80 = Zx8xModelDescriptors.ForModel(Zx8xModel.Zx80);
        MachineDescriptor zx81 = Zx8xModelDescriptors.ForModel(Zx8xModel.Zx81);
        Require(zx80.Family == MachineFamily.Zx8x && zx81.Family == MachineFamily.Zx8x,
            "ZX8x models were assigned to the wrong machine family.");
        Require(zx80.Id != zx81.Id, "ZX80 and ZX81 descriptors share an ID.");
        Require(zx80.CpuClockHz == 3_250_000 && zx81.CpuClockHz == 3_250_000,
            "ZX8x descriptor clock is incorrect.");

        MachineDescriptor spectrum = SpectrumMachineDescriptors.ForModel(SpectrumModel.Spectrum48K);
        Require(spectrum.Family == MachineFamily.Spectrum,
            "Adding the ZX8x family changed the Spectrum descriptor family.");
        Require(spectrum.CpuClockHz == SpectrumModelTraits.CpuClockHz(SpectrumModel.Spectrum48K),
            "Spectrum descriptor no longer reflects its concrete timing profile.");
    }

    private static void VerifyRomDescriptors()
    {
        Zx8xRomDescriptor zx80 = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx80);
        Zx8xRomDescriptor zx81Standard = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81, Zx81RomRevision.Standard);
        Zx8xRomDescriptor zx81Improved = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81, Zx81RomRevision.Improved);

        Require(zx80.SizeBytes == 4096 && zx80.FileName == "zx80.rom", "ZX80 ROM requirement is incorrect.");
        Require(zx81Standard.SizeBytes == 8192 && zx81Improved.SizeBytes == 8192,
            "ZX81 ROM requirements are not 8 KiB.");
        Require(zx81Standard.FileName != zx81Improved.FileName,
            "ZX81 firmware revisions resolve to the same host file.");
    }

    private static void VerifyHostFrameSurface()
    {
        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81);
        Zx8xRomImage rom = Zx8xRomImage.Load(new byte[descriptor.SizeBytes], descriptor);
        Zx8xMachine machine = Zx8xMachineFactory.Create(Zx8xModel.Zx81, rom);
        int completedEvents = 0;
        machine.FrameCompleted += () => completedEvents++;

        ulong cycleBefore = machine.Cpu.Cyc;
        machine.RunFrame();
        Require(machine.Cpu.Cyc > cycleBefore, "A frame-clock slice did not advance ZX8x execution.");

        // A display frame exists only after the guest emits two valid vertical syncs:
        // the second publishes the image built following the first.
        machine.Renderer.BeginFrame(1);
        machine.Renderer.BeginFrame(2);
        machine.StepInstruction();
        var frame = new int[machine.FrameWidth * machine.FrameHeight];
        Require(machine.TryCopyFrame(frame), "A completed ZX8x frame was not available to the host.");
        Require(completedEvents == 1, $"One frame produced {completedEvents} host notifications.");
        Require(frame.All(pixel => ((uint)pixel & 0xFF000000u) == 0xFF000000u),
            "ZX8x host frame contains non-opaque pixels.");

        machine.SetPaused(true);
        ulong pausedCycle = machine.Cpu.Cyc;
        machine.RunFrame();
        Require(machine.Cpu.Cyc == pausedCycle, "Paused ZX8x host execution advanced the CPU.");
    }

    private static void VerifyProgramImages()
    {
        Zx8xMachine zx80 = CreateTestMachine(Zx8xModel.Zx80);
        byte[] oImage = new byte[0x44];
        oImage[0x0A] = 0x40;
        oImage[0x0B] = 0x40; // E_LINE=4040h: the final four bytes are transfer garbage.
        oImage[0x20] = 0x5A;
        oImage.AsSpan(0x40).Fill(0xEE);
        Zx8xProgramImageLoadResult oResult = Zx8xProgramImageLoader.Load(zx80, oImage, ".o");
        Require(oResult.LoadAddress == 0x4000 && oResult.LoadedBytes == 0x40 && oResult.IgnoredTrailingBytes == 4,
            "ZX80 O image boundaries were not derived from E_LINE.");
        Require(zx80.Memory.Read(0x4020) == 0x5A && zx80.Memory.Read(0x4040) == 0,
            "ZX80 O image data or trailing-byte removal is incorrect.");
        Require(zx80.Cpu.PC == 0x0283 && zx80.Cpu.SP == 0x7FFC && zx80.Cpu.I == 0x0E
                && zx80.Cpu.InterruptModeValue == 1,
            "ZX80 post-load CPU state does not return to the ROM main loop.");

        byte[] pImage = new byte[0x103];
        pImage[0x0B] = 0x09;
        pImage[0x0C] = 0x41; // E_LINE=4109h: 100h meaningful bytes.
        pImage[0x80] = 0xA5;
        pImage.AsSpan(0x100).Fill(0xCC);
        Zx8xMachine zx81 = CreateTestMachine(Zx8xModel.Zx81);
        Zx8xProgramImageLoadResult pResult = Zx8xProgramImageLoader.Load(zx81, pImage, ".p");
        Require(pResult.Format == Zx8xProgramImageFormat.Zx81P && pResult.LoadedBytes == 0x100
                && pResult.IgnoredTrailingBytes == 3,
            "ZX81 P image boundaries were not derived from E_LINE.");
        Require(zx81.Memory.Read(0x4089) == 0xA5 && zx81.Memory.Read(0x4109) == 0,
            "ZX81 P image data or trailing-byte removal is incorrect.");
        Require(zx81.Memory.Read(0x4000) == 0xFF && zx81.Memory.Read(0x4004) == 0x00
                && zx81.Memory.Read(0x4005) == 0x80,
            "ZX81 low system variables were not reconstructed for 16 KiB RAM.");
        Require(zx81.Cpu.PC == 0x0207 && zx81.Cpu.SP == 0x7FFC && zx81.Cpu.I == 0x1E
                && zx81.Cpu.InterruptModeValue == 1 && zx81.Io.NmiGeneratorEnabled,
            "ZX81 post-load CPU/SLOW-mode state is incorrect.");

        Zx8xMachine zx81Alias = CreateTestMachine(Zx8xModel.Zx81);
        Zx8xProgramImageLoadResult aliasResult = Zx8xProgramImageLoader.Load(zx81Alias, pImage, ".81");
        Require(aliasResult.Format == Zx8xProgramImageFormat.Zx81_81
                && zx81Alias.Memory.Read(0x4089) == 0xA5,
            "ZX81 .81 was not handled as the P-compatible raw memory format.");

        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81);
        Zx8xMachine oneKiB = Zx8xMachineFactory.Create(
            Zx8xModel.Zx81,
            Zx8xRomImage.Load(new byte[descriptor.SizeBytes], descriptor),
            ramConfiguration: Zx8xRamConfiguration.Internal1K);
        byte[] oversized = new byte[0x500];
        oversized[0x0B] = 0x09;
        oversized[0x0C] = 0x45;
        bool rejected = false;
        try
        {
            _ = Zx8xProgramImageLoader.Load(oneKiB, oversized, ".p");
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Require(rejected, "A ZX81 image larger than the selected 1 KiB RAM was accepted.");
    }

    private static void VerifyTzxPlayback()
    {
        var io = new Zx8xIoDevice(Zx8xModel.Zx81);
        var cassette = new Zx8xCassetteDevice(io);
        var session = new Zx8xTapeSession(cassette);
        string path = Path.Combine(Path.GetTempPath(), $"zedexess-zx8x-{Guid.NewGuid():N}.tzx");
        try
        {
            // Two 140-reference-T-state tone pulses. At the ZX8x 3.25 MHz
            // clock each pulse must occupy exactly 130 machine T-states.
            byte[] image =
            [
                (byte)'Z', (byte)'X', (byte)'T', (byte)'a', (byte)'p', (byte)'e', (byte)'!',
                0x1A, 0x01, 0x14,
                0x12, 140, 0, 2, 0
            ];
            File.WriteAllBytes(path, image);
            session.LoadTzx(path, currentMachineTstate: 0);
            Require(session.Loader?.Blocks.Count == 1, "The ZX8x tape session did not decode the TZX tone block.");

            TapeStopReason? stopped = null;
            session.PlaybackStopped += (_, reason) => stopped = reason;
            session.Play(currentMachineTstate: 0);
            Require(!cassette.InputHigh, "TZX playback did not start at the first pulse level.");

            session.AdvanceTo(129);
            Require(!cassette.InputHigh, "The first TZX edge arrived before 130 ZX8x T-states.");
            session.AdvanceTo(130);
            Require(cassette.InputHigh, "The 3.5 MHz TZX pulse was not converted to 130 ZX8x T-states.");

            session.AdvanceTo(259);
            Require(session.Loader?.IsPlaying == true, "The second pulse ended one ZX8x T-state early.");
            session.AdvanceTo(260);
            Require(session.Loader?.IsPlaying == false && stopped == TapeStopReason.EndOfTape,
                "The ZX8x TZX transport did not stop exactly at end of tape.");

            session.Rewind(500);
            Require(session.Loader?.CurrentBlockIndex == 0 && !cassette.InputHigh,
                "Rewinding ZX8x TZX media did not restore the first block and idle EAR level.");
        }
        finally
        {
            session.Eject(0);
            File.Delete(path);
        }
    }

    private static void VerifyRomSizeRejection()
    {
        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx80);
        bool rejected = false;
        try
        {
            _ = Zx8xRomImage.Load(new byte[descriptor.SizeBytes - 1], descriptor);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Require(rejected, "An incorrectly sized ZX80 ROM was accepted.");
    }

    private static void VerifyZx80MemoryMap()
    {
        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx80);
        byte[] bytes = CreateAddressPattern(descriptor.SizeBytes);
        var memory = new Zx8xMemory(Zx8xModel.Zx80, Zx8xRomImage.Load(bytes, descriptor));

        Require(memory.Read(0x0000) == bytes[0x0000], "ZX80 ROM base is not readable.");
        Require(memory.Read(0x1000) == bytes[0x0000] && memory.Read(0x3ABC) == bytes[0x0ABC],
            "ZX80 4 KiB ROM does not repeat through 0000-3FFF.");
        Require(memory.Read(0x8000) == bytes[0x0000] && memory.Read(0xBABC) == bytes[0x0ABC],
            "ZX80 ROM is not mirrored through the A15 display window.");

        memory.Write(0x4123, 0x5A);
        Require(memory.Read(0x4123) == 0x5A && memory.Read(0x4523) == 0x5A,
            "ZX80 internal 1 KiB RAM does not repeat in the lower RAM window.");
        Require(memory.Read(0xC123) == 0x5A && memory.Read(0xE523) == 0x5A,
            "ZX80 internal RAM is not mirrored through A15.");

        memory.Write(0x0123, 0x00);
        Require(memory.Read(0x0123) == bytes[0x0123], "A write changed ZX80 ROM.");
    }

    private static void VerifyZx81MemoryMap()
    {
        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81);
        byte[] bytes = CreateAddressPattern(descriptor.SizeBytes);
        var memory = new Zx8xMemory(Zx8xModel.Zx81, Zx8xRomImage.Load(bytes, descriptor));

        Require(memory.Read(0x0000) == bytes[0x0000] && memory.Read(0x1ABC) == bytes[0x1ABC],
            "ZX81 8 KiB ROM base window is incorrect.");
        Require(memory.Read(0x2000) == bytes[0x0000] && memory.Read(0x3ABC) == bytes[0x1ABC],
            "ZX81 ROM is not mirrored at 2000-3FFF.");
        Require(memory.Read(0xA000) == bytes[0x0000] && memory.Read(0xBABC) == bytes[0x1ABC],
            "ZX81 ROM is not mirrored through A15.");

        memory.Write(0x43FF, 0xA5);
        Require(memory.Read(0x47FF) == 0xA5 && memory.Read(0xFFFF) == 0xA5,
            "ZX81 internal 1 KiB RAM mirroring is incorrect.");
    }

    private static void VerifyExpansionMemoryMap()
    {
        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81);
        var memory = new Zx8xMemory(
            Zx8xModel.Zx81,
            Zx8xRomImage.Load(CreateAddressPattern(descriptor.SizeBytes), descriptor),
            Zx8xRamConfiguration.Expansion16K);

        memory.Write(0x4000, 0x11);
        memory.Write(0x7FFF, 0x22);
        Require(memory.RamSizeBytes == 16 * 1024, "The expansion did not replace RAM with 16 KiB.");
        Require(memory.Read(0x4000) == 0x11 && memory.Read(0x7FFF) == 0x22,
            "The 16 KiB lower RAM window aliases distinct addresses.");
        Require(memory.Read(0xC000) == 0x11 && memory.Read(0xFFFF) == 0x22,
            "The 16 KiB RAM is not mirrored through the display-file address window.");

        memory.ClearRam(0xCC);
        Require(memory.Read(0x4000) == 0xCC && memory.Read(0x7FFF) == 0xCC,
            "Clearing physical expansion RAM did not cover the complete device.");
    }

    private static byte[] CreateAddressPattern(int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 37 + (i >> 8)) & 0xFF);
        }

        return bytes;
    }

    private static void VerifyKeyboardMatrix()
    {
        var keyboard = new Zx8xKeyboard();
        Require(keyboard.ReadRows(0xFE) == 0x1F, "Released row A8 did not read high.");

        keyboard.SetKeyState(Zx8xKey.Shift, true);
        keyboard.SetKeyState(Zx8xKey.C, true);
        keyboard.SetKeyState(Zx8xKey.NewLine, true);
        Require(keyboard.ReadRows(0xFE) == 0x16, "SHIFT/C row bits are incorrect.");
        Require(keyboard.ReadRows(0xBF) == 0x1E, "NEWLINE row bit is incorrect.");
        Require(keyboard.ReadRows(0xBE) == 0x16,
            "Selecting multiple keyboard rows did not AND their columns.");

        keyboard.SetKeyState(Zx8xKey.C, false);
        Require(keyboard.ReadRows(0xFE) == 0x1E, "Releasing one key disturbed another key.");
        keyboard.ReleaseAll();
        Require(keyboard.ReadRows(0x00) == 0x1F, "ReleaseAll left a matrix key pressed.");
    }

    private static void VerifyKeyboardPort()
    {
        var io = new Zx8xIoDevice(Zx8xModel.Zx80);
        io.Keyboard.SetKeyState(Zx8xKey.Q, true);
        io.CassetteInputHigh = true;
        io.Is50Hz = true;

        Require(io.ReadPort(0xFBFE) == 0xFE,
            "ZX8x port did not combine Q, the 50 Hz link and cassette input.");
        Require(io.ReadPort(0xFBFF) == 0xFF, "An odd I/O port incorrectly selected the keyboard.");
        Require(io.VerticalRetraceActive, "ZX80 keyboard read did not start vertical retrace.");

        io.WritePort(0x0000, 0x00);
        Require(!io.VerticalRetraceActive, "ZX80 I/O write did not end vertical retrace.");
    }

    private static void VerifyZx81IoControls()
    {
        var io = new Zx8xIoDevice(Zx8xModel.Zx81);
        _ = io.ReadPort(0xFEFE);
        Require(io.VerticalRetraceActive, "ZX81 read with NMIs disabled did not start retrace.");

        io.WritePort(0x00FE, 0x00);
        Require(io.NmiGeneratorEnabled && !io.VerticalRetraceActive,
            "ZX81 FEh write did not enable NMIs and end retrace.");
        _ = io.ReadPort(0xFEFE);
        Require(!io.VerticalRetraceActive,
            "ZX81 keyboard read incorrectly started retrace while NMIs were enabled.");

        io.WritePort(0x00FD, 0x00);
        Require(!io.NmiGeneratorEnabled, "ZX81 FDh write did not disable NMIs.");
        _ = io.ReadPort(0xFEFE);
        Require(io.VerticalRetraceActive, "ZX81 read did not restart retrace after disabling NMIs.");
    }

    private static void VerifyCassetteOutputTiming()
    {
        Zx8xMachine machine = CreateCassetteProgramMachine(sampleRate: 44_100);
        var transitions = new List<(ulong Tstate, bool High)>();
        machine.Cassette.OutputLevelChanged += (tstate, high) => transitions.Add((tstate, high));

        machine.StepInstruction(); // IN A,(FE): retrace/MIC becomes low at input T4.
        machine.StepInstruction(); // OUT (FF),A: retrace/MIC becomes high after output T1.

        Require(transitions.Count == 2,
            $"Expected two MIC edges, observed {transitions.Count}.");
        Require(transitions[0] == (10UL, false),
            $"Even-port read MIC edge was {transitions[0]}, expected (10, false).");
        Require(transitions[1] == (19UL, true),
            $"I/O-write MIC edge was {transitions[1]}, expected (19, true).");

        machine.Cassette.InputHigh = true;
        Require((machine.Io.ReadPort(0x00FE) & 0x80) != 0,
            "Cassette device input was not presented on port bit 7.");
    }

    private static void VerifyCassetteAudio()
    {
        const int sampleCount = 30;
        var silent = new short[sampleCount];
        Zx8xMachine silentMachine = CreateCassetteProgramMachine(Zx8xModelDescriptors.CpuClockHz);
        silentMachine.ReadSamples(silent, 0, silent.Length);
        Require(silent.AsSpan().IndexOfAnyExcept((short)0) < 0,
            "ZX8x generated audible output while cassette monitoring was disabled.");

        var monitored = new short[sampleCount];
        Zx8xMachine monitoredMachine = CreateCassetteProgramMachine(Zx8xModelDescriptors.CpuClockHz);
        monitoredMachine.CassetteMonitorEnabled = true;
        monitoredMachine.ReadSamples(monitored, 0, monitored.Length);

        Require(monitored.AsSpan(0, 10).IndexOfAnyInRange((short)1, short.MaxValue) >= 0
                && monitored.AsSpan(0, 10).IndexOfAnyExcept(monitored[0]) < 0,
            "Idle MIC-high interval was not rendered as a stable positive level.");
        Require(monitored.AsSpan(10, 9).IndexOfAnyInRange(short.MinValue, (short)-1) >= 0
                && monitored.AsSpan(10, 9).IndexOfAnyExcept(monitored[10]) < 0,
            "Retrace/MIC-low interval was not rendered as a stable negative level.");
        Require(monitored.AsSpan(19).IndexOfAnyInRange((short)1, short.MaxValue) >= 0
                && monitored.AsSpan(19).IndexOfAnyExcept(monitored[19]) < 0,
            "Post-OUT MIC-high interval did not return to a stable positive level.");
    }

    private static Zx8xMachine CreateCassetteProgramMachine(int sampleRate)
    {
        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81);
        byte[] rom = new byte[descriptor.SizeBytes];
        rom[0] = 0xDB; // IN A,(FE): starts vertical retrace while NMIs are disabled.
        rom[1] = 0xFE;
        rom[2] = 0xD3; // OUT (FF),A: terminates retrace and raises MIC.
        rom[3] = 0xFF;
        return Zx8xMachineFactory.Create(
            Zx8xModel.Zx81,
            Zx8xRomImage.Load(rom, descriptor),
            sampleRate: sampleRate);
    }

    private static void VerifyAudioOwnerHandoff()
    {
        Zx8xMachine machine = CreateCassetteProgramMachine(44_100);
        var samples = new short[16];
        machine.ReadSamples(samples, 0, samples.Length);

        // This models an unthrottled owner advancing a renderer whose capture
        // had previously been started by realtime audio.
        machine.RunForTstates(100_000);
        machine.Audio.DiscardPendingSamples(machine.Cpu.Cyc);
        ulong beforeRealtimeRead = machine.Cpu.Cyc;
        machine.ReadSamples(samples, 0, samples.Length);

        Require(machine.Cpu.Cyc > beforeRealtimeRead,
            "Realtime audio drained stale turbo PCM instead of resuming CPU execution.");
    }

    private static void VerifyCassetteFeedback()
    {
        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81);
        byte[] rom = new byte[descriptor.SizeBytes];
        byte[] outputLoop =
        [
            0xDB, 0xFE,       // IN A,(FE): software sync/MIC low.
            0xD3, 0xFF,       // OUT (FF),A: software sync/MIC high.
            0xC3, 0x00, 0x00  // JP 0000: continue the dense tape-like edge train.
        ];
        outputLoop.CopyTo(rom, 0);

        Zx8xMachine machine = Zx8xMachineFactory.Create(
            Zx8xModel.Zx81,
            Zx8xRomImage.Load(rom, descriptor),
            sampleRate: 44_100);
        var samples = new short[2_000];
        machine.ReadSamples(samples, 0, samples.Length);

        Require(machine.Cassette.OutputActivityActive,
            "Dense ROM cassette output was not distinguished from an isolated display sync pulse.");
        Require(samples.AsSpan().IndexOfAnyExcept((short)0) >= 0,
            "Dense ROM cassette output did not enable audible monitoring.");
        Require(machine.Renderer.CompletedFrameNumber > 0,
            "Dense ROM cassette output did not publish a composite raster frame.");

        var frame = new int[machine.FrameWidth * machine.FrameHeight];
        Require(machine.TryCopyFrame(frame), "Cassette feedback frame was unavailable to the host.");
        Require(frame.Any(pixel => unchecked((uint)pixel) == 0xFF000000u)
                && frame.Any(pixel => unchecked((uint)pixel) == 0xFFFFFFFFu),
            "Cassette feedback frame did not contain both phases of the MIC/sync waveform.");
    }

    private static void VerifyCpuExecution()
    {
        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81);
        byte[] rom = new byte[descriptor.SizeBytes];
        byte[] program =
        [
            0x3E, 0x5A,       // LD A,5A
            0x32, 0x00, 0x40, // LD (4000),A
            0x3A, 0x00, 0x40, // LD A,(4000)
            0xD3, 0xFE        // OUT (FE),A
        ];
        program.CopyTo(rom, 0);

        var memory = new Zx8xMemory(Zx8xModel.Zx81, Zx8xRomImage.Load(rom, descriptor));
        var io = new Zx8xIoDevice(Zx8xModel.Zx81);
        var memoryBus = new Zx8xCpuMemoryBus(memory);
        var cpu = new Zx8xCpu(memoryBus, new Zx8xCpuPortBus(io));
        cpu.Z80Init();

        cpu.Z80Step();
        cpu.Z80Step();
        cpu.Z80Step();
        cpu.Z80Step();

        Require(memory.Read(0x4000) == 0x5A && cpu.A == 0x5A,
            "ZX8x CPU did not execute ROM memory reads and RAM writes.");
        Require(io.NmiGeneratorEnabled,
            "ZX8x CPU OUT cycle did not reach the concrete ZX81 port device.");
        Require(cpu.PC == program.Length, "ZX8x CPU stopped at the wrong program address.");
        Require(cpu.Cyc == 44, "ZX8x CPU instruction timing changed during bus specialization.");
    }

    private static void VerifyDisplayFetch()
    {
        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81);
        var memory = new Zx8xMemory(
            Zx8xModel.Zx81,
            Zx8xRomImage.Load(new byte[descriptor.SizeBytes], descriptor));
        var memoryBus = new Zx8xCpuMemoryBus(memory);
        var sink = new DisplayFetchSink();
        memoryBus.ConfigureDisplaySink(sink);
        var cpu = new Zx8xCpu(
            memoryBus,
            new Zx8xCpuPortBus(new Zx8xIoDevice(Zx8xModel.Zx81)));
        cpu.Z80Init();
        cpu.I = 0x1E;

        memory.Write(0x4000, 0x85); // Inverse character 05, bit 6 clear.
        memory.Write(0x4001, 0x76); // HALT display-line terminator, bit 6 set.
        cpu.PC = 0xC000;
        cpu.Z80Step();

        Require(cpu.PC == 0xC001 && sink.Fetches.Count == 1,
            "Display byte was not replaced by one CPU NOP.");
        Zx8xDisplayFetch fetch = sink.Fetches[0];
        Require(fetch.TState == 0 && fetch.Address == 0xC000 && fetch.DisplayByte == 0x85,
            "Display fetch event did not preserve its bus address, byte and timing.");
        Require(fetch.CharacterCode == 5 && fetch.Inverse && fetch.I == 0x1E,
            "Display fetch did not expose character-generator inputs.");

        cpu.Z80Step();
        Require(cpu.IsHalted && cpu.PC == 0xC002,
            "A bit-6-set display terminator was incorrectly replaced by NOP.");
        Require(sink.Fetches.Count == 1, "HALT terminator incorrectly emitted a character fetch.");
    }

    private static void VerifySlowModeTiming()
    {
        Zx8xMachine machine = CreateTestMachine(Zx8xModel.Zx81);
        machine.PortBus.WriteUncontended(0x00FE, 0x00);

        int instructions = 0;
        while (machine.VideoTiming.NmiPulseCount == 0 && instructions++ < 100)
        {
            machine.StepInstruction();
        }

        Require(machine.VideoTiming.NmiPulseCount == 1,
            "ZX81 SLOW mode did not generate an NMI at horizontal sync.");
        Require(machine.Cpu.PC == 0x0066,
            "ZX81 horizontal NMI was not serviced at the completed instruction boundary.");
        Require(machine.Cpu.Cyc == 217,
            "ZX81 first NMI did not include the remaining HSync WAIT and 11-T NMI entry.");
        Require(machine.VideoTiming.RasterLine == 1,
            "ZX81 line counter did not cross its 207-T boundary during NMI entry.");
    }

    private static void VerifyRefreshInterrupt()
    {
        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81);
        byte[] rom = new byte[descriptor.SizeBytes];
        rom[0] = 0xFB; // EI
        rom[1] = 0x00; // NOP: completes EI delay
        rom[2] = 0x76; // HALT: R wraps to 00 and drives INT low

        var memory = new Zx8xMemory(Zx8xModel.Zx81, Zx8xRomImage.Load(rom, descriptor));
        var memoryBus = new Zx8xCpuMemoryBus(memory);
        var cpu = new Zx8xCpu(memoryBus, new Zx8xCpuPortBus(new Zx8xIoDevice(Zx8xModel.Zx81)));
        cpu.Z80Init();
        cpu.SetInterruptState(1, iff1: false, iff2: false);
        cpu.R = 0x7D;

        cpu.Z80Step();
        memoryBus.CommitRefreshInterruptAssertion();
        cpu.Z80Step();
        memoryBus.CommitRefreshInterruptAssertion();
        cpu.Z80Step();
        memoryBus.CommitRefreshInterruptAssertion();
        cpu.Z80Step();
        memoryBus.CommitRefreshInterruptAssertion();

        Require(cpu.PC == 0x0038 && !cpu.IsHalted,
            "R bit 6 did not release HALT through the ZX8x maskable display interrupt.");
        Require(cpu.Cyc == 29,
            "ZX8x display interrupt was not deferred to the boundary after its asserting refresh cycle.");
    }

    private static void VerifyFastModeTiming()
    {
        Zx8xMachine machine = CreateTestMachine(Zx8xModel.Zx81);
        machine.PortBus.WriteUncontended(0x00FD, 0x00); // NMI generator off: FAST mode.
        machine.RunForTstates(500);

        Require(machine.VideoTiming.NmiPulseCount == 0,
            "ZX81 FAST mode generated horizontal NMIs.");
        Require(machine.VideoTiming.RasterLine >= 2,
            "Free-running line counter stopped while FAST-mode NMIs were disabled.");

        int heldLine = machine.VideoTiming.RasterLine;
        _ = machine.PortBus.ReadUncontended(0x00FE); // Begin vertical retrace and hold counter.
        machine.RunForTstates(500);
        Require(!machine.VideoTiming.CounterRunning && machine.VideoTiming.RasterLine == heldLine,
            "Vertical retrace did not hold the horizontal line counter.");
    }

    private static void VerifyCharacterRaster()
    {
        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81);
        byte[] rom = new byte[descriptor.SizeBytes];
        rom[0x1E10] = 0x80; // Character 2, raster row 0: left-most ink pixel.
        Zx8xMachine machine = Zx8xMachineFactory.Create(
            Zx8xModel.Zx81,
            Zx8xRomImage.Load(rom, descriptor));

        _ = machine.PortBus.ReadUncontended(0x00FE);
        machine.Cpu.AddWaitStates(machine.VideoTiming.Timing.VerticalSyncLines
            * machine.VideoTiming.Timing.TstatesPerLine);
        machine.PortBus.WriteUncontended(0x00FE, 0); // End VSync and begin rendering frame 1.
        ulong visibleLineStart = machine.Cpu.Cyc + (ulong)(machine.VideoTiming.Timing.UpperBorderLines
            * machine.VideoTiming.Timing.TstatesPerLine);
        // DFETCH horizontal placement is selected by refresh A0-A4, not by the
        // absolute host raster timestamp. DFh is the first paper byte and E0h
        // the second in the ROM's standard 32-column display cadence.
        var normal = new Zx8xDisplayFetch(visibleLineStart, 0xC000, 0x02, 0x1E, 0xDF);
        var inverse = new Zx8xDisplayFetch(visibleLineStart + 4, 0xC001, 0x82, 0x1E, 0xE0);
        machine.VideoTiming.OnDisplayFetch(in normal);
        machine.VideoTiming.OnDisplayFetch(in inverse);

        machine.Cpu.AddWaitStates(machine.VideoTiming.Timing.NominalTstatesPerFrame);
        machine.PortBus.WriteUncontended(0x00FD, 0); // Disable NMI so FE read starts retrace.
        _ = machine.PortBus.ReadUncontended(0x00FE);
        machine.Cpu.AddWaitStates(machine.VideoTiming.Timing.VerticalSyncLines
            * machine.VideoTiming.Timing.TstatesPerLine);
        machine.PortBus.WriteUncontended(0x00FF, 0); // Begin frame 2, publishing frame 1.
        ReadOnlySpan<byte> frame = machine.Renderer.FrameBuffer.Span;
        Require(frame[0] == 0x00 && frame[1] == 0xFF,
            "Normal ZX8x glyph bits were not converted to black-on-white pixels.");
        Require(frame[8] == 0xFF && frame[9] == 0x00,
            "Inverse ZX8x display byte did not invert the glyph pixels.");
    }

    private static void VerifyRetraceClassification()
    {
        Zx8xMachine machine = CreateTestMachine(Zx8xModel.Zx81);

        _ = machine.PortBus.ReadUncontended(0x00FE);
        machine.Cpu.AddWaitStates(24);
        machine.PortBus.WriteUncontended(0x00FF, 0);
        Require(machine.VideoTiming.FrameNumber == 0,
            "A short pseudo-hires line-counter reset was mistaken for a vertical frame pulse.");
        Require(machine.VideoTiming.CharacterLine == 0,
            "A short retrace did not reset the character-generator line counter.");

        _ = machine.PortBus.ReadUncontended(0x00FE);
        machine.Cpu.AddWaitStates(machine.VideoTiming.Timing.VerticalSyncLines
            * machine.VideoTiming.Timing.TstatesPerLine);
        machine.PortBus.WriteUncontended(0x00FF, 0);
        Require(machine.VideoTiming.FrameNumber == 1,
            "A sustained vertical retrace did not begin a new frame.");
    }

    private static Zx8xMachine CreateTestMachine(Zx8xModel model)
    {
        Zx8xRomDescriptor descriptor = Zx8xModelDescriptors.GetRom(model);
        return Zx8xMachineFactory.Create(model, Zx8xRomImage.Load(new byte[descriptor.SizeBytes], descriptor));
    }

    private sealed class DisplayFetchSink : IZx8xDisplayFetchSink
    {
        public List<Zx8xDisplayFetch> Fetches { get; } = [];

        public void OnDisplayFetch(in Zx8xDisplayFetch fetch)
        {
            Fetches.Add(fetch);
        }
    }

    private static void VerifyRomFiles(string directory)
    {
        string fullDirectory = Path.GetFullPath(directory);
        Zx8xRomDescriptor[] descriptors =
        [
            Zx8xModelDescriptors.GetRom(Zx8xModel.Zx80),
            Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81, Zx81RomRevision.Standard),
            Zx8xModelDescriptors.GetRom(Zx8xModel.Zx81, Zx81RomRevision.Improved)
        ];

        foreach (Zx8xRomDescriptor descriptor in descriptors)
        {
            string path = Path.Combine(fullDirectory, descriptor.FileName);
            Require(File.Exists(path), $"Required ROM does not exist: {path}");
            Zx8xRomImage image = Zx8xRomImage.Load(path, descriptor);
            Require(image.Bytes.Span.IndexOfAnyExcept((byte)0x00) >= 0, $"ROM {descriptor.FileName} contains only zeroes.");
            Require(image.Bytes.Span.IndexOfAnyExcept((byte)0xFF) >= 0, $"ROM {descriptor.FileName} contains only FFh.");
            Require(image.ReadMirrored((ushort)image.Length) == image.Bytes.Span[0],
                $"ROM {descriptor.FileName} did not mirror at its natural size.");
        }
    }

    private static void VerifyRomBoots(string directory)
    {
        string fullDirectory = Path.GetFullPath(directory);
        (Zx8xModel Model, Zx81RomRevision Revision)[] configurations =
        [
            (Zx8xModel.Zx80, Zx81RomRevision.Standard),
            (Zx8xModel.Zx81, Zx81RomRevision.Standard),
            (Zx8xModel.Zx81, Zx81RomRevision.Improved)
        ];

        foreach ((Zx8xModel model, Zx81RomRevision revision) in configurations)
        {
            Zx8xMachine machine = Zx8xMachineFactory.Create(model, fullDirectory, revision);
            const int BootTstates = 20_000_000;
            machine.RunForTstates(BootTstates);
            Require(machine.Cpu.Cyc >= BootTstates,
                $"{model}/{revision} ROM did not advance through the requested boot interval.");
            Require(machine.Cpu.PC != 0 || machine.Cpu.Cyc > 500_004,
                $"{model}/{revision} ROM remained at its reset vector.");
            Require(machine.VideoTiming.FrameNumber > 0,
                $"{model}/{revision} ROM did not drive a vertical-retrace transition " +
                $"(PC={machine.Cpu.PC:X4}, halted={machine.Cpu.IsHalted}, " +
                $"reads={machine.VideoTiming.IoReadCount}, lastRead={machine.VideoTiming.LastReadPort:X4}, " +
                $"writes={machine.VideoTiming.IoWriteCount}, lastWrite={machine.VideoTiming.LastWritePort:X4}, " +
                $"counter={machine.VideoTiming.CounterRunning}, line={machine.VideoTiming.RasterLine}).");
            Require(machine.Renderer.CompletedFrameNumber > 0,
                $"{model}/{revision} ROM did not complete a rendered frame " +
                $"(frames={machine.VideoTiming.FrameNumber}, PC={machine.Cpu.PC:X4}, halted={machine.Cpu.IsHalted}).");
            Require(machine.Renderer.DisplayFetchCount > 1_000,
                $"{model}/{revision} ROM did not execute a sustained display-file stream " +
                $"(frames={machine.VideoTiming.FrameNumber}, fetches={machine.Renderer.DisplayFetchCount}, " +
                $"PC={machine.Cpu.PC:X4}, halted={machine.Cpu.IsHalted}).");
        }
    }

    /// <summary>
    /// Exercises the actual ROM display loops and rejects the two regressions which
    /// synthetic fetch tests cannot see: an all-white ZX81 frame and ZX80 character
    /// rows whose horizontal position walks across the screen.
    /// </summary>
    private static void VerifyRomBootDisplays(string directory)
    {
        string fullDirectory = Path.GetFullPath(directory);
        (Zx8xModel Model, Zx81RomRevision Revision)[] configurations =
        [
            (Zx8xModel.Zx80, Zx81RomRevision.Standard),
            (Zx8xModel.Zx81, Zx81RomRevision.Standard),
            (Zx8xModel.Zx81, Zx81RomRevision.Improved)
        ];

        foreach ((Zx8xModel model, Zx81RomRevision revision) in configurations)
        {
            Zx8xMachine machine = Zx8xMachineFactory.Create(model, fullDirectory, revision);
            machine.RunForTstates(20_000_000);
            var frame = new int[machine.FrameWidth * machine.FrameHeight];
            Require(machine.TryCopyFrame(frame), $"{model}/{revision} did not publish a boot frame.");

            var blackByLine = new int[machine.FrameHeight];
            int blackPixels = 0;
            for (int y = 0; y < machine.FrameHeight; y++)
            {
                int row = y * machine.FrameWidth;
                for (int x = 0; x < machine.FrameWidth; x++)
                {
                    if (unchecked((uint)frame[row + x]) != 0xFF000000u)
                    {
                        continue;
                    }

                    blackPixels++;
                    blackByLine[y]++;
                }
            }

            Require(blackPixels >= 24,
                $"{model}/{revision} boot frame is blank ({blackPixels} black pixels). " +
                $"frames={machine.Renderer.CompletedFrameNumber}, fetches={machine.Renderer.DisplayFetchCount}.");
            Require(Enumerable.Range(0, blackByLine.Length - 7)
                    .Any(y => blackByLine.Skip(y).Take(8).Count(count => count > 0) >= 6),
                $"{model}/{revision} boot cursor did not occupy a coherent eight-line character cell.");
            Require(blackByLine.Skip(machine.FrameHeight - 8).All(count => count > 0),
                $"{model}/{revision} boot cursor was not aligned to the final eight-line character row. " +
                "This normally means refresh A6 /INT was accepted one M1 boundary too early.");
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
