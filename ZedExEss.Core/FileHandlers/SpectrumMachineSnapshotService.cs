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
        machine.Emulator.SyncToCpu();

        var ram = new byte[machine.Memory.RamBankCount][];
        for (int bank = 0; bank < ram.Length; bank++)
        {
            ram[bank] = machine.Memory.CopyRamBank(bank);
        }

        SpectrumAySnapshot? ay = machine.AyChip == null
            ? null
            : new SpectrumAySnapshot(
                machine.AyDevice?.SelectedRegister ?? 0,
                machine.AyChip.CopyRegisters());

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
            interface1Media?.CaptureSnapshot());
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
                machine.AyChip.RestoreRegisters(snapshot.Ay.Registers);
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
    }
}
