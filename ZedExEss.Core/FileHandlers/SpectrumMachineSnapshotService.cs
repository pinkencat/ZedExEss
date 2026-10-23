using ZedExEss.Spectrum.Core;

namespace ZedExEss.FileHandlers;

/// <summary>Captures and atomically applies portable Spectrum machine snapshots.</summary>
public static class SpectrumMachineSnapshotService
{
    public static SpectrumMachineSnapshot Capture(
        SpectrumMachine machine,
        SpectrumInterface1MediaState? interface1Media = null)
    {
        ArgumentNullException.ThrowIfNull(machine);

        // Reading the machine must not change it. Bringing the subsystems up to the CPU
        // can run the INT line past the point the CPU has not yet looked at, which would
        // lose an interrupt that was about to be taken - so the flag is put back.
        bool interruptPending = machine.Cpu.InterruptPending;
        byte interruptData = machine.Cpu.PendingInterruptData;
        machine.Emulator.SyncToCpu();
        if (interruptPending && !machine.Cpu.InterruptPending)
        {
            machine.Cpu.Z80SetINTLine(true, interruptData);
        }

        var ram = new byte[machine.Memory.RamBankCount][];
        for (int bank = 0; bank < ram.Length; bank++)
        {
            ram[bank] = machine.Memory.CopyRamBank(bank);
        }

        SpectrumAySnapshot? ay = machine.AyChip == null
            ? null
            : new SpectrumAySnapshot(
                machine.AyDevice?.SelectedRegister ?? 0,
                machine.AyChip.CopyRegisters())
            {
                Phase = machine.AyChip.CaptureState()
            };

        return new SpectrumMachineSnapshot(
            machine.Model,
            machine.Cpu.CaptureSnapshotState(),
            ram,
            machine.Memory.Port7FFD,
            machine.Memory.Port1FFD,
            machine.Ula.LastOutputByte,
            machine.Renderer.FrameTstate,
            machine.Renderer.FrameCounter,
            ay,
            interface1Media?.CaptureSnapshot(),
            machine.Emulator.HeldInterruptTstates);
    }

    public static void Restore(
        SpectrumMachine machine,
        SpectrumMachineSnapshot snapshot,
        SpectrumInterface1MediaState? interface1Media = null)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (machine.Model != snapshot.Model)
        {
            throw new InvalidOperationException(
                $"Cannot restore a {snapshot.Model} snapshot into a {machine.Model} machine.");
        }

        if (machine.Memory.RamBankCount != snapshot.RamBankCount)
        {
            throw new InvalidOperationException("Snapshot RAM topology does not match the running machine.");
        }

        if (snapshot.Interface1 != null && interface1Media == null)
        {
            throw new InvalidOperationException("Snapshot contains Interface 1 state but no media session was supplied.");
        }

        for (int bank = 0; bank < snapshot.RamBankCount; bank++)
        {
            machine.Memory.LoadRamBank(bank, snapshot.GetRamBankSpan(bank));
        }

        machine.Memory.RestorePagingState(snapshot.Port7FFD, snapshot.Port1FFD);
        machine.Ula.RestoreOutputLatch(snapshot.UlaOutput);

        if (machine.AyChip != null)
        {
            if (snapshot.Ay != null)
            {
                // The phase when the snapshot was taken from a machine, the registers when it
                // came out of a file that could not express one.
                if (snapshot.Ay.Phase != null)
                {
                    machine.AyChip.RestoreState(snapshot.Ay.Phase);
                }
                else
                {
                    machine.AyChip.RestoreRegisters(snapshot.Ay.Registers);
                }

                if (machine.AyDevice != null)
                {
                    machine.AyDevice.SelectedRegister = snapshot.Ay.SelectedRegister;
                }
            }
            else
            {
                machine.AyChip.Reset();
                if (machine.AyDevice != null)
                {
                    machine.AyDevice.SelectedRegister = 0;
                }
            }
        }

        machine.Cpu.RestoreSnapshotState(snapshot.Cpu);
        machine.Renderer.RestoreTiming(snapshot.FrameTstate, snapshot.FrameCounter);
        if (snapshot.Interface1 != null)
        {
            interface1Media!.RestoreSnapshot(snapshot.Interface1);
        }

        machine.Emulator.ResetSynchronizationAfterSnapshotRestore();
        machine.Emulator.RestoreHeldInterrupt(snapshot.HeldInterruptTstates);

        // Resynchronising drops the INT line, which would quietly cost the restored
        // machine the interrupt it was about to take. A snapshot taken at a frame
        // boundary always has one waiting, so put it back last.
        if (snapshot.Cpu.IntPending)
        {
            machine.Cpu.Z80SetINTLine(true, snapshot.Cpu.InterruptData);
        }
    }
}
