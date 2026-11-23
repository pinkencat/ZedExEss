using ZedExEss.Z80CPU;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ZedExEss.FileHandlers;
using ZedExEss.Machines;
using ZedExEss.Spectrum.Abstractions;
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

    /// <summary>A locally assembled <c>zxnddma_compare.nex</c>, its <c>.sym</c> beside it.</summary>
    public string? DdmaCompareNexPath { get; init; }
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
        writer.WriteLine(
            $"Core target: {ZxNextReferenceBaseline.CoreVersion} " +
            $"(System/Next {ZxNextReferenceBaseline.SystemDistributionVersion})");
        writer.WriteLine(
            $"VHDL reference: {ZxNextReferenceBaseline.VhdlReferenceVersion} " +
            $"({ZxNextReferenceBaseline.VhdlReferenceCommit})");
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
        Check("NMI arbitration follows zxnext.vhd", VerifyNmiArbitrationRules, ref failed);
        Check("NextReg values and resets follow zxnext.vhd", VerifyNextRegValueRules, ref failed);
        Check("ULA contention schedule and bus waits", VerifyUlaContention, ref failed);
        Check("ULA and +3 floating bus", VerifyUlaFloatingBus, ref failed);
        Check("ULA keyboard matrix and output latch", VerifyUlaKeyboard, ref failed);
        Check("Next joystick connectors and cursor-key fallback", VerifyJoystickAndCursorInput, ref failed);
        Check("Next Kempston mouse ports, buttons, wheel and NextReg $0A", VerifyKempstonMouse, ref failed);
        Check("Next Kempston mouse survives resets and snapshots", VerifyKempstonMouseState, ref failed);
        Check("Next beeper and TurboSound audio", VerifyNextAudio, ref failed);
        Check("The DACs follow soundrive.vhd, audio_mixer.vhd and zxnext.vhd's DAC decode", VerifyNextDacs, ref failed);
        Check("The host stream carries audio_mixer.vhd's two sides in stereo", VerifyNextStereoOutput, ref failed);
        Check("Legacy ULA framebuffer", VerifyLegacyUlaFramebuffer, ref failed);
        Check("Timex high-resolution and LoRes video", VerifyTimexAndLoresVideoModes, ref failed);
        Check("Layer 2 registers and CPU aperture", VerifyLayer2RegistersAndAperture, ref failed);
        Check("Layer 2 bitmap modes and compositor", VerifyLayer2BitmapModes, ref failed);
        Check("The compositor and border follow zxnext.vhd and zxula.vhd", VerifyCompositorRules, ref failed);
        Check("Tilemap registers, addressing and compositor", VerifyTilemapRegistersAndRendering, ref failed);
        Check("ULA-over-tilemap video-memory arbitration", VerifyVideoMemoryArbitration, ref failed);
        Check("Sprite ports, attributes and compositor", VerifySpritePortsAndRendering, ref failed);
        Check("Relative and unified sprite chains", VerifyRelativeAndUnifiedSprites, ref failed);
        Check("Timed sprite preparation, collision and overtime", VerifyTimedSpritePreparation, ref failed);
        Check("zxnDMA Layer 2 screen transfer", VerifyZxnDmaLayer2Transfer, ref failed);
        Check("zxnDMA block length, enable, status, WR4 and reset follow dma.vhd",
            VerifyZxnDmaRegisterRules, ref failed);
        Check("zxnDDMA commands, physical addresses, rectangles, transparency and fill",
            VerifyZxnDdma, ref failed);
        Check("DMA prescaler pacing, burst mode and auto restart",
            VerifyDmaPrescalerAndBurst, ref failed);
        Check("DMA accesses to the SPI port wait for its byte", VerifyDmaSpiWait, ref failed);
        Check("clk28 DMA BUSREQ arbitration and endpoint timing", VerifyTimedDmaArbitration, ref failed);
        Check("Raster-visible incremental DMA writes", VerifyDmaRasterInterleaving, ref failed);
        Check("Cycle-accounted ULA raster writes", VerifyCycleAccountedUlaRaster, ref failed);
        Check("CPU/video-BRAM write timing", VerifyCpuDisplayWriteTiming, ref failed);
        Check("Tilemap raster-pixel batching", VerifyTilemapRasterPixelBatching, ref failed);
        Check("Quiet raster spans", VerifyQuietRasterSpans, ref failed);
        Check("Two-layer live compositors", VerifyTwoLayerCompositors, ref failed);
        Check("General mixer live equivalence", VerifyGeneralMixerEquivalence, ref failed);
        Check("Desktop frame-runner seam", VerifyDesktopFrameRunnerSeam, ref failed);
        Check("NextReg instruction-boundary reset requests", VerifyNextRegResetRequests, ref failed);
        Check("Physical RAM configuration", VerifyPhysicalRam, ref failed);
        Check("A captured Next carries on identically", VerifyMachineSnapshotContinues, ref failed);
        Check("A Next snapshot survives a file round trip", VerifySnapshotFileRoundTrip, ref failed);
        Check("Every peripheral field survives a file round trip",
            VerifyPeripheralFieldsRoundTrip, ref failed);
        Check("A Next snapshot rewinds its SD card", VerifySnapshotRewindsSdCard, ref failed);
        Check("A 1 MiB Next snapshot restores into the 2 MiB machine", VerifyOneMiBSnapshotRestores, ref failed);
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
        Check("Memory, paging and contention follow zxnext.vhd", VerifyMemoryPagingContentionRules, ref failed);
        Check("Port decode follows zxnext.vhd", VerifyPortDecodeRules, ref failed);
        Check("Boot ROM and config SRAM mapping", VerifyBootAndConfigMapping, ref failed);
        Check("Alternate ROM read/write overlays", VerifyAlternateRomOverlay, ref failed);
        Check("Hard and soft ROM reset state", VerifyRomResetKinds, ref failed);
        Check("DivMMC control and system-SRAM mapping", VerifyDivMmcControlAndMapping, ref failed);
        Check("DivMMC programmable opcode automap", VerifyDivMmcAutomap, ref failed);
        Check("DivMMC NextReg and reset state", VerifyDivMmcRegisterResetState, ref failed);
        Check("Next SPI ports and SD sector path", VerifyNextSpiAndSdCard, ref failed);
        Check("A command during a multi-block read ends it", VerifySdCommandDuringMultipleRead, ref failed);
        Check("FAT16 folder overlay projection", VerifyFat16FolderOverlay, ref failed);
        Check("Guest changes merge back into the SD folder", VerifySdFolderMerge, ref failed);
        Check("A persistent SD folder still takes an overlay", VerifySdFolderPersistencePolicy, ref failed);
        Check("Z80N accumulator and arithmetic instructions", VerifyZ80NArithmetic, ref failed);
        Check("Z80N barrel instructions", VerifyZ80NBarrelOperations, ref failed);
        Check("Z80N immediate and stack instructions", VerifyZ80NImmediateOperations, ref failed);
        Check("Z80N pixel address instructions", VerifyZ80NPixelOperations, ref failed);
        Check("Z80N I/O instructions", VerifyZ80NIoOperations, ref failed);
        Check("RETI and RETN restore IFF1 from IFF2", VerifyReturnRestoresIff1, ref failed);
        Check("Z80N extended block transfers", VerifyZ80NBlockTransfers, ref failed);
        Check("The Next CPU follows the T80N where it differs from a Zilog Z80", VerifyT80nDifferences, ref failed);
        Check("Hardware IM2 follows im2_peripheral.vhd and zxnext.vhd", VerifyHardwareIm2, ref failed);
        Check("The Copper follows copper.vhd and zxula_timing.vhd's cvc", VerifyCopper, ref failed);
        Check("Opt-in Z80N assembler and disassembler", VerifyZ80NDeveloperTools, ref failed);
        Check("NEXTREG turbo boundary timing", VerifyNextRegTurboTiming, ref failed);
        Check("All CPU speed ratios", VerifyAllCpuSpeedRatios, ref failed);
        Check("The CPU speed switch follows zxnext.vhd's cpu_speed latch and the clock handover", VerifyCpuSpeedSwitch, ref failed);
        Check("+3 contention is decided where the simulated core decides it", VerifyPlus3WaitPhase, ref failed);
        Check("INT is a level for the whole pulse, and EI does not hold off NMI", VerifyIntLevelAndNmiAfterEi, ref failed);
        Check("The CTC follows ctc.vhd and ctc_chan.vhd", VerifyCtc, ref failed);
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

        if (!string.IsNullOrWhiteSpace(options.DdmaCompareNexPath))
        {
            Check(
                "zxnddma_compare reproduces the board's five timings",
                () => VerifyZxnDdmaCompareTimings(options.DdmaCompareNexPath, writer),
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
        Require(actual.Interrupts == expected.Interrupts,
            "The hardware IM2 chain did not come back as captured.");
        Require(actual.Registers.Extended == expected.Registers.Extended,
            "The NMI latch or the NextRegs stored beside it did not come back as captured.");
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

        Require(PsgStatesMatch(expectedAudio.Psgs, actualAudio.Psgs),
            "The PSGs' generator state did not come back as captured.");
        Require(DacStatesMatch(expectedAudio.Dac, actualAudio.Dac),
            "The DACs did not come back as captured.");
    }

    /// <summary>Two CTC states, channel by channel.</summary>
    private static bool CtcStatesMatch(ZxNextCtcState? expected, ZxNextCtcState? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        return expected.Channels.AsSpan().SequenceEqual(actual.Channels)
            && expected with { Channels = actual.Channels } == actual;
    }

    /// <summary>Two soundrive.vhd states: the channels and the sample's level sums.</summary>
    private static bool DacStatesMatch(ZxNextDacState? expected, ZxNextDacState? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        return expected.Channels.AsSpan().SequenceEqual(actual.Channels)
            && expected.LeftLevelSum == actual.LeftLevelSum
            && expected.RightLevelSum == actual.RightLevelSum;
    }

    /// <summary>Two sets of ym2149.vhd generator states, field by field.</summary>
    private static bool PsgStatesMatch(ZxNextPsgState[]? expected, ZxNextPsgState[]? actual)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null;
        }

        if (expected.Length != actual.Length)
        {
            return false;
        }

        for (int index = 0; index < expected.Length; index++)
        {
            ZxNextPsgState left = expected[index];
            ZxNextPsgState right = actual[index];
            if (!left.Registers.AsSpan().SequenceEqual(right.Registers)
                || !left.ToneCount.AsSpan().SequenceEqual(right.ToneCount)
                || right with { Registers = left.Registers, ToneCount = left.ToneCount } != left)
            {
                return false;
            }
        }

        return true;
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
                LastTransferLength: 0x00654321)
            {
                Extended = new ZxNextDmaExtendedState(
                    TransferMode: 2,
                    AutoRestart: true,
                    CeWait: true,
                    DimensionalFlags: 0x2F,
                    PortAExtended: 0x0A,
                    PortBExtended: 0x1C,
                    RowLength: 0x1234,
                    RowCount: 0x5678,
                    PortAStride: 0x9ABC,
                    PortBStride: 0xDEF0,
                    TransparentValue: 0xE3,
                    FillValue: 0x5A,
                    Layer2XLow: 0x44,
                    Layer2XHigh: 0x01,
                    SourceHigh: 0x0B,
                    DestinationHigh: 0x1D,
                    SourceRowStart: 0x0B1234,
                    DestinationRowStart: 0x1D5678,
                    RowPosition: 0x0102,
                    RowIndex: 0x0304,
                    WaitClocks: 0x0777,
                    ResumeTick: 0x0000_0012_3456_789A,
                    BusGranted: true,
                    RequestTick: 0x0000_0098_7654_3210)
            },
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
            Interrupts = new ZxNextInterruptControllerState(
                Status: 0x2ABC,
                Pending: 0x0809,
                InService: 0x0001,
                Im2Line: true,
                CtcEnables: 0x0A,
                UartEnables: 0x55,
                DmaEnables0: 0x83,
                DmaEnablesCtc: 0xC3,
                DmaEnablesUart: 0x66,
                RetiHoldOffEndTick: 0x0000_0012_3456_789C),

            // The CTC's channels, with control bit 7 matching the IM2X enables ($0A): the CTC's
            // own record carries them on a restore.
            Ctc = new ZxNextCtcState(
                [
                    new ZxNextCtcChannelState(3, 0x28, 0x10, 0x07, 0x0C, 0x0000_0012_3456_7000),
                    new ZxNextCtcChannelState(4, 0xC0, 0x00, 0x3F, 0x00, 0),
                    new ZxNextCtcChannelState(2, 0x18, 0xFE, 0x00, 0xFE, 0x0000_0012_3456_7010),
                    new ZxNextCtcChannelState(1, 0x90, 0x55, 0x00, 0x55, 0)
                ],
                Tick: 0x0000_0012_3456_7400,
                PendingWriteChannel: 2,
                PendingWriteValue: 0x85,
                PendingWriteEdge: 0x0000_0012_3456_7401),
            PortBus = new ZxNextPortBusState(
                LastWrittenPort: 0x243B,
                HasLastWrittenPort: true,
                LastWrittenValue: 0x5A,
                SelectedRegister: 0x6B,
                UlaOutputLatch: 0x17),
            FloatingBus = new ZxNextFloatingBusState(
                UlaFetchValue: 0x3C,
                Plus3CpuData: 0xC3,
                UlaDriving: true),
            Registers = captured.Registers with
            {
                Extended = new ZxNextRegisterFileExtendedState(
                    ResetType: 0b101,
                    NmiSource: ZxNextNmiSource.Multiface,
                    NmiPhase: ZxNextNmiPhase.Hold,
                    JoystickIoMode: 0xB1,
                    BusPortDecoding: 0x8123_4567,
                    I2sControl: 0xC5,
                    ContentionDisableBefore: true,
                    ContentionDisableAppliesAt: 0x0000_0012_3456_789B,
                    ScandoublerEffective: false)
            }
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
        Require(reloaded.Interrupts == distinctive.Interrupts,
            "A hardware IM2 field (IM2X) did not survive the round trip.");
        Require(CtcStatesMatch(reloaded.Ctc, distinctive.Ctc),
            "A CTC field (CTCX) did not survive the round trip.");
        Require(reloaded.PortBus == distinctive.PortBus,
            "A port bus latch did not survive the round trip.");
        Require(reloaded.FloatingBus == distinctive.FloatingBus,
            "A floating bus latch did not survive the round trip.");
        Require(reloaded.Registers.Extended == distinctive.Registers.Extended,
            "A NextReg reset-type, NMI latch, $0B, $86-$89 or $A2 field did not survive the round trip.");

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
        Require(recaptured.Interrupts == distinctive.Interrupts,
            "The restored hardware IM2 chain disagrees with the file it came from.");
        Require(CtcStatesMatch(recaptured.Ctc, distinctive.Ctc),
            "The restored CTC disagrees with the file it came from.");
        Require(recaptured.PortBus == distinctive.PortBus,
            "The restored port bus latches disagree with the file they came from.");
        Require(recaptured.FloatingBus == distinctive.FloatingBus,
            "The restored floating bus latches disagree with the file it came from.");
        Require(recaptured.Registers.Extended == distinctive.Registers.Extended,
            "The restored NextReg reset type, NMI latch, $0B, $86-$89 or $A2 disagree with the file they came from.");
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

        Require(PsgStatesMatch(expected.Audio.Psgs, actual.Audio.Psgs),
            "The PSGs' generator state (PSGX) did not survive the round trip.");
        Require(DacStatesMatch(expected.Audio.Dac, actual.Audio.Dac),
            "The DACs (DACS) did not survive the round trip.");
        Require(actual.Audio with { Chips = expected.Audio.Chips,
                SelectedRegisters = expected.Audio.SelectedRegisters,
                Pan = expected.Audio.Pan,
                Psgs = expected.Audio.Psgs,
                Dac = expected.Audio.Dac } == expected.Audio,
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
        // Requested, not yet taken: latched, DivMMC's button pressed, and picked up at the next
        // instruction boundary.
        RequireRestoreContinues(
            "with an NMI requested but not yet taken",
            CreateBoundaryWorkload,
            static machine =>
            {
                SettleBoundaryWorkload(machine);
                StepInstructions(machine, 32);
                ArmDivMmcNmi(machine);
                machine.Registers.Write(0x02, 0x04);
            },
            static machine => StepInstructions(machine, 2_000),
            static (machine, snapshot) =>
            {
                Require(snapshot.Registers.Extended is { NmiSource: ZxNextNmiSource.DivMmc, NmiPhase: ZxNextNmiPhase.Idle }
                        && snapshot.Registers.PendingDivMmcNmi,
                    "The probe does not actually hold a DivMMC NMI waiting for the CPU.");
                Require(machine.DivMmc.NmiActive && !machine.DivMmc.AutomapHeld,
                    "The NMI was taken before the capture, so this probe is the wrong one.");
            },
            comparePicture: false);

        // Taken: inside the service window, with DivMMC paged in by its $0066 fetch.
        RequireRestoreContinues(
            "inside an NMI service window",
            CreateBoundaryWorkload,
            static machine =>
            {
                SettleBoundaryWorkload(machine);
                StepInstructions(machine, 32);
                ArmDivMmcNmi(machine);
                machine.Registers.Write(0x02, 0x04);
                StepInstructions(machine, 2);

                // The CPU has vectored to $0066, which with DivMMC's window paged in is its ROM
                // - blank in a foundation machine, so the CPU would walk forward through it and
                // then through whatever it found. Pointing it at a loop in RAM keeps the probe
                // about the NMI state rather than about where a wandering program ended up.
                machine.Cpu.PC = ServiceLoopAddress;
            },
            static machine => StepInstructions(machine, 2_000),
            static (machine, snapshot) =>
            {
                Require(machine.DivMmc.AutomapHeld && machine.Registers.Nmi.Phase == ZxNextNmiPhase.Hold,
                    "The probe meant to be inside an NMI is not in one.");
                Require(snapshot.DivMmc.AutomapHeld && snapshot.Registers.Extended?.NmiPhase == ZxNextNmiPhase.Hold,
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
                ArmDivMmcNmi(machine);
                machine.Registers.Write(0x02, 0x04);
                StepInstructions(machine, 2);
                machine.Cpu.PC = ReturnFromNmiAddress;
            },
            static machine =>
            {
                StepInstructions(machine, 1);
                Require(!machine.DivMmc.NmiActive && !machine.DivMmc.AutomapHeld
                        && machine.Registers.Nmi.Source == ZxNextNmiSource.None,
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
                    0x7D, 0x00, 0x91, 0x08, 0x00,   // WR0: A to B from $9100, eight bytes
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
        Require(ZxNextReferenceBaseline.VhdlReferenceCommit.Length == 40,
            "The VHDL reference is not a full Git commit ID.");
        Require(ZxNextReferenceBaseline.CoreVersion == "3.03.03"
                && ZxNextReferenceBaseline.SystemDistributionVersion == "26.11a"
                && ZxNextReferenceBaseline.VhdlReferenceVersion == "3.02.03",
            "The executable baseline no longer matches the accepted source ADR.");
        byte majorMinor = ZxNextReferenceBaseline.CoreVersionMajorMinor;
        string reported = string.Create(
            CultureInfo.InvariantCulture,
            $"{majorMinor >> 4}.{majorMinor & 0x0F:D2}.{ZxNextReferenceBaseline.CoreVersionSubMinor:D2}");
        Require(reported == ZxNextReferenceBaseline.CoreVersion,
            $"NextRegs 0x01/0x0E would report {reported}, not {ZxNextReferenceBaseline.CoreVersion}.");
        Require(ZxNextReferenceBaseline.MachineId == 0x08,
            "The foundation must identify as an emulator, not a physical PCB.");
        Require(ZxNextReferenceBaseline.InitialRamBytes == 2 * 1024 * 1024,
            "The supported RAM configuration is no longer the fully expanded 2 MiB.");
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
        Require(machine.Registers.Read(0x01) == 0x33 && machine.Registers.Read(0x0E) == 0x03,
            "NextReg core version fields do not encode 3.03.03.");
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
        Require(machine.Registers.Read(0x7F) == 0x39,
            "A soft reset changed NextReg 0x7F, which zxnext.vhd sets only at power on.");

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

        // NextReg $03's timing and $05's 50/60 Hz take effect at the next frame sync, and $05
        // reads the bit in effect (zxnext.vhd eff_nr_03_machine_timing, eff_nr_05_5060).
        ZxNextMachine sixtyHertz = ZxNextMachineFactory.CreateFoundationMachine();
        int fiftyHertzGeneration = sixtyHertz.UlaTiming.ProfileGeneration;
        sixtyHertz.Registers.Write(0x03, 0x91);
        sixtyHertz.Registers.Write(0x05, 0x44);
        sixtyHertz.StepInstruction();
        Require(sixtyHertz.UlaTiming.ProfileGeneration == fiftyHertzGeneration
                && sixtyHertz.UlaTiming.MasterTicksPerFrame == 567_264
                && (sixtyHertz.Registers.Read(0x05) & 0x05) == 0x01,
            "A NextReg $03/$05 timing change took effect before the frame sync, or $05 read the written bit.");
        RunToNextFrame(sixtyHertz);
        Require(sixtyHertz.Registers.SixtyHertzMode
                && (sixtyHertz.Registers.Read(0x05) & 0x05) == 0x04
                && sixtyHertz.UlaTiming.FrameNumber == 1
                && sixtyHertz.UlaTiming.FrameMasterTick < 1_000
                && sixtyHertz.UlaTiming.PixelsPerLine == 448
                && sixtyHertz.UlaTiming.LinesPerFrame == 264
                && sixtyHertz.UlaTiming.MasterTicksPerFrame == 473_088
                && sixtyHertz.UlaTiming.ProfileGeneration != fiftyHertzGeneration,
            "NextReg $05 did not select the 48K/60 Hz frame profile at the frame sync.");

        ZxNextMachine pentagon = ZxNextMachineFactory.CreateFoundationMachine();
        pentagon.Registers.Write(0x05, 0x45);
        pentagon.Registers.Write(0x03, 0xC4);
        pentagon.StepInstruction();
        RunToNextFrame(pentagon);
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

    /// <summary>
    /// A NextReg $02 DivMMC NMI with the stackless response on: the return address goes to
    /// NextRegs $C2/$C3 instead of the stack, DivMMC pages in through its delayed $0066 entry
    /// point (NextReg $BB bit 0, on at reset), and RETN returns through $C2/$C3 and lets
    /// DivMMC go.
    /// </summary>
    private static void VerifyDivMmcStacklessNmi()
    {
        ZxNextMachine machine = CreateProgram(
            0xED, 0x91, 0xC0, 0x08, // NEXTREG $C0,$08: stackless NMI
            0xED, 0x91, 0x02, 0x04, // NEXTREG $02,$04: DivMMC NMI
            0x00);
        machine.Registers.Write(0x06, 0xB0); // the DRIVE button's NMI enabled
        machine.Registers.Write(0x0A, 0x11); // automap on
        machine.Registers.Write(0xB8, 0x00); // without the RST entry points, $0000 included
        var divMmcRom = new byte[ZxNextPhysicalMemory.PageSize];
        divMmcRom[0x0066] = 0x76; // HALT, which only an instant entry point would execute
        byte[] handler =
        [
            0xED, 0x91, 0xC2, 0x34, // NEXTREG $C2,$34
            0xED, 0x91, 0xC3, 0x12, // NEXTREG $C3,$12
            0xED, 0x45              // RETN
        ];
        handler.CopyTo(divMmcRom, 0x0067);
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
                && !machine.DivMmc.IsMapped
                && machine.Registers.Nmi.Phase == ZxNextNmiPhase.Fetch
                && machine.Registers.Read(0xC2) == 0x08
                && machine.Registers.Read(0xC3) == 0x00
                && machine.MemoryBus.ReadPhysicalRam(11, 0x1FFE) == 0xA5
                && machine.MemoryBus.ReadPhysicalRam(11, 0x1FFF) == 0x5A,
            "The software DivMMC NMI did not reach $0066, its button latched and DivMMC not yet paged, without overwriting the stack.");

        machine.StepInstruction();
        Require(machine.Cpu.PC == 0x0067
                && !machine.Cpu.IsHalted
                && machine.DivMmc.IsMapped
                && !machine.DivMmc.NmiActive
                && machine.Registers.Nmi.Phase == ZxNextNmiPhase.Hold,
            "DivMMC did not page in after the $0066 fetch through its delayed NMI entry point.");

        machine.StepInstruction();
        machine.StepInstruction();
        machine.StepInstruction();
        Require(machine.Cpu.PC == 0x1234
                && machine.Cpu.SP == 0x8000
                && !machine.DivMmc.NmiActive
                && !machine.DivMmc.IsMapped
                && machine.Registers.Nmi.Source == ZxNextNmiSource.None
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
        machine.Registers.Write(0x06, 0xA8); // the M1 button's NMI enabled
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
                && machine.Registers.Read(0x85) == 0x8F,
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
        arbitration.Multiface.PressButton();
        Require(arbitration.Multiface.NmiActive,
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
        // +3 timing decides a T-state after the cycle starts (the simulated core, F3-4).
        ulong waitCycleStart = waitSampleTick
            - (ulong)ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate;
        ulong gapCycleStart = waitSampleTick
            - (2UL * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        gapCycleStart -= (ulong)ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate;

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
        SetCpuSpeedNow(disabled, ZxNextCpuSpeed.Mhz7);
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

    /// <summary>
    /// The Kempston mouse as the pinned core presents it: $FBDF X, $FFDF Y, $FADF wheel and
    /// buttons, decoded on A11-A8 and the low byte only, with the buttons active low.
    /// </summary>
    /// <remarks>
    /// The button polarity is the core's, not ports.txt's: <c>zxnext.vhd</c> drives $FADF
    /// with <c>not</c> of each button, so a released mouse reads $0F.
    /// </remarks>
    private static void VerifyKempstonMouse()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextPortBus ports = machine.PortBus;
        Require(ports.ReadUncontended(0xFBDF) == 0x00
                && ports.ReadUncontended(0xFFDF) == 0x00
                && ports.ReadUncontended(0xFADF) == 0x0F,
            "A mouse at rest did not read X=0, Y=0 and $0F (no wheel, bit 3 set, buttons released).");

        // Movement: right counts X up, and up counts Y up.
        machine.Mouse.MoveHost(10, -5);
        Require(ports.ReadUncontended(0xFBDF) == 10 && ports.ReadUncontended(0xFFDF) == 5,
            "Moving right 10 and up 5 did not read X=10, Y=5.");
        machine.Mouse.MoveHost(-20, 9);
        Require(ports.ReadUncontended(0xFBDF) == 246 && ports.ReadUncontended(0xFFDF) == 252,
            "The counters did not wrap through zero at 8 bits.");

        // A15-A12 are not decoded; A11-A8 other than $A/$B/$F are not the mouse.
        Require(ports.ReadUncontended(0x0BDF) == 246 && ports.ReadUncontended(0x5FDF) == 252
                && ports.ReadUncontended(0x3ADF) == 0x0F,
            "The mouse ports were not decoded on A11-A8 and the low byte alone.");
        Require(ports.ReadUncontended(0xFCDF) == 0xFF && ports.ReadUncontended(0xF9DF) == 0xFF,
            "A $DF port outside the three mouse decodes answered as the mouse.");

        // Fractions of a pixel accumulate rather than vanish.
        machine.Mouse.MoveHost(0.5, 0);
        Require(ports.ReadUncontended(0xFBDF) == 246, "Half a pixel moved the mouse.");
        machine.Mouse.MoveHost(0.5, 0);
        Require(ports.ReadUncontended(0xFBDF) == 247, "Two half pixels did not make one count.");

        // Buttons, active low: bit 1 left, bit 0 right, bit 2 middle.
        machine.Mouse.SetHostButton(ZxNextMouseButton.Left, pressed: true);
        Require(ports.ReadUncontended(0xFADF) == 0x0D, "The left button did not clear bit 1 of $FADF.");
        machine.Mouse.SetHostButton(ZxNextMouseButton.Right, pressed: true);
        Require(ports.ReadUncontended(0xFADF) == 0x0C, "The right button did not clear bit 0 of $FADF.");
        machine.Mouse.SetHostButton(ZxNextMouseButton.Middle, pressed: true);
        Require(ports.ReadUncontended(0xFADF) == 0x08, "The middle button did not clear bit 2 of $FADF.");
        machine.Mouse.ReleaseHost();
        Require(ports.ReadUncontended(0xFADF) == 0x0F
                && ports.ReadUncontended(0xFBDF) == 247,
            "Releasing the host mouse did not release the buttons, or moved the counters.");

        // NextReg $0A bit 3 swaps left and right as a packet is latched, and only then.
        machine.Mouse.SetHostButton(ZxNextMouseButton.Left, pressed: true);
        Require(ports.ReadUncontended(0xFADF) == 0x0D, "The left button was not latched unswapped.");
        machine.Registers.Write(0x0A, 0x09);
        Require(machine.Registers.Read(0x0A) == 0x09,
            "NextReg $0A did not read back the button swap and DPI fields.");
        Require(ports.ReadUncontended(0xFADF) == 0x0D,
            "Setting the button swap changed a button byte that had already been latched.");
        machine.Mouse.MoveHost(1, 0);
        Require(ports.ReadUncontended(0xFADF) == 0x0E,
            "With the swap set, the next packet did not report the left button as the right.");
        machine.Mouse.ReleaseHost();
        _ = ports.ReadUncontended(0xFADF);

        // NextReg $0A bits 1:0 scale each packet: double, as sent, half, quarter. The
        // halving is an arithmetic shift of the 8-bit delta, so a one-count move left still
        // moves while a one-count move right does not - the core's own asymmetry.
        var dpiCases = new (byte Dpi, int Move, int Expected)[]
        {
            (0x00, 10, 20), (0x01, 10, 10), (0x02, 10, 5), (0x03, 10, 2),
            (0x02, 1, 0), (0x02, -1, -1), (0x03, -1, -1), (0x03, 3, 0),
            (0x00, 200, 400), (0x01, 300, 300), (0x02, 300, 149), (0x03, -300, -76)
        };
        foreach ((byte dpi, int move, int expected) in dpiCases)
        {
            machine.Registers.Write(0x0A, dpi);
            byte before = ports.ReadUncontended(0xFBDF);
            machine.Mouse.MoveHost(move, 0);
            byte after = ports.ReadUncontended(0xFBDF);
            Require(after == (byte)(before + expected),
                $"At DPI {dpi}, a host move of {move} added {(sbyte)(byte)(after - before)}, not {expected}.");
        }

        machine.Registers.Write(0x0A, 0x01);

        // The wheel: notches away from the user count down, and $FADF shows the low nibble.
        machine.Mouse.TurnHostWheel(1);
        Require(ports.ReadUncontended(0xFADF) == 0xFF && machine.Mouse.Wheel == 0xFF,
            "One notch up did not count the wheel down to $F.");
        machine.Mouse.TurnHostWheel(-3);
        Require(ports.ReadUncontended(0xFADF) == 0x2F, "Three notches down did not count the wheel up to 2.");
        machine.Mouse.TurnHostWheel(-20);
        Require(ports.ReadUncontended(0xFADF) == 0x6F && machine.Mouse.Wheel == 22,
            "A fast wheel turn did not wrap the visible nibble.");

        // NextReg $83 bit 5 disables the three ports, without disturbing the counters.
        byte xBeforeDisable = ports.ReadUncontended(0xFBDF);
        machine.Registers.Write(0x83, 0xDF);
        // With the mouse off, $xxDF falls through to the Kempston-1 alias, which an idle
        // joystick answers with zero.
        Require(ports.ReadUncontended(0xFBDF) == 0x00 && ports.ReadUncontended(0xFFDF) == 0x00
                && ports.ReadUncontended(0xFADF) == 0x00 && ports.ReadUncontended(0x001F) == 0x00,
            "The mouse ports stayed decoded with NextReg $83 bit 5 clear, or $DF did not fall through to Kempston 1.");
        machine.Registers.Write(0x83, 0xFF);
        Require(ports.ReadUncontended(0xFBDF) == xBeforeDisable && ports.ReadUncontended(0xFADF) == 0x6F,
            "Re-enabling the mouse ports did not show the counters as they were left.");

        // The CPU's own IN reaches the device too, not just the bus entry point.
        ZxNextMachine cpuMachine = ZxNextMachineFactory.CreateFoundationMachine();
        cpuMachine.Mouse.MoveHost(33, -7);
        cpuMachine.Mouse.SetHostButton(ZxNextMouseButton.Right, pressed: true);
        byte[] program =
        [
            0x01, 0xDF, 0xFB,   // LD BC,$FBDF
            0xED, 0x50,         // IN D,(C)
            0x06, 0xFF,         // LD B,$FF
            0xED, 0x58,         // IN E,(C)
            0x06, 0xFA,         // LD B,$FA
            0xED, 0x78,         // IN A,(C)
            0x76                // HALT
        ];
        for (int index = 0; index < program.Length; index++)
        {
            cpuMachine.MemoryBus.WriteCpu((ushort)(0x8000 + index), program[index]);
        }

        cpuMachine.Cpu.PC = 0x8000;
        for (int step = 0; step < 6; step++)
        {
            cpuMachine.StepInstruction();
        }

        Require(cpuMachine.Cpu.D == 33 && cpuMachine.Cpu.E == 7 && cpuMachine.Cpu.A == 0x0E,
            $"IN from the mouse ports read D=${cpuMachine.Cpu.D:X2} E=${cpuMachine.Cpu.E:X2} A=${cpuMachine.Cpu.A:X2}, not $21/$07/$0E.");
    }

    /// <summary>
    /// The mouse's counters are power-on state: soft and hard reset leave them, and
    /// snapshots carry them, including through a file and from a file written before the
    /// mouse existed.
    /// </summary>
    private static void VerifyKempstonMouseState()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.Mouse.MoveHost(40, -12);
        machine.Mouse.TurnHostWheel(-5);
        machine.Mouse.SetHostButton(ZxNextMouseButton.Middle, pressed: true);
        Require(machine.PortBus.ReadUncontended(0xFADF) == 0x5B,
            "The mouse did not reach the state the reset checks start from.");

        machine.Reset(ZxNextResetKind.Soft);
        Require(machine.PortBus.ReadUncontended(0xFBDF) == 40
                && machine.PortBus.ReadUncontended(0xFFDF) == 12
                && machine.PortBus.ReadUncontended(0xFADF) == 0x5B,
            "A soft reset cleared the mouse, which only power-on does.");
        machine.Reset(ZxNextResetKind.Hard);
        Require(machine.PortBus.ReadUncontended(0xFBDF) == 40
                && machine.PortBus.ReadUncontended(0xFADF) == 0x5B,
            "A hard reset cleared the mouse, which only power-on does.");
        Require(machine.Registers.Read(0x0A) == 0x01,
            "A hard reset did not put NextReg $0A back to its default DPI.");

        // A snapshot carries the counters and the latched buttons, and a restore drops
        // movement the host sent to the machine being replaced.
        ZxNextMachineSnapshot captured = ZxNextMachineSnapshotService.Capture(machine);
        Require(captured.Mouse == new ZxNextMouseState(40, 12, 5, 0x04),
            $"The capture recorded {captured.Mouse}, not X=40 Y=12 wheel 5 and the middle button.");

        ZxNextMachine restored = ZxNextMachineFactory.CreateFoundationMachine();
        restored.Mouse.MoveHost(99, 99);
        ZxNextMachineSnapshotService.Restore(restored, captured);
        Require(restored.PortBus.ReadUncontended(0xFBDF) == 40
                && restored.PortBus.ReadUncontended(0xFFDF) == 12
                && restored.PortBus.ReadUncontended(0xFADF) == 0x5B,
            "A restore did not put the mouse back, or let undelivered host movement through.");

        using var file = new MemoryStream();
        ZxNextSnapshotFile.Save(file, captured);
        file.Position = 0;
        ZxNextMachineSnapshot reloaded = ZxNextSnapshotFile.Load(file);
        Require(reloaded.Mouse == captured.Mouse && reloaded.UnknownChunks.Count == 0,
            "The mouse did not survive a file round trip as a chunk this build knows.");

        // A version-1 file written before the mouse chunk existed: same body without MOUS.
        byte[] bytes = file.ToArray();
        byte[] body = new byte[BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4))];
        using (var inflate = new System.IO.Compression.DeflateStream(
            new MemoryStream(bytes, 12, bytes.Length - 12),
            System.IO.Compression.CompressionMode.Decompress))
        {
            inflate.ReadExactly(body);
        }

        using var older = new MemoryStream();
        older.Write(bytes, 0, 6);
        older.Write([0, 0]);
        older.Write(new byte[4]);
        int offset = 0;
        bool sawMouse = false;
        while (offset < body.Length)
        {
            string id = Encoding.ASCII.GetString(body, offset, 4);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 4, 4));
            if (id == "MOUS")
            {
                sawMouse = true;
            }
            else
            {
                older.Write(body, offset, 8 + length);
            }

            offset += 8 + length;
        }

        Require(sawMouse, "The saved file had no MOUS chunk to remove, so the older-file case proves nothing.");
        byte[] olderBytes = older.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(olderBytes.AsSpan(8, 4), (uint)(olderBytes.Length - 12));
        ZxNextMachineSnapshot fromOlder = ZxNextSnapshotFile.Load(new MemoryStream(olderBytes));
        Require(fromOlder.Mouse == default && fromOlder.Cpu == captured.Cpu,
            "A file without a mouse chunk did not load as a mouse at rest.");

        ZxNextMachine resting = ZxNextMachineFactory.CreateFoundationMachine();
        resting.Mouse.MoveHost(5, 5);
        _ = resting.PortBus.ReadUncontended(0xFBDF);
        ZxNextMachineSnapshotService.Restore(resting, fromOlder);
        Require(resting.PortBus.ReadUncontended(0xFBDF) == 0
                && resting.PortBus.ReadUncontended(0xFADF) == 0x0F,
            "Restoring a file from before the mouse did not leave the mouse at rest.");
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
                && machine.PortBus.ReadUncontended(0x0037) == 0xFF,
            "Host Kempston input did not reach the Next's $1F port decode.");

        // $DF aliases Kempston 1 only while the Kempston mouse is disabled (NextReg $83
        // bit 5), which it is not after reset.
        Require(machine.PortBus.ReadUncontended(0x00DF) == 0xFF,
            "$DF answered as the Kempston joystick while the Kempston mouse was enabled.");
        machine.Registers.Write(0x83, 0xDF);
        Require(machine.PortBus.ReadUncontended(0x00DF) == 0x19,
            "With the mouse disabled, $DF did not alias the Kempston-1 joystick.");
        machine.Registers.Write(0x82, 0xBF);
        Require(machine.PortBus.ReadUncontended(0x00DF) == 0xFF,
            "The $DF alias stayed decoded with the $1F joystick port disabled.");
        machine.Registers.Write(0x82, 0xFF);
        machine.Registers.Write(0x83, 0xFF);
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
        // Orders 6 and 7 blend every opaque Layer 2 pixel with the ULA, not just a promoted one.
        int[] expectedOrdinaryPriority = [red, red, blue, red, blue, blue, magenta, darkMagenta];
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
        Require((machine.PortBus.ReadUncontended(ZxNextSpriteDevice.StatusAndSlotPort) & 0x01) == 0,
            "The immediate diagnostic render set the guest-visible sprite collision flag.");
        machine.Video.AdvanceMasterTicks(machine.UlaTiming.MasterTicksPerFrame);
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
            0x7D, 0x00, 0xC1, 0x20, 0x00,
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
            0xFF, 0x3F, // $3FFF: on port $6B that moves $3FFF bytes, not $4000
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

        // dma.vhd counts from 0 on port $6B and stops once the counter reaches the length,
        // so the game's block leaves the last byte of the third as it was.
        for (int offset = 0; offset < 0x4000; offset++)
        {
            byte expected = offset < 0x3FFF ? (byte)((offset * 37) ^ (offset >> 5) ^ 0xA5) : (byte)0;
            byte actual = machine.MemoryBus.ReadPhysicalRam(
                16 + (offset >> 13),
                offset & (ZxNextPhysicalMemory.PageSize - 1));
            Require(actual == expected,
                $"The zxnDMA Layer 2 transfer diverged at offset ${offset:X4}: " +
                $"expected ${expected:X2}, found ${actual:X2}.");
        }

        // The idle state clears "at least one byte", so a finished block reads $1A.
        Require(machine.Dma.LastTransferLength == 0x3FFF
                && machine.Dma.ByteCounter == 0x3FFF
                && machine.Dma.PortAAddressCounter == 0xFFFF
                && machine.Dma.PortBAddressCounter == 0x3FFF
                && machine.PortBus.ReadUncontended(ZxNextDmaDevice.ZxnControlPort) == 0x1A,
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
        for (int offset = 0x0200; offset < 0x020F; offset++)
        {
            Require(machine.MemoryBus.ReadPhysicalRam(16, offset) == 0xE3,
                "The zxnDMA fixed-source Layer 2 fill did not repeat its source byte.");
        }

        Require(machine.MemoryBus.ReadPhysicalRam(16, 0x020F) != 0xE3,
            "A fifteen-byte zxnDMA fill wrote a sixteenth byte.");

        // DMA I/O endpoints go through the ordinary decoded device ports.
        machine.MemoryBus.WritePhysicalRam(0, 0x0200, 0x01);
        machine.MemoryBus.WritePhysicalRam(0, 0x0201, 0x02);
        machine.MemoryBus.WritePhysicalRam(0, 0x0202, 0x03);
        machine.MemoryBus.WritePhysicalRam(0, 0x0203, 0x1F);
        byte[] memoryToIo =
        [
            0xC3,
            0x7D, 0x00, 0xC2, 0x04, 0x00,
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
            0x7D, 0x03, 0xC3, 0x04, 0x00,
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
            0x7D, 0x00, 0xC1, 0x04, 0x00,
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
            "The arbiter did not grant the pending DMA request.");
        Require(machine.MasterTicks == 8
                && machine.BusArbiter.Owner == ZxNextBusOwner.Dma
                && machine.Dma.CurrentTransferLength == 0,
            "The grant did not begin with dma.vhd's one-clock WAITING_ACK state.");
        Require(machine.StepBusArbitration(),
            "The arbiter did not advance the pending DMA source cycle.");
        Require(machine.MasterTicks == 40
                && machine.BusArbiter.Owner == ZxNextBusOwner.Dma
                && machine.BusArbiter.CpuBusAcknowledged
                && machine.Dma.CurrentTransferLength == 0
                && machine.MemoryBus.ReadPhysicalRam(16, 0x0100) == 0,
            "The four-clock source cycle did not remain read-only for 32 clk28 ticks.");

        Require(machine.StepBusArbitration(),
            "The arbiter did not advance the pending DMA destination cycle.");
        Require(machine.MasterTicks == 56
                && machine.Cpu.Cyc == cpuCyclesBefore
                && machine.Dma.CurrentTransferLength == 1
                && machine.Dma.ByteCounter == 1
                && machine.MemoryBus.ReadPhysicalRam(16, 0x0100) == source[0],
            "The two-clock destination cycle did not commit at its 16-tick boundary.");

        // The DMA is clocked from i_CLK_CPU, but cpu_speed latches only while the DMA is off
        // the bus (zxnext.vhd ~5809, dma_holds_bus = '0'). A $07 write during the transfer
        // therefore waits: the rest runs at 3.5 MHz and the switch comes at the CPU's next
        // idle edge.
        machine.Registers.Write(0x07, (byte)ZxNextCpuSpeed.Mhz28);
        RunDmaToCompletion(machine);
        for (int index = 0; index < source.Length; index++)
        {
            Require(machine.MemoryBus.ReadPhysicalRam(16, 0x0100 + index) == source[index],
                $"Timed DMA diverged after the $07 write at byte {index}.");
        }

        // Three more bytes of 4 + 2 clocks at 3.5 MHz, then FINISH_DMA, IDLE and the CPU's
        // two clocks to see BUSRQ high: 56 + (18 + 4) x 8.
        Require(machine.MasterTicks == 232
                && machine.BusArbiter.DmaOwnedMasterTicks == 232
                && machine.BusArbiter.CpuOwnedMasterTicks == 0
                && machine.BusArbiter.DmaGrantCount == 1
                && machine.BusArbiter.Owner == ZxNextBusOwner.Cpu
                && !machine.Dma.IsActive
                && machine.Dma.LastTransferLength == source.Length
                && machine.CpuSpeed == ZxNextCpuSpeed.Mhz3_5
                && machine.Registers.Read(0x07) == 0x03,
            "A $07 write while the DMA held the bus changed the speed before the DMA let go, "
                + $"or the release timing was wrong (master {machine.MasterTicks}, $07 ${machine.Registers.Read(0x07):X2}).");

        // The CPU's next instruction latches 28 MHz on its first edge: that T-state is still on
        // the 3.5 MHz clock (8), the clock pauses to the 28 MHz clock's next edge (1), and the
        // NOP's other three T-states take a tick each.
        machine.MemoryBus.WriteDirect(0x8000, 0x00); // NOP
        machine.Cpu.PC = 0x8000;
        machine.StepInstruction();
        machine.SynchronizeTime();
        Require(machine.MasterTicks == 244 && machine.CpuSpeed == ZxNextCpuSpeed.Mhz28
                && machine.Registers.Read(0x07) == 0x33,
            $"The $07 write held over the DMA transfer did not latch on the next instruction's first edge and hand over (master {machine.MasterTicks}).");

        // A fresh request is not granted at once: BUSRQ goes low a clock after the DMA
        // decodes the enable, and the CPU registers it and grants only at the end of a later
        // machine cycle. Here the next instruction's opcode fetch runs first.
        machine.Cpu.PC = 0x8000;
        WriteDmaCommand(machine,
        [
            0xC3,
            0x7D, 0x00, 0xC1, 0x00, 0x00,
            0x14,
            0x10,
            0xAD, 0x10, 0x01,
            0x82, 0xCF, 0xB3, 0x87
        ]);
        cpuCyclesBefore = machine.Cpu.Cyc;
        machine.StepInstruction();
        Require(machine.Cpu.PC == 0x8001
                && machine.Cpu.Cyc == cpuCyclesBefore + 4
                && machine.BusArbiter.CpuTstatesAtLastDmaGrant == cpuCyclesBefore + 4
                && !machine.Dma.IsActive
                && machine.MemoryBus.ReadPhysicalRam(16, 0x0110) == source[0],
            "A fresh BUSREQ was not granted at the end of the next machine cycle, or the "
                + $"transfer did not finish (PC ${machine.Cpu.PC:X4}, {machine.Cpu.Cyc - cpuCyclesBefore} T, "
                + $"grant at {(long)machine.BusArbiter.CpuTstatesAtLastDmaGrant - (long)cpuCyclesBefore} T, "
                + $"active {machine.Dma.IsActive}, byte ${machine.MemoryBus.ReadPhysicalRam(16, 0x0110):X2}).");

        machine.StepInstruction();
        Require(machine.Cpu.Cyc > cpuCyclesBefore
                && machine.BusArbiter.CpuOwnedMasterTicks > 0,
            "The CPU did not resume after DMA released BUSREQ.");

        // Exercise the actual CPU programming path. The enable is written in OTIR's I/O
        // machine cycle (T12-T16); BUSRQ goes low too late for that cycle's end, so the grant
        // comes at the end of the next one, OTIR's five-T-state repeat interval (T21). The
        // DMA then holds the bus for WAITING_ACK, a two-clock read, a two-clock write and the
        // four clocks that give it back.
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
                && guest.BusArbiter.CpuTstatesAtLastDmaGrant == 21
                && guest.BusArbiter.CpuOwnedMasterTicks == 168
                && guest.BusArbiter.DmaOwnedMasterTicks == (1 + 2 + 2 + 4) * 8
                && guest.MasterTicks == 168 + ((1 + 2 + 2 + 4) * 8)
                && guest.MemoryBus.ReadPhysicalRam(16, 0x0100) == 0x5A,
            "Guest OTIR's enable was not granted at the end of its repeat interval, or the "
                + "hand-off did not cost WAITING_ACK and the four release clocks.");

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
        Require(machine.StepBusArbitration() && machine.StepBusArbitration(),
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
        SetCpuSpeedNow(machine, ZxNextCpuSpeed.Mhz28);
        machine.Video.Reset();
        ZxNextUlaTimingProfile profile = machine.UlaTiming.Profile;
        ulong firstLayer2PixelTick = checked(
            ((ulong)profile.FirstDisplayLine * (ulong)profile.PixelsPerLine
                + (ulong)profile.DisplayStartHorizontal)
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);

        // With four-clock read and write cycles at 28 MHz, the first byte commits
        // exactly as its pixel begins. Later bytes take eight clk28 ticks apiece while
        // adjacent Layer 2 pixels begin four ticks apart, so the beam outruns the copy.
        ulong leadTick = firstLayer2PixelTick - 9;
        machine.Video.AdvanceMasterTicks(leadTick);
        machine.Clock.AdvanceMasterTicks(leadTick);
        machine.UlaTiming.Advance();
        WriteDmaCommand(machine,
        [
            0xC3,
            0x7D, 0x00, 0xC2, 0x04, 0x00,
            0x54, 0x00,
            0x50, 0x00,
            0xAD, 0x00, 0x00,
            0x82, 0xCF, 0xB3, 0x87
        ]);
        RunDmaToCompletion(machine);

        Require(machine.MasterTicks == firstLayer2PixelTick + 28,
            "The four-byte raster DMA did not occupy WAITING_ACK, thirty-two clk28 ticks of "
                + "transfer and the four release ticks.");
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

    /// <summary>
    /// The zxnDMA rules read in the pinned <c>dma.vhd</c> (and stated by the 26.11a guide,
    /// part 2): the length, the enable, the status byte, WR4 and the two resets.
    /// </summary>
    private static void VerifyZxnDmaRegisterRules()
    {
        // Port $6B counts from 0 and stops when the counter reaches the length: N bytes.
        // Port $0B loads -1 first: N + 1. A length of 0 moves one byte on both.
        foreach ((byte port, ushort length, int expected) in new (byte, ushort, int)[]
        {
            (ZxNextDmaDevice.ZxnControlPort, 4, 4),
            (ZxNextDmaDevice.Z80ControlPort, 4, 5),
            (ZxNextDmaDevice.ZxnControlPort, 0, 1),
            (ZxNextDmaDevice.Z80ControlPort, 0, 1)
        })
        {
            ZxNextMachine machine = CreateDmaCopyMachine(out ushort source, out ushort destination);
            WriteDmaCommandOn(machine, port,
            [
                0xC3,
                0x7D, (byte)source, (byte)(source >> 8), (byte)length, (byte)(length >> 8),
                0x14, 0x10,
                0xAD, (byte)destination, (byte)(destination >> 8),
                0xCF, 0x87
            ]);
            RunDmaToCompletion(machine);
            int moved = CountCopiedBytes(machine, source, destination);
            Require(moved == expected && machine.Dma.LastTransferLength == expected,
                $"A length of {length} on port ${port:X2} moved {moved} bytes, not {expected}.");
        }

        // Enable without a load still starts: after a finished block the counter is
        // already at the length and is only tested after a byte, so one more moves.
        ZxNextMachine again = CreateDmaCopyMachine(out ushort againSource, out ushort againDestination);
        WriteDmaCommand(again,
        [
            0xC3,
            0x7D, (byte)againSource, (byte)(againSource >> 8), 0x02, 0x00,
            0x14, 0x10,
            0xAD, (byte)againDestination, (byte)(againDestination >> 8),
            0xCF
        ]);
        Require(again.PortBus.ReadUncontended(ZxNextDmaDevice.ZxnControlPort) == 0x3A,
            "A loaded, idle DMA did not report $3A.");
        WriteDmaCommand(again, [0x87]);
        RunDmaToCompletion(again);
        WriteDmaCommand(again, [0xBF]);
        Require(again.PortBus.ReadUncontended(ZxNextDmaDevice.ZxnControlPort) == 0x1A,
            "A finished block did not report end of block with its at-least-one bit clear ($1A).");
        WriteDmaCommand(again, [0x87]);
        RunDmaToCompletion(again);
        Require(CountCopiedBytes(again, againSource, againDestination) == 3
                && again.Dma.ByteCounter == 3,
            "Enabling a finished block without $CF did not move exactly one more byte.");
        WriteDmaCommand(again, [0x8B, 0xBF]);
        Require(again.PortBus.ReadUncontended(ZxNextDmaDevice.ZxnControlPort) == 0x3A,
            "$8B did not reinitialise the status byte.");

        // WR4 stores its mode; bit 4 with no address byte hangs the decoder until a reset.
        ZxNextMachine wr4 = CreateDmaCopyMachine(out ushort hungSource, out ushort hungDestination);
        WriteDmaCommand(wr4, [0xCD, 0x00, 0x00]);
        Require(wr4.Dma.TransferMode == 0x02, "WR4 $CD did not select burst mode.");
        WriteDmaCommand(wr4, [0xA1]);
        Require(wr4.Dma.TransferMode == 0x01, "WR4 $A1 did not select continuous mode.");
        WriteDmaCommand(wr4,
        [
            0x91,
            0x7D, (byte)hungSource, (byte)(hungSource >> 8), 0x04, 0x00,
            0x14, 0x10, 0xAD, (byte)hungDestination, (byte)(hungDestination >> 8),
            0xC3, 0xCF, 0x87
        ]);
        Require(wr4.Dma.RegisterDecoderHung && !wr4.Dma.IsActive && wr4.Dma.BlockLength == 0,
            "WR4 $91 did not leave the register decoder deaf to every later write.");
        wr4.Reset(ZxNextResetKind.Soft);
        wr4.MemoryBus.SetMmuPage(6, 0x20);
        wr4.MemoryBus.SetMmuPage(7, 0x21);
        WriteDmaCommand(wr4,
        [
            0x7D, (byte)hungSource, (byte)(hungSource >> 8), 0x04, 0x00,
            0x14, 0x10, 0xAD, (byte)hungDestination, (byte)(hungDestination >> 8),
            0xCF, 0x87
        ]);
        RunDmaToCompletion(wr4);
        Require(!wr4.Dma.RegisterDecoderHung && CountCopiedBytes(wr4, hungSource, hungDestination) == 4,
            "A reset did not free the hung DMA register decoder.");

        // $C3 and the machine reset keep the start addresses, length and modes; both put
        // the timings back to three cycles; only the machine reset restores the read mask.
        ZxNextMachine kept = ZxNextMachineFactory.CreateFoundationMachine();
        WriteDmaCommand(kept,
        [
            0x7D, 0x34, 0x12, 0x10, 0x00,
            0x54, 0x02, 0x50, 0x00,
            0xAD, 0x78, 0x56,
            0xB2,
            0xBB, 0x7E
        ]);
        Require(kept.Dma.AutoRestart, "WR5 $B2 did not set auto restart.");
        WriteDmaCommand(kept, [0xC3, 0xCF, 0xA7]);
        Require(ReadDmaBytes(kept, 6).SequenceEqual(new byte[] { 0x00, 0x00, 0x34, 0x12, 0x78, 0x56 })
                && kept.Dma.BlockLength == 0x10
                && !kept.Dma.AutoRestart
                && kept.Dma.CpuClockCyclesUntilBusEvent == 3,
            "$C3 did not keep the addresses, length and read mask while resetting timing and WR5.");
        kept.Reset(ZxNextResetKind.Soft);
        WriteDmaCommand(kept, [0xCF, 0xBB, 0x18]);
        Require(ReadDmaBytes(kept, 2).SequenceEqual(new byte[] { 0x34, 0x12 })
                && kept.Dma.BlockLength == 0x10,
            "A machine reset cleared the DMA's start addresses or block length.");
        kept.Reset(ZxNextResetKind.Soft);
        WriteDmaCommand(kept, [0xA7]);
        Require(kept.PortBus.ReadUncontended(ZxNextDmaDevice.ZxnControlPort) == 0x3A
                && kept.PortBus.ReadUncontended(ZxNextDmaDevice.ZxnControlPort) == 0x00,
            "A machine reset did not restore the $7F read mask.");
    }

    /// <summary>
    /// The zxnDDMA (core 3.03), from the 26.11a guide's part 2: its commands only on port
    /// <c>$6B</c>, physical addresses with the 64K carry and the issue 4 bit-21 wrap, the
    /// SRAM behind the video block RAM, rectangles, the transparent colour, fill, Layer 2
    /// X,Y positions and what <c>$C3</c> clears.
    /// </summary>
    private static void VerifyZxnDdma()
    {
        // On port $0B a zxnDDMA command byte is an ignored WR6 and its follow byte is taken
        // as an ordinary register write: $09 is a WR0 whose Port A low byte follows.
        ZxNextMachine legacy = ZxNextMachineFactory.CreateFoundationMachine();
        WriteDmaCommandOn(legacy, ZxNextDmaDevice.Z80ControlPort, [0xD7, 0x09, 0x34]);
        Require((legacy.Dma.PortAStartAddress & 0xFF) == 0x34 && legacy.Dma.DimensionalFlags == 0,
            "Port $0B decoded a zxnDDMA command instead of ignoring it.");
        WriteDmaCommand(legacy, [0xDF, 0x07]);
        Require(legacy.Dma.DimensionalFlags == 0x07 && (legacy.Dma.PortAStartAddress & 0xFF) == 0x34,
            "Port $6B did not take $DF and its flag byte.");

        // Physical addresses: 22 bits, SRAM only, carrying across 64K.
        ZxNextMachine physical = ZxNextMachineFactory.CreateFoundationMachine();
        for (int index = 0; index < 4; index++)
        {
            physical.MemoryBus.WritePhysicalSram(0x0AFFFE + index, (byte)(0xA0 + index));
        }

        WriteDmaCommand(physical,
        [
            0xC3,
            0xDF, 0x01,
            0xD7, 0x0A,
            0xDB, 0x2C, // bit 21 set: wraps to $0C on the 2 MB board
            0x7D, 0xFE, 0xFF, 0x04, 0x00,
            0x14, 0x10,
            0xAD, 0xFF, 0xFF,
            0xCF, 0x87
        ]);
        RunDmaToCompletion(physical);
        Require(physical.MemoryBus.ReadPhysicalSram(0x0CFFFF) == 0xA0
                && physical.MemoryBus.ReadPhysicalSram(0x0D0000) == 0xA1
                && physical.MemoryBus.ReadPhysicalSram(0x0D0001) == 0xA2
                && physical.MemoryBus.ReadPhysicalSram(0x0D0002) == 0xA3,
            "A physical transfer did not carry across 64K or wrap bit 21 on the issue 4 board.");

        // Bank 5 (page $0A) reaches the SRAM behind the block RAM, never the screen; page
        // $0F, the second half of bank 7, is ordinary SRAM.
        byte screenByte = physical.MemoryBus.ReadPhysicalRam(10, 0);
        physical.MemoryBus.WritePhysicalRam(0, 0x0100, 0x5B);
        physical.MemoryBus.WritePhysicalRam(0, 0x0101, 0x5C);
        WriteDmaCommand(physical,
        [
            0xC3,
            0xDF, 0x01,
            0xD7, 0x04, 0xDB, 0x05,
            0x7D, 0x00, 0x01, 0x01, 0x00, 0x14, 0x10, 0xAD, 0x00, 0x40,
            0xCF, 0x87
        ]);
        RunDmaToCompletion(physical);
        WriteDmaCommand(physical,
        [
            0xC3,
            0xDF, 0x01,
            0xD7, 0x04, 0xDB, 0x05,
            0x7D, 0x01, 0x01, 0x01, 0x00, 0x14, 0x10, 0xAD, 0x00, 0xE0,
            0xCF, 0x87
        ]);
        RunDmaToCompletion(physical);
        Require(physical.MemoryBus.ReadPhysicalRam(10, 0) == screenByte
                && physical.MemoryBus.ReadPhysicalSram(0x054000) == 0x5B
                && physical.MemoryBus.ReadPhysicalRam(15, 0) == 0x5C,
            "Physical DMA reached the bank 5 block RAM, or missed page $0F's SRAM.");

        // A rectangle through MMU addresses: 4 bytes x 3 rows, source stride 16,
        // destination stride 32. The block length is not used.
        ZxNextMachine rectangle = ZxNextMachineFactory.CreateFoundationMachine();
        rectangle.MemoryBus.SetMmuPage(6, 0x20);
        rectangle.MemoryBus.SetMmuPage(7, 0x21);
        for (int index = 0; index < 64; index++)
        {
            rectangle.MemoryBus.WriteDirect((ushort)(0xC000 + index), (byte)(0x40 + index));
        }

        WriteDmaCommand(rectangle,
        [
            0xC3,
            0xDF, 0x02,
            0xE3, 0x04, 0x00, 0xE7, 0x03, 0x00,
            0xEB, 0x10, 0x00, 0xEF, 0x20, 0x00,
            0x7D, 0x00, 0xC0, 0x01, 0x00, 0x14, 0x10, 0xAD, 0x00, 0xE0,
            0xCF, 0x87
        ]);
        RunDmaToCompletion(rectangle);
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 6; column++)
            {
                byte actual = rectangle.MemoryBus.ReadDirect((ushort)(0xE000 + (row * 32) + column));
                byte expected = row < 3 && column < 4 ? (byte)(0x40 + (row * 16) + column) : (byte)0;
                Require(actual == expected,
                    $"The 4x3 rectangle diverged at row {row}, column {column}: " +
                    $"expected ${expected:X2}, found ${actual:X2}.");
            }
        }

        Require(rectangle.Dma.LastTransferLength == 12 && rectangle.Dma.ByteCounter == 12,
            "A 4x3 rectangle did not count twelve bytes.");

        // Transparent: a byte equal to $F3's colour is not written, but takes its cycle.
        ZxNextMachine transparent = ZxNextMachineFactory.CreateFoundationMachine();
        transparent.MemoryBus.SetMmuPage(6, 0x20);
        transparent.MemoryBus.SetMmuPage(7, 0x21);
        byte[] shape = [0x11, 0xE3, 0x33, 0xE3];
        for (int index = 0; index < shape.Length; index++)
        {
            transparent.MemoryBus.WriteDirect((ushort)(0xC000 + index), shape[index]);
            transparent.MemoryBus.WriteDirect((ushort)(0xE000 + index), 0x99);
        }

        ulong ticksBefore = transparent.BusArbiter.DmaOwnedMasterTicks;
        WriteDmaCommand(transparent,
        [
            0xC3,
            0xDF, 0x04, 0xF3, 0xE3,
            0x7D, 0x00, 0xC0, 0x04, 0x00, 0x14, 0x10, 0xAD, 0x00, 0xE0,
            0xCF, 0x87
        ]);
        RunDmaToCompletion(transparent);
        Require(transparent.MemoryBus.ReadDirect(0xE000) == 0x11
                && transparent.MemoryBus.ReadDirect(0xE001) == 0x99
                && transparent.MemoryBus.ReadDirect(0xE002) == 0x33
                && transparent.MemoryBus.ReadDirect(0xE003) == 0x99
                && transparent.BusArbiter.DmaOwnedMasterTicks - ticksBefore == (1 + (4 * 6) + 4) * 8,
            "The transparent colour was written, or its write cycle was skipped.");

        // Fill: no read at all, so a byte takes only its three-cycle write.
        ticksBefore = transparent.BusArbiter.DmaOwnedMasterTicks;
        WriteDmaCommand(transparent,
        [
            0xC3,
            0xDF, 0x08, 0xF7, 0x5A,
            0x7D, 0x00, 0xC0, 0x04, 0x00, 0x14, 0x10, 0xAD, 0x00, 0xE0,
            0xCF, 0x87
        ]);
        RunDmaToCompletion(transparent);
        Require(transparent.MemoryBus.ReadDirect(0xE000) == 0x5A
                && transparent.MemoryBus.ReadDirect(0xE003) == 0x5A
                && transparent.MemoryBus.ReadDirect(0xE004) != 0x5A
                && transparent.BusArbiter.DmaOwnedMasterTicks - ticksBefore == (1 + (4 * 3) + 4) * 8,
            "Fill read its source or wrote the wrong bytes.");

        // $C3 clears the flags: the same programme without $DF now copies via the MMU.
        WriteDmaCommand(transparent,
        [
            0xC3,
            0x7D, 0x00, 0xC0, 0x04, 0x00, 0x14, 0x10, 0xAD, 0x00, 0xE0,
            0xCF, 0x87
        ]);
        RunDmaToCompletion(transparent);
        Require(transparent.Dma.DimensionalFlags == 0 && transparent.MemoryBus.ReadDirect(0xE001) == 0xE3,
            "$C3 left a zxnDDMA mode flag on.");

        // Layer 2 X,Y: 256x192 is row by row, the 320 and 640 modes column by column with
        // a 9-bit X; the hidden screen takes NextReg $13's bank.
        ZxNextMachine layer2 = ZxNextMachineFactory.CreateFoundationMachine();
        layer2.MemoryBus.WritePhysicalRam(0, 0x0100, 0x77);
        layer2.Registers.Write(0x12, 9);
        layer2.Registers.Write(0x13, 12);
        foreach ((byte control, byte flags, int x, byte y, int expected) in new (byte, byte, int, byte, int)[]
        {
            (0x00, 0x11, 5, 3, 0x40000 + (9 * 0x4000) + (3 * 256) + 5),
            (0x10, 0x11, 0x105, 7, 0x40000 + (9 * 0x4000) + (0x105 * 256) + 7),
            (0x00, 0x31, 5, 3, 0x40000 + (12 * 0x4000) + (3 * 256) + 5)
        })
        {
            layer2.Registers.Write(0x70, control);
            WriteDmaCommand(layer2,
            [
                0xC3,
                0xD7, 0x04,
                0x7D, 0x00, 0x01, 0x01, 0x00, 0x14, 0x10, 0xA1,
                0xDF, flags, (byte)x, (byte)(x >> 8), y,
                0xCF, 0x87
            ]);
            RunDmaToCompletion(layer2);
            Require(layer2.MemoryBus.ReadPhysicalSram(expected) == 0x77,
                $"Layer 2 X,Y with $70=${control:X2}, flags ${flags:X2} did not write ${expected:X6}.");
        }

        // The SRAM behind the video block RAM travels in snapshot files.
        ZxNextMachineSnapshot captured = ZxNextMachineSnapshotService.Capture(physical);
        using var file = new MemoryStream();
        ZxNextSnapshotFile.Save(file, captured);
        file.Position = 0;
        ZxNextMachine reloaded = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextMachineSnapshotService.Restore(reloaded, ZxNextSnapshotFile.Load(file));
        Require(reloaded.MemoryBus.ReadPhysicalSram(0x054000) == 0x5B
                && reloaded.MemoryBus.ReadPhysicalRam(10, 0) == screenByte,
            "The SRAM behind bank 5's block RAM did not survive a snapshot file.");
    }

    /// <summary>
    /// The prescaler, burst mode and auto restart, against <c>dma.vhd</c>: the timer is zeroed
    /// at a byte's first read clock and gains one per 28 MHz tick, the DMA waits after the
    /// write until timer / 32 reaches the prescaler, then WAITING_ACK (or FINISH_DMA). So a
    /// byte starts every ceil(32P / ticks-per-clock) + 3 clocks - 403 T at 3.5 MHz and 3203 T
    /// at 28 MHz for P = 100 (the figures Klive IDE measured on its simulation of the core) -
    /// and the last byte's wait ends at FINISH_DMA, one clock sooner.
    /// </summary>
    private static void VerifyDmaPrescalerAndBurst()
    {
        // Continuous mode keeps the bus through every wait.
        foreach ((ZxNextCpuSpeed speed, ulong expectedTicks) in new (ZxNextCpuSpeed, ulong)[]
        {
            (ZxNextCpuSpeed.Mhz3_5, (1 + (19UL * 403) + 402 + 4) * 8),
            (ZxNextCpuSpeed.Mhz28, 1 + (19UL * 3203) + 3202 + 4)
        })
        {
            ZxNextMachine paced = CreateDmaCopyMachine(out ushort source, out ushort destination);
            SetCpuSpeedNow(paced, speed);
            ulong before = paced.BusArbiter.DmaOwnedMasterTicks;
            WriteDmaCommand(paced, PacedCopy(source, destination, 20, mode: 0xAD, prescaler: 100, restart: false));
            RunDmaToCompletion(paced);
            ulong ticks = paced.BusArbiter.DmaOwnedMasterTicks - before;
            Require(ticks == expectedTicks && CountCopiedBytes(paced, source, destination) == 20,
                $"Twenty bytes at prescaler 100 and {speed} took {ticks} master ticks, not {expectedTicks}.");
        }

        // Burst mode gives the bus back while it waits, so the CPU runs between bytes; it
        // keeps to the same pace. Continuous mode, and burst mode without a prescaler, keep
        // the bus for the whole block.
        foreach ((byte mode, byte prescaler, bool cpuRuns) in new (byte, byte, bool)[]
        {
            (0xCD, 100, true),
            (0xAD, 100, false),
            (0xCD, 0, false)
        })
        {
            ZxNextMachine machine = CreateDmaCopyMachine(out ushort source, out ushort destination);
            LoadPassCounter(machine);
            WriteDmaCommand(machine, PacedCopy(source, destination, 16, mode, prescaler, restart: false));
            ulong start = machine.MasterTicks;
            ulong dmaTicksBefore = machine.BusArbiter.DmaOwnedMasterTicks;
            ulong finished = 0;
            // The CPU halts once the last byte lands; the DMA still sits out that byte's
            // prescaler wait before it finishes.
            for (int step = 0; step < 100_000 && (!machine.Cpu.IsHalted || machine.Dma.IsActive); step++)
            {
                machine.StepInstruction();
                if (finished == 0 && !machine.Dma.IsActive)
                {
                    finished = machine.MasterTicks;
                }
            }

            int passes = machine.Cpu.HL;
            Require(machine.Cpu.IsHalted && CountCopiedBytes(machine, source, destination) == 16,
                $"The {(mode == 0xCD ? "burst" : "continuous")} transfer at prescaler {prescaler} did not finish.");
            Require(cpuRuns ? passes > 100 : passes == 1,
                $"WR4 ${mode:X2} at prescaler {prescaler}: the CPU made {passes} passes during the block.");
            if (cpuRuns)
            {
                // Sixteen bytes, each a request answered at the CPU's next machine-cycle end.
                ulong tstates = (finished - start) / 8;
                Require(tstates >= (15 * 403) + 402 && tstates <= 16 * 420,
                    $"Sixteen burst bytes at prescaler 100 took {tstates} T-states.");

                // Each byte holds the CPU for WAITING_ACK, the 3 + 3 clock transfer, the first
                // waiting clock and the CPU's two clocks to see BUSRQ go high: ten clocks.
                ulong held = machine.BusArbiter.DmaOwnedMasterTicks - dmaTicksBefore;
                Require(held == 16 * 10 * 8,
                    $"Burst mode held the bus {held / 8} clocks for sixteen bytes, not 160.");
            }
        }

        // Auto restart: the block repeats from its start addresses, picking up a changed
        // source, and keeps reporting the end of block.
        ZxNextMachine looping = CreateDmaCopyMachine(out ushort loopSource, out ushort loopDestination);
        LoadSpinLoop(looping);
        WriteDmaCommand(looping, PacedCopy(loopSource, loopDestination, 4, mode: 0xCD, prescaler: 50, restart: true));
        looping.RunFrame();
        Require(looping.Dma.IsActive && CountCopiedBytes(looping, loopSource, loopDestination) == 4
                && looping.MemoryBus.ReadDirect((ushort)(loopDestination + 4)) == 0,
            "An auto-restarting block did not copy its four bytes.");
        for (int index = 0; index < 4; index++)
        {
            looping.MemoryBus.WriteDirect((ushort)(loopSource + index), (byte)(0x50 + index));
        }

        looping.RunFrame();
        WriteDmaCommand(looping, [0xBF]);
        Require(looping.MemoryBus.ReadDirect(loopDestination) == 0x50
                && looping.MemoryBus.ReadDirect((ushort)(loopDestination + 3)) == 0x53
                && (looping.PortBus.ReadUncontended(ZxNextDmaDevice.ZxnControlPort) & 0x20) == 0,
            "Auto restart did not repeat the block from its start, or cleared the end of block.");

        // A burst snapshot taken mid-wait carries on identically.
        ZxNextMachineSnapshot captured = ZxNextMachineSnapshotService.Capture(looping);
        using (var file = new MemoryStream())
        {
            ZxNextSnapshotFile.Save(file, captured);
            file.Position = 0;
            ZxNextMachine resumed = ZxNextMachineFactory.CreateFoundationMachine();
            ZxNextMachineSnapshotService.Restore(resumed, ZxNextSnapshotFile.Load(file));
            for (int frame = 0; frame < 3; frame++)
            {
                looping.RunFrame();
                resumed.RunFrame();
            }

            Require(resumed.Dma.CaptureState() == looping.Dma.CaptureState()
                    && resumed.Cpu.Cyc == looping.Cpu.Cyc
                    && resumed.BusArbiter.DmaOwnedMasterTicks == looping.BusArbiter.DmaOwnedMasterTicks,
                "A machine restored in a burst-mode wait did not carry on as the original did.");
        }

        // $83 stops a burst transfer where it is; $87 carries on from there.
        ZxNextMachine stopped = CreateDmaCopyMachine(out ushort stopSource, out ushort stopDestination);
        LoadSpinLoop(stopped);
        WriteDmaCommand(stopped, PacedCopy(stopSource, stopDestination, 64, mode: 0xCD, prescaler: 100, restart: false));
        stopped.RunForTstates(8000);
        WriteDmaCommand(stopped, [0x83]);
        int atStop = CountCopiedBytes(stopped, stopSource, stopDestination);
        stopped.RunForTstates(8000);
        Require(atStop > 5 && atStop < 40
                && CountCopiedBytes(stopped, stopSource, stopDestination) == atStop,
            $"$83 did not stop the burst transfer where it was ({atStop} bytes).");
        WriteDmaCommand(stopped, [0x87]);
        stopped.RunForTstates(40_000);
        Require(CountCopiedBytes(stopped, stopSource, stopDestination) == 64
                && stopped.MemoryBus.ReadDirect((ushort)(stopDestination + 64)) == 0
                && stopped.Dma.ByteCounter == 64,
            "$87 did not carry the stopped transfer on to the end of its block, once.");

        // Auto restart without burst pacing locks the CPU out for good. The frames still
        // complete and the CPU gets no time.
        ZxNextMachine locked = CreateDmaCopyMachine(out ushort lockSource, out ushort lockDestination);
        LoadSpinLoop(locked);
        WriteDmaCommand(locked, PacedCopy(lockSource, lockDestination, 4, mode: 0xAD, prescaler: 0, restart: true));
        locked.RunFrame();
        ulong lockedCycles = locked.Cpu.Cyc;
        ulong lockedFrame = locked.UlaTiming.FrameNumber;
        locked.RunFrame();
        locked.RunForTstates(1000);
        Require(locked.Dma.IsActive && locked.Dma.HoldsBusIndefinitely
                && locked.Cpu.Cyc == lockedCycles
                && locked.UlaTiming.FrameNumber > lockedFrame,
            "A DMA that never lets go of the bus stopped the frames or gave the CPU time.");
    }

    /// <summary>
    /// A DMA access to the SPI data port <c>$EB</c> waits for the SPI master's byte
    /// (<c>dma_wait_n = z80_wait_n and spi_wait_n</c>). Traced through <c>spi_master.vhd</c>
    /// and <c>dma.vhd</c>'s falling-edge registers, a three- or four-clock cycle ends 18 clocks
    /// after it began; a two-clock cycle is over before the wait reaches it; and with the SPI
    /// port disabled (internal port enable bit 11, NextReg <c>$83</c> bit 3) nothing waits.
    /// </summary>
    private static void VerifyDmaSpiWait()
    {
        foreach ((string name, byte[] ports, byte? nextReg83, ulong clocks) in new (string, byte[], byte?, ulong)[]
        {
            // Each case moves two bytes: the grant's WAITING_ACK clock, two reads and two
            // writes, then the four clocks that give the bus back.
            ("memory to $EB, three-clock write", [0x14, 0x28], null, 1 + (2 * (3 + 18)) + 4),
            ("memory to $EB, two-clock write", [0x14, 0x68, 0x02], null, 1 + (2 * (3 + 2)) + 4),
            ("memory to $EB with the SPI port off", [0x14, 0x28], 0xF7, 1 + (2 * (3 + 3)) + 4),
        })
        {
            ZxNextMachine machine = CreateDmaCopyMachine(out ushort source, out _);
            if (nextReg83 is { } value)
            {
                machine.Registers.Write(0x83, value);
            }

            ulong before = machine.BusArbiter.DmaOwnedMasterTicks;
            WriteDmaCommand(machine,
            [
                0xC3,
                0x7D, (byte)source, (byte)(source >> 8), 0x02, 0x00,
                .. ports,
                0xAD, ZxNextPortBus.SpiDataPort, 0x00,
                0xCF, 0x87
            ]);
            RunDmaToCompletion(machine);
            ulong ticks = machine.BusArbiter.DmaOwnedMasterTicks - before;
            Require(ticks == clocks * 8,
                $"DMA {name}: {ticks / 8} clocks, not {clocks}.");
        }

        // Reading from $EB waits the same way.
        ZxNextMachine reader = CreateDmaCopyMachine(out _, out ushort destination);
        ulong readerBefore = reader.BusArbiter.DmaOwnedMasterTicks;
        WriteDmaCommand(reader,
        [
            0xC3,
            0x7D, ZxNextPortBus.SpiDataPort, 0x00, 0x02, 0x00,
            0x2C,       // WR1: I/O, fixed
            0x10,       // WR2: memory, increment
            0xAD, (byte)destination, (byte)(destination >> 8),
            0xCF, 0x87
        ]);
        RunDmaToCompletion(reader);
        ulong readerTicks = reader.BusArbiter.DmaOwnedMasterTicks - readerBefore;
        Require(readerTicks == (1 + (2 * (18 + 3)) + 4) * 8UL,
            $"DMA reads from $EB took {readerTicks / 8} clocks, not 47.");
    }

    /// <summary>
    /// The System/Next 26.11a zxnDDMA guide's <c>zxnddma_compare.asm</c>, assembled by the
    /// user (<c>sjasmplus --sym=zxnddma_compare.sym zxnddma_compare.asm</c>), against the
    /// times its header gives as measured on an issue 5 board at 28 MHz.
    /// </summary>
    /// <remarks>
    /// The program draws 32 16x16 balls five ways, each timed from video line 0 to the line
    /// NextRegs <c>$1E/$1F</c> read at its end, and prints (lines x 64 + 50) / 100 tenths of a
    /// millisecond. So each printed figure allows a range of line counts, and a run must land
    /// in every one. The results are read from the program's <c>times</c> table and
    /// <c>have_zxnddma</c> flag, found in the symbol file beside the NEX. The program is not
    /// distributed with ZedExEss, so this runs only when its path is given.
    /// </remarks>
    private static void VerifyZxnDdmaCompareTimings(string nexPath, StreamWriter writer)
    {
        string symbolPath = Path.ChangeExtension(nexPath, ".sym");
        Require(File.Exists(nexPath) && File.Exists(symbolPath),
            $"Expected {nexPath} and the symbol file {symbolPath} beside it.");
        Dictionary<string, ushort> symbols = File.ReadAllLines(symbolPath)
            .Select(line => line.Split(": EQU "))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => parts[0].Trim(),
                parts => Convert.ToUInt16(parts[1].Trim(), 16));
        if (!symbols.TryGetValue("times", out ushort times)
            || !symbols.TryGetValue("have_zxnddma", out ushort haveZxnDdma))
        {
            throw new InvalidOperationException("The symbol file has no times or have_zxnddma label.");
        }

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextNexLoader.Load(machine, File.ReadAllBytes(nexPath));
        ushort ReadTime(int way) => (ushort)(machine.MemoryBus.ReadDirect((ushort)(times + (way * 2)))
            | (machine.MemoryBus.ReadDirect((ushort)(times + (way * 2) + 1)) << 8));
        int frames = 0;
        while (frames < 200 && ReadTime(4) == 0)
        {
            machine.RunFrame(presentFrame: false);
            frames++;
        }

        (string Name, int TenthsOfMs)[] board =
        [
            ("1 CPU, LDIR", 72),
            ("2 CPU, skip $E3", 156),
            ("3 zxnDMA, per column", 82),
            ("4 zxnDDMA, opaque", 21),
            ("5 zxnDDMA, transparent", 21)
        ];
        Require(machine.MemoryBus.ReadDirect(haveZxnDdma) == 1,
            "The program did not find the zxnDDMA.");
        var results = new List<string>();
        for (int way = 0; way < board.Length; way++)
        {
            int lines = ReadTime(way);
            int tenths = ((lines * 64) + 50) / 100;
            results.Add($"{board[way].Name} {lines} lines = {tenths / 10}.{tenths % 10} ms");
            Require(tenths == board[way].TenthsOfMs,
                $"Way {board[way].Name} took {lines} video lines, which the program prints as "
                    + $"{tenths / 10}.{tenths % 10} ms; the board printed "
                    + $"{board[way].TenthsOfMs / 10}.{board[way].TenthsOfMs % 10} ms.");
        }

        writer.WriteLine($"  zxnddma_compare milestone (after {frames} frames): {string.Join("; ", results)}");
    }

    /// <summary>
    /// Port decode as zxnext.vhd does it, rule by rule: $7FFD's timing-dependent A14, the
    /// Soundrive mode 2 conflict on $F1/$F9, the AY's A2/A0 decode with $BFFD readable only
    /// in +3 timing and $BFF5's information, the $FF port enable, the one ULA interrupt
    /// disable flip-flop shared by $FF, $22 and $C4, $FE bit 6 on an issue 2 keyboard,
    /// undecoded ports reading $FF, and I/O contention following the same decodes.
    /// </summary>
    private static void VerifyPortDecodeRules()
    {
        // The machine timing takes effect for the decode when time next advances, as the
        // CPU's I/O cycles always make it; here nothing runs in between, so it is applied.
        static void SetMachineTiming(ZxNextMachine machine, byte value)
        {
            machine.Registers.Write(0x03, value);
            machine.UlaTiming.SynchronizeProfile();
        }

        // $7FFD: A15 = 0, A1:0 = 01; A14 only in +3 timing.
        foreach ((byte timing, ushort port, bool pages) in new (byte, ushort, bool)[]
        {
            (0xA1, 0x3FFD, true),  // 128K timing
            (0x91, 0x0FFD, true),  // 48K timing
            (0xB3, 0x3FFD, false), // +3 timing
            (0xB3, 0x7FFD, true)
        })
        {
            ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
            SetMachineTiming(machine, timing);
            machine.PortBus.WriteUncontended(port, 0x05);
            Require((machine.Paging.Port7Ffd == 0x05) == pages,
                $"A write to ${port:X4} with NextReg $03=${timing:X2} {(pages ? "did not page" : "paged")}.");
        }

        // With $1FFD disabled, a $1FFD write is a $7FFD write outside +3 timing.
        ZxNextMachine noPlus3Port = ZxNextMachineFactory.CreateFoundationMachine();
        SetMachineTiming(noPlus3Port, 0xA1);
        noPlus3Port.Registers.Write(0x82, 0xF7);
        noPlus3Port.PortBus.WriteUncontended(0x1FFD, 0x03);
        Require(noPlus3Port.Paging.Port7Ffd == 0x03 && noPlus3Port.Paging.Port1Ffd == 0,
            "A $1FFD write with $1FFD disabled did not page as $7FFD in 128K timing.");

        // Soundrive mode 2 (port enable bit 18, NextReg $84 bit 2) keeps $F1 off the paging ports.
        ZxNextMachine soundrive = ZxNextMachineFactory.CreateFoundationMachine();
        SetMachineTiming(soundrive, 0xA1);
        soundrive.PortBus.WriteUncontended(0x7FF1, 0x04);
        Require(soundrive.Paging.Port7Ffd == 0,
            "A $7FF1 write paged while Soundrive mode 2 held $F1.");
        soundrive.Registers.Write(0x84, 0xFB);
        soundrive.PortBus.WriteUncontended(0x7FF1, 0x04);
        Require(soundrive.Paging.Port7Ffd == 0x04,
            "A $7FF1 write did not page with Soundrive mode 2 off.");

        // The AY: $FFFD and $BFFD need A2 = 1 and A1:0 = 01.
        ZxNextMachine ay = ZxNextMachineFactory.CreateFoundationMachine();
        SetMachineTiming(ay, 0xA1);
        ZxNextPortBus ports = ay.PortBus;
        ports.WriteUncontended(0xFFFD, 0x07);
        ports.WriteUncontended(0xBFFD, 0x2A);
        ports.WriteUncontended(0xFFFC, 0x02); // A0 = 0: the ULA, not the AY
        ports.WriteUncontended(0xFFF9, 0x03); // A2 = 0: nothing
        ports.WriteUncontended(0xBFF9, 0x15); // A2 = 0: nothing
        Require(ports.ReadUncontended(0xFFFD) == 0x2A && ay.Audio.ReadInformation() == ports.ReadUncontended(0xBFF5),
            "An address without A0 or A2 reached the AY, or $BFF5 did not read the information.");
        Require(ports.ReadUncontended(0xBFFD) == 0xFF && ports.ReadUncontended(0xDFFD) == 0x2A,
            "$BFFD read the AY outside +3 timing, or $DFFD did not read it.");
        SetMachineTiming(ay, 0xB3);
        Require(ports.ReadUncontended(0xBFFD) == 0x2A,
            "$BFFD did not read the AY in +3 timing.");
        Require(ports.ReadUncontended(0x0003) == 0xFF,
            "An undecoded port did not read $FF.");

        // Port $FF needs its enable (NextReg $82 bit 0) to write and to read the Timex register.
        ZxNextMachine timex = ZxNextMachineFactory.CreateFoundationMachine();
        SetMachineTiming(timex, 0x91);
        timex.Registers.Write(0x08, 0x14); // $FF reads the Timex register
        timex.PortBus.WriteUncontended(0x00FF, 0x06);
        Require(timex.Registers.TimexPort == 0x06 && timex.PortBus.ReadUncontended(0x00FF) == 0x06,
            "Port $FF did not write and read the Timex register.");
        timex.Registers.Write(0x82, 0xFE);
        timex.PortBus.WriteUncontended(0x00FF, 0x01);
        Require(timex.Registers.TimexPort == 0x06 && timex.PortBus.ReadUncontended(0x00FF) != 0x06,
            "Port $FF reached the Timex register with its port enable off.");

        // One interrupt-disable flip-flop: port $FF bit 6, NextReg $22 bit 2, NOT $C4 bit 0.
        ZxNextMachine flipFlop = ZxNextMachineFactory.CreateFoundationMachine();
        flipFlop.Registers.Write(0x22, 0x04);
        Require(!flipFlop.Registers.UlaInterruptEnabled
                && (flipFlop.Registers.TimexPort & 0x40) != 0
                && (flipFlop.Registers.Read(0xC4) & 0x01) == 0,
            "NextReg $22 bit 2 did not set the shared interrupt disable.");
        flipFlop.PortBus.WriteUncontended(0x00FF, 0x00);
        Require(flipFlop.Registers.UlaInterruptEnabled
                && (flipFlop.Registers.Read(0x22) & 0x04) == 0
                && (flipFlop.Registers.Read(0xC4) & 0x01) != 0,
            "A port $FF write did not clear the interrupt disable that $22 had set.");
        flipFlop.Registers.Write(0xC4, 0x80);
        Require(!flipFlop.Registers.UlaInterruptEnabled
                && (flipFlop.Registers.TimexPort & 0x40) != 0
                && (flipFlop.Registers.Read(0x22) & 0x04) != 0,
            "NextReg $C4 bit 0 did not write the shared interrupt disable.");

        // $FE bit 6: EAR out, or MIC on an issue 2 keyboard (the relaxed EAR input).
        ZxNextMachine issue2 = ZxNextMachineFactory.CreateFoundationMachine();
        issue2.PortBus.WriteUncontended(0x00FE, 0x08);
        Require((issue2.PortBus.ReadUncontended(0xFFFE) & 0x40) == 0,
            "$FE bit 6 followed MIC without the issue 2 keyboard.");
        issue2.Registers.Write(0x08, 0x11);
        Require((issue2.PortBus.ReadUncontended(0xFFFE) & 0x40) != 0,
            "$FE bit 6 did not follow MIC on an issue 2 keyboard.");
        issue2.PortBus.WriteUncontended(0x00FE, 0x00);
        Require((issue2.PortBus.ReadUncontended(0xFFFE) & 0x40) == 0,
            "$FE bit 6 stayed set with MIC and EAR out both clear.");

        // Contention follows the same decodes: $3FFD is $7FFD in 128K timing, not in +3, and
        // the ULA+ ports are contended only while enabled (NextReg $85 bit 0).
        ZxNextMachine contended = ZxNextMachineFactory.CreateFoundationMachine();
        SetMachineTiming(contended, 0xA1);
        bool at128 = contended.Contention.IsPortContended(0x3FFD);
        bool ulaPlus = contended.Contention.IsPortContended(0xBF3B);
        contended.Registers.Write(0x85, 0x8E);
        bool ulaPlusOff = contended.Contention.IsPortContended(0xBF3B);
        SetMachineTiming(contended, 0xB3);
        Require(at128 && ulaPlus && !ulaPlusOff && !contended.Contention.IsPortContended(0x3FFD),
            "Port contention did not follow the $7FFD and ULA+ decodes.");
    }

    /// <summary>A copy with WR2's prescaler, the given WR4 mode and optionally WR5's restart.</summary>
    private static byte[] PacedCopy(
        ushort source,
        ushort destination,
        ushort length,
        byte mode,
        byte prescaler,
        bool restart)
    {
        return
        [
            0xC3,
            0x7D, (byte)source, (byte)(source >> 8), (byte)length, (byte)(length >> 8),
            0x14,
            0x50, 0x21, prescaler, // WR2: memory, increment; timing 3 cycles, prescaler follows
            mode, (byte)destination, (byte)(destination >> 8),
            restart ? (byte)0xB2 : (byte)0x82,
            0xCF, 0x87
        ];
    }

    /// <summary>
    /// $8000: counts passes in HL until the block's last destination byte ($E00F) arrives,
    /// then halts. Each pass is 35 T-states.
    /// </summary>
    private static void LoadPassCounter(ZxNextMachine machine)
    {
        byte[] program =
        [
            0xF3,             // DI
            0x21, 0x00, 0x00, // LD HL,0
            0x23,             // INC HL
            0x3A, 0x0F, 0xE0, // LD A,($E00F)
            0xB7,             // OR A
            0x28, 0xF9,       // JR Z,-7
            0x76              // HALT
        ];
        for (int index = 0; index < program.Length; index++)
        {
            machine.MemoryBus.WriteDirect((ushort)(0x8000 + index), program[index]);
        }

        machine.Cpu.PC = 0x8000;
    }

    private static void LoadSpinLoop(ZxNextMachine machine)
    {
        machine.MemoryBus.WriteDirect(0x8000, 0xF3); // DI
        machine.MemoryBus.WriteDirect(0x8001, 0x18); // JR $
        machine.MemoryBus.WriteDirect(0x8002, 0xFE);
        machine.Cpu.PC = 0x8000;
    }

    /// <summary>A machine with 64 numbered bytes at $C000 and an empty page at $E000.</summary>
    private static ZxNextMachine CreateDmaCopyMachine(out ushort source, out ushort destination)
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.MemoryBus.SetMmuPage(6, 0x20);
        machine.MemoryBus.SetMmuPage(7, 0x21);
        source = 0xC000;
        destination = 0xE000;
        for (int index = 0; index < 64; index++)
        {
            machine.MemoryBus.WriteDirect((ushort)(source + index), (byte)(index + 1));
        }

        return machine;
    }

    private static int CountCopiedBytes(ZxNextMachine machine, ushort source, ushort destination)
    {
        int count = 0;
        while (count < 64
            && machine.MemoryBus.ReadDirect((ushort)(destination + count))
                == machine.MemoryBus.ReadDirect((ushort)(source + count)))
        {
            count++;
        }

        return count;
    }

    private static byte[] ReadDmaBytes(ZxNextMachine machine, int count)
    {
        var bytes = new byte[count];
        for (int index = 0; index < count; index++)
        {
            bytes[index] = machine.PortBus.ReadUncontended(ZxNextDmaDevice.ZxnControlPort);
        }

        return bytes;
    }

    private static void WriteDmaCommandOn(ZxNextMachine machine, byte port, ReadOnlySpan<byte> command)
    {
        foreach (byte value in command)
        {
            machine.PortBus.WriteUncontended(port, value);
        }
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

        // zxula.vhd loads the border colour with the shift register, every 8 pixels on the
        // display's cell grid, so the new colour starts at the next group: logical pixel 16.
        const int nextGroupLogicalPixel = 16;
        int[] borderFrame = borderMachine.Video.FrameBuffer;
        Require(borderFrame[0] == darkRed
                && borderFrame[(nextGroupLogicalPixel * 2) - 1] == darkRed
                && borderFrame[nextGroupLogicalPixel * 2] == darkBlue
                && borderFrame[^1] == darkBlue,
            "A mid-line FE write did not take effect at the next 8-pixel group of the border.");

        // Pentagon timing reloads the border colour on every clock: the split is at the pixel.
        ZxNextMachine pentagonBorder = ZxNextMachineFactory.CreateFoundationMachine();
        pentagonBorder.Registers.Write(0x03, 0xC3);
        pentagonBorder.UlaTiming.SynchronizeProfile();
        pentagonBorder.PortBus.WriteUncontended(0x00FE, 0x02);
        pentagonBorder.Video.Reset();
        profile = pentagonBorder.UlaTiming.Profile;
        visibleStartTick = checked(
            ((ulong)profile.VisibleFirstLine * (ulong)profile.PixelsPerLine
                + (ulong)profile.VisibleStartHorizontal)
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        splitTick = visibleStartTick
            + (splitLogicalPixel * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        pentagonBorder.Video.AdvanceMasterTicks(splitTick);
        pentagonBorder.PortBus.WriteUncontended(0x00FE, 0x01);
        pentagonBorder.Video.AdvanceMasterTicks(profile.MasterTicksPerFrame - splitTick);
        borderFrame = pentagonBorder.Video.FrameBuffer;
        Require(borderFrame[(splitLogicalPixel * 2) - 1] == darkRed
                && borderFrame[splitLogicalPixel * 2] == darkBlue,
            "In Pentagon timing a mid-line FE write did not split the border at its pixel.");
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
            SetCpuSpeedNow(turbo, speed);
            turbo.MemoryBus.WritePhysicalRam(10, 0x1800, 0x33);
            turbo.Video.Reset();
            turbo.MemoryBus.WriteCpu(0x5800, 0x44);
            int commitTick = commitTicks[(int)speed];
            Require(turbo.MemoryBus.ReadDisplayRam(10, 0x1800, (ulong)commitTick) == 0x33
                    && turbo.MemoryBus.ReadDisplayRam(10, 0x1800, (ulong)commitTick + 1UL) == 0x44,
                $"The {turbo.CpuClockHz / 1_000_000.0:0.#} MHz screen write missed its T2/BRAM commit edge.");
        }
    }

    /// <summary>
    /// The Next's audio, from <c>audio_mixer.vhd</c>, <c>turbosound.vhd</c> and <c>ym2149.vhd</c>.
    /// </summary>
    /// <remarks>
    /// Every level here was worked out by hand from those files, not read from the device:
    /// EAR is 512 and MIC 128 per side; a chip's ABC output is A+B left and C+B right; a
    /// YM channel at fixed volume v reads the 32-entry table at v*2+1; the host's mono
    /// sample is (left + right) * 2.
    /// </remarks>
    private static void VerifyNextAudio()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        Require(machine.Registers.Read(0x06) == 0xA0
                && machine.Registers.Read(0x08) == 0x90
                && machine.Audio.ActiveChip == 0
                && machine.PortBus.ReadUncontended(0xBFF5) == 0xC0,
            "Next audio reset registers or AY-0 selection are incorrect.");

        // EAR: 512 a side, so (512 + 512) * 2.
        machine.PortBus.WriteUncontended(0x00FE, 0x10);
        short[] beeper = ReadNextAudio(machine, 256);
        Require(beeper.All(static sample => sample == 2048),
            "The Next $FE EAR bit did not give audio_mixer.vhd's 512 a side.");

        // MIC: 128 a side, cancelled by an issue 2 keyboard (beep_mic_final).
        machine.PortBus.WriteUncontended(0x00FE, 0x08);
        Require(ReadNextAudioAfterWrite(machine, 128).All(static sample => sample == 512),
            "The Next $FE MIC bit did not give audio_mixer.vhd's 128 a side.");
        machine.Registers.Write(0x08, 0x91);
        Require(ReadNextAudioAfterWrite(machine, 128).All(static sample => sample == 0),
            "An issue 2 keyboard ($08 bit 0) did not cancel the MIC bit's audio.");
        machine.PortBus.WriteUncontended(0x00FE, 0x18);
        Require(ReadNextAudioAfterWrite(machine, 128).All(static sample => sample == 2048),
            "An issue 2 keyboard changed the EAR bit's audio.");
        machine.Registers.Write(0x08, 0x90);

        machine.PortBus.WriteUncontended(0x00FE, 0x00);
        short[] silence = ReadNextAudioAfterWrite(machine, 128);
        Require(silence.All(static sample => sample == 0),
            "Clearing the Next $FE audio bits did not restore silence.");

        machine.Registers.Write(0x06, 0xE0);
        machine.PortBus.WriteUncontended(0x00FE, 0x10);
        Require(ReadNextAudioAfterWrite(machine, 128).All(static sample => sample == 0),
            "NextReg $06 speaker-exclusive mode did not divert the beeper from host audio.");
        machine.Registers.Write(0x06, 0xA0);
        machine.PortBus.WriteUncontended(0x00FE, 0x00);

        // A tone on A alone: YM level 31 is 255, left only, so 0 or 510. Period 32 toggles
        // every 32 steps of 8 clock enables at 1.75 MHz.
        WriteAyRegister(machine, 0, 0x20);
        WriteAyRegister(machine, 1, 0x00);
        WriteAyRegister(machine, 7, 0x3E);
        WriteAyRegister(machine, 8, 0x0F);
        machine.PortBus.WriteUncontended(0xFFFD, 8);
        Require(machine.PortBus.ReadUncontended(0xFFFD) == 0x0F
                && machine.PortBus.ReadUncontended(0xBFFD) == 0x0F,
            "AY-0 register readback failed on $FFFD/$BFFD.");
        short[] tone = ReadNextAudio(machine, 1024);
        int toggles = 0;
        for (int index = 1; index < tone.Length; index++)
        {
            toggles += tone[index] != tone[index - 1] ? 1 : 0;
        }

        double expectedToggles = tone.Length * (double)ZxNextPsg.ClockHz / (8 * 32 * machine.Audio.SampleRate);
        Require(tone.All(static sample => sample is 0 or 510) && tone.Contains((short)510),
            "A full-volume YM tone on channel A was not 0 or 510 (255 on the left).");
        Require(Math.Abs(toggles - expectedToggles) <= 2,
            $"A period-32 tone toggled {toggles} times in {tone.Length} samples, not about {expectedToggles:0}: ym2149.vhd counts every 8 enables.");

        VerifyNextPsgNoise(machine.Audio.SampleRate);

        VerifyNextPsgRegisters();
        VerifyNextPsgLevelsAndEnvelopes();
        VerifyNextPsgStereo();
        VerifyNextPsgResets();

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

        // Everything at once: three chips with every channel at 15 (A+B and C+B a side, 510
        // each), plus EAR and MIC (640 a side): 2170 a side, 8680 mono. No limiter.
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
        Require(ReadNextAudio(denseMix, 128).All(static sample => sample == 8680),
            "The dense three-PSG/beeper mix was not audio_mixer.vhd's sum (2170 a side).");

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

        var renderedPhase = new ZxNextPsg(44_100);
        var silentPhase = new ZxNextPsg(44_100);
        foreach (ZxNextPsg chip in new[] { renderedPhase, silentPhase })
        {
            chip.WriteRegister(0, 0x31);
            chip.WriteRegister(1, 0x02);
            chip.WriteRegister(6, 0x07);
            chip.WriteRegister(7, 0x36);
            chip.WriteRegister(8, 0x0F);
            chip.WriteRegister(9, 0x10);
            chip.WriteRegister(11, 0x13);
            chip.WriteRegister(13, 0x0A);
        }

        for (int sample = 0; sample < 997; sample++)
        {
            renderedPhase.RenderSample(out _, out _, out _);
            silentPhase.AdvanceSample();
        }

        for (int sample = 0; sample < 257; sample++)
        {
            renderedPhase.RenderSample(out byte renderedA, out byte renderedB, out byte renderedC);
            silentPhase.RenderSample(out byte silentA, out byte silentB, out byte silentC);
            Require(renderedA == silentA && renderedB == silentB && renderedC == silentC,
                "Silent PSG advancement did not preserve tone/noise/envelope continuation phase.");
        }
    }

    /// <summary>
    /// The samples after a level written between pulls. The last pull ran part way into the
    /// sample the write falls in, so that one is the mean across the write and is skipped.
    /// </summary>
    private static short[] ReadNextAudioAfterWrite(ZxNextMachine machine, int count) =>
        ReadNextAudio(machine, count + 1)[1..];

    private static short[] ReadNextAudio(ZxNextMachine machine, int count)
    {
        var samples = new short[count];
        Require(machine.ReadSamples(samples, 0, count) == count,
            "The Next did not produce the audio samples asked for.");
        return samples;
    }

    /// <summary>
    /// ym2149.vhd's noise: <c>poly17 &lt;= (p0 xor p2 xor zero) &amp; p(16 downto 1)</c>, shifted
    /// every 16 clock enables at period 0 or 1 and every period * 16 above that.
    /// </summary>
    private static void VerifyNextPsgNoise(int sampleRate)
    {
        // The register's output bit (bit 0) before and after each shift, from power-on at 0.
        static int[] ReferenceNoise(int shifts)
        {
            var bits = new int[shifts + 1];
            int poly = 0;
            for (int shift = 1; shift <= shifts; shift++)
            {
                int feedback = (poly & 1) ^ ((poly >> 2) & 1) ^ (poly == 0 ? 1 : 0);
                poly = (feedback << 16) | (poly >> 1);
                bits[shift] = poly & 1;
            }

            return bits;
        }

        static int Transitions(IReadOnlyList<int> values)
        {
            int transitions = 0;
            for (int index = 1; index < values.Count; index++)
            {
                transitions += values[index] != values[index - 1] ? 1 : 0;
            }

            return transitions;
        }

        // At 109,375 samples a second each sample is exactly 16 enables, one shift: the PSG's
        // output is the reference sequence itself, give or take where the first shift falls.
        var psg = new ZxNextPsg(ZxNextPsg.ClockHz / 16);
        psg.WriteRegister(6, 0);
        psg.WriteRegister(7, 0x37);
        psg.WriteRegister(8, 0x0F);
        var produced = new int[400];
        for (int sample = 0; sample < produced.Length; sample++)
        {
            psg.RenderSample(out byte a, out byte b, out byte c);
            Require(b == 0 && c == 0 && a is 0 or 0xFF,
                "Noise on channel A alone was not 0 or 255 on A.");
            produced[sample] = a == 0 ? 0 : 1;
        }

        int[] reference = ReferenceNoise(produced.Length + 4);
        bool aligned = false;
        for (int offset = 0; offset <= 2 && !aligned; offset++)
        {
            aligned = produced.AsSpan().SequenceEqual(reference.AsSpan(offset, produced.Length));
        }

        Require(aligned,
            "The PSG noise was not ym2149.vhd's 17-bit sequence (taps 0 and 2, a 1 shifted in from zero).");

        // On the machine at period 31: one shift every 31 * 16 enables, about 12.5 samples,
        // so every change of the output bit is heard.
        ZxNextMachine noisy = ZxNextMachineFactory.CreateFoundationMachine();
        WriteAyRegister(noisy, 6, 31);
        WriteAyRegister(noisy, 7, 0x37);
        WriteAyRegister(noisy, 8, 0x0F);
        short[] noise = ReadNextAudio(noisy, 2048);
        int shifts = (int)(noise.Length * (long)ZxNextPsg.ClockHz / (16L * 31 * sampleRate));
        Require(noise.All(static sample => sample is 0 or 510),
            "Noise on channel A alone was not 0 or 510.");
        int heard = Transitions(noise.Select(static sample => sample == 0 ? 0 : 1).ToArray());
        int expected = Transitions(ReferenceNoise(shifts));
        Require(Math.Abs(heard - expected) <= 1,
            $"Period-31 noise changed {heard} times in {noise.Length} samples, not the {expected} of {shifts} shifts.");
    }

    /// <summary>ym2149.vhd's register store and read-back.</summary>
    private static void VerifyNextPsgRegisters()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        byte ReadPsg(byte register)
        {
            machine.PortBus.WriteUncontended(0xFFFD, register);
            return machine.PortBus.ReadUncontended(0xFFFD);
        }

        machine.PortBus.WriteUncontended(0xFFFD, 7);
        Require(machine.PortBus.ReadUncontended(0xFFFD) == 0xFF,
            "R7 did not read $FF after reset (ym2149.vhd resets it so, every port an output).");

        // All eight bits are stored; the YM reads them back, the AY masks the unused ones.
        byte[] written = [0x31, 0xFF, 0x42, 0xFF, 0x53, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x64, 0x75, 0xFF];
        for (byte register = 0; register < written.Length; register++)
        {
            WriteAyRegister(machine, register, written[register]);
        }

        for (byte register = 0; register < written.Length; register++)
        {
            Require(ReadPsg(register) == written[register],
                $"The YM did not read back all eight bits of R{register}.");
        }

        byte[] ayMasks = [0xFF, 0x0F, 0xFF, 0x0F, 0xFF, 0x0F, 0x1F, 0xFF, 0x1F, 0x1F, 0x1F, 0xFF, 0xFF, 0x0F];
        machine.Registers.Write(0x06, 0xA1);
        for (byte register = 0; register < written.Length; register++)
        {
            Require(ReadPsg(register) == (written[register] & ayMasks[register]),
                $"The AY did not mask R{register} as ym2149.vhd does.");
        }

        machine.Registers.Write(0x06, 0xA0);
        Require(ReadPsg(1) == 0xFF,
            "Switching to the AY and back lost the stored bits of R1.");

        // Registers 16-31: selected (five bits), writes ignored, $FF on a YM, aliases on an AY.
        machine.PortBus.WriteUncontended(0xFFFD, 0x15);
        Require(machine.PortBus.ReadUncontended(0xBFF5) == 0xD5,
            "$BFF5 did not show a five-bit register select.");
        machine.PortBus.WriteUncontended(0xFFFD, 0x10);
        machine.PortBus.WriteUncontended(0xBFFD, 0x99);
        Require(machine.PortBus.ReadUncontended(0xFFFD) == 0xFF,
            "A YM's register 16 did not read $FF (R0 holds $31).");
        Require(ReadPsg(0) == 0x31,
            "A write to register 16 reached R0.");
        machine.Registers.Write(0x06, 0xA1);
        Require(ReadPsg(0x11) == 0x0F && ReadPsg(0x10) == 0x31,
            "An AY's registers 16-31 did not alias 0-15.");
        machine.Registers.Write(0x06, 0xA0);

        // R14 and R15 read the inputs ($FF, tied high) while their port is an input.
        WriteAyRegister(machine, 14, 0x12);
        WriteAyRegister(machine, 15, 0x34);
        WriteAyRegister(machine, 7, 0x3F);
        Require(ReadPsg(14) == 0xFF && ReadPsg(15) == 0xFF,
            "R14/R15 did not read $FF while their ports were inputs.");
        WriteAyRegister(machine, 7, 0xC0);
        Require(ReadPsg(14) == 0x12 && ReadPsg(15) == 0x34,
            "R14/R15 did not read their registers while their ports were outputs.");
        WriteAyRegister(machine, 7, 0x7F);
        Require(ReadPsg(14) == 0x12 && ReadPsg(15) == 0xFF,
            "R7 bit 6 alone did not make R14 an output and leave R15 an input.");
        WriteAyRegister(machine, 7, 0xBF);
        Require(ReadPsg(14) == 0xFF && ReadPsg(15) == 0x34,
            "R7 bit 7 alone did not make R15 an output and leave R14 an input.");

        // A register select with bits 7:5 set is not a select, whatever its low bits.
        machine.PortBus.WriteUncontended(0xFFFD, 3);
        foreach (byte notSelect in new byte[] { 0x25, 0x45, 0x65, 0x85, 0xE5 })
        {
            machine.PortBus.WriteUncontended(0xFFFD, notSelect);
            Require(machine.PortBus.ReadUncontended(0xBFF5) == 0xC3,
                $"A $FFFD write of {notSelect:X2} (bits 7:5 set) changed the selected register.");
        }
    }

    /// <summary>The YM and AY volume tables and the 32-step envelope.</summary>
    private static void VerifyNextPsgLevelsAndEnvelopes()
    {
        byte[] ymTable =
        [
            0x00, 0x01, 0x01, 0x02, 0x02, 0x03, 0x03, 0x04, 0x06, 0x07, 0x09, 0x0A, 0x0C, 0x0E, 0x11, 0x13,
            0x17, 0x1B, 0x20, 0x25, 0x2C, 0x35, 0x3E, 0x47, 0x54, 0x66, 0x77, 0x88, 0xA1, 0xC0, 0xE0, 0xFF
        ];
        byte[] ayTable =
        [
            0x00, 0x03, 0x04, 0x06, 0x0A, 0x0F, 0x15, 0x22, 0x28, 0x41, 0x5B, 0x72, 0x90, 0xB5, 0xD7, 0xFF
        ];

        // Fixed volume v is level v*2+1: the YM reads all 32 entries, the AY the top four bits.
        foreach (bool ayMode in new[] { false, true })
        {
            ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
            if (ayMode)
            {
                machine.Registers.Write(0x06, 0xA1);
            }

            WriteAyRegister(machine, 7, 0x3F);
            for (int volume = 0; volume < 16; volume++)
            {
                int level = volume == 0 ? 0 : (volume * 2) + 1;
                WriteAyRegister(machine, 8, (byte)volume);
                int expected = (ayMode ? ayTable[level >> 1] : ymTable[level]) * 2;
                Require(ReadNextAudio(machine, 16).All(sample => sample == expected),
                    $"The {(ayMode ? "AY" : "YM")} at fixed volume {volume} was not table entry {level}.");
            }
        }

        // A slow envelope (64 steps a level, about 13 samples) on channel A alone.
        short[] Envelope(byte shape, bool ayMode, int count)
        {
            ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
            if (ayMode)
            {
                machine.Registers.Write(0x06, 0xA1);
            }

            WriteAyRegister(machine, 7, 0x3F);
            WriteAyRegister(machine, 8, 0x10);
            WriteAyRegister(machine, 11, 0x40);
            WriteAyRegister(machine, 12, 0x00);
            WriteAyRegister(machine, 13, shape);
            return ReadNextAudio(machine, count);
        }

        static HashSet<int> Levels(short[] samples) => samples.Select(static sample => sample / 2).ToHashSet();

        // An R13 write loads 31 falling (attack clear) or 0 rising and steps at the next clock
        // enable with the period counter restarted, so the first level heard for a whole
        // period is 30 or 1.
        Require(Envelope(0x00, ayMode: false, 12).All(sample => sample == ymTable[30] * 2)
                && Envelope(0x04, ayMode: false, 12).All(sample => sample == ymTable[1] * 2),
            "An R13 write did not start the envelope at level 30 (falling) or 1 (rising).");

        foreach (byte shape in new byte[] { 0x08, 0x0C })
        {
            Require(Levels(Envelope(shape, ayMode: false, 2048)).SetEquals(ymTable.Select(static value => (int)value)),
                $"The YM envelope shape {shape:X2} did not step through all 32 table levels.");
            Require(Levels(Envelope(shape, ayMode: true, 2048)).SetEquals(ayTable.Select(static value => (int)value)),
                $"The AY envelope shape {shape:X2} did not step through the 16-entry table.");
        }

        // The repeating shapes: sawtooths jump between the ends, triangles do not.
        static int LargestStep(short[] samples)
        {
            int largest = 0;
            for (int index = 1; index < samples.Length; index++)
            {
                largest = Math.Max(largest, Math.Abs(samples[index] - samples[index - 1]));
            }

            return largest;
        }

        Require(LargestStep(Envelope(0x0C, ayMode: false, 2048)) == 510
                && LargestStep(Envelope(0x08, ayMode: false, 2048)) == 510,
            "A sawtooth envelope did not wrap from one end to the other.");
        Require(LargestStep(Envelope(0x0E, ayMode: false, 2048)) <= 64
                && LargestStep(Envelope(0x0A, ayMode: false, 2048)) <= 64
                && Levels(Envelope(0x0E, ayMode: false, 2048)).SetEquals(ymTable.Select(static value => (int)value)),
            "A triangle envelope jumped instead of turning round.");

        // The one-shot shapes end at the level the shape table gives, after one 32-step ramp.
        (byte Shape, int End)[] holds =
        [
            (0x00, 0), (0x04, 0), (0x09, 0), (0x0B, 510), (0x0D, 510), (0x0F, 0)
        ];
        foreach ((byte shape, int end) in holds)
        {
            short[] samples = Envelope(shape, ayMode: false, 1024);
            Require(samples.AsSpan(600).ToArray().All(sample => sample == end)
                    && Levels(samples.AsSpan(0, 420).ToArray()).Count >= 24,
                $"The envelope shape {shape:X2} did not ramp once and hold at {end / 2}.");
        }
    }

    /// <summary>turbosound.vhd's per-chip stereo, mono and panning.</summary>
    private static void VerifyNextPsgStereo()
    {
        // A=15 (255), B=12 (level 25, 102), C=8 (level 17, 27) on AY 0 with TurboSound on.
        int Mix(byte nextReg08, byte nextReg09, byte chipSelect)
        {
            ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
            machine.Registers.Write(0x08, nextReg08);
            machine.Registers.Write(0x09, nextReg09);
            machine.PortBus.WriteUncontended(0xFFFD, chipSelect);
            WriteAyRegister(machine, 7, 0x3F);
            WriteAyRegister(machine, 8, 0x0F);
            WriteAyRegister(machine, 9, 0x0C);
            WriteAyRegister(machine, 10, 0x08);
            short[] samples = ReadNextAudio(machine, 16);
            Require(samples.All(sample => sample == samples[0]),
                "Fixed PSG volumes did not give a steady level.");
            return samples[0];
        }

        Require(Mix(0x12, 0x00, 0xFF) == (357 + 129) * 2,
            "ABC stereo was not A+B left and C+B right.");
        Require(Mix(0x32, 0x00, 0xFF) == (282 + 129) * 2,
            "ACB stereo ($08 bit 5) was not A+C left and C+B right.");
        Require(Mix(0x12, 0x20, 0xFF) == (384 + 384) * 2,
            "Mono ($09 bit 5 for AY 0) was not A+B+C on both sides.");
        Require(Mix(0x12, 0x40, 0xFF) == (357 + 129) * 2,
            "$09 bit 6 (AY 1's mono) changed AY 0.");
        Require(Mix(0x12, 0x00, 0xDF) == 357 * 2
                && Mix(0x12, 0x00, 0xBF) == 129 * 2
                && Mix(0x12, 0x00, 0x9F) == 0,
            "The pan bits (6 left, 5 right) did not keep and drop each side.");

        // With TurboSound off only the selected chip is heard.
        ZxNextMachine turbo = ZxNextMachineFactory.CreateFoundationMachine();
        turbo.Registers.Write(0x08, 0x12);
        turbo.PortBus.WriteUncontended(0xFFFD, 0xFE);
        WriteAyRegister(turbo, 7, 0x3F);
        WriteAyRegister(turbo, 8, 0x0F);
        turbo.PortBus.WriteUncontended(0xFFFD, 0xFF);
        WriteAyRegister(turbo, 7, 0x3F);
        WriteAyRegister(turbo, 8, 0x08);
        Require(ReadNextAudio(turbo, 16).All(static sample => sample == (255 + 27) * 2),
            "TurboSound did not sum the chips.");
        turbo.Registers.Write(0x08, 0x10);
        Require(ReadNextAudio(turbo, 16).All(static sample => sample == 27 * 2),
            "With TurboSound off, a chip other than the selected one was heard.");
        turbo.PortBus.WriteUncontended(0xFFFD, 0xFE);
        Require(turbo.Audio.ActiveChip == 0,
            "A chip select took effect with TurboSound off.");
    }

    /// <summary>Resets: PSG mode 11, a soft reset and a hard one.</summary>
    private static void VerifyNextPsgResets()
    {
        // Mode 11 holds turbosound.vhd and the PSGs in reset: AY 0, pans 11, register 0.
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.Registers.Write(0x08, 0x12);
        machine.PortBus.WriteUncontended(0xFFFD, 0x9F); // AY 0, neither side.
        machine.PortBus.WriteUncontended(0xFFFD, 0xBD); // AY 2, right only.
        WriteAyRegister(machine, 8, 0x0F);
        machine.Registers.Write(0x06, 0xA3);
        Require(machine.Audio.ActiveChip == 0 && machine.PortBus.ReadUncontended(0xBFF5) == 0xC0,
            "PSG mode 11 did not reset the chip and register selection.");
        machine.PortBus.WriteUncontended(0xFFFD, 0xFD);
        machine.PortBus.WriteUncontended(0xFFFD, 0x08);
        machine.PortBus.WriteUncontended(0xBFFD, 0x0F);
        Require(machine.Audio.ActiveChip == 0 && machine.PortBus.ReadUncontended(0xBFF5) == 0xC0,
            "A $FFFD write took effect while PSG mode 11 held reset.");
        Require(ReadNextAudio(machine, 32).All(static sample => sample == 0),
            "A PSG was heard while mode 11 held reset.");
        machine.Registers.Write(0x06, 0xA0);
        WriteAyRegister(machine, 7, 0x3F);
        Require(machine.PortBus.ReadUncontended(0xBFFD) == 0x3F,
            "The PSGs did not take writes after leaving mode 11.");
        machine.PortBus.WriteUncontended(0xFFFD, 8);
        Require(machine.PortBus.ReadUncontended(0xFFFD) == 0,
            "A write made while mode 11 held reset reached a register.");
        WriteAyRegister(machine, 8, 0x0F);
        Require(ReadNextAudio(machine, 16).All(static sample => sample == 510),
            "Mode 11 did not reset AY 0's pan to both sides.");

        // A reset clears the registers and leaves the generators running; the FPGA loading
        // (our hard reset) restarts them.
        ZxNextMachine soft = ZxNextMachineFactory.CreateFoundationMachine();
        WriteAyRegister(soft, 0, 0x37);
        WriteAyRegister(soft, 6, 0x01);
        WriteAyRegister(soft, 7, 0x36);
        WriteAyRegister(soft, 11, 0x05);
        WriteAyRegister(soft, 13, 0x0E);
        ReadNextAudio(soft, 333);
        ZxNextPsgState before = soft.Audio.GetChip(0).CaptureState();
        Require(before.Poly17 != 0 && before.ToneCount[0] != 0,
            "The PSG generators had not moved for the reset check.");
        soft.Reset(ZxNextResetKind.Soft);
        ZxNextPsgState afterSoft = soft.Audio.GetChip(0).CaptureState();
        Require(afterSoft.Poly17 == before.Poly17
                && afterSoft.ToneCount.AsSpan().SequenceEqual(before.ToneCount)
                && afterSoft.EnvelopeVolume == before.EnvelopeVolume
                && afterSoft.Registers[7] == 0xFF
                && afterSoft.Registers.Where((value, index) => index != 7).All(static value => value == 0),
            "A soft reset did not clear the PSG registers (R7 $FF) and leave the generators running.");
        soft.Reset(ZxNextResetKind.Hard);
        ZxNextPsgState afterHard = soft.Audio.GetChip(0).CaptureState();
        Require(afterHard.Poly17 == 0 && afterHard.ToneCount.All(static count => count == 0)
                && afterHard.EnvelopeVolume == 0,
            "A hard reset did not restart the PSG generators.");

        // A state from before the PSGX chunk restores the registers and restarts the generators.
        ZxNextMachine source = ZxNextMachineFactory.CreateFoundationMachine();
        WriteAyRegister(source, 0, 0x37);
        WriteAyRegister(source, 8, 0x0C);
        ReadNextAudio(source, 100);
        ZxNextAudioState captured = source.Audio.CaptureState();
        ZxNextMachine legacy = ZxNextMachineFactory.CreateFoundationMachine();
        legacy.Audio.RestoreState(captured with { Psgs = null });
        ZxNextPsgState restored = legacy.Audio.GetChip(0).CaptureState();
        Require(restored.Registers[0] == 0x37 && restored.Registers[8] == 0x0C
                && restored.ToneCount.All(static count => count == 0),
            "A state without PSG generators did not restore the registers alone.");
    }

    private static void WriteAyRegister(ZxNextMachine machine, byte register, byte value)
    {
        machine.PortBus.WriteUncontended(0xFFFD, register);
        machine.PortBus.WriteUncontended(0xBFFD, value);
    }

    /// <summary>
    /// soundrive.vhd's four DACs, zxnext.vhd's port decode and NextReg $84 enables, the $2C-$2E
    /// mirrors and audio_mixer.vhd's sum. Every expectation is worked out from the VHDL.
    /// </summary>
    private static void VerifyNextDacs()
    {
        VerifyNextDacHoldAndEnable();
        VerifyNextDacPortDecode();
        VerifyNextDacMirrors();
        VerifyNextDacMix();
        VerifyNextDacAveraging();
        VerifyNextDacWriters();
        VerifyNextDacCopperTiming(batched: false);
        VerifyNextDacCopperTiming(batched: true);
        VerifyNextDacResetsAndSnapshots();
    }

    private const int DacA = 0x01, DacB = 0x02, DacC = 0x04, DacD = 0x08;

    private static string DescribeDacs(ZxNextMachine machine) =>
        $"A=${machine.Audio.GetDacChannel(0):X2} B=${machine.Audio.GetDacChannel(1):X2} "
        + $"C=${machine.Audio.GetDacChannel(2):X2} D=${machine.Audio.GetDacChannel(3):X2}";

    private static bool DacsAre(ZxNextMachine machine, byte a, byte b, byte c, byte d) =>
        machine.Audio.GetDacChannel(0) == a && machine.Audio.GetDacChannel(1) == b
        && machine.Audio.GetDacChannel(2) == c && machine.Audio.GetDacChannel(3) == d;

    private static bool DacsAtRest(ZxNextMachine machine) => DacsAre(machine, 0x80, 0x80, 0x80, 0x80);

    /// <summary>NextReg $08 with bit 3 (the DACs) and bit 4 (the speaker, as after reset).</summary>
    private static void EnableDacs(ZxNextMachine machine) => machine.Registers.Write(0x08, 0x18);

    /// <summary>
    /// The host level for steady DACs and beeper: audio_mixer.vhd's sides, (L + R) x 2 at the
    /// HDMI level, less the DACs' resting level ($80 + $80 shifted left two, 1024 a side).
    /// </summary>
    private static int ExpectedDacHostLevel(int a, int b, int c, int d, int beeperPerSide = 0, int psgLeft = 0, int psgRight = 0)
    {
        int left = beeperPerSide + psgLeft + ((a + b) << 2);
        int right = beeperPerSide + psgRight + ((c + d) << 2);
        return ((left + right) * 2) - (2 * 1024 * 2);
    }

    private static void VerifyNextDacHoldAndEnable()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        Require(DacsAtRest(machine) && !machine.Audio.DacEnabled && (machine.Registers.Read(0x08) & 0x08) == 0,
            $"The DACs did not come up at $80 and disabled ($08 bit 3 clear): {DescribeDacs(machine)}.");

        // soundrive.vhd's reset is "reset or not nr_08_dac_en": no port or mirror moves it.
        machine.PortBus.WriteUncontended(0x00DF, 0x11);
        machine.PortBus.WriteUncontended(0x00B3, 0x22);
        machine.Registers.Write(0x2C, 0x33);
        machine.Registers.Write(0x2D, 0x44);
        machine.Registers.Write(0x2E, 0x55);
        Require(DacsAtRest(machine),
            $"A DAC write landed while $08 bit 3 held soundrive.vhd in reset: {DescribeDacs(machine)}.");
        Require(ReadNextAudio(machine, 64).All(static sample => sample == 0),
            "DACs held at $80 were heard: the resting level is not taken out of the host stream.");

        EnableDacs(machine);
        Require(machine.Audio.DacEnabled && (machine.Registers.Read(0x08) & 0x08) != 0 && DacsAtRest(machine),
            $"Setting $08 bit 3 did not enable the DACs at $80: {DescribeDacs(machine)}.");
        Require(ReadNextAudio(machine, 64).All(static sample => sample == 0),
            "Enabled DACs at rest were heard.");
        machine.PortBus.WriteUncontended(0x00DF, 0x11);
        Require(DacsAre(machine, 0x11, 0x80, 0x80, 0x11),
            $"With $08 bit 3 set, $DF did not write A and D: {DescribeDacs(machine)}.");

        machine.Registers.Write(0x08, 0x10);
        Require(DacsAtRest(machine) && ReadNextAudio(machine, 32).All(static sample => sample == 0),
            $"Clearing $08 bit 3 did not hold the DACs at $80 again: {DescribeDacs(machine)}.");
        EnableDacs(machine);
        Require(DacsAtRest(machine),
            $"The DACs came back with their old values when $08 bit 3 was set again: {DescribeDacs(machine)}.");
    }

    private static void VerifyNextDacPortDecode()
    {
        byte[] lows = [0x1F, 0xF1, 0x3F, 0x0F, 0xF3, 0x4F, 0xF9, 0x5F, 0xFB, 0xDF, 0xB3];
        const int a = DacA, b = DacB, c = DacC, d = DacD;

        // The zxnext.vhd decode, case by case: which channels each low byte writes with
        // NextReg $84 as given (bit 1 Soundrive 1, 2 Soundrive 2, 3 Profi Covox, 4 Covox,
        // 5 Pentagon $FB, 6 GS Covox $B3, 7 Specdrum $DF; bit 0, the AY, kept on).
        (byte Enables, string Name, int[] Expected)[] cases =
        [
            (0xFF, "every DAC port enabled", [a, a, a, b, b, c, c, d, d, a | d, b | c]),
            (0xFD, "Soundrive 1 off", [0, a, a, b, b, c, c, d, d, a | d, b | c]),
            (0xFB, "Soundrive 2 off", [a, 0, a, b, 0, c, 0, d, a | d, a | d, b | c]),
            (0xF7, "Profi Covox off", [a, a, 0, b, b, c, c, d, d, a | d, b | c]),
            (0xEF, "Covox off", [a, a, a, b, b, c, c, d, d, a | d, b | c]),
            (0xDF, "Pentagon $FB off", [a, a, a, b, b, c, c, d, d, a | d, b | c]),
            (0xBF, "GS Covox off", [a, a, a, b, b, c, c, d, d, a | d, 0]),
            (0x7F, "Specdrum off", [a, a, a, b, b, c, c, d, d, 0, b | c]),
            (0xED, "Soundrive 1 and Covox off", [0, a, a, 0, b, 0, c, d, d, a | d, b | c]),
            (0xF5, "Soundrive 1 and Profi Covox off", [0, a, 0, b, b, c, c, 0, d, a | d, b | c]),
            (0xDB, "Soundrive 2 and Pentagon off", [a, 0, a, b, 0, c, 0, d, 0, a | d, b | c]),
            (0x01, "every DAC port off", [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0])
        ];

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        EnableDacs(machine);
        int Probe(ushort port)
        {
            machine.Registers.Write(0x08, 0x10);
            EnableDacs(machine);
            machine.PortBus.WriteUncontended(port, 0x5A);
            int channels = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                byte value = machine.Audio.GetDacChannel(channel);
                Require(value is 0x5A or 0x80,
                    $"A write to ${port:X4} left DAC {(char)('A' + channel)} at ${value:X2}.");
                channels |= value == 0x5A ? 1 << channel : 0;
            }

            return channels;
        }

        foreach ((byte enables, string name, int[] expected) in cases)
        {
            machine.Registers.Write(0x84, enables);
            for (int index = 0; index < lows.Length; index++)
            {
                // The DAC ports decode A7-A0 only.
                foreach (byte high in new byte[] { 0x00, 0xA5 })
                {
                    ushort port = (ushort)((high << 8) | lows[index]);
                    int channels = Probe(port);
                    Require(channels == expected[index],
                        $"With {name} ($84=${enables:X2}), a write to ${port:X4} reached DAC mask {channels} instead of {expected[index]}.");
                }
            }
        }

        // No other low byte reaches a DAC, whatever the enables.
        machine.Registers.Write(0x84, 0xFF);
        for (int low = 0; low < 256; low++)
        {
            if (Array.IndexOf(lows, (byte)low) >= 0)
            {
                continue;
            }

            Require(Probe((ushort)(0x0000 | low)) == 0,
                $"A write to port ${low:X2} reached a DAC.");
        }

        // The Soundrive 2 conflict: $7FF1 is DAC A and not $7FFD while mode 2 holds $F1.
        ZxNextMachine paging = ZxNextMachineFactory.CreateFoundationMachine();
        EnableDacs(paging);
        byte before7Ffd = paging.Paging.Port7Ffd;
        paging.PortBus.WriteUncontended(0x7FF1, 0x07);
        Require(paging.Audio.GetDacChannel(0) == 0x07 && paging.Paging.Port7Ffd == before7Ffd,
            "With Soundrive mode 2 on, $7FF1 did not write DAC A alone (port_fd_conflict_wr).");

        // Reads: the DAC ports answer nothing, so a DAC port read is $FF.
        ZxNextMachine reads = ZxNextMachineFactory.CreateFoundationMachine();
        EnableDacs(reads);
        foreach (byte low in new byte[] { 0x0F, 0x4F, 0x5F, 0xF3, 0xF9, 0xFB, 0xB3 })
        {
            Require(reads.PortBus.ReadUncontended((ushort)(0x0100 | low)) == 0xFF,
                $"A read of DAC port ${low:X2} did not float to $FF: the DAC ports are write only.");
        }
    }

    private static void VerifyNextDacMirrors()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        EnableDacs(machine);
        machine.Registers.Write(0x2C, 0x12);
        Require(DacsAre(machine, 0x80, 0x12, 0x80, 0x80),
            $"NextReg $2C did not write DAC B alone: {DescribeDacs(machine)}.");
        machine.Registers.Write(0x2D, 0x34);
        Require(DacsAre(machine, 0x34, 0x12, 0x80, 0x34),
            $"NextReg $2D did not write DACs A and D: {DescribeDacs(machine)}.");
        machine.Registers.Write(0x2E, 0x56);
        Require(DacsAre(machine, 0x34, 0x12, 0x56, 0x34),
            $"NextReg $2E did not write DAC C alone: {DescribeDacs(machine)}.");

        // Their reads are the Pi's I2S input, idle at $200: $80, then the latched low bits, 0.
        Require(machine.Registers.Read(0x2C) == 0x80 && machine.Registers.Read(0x2D) == 0x00
                && machine.Registers.Read(0x2E) == 0x80,
            "Reading $2C-$2E did not give the idle I2S input ($80, $00, $80).");
    }

    private static void VerifyNextDacMix()
    {
        int Steady(Action<ZxNextMachine> setUp, int count = 32)
        {
            ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
            EnableDacs(machine);
            setUp(machine);
            short[] samples = ReadNextAudio(machine, count);
            Require(samples.All(sample => sample == samples[0]),
                "Steady DACs did not give a steady level.");
            return samples[0];
        }

        // Soundrive 1's ports write one channel each: $1F A, $0F B, $4F C, $5F D.
        void Write(ZxNextMachine machine, byte a, byte b, byte c, byte d)
        {
            machine.PortBus.WriteUncontended(0x001F, a);
            machine.PortBus.WriteUncontended(0x000F, b);
            machine.PortBus.WriteUncontended(0x004F, c);
            machine.PortBus.WriteUncontended(0x005F, d);
        }

        Require(Steady(m => Write(m, 0xFF, 0xFF, 0x80, 0x80)) == ExpectedDacHostLevel(0xFF, 0xFF, 0x80, 0x80)
                && ExpectedDacHostLevel(0xFF, 0xFF, 0x80, 0x80) == 2032,
            "A and B at $FF were not (A + B) x 4 on the left alone (2032).");
        Require(Steady(m => Write(m, 0x80, 0x80, 0xFF, 0xFF)) == 2032,
            "C and D at $FF were not (C + D) x 4 on the right alone (2032).");
        Require(Steady(m => Write(m, 0x00, 0x00, 0x00, 0x00)) == -4096,
            "All four DACs at 0 were not 1024 below rest on each side (-4096).");
        Require(Steady(m => Write(m, 0x12, 0x34, 0x56, 0x78)) == ExpectedDacHostLevel(0x12, 0x34, 0x56, 0x78)
                && ExpectedDacHostLevel(0x12, 0x34, 0x56, 0x78) == -1888,
            "DACs $12/$34/$56/$78 were not audio_mixer.vhd's sums (-1888).");

        // The DACs add to the beeper and the PSGs, side by side.
        Require(Steady(m =>
                {
                    Write(m, 0xFF, 0xFF, 0xFF, 0xFF);
                    m.PortBus.WriteUncontended(0x00FE, 0x18);
                }) == ExpectedDacHostLevel(0xFF, 0xFF, 0xFF, 0xFF, beeperPerSide: 640),
            "The DACs did not add to EAR and MIC.");
        Require(Steady(m =>
                {
                    Write(m, 0x00, 0x80, 0x80, 0x80);
                    WriteAyRegister(m, 7, 0x3F);
                    WriteAyRegister(m, 8, 0x0F);
                }) == ExpectedDacHostLevel(0x00, 0x80, 0x80, 0x80, psgLeft: 255),
            "The DACs did not add to a PSG channel on its side.");

        // Everything at full: three chips (1530 a side), EAR and MIC, the DACs: no clipping.
        int full = Steady(m =>
        {
            m.Registers.Write(0x08, 0x1A);
            foreach (byte chipSelect in new byte[] { 0xFF, 0xFE, 0xFD })
            {
                m.PortBus.WriteUncontended(0xFFFD, chipSelect);
                WriteAyRegister(m, 7, 0x3F);
                WriteAyRegister(m, 8, 0x0F);
                WriteAyRegister(m, 9, 0x0F);
                WriteAyRegister(m, 10, 0x0F);
            }

            Write(m, 0xFF, 0xFF, 0xFF, 0xFF);
            m.PortBus.WriteUncontended(0x00FE, 0x18);
        });
        Require(full == ExpectedDacHostLevel(0xFF, 0xFF, 0xFF, 0xFF, 640, 1530, 1530) && full == 12744,
            $"The full mix was {full}, not audio_mixer.vhd's sum (4210 a side, 12744).");
    }

    /// <summary>
    /// A DAC level moves only on a write, so each sample is the tick-weighted mean of the
    /// levels during it, as the beeper's is.
    /// </summary>
    private static void VerifyNextDacAveraging()
    {
        // 1000 master ticks a sample.
        var audio = new ZxNextAudioDevice(ZxNextReferenceBaseline.MasterClockHz / 1000);
        audio.Configure(0xA0, 0x18, 0x00);
        audio.BeginCapture();
        audio.AdvanceMasterTicks(1000);
        audio.AdvanceMasterTicks(250);
        audio.WriteDacPort(DacA, 0xFF);   // left (255 - 128) x 4 = 508 for 750 ticks
        audio.AdvanceMasterTicks(750);
        audio.AdvanceMasterTicks(400);
        audio.WriteDacPort(DacA | DacB | DacC | DacD, 0x00);   // -1024 a side for 600 ticks
        audio.AdvanceMasterTicks(600);
        audio.AdvanceMasterTicks(1000);
        short[] samples = new short[8];
        int count = audio.DrainSamples(samples, 0, samples.Length);

        // Sample 1: left 508 x 750 / 1000 = 381, right 0. Sample 2: left (508 x 400 - 1024 x
        // 600) / 1000 = -411 and right -1024 x 600 / 1000 = -614, each truncated to zero.
        short[] expected = [0, 762, -2050, -4096];
        Require(count == 4 && samples.AsSpan(0, 4).SequenceEqual(expected),
            $"Mid-sample DAC writes gave {string.Join(", ", samples.Take(count))}, not the tick-weighted means {string.Join(", ", expected)}.");
    }

    /// <summary>The CPU's OUT, the DMA and the Copper all write the DACs.</summary>
    private static void VerifyNextDacWriters()
    {
        // LD A,$C3; OUT ($DF),A; LD A,$3C; LD BC,$12B3; OUT (C),A; JR $
        ZxNextMachine cpu = CreateProgramAt(0x8000, 0x3E, 0xC3, 0xD3, 0xDF, 0x3E, 0x3C, 0x01, 0xB3, 0x12, 0xED, 0x79, 0x18, 0xFE);
        EnableDacs(cpu);
        for (int step = 0; step < 5; step++)
        {
            cpu.StepInstruction();
        }

        Require(DacsAre(cpu, 0xC3, 0x3C, 0x3C, 0xC3),
            $"The CPU's OUT ($DF),A and OUT ($12B3),A did not reach the DACs: {DescribeDacs(cpu)}.");

        // A DMA memory-to-I/O transfer to $DF, the usual way to play a sample.
        ZxNextMachine dma = ZxNextMachineFactory.CreateFoundationMachine();
        EnableDacs(dma);
        dma.MemoryBus.WritePhysicalRam(0, 0x0200, 0x10);
        dma.MemoryBus.WritePhysicalRam(0, 0x0201, 0x20);
        dma.MemoryBus.WritePhysicalRam(0, 0x0202, 0x30);
        dma.MemoryBus.WritePhysicalRam(0, 0x0203, 0x40);
        byte[] memoryToDac =
        [
            0xC3,
            0x7D, 0x00, 0xC2, 0x04, 0x00,
            0x14,       // WR1: memory A, increment
            0x28,       // WR2: I/O B, fixed
            0xAD, 0xDF, 0x00,
            0x82, 0xCF, 0xB3, 0x87
        ];
        WriteDmaCommand(dma, memoryToDac);
        RunDmaToCompletion(dma);
        Require(DacsAre(dma, 0x40, 0x80, 0x80, 0x40),
            $"A zxnDMA transfer to $DF did not leave its last byte in DACs A and D: {DescribeDacs(dma)}.");
    }

    /// <summary>
    /// A Copper MOVE to $2D is heard from the tick it lands: the area under the host samples is
    /// the level times the time between the two writes, to within the tick a boundary is
    /// rounded to and the truncation of the two samples the edges fall in.
    /// </summary>
    private static void VerifyNextDacCopperTiming(bool batched)
    {
        var log = new List<(ulong Tick, byte Register, byte Value)>();
        ZxNextMachine machine = CreateCopperMachine(log, batched);
        EnableDacs(machine);
        UploadCopper(machine, CopperWait(60), CopperMove(0x2D, 0xFF), CopperWait(160, 20), CopperMove(0x2D, 0x80), CopperHalt);
        short[] warm = new short[1];
        machine.ReadSamples(warm, 0, 1);
        StartCopperAtFrame(machine, 0x40);
        int perFrame = machine.SampleRate / 50 + 1;
        short[] samples = ReadNextAudio(machine, perFrame * 3);
        List<(ulong Tick, byte Register, byte Value)> writes = log.Where(entry => entry.Register == 0x2D).ToList();
        Require(writes.Count == 2 && writes[0].Value == 0xFF && writes[1].Value == 0x80,
            $"The Copper's two $2D writes were not logged ({(batched ? "batched" : "unbatched")}): {DescribeCopperLog(log, 0)}.");

        // $2D at $FF puts A and D at $FF: (255 + 128 - 256) x 4 = 508 a side, 2032 mono.
        const int level = 2032;
        Require(samples.All(static sample => sample is >= 0 and <= level),
            "A sample fell outside rest and the written level while the Copper drove $2D.");
        double samplePeriod = ZxNextReferenceBaseline.MasterClockHz / (double)machine.SampleRate;
        double heard = samples.Sum(static sample => (double)sample) * samplePeriod;
        double expected = level * (double)(writes[1].Tick - writes[0].Tick);
        double tolerance = 2.0 * level * 8;
        Require(Math.Abs(heard - expected) <= tolerance,
            $"The DAC level was heard for {heard / level:0} ticks, not the {expected / level:0} between the Copper's writes ({(batched ? "batched" : "unbatched")}).");
        int full = samples.Count(static sample => sample == level);
        Require(full >= (int)((writes[1].Tick - writes[0].Tick) / samplePeriod) - 2,
            "The DAC level was not steady between the Copper's writes.");
    }

    /// <summary>
    /// The stereo host stream: each side at the HDMI level (shifted left two), left then right,
    /// less the DACs' resting level; the mono stream from the same machine is their mean.
    /// </summary>
    private static void VerifyNextStereoOutput()
    {
        (int Left, int Right) Frame(Action<ZxNextMachine> setUp)
        {
            ZxNextMachine stereo = ZxNextMachineFactory.CreateFoundationMachine();
            stereo.StereoAudio = true;
            Require(stereo.Channels == 2 && ((IAudioSource)stereo).Channels == 2 && stereo.Audio.OutputChannels == 2,
                "Asking for stereo did not make the Next a two-channel source.");
            EnableDacs(stereo);
            setUp(stereo);
            short[] samples = new short[64];
            Require(stereo.ReadSamples(samples, 0, samples.Length) == samples.Length,
                "The Next did not produce the stereo samples asked for.");
            for (int index = 2; index < samples.Length; index += 2)
            {
                Require(samples[index] == samples[0] && samples[index + 1] == samples[1],
                    "Steady levels did not give steady stereo frames.");
            }

            ZxNextMachine mono = ZxNextMachineFactory.CreateFoundationMachine();
            EnableDacs(mono);
            setUp(mono);
            int mean = (samples[0] + samples[1]) / 2;
            Require(ReadNextAudio(mono, 16).All(sample => sample == mean),
                $"The mono stream was not the mean of the stereo sides ({samples[0]}, {samples[1]}).");
            return (samples[0], samples[1]);
        }

        Require(Frame(m => m.PortBus.WriteUncontended(0x00FE, 0x10)) == (2048, 2048),
            "EAR was not 512 x 4 on both sides.");
        Require(Frame(m => { m.PortBus.WriteUncontended(0x001F, 0xFF); m.PortBus.WriteUncontended(0x000F, 0xFF); }) == (4064, 0),
            "DACs A and B at $FF were not (A + B - 256) x 4 x 4 on the left alone.");
        Require(Frame(m => { m.PortBus.WriteUncontended(0x004F, 0x00); m.PortBus.WriteUncontended(0x005F, 0x00); }) == (0, -4096),
            "DACs C and D at 0 were not on the right alone.");

        // Atic Atac Next's music: $0F (B) left and $4F (C) right.
        Require(Frame(m => { m.PortBus.WriteUncontended(0x000F, 0xC0); m.PortBus.WriteUncontended(0x004F, 0x40); }) == (1024, -1024),
            "$0F and $4F did not reach the left and right sides apart.");

        // turbosound.vhd's ABC: A left, C right, B on both; the pan bits per side.
        Require(Frame(m => { WriteAyRegister(m, 7, 0x3F); WriteAyRegister(m, 8, 0x0F); }) == (1020, 0),
            "PSG channel A was not on the left alone.");
        Require(Frame(m => { WriteAyRegister(m, 7, 0x3F); WriteAyRegister(m, 10, 0x0F); }) == (0, 1020),
            "PSG channel C was not on the right alone.");
        Require(Frame(m => { WriteAyRegister(m, 7, 0x3F); WriteAyRegister(m, 9, 0x0F); }) == (1020, 1020),
            "PSG channel B was not on both sides.");
        Require(Frame(m =>
                {
                    m.Registers.Write(0x08, 0x1A);
                    m.PortBus.WriteUncontended(0xFFFD, 0xDF);
                    WriteAyRegister(m, 7, 0x3F);
                    WriteAyRegister(m, 9, 0x0F);
                }) == (1020, 0),
            "A chip panned left (bit 6 alone) was heard on the right.");

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.StereoAudio = true;
        bool rejected = false;
        try
        {
            machine.ReadSamples(new short[3], 0, 3);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Require(rejected, "A stereo read of part of a frame was accepted.");
        short[] frames = new short[4];
        machine.ReadSamples(frames, 0, frames.Length);
        machine.RunFrame();
        Require(machine.Audio.PendingSampleCount > 0 && machine.Audio.PendingSampleCount % 2 == 0,
            "The stereo queue did not hold whole frames.");
        machine.StereoAudio = false;
        Require(machine.Audio.PendingSampleCount == 0 && machine.Channels == 1,
            "Going back to mono did not drop the samples queued in stereo.");
        rejected = false;
        try
        {
            machine.Audio.SetOutputChannels(3);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }

        Require(rejected, "Three host channels were accepted.");
    }

    private static void VerifyNextDacResetsAndSnapshots()
    {
        // A reset puts soundrive.vhd back to $80; $08 bit 3 survives a soft reset (only bit 6
        // is on the reset) and the FPGA loading clears it.
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        EnableDacs(machine);
        machine.Registers.Write(0x2D, 0x01);
        machine.Registers.Write(0x2C, 0x02);
        machine.Registers.Write(0x2E, 0x03);
        machine.Reset(ZxNextResetKind.Soft);
        Require(DacsAtRest(machine) && machine.Audio.DacEnabled,
            $"A soft reset did not return the DACs to $80 with $08 bit 3 kept: {DescribeDacs(machine)}.");
        machine.Registers.Write(0x2D, 0x01);
        machine.Reset(ZxNextResetKind.Hard);
        Require(DacsAtRest(machine) && !machine.Audio.DacEnabled,
            $"A hard reset did not return the DACs to $80, disabled: {DescribeDacs(machine)}.");

        // A capture mid-sample, through a file, carries on exactly as the original.
        ZxNextMachine original = CreateProgramAt(0x8000, 0x18, 0xFE);
        EnableDacs(original);
        original.PortBus.WriteUncontended(0x00B3, 0x9C);
        original.PortBus.WriteUncontended(0x00DF, 0x21);
        ReadNextAudio(original, 50);
        for (int step = 0; step < 7; step++)
        {
            original.StepInstruction();
        }

        original.PortBus.WriteUncontended(0x00DF, 0xE7);
        for (int step = 0; step < 5; step++)
        {
            original.StepInstruction();
        }

        short[] pending = new short[original.Audio.PendingSampleCount];
        original.Audio.DrainSamples(pending, 0, pending.Length);
        ZxNextMachineSnapshot captured = ZxNextMachineSnapshotService.Capture(original);
        Require(captured.Audio.Dac is { } dacState
                && dacState.Channels.AsSpan().SequenceEqual(new byte[] { 0xE7, 0x9C, 0x9C, 0xE7 })
                && dacState.LeftLevelSum != 0,
            "The capture did not hold the DACs and a part-accumulated sample.");
        ZxNextMachine restored = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextMachineSnapshotService.Restore(restored, RoundTripThroughFile(captured));
        Require(DacsAre(restored, 0xE7, 0x9C, 0x9C, 0xE7) && restored.Audio.DacEnabled,
            $"The DACs did not come back from the file: {DescribeDacs(restored)}.");
        short[] originalNext = ReadNextAudio(original, 40);
        short[] restoredNext = ReadNextAudio(restored, 40);
        Require(originalNext.AsSpan().SequenceEqual(restoredNext),
            "After the restore the DACs' samples differed from the original's.");

        // A state from before the DACS chunk restores them at $80.
        ZxNextMachine legacy = ZxNextMachineFactory.CreateFoundationMachine();
        legacy.Audio.RestoreState(captured.Audio with { Dac = null });
        Require(DacsAtRest(legacy),
            $"A state without DACs did not restore them at $80: {DescribeDacs(legacy)}.");
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

    /// <summary>
    /// The guarded ULA-with-sprites and tilemap-with-sprites compositors must be
    /// pixel-identical to the general mixer in every priority order, through clipping and
    /// transparency, and must see palette writes on the next frame.
    /// </summary>
    /// <remarks>
    /// Each scene is rendered functionally first (the general mixer, which is the
    /// reference) and then by the live raster through the guard. A solid sprite sits half
    /// over ink and half over a transparent ULA paper colour, so every branch of the
    /// composition is on screen at once; for the tilemap scene the sprite sits over a
    /// mixture of opaque and transparent tiles. The orders that put sprites first and
    /// those that put the ULA first are both covered, as is a clip window that disables
    /// part of the ULA.
    /// </remarks>
    private static void VerifyTwoLayerCompositors()
    {
        foreach (byte priority in new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 })
        {
            VerifyUlaSpriteScene(priority, clipped: false);
            VerifyTilemapSpriteScene(priority);
        }

        VerifyUlaSpriteScene(0, clipped: true);
        VerifyUlaSpriteScene(3, clipped: true);

        static void VerifyUlaSpriteScene(byte priority, bool clipped)
        {
            ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
            // Ink 7 on paper 3 for the left half of the screen, and paper equal to the
            // global transparency colour on the right, so the sprite is seen over an
            // opaque ULA, over a transparent ULA, and over the border.
            for (int row = 0; row < 24; row++)
            {
                for (int column = 0; column < 32; column++)
                {
                    machine.MemoryBus.WritePhysicalRam(10, 0x1800 + (row * 32) + column,
                        column < 16 ? (byte)0x1F : (byte)0x38);
                }
            }

            for (int offset = 0; offset < 0x1800; offset++)
            {
                machine.MemoryBus.WritePhysicalRam(10, offset, (byte)((offset * 7) & 0xAA));
            }

            // Paper 7 of the right half maps to the transparency colour, and the fallback
            // colour is moved away from it, so a transparent ULA pixel is visibly the
            // fallback (or the sprite behind it) and not the transparency colour itself.
            machine.Registers.Write(0x43, 0x00);
            machine.Registers.Write(0x40, 0x17); // paper entries sit at $10 | paper
            machine.Registers.Write(0x41, 0xE3);
            machine.Registers.Write(0x4A, 0x1C);
            WriteSolidSpritePattern8(machine, 0, 0x02);
            WriteSpriteAttributes(machine, 0, 120, 80, 0, 0x80, 0);
            // The second sprite sits wholly under the transparent paper, so it shows
            // through even when the ULA has priority.
            WriteSpriteAttributes(machine, 1, 200, 100, 0, 0x80, 0);
            machine.Registers.Write(0x15, (byte)(0x03 | (priority << 2)));
            if (clipped)
            {
                machine.Registers.Write(0x1C, 0x04);
                machine.Registers.Write(0x1A, 0x20);
                machine.Registers.Write(0x1A, 0x9F);
                machine.Registers.Write(0x1A, 0x10);
                machine.Registers.Write(0x1A, 0x8F);
            }

            CompareLiveWithFunctional(machine, $"ULA with sprites, priority {priority}{(clipped ? ", clipped" : "")}");

            WriteSpritePalette(machine, 0x02, 0x1C0);
            machine.Video.AdvanceMasterTicks(machine.UlaTiming.MasterTicksPerFrame);
            int redPixels = machine.Video.FrameBuffer.AsSpan().Count(unchecked((int)0xFFFF0000));
            Require(priority >= 3 || redPixels >= 2 * 16 * 16 * 2,
                $"The ULA-with-sprites compositor did not observe a sprite palette write (priority {priority}): {redPixels} red output pixels.");
        }

        static void VerifyTilemapSpriteScene(byte priority)
        {
            ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
            machine.Registers.Write(0x6E, 0x20);
            machine.Registers.Write(0x6F, 0x00);
            machine.Registers.Write(0x6C, 0x2E);
            for (int address = 0; address < 0x2800; address++)
            {
                int page = 10 + (address / ZxNextPhysicalMemory.PageSize);
                // Every fourth tile is the transparent index throughout.
                byte value = (address & 3) == 3 ? (byte)0xFF : (byte)((address * 29 + (address >> 3) + 1) & 0xFF);
                machine.MemoryBus.WritePhysicalRam(page, address & (ZxNextPhysicalMemory.PageSize - 1), value);
            }

            machine.Registers.Write(0x6B, 0x80);
            machine.Registers.Write(0x68, 0x80);
            WriteSolidSpritePattern8(machine, 0, 0x02);
            WriteSpriteAttributes(machine, 0, 100, 60, 0, 0x80, 0);
            WriteSpriteAttributes(machine, 1, 200, 150, 0, 0x80, 0);
            machine.Registers.Write(0x15, (byte)(0x03 | (priority << 2)));
            CompareLiveWithFunctional(machine, $"tilemap with sprites, priority {priority}");
        }

        static void CompareLiveWithFunctional(ZxNextMachine machine, string scene)
        {
            machine.Video.RenderFrame(0);
            int[] reference = (int[])machine.Video.FrameBuffer.Clone();
            // The scene has to contain what the guard is accused of mishandling: a
            // transparent region showing the fallback colour, and sprite pixels.
            int fallbackArgb = machine.Registers.Palettes.GetFallbackArgb();
            if (reference.AsSpan().Count(fallbackArgb) < 1000)
            {
                string histogram = string.Join(", ", reference
                    .GroupBy(pixel => pixel)
                    .OrderByDescending(group => group.Count())
                    .Take(6)
                    .Select(group => $"{group.Key:X8}x{group.Count()}"));
                throw new InvalidOperationException(
                    $"The {scene} scene shows no transparent region (fallback {fallbackArgb:X8}), so transparency is not being tested; colours: {histogram}.");
            }

            Require(reference.AsSpan().Count(machine.Registers.Palettes.GetSpriteArgb(0x02)) >= 100,
                $"The {scene} scene shows no sprite pixels, so sprites are not being tested.");
            machine.Video.Reset();
            machine.Video.AdvanceMasterTicks(machine.UlaTiming.MasterTicksPerFrame * 2);
            int[] live = machine.Video.FrameBuffer;
            int first = live.AsSpan().CommonPrefixLength(reference);
            Require(first == live.Length,
                $"The live {scene} compositor diverged from the general mixer at output ({first % ZxNextVideoRenderer.FrameWidth}, {first / ZxNextVideoRenderer.FrameWidth}): "
                + $"live {live[Math.Min(first, live.Length - 1)]:X8}, reference {reference[Math.Min(first, reference.Length - 1)]:X8}.");
        }
    }

    /// <summary>
    /// The live raster's general mixer must match the reference mixer pixel for pixel in
    /// every priority order, with every layer on, with and without a promoted Layer 2
    /// colour, and with each layer in turn switched off.
    /// </summary>
    /// <remarks>
    /// The snapshot renderer still mixes through <c>ComposePixelCore</c>, which is the
    /// reference; the live path resolves lazily and through the ARGB caches. Each scene
    /// is built so every layer has opaque and transparent pixels under the sprite, which
    /// is where a lazy resolution that stops too early or too late would show.
    /// </remarks>
    private static void VerifyGeneralMixerEquivalence()
    {
        foreach (byte priority in new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 })
        {
            foreach (bool promoted in new[] { false, true })
            {
                VerifyScene(priority, promoted, ula: true, layer2: true, tilemap: true);
            }

            VerifyScene(priority, promoted: false, ula: false, layer2: true, tilemap: true);
            VerifyScene(priority, promoted: true, ula: true, layer2: true, tilemap: false);
            VerifyScene(priority, promoted: false, ula: true, layer2: false, tilemap: true);
        }

        foreach (byte priority in new byte[] { 3, 4, 5, 6, 7 })
        {
            foreach (byte ulaControl in new byte[] { 0x20, 0x40, 0x60, 0x01, 0x80, 0xE1 })
            {
                VerifyScene(priority, promoted: true, ula: true, layer2: true, tilemap: true, ulaControl);
            }
        }

        VerifyScene(0, promoted: false, ula: true, layer2: true, tilemap: true, textMode: true);
        VerifyScene(4, promoted: false, ula: true, layer2: true, tilemap: true, textMode: true);

        static void VerifyScene(
            byte priority,
            bool promoted,
            bool ula,
            bool layer2,
            bool tilemap,
            byte ulaControl = 0,
            bool textMode = false)
        {
            ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
            // ULA: ink 7 on paper 3 left, ink 0 on transparent paper right.
            for (int row = 0; row < 24; row++)
            {
                for (int column = 0; column < 32; column++)
                {
                    machine.MemoryBus.WritePhysicalRam(10, 0x1800 + (row * 32) + column,
                        column < 16 ? (byte)0x1F : (byte)0x38);
                }
            }

            for (int offset = 0; offset < 0x1800; offset++)
            {
                machine.MemoryBus.WritePhysicalRam(10, offset, (byte)((offset * 7) & 0xAA));
            }

            machine.Registers.Write(0x43, 0x00);
            machine.Registers.Write(0x40, 0x17);
            machine.Registers.Write(0x41, 0xE3);
            machine.Registers.Write(0x4A, 0x1C);
            if (!ula || ulaControl != 0)
            {
                machine.Registers.Write(0x68, (byte)(ulaControl | (ula ? 0 : 0x80)));
            }

            // Layer 2, 256x192 in bank 8: a diagonal of transparent pixels through index 1,
            // which is the promoted colour when the scene asks for one.
            if (layer2)
            {
                machine.Registers.Write(0x12, 0x08);
                machine.Registers.Write(0x70, 0x00);
                for (int y = 0; y < 192; y++)
                {
                    for (int x = 0; x < 256; x++)
                    {
                        int address = (y << 8) | x;
                        byte value = ((x + y) & 7) == 0 ? (byte)0xE3 : ((x >> 4) & 1) == 0 ? (byte)0x01 : (byte)0x05;
                        machine.MemoryBus.WritePhysicalRam(16 + (address >> 13), address & 0x1FFF, value);
                    }
                }

                WriteLayer2Palette(machine, 0x01, 0x1C0, priority: promoted);
                WriteLayer2Palette(machine, 0x05, 0x01C, priority: false);
                machine.PortBus.WriteUncontended(0x123B, 0x02);
            }

            if (tilemap)
            {
                machine.Registers.Write(0x6E, 0x20);
                machine.Registers.Write(0x6F, 0x00);
                machine.Registers.Write(0x6C, 0x2E);
                for (int address = 0; address < 0x2800; address++)
                {
                    int page = 10 + (address / ZxNextPhysicalMemory.PageSize);
                    byte value = (address & 3) == 3 ? (byte)0xFF : (byte)((address * 29 + (address >> 3) + 1) & 0xFF);
                    if (address < 0x1B00)
                    {
                        continue; // keep the ULA screen intact; the map lives above it
                    }

                    machine.MemoryBus.WritePhysicalRam(page, address & (ZxNextPhysicalMemory.PageSize - 1), value);
                }

                machine.Registers.Write(0x6E, 0x1B); // map at $5B00: ($5B00 - $4000) / 256
                machine.Registers.Write(0x6B, textMode ? (byte)0x88 : (byte)0x80);
            }

            WriteSolidSpritePattern8(machine, 0, 0x02);
            WriteSpriteAttributes(machine, 0, 100, 60, 0, 0x80, 0);
            WriteSpriteAttributes(machine, 1, 200, 100, 0, 0x80, 0);
            WriteSpriteAttributes(machine, 2, 4, 4, 0, 0x80, 0); // over the border
            machine.Registers.Write(0x15, (byte)(0x03 | (priority << 2)));

            machine.Video.RenderFrame(0);
            int[] reference = (int[])machine.Video.FrameBuffer.Clone();
            machine.Video.Reset();
            machine.Video.AdvanceMasterTicks(machine.UlaTiming.MasterTicksPerFrame * 2);
            int[] live = machine.Video.FrameBuffer;
            int first = live.AsSpan().CommonPrefixLength(reference);
            string scene = $"priority {priority}{(promoted ? ", promoted" : "")}, ULA {(ula ? "on" : "off")}, Layer 2 {(layer2 ? "on" : "off")}, tilemap {(tilemap ? (textMode ? "text" : "on") : "off")}, $68=${ulaControl:X2}";
            Require(first == live.Length,
                $"The live general mixer diverged from the reference ({scene}) at output ({first % ZxNextVideoRenderer.FrameWidth}, {first / ZxNextVideoRenderer.FrameWidth}): "
                + $"live {live[Math.Min(first, live.Length - 1)]:X8}, reference {reference[Math.Min(first, reference.Length - 1)]:X8}.");
        }
    }

    private static void VerifyNextRegResetRequests()
    {
        ZxNextMachine peripheral = CreateProgram(0xED, 0x91, 0x02, 0x80);
        peripheral.StepInstruction();
        Require(peripheral.ResetSequence == 0
                && peripheral.Registers.Read(0x02) == 0x80
                && peripheral.Registers.PeripheralResetAsserted,
            "NextReg $02 bit 7 did not latch peripheral reset independently of CPU reset.");

        ZxNextMachine soft = CreateProgram(0xED, 0x91, 0x02, 0x01);
        soft.MemoryBus.WritePhysicalRam(42, 7, 0xA5);
        soft.StepInstruction();
        Require(soft.ResetSequence == 1 && soft.LastResetKind == ZxNextResetKind.Soft
                && soft.Registers.LastResetKind == ZxNextResetKind.Soft
                && soft.Registers.Read(0x02) == 0x02
                && soft.Cpu.PC == 0 && soft.Cpu.Cyc == 0
                && !soft.MemoryBus.ConfigurationMode && !soft.MemoryBus.BootRomEnabled
                && soft.MemoryBus.ReadPhysicalRam(42, 7) == 0xA5,
            "A guest soft-reset request did not apply at the completed instruction boundary.");

        ZxNextMachine hard = CreateProgram(0xED, 0x91, 0x02, 0x03);
        hard.Registers.Write(0x02, 0x80);
        hard.StepInstruction();
        Require(hard.ResetSequence == 1 && hard.LastResetKind == ZxNextResetKind.Hard
                && hard.Registers.Read(0x02) == 0x00
                && hard.Cpu.PC == 0 && hard.Cpu.Cyc == 0
                && hard.MemoryBus.ConfigurationMode && hard.MemoryBus.BootRomEnabled
                && !hard.Registers.PeripheralResetAsserted,
            "Hard reset did not take precedence or restore hard-reset-only state.");
    }

    /// <summary>
    /// State files saved while the Next was a 1 MiB machine have to keep loading.
    /// </summary>
    /// <remarks>
    /// The snapshot is cut down to the 96 pages a 1 MiB machine has, restored into a 2 MiB
    /// machine whose upper pages hold junk, and then has to agree with an untouched
    /// continuation of the original - which only holds if the low pages came back and the
    /// upper ones were cleared rather than left as they were.
    /// </remarks>
    private static void VerifyOneMiBSnapshotRestores()
    {
        ZxNextMachine original = ZxNextMachineFactory.CreateFoundationMachine();
        for (int page = 0; page < 96; page++)
        {
            original.MemoryBus.WritePhysicalRam(page, page * 7 % ZxNextPhysicalMemory.PageSize, (byte)(page ^ 0x5A));
        }

        for (int frame = 0; frame < 3; frame++)
        {
            original.RunFrame(presentFrame: false);
        }

        ZxNextMachineSnapshot captured = ZxNextMachineSnapshotService.Capture(original);
        Require(captured.PhysicalRam.Length == 1792 * 1024,
            $"A capture carried {captured.PhysicalRam.Length} bytes of RAM, not the 2 MiB machine's 1792 KiB.");
        ZxNextMachineSnapshot fromOneMiB = captured with { PhysicalRam = captured.PhysicalRam[..(768 * 1024)] };

        ZxNextMachine restored = ZxNextMachineFactory.CreateFoundationMachine();
        for (int page = 96; page < 224; page++)
        {
            restored.MemoryBus.WritePhysicalRam(page, 0, 0xEE);
        }

        ZxNextMachineSnapshotService.Restore(restored, fromOneMiB);
        Require(restored.MemoryBus.ReadPhysicalRam(40, 40 * 7 % ZxNextPhysicalMemory.PageSize) == (40 ^ 0x5A),
            "The low pages of a 1 MiB snapshot did not come back.");
        Require(Enumerable.Range(96, 128).All(page => restored.MemoryBus.ReadPhysicalRam(page, 0) == 0),
            "The pages a 1 MiB machine lacks kept the destination's junk instead of being cleared.");

        for (int frame = 0; frame < 3; frame++)
        {
            original.RunFrame(presentFrame: false);
            restored.RunFrame(presentFrame: false);
        }

        Require(original.Cpu.PC == restored.Cpu.PC
                && original.MemoryBus.PhysicalMemory.CopyAll().AsSpan(0, 768 * 1024)
                    .SequenceEqual(restored.MemoryBus.PhysicalMemory.CopyAll().AsSpan(0, 768 * 1024))
                && original.Video.FrameBuffer.AsSpan().SequenceEqual(restored.Video.FrameBuffer),
            "A machine restored from a 1 MiB snapshot did not carry on like the original.");
    }

    private static void VerifyPhysicalRam()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextPhysicalMemory physical = machine.MemoryBus.PhysicalMemory;
        Require(physical.ConfiguredMemoryBytes == 2 * 1024 * 1024,
            "The Next is not the fully expanded 2 MiB machine.");
        Require(physical.InstalledRamBytes == 1792 * 1024 && physical.InstalledRamPageCount == 224,
            "The 256 KiB system reservation was not excluded from guest RAM.");

        Require(machine.MemoryBus.WritePhysicalRam(0, 0, 0x12)
                && machine.MemoryBus.WritePhysicalRam(95, ZxNextPhysicalMemory.PageSize - 1, 0x78)
                && machine.MemoryBus.WritePhysicalRam(96, 0, 0x9A)
                && machine.MemoryBus.WritePhysicalRam(223, ZxNextPhysicalMemory.PageSize - 1, 0x34),
            "A physical page of the 2 MiB machine rejected a write.");
        Require(machine.MemoryBus.ReadPhysicalRam(0, 0) == 0x12
                && machine.MemoryBus.ReadPhysicalRam(95, ZxNextPhysicalMemory.PageSize - 1) == 0x78
                && machine.MemoryBus.ReadPhysicalRam(96, 0) == 0x9A
                && machine.MemoryBus.ReadPhysicalRam(223, ZxNextPhysicalMemory.PageSize - 1) == 0x34,
            "Direct physical RAM access did not preserve boundary writes, including across the old 1 MiB edge.");
        Require(!machine.MemoryBus.WritePhysicalRam(224, 0, 0x56)
                && machine.MemoryBus.ReadPhysicalRam(224, 0) == 0xFF,
            "The first uninstalled physical page did not behave as an open bus.");

        byte[] copy = machine.MemoryBus.CopyPhysicalRamPage(223);
        copy[^1] = 0;
        Require(machine.MemoryBus.ReadPhysicalRam(223, ZxNextPhysicalMemory.PageSize - 1) == 0x34,
            "A physical-page inspection leaked the mutable backing array.");

        // The 1 MiB board is still a valid configuration of the type.
        var oneMiB = new ZxNextPhysicalMemory(1024 * 1024);
        Require(oneMiB.InstalledRamBytes == 768 * 1024 && oneMiB.InstalledRamPageCount == 96
                && !oneMiB.IsPageInstalled(96),
            "The 1 MiB configuration no longer has 96 pages.");

        // A RAM image from a 1 MiB machine loads into the 2 MiB one and clears what it lacks;
        // a larger one is refused without touching anything.
        byte[] smallImage = new byte[768 * 1024];
        smallImage[0] = 0x5A;
        smallImage[^1] = 0xA5;
        physical.LoadAll(smallImage);
        Require(physical.Read(0, 0) == 0x5A
                && physical.Read(95, ZxNextPhysicalMemory.PageSize - 1) == 0xA5
                && physical.Read(96, 0) == 0x00
                && physical.Read(223, ZxNextPhysicalMemory.PageSize - 1) == 0x00,
            "A 1 MiB RAM image did not load into the low pages and clear the pages a 1 MiB machine lacks.");
        physical.Write(150, 0, 0x77);
        bool refused = false;
        try
        {
            physical.LoadAll(new byte[(1792 * 1024) + ZxNextPhysicalMemory.PageSize]);
        }
        catch (ArgumentException)
        {
            refused = true;
        }

        Require(refused && physical.Read(150, 0) == 0x77 && physical.Read(0, 0) == 0x5A,
            "A RAM image larger than the machine was not refused cleanly.");
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
            && !(ZxNextRomLandmarks.IsAtKeyWait(machine)))
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
            && !(ZxNextRomLandmarks.IsAtKeyWait(machine)))
        {
            machine.StepInstruction();
            startupInstructions++;
        }

        if (!(ZxNextRomLandmarks.IsAtKeyWait(machine))
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
                && !(ZxNextRomLandmarks.IsAtKeyWait(machine)))
            {
                machine.StepInstruction();
                commandInstructions++;
                startupInstructions++;
            }

            if (ReadGuestWord(machine, 0x5C45) != 120
                && ZxNextRomLandmarks.IsAtKeyWait(machine))
            {
                _ = PressGuestChord(machine, [SpectrumKey.Enter]);
            }
        }

        while (startupInstructions < maximumStartupInstructions
            && !(ReadGuestWord(machine, 0x5C45) == 120
                && ZxNextRomLandmarks.IsAtKeyWait(machine)))
        {
            machine.StepInstruction();
            startupInstructions++;
        }

        if (!(ReadGuestWord(machine, 0x5C45) == 120
                && ZxNextRomLandmarks.IsAtKeyWait(machine))
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
                accepted = ZxNextRomLandmarks.LeftKeyWaitWithKey(previousPc, machine.Cpu.PC)
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
                    && machine.Registers.Read(0x02) == 0x02
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

                bool atKeyWait = ZxNextRomLandmarks.IsAtKeyWait(machine);
                if (atKeyWait && !previouslyAtKeyWait)
                {
                    keyWaitEntries++;
                }

                previouslyAtKeyWait = atKeyWait;
            }

            Require(keyWaitEntries == 3
                    && machine.UlaTiming.InterruptCount >= 3
                    && ZxNextRomLandmarks.IsAtKeyWait(machine)
                    && machine.Cpu.Iff1
                    && !machine.DivMmc.IsMapped
                    && machine.MemoryBus.CurrentRomBank == 0
                    && ZxNextRomLandmarks.MappedRomHasKeyWait(machine),
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
                    acceptedEnter = ZxNextRomLandmarks.LeftKeyWaitWithKey(previousPc, machine.Cpu.PC)
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
                && !(ZxNextRomLandmarks.IsAtKeyWait(machine)))
            {
                machine.StepInstruction();
                returnInstructions++;
            }

            Require(ZxNextRomLandmarks.IsAtKeyWait(machine),
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
            && !(ZxNextRomLandmarks.IsAtKeyWait(machine)))
        {
            machine.StepInstruction();
            welcomeInstructions++;
        }

        Require(ZxNextRomLandmarks.IsAtKeyWait(machine),
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
                acceptedSpace = ZxNextRomLandmarks.LeftKeyWaitWithKey(previousPc, machine.Cpu.PC)
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

            // The first uninstalled page is $E0 on a 2 MiB machine. In slots 0 and 1, as for
            // every page from $E0, zxnext.vhd shows the ROM; elsewhere it reaches nothing.
            machine.Registers.Write((byte)(0x50 + slot), ZxNextMemoryBus.RomPageSentinel);
            byte romByte = machine.MemoryBus.Read(logicalAddress);
            machine.Registers.Write((byte)(0x50 + slot), (byte)machine.MemoryBus.PhysicalMemory.InstalledRamPageCount);
            machine.MemoryBus.WriteCpu(logicalAddress, 0xA5);
            var openMapping = machine.MemoryBus.GetMapping(logicalAddress);
            Require(slot < 2
                    ? machine.MemoryBus.Read(logicalAddress) == romByte && openMapping.IsRom
                        && openMapping.IsReadOnly && !machine.MemoryBus.CanWriteDirect(logicalAddress)
                    : machine.MemoryBus.Read(logicalAddress) == 0xFF && openMapping.IsOpenBus
                        && openMapping.IsReadOnly && !machine.MemoryBus.CanWriteDirect(logicalAddress),
                $"MMU slot {slot} did not {(slot < 2 ? "show the ROM for" : "reject")} an uninstalled page.");
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
        Require(ZxNextPortBus.IsPort7Ffd(0x5FFD, plus3Timing: true, port1FfdEnabled: true)
                && ZxNextPortBus.IsPortDFfd(0xD1FD)
                && ZxNextPortBus.IsPort1Ffd(0x11FD) && ZxNextPortBus.IsPortEff7(0xE1F7)
                && !ZxNextPortBus.IsPort7Ffd(0x3FFD, plus3Timing: true, port1FfdEnabled: true)
                && ZxNextPortBus.IsPort7Ffd(0x3FFD, plus3Timing: false, port1FfdEnabled: true)
                && !ZxNextPortBus.IsPort7Ffd(0x1FFD, plus3Timing: false, port1FfdEnabled: true)
                && ZxNextPortBus.IsPort7Ffd(0x1FFD, plus3Timing: false, port1FfdEnabled: false),
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
            [4, 5, 6, 3],
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

        // Raw banks 16-127 are the 2 MiB machine's guest RAM, pages 0-223.
        machine.Registers.Write(0x04, 63);
        machine.MemoryBus.WriteCpu(0x3FFF, 0xDE);
        Require(machine.MemoryBus.ReadPhysicalRam(95, ZxNextPhysicalMemory.PageSize - 1) == 0xDE,
            "The last raw SRAM bank of the old 1 MiB range did not map page 95.");
        machine.Registers.Write(0x04, 127);
        machine.MemoryBus.WriteCpu(0x3FFF, 0xDF);
        Require(machine.MemoryBus.ReadPhysicalRam(223, ZxNextPhysicalMemory.PageSize - 1) == 0xDF,
            "The final installed raw SRAM bank did not map the final physical RAM page.");
        // NextReg $04 keeps seven bits on an issue 4 board, so 128 aliases raw bank 0, and with
        // 2 MiB fitted every bank it can reach is installed.
        machine.Registers.Write(0x04, 0);
        byte bankZeroByte = machine.MemoryBus.Read(0x0123);
        machine.Registers.Write(0x04, 128);
        Require(machine.MemoryBus.ConfigurationBank == 0
                && machine.MemoryBus.Read(0x0123) == bankZeroByte
                && !machine.MemoryBus.GetMapping(0x0123).IsOpenBus,
            "NextReg $04 = 128 did not alias raw bank 0.");

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

    /// <summary>
    /// A command clocked in while a CMD18 stream is still delivering data has to abandon
    /// that stream and be answered, with no CMD12 and no chip-select in between.
    /// </summary>
    /// <remarks>
    /// SPI is full duplex and a real card's command decoder never stops listening. Atic
    /// Atac Next relies on this: it ends one asset stream by issuing the next CMD18 straight
    /// into it. The card used to parse commands only once its response queue was empty,
    /// and in CMD18 mode the queue is refilled the moment it drains, so the new command
    /// was swallowed as dummy clocks and the game read the next sector of the old stream
    /// as its asset directory - all zeros, as it happened, which left every asset with a
    /// zero sector count and a loop that walked over its own stack.
    /// </remarks>
    private static void VerifySdCommandDuringMultipleRead()
    {
        string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"ZedExEss-zxnext-sd-midstream-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        string imagePath = Path.Combine(temporaryDirectory, "sd0.img");
        const int Sectors = 8;
        byte[] image = new byte[Sectors * 512];
        for (int sector = 0; sector < Sectors; sector++)
        {
            for (int index = 0; index < 512; index++)
            {
                image[(sector * 512) + index] = (byte)(((index * (sector + 3)) ^ (0x11 * sector)) & 0xFF);
            }
        }

        File.WriteAllBytes(imagePath, image);
        SpectrumDivMmcSdCard card = SpectrumDivMmcSdCard.Open(imagePath, writeProtected: false);
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        try
        {
            machine.DivMmc.AttachSdCard(0, card);
            machine.PortBus.WriteUncontended(0x12E7, 0x02);
            InitializeSdCard(machine);

            // A CMD18 at sector 1 delivers 1, then 2, then 3 ... for as long as it is clocked.
            SendSdCommand(machine, 18, 1);
            Require(ReadSdR1(machine) == 0x00, "CMD18 was rejected.");
            Require(DrainSdReadBlock(machine).AsSpan().SequenceEqual(image.AsSpan(512, 512)),
                "The first block of the stream was not sector 1.");

            // Part way into sector 2, a new CMD18 for sector 5 arrives: no CMD12, no deselect.
            for (int skip = 0; skip < 7; skip++)
            {
                _ = ReadSpi(machine);
            }

            SendSdCommand(machine, 18, 5);
            Require(ReadSdR1(machine) == 0x00, "A CMD18 issued during a running stream was not answered.");
            Require(DrainSdReadBlock(machine).AsSpan().SequenceEqual(image.AsSpan(5 * 512, 512)),
                "A CMD18 issued during a running stream did not restart the stream at its own sector.");
            Require(DrainSdReadBlock(machine).AsSpan().SequenceEqual(image.AsSpan(6 * 512, 512)),
                "The restarted stream did not continue from its own sector.");

            // A single-block read during the stream is answered too, and ends the streaming.
            for (int skip = 0; skip < 100; skip++)
            {
                _ = ReadSpi(machine);
            }

            SendSdCommand(machine, 17, 3);
            Require(ReadSdR1(machine) == 0x00, "A CMD17 issued during a running stream was not answered.");
            Require(DrainSdReadBlock(machine).AsSpan().SequenceEqual(image.AsSpan(3 * 512, 512)),
                "A CMD17 issued during a running stream returned the wrong sector.");
            bool idle = true;
            for (int index = 0; index < 600 && idle; index++)
            {
                idle = ReadSpi(machine) == 0xFF;
            }

            Require(idle, "The stream carried on after a CMD17 had ended it.");

            // And CMD12 in the middle of a block still stops the card cleanly.
            SendSdCommand(machine, 18, 2);
            Require(ReadSdR1(machine) == 0x00, "The second CMD18 was rejected.");
            Require(DrainSdReadBlock(machine).AsSpan().SequenceEqual(image.AsSpan(2 * 512, 512)),
                "The second stream did not start at sector 2.");
            for (int skip = 0; skip < 40; skip++)
            {
                _ = ReadSpi(machine);
            }

            SendSdCommand(machine, 12, 0);
            Require(ReadSdR1(machine) == 0x00, "CMD12 during a block was not answered.");
            idle = true;
            for (int index = 0; index < 600 && idle; index++)
            {
                idle = ReadSpi(machine) == 0xFF;
            }

            Require(idle, "The stream carried on after CMD12.");

            // The card is still a card: an ordinary read works afterwards.
            Require(ReadSdSector(machine, 7).AsSpan().SequenceEqual(image.AsSpan(7 * 512, 512)),
                "An ordinary CMD17 after the interrupted streams returned the wrong sector.");
        }
        finally
        {
            machine.DivMmc.AttachSdCard(0, null);
            card.Dispose();
            try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
        }
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

    /// <summary>
    /// RETI copies IFF2 into IFF1 exactly as RETN does (the shared Z80's RETI did not), so
    /// an NMI handler that ends in RETI returns with interrupts enabled again.
    /// </summary>
    private static void VerifyReturnRestoresIff1()
    {
        foreach (byte opcode in new byte[] { 0x4D, 0x45 })
        {
            ZxNextMachine machine = CreateProgram(0xED, opcode);
            machine.MemoryBus.WriteCpu(0x3000, 0x34);
            machine.MemoryBus.WriteCpu(0x3001, 0x12);
            machine.Cpu.RestoreSnapshotState(machine.Cpu.CaptureSnapshotState() with
            {
                SP = 0x3000,
                Iff1 = false,
                Iff2 = true
            });
            machine.StepInstruction();
            Require(machine.Cpu.PC == 0x1234 && machine.Cpu.SP == 0x3002
                    && machine.Cpu.Iff1 && machine.Cpu.Iff2,
                $"ED {opcode:X2} did not return with IFF1 restored from IFF2 " +
                $"(PC=${machine.Cpu.PC:X4}, IFF1={machine.Cpu.Iff1}).");
        }
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

    /// <summary>
    /// Where the Next's T80N differs from a Zilog Z80, from <c>t80n.vhd</c>,
    /// <c>t80n_mcode.vhd</c> and <c>t80n_alu.vhd</c>.
    /// </summary>
    /// <remarks>
    /// Every expected value was worked out by hand from the pinned VHDL, and the inputs are
    /// chosen so that a Zilog Z80 (Fuse's results, which <c>--verify-z80-core</c> holds the
    /// Spectrum to) would give a different answer.
    /// </remarks>
    private static void VerifyT80nDifferences()
    {
        ZxNextMachine Run(byte[] program, Action<ZxNextMachine> setup, int instructions = 1, ushort at = 0)
        {
            ZxNextMachine machine = at == 0 ? CreateProgram(program) : CreateProgramAt(at, program);
            setup(machine);
            for (int step = 0; step < instructions; step++)
            {
                machine.StepInstruction();
            }

            return machine;
        }

        void RequireFlags(ZxNextMachine machine, byte expected, string what) =>
            Require(machine.Cpu.GetFlags() == expected,
                $"{what}: F was {machine.Cpu.GetFlags():X2}, the T80N gives {expected:X2}.");

        // SCF and CCF: flag bits 5/3 from A alone, never from Q (t80n.vhd's I_SCF/I_CCF).
        // After a NOP, a Zilog Z80 would OR in the old F's bits (0xED and 0xFC).
        RequireFlags(Run([0x00, 0x37], m => { m.Cpu.A = 0x00; m.Cpu.SetFlags(0xEC); }, 2), 0xC5,
            "SCF after an instruction that set no flags");
        RequireFlags(Run([0x00, 0x3F], m => { m.Cpu.A = 0x00; m.Cpu.SetFlags(0xED); }, 2), 0xD4,
            "CCF after an instruction that set no flags");

        // LD A,I and LD A,R keep C and bits 5/3 (Zilog: copied from the value, 0x85 and 0x00).
        ZxNextMachine loadI = Run([0xED, 0x57], m =>
        {
            m.Cpu.I = 0x80;
            m.Cpu.SetFlags(0x29);
            m.Cpu.SetInterruptState(1, iff1: true, iff2: true);
        });
        Require(loadI.Cpu.A == 0x80, "LD A,I did not load I.");
        RequireFlags(loadI, 0xAD, "LD A,I");
        // And the value's own bits 5/3 are not copied (Zilog: 0xAD).
        RequireFlags(Run([0xED, 0x57], m =>
        {
            m.Cpu.I = 0xA8;
            m.Cpu.SetFlags(0x01);
            m.Cpu.SetInterruptState(1, iff1: true, iff2: true);
        }), 0x85, "LD A,I with I = $A8");
        RequireFlags(Run([0xED, 0x5F], m =>
        {
            m.Cpu.R = 0x00;
            m.Cpu.SetFlags(0x28);
            m.Cpu.SetInterruptState(1, iff1: false, iff2: false);
        }), 0x28, "LD A,R");

        // IN A,(C) keeps C and bits 5/3 (Zilog: 0x45). Port $253B reads NextReg $7F.
        ZxNextMachine input = Run([0xED, 0x78], m =>
        {
            m.Registers.Write(0x7F, 0x00);
            m.PortBus.WriteUncontended(0x243B, 0x7F);
            m.Cpu.B = 0x25;
            m.Cpu.C = 0x3B;
            m.Cpu.A = 0x99;
            m.Cpu.SetFlags(0x29);
        });
        Require(input.Cpu.A == 0x00, "IN A,(C) did not read NextReg $7F through $253B.");
        RequireFlags(input, 0x6D, "IN A,(C)");

        // CPI takes bits 5/3 from the byte compared (Zilog: from A - value - H, 0xB6).
        RequireFlags(Run([0xED, 0xA1], m =>
        {
            m.Cpu.A = 0x00;
            m.Cpu.B = 0x00;
            m.Cpu.C = 0x02;
            m.Cpu.H = 0x10;
            m.Cpu.L = 0x00;
            m.MemoryBus.WriteCpu(0x1000, 0x28);
            m.Cpu.SetFlags(0x00);
        }), 0xBE, "CPI");

        // INI/OUTI: every flag from SUB B,1, whatever the byte (Zilog: 0x2C and 0x40 here).
        RequireFlags(Run([0xED, 0xA2], m =>
        {
            m.Cpu.B = 0x80;
            m.Cpu.C = 0xFE;
            m.Cpu.H = 0x10;
            m.Cpu.L = 0x00;
            m.Cpu.SetFlags(0x00);
        }), 0x3E, "INI with B = $80");
        RequireFlags(Run([0xED, 0xA3], m =>
        {
            m.Cpu.B = 0x01;
            m.Cpu.C = 0xFE;
            m.Cpu.H = 0x10;
            m.Cpu.L = 0x00;
            m.MemoryBus.WriteCpu(0x1000, 0x00);
            m.Cpu.SetFlags(0x00);
        }), 0x42, "OUTI with B = $01");

        // A repeating iteration changes no flag (a Zilog Z80 copies PC bits 13/11, here
        // $A8's, into bits 5/3, and INIR/OTIR's other repeat flags).
        RequireFlags(Run([0xED, 0xB0], m =>
        {
            m.Cpu.B = 0x00;
            m.Cpu.C = 0x02;
            m.Cpu.H = 0x90;
            m.Cpu.L = 0x00;
            m.Cpu.D = 0x91;
            m.Cpu.E = 0x00;
            m.Cpu.A = 0x00;
            m.MemoryBus.WriteCpu(0x9000, 0x00);
            m.Cpu.SetFlags(0x00);
        }, at: 0xA800), 0x04, "A repeating LDIR at $A800");
        RequireFlags(Run([0xED, 0xB1], m =>
        {
            m.Cpu.B = 0x00;
            m.Cpu.C = 0x02;
            m.Cpu.H = 0x90;
            m.Cpu.L = 0x00;
            m.Cpu.A = 0x00;
            m.MemoryBus.WriteCpu(0x9000, 0x01);
            m.Cpu.SetFlags(0x00);
        }, at: 0xA800), 0x96, "A repeating CPIR at $A800");
        ZxNextMachine inir = Run([0xED, 0xB2], m =>
        {
            m.Cpu.B = 0x02;
            m.Cpu.C = 0xFE;
            m.Cpu.H = 0x90;
            m.Cpu.L = 0x00;
            m.Cpu.SetFlags(0x00);
        }, at: 0xA800);
        Require(inir.Cpu.PC == 0xA800 && inir.Cpu.B == 0x01, "INIR did not repeat.");
        RequireFlags(inir, 0x02, "A repeating INIR at $A800");
        RequireFlags(Run([0xED, 0xB4], m =>
        {
            m.Cpu.B = 0x00;
            m.Cpu.C = 0x02;
            m.Cpu.H = 0x90;
            m.Cpu.L = 0x00;
            m.Cpu.D = 0x91;
            m.Cpu.E = 0x00;
            m.Cpu.A = 0x00;
            m.MemoryBus.WriteCpu(0x9000, 0x11);
            m.Cpu.SetFlags(0x00);
        }, at: 0xA800), 0x04, "A repeating LDIRX at $A800");

        // BIT: bits 5/3 cleared for an opcode ending 110, otherwise from the byte; never MEMPTR
        // (a Zilog Z80 gives 0x38 and 0x18 here).
        RequireFlags(Run([0xCB, 0x46], m =>
        {
            m.Cpu.H = 0x10;
            m.Cpu.L = 0x00;
            m.MemoryBus.WriteCpu(0x1000, 0x29);
            m.Cpu.MemPtr = 0x2800;
            m.Cpu.SetFlags(0x00);
        }), 0x10, "BIT 0,(HL) with MEMPTR $2800");
        RequireFlags(Run([0xDD, 0xCB, 0x00, 0x46], m =>
        {
            m.Cpu.IX = 0x0800;
            m.MemoryBus.WriteCpu(0x0800, 0x29);
            m.Cpu.SetFlags(0x00);
        }), 0x10, "BIT 0,(IX+0) at $0800");
        RequireFlags(Run([0xDD, 0xCB, 0x00, 0x40], m =>
        {
            m.Cpu.IX = 0x0800;
            m.MemoryBus.WriteCpu(0x0800, 0x29);
            m.Cpu.SetFlags(0x00);
        }), 0x38, "The undocumented DD CB d 40 BIT form at $0800");

        // Z80N: OUTINB keeps F; LDWS carries out of D + 1; LDPIRX's flags come from B (after
        // BC's decrement) OR the byte; LDIX's from A + the byte.
        RequireFlags(Run([0xED, 0x90], m =>
        {
            m.Cpu.B = 0x12;
            m.Cpu.C = 0x34;
            m.Cpu.H = 0x10;
            m.Cpu.L = 0x00;
            m.MemoryBus.WriteCpu(0x1000, 0xA5);
            m.Cpu.SetFlags(0xD7);
        }), 0xD7, "OUTINB");
        RequireFlags(Run([0xED, 0xA5], m =>
        {
            m.Cpu.H = 0x10;
            m.Cpu.L = 0xFF;
            m.Cpu.D = 0xD0;
            m.Cpu.E = 0x10;
            m.Cpu.SetFlags(0x01);
        }), 0x80, "LDWS with D = $D0");
        RequireFlags(Run([0xED, 0xA5], m =>
        {
            m.Cpu.H = 0x10;
            m.Cpu.L = 0x00;
            m.Cpu.D = 0xFF;
            m.Cpu.E = 0x10;
            m.Cpu.SetFlags(0x00);
        }), 0x51, "LDWS with D = $FF");
        ZxNextMachine ldpirx = Run([0xED, 0xB7], m =>
        {
            m.Cpu.A = 0x5F;
            m.Cpu.B = 0x02;
            m.Cpu.C = 0x00;
            m.Cpu.H = 0x10;
            m.Cpu.L = 0x00;
            m.Cpu.D = 0x20;
            m.Cpu.E = 0x00;
            m.MemoryBus.WriteCpu(0x1000, 0x00);
            m.Cpu.SetFlags(0x00);
        });
        Require(ldpirx.Cpu.BC == 0x01FF, "LDPIRX did not decrement BC.");
        RequireFlags(ldpirx, 0x04, "LDPIRX with BC = $0200 (B | byte after the decrement is $01)");
        RequireFlags(Run([0xED, 0xA4], m =>
        {
            m.Cpu.A = 0x00;
            m.Cpu.B = 0x00;
            m.Cpu.C = 0x02;
            m.Cpu.H = 0x10;
            m.Cpu.L = 0x00;
            m.Cpu.D = 0x20;
            m.Cpu.E = 0x00;
            m.MemoryBus.WriteCpu(0x1000, 0x0A);
            m.Cpu.SetFlags(0xC1);
        }), 0xED, "LDIX of $0A with A = 0");

        // ED B6 (LDIRSCALE) runs as LDIRX: its scaling is commented out of t80n.vhd.
        ZxNextMachine scale = Run([0xED, 0xB6], m =>
        {
            m.Cpu.A = 0x00;
            m.Cpu.B = 0x00;
            m.Cpu.C = 0x01;
            m.Cpu.H = 0x10;
            m.Cpu.L = 0x00;
            m.Cpu.D = 0x20;
            m.Cpu.E = 0x00;
            m.MemoryBus.WriteCpu(0x1000, 0x77);
        });
        Require(scale.MemoryBus.Read(0x2000) == 0x77 && scale.Cpu.HL == 0x1001 && scale.Cpu.DE == 0x2001
                && scale.Cpu.BC == 0 && scale.Cpu.PC == 2 && scale.Cpu.Cyc == 16,
            "ED B6 did not run as LDIRX (copy, HL+1, DE+1, BC-1, 16 T-states).");
        ZxNextMachine scaleSkip = Run([0xED, 0xB6], m =>
        {
            m.Cpu.A = 0x77;
            m.Cpu.B = 0x00;
            m.Cpu.C = 0x02;
            m.Cpu.H = 0x10;
            m.Cpu.L = 0x00;
            m.Cpu.D = 0x20;
            m.Cpu.E = 0x00;
            m.MemoryBus.WriteCpu(0x1000, 0x77);
            m.MemoryBus.WriteCpu(0x2000, 0x55);
        });
        Require(scaleSkip.MemoryBus.Read(0x2000) == 0x55 && scaleSkip.Cpu.PC == 0 && scaleSkip.Cpu.Cyc == 21,
            "ED B6 did not skip the byte equal to A and repeat as LDIRX does.");

        // PIXELDN: the 8-bit row H4H3 L7L6L5 H2H1H0 plus one; H7-H5 and L4-L0 untouched.
        (ushort From, ushort To)[] pixelDown =
        [
            (0x4000, 0x4100), (0x4700, 0x4020), (0x47E0, 0x4800), (0x57E5, 0x5805),
            (0x5FE0, 0x4000), (0x7FFF, 0x601F)
        ];
        foreach ((ushort from, ushort to) in pixelDown)
        {
            ZxNextMachine down = Run([0xED, 0x93], m =>
            {
                m.Cpu.H = (byte)(from >> 8);
                m.Cpu.L = (byte)from;
                m.Cpu.SetFlags(0xD7);
            });
            Require(down.Cpu.HL == to && down.Cpu.GetFlags() == 0xD7,
                $"PIXELDN {from:X4} gave {down.Cpu.HL:X4}, the T80N gives {to:X4} (row 255 wraps within H7-H5).");
        }

        // Bus cycles (FIX-CPU-3). Each line is a cycle: F fetch, R read, W write, I one internal
        // T-state, with its address.
        string[] Cycles(byte[] program, Action<ZxNextMachine> setup)
        {
            ZxNextMachine machine = CreateProgram(program);
            setup(machine);
            var log = new BusCycleLog();
            machine.Cpu.ConfigureBusObserver(log);
            machine.StepInstruction();
            machine.Cpu.ConfigureBusObserver(null);
            return log.Cycles.ToArray();
        }

        void RequireCycles(string[] actual, string[] expected, string what) =>
            Require(actual.SequenceEqual(expected),
                $"{what}: cycles were [{string.Join(" ", actual)}], t80n_mcode.vhd gives [{string.Join(" ", expected)}].");

        // NEXTREG: two read cycles after the operands, not internal cycles.
        RequireCycles(Cycles([0xED, 0x91, 0x7F, 0x05], _ => { }),
            ["F0000", "F0001", "R0002", "R0003", "R0004", "R0004"], "NEXTREG $7F,5");
        RequireCycles(Cycles([0xED, 0x92, 0x7F], m => m.Cpu.A = 0x05),
            ["F0000", "F0001", "R0002", "R0003", "R0003"], "NEXTREG $7F,A");
        // PUSH nn: each operand read is followed by its stack write, then a read cycle.
        RequireCycles(Cycles([0xED, 0x8A, 0x12, 0x34], m => m.Cpu.SP = 0x3000),
            ["F0000", "F0001", "R0002", "W2FFF", "R0003", "W2FFE", "R0004"], "PUSH $1234");
        // LDIX with the write suppressed: the destination M-cycle is a read at DE.
        RequireCycles(Cycles([0xED, 0xA4], m =>
            {
                m.Cpu.A = 0xA5;
                m.Cpu.B = 0x00;
                m.Cpu.C = 0x01;
                m.Cpu.H = 0x10;
                m.Cpu.L = 0x00;
                m.Cpu.D = 0x20;
                m.Cpu.E = 0x00;
                m.MemoryBus.WriteCpu(0x1000, 0xA5);
            }),
            ["F0000", "F0001", "R1000", "R2000", "I2000", "I2000"], "LDIX with the byte equal to A");

        ZxNextMachine nextReg = Run([0xED, 0x91, 0x7F, 0x05], _ => { });
        ZxNextMachine push = Run([0xED, 0x8A, 0x12, 0x34], m => m.Cpu.SP = 0x3000);
        Require(nextReg.Registers.Read(0x7F) == 0x05 && nextReg.Cpu.Cyc == 20 && nextReg.Cpu.PC == 4
                && push.MemoryBus.Read(0x2FFF) == 0x12 && push.MemoryBus.Read(0x2FFE) == 0x34
                && push.Cpu.SP == 0x2FFE && push.Cpu.Cyc == 23 && push.Cpu.PC == 4,
            "NEXTREG or PUSH nn changed its result or length (20 and 23 T-states).");
    }

    /// <summary>Bus cycles in order, as the CPU reports them.</summary>
    private sealed class BusCycleLog : ZedExEss.Z80CPU.IZ80BusObserver
    {
        private int _pendingCycles;
        private ushort _pendingAddress;

        public List<string> Cycles { get; } = [];

        public void OnContend(ushort address, int cycles)
        {
            if (cycles == 1)
            {
                Cycles.Add($"I{address:X4}");
                return;
            }

            _pendingCycles = cycles;
            _pendingAddress = address;
        }

        public void OnRead(ushort address, byte value)
        {
            Cycles.Add($"{(_pendingCycles == 4 ? 'F' : 'R')}{address:X4}");
            _pendingCycles = 0;
        }

        public void OnWrite(ushort address, byte value)
        {
            Cycles.Add($"W{address:X4}");
            _pendingCycles = 0;
        }

        public void OnPortContend(ushort port, int cycles)
        {
        }

        public void OnPortRead(ushort port, byte value) => Cycles.Add($"P{port:X4}");

        public void OnPortWrite(ushort port, byte value) => Cycles.Add($"O{port:X4}");
    }

    /// <summary>
    /// Hardware IM2 and the pulse, from <c>peripherals.vhd</c>, <c>im2_peripheral.vhd</c>,
    /// <c>im2_device.vhd</c>, <c>im2_control.vhd</c> and <c>zxnext.vhd</c>'s interrupt lines.
    /// </summary>
    /// <remarks>
    /// The expected vectors, orders and register values were worked out by hand from the VHDL:
    /// priority is the source index (0 line, 3-10 CTC 0-7, 11 ULA), the vector
    /// <c>($C0 &amp; $E0) | index * 2</c>.
    /// </remarks>
    private static void VerifyHardwareIm2()
    {
        // --- The NextRegs ------------------------------------------------------------------
        ZxNextMachine registers = ZxNextMachineFactory.CreateFoundationMachine();
        Require(registers.Registers.Read(0xC0) == 0x00 && registers.Registers.Read(0xC4) == 0x81
                && registers.Registers.Read(0xC5) == 0 && registers.Registers.Read(0xC6) == 0
                && registers.Registers.Read(0xC8) == 0 && registers.Registers.Read(0xC9) == 0
                && registers.Registers.Read(0xCA) == 0 && registers.Registers.Read(0xCC) == 0
                && registers.Registers.Read(0xCD) == 0 && registers.Registers.Read(0xCE) == 0
                && registers.Registers.Read(0x20) == 0,
            "The interrupt NextRegs did not reset to $C0 = 0, $C4 = $81 and the rest 0.");
        foreach (byte register in new byte[] { 0xC0, 0xC5, 0xC6, 0xCC, 0xCD, 0xCE })
        {
            registers.Registers.Write(register, 0xFF);
        }

        Require(registers.Registers.Read(0xC0) == 0xE9 && registers.Registers.Read(0xC5) == 0x0F
                && registers.Registers.Read(0xC6) == 0x77 && registers.Registers.Read(0xCC) == 0x83
                && registers.Registers.Read(0xCD) == 0xFF && registers.Registers.Read(0xCE) == 0x77,
            "$C0, $C5 (four CTC channels), $C6 or $CC-$CE did not keep the bits zxnext.vhd stores.");
        registers.Registers.Write(0xC0, 0x00);
        ZxNextMachine im2Mode = CreateProgram(0xED, 0x5E);
        im2Mode.StepInstruction();
        Require((im2Mode.Registers.Read(0xC0) & 0x06) == 0x04,
            "$C0 bits 2-1 did not read the IM 2 the CPU executed.");

        // --- Status, in both modes ------------------------------------------------------------
        ZxNextMachine status = ZxNextMachineFactory.CreateFoundationMachine();
        status.Registers.Write(0x20, 0xC5);
        Require(status.Registers.Read(0x20) == 0xC5 && status.Registers.Read(0xC8) == 0x03
                && status.Registers.Read(0xC9) == 0x05 && status.UlaTiming.InterruptLineActive,
            "A $20 write in pulse mode did not latch the line, ULA and CTC 0/2 status bits and pulse INT.");
        status.Registers.Write(0xC8, 0x02);
        status.Registers.Write(0xC9, 0x01);
        Require(status.Registers.Read(0x20) == 0x44 && status.Registers.Read(0xC8) == 0x01,
            "Writing 1 to $C8/$C9 bits did not clear those status bits alone.");

        ZxNextMachine pending = ZxNextMachineFactory.CreateFoundationMachine();
        pending.Registers.Write(0xC0, 0x01);
        pending.Registers.Write(0x20, 0x80);
        pending.Registers.Write(0xC8, 0x02);
        Require(pending.Registers.Read(0xC8) == 0x02 && !pending.UlaTiming.InterruptLineActive,
            "In hardware IM2 mode a pending line interrupt's status did not stay set after a clear, or it pulsed.");
        pending.Registers.Write(0xC0, 0x00);
        Require(pending.Registers.Read(0xC8) == 0x00 && pending.Registers.Interrupts.Pending == 0,
            "Pulse mode did not drop the pending hardware IM2 request.");

        // --- The ULA exception -----------------------------------------------------------------
        ZxNextMachine imOne = ZxNextMachineFactory.CreateFoundationMachine();
        imOne.Registers.Write(0xC0, 0x01);
        imOne.Cpu.SetInterruptState(1, iff1: false, iff2: false);
        imOne.Registers.Write(0x20, 0x80);
        Require(!imOne.UlaTiming.InterruptLineActive && !imOne.Cpu.InterruptPending,
            "A line interrupt reached a CPU in IM 1 in hardware IM2 mode.");
        imOne.Registers.Write(0x20, 0x40);
        Require(imOne.UlaTiming.InterruptLineActive && imOne.Cpu.InterruptPending,
            "The ULA did not pulse for a CPU in IM 1 in hardware IM2 mode (the EXCEPTION generic).");

        // The same for the frame interrupt itself: it pulses, and is also left pending.
        ZxNextMachine frame = CreateProgram(0xF3, 0xED, 0x56, 0x18, 0xFE);
        frame.Registers.Write(0xC0, 0x01);
        ulong frameInterrupts = frame.UlaTiming.InterruptCount;
        for (int step = 0; step < 20_000 && frame.UlaTiming.InterruptCount == frameInterrupts; step++)
        {
            frame.StepInstruction();
        }

        Require(frame.UlaTiming.InterruptCount > frameInterrupts && frame.UlaTiming.InterruptLineActive
                && frame.Registers.Interrupts.Pending == 0x0800,
            "The frame interrupt did not pulse for a CPU in IM 1 in hardware IM2 mode.");

        // A real line interrupt in hardware IM2 mode is a request, not a pulse: it has no
        // exception, so a CPU in IM 1 sees nothing and the request waits.
        ZxNextMachine line = CreateProgram(0xF3, 0xED, 0x56, 0x18, 0xFE);
        line.Registers.Write(0xC0, 0x01);
        line.Registers.Write(0x22, 0x06);
        line.Registers.Write(0x23, 0x40);
        ulong lineInterrupts = line.UlaTiming.LineInterruptCount;
        for (int step = 0; step < 20_000 && line.UlaTiming.LineInterruptCount == lineInterrupts; step++)
        {
            line.StepInstruction();
        }

        Require(line.UlaTiming.LineInterruptCount > lineInterrupts && !line.UlaTiming.InterruptLineActive
                && line.Registers.Interrupts.Pending == 0x0001 && !line.Cpu.InterruptPending,
            "A line interrupt in hardware IM2 mode pulsed, or was not left pending, for a CPU in IM 1.");

        // A reset clears the chain and its registers.
        ZxNextMachine reset = ZxNextMachineFactory.CreateFoundationMachine();
        reset.Registers.Write(0xC0, 0xA1);
        reset.Registers.Write(0xC5, 0x0F);
        reset.Registers.Write(0xC6, 0x77);
        reset.Registers.Write(0xCC, 0x83);
        reset.Registers.Write(0xCD, 0xFF);
        reset.Registers.Write(0xCE, 0x77);
        reset.Registers.Write(0x20, 0xCF);
        reset.Reset(ZxNextResetKind.Soft);
        Require(reset.Registers.Read(0xC0) == 0 && reset.Registers.Read(0xC5) == 0
                && reset.Registers.Read(0xC6) == 0 && reset.Registers.Read(0xCC) == 0
                && reset.Registers.Read(0xCD) == 0 && reset.Registers.Read(0xCE) == 0
                && reset.Registers.Read(0x20) == 0 && reset.Registers.Interrupts.Pending == 0,
            "A soft reset did not clear the interrupt chain and its registers.");

        // INT follows the IM: a request pending under IM 0 drives INT once IM 2 is executed.
        ZxNextMachine imChange = CreateProgram(0xED, 0x5E);
        imChange.Registers.Write(0xC0, 0x01);
        imChange.Registers.Write(0x20, 0x80);
        Require(!imChange.Cpu.InterruptPending, "A hardware IM2 request drove INT for a CPU in IM 0.");
        imChange.StepInstruction();
        Require(imChange.Cpu.InterruptPending && imChange.Registers.Interrupts.Im2InterruptAsserted,
            "Executing IM 2 did not let a pending hardware IM2 request drive INT.");

        // --- Vectors, priority, nesting -------------------------------------------------------
        RequireIm2Log("$20 = $C9: line, CTC 0 and 3, ULA pending together",
            "NEXTREG $20,$C9", [], [0xA0, 0xA6, 0xAC, 0xB6]);
        // The line nests in the ULA's handler and raises CTC 0; its RETI ends the line's
        // service, not the ULA's, so CTC 0 (above the ULA) nests at once.
        RequireIm2Log("a line interrupt raised inside the ULA's handler nests",
            "NEXTREG $20,$40",
            [
                (0xB6, "LD A,$B6\nCALL LogA\nNEXTREG $20,$80\nEI\nNOP\nNOP\nLD A,$B7\nCALL LogA\nEI\nRETI"),
                (0xA0, "LD A,$A0\nCALL LogA\nNEXTREG $20,$01\nEI\nRETI")
            ],
            [0xB6, 0xA0, 0xA6, 0xB7]);
        RequireIm2Log("a ULA interrupt raised inside the line's handler waits for its RETI",
            "NEXTREG $20,$80",
            [(0xA0, "LD A,$A0\nCALL LogA\nNEXTREG $20,$40\nEI\nNOP\nNOP\nLD A,$A1\nCALL LogA\nEI\nRETI")],
            [0xA0, 0xA1, 0xB6]);
        ZxNextMachine retn = RequireIm2Log("a RETN ends no service",
            "NEXTREG $20,$80\nEI\nNOP\nNOP\nNEXTREG $20,$40",
            [(0xA0, "LD A,$A0\nCALL LogA\nEI\nRETN")],
            [0xA0]);
        Require(retn.Registers.Interrupts.InService == 0x0001 && retn.Registers.Interrupts.Pending == 0x0801,
            "After a RETN the line was not still in service with the ULA waiting behind it.");
        ZxNextMachine retiInImOne = RequireIm2Log("a RETI in IM 1 ends no service",
            "NEXTREG $20,$80",
            [(0xA0, "LD A,$A0\nCALL LogA\nIM 1\nRETI")],
            [0xA0]);
        Require(retiInImOne.Registers.Interrupts.InService == 0x0001,
            "A RETI decoded while the CPU was in IM 1 ended the line's service.");

        // In pulse mode an IM 2 acknowledge reads $FF from the bus.
        RequireIm2Log("IM 2 in pulse mode reads its pointer at I * 256 + $FF",
            "NEXTREG $C0,$00\nNEXTREG $20,$40", [], [0xFF]);

        VerifyIm2DmaHoldOff(holdOff: true);
        VerifyIm2DmaHoldOff(holdOff: false);
        VerifyIm2DmaHoldOffAtStartAndInBurst();

        // An active NMI holds the DMA off only with $CC bit 7.
        ZxNextMachine nmi = ZxNextMachineFactory.CreateFoundationMachine();
        nmi.Registers.Nmi.Request(multiface: true, divMmc: false);
        Require(nmi.Registers.Nmi.Activated && !nmi.Registers.Interrupts.DmaHeldOff(nmi.Clock.MasterTicks),
            "An NMI held the DMA off without $CC bit 7.");
        nmi.Registers.Write(0xCC, 0x80);
        Require(nmi.Registers.Interrupts.DmaHeldOff(nmi.Clock.MasterTicks),
            "An active NMI did not hold the DMA off with $CC bit 7.");
    }

    /// <summary>
    /// Runs an IM 2 program in hardware IM2 mode (vectors $A0) and requires its handlers' log.
    /// </summary>
    /// <remarks>
    /// Table at $7E00 (I = $7E); every even vector and $FF enters a stub that logs the vector
    /// and returns with EI, RETI, unless a handler of its own is given. The ULA's frame
    /// interrupt is off ($22 bit 2), so only the program's requests are seen.
    /// </remarks>
    private static ZxNextMachine RequireIm2Log(
        string what,
        string body,
        (byte Vector, string Code)[] handlers,
        byte[] expected)
    {
        ushort logA = 0x6F00;
        ushort commonAddress = 0x6F80;
        const string logRoutine = """
                    PUSH HL
                    LD HL,($7F80)
                    LD (HL),A
                    INC HL
                    LD ($7F80),HL
                    POP HL
                    RET
            """;
        const string commonRoutine = """
                    CALL $6F00
                    EI
                    RETI
            """;
        string program = $"""
                    DI
                    LD SP,$5FFE
                    LD A,$7E
                    DB $ED,$47
                    IM 2
                    NEXTREG $22,$04
                    NEXTREG $C0,$A1
            {body}
                    EI
            Park:   JR Park
            """;
        var assembler = new Z80InlineAssembler(enableZ80N: true);
        Z80AssemblyResult main = assembler.Assemble(0x6000, program);
        Z80AssemblyResult log = assembler.Assemble(logA, logRoutine);
        Z80AssemblyResult common = assembler.Assemble(commonAddress, commonRoutine);
        Require(main.Success && log.Success && common.Success,
            $"{what}: the program did not assemble: {main.Error}{log.Error}{common.Error}");
        ZxNextMachine machine = CreateProgramAt(0x6000, main.Bytes);
        WriteBytes(machine, logA, log.Bytes);
        WriteBytes(machine, commonAddress, common.Bytes);
        machine.MemoryBus.WriteCpu(0x7F80, 0x90);
        machine.MemoryBus.WriteCpu(0x7F81, 0x7F);

        ushort StubAddress(int vector) => (ushort)(0x7000 + (vector * 8));
        for (int vector = 0; vector < 256; vector++)
        {
            if (vector % 2 == 1 && vector != 0xFF)
            {
                continue;
            }

            ushort stub = StubAddress(vector);
            machine.MemoryBus.WriteCpu((ushort)(0x7E00 + vector), (byte)stub);
            machine.MemoryBus.WriteCpu((ushort)(0x7E01 + vector), (byte)(stub >> 8));
            byte[] code = [0x3E, (byte)vector, 0xC3, (byte)commonAddress, (byte)(commonAddress >> 8)];
            for (int index = 0; index < code.Length; index++)
            {
                machine.MemoryBus.WriteCpu((ushort)(stub + index), code[index]);
            }
        }

        // Handlers of their own go in $9000 upwards.
        ushort next = 0x9000;
        foreach ((byte vector, string code) in handlers)
        {
            Z80AssemblyResult handler = assembler.Assemble(next, code.Replace("LogA", $"${logA:X4}"));
            Require(handler.Success, $"{what}: a handler did not assemble: {handler.Error}");
            for (int index = 0; index < handler.Bytes.Length; index++)
            {
                machine.MemoryBus.WriteCpu((ushort)(next + index), handler.Bytes[index]);
            }

            machine.MemoryBus.WriteCpu((ushort)(0x7E00 + vector), (byte)next);
            machine.MemoryBus.WriteCpu((ushort)(0x7E01 + vector), (byte)(next >> 8));
            next = (ushort)(next + 0x100);
        }

        for (int step = 0; step < 4000; step++)
        {
            machine.StepInstruction();
        }

        int count = (machine.MemoryBus.ReadDirect(0x7F80) | (machine.MemoryBus.ReadDirect(0x7F81) << 8)) - 0x7F90;
        byte[] entries = new byte[Math.Max(0, count)];
        for (int index = 0; index < entries.Length; index++)
        {
            entries[index] = machine.MemoryBus.ReadDirect((ushort)(0x7F90 + index));
        }

        Require(entries.SequenceEqual(expected),
            $"{what}: the handlers ran [{string.Join(" ", entries.Select(static v => v.ToString("X2")))}], "
            + $"the daisy chain gives [{string.Join(" ", expected.Select(static v => v.ToString("X2")))}].");
        return machine;
    }

    /// <summary>
    /// A 16K continuous DMA copy spans the ULA's frame interrupt. With $CC bit 0 the request
    /// holds the DMA off after its current byte, the CPU takes the interrupt, and the DMA
    /// carries on after the handler's RETI; without it the interrupt waits for the end.
    /// </summary>
    private static void VerifyIm2DmaHoldOff(bool holdOff)
    {
        string program = $"""
                    DI
                    LD SP,$5FFE
                    LD A,$7E
                    DB $ED,$47
                    IM 2
                    NEXTREG $C0,$A1
                    NEXTREG $CC,{(holdOff ? "$01" : "$00")}
                    LD HL,Dma
                    LD B,12
                    LD C,$6B
                    EI
                    OTIR
            Park:   JR Park
            Dma:    DB $7D,$00,$C0,$00,$40,$14,$10,$AD,$00,$80,$CF,$87
            """;
        string handler = """
                    PUSH AF
                    LD A,($7F01)
                    OR A
                    JR NZ,Seen
                    LD A,($BFFF)
                    LD ($7F00),A
                    LD A,1
                    LD ($7F01),A
            Seen:   POP AF
                    EI
                    RETI
            """;
        var assembler = new Z80InlineAssembler(enableZ80N: true);
        Z80AssemblyResult main = assembler.Assemble(0x6000, program);
        Z80AssemblyResult isr = assembler.Assemble(0x7000, handler);
        Require(main.Success && isr.Success, $"The DMA hold-off programs did not assemble: {main.Error}{isr.Error}");
        ZxNextMachine machine = CreateProgramAt(0x6000, main.Bytes);
        WriteBytes(machine, 0x7000, isr.Bytes);

        // Start just after a frame interrupt, so the next one falls inside the 16K transfer.
        machine.MemoryBus.WriteCpu(0x5F00, 0x18);
        machine.MemoryBus.WriteCpu(0x5F01, 0xFE);
        machine.Cpu.PC = 0x5F00;
        ulong interrupts = machine.UlaTiming.InterruptCount;
        for (int step = 0; step < 100_000 && machine.UlaTiming.InterruptCount == interrupts; step++)
        {
            machine.StepInstruction();
        }

        machine.Cpu.PC = 0x6000;

        // The ULA is source 11: vector $B6.
        machine.MemoryBus.WriteCpu(0x7EB6, 0x00);
        machine.MemoryBus.WriteCpu(0x7EB7, 0x70);
        machine.MemoryBus.WriteCpu(0x7F00, 0x00);
        machine.MemoryBus.WriteCpu(0x7F01, 0x00);
        for (int offset = 0; offset < 0x4000; offset++)
        {
            machine.MemoryBus.WriteDirect((ushort)(0xC000 + offset), 0x5A);
            machine.MemoryBus.WriteDirect((ushort)(0x8000 + offset), 0x00);
        }

        ushort reti = (ushort)(0x7000 + isr.Bytes.Length - 2);
        ushort countAtEntry = 0;
        ushort countAtReti = 0;
        bool sawEntry = false;
        bool sawReti = false;
        bool activeAtEntry = false;
        bool resumedAfterReti = false;
        for (int step = 0; step < 200_000 && !resumedAfterReti; step++)
        {
            if (!sawEntry && machine.Cpu.PC == 0x7000)
            {
                sawEntry = true;
                activeAtEntry = machine.Dma.IsActive;
                countAtEntry = machine.Dma.ByteCounter;
            }

            bool atReti = sawEntry && !sawReti && machine.Cpu.PC == reti;
            var reads = new DmaTicksAtReads(machine);
            if (atReti)
            {
                sawReti = true;
                countAtReti = machine.Dma.ByteCounter;
                machine.Cpu.ConfigureBusObserver(reads);
            }

            machine.StepInstruction();
            if (atReti)
            {
                machine.Cpu.ConfigureBusObserver(null);
                // The RETI's reads: ED, 4D and the two stack reads, all before the DMA's next
                // byte (dma_delay holds it through the first; the grant follows the second).
                bool heldThroughReti = reads.Ticks.Count == 4 && reads.Ticks.All(t => t == reads.Ticks[0]);
                resumedAfterReti = !holdOff
                    || (heldThroughReti && machine.BusArbiter.DmaOwnedMasterTicks > reads.Ticks[0]);
            }
        }

        Require(sawEntry && sawReti, "The ULA's IM 2 handler never ran during the DMA test.");
        if (holdOff)
        {
            Require(activeAtEntry && countAtEntry > 0 && countAtEntry < 0x4000
                    && machine.MemoryBus.ReadDirect(0x7F00) == 0x00,
                "With $CC bit 0 the ULA interrupt did not break into the DMA transfer.");
            Require(countAtReti == countAtEntry,
                "The DMA moved while the interrupt it was held off for was in service.");
            Require(resumedAfterReti,
                "The DMA did not resume by the end of the handler's RETI.");
        }
        else
        {
            Require(!activeAtEntry && machine.MemoryBus.ReadDirect(0x7F00) == 0x5A,
                "Without $CC bit 0 the ULA interrupt broke into the DMA transfer.");
        }

        for (int step = 0; step < 200_000 && machine.Dma.IsActive; step++)
        {
            machine.StepInstruction();
        }

        Require(!machine.Dma.IsActive && machine.MemoryBus.ReadDirect(0xBFFF) == 0x5A
                && machine.MemoryBus.ReadDirect(0x8000) == 0x5A,
            "The DMA transfer did not complete after the interrupt.");
    }

    /// <summary>
    /// A hold-off already on when a transfer is enabled keeps START_DMA from asking for the bus;
    /// in burst mode the hold-off is seen when a prescaler wait ends.
    /// </summary>
    private static void VerifyIm2DmaHoldOffAtStartAndInBurst()
    {
        ZxNextMachine start = CreateDmaCopyMachine(out ushort source, out ushort destination);
        LoadSpinLoop(start);
        start.Registers.Write(0xC0, 0x01);
        start.Registers.Write(0xCC, 0x01);
        start.Registers.Write(0x20, 0x40);
        WriteDmaCommand(start, PacedCopy(source, destination, 16, 0xAD, 0, restart: false));
        StepInstructions(start, 200);
        Require(start.Dma.IsActive && CountCopiedBytes(start, source, destination) == 0,
            "A DMA enabled during a hold-off asked for the bus.");
        start.Registers.Write(0xC0, 0x00);
        StepInstructions(start, 200);
        Require(!start.Dma.IsActive && CountCopiedBytes(start, source, destination) == 16,
            "The DMA did not start once the hold-off ended.");

        ZxNextMachine burst = CreateDmaCopyMachine(out source, out destination);
        LoadSpinLoop(burst);
        burst.Registers.Write(0x22, 0x04);
        burst.Registers.Write(0xC0, 0x01);
        burst.Registers.Write(0xCC, 0x01);
        WriteDmaCommand(burst, PacedCopy(source, destination, 16, 0xCD, 100, restart: false));
        for (int step = 0; step < 10_000 && CountCopiedBytes(burst, source, destination) < 2; step++)
        {
            burst.StepInstruction();
        }

        burst.Registers.Write(0x20, 0x40);
        int held = CountCopiedBytes(burst, source, destination);
        StepInstructions(burst, 2000);
        Require(held >= 2 && burst.Dma.IsActive && CountCopiedBytes(burst, source, destination) == held,
            $"A burst transfer carried on through a hold-off after its prescaler wait (held {held}, now {CountCopiedBytes(burst, source, destination)}, active {burst.Dma.IsActive}).");
        burst.Registers.Write(0xC0, 0x00);
        for (int step = 0; step < 20_000 && burst.Dma.IsActive; step++)
        {
            burst.StepInstruction();
        }

        Require(CountCopiedBytes(burst, source, destination) == 16,
            "The burst transfer did not finish once the hold-off ended.");
    }

    /// <summary>The DMA's bus time at each CPU read, to see when the DMA came back.</summary>
    private sealed class DmaTicksAtReads(ZxNextMachine machine) : ZedExEss.Z80CPU.IZ80BusObserver
    {
        public List<ulong> Ticks { get; } = [];

        public void OnContend(ushort address, int cycles)
        {
        }

        public void OnRead(ushort address, byte value) => Ticks.Add(machine.BusArbiter.DmaOwnedMasterTicks);

        public void OnWrite(ushort address, byte value)
        {
        }

        public void OnPortContend(ushort port, int cycles)
        {
        }

        public void OnPortRead(ushort port, byte value)
        {
        }

        public void OnPortWrite(ushort port, byte value)
        {
        }
    }

    /// <summary>
    /// The Copper, from <c>device/copper.vhd</c>, its wiring in <c>zxnext.vhd</c> and cvc in
    /// <c>video/zxula_timing.vhd</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The write ticks were worked out by hand from copper.vhd. Started at tick T, the change of
    /// mode takes edge T+1 and instruction 0 runs at edge T+2. A NOP takes one edge, a MOVE two,
    /// and a MOVE run at edge E writes at E+2. Edge E sees the beam as it was during tick E-1, so
    /// a WAIT whose position starts at tick X is satisfied at edge X+1.
    /// </para>
    /// <para>
    /// The beam is tied to the line interrupt, which the timing profile pins on its own: the
    /// interrupt for line L comes when hc_ula is 255 on cvc line L-1, a pixel and half a clock
    /// later, so 1026 ticks after hc_ula 0 of that line. A WAIT matching at hc_ula h on that
    /// line, followed by a MOVE, therefore writes 1026 - 4h - 4 ticks before the interrupt.
    /// </para>
    /// </remarks>
    private static void VerifyCopper()
    {
        VerifyCopperRegisters();
        VerifyCopperInstructionTiming(batched: false);
        VerifyCopperInstructionTiming(batched: true);
        VerifyCopperWaits(0x90, false, "48K");
        VerifyCopperWaits(0x90, true, "48K 60 Hz");
        VerifyCopperWaits(0xB0, false, "+3");
        VerifyCopperWaits(0xC0, false, "Pentagon");
        VerifyCopperRestartAndLineOffset();
        VerifyCopperLineOffsetLatch();
        VerifyCopperModes();
        VerifyCopperEffectsReachTheCpu(batched: false);
        VerifyCopperEffectsReachTheCpu(batched: true);
        VerifyCopperNmiDuringDma(holdOff: true);
        VerifyCopperNmiDuringDma(holdOff: false);
        VerifyNmiRetnGivesTheDmaTheBus(stackless: false);
        VerifyNmiRetnGivesTheDmaTheBus(stackless: true);
        VerifyCopperRasterIndependentOfCpu();
        VerifyCopperSnapshot();
        VerifyCopperResets();
    }

    private const ushort CopperHalt = 0xFFFF;

    private static ushort CopperMove(int register, int value) =>
        (ushort)(((register & 0x7F) << 8) | (value & 0xFF));

    private static ushort CopperWait(int line, int h = 0) =>
        (ushort)(0x8000 | ((h & 0x3F) << 9) | (line & 0x1FF));

    /// <summary>
    /// A machine in the given timing with the CPU parked (by default DI; JR $ at $8000) and the
    /// Copper's writes logged with the ticks they land at.
    /// </summary>
    private static ZxNextMachine CreateCopperMachine(
        List<(ulong Tick, byte Register, byte Value)> log,
        bool batched = false,
        byte timing = 0xB0,
        bool sixtyHertz = false,
        byte[]? program = null)
    {
        ZxNextMachine machine = CreateProgramAt(0x8000, program ?? [0xF3, 0x18, 0xFE]);
        machine.Cpu.SP = 0x7000;
        machine.Registers.Write(0x22, 0x04);
        machine.Registers.Write(0x03, timing);
        machine.Registers.Write(0x05, (byte)(sixtyHertz ? 0x04 : 0x00));
        machine.UlaTiming.SynchronizeProfile();
        if (batched)
        {
            machine.Registers.Write(0x6B, 0x80);
            Require(machine.Cpu.InstructionTstateBatchingEnabled,
                "The tilemap did not turn instruction batching on for the Copper checks.");
        }

        machine.Registers.CopperWritten = (register, value, tick) => log.Add((tick, register, value));
        return machine;
    }

    /// <summary>Stops the Copper and writes <paramref name="list"/> from instruction 0 with $60.</summary>
    private static void UploadCopper(ZxNextMachine machine, params ushort[] list)
    {
        machine.SynchronizeTime();
        machine.Registers.Write(0x62, 0x00);
        machine.Registers.Write(0x61, 0x00);
        foreach (ushort instruction in list)
        {
            machine.Registers.Write(0x60, (byte)(instruction >> 8));
            machine.Registers.Write(0x60, (byte)instruction);
        }
    }

    /// <summary>Starts the Copper in <paramref name="mode"/> just after a frame begins; the tick it was started at.</summary>
    private static ulong StartCopperAtFrame(ZxNextMachine machine, byte mode)
    {
        machine.RunFrame();
        machine.SynchronizeTime();
        ulong start = machine.Clock.MasterTicks;
        machine.Registers.Write(0x62, mode);
        return start;
    }

    private static void RunFrames(ZxNextMachine machine, int frames)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            machine.RunFrame();
        }
    }

    private static string DescribeCopperLog(List<(ulong Tick, byte Register, byte Value)> log, ulong start) =>
        log.Count == 0
            ? "no writes"
            : string.Join(", ", log.Select(entry => $"+{(long)(entry.Tick - start)} ${entry.Register:X2}=${entry.Value:X2}"));

    /// <summary>$1E/$1F as one 9-bit line: cvc.</summary>
    private static int CopperLine(ZxNextMachine machine) =>
        ((machine.Registers.Read(0x1E) & 0x01) << 8) | machine.Registers.Read(0x1F);

    /// <summary>Where <paramref name="tick"/> falls in its frame.</summary>
    private static ulong FramePhase(ZxNextMachine machine, ulong tick)
    {
        ulong frame = machine.UlaTiming.MasterTicksPerFrame;
        return (tick + frame - (machine.UlaTiming.FrameStartMasterTick % frame)) % frame;
    }

    private static ulong LineInterruptPhase(ZxNextMachine machine, int line) =>
        machine.UlaTiming.Profile.GetLineInterruptOffsetMasterTicks((ushort)line)
            ?? throw new InvalidOperationException($"Line {line} has no line interrupt.");

    private static void VerifyCopperRegisters()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextRegisterFile registers = machine.Registers;
        ZxNextCopperDevice copper = registers.Copper;
        Require(registers.Read(0x61) == 0 && registers.Read(0x62) == 0 && registers.Read(0x64) == 0
                && !copper.IsActive,
            "The Copper did not come up stopped with $61, $62 and $64 at 0.");
        registers.Write(0x61, 0x34);
        registers.Write(0x62, 0x05);
        Require(registers.Read(0x61) == 0x34 && registers.Read(0x62) == 0x05,
            "$61/$62 did not read back the write address and mode.");
        registers.Write(0x62, 0x3F);
        Require(registers.Read(0x62) == 0x07, "$62 stored bits 5-3, which zxnext.vhd reads as 0.");

        // $60 at an odd address writes the low byte, and the address carries into bits 10-8.
        registers.Write(0x62, 0x01);
        registers.Write(0x61, 0xFF);
        registers.Write(0x60, 0x12);
        Require(copper.ReadInstruction(0xFF) == 0x0012 && registers.Read(0x61) == 0x00 && registers.Read(0x62) == 0x02,
            "$60 at address $1FF did not write instruction $FF's low byte and carry into bits 10-8.");

        // $63: an even address only stores the byte; the odd one writes both.
        registers.Write(0x63, 0xAB);
        Require(copper.ReadInstruction(0x100) == 0x0000,
            "$63 at an even address wrote the list; it only stores the byte.");
        registers.Write(0x63, 0xCD);
        Require(copper.ReadInstruction(0x100) == 0xABCD && registers.Read(0x61) == 0x02 && registers.Read(0x62) == 0x02,
            "$63 at the odd address did not write the stored byte with it, or did not move the address on.");

        // $60 at an even address writes the high byte at once.
        registers.Write(0x60, 0x7E);
        Require(copper.ReadInstruction(0x101) == 0x7E00,
            "$60 at an even address did not write the high byte at once.");

        // The write address wraps from $7FF to 0.
        registers.Write(0x61, 0xFE);
        registers.Write(0x62, 0x07);
        registers.Write(0x60, 0x11);
        registers.Write(0x60, 0x22);
        registers.Write(0x60, 0x33);
        Require(copper.ReadInstruction(0x3FF) == 0x1122 && copper.ReadInstruction(0) == 0x3300
                && registers.Read(0x61) == 0x01 && registers.Read(0x62) == 0x00,
            "The write address did not wrap from $7FF (instruction 1023) to 0.");

        registers.Write(0x62, 0xC0);
        registers.Write(0x64, 0xA5);
        Require(registers.Read(0x62) == 0xC0 && registers.Read(0x64) == 0xA5,
            "$62 did not read back mode 11, or $64 did not read back.");
    }

    private static void VerifyCopperInstructionTiming(bool batched)
    {
        string label = batched ? " (with batched CPU time)" : "";
        var log = new List<(ulong Tick, byte Register, byte Value)>();
        ZxNextMachine machine = CreateCopperMachine(log, batched);
        UploadCopper(machine,
            CopperMove(0x7F, 1), CopperMove(0x7F, 2), 0x0000, 0x0000, 0x0000, CopperMove(0x7F, 3), CopperHalt);
        ulong start = StartCopperAtFrame(machine, 0x40);
        RunFrames(machine, 2);
        Require(log.Count == 3
                && log[0] == (start + 4, (byte)0x7F, (byte)1)
                && log[1] == (start + 6, (byte)0x7F, (byte)2)
                && log[2] == (start + 11, (byte)0x7F, (byte)3)
                && machine.Registers.Read(0x7F) == 3,
            $"MOVE, MOVE, three NOPs, MOVE did not write at +4, +6 and +11{label}: {DescribeCopperLog(log, start)}.");

        // A rewritten list is run as written: two NOPs then a MOVE, then one NOP then a MOVE.
        foreach ((ushort[] list, ulong at, byte value) in new[]
                 {
                     (new ushort[] { 0x0000, 0x0000, CopperMove(0x7F, 4), CopperHalt }, 6UL, (byte)4),
                     (new ushort[] { 0x0000, CopperMove(0x7F, 5), CopperHalt }, 5UL, (byte)5)
                 })
        {
            log.Clear();
            UploadCopper(machine, list);
            start = StartCopperAtFrame(machine, 0x40);
            RunFrames(machine, 1);
            Require(log.Count == 1 && log[0] == (start + at, (byte)0x7F, value),
                $"A list rewritten with {Array.IndexOf(list, CopperMove(0x7F, value))} NOPs before its MOVE did not " +
                $"write at +{at}{label}: {DescribeCopperLog(log, start)}.");
        }
    }

    private static void VerifyCopperWaits(byte timing, bool sixtyHertz, string name)
    {
        var log = new List<(ulong Tick, byte Register, byte Value)>();
        ZxNextMachine machine = CreateCopperMachine(log, timing: timing, sixtyHertz: sixtyHertz);
        ZxNextUlaTiming beam = machine.UlaTiming;
        int lines = beam.LinesPerFrame;
        int lastH = (beam.PixelsPerLine - 1 - 12) / 8;

        // (WAIT line, H, the line interrupt it is tied to, hc_ula it matches at)
        (int Line, int H, int Anchor, int Hc)[] matching =
        [
            (0, 30, 1, 252),
            (100, 30, 101, 252),
            (lines - 1, 30, lines, 252),
            (100, 0, 101, 12),
            (100, 63, 101, 4),
            (100, lastH, 101, (lastH * 8) + 12)
        ];
        foreach ((int line, int h, int anchor, int hc) in matching)
        {
            log.Clear();
            UploadCopper(machine, CopperWait(line, h), CopperMove(0x7F, 0x5A), CopperHalt);
            ulong start = StartCopperAtFrame(machine, 0x40);
            ulong frame = beam.MasterTicksPerFrame;
            ulong expected = (ulong)((long)beam.FrameStartMasterTick + (long)LineInterruptPhase(machine, anchor)
                - (1026 - (4 * hc) - 4));
            while (expected < start + 4)
            {
                expected += frame;
            }

            RunFrames(machine, 2);
            Require(log.Count == 1 && log[0].Tick == expected,
                $"{name}: WAIT line {line}, H {h} (hc_ula {hc}) then MOVE did not write {1026 - (4 * hc) - 4} ticks " +
                $"before line {anchor}'s interrupt (expected +{expected - start}): {DescribeCopperLog(log, start)}.");
        }

        // A WAIT reached after its position in the line matches on the next edge: hc_ula >= h * 8 + 12.
        log.Clear();
        UploadCopper(machine, CopperWait(100, 30), CopperMove(0x7F, 1), CopperWait(100, 0), CopperMove(0x7F, 2), CopperHalt);
        ulong passedStart = StartCopperAtFrame(machine, 0x40);
        RunFrames(machine, 2);
        Require(log.Count == 2 && log[1].Tick == log[0].Tick + 3,
            $"{name}: a WAIT for hc_ula 12 reached at hc_ula 252 of its line did not match at once: " +
            $"{DescribeCopperLog(log, passedStart)}.");

        (int Line, int H)[] never = [(lines, 0), (400, 0), (100, lastH + 1)];
        foreach ((int line, int h) in never)
        {
            log.Clear();
            UploadCopper(machine, CopperWait(line, h), CopperMove(0x7F, 0x5A), CopperHalt);
            ulong start = StartCopperAtFrame(machine, 0x40);
            RunFrames(machine, 3);
            Require(log.Count == 0,
                $"{name}: WAIT line {line}, H {h} matched; cvc stops at {lines - 1} and hc_ula at {beam.PixelsPerLine - 1}: " +
                $"{DescribeCopperLog(log, start)}.");
        }

        // FIX-INT-1: a line interrupt for line LinesPerFrame compares with c_max_vc, as line 0 does.
        machine.Registers.Write(0x23, (byte)lines);
        machine.Registers.Write(0x22, (byte)(0x06 | (lines >> 8)));
        RunFrames(machine, 2);
        ulong last = beam.LastLineInterruptMasterTick;
        Require(beam.LineInterruptCount > 0
                && FramePhase(machine, last) == LineInterruptPhase(machine, lines)
                && LineInterruptPhase(machine, lines) == LineInterruptPhase(machine, 0),
            $"{name}: a line interrupt for line {lines} did not come where line 0's does (c_max_vc).");
        machine.Registers.Write(0x22, 0x04);
    }

    /// <summary>
    /// Mode 11 restarts at cvc 0, hc_ula 0: the four edges seeing it reset the address, the next
    /// runs instruction 0, so a MOVE there writes 1026 - 7 = 1019 ticks before line 1's interrupt.
    /// With $64 = 200 that is in the middle of the paper, and the line interrupt moves with it.
    /// </summary>
    private static void VerifyCopperRestartAndLineOffset()
    {
        var log = new List<(ulong Tick, byte Register, byte Value)>();
        ZxNextMachine machine = CreateCopperMachine(log, timing: 0x90);
        ZxNextUlaTiming beam = machine.UlaTiming;
        int lines = beam.LinesPerFrame;
        machine.Registers.Write(0x64, 200);
        machine.Registers.Write(0x23, 0x01);
        machine.Registers.Write(0x22, 0x06);
        UploadCopper(machine, CopperMove(0x7F, 0x11), CopperHalt);
        ulong start = StartCopperAtFrame(machine, 0xC0);
        RunFrames(machine, 3);

        ulong cvcZero = LineInterruptPhase(machine, lines - 200 + 1);
        ulong expected = cvcZero - 1019;
        List<(ulong Tick, byte Register, byte Value)> restarts = log.Skip(1).ToList();
        Require(log.Count >= 3 && log[0].Tick == start + 4
                && restarts.All(entry => FramePhase(machine, entry.Tick) == expected)
                && restarts.Zip(restarts.Skip(1)).All(pair => pair.Second.Tick - pair.First.Tick == beam.MasterTicksPerFrame),
            $"Mode 11 with $64 = 200 did not restart once a frame 1019 ticks before cvc 0's line interrupt " +
            $"(phase {expected}): {string.Join(", ", log.Select(entry => FramePhase(machine, entry.Tick)))}.");
        Require(beam.LineInterruptCount > 0 && FramePhase(machine, beam.LastLineInterruptMasterTick) == cvcZero,
            "With $64 = 200 the line interrupt for line 1 did not come where cvc is 0.");

        // A new offset moves the restart from the next reload on.
        log.Clear();
        machine.Registers.Write(0x64, 100);
        RunFrames(machine, 3);
        ulong moved = LineInterruptPhase(machine, lines - 100 + 1) - 1019;
        Require(log.Count >= 2 && log.All(entry => FramePhase(machine, entry.Tick) == moved),
            $"After $64 = 100 the mode 11 restart did not move to phase {moved}: " +
            $"{string.Join(", ", log.Select(entry => FramePhase(machine, entry.Tick)))}.");

        // A WAIT already waiting when $64 changes matches the new count after the reload: WAIT
        // line 100 with $64 = $20 is where cvc 68 would be without it (hc_ula 12: 974 ticks
        // before line 69's interrupt).
        log.Clear();
        machine.Registers.Write(0x64, 0x00);
        RunFrames(machine, 1);
        UploadCopper(machine, CopperWait(100), CopperMove(0x7F, 0x22), CopperHalt);
        machine.RunFrame();
        ulong pastLine100 = machine.UlaTiming.FrameStartMasterTick + LineInterruptPhase(machine, 151);
        while (machine.Clock.MasterTicks < pastLine100)
        {
            machine.StepInstruction();
        }

        // Started past this frame's line 100, it waits for the next frame's and looks ahead.
        log.Clear();
        machine.Registers.Write(0x62, 0x40);
        for (int instruction = 0; instruction < 10; instruction++)
        {
            machine.StepInstruction();
        }

        Require(log.Count == 0, "The WAIT for line 100 matched again in the frame it had passed.");
        machine.Registers.Write(0x64, 0x20);
        RunFrames(machine, 2);
        ulong shifted = LineInterruptPhase(machine, 69) - 974;
        Require(log.Count == 1 && FramePhase(machine, log[0].Tick) == shifted,
            $"A WAIT for line 100 waiting across a $64 = $20 write did not match where cvc 68 is without it " +
            $"(phase {shifted}): {string.Join(", ", log.Select(entry => FramePhase(machine, entry.Tick)))}.");
    }

    /// <summary>
    /// COP-016: $64 is loaded into cvc only at the first display line, so a write after it
    /// changes nothing until the next frame's, and a write before it shows from this frame's.
    /// Two machines run the same instructions in step; one writes $64.
    /// </summary>
    private static void VerifyCopperLineOffsetLatch()
    {
        ZxNextMachine reference = CreateCopperMachine([]);
        ZxNextMachine written = CreateCopperMachine([]);
        int lines = reference.UlaTiming.LinesPerFrame;
        foreach (ZxNextMachine machine in new[] { reference, written })
        {
            machine.Registers.Write(0x23, 150);
            machine.Registers.Write(0x22, 0x06);
            machine.RunFrame();
        }

        void StepBoth()
        {
            reference.StepInstruction();
            written.StepInstruction();
        }

        void RunBothToPhase(ulong phase)
        {
            ulong target = reference.UlaTiming.FrameStartMasterTick + phase;
            while (reference.Clock.MasterTicks < target)
            {
                StepBoth();
            }
        }

        void RunBothFrame()
        {
            reference.RunFrame();
            written.RunFrame();
        }

        // After the reload: this frame's count and line interrupt are untouched.
        ulong line100 = LineInterruptPhase(reference, 101);
        RunBothToPhase(line100);
        written.Registers.Write(0x64, 0x20);
        Require(CopperLine(written) == CopperLine(reference) && written.Registers.Read(0x64) == 0x20,
            "A $64 write after the first display line moved cvc at once.");
        ulong referenceInterrupts = reference.UlaTiming.LineInterruptCount;
        while (reference.UlaTiming.LineInterruptCount == referenceInterrupts)
        {
            StepBoth();
        }

        Require(written.UlaTiming.LastLineInterruptMasterTick == reference.UlaTiming.LastLineInterruptMasterTick,
            "A $64 write after the reload moved this frame's line interrupt.");

        // The next frame counts from the new offset.
        RunBothFrame();
        RunBothToPhase(line100);
        Require(reference.Clock.MasterTicks == written.Clock.MasterTicks
                && CopperLine(written) == (CopperLine(reference) + 0x20) % lines,
            $"The next frame's cvc did not count from $64 = $20 ({CopperLine(written)} against {CopperLine(reference)}).");
        referenceInterrupts = reference.UlaTiming.LineInterruptCount;
        ulong writtenInterrupts = written.UlaTiming.LineInterruptCount;
        while (reference.UlaTiming.LineInterruptCount == referenceInterrupts
               || written.UlaTiming.LineInterruptCount == writtenInterrupts)
        {
            StepBoth();
        }

        Require(FramePhase(reference, reference.UlaTiming.LastLineInterruptMasterTick) == LineInterruptPhase(reference, 150)
                && FramePhase(written, written.UlaTiming.LastLineInterruptMasterTick) == LineInterruptPhase(written, 150 - 0x20),
            "With $64 = $20 latched, the line interrupt for line 150 did not move to where line 118's is without it.");

        // Before the reload: the old offset counts until the first display line.
        RunBothFrame();
        written.Registers.Write(0x64, 0x40);
        Require(CopperLine(written) == (CopperLine(reference) + 0x20) % lines,
            "A $64 write before the first display line changed the lines still counting from the old offset.");
        RunBothToPhase(line100);
        Require(CopperLine(written) == (CopperLine(reference) + 0x40) % lines,
            "A $64 write before the first display line did not show from that line.");
    }

    private static void VerifyCopperModes()
    {
        var log = new List<(ulong Tick, byte Register, byte Value)>();

        byte RunMode(ZxNextMachine machine, byte mode)
        {
            machine.SynchronizeTime();
            machine.Registers.Write(0x7F, 0x33);
            machine.Registers.Write(0x62, mode);
            RunFrames(machine, 2);
            return machine.Registers.Read(0x7F);
        }

        ZxNextMachine everyFrame = CreateCopperMachine(log);
        UploadCopper(everyFrame, CopperMove(0x7F, 0x5A), CopperHalt);
        Require(RunMode(everyFrame, 0xC0) == 0x5A && RunMode(everyFrame, 0xC0) == 0x5A,
            "Mode 11 did not run the list again in the next frame.");

        ZxNextMachine once = CreateCopperMachine(log);
        UploadCopper(once, CopperMove(0x7F, 0x5A), CopperHalt);
        once.Registers.Write(0x62, 0x40);
        RunFrames(once, 2);
        Require(once.Registers.Read(0x7F) == 0x5A, "Mode 01 did not run the list.");
        Require(RunMode(once, 0x40) == 0x33, "Rewriting mode 01 restarted the list.");
        Require(RunMode(once, 0x80) == 0x33, "01 to 10 restarted the list instead of continuing at the HALT.");
        Require(RunMode(once, 0x80) == 0x33, "Rewriting mode 10 restarted the list.");
        Require(RunMode(once, 0x40) == 0x5A, "10 to 01 did not restart the list.");
        Require(RunMode(once, 0x00) == 0x33, "Mode 00 did not stop the Copper.");
        Require(RunMode(once, 0x80) == 0x33, "00 to 10 restarted the list.");
        Require(RunMode(once, 0xC0) == 0x5A, "10 to 11 did not restart the list.");
        Require(RunMode(once, 0x40) == 0x5A, "11 to 01 did not restart the list.");

        // COP-011: MOVE $62,0 lands two edges later, after the next MOVE has run, so that MOVE's
        // write goes out and the one after it does not run; mode 10 continues there.
        log.Clear();
        ZxNextMachine stop = CreateCopperMachine(log);
        UploadCopper(stop, CopperMove(0x62, 0x00), CopperMove(0x7F, 0x5A), CopperMove(0x4A, 0x5A), CopperHalt);
        stop.Registers.Write(0x7F, 0xE3);
        stop.Registers.Write(0x4A, 0xE3);
        ulong start = StartCopperAtFrame(stop, 0x40);
        RunFrames(stop, 2);
        Require(log.Count == 2 && log[0] == (start + 4, (byte)0x62, (byte)0) && log[1] == (start + 6, (byte)0x7F, (byte)0x5A)
                && stop.Registers.Read(0x62) >> 6 == 0 && stop.Registers.Read(0x4A) == 0xE3,
            $"A Copper MOVE to $62 did not stop it after exactly one more MOVE: {DescribeCopperLog(log, start)}.");
        stop.Registers.Write(0x62, 0x80);
        RunFrames(stop, 1);
        Require(stop.Registers.Read(0x4A) == 0x5A, "Mode 10 did not continue after the Copper had stopped itself.");
    }

    /// <summary>
    /// A Copper write lands under a running CPU: a MOVE to MMU slot 6 is seen by the next read,
    /// and a MOVE $02,$04 raises the DivMMC NMI at the end of the instruction it lands in (the
    /// way Atic Atac Next paces its streaming). With batched CPU time the boundary deadline has
    /// to include the Copper's next write for either to come in time.
    /// </summary>
    private static void VerifyCopperEffectsReachTheCpu(bool batched)
    {
        string label = batched ? " with batched CPU time" : "";

        // DI; Loop: LD A,($C000); CP $5A; JR NZ,Loop; HALT
        var log = new List<(ulong Tick, byte Register, byte Value)>();
        ZxNextMachine mmu = CreateCopperMachine(log, batched,
            program: [0xF3, 0x3A, 0x00, 0xC0, 0xFE, 0x5A, 0x20, 0xF9, 0x76]);
        mmu.Registers.Write(0x56, 10);
        mmu.MemoryBus.WriteCpu(0xC000, 0x5A);
        mmu.Registers.Write(0x56, 0);
        mmu.MemoryBus.WriteCpu(0xC000, 0x11);
        UploadCopper(mmu, CopperWait(100), CopperMove(0x56, 10), CopperHalt);
        StartCopperAtFrame(mmu, 0x40);
        int steps = 0;
        while (!mmu.Cpu.IsHalted && steps++ < 200_000)
        {
            mmu.StepInstruction();
        }

        mmu.SynchronizeTime();
        Require(mmu.Cpu.IsHalted && log.Count == 1 && log[0].Register == 0x56
                && mmu.Clock.MasterTicks > log[0].Tick && mmu.Clock.MasterTicks - log[0].Tick <= 700,
            $"The CPU did not see the Copper's MMU write within two passes of its loop{label} " +
            $"(write at {(log.Count > 0 ? log[0].Tick : 0)}, HALT at {mmu.Clock.MasterTicks}).");

        // DI; JR $, with the DivMMC NMI enabled ($06 bit 4) and not.
        foreach (bool enabled in new[] { true, false })
        {
            log.Clear();
            ZxNextMachine nmi = CreateCopperMachine(log, batched);
            nmi.Registers.Write(0x06, (byte)(enabled ? 0xB0 : 0xA0));
            UploadCopper(nmi, CopperWait(100), CopperMove(0x02, 0x04), CopperHalt);
            StartCopperAtFrame(nmi, 0x40);
            steps = 0;
            while (nmi.Cpu.PC != 0x0066 && steps++ < 10_000)
            {
                nmi.StepInstruction();
            }

            nmi.SynchronizeTime();
            if (enabled)
            {
                Require(nmi.Cpu.PC == 0x0066 && log.Count == 1 && log[0] == (log[0].Tick, (byte)0x02, (byte)0x04)
                        && nmi.Clock.MasterTicks > log[0].Tick && nmi.Clock.MasterTicks - log[0].Tick <= 400
                        && (nmi.Registers.Read(0x02) & 0x04) != 0,
                    $"A Copper MOVE $02,$04 did not take the CPU to $0066 straight after it landed{label} " +
                    $"(write at {(log.Count > 0 ? log[0].Tick : 0)}, $0066 at {nmi.Clock.MasterTicks}).");
            }
            else
            {
                Require(nmi.Cpu.PC != 0x0066 && log.Count == 1,
                    $"A Copper MOVE $02,$04 raised the DivMMC NMI with $06 bit 4 clear{label}.");
            }
        }
    }

    /// <summary>
    /// A Copper NMI landing while the DMA holds the bus, as Atic Atac Next's do: the CPU takes
    /// it only with the bus back and at the end of an instruction. With $CC bit 7 the NMI holds
    /// the DMA off and breaks into the transfer; without it the NMI waits for the transfer to
    /// end. Either way the stacked return address is an instruction's.
    /// </summary>
    /// <remarks>
    /// The DMA is enabled by the last OUT of an OTIR, so it takes the bus inside an instruction
    /// and runs between that instruction's machine cycles.
    /// </remarks>
    private static void VerifyCopperNmiDuringDma(bool holdOff)
    {
        string label = holdOff ? "with $CC bit 7" : "without $CC bit 7";
        var log = new List<(ulong Tick, byte Register, byte Value)>();
        ZxNextMachine machine = CreateCopperMachine(log);
        machine.Registers.Write(0x56, 0x20);
        machine.Registers.Write(0x57, 0x21);
        machine.Registers.Write(0x06, 0xB0);
        machine.Registers.Write(0xCC, (byte)(holdOff ? 0x80 : 0x00));

        // $9000 DI; LD SP,$7000; LD HL,$9014; LD BC,$0D6B; OTIR
        // $900C Loop: LD A,($C000); LD ($7F80),A; JR Loop
        // $9014 an 8K continuous copy from $C000 to $E000 on $6B.
        byte[] program =
        [
            0xF3, 0x31, 0x00, 0x70, 0x21, 0x14, 0x90, 0x01, 0x6B, 0x0D, 0xED, 0xB3,
            0x3A, 0x00, 0xC0, 0x32, 0x80, 0x7F, 0x18, 0xF8,
            0xC3, 0x7D, 0x00, 0xC0, 0x00, 0x20, 0x14, 0x10, 0xAD, 0x00, 0xE0, 0xCF, 0x87
        ];
        ushort[] starts = [0x9000, 0x9001, 0x9004, 0x9007, 0x900A, 0x900C, 0x900F, 0x9012];
        WriteBytes(machine, 0x9000, program);

        // The NMI comes at cvc 0, the first display line, well inside the transfer.
        UploadCopper(machine, CopperWait(0), CopperMove(0x02, 0x04), CopperHalt);
        StartCopperAtFrame(machine, 0x40);
        machine.Cpu.PC = 0x9000;
        bool dmaRan = false;
        int steps = 0;
        while (machine.Cpu.PC != 0x0066 && steps++ < 100_000)
        {
            machine.StepInstruction();
            dmaRan |= machine.Dma.IsActive;
        }

        ushort returnAddress = (ushort)(machine.MemoryBus.ReadDirect(0x6FFE) | (machine.MemoryBus.ReadDirect(0x6FFF) << 8));
        Require(machine.Cpu.PC == 0x0066 && dmaRan && log.Count == 1
                && machine.BusArbiter.Owner == ZxNextBusOwner.Cpu
                && machine.Cpu.SP == 0x6FFE && starts.Contains(returnAddress),
            $"A Copper NMI during a DMA transfer {label} was not taken with the bus back and at an instruction's end " +
            $"(PC ${machine.Cpu.PC:X4}, return ${returnAddress:X4}, owner {machine.BusArbiter.Owner}).");
        Require(machine.Dma.IsActive == holdOff,
            holdOff
                ? "With $CC bit 7 the Copper's NMI did not break into the DMA transfer."
                : "Without $CC bit 7 the Copper's NMI broke into the DMA transfer.");
    }

    /// <summary>
    /// Mid-line palette changes by the Copper paint the same picture whatever the CPU runs:
    /// each write reaches the renderer at its own tick, not where the CPU's time happened to be
    /// published. A NOP loop and an EX (SP),HL loop publish at different machine cycles.
    /// </summary>
    private static void VerifyCopperRasterIndependentOfCpu()
    {
        int[] Render(byte[] loop)
        {
            ZxNextMachine machine = CreateCopperMachine([], program: loop);
            machine.Registers.Write(0x43, 0x00);
            machine.Registers.Write(0x40, 0x10);
            machine.Registers.Write(0x41, 0x00);
            for (ushort address = 0x4000; address < 0x5B00; address++)
            {
                machine.MemoryBus.WriteCpu(address, 0x00);
            }

            // PAPER 0 and BORDER 0 are entry 16: red from line 100 at hc_ula 140, green from
            // line 120 at hc_ula 60, blue from line 140 at hc_ula 284, black again at cvc 0.
            UploadCopper(machine,
                CopperMove(0x40, 0x10), CopperMove(0x41, 0x00),
                CopperWait(100, 16), CopperMove(0x40, 0x10), CopperMove(0x41, 0xE0),
                CopperWait(120, 6), CopperMove(0x40, 0x10), CopperMove(0x41, 0x1C),
                CopperWait(140, 34), CopperMove(0x40, 0x10), CopperMove(0x41, 0x03),
                CopperHalt);
            StartCopperAtFrame(machine, 0xC0);
            RunFrames(machine, 3);
            var frame = new int[machine.FrameWidth * machine.FrameHeight];
            machine.TryCopyFrame(frame);
            return frame;
        }

        int[] nops = Render([0xF3, 0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0xF9]);
        int[] exchanges = Render([0xF3, 0xE3, 0xE3, 0xE3, 0x18, 0xFB]);
        int colours = nops.Distinct().Count();
        int differing = nops.Zip(exchanges).Count(pair => pair.First != pair.Second);
        Require(colours >= 4 && differing == 0,
            $"The Copper's mid-line palette changes painted a different picture under a different CPU loop " +
            $"({differing} pixels differ, {colours} colours).");
    }

    /// <summary>
    /// Copper NMIs holding a long DMA transfer off ($CC bit 7), as Atic Atac Next's sample player
    /// does: the program the DMA interrupted does not move until the transfer ends.
    /// </summary>
    /// <remarks>
    /// divmmc.vhd lets the NMI go at the RETN's opcode fetch (<c>retn_seen</c>), so the DMA asks
    /// for the bus again during the RETN and is granted at the end of one of its stack reads.
    /// The CPU finishes the RETN only after the next NMI has held the DMA off again, and takes
    /// that NMI at once. A stackless RETN runs the same two read cycles with $C2/$C3 in place
    /// of the data. Without them the program crept on an instruction per NMI and called code
    /// the transfer had not yet written.
    /// </remarks>
    private static void VerifyNmiRetnGivesTheDmaTheBus(bool stackless)
    {
        string label = stackless ? "a stackless RETN" : "a RETN";
        ZxNextMachine machine = CreateCopperMachine([]);
        machine.Registers.Write(0x56, 0x20);
        machine.Registers.Write(0x57, 0x21);
        machine.Registers.Write(0x06, 0xB0);
        machine.Registers.Write(0x0A, 0x11);
        machine.Registers.Write(0xB8, 0x00);
        machine.Registers.Write(0xCC, 0x80);
        machine.Registers.Write(0xC0, (byte)(stackless ? 0x08 : 0x00));

        // The handler, in DivMMC ROM after the $0066 fetch pages it in: RETN.
        var divMmcRom = new byte[ZxNextPhysicalMemory.PageSize];
        divMmcRom[0x0067] = 0xED;
        divMmcRom[0x0068] = 0x45;
        machine.MemoryBus.LoadDivMmcRom(divMmcRom);

        // $9000 DI; LD SP,$7000; LD HL,$9014; LD BC,$0D6B; OTIR
        // $900C Loop: INC A; JR Loop (padded to $9014), then the 8K continuous copy.
        byte[] program =
        [
            0xF3, 0x31, 0x00, 0x70, 0x21, 0x14, 0x90, 0x01, 0x6B, 0x0D, 0xED, 0xB3,
            0x3C, 0x18, 0xFD, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xC3, 0x7D, 0x00, 0xC0, 0x00, 0x20, 0x14, 0x10, 0xAD, 0x00, 0xE0, 0xCF, 0x87
        ];
        WriteBytes(machine, 0x9000, program);

        // MOVE $02,$04 and 1023 NOPs: an NMI every 1024 ticks.
        UploadCopper(machine, CopperMove(0x02, 0x04));
        StartCopperAtFrame(machine, 0x40);
        machine.Cpu.PC = 0x9000;
        int nmis = 0;
        int crept = 0;
        bool dmaSeen = false;
        for (int step = 0; step < 400_000 && !(dmaSeen && !machine.Dma.IsActive); step++)
        {
            ushort pc = machine.Cpu.PC;
            if (pc == 0x0066)
            {
                nmis++;
            }
            else if (nmis > 0 && machine.Dma.IsActive && pc is 0x900C or 0x900D)
            {
                crept++;
            }

            dmaSeen |= machine.Dma.IsActive;
            machine.StepInstruction();
        }

        Require(dmaSeen && !machine.Dma.IsActive && nmis > 50 && crept == 0,
            $"With Copper NMIs holding an 8K DMA transfer off, the program moved on {crept} times through {label} " +
            $"({nmis} NMIs, transfer {(machine.Dma.IsActive ? "unfinished" : "done")}).");
    }

    /// <summary>
    /// A snapshot taken with a Copper write in flight and a $64 write not yet latched carries
    /// on exactly as the machine it was taken from, through the file format.
    /// </summary>
    private static void VerifyCopperSnapshot()
    {
        var log = new List<(ulong Tick, byte Register, byte Value)>();
        ZxNextMachine original = CreateCopperMachine(log);
        var list = new List<ushort> { CopperWait(10), CopperMove(0x7F, 1), CopperWait(20, 5) };
        for (int index = 0; index < 100; index++)
        {
            list.Add(CopperMove(0x7F, index));
        }

        list.Add(CopperWait(30, 40));
        list.Add(CopperMove(0x7F, 0xEE));
        list.Add(CopperHalt);
        UploadCopper(original, list.ToArray());
        StartCopperAtFrame(original, 0xC0);
        RunFrames(original, 1);

        // After the reload (cvc 5), so the $64 write waits for the next frame's.
        ulong afterReload = original.UlaTiming.FrameStartMasterTick + LineInterruptPhase(original, 6);
        while (original.Clock.MasterTicks < afterReload)
        {
            original.StepInstruction();
        }

        original.Registers.Write(0x64, 0x30);
        int steps = 0;
        while (!original.Registers.Copper.CaptureState().HasWrite0 && steps++ < 10_000)
        {
            original.StepInstruction();
        }

        Require(original.Registers.Copper.CaptureState().HasWrite0,
            "The snapshot check never caught a Copper write in flight.");
        ZxNextMachineSnapshot captured = ZxNextMachineSnapshotService.Capture(original);
        using var stream = new MemoryStream();
        ZxNextSnapshotFile.Save(stream, captured);
        stream.Position = 0;
        ZxNextMachineSnapshot loaded = ZxNextSnapshotFile.Load(stream);

        var restoredLog = new List<(ulong Tick, byte Register, byte Value)>();
        ZxNextMachine restored = CreateCopperMachine(restoredLog);
        ZxNextMachineSnapshotService.Restore(restored, loaded);
        Require(restored.UlaTiming.CopperLineOffset == 0x30 && restored.UlaTiming.ActiveCopperLineOffset == 0
                && CopperLine(restored) == CopperLine(original),
            "The restore did not bring back $64 = $30 waiting for the reload, with cvc still on the old count.");

        int captureIndex = log.Count;
        for (int instruction = 0; instruction < 20_000; instruction++)
        {
            original.StepInstruction();
            restored.StepInstruction();
        }

        List<(ulong Tick, byte Register, byte Value)> after = log.Skip(captureIndex).ToList();
        ZxNextCopperState left = original.Registers.Copper.CaptureState();
        ZxNextCopperState right = restored.Registers.Copper.CaptureState();
        Require(after.Count > 100 && after.SequenceEqual(restoredLog)
                && left.List.SequenceEqual(right.List) && left with { List = right.List } == right
                && CopperLine(restored) == CopperLine(original),
            $"The restored Copper did not carry on as the original: {after.Count} writes against {restoredLog.Count}, " +
            $"PC {left.ProgramCounter} against {right.ProgramCounter}.");
    }

    private static void VerifyCopperResets()
    {
        var log = new List<(ulong Tick, byte Register, byte Value)>();
        ZxNextMachine machine = CreateCopperMachine(log);
        UploadCopper(machine, CopperMove(0x7F, 0x5A), CopperWait(50), CopperHalt);
        machine.Registers.Write(0x64, 0x10);
        machine.Registers.Write(0x62, 0xC0);
        RunFrames(machine, 1);
        machine.Reset(ZxNextResetKind.Soft);
        ZxNextCopperDevice copper = machine.Registers.Copper;
        Require(!copper.IsActive && copper.Mode == 0 && machine.Registers.Read(0x61) == 0
                && machine.Registers.Read(0x62) == 0 && machine.Registers.Read(0x64) == 0
                && copper.ReadInstruction(0) == CopperMove(0x7F, 0x5A) && copper.ReadInstruction(1) == CopperWait(50),
            "A soft reset did not stop the Copper and clear $61-$64 while keeping the list.");
        machine.Reset(ZxNextResetKind.Hard);
        Require(Enumerable.Range(0, 1024).All(index => copper.ReadInstruction(index) == 0),
            "A hard reset, which loads the FPGA again, did not clear the list.");

        // The Copper's writes go down the CPU's path: $02 bit 0 resets the machine.
        log.Clear();
        ZxNextMachine resets = CreateCopperMachine(log);
        UploadCopper(resets, CopperWait(50), CopperMove(0x02, 0x01), CopperHalt);
        ulong sequence = resets.ResetSequence;
        resets.Registers.Write(0x62, 0x40);
        RunFrames(resets, 2);
        Require(resets.ResetSequence == sequence + 1 && resets.LastResetKind == ZxNextResetKind.Soft
                && !resets.Registers.Copper.IsActive
                && resets.Registers.Copper.ReadInstruction(1) == CopperMove(0x02, 0x01),
            "A Copper MOVE $02,$01 did not soft-reset the machine, stopping the Copper and keeping its list.");
    }

    private static void WriteBytes(ZxNextMachine machine, ushort address, byte[] bytes)
    {
        for (int index = 0; index < bytes.Length; index++)
        {
            machine.MemoryBus.WriteCpu((ushort)(address + index), bytes[index]);
        }
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
        // what a reader of the clock has to do.
        //
        // NEXTREG n,n requests the write as its fourth M-cycle starts; cpu_speed latches at the
        // end of that cycle (17 T at 3.5 MHz, 136 ticks), the fifth cycle's first T is still on
        // the old clock (8), the clock pauses to the 7 MHz clock's next edge strictly after
        // (4), and the last two T-states are at 7 MHz (8): 156.
        machine.StepInstruction();
        machine.SynchronizeTime();
        Require(machine.Cpu.Cyc == 20 && machine.MasterTicks == 156
                && machine.CpuSpeed == ZxNextCpuSpeed.Mhz7 && machine.CpuClockHz == 7_000_000
                && machine.Registers.Read(0x07) == 0x11,
            $"NEXTREG n,n did not latch 7 MHz after its fourth M-cycle and hand the clock over "
                + $"(Cyc {machine.Cpu.Cyc}, master {machine.MasterTicks}).");

        // NEXTREG n,A: 14 T at 7 MHz to the latch (56), one old T (4), a pause to the 14 MHz
        // edge (2), two T at 14 MHz (4): 156 + 66.
        machine.StepInstruction();
        machine.SynchronizeTime();
        Require(machine.Cpu.Cyc == 37 && machine.MasterTicks == 222
                && machine.CpuSpeed == ZxNextCpuSpeed.Mhz14 && machine.Registers.Read(0x07) == 0x22,
            $"NEXTREG n,A did not latch 14 MHz after its fourth M-cycle and hand the clock over "
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

        // The DACs have a trace each, at the PSG channels' scale.
        machine.Registers.Write(0x08, 0x18);
        machine.Registers.Write(0x2D, 0xC0);
        machine.Audio.AdvanceMasterTicks(ZxNextReferenceBaseline.MasterClockHz / 20);
        for (int channel = 0; channel < 4; channel++)
        {
            int index = AudioScopeLayout.GetNextDacChannelIndex(channel);
            short level = (short)((channel is 0 or 3 ? 0xC0 : 0x80) * 64);
            capture.CopyLatest(index, window);
            Require(index < AudioScopeLayout.ZxNext.ChannelCount && window.All(sample => sample == level),
                $"Oscilloscope channel {index} (DAC {(char)('A' + channel)}) did not show the channel's value.");
        }

        machine.Audio.SetScopeCapture(null);
    }

    /// <remarks>
    /// Each $07 write lands between instructions, so it latches on the next instruction's first
    /// edge; that T-state is on the old clock and the clock then pauses to the new one's next
    /// edge: 8 + 4, 4 + 2 and 2 + 1 ticks for the three switches, 14 more than 15 x 4096.
    /// </remarks>
    private static void VerifyAllCpuSpeedRatios()
    {
        const int cpuTstatesPerSpeed = 4096;
        ZxNextMachine machine = CreateProgram(0x00);
        foreach (ZxNextCpuSpeed speed in Enum.GetValues<ZxNextCpuSpeed>())
        {
            machine.Registers.Write(0x07, (byte)speed);
            machine.RunForTstates(cpuTstatesPerSpeed);
        }

        machine.SynchronizeTime();
        Require(machine.Cpu.Cyc == 4UL * cpuTstatesPerSpeed && machine.MasterTicks == (15UL * cpuTstatesPerSpeed) + 14,
            $"Long 3.5/7/14/28 MHz intervals took {machine.MasterTicks} master ticks, not 15 x 4096 + 14.");
    }

    /// <summary>
    /// TIM-2: cpu_speed latches on the CPU's next idle edge (zxnext.vhd ~5796-5826), the clock runs
    /// one more T-state at the old speed and the BUFGMUX_1s hand over on the new clock's next edge,
    /// strictly after (Klive's F3-5, matched to the simulated core). Every figure is worked out
    /// from that rule; the machine starts at tick 0 of a frame, on every clock's grid.
    /// </summary>
    private static void VerifyCpuSpeedSwitch()
    {
        foreach (bool batched in new[] { false, true })
        {
            VerifyCpuSpeedSwitchByOut(batched);
            VerifyCpuSpeedSwitchDownAtEveryPhase(batched);
        }

        VerifyCpuSpeedLatchBetweenInstructions();
        VerifyNoContentionAfterSwitchUp();
        VerifyCpuSpeedSnapshotAndReset();
    }

    /// <summary>
    /// Contention stops as cpu_speed leaves 3.5 MHz (zxnext.vhd's <c>i_contention_en</c>), though
    /// the clock still runs a T-state on CLK_3M5_CONT: a NOP fetched from contended memory in the
    /// paper right after an OUT that switched up takes 8 + 1 + 3 ticks at every phase. The OUT
    /// runs from uncontended memory, so it ends at every phase of the pattern (after a
    /// contended access the CPU always resumes where the pattern lets it).
    /// </summary>
    private static void VerifyNoContentionAfterSwitchUp()
    {
        for (int phase = 0; phase < 8; phase++)
        {
            ZxNextMachine machine = CreateSpeedMachine(false);
            machine.Registers.Write(0x03, 0xB0);
            machine.UlaTiming.SynchronizeProfile();
            machine.Registers.Write(0x56, 0x0E); // bank 7 at $C000, contended in +3 timing
            machine.MemoryBus.WriteCpu(0xBFFE, 0xED);
            machine.MemoryBus.WriteCpu(0xBFFF, 0x79); // OUT (C),A, in bank 2
            machine.MemoryBus.WriteCpu(0xC000, 0x00); // NOP, in bank 7
            machine.Cpu.PC = 0xBFFE;
            machine.PortBus.WriteUncontended(ZxNextPortBus.NextRegisterSelectPort, 0x07);
            machine.Cpu.A = 0x03;
            machine.Cpu.SetBC(ZxNextPortBus.NextRegisterDataPort);
            int start = 22_896 + phase;
            machine.Clock.AdvanceCpuTstates(start);
            machine.Video.AdvanceMasterTicks((ulong)start * ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate);
            ulong afterOut = StepAndTime(machine);
            Require(machine.CpuSpeed == ZxNextCpuSpeed.Mhz28 && afterOut == ((ulong)start + 12) * 8,
                $"The uncontended OUT at T {start} did not take 12 T at 3.5 MHz and latch 28 MHz.");
            ulong nopTicks = StepAndTime(machine) - afterOut;
            Require(nopTicks == 12,
                $"The NOP after switching up at T {start} took {nopTicks} ticks, not 12: contention outlived cpu_speed's 3.5 MHz.");
        }
    }

    private static ZxNextMachine CreateSpeedMachine(bool batched, params byte[] program)
    {
        ZxNextMachine machine = CreateProgramAt(0x8000, program);
        if (batched)
        {
            machine.Registers.Write(0x6B, 0x80);
            Require(machine.Cpu.InstructionTstateBatchingEnabled,
                "The tilemap did not turn instruction batching on for the speed checks.");
        }

        return machine;
    }

    private static ulong StepAndTime(ZxNextMachine machine)
    {
        machine.StepInstruction();
        machine.SynchronizeTime();
        return machine.MasterTicks;
    }

    /// <summary>
    /// An OUT to $253B with $07 selected: IORQ is high again from the middle of T3, so cpu_speed
    /// latches as the I/O cycle ends and the whole OUT runs at the old speed.
    /// </summary>
    private static void VerifyCpuSpeedSwitchByOut(bool batched)
    {
        string mode = batched ? "batched" : "unbatched";
        ZxNextMachine machine = CreateSpeedMachine(batched, 0xED, 0x79, 0x00, 0x00); // OUT (C),A; NOP; NOP
        machine.PortBus.WriteUncontended(ZxNextPortBus.NextRegisterSelectPort, 0x07);
        machine.Cpu.A = 0x03;
        machine.Cpu.SetBC(ZxNextPortBus.NextRegisterDataPort);

        // 12 T at 3.5 MHz; the latch is at the end and the clock has not handed over yet.
        Require(StepAndTime(machine) == 96 && machine.Cpu.Cyc == 12
                && machine.CpuSpeed == ZxNextCpuSpeed.Mhz28
                && machine.Clock.ClockSpeed == ZxNextCpuSpeed.Mhz3_5
                && machine.Clock.ClockHandoverPending
                && machine.Registers.Read(0x07) == 0x33,
            $"OUT (C),A to $07 did not run its whole I/O cycle at 3.5 MHz and latch 28 MHz at its end ({mode}, master {machine.MasterTicks}).");

        // The NOP: one T on the 3.5 MHz clock (8), a pause to the next 28 MHz edge (1), three T.
        Require(StepAndTime(machine) == 108 && !machine.Clock.ClockHandoverPending,
            $"The instruction after the OUT did not run one 3.5 MHz T-state and hand the clock over ({mode}, master {machine.MasterTicks}).");
        Require(StepAndTime(machine) == 112,
            $"28 MHz did not take a tick a T-state after the handover ({mode}, master {machine.MasterTicks}).");
    }

    /// <summary>
    /// NEXTREG $07,0 from 28 MHz with the instruction starting at every phase of the 3.5 MHz
    /// clock: the latch after 17 ticks, one tick on the 28 MHz clock, a pause to the next 3.5 MHz
    /// edge strictly after, then two T-states of 8. The CPU always comes back on the 3.5 MHz grid,
    /// which is what keeps contention in step with the beam after any turbo period.
    /// </summary>
    private static void VerifyCpuSpeedSwitchDownAtEveryPhase(bool batched)
    {
        string mode = batched ? "batched" : "unbatched";
        for (int phase = 0; phase < 8; phase++)
        {
            // LD A,n takes 7 ticks at 28 MHz, so k of them start the NEXTREG at 7k mod 8.
            var program = new List<byte>();
            for (int index = 0; index < phase; index++)
            {
                program.AddRange([0x3E, 0x00]);
            }

            program.AddRange([0xED, 0x91, 0x07, 0x00, 0x00]); // NEXTREG $07,0; NOP
            ZxNextMachine machine = CreateSpeedMachine(batched, program.ToArray());
            SetCpuSpeedNow(machine, ZxNextCpuSpeed.Mhz28);
            for (int index = 0; index < phase; index++)
            {
                StepAndTime(machine);
            }

            ulong start = machine.MasterTicks;
            Require(start == 7UL * (ulong)phase, $"The phase-{phase} lead-in did not take {7 * phase} ticks at 28 MHz ({mode}).");
            ulong oldClockEnd = start + 17 + 1;
            ulong expected = oldClockEnd + (8 - (oldClockEnd % 8)) + 16;
            ulong end = StepAndTime(machine);
            Require(end == expected && end % 8 == 0
                    && machine.CpuSpeed == ZxNextCpuSpeed.Mhz3_5 && machine.Clock.ClockSpeed == ZxNextCpuSpeed.Mhz3_5
                    && machine.Registers.Read(0x07) == 0x00,
                $"NEXTREG $07,0 at phase {start % 8} ended at tick {end}, not {expected} on the 3.5 MHz grid ({mode}).");
            Require(StepAndTime(machine) == expected + 32,
                $"The NOP after switching down at phase {start % 8} was not four 3.5 MHz T-states ({mode}).");
        }
    }

    /// <summary>
    /// A $07 write that lands between instructions (a host's, or the Copper's at that point) is
    /// latched on the next instruction's first edge; before that, $07 reads the request in bits
    /// 1:0 and the old speed in bits 5:4.
    /// </summary>
    private static void VerifyCpuSpeedLatchBetweenInstructions()
    {
        ZxNextMachine machine = CreateSpeedMachine(false, 0x00, 0x00);
        machine.Registers.Write(0x07, 0x02);
        Require(machine.Registers.Read(0x07) == 0x02 && machine.Clock.SpeedLatchPending
                && machine.CpuSpeed == ZxNextCpuSpeed.Mhz3_5 && machine.CpuClockHz == 3_500_000,
            "A $07 write took effect before the CPU's next edge.");

        // One T on the 3.5 MHz clock (8), a pause to the next 14 MHz edge (2), three T of 2.
        Require(StepAndTime(machine) == 16 && machine.Registers.Read(0x07) == 0x22
                && machine.CpuClockHz == 14_000_000,
            $"A $07 write between instructions was not latched on the next instruction's first edge (master {machine.MasterTicks}).");
        Require(StepAndTime(machine) == 24,
            $"14 MHz did not take two ticks a T-state after the handover (master {machine.MasterTicks}).");
    }

    /// <summary>
    /// A snapshot taken with the clock handover pending (right after an OUT that switched) carries
    /// the three stages and carries on identically; a reset puts all three back at 3.5 MHz.
    /// </summary>
    private static void VerifyCpuSpeedSnapshotAndReset()
    {
        ZxNextMachine original = CreateSpeedMachine(false, 0xED, 0x79, 0x00, 0x00);
        original.PortBus.WriteUncontended(ZxNextPortBus.NextRegisterSelectPort, 0x07);
        original.Cpu.A = 0x01;
        original.Cpu.SetBC(ZxNextPortBus.NextRegisterDataPort);
        StepAndTime(original);
        Require(original.Clock.ClockHandoverPending,
            "The snapshot check did not catch the clock handover pending.");
        ZxNextMachineSnapshot captured = ZxNextMachineSnapshotService.Capture(original);
        ZxNextMachineSnapshot loaded = RoundTripThroughFile(captured);
        Require(loaded.Clock == captured.Clock
                && loaded.Clock.CpuSpeed == ZxNextCpuSpeed.Mhz7
                && loaded.Clock.ClockSpeed == ZxNextCpuSpeed.Mhz3_5
                && loaded.Clock.RequestedCpuSpeed == ZxNextCpuSpeed.Mhz7,
            "The clock's three stages did not survive a file round trip (CLKX).");

        ZxNextMachine restored = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextMachineSnapshotService.Restore(restored, loaded);
        ulong originalEnd = StepAndTime(original);
        ulong restoredEnd = StepAndTime(restored);
        Require(originalEnd == restoredEnd && originalEnd == 96 + 8 + 4 + 12
                && !restored.Clock.ClockHandoverPending,
            $"A restore with the handover pending did not carry on as the original ({restoredEnd}, {originalEnd}).");

        // A capture with a $07 write not yet latched: the restored machine latches it on its
        // next instruction's first edge, as the original does.
        ZxNextMachine pending = CreateSpeedMachine(false, 0x00, 0x00);
        pending.Registers.Write(0x07, 0x03);
        ZxNextMachine pendingRestored = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextMachineSnapshotService.Restore(pendingRestored, RoundTripThroughFile(ZxNextMachineSnapshotService.Capture(pending)));
        Require(pendingRestored.Clock.SpeedLatchPending && pendingRestored.Registers.Read(0x07) == 0x03
                && StepAndTime(pending) == 12 && StepAndTime(pendingRestored) == 12
                && pendingRestored.Registers.Read(0x07) == 0x33,
            "A restore with a $07 write pending did not latch it on the next instruction as the original did.");

        // A state from before the stages were kept has them all at the speed in effect.
        ZxNextMachine legacy = ZxNextMachineFactory.CreateFoundationMachine();
        legacy.Clock.RestoreState(new ZxNextClockState(0, ZxNextCpuSpeed.Mhz14));
        Require(legacy.Clock.RequestedCpuSpeed == ZxNextCpuSpeed.Mhz14 && legacy.Clock.ClockSpeed == ZxNextCpuSpeed.Mhz14,
            "A clock state without its stages did not restore them equal.");

        original.Registers.Write(0x07, 0x03);
        original.Reset(ZxNextResetKind.Soft);
        Require(original.Registers.Read(0x07) == 0x00 && original.CpuSpeed == ZxNextCpuSpeed.Mhz3_5
                && original.Clock.ClockSpeed == ZxNextCpuSpeed.Mhz3_5 && !original.Clock.SpeedLatchPending,
            "A reset did not put the requested, in-effect and clock speeds back at 3.5 MHz.");
    }

    /// <summary>
    /// TIM-3: +3 timing decides its WAIT a T-state after the memory cycle starts. Klive's
    /// simulated-core probes (F3-4, group C) ran LD A,(HL) and LD (HL),A on bank 7 starting at
    /// 16 consecutive T-states from T 22,900 after a first fetch at the core's line 310, hc 448,
    /// which is 8 hc before our frame's tick 0; the core's maximum wait came at T 22,903 and
    /// 22,911, our T 22,899 and 22,907. The waits around them follow the +3 pattern.
    /// </summary>
    private static void VerifyPlus3WaitPhase()
    {
        int[] waits = [2, 1, 0, 7, 6, 5, 4, 3, 2, 1, 0, 7, 6, 5, 4, 3];
        const int first = 22_896;
        foreach (bool batched in new[] { false, true })
        {
            foreach ((string name, byte opcode, byte page) in new (string, byte, byte)[]
            {
                ("read", 0x7E, 0x0E),
                ("write", 0x77, 0x0E),
                ("uncontended read (bank 2)", 0x7E, 0x04)
            })
            {
                for (int index = 0; index < waits.Length; index++)
                {
                    ZxNextMachine machine = CreateSpeedMachine(batched, opcode);
                    machine.Registers.Write(0x03, 0xB0);
                    machine.UlaTiming.SynchronizeProfile();
                    machine.Registers.Write(0x56, page);
                    machine.Cpu.SetHL(0xC000);
                    int start = first + index;
                    machine.Clock.AdvanceCpuTstates(start);
                    machine.Video.AdvanceMasterTicks((ulong)start * ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate);
                    ulong before = machine.Cpu.Cyc;
                    machine.StepInstruction();
                    int expected = 7 + (page == 0x0E ? waits[index] : 0);
                    int taken = checked((int)(machine.Cpu.Cyc - before));
                    Require(machine.UlaTiming.Profile.IsPlus3 && taken == expected,
                        $"A +3 {name} starting at T {start} (the probes' T {start + 4}) took {taken} T, not {expected} ({(batched ? "batched" : "unbatched")}).");
                }
            }
        }
    }

    /// <summary>
    /// FIX-INT-2: the T80N registers INT_n into INT_s every clock and takes INT at an
    /// instruction's end with IFF1 set, so INT is a level: a handler that enables interrupts and
    /// returns while the 32/36-clock pulse (zxnext.vhd pulse_int_n) is still low is interrupted
    /// again. And EI holds off the maskable interrupt alone: NMI_s has no SetEI term.
    /// </summary>
    private static void VerifyIntLevelAndNmiAfterEi()
    {
        // IM 1 from a HALT loop: the interrupt is taken at a HALT boundary (under 4 T into the
        // pulse), the acknowledge and EI; RET take 13 + 4 + 10 = 27 T, so RET ends inside the
        // pulse and the handler runs again; the second RET ends past it. With EI first and four
        // NOPs after it, the first NOP already ends inside the pulse, so the handler is
        // interrupted inside itself, once. With the NOPs first and EI; RET last (43 T), RET ends
        // past the pulse and the handler runs once.
        byte[] shortHandler = [0xFB, 0xC9];
        byte[] nestedHandler = [0xFB, 0x00, 0x00, 0x00, 0x00, 0xC9];
        byte[] longHandler = [0x00, 0x00, 0x00, 0x00, 0xFB, 0xC9];
        foreach ((string name, byte timing, ZxNextCpuSpeed speed, byte[] handler, int perFrame) in
            new (string, byte, ZxNextCpuSpeed, byte[], int)[]
            {
                ("+3, 3.5 MHz, EI; RET", 0xB0, ZxNextCpuSpeed.Mhz3_5, shortHandler, 2),
                ("128K, 3.5 MHz, EI; RET", 0xA0, ZxNextCpuSpeed.Mhz3_5, shortHandler, 2),
                ("+3, 28 MHz, EI; RET", 0xB0, ZxNextCpuSpeed.Mhz28, shortHandler, 2),
                ("+3, 3.5 MHz, EI then four NOPs", 0xB0, ZxNextCpuSpeed.Mhz3_5, nestedHandler, 2),
                ("128K, 3.5 MHz, EI then four NOPs", 0xA0, ZxNextCpuSpeed.Mhz3_5, nestedHandler, 2),
                ("+3, 3.5 MHz, four NOPs then EI; RET", 0xB0, ZxNextCpuSpeed.Mhz3_5, longHandler, 1),
                ("128K, 3.5 MHz, four NOPs then EI; RET", 0xA0, ZxNextCpuSpeed.Mhz3_5, longHandler, 1)
            })
        {
            ZxNextMachine machine = CreateIm1HaltLoop(timing, speed, handler);
            ulong before = machine.Cpu.InterruptsAccepted;
            RunFrames(machine, 3);
            ulong accepted = machine.Cpu.InterruptsAccepted - before;
            Require(accepted == (ulong)(3 * perFrame),
                $"{name}: {accepted} interrupts taken in three frames, not {3 * perFrame}.");
        }

        // A capture between the two entries (the line still low, the request still up) carries
        // on into the second as the original does.
        ZxNextMachine original = CreateIm1HaltLoop(0xB0, ZxNextCpuSpeed.Mhz3_5, shortHandler);
        int steps = 0;
        while (original.Cpu.InterruptsAccepted == 0 && steps++ < 100_000)
        {
            original.StepInstruction();
        }

        Require(original.Cpu.InterruptPending,
            "Taking the interrupt cleared the request while the pulse still held INT low.");
        ZxNextMachine restored = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextMachineSnapshotService.Restore(restored, RoundTripThroughFile(ZxNextMachineSnapshotService.Capture(original)));
        ulong originalBefore = original.Cpu.InterruptsAccepted;
        ulong restoredBefore = restored.Cpu.InterruptsAccepted;
        for (int step = 0; step < 4; step++)
        {
            original.StepInstruction();
            restored.StepInstruction();
        }

        Require(original.Cpu.InterruptsAccepted - originalBefore == 1
                && restored.Cpu.InterruptsAccepted - restoredBefore == 1
                && restored.Cpu.PC == original.Cpu.PC,
            "A restore between the two entries did not run the handler again as the original did.");

        // An NMI already pending at an EI's end is taken there, returning to the instruction
        // after the EI. (The machine's own NMI path delivers a latched NMI after the step and
        // looks again at the same boundary, which hides the rule; so the NMI is raised in the
        // CPU before the EI runs, as the Spectrum's devices raise theirs.)
        ZxNextMachine nmi = CreateProgramAt(0x8000, 0xFB, 0x00, 0x00); // EI; NOP; NOP
        nmi.Registers.Write(0x50, 0x20); // RAM at $0000-$1FFF
        nmi.MemoryBus.WriteCpu(0x0066, 0xED);
        nmi.MemoryBus.WriteCpu(0x0067, 0x45); // RETN
        nmi.Cpu.SP = 0x7000;
        nmi.Cpu.RequestNmi(ZxNextNmiSource.Multiface);
        nmi.StepInstruction();
        ushort returnAddress = (ushort)(nmi.MemoryBus.Read(nmi.Cpu.SP) | (nmi.MemoryBus.Read((ushort)(nmi.Cpu.SP + 1)) << 8));
        Require(nmi.Cpu.PC == 0x0066 && nmi.Cpu.SP == 0x6FFE && returnAddress == 0x8001,
            $"An NMI pending at an EI's end was not taken there (PC ${nmi.Cpu.PC:X4}, return ${returnAddress:X4}).");

        // The NMI ends the EI's delay: its RETN brings back the IFF2 that EI set, and an INT
        // already on the line is taken at the RETN's end (IM 0 with $FF on the bus: RST $38).
        nmi.Cpu.Z80SetINTLine(true, 0xFF);
        nmi.StepInstruction();
        Require(nmi.Cpu.PC == 0x0038,
            $"An INT waiting through an NMI taken after EI was not taken at the RETN's end (PC ${nmi.Cpu.PC:X4}).");
    }

    /// <summary>IM 1 with <paramref name="handler"/> in RAM at $0038, the CPU parked in HALT; JR.</summary>
    private static ZxNextMachine CreateIm1HaltLoop(byte timing, ZxNextCpuSpeed speed, byte[] handler)
    {
        // IM 1; EI; HALT; JR back to the HALT.
        ZxNextMachine machine = CreateProgramAt(0x8000, 0xED, 0x56, 0xFB, 0x76, 0x18, 0xFD);
        machine.Registers.Write(0x03, timing);
        machine.UlaTiming.SynchronizeProfile();
        machine.Registers.Write(0x50, 0x20); // RAM at $0000-$1FFF
        for (int index = 0; index < handler.Length; index++)
        {
            machine.MemoryBus.WriteCpu((ushort)(0x0038 + index), handler[index]);
        }

        machine.Cpu.SP = 0x7000;
        if (speed != ZxNextCpuSpeed.Mhz3_5)
        {
            SetCpuSpeedNow(machine, speed);
        }

        return machine;
    }

    /// <summary>
    /// HW-4: the Z80 CTC as ctc.vhd, ctc_chan.vhd and zxnext.vhd build it. Every expected count
    /// and edge is worked out from the VHDL: a write at tick w is seen on edge w + 1; after the
    /// time constant the channel leaves S_TRIGGER on edge w + 2 with the prescaler at 0, so a
    /// timer at prescaler 16 first counts on edge w + 18; a read returns the counter after the
    /// edge at its tick; a zero count's request is seen on the edge after it.
    /// </summary>
    private static void VerifyCtc()
    {
        VerifyCtcProtocol();
        VerifyCtcPrescalerAndReload();
        VerifyCtcResetsAndNewConstants();
        VerifyCtcChainAndTrigger();
        VerifyCtcCounterEdges();
        VerifyCtcPortsAndStatus();
        VerifyCtcInterrupts(batched: false);
        VerifyCtcInterrupts(batched: true);
        VerifyCtcReadMatchesTheCore();
        VerifyCtcSnapshotAndReset();
    }

    private static ushort CtcPort(int channel) => (ushort)(0x183B | (channel << 8));

    private static int Word(ZxNextMachine machine, ushort address) =>
        machine.MemoryBus.Read(address) | (machine.MemoryBus.Read((ushort)(address + 1)) << 8);

    private static void CtcOut(ZxNextMachine machine, int channel, byte value) =>
        machine.PortBus.WriteUncontended(CtcPort(channel), value);

    private static byte CtcIn(ZxNextMachine machine, int channel) =>
        machine.PortBus.ReadUncontended(CtcPort(channel));

    /// <summary>Moves the clock to <paramref name="tick"/> (the CTC follows when asked).</summary>
    private static void CtcTo(ZxNextMachine machine, ulong tick)
    {
        Require(tick >= machine.Clock.MasterTicks, "A CTC check tried to go back in time.");
        machine.Clock.AdvanceMasterTicks(tick - machine.Clock.MasterTicks);
    }

    private static byte CtcInAt(ZxNextMachine machine, int channel, ulong tick)
    {
        CtcTo(machine, tick);
        return CtcIn(machine, channel);
    }

    /// <summary>$C9 at <paramref name="tick"/>, the CTC brought there first.</summary>
    private static byte CtcStatusAt(ZxNextMachine machine, ulong tick)
    {
        CtcTo(machine, tick);
        machine.Registers.Ctc.Advance(tick);
        return machine.Registers.Read(0xC9);
    }

    /// <summary>A control word, two ticks, then the time constant; the tick the constant was written at.</summary>
    private static ulong CtcProgram(ZxNextMachine machine, int channel, byte control, byte constant)
    {
        CtcOut(machine, channel, control);
        CtcTo(machine, machine.Clock.MasterTicks + 2);
        ulong written = machine.Clock.MasterTicks;
        CtcOut(machine, channel, constant);
        return written;
    }

    private static int CtcState(ZxNextMachine machine, int channel)
    {
        machine.Registers.Ctc.Advance(machine.Clock.MasterTicks);
        return machine.Registers.Ctc.GetChannel(channel).State;
    }

    private static void VerifyCtcProtocol()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        for (int channel = 0; channel < 4; channel++)
        {
            Require(CtcIn(machine, channel) == 0x00 && CtcState(machine, channel) == 0,
                $"CTC channel {channel} did not come up reset and reading 0.");
        }

        Require(machine.Registers.Read(0xC5) == 0x00, "NextReg $C5 did not come up 0.");

        // A control word without D2 leaves the channel in S_RESET.
        CtcOut(machine, 0, 0x01);
        CtcTo(machine, machine.Clock.MasterTicks + 4);
        Require(CtcIn(machine, 0) == 0x00 && CtcState(machine, 0) == 0,
            "A CTC control word without D2 took the channel out of reset.");

        // Timer, prescaler 16, constant $40.
        ulong w = CtcProgram(machine, 0, 0x05, 0x40);
        byte[] expected = [0x00, 0x40, 0x40, 0x3F, 0x3E];
        ulong[] at = [w + 1, w + 2, w + 17, w + 18, w + 34];
        for (int index = 0; index < at.Length; index++)
        {
            byte count = CtcInAt(machine, 0, at[index]);
            Require(count == expected[index],
                $"CTC channel 0 read ${count:X2} {at[index] - w} ticks after its constant, not ${expected[index]:X2}.");
        }

        // The byte after a D2 control word is the constant, D0 set or not; each port is its own channel.
        CtcProgram(machine, 1, 0x05, 0xC5);
        CtcProgram(machine, 2, 0x45, 0x22);
        CtcProgram(machine, 3, 0x45, 0x33);
        CtcTo(machine, machine.Clock.MasterTicks + 2);
        Require(CtcIn(machine, 1) == 0xC5 && CtcIn(machine, 2) == 0x22 && CtcIn(machine, 3) == 0x33,
            "An odd time constant was not taken as the constant, or a port reached another channel.");
    }

    private static void VerifyCtcPrescalerAndReload()
    {
        // Prescaler 256 (D5), constant 2: counts on w + 258, zero count on w + 514.
        ZxNextMachine slow = ZxNextMachineFactory.CreateFoundationMachine();
        ulong w = CtcProgram(slow, 0, 0x25, 0x02);
        Require(CtcInAt(slow, 0, w + 257) == 2 && CtcInAt(slow, 0, w + 258) == 1
                && CtcInAt(slow, 0, w + 513) == 1 && CtcInAt(slow, 0, w + 514) == 2,
            "A prescaler-256 timer did not count every 256 ticks and reload its constant.");
        Require((CtcStatusAt(slow, w + 514) & 0x01) == 0 && (CtcStatusAt(slow, w + 515) & 0x01) != 0,
            "The zero count's status was not latched on the edge after it.");

        // Constant 3 at prescaler 16: 3, 2, 1, and the edge that would read 0 reloads.
        ZxNextMachine reload = ZxNextMachineFactory.CreateFoundationMachine();
        w = CtcProgram(reload, 0, 0x05, 0x03);
        Require(CtcInAt(reload, 0, w + 17) == 3 && CtcInAt(reload, 0, w + 18) == 2
                && CtcInAt(reload, 0, w + 34) == 1 && CtcInAt(reload, 0, w + 50) == 3,
            "The CTC counter read 0, or did not reload on the edge that would count 1 to 0.");

        // Constant 0 counts 256.
        ZxNextMachine full = ZxNextMachineFactory.CreateFoundationMachine();
        w = CtcProgram(full, 0, 0x05, 0x00);
        Require(CtcInAt(full, 0, w + 17) == 0x00 && CtcInAt(full, 0, w + 18) == 0xFF
                && CtcInAt(full, 0, w + 4082) == 0x01 && CtcInAt(full, 0, w + 4098) == 0x00
                && (CtcStatusAt(full, w + 4098) & 0x01) == 0 && (CtcStatusAt(full, w + 4099) & 0x01) != 0,
            "A time constant of 0 did not count 256 before its zero count.");
    }

    private static void VerifyCtcResetsAndNewConstants()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        ulong w = CtcProgram(machine, 0, 0x05, 0x40);
        Require(CtcInAt(machine, 0, w + 50) == 0x3D, "The timer had not counted three times by w + 50.");

        // A vector byte (D0 clear) changes nothing on the Next.
        CtcOut(machine, 0, 0x10);
        Require(CtcInAt(machine, 0, w + 66) == 0x3C && machine.Registers.Ctc.GetChannel(0).Control == 0x00,
            "A CTC vector write disturbed the channel.");

        // Software reset with D2 at w + 66: seen on w + 67, the counter held at the constant from w + 68.
        CtcOut(machine, 0, 0x07);
        Require(CtcInAt(machine, 0, w + 67) == 0x3C && CtcInAt(machine, 0, w + 68) == 0x40
                && CtcInAt(machine, 0, w + 300) == 0x40 && CtcState(machine, 0) == 1,
            "A software reset with D2 did not stop the channel at its constant, waiting for a new one.");
        CtcOut(machine, 0, 0x20);
        Require(CtcInAt(machine, 0, w + 301) == 0x40 && CtcInAt(machine, 0, w + 302) == 0x20
                && CtcInAt(machine, 0, w + 318) == 0x1F,
            "The new constant after a software reset did not restart the channel from it.");

        // Software reset without D2: S_RESET; a vector byte is ignored there.
        CtcOut(machine, 0, 0x03);
        ulong t = machine.Clock.MasterTicks;
        CtcTo(machine, t + 2);
        CtcOut(machine, 0, 0x10);
        Require(CtcInAt(machine, 0, t + 4) == 0x20 && CtcState(machine, 0) == 0,
            "A software reset without D2 did not return the channel to S_RESET, holding its constant.");

        // CTC-019: D2 without D1 on a running timer: it counts on, the new constant comes at the
        // next zero count.
        ZxNextMachine running = ZxNextMachineFactory.CreateFoundationMachine();
        w = CtcProgram(running, 0, 0x05, 0x04);
        CtcTo(running, w + 20);
        CtcOut(running, 0, 0x05);
        CtcTo(running, w + 22);
        CtcOut(running, 0, 0x02);
        Require(CtcInAt(running, 0, w + 34) == 2 && CtcInAt(running, 0, w + 50) == 1
                && CtcInAt(running, 0, w + 66) == 2 && CtcInAt(running, 0, w + 82) == 1
                && CtcInAt(running, 0, w + 98) == 2,
            "A new constant on a running timer did not wait for the next zero count.");

        // CTC-018: a D2 control word while a timer waits for its trigger asks for a constant again.
        ZxNextMachine waiting = ZxNextMachineFactory.CreateFoundationMachine();
        w = CtcProgram(waiting, 1, 0x0D, 0x10);
        Require(CtcInAt(waiting, 1, w + 100) == 0x10 && CtcState(waiting, 1) == 2,
            "A timer with D3 did not wait for its trigger in S_TRIGGER.");
        CtcOut(waiting, 1, 0x0D);
        CtcTo(waiting, w + 102);
        Require(CtcState(waiting, 1) == 1, "A D2 control word in S_TRIGGER did not go back to S_RESET_TC.");
        CtcOut(waiting, 1, 0x08);
        Require(CtcInAt(waiting, 1, w + 104) == 0x08 && CtcState(waiting, 1) == 2,
            "The constant after a D2 control word in S_TRIGGER was not taken.");
    }

    private static void VerifyCtcChainAndTrigger()
    {
        // Timer 0 (constant 2) -> counter 1 (rising, 3) -> counter 2 (falling, 2).
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        CtcProgram(machine, 1, 0x55, 0x03);
        CtcProgram(machine, 2, 0x45, 0x02);
        ulong w = CtcProgram(machine, 0, 0x05, 0x02);

        // Channel 0's zero counts fall on w + 34 + 32k; channel 1 counts on the edge after each
        // (w + 35, 67, 99: zero count), channel 2 on the second edge after channel 1's (w + 101, 197).
        Require(CtcInAt(machine, 1, w + 34) == 3 && CtcInAt(machine, 1, w + 35) == 2
                && CtcInAt(machine, 1, w + 98) == 1 && CtcInAt(machine, 1, w + 99) == 3
                && (CtcStatusAt(machine, w + 99) & 0x02) == 0 && (CtcStatusAt(machine, w + 100) & 0x02) != 0,
            "A rising-edge counter did not count on the edge after its upstream's zero count.");
        Require(CtcInAt(machine, 2, w + 100) == 2 && CtcInAt(machine, 2, w + 101) == 1
                && CtcInAt(machine, 2, w + 196) == 1 && CtcInAt(machine, 2, w + 197) == 2
                && (CtcStatusAt(machine, w + 197) & 0x04) == 0 && (CtcStatusAt(machine, w + 198) & 0x04) != 0,
            "A falling-edge counter did not count two edges after its upstream's zero count.");

        // The ring closes: channel 3's zero counts clock channel 0.
        ZxNextMachine ring = ZxNextMachineFactory.CreateFoundationMachine();
        CtcProgram(ring, 0, 0x55, 0x02);
        w = CtcProgram(ring, 3, 0x05, 0x01);
        Require(CtcInAt(ring, 0, w + 18) == 2 && CtcInAt(ring, 0, w + 19) == 1 && CtcInAt(ring, 0, w + 35) == 2,
            "Channel 3's zero count did not clock channel 0.");

        // A timer with D3 starts on its trigger: channel 0's zero count at w + 34, the rising edge
        // on w + 35 leaves S_TRIGGER with the prescaler at 0, the first count on w + 51.
        ZxNextMachine trigger = ZxNextMachineFactory.CreateFoundationMachine();
        CtcProgram(trigger, 1, 0x1D, 0x04);
        w = CtcProgram(trigger, 0, 0x05, 0x02);
        CtcTo(trigger, w + 34);
        Require(CtcState(trigger, 1) == 2, "The triggered timer started before its trigger.");
        CtcTo(trigger, w + 35);
        Require(CtcState(trigger, 1) == 3 && CtcInAt(trigger, 1, w + 50) == 4 && CtcInAt(trigger, 1, w + 51) == 3,
            "A timer with D3 did not start on its upstream's zero count.");

        // Changing the edge select (D4) is a trigger edge itself.
        ZxNextMachine flip = ZxNextMachineFactory.CreateFoundationMachine();
        CtcProgram(flip, 1, 0x1D, 0x04);
        CtcTo(flip, flip.Clock.MasterTicks + 10);
        ulong t = flip.Clock.MasterTicks;
        CtcOut(flip, 1, 0x09);
        Require(CtcInAt(flip, 1, t + 16) == 4 && CtcInAt(flip, 1, t + 17) == 3,
            "Changing the edge select did not start a timer waiting for its trigger.");
    }

    private static void VerifyCtcCounterEdges()
    {
        // A counter never counts on the prescaler.
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        CtcProgram(machine, 2, 0x55, 0x03);
        CtcProgram(machine, 1, 0x45, 0x05);
        Require(CtcInAt(machine, 1, machine.Clock.MasterTicks + 1000) == 5,
            "A counter with no upstream zero count counted.");

        // A control word that flips D4 counts one edge; the same D4 does not.
        byte[] words = [0x51, 0x51, 0x41, 0x51, 0x41];
        byte[] counts = [4, 4, 3, 2, 1];
        for (int index = 0; index < words.Length; index++)
        {
            CtcOut(machine, 1, words[index]);
            byte count = CtcInAt(machine, 1, machine.Clock.MasterTicks + 2);
            Require(count == counts[index],
                $"Control word ${words[index]:X2} left the counter at {count}, not {counts[index]}.");
        }

        // The flip that reaches zero is a zero count: reload, status, and the next channel counts.
        ulong t = machine.Clock.MasterTicks;
        CtcOut(machine, 1, 0x51);
        Require(CtcInAt(machine, 1, t + 1) == 5 && (CtcStatusAt(machine, t + 2) & 0x02) != 0
                && CtcInAt(machine, 2, t + 2) == 2,
            "A D4 flip that reached zero did not reload, latch the status and clock the next channel.");
    }

    private static void VerifyCtcPortsAndStatus()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        for (int channel = 4; channel < 8; channel++)
        {
            CtcOut(machine, channel, 0x05);
            CtcTo(machine, machine.Clock.MasterTicks + 2);
            CtcOut(machine, channel, 0x40);
            Require(CtcIn(machine, channel) == 0x00, $"CTC port ${CtcPort(channel):X4} did not read $00.");
        }

        CtcTo(machine, machine.Clock.MasterTicks + 4);
        for (int channel = 0; channel < 4; channel++)
        {
            Require(CtcIn(machine, channel) == 0x00 && CtcState(machine, channel) == 0,
                $"A write to CTC channels 4-7 reached channel {channel}.");
        }

        // $C9 latches each zero count; writing 1 clears that bit alone.
        ulong w0 = CtcProgram(machine, 0, 0x05, 0x01);
        CtcProgram(machine, 1, 0x05, 0x01);
        Require((CtcStatusAt(machine, w0 + 100) & 0x03) == 0x03, "The zero counts of channels 0 and 1 did not latch.");
        machine.Registers.Write(0xC9, 0x02);
        Require((machine.Registers.Read(0xC9) & 0x03) == 0x01, "Writing $C9 bit 1 did not clear channel 1's status alone.");

        // Port enable bit 27 ($85 bit 3) off: the ports are gone, and a running channel runs on.
        ZxNextMachine disabled = ZxNextMachineFactory.CreateFoundationMachine();
        ulong w = CtcProgram(disabled, 0, 0x05, 0x40);
        byte enables = disabled.Registers.Read(0x85);
        disabled.Registers.Write(0x85, (byte)(enables & ~0x08));
        CtcOut(disabled, 0, 0x03);
        Require(CtcInAt(disabled, 0, w + 50) == 0xFF, "A CTC port read with its enable off did not float to $FF.");
        disabled.Registers.Write(0x85, enables);
        Require(CtcInAt(disabled, 0, w + 66) == 0x3C,
            "A CTC write reached the channel with the port enable off, or the channel stopped.");
    }

    /// <summary>
    /// A CPU taking the CTC's interrupts: hardware IM2 on vector $A0 | (3 + channel) * 2, then pulse
    /// mode on $FF, a $C5 write stopping them while the status still latches, and the same zero
    /// counts at 28 MHz (the CTC runs from the 28 MHz clock).
    /// </summary>
    private static void VerifyCtcInterrupts(bool batched)
    {
        string mode = batched ? "batched" : "unbatched";
        (int Taken, int Wrong, ulong FirstAccepted, ZxNextMachine Machine, int ZeroCounts) Run(bool hardware, ZxNextCpuSpeed speed, int frames)
        {
            // IM 2; LD A,$B0; LD I,A; EI; HALT; JR back to the HALT.
            ZxNextMachine machine = CreateSpeedMachine(batched, 0xED, 0x5E, 0x3E, 0xB0, 0xED, 0x47, 0xFB, 0x76, 0x18, 0xFD);
            machine.Registers.Write(0x22, 0x04); // no ULA interrupt
            machine.Registers.Write(0xC0, (byte)(hardware ? 0xA1 : 0xA0));
            machine.Cpu.SP = 0x7000;

            // Every vector to a handler counting a word at $A002; the CTC's (or $FF's) to one counting at $A000.
            byte[] counting = [0xE5, 0x2A, 0x00, 0xA0, 0x23, 0x22, 0x00, 0xA0, 0xE1, 0xFB, 0xED, 0x4D];
            byte[] wrong = [0xE5, 0x2A, 0x02, 0xA0, 0x23, 0x22, 0x02, 0xA0, 0xE1, 0xFB, 0xED, 0x4D];
            for (int index = 0; index < counting.Length; index++)
            {
                machine.MemoryBus.WriteCpu((ushort)(0x9000 + index), counting[index]);
                machine.MemoryBus.WriteCpu((ushort)(0x9100 + index), wrong[index]);
            }

            for (int vector = 0; vector < 0x101; vector++)
            {
                machine.MemoryBus.WriteCpu((ushort)(0xB000 + vector), (byte)((vector & 1) == 0 ? 0x00 : 0x91));
            }

            ushort used = hardware ? (ushort)0xB0A6 : (ushort)0xB0FF;
            machine.MemoryBus.WriteCpu(used, 0x00);
            machine.MemoryBus.WriteCpu((ushort)(used + 1), 0x90);
            for (ushort address = 0xA000; address < 0xA004; address++)
            {
                machine.MemoryBus.WriteCpu(address, 0x00);
            }

            if (speed != ZxNextCpuSpeed.Mhz3_5)
            {
                SetCpuSpeedNow(machine, speed);
            }

            // Channel 0: interrupt (D7), timer, prescaler 16, constant $40: a zero count every 1024 ticks.
            machine.StepInstruction();
            machine.StepInstruction();
            machine.StepInstruction();
            machine.StepInstruction();
            machine.SynchronizeTime();
            machine.PortBus.WriteUncontended(CtcPort(0), 0x85);
            machine.Clock.AdvanceMasterTicks(0);
            ulong constantTick = machine.Clock.MasterTicks;
            machine.PortBus.WriteUncontended(CtcPort(0), 0x40);
            ulong firstAccepted = 0;
            ulong accepted = machine.Cpu.InterruptsAccepted;
            int steps = 0;
            while (machine.Cpu.InterruptsAccepted == accepted && steps++ < 1_000_000)
            {
                machine.StepInstruction();
            }

            machine.SynchronizeTime();
            firstAccepted = machine.Clock.MasterTicks;
            RunFrames(machine, frames);
            machine.SynchronizeTime();

            // Zero counts on edges w + 1026 + 1024k, each seen on the next edge.
            ulong end = machine.Clock.MasterTicks;
            int zeroCounts = end < constantTick + 1027 ? 0 : (int)((end - (constantTick + 1027)) / 1024) + 1;
            // The first request is seen on edge w + 1027; the HALT loop's next boundary takes it,
            // and the step ends after the 19 T acknowledge: 152 to 183 ticks on at 3.5 MHz.
            return (Word(machine, 0xA000), Word(machine, 0xA002), firstAccepted - (constantTick + 1027), machine, zeroCounts);
        }

        // The constant is written between instructions at a known tick, so the zero counts are
        // known: the first on edge w + 1026 (64 counts from w + 18), then every 1024.
        // Every zero count is taken, the last perhaps not yet (its handler still to come).
        (int taken, int wrong, ulong first, ZxNextMachine hw, int zeroCounts) = Run(hardware: true, ZxNextCpuSpeed.Mhz3_5, frames: 2);
        Require(wrong == 0 && hw.Registers.Read(0xC5) == 0x01,
            $"A CTC interrupt took a vector other than $A6, or $C5 did not show control bit 7 ({mode}).");
        Require(taken >= zeroCounts - 1 && taken <= zeroCounts,
            $"Hardware IM2 took {taken} CTC interrupts for {zeroCounts} zero counts ({mode}).");
        Require(first is >= 152 and < 184,
            $"The first CTC interrupt was taken {first} ticks after its request, not at the next boundary ({mode}).");

        (int pulseTaken, int pulseWrong, ulong pulseFirst, _, int pulseZeroCounts) = Run(hardware: false, ZxNextCpuSpeed.Mhz3_5, frames: 2);
        Require(pulseWrong == 0 && pulseTaken >= pulseZeroCounts - 1 && pulseTaken <= pulseZeroCounts && pulseFirst == first,
            $"In pulse mode the CTC's interrupts did not come through vector $FF at the same times ({pulseTaken} for {pulseZeroCounts}, first {pulseFirst} not {first}, {mode}).");

        // $C5 cleared: no more interrupts, the status still latching.
        hw.Registers.Write(0xC5, 0x00);
        hw.Registers.Write(0xC9, 0x0F);
        int before = Word(hw, 0xA000);
        RunFrames(hw, 1);
        Require(Word(hw, 0xA000) == before && (hw.Registers.Read(0xC9) & 0x01) != 0
                && hw.Registers.Read(0xC5) == 0x00,
            $"Clearing $C5 did not stop the CTC's interrupts, or stopped its status latching ({mode}).");

        // At 28 MHz the zero counts are the same: the CTC counts the 28 MHz clock, not the CPU's.
        (int fastTaken, _, _, _, int fastZeroCounts) = Run(hardware: true, ZxNextCpuSpeed.Mhz28, frames: 2);
        Require(fastTaken >= fastZeroCounts - 1 && fastTaken <= fastZeroCounts,
            $"At 28 MHz the CPU took {fastTaken} CTC interrupts for {fastZeroCounts} zero counts ({mode}).");
    }

    /// <summary>
    /// The counter a program reads, as Klive's simulated-core probes measured it (its fidelity
    /// plan's F3-8, probe group T, 24 reads one T-state apart). The probe program: control $05,
    /// constant 10, a delay of 100 + k T-states, IN A,(C). Klive's model, which matches the core in
    /// all 24, puts the count read at 10 - (n mod 10), n = floor((d + 14) / 16), where d is the
    /// distance in ticks from the constant's OUT to the IN, both I/O cycles 8 T into their
    /// instructions.
    /// </summary>
    private static void VerifyCtcReadMatchesTheCore()
    {
        for (int k = 0; k < 24; k++)
        {
            int delay = 100 + k;
            var program = new List<byte> { 0xAF, 0x01, 0x3B, 0x18, 0x3E, 0x05, 0xED, 0x79, 0x3E, 0x0A, 0xED, 0x79 };
            // Klive's delay(): RET NZ (5, Z set), INC HL (6) or LD A,0 (7) for the remainder, then NOPs.
            int filler = delay % 4;
            int fillerT = filler switch { 1 => 5, 2 => 6, 3 => 7, _ => 0 };
            program.AddRange(filler switch { 1 => [0xC0], 2 => [0x23], 3 => [0x3E, 0x00], _ => [] });
            program.AddRange(Enumerable.Repeat((byte)0x00, (delay - fillerT) / 4));
            program.AddRange([0xED, 0x78, 0x18, 0xFE]);
            ZxNextMachine machine = CreateSpeedMachine(false, program.ToArray());
            machine.Registers.Write(0x22, 0x04);

            // XOR A, LD BC, LD A, OUT, LD A: to the second OUT.
            for (int step = 0; step < 5; step++)
            {
                machine.StepInstruction();
            }

            machine.SynchronizeTime();
            ulong outStart = machine.Clock.MasterTicks;
            while (machine.Cpu.PC != 0x8000 + program.Count - 4)
            {
                machine.StepInstruction();
            }

            machine.SynchronizeTime();
            ulong inStart = machine.Clock.MasterTicks;
            machine.StepInstruction();
            ulong distance = inStart - outStart;
            int n = (int)((distance + 14) / 16);
            int expected = 10 - (n % 10);
            Require(distance == (ulong)(12 + delay) * 8 && machine.Cpu.A == expected,
                $"The CTC read ${machine.Cpu.A:X2} with the IN {delay} T after the constant, not ${expected:X2} as the simulated core.");
        }
    }

    private static void VerifyCtcSnapshotAndReset()
    {
        // Captured with a write not yet seen and with a zero count's request still to come.
        foreach (bool atZeroCount in new[] { false, true })
        {
            ZxNextMachine original = ZxNextMachineFactory.CreateFoundationMachine();
            CtcProgram(original, 1, 0x55, 0x03);
            ulong w = CtcProgram(original, 0, 0x85, 0x02);
            if (atZeroCount)
            {
                CtcTo(original, w + 34);
                original.Registers.Ctc.Advance(w + 34);
            }
            else
            {
                CtcTo(original, w + 40);
                CtcOut(original, 2, 0x45);
            }

            ZxNextMachineSnapshot captured = ZxNextMachineSnapshotService.Capture(original);
            Require(atZeroCount ? captured.Ctc!.PendingRequests == 0x01 : captured.Ctc!.PendingWriteChannel == 2,
                "The CTC snapshot check did not catch the moment it was after.");
            ZxNextMachine restored = ZxNextMachineFactory.CreateFoundationMachine();
            ZxNextMachineSnapshotService.Restore(restored, RoundTripThroughFile(captured));
            foreach (ulong offset in new ulong[] { 1, 2, 30, 70, 200 })
            {
                ulong tick = original.Clock.MasterTicks + offset;
                Require(CtcInAt(original, 0, tick) == CtcInAt(restored, 0, tick)
                        && CtcInAt(original, 1, tick) == CtcInAt(restored, 1, tick)
                        && CtcState(original, 2) == CtcState(restored, 2)
                        && CtcStatusAt(original, tick) == CtcStatusAt(restored, tick),
                    $"A restored CTC differed from the original {offset} ticks on ({(atZeroCount ? "request pending" : "write pending")}).");
            }
        }

        // A reset (soft or hard) puts every channel back in S_RESET with its enables clear.
        ZxNextMachine reset = ZxNextMachineFactory.CreateFoundationMachine();
        CtcProgram(reset, 0, 0x85, 0x40);
        CtcTo(reset, reset.Clock.MasterTicks + 100);
        reset.Reset(ZxNextResetKind.Soft);
        Require(CtcIn(reset, 0) == 0x00 && CtcState(reset, 0) == 0 && reset.Registers.Read(0xC5) == 0x00,
            "A soft reset did not reset the CTC.");
    }

    /// <summary>
    /// Puts a machine at a speed for a check that is not about the switch, as if it had long
    /// been in effect.
    /// </summary>
    private static void SetCpuSpeedNow(ZxNextMachine machine, ZxNextCpuSpeed speed)
    {
        machine.Registers.Write(0x07, (byte)speed);
        machine.ForceCpuSpeed(speed);
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

    /// <summary>
    /// A machine for the NMI rules: out of configuration mode (where NMI requests are ignored),
    /// RAM pages 0 and 1 at the bottom with NOPs at $0066, a JR-to-itself loop at $8000, both
    /// buttons' NMIs enabled (NextReg $06 bits 3 and 4), automap on with no RST entry points,
    /// and blank DivMMC and Multiface ROMs.
    /// </summary>
    private static ZxNextMachine CreateNmiMachine()
    {
        ZxNextMachine machine = CreateProgramAt(0x8000, 0x18, 0xFE); // JR $8000
        machine.Registers.Write(0x50, 0);
        machine.Registers.Write(0x51, 1);
        machine.MemoryBus.WriteCpu(0x0066, 0x00);
        machine.MemoryBus.WriteCpu(0x0067, 0x00);
        machine.Registers.Write(0x06, 0xB8);
        machine.Registers.Write(0x0A, 0x11);
        machine.Registers.Write(0xB8, 0x00);
        machine.MemoryBus.LoadDivMmcRom(new byte[ZxNextPhysicalMemory.PageSize]);
        machine.MemoryBus.LoadMultifaceRom(new byte[ZxNextPhysicalMemory.PageSize]);
        machine.Cpu.SP = 0x9000;
        return machine;
    }

    /// <summary>A page of ROM with the given bytes from $0067, after the NMI's first opcode.</summary>
    private static byte[] NmiRoutine(params byte[] bytes)
    {
        var rom = new byte[ZxNextPhysicalMemory.PageSize];
        bytes.CopyTo(rom, 0x0067);
        return rom;
    }

    /// <summary>
    /// The NMI arbitration of zxnext.vhd, rule by rule: the NextReg $06 gates, configuration
    /// mode, one NMI at a time with later requests lost, the $02 cause bits, the interlocks
    /// between the Multiface and DivMMC, a disabled Multiface, DivMMC paging in at the $0066
    /// fetch through NextReg $BB, NextReg $C0 bit 3 cancelling the stackless return, and RETN
    /// releasing only as ED 45, before it reads the stack.
    /// </summary>
    private static void VerifyNmiArbitrationRules()
    {
        // NextReg $06 bits 3 and 4 gate the requests, but not the cause bits.
        ZxNextMachine gated = CreateNmiMachine();
        gated.Registers.Write(0x06, 0xA0);
        gated.Registers.Write(0x02, 0x0C);
        gated.StepInstruction();
        Require(gated.Registers.Nmi.Source == ZxNextNmiSource.None
                && gated.Cpu.PC == 0x8000
                && (gated.Registers.Read(0x02) & 0x0C) == 0x0C,
            "With NextReg $06 bits 3 and 4 clear a $02 request raised an NMI, or its cause bits were not recorded.");

        // Configuration mode ignores them.
        ZxNextMachine configuring = CreateNmiMachine();
        configuring.Registers.Write(0x03, 0x07);
        configuring.Registers.Write(0x02, 0x08);
        Require(configuring.Registers.Nmi.Source == ZxNextNmiSource.None && !configuring.Multiface.NmiActive,
            "A $02 request latched an NMI in configuration mode.");

        // One NMI at a time. A request while one is active is lost, and once $0066 has been
        // fetched it leaves no cause bit either.
        ZxNextMachine oneShot = CreateNmiMachine();
        oneShot.MemoryBus.LoadMultifaceRom(NmiRoutine(0xED, 0x45)); // RETN
        oneShot.Registers.Write(0x02, 0x08);
        Require(oneShot.Registers.Nmi.Source == ZxNextNmiSource.Multiface
                && oneShot.Registers.Nmi.Phase == ZxNextNmiPhase.Idle
                && oneShot.Multiface.NmiActive,
            "A Multiface request did not latch the NMI and press the Multiface's button.");
        oneShot.Registers.Write(0x02, 0x0C);
        Require(oneShot.Registers.Nmi.Source == ZxNextNmiSource.Multiface
                && !oneShot.DivMmc.NmiActive
                && (oneShot.Registers.Read(0x02) & 0x0C) == 0x0C,
            "A DivMMC request made while a Multiface NMI waited for the CPU was not lost, or its cause was not recorded.");
        oneShot.StepInstruction();
        Require(oneShot.Cpu.PC == 0x0066
                && oneShot.Registers.Nmi.Phase == ZxNextNmiPhase.Fetch
                && !oneShot.Multiface.IsMapped,
            "The CPU did not take the latched NMI at the next instruction boundary.");
        oneShot.StepInstruction();
        Require(oneShot.Cpu.PC == 0x0067
                && oneShot.Multiface.IsMapped
                && oneShot.Registers.Nmi.Phase == ZxNextNmiPhase.Hold,
            "The $0066 fetch did not page the Multiface in and hold the NMI.");
        oneShot.Registers.Write(0x02, 0x00);
        oneShot.Registers.Write(0x02, 0x04);
        Require(oneShot.Registers.Nmi.Source == ZxNextNmiSource.Multiface
                && !oneShot.DivMmc.NmiActive
                && (oneShot.Registers.Read(0x02) & 0x0C) == 0,
            "A request inside the NMI routine was latched or recorded a cause, or a $02 write of 0 did not clear the cause bits.");
        oneShot.StepInstruction();
        Require(oneShot.Cpu.PC == 0x8000
                && !oneShot.Multiface.IsMapped
                && !oneShot.Multiface.NmiActive
                && oneShot.Registers.Nmi.Source == ZxNextNmiSource.None,
            "RETN did not release the Multiface and end the NMI.");
        oneShot.StepInstruction();
        Require(oneShot.Cpu.PC == 0x8000,
            "A request lost inside the NMI routine was queued and taken afterwards.");

        // The Multiface paged in - here by its enable port, which works once an NMI has made
        // it visible - keeps a DivMMC request out.
        _ = oneShot.PortBus.ReadUncontended(0x3F3F);
        Require(oneShot.Multiface.IsMapped && !oneShot.Multiface.NmiActive,
            "Setup: the Multiface enable port did not page it in.");
        oneShot.Registers.Write(0x02, 0x04);
        Require(oneShot.Registers.Nmi.Source == ZxNextNmiSource.None && !oneShot.DivMmc.NmiActive,
            "A DivMMC request was latched while the Multiface was paged in.");
        _ = oneShot.PortBus.ReadUncontended(0x00BF);
        oneShot.Registers.Write(0x02, 0x04);
        Require(oneShot.Registers.Nmi.Source == ZxNextNmiSource.DivMmc && oneShot.DivMmc.NmiActive,
            "A DivMMC request was not latched once the Multiface had paged out.");

        // DivMMC's CONMEM keeps a Multiface request out, so with both requested DivMMC wins.
        ZxNextMachine conmem = CreateNmiMachine();
        conmem.PortBus.WriteUncontended(0x00E3, 0x80);
        conmem.Registers.Write(0x02, 0x0C);
        Require(conmem.Registers.Nmi.Source == ZxNextNmiSource.DivMmc && !conmem.Multiface.NmiActive,
            "With DivMMC's CONMEM on, a Multiface request was latched, or DivMMC's was not.");

        // So does DivMMC holding automap. DivMMC's own request still latches, and its button
        // clears at once: the automap holds the NMI instead.
        ZxNextMachine automapped = CreateNmiMachine();
        automapped.Registers.Write(0xB8, 0x01);
        _ = automapped.MemoryBus.FetchOpcode(0x0000);
        Require(automapped.DivMmc.AutomapHeld, "Setup: the $0000 entry point did not hold automap.");
        automapped.Registers.Write(0x02, 0x08);
        Require(automapped.Registers.Nmi.Source == ZxNextNmiSource.None && !automapped.Multiface.NmiActive,
            "A Multiface request was latched while DivMMC held automap.");
        automapped.Registers.Write(0x02, 0x04);
        Require(automapped.Registers.Nmi.Source == ZxNextNmiSource.DivMmc
                && !automapped.DivMmc.NmiActive
                && automapped.DivMmc.NmiHold,
            "A DivMMC request with automap held did not latch, or left its button latched.");

        // A disabled Multiface (port enable bit 9) ignores its button, so the NMI runs whatever
        // is at $0066 and ends with that fetch.
        ZxNextMachine disabled = CreateNmiMachine();
        disabled.Registers.Write(0x83, 0xFD);
        disabled.Registers.Write(0x02, 0x08);
        Require(disabled.Registers.Nmi.Source == ZxNextNmiSource.Multiface && !disabled.Multiface.NmiActive,
            "A request for a disabled Multiface was not latched, or pressed its button.");

        // Nothing holds that NMI - no interlock stands in DivMMC's way - so only the latch
        // itself makes a DivMMC request now lost.
        disabled.Registers.Write(0x02, 0x04);
        Require(disabled.Registers.Nmi.Source == ZxNextNmiSource.Multiface && !disabled.DivMmc.NmiActive,
            "A DivMMC request made while a disabled Multiface's NMI was latched took the NMI over.");
        StepInstructions(disabled, 2);
        Require(disabled.Cpu.PC == 0x0067
                && !disabled.Multiface.IsMapped
                && !disabled.DivMmc.IsMapped
                && disabled.Registers.Nmi.Source == ZxNextNmiSource.None,
            "A disabled Multiface's NMI did not run $0066 from the memory underneath and end with that fetch.");
        disabled.Registers.Write(0x02, 0x08);
        Require(disabled.Registers.Nmi.Source == ZxNextNmiSource.Multiface,
            "The NMI after a disabled Multiface's was not latched.");

        // DivMMC pages in at the $0066 fetch: delayed with NextReg $BB bit 0 (its reset value),
        // instant with bit 1, and not at all with automap off, when the NMI ends at the fetch.
        foreach ((byte entryPoints, bool automap, bool instant) in new (byte, bool, bool)[]
        {
            (0xCD, true, false),
            (0xCE, true, true),
            (0xCD, false, false)
        })
        {
            ZxNextMachine paging = CreateNmiMachine();
            var rom = new byte[ZxNextPhysicalMemory.PageSize];
            rom[0x0066] = 0x76; // HALT, which only an instant entry executes
            paging.MemoryBus.LoadDivMmcRom(rom);
            paging.Registers.Write(0xBB, entryPoints);
            if (!automap)
            {
                paging.Registers.Write(0x0A, 0x01);
            }

            paging.Registers.Write(0x02, 0x04);
            Require(paging.DivMmc.NmiActive == automap && !paging.DivMmc.IsMapped,
                $"$BB=${entryPoints:X2}, automap {(automap ? "on" : "off")}: the DivMMC request did not latch its button as expected, or paged DivMMC in before $0066.");
            StepInstructions(paging, 2);
            Require(paging.Cpu.IsHalted == instant
                    && paging.DivMmc.IsMapped == automap
                    && !paging.DivMmc.NmiActive
                    && (paging.Registers.Nmi.Source == ZxNextNmiSource.DivMmc) == automap,
                $"$BB=${entryPoints:X2}, automap {(automap ? "on" : "off")}: after the $0066 fetch halted={paging.Cpu.IsHalted}, "
                    + $"mapped={paging.DivMmc.IsMapped}, button={paging.DivMmc.NmiActive}, NMI={paging.Registers.Nmi.Source}.");
        }

        // Clearing NextReg $C0 bit 3 inside the routine sends a stackless NMI's RETN back to the
        // stack, which the acknowledge did not write.
        ZxNextMachine cancel = CreateNmiMachine();
        cancel.Registers.Write(0xC0, 0x08);
        cancel.MemoryBus.LoadDivMmcRom(NmiRoutine(0xED, 0x91, 0xC0, 0x00, 0xED, 0x45)); // NEXTREG $C0,0; RETN
        cancel.MemoryBus.WriteCpu(0x8FFE, 0x34);
        cancel.MemoryBus.WriteCpu(0x8FFF, 0x12);
        cancel.Registers.Write(0x02, 0x04);
        StepInstructions(cancel, 4);
        Require(cancel.Cpu.PC == 0x1234 && cancel.Cpu.SP == 0x9000,
            $"Clearing NextReg $C0 bit 3 inside the NMI routine did not send its RETN to the stack (PC=${cancel.Cpu.PC:X4}).");

        // Only ED 45 releases: the core's RETN decoder does not know the other encodings.
        ZxNextMachine alias = CreateNmiMachine();
        alias.MemoryBus.LoadDivMmcRom(NmiRoutine(0xED, 0x55)); // RETN, as ED 55
        alias.Registers.Write(0x02, 0x04);
        StepInstructions(alias, 3);
        Require(alias.Cpu.PC == 0x8000
                && alias.DivMmc.AutomapHeld
                && alias.Registers.Nmi.Source == ZxNextNmiSource.DivMmc,
            "A RETN encoded other than ED 45 released DivMMC or ended the NMI.");

        // ED 45 releases at its fetch, before it reads the stack: with the stack under DivMMC's
        // RAM window, the return address comes from the RAM the acknowledge pushed it to.
        ZxNextMachine early = CreateNmiMachine();
        early.MemoryBus.LoadDivMmcRom(NmiRoutine(0xED, 0x45));
        early.Cpu.SP = 0x3000;
        early.Registers.Write(0x02, 0x04);
        StepInstructions(early, 3);
        Require(early.Cpu.PC == 0x8000 && !early.DivMmc.IsMapped,
            $"RETN returned to ${early.Cpu.PC:X4}: it read the stack before releasing DivMMC's RAM window.");
    }

    /// <summary>
    /// NextReg values zxnext.vhd gives at power on and keeps or resets on a soft reset: the
    /// $02 reset-type shift register, $0B, $0F, $10, the I2S mirrors $2C-$2E, $7F, the
    /// $82-$85 and $86-$89 port enables with their reset-type bits, and $A2.
    /// </summary>
    private static void VerifyNextRegValueRules()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        (byte Register, byte Value)[] powerOn =
        [
            (0x0B, 0x01), (0x0F, 0x02), (0x10, 0x04), (0x2C, 0x80), (0x2D, 0x00), (0x2E, 0x80),
            (0x7F, 0xFF), (0x82, 0xFF), (0x83, 0xFF), (0x84, 0xFF), (0x85, 0x8F),
            (0x86, 0xFF), (0x87, 0xFF), (0x88, 0xFF), (0x89, 0x8F), (0xA2, 0x02)
        ];
        foreach ((byte register, byte value) in powerOn)
        {
            Require(machine.Registers.Read(register) == value,
                $"NextReg ${register:X2} read ${machine.Registers.Read(register):X2} at power on, not ${value:X2}.");
        }

        Require((machine.Registers.Read(0x02) & 0x03) == 0,
            "NextReg $02 reported a reset type before any soft reset.");

        // What the registers keep of a write.
        machine.Registers.Write(0x0B, 0xFF);
        machine.Registers.Write(0xA2, 0xFF);
        machine.Registers.Write(0x85, 0xFF);
        machine.Registers.Write(0x89, 0x70);
        machine.Registers.Write(0x86, 0x12);
        machine.Registers.Write(0x82, 0x00);
        machine.Registers.Write(0x7F, 0x39);
        Require(machine.Registers.Read(0x0B) == 0xB1
                && machine.Registers.Read(0xA2) == 0xDF
                && machine.Registers.Read(0x85) == 0x8F
                && machine.Registers.Read(0x89) == 0x00
                && machine.Registers.Read(0x86) == 0x12,
            "NextRegs $0B, $A2, $85 or $89 did not keep only their implemented bits, or $86 its write.");

        // The first soft reset after power on reads as hard. $85 bit 7 set resets the internal
        // enables, $89 bit 7 clear the expansion bus ones, and $7F survives.
        machine.Reset(ZxNextResetKind.Soft);
        Require((machine.Registers.Read(0x02) & 0x03) == 0x02,
            "NextReg $02 did not report a hard reset after power on and one soft reset.");
        Require(machine.Registers.Read(0x7F) == 0x39
                && machine.Registers.Read(0x0B) == 0x01
                && machine.Registers.Read(0xA2) == 0x02
                && machine.Registers.Read(0x82) == 0xFF
                && machine.Registers.Read(0x85) == 0x8F
                && machine.Registers.Read(0x86) == 0xFF
                && machine.Registers.Read(0x89) == 0x0F,
            "A soft reset did not keep $7F, reset $0B and $A2, or reset the port enables by their reset-type bits.");

        // The other way round: the enables survive.
        machine.Registers.Write(0x85, 0x0F);
        machine.Registers.Write(0x82, 0x00);
        machine.Registers.Write(0x89, 0x8F);
        machine.Registers.Write(0x86, 0x12);
        machine.Reset(ZxNextResetKind.Soft);
        Require((machine.Registers.Read(0x02) & 0x03) == 0x01
                && machine.Registers.Read(0x82) == 0x00
                && machine.Registers.Read(0x85) == 0x0F
                && machine.Registers.Read(0x86) == 0x12,
            "A second soft reset did not read as soft, or reset port enables its reset-type bits protect.");
        machine.Reset(ZxNextResetKind.Soft);
        Require((machine.Registers.Read(0x02) & 0x03) == 0x01,
            "A third soft reset did not read as soft.");

        // A hard reset reloads the FPGA: power-on values again.
        machine.Reset(ZxNextResetKind.Hard);
        Require((machine.Registers.Read(0x02) & 0x03) == 0
                && machine.Registers.Read(0x7F) == 0xFF
                && machine.Registers.Read(0x82) == 0xFF
                && machine.Registers.Read(0x85) == 0x8F
                && machine.Registers.Read(0x86) == 0xFF
                && machine.Registers.Read(0x89) == 0x8F,
            "A hard reset did not bring NextRegs $02, $7F and $82-$89 back to their power-on values.");
    }

    /// <summary>
    /// Readies the boundary workload for a NextReg $02 DivMMC NMI that pages DivMMC in at its
    /// $0066 fetch: out of configuration mode, where NMI requests are ignored, with the DRIVE
    /// button's NMI enabled (NextReg $06 bit 4), automap on ($0A bit 4) and DivMMC's NMI entry
    /// point instant ($BB bit 1, not bit 0).
    /// </summary>
    private static void ArmDivMmcNmi(ZxNextMachine machine)
    {
        machine.Registers.Write(0x03, 0x03);
        machine.Registers.Write(0x06, (byte)(machine.Registers.Read(0x06) | 0x10));
        machine.Registers.Write(0x0A, (byte)(machine.Registers.Read(0x0A) | 0x10));
        machine.Registers.Write(0xBB, (byte)((machine.Registers.Read(0xBB) & ~0x01) | 0x02));
    }

    /// <summary>
    /// A machine of the given NextReg $03 machine type with four marked ROMs: ROM n reads
    /// $A0 + n at $0123. Written from configuration mode, which the write then leaves.
    /// </summary>
    private static ZxNextMachine CreateRomMachine(byte machineType)
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        for (int bank = 0; bank < 4; bank++)
        {
            var rom = new byte[ZxNextMemoryBus.RomBankSize];
            rom[0x0123] = (byte)(0xA0 + bank);
            machine.MemoryBus.LoadRomBank(bank, rom);
        }

        machine.Registers.Write(0x03, machineType);
        return machine;
    }

    /// <summary>
    /// Moves a machine's clock and raster to the first contended cycle of the display's first
    /// line, less <paramref name="tstatesEarlier"/>, so an access made that much later waits.
    /// </summary>
    private static void MoveToContentionWindow(ZxNextMachine machine, int tstatesEarlier = 0)
    {
        machine.UlaTiming.SynchronizeProfile();
        ZxNextUlaTimingProfile profile = machine.UlaTiming.Profile;
        ulong sample = checked(((ulong)profile.FirstDisplayLine * (ulong)profile.PixelsPerLine
                + (ulong)(profile.ContentionCounterStartHorizontal + 3))
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        ulong tstate = ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate;
        ulong cycleStart = profile.IsPlus3 ? sample - tstate : sample;
        int tstates = checked((int)((cycleStart / tstate) - (ulong)tstatesEarlier));
        machine.Clock.AdvanceCpuTstates(tstates);
        machine.Video.AdvanceMasterTicks((ulong)tstates * tstate);
    }

    /// <summary>
    /// Runs one instruction at $8000 from the first contended cycle of the display, in the
    /// given machine timing, with the tilemap (and so instruction-time batching) on or off,
    /// and returns how many T-states it took.
    /// </summary>
    private static int TimeInstructionInWindow(
        byte timing,
        bool tilemap,
        Action<ZxNextMachine> setUp,
        int tstatesBeforeAccess,
        params byte[] instruction)
    {
        ZxNextMachine machine = CreateProgramAt(0x8000, instruction);
        machine.Registers.Write(0x03, timing);
        if (tilemap)
        {
            machine.Registers.Write(0x6B, 0x80);
        }

        setUp(machine);
        MoveToContentionWindow(machine, tstatesBeforeAccess);
        ulong before = machine.Cpu.Cyc;
        machine.StepInstruction();
        return checked((int)(machine.Cpu.Cyc - before));
    }

    /// <summary>
    /// Memory, paging and contention as zxnext.vhd does them: ROM selection by machine type,
    /// MMU pages $E0-$FF showing the ROM in slots 0 and 1, contention by the slot's MMU page
    /// (internal cycles included), the I/O cycle's high-byte term, NextReg $04's seven bits,
    /// the Layer 2 aperture's eight-bit page, $DFFD bits 4 and 6, NextReg $08 bit 6 latched
    /// at the end of the line's contention window, and the ROM chosen in all-RAM mode.
    /// </summary>
    private static void VerifyMemoryPagingContentionRules()
    {
        // ROM selection by machine type: a 48K has ROM 0 only, a 128K (and the Pentagon type)
        // takes $7FFD bit 4, a +3 takes $1FFD bit 2 as well.
        foreach (byte type in new byte[] { 1, 2, 3, 4 })
        {
            for (int selection = 0; selection < 4; selection++)
            {
                ZxNextMachine machine = CreateRomMachine(type);
                machine.Paging.WritePort1Ffd((byte)((selection & 0x02) << 1));
                machine.Paging.WritePort7Ffd((byte)((selection & 0x01) << 4));
                int expected = type switch { 1 => 0, 3 => selection, _ => selection & 0x01 };
                Require(machine.MemoryBus.Read(0x0123) == 0xA0 + expected,
                    $"Machine type {type} with ports selecting ROM {selection} showed ROM {machine.MemoryBus.Read(0x0123) - 0xA0}, not {expected}.");
            }

            // NextReg $8C's lock bits: ignored by a 48K, bit 5 alone on a 128K, both on a +3.
            for (int locks = 1; locks < 4; locks++)
            {
                ZxNextMachine machine = CreateRomMachine(type);
                machine.Registers.Write(0x8C, (byte)(locks << 4));
                int expected = type switch { 1 => 0, 3 => locks, _ => locks >> 1 };
                Require(machine.MemoryBus.Read(0x0123) == 0xA0 + expected,
                    $"Machine type {type} with ROM locks {locks} showed ROM {machine.MemoryBus.Read(0x0123) - 0xA0}, not {expected}.");
            }
        }

        // A 48K's alternate ROM is the 48K one unless the locks are exactly 01.
        ZxNextMachine alternate48 = CreateRomMachine(1);
        bool alternateOk = alternate48.MemoryBus.EffectiveAlternateRomBank == 1;
        alternate48.Registers.Write(0x8C, 0x10);
        alternateOk &= alternate48.MemoryBus.EffectiveAlternateRomBank == 0;
        alternate48.Registers.Write(0x8C, 0x30);
        alternateOk &= alternate48.MemoryBus.EffectiveAlternateRomBank == 1;
        Require(alternateOk, "A 48K did not choose its alternate ROM as sram_alt_128_n does.");

        // DivMMC's ROM 3 entry points ($3Dxx here) follow the machine type's 48K BASIC ROM:
        // always on a 48K, ROM 1 on a 128K, ROM 3 on a +3.
        foreach ((byte type, byte port1Ffd, byte port7Ffd, bool rom3) in new (byte, byte, byte, bool)[]
        {
            (1, 0x00, 0x00, true),
            (2, 0x00, 0x00, false),
            (2, 0x00, 0x10, true),
            (3, 0x00, 0x10, false),
            (3, 0x04, 0x10, true)
        })
        {
            ZxNextMachine machine = CreateRomMachine(type);
            machine.Paging.WritePort1Ffd(port1Ffd);
            machine.Paging.WritePort7Ffd(port7Ffd);
            machine.Registers.Write(0x0A, 0x11);
            _ = machine.MemoryBus.FetchOpcode(0x3D00);
            Require(machine.DivMmc.AutomapHeld == rom3,
                $"Machine type {type} with $1FFD=${port1Ffd:X2}, $7FFD=${port7Ffd:X2}: the $3D00 entry point {(rom3 ? "did not fire" : "fired")}.");
        }

        // ... and not under a Layer 2 read mapping over the ROM.
        ZxNextMachine hidden = CreateRomMachine(1);
        hidden.Registers.Write(0x0A, 0x11);
        hidden.PortBus.WriteUncontended(0x123B, 0x04);
        _ = hidden.MemoryBus.FetchOpcode(0x3D00);
        Require(!hidden.DivMmc.AutomapHeld,
            "A ROM 3 entry point fired under a Layer 2 read mapping.");

        // MMU pages $E0-$FF all show the ROM in slots 0 and 1, and reach nothing elsewhere.
        ZxNextMachine highPages = CreateRomMachine(3);
        highPages.Registers.Write(0x50, 0xE5);
        highPages.Registers.Write(0x52, 0xE5);
        Require(highPages.MemoryBus.Read(0x0123) == 0xA0
                && highPages.MemoryBus.GetMapping(0x0123).IsRom
                && highPages.MemoryBus.Read(0x4123) == 0xFF,
            "MMU page $E5 did not show the ROM in slot 0, or reached something in slot 2.");

        // Contention follows the slot's MMU page, not what is paged over it (+3 timing).
        ZxNextMachine layer2Over = CreateRomMachine(3);
        layer2Over.PortBus.WriteUncontended(0x123B, 0xC1);
        MoveToContentionWindow(layer2Over);
        ulong before = layer2Over.Cpu.Cyc;
        layer2Over.MemoryBus.WriteCpu(0x4000, 0x00);
        Require(layer2Over.Cpu.Cyc > before && layer2Over.MemoryBus.GetMapping(0x4000).IsContended,
            "A Layer 2 write mapping over bank 5's MMU slot was not contended.");

        ZxNextMachine layer2Under = CreateRomMachine(3);
        layer2Under.Registers.Write(0x12, 0x05);
        layer2Under.PortBus.WriteUncontended(0x123B, 0x01);
        MoveToContentionWindow(layer2Under);
        before = layer2Under.Cpu.Cyc;
        layer2Under.MemoryBus.WriteCpu(0x0123, 0x00);
        Require(layer2Under.Cpu.Cyc == before && layer2Under.MemoryBus.ReadPhysicalRam(10, 0x0123) == 0x00,
            "A Layer 2 write mapping of bank 5 over the ROM slot was contended.");

        ZxNextMachine divMmcOver = CreateRomMachine(3);
        divMmcOver.Registers.Write(0x50, 10);
        divMmcOver.PortBus.WriteUncontended(0x00E3, 0x80);
        MoveToContentionWindow(divMmcOver);
        before = divMmcOver.Cpu.Cyc;
        _ = divMmcOver.MemoryBus.Read(0x0123);
        Require(divMmcOver.Cpu.Cyc > before,
            "DivMMC paged over a contended MMU slot hid the contention.");

        // Internal cycles go by the 8 KiB slot (128K timing): EX (SP),HL with SP in slot 3.
        // And identically with the tilemap on, where instruction time is batched.
        int[] SlotTimes(bool tilemap)
        {
            int OneSlot(byte slot2, byte slot3) => TimeInstructionInWindow(0xA3, tilemap, machine =>
            {
                machine.Registers.Write(0x52, slot2);
                machine.Registers.Write(0x53, slot3);
                machine.Cpu.SP = 0x6000;
            }, 0, 0xE3);
            return [OneSlot(4, 4), OneSlot(2, 4), OneSlot(4, 2)];
        }

        int[] slotTimes = SlotTimes(tilemap: false);
        int[] batchedSlotTimes = SlotTimes(tilemap: true);
        Require(slotTimes[0] == slotTimes[1] && slotTimes[2] > slotTimes[0]
                && batchedSlotTimes.SequenceEqual(slotTimes),
            $"EX (SP),HL with SP in an uncontended slot took {slotTimes[1]} T-states when the other half of its "
                + $"16 KiB was contended, {slotTimes[0]} when neither was, {slotTimes[2]} when its own was; "
                + $"with instruction time batched {string.Join("/", batchedSlotTimes)}.");

        // The I/O cycle's high byte counts as an address (48K timing). IN A,(C) with its I/O
        // cycle starting at hc 3, where the wait pattern is 6,5,4,3,2,1,0,0, takes the
        // classic C:1,C:1,C:1,C:1 = 24, N:4 = 12, C:1,C:3 = 18 and N:1,C:3 = 17. And the same
        // with instruction time batched.
        int[] InTimes(bool tilemap)
        {
            int In(ushort port) => TimeInstructionInWindow(0x91, tilemap, machine =>
            {
                machine.Cpu.B = (byte)(port >> 8);
                machine.Cpu.C = (byte)port;
            }, 8, 0xED, 0x78);
            return [In(0x40FF), In(0x80FF), In(0x40FE), In(0x80FE)];
        }

        int[] expectedIn = [24, 12, 18, 17];
        int[] inTimes = InTimes(tilemap: false);
        int[] batchedInTimes = InTimes(tilemap: true);
        Require(inTimes.SequenceEqual(expectedIn) && batchedInTimes.SequenceEqual(expectedIn),
            $"IN A,(C) to $40FF, $80FF, $40FE and $80FE took {string.Join("/", inTimes)} T-states "
                + $"({string.Join("/", batchedInTimes)} with instruction time batched), not 24/12/18/17.");

        // NextReg $04 keeps seven bits on an issue 4 board.
        ZxNextMachine configBank = ZxNextMachineFactory.CreateFoundationMachine();
        configBank.Registers.Write(0x04, 0x85);
        Require(configBank.MemoryBus.ConfigurationBank == 0x05,
            "NextReg $04 kept bit 7.");

        // The Layer 2 aperture's page has eight bits: bank 127 plus one wraps to bank 0.
        ZxNextMachine wrap = CreateRomMachine(3);
        wrap.Registers.Write(0x12, 0x7F);
        wrap.PortBus.WriteUncontended(0x123B, 0xC1);
        wrap.MemoryBus.WriteCpu(0x4123, 0x6E);
        Require(wrap.MemoryBus.ReadPhysicalRam(0, 0x0123) == 0x6E,
            "The Layer 2 aperture did not wrap bank 128 to bank 0.");

        // $DFFD keeps bits 4 and 6, which NextReg $8E leaves alone and the Multiface +3 reads.
        ZxNextMachine dffd = CreateRomMachine(3);
        dffd.Paging.WritePortDFfd(0xFF);
        bool dffdOk = dffd.Paging.PortDFfd == 0x5F && dffd.Paging.SelectedRamBank == 0x78;
        dffd.Registers.Write(0x8E, 0x08);
        dffdOk &= dffd.Paging.PortDFfd == 0x50;
        dffd.Multiface.PressButton();
        Require(dffdOk && dffd.PortBus.ReadUncontended(0xDF3F) == 0x50,
            "$DFFD did not keep bits 4 and 6 through NextReg $8E, or the Multiface +3 did not read them.");

        // NextReg $08 bit 6 waits for the end of the line's contention window (+3 timing).
        ZxNextMachine latch = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextUlaTimingProfile latchProfile = latch.UlaTiming.Profile;
        int inWindow = checked((int)((((ulong)latchProfile.FirstDisplayLine * (ulong)latchProfile.PixelsPerLine
                + (ulong)(latchProfile.ContentionCounterStartHorizontal + 8))
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel) / ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate));
        latch.Clock.AdvanceCpuTstates(inWindow);
        latch.Registers.Write(0x08, 0x50);
        bool latchOk = !latch.Registers.ContentionDisabled
            && (latch.Registers.Read(0x08) & 0x40) == 0
            && latch.Contention.Enabled;
        latch.Clock.AdvanceCpuTstates(100);
        latchOk &= !latch.Registers.ContentionDisabled;
        latch.Clock.AdvanceCpuTstates(30);
        latchOk &= latch.Registers.ContentionDisabled
            && (latch.Registers.Read(0x08) & 0x40) != 0
            && !latch.Contention.Enabled;
        latch.Registers.Write(0x08, 0x10);
        latchOk &= !latch.Registers.ContentionDisabled;
        Require(latchOk,
            "NextReg $08 bit 6 took effect inside the line's contention window, never, or late outside it.");

        // The ROM chosen in all-RAM mode is the one an MMU page of $FF then shows.
        ZxNextMachine allRam = CreateRomMachine(3);
        allRam.Paging.WritePort1Ffd(0x01);
        allRam.Paging.WritePort7Ffd(0x10);
        allRam.Registers.Write(0x50, 0xFF);
        Require(allRam.MemoryBus.Read(0x0123) == 0xA1,
            "A ROM chosen while in all-RAM mode was not the one an MMU page of $FF showed.");

        // A restore brings the machine type back to the ROM selection too: a 48K saved with
        // $7FFD bit 4 set, restored into a +3, still shows ROM 0.
        ZxNextMachine saved48 = CreateRomMachine(1);
        saved48.Paging.WritePort7Ffd(0x10);
        ZxNextMachine restoredInto = CreateRomMachine(3);
        ZxNextMachineSnapshotService.Restore(restoredInto, ZxNextMachineSnapshotService.Capture(saved48));
        Require(restoredInto.MemoryBus.MachineType == 1 && restoredInto.MemoryBus.Read(0x0123) == 0xA0,
            "A restored 48K did not take its machine type back to the ROM selection.");
    }

    /// <summary>Steps a machine until its ULA timing starts the next frame.</summary>
    private static void RunToNextFrame(ZxNextMachine machine)
    {
        ulong frame = machine.UlaTiming.FrameNumber;
        while (machine.UlaTiming.FrameNumber == frame)
        {
            machine.StepInstruction();
        }
    }

    /// <summary>
    /// Renders a frame twice, through the snapshot reference and the live raster, requires the
    /// two to agree, and returns the picture.
    /// </summary>
    private static int[] RenderBothWays(ZxNextMachine machine, string scene)
    {
        machine.Video.RenderFrame(0);
        int[] reference = (int[])machine.Video.FrameBuffer.Clone();
        machine.Video.Reset();
        machine.Video.AdvanceMasterTicks(machine.UlaTiming.MasterTicksPerFrame * 2);
        int[] live = (int[])machine.Video.FrameBuffer.Clone();
        int first = live.AsSpan().CommonPrefixLength(reference);
        Require(first == live.Length,
            $"{scene}: the live raster diverged from the reference at output "
                + $"({first % ZxNextVideoRenderer.FrameWidth}, {first / ZxNextVideoRenderer.FrameWidth}).");
        return live;
    }

    /// <summary>
    /// A scene for the mixer rules. Display pixel (0,0): bright blue ULA ink over Layer 2 index 1
    /// (red). Display pixel (1,0): black ULA paper over transparent Layer 2. The fallback colour
    /// ($4A) is green, so it cannot be mistaken for a layer.
    /// </summary>
    private static ZxNextMachine CreateMixerScene()
    {
        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine();
        machine.MemoryBus.WritePhysicalRam(10, 0x0000, 0x80);
        machine.MemoryBus.WritePhysicalRam(10, 0x1800, 0x41);
        WriteLayer2Palette(machine, 0x01, 0x1C0);
        WriteLayer2Byte(machine, 8, 0x0000, 0x01);
        WriteLayer2Byte(machine, 8, 0x0001, 0xE3);
        machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x02);
        machine.Registers.Write(0x4A, 0x1C);
        return machine;
    }

    /// <summary>
    /// A 40x32 tilemap of tile 0 over bank 5 (map at $6000, tiles at $7000), tile 0 all
    /// tilemap palette index 1, coloured cyan; in text mode its glyph is all ink.
    /// </summary>
    private static void AddCyanTilemap(ZxNextMachine machine, bool textMode)
    {
        for (int offset = 0; offset < 32; offset++)
        {
            machine.MemoryBus.WritePhysicalRam(11, 0x1000 + offset, textMode ? (byte)0xFF : (byte)0x11);
        }

        machine.Registers.Write(0x6E, 0x20);
        machine.Registers.Write(0x6F, 0x30);
        machine.Registers.Write(0x43, 0x30);
        machine.Registers.Write(0x40, 0x01);
        machine.Registers.Write(0x44, 0x03F >> 1);
        machine.Registers.Write(0x44, 0x03F & 0x01);
        machine.Registers.Write(0x6B, textMode ? (byte)0x88 : (byte)0x80);
    }

    /// <summary>
    /// zxnext.vhd's stage 2 mixer and zxula.vhd's border, case by case: the blend operand of
    /// orders 6 and 7 per NextReg $68 bits 6:5 (and its disregard of the ULA enable), every
    /// opaque Layer 2 pixel blended, a sprite over the ULA's border in LUS/USL/ULS, the
    /// stencil, text-mode tilemap pixels equal to $14, the Timex hi-res border, Layer 2 scroll
    /// beyond its documented range, a tilemap disabled mid-line, and the $28, $03 bit 7 and
    /// $6B bit 2 readbacks. Every scene is also rendered live and held to the reference.
    /// </summary>
    private static void VerifyCompositorRules()
    {
        int pixel0 = (ZxNextVideoRenderer.UlaTop * ZxNextVideoRenderer.FrameWidth) + ZxNextVideoRenderer.UlaLeft;
        int pixel1 = pixel0 + 2;
        int red = ZxNextPaletteDevice.ToArgb(0x1C0);
        int blue = ZxNextPaletteDevice.ToArgb(0x007);
        int black = ZxNextPaletteDevice.ToArgb(0x000);
        int green = ZxNextPaletteDevice.ToArgb(0x038);
        int cyan = ZxNextPaletteDevice.ToArgb(0x03F);
        int magenta = ZxNextPaletteDevice.ToArgb(0x1C7);
        int darkMagenta = ZxNextPaletteDevice.ToArgb(0x082);

        // Orders 6 and 7. The tilemap is off, so its below flag is NOT $6B bit 0: below.
        foreach ((byte order, byte ulaControl, int expected0, int expected1, string what) in new (byte, byte, int, int, string)[]
        {
            (6, 0x00, magenta, green, "ULA operand: blended where Layer 2 is opaque, unseen elsewhere"),
            (7, 0x00, darkMagenta, green, "ULA operand, add minus 5"),
            (6, 0x20, blue, black, "no operand: the ULA above"),
            (6, 0x40, magenta, green, "ULA/tilemap combination as operand"),
            (6, 0x60, blue, black, "tilemap operand: the ULA above"),
            (6, 0x80, magenta, green, "ULA disabled: still the operand"),
            (6, 0xA0, red, green, "ULA disabled, no operand: Layer 2 alone")
        })
        {
            ZxNextMachine machine = CreateMixerScene();
            machine.Registers.Write(0x15, (byte)(order << 2));
            machine.Registers.Write(0x68, ulaControl);
            string scene = $"Order {order}, $68=${ulaControl:X2} ({what})";
            int[] frame = RenderBothWays(machine, scene);
            Require(frame[pixel0] == expected0 && frame[pixel1] == expected1,
                $"{scene}: got ${frame[pixel0]:X8}/${frame[pixel1]:X8}, expected ${expected0:X8}/${expected1:X8}.");
        }

        int yellow = ZxNextPaletteDevice.ToArgb(0x1F8);

        // With a sprite over the pixel the layer above it and the layer below it differ:
        // with no operand the ULA is above (the tilemap is off, so below); with the ULA as
        // operand nothing is above, and the sprite shows.
        foreach ((byte ulaControl, int expected0, int expected1) in new (byte, int, int)[]
        {
            (0x20, blue, black),
            (0x00, yellow, yellow)
        })
        {
            ZxNextMachine machine = CreateMixerScene();
            WriteSolidSpritePattern8(machine, 0, 0x02);
            WriteSpritePalette(machine, 0x02, 0x1F8);
            WriteSpriteAttributes(machine, 0, 32, 32, 0, 0x80, 0);
            machine.Registers.Write(0x15, 0x19);
            machine.Registers.Write(0x68, ulaControl);
            string scene = $"Order 6, $68=${ulaControl:X2}, a sprite over the pixel";
            int[] frame = RenderBothWays(machine, scene);
            Require(frame[pixel0] == expected0 && frame[pixel1] == expected1,
                $"{scene}: got ${frame[pixel0]:X8}/${frame[pixel1]:X8}, expected ${expected0:X8}/${expected1:X8}.");
        }

        // The single- and two-layer compositions in order 6, where the operand hides a layer:
        // the ULA alone, the tilemap alone as the operand, and the tilemap as the operand with
        // a sprite.
        ZxNextMachine ulaAlone = CreateMixerScene();
        ulaAlone.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x00);
        ulaAlone.Registers.Write(0x15, 0x18);
        int[] ulaAloneFrame = RenderBothWays(ulaAlone, "Order 6, the ULA alone");
        Require(ulaAloneFrame[pixel0] == green && ulaAloneFrame[pixel1] == green,
            "In order 6 the ULA alone, the blend operand with no Layer 2 to blend, was not hidden.");

        ZxNextMachine tilemapAlone = CreateMixerScene();
        tilemapAlone.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x00);
        AddCyanTilemap(tilemapAlone, textMode: false);
        tilemapAlone.Registers.Write(0x15, 0x18);
        tilemapAlone.Registers.Write(0x68, 0xE0);
        int[] tilemapAloneFrame = RenderBothWays(tilemapAlone, "Order 6, the tilemap alone as operand");
        Require(tilemapAloneFrame[pixel0] == green,
            "In order 6 the tilemap alone, as the blend operand, was not hidden.");

        ZxNextMachine tilemapSprite = CreateMixerScene();
        tilemapSprite.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x00);
        AddCyanTilemap(tilemapSprite, textMode: false);
        WriteSolidSpritePattern8(tilemapSprite, 0, 0x02);
        WriteSpritePalette(tilemapSprite, 0x02, 0x1F8);
        WriteSpriteAttributes(tilemapSprite, 0, 32, 32, 0, 0x80, 0);
        tilemapSprite.Registers.Write(0x15, 0x19);
        tilemapSprite.Registers.Write(0x68, 0xC0);
        int[] tilemapSpriteFrame = RenderBothWays(tilemapSprite, "Order 6, the tilemap as operand under a sprite");
        Require(tilemapSpriteFrame[pixel0] == yellow,
            "In order 6 the tilemap as the blend operand was drawn over a sprite.");

        // LUS, USL, ULS: a sprite shows over the ULA's border, not over its display. In order
        // 6 the ULA is only the operand, so the sprite shows over both.
        foreach (byte order in new byte[] { 3, 4, 5, 6 })
        {
            foreach (bool layer2 in new[] { true, false })
            {
                ZxNextMachine machine = CreateMixerScene();
                if (!layer2)
                {
                    machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x00);
                }

                WriteSolidSpritePattern8(machine, 0, 0x02);
                WriteSpritePalette(machine, 0x02, 0x1F8);
                WriteSpriteAttributes(machine, 0, 0, 0, 0, 0x80, 0);
                WriteSpriteAttributes(machine, 1, 32, 32, 0, 0x80, 0);
                machine.Registers.Write(0x15, (byte)(0x03 | (order << 2)));
                string scene = $"Order {order}, Layer 2 {(layer2 ? "on" : "off")}, sprites over border and display";
                int[] frame = RenderBothWays(machine, scene);
                int expectedDisplay = order == 6 ? yellow : order == 3 && layer2 ? red : blue;
                Require(frame[0] == yellow && frame[pixel0] == expectedDisplay,
                    $"{scene}: the border pixel was ${frame[0]:X8} (sprite ${yellow:X8}), the display pixel ${frame[pixel0]:X8}.");
            }
        }

        // The stencil: where both are opaque, ULA AND tilemap; otherwise transparent.
        foreach (bool stencil in new[] { false, true })
        {
            ZxNextMachine machine = CreateMixerScene();
            machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x00);
            AddCyanTilemap(machine, textMode: false);
            machine.Registers.Write(0x68, stencil ? (byte)0x01 : (byte)0x00);
            string scene = $"Stencil {(stencil ? "on" : "off")}";
            int[] frame = RenderBothWays(machine, scene);
            Require(stencil
                    ? frame[pixel0] == blue && frame[pixel1] == black
                    : frame[pixel0] == cyan && frame[pixel1] == cyan,
                $"{scene}: got ${frame[pixel0]:X8}/${frame[pixel1]:X8}.");
        }

        // Text-mode tilemap pixels equal to $14 are transparent; graphics-mode ones are not.
        foreach ((bool textMode, byte transparency, int expected) in new (bool, byte, int)[]
        {
            (true, 0x1F, blue),
            (true, 0xE3, cyan),
            (false, 0x1F, cyan)
        })
        {
            ZxNextMachine machine = CreateMixerScene();
            machine.PortBus.WriteUncontended(ZxNextLayer2Device.ControlPort, 0x00);
            AddCyanTilemap(machine, textMode);
            machine.Registers.Write(0x14, transparency);
            string scene = $"{(textMode ? "Text" : "Graphics")}-mode tilemap with $14=${transparency:X2}";
            int[] frame = RenderBothWays(machine, scene);
            Require(frame[pixel0] == expected,
                $"{scene}: got ${frame[pixel0]:X8}, expected ${expected:X8}.");
        }

        // Timex hi-res: the border is the bright paper of the inverted ink, not the $FE colour.
        ZxNextMachine hires = CreateMixerScene();
        hires.PortBus.WriteUncontended(0x00FE, 0x00);
        hires.PortBus.WriteUncontended(0x00FF, 0x16);
        int hiresBorder = ZxNextPaletteDevice.ToArgb(
            hires.Registers.Palettes.GetUlaPaperPaletteColor(0x40 | (5 << 3) | 2));
        int[] hiresFrame = RenderBothWays(hires, "Timex hi-res border");
        Require(hiresFrame[0] == hiresBorder && hiresBorder != black,
            $"The Timex hi-res border was ${hiresFrame[0]:X8}, not the bright paper ${hiresBorder:X8}.");

        // Layer 2 scrolled past its documented range reads past the picture, as layer2.vhd's
        // one-step wrap does: row 184 scrolled by 200 is row 192, column 300 by 400 is 380.
        ZxNextMachine scrolledY = CreateMixerScene();
        WriteLayer2Palette(scrolledY, 0x02, 0x038);
        WriteLayer2Byte(scrolledY, 8, 0xC000, 0x02);
        scrolledY.Registers.Write(0x17, 200);
        int[] scrolledYFrame = RenderBothWays(scrolledY, "Layer 2 scrolled down 200");
        Require(scrolledYFrame[pixel0 + (184 * ZxNextVideoRenderer.FrameWidth)] == green,
            "Layer 2 row 184 scrolled by 200 did not read row 192.");

        ZxNextMachine scrolledX = CreateMixerScene();
        WriteLayer2Palette(scrolledX, 0x02, 0x038);
        scrolledX.Registers.Write(0x70, 0x10);
        SetLayer2Clip(scrolledX, 0, 159, 0, 255);
        WriteLayer2Byte(scrolledX, 8, 60 << 8, 0x01);
        WriteLayer2Byte(scrolledX, 8, 380 << 8, 0x02);
        scrolledX.Registers.Write(0x16, 400 & 0xFF);
        scrolledX.Registers.Write(0x71, 0x01);
        int[] scrolledXFrame = RenderBothWays(scrolledX, "320-wide Layer 2 scrolled right 400");
        Require(scrolledXFrame[600] == green,
            "320-wide Layer 2 column 300 scrolled by 400 did not read column 380.");

        // A tilemap disabled mid-line is gone from the next pixel, timed fetch or not.
        ZxNextMachine midLine = CreateMixerScene();
        for (int address = 0; address < 0xC000; address++)
        {
            WriteLayer2Byte(midLine, 8, address, 0xE3);
        }

        AddCyanTilemap(midLine, textMode: false);
        midLine.Video.Reset();
        ZxNextUlaTimingProfile profile = midLine.UlaTiming.Profile;
        ulong disableTick = checked(
            ((ulong)(profile.FirstDisplayLine + 100) * (ulong)profile.PixelsPerLine
                + (ulong)(profile.DisplayStartHorizontal + 100))
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        midLine.Video.AdvanceMasterTicks(disableTick);
        midLine.Registers.Write(0x6B, 0x00);
        midLine.Video.AdvanceMasterTicks(profile.MasterTicksPerFrame - disableTick);
        int row100 = pixel0 + (100 * ZxNextVideoRenderer.FrameWidth);
        int[] midLineFrame = midLine.Video.FrameBuffer;
        Require(midLineFrame[row100 + (90 * 2)] == cyan
                && midLineFrame[row100 + (100 * 2)] == black
                && midLineFrame[row100 + (104 * 2)] == black,
            "A tilemap disabled mid-line was still drawn from its timed fetch.");

        // The border colour taken for a group survives a restore made inside that group after
        // a $FE write: the rest of the group keeps the old colour in both machines.
        ZxNextMachine borderOriginal = ZxNextMachineFactory.CreateFoundationMachine();
        borderOriginal.PortBus.WriteUncontended(0x00FE, 0x02);
        borderOriginal.Video.Reset();
        profile = borderOriginal.UlaTiming.Profile;
        ulong midGroupTick = checked(
            ((ulong)profile.VisibleFirstLine * (ulong)profile.PixelsPerLine
                + (ulong)profile.VisibleStartHorizontal + 10)
            * ZxNextUlaTimingProfile.MasterTicksPerRasterPixel);
        borderOriginal.Video.AdvanceMasterTicks(midGroupTick);
        borderOriginal.PortBus.WriteUncontended(0x00FE, 0x01);
        ZxNextMachineSnapshot midGroup = ZxNextMachineSnapshotService.Capture(borderOriginal);
        ZxNextMachine borderRestored = ZxNextMachineFactory.CreateFoundationMachine();
        ZxNextMachineSnapshotService.Restore(borderRestored, midGroup);
        borderOriginal.Video.AdvanceMasterTicks(profile.MasterTicksPerFrame - midGroupTick);
        borderRestored.Video.AdvanceMasterTicks(profile.MasterTicksPerFrame - midGroupTick);
        int[] borderOriginalFrame = borderOriginal.Video.FrameBuffer;
        int[] borderRestoredFrame = borderRestored.Video.FrameBuffer;
        Require(borderRestoredFrame[(15 * 2) + 1] == borderOriginalFrame[(15 * 2) + 1]
                && borderOriginalFrame[(15 * 2) + 1] != borderOriginalFrame[16 * 2],
            "A machine restored inside a border group did not keep the colour taken for that group.");

        // Readbacks: $28 the first byte of a nine-bit write, $03 bit 7 the half-done write,
        // $6B every bit.
        ZxNextMachine readbacks = ZxNextMachineFactory.CreateFoundationMachine();
        readbacks.Registers.Write(0x40, 0x10);
        readbacks.Registers.Write(0x44, 0x5A);
        bool halfDone = readbacks.Registers.Read(0x28) == 0x5A && (readbacks.Registers.Read(0x03) & 0x80) != 0;
        readbacks.Registers.Write(0x44, 0x01);
        bool done = (readbacks.Registers.Read(0x03) & 0x80) == 0 && readbacks.Registers.Read(0x28) == 0x5A;
        readbacks.Registers.Write(0x6B, 0x7F);
        Require(halfDone && done && readbacks.Registers.Read(0x6B) == 0x7F,
            "NextReg $28, $03 bit 7 or $6B bit 2 did not read back as zxnext.vhd stores them.");
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
