using System;
using System.IO;

namespace ZedExEss.ZxNext.Core
{
    /// <summary>One saved Next state and when it was taken.</summary>
    public sealed record ZxNextStateSlotEntry(ZxNextMachineSnapshot Snapshot, DateTimeOffset SavedAt);

    /// <summary>
    /// Save states for a Next: ten in memory, and files on disk.
    /// </summary>
    /// <remarks>
    /// The Spectrum's equivalent lives beside its replay session, because a slot there has to
    /// remember where a recording had got to as well. A Next has no recording yet, so this is
    /// the simpler thing: states and the file format, with the policy both hosts need - what
    /// counts as occupied, what a failure says - kept here rather than written twice.
    /// </remarks>
    public sealed class ZxNextStateSlots
    {
        /// <summary>How many slots there are, which is also how they are numbered: 0-9.</summary>
        public const int SlotCount = 10;

        private readonly ZxNextStateSlotEntry?[] _slots = new ZxNextStateSlotEntry?[SlotCount];

        public static bool IsValidSlot(int slot) => (uint)slot < SlotCount;

        public ZxNextStateSlotEntry? Peek(int slot) => IsValidSlot(slot) ? _slots[slot] : null;

        public bool IsOccupied(int slot) => Peek(slot) != null;

        public void Save(int slot, ZxNextMachine machine)
        {
            ArgumentNullException.ThrowIfNull(machine);
            if (!IsValidSlot(slot))
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }

            _slots[slot] = new ZxNextStateSlotEntry(
                ZxNextMachineSnapshotService.Capture(machine),
                DateTimeOffset.Now);
        }

        public bool TryLoad(int slot, ZxNextMachine machine) => TryLoad(slot, machine, out _);

        /// <summary>
        /// Loads a slot, reporting anything the mounted media would not accept.
        /// </summary>
        /// <remarks>
        /// The report is almost always empty. When it is not, the person loading the state
        /// needs to see it: a machine whose card was left as it was is a machine that may be
        /// about to write a directory entry over the wrong sector, and that is worth a line on
        /// the status bar rather than a silent success.
        /// </remarks>
        public bool TryLoad(int slot, ZxNextMachine machine, out ZxNextMediaRestoreReport report)
        {
            ArgumentNullException.ThrowIfNull(machine);

            report = ZxNextMediaRestoreReport.Silent;
            ZxNextStateSlotEntry? entry = Peek(slot);
            if (entry == null)
            {
                return false;
            }

            report = ZxNextMachineSnapshotService.Restore(machine, entry.Snapshot);
            return true;
        }

        /// <summary>Turns a media report into something to put after a status message.</summary>
        public static string DescribeMediaNotes(ZxNextMediaRestoreReport report)
        {
            ArgumentNullException.ThrowIfNull(report);
            return report.Notes.Count == 0 ? string.Empty : " - " + string.Join("; ", report.Notes);
        }

        public void Clear() => Array.Clear(_slots);

        /// <summary>
        /// Writes the machine to a file, reporting what happened rather than throwing.
        /// </summary>
        /// <remarks>
        /// Saving writes to a temporary file beside the target and moves it into place, so a
        /// failure part way through cannot replace a good save state with a truncated one.
        /// </remarks>
        public static string SaveToFile(string path, ZxNextMachine machine)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(machine);

            ZxNextMachineSnapshot snapshot = ZxNextMachineSnapshotService.Capture(machine);
            string temporary = path + ".partial";
            try
            {
                using (FileStream file = File.Create(temporary))
                {
                    ZxNextSnapshotFile.Save(file, snapshot);
                }

                File.Move(temporary, path, overwrite: true);
                return $"Saved {Path.GetFileName(path)}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(temporary);
                return $"Could not save {Path.GetFileName(path)}: {ex.Message}";
            }
        }

        /// <summary>
        /// Reads a file into the machine, reporting what happened rather than throwing.
        /// </summary>
        /// <remarks>
        /// The whole file is read and validated before anything is restored, so a bad file
        /// leaves the running machine exactly as it was rather than half replaced.
        /// </remarks>
        public static string LoadFromFile(string path, ZxNextMachine machine)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(machine);

            ZxNextMachineSnapshot snapshot;
            try
            {
                using FileStream file = File.OpenRead(path);
                snapshot = ZxNextSnapshotFile.Load(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return $"Could not load {Path.GetFileName(path)}: {ex.Message}";
            }

            ZxNextMediaRestoreReport report;
            try
            {
                report = ZxNextMachineSnapshotService.Restore(machine, snapshot);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return $"{Path.GetFileName(path)} does not fit this machine: {ex.Message}";
            }

            return $"Loaded {Path.GetFileName(path)}{DescribeMediaNotes(report)}";
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover partial file is untidy, not harmful.
            }
        }
    }
}
