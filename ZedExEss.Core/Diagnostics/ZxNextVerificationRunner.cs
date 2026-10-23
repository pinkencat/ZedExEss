using ZedExEss.Z80CPU;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ZedExEss.FileHandlers;
using ZedExEss.Machines;
using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Debugging;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.Spectrum.Input;
using ZedExEss.Zx8x.Core;
using ZedExEss.ZxNext.Audio;
using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Dma;
using ZedExEss.ZxNext.DivMmc;
using ZedExEss.ZxNext.Firmware;
using ZedExEss.ZxNext.Input;
using ZedExEss.ZxNext.Memory;
using ZedExEss.ZxNext.Ports;
using ZedExEss.ZxNext.Video;

namespace ZedExEss.Diagnostics;

public sealed class ZxNextVerificationOptions
{
    public string? OutputPath { get; init; }
    public string? SystemDirectory { get; init; }
    public string? BootRomPath { get; init; }
}

/// <summary>Headless checks for the ZX Spectrum Next architecture foundation.</summary>
public static class ZxNextVerificationRunner
{
    public static int Run(ZxNextVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string outputPath = Path.GetFullPath(options.OutputPath ?? "zxnext-verification.log");
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        int failed = 0;
        writer.WriteLine("ZX Spectrum Next foundation verification");
        writer.WriteLine($"FPGA source: {ZxNextReferenceBaseline.FpgaRepositoryCommit}");
        writer.WriteLine($"Core target: {ZxNextReferenceBaseline.CoreVersion}");
        writer.WriteLine($"Output: {outputPath}");
        writer.WriteLine();

        Check("Pinned source baseline", VerifyPinnedSourceBaseline, ref failed);
        Check("Separate machine-family descriptor", VerifyMachineDescriptor, ref failed);
        Check("Sealed Next buses", VerifyBootstrapBuses, ref failed);
        Check("Reset-ROM execution", VerifyBlankMachineExecution, ref failed);
        Check("CPU-to-master timing seam", VerifyMasterTimeline, ref failed);
        Check("Foundational NextRegs", VerifyFoundationNextRegisters, ref failed);
        Check("ULA frame timing and interrupt pulse", VerifyUlaFrameInterrupt, ref failed);
        Check("Next interrupt enable registers", VerifyInterruptEnableRegisters, ref failed);
        Check("Raster line interrupt timing and pulse", VerifyRasterLineInterrupt, ref failed);
        Check("Software DivMMC stackless NMI", VerifyDivMmcStacklessNmi, ref failed);
        Check("Software Multiface stackless NMI", VerifyMultifaceStacklessNmi, ref failed);
        Check("ULA contention schedule and bus waits", VerifyUlaContention, ref failed);
        Check("ULA and +3 floating bus", VerifyUlaFloatingBus, ref failed);
        Check("ULA keyboard matrix and output latch", VerifyUlaKeyboard, ref failed);
        Check("Next joystick connectors and cursor-key fallback", VerifyJoystickAndCursorInput, ref failed);
        Check("Next beeper and TurboSound audio", VerifyNextAudio, ref failed);
        Check("Legacy ULA framebuffer", VerifyLegacyUlaFramebuffer, ref failed);
        Check("Timex high-resolution and LoRes video", VerifyTimexAndLoresVideoModes, ref failed);
        Check("Layer 2 registers and CPU aperture", VerifyLayer2RegistersAndAperture, ref failed);
        Check("Layer 2 bitmap modes and compositor", VerifyLayer2BitmapModes, ref failed);
        Check("Tilemap registers, addressing and compositor", VerifyTilemapRegistersAndRendering, ref failed);
        Check("ULA-over-tilemap video-memory arbitration", VerifyVideoMemoryArbitration, ref failed);
        Check("Sprite ports, attributes and compositor", VerifySpritePortsAndRendering, ref failed);
        Check("Relative and unified sprite chains", VerifyRelativeAndUnifiedSprites, ref failed);
        Check("Timed sprite preparation, collision and overtime", VerifyTimedSpritePreparation, ref failed);
        Check("zxnDMA Layer 2 screen transfer", VerifyZxnDmaLayer2Transfer, ref failed);
        Check("clk28 DMA BUSREQ arbitration and endpoint timing", VerifyTimedDmaArbitration, ref failed);
        Check("Raster-visible incremental DMA writes", VerifyDmaRasterInterleaving, ref failed);
        Check("Cycle-accounted ULA raster writes", VerifyCycleAccountedUlaRaster, ref failed);
        Check("CPU/video-BRAM write timing", VerifyCpuDisplayWriteTiming, ref failed);
        Check("Tilemap raster-pixel batching", VerifyTilemapRasterPixelBatching, ref failed);
        Check("Quiet raster spans", VerifyQuietRasterSpans, ref failed);
        Check("Desktop frame-runner seam", VerifyDesktopFrameRunnerSeam, ref failed);
        Check("NextReg instruction-boundary reset requests", VerifyNextRegResetRequests, ref failed);
        Check("Physical RAM configuration", VerifyPhysicalRam, ref failed);
        Check("A captured Next carries on identically", VerifyMachineSnapshotContinues, ref failed);
        Check("A Next snapshot survives a file round trip", VerifySnapshotFileRoundTrip, ref failed);
        Check("Every peripheral field survives a file round trip",
            VerifyPeripheralFieldsRoundTrip, ref failed);
        Check("A Next snapshot rewinds its SD card", VerifySnapshotRewindsSdCard, ref failed);
        Check("A Next snapshot refuses to rewind the wrong card",
            VerifySnapshotRefusesForeignSdCard, ref failed);
        Check("A Next resumes from every instruction boundary around an interrupt",
            VerifyRestoreAcrossInterruptBoundaries, ref failed);
        Check("A Next resumes from a frame publication boundary",
            VerifyRestoreAtFramePublication, ref failed);
        Check("A Next resumes from inside an NMI", VerifyRestoreAroundNmi, ref failed);
        Check("A Next resumes with a DMA transfer still pending",
            VerifyRestoreWithDmaPending, ref failed);
        Check("A Next resumes mid-sector-write with the same bytes on the card",
            VerifyRestoreMidSectorWrite, ref failed);
        Check("A Next resumes into a machine holding batched instruction time",
            VerifyRestoreIntoBatchedMachine, ref failed);
        Check("A damaged Next snapshot is refused", VerifySnapshotFileFailures, ref failed);
        Check("48K SNA/SNX snapshot restore", VerifyZxNextSnapshotLoader, ref failed);
        Check("Transactional NEX loading", VerifyNexLoader, ref failed);
        Check("System/Next asset discovery and loading", VerifySystemAssets, ref failed);
        Check("8 KiB MMU mapping", VerifyMmuMapping, ref failed);
        Check("NextReg select/data ports", VerifyNextRegPorts, ref failed);
        Check("Legacy paging and ROM selection", VerifyLegacyPaging, ref failed);
        Check("Guest legacy paging I/O", VerifyGuestLegacyPaging, ref failed);
        Check("Boot ROM and config SRAM mapping", VerifyBootAndConfigMapping, ref failed);
        Check("Alternate ROM read/write overlays", VerifyAlternateRomOverlay, ref failed);
        Check("Hard and soft ROM reset state", VerifyRomResetKinds, ref failed);
        Check("DivMMC control and system-SRAM mapping", VerifyDivMmcControlAndMapping, ref failed);
        Check("DivMMC programmable opcode automap", VerifyDivMmcAutomap, ref failed);
        Check("DivMMC NextReg and reset state", VerifyDivMmcRegisterResetState, ref failed);
        Check("Next SPI ports and SD sector path", VerifyNextSpiAndSdCard, ref failed);
        Check("FAT16 folder overlay projection", VerifyFat16FolderOverlay, ref failed);
        Check("Guest changes merge back into the SD folder", VerifySdFolderMerge, ref failed);
        Check("A persistent SD folder still takes an overlay", VerifySdFolderPersistencePolicy, ref failed);
        Check("Z80N accumulator and arithmetic instructions", VerifyZ80NArithmetic, ref failed);
        Check("Z80N barrel instructions", VerifyZ80NBarrelOperations, ref failed);
        Check("Z80N immediate and stack instructions", VerifyZ80NImmediateOperations, ref failed);
        Check("Z80N pixel address instructions", VerifyZ80NPixelOperations, ref failed);
        Check("Z80N I/O instructions", VerifyZ80NIoOperations, ref failed);
        Check("Z80N extended block transfers", VerifyZ80NBlockTransfers, ref failed);
        Check("Opt-in Z80N assembler and disassembler", VerifyZ80NDeveloperTools, ref failed);
        Check("NEXTREG turbo boundary timing", VerifyNextRegTurboTiming, ref failed);
        Check("All CPU speed ratios", VerifyAllCpuSpeedRatios, ref failed);
        Check("Foundation reset and pause", VerifyResetAndPause, ref failed);
        Check("Debugger attaches to the Next machine", VerifyDebuggerAttach, ref failed);
        Check("NextReg write breakpoints", VerifyNextRegBreakpoints, ref failed);
        Check("Next debugger MMU and NextReg views", VerifyDebuggerMachineViews, ref failed);
        Check("Oscilloscope separates all Next channels", VerifyAudioScopeChannels, ref failed);

        if (!string.IsNullOrWhiteSpace(options.SystemDirectory))
        {
            Check(
                "User-supplied System/Next distribution",
                () => VerifySuppliedSystemAssets(
                    options.SystemDirectory,
                    options.BootRomPath,
                    writer),
                ref failed);
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


    /// <summary>
    /// A captured machine, put back, has to carry on exactly as the original did.
    /// </summary>
    /// <remarks>
    /// This is the completeness oracle for the whole snapshot workstream, not a test of
    /// one field: it runs a machine on, captures it, lets the original continue, then
    /// restores the capture into a *different* machine and runs that the same distance.
    /// Any state the capture missed shows up as a divergence, so the check gets stricter
    /// for free as the machine grows.
    ///
    /// Both the registers and RAM and the produced picture are compared, because a machine
    /// can resume computing correctly and still resume looking wrong - the palette and the
    /// beam are as much part of the state as the accumulator.
    /// </remarks>
    private static void VerifyMachineSnapshotContinues()
    {
        ZxNextMachine original = CreateSnapshotWorkload();

        // Far enough in that paging, the raster and the interrupt schedule are all live.
        for (int frame = 0; frame < 12; frame++)
        {
            original.RunFrame();
        }

        // A workload that silently never ran would make every assertion below vacuous.
        Require(original.Cpu.PC >= 0x8000 && original.Cpu.PC < 0x8020,
            $"The snapshot workload is not executing; PC is ${original.Cpu.PC:X4}.");
        Require(original.MemoryBus.ReadDirect(0x4000) != 0,
            "The snapshot workload did not write to the display.");
        Require(original.Registers.Read(0x7F) != 0xFF,
            "The snapshot workload did not change register-file state.");
        Require(original.Tilemap.Enabled && original.Tilemap.CompletedCharacterFetches > 0,
            "The snapshot workload left the tilemap pipeline idle.");
        Require(original.Sprites.SpritePreparationMasterTicks > 0,
            "The snapshot workload left the sprite engine idle.");
        Require(original.Dma.LastTransferLength > 0,
            "The snapshot workload's DMA transfer never ran.");
        Require(original.Audio.PendingSampleCount > 0,
            "The snapshot workload produced no audio.");
        Require(original.DivMmc.IsMapped && original.DivMmc.AutomapEnabled,
            "The snapshot workload left DivMMC unmapped.");
        Require(original.Multiface.IsMapped, "The snapshot workload left Multiface unmapped.");
        Require(original.Keyboard.ReadRows(0xFD) != 0xFF,
            "The snapshot workload is holding no keys.");
        Require(original.PortBus.UlaOutputLatch != 0,
            "The snapshot workload never wrote the border or beeper.");

        ZxNextMachineSnapshot snapshot = ZxNextMachineSnapshotService.Capture(original);
        Require(snapshot.PhysicalRam.Length == original.MemoryBus.PhysicalMemory.InstalledRamBytes,
            "The capture did not take all of the machine's RAM.");

        long capturedFingerprint = FingerprintNext(original);
        byte capturedUserRegister = original.Registers.Read(0x7F);
        byte capturedUlaScrollX = original.Registers.Read(0x26);
        byte capturedMmu7 = original.MemoryBus.GetMmuPage(7);

        // Through the mapping, not just the bookkeeping: restoring the MMU page numbers
        // is worthless if the effective map is not rebuilt from them.
        byte capturedThroughMmu7 = original.MemoryBus.ReadDirect(0xE000);
        ulong capturedMasterTicks = original.Clock.MasterTicks;
        ulong capturedFrame = original.UlaTiming.FrameNumber;
        byte capturedPaletteIndex = original.Registers.Palettes.Index;
        byte capturedLayer2ScrollX = original.Layer2.ScrollXLeastSignificant;
        byte capturedTilemapScrollX = original.Tilemap.ScrollXMostSignificant;
        ulong capturedTilemapFetches = original.Tilemap.CompletedCharacterFetches;
        ulong capturedSpriteTicks = original.Sprites.SpritePreparationMasterTicks;
        byte capturedDivMmcRom = original.MemoryBus.ReadDirect(0x0000);
        byte capturedKeyboardRow = original.Keyboard.ReadRows(0xFD);
        ushort capturedDmaCounter = original.Dma.ByteCounter;
        int capturedDmaLength = original.Dma.LastTransferLength;

        // Samples already produced belong to the host, and the audio state deliberately does
        // not carry them, so both machines start the compared stretch with an empty queue.
        DrainAudio(original);

        for (int frame = 0; frame < 6; frame++)
        {
            original.RunFrame();
        }

        long expected = FingerprintNext(original);
        long expectedFrame = HashFrame(original);
        short[] expectedSamples = DrainAudio(original);
        Require(expectedSamples.Length > 0, "The original produced no audio to compare against.");

        // A different machine, so nothing can be shared by accident.
        ZxNextMachine restored = CreateSnapshotWorkload();
        for (int frame = 0; frame < 3; frame++)
        {
            restored.RunFrame();
        }

        PerturbPeripherals(restored);
        ZxNextMachineSnapshotService.Restore(restored, snapshot);

        // Per device, so a failure says which one was missed.
        Require(restored.Registers.Read(0x7F) == capturedUserRegister,
            $"The register file came back with $7F = ${restored.Registers.Read(0x7F):X2}, "
                + $"not ${capturedUserRegister:X2}.");
        Require(restored.Registers.Read(0x26) == capturedUlaScrollX,
            $"The register file came back with $26 = ${restored.Registers.Read(0x26):X2}, "
                + $"not ${capturedUlaScrollX:X2}.");
        Require(restored.MemoryBus.ReadDirect(0xE000) == capturedThroughMmu7,
            $"Reading through MMU7 gave ${restored.MemoryBus.ReadDirect(0xE000):X2}, "
                + $"not ${capturedThroughMmu7:X2}: the effective map was not rebuilt.");
        Require(restored.MemoryBus.GetMmuPage(7) == capturedMmu7,
            $"The memory bus came back with MMU7 = {restored.MemoryBus.GetMmuPage(7)}, "
                + $"not {capturedMmu7}.");
        Require(restored.Clock.MasterTicks == capturedMasterTicks,
            $"The clock came back at {restored.Clock.MasterTicks}, not {capturedMasterTicks}.");
        Require(restored.UlaTiming.FrameNumber == capturedFrame,
            $"The raster came back on frame {restored.UlaTiming.FrameNumber}, not {capturedFrame}.");

        Require(restored.Registers.Palettes.Index == capturedPaletteIndex,
            $"The palettes came back with index ${restored.Registers.Palettes.Index:X2}, "
                + $"not ${capturedPaletteIndex:X2}.");
        Require(restored.Layer2.ScrollXLeastSignificant == capturedLayer2ScrollX,
            $"Layer 2 came back scrolled to {restored.Layer2.ScrollXLeastSignificant}, "
                + $"not {capturedLayer2ScrollX}.");
        Require(restored.Tilemap.ScrollXMostSignificant == capturedTilemapScrollX,
            $"The tilemap came back scrolled to {restored.Tilemap.ScrollXMostSignificant}, "
                + $"not {capturedTilemapScrollX}.");
        Require(restored.Tilemap.CompletedCharacterFetches == capturedTilemapFetches,
            $"The tilemap pipeline came back {restored.Tilemap.CompletedCharacterFetches} "
                + $"fetches in, not {capturedTilemapFetches}.");
        Require(restored.Sprites.SpritePreparationMasterTicks == capturedSpriteTicks,
            $"The sprite engine came back {restored.Sprites.SpritePreparationMasterTicks} "
                + $"ticks in, not {capturedSpriteTicks}.");

        // The peripherals, by recapturing the machine that was just restored: if capture and
        // restore are inverses, the second capture equals the first, device by device.
        RequirePeripheralsMatch(snapshot, ZxNextMachineSnapshotService.Capture(restored));

        // And through the devices rather than only through their state, so a restore that put
        // the latches back without the map or the matrix following is still caught.
        Require(restored.MemoryBus.ReadDirect(0x0000) == capturedDivMmcRom,
            $"Reading the bottom of the map gave ${restored.MemoryBus.ReadDirect(0x0000):X2}, "
                + $"not ${capturedDivMmcRom:X2}: DivMMC's paging did not follow its latches.");
        Require(restored.Keyboard.ReadRows(0xFD) == capturedKeyboardRow,
            $"The keyboard answered ${restored.Keyboard.ReadRows(0xFD):X2}, "
                + $"not ${capturedKeyboardRow:X2}.");
        Require(restored.Dma.ByteCounter == capturedDmaCounter
                && restored.Dma.LastTransferLength == capturedDmaLength,
            $"The DMA controller came back {restored.Dma.ByteCounter} bytes in with a last "
                + $"length of {restored.Dma.LastTransferLength}, not {capturedDmaCounter} "
                + $"and {capturedDmaLength}.");

        Require(FingerprintNext(restored) == capturedFingerprint,
            "Restoring did not reproduce the machine that was captured.");

        DrainAudio(restored);

        for (int frame = 0; frame < 6; frame++)
        {
            restored.RunFrame();
        }

        Require(FingerprintNext(restored) == expected,
            "A restored Next diverged from the original while running the same frames.");
        Require(HashFrame(restored) == expectedFrame,
            "A restored Next produced a different picture from the original.");

        // The PCM, which is the only oracle that covers the PSG's generator phase: a chip
        // restored from its registers alone produces plausible audio that is not this audio.
        short[] restoredSamples = DrainAudio(restored);
        Require(restoredSamples.Length == expectedSamples.Length,
            $"A restored Next produced {restoredSamples.Length} samples, not "
                + $"{expectedSamples.Length}.");
        Require(restoredSamples.AsSpan().SequenceEqual(expectedSamples),
            "A restored Next produced different audio from the original.");

        // And the capture is a copy, not a view: running on must not have altered it.
        Require(snapshot.PhysicalRam.Length == restored.MemoryBus.PhysicalMemory.InstalledRamBytes,
            "The snapshot's RAM was resized by running the machine.");
    }

    /// <summary>
    /// A Next running something that touches paging, the raster and an interrupt handler.
    /// </summary>
    /// <remarks>
    /// A machine sitting in a HALT would pass a snapshot test that captured almost nothing,
    /// so the workload deliberately keeps state moving: it writes through a paged MMU slot,
    /// scribbles on the display and changes CPU speed.
    /// </remarks>
    private static ZxNextMachine CreateSnapshotWorkload()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.Registers.Write(0x07, (byte)ZxNextCpuSpeed.Mhz14);
        machine.Registers.Write(0x22, 0x02);

        // The video devices have to be running, or their state is all zeroes and a capture
        // that dropped it would look identical to one that kept it.
        machine.Registers.Write(0x6B, 0x80);     // tilemap on, 40 columns
        machine.Registers.Write(0x15, 0x01);     // sprites visible
        machine.Registers.Write(0x12, 0x09);     // Layer 2 active bank
        machine.Registers.Write(0x70, 0x00);     // Layer 2 256x192
        for (int sprite = 0; sprite < 8; sprite++)
        {
            machine.Registers.Write(0x34, (byte)sprite);
            machine.Registers.Write(0x35, (byte)(40 + (sprite * 12)));
            machine.Registers.Write(0x36, (byte)(60 + sprite));
            machine.Registers.Write(0x37, 0x00);
            machine.Registers.Write(0x38, 0x80);
        }

        // The peripherals have to be doing something too, for the same reason as the video
        // devices: a capture that dropped a device sitting at its reset values looks exactly
        // like one that kept it.
        machine.Registers.Write(0x08, 0x12);        // peripheral 3: TurboSound enabled

        // A PSG with a tone, noise and a volume, so its counters, shift register and
        // envelope all have somewhere to go while the machine runs.
        WriteAyRegister(machine, 0x00, 0x80);       // channel A fine tune
        WriteAyRegister(machine, 0x01, 0x01);       // channel A coarse tune
        WriteAyRegister(machine, 0x06, 0x0F);       // noise period
        WriteAyRegister(machine, 0x07, 0x30);       // mixer: tone and noise on A
        WriteAyRegister(machine, 0x08, 0x0F);       // channel A volume
        WriteAyRegister(machine, 0x0D, 0x0E);       // envelope shape
        machine.Audio.BeginCapture();

        // DivMMC paged in with its automapper armed, so the bottom of the map and the
        // automap latches are both away from their reset values.
        machine.DivMmc.SetAutomapEnabled(true);
        machine.DivMmc.WriteControl(0x83);          // CONMEM, RAM bank 3
        machine.DivMmc.WriteEntryPoints1(0xC0);
        machine.DivMmc.WriteCardSelect(0xFE);

        // An MF48 read of its enable port pages the unit in and clears invisibility.
        machine.Multiface.TryReadPort(0x009F, 0x03, machine.Paging, 0x00, out _);

        // Two connectors routed onto the keyboard matrix with a direction held on each, so
        // the matrix, the overlay and the connector modes all differ from a reset machine.
        machine.Joystick.ConfigureModes(0x80);
        machine.Joystick.SetConnectorButtonState(
            ZxNextJoystickConnector.Joystick1, SpectrumJoystickButton.Left, pressed: true);
        machine.Joystick.SetConnectorButtonState(
            ZxNextJoystickConnector.Joystick2, SpectrumJoystickButton.Fire, pressed: true);
        machine.Keyboard.SetKeyState(SpectrumKey.A, pressed: true);
        machine.Keyboard.SetKeyState(SpectrumKey.Space, pressed: true);

        // A NextReg selected through $243B, which is the port bus's latch rather than the
        // register file's, and is how a guest that was mid-write resumes.
        machine.PortBus.WriteUncontended(0x243B, 0x7F);

        for (int index = 0; index < 8; index++)
        {
            machine.MemoryBus.WriteDirect((ushort)(0x9000 + index), (byte)(0xA0 + index));
        }

        ProgramDmaTransfer(machine);

        // LD HL,$4000 / LD DE,$5B00 / loop: write, bump, page, repeat.
        byte[] program =
        [
            0x21, 0x00, 0x40,       // LD HL,$4000
            0x11, 0x00, 0x5B,       // LD DE,$5B00
            0x3E, 0x01,             // LD A,$01
            0x77,                   // LD (HL),A      <- loop, offset 8
            0x23,                   // INC HL
            0x12,                   // LD (DE),A
            0x13,                   // INC DE
            0x3C,                   // INC A
            0xED, 0x92, 0x26,       // NEXTREG $26,A  (ULA scroll X)
            0xED, 0x92, 0x7F,       // NEXTREG $7F,A  (user register)
            0xED, 0x92, 0x57,       // NEXTREG $57,A  (MMU7, which nothing here executes from)
            0xED, 0x92, 0x16,       // NEXTREG $16,A  (Layer 2 scroll X)
            0xED, 0x92, 0x2F,       // NEXTREG $2F,A  (tilemap scroll X high)
            0xED, 0x92, 0x40,       // NEXTREG $40,A  (palette index)
            0xED, 0x92, 0x41,       // NEXTREG $41,A  (palette value, bumps the index)
            0xD3, 0xFE,             // OUT ($FE),A    (border and beeper, via the port bus)
            0x01, 0xFD, 0xFF,       // LD BC,$FFFD
            0xED, 0x79,             // OUT (C),A      (PSG register select)
            0x06, 0xBF,             // LD B,$BF
            0xED, 0x79,             // OUT (C),A      (PSG register write)
            0x18, 0xD9              // JR back to offset 8
        ];

        for (int index = 0; index < program.Length; index++)
        {
            machine.MemoryBus.WriteDirect((ushort)(0x8000 + index), program[index]);
        }

        machine.Cpu.PC = 0x8000;
        machine.Cpu.SP = 0x7FF0;
        return machine;
    }

    /// <summary>
    /// Loads, runs and then half-reprograms a DMA transfer.
    /// </summary>
    /// <remarks>
    /// Three different kinds of DMA state in one go. The completed transfer leaves the byte
    /// counter, the end-of-block latch and the last length away from their reset values; the
    /// enable leaves the controller to drain on the machine's next step, so the transfer is
    /// real rather than asserted; and the trailing partial write leaves the pending-write
    /// cursor expecting two more bytes, which is the state a capture taken between a header
    /// and its operands has to carry.
    /// </remarks>
    private static void ProgramDmaTransfer(ZxNextMachine machine)
    {
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x7D);  // WR0: A to B, address and length follow
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x00);  // port A low
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x90);  // port A high
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x07);  // length low
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x00);  // length high
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x14);  // WR1: port A memory, increment
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x10);  // WR2: port B memory, increment
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x8D);  // WR4: port B address follows
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x00);  // port B low
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0xA0);  // port B high
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0xCF);  // load
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x87);  // enable

        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x7D);  // and a header whose operands never came
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x00);
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x90);
    }

    /// <summary>
    /// Moves every peripheral away from what the workload set up.
    /// </summary>
    /// <remarks>
    /// Without this a restore test is far weaker than it looks. The destination machine is
    /// built by the same workload as the source, so its DivMMC latches, held keys and DMA
    /// registers already match the ones being restored, and dropping those restores
    /// altogether would still pass. Perturbing them first is also the honest case: loading a
    /// save state happens into a machine that has been doing something else.
    /// </remarks>
    private static void PerturbPeripherals(ZxNextMachine machine)
    {
        machine.DivMmc.WriteControl(0x00);
        machine.DivMmc.SetAutomapEnabled(false);
        machine.DivMmc.WriteEntryPoints1(0x00);
        machine.DivMmc.WriteCardSelect(0xFF);
        machine.Multiface.TryReadPort(0x001F, 0x03, machine.Paging, 0x00, out _);
        machine.Keyboard.ReleaseAll();
        machine.Joystick.ReleaseAll();
        machine.Joystick.ConfigureModes(0x00);
        machine.PortBus.WriteUncontended(0x243B, 0x00);
        machine.PortBus.WriteUncontended(0x00FE, 0x00);
        WriteAyRegister(machine, 0x00, 0x01);
        WriteAyRegister(machine, 0x08, 0x00);
        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0xC3);
        DrainAudio(machine);
    }

    /// <summary>Takes everything the audio device has finished producing.</summary>
    private static short[] DrainAudio(ZxNextMachine machine)
    {
        var samples = new short[machine.Audio.PendingSampleCount];
        machine.Audio.DrainSamples(samples, 0, samples.Length);
        return samples;
    }

    /// <summary>
    /// Compares the peripheral half of two snapshots, naming the device that differs.
    /// </summary>
    /// <remarks>
    /// Used by recapturing a machine that has just been restored: if restore and capture are
    /// inverses, the second capture equals the first, and any field the restore path forgets
    /// shows up here with the device named rather than as a divergence five frames later.
    /// The records with arrays in them are compared element by element, because record
    /// equality on an array is reference equality and would pass for free.
    /// </remarks>
    private static void RequirePeripheralsMatch(
        ZxNextMachineSnapshot expected,
        ZxNextMachineSnapshot actual)
    {
        Require(actual.Dma == expected.Dma, "The DMA controller did not come back as captured.");
        Require(actual.BusArbiter == expected.BusArbiter,
            "The bus arbiter did not come back as captured.");
        Require(actual.DivMmc == expected.DivMmc, "DivMMC did not come back as captured.");
        Require(actual.Multiface == expected.Multiface, "Multiface did not come back as captured.");
        Require(actual.NmiRouting == expected.NmiRouting,
            "The pending NMI's owner did not come back as captured.");
        Require(actual.PortBus == expected.PortBus, "The port bus did not come back as captured.");
        Require(actual.FloatingBus == expected.FloatingBus,
            "The floating bus did not come back as captured.");

        Require(actual.Input.KeyboardRows.AsSpan().SequenceEqual(expected.Input.KeyboardRows),
            "The held keys did not come back as captured.");
        Require(actual.Input.JoystickRows.AsSpan().SequenceEqual(expected.Input.JoystickRows),
            "The joystick's keyboard overlay did not come back as captured.");
        Require(actual.Input.ConnectorStates.AsSpan().SequenceEqual(expected.Input.ConnectorStates),
            "The held joystick directions did not come back as captured.");
        Require(actual.Input.ConnectorModes.AsSpan().SequenceEqual(expected.Input.ConnectorModes),
            "The joystick connector modes did not come back as captured.");

        ZxNextAudioState expectedAudio = expected.Audio;
        ZxNextAudioState actualAudio = actual.Audio;
        Require(actualAudio.SelectedRegisters.AsSpan().SequenceEqual(expectedAudio.SelectedRegisters)
                && actualAudio.Pan.AsSpan().SequenceEqual(expectedAudio.Pan)
                && actualAudio.ActiveChip == expectedAudio.ActiveChip
                && actualAudio.UlaOutputLatch == expectedAudio.UlaOutputLatch
                && actualAudio.Peripheral2Settings == expectedAudio.Peripheral2Settings
                && actualAudio.Peripheral3Settings == expectedAudio.Peripheral3Settings
                && actualAudio.Peripheral4Settings == expectedAudio.Peripheral4Settings
                && actualAudio.CurrentUlaLevel == expectedAudio.CurrentUlaLevel
                && actualAudio.UlaLevelRunTicks == expectedAudio.UlaLevelRunTicks
                && actualAudio.UlaLevelSum == expectedAudio.UlaLevelSum
                && actualAudio.UlaLevelWeight == expectedAudio.UlaLevelWeight
                && actualAudio.SampleRemainder == expectedAudio.SampleRemainder,
            "The audio device did not come back as captured.");
        Require(actualAudio.Chips.Length == expectedAudio.Chips.Length,
            "A PSG went missing across the restore.");
        for (int chip = 0; chip < expectedAudio.Chips.Length; chip++)
        {
            AySnapshotState expectedChip = expectedAudio.Chips[chip];
            AySnapshotState actualChip = actualAudio.Chips[chip];
            Require(actualChip.Registers.AsSpan().SequenceEqual(expectedChip.Registers),
                $"PSG {chip}'s registers did not come back as captured.");
            Require(actualChip with { Registers = expectedChip.Registers } == expectedChip,
                $"PSG {chip}'s generator phase did not come back as captured.");
        }
    }

    /// <summary>NextRegs the register file itself owns, for the continuation fingerprint.</summary>
    private static readonly byte[] RegisterFileFingerprintRegisters =
    [
        0x03, 0x05, 0x06, 0x07, 0x08, 0x09, 0x22, 0x26, 0x27, 0x32, 0x33, 0x6A, 0x7F
    ];

    /// <summary>Registers plus all installed RAM, as one number.</summary>
    private static long FingerprintNext(ZxNextMachine machine)
    {
        unchecked
        {
            long hash = 1469598103934665603;
            void Mix(ulong value)
            {
                for (int shift = 0; shift < 64; shift += 8)
                {
                    hash ^= (byte)(value >> shift);
                    hash *= 1099511628211;
                }
            }

            Z80SnapshotState cpu = machine.Cpu.CaptureSnapshotState();
            Mix((ulong)((cpu.A << 8) | cpu.F));
            Mix((ulong)((cpu.B << 8) | cpu.C));
            Mix((ulong)((cpu.D << 8) | cpu.E));
            Mix((ulong)((cpu.H << 8) | cpu.L));
            Mix((ulong)((cpu.AlternateA << 8) | cpu.AlternateF));
            Mix((ulong)((cpu.AlternateB << 8) | cpu.AlternateC));
            Mix((ulong)((cpu.AlternateD << 8) | cpu.AlternateE));
            Mix((ulong)((cpu.AlternateH << 8) | cpu.AlternateL));
            Mix(cpu.IX);
            Mix(cpu.IY);
            Mix(cpu.SP);
            Mix(cpu.PC);
            Mix(cpu.Cycles);
            Mix(machine.Clock.MasterTicks);
            Mix((ulong)machine.Clock.CpuSpeed);
            Mix(machine.UlaTiming.FrameNumber);
            Mix(machine.UlaTiming.InterruptCount);

            // Registers this device owns and reports back. Deliberately not the palette,
            // Layer 2, tilemap or sprite registers: those devices are not captured yet
            // (SNP-003), and asserting them would be asserting a known gap.
            foreach (byte register in RegisterFileFingerprintRegisters)
            {
                Mix(machine.Registers.Read(register));
            }

            byte[] ram = machine.MemoryBus.PhysicalMemory.CopyAll();
            foreach (byte value in ram)
            {
                hash ^= value;
                hash *= 1099511628211;
            }

            return hash;
        }
    }

    /// <summary>The produced picture, as one number.</summary>
    private static long HashFrame(ZxNextMachine machine)
    {
        unchecked
        {
            long hash = 1469598103934665603;
            foreach (int pixel in machine.Video.FrameBuffer)
            {
                hash ^= pixel;
                hash *= 1099511628211;
            }

            return hash;
        }
    }


    /// <summary>
    /// A snapshot written to a file and read back has to restore the same machine.
    /// </summary>
    /// <remarks>
    /// The oracle for serialisation, in the same spirit as the continuation check is the
    /// oracle for capture: a field left out of the writer or read back in the wrong order
    /// shows up as a machine that diverges, not as a subtly different field nobody inspects.
    /// The comparison is done by running both machines on afterwards, because that is what a
    /// save state is for.
    /// </remarks>
    private static void VerifySnapshotFileRoundTrip()
    {
        ZxNextMachine original = CreateSnapshotWorkload();
        for (int frame = 0; frame < 12; frame++)
        {
            original.RunFrame();
        }

        ZxNextMachineSnapshot captured = ZxNextMachineSnapshotService.Capture(original);
        Require(captured.UnknownChunks.Count == 0,
            "A freshly captured snapshot claimed to carry chunks from a file.");

        using var file = new MemoryStream();
        ZxNextSnapshotFile.Save(file, captured);
        Require(file.Length > 0, "Saving produced an empty file.");

        // The header has to be identifiable without inflating anything.
        byte[] bytes = file.ToArray();
        Require(bytes.Length > 12 && bytes[0] == (byte)'Z' && bytes[1] == (byte)'X'
                && bytes[2] == (byte)'N' && bytes[3] == (byte)'S',
            "The file does not start with the ZXNS signature.");

        file.Position = 0;
        ZxNextMachineSnapshot reloaded = ZxNextSnapshotFile.Load(file);

        // The parts most easily lost in serialisation, named so a failure says which.
        Require(reloaded.PhysicalRam.Length == captured.PhysicalRam.Length,
            "The RAM came back a different size.");
        Require(reloaded.PhysicalRam.AsSpan().SequenceEqual(captured.PhysicalRam),
            "The RAM did not survive the round trip.");
        Require(reloaded.Cpu == captured.Cpu, "The CPU state did not survive the round trip.");
        Require(reloaded.Clock == captured.Clock, "The clock did not survive the round trip.");
        Require(reloaded.Paging == captured.Paging, "The paging ports did not survive the round trip.");
        Require(reloaded.UlaTiming.Profile.Equals(captured.UlaTiming.Profile),
            "The timing profile was not rebuilt from the machine timing it was derived from.");
        Require(reloaded.Raster.FrameBuffer.AsSpan().SequenceEqual(captured.Raster.FrameBuffer),
            "The drawn picture did not survive the round trip.");

        // The decisive comparison: restore the captured snapshot and the reloaded one into
        // two fresh machines and compare them *before* either runs. Letting them run first
        // lets the guest rewrite anything that was lost - it writes several of these registers
        // every loop - so a field dropped in serialisation would heal before being noticed.
        ZxNextMachine fromCapture = CreateSnapshotWorkload();
        ZxNextMachine fromFile = CreateSnapshotWorkload();
        PerturbPeripherals(fromCapture);
        PerturbPeripherals(fromFile);
        ZxNextMachineSnapshotService.Restore(fromCapture, captured);
        ZxNextMachineSnapshotService.Restore(fromFile, reloaded);

        Require(FingerprintNext(fromFile) == FingerprintNext(fromCapture),
            "A machine restored from the file differs from one restored from the capture.");
        Require(HashFrame(fromFile) == HashFrame(fromCapture),
            "A machine restored from the file shows a different picture to the capture.");

        foreach (byte register in RegisterFileFingerprintRegisters)
        {
            Require(fromFile.Registers.Read(register) == fromCapture.Registers.Read(register),
                $"NextReg ${register:X2} came back as ${fromFile.Registers.Read(register):X2} from the "
                    + $"file, not ${fromCapture.Registers.Read(register):X2}.");
        }

        // And then that both carry on identically, which catches an ordering mistake that
        // leaves the state right but the schedule wrong.
        for (int frame = 0; frame < 6; frame++)
        {
            original.RunFrame();
        }

        long expected = FingerprintNext(original);
        long expectedFrame = HashFrame(original);

        ZxNextMachine restored = CreateSnapshotWorkload();
        for (int frame = 0; frame < 3; frame++)
        {
            restored.RunFrame();
        }

        PerturbPeripherals(restored);
        ZxNextMachineSnapshotService.Restore(restored, reloaded);
        for (int frame = 0; frame < 6; frame++)
        {
            restored.RunFrame();
        }

        Require(FingerprintNext(restored) == expected,
            "A machine restored from a file diverged from the original.");
        Require(HashFrame(restored) == expectedFrame,
            "A machine restored from a file produced a different picture.");

        // A chunk from a newer build has to come back out again, in the file this build writes.
        var withUnknown = reloaded with
        {
            UnknownChunks = [new ZxNextSnapshotUnknownChunk("XTRA", [1, 2, 3, 4])]
        };
        using var preserved = new MemoryStream();
        ZxNextSnapshotFile.Save(preserved, withUnknown);
        preserved.Position = 0;
        ZxNextMachineSnapshot afterPreserve = ZxNextSnapshotFile.Load(preserved);
        Require(afterPreserve.UnknownChunks.Count == 1,
            $"An unknown chunk was dropped: {afterPreserve.UnknownChunks.Count} survived.");
        Require(afterPreserve.UnknownChunks[0].Id == "XTRA"
                && afterPreserve.UnknownChunks[0].Payload.AsSpan().SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            "An unknown chunk came back changed.");
        Require(FingerprintNext(restored) == expected,
            "Preserving an unknown chunk disturbed the state around it.");
    }

    /// <summary>
    /// Every peripheral field written into a file comes back out of it.
    /// </summary>
    /// <remarks>
    /// The continuation check proves the capture is complete and the file round trip proves a
    /// machine resumes, but both are driven by a workload, and a workload can only move the
    /// fields a guest can reach. A pending NMI's owner, for instance, is cleared by the time
    /// any two instructions have passed, so no workload can be captured holding one.
    ///
    /// So this goes the other way: it takes a real capture, overwrites the peripheral records
    /// with values nothing else would produce, and requires them back after a round trip.
    /// Record equality does the work, so a field left out of the writer or the reader fails
    /// here even though no machine would ever notice it.
    /// </remarks>
    private static void VerifyPeripheralFieldsRoundTrip()
    {
        ZxNextMachine machine = CreateSnapshotWorkload();
        for (int frame = 0; frame < 4; frame++)
        {
            machine.RunFrame();
        }

        ZxNextMachineSnapshot captured = ZxNextMachineSnapshotService.Capture(machine);
        var distinctive = captured with
        {
            Dma = new ZxNextDmaState(
                IsActive: true,
                Loaded: true,
                Z80CompatibleMode: true,
                DirectionAtoB: true,
                EndOfBlockNot: false,
                TransferredAtLeastOne: true,
                PortAIsIo: true,
                PortBIsIo: false,
                PendingWrite: 9,
                PendingHeader: 0x7D,
                ReadField: 5,
                ReadMask: 0x5A,
                TransferPhase: 1,
                PortAAddressMode: 2,
                PortBAddressMode: 0,
                PortATiming: 3,
                PortBTiming: 2,
                PortBPrescaler: 0x11,
                TransferByte: 0xC3,
                PortAStart: 0x1234,
                PortBStart: 0x5678,
                BlockLength: 0x9ABC,
                SourceAddress: 0xDEF0,
                DestinationAddress: 0x0FED,
                Counter: 0xCBA9,
                CurrentTransferLength: 0x00123456,
                LastTransferLength: 0x00654321),
            BusArbiter = new ZxNextBusArbiterState(ZxNextBusOwner.Dma, 11, 22, 33, 44),
            DivMmc = new ZxNextDivMmcState(
                ControlRegister: 0xC7,
                CardSelectRegister: 0xFD,
                SdCardSwap: true,
                Enabled: false,
                AutomapEnabled: true,
                AutomapHeld: true,
                NmiActive: true,
                EntryPoints0: 0x11,
                EntryPointsValid0: 0x22,
                EntryPointsTiming0: 0x33,
                EntryPoints1: 0x44),
            Multiface = new ZxNextMultifaceState(
                Enabled: false,
                NmiActive: true,
                IsMapped: true,
                Invisible: false),
            NmiRouting = new ZxNextNmiRoutingState(2),
            PortBus = new ZxNextPortBusState(
                LastWrittenPort: 0x243B,
                HasLastWrittenPort: true,
                LastWrittenValue: 0x5A,
                SelectedRegister: 0x6B,
                UlaOutputLatch: 0x17),
            FloatingBus = new ZxNextFloatingBusState(
                UlaFetchValue: 0x3C,
                Plus3CpuData: 0xC3,
                UlaDriving: true)
        };

        using var file = new MemoryStream();
        ZxNextSnapshotFile.Save(file, distinctive);
        file.Position = 0;
        ZxNextMachineSnapshot reloaded = ZxNextSnapshotFile.Load(file);

        Require(reloaded.Dma == distinctive.Dma,
            "A DMA controller field did not survive the round trip.");
        Require(reloaded.BusArbiter == distinctive.BusArbiter,
            "A bus arbiter field did not survive the round trip.");
        Require(reloaded.DivMmc == distinctive.DivMmc,
            "A DivMMC field did not survive the round trip.");
        Require(reloaded.Multiface == distinctive.Multiface,
            "A Multiface field did not survive the round trip.");
        Require(reloaded.NmiRouting == distinctive.NmiRouting,
            "The pending NMI's owner did not survive the round trip.");
        Require(reloaded.PortBus == distinctive.PortBus,
            "A port bus latch did not survive the round trip.");
        Require(reloaded.FloatingBus == distinctive.FloatingBus,
            "A floating bus latch did not survive the round trip.");

        // The two records with arrays in them, where equality would pass for free.
        RequireAudioAndInputMatch(distinctive, reloaded);

        // And a machine built from the reloaded file agrees with the records, which is what
        // makes this more than a test of the writer: the restore path has to read them too.
        ZxNextMachine rebuilt = CreateSnapshotWorkload();
        PerturbPeripherals(rebuilt);
        ZxNextMachineSnapshotService.Restore(rebuilt, reloaded);
        ZxNextMachineSnapshot recaptured = ZxNextMachineSnapshotService.Capture(rebuilt);
        Require(recaptured.DivMmc == distinctive.DivMmc,
            "DivMMC's restored latches disagree with the file they came from.");
        Require(recaptured.Multiface == distinctive.Multiface,
            "Multiface's restored latches disagree with the file they came from.");
        Require(recaptured.NmiRouting == distinctive.NmiRouting,
            "The restored NMI owner disagrees with the file it came from.");
        Require(recaptured.PortBus == distinctive.PortBus,
            "The restored port bus latches disagree with the file they came from.");
        Require(recaptured.FloatingBus == distinctive.FloatingBus,
            "The restored floating bus latches disagree with the file it came from.");
        Require(recaptured.Dma == distinctive.Dma,
            "The restored DMA controller disagrees with the file it came from.");
        Require(recaptured.BusArbiter == distinctive.BusArbiter,
            "The restored bus arbiter disagrees with the file it came from.");
    }

    /// <summary>Compares the two peripheral records that hold arrays.</summary>
    private static void RequireAudioAndInputMatch(
        ZxNextMachineSnapshot expected,
        ZxNextMachineSnapshot actual)
    {
        Require(actual.Input.KeyboardRows.AsSpan().SequenceEqual(expected.Input.KeyboardRows)
                && actual.Input.JoystickRows.AsSpan().SequenceEqual(expected.Input.JoystickRows)
                && actual.Input.ConnectorStates.AsSpan().SequenceEqual(expected.Input.ConnectorStates)
                && actual.Input.ConnectorModes.AsSpan().SequenceEqual(expected.Input.ConnectorModes),
            "The input state did not survive the round trip.");

        Require(actual.Audio.Chips.Length == expected.Audio.Chips.Length,
            "A PSG went missing in the round trip.");
        for (int chip = 0; chip < expected.Audio.Chips.Length; chip++)
        {
            AySnapshotState expectedChip = expected.Audio.Chips[chip];
            AySnapshotState actualChip = actual.Audio.Chips[chip];
            Require(actualChip.Registers.AsSpan().SequenceEqual(expectedChip.Registers),
                $"PSG {chip}'s registers did not survive the round trip.");
            Require(actualChip with { Registers = expectedChip.Registers } == expectedChip,
                $"PSG {chip}'s generator phase did not survive the round trip.");
        }

        Require(actual.Audio with { Chips = expected.Audio.Chips,
                SelectedRegisters = expected.Audio.SelectedRegisters,
                Pan = expected.Audio.Pan } == expected.Audio,
            "An audio field did not survive the round trip.");
        Require(actual.Audio.SelectedRegisters.AsSpan().SequenceEqual(expected.Audio.SelectedRegisters)
                && actual.Audio.Pan.AsSpan().SequenceEqual(expected.Audio.Pan),
            "The PSG selection or panning did not survive the round trip.");
    }

    /// <summary>
    /// A restore puts the card back to the sectors the machine was captured with.
    /// </summary>
    /// <remarks>
    /// The point of carrying media at all is that a machine and its card have to agree. A
    /// guest's filesystem driver holds directory entries and allocation tables in RAM; restore
    /// the RAM to an earlier point and leave the card where it is, and the next thing the
    /// guest writes goes somewhere the card no longer expects. So this asserts both halves:
    /// sectors the guest wrote after the capture are rewound to what the capture holds, and
    /// sectors it wrote after the capture that the capture never saw are rewound to what they
    /// held when the card was mounted.
    ///
    /// It also captures the card in the middle of an SPI command. A conversation half way
    /// through a six-byte command is the normal case rather than an edge one - a capture lands
    /// wherever the frame ended - and a restore that dropped it would answer the guest's next
    /// clock pulse from the wrong place.
    /// </remarks>
    private static void VerifySnapshotRewindsSdCard()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ZedExEss-snp005-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string imagePath = Path.Combine(directory, "card.img");

        // Small, but big enough that the sectors used here are not adjacent.
        byte[] pristine = new byte[64 * 512];
        for (int index = 0; index < pristine.Length; index++)
        {
            pristine[index] = (byte)(index * 7);
        }

        File.WriteAllBytes(imagePath, pristine);

        byte[] atCapture = BuildSectorPattern(0x11);
        byte[] afterCapture = BuildSectorPattern(0x22);
        byte[] untouchedByCapture = BuildSectorPattern(0x33);

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        SpectrumDivMmcSdCard card = SpectrumDivMmcSdCard.Open(imagePath, writeProtected: false);
        try
        {
            machine.DivMmc.AttachSdCard(0, card);
            machine.PortBus.WriteUncontended(0x12E7, 0x02);
            InitializeSdCard(machine);

            WriteSdSector(machine, 5, atCapture);
            Require(card.JournalledSectorCount == 1 && card.SectorJournalComplete,
                $"One written sector left a journal of {card.JournalledSectorCount}.");
            WriteSdSector(machine, 5, atCapture);
            Require(card.JournalledSectorCount == 1,
                "Writing the same sector twice journalled it twice.");

            // Captured part way through a command: four of the six bytes of a CMD17 have been
            // clocked in and the card is waiting for the rest.
            WriteSpi(machine, 0x40 | 17);
            WriteSpi(machine, 0x00);
            WriteSpi(machine, 0x00);
            WriteSpi(machine, 0x00);

            ZxNextMachineSnapshot snapshot = ZxNextMachineSnapshotService.Capture(machine);
            SpectrumSdCardState? capturedSlot = snapshot.Media.Slots[0];
            Require(capturedSlot != null, "The capture did not record the card in slot 0.");
            Require(capturedSlot!.CommandLength == 4,
                $"The capture recorded {capturedSlot.CommandLength} command byte(s), not four.");
            Require(capturedSlot.DirtySectorLbas.Length == 1 && capturedSlot.DirtySectorLbas[0] == 5,
                "The capture did not carry the one sector the guest had written.");
            Require(capturedSlot.DirtySectorData.AsSpan(0, 512).SequenceEqual(atCapture),
                "The capture carried the wrong contents for the written sector.");
            Require(snapshot.Media.Slots[1] == null, "The capture invented a card in the empty slot.");

            // Carry on: finish that command, overwrite the captured sector and write another
            // one the capture has never seen.
            WriteSpi(machine, 0x05);
            WriteSpi(machine, 0xFF);
            Require(ReadSdR1(machine) == 0x00, "The half-sent CMD17 did not complete.");
            DrainSdReadBlock(machine);

            WriteSdSector(machine, 5, afterCapture);
            WriteSdSector(machine, 9, untouchedByCapture);
            Require(ReadSdSector(machine, 5).AsSpan().SequenceEqual(afterCapture)
                    && ReadSdSector(machine, 9).AsSpan().SequenceEqual(untouchedByCapture),
                "The machine did not actually write the sectors it was meant to write.");

            ZxNextMediaRestoreReport report = ZxNextMachineSnapshotService.Restore(machine, snapshot);
            Require(report.Notes.Count == 0,
                $"Restoring its own card was refused: {string.Join("; ", report.Notes)}");

            // The restored card is mid-command, so that conversation has to be finished before
            // anything else is asked of it - which is the whole point of carrying it. Two more
            // bytes complete the CMD17 that was half sent when the capture was taken.
            WriteSpi(machine, 0x05);
            WriteSpi(machine, 0xFF);
            Require(ReadSdR1(machine) == 0x00,
                "The command that was half sent at capture time did not survive the restore.");
            byte[] streamed = DrainSdReadBlock(machine);
            Require(streamed.AsSpan().SequenceEqual(atCapture),
                "Completing the restored command read the wrong sector.");

            Require(ReadSdSector(machine, 5).AsSpan().SequenceEqual(atCapture),
                "The sector the capture carried was not put back.");
            Require(ReadSdSector(machine, 9).AsSpan().SequenceEqual(pristine.AsSpan(9 * 512, 512)),
                "A sector written after the capture was not rewound to what the card was mounted with.");

            // Through the file on disk as well, because that is what the guest will read next
            // time and what a folder merge would export.
            byte[] onDisk = ReadImageWhileMounted(imagePath);
            Require(onDisk.AsSpan(5 * 512, 512).SequenceEqual(atCapture),
                "The backing file still holds the post-capture contents of sector 5.");
            Require(onDisk.AsSpan(9 * 512, 512).SequenceEqual(pristine.AsSpan(9 * 512, 512)),
                "The backing file still holds the post-capture contents of sector 9.");

            // The same thing through a file, which is the only test of the media chunk.
            ZxNextMachineSnapshot reloaded = RoundTripThroughFile(snapshot);
            SpectrumSdCardState? reloadedSlot = reloaded.Media.Slots[0];
            Require(reloadedSlot != null, "The media chunk lost the card in slot 0.");
            Require(reloadedSlot.Identity == capturedSlot.Identity,
                "The card's identity did not survive the round trip.");
            Require(reloadedSlot.CommandLength == capturedSlot.CommandLength
                    && reloadedSlot.Command.AsSpan().SequenceEqual(capturedSlot.Command)
                    && reloadedSlot.Response.AsSpan().SequenceEqual(capturedSlot.Response)
                    && reloadedSlot.WriteBuffer.AsSpan().SequenceEqual(capturedSlot.WriteBuffer)
                    && reloadedSlot.WriteState == capturedSlot.WriteState
                    && reloadedSlot.WriteLba == capturedSlot.WriteLba
                    && reloadedSlot.WriteIndex == capturedSlot.WriteIndex
                    && reloadedSlot.Idle == capturedSlot.Idle
                    && reloadedSlot.AppCommand == capturedSlot.AppCommand
                    && reloadedSlot.MultipleRead == capturedSlot.MultipleRead
                    && reloadedSlot.MultipleReadLba == capturedSlot.MultipleReadLba
                    && reloadedSlot.WriteCount == capturedSlot.WriteCount
                    && reloadedSlot.Modified == capturedSlot.Modified
                    && reloadedSlot.FolderBackingFlushed == capturedSlot.FolderBackingFlushed
                    && reloadedSlot.SectorsComplete == capturedSlot.SectorsComplete,
                "The card's SPI state did not survive the round trip.");
            Require(reloadedSlot.DirtySectorLbas.AsSpan().SequenceEqual(capturedSlot.DirtySectorLbas)
                    && reloadedSlot.DirtySectorData.AsSpan().SequenceEqual(capturedSlot.DirtySectorData),
                "The journalled sectors did not survive the round trip.");

            // A card written to beyond what the journal can hold is left alone and says so,
            // rather than being put back from a journal that is missing most of it.
            // Same card, same journal, but flagged as too big to put back. The command length
            // is cleared with it: this is a test of the sector policy, and leaving the card
            // mid-command would have the reads below answered as the tail of that command.
            SpectrumSdCardState incomplete = capturedSlot with
            {
                SectorsComplete = false,
                CommandLength = 0
            };
            WriteSdSector(machine, 9, untouchedByCapture);
            ZxNextMediaRestoreReport declined = machine.DivMmc.RestoreMediaState(
                new ZxNextMediaState([incomplete, null]));
            Require(declined.Notes.Count == 1 && declined.Notes[0].Contains("too heavily", StringComparison.Ordinal),
                $"An uncapturable card was not reported: {string.Join("; ", declined.Notes)}");
            Require(ReadSdSector(machine, 9).AsSpan().SequenceEqual(untouchedByCapture),
                "A card that could not be put back was written to anyway.");
        }
        finally
        {
            machine.DivMmc.AttachSdCard(0, null);
            card.Dispose();
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// A state saved against one card does not get to write sectors into another.
    /// </summary>
    /// <remarks>
    /// This is the safety half of SNP-005. A snapshot is a document, and it names a path; if a
    /// restore acted on that name it would be a way of writing chosen bytes into a chosen file
    /// on someone else's machine. So the medium is matched first and the sector data is
    /// discarded when it does not fit what the host has actually mounted - and the refusal is
    /// reported, because a machine whose card was not put back is worth knowing about.
    /// </remarks>
    private static void VerifySnapshotRefusesForeignSdCard()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ZedExEss-snp005-foreign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string minePath = Path.Combine(directory, "mine.img");
        string theirsPath = Path.Combine(directory, "theirs.img");
        File.WriteAllBytes(minePath, new byte[32 * 512]);
        File.WriteAllBytes(theirsPath, new byte[32 * 512]);

        byte[] mineAtCapture = BuildSectorPattern(0x44);
        byte[] theirsContents = BuildSectorPattern(0x55);

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        SpectrumDivMmcSdCard mine = SpectrumDivMmcSdCard.Open(minePath, writeProtected: false);
        SpectrumDivMmcSdCard theirs = SpectrumDivMmcSdCard.Open(theirsPath, writeProtected: false);
        try
        {
            machine.DivMmc.AttachSdCard(0, mine);
            machine.PortBus.WriteUncontended(0x12E7, 0x02);
            InitializeSdCard(machine);
            WriteSdSector(machine, 3, mineAtCapture);

            ZxNextMachineSnapshot snapshot = ZxNextMachineSnapshotService.Capture(machine);

            // The user swaps the card. The state still describes the old one.
            machine.DivMmc.AttachSdCard(0, theirs);
            InitializeSdCard(machine);
            WriteSdSector(machine, 3, theirsContents);

            ZxNextMediaRestoreReport report = ZxNextMachineSnapshotService.Restore(machine, snapshot);
            Require(report.Notes.Count == 1,
                $"Restoring onto a different card produced {report.Notes.Count} note(s).");
            Require(report.Notes[0].Contains("mine.img", StringComparison.Ordinal)
                    && report.Notes[0].Contains("theirs.img", StringComparison.Ordinal),
                $"The refusal does not name both cards: {report.Notes[0]}");

            machine.PortBus.WriteUncontended(0x12E7, 0x02);
            InitializeSdCard(machine);
            Require(ReadSdSector(machine, 3).AsSpan().SequenceEqual(theirsContents),
                "A state from another card overwrote a sector of the mounted one.");
            Require(ReadImageWhileMounted(theirsPath).AsSpan(3 * 512, 512).SequenceEqual(theirsContents),
                "A state from another card overwrote a sector of the mounted card's file.");
            Require(ReadImageWhileMounted(minePath).AsSpan(3 * 512, 512).SequenceEqual(mineAtCapture),
                "Restoring disturbed a card that was no longer mounted.");

            // An empty slot is not an instruction to eject.
            machine.DivMmc.AttachSdCard(1, theirs);
            ZxNextMediaRestoreReport extra = machine.DivMmc.RestoreMediaState(
                new ZxNextMediaState([null, null]));
            Require(extra.Notes.Count == 2,
                $"Restoring over two mounted cards reported {extra.Notes.Count} note(s).");
        }
        finally
        {
            machine.DivMmc.AttachSdCard(0, null);
            machine.DivMmc.AttachSdCard(1, null);
            mine.Dispose();
            theirs.Dispose();
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Reads a card image while the card still has it open.
    /// </summary>
    /// <remarks>
    /// The card holds the file for writing, so a plain read is refused: the sharing mode has
    /// to admit that someone else is writing. Reading the file rather than the card is the
    /// point - it is what the guest will see next time, and what a folder merge exports.
    /// </remarks>
    private static byte[] ReadImageWhileMounted(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] BuildSectorPattern(byte seed)
    {
        var sector = new byte[512];
        for (int index = 0; index < sector.Length; index++)
        {
            sector[index] = (byte)((index * 31) ^ seed);
        }

        return sector;
    }

    /// <summary>Reads the data block of a CMD17 whose R1 has already been taken.</summary>
    private static byte[] DrainSdReadBlock(ZxNextMachine machine)
    {
        byte token = 0xFF;
        for (int attempt = 0; attempt < 16 && token == 0xFF; attempt++)
        {
            token = ReadSpi(machine);
        }

        Require(token == 0xFE, "A sector read did not return a data token.");
        var sector = new byte[512];
        for (int index = 0; index < sector.Length; index++)
        {
            sector[index] = ReadSpi(machine);
        }

        _ = ReadSpi(machine);
        _ = ReadSpi(machine);
        return sector;
    }

    private static ZxNextMachineSnapshot RoundTripThroughFile(ZxNextMachineSnapshot snapshot)
    {
        using var file = new MemoryStream();
        ZxNextSnapshotFile.Save(file, snapshot);
        file.Position = 0;
        return ZxNextSnapshotFile.Load(file);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary directory left behind is untidy, not a failure of what was checked.
        }
    }

    // ------------------------------------------------------ SNP-006 boundaries

    /// <summary>
    /// A machine that interrupts, draws and makes a noise, and never modifies its own code.
    /// </summary>
    /// <remarks>
    /// The snapshot workload is deliberately left alone - other checks depend on exactly what
    /// it does - and it is not suitable here anyway: it walks a pointer through the whole
    /// address space, which is fine for a fingerprint but means the program under test is not
    /// the program that was loaded. This one keeps its writes inside one display page, so a
    /// divergence a few thousand instructions after a restore is a divergence in the restore
    /// and not in whatever the machine had overwritten itself with.
    ///
    /// It also enables interrupts, which the other workloads do not. Most of SNP-006 is about
    /// boundaries that only exist when something can interrupt the CPU, and the handlers have
    /// to live in RAM: DivMMC is unmapped and page 16 is mapped over the bottom of the address
    /// space so $0038 and $0066 are ours to write.
    /// </remarks>
    private static ZxNextMachine CreateBoundaryWorkload()
    {
        ZxNextMachine machine = CreateSnapshotWorkload();

        // Undo what the snapshot workload sets up that would get in the way here. DivMMC and
        // Multiface both page themselves over the bottom of the address space, and Multiface
        // being mapped also suppresses the RETN that releases DivMMC - the exact thing one of
        // the probes below is about.
        machine.DivMmc.SetAutomapEnabled(false);
        machine.DivMmc.WriteControl(0x00);
        machine.Multiface.Reset();

        // The DMA controller is left mid-register-write over there, on purpose, so a reset byte
        // sent now would be swallowed as the operand that header is still waiting for. Four
        // bytes satisfy the longest pending header there is; a stray one is read as a harmless
        // port B mode write.
        for (int filler = 0; filler < 4; filler++)
        {
            machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0x00);
        }

        machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, 0xC3);

        // Interrupt mode 2 with a table in ordinary RAM, rather than mode 1. The bottom of a
        // Next's map is ROM and stays ROM whatever the MMU says while the boot ROM is enabled,
        // so a handler written to $0038 is a handler that is never executed - the CPU vectors
        // into ROM and walks off into whatever it finds. A full page of $7E, which is the usual
        // trick for not knowing what the data bus holds, puts the handler at $7E7E instead.
        for (int index = 0; index <= 0x100; index++)
        {
            machine.MemoryBus.WriteDirect((ushort)(InterruptVectorTable + index), InterruptHandler);
        }

        byte[] irqHandler =
        [
            0xF5,                   // PUSH AF
            0x3A, 0x00, 0x5F,       // LD A,($5F00)
            0x3C,                   // INC A
            0x32, 0x00, 0x5F,       // LD ($5F00),A
            0xF1,                   // POP AF
            0xFB,                   // EI
            0xED, 0x4D              // RETI
        ];
        for (int index = 0; index < irqHandler.Length; index++)
        {
            machine.MemoryBus.WriteDirect((ushort)(InterruptHandlerAddress + index), irqHandler[index]);
        }

        // A tight loop in RAM for a probe that needs the CPU somewhere predictable, and a RETN
        // for the one that needs to be about to leave an NMI.
        machine.MemoryBus.WriteDirect(ServiceLoopAddress, 0x3C);        // INC A
        machine.MemoryBus.WriteDirect((ushort)(ServiceLoopAddress + 1), 0x18);
        machine.MemoryBus.WriteDirect((ushort)(ServiceLoopAddress + 2), 0xFD);  // JR -3
        machine.MemoryBus.WriteDirect(ReturnFromNmiAddress, 0xED);
        machine.MemoryBus.WriteDirect((ushort)(ReturnFromNmiAddress + 1), 0x45);  // RETN

        byte[] program =
        [
            0x3E, InterruptVectorPage,
            0xED, 0x47,             // LD I,$7D
            0xED, 0x5E,             // IM 2
            0xFB,                   // EI
            0x21, 0x00, 0x40,       // LD HL,$4000
            0x3E, 0x01,             // LD A,$01
            0x77,                   // LD (HL),A        <- loop at $800C
            0x2C,                   // INC L            (stays inside one page)
            0x77,                   // LD (HL),A
            0x3C,                   // INC A
            0xED, 0x92, 0x26,       // NEXTREG $26,A
            0xED, 0x92, 0x7F,       // NEXTREG $7F,A
            0xD3, 0xFE,             // OUT ($FE),A
            0x01, 0xFD, 0xFF,       // LD BC,$FFFD
            0xED, 0x79,             // OUT (C),A
            0x06, 0xBF,             // LD B,$BF
            0xED, 0x79,             // OUT (C),A
            0x18, 0xE9              // JR back to the start of the loop
        ];
        for (int index = 0; index < program.Length; index++)
        {
            machine.MemoryBus.WriteDirect((ushort)(0x8000 + index), program[index]);
        }

        machine.Cpu.PC = 0x8000;
        machine.Cpu.SP = 0x7FF0;
        return machine;
    }

    /// <summary>Where the boundary workload keeps its interrupt table, handlers and loop.</summary>
    /// <remarks>
    /// All in ordinary RAM above the display, because the bottom of a Next's map belongs to ROM
    /// and to whichever of DivMMC and Multiface has paged itself in.
    /// </remarks>
    private const byte InterruptVectorPage = 0x7D;
    private const ushort InterruptVectorTable = InterruptVectorPage << 8;
    private const byte InterruptHandler = 0x7E;
    private const ushort InterruptHandlerAddress = (InterruptHandler << 8) | InterruptHandler;
    private const ushort ServiceLoopAddress = 0x4200;
    private const ushort ReturnFromNmiAddress = 0x4100;

    /// <summary>The instruction window the boundary workload's main loop occupies.</summary>
    private const ushort BoundaryLoopStart = 0x800C;
    private const ushort BoundaryLoopEnd = 0x8023;

    /// <summary>Runs the boundary workload to a repeatable starting point.</summary>
    private static void SettleBoundaryWorkload(ZxNextMachine machine)
    {
        for (int frame = 0; frame < 3; frame++)
        {
            machine.RunFrame();
        }
    }

    private static void StepInstructions(ZxNextMachine machine, int instructions)
    {
        for (int step = 0; step < instructions; step++)
        {
            machine.StepInstruction();
        }
    }

    /// <summary>Steps until something becomes true, saying how many instructions it took.</summary>
    private static int StepUntil(
        ZxNextMachine machine,
        Func<ZxNextMachine, bool> condition,
        int limit,
        string what)
    {
        for (int step = 0; step < limit; step++)
        {
            if (condition(machine))
            {
                return step;
            }

            machine.StepInstruction();
        }

        throw new InvalidOperationException(
            $"The boundary workload never reached {what} within {limit} instructions.");
    }

    /// <summary>
    /// Captures a machine at a chosen point and requires a restore to continue identically.
    /// </summary>
    /// <remarks>
    /// Built from <paramref name="build"/> twice rather than by restoring one machine twice, so
    /// the expected continuation comes from a machine that was never restored. A restore that
    /// was wrong in the same way on both sides would otherwise agree with itself.
    ///
    /// The destination is run on and perturbed first, for the reason recorded against
    /// SNP-004: two machines built by the same recipe already agree, and a restore that
    /// dropped something would pass by accident.
    /// </remarks>
    private static void RequireRestoreContinues(
        string where,
        Func<ZxNextMachine> build,
        Action<ZxNextMachine> positionAtProbe,
        Action<ZxNextMachine> continueOn,
        Action<ZxNextMachine, ZxNextMachineSnapshot>? guard = null,
        bool comparePicture = true)
    {
        ZxNextMachine original = build();
        positionAtProbe(original);
        ZxNextMachineSnapshot snapshot = ZxNextMachineSnapshotService.Capture(original);
        guard?.Invoke(original, snapshot);

        DrainAudio(original);
        continueOn(original);
        long expected = FingerprintNext(original);
        long expectedPicture = HashFrame(original);
        short[] expectedPcm = DrainAudio(original);

        ZxNextMachine destination = build();
        SettleBoundaryWorkload(destination);
        PerturbPeripherals(destination);
        ZxNextMachineSnapshotService.Restore(destination, snapshot);
        Require(destination.Clock.MasterTicks == snapshot.Clock.MasterTicks,
            $"A restore {where} landed at {destination.Clock.MasterTicks} master ticks, not "
                + $"{snapshot.Clock.MasterTicks}.");

        DrainAudio(destination);
        continueOn(destination);

        Require(FingerprintNext(destination) == expected,
            $"A restore {where} diverged from the machine it was captured from.");
        if (comparePicture)
        {
            Require(HashFrame(destination) == expectedPicture,
                $"A restore {where} produced a different picture.");
        }

        Require(DrainAudio(destination).AsSpan().SequenceEqual(expectedPcm),
            $"A restore {where} produced different audio.");
    }

    /// <summary>
    /// Every instruction boundary across an interrupt has to be a valid place to resume from.
    /// </summary>
    /// <remarks>
    /// The other snapshot checks capture at whatever boundary a frame happened to end on, which
    /// is where a person pressing a key captures. A greenzone or a TAS captures wherever it
    /// likes, and the interesting places are the ones where something is half done: the
    /// interrupt line has gone active but the CPU has not taken it yet, the CPU has just pushed
    /// the return address, the handler has run two instructions, the RETI has not happened.
    /// Sweeping consecutive boundaries covers all of those without having to name them, and
    /// covers the audio sample boundaries scattered among them for free.
    /// </remarks>
    private static void VerifyRestoreAcrossInterruptBoundaries()
    {
        // Where the interrupt is, counted in instructions from the settle point.
        ZxNextMachine scout = CreateBoundaryWorkload();
        SettleBoundaryWorkload(scout);
        Require(scout.Cpu.PC >= BoundaryLoopStart && scout.Cpu.PC <= BoundaryLoopEnd,
            $"The boundary workload is at ${scout.Cpu.PC:X4}, not in its loop.");
        StepInstructions(scout, 64);
        int toInterrupt = 64 + StepUntil(
            scout,
            static m => m.UlaTiming.InterruptLineActive,
            200_000,
            "an active interrupt line");
        // The CPU takes the interrupt on the same boundary where the line goes active: the Z80
        // samples it at the end of an instruction, so by the time a host can look, the vector
        // has already been fetched. "Line active but not yet taken" is therefore not a state
        // this machine can be captured in, and the probe at offset zero is the handler's first
        // instruction rather than the one before it.
        int handlerEntry = toInterrupt + StepUntil(
            scout,
            static m => m.Cpu.PC >= InterruptHandlerAddress
                && m.Cpu.PC < InterruptHandlerAddress + 12,
            2_000,
            "its interrupt handler");
        Require(handlerEntry == toInterrupt,
            $"The interrupt was taken {handlerEntry - toInterrupt} instruction(s) after the line "
                + "went active, so the sweep below is not centred on it.");
        Require(scout.MemoryBus.ReadDirect(0x5F00) > 0,
            "The interrupt handler never ran, so the sweep below would prove nothing.");

        for (int offset = -4; offset <= 20; offset += 2)
        {
            int instructions = toInterrupt + offset;
            string where = offset == 0
                ? "on the interrupt handler's first instruction"
                : $"{Math.Abs(offset)} instruction(s) {(offset < 0 ? "before" : "after")} the interrupt";

            RequireRestoreContinues(
                where,
                CreateBoundaryWorkload,
                machine =>
                {
                    SettleBoundaryWorkload(machine);
                    StepInstructions(machine, instructions);
                },
                static machine => StepInstructions(machine, 4_000),
                comparePicture: false);
        }

        // And across a whole frame from inside the handler, which is the case where the
        // interrupt schedule and the beam have to agree about what has already happened.
        RequireRestoreContinues(
            "from inside the interrupt handler",
            CreateBoundaryWorkload,
            machine =>
            {
                SettleBoundaryWorkload(machine);
                StepInstructions(machine, handlerEntry + 2);
            },
            static machine =>
            {
                machine.RunFrame();
                machine.RunFrame();
            },
            static (machine, snapshot) => Require(
                snapshot.Cpu.PC >= InterruptHandlerAddress
                    && snapshot.Cpu.PC < InterruptHandlerAddress + 12,
                $"The probe meant to be inside the handler is at ${snapshot.Cpu.PC:X4}."));
    }

    /// <summary>
    /// Publishing a frame is a boundary like any other, and has to be resumable.
    /// </summary>
    /// <remarks>
    /// Kept apart from the sweep because the failure it looks for is specific: a machine
    /// restored just before a frame is published can publish it twice or not at all, and
    /// either shows up as a host that is one frame out for the rest of the session rather
    /// than as a wrong pixel.
    /// </remarks>
    private static void VerifyRestoreAtFramePublication()
    {
        ZxNextMachine scout = CreateBoundaryWorkload();
        SettleBoundaryWorkload(scout);
        ulong frame = scout.UlaTiming.FrameNumber;
        int toPublication = StepUntil(
            scout,
            m => m.UlaTiming.FrameNumber != frame,
            300_000,
            "the next frame");

        foreach (int offset in new[] { -2, -1, 0, 1, 2 })
        {
            int instructions = toPublication + offset;
            RequireRestoreContinues(
                $"{Math.Abs(offset)} instruction(s) {(offset < 0 ? "before" : "after")} a frame was published",
                CreateBoundaryWorkload,
                machine =>
                {
                    SettleBoundaryWorkload(machine);
                    StepInstructions(machine, instructions);
                },
                static machine =>
                {
                    machine.RunFrame();
                    machine.RunFrame();
                });
        }
    }

    /// <summary>
    /// A machine captured inside an NMI service window has to come back inside it.
    /// </summary>
    /// <remarks>
    /// This is where the owner of a pending NMI earns its place in the snapshot. An NMI on a
    /// Next is not just "the CPU is at $0066": DivMMC's window is paged in over the bottom of
    /// the address space for the duration, and the RETN that ends it releases the device that
    /// raised it. A restore that put the CPU back without the routing would release the wrong
    /// device, and one that put the latches back without the automap would run the service
    /// routine with nothing underneath it.
    ///
    /// The continuations are counted in instructions rather than frames on purpose: with
    /// DivMMC paged in the service routine is whatever is in its ROM, which in a foundation
    /// machine is blank, so the CPU walks forward through it. A few thousand instructions stay
    /// inside that window, which keeps the comparison about the restore instead of about what
    /// a wandering program eventually did.
    /// </remarks>
    private static void VerifyRestoreAroundNmi()
    {
        // Requested, not yet taken: the request lives in the register file and is picked up at
        // the next instruction boundary.
        RequireRestoreContinues(
            "with an NMI requested but not yet taken",
            CreateBoundaryWorkload,
            static machine =>
            {
                SettleBoundaryWorkload(machine);
                StepInstructions(machine, 32);
                machine.Registers.Write(0x02, 0x04);
            },
            static machine => StepInstructions(machine, 2_000),
            static (machine, snapshot) =>
            {
                Require(snapshot.Registers.PendingDivMmcNmi,
                    "The probe does not actually hold a pending DivMMC NMI.");
                Require(!machine.DivMmc.NmiActive,
                    "The NMI was taken before the capture, so this probe is the wrong one.");
            },
            comparePicture: false);

        // Taken: inside the service window, with DivMMC's automap held.
        RequireRestoreContinues(
            "inside an NMI service window",
            CreateBoundaryWorkload,
            static machine =>
            {
                SettleBoundaryWorkload(machine);
                StepInstructions(machine, 32);
                machine.Registers.Write(0x02, 0x04);
                StepInstructions(machine, 1);

                // The CPU has vectored to $0066, which with DivMMC's window paged in is its ROM
                // - blank in a foundation machine, so the CPU would walk forward through it and
                // then through whatever it found. Pointing it at a loop in RAM keeps the probe
                // about the NMI state rather than about where a wandering program ended up.
                machine.Cpu.PC = ServiceLoopAddress;
            },
            static machine => StepInstructions(machine, 2_000),
            static (machine, snapshot) =>
            {
                Require(machine.DivMmc.NmiActive && machine.DivMmc.AutomapHeld,
                    "The probe meant to be inside an NMI is not in one.");
                Require(snapshot.DivMmc.NmiActive && snapshot.DivMmc.AutomapHeld,
                    "The capture did not record being inside an NMI.");
            },
            comparePicture: false);

        // About to leave: a RETN placed in ordinary RAM, outside the window DivMMC has paged
        // in, so the instruction that releases the device is the next thing to execute.
        RequireRestoreContinues(
            "immediately before the RETN that ends an NMI",
            CreateBoundaryWorkload,
            static machine =>
            {
                SettleBoundaryWorkload(machine);
                StepInstructions(machine, 32);
                machine.Registers.Write(0x02, 0x04);
                StepInstructions(machine, 1);
                machine.Cpu.PC = ReturnFromNmiAddress;
            },
            static machine =>
            {
                StepInstructions(machine, 1);
                Require(!machine.DivMmc.NmiActive && !machine.DivMmc.AutomapHeld,
                    "RETN did not end the NMI service window.");
                StepInstructions(machine, 1_999);
            },
            static (machine, snapshot) => Require(
                snapshot.DivMmc.AutomapHeld && snapshot.Cpu.PC == ReturnFromNmiAddress,
                "The probe is not sitting on the RETN inside an NMI."),
            comparePicture: false);
    }

    /// <summary>
    /// A transfer that has been enabled but not yet granted the bus has to survive.
    /// </summary>
    /// <remarks>
    /// The machine drains a DMA request completely within one step, so between instructions is
    /// the only place a host can capture - and the adversarial case there is a transfer that
    /// has been enabled and has not moved a byte. A restore that lost it leaves the guest
    /// waiting for a blit that never happens, with the destination still holding whatever was
    /// there before.
    /// </remarks>
    private static void VerifyRestoreWithDmaPending()
    {
        RequireRestoreContinues(
            "with a DMA transfer enabled but not yet started",
            CreateBoundaryWorkload,
            static machine =>
            {
                SettleBoundaryWorkload(machine);
                StepInstructions(machine, 16);
                for (int index = 0; index < 8; index++)
                {
                    machine.MemoryBus.WriteDirect((ushort)(0x9100 + index), (byte)(0x5A + index));
                    machine.MemoryBus.WriteDirect((ushort)(0xA100 + index), 0x00);
                }

                byte[] sequence =
                [
                    0x7D, 0x00, 0x91, 0x07, 0x00,   // WR0: A to B from $9100, eight bytes
                    0x14, 0x10,                     // WR1/WR2: memory, incrementing
                    0x8D, 0x00, 0xA1,               // WR4: to $A100
                    0xCF,                           // load
                    0x87                            // enable
                ];
                foreach (byte value in sequence)
                {
                    machine.Dma.Write(ZxNextDmaDevice.ZxnControlPort, value);
                }
            },
            static machine =>
            {
                StepInstructions(machine, 8);
                for (int index = 0; index < 8; index++)
                {
                    Require(machine.MemoryBus.ReadDirect((ushort)(0xA100 + index)) == (byte)(0x5A + index),
                        "The pending transfer did not copy its block once the machine ran on.");
                }

                StepInstructions(machine, 2_000);
            },
            static (machine, snapshot) =>
            {
                Require(machine.Dma.IsActive && snapshot.Dma.IsActive,
                    "The probe does not hold an enabled transfer.");
                Require(machine.MemoryBus.ReadDirect(0xA100) == 0x00,
                    "The transfer had already moved a byte before the capture.");
            },
            comparePicture: false);
    }

    /// <summary>
    /// A capture half way through a sector write has to put the same bytes on the card.
    /// </summary>
    /// <remarks>
    /// The storage half of SNP-006, and the one oracle that looks at the medium rather than at
    /// the machine. The guest is left clocking a payload out of the accumulator, so the bytes
    /// that reach the sector depend on exactly where the capture was taken: a restore that got
    /// the SPI framing, the write index or the CPU's phase wrong writes a different sector,
    /// and the card is where that shows.
    ///
    /// Both continuations run on the same machine, because the medium cannot be shared by two
    /// machines - a second mount of the same file is refused by the host filesystem, and
    /// mounting a copy would be refused by the identity check. That makes this the one
    /// continuation check whose expected side comes from a machine that was restored, which is
    /// acceptable here because what is compared is the card, not the machine.
    /// </remarks>
    private static void VerifyRestoreMidSectorWrite()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ZedExEss-snp006-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string imagePath = Path.Combine(directory, "card.img");
        File.WriteAllBytes(imagePath, new byte[64 * 512]);

        ZxNextMachine machine = CreateBoundaryWorkload();
        SpectrumDivMmcSdCard card = SpectrumDivMmcSdCard.Open(imagePath, writeProtected: false);
        try
        {
            machine.DivMmc.AttachSdCard(0, card);
            machine.PortBus.WriteUncontended(0x12E7, 0x02);
            InitializeSdCard(machine);

            // The guest clocks the payload: OUT ($EB),A / INC A / JR back.
            byte[] clocker = [0xD3, 0xEB, 0x3C, 0x18, 0xFB];
            for (int index = 0; index < clocker.Length; index++)
            {
                machine.MemoryBus.WriteDirect((ushort)(0x8100 + index), clocker[index]);
            }

            SendSdCommand(machine, 24, 3);
            Require(ReadSdR1(machine) == 0x00, "CMD24 was rejected before the probe.");
            WriteSpi(machine, 0xFE);

            machine.Cpu.PC = 0x8100;
            machine.Cpu.A = 0x40;
            StepInstructions(machine, 61);

            ZxNextMachineSnapshot snapshot = ZxNextMachineSnapshotService.Capture(machine);
            SpectrumSdCardState? slot = snapshot.Media.Slots[0];
            Require(slot != null, "The probe did not capture the card.");
            Require(slot!.WriteIndex is > 0 and < 514,
                $"The probe is not inside a sector payload; the write index is {slot.WriteIndex}.");

            StepInstructions(machine, 4_000);
            byte[] firstPass = ReadImageWhileMounted(imagePath).AsSpan(3 * 512, 512).ToArray();
            Require(firstPass.AsSpan().IndexOfAnyExcept((byte)0) >= 0,
                "The guest never finished the sector, so there is nothing to compare.");

            ZxNextMediaRestoreReport report = ZxNextMachineSnapshotService.Restore(machine, snapshot);
            Require(report.Notes.Count == 0,
                $"Restoring the mid-write card was refused: {string.Join("; ", report.Notes)}");
            Require(ReadImageWhileMounted(imagePath).AsSpan(3 * 512, 512).IndexOfAnyExcept((byte)0) < 0,
                "The restore did not rewind the sector the guest had written.");

            StepInstructions(machine, 4_000);
            byte[] secondPass = ReadImageWhileMounted(imagePath).AsSpan(3 * 512, 512).ToArray();
            Require(secondPass.AsSpan().SequenceEqual(firstPass),
                "Resuming a half-finished sector write put different bytes on the card.");
        }
        finally
        {
            machine.DivMmc.AttachSdCard(0, null);
            card.Dispose();
            TryDeleteDirectory(directory);
        }
    }

    /// <summary>
    /// Restoring into a machine that is holding instruction time must not add it on.
    /// </summary>
    /// <remarks>
    /// A regression test for a real bug, found while SNP-004 was being built. The CPU batches
    /// instruction T-states and publishes them to the clk28 timeline in groups; putting the CPU
    /// back flushes whatever the machine being overwritten was still holding, and that used to
    /// happen *after* the clock had been set from the snapshot, so a restore landed a few ticks
    /// ahead of the machine that was captured. It only showed up at all because a longer
    /// workload left a batch outstanding at a frame boundary where a shorter one did not, which
    /// is exactly the kind of accident a named check should not depend on.
    /// </remarks>
    private static void VerifyRestoreIntoBatchedMachine()
    {
        ZxNextMachine original = CreateBoundaryWorkload();
        SettleBoundaryWorkload(original);
        StepInstructions(original, 123);
        ZxNextMachineSnapshot snapshot = ZxNextMachineSnapshotService.Capture(original);

        for (int instructions = 1; instructions <= 9; instructions += 2)
        {
            ZxNextMachine destination = CreateBoundaryWorkload();
            SettleBoundaryWorkload(destination);

            // Stopping part way through a group leaves time the CPU has counted and the devices
            // have not seen. SynchronizeTime is deliberately not called: that is the host's
            // state at the moment it loads a save state.
            StepInstructions(destination, instructions);
            ZxNextMachineSnapshotService.Restore(destination, snapshot);

            Require(destination.Clock.MasterTicks == snapshot.Clock.MasterTicks,
                $"Restoring over a machine {instructions} instruction(s) into a batch landed at "
                    + $"{destination.Clock.MasterTicks} master ticks, not "
                    + $"{snapshot.Clock.MasterTicks}.");
            Require(destination.Cpu.Cyc == snapshot.Cpu.Cycles,
                $"The restored CPU is at {destination.Cpu.Cyc} T-states, not {snapshot.Cpu.Cycles}.");

            // And the time stays right once it starts running again, which is what the clock
            // being wrong would really cost: a machine a few ticks out drifts a frame apart.
            StepInstructions(destination, 500);
            StepInstructions(original, 500);
            original.SynchronizeTime();
            destination.SynchronizeTime();
            Require(destination.Clock.MasterTicks == original.Clock.MasterTicks,
                "A restored machine and the one it was captured from kept different time.");
            ZxNextMachineSnapshotService.Restore(original, snapshot);
        }
    }

    /// <summary>
    /// Every way a snapshot file can be wrong is refused by name.
    /// </summary>
    /// <remarks>
    /// A save state is loaded by people who may have picked the wrong file, and the failure
    /// has to say so rather than throw something generic or - worse - restore half a machine.
    /// </remarks>
    private static void VerifySnapshotFileFailures()
    {
        ZxNextMachine machine = CreateSnapshotWorkload();
        machine.RunFrame();
        ZxNextMachineSnapshot snapshot = ZxNextMachineSnapshotService.Capture(machine);

        using var good = new MemoryStream();
        ZxNextSnapshotFile.Save(good, snapshot);
        byte[] bytes = good.ToArray();

        RequireRefused(
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13],
            "signature",
            "A file with the wrong signature was accepted.");

        RequireRefused([(byte)'Z', (byte)'X'], "header", "A file too short to hold a header was accepted.");

        // A version from the future must be refused by number rather than misread.
        byte[] future = bytes.ToArray();
        future[4] = 99;
        future[5] = 0;
        RequireRefused(future, "version", "A newer format version was accepted.");

        // A truncated body is a truncated body, however plausible the header.
        RequireRefused(bytes.AsSpan(0, bytes.Length - 10).ToArray(), null,
            "A truncated file was accepted.");

        // A good file still loads, so none of the above is passing by accident.
        using var reread = new MemoryStream(bytes);
        Require(ZxNextSnapshotFile.Load(reread).PhysicalRam.Length == snapshot.PhysicalRam.Length,
            "The unmodified file stopped loading.");

        static void RequireRefused(byte[] content, string? mentions, string message)
        {
            using var stream = new MemoryStream(content);
            try
            {
                ZxNextSnapshotFile.Load(stream);
            }
            catch (InvalidDataException ex)
            {
                Require(
                    mentions == null
                        || ex.Message.Contains(mentions, StringComparison.OrdinalIgnoreCase),
                    $"The refusal did not mention '{mentions}': {ex.Message}");
                return;
            }

            Require(false, message);
        }
    }

    private static void VerifyPinnedSourceBaseline()
    {
        Require(ZxNextReferenceBaseline.FpgaRepositoryCommit.Length == 40,
            "The FPGA source baseline is not a full Git commit ID.");
        Require(ZxNextReferenceBaseline.SystemRepositoryCommit.Length == 40,
            "The System/Next source baseline is not a full Git commit ID.");
        Require(ZxNextReferenceBaseline.CoreVersion == "3.02.03",
            "The executable baseline no longer matches the accepted source ADR.");
        Require(ZxNextReferenceBaseline.MachineId == 0x08,
            "The foundation must identify as an emulator, not a physical PCB.");
        Require(ZxNextReferenceBaseline.InitialRamBytes == 1024 * 1024,
            "The initial supported RAM configuration is no longer 1 MiB.");
    }

    private static void VerifyMachineDescriptor()
    {
        MachineDescriptor descriptor = ZxNextModelDescriptors.Native;
        Require(descriptor.Family == MachineFamily.ZxNext,
            "The Next descriptor was assigned to another machine family.");
        Require(descriptor.Id == "zx-spectrum-next", "The Next descriptor ID is unstable.");
        Require(descriptor.CpuClockHz == ZxNextReferenceBaseline.BaseCpuClockHz,
            "The Next descriptor does not report its reset clock.");

        Require(SpectrumMachineDescriptors.ForModel(SpectrumModel.Spectrum48K).Family == MachineFamily.Spectrum,
            "Adding the Next family changed the Spectrum descriptor family.");
        Require(Zx8xModelDescriptors.ForModel(Zx8xModel.Zx81).Family == MachineFamily.Zx8x,
            "Adding the Next family changed the ZX8x descriptor family.");
    }

    private static void VerifyBootstrapBuses()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        Require(machine.MemoryBus.GetType().IsSealed, "The bootstrap memory bus is not sealed.");
        Require(machine.PortBus.GetType().IsSealed, "The bootstrap port bus is not sealed.");
        Require(machine.Cpu.GetType().IsSealed, "The Next CPU specialization is not sealed.");
        Require(machine.PortBus.ReadUncontended(ZxNextPortBus.NextRegisterSelectPort) == 0x24,
            "NextReg selector did not expose its hardware reset value.");

        machine.MemoryBus.WriteCpu(0x8000, 0xA5);
        Require(machine.MemoryBus.Read(0x8000) == 0xA5
                && machine.MemoryBus.FetchOpcode(0x8000) == 0xA5,
            "The reset MMU mapping did not preserve a CPU write.");
    }

    private static void VerifyBlankMachineExecution()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.StepInstruction();
        Require(machine.Cpu.PC == 1, "A zero-filled reset ROM NOP did not advance PC.");
        Require(machine.Cpu.Cyc == 4, "A reset-ROM NOP did not consume four CPU T-states.");
    }

    private static void VerifyMasterTimeline()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.RunForTstates(17);
        Require(machine.Cpu.Cyc >= 17, "The requested CPU interval was not executed.");
        Require(machine.MasterTicks == machine.Cpu.Cyc * ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate,
            "Base-speed CPU time was not converted to exact mode-0 master ticks.");
    }

    private static void VerifyFoundationNextRegisters()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        Require(machine.Registers.Read(0x00) == 0x08, "NextReg 0x00 did not expose emulator identity.");
        Require(machine.Registers.Read(0x01) == 0x32 && machine.Registers.Read(0x0E) == 0x03,
            "NextReg core version fields do not encode 3.02.03.");
        Require(machine.Registers.Read(0x03) == 0x33 && machine.MemoryBus.ConfigurationMode
                && machine.MemoryBus.BootRomEnabled,
            "NextReg 0x03 or the initial boot/config state did not reset to native +3 defaults.");
        Require(machine.Registers.Read(0x07) == 0x00,
            "NextReg 0x07 did not reset programmed and actual CPU speed.");
        Require(machine.Registers.Read(0x08) == 0x90,
            "NextReg 0x08 did not expose the unlocked paging and speaker reset state.");
        Require(machine.Registers.Read(0x05) == 0x41,
            "NextReg 0x05 did not expose the 50 Hz/scandoubler/joystick reset state.");
        Require(machine.Registers.Read(0x7F) == 0xFF,
            "NextReg 0x7F did not expose its documented reset value.");
        machine.Registers.Write(0x7F, 0x39);
        Require(machine.Registers.Read(0x7F) == 0x39,
            "NextReg 0x7F did not preserve user storage.");
        machine.Reset(ZxNextResetKind.Soft);
        Require(machine.Registers.Read(0x7F) == 0xFF,
            "A soft reset did not restore NextReg 0x7F to 0xFF.");

        byte[] expectedMmu = [0xFF, 0xFF, 10, 11, 4, 5, 0, 1];
        for (int slot = 0; slot < expectedMmu.Length; slot++)
        {
            Require(machine.Registers.Read((byte)(0x50 + slot)) == expectedMmu[slot],
                $"MMU slot {slot} did not expose its documented reset page.");
        }
    }

    private static void VerifyUlaFrameInterrupt()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextUlaTiming timing = machine.UlaTiming;
        int resetProfileGeneration = timing.ProfileGeneration;
        Require(timing.PixelsPerLine == 456
                && timing.LinesPerFrame == 311
                && timing.MasterTicksPerFrame == 567_264
                && timing.InterruptOffsetMasterTicks == 2_334
                && timing.InterruptPulseCpuTstates == 32,
            "The reset +3/50 Hz raster geometry does not match zxula_timing.vhd.");
        Require(!timing.SynchronizeProfile()
                && timing.ProfileGeneration == resetProfileGeneration,
            "An unchanged ULA profile unexpectedly advanced its generation.");

        machine.RunForTstates(288);
        Require(timing.InterruptCount == 0 && !timing.InterruptLineActive,
            "The +3 ULA interrupt started before its pinned raster coordinate.");
        machine.StepInstruction();
        Require(timing.InterruptCount == 1 && timing.InterruptLineActive,
            "The +3 ULA interrupt was not asserted while crossing its raster coordinate.");
        machine.RunForTstates(32);
        Require(!timing.InterruptLineActive,
            "The +3 ULA interrupt pulse exceeded 32 CPU T-states.");
        machine.RunForTstates(70_584);
        Require(timing.FrameNumber == 1 && timing.FrameMasterTick == 0,
            "The +3/50 Hz frame did not complete after exactly 567264 master ticks.");

        ZxNextMachine sixtyHertz = ZxNextMachineFactory.CreateFoundationMachine();
        int fiftyHertzGeneration = sixtyHertz.UlaTiming.ProfileGeneration;
        sixtyHertz.Registers.Write(0x03, 0x91);
        sixtyHertz.Registers.Write(0x05, 0x45);
        sixtyHertz.StepInstruction();
        Require(sixtyHertz.Registers.SixtyHertzMode
                && sixtyHertz.UlaTiming.PixelsPerLine == 448
                && sixtyHertz.UlaTiming.LinesPerFrame == 264
                && sixtyHertz.UlaTiming.MasterTicksPerFrame == 473_088
                && sixtyHertz.UlaTiming.ProfileGeneration != fiftyHertzGeneration,
            "NextReg $05 did not select the 48K/60 Hz frame profile.");

        ZxNextMachine pentagon = ZxNextMachineFactory.CreateFoundationMachine();
        pentagon.Registers.Write(0x05, 0x45);
        pentagon.Registers.Write(0x03, 0xC4);
        pentagon.StepInstruction();
        Require(!pentagon.Registers.SixtyHertzMode
                && (pentagon.Registers.Read(0x05) & 0x04) == 0
                && pentagon.UlaTiming.PixelsPerLine == 448
                && pentagon.UlaTiming.LinesPerFrame == 320
                && pentagon.UlaTiming.MasterTicksPerFrame == 573_440
                && pentagon.UlaTiming.InterruptPulseCpuTstates == 36,
            "Pentagon timing did not force its 50 Hz geometry and interrupt pulse.");

        ZxNextMachine halted = CreateProgram(0x76);
        halted.Cpu.SetInterruptState(1, iff1: true, iff2: true);
        halted.StepInstruction();
        Require(halted.Cpu.IsHalted && halted.Cpu.PC == 0,
            "The ULA interrupt wake-up fixture did not enter HALT.");
        while (halted.UlaTiming.InterruptCount == 0)
        {
            halted.StepInstruction();
        }

        Require(!halted.Cpu.IsHalted && halted.Cpu.PC == 0x0038
                && !halted.Cpu.Iff1,
            "The frame interrupt did not release HALT and enter the IM1 vector.");
        halted.Reset();
        Require(halted.UlaTiming.FrameNumber == 0
                && halted.UlaTiming.InterruptCount == 0
                && !halted.UlaTiming.InterruptLineActive,
            "Machine reset did not reset the ULA frame and interrupt state.");
    }

    private static void VerifyInterruptEnableRegisters()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        Require(machine.Registers.Read(0xC4) == 0x81
                && machine.Registers.Read(0x22) == 0x00,
            "The interrupt-enable registers did not expose their reset state.");

        machine.Registers.Write(0xC4, 0x82);
        Require(machine.Registers.Read(0xC4) == 0x82
                && machine.Registers.Read(0x22) == 0x06,
            "NextReg $C4 did not update the shared ULA and line-interrupt enables.");

        machine.Registers.Write(0x22, 0x03);
        Require(machine.Registers.Read(0x22) == 0x03
                && machine.Registers.Read(0xC4) == 0x83,
            "NextReg $22 did not update the aliased ULA and line-interrupt enables.");

        machine.Registers.Write(0x22, 0x04);
        machine.RunForTstates(288);
        machine.StepInstruction();
        Require(machine.UlaTiming.InterruptCount == 1
                && !machine.UlaTiming.InterruptLineActive,
            "A disabled ULA interrupt still asserted the CPU interrupt line.");

        machine.Registers.Write(0xC4, 0x81);
        machine.RunForTstates(70_552);
        while (machine.UlaTiming.InterruptCount < 2)
        {
            machine.StepInstruction();
        }

        Require(machine.UlaTiming.InterruptLineActive,
            "Re-enabling the ULA interrupt did not restore the next frame pulse.");
        machine.Reset();
        Require(machine.Registers.Read(0xC4) == 0x81
                && machine.Registers.Read(0x22) == 0x00,
            "Machine reset did not restore the interrupt-enable register state.");
    }

    private static void VerifyRasterLineInterrupt()
    {
        ZxNextMachine machine = CreateProgram(
            0xFB, // EI
            0x76, // HALT
            0x00);
        machine.Registers.Write(0x23, 0xD0);
        machine.Registers.Write(0x22, 0x06); // ULA disabled, line IRQ enabled, line 208.

        ulong expectedTick = machine.UlaTiming.Profile
            .GetLineInterruptOffsetMasterTicks(0x00D0)
            ?? throw new InvalidOperationException("Line 208 is not present in +3 timing.");
        int instructions = 0;
        while (machine.UlaTiming.LineInterruptCount == 0 && instructions < 100_000)
        {
            machine.StepInstruction();
            instructions++;
        }

        Require(machine.UlaTiming.LineInterruptCount == 1
                && machine.UlaTiming.LastLineInterruptMasterTick == expectedTick
                && machine.Registers.Read(0x23) == 0xD0
                && (machine.Registers.Read(0x22) & 0x87) == 0x86,
            $"The line-208 comparator did not assert at its pinned raster edge " +
            $"(expected {expectedTick}, actual {machine.UlaTiming.LastLineInterruptMasterTick}, " +
            $"NR22=${machine.Registers.Read(0x22):X2}).");

        while (machine.Cpu.PC != 0x0038 && instructions < 100_100)
        {
            machine.StepInstruction();
            instructions++;
        }

        Require(machine.Cpu.PC == 0x0038 && !machine.Cpu.IsHalted,
            "The raster line pulse did not release HALT and enter the conventional interrupt vector.");

        machine.Reset();
        Require(machine.UlaTiming.LineInterruptCount == 0
                && machine.Registers.Read(0x23) == 0
                && (machine.Registers.Read(0x22) & 0x07) == 0,
            "Reset did not clear the line-interrupt comparator and scheduler state.");
    }

    private static void VerifyDivMmcStacklessNmi()
    {
        ZxNextMachine machine = CreateProgram(
            0xED, 0x91, 0xC0, 0x08, // NEXTREG $C0,$08: stackless NMI
            0xED, 0x91, 0x02, 0x04, // NEXTREG $02,$04: DivMMC NMI
            0x00);
        var divMmcRom = new byte[ZxNextPhysicalMemory.PageSize];
        byte[] handler =
        [
            0xED, 0x91, 0xC2, 0x34, // NEXTREG $C2,$34
            0xED, 0x91, 0xC3, 0x12, // NEXTREG $C3,$12
            0xED, 0x45              // RETN
        ];
        handler.CopyTo(divMmcRom, 0x0066);
        machine.MemoryBus.LoadDivMmcRom(divMmcRom);
        machine.Cpu.SP = 0x8000;
        machine.MemoryBus.WriteCpu(0x7FFE, 0xA5);
        machine.MemoryBus.WriteCpu(0x7FFF, 0x5A);

        machine.StepInstruction();
        Require(machine.Registers.Read(0xC0) == 0x08,
            "NextReg $C0 did not enable stackless NMI response.");
        machine.StepInstruction();
        Require(machine.Cpu.PC == 0x0066
                && machine.Cpu.SP == 0x7FFE
                && machine.DivMmc.NmiActive
                && machine.DivMmc.IsMapped
                && machine.Registers.Read(0xC2) == 0x08
                && machine.Registers.Read(0xC3) == 0x00
                && machine.MemoryBus.ReadPhysicalRam(11, 0x1FFE) == 0xA5
                && machine.MemoryBus.ReadPhysicalRam(11, 0x1FFF) == 0x5A,
            "The software DivMMC NMI did not enter $0066 without overwriting the stack.");

        machine.StepInstruction();
        machine.StepInstruction();
        machine.StepInstruction();
        Require(machine.Cpu.PC == 0x1234
                && machine.Cpu.SP == 0x8000
                && !machine.DivMmc.NmiActive
                && !machine.DivMmc.IsMapped
                && machine.Registers.Read(0xC2) == 0x34
                && machine.Registers.Read(0xC3) == 0x12,
            $"Stackless RETN did not use the hardware return registers or release DivMMC " +
            $"(PC=${machine.Cpu.PC:X4}, SP=${machine.Cpu.SP:X4}, " +
            $"mapped={machine.DivMmc.IsMapped}, active={machine.DivMmc.NmiActive}, " +
            $"return=${machine.Registers.Read(0xC3):X2}{machine.Registers.Read(0xC2):X2}).");
        Require((machine.Registers.Read(0x02) & 0x04) != 0,
            "NextReg $02 did not report the software DivMMC NMI cause.");
        machine.Registers.Write(0x02, 0x00);
        Require((machine.Registers.Read(0x02) & 0x0C) == 0,
            "NextReg $02 did not clear its software NMI cause bits.");
    }

    private static void VerifyMultifaceStacklessNmi()
    {
        ZxNextMachine machine = CreateProgram(
            0xED, 0x91, 0xC0, 0x08, // NEXTREG $C0,$08: stackless NMI
            0xED, 0x91, 0x02, 0x08, // NEXTREG $02,$08: Multiface NMI
            0x00);
        var multifaceRom = new byte[ZxNextPhysicalMemory.PageSize];
        byte[] handler =
        [
            0xED, 0x91, 0xC2, 0x78, // NEXTREG $C2,$78
            0xED, 0x91, 0xC3, 0x56, // NEXTREG $C3,$56
            0xED, 0x45              // RETN
        ];
        handler.CopyTo(multifaceRom, 0x0066);
        machine.MemoryBus.LoadMultifaceRom(multifaceRom);
        machine.Cpu.SP = 0x8000;
        machine.MemoryBus.WriteCpu(0x7FFE, 0xC3);
        machine.MemoryBus.WriteCpu(0x7FFF, 0x3C);

        machine.StepInstruction();
        machine.StepInstruction();
        Require(machine.Cpu.PC == 0x0066
                && machine.Cpu.SP == 0x7FFE
                && machine.Multiface.NmiActive
                && !machine.Multiface.IsMapped
                && machine.Registers.Read(0xC2) == 0x08
                && machine.Registers.Read(0xC3) == 0x00,
            "The software Multiface NMI did not reach its vector with stackless state armed.");

        machine.StepInstruction();
        Require(machine.Multiface.IsMapped
                && machine.Registers.Read(0xC2) == 0x78
                && machine.MemoryBus.ReadPhysicalRam(11, 0x1FFE) == 0xC3
                && machine.MemoryBus.ReadPhysicalRam(11, 0x1FFF) == 0x3C,
            "The $0066 opcode fetch did not page the Multiface ROM without touching the stack.");

        Require(machine.Registers.Read(0x82) == 0xFF
                && machine.Registers.Read(0x83) == 0xFF
                && machine.Registers.Read(0x84) == 0xFF
                && machine.Registers.Read(0x85) == 0xFF,
            "Internal port-decode enables did not start in the pinned all-enabled state.");

        machine.Paging.WritePort1Ffd(0x04);
        machine.Paging.WritePort7Ffd(0x1B);
        machine.Paging.WritePortDFfd(0x03);
        machine.Paging.WritePortEff7(0x0C);
        machine.PortBus.WriteUncontended(0x00FE, 0x05);
        Require(machine.PortBus.ReadUncontended(0x1F3F) == 0x04
                && machine.PortBus.ReadUncontended(0x7F3F) == 0x1B
                && machine.PortBus.ReadUncontended(0xDF3F) == 0x03
                && machine.PortBus.ReadUncontended(0xEF3F) == 0x0C
                && machine.PortBus.ReadUncontended(0xFE3F) == 0x05,
            "The MF+3 status port did not expose the saved paging and border latches.");
        machine.MemoryBus.WriteCpu(0x2345, 0xA6);
        machine.StepInstruction();
        machine.StepInstruction();
        Require(machine.Cpu.PC == 0x5678
                && machine.Cpu.SP == 0x8000
                && !machine.Multiface.NmiActive
                && !machine.Multiface.IsMapped
                && (machine.Registers.Read(0x02) & 0x08) != 0,
            "Multiface stackless RETN did not resume at the hardware return address.");
        machine.MemoryBus.SetConfigurationMode(true);
        machine.MemoryBus.SetConfigurationBank(5);
        machine.MemoryBus.SetMmuPage(0, ZxNextMemoryBus.RomPageSentinel);
        machine.MemoryBus.SetMmuPage(1, ZxNextMemoryBus.RomPageSentinel);
        Require(machine.MemoryBus.ReadDirect(0x0066) == multifaceRom[0x0066]
                && machine.MemoryBus.ReadDirect(0x2345) == 0xA6,
            "Multiface ROM/RAM did not persist in pinned system-SRAM pages 10/11.");
        machine.Registers.Write(0x02, 0x00);

        ZxNextMachine arbitration = ZxNextMachineFactory.CreateFoundationMachine();
        arbitration.Registers.Write(0x03, 0x03);
        arbitration.Paging.WritePort7Ffd(0x10);
        arbitration.Paging.WritePort1Ffd(0x04);
        arbitration.MemoryBus.LoadMultifaceRom(new byte[ZxNextPhysicalMemory.PageSize]);
        arbitration.Registers.Write(0x0A, 0x11);
        Require(arbitration.Multiface.BeginNmi(),
            "Multiface/DivMMC arbitration setup could not assert Multiface NMI.");
        _ = arbitration.MemoryBus.FetchOpcode(0x0066);
        Require(arbitration.Multiface.IsMapped,
            "Multiface/DivMMC arbitration setup did not page Multiface at $0066.");
        _ = arbitration.MemoryBus.FetchOpcode(0x0038);
        Require(!arbitration.DivMmc.AutomapHeld,
            "A Multiface opcode fetch incorrectly activated the DivMMC ROM-3 automapper.");
    }

    private static void VerifyUlaKeyboard()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        Require(ZxNextPortBus.IsUlaPort(0x00FE)
                && ZxNextPortBus.IsUlaPort(0xFE00)
                && !ZxNextPortBus.IsUlaPort(0x00FF),
            "The Next ULA port does not use the pinned A0-only decode.");
        Require(machine.PortBus.ReadUncontended(0x00FE) == 0xBF
                && machine.PortBus.ReadUncontended(0xFEFE) == 0xBF
                && machine.PortBus.ReadUncontended(0x00FF) == 0xFF,
            "A released Next keyboard matrix or odd open-bus port returned the wrong value.");

        machine.Keyboard.SetKeyState(SpectrumKey.CapsShift, pressed: true);
        machine.Keyboard.SetKeyState(SpectrumKey.Z, pressed: true);
        machine.Keyboard.SetKeyState(SpectrumKey.A, pressed: true);
        machine.Keyboard.SetKeyState(SpectrumKey.Enter, pressed: true);
        Require(machine.PortBus.ReadUncontended(0xFEFE) == 0xBC,
            "Caps Shift/Z did not pull the expected first keyboard-row columns low.");
        Require(machine.PortBus.ReadUncontended(0xFDFE) == 0xBE,
            "A did not pull the expected second keyboard-row column low.");
        Require(machine.PortBus.ReadUncontended(0xBFFE) == 0xBE,
            "Enter did not pull the expected seventh keyboard-row column low.");
        Require(machine.PortBus.ReadUncontended(0xFCFE) == 0xBC,
            "Selecting multiple keyboard rows did not AND their active-low columns.");
        Require(machine.PortBus.ReadUncontended(0x7FFE) == 0xBF,
            "Keys in unselected rows leaked into another keyboard row.");

        machine.PortBus.WriteUncontended(0x12FE, 0xFD);
        Require(machine.PortBus.UlaOutputLatch == 0x1D
                && machine.PortBus.ReadUncontended(0xFEFE) == 0xFC,
            "The ULA output latch did not mask to five bits or loop EAR output into bit 6.");

        ZxNextMachine guest = CreateProgram(
            0x01, 0xFE, 0xFE, // LD BC,$FEFE
            0xED, 0x78,       // IN A,(C)
            0x3E, 0x1A,       // LD A,$1A
            0xD3, 0xFE);      // OUT ($FE),A
        guest.Keyboard.SetKeyState(SpectrumKey.CapsShift, pressed: true);
        guest.StepInstruction();
        guest.StepInstruction();
        Require(guest.Cpu.A == 0xBE,
            "A guest IN instruction did not observe the Next keyboard matrix.");
        guest.StepInstruction();
        guest.StepInstruction();
        Require(guest.PortBus.UlaOutputLatch == 0x1A,
            "A guest OUT instruction did not update the Next ULA latch.");

        machine.Reset(ZxNextResetKind.Soft);
        Require(machine.PortBus.UlaOutputLatch == 0
                && machine.PortBus.ReadUncontended(0xFEFE) == 0xBC,
            "Soft reset did not clear the ULA latch or incorrectly released physical keys.");
        machine.Keyboard.SetKeyState(SpectrumKey.CapsShift, pressed: false);
        machine.Keyboard.SetKeyState(SpectrumKey.Z, pressed: false);
        machine.Keyboard.SetKeyState(SpectrumKey.A, pressed: false);
        machine.Keyboard.SetKeyState(SpectrumKey.Enter, pressed: false);
        Require(machine.PortBus.ReadUncontended(0x00FE) == 0xBF,
            "Releasing the keyboard matrix did not restore all columns high.");
        machine.Keyboard.SetKeyState(SpectrumKey.Q, pressed: true);
        machine.Keyboard.ReleaseAll();
        Require(machine.PortBus.ReadUncontended(0xFBFE) == 0xBF,
            "Host-wide keyboard release did not restore the Next matrix.");
    }

    private static void VerifyUlaContention()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextUlaTimingProfile profile = machine.UlaTiming.Profile;
        ulong waitSampleTick = checked(
            ((ulong)profile.FirstDisplayLine * (ulong)profile.PixelsPerLine
                + (ulong)(profile.ContentionCounterStartHorizontal + 3))
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        ulong waitCycleStart = waitSampleTick
            - (2UL * ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate);
        ulong gapCycleStart = waitSampleTick
            - (2UL * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        gapCycleStart -= 2UL * ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate;

        Require(profile.IsPlus3
                && profile.ContentionCounterStartHorizontal == 125
                && machine.Contention.IsPhysicalPageContended(10)
                && !machine.Contention.IsPhysicalPageContended(4)
                && machine.Contention.GetMemoryDelayAtMasterTick(waitCycleStart, 10) == 7
                && machine.Contention.GetMemoryDelayAtMasterTick(gapCycleStart, 10) == 0
                && machine.Contention.GetNoMreqDelay(0) == 0
                && machine.MemoryBus.GetMapping(0x4000).IsContended,
            "The +3 ULA phase, MMU classification, wait waveform, no-MREQ rule, or debugger mapping is incorrect.");

        int targetTstates = checked((int)(waitCycleStart
            / ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate));
        machine.Clock.AdvanceCpuTstates(targetTstates);
        machine.Video.AdvanceMasterTicks(waitCycleStart);
        Require(machine.MasterTicks == waitCycleStart,
            "The contention fixture did not reach the selected clk28 edge exactly.");
        ulong cpuBefore = machine.Cpu.Cyc;
        ulong masterBefore = machine.MasterTicks;
        _ = machine.MemoryBus.Read(0x4000);
        Require(machine.Cpu.Cyc - cpuBefore == 7
                && machine.MasterTicks - masterBefore == 56
                && machine.Video.FrameMasterTick == machine.UlaTiming.FrameMasterTick,
            "A contended MMU read did not hold the CPU for seven source-derived T-states while advancing the raster.");

        ZxNextMachine portMachine = ZxNextMachineFactory.CreateFoundationMachine();
        portMachine.Clock.AdvanceCpuTstates(targetTstates);
        portMachine.Video.AdvanceMasterTicks(waitCycleStart);
        cpuBefore = portMachine.Cpu.Cyc;
        portMachine.PortBus.ApplyIoContentionBeforeCycle(0x00FE, phase: 1);
        Require(portMachine.Cpu.Cyc == cpuBefore,
            "The +3 personality applied the I/O wait disabled in zxula.vhd.");

        ZxNextMachine personalities = ZxNextMachineFactory.CreateFoundationMachine();
        personalities.Registers.Write(0x03, 0x91); // 48K timing.
        personalities.UlaTiming.SynchronizeProfile();
        Require(personalities.Contention.IsPhysicalPageContended(10)
                && !personalities.Contention.IsPhysicalPageContended(14)
                && !personalities.Contention.IsPortContended(0x7FFD),
            "48K timing did not restrict contention to physical bank 5.");
        personalities.Registers.Write(0x03, 0xA1); // 128K timing.
        personalities.UlaTiming.SynchronizeProfile();
        Require(personalities.Contention.IsPhysicalPageContended(2)
                && !personalities.Contention.IsPhysicalPageContended(4)
                && personalities.Contention.IsPortContended(0x7FFD),
            "128K timing did not contend odd physical 16 KiB banks.");

        ZxNextMachine disabled = ZxNextMachineFactory.CreateFoundationMachine();
        disabled.Registers.Write(0x08, 0x50);
        Require(disabled.Registers.ContentionDisabled
                && disabled.Contention.GetMemoryDelayAtMasterTick(waitCycleStart, 10) == 0,
            "NextReg $08 bit 6 did not disable legacy contention.");
        disabled.Registers.Write(0x08, 0x10);
        disabled.Registers.Write(0x07, 0x01);
        Require(!disabled.Contention.Enabled,
            "Legacy contention remained enabled above 3.5 MHz.");
        disabled.Registers.Write(0x07, 0x00);
        disabled.Registers.Write(0x03, 0xC4);
        disabled.UlaTiming.SynchronizeProfile();
        Require(disabled.UlaTiming.Profile.IsPentagon && !disabled.Contention.Enabled,
            "Pentagon timing incorrectly enabled synthetic ULA contention.");
    }

    private static void VerifyUlaFloatingBus()
    {
        ZxNextMachine spectrum48 = ZxNextMachineFactory.CreateFoundationMachine();
        spectrum48.Registers.Write(0x03, 0x91);
        spectrum48.UlaTiming.SynchronizeProfile();
        ZxNextUlaTimingProfile profile = spectrum48.UlaTiming.Profile;
        spectrum48.MemoryBus.WritePhysicalRam(10, 0x0000, 0xA4);
        spectrum48.MemoryBus.WritePhysicalRam(10, 0x0001, 0xC2);
        spectrum48.MemoryBus.WritePhysicalRam(10, 0x1800, 0x34);
        spectrum48.MemoryBus.WritePhysicalRam(10, 0x1801, 0x7E);
        spectrum48.Video.Reset();

        ulong cursor = 0;
        ulong FetchTick(int horizontal) => checked(
            ((ulong)profile.FirstDisplayLine * (ulong)profile.PixelsPerLine
                + (ulong)horizontal)
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel
            + ZxNextUlaTimingProfile.FallingEdgeMasterTick);
        void AdvanceTo(ulong target)
        {
            spectrum48.Video.AdvanceMasterTicks(target - cursor);
            cursor = target;
        }

        AdvanceTo(FetchTick(
            profile.DisplayStartHorizontal
                - ZxNextUlaTimingProfile.EvenPixelFetchLeadPixels));
        Require(spectrum48.FloatingBus.UlaDriving
                && spectrum48.PortBus.ReadUncontended(0x12FF) == 0xA4
                && spectrum48.PortBus.ReadUncontended(0x12FD) == 0xFF,
            "48K timing did not expose the first bitmap fetch only through low-byte $FF.");

        AdvanceTo(FetchTick(
            profile.DisplayStartHorizontal
                - ZxNextUlaTimingProfile.EvenAttributeFetchLeadPixels));
        Require(spectrum48.PortBus.ReadUncontended(0x12FF) == 0x34,
            "The first attribute fetch was not exposed on the 48K floating bus.");
        AdvanceTo(FetchTick(
            profile.DisplayStartHorizontal + 8
                - ZxNextUlaTimingProfile.OddPixelFetchLeadPixels));
        Require(spectrum48.PortBus.ReadUncontended(0x12FF) == 0xC2,
            "The second bitmap fetch was not exposed on the 48K floating bus.");
        AdvanceTo(FetchTick(
            profile.DisplayStartHorizontal + 8
                - ZxNextUlaTimingProfile.OddAttributeFetchLeadPixels));
        Require(spectrum48.PortBus.ReadUncontended(0x12FF) == 0x7E,
            "The second attribute fetch was not exposed on the 48K floating bus.");
        AdvanceTo(FetchTick(profile.DisplayStartHorizontal + 6));
        Require(!spectrum48.FloatingBus.UlaDriving
                && spectrum48.PortBus.ReadUncontended(0x12FF) == 0xFF,
            "The 48K ULA fetch latch did not release at hc(3:0)=$1.");

        ZxNextMachine shadow = ZxNextMachineFactory.CreateFoundationMachine();
        shadow.Registers.Write(0x03, 0x91);
        shadow.UlaTiming.SynchronizeProfile();
        profile = shadow.UlaTiming.Profile;
        shadow.MemoryBus.WritePhysicalRam(10, 0x0000, 0x11);
        shadow.MemoryBus.WritePhysicalRam(14, 0x0000, 0x66);
        shadow.Paging.WritePort7Ffd(0x08);
        shadow.Video.Reset();
        shadow.Video.AdvanceMasterTicks(checked(
            ((ulong)profile.FirstDisplayLine * (ulong)profile.PixelsPerLine
                + (ulong)(profile.DisplayStartHorizontal
                    - ZxNextUlaTimingProfile.EvenPixelFetchLeadPixels))
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel
            + ZxNextUlaTimingProfile.FallingEdgeMasterTick));
        Require(shadow.PortBus.ReadUncontended(0x00FF) == 0x66,
            "The floating bus ignored $7FFD shadow-screen selection.");

        ZxNextMachine plus3 = ZxNextMachineFactory.CreateFoundationMachine();
        profile = plus3.UlaTiming.Profile;
        plus3.Video.Reset();
        plus3.MemoryBus.WritePhysicalRam(10, 0x0100, 0x3C);
        _ = plus3.MemoryBus.Read(0x4100);
        Require(plus3.FloatingBus.Plus3CpuData == 0x3C
                && plus3.PortBus.ReadUncontended(0x00FD) == 0x3C
                && plus3.PortBus.ReadUncontended(0x10FD) == 0xFF
                && plus3.PortBus.ReadUncontended(0x12FF) == 0xFF,
            "The +3 floating port family or last-contended-read fallback is incorrect.");

        plus3.Registers.Write(0x08, 0x50);
        plus3.MemoryBus.WriteCpu(0x4100, 0x5D);
        Require(!plus3.Contention.Enabled
                && plus3.FloatingBus.Plus3CpuData == 0x5D
                && plus3.PortBus.ReadUncontended(0x0BFD) == 0x5D,
            "Disabling contention waits incorrectly disabled the +3 CPU-data floating-bus latch.");
        plus3.Registers.Write(0x08, 0x10);
        plus3.Registers.Write(0x07, 0x01);
        plus3.MemoryBus.WriteCpu(0x4100, 0x6C);
        Require(plus3.FloatingBus.Plus3CpuData == 0x6C
                && plus3.PortBus.ReadUncontended(0x0BFD) == 0x6C,
            "Turbo incorrectly disabled the +3 CPU-data floating-bus latch.");
        plus3.MemoryBus.WritePhysicalRam(10, 0x0000, 0xA4);
        ulong plus3FetchTick = checked(
            ((ulong)profile.FirstDisplayLine * (ulong)profile.PixelsPerLine
                + (ulong)(profile.DisplayStartHorizontal
                    - ZxNextUlaTimingProfile.EvenPixelFetchLeadPixels))
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel
            + ZxNextUlaTimingProfile.FallingEdgeMasterTick);
        plus3.Video.AdvanceMasterTicks(plus3FetchTick);
        Require(plus3.FloatingBus.UlaDriving
                && plus3.PortBus.ReadUncontended(0x00FD) == 0xA5,
            "The +3 ULA fetch did not override CPU data and force floating-bus bit 0 high.");
        ulong plus3ReleaseTick = checked(
            ((ulong)profile.FirstDisplayLine * (ulong)profile.PixelsPerLine
                + (ulong)(profile.DisplayStartHorizontal + 6))
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel
            + ZxNextUlaTimingProfile.FallingEdgeMasterTick);
        plus3.Video.AdvanceMasterTicks(plus3ReleaseTick - plus3FetchTick);
        Require(!plus3.FloatingBus.UlaDriving
                && plus3.PortBus.ReadUncontended(0x00FD) == 0x6C,
            "The +3 floating bus did not return to the last CPU data after the ULA window.");
        plus3.Paging.WritePort7Ffd(0x20);
        Require(plus3.PortBus.ReadUncontended(0x00FD) == 0xFF,
            "The +3 floating port remained visible after the $7FFD paging lock.");
        plus3.Reset(ZxNextResetKind.Soft);
        Require(plus3.FloatingBus.Plus3CpuData == 0xFF
                && !plus3.FloatingBus.UlaDriving,
            "Machine reset did not clear both floating-bus latches.");

        ZxNextMachine pentagon = ZxNextMachineFactory.CreateFoundationMachine();
        pentagon.Registers.Write(0x03, 0xC4);
        pentagon.UlaTiming.SynchronizeProfile();
        Require(!pentagon.FloatingBus.HandlesPort(0x00FF)
                && pentagon.PortBus.ReadUncontended(0x00FF) == 0xFF,
            "Pentagon timing incorrectly exposed the synthetic legacy floating bus.");
    }

    private static void VerifyJoystickAndCursorInput()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        Require(machine.Joystick.HostType == SpectrumJoystickType.None
                && machine.Joystick.GetMode(ZxNextJoystickConnector.Joystick1)
                    == ZxNextJoystickMode.Kempston1
                && machine.Joystick.GetMode(ZxNextJoystickConnector.Joystick2)
                    == ZxNextJoystickMode.Sinclair2
                && machine.PortBus.ReadUncontended(0x001F) == 0
                && machine.PortBus.ReadUncontended(0x0037) == 0xFF,
            "NextReg $05 reset routing did not select Kempston 1 and Sinclair 2.");
        Require(ZxNextPortBus.IsKempstonAliasPort(0x00DF)
                && !ZxNextPortBus.IsKempstonAliasPort(0x0037),
            "The pinned Kempston-1 alias decode is incorrect.");

        machine.SetHostJoystickType(SpectrumJoystickType.Kempston);
        machine.Joystick.SetButtonState(SpectrumJoystickButton.Right, pressed: true);
        machine.Joystick.SetButtonState(SpectrumJoystickButton.Up, pressed: true);
        machine.Joystick.SetButtonState(SpectrumJoystickButton.Fire, pressed: true);
        Require(machine.PortBus.ReadUncontended(0x001F) == 0x19
                && machine.PortBus.ReadUncontended(0xAA1F) == 0x19
                && machine.PortBus.ReadUncontended(0x00DF) == 0x19
                && machine.PortBus.ReadUncontended(0x0037) == 0xFF,
            "Host Kempston input did not reach the Next's $1F/$DF port decode.");
        machine.Joystick.ReleaseAll();
        Require(machine.PortBus.ReadUncontended(0x001F) == 0,
            "Host-wide joystick release did not clear the Kempston state.");

        // Connector 2 can independently route its standard five buttons to
        // Kempston 2 while connector 1 remains on Kempston 1.
        machine.Registers.Write(0x05, 0x43);
        machine.Joystick.SetConnectorButtonState(
            ZxNextJoystickConnector.Joystick2,
            SpectrumJoystickButton.Left,
            pressed: true);
        machine.Joystick.SetConnectorButtonState(
            ZxNextJoystickConnector.Joystick2,
            SpectrumJoystickButton.Fire,
            pressed: true);
        Require(machine.PortBus.ReadUncontended(0x001F) == 0
                && machine.PortBus.ReadUncontended(0x0037) == 0x12,
            "Connector 2 did not route to the Kempston-2 $37 port.");
        machine.Joystick.ReleaseAll();

        // Multiple connectors routed to the same interface are electrically ORed.
        machine.Registers.Write(0x05, 0x51);
        machine.Joystick.SetConnectorButtonState(
            ZxNextJoystickConnector.Joystick1,
            SpectrumJoystickButton.Right,
            pressed: true);
        machine.Joystick.SetConnectorButtonState(
            ZxNextJoystickConnector.Joystick2,
            SpectrumJoystickButton.Left,
            pressed: true);
        Require(machine.PortBus.ReadUncontended(0x001F) == 0x03,
            "Two connectors routed to Kempston 1 were not combined.");
        machine.Joystick.ReleaseAll();

        machine.SetHostJoystickType(SpectrumJoystickType.Sinclair1);
        Require(machine.Joystick.GetMode(ZxNextJoystickConnector.Joystick1)
                == ZxNextJoystickMode.Sinclair1,
            "The desktop selector did not configure connector 1 through NextReg $05.");
        machine.Joystick.SetButtonState(SpectrumJoystickButton.Left, pressed: true);
        machine.Joystick.SetButtonState(SpectrumJoystickButton.Fire, pressed: true);
        Require(machine.PortBus.ReadUncontended(0xEFFE) == 0xAE,
            "Sinclair 1 host input did not overlay keys 6 and 0 on the matrix.");
        machine.SetHostJoystickType(SpectrumJoystickType.None);
        Require(machine.PortBus.ReadUncontended(0xEFFE) == 0xBF,
            "Disabling a keyboard-mapped joystick left stale matrix keys pressed.");

        // Both connectors may press the same emulated key. Rebuilding the complete
        // overlay ensures releasing either connector cannot release the other one.
        machine.Registers.Write(0x05, 0xF1);
        machine.Joystick.SetConnectorButtonState(
            ZxNextJoystickConnector.Joystick1,
            SpectrumJoystickButton.Left,
            pressed: true);
        machine.Joystick.SetConnectorButtonState(
            ZxNextJoystickConnector.Joystick2,
            SpectrumJoystickButton.Left,
            pressed: true);
        machine.Joystick.SetConnectorButtonState(
            ZxNextJoystickConnector.Joystick1,
            SpectrumJoystickButton.Left,
            pressed: false);
        Require(machine.PortBus.ReadUncontended(0xEFFE) == 0xAF,
            "Releasing one connector incorrectly released a shared matrix key.");
        machine.Joystick.ReleaseAll();

        machine.Reset(ZxNextResetKind.Soft);
        Require(machine.Registers.Read(0x05) == 0xF1
                && machine.Joystick.GetMode(ZxNextJoystickConnector.Joystick2)
                    == ZxNextJoystickMode.Sinclair1,
            "Soft reset did not preserve the two Next joystick mode fields.");
        machine.Reset(ZxNextResetKind.Hard);
        Require(machine.Registers.Read(0x05) == 0x41
                && machine.Joystick.GetMode(ZxNextJoystickConnector.Joystick1)
                    == ZxNextJoystickMode.Kempston1
                && machine.Joystick.GetMode(ZxNextJoystickConnector.Joystick2)
                    == ZxNextJoystickMode.Sinclair2,
            "Hard reset did not restore the pinned Next joystick modes.");

        // With no joystick selected, desktop arrow keys map to these actual Spectrum
        // cursor chords rather than to the Cursor/Protek joystick's bare number keys.
        machine.Keyboard.SetKeyState(SpectrumKey.CapsShift, pressed: true);
        machine.Keyboard.SetKeyState(SpectrumKey.D5, pressed: true);
        Require(machine.PortBus.ReadUncontended(0xFEFE) == 0xBE
                && machine.PortBus.ReadUncontended(0xF7FE) == 0xAF,
            "The no-joystick Left cursor chord was not visible as Caps Shift+5.");
        machine.Keyboard.ReleaseAll();
        Require(machine.PortBus.ReadUncontended(0xFEFE) == 0xBF
                && machine.PortBus.ReadUncontended(0xF7FE) == 0xBF,
            "Releasing a host cursor chord left a Spectrum key pressed.");
    }

    private static void VerifyLegacyUlaFramebuffer()
    {
        const int black = unchecked((int)0xFF000000);
        const int darkBlue = unchecked((int)0xFF0000B6);
        const int darkRed = unchecked((int)0xFFB60000);
        const int darkMagenta = unchecked((int)0xFFB600B6);
        const int brightBlue = unchecked((int)0xFF0000FF);
        const int brightRed = unchecked((int)0xFFFF0000);
        const int brightGreen = unchecked((int)0xFF00FF00);

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextVideoRenderer video = machine.Video;
        Require(ZxNextVideoRenderer.FrameWidth == 640
                && ZxNextVideoRenderer.FrameHeight == 256
                && ZxNextVideoRenderer.DisplayHeightScale == 2
                && video.FrameBuffer.Length == 640 * 256
                && video.FrameBuffer[0] == black,
            "The stable Next output surface did not reset to a 640x256 black frame.");

        machine.PortBus.WriteUncontended(0x00FE, 0x02);
        machine.MemoryBus.WritePhysicalRam(10, 0x0000, 0x80);
        machine.MemoryBus.WritePhysicalRam(10, 0x0100, 0x40);
        machine.MemoryBus.WritePhysicalRam(10, 0x1800, 0x4A);
        video.RenderFrame(0);
        int[] frame = video.FrameBuffer;
        int active = (ZxNextVideoRenderer.UlaTop * ZxNextVideoRenderer.FrameWidth)
            + ZxNextVideoRenderer.UlaLeft;
        int secondUlaLine = ((ZxNextVideoRenderer.UlaTop + 1)
                * ZxNextVideoRenderer.FrameWidth)
            + ZxNextVideoRenderer.UlaLeft;
        Require(video.BorderColorIndex == 2
                && frame[0] == darkRed
                && frame[active] == brightRed
                && frame[active + 1] == brightRed
                && frame[active + 2] == brightBlue
                && frame[secondUlaLine] == brightBlue
                && frame[secondUlaLine + 2] == brightRed,
            "Legacy bitmap/attribute decoding, Spectrum row interleave, border, or 2x horizontal scaling was incorrect.");

        machine.MemoryBus.WritePhysicalRam(14, 0x0000, 0x00);
        machine.MemoryBus.WritePhysicalRam(14, 0x1800, 0x60);
        machine.Paging.WritePort7Ffd(0x08);
        video.RenderFrame(1);
        Require(video.SelectedScreenBank == 7 && frame[active] == brightGreen,
            "7FFD shadow-screen selection did not render physical bank 7.");
        machine.Paging.WritePort7Ffd(0x00);
        video.RenderFrame(2);
        Require(video.SelectedScreenBank == 5 && frame[active] == brightRed,
            "Clearing 7FFD shadow-screen selection did not restore physical bank 5.");

        machine.MemoryBus.WritePhysicalRam(10, 0x0000, 0x80);
        machine.MemoryBus.WritePhysicalRam(10, 0x1800, 0x8A);
        video.RenderFrame(0);
        Require(frame[active] == darkRed && frame[active + 2] == darkBlue,
            "The first ULA flash phase did not preserve ink and paper.");
        video.RenderFrame(16);
        Require(frame[active] == darkBlue && frame[active + 2] == darkRed,
            "The second ULA flash phase did not exchange ink and paper.");

        // Palette writes use the pinned RRRGGGBB(+B) protocol and affect the
        // completed-frame ULA path through the selected active palette.
        machine.Registers.Write(0x43, 0x42); // Write/select second ULA palette.
        machine.Registers.Write(0x40, 0x02);
        machine.Registers.Write(0x41, 0x1C); // Green, auto-increment to index 3.
        Require(machine.Registers.Read(0x40) == 0x03,
            "NextReg $41 did not auto-increment the palette index.");
        machine.MemoryBus.WritePhysicalRam(10, 0x1800, 0x02);
        video.RenderFrame(17);
        Require(frame[active] == brightGreen,
            "The active second ULA palette did not replace standard ink 2.");

        machine.Registers.Write(0x43, 0x01); // First ULA palette, ULANext enabled.
        machine.Registers.Write(0x42, 0x07);
        machine.Registers.Write(0x40, 0x02);
        machine.Registers.Write(0x44, 0xE0);
        machine.Registers.Write(0x44, 0x00); // Full red in 9-bit format.
        machine.Registers.Write(0x40, 0x94); // $80 | ($A2 >> 3).
        machine.Registers.Write(0x44, 0x03);
        machine.Registers.Write(0x44, 0x01); // Full blue in 9-bit format.
        machine.MemoryBus.WritePhysicalRam(10, 0x0000, 0x80);
        machine.MemoryBus.WritePhysicalRam(10, 0x1800, 0xA2);
        video.RenderFrame(18);
        Require(frame[active] == brightRed && frame[active + 2] == brightBlue,
            "ULANext ink/paper attribute partitioning did not select the programmed colours.");

        machine.Registers.Write(0x42, 0xFF);
        machine.Registers.Write(0x4A, 0x1C);
        video.RenderFrame(19);
        Require(frame[0] == brightGreen && frame[active + 2] == brightGreen,
            "Full-ink ULANext mode did not use the fallback colour for border and paper.");
        machine.Registers.Write(0x68, 0x80);
        video.RenderFrame(20);
        Require(frame[active] == brightGreen,
            "Disabling ULA output did not expose the fallback colour.");

        var copiedFrame = new int[frame.Length];
        video.CopyFrame(copiedFrame);
        Require(copiedFrame.AsSpan().SequenceEqual(frame),
            "CopyFrame did not return the completed Next framebuffer.");

        machine.Reset(ZxNextResetKind.Soft);
        machine.Registers.Write(0x40, 0x02);
        Require(machine.Registers.Read(0x41) == 0xE0
                && machine.Registers.Read(0x44) == 0
                && machine.Registers.Read(0x42) == 0x07
                && machine.Registers.Read(0x43) == 0
                && machine.Registers.Read(0x4A) == 0xE3
                && machine.Registers.Read(0x68) == 0,
            "Soft reset did not preserve palette RAM while resetting palette controls.");

        ZxNextMachine timed = ZxNextMachineFactory.CreateFoundationMachine();
        timed.PortBus.WriteUncontended(0x00FE, 0x03);
        timed.RunForTstates(70_908);
        Require(timed.UlaTiming.FrameNumber == 1
                && timed.Video.CompletedFrameNumber == 1
                && timed.Video.FrameBuffer[0] == darkMagenta,
            "The legacy framebuffer was not accepted exactly at the completed-frame boundary.");
        timed.Reset(ZxNextResetKind.Soft);
        Require(timed.Video.CompletedFrameNumber == 0
                && timed.Video.BorderColorIndex == 0
                && timed.Video.FrameBuffer[0] == black,
            "Soft reset did not reset the Next video frame and border state.");
    }

    private static void VerifyTimexAndLoresVideoModes()
    {
        const int black = unchecked((int)0xFF000000);
        const int darkBlue = unchecked((int)0xFF0000B6);
        const int darkRed = unchecked((int)0xFFB60000);
        const int darkGreen = unchecked((int)0xFF00B600);
        const int brightRed = unchecked((int)0xFFFF0000);
        const int brightCyan = unchecked((int)0xFF00FFFF);
        int active = (ZxNextVideoRenderer.UlaTop * ZxNextVideoRenderer.FrameWidth)
            + ZxNextVideoRenderer.UlaLeft;

        ZxNextMachine ulaScroll = ZxNextMachineFactory.CreateFoundationMachine();
        ulaScroll.MemoryBus.WritePhysicalRam(10, 0x0000, 0x40);
        ulaScroll.MemoryBus.WritePhysicalRam(10, 0x0001, 0x80);
        ulaScroll.MemoryBus.WritePhysicalRam(10, 0x1800, 0x02);
        ulaScroll.MemoryBus.WritePhysicalRam(10, 0x1801, 0x02);
        ulaScroll.Registers.Write(0x26, 0x01);
        ulaScroll.Video.RenderFrame(0);
        Require(ulaScroll.Video.FrameBuffer[active] == darkRed,
            "NR $26 did not shift standard ULA pixels within a bitmap byte.");

        ulaScroll.MemoryBus.WritePhysicalRam(10, 0x0000, 0x80);
        ulaScroll.Registers.Write(0x26, 0x00);
        ulaScroll.Registers.Write(0x68, 0x04);
        ulaScroll.Video.RenderFrame(0);
        Require(ulaScroll.Video.FrameBuffer[active] == darkRed
                && ulaScroll.Video.FrameBuffer[active + 1] == black,
            "NR $68 bit 2 did not apply the ULA 14 MHz half-pixel scroll.");

        ulaScroll.Registers.Write(0x68, 0x00);
        ulaScroll.Registers.Write(0x26, 0x08);
        ulaScroll.Video.RenderFrame(0);
        Require(ulaScroll.Video.FrameBuffer[active] == darkRed
                && ulaScroll.Video.FrameBuffer[active + 1] == darkRed,
            "NR $26 did not advance and wrap the ULA bitmap byte column.");

        ulaScroll.MemoryBus.WritePhysicalRam(10, 0x0000, 0x80);
        ulaScroll.MemoryBus.WritePhysicalRam(10, 0x1800, 0x02);
        ulaScroll.Registers.Write(0x26, 0x00);
        ulaScroll.Registers.Write(0x27, 0xC0);
        ulaScroll.Video.RenderFrame(0);
        Require(ulaScroll.Video.FrameBuffer[active] == darkRed,
            "NR $27 did not apply the FPGA's 192/256-line ULA vertical wrap mux.");

        ZxNextMachine timex = ZxNextMachineFactory.CreateFoundationMachine();
        timex.PortBus.WriteUncontended(0x00FF, 0x56); // Bright red ink, hires, IRQ disabled.
        Require(timex.PortBus.ReadUncontended(0x00FF) == 0xFF
                && !timex.Registers.UlaInterruptEnabled,
            "Port $FF was readable before NR $08 enabled it, or bit 6 did not disable the ULA interrupt.");
        timex.Registers.Write(0x08, 0x14);
        Require(timex.PortBus.ReadUncontended(0x00FF) == 0x56,
            "NR $08 bit 2 did not expose the Timex port latch.");
        timex.Registers.Write(0x69, 0x16);
        Require(timex.PortBus.ReadUncontended(0x00FF) == 0x56
                && timex.Registers.Read(0x69) == 0x16,
            "NR $69 did not alias only the low six Timex port bits.");

        timex.MemoryBus.WritePhysicalRam(10, 0x0000, 0xA5);
        timex.MemoryBus.WritePhysicalRam(11, 0x0000, 0x3C);
        timex.MemoryBus.WritePhysicalRam(10, 0x0001, 0xC3);
        timex.MemoryBus.WritePhysicalRam(11, 0x0001, 0x5A);
        timex.Video.Reset();
        timex.Video.AdvanceMasterTicks(timex.UlaTiming.Profile.MasterTicksPerFrame);
        int[] timexFrame = timex.Video.FrameBuffer;
        Require(timexFrame[active] == brightRed
                && timexFrame[active + 1] == brightCyan
                && timexFrame[active + 2] == brightRed
                && timexFrame[active + 3] == brightCyan
                && timexFrame[active + 7] == brightRed
                && timexFrame[active + 8] == brightCyan
                && timexFrame[active + 9] == brightCyan
                && timexFrame[active + 10] == brightRed
                && timexFrame[active + 15] == brightCyan
                && timexFrame[active + 16] == brightRed
                && timexFrame[active + 17] == brightRed
                && timexFrame[active + 24] == brightCyan
                && timexFrame[active + 25] == brightRed,
            "Timex 512x192 mode did not emit each $4000/$6000 bitmap byte as eight consecutive 14 MHz pixels.");

        timex.Registers.Write(0x26, 0x04);
        timex.Video.Reset();
        timex.Video.AdvanceMasterTicks(timex.UlaTiming.Profile.MasterTicksPerFrame);
        timexFrame = timex.Video.FrameBuffer;
        Require(timexFrame[active] == brightCyan
                && timexFrame[active + 2] == brightRed,
            "NR $26 did not scroll Timex output across its eight-pixel display-file boundary.");

        timex.Registers.Write(0x26, 0x00);
        timex.Registers.Write(0x68, 0x04);
        timex.Video.RenderFrame(0);
        Require(timex.Video.FrameBuffer[active] == brightCyan
                && timex.Video.FrameBuffer[active + 1] == brightRed,
            "NR $68 bit 2 did not shift Timex output by one 14 MHz pixel.");
        timex.Registers.Write(0x68, 0x00);

        timex.Paging.WritePort7Ffd(0x08);
        timex.MemoryBus.WritePhysicalRam(14, 0x0000, 0x80);
        timex.MemoryBus.WritePhysicalRam(14, 0x1800, 0x02);
        timex.Video.RenderFrame(1);
        Require(timex.Video.FrameBuffer[active] == darkRed
                && timex.Video.FrameBuffer[active + 1] == darkRed,
            "Selecting shadow bank 7 did not force the Timex display back to standard ULA mode.");

        ZxNextMachine lores = ZxNextMachineFactory.CreateFoundationMachine();
        lores.MemoryBus.WritePhysicalRam(10, 0x0000, 0x02);
        lores.MemoryBus.WritePhysicalRam(10, 0x0001, 0x04);
        lores.MemoryBus.WritePhysicalRam(11, 0x0000, 0x01);
        lores.Registers.Write(0x15, 0x80);
        lores.Video.Reset();
        lores.Video.AdvanceMasterTicks(lores.UlaTiming.Profile.MasterTicksPerFrame);
        int[] loresFrame = lores.Video.FrameBuffer;
        int lowerHalf = ((ZxNextVideoRenderer.UlaTop + 96)
                * ZxNextVideoRenderer.FrameWidth)
            + ZxNextVideoRenderer.UlaLeft;
        Require(loresFrame[active] == darkRed
                && loresFrame[active + 3] == darkRed
                && loresFrame[active + 4] == darkGreen
                && loresFrame[lowerHalf] == darkBlue,
            "LoRes 128x96 addressing, four-output-pixel expansion, or the bank-5 6 KiB gap was incorrect.");

        lores.Registers.Write(0x32, 0x02);
        lores.Video.RenderFrame(1);
        Require(lores.Video.FrameBuffer[active] == darkGreen,
            "NR $32 did not scroll LoRes at its documented half-pixel resolution.");

        ZxNextMachine radastan = ZxNextMachineFactory.CreateFoundationMachine();
        radastan.MemoryBus.WritePhysicalRam(10, 0x0000, 0x24);
        radastan.Registers.Write(0x15, 0x80);
        radastan.Registers.Write(0x6A, 0x20);
        radastan.Video.RenderFrame(0);
        Require(radastan.Video.FrameBuffer[active] == darkRed
                && radastan.Video.FrameBuffer[active + 4] == darkGreen,
            "Radastan 4-bpp LoRes did not select the high and low source nibbles.");

        lores.Registers.Write(0x1C, 0x04);
        lores.Registers.Write(0x1A, 0x10);
        lores.Registers.Write(0x1A, 0x20);
        lores.Registers.Write(0x1A, 0x30);
        lores.Registers.Write(0x1A, 0x40);
        Require(lores.Registers.Read(0x1C) == 0
                && lores.Registers.UlaClipX1 == 0x10
                && lores.Registers.UlaClipY2 == 0x40,
            "The shared ULA/LoRes clip window or clip-index control did not advance and wrap.");

        lores.Registers.Write(0x26, 0x12);
        lores.Registers.Write(0x27, 0x34);
        lores.Reset(ZxNextResetKind.Soft);
        Require(lores.Registers.Read(0x15) == 0
                && lores.Registers.Read(0x1A) == 0
                && lores.Registers.Read(0x26) == 0
                && lores.Registers.Read(0x27) == 0
                && lores.Registers.Read(0x32) == 0
                && lores.Registers.Read(0x33) == 0
                && lores.Registers.Read(0x69) == 0
                && lores.Registers.Read(0x6A) == 0,
            "Soft reset did not restore Timex/LoRes video registers and clipping defaults.");
    }

    private static void VerifyLayer2RegistersAndAperture()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        Require(machine.Registers.Read(0x12) == 8
                && machine.Registers.Read(0x13) == 11
                && machine.Registers.Read(0x14) == 0xE3
                && machine.Registers.Read(0x15) == 0
                && machine.Registers.Read(0x16) == 0
                && machine.Registers.Read(0x17) == 0
                && machine.Registers.Read(0x18) == 0
                && machine.Registers.Read(0x1C) == 0
                && machine.Registers.Read(0x70) == 0
                && machine.Registers.Read(0x71) == 0
                && machine.PortBus.ReadUncontended(ZxNextLayer2Device.ControlPort) == 0,
            "Layer 2 registers or port $123B did not expose their soft-reset values.");

        machine.Registers.Write(0x18, 3);
        machine.Registers.Write(0x18, 9);
        machine.Registers.Write(0x18, 5);
        machine.Registers.Write(0x18, 7);
        Require(machine.Registers.Read(0x1C) == 0 && machine.Registers.Read(0x18) == 3,
            "Layer 2 clip writes did not wrap the component index.");
        machine.Registers.Write(0x18, 11);
        Require(machine.Registers.Read(0x1C) == 1 && machine.Registers.Read(0x18) == 9,
            "Reading NextReg $18 advanced or selected the wrong clip component.");
        machine.Registers.Write(0x1C, 1);
        Require(machine.Registers.Read(0x1C) == 0 && machine.Registers.Read(0x18) == 11,
            "NextReg $1C did not reset the Layer 2 clip index.");

        machine.Registers.Write(0x03, 0x03); // Leave boot/config overlays.
        machine.Registers.Write(0x12, 8);
        machine.Registers.Write(0x50, 2);
        machine.MemoryBus.WritePhysicalRam(2, 0x0123, 0x22);
        machine.MemoryBus.WritePhysicalRam(16, 0x0123, 0x81);
        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x05);
        Require(machine.PortBus.ReadUncontended(ZxNextLayer2Device.ControlPort) == 0x05
                && machine.MemoryBus.Read(0x0123) == 0x81,
            "The first Layer 2 16K aperture did not overlay the MMU for reads.");
        machine.MemoryBus.WriteCpu(0x0123, 0x82);
        Require(machine.MemoryBus.ReadPhysicalRam(16, 0x0123) == 0x82
                && machine.MemoryBus.GetMapping(0x0123).BankIndex == 16,
            "The Layer 2 read/write aperture did not resolve active bank 8.");

        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x12);
        machine.MemoryBus.WriteCpu(0x0123, 0xA2);
        Require(machine.Layer2.MapOffset == 2
                && machine.PortBus.ReadUncontended(ZxNextLayer2Device.ControlPort) == 0x05
                && machine.MemoryBus.ReadPhysicalRam(20, 0x0123) == 0xA2,
            "The alternate $123B write format did not apply its 16K bank offset independently.");

        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x10);
        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0xC5);
        machine.MemoryBus.WriteCpu(0x0123, 0xB0);
        machine.MemoryBus.WriteCpu(0x4123, 0xB1);
        machine.MemoryBus.WriteCpu(0x8123, 0xB2);
        Require(machine.MemoryBus.ReadPhysicalRam(16, 0x0123) == 0xB0
                && machine.MemoryBus.ReadPhysicalRam(18, 0x0123) == 0xB1
                && machine.MemoryBus.ReadPhysicalRam(20, 0x0123) == 0xB2,
            "The 48K Layer 2 aperture did not map three consecutive 16K banks.");

        machine.Registers.Write(0x13, 20);
        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x0D);
        machine.MemoryBus.WriteCpu(0x0123, 0xC0);
        Require(machine.MemoryBus.ReadPhysicalRam(40, 0x0123) == 0xC0,
            "Port $123B bit 3 did not select the shadow Layer 2 bank for the CPU aperture.");

        machine.Registers.Write(0x12, 8);
        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x10);
        machine.MemoryBus.WritePhysicalRam(16, 0x0123, 0xD0);
        machine.MemoryBus.WritePhysicalRam(2, 0x0123, 0xD1);
        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x04);
        machine.MemoryBus.WriteCpu(0x0123, 0xD2);
        Require(machine.MemoryBus.Read(0x0123) == 0xD0
                && machine.MemoryBus.ReadPhysicalRam(16, 0x0123) == 0xD0
                && machine.MemoryBus.ReadPhysicalRam(2, 0x0123) == 0xD2,
            "A read-only Layer 2 aperture incorrectly redirected writes.");
        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x01);
        machine.MemoryBus.WriteCpu(0x0123, 0xD3);
        Require(machine.MemoryBus.Read(0x0123) == 0xD2
                && machine.MemoryBus.ReadPhysicalRam(16, 0x0123) == 0xD3,
            "A write-only Layer 2 aperture incorrectly redirected reads.");

        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x02);
        Require(machine.Layer2.DisplayEnabled && (machine.Registers.Read(0x69) & 0x80) != 0,
            "Port $123B did not enable Layer 2 through the NextReg $69 alias.");
        machine.Registers.Write(0x69, 0x40);
        Require(!machine.Layer2.DisplayEnabled && machine.Video.SelectedScreenBank == 7
                && machine.Registers.Read(0x69) == 0x40,
            "NextReg $69 did not update Layer 2 enable and ULA shadow aliases.");

        machine.Registers.Write(0x12, 127);
        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x05);
        Require(machine.MemoryBus.Read(0x0123) == 0xFF && !machine.MemoryBus.CanWriteDirect(0x0123),
            "An out-of-range Layer 2 bank did not behave as an open bus.");

        machine.Reset(ZxNextResetKind.Soft);
        Require(machine.Registers.Read(0x12) == 8
                && machine.Registers.Read(0x13) == 11
                && machine.Registers.Read(0x14) == 0xE3
                && machine.Registers.Read(0x18) == 0
                && machine.PortBus.ReadUncontended(ZxNextLayer2Device.ControlPort) == 0,
            "Soft reset did not restore the Layer 2 registers and control aperture.");
    }

    private static void VerifyLayer2BitmapModes()
    {
        const int black = unchecked((int)0xFF000000);
        const int red = unchecked((int)0xFFFF0000);
        const int green = unchecked((int)0xFF00FF00);
        const int blue = unchecked((int)0xFF0000FF);
        const int magenta = unchecked((int)0xFFFF00FF);
        const int darkMagenta = unchecked((int)0xFF490049);

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        WriteLayer2Palette(machine, 0x01, 0x1C0);
        WriteLayer2Palette(machine, 0x02, 0x038);
        WriteLayer2Palette(machine, 0x03, 0x007);

        // 256x192x8 is row-major and centred in the 320x256 logical surface.
        WriteLayer2Byte(machine, 8, 0x0000, 0x01);
        WriteLayer2Byte(machine, 8, 0x0001, 0x02);
        WriteLayer2Byte(machine, 8, 0x0100, 0x03);
        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x02);
        machine.Video.RenderFrame(0);
        int[] frame = machine.Video.FrameBuffer;
        int layer256 = (ZxNextVideoRenderer.UlaTop * ZxNextVideoRenderer.FrameWidth)
            + ZxNextVideoRenderer.UlaLeft;
        Require(frame[0] == black
                && frame[layer256] == red && frame[layer256 + 1] == red
                && frame[layer256 + 2] == green
                && frame[layer256 + ZxNextVideoRenderer.FrameWidth] == blue,
            "The centred 256x192 Layer 2 row-major bitmap was decoded incorrectly.");

        WriteLayer2Palette(machine, 0x11, 0x038);
        machine.Registers.Write(0x70, 0x01);
        machine.Video.RenderFrame(1);
        Require(frame[layer256] == green,
            "The Layer 2 palette offset was not added to the pixel's upper nibble.");
        machine.Registers.Write(0x70, 0);

        machine.Registers.Write(0x16, 1);
        machine.Video.RenderFrame(2);
        Require(frame[layer256] == green,
            "Layer 2 horizontal scroll did not wrap the 256-pixel surface.");
        machine.Registers.Write(0x16, 0);
        machine.Registers.Write(0x17, 1);
        machine.Video.RenderFrame(3);
        Require(frame[layer256] == blue,
            "Layer 2 vertical scroll did not use row-major 256x192 addressing.");
        machine.Registers.Write(0x17, 0);
        SetLayer2Clip(machine, 1, 1, 0, 0);
        machine.Video.RenderFrame(4);
        Require(frame[layer256] == black && frame[layer256 + 2] == green,
            "The 256-mode Layer 2 clip window was not inclusive or pixel-addressed.");

        // CPU shadow selection must not change the display bank.
        SetLayer2Clip(machine, 0, 255, 0, 191);
        machine.Registers.Write(0x13, 20);
        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x0A);
        machine.Video.RenderFrame(4);
        Require(frame[layer256] == red,
            "Selecting the shadow CPU aperture incorrectly changed the displayed Layer 2 bank.");

        // 320x256x8 is column-major and covers the complete logical surface.
        machine.Registers.Write(0x12, 20);
        machine.Registers.Write(0x70, 0x10);
        SetLayer2Clip(machine, 0, 159, 0, 255);
        WriteLayer2Byte(machine, 20, 0x0000, 0x01);
        WriteLayer2Byte(machine, 20, 0x0100, 0x02);
        WriteLayer2Byte(machine, 20, 0x0001, 0x03);
        machine.Video.RenderFrame(5);
        Require(frame[0] == red && frame[1] == red && frame[2] == green,
            "The 320x256 Layer 2 column-major bitmap or horizontal scaling was incorrect.");
        machine.Registers.Write(0x16, 1);
        machine.Video.RenderFrame(6);
        Require(frame[0] == green,
            "The 320-mode Layer 2 X scroll did not advance one column.");
        machine.Registers.Write(0x16, 0);
        machine.Registers.Write(0x17, 1);
        machine.Video.RenderFrame(7);
        Require(frame[0] == blue,
            "The 320-mode Layer 2 Y scroll did not advance within a column.");

        machine.Registers.Write(0x68, 0x80);
        machine.Video.RenderFrame(8);
        int layer2OnlyReference = frame[0];
        machine.Video.Reset();
        machine.Video.AdvanceMasterTicks(machine.UlaTiming.MasterTicksPerFrame);
        Require(machine.Video.FrameBuffer[0] == layer2OnlyReference,
            "The live Layer 2-only composition path diverged from the functional reference frame.");
        WriteLayer2Palette(machine, 0x03, 0x1C0);
        machine.Video.AdvanceMasterTicks(machine.UlaTiming.MasterTicksPerFrame);
        Require(machine.Video.FrameBuffer[0] == red,
            "A Layer 2 palette write did not invalidate the cached ARGB entry.");
        WriteLayer2Palette(machine, 0x03, 0x007);
        machine.Registers.Write(0x68, 0x00);

        // 640x256x4 packs two independently coloured host pixels into each byte.
        machine.Registers.Write(0x12, 30);
        machine.Registers.Write(0x16, 0);
        machine.Registers.Write(0x17, 0);
        machine.Registers.Write(0x70, 0x20);
        SetLayer2Clip(machine, 0, 159, 0, 255);
        WriteLayer2Byte(machine, 30, 0x0000, 0x12);
        WriteLayer2Byte(machine, 30, 0x0100, 0x23);
        machine.Video.RenderFrame(8);
        Require(frame[0] == red && frame[1] == green,
            "The 640x256 Layer 2 path did not unpack high and low nibbles into adjacent pixels.");
        machine.Registers.Write(0x16, 1);
        machine.Video.RenderFrame(9);
        Require(frame[0] == green && frame[1] == blue,
            "The 640-mode Layer 2 X scroll did not advance one packed byte.");
        machine.Registers.Write(0x16, 0);
        SetLayer2Clip(machine, 1, 1, 0, 0);
        WriteLayer2Byte(machine, 30, 0x0200, 0x11);
        WriteLayer2Byte(machine, 30, 0x0300, 0x11);
        machine.Video.RenderFrame(10);
        Require(frame[0] == black && frame[4] == red && frame[7] == red && frame[8] == black,
            "The wide-mode Layer 2 clip coordinates did not expand to four host pixels.");

        // Transparency, ULA ordering, priority colours and the two blend modes.
        machine.Registers.Write(0x12, 8);
        machine.Registers.Write(0x70, 0);
        SetLayer2Clip(machine, 0, 255, 0, 191);
        machine.MemoryBus.WritePhysicalRam(10, 0x0000, 0x80);
        machine.MemoryBus.WritePhysicalRam(10, 0x1800, 0x41); // Bright-blue ink.
        WriteLayer2Byte(machine, 8, 0, 0x01);
        int[] expectedOrdinaryPriority = [red, red, blue, red, blue, blue, blue, blue];
        for (int priority = 0; priority < expectedOrdinaryPriority.Length; priority++)
        {
            machine.Registers.Write(0x15, (byte)(priority << 2));
            machine.Video.RenderFrame((ulong)(11 + priority));
            Require(frame[layer256] == expectedOrdinaryPriority[priority],
                $"NextReg $15 priority {priority} composed ordinary ULA/Layer 2 pixels incorrectly.");
        }

        WriteLayer2Palette(machine, 0x01, 0x1C0, priority: true);
        machine.Registers.Write(0x40, 0x01);
        Require(machine.Registers.Read(0x44) == 0x80,
            "NextReg $44 did not read back the Layer 2 promotion bit.");
        machine.Registers.Write(0x15, 0x08); // SUL.
        machine.Video.RenderFrame(20);
        Require(frame[layer256] == red,
            "A promoted Layer 2 palette colour did not move above the ULA.");
        machine.Registers.Write(0x15, 0x18); // Blend mode 6.
        machine.Video.RenderFrame(21);
        Require(frame[layer256] == magenta,
            "Layer-priority blend mode 6 did not add and clamp ULA and Layer 2 RGB.");
        machine.Registers.Write(0x15, 0x1C); // Blend mode 7.
        machine.Video.RenderFrame(22);
        Require(frame[layer256] == darkMagenta,
            "Layer-priority blend mode 7 did not apply the subtract-five blend.");

        WriteLayer2Byte(machine, 8, 0, 0xE3);
        machine.Registers.Write(0x15, 0);
        machine.Video.RenderFrame(16);
        Require(frame[layer256] == blue,
            "The global transparency colour did not reveal the ULA below Layer 2.");
        WriteLayer2Byte(machine, 8, 0, 0x01);
        machine.Registers.Write(0x15, 0x08);
        machine.Registers.Write(0x14, 0x03); // Bright-blue palette colour's upper eight bits.
        machine.Video.RenderFrame(17);
        Require(frame[layer256] == red,
            "Global ULA transparency did not reveal Layer 2 in a ULA-first ordering.");
        machine.Registers.Write(0x14, 0xE3);

        WriteLayer2Palette(machine, 0x01, 0x038, secondPalette: true);
        machine.Registers.Write(0x15, 0);
        machine.Video.RenderFrame(18);
        Require(frame[layer256] == green,
            "NextReg $43 did not select the second Layer 2 palette for display.");

        // Register state is sampled by the live raster path rather than once per frame.
        ZxNextMachine raster = ZxNextMachineFactory.CreateFoundationMachine();
        WriteLayer2Palette(raster, 1, 0x1C0);
        WriteLayer2Palette(raster, 2, 0x038);
        for (int x = 0; x < 320; x++)
        {
            WriteLayer2Byte(raster, 20, x << 8, 1);
            WriteLayer2Byte(raster, 30, x << 8, 2);
        }

        raster.Registers.Write(0x12, 20);
        raster.Registers.Write(0x70, 0x10);
        SetLayer2Clip(raster, 0, 159, 0, 255);
        raster.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x02);
        raster.Video.Reset();
        ZxNextUlaTimingProfile profile = raster.UlaTiming.Profile;
        const int splitLogicalX = 80;
        ulong splitTick = checked(
            ((ulong)profile.VisibleFirstLine * (ulong)profile.PixelsPerLine
                + (ulong)(profile.VisibleStartHorizontal + splitLogicalX))
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        raster.Video.AdvanceMasterTicks(splitTick);
        raster.Registers.Write(0x12, 30);
        raster.Video.AdvanceMasterTicks(profile.MasterTicksPerFrame - splitTick);
        Require(raster.Video.FrameBuffer[0] == red
                && raster.Video.FrameBuffer[(splitLogicalX * 2) - 1] == red
                && raster.Video.FrameBuffer[splitLogicalX * 2] == green,
            $"A mid-raster Layer 2 bank change was deferred or moved from its beam coordinate "
            + $"(${raster.Video.FrameBuffer[0]:X8}, "
            + $"${raster.Video.FrameBuffer[(splitLogicalX * 2) - 1]:X8}, "
            + $"${raster.Video.FrameBuffer[splitLogicalX * 2]:X8}).");
    }

    private static void VerifyTilemapRegistersAndRendering()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextTilemapDevice tilemap = machine.Tilemap;
        Require(!tilemap.Enabled
                && tilemap.TransparencyIndex == 0x0F
                && tilemap.MapBase == 0x2C
                && tilemap.TileBase == 0x0C
                && tilemap.ClipX1 == 0
                && tilemap.ClipX2 == 159
                && tilemap.ClipY1 == 0
                && tilemap.ClipY2 == 255,
            "Tilemap soft-reset register state is incorrect.");

        machine.PortBus.WriteNextRegister(0x1C, 0x08);
        machine.PortBus.WriteNextRegister(0x1B, 1);
        machine.PortBus.WriteNextRegister(0x1B, 157);
        machine.PortBus.WriteNextRegister(0x1B, 2);
        machine.PortBus.WriteNextRegister(0x1B, 250);
        Require(tilemap.ClipX1 == 1
                && tilemap.ClipX2 == 157
                && tilemap.ClipY1 == 2
                && tilemap.ClipY2 == 250
                && tilemap.ClipIndex == 0,
            "Tilemap clip writes did not cycle over X1/X2/Y1/Y2.");

        machine.PortBus.WriteNextRegister(0x1C, 0x08);
        machine.PortBus.WriteNextRegister(0x1B, 0);
        machine.PortBus.WriteNextRegister(0x1B, 159);
        machine.PortBus.WriteNextRegister(0x1B, 0);
        machine.PortBus.WriteNextRegister(0x1B, 255);
        machine.PortBus.WriteNextRegister(0x6E, 0x20); // map at $6000 in bank 5
        machine.PortBus.WriteNextRegister(0x6F, 0x00); // definitions at $4000 in bank 5
        machine.PortBus.WriteNextRegister(0x6C, 0x00);
        machine.PortBus.WriteNextRegister(0x6B, 0xE0); // 80 columns, attributes stripped

        machine.MemoryBus.WritePhysicalRam(10, 0, 0x12);
        machine.MemoryBus.WritePhysicalRam(10, 1, 0x34);
        machine.MemoryBus.WritePhysicalRam(10, 2, 0x56);
        machine.MemoryBus.WritePhysicalRam(10, 3, 0x78);
        Require(tilemap.TryGetPaletteIndex(
                    0, 0, machine.MemoryBus.PhysicalMemory, out byte first, out bool firstBelow)
                && tilemap.TryGetPaletteIndex(
                    1, 0, machine.MemoryBus.PhysicalMemory, out byte second, out _)
                && first == 1
                && second == 2
                && !firstBelow,
            "80-column tilemap did not unpack consecutive 4-bit pixels.");

        machine.PortBus.WriteNextRegister(0x30, 1);
        Require(tilemap.TryGetPaletteIndex(
                    0, 0, machine.MemoryBus.PhysicalMemory, out byte scrolled, out _)
                && scrolled == 2,
            "Tilemap X scrolling did not advance at 14 MHz pixel resolution.");

        machine.PortBus.WriteNextRegister(0x30, 0);
        machine.PortBus.WriteNextRegister(0x6B, 0xA0); // 40 columns, attributes stripped
        Require(tilemap.TryGetPaletteIndex(
                    0, 0, machine.MemoryBus.PhysicalMemory, out byte wideFirst, out _)
                && tilemap.TryGetPaletteIndex(
                    1, 0, machine.MemoryBus.PhysicalMemory, out byte wideDuplicate, out _)
                && tilemap.TryGetPaletteIndex(
                    2, 0, machine.MemoryBus.PhysicalMemory, out byte wideSecond, out _)
                && wideFirst == 1
                && wideDuplicate == 1
                && wideSecond == 2,
            "40-column tilemap did not double source pixels.");

        machine.MemoryBus.WritePhysicalRam(11, 1, 0xA8);
        machine.PortBus.WriteNextRegister(0x6B, 0x80); // attributes present
        Require(tilemap.TryGetPaletteIndex(
                    0, 0, machine.MemoryBus.PhysicalMemory, out byte mirrored, out bool below)
                && mirrored == 0xA8
                && !below,
            "Tilemap attributes did not apply palette offset and X mirror.");

        machine.PortBus.WriteNextRegister(0x4C, 0x08);
        Require(!tilemap.TryGetPaletteIndex(
                0, 0, machine.MemoryBus.PhysicalMemory, out _, out _),
            "Tilemap transparency did not compare the un-offset 4-bit colour.");

        machine.PortBus.WriteNextRegister(0x4C, 0x0F);
        machine.MemoryBus.WritePhysicalRam(11, 1, 0x00);
        machine.PortBus.WriteNextRegister(0x6B, 0xE0);
        machine.PortBus.WriteNextRegister(0x68, 0x80); // disable ULA output
        machine.Video.RenderFrame(1);
        int[] frame = new int[ZxNextVideoRenderer.FrameWidth * ZxNextVideoRenderer.FrameHeight];
        machine.Video.CopyFrame(frame);
        Require(frame[0] != frame[1],
            "Tilemap pixels did not enter the stable framebuffer compositor.");

        int snapshotFirst = frame[0];
        int snapshotSecond = frame[1];
        machine.Video.Reset();
        machine.Video.AdvanceMasterTicks(machine.UlaTiming.MasterTicksPerFrame);
        Require(machine.Video.FrameBuffer[0] == snapshotFirst
                && machine.Video.FrameBuffer[1] == snapshotSecond,
            "The live tilemap-only composition path diverged from the functional reference frame.");

        machine.Registers.Write(0x43, 0x30); // First tilemap palette.
        machine.Registers.Write(0x40, 0x01);
        machine.Registers.Write(0x44, 0xE0);
        machine.Registers.Write(0x44, 0x00); // Full red.
        machine.Video.AdvanceMasterTicks(machine.UlaTiming.MasterTicksPerFrame);
        Require(machine.Video.FrameBuffer[0] == ZxNextPaletteDevice.ToArgb(0x1C0),
            "A tilemap palette write did not invalidate the cached ARGB entry.");

        machine.Registers.Write(0x2F, 0x03);
        machine.Registers.Write(0x30, 0xFF);
        machine.Video.RenderFrame(2);
        int[] maximumScrollReference = (int[])machine.Video.FrameBuffer.Clone();
        machine.Video.Reset();
        machine.Video.AdvanceMasterTicks(machine.UlaTiming.MasterTicksPerFrame);
        Require(machine.Video.FrameBuffer.AsSpan().SequenceEqual(maximumScrollReference),
            "The timed tilemap pre-wrap diverged at the maximum 10-bit X scroll.");

        machine.Reset(ZxNextResetKind.Soft);
        Require(!tilemap.Enabled
                && tilemap.ScrollX == 0
                && tilemap.ScrollY == 0
                && tilemap.TransparencyIndex == 0x0F,
            "Soft reset did not restore tilemap registers.");
    }

    private static void VerifyVideoMemoryArbitration()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextVideoMemoryArbiter arbiter = machine.VideoMemoryArbiter;
        machine.MemoryBus.WritePhysicalRam(10, 0x0123, 0xA5);
        machine.MemoryBus.WritePhysicalRam(14, 0x0456, 0x5A);

        var ula = new ZxNextVideoMemoryRequest(ZxNextVideoMemoryBank.Bank5, 0x0123);
        var tilemap = new ZxNextVideoMemoryRequest(ZxNextVideoMemoryBank.Bank7, 0x0456);
        arbiter.Clock(0, ula, tilemap);
        Require(arbiter.ScheduledOwner == ZxNextVideoMemoryOwner.Ula
                && !arbiter.UlaResponseValid
                && !arbiter.TilemapResponseValid
                && arbiter.UlaGrantCount == 1
                && arbiter.TilemapGrantCount == 0
                && arbiter.TilemapStallCount == 1,
            "A simultaneous tilemap request was not stalled behind the ULA.");

        arbiter.Clock(1, null, tilemap);
        Require(arbiter.UlaResponseValid
                && arbiter.UlaResponseData == 0xA5
                && !arbiter.TilemapResponseValid
                && arbiter.ScheduledOwner == ZxNextVideoMemoryOwner.Tilemap
                && arbiter.TilemapGrantCount == 1,
            "The ULA response pipeline or retained tilemap grant was incorrect.");

        arbiter.Clock(2, null, null);
        Require(!arbiter.UlaResponseValid
                && arbiter.TilemapResponseValid
                && arbiter.TilemapResponseData == 0x5A
                && arbiter.ScheduledOwner == ZxNextVideoMemoryOwner.None,
            "The granted tilemap read did not complete one clk28 cycle later.");

        machine.Reset(ZxNextResetKind.Soft);
        Require(arbiter.ScheduledOwner == ZxNextVideoMemoryOwner.None
                && !arbiter.UlaResponseValid
                && !arbiter.TilemapResponseValid
                && arbiter.UlaGrantCount == 0
                && arbiter.TilemapGrantCount == 0
                && arbiter.TilemapStallCount == 0,
            "Reset did not clear video-memory arbitration state.");

        machine.Video.AdvanceMasterTicks(machine.UlaTiming.MasterTicksPerFrame);
        Require(arbiter.UlaGrantCount == 12_288
                && arbiter.TilemapGrantCount == 0
                && arbiter.TilemapStallCount == 0,
            $"The live ULA schedule did not issue its 12288 pixel/attribute reads "
            + $"through the shared port (grants={arbiter.UlaGrantCount}).");

        machine.PortBus.WriteNextRegister(0x6E, 0x20);
        machine.PortBus.WriteNextRegister(0x6F, 0x00);
        machine.PortBus.WriteNextRegister(0x6C, 0x00);
        machine.PortBus.WriteNextRegister(0x6B, 0xE0);
        machine.MemoryBus.WritePhysicalRam(10, 0, 0x12);
        machine.MemoryBus.WritePhysicalRam(10, 1, 0x34);
        machine.MemoryBus.WritePhysicalRam(10, 2, 0x56);
        machine.MemoryBus.WritePhysicalRam(10, 3, 0x78);
        machine.MemoryBus.WritePhysicalRam(11, 0, 0x00);
        machine.Video.Reset();

        ZxNextUlaTimingProfile profile = machine.UlaTiming.Profile;
        ulong firstLineProbe = checked(
            (((ulong)profile.VisibleFirstLine * (ulong)profile.PixelsPerLine)
                + (ulong)profile.VisibleStartHorizontal + 1UL)
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        machine.Video.AdvanceMasterTicks(firstLineProbe);
        Require(machine.Tilemap.TryGetTimedPaletteIndex(0, 0, out byte timed0, out bool below0)
                && machine.Tilemap.TryGetTimedPaletteIndex(1, 0, out byte timed1, out _)
                && timed0 == 1
                && timed1 == 2
                && !below0
                && machine.Tilemap.CompletedCharacterFetches >= 1
                && machine.Tilemap.FetchedPixelCount >= 8
                && machine.Tilemap.TimedPixelDeadlineMisses == 0
                && arbiter.TilemapGrantCount >= 10,
            "The live tilemap map/attribute/pixel request sequence did not fill its first buffer.");

        ulong contendedLineProbe = checked(
            (((ulong)(profile.VisibleFirstLine + 32) * (ulong)profile.PixelsPerLine)
                + (ulong)profile.VisibleStartHorizontal + 64UL)
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        machine.Video.AdvanceMasterTicks(contendedLineProbe - firstLineProbe);
        Require(arbiter.TilemapStallCount > 0
                && machine.Tilemap.TryGetTimedPaletteIndex(0, 32, out byte contendedPixel, out _)
                && contendedPixel == 1,
            "ULA grants did not stall and resume the live tilemap request stream.");
    }

    private static void VerifySpritePortsAndRendering()
    {
        const int black = unchecked((int)0xFF000000);
        const int red = unchecked((int)0xFFFF0000);
        const int green = unchecked((int)0xFF00FF00);
        const int blue = unchecked((int)0xFF0000FF);
        const int cyan = unchecked((int)0xFF00FFFF);
        const int magenta = unchecked((int)0xFFFF00FF);
        const int yellow = unchecked((int)0xFFFFFF00);

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        byte[] pattern = new byte[ZxNextSpriteDevice.PatternSize];
        Array.Fill(pattern, (byte)0xE3);
        pattern[0] = 0x01;
        pattern[1] = 0x01;
        pattern[2] = 0x01;
        pattern[15] = 0x02;
        pattern[240] = 0x03;

        machine.PortBus.WriteUncontended(ZxNextSpriteDevice.StatusAndSlotPort, 0);
        for (int index = 0; index < pattern.Length; index++)
        {
            // The low-byte sprite ports must remain decoded while OTIR changes B.
            machine.PortBus.WriteUncontended(
                (ushort)(((pattern.Length - index - 1) << 8) | ZxNextSpriteDevice.PatternPort),
                pattern[index]);
        }

        machine.PortBus.WriteUncontended(ZxNextSpriteDevice.StatusAndSlotPort, 0);
        byte[] attributes = [32, 32, 0, 0x80];
        for (int index = 0; index < attributes.Length; index++)
        {
            machine.PortBus.WriteUncontended(
                (ushort)(((0x80 - index) << 8) | ZxNextSpriteDevice.AttributePort),
                attributes[index]);
        }

        Require(machine.Sprites.ReadPatternByte(0) == 0x01
                && machine.Sprites.ReadPatternByte(15) == 0x02
                && machine.Sprites.ReadPatternByte(240) == 0x03
                && machine.Sprites.ReadAttribute(0, 0) == 32
                && machine.Sprites.ReadAttribute(0, 3) == 0x80
                && machine.Sprites.SelectedPortSprite == 1
                && machine.Sprites.PortAttributeIndex == 0,
            "The sprite upload ports did not store pattern/attribute data or post-increment the slot.");

        machine.Registers.Write(0x34, 7);
        machine.Registers.Write(0x75, 0x55);
        Require(machine.Sprites.ReadAttribute(7, 0) == 0x55
                && machine.Sprites.SelectedNextRegisterSprite == 8
                && machine.Sprites.SelectedPortSprite == 1,
            "The incrementing sprite NextReg mirror advanced the wrong selector.");

        machine.Registers.Write(0x09, 0x10);
        machine.Registers.Write(0x34, 0x82);
        Require(machine.Sprites.IdLockstep
                && machine.Sprites.SelectedPortSprite == 2
                && machine.Sprites.SelectedNextRegisterSprite == 2
                && machine.Sprites.PatternAddress == 0x0280,
            "Sprite-id lockstep did not synchronize NextReg $34 with port $303B.");
        machine.Registers.Write(0x09, 0);

        WriteSpritePalette(machine, 0x01, 0x1C0);
        WriteSpritePalette(machine, 0x02, 0x1C7);
        WriteSpritePalette(machine, 0x03, 0x1F8);
        WriteSpritePalette(machine, 0x11, 0x03F);
        WriteSpritePalette(machine, 0x21, 0x1F8);
        WriteLayer2Palette(machine, 0x04, 0x038);
        WriteLayer2Byte(machine, 8, 0, 0x04);
        WriteLayer2Byte(machine, 8, 1, 0x04);
        WriteLayer2Byte(machine, 8, 2, 0x04);
        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x02);
        machine.MemoryBus.WritePhysicalRam(10, 0x0000, 0x80);
        machine.MemoryBus.WritePhysicalRam(10, 0x1800, 0x41); // Bright blue ULA ink.

        int spritePixel = (ZxNextVideoRenderer.UlaTop * ZxNextVideoRenderer.FrameWidth)
            + ZxNextVideoRenderer.UlaLeft;
        int[] expectedPriority = [red, green, red, green, blue, blue];
        for (int priority = 0; priority < expectedPriority.Length; priority++)
        {
            machine.Registers.Write(0x15, (byte)((priority << 2) | 0x01));
            machine.Video.RenderFrame((ulong)priority);
            Require(machine.Video.FrameBuffer[spritePixel] == expectedPriority[priority],
                $"Sprite layer priority {priority} composed S/L/U in the wrong order.");
        }

        machine.Registers.Write(0x34, 0);
        machine.Registers.Write(0x37, 0x08); // X mirror: source pixel 15 reaches destination 0.
        machine.Registers.Write(0x15, 0x01);
        machine.Video.RenderFrame(6);
        Require(machine.Video.FrameBuffer[spritePixel] == magenta,
            "The sprite X-mirror transform did not reverse the pattern row.");

        machine.Registers.Write(0x37, 0x02); // Rotate: source row 15, column 0 reaches destination 0.
        machine.Video.RenderFrame(7);
        Require(machine.Video.FrameBuffer[spritePixel] == yellow,
            "The sprite rotation transform did not use the FPGA pattern stride.");
        machine.Registers.Write(0x37, 0);

        machine.Registers.Write(0x34, 1);
        machine.Registers.Write(0x35, 32);
        machine.Registers.Write(0x36, 32);
        machine.Registers.Write(0x37, 0x10);
        machine.Registers.Write(0x38, 0x80);
        machine.Registers.Write(0x15, 0x01);
        machine.Video.RenderFrame(8);
        Require(machine.Video.FrameBuffer[spritePixel] == cyan,
            "Default sprite ordering did not put the higher-numbered sprite on top.");
        machine.Registers.Write(0x15, 0x41);
        machine.Video.RenderFrame(9);
        Require(machine.Video.FrameBuffer[spritePixel] == red,
            "NextReg $15 bit 6 did not put sprite zero on top.");
        machine.Registers.Write(0x34, 1);
        machine.Registers.Write(0x38, 0);
        machine.Registers.Write(0x15, 0x01);

        // A 4-bit pattern selected through the +128 half-slot flag, scaled 2x in X.
        machine.PortBus.WriteUncontended(ZxNextSpriteDevice.StatusAndSlotPort, 0x80);
        for (int index = 0; index < 128; index++)
        {
            machine.PortBus.WriteUncontended(
                (ushort)(((127 - index) << 8) | ZxNextSpriteDevice.PatternPort),
                index == 0 ? (byte)0x13 : (byte)0x33);
        }

        machine.Registers.Write(0x34, 0);
        machine.Registers.Write(0x35, 32);
        machine.Registers.Write(0x36, 32);
        machine.Registers.Write(0x37, 0x20);
        machine.Registers.Write(0x38, 0xC0);
        machine.Registers.Write(0x39, 0xC8);
        machine.Video.RenderFrame(10);
        Require(machine.Video.FrameBuffer[spritePixel] == yellow
                && machine.Video.FrameBuffer[spritePixel + 2] == yellow
                && machine.Video.FrameBuffer[spritePixel + 4] == green,
            "The 4-bit sprite pattern selector, nibble transparency or X scaling was incorrect "
            + $"(${machine.Video.FrameBuffer[spritePixel]:X8}, "
            + $"${machine.Video.FrameBuffer[spritePixel + 2]:X8}, "
            + $"${machine.Video.FrameBuffer[spritePixel + 4]:X8}).");

        // Restore the 8-bit sprite and add a second overlapping sprite for collision state.
        machine.Registers.Write(0x38, 0x80);
        machine.Registers.Write(0x39, 0);
        machine.Registers.Write(0x37, 0);
        machine.Registers.Write(0x34, 1);
        machine.Registers.Write(0x35, 32);
        machine.Registers.Write(0x36, 32);
        machine.Registers.Write(0x37, 0);
        machine.Registers.Write(0x38, 0x80);
        machine.Video.RenderFrame(11);
        Require((machine.PortBus.ReadUncontended(ZxNextSpriteDevice.StatusAndSlotPort) & 0x01) != 0
                && machine.PortBus.ReadUncontended(ZxNextSpriteDevice.StatusAndSlotPort) == 0,
            "Sprite collision status was not latched and cleared by a port $303B read.");

        machine.Registers.Write(0x34, 1);
        machine.Registers.Write(0x38, 0);
        machine.Registers.Write(0x34, 0);
        machine.Registers.Write(0x35, 0);
        machine.Registers.Write(0x36, 0);
        machine.Registers.Write(0x37, 0);
        machine.Registers.Write(0x38, 0x80);
        machine.Registers.Write(0x15, 0x01);
        machine.Video.RenderFrame(12);
        Require(machine.Video.FrameBuffer[0] == black,
            "A sprite escaped the standard 256x192 clip/origin when over-border mode was disabled.");

        machine.Registers.Write(0x15, 0x03);
        machine.Video.RenderFrame(13);
        Require(machine.Video.FrameBuffer[0] == red,
            "Sprite over-border mode did not expose the complete 320x256 sprite surface.");

        machine.Registers.Write(0x1C, 0x02);
        machine.Registers.Write(0x19, 1);
        machine.Registers.Write(0x19, 1);
        machine.Registers.Write(0x19, 0);
        machine.Registers.Write(0x19, 0);
        Require((machine.Registers.Read(0x1C) & 0x0C) == 0,
            "The sprite clip index did not wrap or appear in NextReg $1C.");
        machine.Registers.Write(0x15, 0x23);
        machine.Video.RenderFrame(14);
        Require(machine.Video.FrameBuffer[0] == black
                && machine.Video.FrameBuffer[4] == red,
            "Sprite clipping in over-border mode did not double the horizontal coordinates.");

        machine.Reset(ZxNextResetKind.Soft);
        Require(machine.Registers.Read(0x4B) == 0xE3
                && machine.Sprites.SelectedPortSprite == 0
                && machine.Sprites.SelectedNextRegisterSprite == 0
                && machine.Sprites.ClipX2 == 0xFF
                && machine.Sprites.ClipY2 == 0xBF,
            "Soft reset did not restore sprite interface and register state.");
    }

    private static void VerifyRelativeAndUnifiedSprites()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        WriteSpritePattern8(machine, 0, 0x01, 0);
        WriteSpritePattern8(machine, 1, 0x02, 0, 240);
        WriteSpritePattern8(machine, 2, 0x03, 0);
        WriteSpritePattern8(machine, 3, 0x04, 0, 5);
        WriteSpritePattern4(machine, 8, 0x06, 0);
        WriteSpritePattern4(machine, 10, 0x07, 0);

        // Composite anchor: relative coordinates are unscaled, while the relative
        // sprite retains its own scale/transform fields. Pattern and palette offsets
        // can independently be relative to the anchor.
        WriteSpriteAttributes(machine, 0, 40, 50, 0x20, 0x82, 0);
        WriteSpriteAttributes(machine, 1, 20, 5, 0x11, 0xC1, 0x41);
        Require(TryGetSpritePixel(machine, 40, 50, 0x03, out byte anchorPixel)
                && anchorPixel == 0x23
                && TryGetSpritePixel(machine, 60, 55, 0x03, out byte relativePixel)
                && relativePixel == 0x34,
            "Composite relative coordinates, pattern addition or palette addition were incorrect.");

        machine.Registers.Write(0x34, 0);
        machine.Registers.Write(0x38, 0x02);
        Require(!TryGetSpritePixel(machine, 60, 55, 0x03, out _),
            "A relative sprite remained visible while its anchor was hidden.");
        machine.Registers.Write(0x38, 0x82);

        machine.Registers.Write(0x34, 1);
        machine.Registers.Write(0x35, unchecked((byte)-45));
        Require(TryGetSpritePixel(machine, 0, 55, 0x03, out byte wrappedPixel)
                && wrappedPixel == 0x34,
            "A negative relative offset did not wrap on the 9-bit sprite coordinate ring.");

        machine.Registers.Write(0x35, 20);
        machine.Registers.Write(0x37, 0);
        machine.Registers.Write(0x39, 0x48);
        Require(TryGetSpritePixel(machine, 60, 55, 0x03, out byte compositeScale0)
                && compositeScale0 == 0x02
                && TryGetSpritePixel(machine, 61, 55, 0x03, out byte compositeScale1)
                && compositeScale1 == 0x02
                && !TryGetSpritePixel(machine, 62, 55, 0x03, out _),
            "A composite relative sprite did not apply its own X scale.");

        // Unified anchor: coordinates and output scale inherit the anchor transform.
        WriteSpriteAttributes(machine, 2, 100, 80, 0, 0xC0, 0x28);
        WriteSpriteAttributes(machine, 3, 10, 3, 0, 0xC1, 0x40);
        Require(TryGetSpritePixel(machine, 120, 83, 0x03, out byte unifiedScale0)
                && unifiedScale0 == 0x02
                && TryGetSpritePixel(machine, 121, 83, 0x03, out byte unifiedScale1)
                && unifiedScale1 == 0x02
                && !TryGetSpritePixel(machine, 122, 83, 0x03, out _),
            "A unified relative sprite did not inherit anchor coordinate/output scaling.");

        machine.Registers.Write(0x34, 2);
        machine.Registers.Write(0x37, 0x02);
        Require(TryGetSpritePixel(machine, 94, 90, 0x03, out byte rotatedPixel)
                && rotatedPixel == 0x02,
            "Unified anchor rotation did not rotate/scale the relative offset and pattern.");

        machine.Registers.Write(0x37, 0x0A);
        Require(TryGetSpritePixel(machine, 106, 90, 0x03, out byte mirroredRotationPixel)
                && mirroredRotationPixel == 0x02,
            "Unified anchor X mirror did not combine with rotation as the FPGA does.");

        machine.Registers.Write(0x37, 0x04);
        Require(TryGetSpritePixel(machine, 120, 77, 0x03, out byte yMirroredPixel)
                && yMirroredPixel == 0x02,
            "Unified anchor Y mirror did not transform the relative coordinate and pattern.");

        // A new non-relative sprite becomes the anchor for the following chain.
        WriteSpriteAttributes(machine, 4, 150, 100, 0, 0xC4, 0x80);
        WriteSpriteAttributes(machine, 5, 10, 0, 0x10, 0xC1, 0x41);
        Require(TryGetSpritePixel(machine, 150, 100, 0x03, out byte fourBitAnchor)
                && fourBitAnchor == 0x06
                && TryGetSpritePixel(machine, 160, 100, 0x03, out byte fourBitRelative)
                && fourBitRelative == 0x17,
            "A relative sprite did not inherit 4-bit mode or add its 7-bit pattern number.");

        Require(TryGetSpritePixel(machine, 160, 100, 0x43, out byte reversedPriorityPixel)
                && reversedPriorityPixel == 0x17,
            "Reversing sprite priority changed relative-anchor resolution order.");

        machine.Registers.Write(0x34, 4);
        machine.Registers.Write(0x38, 0x40);
        Require(!TryGetSpritePixel(machine, 160, 100, 0x03, out _),
            "A 4-bit relative sprite ignored the visibility of its new anchor.");
    }

    private static void VerifyTimedSpritePreparation()
    {
        // The FPGA prepares line N during line N-1. Once a pixel has entered the
        // build buffer, later pattern writes must not retroactively change it.
        ZxNextMachine prepared = ZxNextMachineFactory.CreateFoundationMachine();
        WriteSolidSpritePattern8(prepared, 0, 0x01);
        WriteSpriteAttributes(prepared, 0, 0, 0, 0, 0x80, 0);
        prepared.Registers.Write(0x15, 0x03);
        prepared.Video.Reset();
        ZxNextUlaTimingProfile profile = prepared.UlaTiming.Profile;
        ulong firstPreparationTick = GetFirstSpritePreparationTick(profile);
        ulong lineMasterTicks = checked(
            (ulong)profile.PixelsPerLine
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);

        prepared.Video.AdvanceMasterTicks(firstPreparationTick + 200);
        Require(!prepared.Sprites.PreparationBusy
                && prepared.Sprites.BuildingLine == 0,
            "A single scale-1 sprite did not finish comfortably inside one line budget.");
        WriteSolidSpritePattern8(prepared, 0, 0x02);
        prepared.Video.AdvanceMasterTicks(lineMasterTicks - 199);
        Require(prepared.Sprites.PreparedLine == 0
                && prepared.Sprites.TryGetPreparedPaletteIndex(0, 0, 0x03, out byte preparedPixel)
                && preparedPixel == 0x01
                && prepared.Sprites.LastPreparationMasterTicks == 145
                && prepared.Sprites.LastQualifiedSpriteCount == ZxNextSpriteDevice.SpriteCount
                && prepared.Sprites.OvertimeCount == 0,
            "The one-line-ahead sprite buffer did not freeze pixels or account setup/qualification time.");

        // Collision is generated while writing the line buffer, before display clip
        // is applied. Two hidden border sprites must therefore still collide.
        ZxNextMachine collision = ZxNextMachineFactory.CreateFoundationMachine();
        WriteSolidSpritePattern8(collision, 0, 0x01);
        WriteSpriteAttributes(collision, 0, 0, 0, 0, 0x80, 0);
        WriteSpriteAttributes(collision, 1, 0, 0, 0, 0x80, 0);
        collision.Registers.Write(0x15, 0x01);
        collision.Video.Reset();
        profile = collision.UlaTiming.Profile;
        firstPreparationTick = GetFirstSpritePreparationTick(profile);
        lineMasterTicks = checked(
            (ulong)profile.PixelsPerLine
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        collision.Video.AdvanceMasterTicks(firstPreparationTick + lineMasterTicks + 1);
        Require(!collision.Sprites.TryGetPreparedPaletteIndex(0, 0, 0x01, out _)
                && (collision.PortBus.ReadUncontended(ZxNextSpriteDevice.StatusAndSlotPort) & 0x01) != 0
                && collision.PortBus.ReadUncontended(ZxNextSpriteDevice.StatusAndSlotPort) == 0,
            "Sprite collision was clipped away or port $303B did not clear the latched status.");

        // 128 visible 8x-scaled sprites cannot finish in a 456-pixel scanline. Keep
        // DMA active across most of that interval to prove the sprite state machine
        // advances in parallel with BUSREQ rather than being serialized behind it.
        ZxNextMachine overload = ZxNextMachineFactory.CreateFoundationMachine();
        WriteSolidSpritePattern8(overload, 0, 0x01);
        for (int sprite = 0; sprite < ZxNextSpriteDevice.SpriteCount; sprite++)
        {
            WriteSpriteAttributes(overload, sprite, 0, 0, 0, 0xC0, 0x18);
        }

        overload.Registers.Write(0x15, 0x03);
        overload.Video.Reset();
        profile = overload.UlaTiming.Profile;
        firstPreparationTick = GetFirstSpritePreparationTick(profile);
        lineMasterTicks = checked(
            (ulong)profile.PixelsPerLine
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        overload.Video.AdvanceMasterTicks(firstPreparationTick + 1);
        ulong preparationBeforeDma = overload.Sprites.SpritePreparationMasterTicks;
        ulong dmaTicksBefore = overload.BusArbiter.DmaOwnedMasterTicks;
        ulong cpuCyclesBefore = overload.Cpu.Cyc;
        WriteDmaCommand(overload,
        [
            0xC3,
            0x7D, 0x00, 0xC1, 0x1F, 0x00,
            0x14,
            0x10,
            0xAD, 0x00, 0x01,
            0x82, 0xCF, 0xB3, 0x87
        ]);
        RunDmaToCompletion(overload);
        ulong dmaTicks = overload.BusArbiter.DmaOwnedMasterTicks - dmaTicksBefore;
        Require(dmaTicks > 0
                && overload.Cpu.Cyc == cpuCyclesBefore
                && overload.Sprites.SpritePreparationMasterTicks - preparationBeforeDma == dmaTicks,
            "Sprite preparation stopped or advanced at the wrong rate while DMA owned the CPU bus.");
        Require(dmaTicks < lineMasterTicks,
            "The diagnostic DMA block unexpectedly exceeded the complete sprite line budget.");

        overload.Video.AdvanceMasterTicks(lineMasterTicks - dmaTicks);
        byte overloadStatus = overload.PortBus.ReadUncontended(
            ZxNextSpriteDevice.StatusAndSlotPort);
        Require((overloadStatus & 0x02) != 0
                && overload.Sprites.OvertimeCount == 1
                && overload.Sprites.LastPreparationMasterTicks == lineMasterTicks
                && overload.Sprites.LastQualifiedSpriteCount < ZxNextSpriteDevice.SpriteCount
                && overload.Sprites.PreparedLine == 0
                && overload.Sprites.PreparationBusy
                && overload.PortBus.ReadUncontended(ZxNextSpriteDevice.StatusAndSlotPort) == 0,
            "An over-budget sprite line did not swap its partial buffer or latch/clear overtime.");

        // The live renderer has a guarded sprite-only compositor. It must remain
        // pixel-identical to the functional compositor and observe palette writes
        // through the cached ARGB lookup on the following frame.
        ZxNextMachine spriteOnly = ZxNextMachineFactory.CreateFoundationMachine();
        WriteSolidSpritePattern8(spriteOnly, 0, 0x01);
        WriteSpriteAttributes(spriteOnly, 0, 0, 0, 0, 0x80, 0);
        spriteOnly.Registers.Write(0x15, 0x03);
        spriteOnly.Registers.Write(0x68, 0x80);
        spriteOnly.Video.RenderFrame(0);
        int[] spriteOnlyReference = (int[])spriteOnly.Video.FrameBuffer.Clone();
        spriteOnly.Video.Reset();
        spriteOnly.Video.AdvanceMasterTicks(spriteOnly.UlaTiming.MasterTicksPerFrame * 2);
        Require(spriteOnly.Video.FrameBuffer.AsSpan().SequenceEqual(spriteOnlyReference),
            "The live sprite-only compositor diverged from the functional compositor.");

        WriteSpritePalette(spriteOnly, 0x01, 0x1C0);
        spriteOnly.Video.AdvanceMasterTicks(spriteOnly.UlaTiming.MasterTicksPerFrame);
        Require(spriteOnly.Video.FrameBuffer[0] == unchecked((int)0xFFFF0000),
            "The sprite-only ARGB cache did not observe a palette write on the next frame.");

        overload.Reset(ZxNextResetKind.Soft);
        Require(!overload.Sprites.PreparationBusy
                && overload.Sprites.PreparedLine == -1
                && overload.Sprites.BuildingLine == -1
                && overload.Sprites.SpritePreparationMasterTicks == 0
                && overload.Sprites.OvertimeCount == 0,
            "Soft reset did not clear the timed sprite engine and its diagnostics.");
    }

    private static void VerifyZxnDmaLayer2Transfer()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        for (int offset = 0; offset < 0x4000; offset++)
        {
            byte value = (byte)((offset * 37) ^ (offset >> 5) ^ 0xA5);
            Require(machine.MemoryBus.WritePhysicalRam(
                    offset >> 13,
                    offset & (ZxNextPhysicalMemory.PageSize - 1),
                    value),
                "The zxnDMA source pattern could not be installed.");
        }

        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x03);
        byte[] angryBloatersScreenTransfer =
        [
            0xC3,       // WR6 reset
            0xC7,       // WR6 reset port A timing
            0xCB,       // WR6 reset port B timing
            0x7D,       // WR0: A->B, A address and length follow
            0x00, 0xC0, // port A source $C000
            0xFF, 0x3F, // $4000 bytes (native DMA encodes length minus one)
            0x54, 0x01, // WR1: memory A, increment, three-cycle timing
            0x50, 0x01, // WR2: memory B, increment, three-cycle timing
            0xAD,       // WR4: continuous mode, B address follows
            0x00, 0x00, // port B destination $0000 (Layer 2 aperture)
            0x82,       // WR5
            0xCF,       // WR6 load
            0xB3,       // WR6 force ready
            0x87        // WR6 enable DMA
        ];

        WriteDmaCommand(machine, angryBloatersScreenTransfer);
        RunDmaToCompletion(machine);

        for (int offset = 0; offset < 0x4000; offset++)
        {
            byte expected = (byte)((offset * 37) ^ (offset >> 5) ^ 0xA5);
            byte actual = machine.MemoryBus.ReadPhysicalRam(
                16 + (offset >> 13),
                offset & (ZxNextPhysicalMemory.PageSize - 1));
            Require(actual == expected,
                $"The zxnDMA Layer 2 transfer diverged at offset ${offset:X4}: " +
                $"expected ${expected:X2}, found ${actual:X2}.");
        }

        Require(machine.Dma.LastTransferLength == 0x4000
                && machine.Dma.ByteCounter == 0x4000
                && machine.Dma.PortAAddressCounter == 0x0000
                && machine.Dma.PortBAddressCounter == 0x4000
                && machine.PortBus.ReadUncontended(ZxNextDmaDevice.ZxnControlPort) == 0x1B,
            "The zxnDMA length, address counters or end-of-block status were incorrect.");

        // Fixed-source fills are a common clear-screen form of the same interface.
        machine.MemoryBus.WritePhysicalRam(0, 0x0123, 0xE3);
        byte[] fixedSourceFill =
        [
            0xC3,
            0x7D, 0x23, 0xC1, 0x0F, 0x00,
            0x24,       // WR1: memory A, fixed
            0x10,       // WR2: memory B, increment
            0xAD, 0x00, 0x02,
            0x82, 0xCF, 0xB3, 0x87
        ];
        WriteDmaCommand(machine, fixedSourceFill);
        RunDmaToCompletion(machine);
        for (int offset = 0x0200; offset < 0x0210; offset++)
        {
            Require(machine.MemoryBus.ReadPhysicalRam(16, offset) == 0xE3,
                "The zxnDMA fixed-source Layer 2 fill did not repeat its source byte.");
        }

        // DMA I/O endpoints go through the ordinary decoded device ports.
        machine.MemoryBus.WritePhysicalRam(0, 0x0200, 0x01);
        machine.MemoryBus.WritePhysicalRam(0, 0x0201, 0x02);
        machine.MemoryBus.WritePhysicalRam(0, 0x0202, 0x03);
        machine.MemoryBus.WritePhysicalRam(0, 0x0203, 0x1F);
        byte[] memoryToIo =
        [
            0xC3,
            0x7D, 0x00, 0xC2, 0x03, 0x00,
            0x14,       // WR1: memory A, increment
            0x28,       // WR2: I/O B, fixed
            0xAD, 0xFE, 0x00,
            0x82, 0xCF, 0xB3, 0x87
        ];
        WriteDmaCommand(machine, memoryToIo);
        RunDmaToCompletion(machine);
        Require(machine.PortBus.UlaOutputLatch == 0x1F,
            "The zxnDMA memory-to-I/O transfer did not use the decoded ULA port.");

        machine.MemoryBus.WritePhysicalRam(0, 0x0303, 0x11);
        machine.MemoryBus.WritePhysicalRam(0, 0x0302, 0x22);
        machine.MemoryBus.WritePhysicalRam(0, 0x0301, 0x33);
        machine.MemoryBus.WritePhysicalRam(0, 0x0300, 0x44);
        byte[] decrementingSource =
        [
            0xC3,
            0x7D, 0x03, 0xC3, 0x03, 0x00,
            0x04,       // WR1: memory A, decrement
            0x10,       // WR2: memory B, increment
            0xAD, 0x00, 0x03,
            0x82, 0xCF, 0xB3, 0x87
        ];
        WriteDmaCommand(machine, decrementingSource);
        RunDmaToCompletion(machine);
        byte[] decrementExpected = [0x11, 0x22, 0x33, 0x44];
        for (int index = 0; index < decrementExpected.Length; index++)
        {
            Require(machine.MemoryBus.ReadPhysicalRam(16, 0x0300 + index)
                    == decrementExpected[index],
                "The zxnDMA decrementing source address mode was incorrect.");
        }

        machine.Reset(ZxNextResetKind.Soft);
        Require(!machine.Dma.HasLoadedTransfer
                && !machine.Dma.IsActive
                && machine.Dma.LastTransferLength == 0
                && machine.PortBus.ReadUncontended(ZxNextDmaDevice.ZxnControlPort) == 0x3A,
            "Soft reset did not restore the DMA engine and status byte.");
    }

    private static void VerifyTimedDmaArbitration()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x03);
        byte[] source = [0x11, 0x22, 0x33, 0x44];
        for (int index = 0; index < source.Length; index++)
        {
            machine.MemoryBus.WritePhysicalRam(0, 0x0100 + index, source[index]);
        }

        byte[] timedTransfer =
        [
            0xC3,
            0x7D, 0x00, 0xC1, 0x03, 0x00,
            0x54, 0x00, // Port A: incrementing memory, four CPU clocks.
            0x50, 0x02, // Port B: incrementing memory, two CPU clocks.
            0xAD, 0x00, 0x01,
            0x82, 0xCF, 0xB3, 0x87
        ];
        WriteDmaCommand(machine, timedTransfer);

        Require(machine.Dma.IsActive
                && machine.Dma.BusRequestAsserted
                && machine.Dma.CurrentTransferLength == 0
                && machine.BusArbiter.DmaRequestPending
                && machine.BusArbiter.Owner == ZxNextBusOwner.Cpu
                && machine.MemoryBus.ReadPhysicalRam(16, 0x0100) == 0,
            "Enabling DMA completed data atomically instead of leaving an in-flight BUSREQ.");

        ulong cpuCyclesBefore = machine.Cpu.Cyc;
        Require(machine.StepBusArbitration(),
            "The arbiter did not grant the pending DMA source cycle.");
        Require(machine.MasterTicks == 32
                && machine.BusArbiter.Owner == ZxNextBusOwner.Dma
                && machine.BusArbiter.CpuBusAcknowledged
                && machine.Dma.CurrentTransferLength == 0
                && machine.MemoryBus.ReadPhysicalRam(16, 0x0100) == 0,
            "The four-clock source cycle did not remain read-only for 32 clk28 ticks.");

        Require(machine.StepBusArbitration(),
            "The arbiter did not advance the pending DMA destination cycle.");
        Require(machine.MasterTicks == 48
                && machine.Cpu.Cyc == cpuCyclesBefore
                && machine.Dma.CurrentTransferLength == 1
                && machine.Dma.ByteCounter == 1
                && machine.MemoryBus.ReadPhysicalRam(16, 0x0100) == source[0],
            "The two-clock destination cycle did not commit at its 16-tick boundary.");

        // The FPGA clocks DMA from i_CLK_CPU. A speed change therefore changes the
        // clk28 duration of subsequent DMA states without advancing the stopped Z80.
        machine.Registers.Write(0x07, (byte)ZxNextCpuSpeed.Mhz28);
        RunDmaToCompletion(machine);
        for (int index = 0; index < source.Length; index++)
        {
            Require(machine.MemoryBus.ReadPhysicalRam(16, 0x0100 + index) == source[index],
                $"Timed DMA diverged after the turbo switch at byte {index}.");
        }

        Require(machine.MasterTicks == 66
                && machine.BusArbiter.DmaOwnedMasterTicks == 66
                && machine.BusArbiter.CpuOwnedMasterTicks == 0
                && machine.BusArbiter.DmaGrantCount == 1
                && machine.BusArbiter.Owner == ZxNextBusOwner.Cpu
                && !machine.Dma.IsActive
                && machine.Dma.LastTransferLength == source.Length,
            "DMA ownership, turbo conversion or release timing was incorrect.");

        // StepInstruction must service BUSREQ without allowing the CPU to begin the
        // next instruction. A following call may then resume normal CPU ownership.
        WriteDmaCommand(machine,
        [
            0xC3,
            0x7D, 0x00, 0xC1, 0x00, 0x00,
            0x14,
            0x10,
            0xAD, 0x10, 0x01,
            0x82, 0xCF, 0xB3, 0x87
        ]);
        ushort programCounterBefore = machine.Cpu.PC;
        cpuCyclesBefore = machine.Cpu.Cyc;
        machine.StepInstruction();
        Require(machine.Cpu.PC == programCounterBefore
                && machine.Cpu.Cyc == cpuCyclesBefore
                && !machine.Dma.IsActive
                && machine.MemoryBus.ReadPhysicalRam(16, 0x0110) == source[0],
            "The CPU executed while BUSREQ owned the bus or the granted transfer did not finish.");

        machine.StepInstruction();
        Require(machine.Cpu.Cyc > cpuCyclesBefore
                && machine.BusArbiter.CpuOwnedMasterTicks > 0,
            "The CPU did not resume after DMA released BUSREQ.");

        // Exercise the actual CPU programming path. OTIR has a five-T-state repeat
        // interval after its I/O machine cycle; BUSREQ must be granted before that
        // interval rather than after the complete instruction returns.
        ZxNextMachine guest = ZxNextMachineFactory.CreateFoundationMachine();
        guest.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x03);
        guest.MemoryBus.WritePhysicalRam(0, 0x0100, 0x5A);
        WriteDmaCommand(guest,
        [
            0xC3,
            0x7D, 0x00, 0xC1, 0x00, 0x00,
            0x54, 0x02,
            0x50, 0x02,
            0xAD, 0x00, 0x01,
            0x82, 0xCF, 0xB3
        ]);
        guest.MemoryBus.WritePhysicalRam(0, 0x0300, 0xED);
        guest.MemoryBus.WritePhysicalRam(0, 0x0301, 0xB3); // OTIR
        guest.MemoryBus.WritePhysicalRam(0, 0x0310, 0x87); // WR6 enable
        guest.Cpu.PC = 0xC300;
        guest.Cpu.SetHL(0xC310);
        guest.Cpu.B = 2;
        guest.Cpu.C = ZxNextDmaDevice.ZxnControlPort;
        guest.StepInstruction();
        Require(guest.Cpu.Cyc == 21
                && guest.Cpu.PC == 0xC300
                && guest.BusArbiter.CpuTstatesAtLastDmaGrant == 16
                && guest.BusArbiter.CpuOwnedMasterTicks == 168
                && guest.BusArbiter.DmaOwnedMasterTicks == 32
                && guest.MasterTicks == 200
                && guest.MemoryBus.ReadPhysicalRam(16, 0x0100) == 0x5A,
            "Guest OTIR did not yield at its completed I/O machine cycle before the repeat interval.");

        // Reset must cancel a partially read byte and restore CPU ownership.
        WriteDmaCommand(machine,
        [
            0xC3,
            0x7D, 0x00, 0xC1, 0x00, 0x00,
            0x14,
            0x10,
            0xAD, 0x20, 0x01,
            0x82, 0xCF, 0xB3, 0x87
        ]);
        Require(machine.StepBusArbitration(),
            "The reset vector could not place DMA between source and destination cycles.");
        machine.Reset(ZxNextResetKind.Soft);
        Require(!machine.Dma.IsActive
                && !machine.Dma.HasLoadedTransfer
                && machine.BusArbiter.Owner == ZxNextBusOwner.Cpu
                && machine.BusArbiter.DmaOwnedMasterTicks == 0
                && machine.MasterTicks == 0
                && machine.MemoryBus.ReadPhysicalRam(16, 0x0120) == 0,
            "Soft reset did not cancel an in-flight DMA byte and restore the arbiter.");
    }

    private static void VerifyDmaRasterInterleaving()
    {
        const int red = unchecked((int)0xFFFF0000);
        const int green = unchecked((int)0xFF00FF00);

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        WriteLayer2Palette(machine, 1, 0x1C0);
        WriteLayer2Palette(machine, 2, 0x038);
        for (int index = 0; index < 4; index++)
        {
            machine.MemoryBus.WritePhysicalRam(0, 0x0200 + index, 1);
            WriteLayer2Byte(machine, 8, index, 2);
        }

        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x03);
        machine.Registers.Write(0x07, (byte)ZxNextCpuSpeed.Mhz28);
        machine.Video.Reset();
        ZxNextUlaTimingProfile profile = machine.UlaTiming.Profile;
        ulong firstLayer2PixelTick = checked(
            ((ulong)profile.FirstDisplayLine * (ulong)profile.PixelsPerLine
                + (ulong)profile.DisplayStartHorizontal)
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);

        // With four-clock read and write cycles at 28 MHz, the first byte commits
        // exactly as its pixel begins. Later bytes take eight clk28 ticks apiece while
        // adjacent Layer 2 pixels begin four ticks apart, so the beam outruns the copy.
        ulong leadTick = firstLayer2PixelTick - 8;
        machine.Video.AdvanceMasterTicks(leadTick);
        machine.Clock.AdvanceMasterTicks(leadTick);
        machine.UlaTiming.Advance();
        WriteDmaCommand(machine,
        [
            0xC3,
            0x7D, 0x00, 0xC2, 0x03, 0x00,
            0x54, 0x00,
            0x50, 0x00,
            0xAD, 0x00, 0x00,
            0x82, 0xCF, 0xB3, 0x87
        ]);
        RunDmaToCompletion(machine);

        Require(machine.MasterTicks == firstLayer2PixelTick + 24,
            "The four-byte raster DMA did not occupy thirty-two clk28 ticks.");
        machine.Video.AdvanceMasterTicks(
            profile.MasterTicksPerFrame - machine.MasterTicks);
        int firstPixel = (ZxNextVideoRenderer.UlaTop * ZxNextVideoRenderer.FrameWidth)
            + ZxNextVideoRenderer.UlaLeft;
        Require(machine.Video.FrameBuffer[firstPixel] == red
                && machine.Video.FrameBuffer[firstPixel + 1] == red
                && machine.Video.FrameBuffer[firstPixel + 2] == green
                && machine.Video.FrameBuffer[firstPixel + 3] == green,
            "Layer 2 did not observe old and new DMA bytes on their raster-time boundaries "
            + $"(${machine.Video.FrameBuffer[firstPixel]:X8}, "
            + $"${machine.Video.FrameBuffer[firstPixel + 1]:X8}, "
            + $"${machine.Video.FrameBuffer[firstPixel + 2]:X8}, "
            + $"${machine.Video.FrameBuffer[firstPixel + 3]:X8}).");
    }

    private static void WriteDmaCommand(ZxNextMachine machine, ReadOnlySpan<byte> command)
    {
        for (int index = 0; index < command.Length; index++)
        {
            // OTIR changes the high byte on every output; DMA ports decode A7-A0 only.
            ushort port = (ushort)(((command.Length - index) << 8)
                | ZxNextDmaDevice.ZxnControlPort);
            machine.PortBus.WriteUncontended(port, command[index]);
        }
    }

    private static void RunDmaToCompletion(ZxNextMachine machine)
    {
        int busEvents = 0;
        while (machine.Dma.BusRequestAsserted)
        {
            Require(machine.StepBusArbitration(),
                "The DMA arbiter stopped while BUSREQ was asserted.");
            busEvents++;
            Require(busEvents <= 131_072,
                "The DMA arbiter did not complete within one maximum-sized block.");
        }
    }

    private static void WriteLayer2Palette(
        ZxNextMachine machine,
        byte index,
        ushort color,
        bool priority = false,
        bool secondPalette = false)
    {
        machine.Registers.Write(0x43, secondPalette ? (byte)0x54 : (byte)0x10);
        machine.Registers.Write(0x40, index);
        machine.Registers.Write(0x44, (byte)(color >> 1));
        machine.Registers.Write(0x44, (byte)((color & 0x01) | (priority ? 0x80 : 0)));
    }

    private static void WriteSpritePalette(
        ZxNextMachine machine,
        byte index,
        ushort color,
        bool secondPalette = false)
    {
        machine.Registers.Write(0x43, secondPalette ? (byte)0x60 : (byte)0x20);
        machine.Registers.Write(0x40, index);
        machine.Registers.Write(0x44, (byte)(color >> 1));
        machine.Registers.Write(0x44, (byte)(color & 0x01));
    }

    private static void WriteSpritePattern8(
        ZxNextMachine machine,
        int pattern,
        byte pixel,
        params int[] opaqueOffsets)
    {
        byte[] data = new byte[ZxNextSpriteDevice.PatternSize];
        Array.Fill(data, machine.Sprites.TransparencyIndex);
        foreach (int offset in opaqueOffsets)
        {
            data[offset & 0xFF] = pixel;
        }

        machine.PortBus.WriteUncontended(
            ZxNextSpriteDevice.StatusAndSlotPort,
            (byte)(pattern & 0x3F));
        foreach (byte value in data)
        {
            machine.PortBus.WriteUncontended(ZxNextSpriteDevice.PatternPort, value);
        }
    }

    private static void WriteSolidSpritePattern8(
        ZxNextMachine machine,
        int pattern,
        byte pixel)
    {
        machine.PortBus.WriteUncontended(
            ZxNextSpriteDevice.StatusAndSlotPort,
            (byte)(pattern & 0x3F));
        for (int index = 0; index < ZxNextSpriteDevice.PatternSize; index++)
        {
            machine.PortBus.WriteUncontended(ZxNextSpriteDevice.PatternPort, pixel);
        }
    }

    private static ulong GetFirstSpritePreparationTick(ZxNextUlaTimingProfile profile) =>
        checked(
            ((ulong)(profile.VisibleFirstLine - 1) * (ulong)profile.PixelsPerLine
                + (ulong)(profile.VisibleStartHorizontal - 1))
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);

    private static void WriteSpritePattern4(
        ZxNextMachine machine,
        int pattern,
        byte pixel,
        params int[] opaquePixels)
    {
        byte transparent = (byte)(machine.Sprites.TransparencyIndex & 0x0F);
        byte packedTransparent = (byte)((transparent << 4) | transparent);
        byte[] data = new byte[ZxNextSpriteDevice.PatternSize / 2];
        Array.Fill(data, packedTransparent);
        foreach (int pixelOffset in opaquePixels)
        {
            int address = (pixelOffset & 0xFF) >> 1;
            data[address] = (byte)(((pixelOffset & 0x01) == 0)
                ? ((pixel & 0x0F) << 4) | (data[address] & 0x0F)
                : (data[address] & 0xF0) | (pixel & 0x0F));
        }

        byte selector = (byte)((pattern >> 1) & 0x3F);
        if ((pattern & 0x01) != 0)
        {
            selector |= 0x80;
        }

        machine.PortBus.WriteUncontended(ZxNextSpriteDevice.StatusAndSlotPort, selector);
        foreach (byte value in data)
        {
            machine.PortBus.WriteUncontended(ZxNextSpriteDevice.PatternPort, value);
        }
    }

    private static void WriteSpriteAttributes(
        ZxNextMachine machine,
        int sprite,
        byte attr0,
        byte attr1,
        byte attr2,
        byte attr3,
        byte attr4)
    {
        machine.Registers.Write(0x34, (byte)sprite);
        machine.Registers.Write(0x35, attr0);
        machine.Registers.Write(0x36, attr1);
        machine.Registers.Write(0x37, attr2);
        machine.Registers.Write(0x38, attr3);
        machine.Registers.Write(0x39, attr4);
    }

    private static bool TryGetSpritePixel(
        ZxNextMachine machine,
        int logicalX,
        int logicalY,
        byte control,
        out byte paletteIndex) =>
        machine.Sprites.TryGetPaletteIndex(logicalX, logicalY, control, out paletteIndex);

    private static void SetLayer2Clip(
        ZxNextMachine machine,
        byte x1,
        byte x2,
        byte y1,
        byte y2)
    {
        machine.Registers.Write(0x1C, 1);
        machine.Registers.Write(0x18, x1);
        machine.Registers.Write(0x18, x2);
        machine.Registers.Write(0x18, y1);
        machine.Registers.Write(0x18, y2);
    }

    private static void WriteLayer2Byte(
        ZxNextMachine machine,
        int bank,
        int address,
        byte value)
    {
        int page = (bank * 2) + (address / ZxNextPhysicalMemory.PageSize);
        int offset = address & (ZxNextPhysicalMemory.PageSize - 1);
        Require(machine.MemoryBus.WritePhysicalRam(page, offset, value),
            $"Layer 2 test address ${address:X5} in bank {bank} was not installed.");
    }

    private static void VerifyCycleAccountedUlaRaster()
    {
        const int darkBlue = unchecked((int)0xFF0000B6);
        const int darkRed = unchecked((int)0xFFB60000);
        const int darkGreen = unchecked((int)0xFF00B600);

        ZxNextMachine fetchMachine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextUlaTimingProfile profile = fetchMachine.UlaTiming.Profile;
        fetchMachine.MemoryBus.WritePhysicalRam(10, 0x0000, 0x80);
        fetchMachine.MemoryBus.WritePhysicalRam(10, 0x1800, 0x0A);
        fetchMachine.Video.Reset();

        ulong firstFetchTick = checked(
            ((ulong)profile.FirstDisplayLine * (ulong)profile.PixelsPerLine
                + (ulong)(profile.DisplayStartHorizontal
                    - ZxNextUlaTimingProfile.EvenAttributeFetchLeadPixels))
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel
            + ZxNextUlaTimingProfile.FallingEdgeMasterTick);
        fetchMachine.Video.AdvanceMasterTicks(firstFetchTick);
        fetchMachine.MemoryBus.WritePhysicalRam(10, 0x0000, 0x00);
        fetchMachine.MemoryBus.WritePhysicalRam(10, 0x1800, 0x20);
        fetchMachine.Video.AdvanceMasterTicks(
            profile.MasterTicksPerFrame - firstFetchTick);

        int active = (ZxNextVideoRenderer.UlaTop * ZxNextVideoRenderer.FrameWidth)
            + ZxNextVideoRenderer.UlaLeft;
        Require(fetchMachine.Video.CompletedFrameNumber == 1
                && fetchMachine.Video.FrameBuffer[active] == darkRed
                && fetchMachine.Video.FrameBuffer[active + 2] == darkBlue,
            "Writes after the first ULA fetch changed pixels already owned by the beam.");

        fetchMachine.Video.AdvanceMasterTicks(profile.MasterTicksPerFrame);
        Require(fetchMachine.Video.CompletedFrameNumber == 2
                && fetchMachine.Video.FrameBuffer[active] == darkGreen,
            "A display write made after one frame's fetch was not visible in the next frame.");

        ZxNextMachine splitFetchMachine = ZxNextMachineFactory.CreateFoundationMachine();
        profile = splitFetchMachine.UlaTiming.Profile;
        splitFetchMachine.MemoryBus.WritePhysicalRam(10, 0x0000, 0x80);
        splitFetchMachine.MemoryBus.WritePhysicalRam(10, 0x1800, 0x0A);
        splitFetchMachine.Video.Reset();
        ulong firstPixelFetchTick = checked(
            ((ulong)profile.FirstDisplayLine * (ulong)profile.PixelsPerLine
                + (ulong)(profile.DisplayStartHorizontal
                    - ZxNextUlaTimingProfile.EvenPixelFetchLeadPixels))
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel
            + ZxNextUlaTimingProfile.FallingEdgeMasterTick);
        splitFetchMachine.Video.AdvanceMasterTicks(firstPixelFetchTick);
        splitFetchMachine.MemoryBus.WritePhysicalRam(10, 0x0000, 0x00);
        splitFetchMachine.MemoryBus.WritePhysicalRam(10, 0x1800, 0x04);
        splitFetchMachine.Video.AdvanceMasterTicks(
            profile.MasterTicksPerFrame - firstPixelFetchTick);
        Require(splitFetchMachine.Video.FrameBuffer[active] == darkGreen,
            "A write between the pinned pixel and attribute fetch phases did not retain the old bitmap with the new attribute.");

        ZxNextMachine borderMachine = ZxNextMachineFactory.CreateFoundationMachine();
        borderMachine.PortBus.WriteUncontended(0x00FE, 0x02);
        borderMachine.Video.Reset();
        profile = borderMachine.UlaTiming.Profile;
        ulong visibleStartTick = checked(
            ((ulong)profile.VisibleFirstLine * (ulong)profile.PixelsPerLine
                + (ulong)profile.VisibleStartHorizontal)
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        const int splitLogicalPixel = 10;
        ulong splitTick = visibleStartTick
            + (splitLogicalPixel * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        borderMachine.Video.AdvanceMasterTicks(splitTick);
        borderMachine.PortBus.WriteUncontended(0x00FE, 0x01);
        borderMachine.Video.AdvanceMasterTicks(profile.MasterTicksPerFrame - splitTick);

        int[] borderFrame = borderMachine.Video.FrameBuffer;
        Require(borderFrame[0] == darkRed
                && borderFrame[(splitLogicalPixel * 2) - 1] == darkRed
                && borderFrame[splitLogicalPixel * 2] == darkBlue
                && borderFrame[^1] == darkBlue,
            "A mid-line FE write did not split the border at its cycle-accounted raster coordinate.");
    }

    private static void VerifyCpuDisplayWriteTiming()
    {
        // The real core implements all 16 KiB of bank 5, and the lower 8 KiB
        // of bank 7, in dual-port video BRAM.  Tilemaps can place maps and
        // definitions beyond the legacy 6912-byte ULA display file (RAMTEST
        // places its 1-bpp font at bank-5 offset $1C00).
        ZxNextMachine fullVideoBram = ZxNextMachineFactory.CreateFoundationMachine();
        fullVideoBram.MemoryBus.WritePhysicalRam(10, 0x1E08, 0x80);
        fullVideoBram.MemoryBus.WritePhysicalRam(14, 0x1E08, 0x40);
        Require(fullVideoBram.MemoryBus.ReadDisplayRam(10, 0x1E08, 0) == 0x80
                && fullVideoBram.MemoryBus.ReadDisplayRam(14, 0x1E08, 0) == 0x40,
            "Tilemap definitions outside the legacy ULA display file were not mirrored into video BRAM.");

        ZxNextMachine upperBank5 = ZxNextMachineFactory.CreateFoundationMachine();
        upperBank5.Registers.Write(0x08, 0x50);
        upperBank5.MemoryBus.WritePhysicalRam(11, 0x0123, 0x11);
        upperBank5.MemoryBus.WriteCpu(0x6123, 0x22);
        Require(upperBank5.MemoryBus.ReadDisplayRam(11, 0x0123, 13) == 0x11
                && upperBank5.MemoryBus.ReadDisplayRam(11, 0x0123, 14) == 0x22,
            "The upper 8 KiB of bank 5 did not use the dual-port video-BRAM write boundary.");

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.Registers.Write(0x08, 0x50); // Isolate RAM visibility from compatibility waits.
        machine.MemoryBus.WritePhysicalRam(10, 0x0000, 0x80);
        machine.MemoryBus.WritePhysicalRam(10, 0x1800, 0x0A);
        machine.Video.Reset();

        ZxNextUlaTimingProfile profile = machine.UlaTiming.Profile;
        ulong firstAttributeFetch = checked(
            ((ulong)profile.FirstDisplayLine * (ulong)profile.PixelsPerLine
                + (ulong)(profile.DisplayStartHorizontal
                    - ZxNextUlaTimingProfile.EvenAttributeFetchLeadPixels))
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel
            + ZxNextUlaTimingProfile.FallingEdgeMasterTick);
        ulong writeStart = firstAttributeFetch
            - (firstAttributeFetch % ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate);
        int leadTstates = checked((int)(writeStart
            / ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate));
        machine.Clock.AdvanceCpuTstates(leadTstates);
        machine.Video.AdvanceMasterTicks(writeStart);

        machine.MemoryBus.WriteCpu(0x5800, 0x20);
        Require(machine.MemoryBus.ReadPhysicalRam(10, 0x1800) == 0x20,
            "A CPU display write was not immediately visible to the CPU-facing RAM view.");
        machine.Video.AdvanceMasterTicks(firstAttributeFetch - writeStart);
        Require(machine.FloatingBus.UlaDriving
                && machine.FloatingBus.UlaFetchValue == 0x0A,
            "The ULA observed a CPU attribute write before the T2/BRAM commit edge.");

        ulong secondLineAttributeFetch = firstAttributeFetch
            + ((ulong)profile.PixelsPerLine
                * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        machine.Video.AdvanceMasterTicks(secondLineAttributeFetch - firstAttributeFetch);
        Require(machine.FloatingBus.UlaDriving
                && machine.FloatingBus.UlaFetchValue == 0x20,
            "A completed CPU attribute write was not visible to the following raster fetch.");

        ZxNextMachine paired = ZxNextMachineFactory.CreateFoundationMachine();
        paired.Registers.Write(0x08, 0x50);
        paired.MemoryBus.WritePhysicalRam(10, 0x1800, 0x01);
        paired.MemoryBus.WritePhysicalRam(10, 0x1801, 0x02);
        paired.Video.Reset();
        paired.MemoryBus.WriteCpu(0x5800, 0x11);
        paired.Clock.AdvanceCpuTstates(3);
        paired.MemoryBus.WriteCpu(0x5801, 0x22);
        Require(paired.MemoryBus.ReadDisplayRam(10, 0x1800, 13) == 0x01
                && paired.MemoryBus.ReadDisplayRam(10, 0x1800, 14) == 0x11
                && paired.MemoryBus.ReadDisplayRam(10, 0x1801, 37) == 0x02
                && paired.MemoryBus.ReadDisplayRam(10, 0x1801, 38) == 0x22,
            "Paired CPU writes became video-visible atomically instead of on separate bus cycles.");

        int[] commitTicks = [13, 7, 4, 2];
        foreach (ZxNextCpuSpeed speed in Enum.GetValues<ZxNextCpuSpeed>())
        {
            ZxNextMachine turbo = ZxNextMachineFactory.CreateFoundationMachine();
            turbo.Registers.Write(0x08, 0x50);
            turbo.Registers.Write(0x07, (byte)speed);
            turbo.MemoryBus.WritePhysicalRam(10, 0x1800, 0x33);
            turbo.Video.Reset();
            turbo.MemoryBus.WriteCpu(0x5800, 0x44);
            int commitTick = commitTicks[(int)speed];
            Require(turbo.MemoryBus.ReadDisplayRam(10, 0x1800, (ulong)commitTick) == 0x33
                    && turbo.MemoryBus.ReadDisplayRam(10, 0x1800, (ulong)commitTick + 1UL) == 0x44,
                $"The {turbo.CpuClockHz / 1_000_000.0:0.#} MHz screen write missed its T2/BRAM commit edge.");
        }
    }

    private static void VerifyNextAudio()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        Require(machine.Registers.Read(0x06) == 0xA0
                && machine.Registers.Read(0x08) == 0x90
                && machine.Audio.ActiveChip == 0
                && machine.PortBus.ReadUncontended(0xBFF5) == 0xC0,
            "Next audio reset registers or AY-0 selection are incorrect.");

        machine.PortBus.WriteUncontended(0x00FE, 0x10);
        short[] beeper = new short[256];
        Require(machine.ReadSamples(beeper, 0, beeper.Length) == beeper.Length
                && beeper.Max() >= 6000,
            "The Next $FE EAR bit did not produce a useful sparse-mix host level.");
        machine.PortBus.WriteUncontended(0x00FE, 0x00);
        short[] silence = new short[128];
        machine.ReadSamples(silence, 0, silence.Length);
        Require(silence.All(static sample => sample == 0),
            "Clearing the Next $FE audio bits did not restore silence.");

        machine.Registers.Write(0x06, 0xE0);
        machine.PortBus.WriteUncontended(0x00FE, 0x10);
        machine.ReadSamples(silence, 0, silence.Length);
        Require(silence.All(static sample => sample == 0),
            "NextReg $06 speaker-exclusive mode did not divert the beeper from host audio.");
        machine.Registers.Write(0x06, 0xA0);
        machine.PortBus.WriteUncontended(0x00FE, 0x00);

        WriteAyRegister(machine, 0, 0x20);
        WriteAyRegister(machine, 1, 0x00);
        WriteAyRegister(machine, 7, 0x3E);
        WriteAyRegister(machine, 8, 0x0F);
        machine.PortBus.WriteUncontended(0xFFFD, 8);
        Require(machine.PortBus.ReadUncontended(0xFFFD) == 0x0F
                && machine.PortBus.ReadUncontended(0xBFFD) == 0x0F,
            "AY-0 register readback failed on $FFFD/$BFFD.");
        short[] tone = new short[1024];
        machine.ReadSamples(tone, 0, tone.Length);
        Require(tone.Min() == 0 && tone.Max() >= 4000 && tone.Distinct().Count() > 1,
            "The fixed-clock AY tone generator did not preserve a useful single-channel level.");

        machine.Registers.Write(0x08, 0x12);
        machine.PortBus.WriteUncontended(0xFFFD, 0xFE); // AY 1, both output sides.
        Require(machine.Audio.ActiveChip == 1
                && machine.PortBus.ReadUncontended(0xBFF5) == 0x80,
            "TurboSound did not select AY 1 or expose it through $BFF5.");
        WriteAyRegister(machine, 8, 0x0A);
        machine.PortBus.WriteUncontended(0xFFFD, 0xFD); // AY 2, both output sides.
        Require(machine.Audio.ActiveChip == 2
                && machine.PortBus.ReadUncontended(0xBFF5) == 0x40,
            "TurboSound did not select AY 2 or expose it through $BFF5.");
        machine.PortBus.WriteUncontended(0xFFFD, 0xFF); // AY 0, both output sides.
        Require(machine.Audio.ActiveChip == 0
                && machine.PortBus.ReadUncontended(0xBFF5) == 0xC8,
            "TurboSound did not restore AY 0 and its independent register latch.");

        machine.Registers.Write(0x06, 0xA3);
        machine.PortBus.WriteUncontended(0xFFFD, 8);
        Require(machine.PortBus.ReadUncontended(0xBFFD) == 0,
            "Holding the PSGs in reset did not clear their registers.");

        ZxNextMachine denseMix = ZxNextMachineFactory.CreateFoundationMachine();
        denseMix.Registers.Write(0x08, 0x12);
        foreach (byte chipSelect in new byte[] { 0xFF, 0xFE, 0xFD })
        {
            denseMix.PortBus.WriteUncontended(0xFFFD, chipSelect);
            WriteAyRegister(denseMix, 7, 0x3F);
            WriteAyRegister(denseMix, 8, 0x0F);
            WriteAyRegister(denseMix, 9, 0x0F);
            WriteAyRegister(denseMix, 10, 0x0F);
        }

        denseMix.PortBus.WriteUncontended(0x00FE, 0x18);
        short[] limitedMix = new short[128];
        denseMix.ReadSamples(limitedMix, 0, limitedMix.Length);
        Require(limitedMix.Max() is >= 28000 and < short.MaxValue,
            "The dense three-PSG/beeper mix did not use limiter headroom without hard clipping.");

        ulong minimumDelta = ulong.MaxValue;
        ulong maximumDelta = 0;
        foreach (ZxNextCpuSpeed speed in Enum.GetValues<ZxNextCpuSpeed>())
        {
            ZxNextMachine timed = ZxNextMachineFactory.CreateFoundationMachine();
            timed.Registers.Write(0x07, (byte)speed);
            ulong before = timed.MasterTicks;
            timed.ReadSamples(silence, 0, silence.Length);
            ulong delta = timed.MasterTicks - before;
            minimumDelta = Math.Min(minimumDelta, delta);
            maximumDelta = Math.Max(maximumDelta, delta);
        }

        Require(maximumDelta - minimumDelta <= 32,
            "Audio duration changed with CPU turbo instead of remaining on the 28 MHz clock.");

        var renderedPhase = new AY38912(ZxNextAudioDevice.AyClockHz, 44_100);
        var silentPhase = new AY38912(ZxNextAudioDevice.AyClockHz, 44_100);
        foreach (AY38912 chip in new[] { renderedPhase, silentPhase })
        {
            chip.WriteRegister(0, 0x31);
            chip.WriteRegister(1, 0x02);
            chip.WriteRegister(6, 0x07);
            chip.WriteRegister(7, 0x36);
            chip.WriteRegister(8, 0x0F);
            chip.WriteRegister(11, 0x13);
            chip.WriteRegister(13, 0x0A);
        }

        short[] discardedPhase = new short[997];
        renderedPhase.RenderSamples(discardedPhase.Length, discardedPhase, 0);
        silentPhase.AdvanceSamples(discardedPhase.Length);
        short[] renderedContinuation = new short[257];
        short[] silentContinuation = new short[257];
        renderedPhase.RenderSamples(renderedContinuation.Length, renderedContinuation, 0);
        silentPhase.RenderSamples(silentContinuation.Length, silentContinuation, 0);
        Require(renderedContinuation.SequenceEqual(silentContinuation),
            "Silent PSG advancement did not preserve tone/noise/envelope continuation phase.");
    }

    private static void WriteAyRegister(ZxNextMachine machine, byte register, byte value)
    {
        machine.PortBus.WriteUncontended(0xFFFD, register);
        machine.PortBus.WriteUncontended(0xBFFD, value);
    }

    private static void VerifyDesktopFrameRunnerSeam()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        int publishedFrames = 0;
        machine.FrameCompleted += () => publishedFrames++;
        machine.RunFrame(presentFrame: false);
        Require(machine.UlaTiming.FrameNumber == 1
                && machine.Video.CompletedFrameNumber == 1
                && publishedFrames == 0,
            "A suppressed desktop frame did not advance without publishing.");

        machine.RunFrame(presentFrame: true);
        var frame = new int[machine.FrameWidth * machine.FrameHeight];
        Require(machine.UlaTiming.FrameNumber == 2
                && machine.Video.CompletedFrameNumber == 2
                && publishedFrames == 1
                && machine.TryCopyFrame(frame)
                && frame.AsSpan().SequenceEqual(machine.Video.FrameBuffer),
            "A presented desktop frame did not publish and copy exactly once.");

        int[] firstPublishedBuffer = machine.Video.FrameBuffer;
        machine.RunFrame(presentFrame: true);
        Require(!ReferenceEquals(firstPublishedBuffer, machine.Video.FrameBuffer)
                && machine.Video.CompletedFrameNumber == 3
                && publishedFrames == 2,
            "Completed Next frames did not rotate the synchronized publication buffers.");

        ZxNextMachine copyReference = ZxNextMachineFactory.CreateFoundationMachine();
        copyReference.Video.UseFrameBufferSwap = false;
        int[] fixedPublishedBuffer = copyReference.Video.FrameBuffer;
        copyReference.RunFrame();
        Require(ReferenceEquals(fixedPublishedBuffer, copyReference.Video.FrameBuffer),
            "The diagnostic frame-copy reference path unexpectedly swapped buffers.");

        ZxNextMachine concurrentCopy = ZxNextMachineFactory.CreateFoundationMachine();
        concurrentCopy.Registers.Write(0x68, 0x80);
        concurrentCopy.Registers.Write(0x4A, 0xE0);
        concurrentCopy.RunFrame();
        var copyGate = new ManualResetEventSlim(false);
        int copiedFrames = 0;
        int tornFrames = 0;
        Task copyTask = Task.Run(() =>
        {
            var copied = new int[concurrentCopy.FrameWidth * concurrentCopy.FrameHeight];
            copyGate.Wait();
            for (int copy = 0; copy < 64; copy++)
            {
                concurrentCopy.TryCopyFrame(copied);
                int first = copied[0];
                if (copied.AsSpan().IndexOfAnyExcept(first) >= 0)
                {
                    Interlocked.Exchange(ref tornFrames, 1);
                }

                Interlocked.Increment(ref copiedFrames);
                Thread.Yield();
            }
        });
        copyGate.Set();
        for (int frameIndex = 0; frameIndex < 12; frameIndex++)
        {
            concurrentCopy.Registers.Write(0x4A, (byte)((frameIndex & 1) == 0 ? 0x1C : 0xE0));
            concurrentCopy.RunFrame();
        }

        copyTask.GetAwaiter().GetResult();
        Require(copiedFrames == 64 && tornFrames == 0,
            "Concurrent desktop publication observed a recycled buffer while it was being copied.");

        machine.SetPaused(true);
        machine.RunFrame();
        Require(machine.UlaTiming.FrameNumber == 3 && publishedFrames == 2,
            "A paused desktop Next frame advanced or published.");
    }

    private static void VerifyTilemapRasterPixelBatching()
    {
        ZxNextMachine optimized = CreateTimedTilemapMachine(useRasterPixelBatching: true);
        ZxNextMachine reference = CreateTimedTilemapMachine(useRasterPixelBatching: false);
        ulong targetTicks = checked(optimized.UlaTiming.MasterTicksPerFrame * 2UL);
        int[] chunks = [1, 2, 3, 5, 7, 11, 17, 4, 13];
        ulong advanced = 0;
        int chunkIndex = 0;
        bool changedScroll = false;
        while (advanced < targetTicks)
        {
            if (!changedScroll && advanced >= targetTicks / 3)
            {
                optimized.Registers.Write(0x30, 0x07);
                reference.Registers.Write(0x30, 0x07);
                changedScroll = true;
            }

            ulong chunk = Math.Min((ulong)chunks[chunkIndex], targetTicks - advanced);
            optimized.Video.AdvanceMasterTicks(chunk);
            reference.Video.AdvanceMasterTicks(chunk);
            advanced += chunk;
            chunkIndex = (chunkIndex + 1) % chunks.Length;
        }

        ZxNextVideoMemoryArbiter optimizedArbiter = optimized.VideoMemoryArbiter;
        ZxNextVideoMemoryArbiter referenceArbiter = reference.VideoMemoryArbiter;
        Require(optimized.Video.FrameBuffer.AsSpan().SequenceEqual(reference.Video.FrameBuffer)
                && optimized.Video.CompletedFrameNumber == reference.Video.CompletedFrameNumber
                && optimizedArbiter.UlaGrantCount == referenceArbiter.UlaGrantCount
                && optimizedArbiter.TilemapGrantCount == referenceArbiter.TilemapGrantCount
                && optimizedArbiter.TilemapStallCount == referenceArbiter.TilemapStallCount
                && optimizedArbiter.ScheduledOwner == referenceArbiter.ScheduledOwner
                && optimized.Tilemap.CompletedCharacterFetches
                    == reference.Tilemap.CompletedCharacterFetches
                && optimized.Tilemap.FetchedPixelCount == reference.Tilemap.FetchedPixelCount
                && optimized.Tilemap.TimedPixelDeadlineMisses
                    == reference.Tilemap.TimedPixelDeadlineMisses
                && optimized.Sprites.SpritePreparationMasterTicks
                    == reference.Sprites.SpritePreparationMasterTicks
                && optimized.Sprites.OvertimeCount == reference.Sprites.OvertimeCount,
            "Raster-pixel batching changed the timed tilemap surface, arbitration, "
            + "pipeline, or concurrent sprite clock under partial master-tick chunks.");

        static ZxNextMachine CreateTimedTilemapMachine(bool useRasterPixelBatching)
        {
            ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
            machine.Video.UseTilemapRasterPixelBatching = useRasterPixelBatching;
            machine.Registers.Write(0x6E, 0x20);
            machine.Registers.Write(0x6F, 0x00);
            machine.Registers.Write(0x6C, 0x2E);
            machine.Registers.Write(0x6B, 0xE8);
            machine.Registers.Write(0x68, 0x80);
            for (int address = 0; address < 0x2800; address++)
            {
                int page = 10 + (address / ZxNextPhysicalMemory.PageSize);
                machine.MemoryBus.WritePhysicalRam(
                    page,
                    address & (ZxNextPhysicalMemory.PageSize - 1),
                    (byte)((address * 29 + (address >> 3) + 1) & 0xFF));
            }

            machine.Video.Reset();
            return machine;
        }
    }

    /// <summary>
    /// Skipping through blanking must be invisible: the picture, every arbitration count,
    /// the sprite clock and the frame count have to match the single-pixel loop exactly.
    /// </summary>
    /// <remarks>
    /// The workload is deliberately everything a quiet span has to steer around: the ULA
    /// display, 32 sprites so preparation runs through the blanking the span crosses, and
    /// a tilemap that is switched on part way so the wake position before the visible
    /// window is exercised both ways. The chunk sizes run from a single tick to more than
    /// a line, so spans are cut short and resumed at every alignment a CPU or DMA bus
    /// cycle could produce. The border colour is also changed mid-frame, because the
    /// border is drawn outside the display but inside the visible window, which is
    /// exactly the edge the span must not cross.
    /// </remarks>
    private static void VerifyQuietRasterSpans()
    {
        ZxNextMachine optimized = CreateQuietSpanMachine(useQuietSpans: true);
        ZxNextMachine reference = CreateQuietSpanMachine(useQuietSpans: false);
        ulong targetTicks = checked(optimized.UlaTiming.MasterTicksPerFrame * 3UL);
        int[] chunks = [1, 2, 3, 5, 7, 11, 17, 4, 13, 64, 301, 1000, 1793, 4096, 9000];
        ulong advanced = 0;
        int chunkIndex = 0;
        int stage = 0;
        while (advanced < targetTicks)
        {
            if (stage == 0 && advanced >= targetTicks / 4)
            {
                // 80-column mode, scrolled, so the fetch pipeline leans on the lead pixels
                // before the visible window as hard as it can.
                optimized.Registers.Write(0x6B, 0xC0);
                reference.Registers.Write(0x6B, 0xC0);
                optimized.Registers.Write(0x30, 0x07);
                reference.Registers.Write(0x30, 0x07);
                optimized.Registers.Write(0x31, 0x05);
                reference.Registers.Write(0x31, 0x05);
                stage = 1;
            }
            else if (stage == 1 && advanced >= targetTicks / 2)
            {
                optimized.PortBus.WriteUncontended(0x00FE, 0x05);
                reference.PortBus.WriteUncontended(0x00FE, 0x05);
                stage = 2;
            }
            else if (stage == 2 && advanced >= (targetTicks * 3) / 4)
            {
                optimized.Registers.Write(0x6B, 0x00);
                reference.Registers.Write(0x6B, 0x00);
                stage = 3;
            }

            ulong chunk = Math.Min((ulong)chunks[chunkIndex], targetTicks - advanced);
            optimized.Video.AdvanceMasterTicks(chunk);
            reference.Video.AdvanceMasterTicks(chunk);
            advanced += chunk;
            chunkIndex = (chunkIndex + 1) % chunks.Length;
        }

        Require(optimized.Sprites.SpritePreparationMasterTicks > 0,
            "The quiet-span workload never prepared a sprite, so the sprite clock was not exercised.");
        Require(optimized.VideoMemoryArbiter.TilemapGrantCount > 0,
            "The quiet-span workload never fetched a tile, so the tilemap wake position was not exercised.");

        ZxNextVideoMemoryArbiter optimizedArbiter = optimized.VideoMemoryArbiter;
        ZxNextVideoMemoryArbiter referenceArbiter = reference.VideoMemoryArbiter;
        Require(optimized.Video.CompletedFrameNumber == reference.Video.CompletedFrameNumber,
            $"Quiet raster spans changed the frame count: {optimized.Video.CompletedFrameNumber} against {reference.Video.CompletedFrameNumber}.");
        Require(optimized.Video.FrameBuffer.AsSpan().SequenceEqual(reference.Video.FrameBuffer),
            "Quiet raster spans changed the picture.");
        Require(optimizedArbiter.UlaGrantCount == referenceArbiter.UlaGrantCount
                && optimizedArbiter.TilemapGrantCount == referenceArbiter.TilemapGrantCount
                && optimizedArbiter.TilemapStallCount == referenceArbiter.TilemapStallCount
                && optimizedArbiter.ScheduledOwner == referenceArbiter.ScheduledOwner,
            $"Quiet raster spans changed video memory arbitration: ULA {optimizedArbiter.UlaGrantCount}/{referenceArbiter.UlaGrantCount}, "
            + $"tilemap {optimizedArbiter.TilemapGrantCount}/{referenceArbiter.TilemapGrantCount}, stalls {optimizedArbiter.TilemapStallCount}/{referenceArbiter.TilemapStallCount}.");
        Require(optimized.Tilemap.CompletedCharacterFetches == reference.Tilemap.CompletedCharacterFetches
                && optimized.Tilemap.FetchedPixelCount == reference.Tilemap.FetchedPixelCount
                && optimized.Tilemap.TimedPixelDeadlineMisses == reference.Tilemap.TimedPixelDeadlineMisses,
            "Quiet raster spans changed the tilemap pipeline counters.");
        Require(optimized.Sprites.SpritePreparationMasterTicks == reference.Sprites.SpritePreparationMasterTicks
                && optimized.Sprites.OvertimeCount == reference.Sprites.OvertimeCount,
            $"Quiet raster spans changed the sprite clock: {optimized.Sprites.SpritePreparationMasterTicks} ticks against {reference.Sprites.SpritePreparationMasterTicks}, "
            + $"overtime {optimized.Sprites.OvertimeCount}/{reference.Sprites.OvertimeCount}.");

        static ZxNextMachine CreateQuietSpanMachine(bool useQuietSpans)
        {
            ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
            machine.Video.UseQuietRasterSpans = useQuietSpans;
            for (int address = 0; address < 0x2800; address++)
            {
                int page = 10 + (address / ZxNextPhysicalMemory.PageSize);
                machine.MemoryBus.WritePhysicalRam(
                    page,
                    address & (ZxNextPhysicalMemory.PageSize - 1),
                    (byte)((address * 29 + (address >> 3) + 1) & 0xFF));
            }

            machine.PortBus.WriteUncontended(ZxNextSpriteDevice.StatusAndSlotPort, 0);
            for (int index = 0; index < ZxNextSpriteDevice.PatternSize; index++)
            {
                machine.PortBus.WriteUncontended(ZxNextSpriteDevice.PatternPort, 0x01);
            }

            for (int sprite = 0; sprite < 32; sprite++)
            {
                machine.Registers.Write(0x34, (byte)sprite);
                machine.Registers.Write(0x35, (byte)((sprite * 19) & 0xFF));
                machine.Registers.Write(0x36, (byte)((sprite * 7) % 240));
                machine.Registers.Write(0x37, 0x00);
                machine.Registers.Write(0x38, 0x80);
                machine.Registers.Write(0x39, 0x00);
            }

            machine.Registers.Write(0x15, 0x03);
            machine.Registers.Write(0x6E, 0x20);
            machine.Registers.Write(0x6F, 0x00);
            machine.Registers.Write(0x6C, 0x2E);
            machine.Registers.Write(0x68, 0x80);
            machine.Video.Reset();
            return machine;
        }
    }

    private static void VerifyNextRegResetRequests()
    {
        ZxNextMachine peripheral = CreateProgram(0xED, 0x91, 0x02, 0x80);
        peripheral.StepInstruction();
        Require(peripheral.ResetSequence == 0
                && peripheral.Registers.Read(0x02) == 0x82
                && peripheral.Registers.PeripheralResetAsserted,
            "NextReg $02 bit 7 did not latch peripheral reset independently of CPU reset.");

        ZxNextMachine soft = CreateProgram(0xED, 0x91, 0x02, 0x01);
        soft.MemoryBus.WritePhysicalRam(42, 7, 0xA5);
        soft.StepInstruction();
        Require(soft.ResetSequence == 1 && soft.LastResetKind == ZxNextResetKind.Soft
                && soft.Registers.LastResetKind == ZxNextResetKind.Soft
                && soft.Registers.Read(0x02) == 0x01
                && soft.Cpu.PC == 0 && soft.Cpu.Cyc == 0
                && !soft.MemoryBus.ConfigurationMode && !soft.MemoryBus.BootRomEnabled
                && soft.MemoryBus.ReadPhysicalRam(42, 7) == 0xA5,
            "A guest soft-reset request did not apply at the completed instruction boundary.");

        ZxNextMachine hard = CreateProgram(0xED, 0x91, 0x02, 0x03);
        hard.Registers.Write(0x02, 0x80);
        hard.StepInstruction();
        Require(hard.ResetSequence == 1 && hard.LastResetKind == ZxNextResetKind.Hard
                && hard.Registers.Read(0x02) == 0x02
                && hard.Cpu.PC == 0 && hard.Cpu.Cyc == 0
                && hard.MemoryBus.ConfigurationMode && hard.MemoryBus.BootRomEnabled
                && !hard.Registers.PeripheralResetAsserted,
            "Hard reset did not take precedence or restore hard-reset-only state.");
    }

    private static void VerifyPhysicalRam()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextPhysicalMemory physical = machine.MemoryBus.PhysicalMemory;
        Require(physical.ConfiguredMemoryBytes == 1024 * 1024,
            "The base Next configuration is not 1 MiB.");
        Require(physical.InstalledRamBytes == 768 * 1024 && physical.InstalledRamPageCount == 96,
            "The 256 KiB system reservation was not excluded from guest RAM.");

        Require(machine.MemoryBus.WritePhysicalRam(0, 0, 0x12)
                && machine.MemoryBus.WritePhysicalRam(95, ZxNextPhysicalMemory.PageSize - 1, 0x34),
            "The first or last installed physical page rejected a write.");
        Require(machine.MemoryBus.ReadPhysicalRam(0, 0) == 0x12
                && machine.MemoryBus.ReadPhysicalRam(95, ZxNextPhysicalMemory.PageSize - 1) == 0x34,
            "Direct physical RAM access did not preserve boundary writes.");
        Require(!machine.MemoryBus.WritePhysicalRam(96, 0, 0x56)
                && machine.MemoryBus.ReadPhysicalRam(96, 0) == 0xFF,
            "The first uninstalled physical page did not behave as an open bus.");

        byte[] copy = machine.MemoryBus.CopyPhysicalRamPage(95);
        copy[^1] = 0;
        Require(machine.MemoryBus.ReadPhysicalRam(95, ZxNextPhysicalMemory.PageSize - 1) == 0x34,
            "A physical-page inspection leaked the mutable backing array.");
    }

    private static void VerifySystemAssets()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(), $"zedexess-zxnext-assets-{Guid.NewGuid():N}");
        try
        {
            string machinesDirectory = Path.Combine(temporaryDirectory, "machines", "next");
            string loaderDirectory = Path.Combine(temporaryDirectory, "src", "firmware", "loader");
            Directory.CreateDirectory(machinesDirectory);
            Directory.CreateDirectory(loaderDirectory);

            byte[] mainRom = CreateAssetPattern(ZxNextSystemAssets.MainRomSize, 0x11);
            byte[] alternateRom = CreateAssetPattern(ZxNextSystemAssets.AlternateRomSize, 0x32);
            byte[] divMmcRom = CreateAssetPattern(ZxNextSystemAssets.ExpansionRomSize, 0x53);
            byte[] multifaceRom = CreateAssetPattern(ZxNextSystemAssets.ExpansionRomSize, 0x74);
            byte[] bootLoaderRom = CreateAssetPattern(ZxNextMemoryBus.BootRomSize, 0x95);
            byte[] firmwarePackage = CreateSyntheticFirmwarePackage();

            File.WriteAllBytes(Path.Combine(machinesDirectory, ZxNextSystemAssets.MainRomFileName), mainRom);
            File.WriteAllBytes(
                Path.Combine(machinesDirectory, ZxNextSystemAssets.AlternateRomFileName), alternateRom);
            File.WriteAllBytes(
                Path.Combine(machinesDirectory, ZxNextSystemAssets.DivMmcRomFileName), divMmcRom);
            File.WriteAllBytes(
                Path.Combine(machinesDirectory, ZxNextSystemAssets.MultifaceRomFileName), multifaceRom);
            File.WriteAllBytes(
                Path.Combine(temporaryDirectory, ZxNextSystemAssets.FirmwareFileName), firmwarePackage);
            File.WriteAllBytes(
                Path.Combine(loaderDirectory, ZxNextSystemAssets.CompiledLoaderFileName), bootLoaderRom);

            ZxNextSystemAssets assets = ZxNextSystemAssets.LoadFromDirectory(temporaryDirectory);
            Require(assets.MainRom.Length == ZxNextSystemAssets.MainRomSize
                    && assets.AlternateRom.Length == ZxNextSystemAssets.AlternateRomSize
                    && assets.DivMmcRom.Length == ZxNextSystemAssets.ExpansionRomSize
                    && assets.MultifaceRom.Length == ZxNextSystemAssets.ExpansionRomSize,
                "A correctly sized System/Next ROM was misclassified.");
            Require(assets.HasBootLoaderRom
                    && assets.BootLoaderRom?.Length == ZxNextMemoryBus.BootRomSize,
                "The optional compiled FPGA loader ROM was not discovered.");
            Require(assets.FirmwareBlocks.Count == ZxNextSystemAssets.FirmwareBlockMapEntries
                    && assets.FirmwareBlocks.All(block => block.StartBlock == 0 && block.BlockCount == 1),
                "The TBBLUE.FW block map was not decoded correctly.");
            Require(assets.GetFirmwareBlockData(0).Span.SequenceEqual(
                    firmwarePackage.AsSpan(ZxNextSystemAssets.FirmwareBlockSize)),
                "A decoded TBBLUE.FW module did not expose its mapped payload bytes.");
            Require(assets.CoreUpdatePackage is null,
                "An absent TBBLUE.TBU package was reported as present.");

            string vhdlPath = Path.Combine(temporaryDirectory, "bootrom.vhd");
            File.WriteAllText(vhdlPath, CreateSyntheticBootRomVhdl(bootLoaderRom));
            ZxNextSystemAssets vhdlAssets = ZxNextSystemAssets.LoadFromDirectory(
                temporaryDirectory, vhdlPath);
            Require(vhdlAssets.BootLoaderRomData.Span.SequenceEqual(bootLoaderRom)
                    && vhdlAssets.BootLoaderRom?.Path == Path.GetFullPath(vhdlPath),
                "An explicit VHDL ROM array was not decoded as the optional boot-loader ROM.");

            ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine(assets);
            Require(machine.MemoryBus.Read(0) == bootLoaderRom[0],
                "The optional loader ROM was not applied to the boot overlay.");
            VerifyAppliedAssetBytes(machine, assets);

            using (ZxNextSystemSession desktopSession = ZxNextSystemSession.Create(
                temporaryDirectory,
                vhdlPath))
            {
                Require(!desktopSession.SystemCard.WriteProtected
                        && !desktopSession.SystemCard.WritesBackToFolder
                        && desktopSession.SystemCard.Path == Path.GetFullPath(temporaryDirectory)
                        && desktopSession.Machine.MemoryBus.Read(0) == bootLoaderRom[0],
                    "The desktop session did not own a disposable writable System/Next snapshot and loader ROM.");
            }

            string mainRomPath = Path.Combine(
                machinesDirectory, ZxNextSystemAssets.MainRomFileName);
            File.WriteAllBytes(mainRomPath, new byte[1]);
            RequireAssetLoadFailure<InvalidDataException>(temporaryDirectory,
                "A wrong-sized main ROM was accepted.");
            File.WriteAllBytes(mainRomPath, mainRom);

            firmwarePackage[2] = 2;
            File.WriteAllBytes(
                Path.Combine(temporaryDirectory, ZxNextSystemAssets.FirmwareFileName),
                firmwarePackage);
            RequireAssetLoadFailure<InvalidDataException>(temporaryDirectory,
                "An out-of-range TBBLUE.FW module map was accepted.");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static void VerifyNexLoader()
    {
        var image = new byte[ZxNextNexLoader.HeaderLength + (2 * ZxNextNexLoader.BankLength)];
        "Next"u8.CopyTo(image);
        "V1.1"u8.CopyTo(image.AsSpan(4));
        image[9] = 2;
        image[11] = 3;
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(12, 2), 0xF000);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(14, 2), 0x8000);
        image[18 + 5] = 1;
        image[18 + 0] = 1;
        image[ZxNextNexLoader.HeaderLength] = 0x55;
        image[ZxNextNexLoader.HeaderLength + ZxNextNexLoader.BankLength] = 0xA0;

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextNexLoadResult result = ZxNextNexLoader.Load(machine, image);
        Require(result.Version == "V1.1"
                && result.LoadedBankCount == 2
                && machine.Cpu.PC == 0x8000
                && machine.Cpu.SP == 0xF000
                && machine.Cpu.IY == 0x5C3A
                && machine.Cpu.BC == 0
                && machine.Cpu.HL == 0x8000
                && machine.Cpu.A == 0x80
                && machine.Cpu.GetFlags() == 0x80
                && machine.CpuSpeed == ZxNextCpuSpeed.Mhz28
                && machine.MemoryBus.ReadPhysicalRam(10, 0) == 0x55
                && machine.MemoryBus.ReadPhysicalRam(0, 0) == 0xA0,
            "A valid screenless NEX image did not restore its banks and entry state.");

        const int layer2ScreenBytes = 3 * ZxNextNexLoader.BankLength;
        var screened = new byte[
            ZxNextNexLoader.HeaderLength + 512 + layer2ScreenBytes + ZxNextNexLoader.BankLength];
        "Next"u8.CopyTo(screened);
        "V1.1"u8.CopyTo(screened.AsSpan(4));
        screened[9] = 1;
        screened[10] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(screened.AsSpan(12, 2), 0xF000);
        BinaryPrimitives.WriteUInt16LittleEndian(screened.AsSpan(14, 2), 0xC000);
        screened[18] = 1;
        int screenOffset = ZxNextNexLoader.HeaderLength + 512;
        screened[screenOffset] = 0x6D;
        int bankOffset = screenOffset + layer2ScreenBytes;
        screened[bankOffset] = 0xC9;

        ZxNextMachine loadingScreen = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextNexLoadResult screenedResult = ZxNextNexLoader.Load(loadingScreen, screened);
        Require(screenedResult.LoadedBankCount == 1
                && loadingScreen.Layer2.DisplayEnabled
                && loadingScreen.MemoryBus.ReadPhysicalRam(18, 0) == 0x6D
                && loadingScreen.MemoryBus.ReadPhysicalRam(0, 0) == 0xC9,
            "A standard Layer 2 loading-screen payload displaced or corrupted its following bank data.");

        ZxNextMachine transactional = ZxNextMachineFactory.CreateFoundationMachine();
        transactional.MemoryBus.WritePhysicalRam(10, 0, 0xCC);
        byte[] truncated = image[..^1];
        bool rejected = false;
        try
        {
            _ = ZxNextNexLoader.Load(transactional, truncated);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Require(rejected
                && transactional.MemoryBus.ReadPhysicalRam(10, 0) == 0xCC
                && transactional.Cpu.PC == 0,
            "A truncated NEX image changed machine state before validation completed.");
    }

    private static void VerifyZxNextSnapshotLoader()
    {
        var snapshot = new byte[ZxNextSnapshotLoader.SnapshotLength];
        snapshot[0] = 0x3F;
        snapshot[19] = 0x04;
        snapshot[20] = 0x55;
        snapshot[21] = 0xA5;
        snapshot[22] = 0x5A;
        snapshot[23] = 0x00;
        snapshot[24] = 0x80;
        snapshot[25] = 2;
        snapshot[26] = 6;
        int stackOffset = ZxNextSnapshotLoader.HeaderLength + 0x4000;
        snapshot[stackOffset] = 0x00;
        snapshot[stackOffset + 1] = 0x90;
        snapshot[ZxNextSnapshotLoader.HeaderLength] = 0x5A;
        snapshot[ZxNextSnapshotLoader.HeaderLength + 0x8002] = 0xA5;

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextSnapshotLoader.Load48K(machine, snapshot);
        Require(machine.Cpu.PC == 0x9000
                && machine.Cpu.SP == 0x8002
                && machine.Cpu.A == 0x5A
                && machine.Cpu.GetFlags() == 0xA5
                && machine.Cpu.I == 0x3F
                && machine.Cpu.R == 0x55
                && machine.Cpu.Iff1
                && machine.Cpu.Iff2
                && machine.Cpu.InterruptModeValue == 2
                && machine.MemoryBus.ReadPhysicalRam(10, 0) == 0x5A
                && machine.MemoryBus.ReadPhysicalRam(0, 2) == 0xA5
                && machine.PortBus.UlaOutputLatch == 6,
            "The 48K SNA/SNX loader did not restore CPU, RAM and border state.");
    }

    private static void RequireAssetLoadFailure<TException>(string directory, string message)
        where TException : Exception
    {
        try
        {
            _ = ZxNextSystemAssets.LoadFromDirectory(directory);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void VerifySuppliedSystemAssets(
        string systemDirectory,
        string? bootRomPath,
        TextWriter writer)
    {
        ZxNextSystemAssets assets = ZxNextSystemAssets.LoadFromDirectory(systemDirectory, bootRomPath);
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine(assets);
        VerifyAppliedAssetBytes(machine, assets);

        writer.WriteLine($"  System/Next root: {assets.RootDirectory}");
        WriteAsset(assets.MainRom);
        WriteAsset(assets.AlternateRom);
        WriteAsset(assets.DivMmcRom);
        WriteAsset(assets.MultifaceRom);
        WriteAsset(assets.FirmwarePackage);
        if (assets.CoreUpdatePackage is not null)
        {
            WriteAsset(assets.CoreUpdatePackage);
        }

        writer.WriteLine(assets.BootLoaderRom is null
            ? "  FPGA boot-loader ROM: not present (loader source is not a compiled ROM)"
            : $"  FPGA boot-loader ROM: {assets.BootLoaderRom.Path} " +
              $"({assets.BootLoaderRom.Length} bytes, SHA-256 {assets.BootLoaderRom.Sha256})");
        foreach (ZxNextFirmwareBlock block in assets.FirmwareBlocks)
        {
            writer.WriteLine(
                $"  TBBLUE.FW[{block.Index}] {block.Name}: " +
                $"payload block {block.StartBlock}, count {block.BlockCount}, " +
                $"file offset {block.FileOffset}");
        }

        if (assets.HasBootLoaderRom)
        {
            VerifyDesktopSystemSession(assets, writer);
            VerifyBootLoaderFirmwareHandoff(assets, writer);
            VerifyCompleteSystemFirmwareBoot(assets, writer);
        }

        void WriteAsset(ZxNextSystemAssetInfo asset) =>
            writer.WriteLine(
                $"  {asset.Role}: {asset.Path} ({asset.Length} bytes, SHA-256 {asset.Sha256})");
    }

    private static void DiagnoseSuppliedSnxSnapshot(
        ZxNextSystemAssets assets,
        ZxNextMachine machine,
        TextWriter writer)
    {
        string path = Path.Combine(
            assets.RootDirectory,
            "demos",
            "OnlyYou6ch",
            "6ch-OnlyYou.snx");
        if (!File.Exists(path))
        {
            writer.WriteLine("  SNX diagnostic: Only You snapshot not present in supplied tree");
            return;
        }

        byte[] snapshot = File.ReadAllBytes(path);
        ZxNextSnapshotLoader.Load48K(machine, snapshot);
        // The 48K SNA form used by SNX has no paging field.  SPECTRUM selects
        // the 48K personality before resuming it; mirror that transition here.
        machine.PortBus.WriteNextRegister(0x8E, 0x0B);
        ushort entryPc = machine.Cpu.PC;
        ulong entryCycles = machine.Cpu.Cyc;
        var samples = new short[machine.SampleRate * 2];
        machine.ReadSamples(samples, 0, samples.Length);

        int peak = 0;
        double squareSum = 0;
        foreach (short sample in samples)
        {
            int magnitude = Math.Abs((int)sample);
            peak = Math.Max(peak, magnitude);
            squareSum += (double)sample * sample;
        }

        int[] frame = new int[ZxNextVideoRenderer.FrameWidth * ZxNextVideoRenderer.FrameHeight];
        machine.Video.CopyFrame(frame);
        int firstColour = frame[0];
        int differingPixels = 0;
        foreach (int pixel in frame)
        {
            if (pixel != firstColour)
            {
                differingPixels++;
            }
        }

        double rms = Math.Sqrt(squareSum / samples.Length);
        Require(machine.Cpu.Cyc > entryCycles
                && machine.Cpu.PC != entryPc
                && machine.UlaTiming.FrameNumber > 0,
            "The supplied Only You SNX did not continue executing after snapshot restore.");
        writer.WriteLine(
            $"  SNX diagnostic: Only You ${entryPc:X4}->${machine.Cpu.PC:X4}, " +
            $"{machine.Cpu.Cyc - entryCycles} T-states, {machine.UlaTiming.FrameNumber} frames, " +
            $"audio peak {peak}, RMS {rms:F1}, non-uniform frame pixels {differingPixels}, " +
            $"TurboSound {(machine.Audio.TurboSoundEnabled ? "enabled" : "disabled")}");
    }

    private static void DiagnoseSuppliedHottNex(
        ZxNextSystemAssets assets,
        ZxNextMachine machine,
        TextWriter writer)
    {
        string path = Path.Combine(
            assets.RootDirectory,
            "games",
            "Next",
            "Halls of The Things",
            "hott.nex");
        if (!File.Exists(path))
        {
            writer.WriteLine("  NEX diagnostic: Halls of the Things image not present in supplied tree");
            return;
        }

        ZxNextNexLoadResult result = ZxNextNexLoader.Load(machine, File.ReadAllBytes(path));
        ushort entryPc = machine.Cpu.PC;
        for (int frame = 0; frame < 120; frame++)
        {
            machine.RunFrame(presentFrame: false);
        }

        machine.Keyboard.SetKeyState(SpectrumKey.D1, pressed: true);
        try
        {
            for (int frame = 0; frame < 3; frame++)
            {
                machine.RunFrame(presentFrame: false);
            }
        }
        finally
        {
            machine.Keyboard.SetKeyState(SpectrumKey.D1, pressed: false);
        }

        int gameFrames = 0;
        while (gameFrames < 180 && !machine.Tilemap.Enabled)
        {
            machine.RunFrame(presentFrame: false);
            gameFrames++;
        }

        ulong grantsBeforeSteadyState = machine.VideoMemoryArbiter.TilemapGrantCount;
        ulong stallsBeforeSteadyState = machine.VideoMemoryArbiter.TilemapStallCount;
        ulong missesBeforeSteadyState = machine.Tilemap.TimedPixelDeadlineMisses;
        for (int frame = 0; frame < 30; frame++)
        {
            machine.RunFrame(presentFrame: frame == 29);
        }

        ulong steadyStateGrants = machine.VideoMemoryArbiter.TilemapGrantCount
            - grantsBeforeSteadyState;
        ulong steadyStateStalls = machine.VideoMemoryArbiter.TilemapStallCount
            - stallsBeforeSteadyState;
        ulong steadyStateMisses = machine.Tilemap.TimedPixelDeadlineMisses
            - missesBeforeSteadyState;

        int[] framePixels = new int[ZxNextVideoRenderer.FrameWidth * ZxNextVideoRenderer.FrameHeight];
        machine.Video.CopyFrame(framePixels);
        var colors = new HashSet<int>(framePixels);
        int first = framePixels[0];
        int differingPixels = framePixels.Count(pixel => pixel != first);
        Require(machine.Cpu.PC != entryPc
                && machine.Tilemap.Enabled
                && machine.Registers.Read(0x6B) == 0xE0
                && machine.Registers.Read(0x68) == 0x80
                && colors.Count > 2
                && differingPixels > 1_000
                && steadyStateGrants > 0
                && steadyStateStalls > 0
                && steadyStateMisses == 0,
            $"The supplied Halls NEX did not reach a visible tilemap frame " +
            $"(PC=${machine.Cpu.PC:X4}, control=${machine.Registers.Read(0x6B):X2}, " +
            $"colors={colors.Count}, changed pixels={differingPixels}, " +
            $"steady grants={steadyStateGrants}, stalls={steadyStateStalls}, " +
            $"fallbacks={steadyStateMisses}).");
        writer.WriteLine(
            $"  NEX diagnostic: Halls {result.Version}, {result.LoadedBankCount} banks, " +
            $"${entryPc:X4}->${machine.Cpu.PC:X4}, tilemap control " +
            $"${machine.Registers.Read(0x6B):X2}, map ${machine.Registers.Read(0x6E):X2}, " +
            $"tiles ${machine.Registers.Read(0x6F):X2}, colors {colors.Count}, " +
            $"non-background pixels {differingPixels}, entered after {gameFrames} frames, " +
            $"steady tile grants {steadyStateGrants}, ULA stalls {steadyStateStalls}, " +
            $"deadline fallbacks {steadyStateMisses} " +
            $"(startup total {machine.Tilemap.TimedPixelDeadlineMisses})");
    }

    private static void DiagnoseSuppliedWarhawkNex(
        ZxNextSystemAssets assets,
        ZxNextMachine machine,
        TextWriter writer)
    {
        string path = Path.Combine(
            assets.RootDirectory,
            "games",
            "Next",
            "Warhawk",
            "Warhawk.nex");
        if (!File.Exists(path))
        {
            writer.WriteLine("  NEX diagnostic: Warhawk image not present in supplied tree");
            return;
        }

        bool automapBeforeLoad = machine.DivMmc.AutomapEnabled;
        ZxNextNexLoadResult result = ZxNextNexLoader.Load(machine, File.ReadAllBytes(path));
        bool automapAtEntry = machine.DivMmc.AutomapEnabled;
        ushort entryPc = machine.Cpu.PC;
        ulong initialResetSequence = machine.ResetSequence;
        string? firstLowMemoryTransition = null;
        int lowMemoryTransitionCount = 0;
        int startupTraceInstructions = 0;
        while (startupTraceInstructions < 5_000_000
            && !(machine.Cpu.IsHalted && machine.Cpu.PC is 0x0C8F or 0x0C90))
        {
            ushort previousPc = machine.Cpu.PC;
            ushort previousSp = machine.Cpu.SP;
            machine.StepInstruction();
            startupTraceInstructions++;
            if (previousPc >= 0x4000 && machine.Cpu.PC < 0x4000)
            {
                lowMemoryTransitionCount++;
                firstLowMemoryTransition ??=
                    $"${previousPc:X4}->${machine.Cpu.PC:X4}/SP${previousSp:X4}->${machine.Cpu.SP:X4}";
            }
        }

        int resetFrame = -1;
        var sampledPcs = new Dictionary<ushort, int>();
        var frameHashes = new HashSet<ulong>();
        for (int frame = 0; frame < 180; frame++)
        {
            machine.RunFrame(presentFrame: frame % 30 == 29);
            if (resetFrame < 0 && machine.ResetSequence != initialResetSequence)
            {
                resetFrame = frame + 1;
            }
            sampledPcs[machine.Cpu.PC] = sampledPcs.GetValueOrDefault(machine.Cpu.PC) + 1;
            if (frame % 30 == 29)
            {
                ulong frameHash = 14_695_981_039_346_656_037UL;
                foreach (int pixel in machine.Video.FrameBuffer)
                {
                    frameHash ^= unchecked((uint)pixel);
                    frameHash *= 1_099_511_628_211UL;
                }

                frameHashes.Add(frameHash);
            }
        }

        string commonPcs = string.Join(
            ", ",
            sampledPcs
                .OrderByDescending(pair => pair.Value)
                .Take(5)
                .Select(pair => $"${pair.Key:X4}:{pair.Value}"));
        writer.WriteLine(
            $"  NEX diagnostic: Warhawk {result.Version}, {result.LoadedBankCount} banks, " +
            $"${entryPc:X4}->${machine.Cpu.PC:X4}, 180 frames, sampled hashes {frameHashes.Count}, " +
            $"reset frame {resetFrame}, reset sequence {initialResetSequence}->{machine.ResetSequence}/" +
            $"{machine.LastResetKind}, " +
            $"HALT={(machine.Cpu.IsHalted ? 1 : 0)}, IFF={(machine.Cpu.Iff1 ? 1 : 0)}, " +
            $"line ${machine.Registers.LineInterruptValue:X3}/" +
            $"{(machine.Registers.LineInterruptEnabled ? "on" : "off")}, " +
            $"line IRQs {machine.UlaTiming.LineInterruptCount}, " +
            $"Layer2={(machine.Layer2.DisplayEnabled ? 1 : 0)}, " +
            $"tilemap={(machine.Tilemap.Enabled ? 1 : 0)}, sprites=${machine.Registers.Read(0x15):X2}, " +
            $"automap={(automapBeforeLoad ? 1 : 0)}->{(automapAtEntry ? 1 : 0)}/" +
            $"{(machine.DivMmc.AutomapEnabled ? 1 : 0)}, " +
            $"startup trace={startupTraceInstructions}, first low transition " +
            $"[{firstLowMemoryTransition ?? "none"}], low transition count " +
            $"{lowMemoryTransitionCount}, common PCs [{commonPcs}]");
    }

    private static void DiagnoseSuppliedBrowserWarhawkNex(
        ZxNextSystemAssets assets,
        TextWriter writer)
    {
        string hostPath = Path.Combine(
            assets.RootDirectory,
            "games",
            "Next",
            "Warhawk",
            "Warhawk.nex");
        if (!File.Exists(hostPath))
        {
            writer.WriteLine("  Browser NEX diagnostic: Warhawk image not present in supplied tree");
            return;
        }

        string bootRomPath = assets.BootLoaderRom?.Path
            ?? throw new InvalidOperationException("The Browser NEX diagnostic requires a loader ROM.");
        ReadOnlyMemory<byte> image = File.ReadAllBytes(hostPath);
        ReadOnlyMemory<byte> autoexec = CreatePostWelcomeNexAutoexec(
            assets.RootDirectory,
            "c:/games/Next/Warhawk/Warhawk.nex");
        var overlay = new Dictionary<string, ReadOnlyMemory<byte>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["warhawk.nex"] = image,
            ["nextzxos/warhawk.nex"] = image,
            ["nextzxos/autoexec.1st"] = autoexec
        };

        using ZxNextSystemSession session = ZxNextSystemSession.Create(
            assets.RootDirectory,
            bootRomPath,
            overlay);
        ZxNextMachine machine = session.Machine;
        const ushort entryPc = 0x5C50;
        const int maximumStartupInstructions = 50_000_000;
        int startupInstructions = 0;
        while (startupInstructions < maximumStartupInstructions
            && !(machine.Cpu.IsHalted && machine.Cpu.PC is 0x0C8F or 0x0C90))
        {
            machine.StepInstruction();
            startupInstructions++;
        }

        if (!(machine.Cpu.IsHalted && machine.Cpu.PC is 0x0C8F or 0x0C90)
            || !PressGuestChord(machine, [SpectrumKey.Enter]))
        {
            writer.WriteLine(
                $"  Browser NEX diagnostic: NextZXOS did not accept its bootstrap " +
                $"AUTOEXEC Enter (PC=${machine.Cpu.PC:X4}, instructions={startupInstructions})");
            return;
        }

        for (int bootstrapCommand = 1;
            bootstrapCommand < 3 && ReadGuestWord(machine, 0x5C45) != 120;
            bootstrapCommand++)
        {
            int commandInstructions = 0;
            while (commandInstructions < 3_000_000
                && ReadGuestWord(machine, 0x5C45) != 120
                && !(machine.Cpu.IsHalted && machine.Cpu.PC is 0x0C8F or 0x0C90))
            {
                machine.StepInstruction();
                commandInstructions++;
                startupInstructions++;
            }

            if (ReadGuestWord(machine, 0x5C45) != 120
                && machine.Cpu.IsHalted
                && machine.Cpu.PC is 0x0C8F or 0x0C90)
            {
                _ = PressGuestChord(machine, [SpectrumKey.Enter]);
            }
        }

        while (startupInstructions < maximumStartupInstructions
            && !(ReadGuestWord(machine, 0x5C45) == 120
                && machine.Cpu.IsHalted
                && machine.Cpu.PC is 0x0C8F or 0x0C90))
        {
            machine.StepInstruction();
            startupInstructions++;
        }

        if (!(ReadGuestWord(machine, 0x5C45) == 120
                && machine.Cpu.IsHalted
                && machine.Cpu.PC is 0x0C8F or 0x0C90)
            || !PressGuestChord(machine, [SpectrumKey.Space]))
        {
            writer.WriteLine(
                $"  Browser NEX diagnostic: NextZXOS did not accept the Welcome Space key " +
                $"(PC=${machine.Cpu.PC:X4}, startup instructions={startupInstructions})");
            return;
        }

        const int maximumLoadInstructions = 30_000_000;
        int loadInstructions = 0;
        bool entered = false;
        bool nexloadEntered = false;
        int nexloadInstructions = 0;
        var loadPcs = new Dictionary<ushort, int>();
        while (loadInstructions < maximumLoadInstructions && !entered)
        {
            ushort pc = machine.Cpu.PC;
            if (pc is >= 0x8000 and < 0x8A00)
            {
                nexloadEntered = true;
                nexloadInstructions++;
            }

            if ((loadInstructions & 0x3FFF) == 0)
            {
                loadPcs[pc] = loadPcs.GetValueOrDefault(pc) + 1;
            }

            entered = pc == entryPc;
            if (!entered)
            {
                machine.StepInstruction();
                loadInstructions++;
            }
        }

        if (!entered)
        {
            string commonLoadPcs = string.Join(
                ", ",
                loadPcs
                    .OrderByDescending(pair => pair.Value)
                    .Take(8)
                    .Select(pair => $"${pair.Key:X4}:{pair.Value}"));
            writer.WriteLine(
                $"  Browser NEX diagnostic: post-Welcome .nexload did not enter Warhawk at ${entryPc:X4} " +
                $"within {maximumLoadInstructions} instructions (PC=${machine.Cpu.PC:X4}, " +
                $"HALT={(machine.Cpu.IsHalted ? 1 : 0)}, LAST_K=${machine.MemoryBus.Read(0x5C08):X2}, " +
                $"ERR_NR=${machine.MemoryBus.Read(0x5C3A):X2}, " +
                $"NEXLOAD={(nexloadEntered ? "entered" : "not-entered")}/" +
                $"{nexloadInstructions} instructions, " +
                $"last NextReg=${machine.Registers.LastWrittenRegister:X2}/" +
                $"${machine.Registers.LastWrittenValue:X2}, sampled PCs [{commonLoadPcs}])");
            return;
        }

        ulong initialResetSequence = machine.ResetSequence;
        ulong initialLineInterrupts = machine.UlaTiming.LineInterruptCount;
        var sampledPcs = new Dictionary<ushort, int>();
        var frameHashes = new HashSet<ulong>();
        var lastRegisterPairs = new HashSet<ushort>();
        for (int frame = 0; frame < 300; frame++)
        {
            ulong targetFrame = machine.UlaTiming.FrameNumber + 1;
            while (machine.UlaTiming.FrameNumber < targetFrame)
            {
                machine.StepInstruction();
                if (machine.Registers.LastWrittenRegister is byte register)
                {
                    lastRegisterPairs.Add((ushort)((register << 8)
                        | machine.Registers.LastWrittenValue));
                }
            }

            sampledPcs[machine.Cpu.PC] = sampledPcs.GetValueOrDefault(machine.Cpu.PC) + 1;
            if (frame % 25 == 24)
            {
                ulong frameHash = 14_695_981_039_346_656_037UL;
                foreach (int pixel in machine.Video.FrameBuffer)
                {
                    frameHash ^= unchecked((uint)pixel);
                    frameHash *= 1_099_511_628_211UL;
                }

                frameHashes.Add(frameHash);
            }
        }

        string commonPcs = string.Join(
            ", ",
            sampledPcs
                .OrderByDescending(pair => pair.Value)
                .Take(8)
                .Select(pair => $"${pair.Key:X4}:{pair.Value}"));
        string relevantWrites = string.Join(
            ", ",
            lastRegisterPairs
                .Where(pair => ((pair >> 8) is 0x15 or 0x22 or 0x23 or 0x60 or 0x61
                    or 0x62 or 0x63 or 0x64 or 0x69 or 0x6B or 0xC0 or 0xC4))
                .OrderBy(pair => pair)
                .Select(pair => $"${pair >> 8:X2}=${pair & 0xFF:X2}"));
        writer.WriteLine(
            $"  Browser NEX diagnostic: Warhawk entered ${entryPc:X4} after " +
            $"{loadInstructions} instructions; after 300 frames PC=${machine.Cpu.PC:X4}, " +
            $"hashes={frameHashes.Count}, reset={initialResetSequence}->{machine.ResetSequence}, " +
            $"HALT={(machine.Cpu.IsHalted ? 1 : 0)}, IFF={(machine.Cpu.Iff1 ? 1 : 0)}, " +
            $"line=${machine.Registers.LineInterruptValue:X3}/" +
            $"{(machine.Registers.LineInterruptEnabled ? "on" : "off")}, " +
            $"line IRQs={machine.UlaTiming.LineInterruptCount - initialLineInterrupts}, " +
            $"Layer2={(machine.Layer2.DisplayEnabled ? 1 : 0)}, " +
            $"tilemap={(machine.Tilemap.Enabled ? 1 : 0)}, " +
            $"common PCs [{commonPcs}], relevant NextRegs [{relevantWrites}]");
    }

    private static bool PressGuestChord(
        ZxNextMachine machine,
        ReadOnlySpan<SpectrumKey> keys,
        int maximumInstructions = 1_000_000)
    {
        foreach (SpectrumKey key in keys)
        {
            machine.Keyboard.SetKeyState(key, pressed: true);
        }

        bool accepted = false;
        try
        {
            for (int instructions = 0; instructions < maximumInstructions && !accepted; instructions++)
            {
                ushort previousPc = machine.Cpu.PC;
                machine.StepInstruction();
                accepted = previousPc == 0x0C95
                    && machine.Cpu.PC == 0x0C97
                    && machine.MemoryBus.CurrentRomBank == 0
                    && !machine.DivMmc.IsMapped;
            }
        }
        finally
        {
            foreach (SpectrumKey key in keys)
            {
                machine.Keyboard.SetKeyState(key, pressed: false);
            }
        }

        return accepted;
    }

    private static byte[] CreatePostWelcomeNexAutoexec(
        string systemRoot,
        string nexFileName)
    {
        // A tree whose welcome screen has been disabled keeps the same program under its
        // original name, so it still serves as the file to patch.
        string path = Path.Combine(systemRoot, "nextzxos", "autoexec.1st");
        if (!File.Exists(path))
        {
            path = Path.Combine(systemRoot, "nextzxos", "welcome.bas");
        }

        byte[] original = File.ReadAllBytes(path);
        if (original.Length < 128
            || !original.AsSpan(0, 9).SequenceEqual("PLUS3DOS\x1A"u8))
        {
            throw new InvalidDataException("The supplied AUTOEXEC.1ST is not a +3DOS BASIC file.");
        }

        int insertOffset = -1;
        int position = 128;
        while (position + 4 <= original.Length)
        {
            int lineNumber = (original[position] << 8) | original[position + 1];
            int lineLength = original[position + 2] | (original[position + 3] << 8);
            if (lineNumber >= 200)
            {
                insertOffset = position;
                break;
            }

            position += 4 + lineLength;
        }

        if (insertOffset < 0)
        {
            throw new InvalidDataException("The supplied AUTOEXEC.1ST has no line 200 insertion point.");
        }

        byte[] body = Encoding.ASCII.GetBytes($".nexload {nexFileName}\r");
        var line = new byte[4 + body.Length];
        line[0] = 0x00;
        line[1] = 0xC3; // Line 195, immediately before the menu invocation at line 200.
        BinaryPrimitives.WriteUInt16LittleEndian(line.AsSpan(2, 2), (ushort)body.Length);
        body.CopyTo(line, 4);

        var result = new byte[original.Length + line.Length];
        original.AsSpan(0, insertOffset).CopyTo(result);
        line.CopyTo(result, insertOffset);
        original.AsSpan(insertOffset).CopyTo(result.AsSpan(insertOffset + line.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(11, 4), (uint)result.Length);
        ushort programLength = checked((ushort)(result.Length - 128));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(16, 2), programLength);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20, 2), programLength);
        byte checksum = 0;
        for (int index = 0; index < 127; index++)
        {
            checksum ^= result[index];
        }

        result[127] = checksum;
        return result;
    }

    private static ushort ReadGuestWord(ZxNextMachine machine, ushort address)
    {
        byte low = machine.MemoryBus.Read(address);
        byte high = machine.MemoryBus.Read((ushort)(address + 1));
        return (ushort)(low | (high << 8));
    }


    private static void VerifyDesktopSystemSession(
        ZxNextSystemAssets assets,
        TextWriter writer)
    {
        string bootRomPath = assets.BootLoaderRom?.Path
            ?? throw new InvalidOperationException("The desktop boot check requires a loader ROM.");
        using ZxNextSystemSession session = ZxNextSystemSession.Create(
            assets.RootDirectory,
            bootRomPath);
        ZxNextMachine machine = session.Machine;
        int publishedFrames = 0;
        machine.FrameCompleted += () => publishedFrames++;
        const int maximumInstructions = 2_000_000;
        int instructions = 0;
        while (instructions < maximumInstructions && machine.Cpu.PC != 0x6000)
        {
            machine.StepInstruction();
            instructions++;
        }

        Require(machine.Cpu.PC == 0x6000
                && publishedFrames > 0
                && machine.Video.CompletedFrameNumber == machine.UlaTiming.FrameNumber,
            "The desktop System/Next session did not publish frames while reaching firmware.");
        writer.WriteLine(
            $"  Desktop milestone: disposable writable System/Next session published {publishedFrames} " +
            $"frames and reached $6000 after {instructions} instructions");
    }

    private static void VerifyBootLoaderFirmwareHandoff(
        ZxNextSystemAssets assets,
        TextWriter writer)
    {
        string stagingDirectory = Path.Combine(
            Path.GetTempPath(), $"zedexess-zxnext-boot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        SpectrumDivMmcSdCard? sdCard = null;
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine(assets);
        try
        {
            File.WriteAllBytes(
                Path.Combine(stagingDirectory, ZxNextSystemAssets.FirmwareFileName),
                assets.FirmwarePackageData.ToArray());
            sdCard = SpectrumDivMmcSdCard.OpenFolderBacked(stagingDirectory, writeProtected: true);
            machine.DivMmc.AttachSdCard(0, sdCard);

            const int maximumInstructions = 2_000_000;
            int instructions = 0;
            while (instructions < maximumInstructions && machine.Cpu.PC != 0x6000)
            {
                machine.StepInstruction();
                instructions++;
            }

            ReadOnlySpan<byte> bootModule = assets.GetFirmwareBlockData(0).Span;
            Require(machine.Cpu.PC == 0x6000,
                $"The real boot-loader ROM did not hand off to firmware at $6000 " +
                $"within {maximumInstructions} instructions (PC=${machine.Cpu.PC:X4}).");
            Require(machine.MemoryBus.Read(0x6000) == bootModule[0]
                    && machine.MemoryBus.Read((ushort)(0x6000 + bootModule.Length - 1)) == bootModule[^1],
                "The boot loader reached $6000 without loading the mapped TBBLUE.FW boot module.");
            writer.WriteLine(
                $"  Boot milestone: loader copied {bootModule.Length} bytes and reached $6000 " +
                $"after {instructions} instructions / {machine.Cpu.Cyc} CPU T-states");
        }
        finally
        {
            machine.DivMmc.AttachSdCard(0, null);
            sdCard?.Dispose();
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static void VerifyCompleteSystemFirmwareBoot(
        ZxNextSystemAssets assets,
        TextWriter writer)
    {
        SpectrumDivMmcSdCard? sdCard = null;
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine(assets);
        var emptyRomBank = new byte[ZxNextMemoryBus.RomBankSize];
        for (int bank = 0; bank < 4; bank++)
        {
            machine.MemoryBus.LoadRomBank(bank, emptyRomBank);
        }

        machine.MemoryBus.LoadDivMmcRom(new byte[ZxNextPhysicalMemory.PageSize]);
        try
        {
            var bootProfileOverlay = new Dictionary<string, ReadOnlyMemory<byte>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["machines/next/config.ini"] = Encoding.ASCII.GetBytes("timing=7\r\n")
            };
            sdCard = SpectrumDivMmcSdCard.OpenWritableFolderSnapshot(
                assets.RootDirectory,
                bootProfileOverlay);
            machine.DivMmc.AttachSdCard(0, sdCard);

            const int maximumInstructions = 25_000_000;
            int instructions = 0;
            bool firmwareEntered = false;
            bool loggedCodeEscape = false;
            while (instructions < maximumInstructions && machine.ResetSequence == 0)
            {
                ushort previousPc = machine.Cpu.PC;
                ushort previousSp = machine.Cpu.SP;
                firmwareEntered |= previousPc == 0x6000;
                machine.StepInstruction();
                instructions++;

                int firmwareEnd = 0x6000 + assets.GetFirmwareBlockData(0).Length;
                if (firmwareEntered && !loggedCodeEscape && machine.ResetSequence == 0
                    && (machine.Cpu.PC < 0x6000 || machine.Cpu.PC >= firmwareEnd))
                {
                    loggedCodeEscape = true;
                    writer.WriteLine(
                        $"  Firmware trace: execution left $6000-${firmwareEnd - 1:X4}; " +
                        $"${previousPc:X4} -> ${machine.Cpu.PC:X4}, SP ${previousSp:X4}->" +
                        $"${machine.Cpu.SP:X4}, AF=${machine.Cpu.AF:X4}, " +
                        $"BC=${machine.Cpu.BC:X4}, DE=${machine.Cpu.DE:X4}, HL=${machine.Cpu.HL:X4}");
                }
            }

            Require(machine.ResetSequence == 1
                    && machine.LastResetKind == ZxNextResetKind.Soft
                    && machine.Registers.Read(0x02) == 0x01
                    && machine.Cpu.PC == 0,
                $"System/Next firmware did not request its final soft reset within " +
                $"{maximumInstructions} instructions (PC=${machine.Cpu.PC:X4}, " +
                $"NextReg=${machine.PortBus.SelectedRegister:X2}, " +
                $"last write={machine.Registers.LastWrittenRegister:X2}/" +
                $"{machine.Registers.LastWrittenValue:X2}).");
            Require(!machine.MemoryBus.ConfigurationMode && !machine.MemoryBus.BootRomEnabled,
                "Firmware soft reset did not leave the selected machine personality visible.");

            for (int bank = 0; bank < 4; bank++)
            {
                machine.MemoryBus.SelectRomBank(bank);
                int sourceOffset = bank * ZxNextMemoryBus.RomBankSize;
                Require(machine.MemoryBus.Read(0) == assets.MainRomData.Span[sourceOffset]
                        && machine.MemoryBus.Read(0x3FFF)
                            == assets.MainRomData.Span[
                                sourceOffset + ZxNextMemoryBus.RomBankSize - 1],
                    $"Firmware did not load normal ROM bank {bank} from the projected System/Next volume.");
            }

            machine.DivMmc.WriteControl(0x80);
            Require(machine.MemoryBus.Read(0) == assets.DivMmcRomData.Span[0]
                    && machine.MemoryBus.Read(0x1FFF) == assets.DivMmcRomData.Span[^1],
                "Firmware did not load the selected DivMMC ROM before soft reset.");
            writer.WriteLine(
                $"  Firmware milestone: projected a {sdCard.SectorCount}-sector FAT16 boot profile, " +
                $"loaded normal/DivMMC ROMs, and requested soft reset after {instructions} instructions");

            machine.StepInstruction();
            Require(machine.Cpu.PC == 0x0001 && machine.DivMmc.AutomapHeld,
                "The selected normal ROM reset vector did not trigger delayed DivMMC automap.");
            machine.StepInstruction();
            Require(machine.Cpu.PC == 0x006A,
                "The first mapped DivMMC instruction did not enter its boot routine.");
            writer.WriteLine(
                "  ROM milestone: normal ROM reset vector activated DivMMC and entered $006A");

            const int maximumTraceInstructions = 5_000_000;
            var programCounters = new Dictionary<ushort, int>();
            int haltedInstructions = 0;
            int longestHaltRun = 0;
            int currentHaltRun = 0;
            int keyWaitEntries = 0;
            int traceInstructions = 0;
            bool previouslyAtKeyWait = false;
            while (traceInstructions < maximumTraceInstructions && keyWaitEntries < 3)
            {
                programCounters.TryGetValue(machine.Cpu.PC, out int count);
                programCounters[machine.Cpu.PC] = count + 1;
                machine.StepInstruction();
                traceInstructions++;
                if (machine.Cpu.IsHalted)
                {
                    haltedInstructions++;
                    currentHaltRun++;
                    longestHaltRun = Math.Max(longestHaltRun, currentHaltRun);
                }
                else
                {
                    currentHaltRun = 0;
                }

                bool atKeyWait = machine.Cpu.IsHalted && machine.Cpu.PC is 0x0C8F or 0x0C90;
                if (atKeyWait && !previouslyAtKeyWait)
                {
                    keyWaitEntries++;
                }

                previouslyAtKeyWait = atKeyWait;
            }

            Require(keyWaitEntries == 3
                    && machine.UlaTiming.InterruptCount >= 3
                    && machine.Cpu.IsHalted
                    && machine.Cpu.PC is 0x0C8F or 0x0C90
                    && machine.Cpu.Iff1
                    && !machine.DivMmc.IsMapped
                    && machine.MemoryBus.CurrentRomBank == 0
                    && machine.MemoryBus.Read(0x0C8F) == 0x76
                    && machine.MemoryBus.Read(0x0C90) == 0x21,
                $"The loaded system ROM did not reach and repeatedly return to its " +
                $"interrupt-driven keyboard wait within {maximumTraceInstructions} instructions " +
                $"(PC=${machine.Cpu.PC:X4}, waits={keyWaitEntries}, " +
                $"interrupts={machine.UlaTiming.InterruptCount}).");
            string commonProgramCounters = string.Join(
                ", ",
                programCounters
                    .OrderByDescending(pair => pair.Value)
                    .Take(8)
                    .Select(pair => $"${pair.Key:X4}:{pair.Value}"));
            writer.WriteLine(
                $"  System-ROM milestone: bank 0 reached its keyboard wait and was " +
                $"released/re-entered {keyWaitEntries} times in {traceInstructions} instructions; " +
                $"halted steps={haltedInstructions}, longest halt={longestHaltRun}, " +
                $"ULA interrupts={machine.UlaTiming.InterruptCount}, " +
                $"common PCs [{commonProgramCounters}]");

            const int maximumKeyInstructions = 3_000_000;
            bool acceptedEnter = false;
            int keyInstructions = 0;
            machine.Keyboard.SetKeyState(SpectrumKey.Enter, pressed: true);
            try
            {
                while (keyInstructions < maximumKeyInstructions && !acceptedEnter)
                {
                    ushort previousPc = machine.Cpu.PC;
                    machine.StepInstruction();
                    keyInstructions++;
                    acceptedEnter = previousPc == 0x0C95
                        && machine.Cpu.PC == 0x0C97
                        && machine.MemoryBus.CurrentRomBank == 0
                        && !machine.DivMmc.IsMapped;
                }
            }
            finally
            {
                machine.Keyboard.SetKeyState(SpectrumKey.Enter, pressed: false);
            }

            byte acceptedKeyCode = machine.MemoryBus.Read(0x5C08);
            Require(acceptedEnter && acceptedKeyCode == 0x0D,
                $"The supplied system ROM did not accept scripted Enter within " +
                $"{maximumKeyInstructions} instructions (PC=${machine.Cpu.PC:X4}, " +
                $"LAST_K=${acceptedKeyCode:X2}).");

            int returnInstructions = 0;
            while (returnInstructions < maximumKeyInstructions
                && !(machine.Cpu.IsHalted && machine.Cpu.PC is 0x0C8F or 0x0C90))
            {
                machine.StepInstruction();
                returnInstructions++;
            }

            Require(machine.Cpu.IsHalted && machine.Cpu.PC is 0x0C8F or 0x0C90,
                $"The supplied system ROM did not return to its input wait after " +
                $"scripted Enter release within {maximumKeyInstructions} instructions " +
                $"(PC=${machine.Cpu.PC:X4}).");
            writer.WriteLine(
                $"  Keyboard milestone: scripted Enter left the bank-0 key wait after " +
                $"{keyInstructions} instructions with LAST_K=${acceptedKeyCode:X2}, then " +
                $"returned after release in {returnInstructions} instructions");

            var activeColors = new HashSet<int>();
            int activeNonBlackPixels = 0;
            int[] completedFrame = machine.Video.FrameBuffer;
            for (int y = ZxNextVideoRenderer.UlaTop;
                y < ZxNextVideoRenderer.UlaTop + ZxNextVideoRenderer.UlaHeight;
                y++)
            {
                int row = y * ZxNextVideoRenderer.FrameWidth;
                for (int x = ZxNextVideoRenderer.UlaLeft;
                    x < ZxNextVideoRenderer.UlaLeft + (ZxNextVideoRenderer.UlaWidth * 2);
                    x++)
                {
                    int color = completedFrame[row + x];
                    activeColors.Add(color);
                    if (color != unchecked((int)0xFF000000))
                    {
                        activeNonBlackPixels++;
                    }
                }
            }

            ulong frameHash = 14_695_981_039_346_656_037UL;
            foreach (int pixel in completedFrame)
            {
                frameHash ^= unchecked((uint)pixel);
                frameHash *= 1_099_511_628_211UL;
            }

            Require(machine.Video.CompletedFrameNumber == machine.UlaTiming.FrameNumber
                    && machine.Video.CompletedFrameNumber > 0
                    && activeColors.Count >= 2
                    && activeNonBlackPixels > 0,
                "The booted system did not produce a non-empty accepted legacy ULA frame.");
            writer.WriteLine(
                $"  Video milestone: accepted 640x512 frame {machine.Video.CompletedFrameNumber}, " +
                $"legacy area colors={activeColors.Count}, non-black pixels={activeNonBlackPixels}, " +
                $"FNV-1a=${frameHash:X16}");

            DiagnoseSuppliedSnxSnapshot(assets, machine, writer);
            DiagnoseSuppliedHottNex(assets, machine, writer);
            DiagnoseSuppliedWarhawkNex(assets, machine, writer);
            VerifyPostHardResetSpaceWorkflow(machine, writer);
        }
        finally
        {
            machine.DivMmc.AttachSdCard(0, null);
            sdCard?.Dispose();
        }
    }

    private static void VerifyPostHardResetSpaceWorkflow(
        ZxNextMachine machine,
        TextWriter writer)
    {
        machine.Keyboard.ReleaseAll();
        machine.Reset(ZxNextResetKind.Hard);
        ulong hardResetSequence = machine.ResetSequence;
        Require(machine.LastResetKind == ZxNextResetKind.Hard
                && machine.Cpu.PC == 0
                && machine.MemoryBus.BootRomEnabled
                && machine.MemoryBus.ConfigurationMode,
            "A desktop-style hard reset did not restore the loader mapping atomically.");

        const int maximumFirmwareInstructions = 25_000_000;
        int firmwareInstructions = 0;
        while (firmwareInstructions < maximumFirmwareInstructions
            && machine.ResetSequence == hardResetSequence)
        {
            machine.StepInstruction();
            firmwareInstructions++;
        }

        Require(machine.ResetSequence == hardResetSequence + 1
                && machine.LastResetKind == ZxNextResetKind.Soft
                && machine.Cpu.PC == 0
                && !machine.MemoryBus.ConfigurationMode
                && !machine.MemoryBus.BootRomEnabled,
            $"The supplied system did not complete its firmware soft reset after a " +
            $"desktop-style hard reset within {maximumFirmwareInstructions} instructions " +
            $"(PC=${machine.Cpu.PC:X4}, reset sequence={machine.ResetSequence}).");

        machine.StepInstruction();
        machine.StepInstruction();
        Require(machine.Cpu.PC == 0x006A,
            "The post-hard-reset ROM path did not re-enter the DivMMC boot routine.");

        const int maximumWelcomeInstructions = 5_000_000;
        int welcomeInstructions = 0;
        while (welcomeInstructions < maximumWelcomeInstructions
            && !(machine.Cpu.IsHalted && machine.Cpu.PC is 0x0C8F or 0x0C90))
        {
            machine.StepInstruction();
            welcomeInstructions++;
        }

        Require(machine.Cpu.IsHalted && machine.Cpu.PC is 0x0C8F or 0x0C90,
            $"The supplied system did not return to its Welcome/input wait after a hard " +
            $"reset within {maximumWelcomeInstructions} instructions (PC=${machine.Cpu.PC:X4}).");

        const int maximumSpaceInstructions = 3_000_000;
        int spaceInstructions = 0;
        bool acceptedSpace = false;
        machine.Keyboard.SetKeyState(SpectrumKey.Space, pressed: true);
        try
        {
            while (spaceInstructions < maximumSpaceInstructions && !acceptedSpace)
            {
                ushort previousPc = machine.Cpu.PC;
                machine.StepInstruction();
                spaceInstructions++;
                acceptedSpace = previousPc == 0x0C95
                    && machine.Cpu.PC == 0x0C97
                    && machine.MemoryBus.CurrentRomBank == 0
                    && !machine.DivMmc.IsMapped;
            }
        }
        finally
        {
            machine.Keyboard.SetKeyState(SpectrumKey.Space, pressed: false);
        }

        byte acceptedKeyCode = machine.MemoryBus.Read(0x5C08);
        Require(acceptedSpace && acceptedKeyCode == 0x20,
            $"The post-hard-reset Welcome/input wait did not accept Space within " +
            $"{maximumSpaceInstructions} instructions (PC=${machine.Cpu.PC:X4}, " +
            $"LAST_K=${acceptedKeyCode:X2}).");

        ulong targetInterrupts = machine.UlaTiming.InterruptCount + 3;
        int continuationInstructions = 0;
        while (continuationInstructions < maximumWelcomeInstructions
            && machine.UlaTiming.InterruptCount < targetInterrupts)
        {
            machine.StepInstruction();
            continuationInstructions++;
        }

        Require(machine.UlaTiming.InterruptCount >= targetInterrupts,
            "The post-hard-reset machine stopped advancing after Space was accepted.");
        writer.WriteLine(
            $"  Hard-reset milestone: firmware rebooted in {firmwareInstructions} instructions, " +
            $"returned to the Welcome/input wait in {welcomeInstructions}, accepted Space in " +
            $"{spaceInstructions}, and continued for three interrupts");
    }

    private static void VerifyAppliedAssetBytes(ZxNextMachine machine, ZxNextSystemAssets assets)
    {
        machine.Registers.Write(0x03, 0x03);
        for (int bank = 0; bank < 4; bank++)
        {
            machine.MemoryBus.SelectRomBank(bank);
            int sourceOffset = bank * ZxNextMemoryBus.RomBankSize;
            Require(machine.MemoryBus.Read(0) == assets.MainRomData.Span[sourceOffset]
                    && machine.MemoryBus.Read(0x3FFF)
                        == assets.MainRomData.Span[sourceOffset + ZxNextMemoryBus.RomBankSize - 1],
                $"System ROM bank {bank} was not applied to normal ROM storage.");
        }

        machine.MemoryBus.SelectRomBank(0);
        machine.Registers.Write(0x8C, 0x80);
        Require(machine.MemoryBus.Read(0) == assets.AlternateRomData.Span[0],
            "Alternate ROM bank 0 was not applied.");
        machine.Registers.Write(0x8C, 0xA0);
        Require(machine.MemoryBus.Read(0)
                == assets.AlternateRomData.Span[ZxNextMemoryBus.RomBankSize],
            "Alternate ROM bank 1 was not applied.");

        machine.Registers.Write(0x8C, 0);
        machine.DivMmc.WriteControl(0x80);
        Require(machine.MemoryBus.Read(0) == assets.DivMmcRomData.Span[0]
                && machine.MemoryBus.Read(0x1FFF) == assets.DivMmcRomData.Span[^1],
            "The DivMMC ROM was not applied to system SRAM page 8.");
    }

    private static byte[] CreateAssetPattern(int length, int seed)
    {
        var data = new byte[length];
        for (int index = 0; index < data.Length; index++)
        {
            data[index] = (byte)(seed + (index * 13) + ((index / ZxNextPhysicalMemory.PageSize) * 29));
        }

        return data;
    }

    private static byte[] CreateSyntheticFirmwarePackage()
    {
        var data = new byte[2 * ZxNextSystemAssets.FirmwareBlockSize];
        for (int index = 0; index < ZxNextSystemAssets.FirmwareBlockMapEntries; index++)
        {
            data[(index * 4) + 2] = 1;
        }

        CreateAssetPattern(ZxNextSystemAssets.FirmwareBlockSize, 0xB6)
            .CopyTo(data, ZxNextSystemAssets.FirmwareBlockSize);
        return data;
    }

    private static string CreateSyntheticBootRomVhdl(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder();
        builder.AppendLine("type ROM_ARRAY is array(0 to 8191) of std_logic_vector(7 downto 0);");
        builder.AppendLine("constant ROM : ROM_ARRAY := (");
        for (int index = 0; index < data.Length; index++)
        {
            if (index != 0)
            {
                builder.Append(',');
            }

            builder.Append("x\"");
            builder.Append(data[index].ToString("X2", CultureInfo.InvariantCulture));
            builder.Append('"');
        }

        builder.AppendLine(");");
        return builder.ToString();
    }

    private static void VerifyMmuMapping()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.Registers.Write(0x03, 0x03);

        for (int slot = 0; slot < ZxNextMemoryBus.MmuSlotCount; slot++)
        {
            ushort logicalAddress = (ushort)((slot << 13) | 0x0123);
            for (int page = 0; page < machine.MemoryBus.PhysicalMemory.InstalledRamPageCount; page++)
            {
                byte value = (byte)((slot * 37) ^ page);
                machine.Registers.Write((byte)(0x50 + slot), (byte)page);
                machine.MemoryBus.WriteCpu(logicalAddress, value);

                Require(machine.MemoryBus.Read(logicalAddress) == value
                        && machine.MemoryBus.ReadPhysicalRam(page, 0x0123) == value,
                    $"MMU slot {slot} failed to map installed page {page}.");

                var mapping = machine.MemoryBus.GetMapping(logicalAddress);
                Require(mapping.Page == slot && mapping.IsRam && mapping.BankIndex == page
                        && mapping.Offset == 0x0123 && !mapping.IsReadOnly,
                    $"Debugger mapping misidentified MMU slot {slot}, page {page}.");
            }

            machine.Registers.Write((byte)(0x50 + slot), 96);
            machine.MemoryBus.WriteCpu(logicalAddress, 0xA5);
            var openMapping = machine.MemoryBus.GetMapping(logicalAddress);
            Require(machine.MemoryBus.Read(logicalAddress) == 0xFF && openMapping.IsOpenBus
                    && openMapping.IsReadOnly && !machine.MemoryBus.CanWriteDirect(logicalAddress),
                $"MMU slot {slot} did not reject an uninstalled page.");
        }

        byte[] rom = new byte[ZxNextMemoryBus.RomBankSize];
        rom[0x0123] = 0xA5;
        rom[0x2123] = 0x5A;
        machine.MemoryBus.LoadRomBank(0, rom);
        machine.Registers.Write(0x03, 0x03);
        machine.Registers.Write(0x50, ZxNextMemoryBus.RomPageSentinel);
        machine.Registers.Write(0x51, ZxNextMemoryBus.RomPageSentinel);
        Require(machine.MemoryBus.Read(0x0123) == 0xA5 && machine.MemoryBus.Read(0x2123) == 0x5A,
            "The ROM sentinel did not select the correct lower or upper 8 KiB half.");
        machine.MemoryBus.WriteCpu(0x0123, 0);
        var romMapping = machine.MemoryBus.GetMapping(0x0123);
        Require(machine.MemoryBus.Read(0x0123) == 0xA5 && romMapping.IsRom
                && romMapping.IsReadOnly && romMapping.RomBank == 0,
            "ROM mapping was writable or incorrectly described to the debugger.");

        machine.Registers.Reset();
        byte[] expectedMmu = [0xFF, 0xFF, 10, 11, 4, 5, 0, 1];
        for (int slot = 0; slot < expectedMmu.Length; slot++)
        {
            Require(machine.MemoryBus.GetMmuPage(slot) == expectedMmu[slot],
                $"MMU slot {slot} did not return to its reset mapping.");
        }
    }

    private static void VerifyNextRegPorts()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextPortBus ports = machine.PortBus;

        Require(ports.SelectedRegister == 0x24
                && ports.ReadUncontended(ZxNextPortBus.NextRegisterSelectPort) == 0x24,
            "NextReg selector latch did not reset to $24.");
        Require(ports.ReadUncontended(0x243A) == 0xBF && ports.ReadUncontended(0x253A) == 0xBF,
            "Non-matching even NextReg aliases did not fall through to the partially decoded ULA port.");

        ports.WriteUncontended(ZxNextPortBus.NextRegisterSelectPort, 0x52);
        Require(ports.ReadUncontended(ZxNextPortBus.NextRegisterDataPort) == 10,
            "The NextReg data port did not read the selected MMU register.");
        ports.WriteUncontended(ZxNextPortBus.NextRegisterDataPort, 30);
        Require(machine.Registers.Read(0x52) == 30 && machine.MemoryBus.GetMmuPage(2) == 30,
            "A NextReg data-port write did not reach the MMU.");

        ports.WriteNextRegister(0x52, 31);
        Require(ports.SelectedRegister == 0x52
                && ports.ReadUncontended(ZxNextPortBus.NextRegisterDataPort) == 31,
            "A Z80N NEXTREG write changed the port selector or diverged from port-visible state.");

        ZxNextMachine guest = CreateProgram(
            0x01, 0x3B, 0x24,       // LD BC,$243B
            0x3E, 0x56,             // LD A,$56
            0xED, 0x79,             // OUT (C),A
            0x01, 0x3B, 0x25,       // LD BC,$253B
            0x3E, 0x14,             // LD A,$14
            0xED, 0x79);            // OUT (C),A
        for (int instruction = 0; instruction < 6; instruction++)
        {
            guest.StepInstruction();
        }

        Require(guest.PortBus.SelectedRegister == 0x56
                && guest.Registers.Read(0x56) == 0x14
                && guest.MemoryBus.GetMmuPage(6) == 0x14,
            "Guest Z80 OUT instructions did not update the selected MMU page.");
    }

    private static void VerifyLegacyPaging()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.Registers.Write(0x03, 0x03);
        ZxNextPagingController paging = machine.Paging;
        ZxNextPortBus ports = machine.PortBus;

        for (int bank = 0; bank < 4; bank++)
        {
            byte[] rom = new byte[ZxNextMemoryBus.RomBankSize];
            rom[0x0123] = (byte)(0xA0 + bank);
            machine.MemoryBus.LoadRomBank(bank, rom);
        }

        Require(paging.Port7Ffd == 0 && paging.PortDFfd == 0 && paging.Port1Ffd == 0
                && paging.PortEff7 == 0 && !paging.PagingLocked
                && paging.SelectedRamBank == 0 && paging.SelectedRomBank == 0,
            "Legacy paging registers did not reset to native +3 defaults.");
        Require(ZxNextPortBus.IsPort7Ffd(0x5FFD) && ZxNextPortBus.IsPortDFfd(0xD1FD)
                && ZxNextPortBus.IsPort1Ffd(0x11FD) && ZxNextPortBus.IsPortEff7(0xE1F7)
                && !ZxNextPortBus.IsPort7Ffd(0x3FFD),
            "A native legacy paging port mask does not match the pinned decoder.");
        Require(ports.ReadUncontended(0x7FFD) == 0xFF,
            "A write-only legacy paging port did not return open bus.");

        ports.WriteUncontended(0x7FFD, 0x13);
        Require(paging.SelectedRamBank == 3 && paging.SelectedRomBank == 1
                && machine.MemoryBus.GetMmuPage(6) == 6 && machine.MemoryBus.GetMmuPage(7) == 7
                && machine.MemoryBus.Read(0x0123) == 0xA1,
            "$7FFD did not select the expected RAM and ROM banks.");

        ports.WriteUncontended(0xDFFD, 0x01);
        Require(paging.SelectedRamBank == 11
                && machine.MemoryBus.GetMmuPage(6) == 22 && machine.MemoryBus.GetMmuPage(7) == 23,
            "$DFFD did not extend the top 16 KiB bank number.");

        ports.WriteUncontended(0xE1F7, 0x08);
        Require(machine.MemoryBus.GetMmuPage(0) == 0 && machine.MemoryBus.GetMmuPage(1) == 1,
            "$EFF7 bit 3 did not page RAM bank zero over ROM.");
        ports.WriteUncontended(0xEFF7, 0x00);
        Require(machine.MemoryBus.GetMmuPage(0) == 0xFF && machine.MemoryBus.GetMmuPage(1) == 0xFF,
            "Clearing $EFF7 bit 3 did not reveal ROM again.");

        machine.Registers.Write(0x52, 40);
        machine.Registers.Write(0x53, 41);
        machine.Registers.Write(0x54, 42);
        machine.Registers.Write(0x55, 43);
        ports.WriteUncontended(0x7FFD, 0x12);
        Require(machine.MemoryBus.GetMmuPage(2) == 40 && machine.MemoryBus.GetMmuPage(3) == 41
                && machine.MemoryBus.GetMmuPage(4) == 42 && machine.MemoryBus.GetMmuPage(5) == 43,
            "A normal legacy paging write destroyed custom middle MMU mappings.");

        int[][] allRamBanks =
        [
            [0, 1, 2, 3],
            [4, 5, 6, 7],
            [5, 5, 6, 3],
            [4, 7, 6, 3]
        ];
        for (int mode = 0; mode < allRamBanks.Length; mode++)
        {
            ports.WriteUncontended(0x1FFD, (byte)((mode << 1) | 0x01));
            for (int pair = 0; pair < 4; pair++)
            {
                int page = allRamBanks[mode][pair] * 2;
                Require(machine.MemoryBus.GetMmuPage(pair * 2) == page
                        && machine.MemoryBus.GetMmuPage((pair * 2) + 1) == page + 1,
                    $"All-RAM mode {mode} mapped 16 KiB slot {pair} incorrectly.");
            }
        }

        ports.WriteUncontended(0x1FFD, 0x00);
        Require(!paging.IsAllRamMode
                && machine.MemoryBus.GetMmuPage(0) == 0xFF && machine.MemoryBus.GetMmuPage(1) == 0xFF
                && machine.MemoryBus.GetMmuPage(2) == 10 && machine.MemoryBus.GetMmuPage(3) == 11
                && machine.MemoryBus.GetMmuPage(4) == 4 && machine.MemoryBus.GetMmuPage(5) == 5
                && machine.MemoryBus.GetMmuPage(6) == 20 && machine.MemoryBus.GetMmuPage(7) == 21,
            "Leaving all-RAM mode did not restore native normal-mode mappings.");

        for (int romBank = 0; romBank < 4; romBank++)
        {
            ports.WriteUncontended(0x1FFD, (byte)((romBank & 0x02) << 1));
            ports.WriteUncontended(0x7FFD, (byte)((paging.Port7Ffd & 0x07) | ((romBank & 0x01) << 4)));
            Require(paging.SelectedRomBank == romBank && machine.MemoryBus.Read(0x0123) == 0xA0 + romBank,
                $"Legacy paging did not select ROM bank {romBank}.");
        }

        machine.Reset();
        ports.WriteUncontended(0x7FFD, 0x25);
        Require(paging.PagingLocked && paging.SelectedRamBank == 5
                && machine.MemoryBus.GetMmuPage(6) == 10 && machine.MemoryBus.GetMmuPage(7) == 11,
            "$7FFD did not latch the paging lock after accepting the locking write.");
        ports.WriteUncontended(0x7FFD, 0x01);
        ports.WriteUncontended(0xDFFD, 0x02);
        ports.WriteUncontended(0x1FFD, 0x05);
        Require(paging.Port7Ffd == 0x25 && paging.PortDFfd == 0 && paging.Port1Ffd == 0,
            "A locked legacy paging register accepted a write.");
        ports.WriteUncontended(0xEFF7, 0x08);
        Require(paging.PortEff7 == 0x08 && machine.MemoryBus.GetMmuPage(0) == 0,
            "$EFF7 was incorrectly blocked by the $7FFD paging lock.");
        Require(machine.Registers.Read(0x08) == 0x10,
            "NextReg 0x08 did not report the locked paging state.");
        machine.Registers.Write(0x08, 0x80);
        Require(!paging.PagingLocked && machine.Registers.Read(0x08) == 0x80,
            "NextReg 0x08 bit 7 did not unlock legacy paging.");

        machine.Reset();
        machine.Registers.Write(0x03, 0x03);
        machine.Registers.Write(0x8E, 0xBB);
        Require(machine.Registers.Read(0x8E) == 0xBB
                && paging.SelectedRamBank == 11 && paging.SelectedRomBank == 3
                && machine.MemoryBus.GetMmuPage(6) == 22 && machine.MemoryBus.GetMmuPage(7) == 23
                && machine.MemoryBus.Read(0x0123) == 0xA3,
            "NextReg 0x8E did not synchronize legacy RAM/ROM state with the MMU.");
    }

    private static void VerifyGuestLegacyPaging()
    {
        ZxNextMachine machine = CreateProgramAt(
            0x8000,
            0x01, 0xFD, 0x7F,       // LD BC,$7FFD
            0x3E, 0x13,             // LD A,$13
            0xED, 0x79,             // OUT (C),A
            0x01, 0xFD, 0xDF,       // LD BC,$DFFD
            0x3E, 0x01,             // LD A,$01
            0xED, 0x79);            // OUT (C),A
        for (int instruction = 0; instruction < 6; instruction++)
        {
            machine.StepInstruction();
        }

        Require(machine.Paging.SelectedRamBank == 11 && machine.Paging.SelectedRomBank == 1
                && machine.MemoryBus.GetMmuPage(6) == 22 && machine.MemoryBus.GetMmuPage(7) == 23,
            "Guest OUT instructions did not drive native legacy paging.");
    }

    private static void VerifyBootAndConfigMapping()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        byte[] boot = new byte[ZxNextMemoryBus.BootRomSize];
        boot[0x0123] = 0xB0;
        machine.MemoryBus.LoadBootRom(boot);

        byte[] rom0 = new byte[ZxNextMemoryBus.RomBankSize];
        rom0[0x0123] = 0xA0;
        machine.MemoryBus.LoadRomBank(0, rom0);

        Require(machine.MemoryBus.Read(0x0123) == 0xB0 && machine.MemoryBus.Read(0x2123) == 0xB0,
            "The 8 KiB boot ROM was not mirrored across the lower 16 KiB.");
        var bootMapping = machine.MemoryBus.GetMapping(0x0123);
        Require(bootMapping.IsRom && bootMapping.RomBank == ZxNextMemoryBus.BootRomDebuggerBank
                && !bootMapping.IsReadOnly && machine.MemoryBus.CanWriteDirect(0x0123),
            "The debugger did not describe the boot ROM's write-through config mapping.");

        machine.MemoryBus.WriteCpu(0x0123, 0xC0);
        Require(machine.MemoryBus.Read(0x0123) == 0xB0,
            "A write incorrectly replaced the visible boot ROM byte.");
        machine.Registers.Write(0x03, 0x00);
        Require(!machine.MemoryBus.BootRomEnabled && machine.MemoryBus.ConfigurationMode
                && machine.MemoryBus.Read(0x0123) == 0xC0,
            "Writing NextReg 0x03 did not disable boot ROM and reveal config SRAM.");

        machine.Registers.Write(0x04, ZxNextMemoryBus.GuestRamFirstRawBank);
        machine.MemoryBus.WriteCpu(0x0123, 0xD0);
        machine.MemoryBus.WriteCpu(0x2123, 0xD1);
        Require(machine.MemoryBus.ReadPhysicalRam(0, 0x0123) == 0xD0
                && machine.MemoryBus.ReadPhysicalRam(1, 0x0123) == 0xD1,
            "Config mapping did not address the two physical pages of a raw 16 KiB SRAM bank.");

        machine.Registers.Write(0x50, 2);
        machine.MemoryBus.WriteCpu(0x0123, 0xE0);
        Require(machine.MemoryBus.ReadPhysicalRam(2, 0x0123) == 0xE0
                && machine.MemoryBus.ReadPhysicalRam(0, 0x0123) == 0xD0,
            "A valid MMU page did not take priority over config mapping.");
        machine.Registers.Write(0x50, ZxNextMemoryBus.RomPageSentinel);

        machine.Registers.Write(0x04, 63);
        machine.MemoryBus.WriteCpu(0x3FFF, 0xDF);
        Require(machine.MemoryBus.ReadPhysicalRam(95, ZxNextPhysicalMemory.PageSize - 1) == 0xDF,
            "The final installed raw SRAM bank did not map the final physical RAM page.");
        machine.Registers.Write(0x04, 64);
        machine.MemoryBus.WriteCpu(0x0123, 0xEE);
        var invalidConfigMapping = machine.MemoryBus.GetMapping(0x0123);
        Require(machine.MemoryBus.Read(0x0123) == 0xFF && invalidConfigMapping.IsOpenBus
                && !machine.MemoryBus.CanWriteDirect(0x0123),
            "The first uninstalled raw SRAM bank did not behave as open bus.");

        machine.Registers.Write(0x04, 0x01);
        machine.MemoryBus.WriteCpu(0x0123, 0xE1);
        var configMapping = machine.MemoryBus.GetMapping(0x0123);
        Require(configMapping.IsRam && !configMapping.IsReadOnly && configMapping.BankIndex == 1,
            "The debugger did not identify the selected config SRAM bank.");
        machine.Registers.Write(0x03, 0x03);
        machine.Paging.WritePort7Ffd(0x10);
        Require(!machine.MemoryBus.ConfigurationMode && machine.MemoryBus.CurrentRomBank == 1
                && machine.MemoryBus.Read(0x0123) == 0xE1,
            "Exiting config mode did not expose ROM data loaded through raw SRAM mapping.");

        machine.Registers.Write(0x03, 0x07);
        Require(machine.MemoryBus.ConfigurationMode && !machine.MemoryBus.BootRomEnabled
                && machine.MemoryBus.Read(0x0123) == 0xE1,
            "Re-entering config mode unexpectedly re-enabled boot ROM or lost the config bank.");

        ZxNextMachine guest = ZxNextMachineFactory.CreateFoundationMachine();
        byte[] guestBoot = new byte[ZxNextMemoryBus.BootRomSize];
        guestBoot[0] = 0xED;
        guestBoot[1] = 0x91;
        guestBoot[2] = 0x03;
        guestBoot[3] = 0x03;
        guest.MemoryBus.LoadBootRom(guestBoot);
        guest.StepInstruction();
        Require(guest.Cpu.PC == 4 && guest.Cpu.Cyc == 20
                && !guest.MemoryBus.BootRomEnabled && !guest.MemoryBus.ConfigurationMode,
            "A guest NEXTREG instruction could not leave boot/config mode from the boot ROM.");
    }

    private static void VerifyAlternateRomOverlay()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.Registers.Write(0x03, 0x03);

        for (int bank = 0; bank < 4; bank++)
        {
            byte[] rom = new byte[ZxNextMemoryBus.RomBankSize];
            rom[0x0123] = (byte)(0x10 + bank);
            machine.MemoryBus.LoadRomBank(bank, rom);
        }

        for (int bank = 0; bank < 2; bank++)
        {
            byte[] alternate = new byte[ZxNextMemoryBus.RomBankSize];
            alternate[0x0123] = (byte)(0xA0 + bank);
            machine.MemoryBus.LoadAlternateRomBank(bank, alternate);
        }

        machine.Paging.WritePort1Ffd(0x04);
        machine.Paging.WritePort7Ffd(0x00);
        Require(machine.Paging.SelectedRomBank == 2, "The alternate-ROM test did not select ROM2.");
        machine.Registers.Write(0x8C, 0x80);
        Require(machine.MemoryBus.Read(0x0123) == 0xA0,
            "Read-visible alternate ROM did not replace ROM2 with alternate bank 0.");
        var alternateMapping = machine.MemoryBus.GetMapping(0x0123);
        Require(alternateMapping.IsRom && alternateMapping.RomBank == ZxNextMemoryBus.AlternateRomDebuggerBank
                && alternateMapping.IsReadOnly,
            "The debugger did not describe the read-visible alternate ROM.");

        machine.Paging.WritePort7Ffd(0x10);
        Require(machine.Paging.SelectedRomBank == 3 && machine.MemoryBus.Read(0x0123) == 0xA1,
            "Normal ROM selection did not choose alternate bank 1 for ROM3.");

        machine.Registers.Write(0x8C, 0xC0);
        Require(machine.MemoryBus.Read(0x0123) == 0x13 && machine.MemoryBus.CanWriteDirect(0x0123),
            "Write-only alternate ROM did not leave normal ROM3 visible.");
        machine.MemoryBus.WriteCpu(0x0123, 0xC1);
        Require(machine.MemoryBus.Read(0x0123) == 0x13,
            "A write-only alternate ROM became visible during reads.");
        machine.Registers.Write(0x8C, 0x80);
        Require(machine.MemoryBus.Read(0x0123) == 0xC1,
            "The write-only alternate ROM did not preserve its write.");

        machine.Registers.Write(0x8C, 0x10);
        Require(machine.MemoryBus.EffectiveRomBank == 1 && machine.MemoryBus.Read(0x0123) == 0x11,
            "Alternate ROM lock bit 4 did not force normal ROM1.");
        machine.Registers.Write(0x8C, 0x20);
        Require(machine.MemoryBus.EffectiveRomBank == 2 && machine.MemoryBus.Read(0x0123) == 0x12,
            "Alternate ROM lock bit 5 did not force normal ROM2.");
        machine.Registers.Write(0x8C, 0xB0);
        Require(machine.MemoryBus.EffectiveRomBank == 3 && machine.MemoryBus.EffectiveAlternateRomBank == 1
                && machine.MemoryBus.Read(0x0123) == 0xC1,
            "Combined ROM locks did not force ROM3 and alternate bank 1.");

        machine.Registers.Write(0x50, 2);
        machine.MemoryBus.WriteCpu(0x0123, 0x22);
        Require(machine.MemoryBus.Read(0x0123) == 0x22,
            "Alternate ROM incorrectly overrode a valid lower MMU page.");
    }

    private static void VerifyRomResetKinds()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.Registers.Write(0x03, 0x03);
        machine.Registers.Write(0x04, 42);
        machine.Registers.Write(0x8C, 0x0D);
        machine.MemoryBus.WritePhysicalRam(7, 9, 0xA5);

        machine.Reset(ZxNextResetKind.Soft);
        Require(!machine.MemoryBus.ConfigurationMode && !machine.MemoryBus.BootRomEnabled
                && machine.MemoryBus.ConfigurationBank == 42
                && machine.MemoryBus.AlternateRomRegister == 0xDD
                && machine.MemoryBus.ReadPhysicalRam(7, 9) == 0xA5,
            "Soft reset did not preserve machine/config state or apply alternate-ROM reset bits.");

        machine.Registers.Write(0x03, 0x07);
        machine.Reset(ZxNextResetKind.Soft);
        Require(machine.MemoryBus.ConfigurationMode && machine.MemoryBus.BootRomEnabled,
            "Soft reset in config mode did not re-enable boot ROM.");

        machine.Reset(ZxNextResetKind.Hard);
        Require(machine.MemoryBus.ConfigurationMode && machine.MemoryBus.BootRomEnabled
                && machine.MemoryBus.ConfigurationBank == 0 && machine.MemoryBus.AlternateRomRegister == 0
                && machine.Registers.Read(0x03) == 0x33
                && machine.MemoryBus.ReadPhysicalRam(7, 9) == 0xA5,
            "Hard reset did not restore boot/config controls while preserving RAM.");
    }

    private static void VerifyDivMmcControlAndMapping()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();

        byte[] normalRom = new byte[ZxNextMemoryBus.RomBankSize];
        normalRom[0x0123] = 0x10;
        machine.MemoryBus.LoadRomBank(0, normalRom);

        byte[] divMmcRom = new byte[ZxNextPhysicalMemory.PageSize];
        divMmcRom[0x0123] = 0xD0;
        machine.MemoryBus.LoadDivMmcRom(divMmcRom);

        // Raw 16 KiB system bank 9 contains DivMMC RAM pages 2 and 3; bank 10
        // contains pages 4 and 5. Populate pages 3 and 5 through config mode to
        // prove that paging and configuration access share the same SRAM bytes.
        machine.Registers.Write(0x03, 0x00);
        machine.Registers.Write(0x04, 9);
        machine.MemoryBus.WriteCpu(0x2123, 0xB3);
        machine.Registers.Write(0x04, 10);
        machine.MemoryBus.WriteCpu(0x2123, 0xB5);
        machine.Registers.Write(0x03, 0x03);

        Require(machine.PortBus.ReadUncontended(0x00E3) == 0
                && machine.PortBus.ReadUncontended(0xFFE3) == 0,
            "DivMMC control did not reset to zero or use low-byte-only decoding.");

        machine.PortBus.WriteUncontended(0x12E3, 0x85);
        Require(machine.DivMmc.Conmem && !machine.DivMmc.Mapram
                && machine.DivMmc.SelectedRamBank == 5
                && machine.PortBus.ReadUncontended(0xFFE3) == 0x85,
            "$E3 did not expose CONMEM, MAPRAM and the selected bank latch.");
        Require(machine.MemoryBus.Read(0x0123) == 0xD0 && machine.MemoryBus.Read(0x2123) == 0xB5,
            "CONMEM did not map the DivMMC ROM and selected RAM page.");

        DebuggerMemoryMapping divRomMapping = machine.MemoryBus.GetMapping(0x0123);
        DebuggerMemoryMapping divRamMapping = machine.MemoryBus.GetMapping(0x2123);
        Require(divRomMapping.IsRom && divRomMapping.IsReadOnly
                && divRomMapping.RomBank == ZxNextMemoryBus.DivMmcRomDebuggerBank
                && divRamMapping.IsRam && !divRamMapping.IsReadOnly
                && divRamMapping.BankIndex == ZxNextMemoryBus.DivMmcRamFirstRawPage + 5,
            "Debugger mapping did not describe the two DivMMC windows.");

        machine.MemoryBus.WriteCpu(0x2123, 0xB6);
        machine.PortBus.WriteUncontended(0x00E3, 0x05);
        Require(!machine.DivMmc.IsMapped && machine.MemoryBus.Read(0x0123) == 0x10,
            "Clearing CONMEM did not reveal the underlying ROM immediately.");
        machine.Registers.Write(0x03, 0x07);
        machine.Registers.Write(0x04, 10);
        Require(machine.MemoryBus.Read(0x2123) == 0xB6,
            "A DivMMC RAM write was not visible through config SRAM mapping.");
        machine.Registers.Write(0x03, 0x03);

        machine.PortBus.WriteUncontended(0x00E3, 0xC5);
        Require(machine.DivMmc.Mapram && machine.MemoryBus.Read(0x0123) == 0xB3,
            "MAPRAM did not replace the lower DivMMC ROM with RAM bank 3 under CONMEM.");
        machine.MemoryBus.WriteCpu(0x0123, 0xEE);
        Require(machine.MemoryBus.Read(0x0123) == 0xB3,
            "MAPRAM bank 3 was writable in the lower protected window.");

        machine.PortBus.WriteUncontended(0x00E3, 0xC3);
        machine.MemoryBus.WriteCpu(0x2123, 0xEE);
        Require(machine.MemoryBus.Read(0x2123) == 0xB3,
            "MAPRAM did not protect selected bank 3 in the upper window.");

        machine.PortBus.WriteUncontended(0x00E3, 0x00);
        Require(machine.PortBus.ReadUncontended(0x00E3) == 0x40 && !machine.DivMmc.IsMapped,
            "MAPRAM did not remain latched after bit 6 was written as zero.");
        machine.Registers.Write(0x09, 0x08);
        Require(machine.PortBus.ReadUncontended(0x00E3) == 0 && machine.Registers.Read(0x09) == 0,
            "NextReg $09 bit 3 did not clear MAPRAM or incorrectly read back as one.");

        machine.Reset();
        machine.PortBus.WriteUncontended(0x00E3, 0x80);
        Require(machine.MemoryBus.Read(0x0123) == 0,
            "DivMMC incorrectly overrode the higher-priority boot ROM.");
    }

    private static void VerifyDivMmcAutomap()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.Registers.Write(0x04, 8);
        machine.MemoryBus.WriteCpu(0x1D55, 0xB0);
        machine.Registers.Write(0x03, 0x03);

        byte[] rom0 = new byte[ZxNextMemoryBus.RomBankSize];
        rom0[0x0000] = 0x10;
        rom0[0x0001] = 0x11;
        rom0[0x0038] = 0x18;
        rom0[0x1FF9] = 0x19;
        machine.MemoryBus.LoadRomBank(0, rom0);

        byte[] rom3 = new byte[ZxNextMemoryBus.RomBankSize];
        rom3[0x0000] = 0x30;
        rom3[0x0038] = 0x38;
        rom3[0x04C6] = 0x46;
        machine.MemoryBus.LoadRomBank(3, rom3);

        byte[] divMmcRom = new byte[ZxNextPhysicalMemory.PageSize];
        divMmcRom[0x0000] = 0xD0;
        divMmcRom[0x0001] = 0xD1;
        divMmcRom[0x0038] = 0xD8;
        divMmcRom[0x04C6] = 0xD6;
        divMmcRom[0x1FF8] = 0xE8;
        machine.MemoryBus.LoadDivMmcRom(divMmcRom);

        machine.Registers.Write(0x0A, 0x11);
        Require(machine.MemoryBus.FetchOpcode(0x0000) == 0x10 && machine.DivMmc.AutomapHeld,
            "The default delayed $0000 entry changed the triggering opcode or failed to map afterward.");
        Require(machine.MemoryBus.FetchOpcode(0x0001) == 0xD1,
            "A delayed entry did not expose DivMMC on the following opcode fetch.");
        Require(machine.MemoryBus.FetchOpcode(0x1FF8) == 0xE8 && !machine.DivMmc.AutomapHeld
                && machine.MemoryBus.Read(0x1FF9) == 0x19,
            "The delayed $1FF8-$1FFF exit did not retain the triggering byte and unmap afterward.");

        Require(machine.MemoryBus.FetchOpcode(0x0038) == 0x18 && !machine.DivMmc.AutomapHeld,
            "The ROM3-qualified default $0038 entry fired while ROM0 was present.");
        machine.Paging.WritePort7Ffd(0x10);
        machine.Paging.WritePort1Ffd(0x04);
        Require(machine.Paging.SelectedRomBank == 3
                && machine.MemoryBus.FetchOpcode(0x0038) == 0x38
                && machine.DivMmc.AutomapHeld,
            "The default $0038 entry did not map after its ROM3 opcode was fetched.");
        _ = machine.MemoryBus.FetchOpcode(0x1FF8);

        Require(machine.MemoryBus.FetchOpcode(0x04C6) == 0x46 && machine.DivMmc.AutomapHeld
                && machine.MemoryBus.Read(0x04C6) == 0xD6,
            "The fixed delayed ROM3 tape entry did not map after its triggering opcode.");
        _ = machine.MemoryBus.FetchOpcode(0x1FF8);
        Require(machine.MemoryBus.FetchOpcode(0x3D55) == 0xB0 && machine.DivMmc.AutomapHeld,
            "The fixed instant ROM3 $3DXX entry did not return the selected DivMMC RAM byte.");
        _ = machine.MemoryBus.FetchOpcode(0x1FF8);

        machine.Registers.Write(0xB8, 0x01);
        machine.Registers.Write(0xB9, 0x01);
        machine.Registers.Write(0xBA, 0x01);
        machine.Registers.Write(0xBB, 0x40);
        Require(machine.MemoryBus.FetchOpcode(0x0000) == 0xD0 && machine.DivMmc.AutomapHeld,
            "A programmable instant entry did not affect the triggering opcode fetch.");

        machine.Registers.Write(0x0A, 0x01);
        Require(!machine.DivMmc.AutomapHeld
                && machine.MemoryBus.FetchOpcode(0x0000) == 0x30
                && !machine.DivMmc.AutomapHeld,
            "Disabling NextReg $0A automap did not clear and suppress the held mapping.");

        machine.Registers.Write(0x50, 0);
        machine.MemoryBus.WritePhysicalRam(0, 0, 0xA0);
        machine.Registers.Write(0xBA, 0x00);
        machine.Registers.Write(0x0A, 0x11);
        Require(machine.MemoryBus.FetchOpcode(0x0000) == 0xA0 && machine.DivMmc.AutomapHeld,
            "An always-valid programmable entry did not operate over a valid MMU page.");

        ZxNextMachine guest = ZxNextMachineFactory.CreateFoundationMachine();
        guest.Registers.Write(0x03, 0x03);
        byte[] guestRom = new byte[ZxNextMemoryBus.RomBankSize];
        byte[] guestDivMmcRom = new byte[ZxNextPhysicalMemory.PageSize];
        guestDivMmcRom[1] = 0x76; // HALT
        guest.MemoryBus.LoadRomBank(0, guestRom);
        guest.MemoryBus.LoadDivMmcRom(guestDivMmcRom);
        guest.Registers.Write(0x0A, 0x11);
        guest.StepInstruction();
        Require(guest.Cpu.PC == 1 && guest.DivMmc.AutomapHeld,
            "Guest execution did not activate delayed automap after its reset-vector NOP.");
        guest.StepInstruction();
        Require(guest.Cpu.IsHalted,
            "The CPU did not fetch the following opcode from the newly mapped DivMMC ROM.");

        ZxNextMachine retn = ZxNextMachineFactory.CreateFoundationMachine();
        retn.Registers.Write(0x03, 0x03);
        byte[] retnRom = new byte[ZxNextMemoryBus.RomBankSize];
        byte[] retnDivMmcRom = new byte[ZxNextPhysicalMemory.PageSize];
        retnDivMmcRom[1] = 0xED;
        retnDivMmcRom[2] = 0x45; // RETN
        retn.MemoryBus.LoadRomBank(0, retnRom);
        retn.MemoryBus.LoadDivMmcRom(retnDivMmcRom);
        retn.MemoryBus.WriteCpu(0x8000, 0x00);
        retn.MemoryBus.WriteCpu(0x8001, 0x40);
        retn.Cpu.SP = 0x8000;
        retn.Registers.Write(0x0A, 0x11);
        retn.StepInstruction();
        Require(retn.Cpu.PC == 1 && retn.DivMmc.AutomapHeld,
            "RETN regression setup did not enter the delayed DivMMC mapping.");
        retn.StepInstruction();
        Require(retn.Cpu.PC == 0x4000 && !retn.DivMmc.AutomapHeld && !retn.DivMmc.NmiActive,
            "An ordinary RETN did not release the held DivMMC automap mapping.");
    }

    private static void VerifyDivMmcRegisterResetState()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        Require(machine.Registers.Read(0x0A) == 0x01
                && machine.Registers.Read(0xB8) == ZxNextDivMmcDevice.ResetEntryPoints0
                && machine.Registers.Read(0xB9) == ZxNextDivMmcDevice.ResetEntryPointsValid0
                && machine.Registers.Read(0xBA) == ZxNextDivMmcDevice.ResetEntryPointsTiming0
                && machine.Registers.Read(0xBB) == ZxNextDivMmcDevice.ResetEntryPoints1,
            "DivMMC NextRegs did not expose their pinned reset values.");

        machine.Registers.Write(0x0A, 0xD9);
        machine.Registers.Write(0x03, 0x03);
        machine.Registers.Write(0xB8, 0x12);
        machine.Registers.Write(0xB9, 0x34);
        machine.Registers.Write(0xBA, 0x56);
        machine.Registers.Write(0xBB, 0x78);
        machine.PortBus.WriteUncontended(0x00E3, 0xCF);
        machine.Reset(ZxNextResetKind.Soft);
        Require(machine.PortBus.ReadUncontended(0x00E3) == 0
                && machine.Registers.Read(0x0A) == 0xD9
                && machine.Registers.Read(0xB8) == ZxNextDivMmcDevice.ResetEntryPoints0
                && machine.Registers.Read(0xB9) == ZxNextDivMmcDevice.ResetEntryPointsValid0
                && machine.Registers.Read(0xBA) == ZxNextDivMmcDevice.ResetEntryPointsTiming0
                && machine.Registers.Read(0xBB) == ZxNextDivMmcDevice.ResetEntryPoints1,
            "Soft reset did not clear $E3, preserve hard-reset $0A state and restore automap entry defaults.");

        machine.Reset(ZxNextResetKind.Hard);
        Require(machine.Registers.Read(0x0A) == 0x01 && !machine.DivMmc.AutomapEnabled,
            "Hard reset did not restore the DivMMC automap enable default.");
    }

    private static void VerifyNextSpiAndSdCard()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(), $"ZedExEss-zxnext-sd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string sd0Path = Path.Combine(temporaryDirectory, "sd0.img");
        string sd1Path = Path.Combine(temporaryDirectory, "sd1.img");

        byte[] sd0Image = new byte[1024];
        byte[] sd1Image = new byte[1024];
        for (int index = 0; index < 512; index++)
        {
            sd0Image[index] = (byte)((index * 17) ^ 0xA5);
            sd1Image[index] = (byte)((index * 29) ^ 0x5A);
        }

        File.WriteAllBytes(sd0Path, sd0Image);
        File.WriteAllBytes(sd1Path, sd1Image);
        SpectrumDivMmcSdCard sd0 = SpectrumDivMmcSdCard.Open(sd0Path, writeProtected: false);
        SpectrumDivMmcSdCard sd1 = SpectrumDivMmcSdCard.Open(sd1Path, writeProtected: false);

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        try
        {
            machine.DivMmc.AttachSdCard(0, sd0);
            machine.DivMmc.AttachSdCard(1, sd1);

            Require(machine.DivMmc.CardSelectRegister == 0xFF
                    && machine.DivMmc.SelectedSdCard == -1
                    && machine.PortBus.ReadUncontended(0x00E7) == 0xFF
                    && machine.PortBus.ReadUncontended(0x12EB) == 0xFF,
                "Next SPI ports did not reset deselected or use the pinned open-bus behavior.");

            machine.PortBus.WriteUncontended(0x12E7, 0x02);
            Require(machine.DivMmc.CardSelectRegister == 0xFE && machine.DivMmc.SelectedSdCard == 0,
                "$E7 did not normalize a low-bit SD0 selection or decode by low byte.");
            InitializeSdCard(machine);
            byte[] sd0Sector = ReadSdSector(machine, 0);
            Require(sd0Image.AsSpan(0, 512).SequenceEqual(sd0Sector),
                "SD0 sector data did not pass through the Next $E7/$EB port path.");

            machine.Registers.Write(0x0A, 0x21);
            Require(machine.DivMmc.SdCardSwap && machine.DivMmc.CardSelectRegister == 0xFE,
                "Changing the SD swap bit incorrectly changed the active chip-select pins.");
            machine.PortBus.WriteUncontended(0x00E7, 0x02);
            Require(machine.DivMmc.CardSelectRegister == 0xFD && machine.DivMmc.SelectedSdCard == 1,
                "NextReg $0A SD swap did not affect a future logical SD0 selection.");
            InitializeSdCard(machine);
            byte[] sd1Sector = ReadSdSector(machine, 0);
            Require(sd1Image.AsSpan(0, 512).SequenceEqual(sd1Sector),
                "SD swap selected the wrong physical card.");

            machine.Registers.Write(0x03, 0x03);
            machine.Registers.Write(0x0A, 0x01);
            Require(machine.Registers.Read(0x0A) == 0x21 && machine.DivMmc.SdCardSwap,
                "SD swap was writable outside configuration mode.");
            machine.PortBus.WriteUncontended(0x00E7, 0x01);
            Require(machine.DivMmc.CardSelectRegister == 0xFE && machine.DivMmc.SelectedSdCard == 0,
                "A swapped logical SD1 selection did not return to physical SD0.");

            byte[] writeSector = new byte[512];
            for (int index = 0; index < writeSector.Length; index++)
            {
                writeSector[index] = (byte)(index ^ 0xC3);
            }

            SendSdCommand(machine, 24, 1);
            Require(ReadSdR1(machine) == 0x00, "CMD24 was rejected by writable SD0 media.");
            WriteSpi(machine, 0xFE);
            foreach (byte value in writeSector)
            {
                WriteSpi(machine, value);
            }

            WriteSpi(machine, 0xFF);
            WriteSpi(machine, 0xFF);
            Require(ReadSpi(machine) == 0x05 && ReadSpi(machine) == 0x00 && ReadSpi(machine) == 0xFF,
                "SD0 single-block write did not return accepted/busy/ready status.");
            Require(writeSector.SequenceEqual(ReadSdSector(machine, 1)),
                "CMD24 data was not readable through a following CMD17.");

            WriteSpi(machine, 0x51);
            WriteSpi(machine, 0x00);
            machine.Reset(ZxNextResetKind.Soft);
            Require(machine.DivMmc.CardSelectRegister == 0xFF
                    && machine.DivMmc.SelectedSdCard == -1
                    && machine.DivMmc.SdCardSwap,
                "Soft reset did not deselect SPI media while preserving hard-reset SD swap state.");
            machine.PortBus.WriteUncontended(0x00E7, 0x01);
            Require(writeSector.SequenceEqual(ReadSdSector(machine, 1)),
                "Soft reset did not cancel partial SPI framing or retain attached media.");

            machine.Reset(ZxNextResetKind.Hard);
            Require(machine.DivMmc.CardSelectRegister == 0xFF && !machine.DivMmc.SdCardSwap,
                "Hard reset did not restore the SPI selector and SD swap defaults.");
            machine.PortBus.WriteUncontended(0x00E7, 0xFE);
            machine.DivMmc.AttachSdCard(0, null);
            Require(ReadSpi(machine) == 0xFF,
                "Ejecting the selected card did not leave MISO high.");
            machine.DivMmc.AttachSdCard(0, sd0);
            SendSdCommand(machine, 0, 0, crc: 0x95);
            Require(ReadSdR1(machine) == 0x01,
                "Reattaching media did not reset the card to its power-on idle state.");

            machine.PortBus.WriteUncontended(0x00E7, 0xF7);
            Require(machine.DivMmc.CardSelectRegister == 0xF7
                    && machine.DivMmc.SelectedSdCard == -1
                    && ReadSpi(machine) == 0xFF,
                "Selecting an unavailable non-SD SPI target did not deselect both cards.");
        }
        finally
        {
            machine.DivMmc.AttachSdCard(0, null);
            machine.DivMmc.AttachSdCard(1, null);
            sd0.Dispose();
            sd1.Dispose();
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static void InitializeSdCard(ZxNextMachine machine)
    {
        SendSdCommand(machine, 0, 0, crc: 0x95);
        Require(ReadSdR1(machine) == 0x01, "CMD0 did not put selected SD media into idle state.");
        SendSdCommand(machine, 1, 0);
        Require(ReadSdR1(machine) == 0x00, "CMD1 did not initialize selected SD media.");
    }


    /// <summary>
    /// Writing the guest's changes back into the folder it was projected from has to
    /// change what the guest changed and nothing else.
    /// </summary>
    /// <remarks>
    /// This is the check that stands between a saved game and a wrecked NextZXOS install.
    /// The folder is the user's, mostly full of files the guest never opened, so the
    /// things asserted here are as much about what is left alone as what is written: an
    /// untouched file keeps its bytes *and* its timestamp, a long filename survives the
    /// 8.3 round trip, and the configuration the emulator injected is never materialised
    /// as though the user had put it there.
    /// </remarks>
    private static void VerifySdFolderMerge()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ZedExEss-sd-merge-{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "card");
        string machines = Path.Combine(folder, "machines", "next");
        string guestFolder = Path.Combine(root, "guest");
        string guestMachines = Path.Combine(guestFolder, "machines", "next");
        string image = Path.Combine(root, "projection.img");
        string guestImage = Path.Combine(root, "guest.img");
        const string LongName = "Crystal Kingdom Dizzy (1992)(Codemasters).tap";

        Directory.CreateDirectory(machines);
        Directory.CreateDirectory(guestMachines);
        try
        {
            // The folder as the user has it.
            File.WriteAllText(Path.Combine(machines, "keep.txt"), "untouched", Encoding.ASCII);
            File.WriteAllText(Path.Combine(machines, "change.txt"), "before", Encoding.ASCII);
            File.WriteAllText(Path.Combine(machines, "delete.txt"), "doomed", Encoding.ASCII);
            File.WriteAllText(Path.Combine(folder, LongName), "tape data", Encoding.ASCII);
            File.WriteAllText(Path.Combine(machines, "config.ini"), "timing=8\r\n", Encoding.ASCII);

            var overlay = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["machines/next/config.ini"] = Encoding.ASCII.GetBytes("timing=7\r\n")
            };

            SpectrumFatImageBuilder.CreateBlankFat16Image(image, 16);
            SpectrumFatProjectionManifest manifest =
                SpectrumFatImageBuilder.ImportDirectoryIntoFat16Image(image, folder, overlay);

            Require(manifest.OverlayFiles.Contains("machines/next/config.ini"),
                "The manifest did not record the injected configuration as an overlay.");
            Require(manifest.ImportedFiles.Contains("machines/next/keep.txt"),
                "The manifest did not record the files it imported.");
            Require(!manifest.ImportedFiles.Contains("machines/next/config.ini"),
                "An overlaid path was recorded as though it came from the folder.");

            // What the guest leaves behind: one file edited, one added, one deleted.
            File.WriteAllText(Path.Combine(guestMachines, "keep.txt"), "untouched", Encoding.ASCII);
            File.WriteAllText(Path.Combine(guestMachines, "change.txt"), "after", Encoding.ASCII);
            File.WriteAllText(Path.Combine(guestMachines, "saved.sna"), "new save", Encoding.ASCII);
            File.WriteAllText(Path.Combine(guestFolder, LongName), "tape data", Encoding.ASCII);
            File.WriteAllText(Path.Combine(guestMachines, "config.ini"), "timing=7\r\n", Encoding.ASCII);
            SpectrumFatImageBuilder.CreateBlankFat16Image(guestImage, 16);
            SpectrumFatImageBuilder.ImportDirectoryIntoFat16Image(guestImage, guestFolder);

            string keepPath = Path.Combine(machines, "keep.txt");
            DateTime keepWrittenAt = File.GetLastWriteTimeUtc(keepPath);
            Thread.Sleep(20);

            SpectrumFatMergeResult result =
                SpectrumFatImageBuilder.MergeFat16ImageIntoDirectory(guestImage, folder, manifest);

            Require(File.ReadAllText(Path.Combine(machines, "change.txt"), Encoding.ASCII) == "after",
                "An edited file was not written back.");
            Require(File.Exists(Path.Combine(machines, "saved.sna")),
                "A file the guest created was not written back.");
            Require(!File.Exists(Path.Combine(machines, "delete.txt")),
                "A file the guest deleted was left behind.");

            Require(File.ReadAllText(keepPath, Encoding.ASCII) == "untouched",
                "An untouched file was corrupted by the merge.");
            Require(File.GetLastWriteTimeUtc(keepPath) == keepWrittenAt,
                "An untouched file was rewritten, which would reset every timestamp in the folder.");

            Require(File.ReadAllText(Path.Combine(folder, LongName), Encoding.ASCII) == "tape data",
                "A long filename did not survive the projection round trip.");

            // The injected configuration must not replace what the user wrote.
            Require(File.ReadAllText(Path.Combine(machines, "config.ini"), Encoding.ASCII) == "timing=8\r\n",
                "The emulator's injected configuration was written into the user's folder.");

            Require(result.Written == 2,
                $"The merge wrote {result.Written} files, expected the two that changed.");
            Require(result.Deleted == 1, $"The merge deleted {result.Deleted} files, expected one.");
            Require(result.Unchanged >= 2,
                $"The merge only recognised {result.Unchanged} files as unchanged.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// A projected folder can now be both written back to and overlaid, which it could
    /// not before, and a snapshot still refuses to persist.
    /// </summary>
    private static void VerifySdFolderPersistencePolicy()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ZedExEss-sd-policy-{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "card");
        string machines = Path.Combine(folder, "machines", "next");
        Directory.CreateDirectory(machines);
        try
        {
            File.WriteAllText(Path.Combine(machines, "keep.txt"), "data", Encoding.ASCII);
            var overlay = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase)
            {
                ["machines/next/config.ini"] = Encoding.ASCII.GetBytes("timing=7\r\n")
            };

            using (SpectrumDivMmcSdCard persistent = SpectrumDivMmcSdCard.OpenFolderBacked(
                folder,
                writeProtected: false,
                overlayFiles: overlay,
                writeBackChanges: true))
            {
                Require(persistent.WritesBackToFolder,
                    "A persistent folder-backed card did not report that it writes back.");
                Require(!persistent.HasUnflushedChanges,
                    "A freshly opened card claimed to have changes waiting.");

                // Nothing written, so flushing must leave the folder exactly as it is.
                persistent.FlushFolderBacking();
                Require(!File.Exists(Path.Combine(machines, "config.ini")),
                    "Flushing an unmodified card materialised the injected configuration.");
                Require(File.ReadAllText(Path.Combine(machines, "keep.txt"), Encoding.ASCII) == "data",
                    "Flushing an unmodified card disturbed the folder.");
            }

            using SpectrumDivMmcSdCard snapshot =
                SpectrumDivMmcSdCard.OpenWritableFolderSnapshot(folder, overlay);
            Require(!snapshot.WritesBackToFolder,
                "A writable snapshot must still discard everything the guest writes.");
            Require(!snapshot.HasUnflushedChanges,
                "A snapshot reported changes waiting to be written back.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static void VerifyFat16FolderOverlay()
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(), $"ZedExEss-zxnext-overlay-{Guid.NewGuid():N}");
        string sourceDirectory = Path.Combine(temporaryDirectory, "source");
        string machinesDirectory = Path.Combine(sourceDirectory, "machines", "next");
        string exportDirectory = Path.Combine(temporaryDirectory, "export");
        string imagePath = Path.Combine(temporaryDirectory, "projection.img");
        Directory.CreateDirectory(machinesDirectory);
        try
        {
            string sourceConfigPath = Path.Combine(machinesDirectory, "config.ini");
            File.WriteAllText(sourceConfigPath, "timing=8\r\n", Encoding.ASCII);
            File.WriteAllText(
                Path.Combine(machinesDirectory, "keep.txt"),
                "source data",
                Encoding.ASCII);
            const string longTapeName =
                "Dizzy_VII_-_Crystal_Kindom_Dizzy__2017__v1.0.5.tap";
            File.WriteAllText(
                Path.Combine(machinesDirectory, longTapeName),
                "long filename data",
                Encoding.ASCII);
            string longDirectory = Path.Combine(sourceDirectory, "Next BASIC Assets");
            Directory.CreateDirectory(longDirectory);
            File.WriteAllText(
                Path.Combine(longDirectory, "MixedCaseLoader.bas"),
                "loader data",
                Encoding.ASCII);
            var overlays = new Dictionary<string, ReadOnlyMemory<byte>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["machines/next/config.ini"] = Encoding.ASCII.GetBytes("timing=7\r\n"),
                ["machines/next/virtual.txt"] = Encoding.ASCII.GetBytes("virtual data")
            };

            SpectrumFatImageBuilder.CreateBlankFat16Image(imagePath, 16);
            SpectrumFatImageBuilder.ImportDirectoryIntoFat16Image(
                imagePath,
                sourceDirectory,
                overlays);
            SpectrumFatImageBuilder.ExportFat16ImageToDirectory(imagePath, exportDirectory);

            Require(File.ReadAllText(sourceConfigPath, Encoding.ASCII) == "timing=8\r\n",
                "FAT16 overlay projection modified the source folder.");
            Require(File.ReadAllText(
                    Path.Combine(exportDirectory, "machines", "next", "config.ini"),
                    Encoding.ASCII) == "timing=7\r\n",
                "A FAT16 overlay did not replace the projected source file.");
            Require(File.ReadAllText(
                    Path.Combine(exportDirectory, "machines", "next", "virtual.txt"),
                    Encoding.ASCII) == "virtual data",
                "A virtual FAT16 overlay file was not added to the projected directory.");
            Require(File.ReadAllText(
                    Path.Combine(exportDirectory, "machines", "next", "keep.txt"),
                    Encoding.ASCII) == "source data",
                "Overlay projection omitted an unchanged source file.");
            Require(Directory.EnumerateFiles(Path.Combine(exportDirectory, "machines", "next"))
                    .Select(Path.GetFileName)
                    .Contains("keep.txt", StringComparer.Ordinal),
                "The FAT16 projection did not retain canonical filename case in its VFAT entry.");
            Require(File.ReadAllText(
                    Path.Combine(exportDirectory, "machines", "next", longTapeName),
                    Encoding.ASCII) == "long filename data",
                "The FAT16 projection did not preserve a loader-visible VFAT long filename.");
            Require(File.ReadAllText(
                    Path.Combine(exportDirectory, "Next BASIC Assets", "MixedCaseLoader.bas"),
                    Encoding.ASCII) == "loader data",
                "The FAT16 projection did not preserve long directory or mixed-case BASIC asset names.");

            ZxNextMachine snapshotMachine = ZxNextMachineFactory.CreateFoundationMachine();
            using (SpectrumDivMmcSdCard snapshot =
                   SpectrumDivMmcSdCard.OpenWritableFolderSnapshot(sourceDirectory))
            {
                Require(!snapshot.WriteProtected && !snapshot.WritesBackToFolder,
                    "A writable folder snapshot exposed the wrong persistence policy.");
                snapshotMachine.DivMmc.AttachSdCard(0, snapshot);
                snapshotMachine.PortBus.WriteUncontended(0x00E7, 0x02);
                InitializeSdCard(snapshotMachine);
                byte[] mbr = ReadSdSector(snapshotMachine, 0);
                uint partitionStart = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(0x1C6, 4));
                byte[] bootSector = ReadSdSector(snapshotMachine, partitionStart);
                ushort reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.AsSpan(14, 2));
                byte fatCount = bootSector[16];
                ushort sectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.AsSpan(22, 2));
                uint rootDirectoryStart = partitionStart
                    + reservedSectors
                    + ((uint)fatCount * sectorsPerFat);
                WriteSdSector(snapshotMachine, rootDirectoryStart, new byte[512]);
                Require(ReadSdSector(snapshotMachine, rootDirectoryStart).All(static value => value == 0),
                    "The disposable System/Next policy did not accept guest sector writes.");
                snapshotMachine.DivMmc.AttachSdCard(0, null);
            }

            Require(File.ReadAllText(sourceConfigPath, Encoding.ASCII) == "timing=8\r\n"
                    && File.Exists(Path.Combine(machinesDirectory, longTapeName)),
                "Disposing a writable folder snapshot propagated guest changes into its source tree.");
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static byte[] ReadSdSector(ZxNextMachine machine, uint lba)
    {
        SendSdCommand(machine, 17, lba);
        Require(ReadSdR1(machine) == 0x00, $"CMD17 rejected sector {lba}.");

        byte token = 0xFF;
        for (int attempt = 0; attempt < 16 && token == 0xFF; attempt++)
        {
            token = ReadSpi(machine);
        }

        Require(token == 0xFE, $"CMD17 sector {lba} did not return a data token.");
        byte[] sector = new byte[512];
        for (int index = 0; index < sector.Length; index++)
        {
            sector[index] = ReadSpi(machine);
        }

        _ = ReadSpi(machine);
        _ = ReadSpi(machine);
        return sector;
    }

    private static void WriteSdSector(ZxNextMachine machine, uint lba, byte[] data)
    {
        Require(data.Length == 512, "An SD verification write must contain exactly one sector.");
        SendSdCommand(machine, 24, lba);
        Require(ReadSdR1(machine) == 0x00, $"CMD24 rejected sector {lba}.");
        WriteSpi(machine, 0xFE);
        foreach (byte value in data)
        {
            WriteSpi(machine, value);
        }

        WriteSpi(machine, 0xFF);
        WriteSpi(machine, 0xFF);
        Require(ReadSpi(machine) == 0x05 && ReadSpi(machine) == 0x00 && ReadSpi(machine) == 0xFF,
            $"CMD24 sector {lba} did not return accepted/busy/ready status.");
    }

    private static void SendSdCommand(ZxNextMachine machine, byte command, uint argument, byte crc = 0xFF)
    {
        WriteSpi(machine, (byte)(0x40 | command));
        WriteSpi(machine, (byte)(argument >> 24));
        WriteSpi(machine, (byte)(argument >> 16));
        WriteSpi(machine, (byte)(argument >> 8));
        WriteSpi(machine, (byte)argument);
        WriteSpi(machine, crc);
    }

    private static byte ReadSdR1(ZxNextMachine machine)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            byte value = ReadSpi(machine);
            if (value != 0xFF)
            {
                return value;
            }
        }

        return 0xFF;
    }

    private static byte ReadSpi(ZxNextMachine machine) =>
        machine.PortBus.ReadUncontended(0x00EB);

    private static void WriteSpi(ZxNextMachine machine, byte value) =>
        machine.PortBus.WriteUncontended(0x00EB, value);

    private static void VerifyZ80NArithmetic()
    {
        ZxNextMachine swap = CreateProgram(0xED, 0x23);
        swap.Cpu.A = 0x3C;
        swap.Cpu.SetFlags(0xA5);
        swap.StepInstruction();
        Require(swap.Cpu.A == 0xC3 && swap.Cpu.GetFlags() == 0xA5 && swap.Cpu.Cyc == 8,
            "SWAPNIB result, flags or timing is incorrect.");

        ZxNextMachine mirror = CreateProgram(0xED, 0x24);
        mirror.Cpu.A = 0x96;
        mirror.Cpu.SetFlags(0x5A);
        mirror.StepInstruction();
        Require(mirror.Cpu.A == 0x69 && mirror.Cpu.GetFlags() == 0x5A,
            "MIRROR A changed flags or mirrored the wrong bit order.");

        ZxNextMachine test = CreateProgram(0xED, 0x27, 0x0F);
        test.Cpu.A = 0xF0;
        test.StepInstruction();
        Require(test.Cpu.A == 0xF0 && test.Cpu.GetFlags() == 0x54 && test.Cpu.Cyc == 11,
            "TEST did not apply AND flags without changing A.");

        ZxNextMachine multiply = CreateProgram(0xED, 0x30);
        multiply.Cpu.D = 12;
        multiply.Cpu.E = 20;
        multiply.Cpu.SetFlags(0xD7);
        multiply.StepInstruction();
        Require(multiply.Cpu.DE == 240 && multiply.Cpu.GetFlags() == 0xD7 && multiply.Cpu.Cyc == 8,
            "MUL D,E result, flags or timing is incorrect.");

        ZxNextMachine addA = CreateProgram(0xED, 0x31);
        addA.Cpu.H = 0xFF;
        addA.Cpu.L = 0xF0;
        addA.Cpu.A = 0x20;
        addA.Cpu.SetFlags(0xFF);
        addA.StepInstruction();
        Require(addA.Cpu.HL == 0x0010 && addA.Cpu.GetFlags() == 0xFE,
            "Pinned-core ADD HL,A did not clear carry while preserving other flags.");

        ZxNextMachine addPairs = CreateProgram(
            0xED, 0x32,
            0xED, 0x33,
            0xED, 0x35, 0x02, 0x01,
            0xED, 0x36, 0x04, 0x03);
        addPairs.Cpu.A = 1;
        addPairs.Cpu.D = 0x10;
        addPairs.Cpu.B = 0x20;
        addPairs.Cpu.SetFlags(0x01);
        addPairs.StepInstruction();
        addPairs.StepInstruction();
        addPairs.StepInstruction();
        addPairs.StepInstruction();
        Require(addPairs.Cpu.DE == 0x1103 && addPairs.Cpu.BC == 0x2305
                && addPairs.Cpu.GetFlags() == 0 && addPairs.Cpu.Cyc == 48,
            "ADD DE/BC,A or ADD DE/BC,nn produced an incorrect result, flags or timing.");
    }

    private static void VerifyZ80NBarrelOperations()
    {
        VerifyBarrel(0x28, 0x0001, 4, 0x0010, "BSLA");
        VerifyBarrel(0x29, 0x8000, 4, 0xF800, "BSRA");
        VerifyBarrel(0x2A, 0x8000, 4, 0x0800, "BSRL");
        VerifyBarrel(0x2B, 0x0000, 4, 0xF000, "BSRF");
        VerifyBarrel(0x2C, 0x8001, 1, 0x0003, "BRLC");

        static void VerifyBarrel(byte opcode, ushort input, byte count, ushort expected, string name)
        {
            ZxNextMachine machine = CreateProgram(0xED, opcode);
            machine.Cpu.D = (byte)(input >> 8);
            machine.Cpu.E = (byte)input;
            machine.Cpu.B = count;
            machine.Cpu.SetFlags(0xA5);
            machine.StepInstruction();
            Require(machine.Cpu.DE == expected && machine.Cpu.GetFlags() == 0xA5 && machine.Cpu.Cyc == 8,
                $"{name} result, flags or timing is incorrect.");
        }
    }

    private static void VerifyZ80NImmediateOperations()
    {
        ZxNextMachine add = CreateProgram(0xED, 0x34, 0x34, 0x12);
        add.Cpu.H = 0x10;
        add.Cpu.L = 0x00;
        add.Cpu.SetFlags(0xA5);
        add.StepInstruction();
        Require(add.Cpu.HL == 0x2234 && add.Cpu.GetFlags() == 0xA5 && add.Cpu.Cyc == 16,
            "ADD HL,nn decoded the wrong byte order or altered flags/timing.");

        ZxNextMachine push = CreateProgram(0xED, 0x8A, 0x12, 0x34);
        push.Cpu.SP = 0xFFFE;
        push.StepInstruction();
        Require(push.Cpu.SP == 0xFFFC && push.MemoryBus.Read(0xFFFC) == 0x34
                && push.MemoryBus.Read(0xFFFD) == 0x12 && push.Cpu.Cyc == 23,
            "PUSH nn did not honor the Z80N-only high/low immediate encoding.");
    }

    private static void VerifyZ80NPixelOperations()
    {
        ZxNextMachine pixelAddress = CreateProgram(0xED, 0x94);
        pixelAddress.Cpu.D = 191;
        pixelAddress.Cpu.E = 255;
        pixelAddress.StepInstruction();
        Require(pixelAddress.Cpu.HL == 0x57FF, "PIXELAD produced the wrong ULA address.");

        ZxNextMachine pixelDown = CreateProgram(0xED, 0x93);
        pixelDown.Cpu.H = 0x47;
        pixelDown.Cpu.L = 0xE0;
        pixelDown.StepInstruction();
        Require(pixelDown.Cpu.HL == 0x4800, "PIXELDN failed at a character-third boundary.");

        ZxNextMachine setAe = CreateProgram(0xED, 0x95);
        setAe.Cpu.E = 3;
        setAe.StepInstruction();
        Require(setAe.Cpu.A == 0x10, "SETAE produced the wrong pixel mask.");
    }

    private static void VerifyZ80NIoOperations()
    {
        ZxNextMachine output = CreateProgram(0xED, 0x90);
        output.Cpu.B = 0x12;
        output.Cpu.C = 0x34;
        output.Cpu.H = 0x10;
        output.Cpu.L = 0x00;
        output.MemoryBus.WriteCpu(0x1000, 0xA5);
        output.StepInstruction();
        Require(output.PortBus.LastWrittenPort == 0x1234 && output.PortBus.LastWrittenValue == 0xA5
                && output.Cpu.B == 0x12 && output.Cpu.HL == 0x1001 && output.Cpu.Cyc == 16,
            "OUTINB port, source increment, B preservation or timing is incorrect.");

        ZxNextMachine jump = CreateProgram(0xED, 0x98);
        jump.Cpu.B = 0x12;
        jump.Cpu.C = 0x35;
        jump.StepInstruction();
        Require(jump.Cpu.PC == 0x3FC0 && jump.Cpu.Cyc == 13,
            "JP (C) did not compose PC from the open-bus byte and current 16K region.");
    }

    private static void VerifyZ80NBlockTransfers()
    {
        ZxNextMachine ldix = CreateProgram(0xED, 0xA4);
        ldix.Cpu.A = 0;
        ldix.Cpu.B = 0;
        ldix.Cpu.C = 1;
        ldix.Cpu.H = 0x10;
        ldix.Cpu.L = 0;
        ldix.Cpu.D = 0x20;
        ldix.Cpu.E = 0;
        ldix.Cpu.SetFlags(0xC1);
        ldix.MemoryBus.WriteCpu(0x1000, 0xA5);
        ldix.StepInstruction();
        Require(ldix.MemoryBus.Read(0x2000) == 0xA5 && ldix.Cpu.HL == 0x1001
                && ldix.Cpu.DE == 0x2001 && ldix.Cpu.BC == 0 && ldix.Cpu.GetFlags() == 0xC1
                && ldix.Cpu.Cyc == 16,
            "LDIX transfer, flags or timing is incorrect.");

        ZxNextMachine excluded = CreateProgram(0xED, 0xA4);
        excluded.Cpu.A = 0xA5;
        excluded.Cpu.B = 0;
        excluded.Cpu.C = 1;
        excluded.Cpu.H = 0x10;
        excluded.Cpu.D = 0x20;
        excluded.MemoryBus.WriteCpu(0x1000, 0xA5);
        excluded.MemoryBus.WriteCpu(0x2000, 0x5A);
        excluded.StepInstruction();
        Require(excluded.MemoryBus.Read(0x2000) == 0x5A && excluded.Cpu.Cyc == 16,
            "LDIX exclusion removed the destination cycle or wrote the excluded byte.");

        ZxNextMachine repeat = CreateProgram(0xED, 0xB4);
        repeat.Cpu.A = 0;
        repeat.Cpu.B = 0;
        repeat.Cpu.C = 2;
        repeat.Cpu.H = 0x10;
        repeat.Cpu.D = 0x20;
        repeat.MemoryBus.WriteCpu(0x1000, 0x11);
        repeat.MemoryBus.WriteCpu(0x1001, 0x22);
        repeat.StepInstruction();
        Require(repeat.Cpu.PC == 0 && repeat.Cpu.Cyc == 21 && repeat.Cpu.BC == 1,
            "LDIRX repeating iteration did not rewind PC in 21 T-states.");
        repeat.StepInstruction();
        Require(repeat.Cpu.PC == 2 && repeat.Cpu.Cyc == 37 && repeat.Cpu.BC == 0
                && repeat.MemoryBus.Read(0x2001) == 0x22,
            "LDIRX final iteration or 21/16 timing is incorrect.");

        ZxNextMachine lddx = CreateProgram(0xED, 0xAC);
        lddx.Cpu.A = 0;
        lddx.Cpu.B = 0;
        lddx.Cpu.C = 1;
        lddx.Cpu.H = 0x10;
        lddx.Cpu.L = 1;
        lddx.Cpu.D = 0x20;
        lddx.MemoryBus.WriteCpu(0x1001, 0x33);
        lddx.StepInstruction();
        Require(lddx.MemoryBus.Read(0x2000) == 0x33 && lddx.Cpu.HL == 0x1000
                && lddx.Cpu.DE == 0x2001 && lddx.Cpu.Cyc == 16,
            "LDDX did not decrement HL while incrementing DE.");

        ZxNextMachine lddrx = CreateProgram(0xED, 0xBC);
        lddrx.Cpu.A = 0;
        lddrx.Cpu.B = 0;
        lddrx.Cpu.C = 2;
        lddrx.Cpu.H = 0x10;
        lddrx.Cpu.L = 1;
        lddrx.Cpu.D = 0x20;
        lddrx.MemoryBus.WriteCpu(0x1001, 0x44);
        lddrx.MemoryBus.WriteCpu(0x1000, 0x55);
        lddrx.StepInstruction();
        lddrx.StepInstruction();
        Require(lddrx.Cpu.Cyc == 37 && lddrx.Cpu.HL == 0x0FFF && lddrx.Cpu.DE == 0x2002
                && lddrx.MemoryBus.Read(0x2000) == 0x44 && lddrx.MemoryBus.Read(0x2001) == 0x55,
            "LDDRX repeat direction or 21/16 timing is incorrect.");

        ZxNextMachine pattern = CreateProgram(0xED, 0xB7);
        pattern.Cpu.A = 0;
        pattern.Cpu.B = 0;
        pattern.Cpu.C = 1;
        pattern.Cpu.H = 0x10;
        pattern.Cpu.L = 7;
        pattern.Cpu.D = 0x20;
        pattern.Cpu.E = 3;
        pattern.MemoryBus.WriteCpu(0x1003, 0x66);
        pattern.StepInstruction();
        Require(pattern.MemoryBus.Read(0x2003) == 0x66 && pattern.Cpu.HL == 0x1007
                && pattern.Cpu.DE == 0x2004 && pattern.Cpu.Cyc == 16,
            "LDPIRX did not combine the HL table base with the low three DE bits.");

        ZxNextMachine ldws = CreateProgram(0xED, 0xA5);
        ldws.Cpu.H = 0x10;
        ldws.Cpu.L = 0xFF;
        ldws.Cpu.D = 0x20;
        ldws.Cpu.E = 0;
        ldws.MemoryBus.WriteCpu(0x10FF, 0x7E);
        ldws.StepInstruction();
        Require(ldws.MemoryBus.Read(0x2000) == 0x7E && ldws.Cpu.HL == 0x1000
                && ldws.Cpu.DE == 0x2100 && ldws.Cpu.Cyc == 14,
            "LDWS did not increment only L and D with 14-T-state timing.");
    }

    private static void VerifyZ80NDeveloperTools()
    {
        const string source = """
            SWAPNIB
            MIRROR A
            TEST $5A
            BSLA DE,B
            BSRA DE,B
            BSRL DE,B
            BSRF DE,B
            BRLC DE,B
            MUL D,E
            ADD HL,A
            ADD DE,A
            ADD BC,A
            ADD HL,$1234
            ADD DE,$1234
            ADD BC,$1234
            PUSH $BEEF
            OUTINB
            NEXTREG $07,$03
            NEXTREG $07,A
            PIXELDN
            PIXELAD
            SETAE
            JP (C)
            LDIX
            LDWS
            LDDX
            LDIRX
            LDPIRX
            LDDRX
            """;

        byte[] expected =
        [
            0xED, 0x23, 0xED, 0x24, 0xED, 0x27, 0x5A,
            0xED, 0x28, 0xED, 0x29, 0xED, 0x2A, 0xED, 0x2B, 0xED, 0x2C,
            0xED, 0x30, 0xED, 0x31, 0xED, 0x32, 0xED, 0x33,
            0xED, 0x34, 0x34, 0x12, 0xED, 0x35, 0x34, 0x12, 0xED, 0x36, 0x34, 0x12,
            0xED, 0x8A, 0xBE, 0xEF, 0xED, 0x90,
            0xED, 0x91, 0x07, 0x03, 0xED, 0x92, 0x07,
            0xED, 0x93, 0xED, 0x94, 0xED, 0x95, 0xED, 0x98,
            0xED, 0xA4, 0xED, 0xA5, 0xED, 0xAC, 0xED, 0xB4, 0xED, 0xB7, 0xED, 0xBC
        ];

        var assembler = new Z80InlineAssembler(enableZ80N: true);
        Z80AssemblyResult result = assembler.Assemble(0, source);
        Require(result.Success, $"Z80N assembly failed: {result.Error}");
        Require(result.Bytes.SequenceEqual(expected), "Z80N assembly emitted an incorrect opcode sequence.");

        ZxNextMachine machine = CreateProgram(expected);

        var disassembler = new Z80Disassembler(enableZ80N: true);
        Require(disassembler.Disassemble(machine.MemoryBus, 0).Text == "SWAPNIB",
            "Z80N disassembly did not recognize SWAPNIB.");
        Require(disassembler.Disassemble(machine.MemoryBus, 4).Text == "TEST $5A"
                && disassembler.Disassemble(machine.MemoryBus, 25).Text == "ADD HL,$1234"
                && disassembler.Disassemble(machine.MemoryBus, 37).Text == "PUSH $BEEF",
            "Z80N disassembly decoded an immediate operand incorrectly.");

        Require(new Z80Disassembler().Disassemble(machine.MemoryBus, 0).Text == "ED 23",
            "The ordinary Z80 disassembler enabled Z80N without an explicit request.");
        Require(!new Z80InlineAssembler().Assemble(0, "SWAPNIB").Success,
            "The ordinary Z80 assembler enabled Z80N without an explicit request.");
    }

    private static void VerifyNextRegTurboTiming()
    {
        ZxNextMachine machine = CreateProgram(
            0xED, 0x91, 0x07, 0x01,
            0xED, 0x92, 0x07);
        machine.Cpu.A = 0x02;
        machine.Registers.Write(0x6B, 0x80);
        Require(machine.Cpu.InstructionTstateBatchingEnabled,
            "Enabling the timed tilemap did not enable instruction-bounded scheduling.");

        // Instruction time is published when a device needs it, not at every boundary,
        // so the tail of the instruction is still held by the CPU here. Asking for it is
        // what a reader of the clock has to do; the assertion is unchanged.
        machine.StepInstruction();
        machine.SynchronizeTime();
        Require(machine.Cpu.Cyc == 20 && machine.MasterTicks == 136
                && machine.CpuSpeed == ZxNextCpuSpeed.Mhz7 && machine.CpuClockHz == 7_000_000,
            $"NEXTREG n,n did not switch the remaining instruction interval to 7 MHz exactly "
                + $"(Cyc {machine.Cpu.Cyc}, master {machine.MasterTicks}).");

        machine.StepInstruction();
        machine.SynchronizeTime();
        Require(machine.Cpu.Cyc == 37 && machine.MasterTicks == 192
                && machine.CpuSpeed == ZxNextCpuSpeed.Mhz14 && machine.Registers.Read(0x07) == 0x22,
            $"NEXTREG n,A did not switch the remaining instruction interval to 14 MHz exactly "
                + $"(Cyc {machine.Cpu.Cyc}, master {machine.MasterTicks}).");
    }

    /// <summary>The shared debugger drives a Next machine through its own step hooks.</summary>
    private static void VerifyDebuggerAttach()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        var debugger = new SpectrumDebuggerController();
        debugger.Attach(machine.Cpu, machine.MemoryBus, tstatesPerFrame: 70908, tstatesPerLine: 224);
        machine.ConfigureCpuStepHooks(debugger.BeforeCpuStep, debugger.AfterCpuStep);

        // ED B4 is Z80N LDIRX; only the Z80N table names it, so this proves the
        // debugger is given the extended decoder when a Next is attached.
        machine.MemoryBus.WriteDirect(0x8000, 0xED);
        machine.MemoryBus.WriteDirect(0x8001, 0xB4);
        string z80nText = new Z80Disassembler(enableZ80N: true)
            .Disassemble(machine.MemoryBus, 0x8000).Text;
        string plainText = new Z80Disassembler()
            .Disassemble(machine.MemoryBus, 0x8000).Text;
        Require(z80nText == "LDIRX", $"Z80N disassembly of ED B4 produced '{z80nText}'.");
        Require(plainText != "LDIRX", "The plain Z80 decoder should not know LDIRX.");

        machine.Cpu.PC = 0x8000;
        debugger.AddExecuteBreakpoint(0x9000);
        machine.MemoryBus.WriteDirect(0x9000, 0x00);
        debugger.PrepareRunTo(0x9000);
        Require(!debugger.IsPaused, "Run-to should not begin paused.");

        // The hook has to be able to stop the Next, not merely be installed on it.
        debugger.Pause("Verification");
        Require(debugger.IsPaused, "The debugger did not pause the attached Next machine.");
        ulong before = machine.Cpu.Cyc;
        machine.StepInstruction();
        Require(
            machine.Cpu.Cyc == before,
            "A paused debugger did not stop the Next machine from executing.");
    }

    /// <summary>A NextReg write is not a port write, so it needs its own breakpoint type.</summary>
    private static void VerifyNextRegBreakpoints()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        var debugger = new SpectrumDebuggerController();
        debugger.Attach(machine.Cpu, machine.MemoryBus, tstatesPerFrame: 70908, tstatesPerLine: 224);
        machine.Registers.RegisterWritten = debugger.OnDeviceRegisterWrite;
        debugger.Run();

        // Unqualified: any write to the tilemap control register stops.
        debugger.AddDeviceRegisterBreakpoint(0x6B);
        machine.Registers.Write(0x30, 0x11);
        debugger.AfterCpuStep();
        Require(!debugger.IsPaused, "An unrelated NextReg write raised a breakpoint.");

        machine.Registers.Write(0x6B, 0x80);
        debugger.AfterCpuStep();
        Require(debugger.IsPaused, "A NextReg write did not raise its breakpoint.");
        Require(
            debugger.LastHit?.Type == DebuggerBreakType.DeviceRegisterWrite
                && debugger.LastHit.Value == 0x80,
            "The NextReg break hit did not describe the write.");

        // Value-qualified: the same register with a different value must not stop.
        var valueDebugger = new SpectrumDebuggerController();
        valueDebugger.Attach(machine.Cpu, machine.MemoryBus, 70908, 224);
        machine.Registers.RegisterWritten = valueDebugger.OnDeviceRegisterWrite;
        valueDebugger.Run();
        valueDebugger.AddDeviceRegisterBreakpoint(0x07, value: 0x03);
        machine.Registers.Write(0x07, 0x01);
        valueDebugger.AfterCpuStep();
        Require(!valueDebugger.IsPaused, "A value-qualified NextReg breakpoint matched the wrong value.");
        machine.Registers.Write(0x07, 0x03);
        valueDebugger.AfterCpuStep();
        Require(valueDebugger.IsPaused, "A value-qualified NextReg breakpoint did not match its value.");
    }

    private static void VerifyDebuggerMachineViews()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        var extensions = new ZxNextDebuggerExtensions(machine);
        machine.MemoryBus.SetMmuPage(3, 42);

        IReadOnlyList<DebuggerMemorySlot> slots = extensions.GetMemorySlots();
        Require(slots.Count == ZxNextMemoryBus.MmuSlotCount, "The MMU view did not report eight slots.");
        Require(
            slots[3].Page == 42 && slots[3].StartAddress == 0x6000 && slots[3].EndAddress == 0x7FFF,
            $"MMU slot 3 reported page {slots[3].Page} at {slots[3].RangeText}.");

        machine.Registers.Write(0x07, 0x02);
        IReadOnlyList<DebuggerDeviceRegister> registers = extensions.GetDeviceRegisters();
        Require(registers.Count > 0, "The NextReg view reported no registers.");
        DebuggerDeviceRegister speed = registers.First(register => register.Number == 0x07);
        Require(
            (speed.Value & 0x03) == 0x02,
            $"The NextReg view reported CPU speed ${speed.Value:X2} after selecting 14 MHz.");
        Require(
            registers.Any(register => register.Number == 0x6B),
            "The NextReg view omitted the tilemap control register.");
    }

    /// <summary>All three PSG chips reach the oscilloscope, not only the selected one.</summary>
    private static void VerifyAudioScopeChannels()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        var capture = new AudioScopeCapture(
            AudioScopeLayout.ZxNext.ChannelCount,
            machine.SampleRate);
        machine.Audio.SetScopeCapture(capture);

        // TurboSound off, so chip 0 is the only one being mixed to the speaker; the
        // scope must still separate chips 1 and 2.
        for (int chip = 0; chip < 3; chip++)
        {
            machine.Audio.GetChip(chip).WriteRegister(0x00, 0x40);
            machine.Audio.GetChip(chip).WriteRegister(0x07, 0x38);
            machine.Audio.GetChip(chip).WriteRegister(0x08, 0x0F);
            machine.Audio.GetChip(chip).WriteRegister(0x09, 0x0F);
            machine.Audio.GetChip(chip).WriteRegister(0x0A, 0x0F);
        }

        machine.Audio.AdvanceMasterTicks(ZxNextReferenceBaseline.MasterClockHz / 20);

        short[] window = new short[256];
        for (int chip = 0; chip < 3; chip++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                int index = AudioScopeLayout.GetPsgChannelIndex(chip, channel);
                capture.CopyLatest(index, window);
                Require(
                    window.Any(sample => sample != 0),
                    $"Oscilloscope channel {index} (chip {chip}, channel {channel}) stayed silent.");
            }
        }

        machine.Audio.SetScopeCapture(null);
    }

    private static void VerifyAllCpuSpeedRatios()
    {
        const int cpuTstatesPerSpeed = 4096;
        ZxNextMachine machine = CreateProgram(0x00);
        foreach (ZxNextCpuSpeed speed in Enum.GetValues<ZxNextCpuSpeed>())
        {
            machine.Registers.Write(0x07, (byte)speed);
            machine.RunForTstates(cpuTstatesPerSpeed);
        }

        Require(machine.Cpu.Cyc == 4UL * cpuTstatesPerSpeed && machine.MasterTicks == 15UL * cpuTstatesPerSpeed,
            "Long 3.5/7/14/28 MHz intervals accumulated master-clock drift.");
    }

    private static void VerifyResetAndPause()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.MemoryBus.WritePhysicalRam(42, 7, 0xA5);
        machine.Registers.Write(0x56, 42);
        machine.Paging.WritePort7Ffd(0x25);
        machine.MemoryBus.SelectRomBank(3);
        machine.PortBus.WriteUncontended(ZxNextPortBus.NextRegisterSelectPort, 0x56);
        machine.Registers.Write(0x6B, 0x80);
        Require(machine.Cpu.InstructionTstateBatchingEnabled,
            "The reset fixture did not enter instruction-bounded tilemap scheduling.");
        machine.StepInstruction();
        machine.SetPaused(true);
        ulong pausedCpuTstates = machine.Cpu.Cyc;
        ulong pausedMasterTicks = machine.MasterTicks;
        machine.StepInstruction();
        Require(machine.Cpu.Cyc == pausedCpuTstates && machine.MasterTicks == pausedMasterTicks,
            "A paused foundation machine advanced time.");

        machine.Reset();
        Require(machine.Cpu.Cyc == 0 && machine.Cpu.PC == 0 && machine.MasterTicks == 0 && !machine.IsPaused
                && machine.PortBus.SelectedRegister == ZxNextPortBus.ResetSelectedRegister
                && machine.Registers.Read(0x50) == 0xFF && machine.Registers.Read(0x57) == 1
                && machine.MemoryBus.CurrentRomBank == 0
                && machine.Paging.Port7Ffd == 0 && machine.Paging.PortDFfd == 0
                && machine.Paging.Port1Ffd == 0 && machine.Paging.PortEff7 == 0
                && !machine.Cpu.InstructionTstateBatchingEnabled
                && machine.MemoryBus.ReadPhysicalRam(42, 7) == 0xA5,
            "Foundation reset did not reinitialize CPU and master-clock state.");
    }

    private static ZxNextMachine CreateProgram(params byte[] bytes)
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.Registers.Write(0x03, 0x03);
        machine.Registers.Write(0x50, 0);
        machine.Registers.Write(0x51, 1);
        for (int index = 0; index < bytes.Length; index++)
        {
            machine.MemoryBus.WriteCpu((ushort)index, bytes[index]);
        }

        return machine;
    }

    private static ZxNextMachine CreateProgramAt(ushort address, params byte[] bytes)
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.Registers.Write(0x03, 0x03);
        for (int index = 0; index < bytes.Length; index++)
        {
            machine.MemoryBus.WriteCpu(unchecked((ushort)(address + index)), bytes[index]);
        }

        machine.Cpu.PC = address;
        return machine;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
