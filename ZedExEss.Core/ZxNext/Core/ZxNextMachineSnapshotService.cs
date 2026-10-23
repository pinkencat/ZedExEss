using ZedExEss.ZxNext.Input;

namespace ZedExEss.ZxNext.Core;

/// <summary>Captures and restores a whole ZX Spectrum Next.</summary>
/// <remarks>
/// Restore order matters and is deliberate. RAM and the legacy paging ports go back first,
/// then the MMU pages, which on a Next override what those ports selected; the register
/// file follows, because the audio routing it reconfigures reads from it; the raster
/// cadence and the CPU go last, so the beam and the processor agree about where they are.
///
/// The peripherals go back between the register file and the beam. DivMMC and Multiface
/// first, because the memory map the renderer and the CPU are about to read depends on
/// whether their windows are paged in; then audio, which has to land after the register
/// file has finished reconfiguring its routing; then the DMA controller before the
/// arbiter, so that a restored bus grant has a controller able to say which endpoint cycle
/// it owes.
///
/// Mounted media goes back with the DivMMC latches, and is the one part that can report
/// having declined: a card is a real file on the host, so a restore matches the medium it was
/// captured from before writing a sector into it, and says so when it will not. The return
/// value carries those notes to whoever is loading the state.
///
/// What is <b>not</b> covered is the Copper, which does not exist yet (COP-001).
/// </remarks>
public static class ZxNextMachineSnapshotService
{
    public static ZxNextMachineSnapshot Capture(ZxNextMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);

        // Any instruction time the CPU is still holding has to reach the devices first, or
        // the captured clock would describe an earlier machine than the captured CPU.
        machine.SynchronizeTime();

        return new ZxNextMachineSnapshot(
            machine.Cpu.CaptureSnapshotState(),
            machine.Clock.CaptureState(),
            machine.MemoryBus.PhysicalMemory.CopyAll(),
            machine.MemoryBus.CaptureState(),
            machine.Paging.CaptureState(),
            machine.Registers.CaptureState(),
            machine.UlaTiming.CaptureState(),
            machine.Video.CaptureState(),
            machine.Registers.Palettes.CaptureState(),
            machine.Layer2.CaptureState(),
            machine.Tilemap.CaptureState(),
            machine.Sprites.CaptureState(),
            machine.Dma.CaptureState(),
            machine.BusArbiter.CaptureState(),
            machine.DivMmc.CaptureState(),
            machine.Multiface.CaptureState(),
            machine.Cpu.CaptureNmiRoutingState(),
            machine.Audio.CaptureState(),
            CaptureInput(machine),
            machine.PortBus.CaptureState(),
            machine.FloatingBus.CaptureState(),
            machine.DivMmc.CaptureMediaState());
    }

    public static ZxNextMediaRestoreReport Restore(
        ZxNextMachine machine,
        ZxNextMachineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(snapshot);

        // Whatever instruction time the machine being overwritten is still holding has to
        // reach its devices before the restore starts. Putting the CPU back flushes that
        // batch as a side effect, and by then the clock has already been set from the
        // snapshot, so an unpublished batch would be added to the restored timeline and
        // leave a machine a few ticks ahead of the one that was captured.
        machine.SynchronizeTime();

        machine.MemoryBus.PhysicalMemory.LoadAll(snapshot.PhysicalRam);
        machine.Paging.RestoreState(snapshot.Paging);
        machine.MemoryBus.RestoreState(snapshot.MemoryBus);
        machine.Registers.RestoreState(snapshot.Registers);
        machine.Clock.RestoreState(snapshot.Clock);
        machine.UlaTiming.RestoreState(snapshot.UlaTiming);
        machine.Registers.Palettes.RestoreState(snapshot.Palettes);
        machine.Layer2.RestoreState(snapshot.Layer2);
        machine.Tilemap.RestoreState(snapshot.Tilemap);
        machine.Sprites.RestoreState(snapshot.Sprites);

        machine.DivMmc.RestoreState(snapshot.DivMmc);
        ZxNextMediaRestoreReport media = machine.DivMmc.RestoreMediaState(snapshot.Media);
        machine.Multiface.RestoreState(snapshot.Multiface);
        machine.Audio.RestoreState(snapshot.Audio);
        machine.PortBus.RestoreState(snapshot.PortBus);
        machine.FloatingBus.RestoreState(snapshot.FloatingBus);
        RestoreInput(machine, snapshot.Input);
        machine.Dma.RestoreState(snapshot.Dma);
        machine.BusArbiter.RestoreState(snapshot.BusArbiter);

        // The renderer last, so the beam is put back after the layers it composes.
        machine.Video.RestoreState(snapshot.Raster);
        machine.Cpu.RestoreSnapshotState(snapshot.Cpu);
        machine.Cpu.RestoreNmiRoutingState(snapshot.NmiRouting);
        return media;
    }

    /// <summary>
    /// Captures held keys and joystick directions as one record.
    /// </summary>
    /// <remarks>
    /// Taken from two devices because that is how the machine is built - the joystick maps
    /// onto the keyboard matrix - but stored as one, because they are one thing to a player
    /// and restoring half of them would leave the overlay disagreeing with the matrix.
    /// </remarks>
    private static ZxNextInputState CaptureInput(ZxNextMachine machine)
    {
        (byte[] states, ZxNextJoystickMode[] modes) = machine.Joystick.CaptureState();
        return new ZxNextInputState(
            machine.Keyboard.CopyKeyboardRows(),
            machine.Keyboard.CopyJoystickRows(),
            states,
            modes);
    }

    private static void RestoreInput(ZxNextMachine machine, ZxNextInputState input)
    {
        // The joystick first: restoring it rebuilds its half of the matrix, and the matrix
        // restore then puts both halves back as they were captured.
        machine.Joystick.RestoreState(input.ConnectorStates, input.ConnectorModes);
        machine.Keyboard.RestoreRows(input.KeyboardRows, input.JoystickRows);
    }
}
