using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ZedExEss.FileHandlers;
using ZedExEss.Hosting;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Tape;

namespace ZedExEss.Diagnostics;

/// <summary>Settings for the tape recording checks.</summary>
public sealed class TapeSaveVerificationOptions
{
    public string? OutputPath { get; init; }

    /// <summary>Directory holding 48.rom.</summary>
    public string? RomDirectory { get; init; }
}

/// <summary>
/// Verifies that what the machine writes to its MIC line comes back as tape blocks, and
/// that the save trap produces the same bytes as listening to a real save does.
/// </summary>
public static class TapeSaveVerificationRunner
{
    /// <summary>Where the test block lives in RAM.</summary>
    private const ushort BlockAddress = 0x8000;

    /// <summary>An address to return to, so the runner knows the save finished.</summary>
    private const ushort SaveCallerAddress = 0x7000;

    public static int Run(TapeSaveVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string outputPath = Path.GetFullPath(options.OutputPath ?? "tape-save-verification.log");
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        int failed = 0;

        writer.WriteLine("Tape save verification");
        writer.WriteLine($"Output: {outputPath}");
        writer.WriteLine();

        string romDirectory = options.RomDirectory ?? Path.Combine(AppContext.BaseDirectory, "ROMs");
        string machineRom = Path.Combine(romDirectory, "48.rom");

        Check("Pulses decode back into a standard block", VerifyPulseDecode, ref failed);
        Check("Undecodable pulses survive as a raw block", VerifyRawPulsesKept, ref failed);
        Check("A real ROM save is recorded from the MIC line", () => VerifyRomSave(machineRom, writer), ref failed);
        Check("The save trap produces the same block", () => VerifySaveTrap(machineRom), ref failed);
        Check("The trap ignores anything but the real ROM", () => VerifyTrapSignature(machineRom), ref failed);
        Check("A recording reloads as a TAP", () => VerifyTapRoundTrip(machineRom), ref failed);
        Check("A recording reloads as a TZX", () => VerifyTzxRoundTrip(machineRom), ref failed);

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

    /// <summary>
    /// Feeds the recorder a hand-built ROM-format pulse train, with no machine involved.
    /// </summary>
    private static void VerifyPulseDecode()
    {
        // Flag, three data bytes, then their parity: 0xFF ^ 0x01 ^ 0x02 ^ 0x03.
        byte[] payload = [0xFF, 0x01, 0x02, 0x03, 0xFF];
        var recorder = new SpectrumTapeRecorder();
        ulong clock = 0;
        recorder.Attach(() => clock, () => 1);
        recorder.Start();

        WriteStandardBlock(recorder, ref clock, payload);
        recorder.Stop();

        IReadOnlyList<SpectrumTapeRecordedBlock> blocks = recorder.Blocks;
        Require(blocks.Count == 1, $"Expected one block, got {blocks.Count}.");
        Require(blocks[0].IsStandard, "The block should have decoded as standard format.");
        Require(blocks[0].ChecksumValid, "The decoded checksum should match.");
        RequireBytes(blocks[0].Data, payload);
    }

    /// <summary>
    /// A saver this does not understand must not cost the user their recording.
    /// </summary>
    private static void VerifyRawPulsesKept()
    {
        var recorder = new SpectrumTapeRecorder();
        ulong clock = 0;
        recorder.Attach(() => clock, () => 1);
        recorder.Start();

        // Nothing here is ROM format: no pilot, no sync, and the wrong bit lengths.
        bool level = false;
        for (int i = 0; i < 64; i++)
        {
            Edge(recorder, ref clock, ref level, 3000 + (i * 7));
        }

        recorder.Stop();

        IReadOnlyList<SpectrumTapeRecordedBlock> blocks = recorder.Blocks;
        Require(blocks.Count == 1, $"Expected one block, got {blocks.Count}.");
        Require(!blocks[0].IsStandard, "The block should have been kept as raw pulses.");
        Require(blocks[0].Pulses.Count >= 60, "The raw pulses should have been kept.");
        Require(recorder.BuildTap().Length == 0, "A TAP cannot carry raw pulses.");
        Require(recorder.BuildTzx().Length > 10, "A TZX should carry the raw pulses.");
    }

    /// <summary>
    /// Runs the machine's own SA_BYTES at tape speed and listens to the result. This is
    /// the check that ties the recorder to real hardware behaviour rather than to an
    /// idea of what the ROM emits.
    /// </summary>
    private static void VerifyRomSave(string machineRom, StreamWriter writer)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        byte[] expected = PrepareBlock(machine);

        machine.TapeRecorder.Start();
        StartSave(machine);

        int steps = 0;
        while (machine.Cpu.PC != SaveCallerAddress && steps < 20_000_000)
        {
            machine.Emulator.StepInstruction();
            steps++;
        }

        machine.TapeRecorder.Stop();
        Require(steps < 20_000_000, "The ROM save never returned.");
        writer.WriteLine($"  saved in {steps} instructions");

        IReadOnlyList<SpectrumTapeRecordedBlock> blocks = machine.TapeRecorder.Blocks;
        Require(blocks.Count == 1, $"Expected one recorded block, got {blocks.Count}.");
        Require(blocks[0].IsStandard, "The ROM's own output should decode as standard format.");
        Require(blocks[0].ChecksumValid, "The ROM's checksum should match.");
        RequireBytes(blocks[0].Data, expected);
    }

    private static void VerifySaveTrap(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        byte[] expected = PrepareBlock(machine);
        StartSave(machine);

        Require(
            SpectrumRomTapeSaver.TryFastSave(machine, machine.TapeRecorder),
            "The trap should have taken the save.");
        Require(machine.Cpu.PC == 0x053F, $"The trap should return through SA/LD_RET, not ${machine.Cpu.PC:X4}.");

        IReadOnlyList<SpectrumTapeRecordedBlock> blocks = machine.TapeRecorder.Blocks;
        Require(blocks.Count == 1, $"Expected one block, got {blocks.Count}.");
        RequireBytes(blocks[0].Data, expected);

        // Finishing the ROM's exit path must leave the caller where it expected to be.
        int steps = 0;
        while (machine.Cpu.PC != SaveCallerAddress && steps < 100_000)
        {
            machine.Emulator.StepInstruction();
            steps++;
        }

        Require(machine.Cpu.PC == SaveCallerAddress, "The trapped save should return to its caller.");
    }

    private static void VerifyTrapSignature(string machineRom)
    {
        RequireRom(machineRom);

        // A machine with no ROM at all has nothing at $04C2 that looks like SA_BYTES.
        SpectrumMachine blank = SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum48K,
            Roms = RomSet.CreateBlank(SpectrumModelTraits.RomBankCount(SpectrumModel.Spectrum48K)),
            RenderEnabled = false
        });
        blank.Cpu.PC = 0x04C2;
        Require(
            !SpectrumRomTapeSaver.TryFastSave(blank, blank.TapeRecorder),
            "The trap must check the ROM before firing.");

        // The right ROM at the wrong address must not fire either.
        SpectrumMachine machine = CreateMachine(machineRom);
        machine.Cpu.PC = 0x04C3;
        Require(
            !SpectrumRomTapeSaver.TryFastSave(machine, machine.TapeRecorder),
            "The trap must only fire at the routine's entry point.");
    }

    private static void VerifyTapRoundTrip(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        byte[] expected = PrepareBlock(machine);
        StartSave(machine);
        SpectrumRomTapeSaver.TryFastSave(machine, machine.TapeRecorder);

        string path = Path.Combine(Path.GetTempPath(), $"zedexess-tapesave-{Guid.NewGuid():N}.tap");
        try
        {
            File.WriteAllBytes(path, machine.TapeRecorder.BuildTap());
            var loader = new TzxLoader(new NullEarSink(), 3_500_000);
            loader.LoadTapFile(path);
            Require(loader.Blocks.Count == 1, $"The TAP should hold one block, not {loader.Blocks.Count}.");
            Require(
                SpectrumRomTapeLoader.TryGetFirstStandardBlock(loader, out byte[] reloaded),
                "The reloaded TAP should expose a standard block.");
            RequireBytes(reloaded, expected);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void VerifyTzxRoundTrip(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        byte[] expected = PrepareBlock(machine);
        StartSave(machine);
        SpectrumRomTapeSaver.TryFastSave(machine, machine.TapeRecorder);

        string path = Path.Combine(Path.GetTempPath(), $"zedexess-tapesave-{Guid.NewGuid():N}.tzx");
        try
        {
            File.WriteAllBytes(path, machine.TapeRecorder.BuildTzx());
            var loader = new TzxLoader(new NullEarSink(), 3_500_000);
            loader.LoadTape(path);
            Require(loader.Blocks.Count == 1, $"The TZX should hold one block, not {loader.Blocks.Count}.");
            Require(
                SpectrumRomTapeLoader.TryGetFirstStandardBlock(loader, out byte[] reloaded),
                "The reloaded TZX should expose a standard block.");
            RequireBytes(reloaded, expected);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static SpectrumMachine CreateMachine(string machineRom) =>
        SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum48K,
            Roms = RomSet.LoadFromFiles([machineRom]),
            RenderEnabled = false
        });

    /// <summary>Fills memory with the test payload and returns the block the ROM should emit.</summary>
    private static byte[] PrepareBlock(SpectrumMachine machine)
    {
        const int length = 16;
        var block = new byte[length + 2];
        block[0] = 0xFF;

        byte parity = 0xFF;
        for (int i = 0; i < length; i++)
        {
            byte value = (byte)(0xA0 + i);
            machine.Memory.WriteDirect((ushort)(BlockAddress + i), value);
            block[i + 1] = value;
            parity ^= value;
        }

        block[^1] = parity;
        return block;
    }

    /// <summary>Sets the machine up as though BASIC had just called SA_BYTES.</summary>
    private static void StartSave(SpectrumMachine machine)
    {
        machine.Cpu.SP = 0x9000;
        machine.Memory.WriteDirect(0x9000, SaveCallerAddress & 0xFF);
        machine.Memory.WriteDirect(0x9001, SaveCallerAddress >> 8);
        machine.Cpu.A = 0xFF;
        machine.Cpu.IX = BlockAddress;
        machine.Cpu.D = 0x00;
        machine.Cpu.E = 0x10;
        machine.Cpu.PC = 0x04C2;
    }

    private static void WriteStandardBlock(SpectrumTapeRecorder recorder, ref ulong clock, byte[] data)
    {
        bool level = false;
        for (int i = 0; i < 64; i++)
        {
            Edge(recorder, ref clock, ref level, SpectrumTapeRecorder.PilotPulse);
        }

        Edge(recorder, ref clock, ref level, SpectrumTapeRecorder.FirstSyncPulse);
        Edge(recorder, ref clock, ref level, SpectrumTapeRecorder.SecondSyncPulse);

        foreach (byte value in data)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                int length = (value & (1 << bit)) != 0
                    ? SpectrumTapeRecorder.OneBitPulse
                    : SpectrumTapeRecorder.ZeroBitPulse;
                Edge(recorder, ref clock, ref level, length);
                Edge(recorder, ref clock, ref level, length);
            }
        }
    }

    /// <summary>Advances the fake clock and flips the MIC line.</summary>
    private static void Edge(SpectrumTapeRecorder recorder, ref ulong clock, ref bool level, int tstates)
    {
        clock += (ulong)tstates;
        level = !level;
        recorder.SetLevel(level);
    }

    private static void RequireRom(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"48K ROM not found: {path}");
        }
    }

    private static void RequireBytes(byte[] actual, byte[] expected)
    {
        Require(
            actual.Length == expected.Length,
            $"Expected {expected.Length} bytes, got {actual.Length}.");
        for (int i = 0; i < expected.Length; i++)
        {
            Require(actual[i] == expected[i], $"Byte {i} should be ${expected[i]:X2}, not ${actual[i]:X2}.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary file is not worth failing a verification run over.
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>The round trips only need the loader to parse, not to make a sound.</summary>
    private sealed class NullEarSink : IEarInputSink
    {
        public void SetEarLevel(bool high)
        {
        }
    }
}
