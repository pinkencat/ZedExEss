using System;
using ZedExEss.FileHandlers;
using ZedExEss.Spectrum.Core;

namespace ZedExEss.Spectrum.Replay
{
    /// <summary>One saved state, and where the recording had got to when it was taken.</summary>
    public sealed record SpectrumStateSlotEntry(
        SpectrumMachineSnapshot Snapshot,
        SpectrumReplayPosition Position,
        DateTimeOffset SavedAt);

    /// <summary>
    /// A small set of machine states held in memory, saved and loaded without touching
    /// disk.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from the snapshot file formats. A run is built by saving and
    /// loading constantly - hundreds of times an hour - so this has to cost a memory copy
    /// and nothing else: no dialog, no serialisation, no file handle. Anything that wants
    /// a state to survive the session should write a snapshot file instead.
    ///
    /// Each slot also remembers how many frames had been recorded when it was taken. That
    /// is what lets loading one put a recording back to the same point rather than leave
    /// it describing a machine that no longer exists, and it is why the slots live beside
    /// the replay session instead of in the session controller.
    /// </remarks>
    public sealed class SpectrumStateSlots
    {
        /// <summary>How many slots there are, which is also how they are numbered: 0-9.</summary>
        public const int SlotCount = 10;

        private readonly SpectrumStateSlotEntry?[] _slots = new SpectrumStateSlotEntry?[SlotCount];

        public static bool IsValidSlot(int slot) => (uint)slot < SlotCount;

        /// <summary>The state in a slot, or null if it is empty or the number is out of range.</summary>
        public SpectrumStateSlotEntry? Peek(int slot) => IsValidSlot(slot) ? _slots[slot] : null;

        public bool IsOccupied(int slot) => Peek(slot) != null;

        /// <summary>Captures the machine into a slot, replacing whatever was there.</summary>
        public void Save(
            int slot,
            SpectrumMachine machine,
            SpectrumInterface1MediaState? interface1Media,
            SpectrumReplayPosition position)
        {
            ArgumentNullException.ThrowIfNull(machine);
            if (!IsValidSlot(slot))
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }

            _slots[slot] = new SpectrumStateSlotEntry(
                SpectrumMachineSnapshotService.Capture(machine, interface1Media),
                position,
                DateTimeOffset.Now);
        }

        /// <summary>
        /// Puts a slot back into the machine, if it holds one that this machine can take.
        /// </summary>
        /// <remarks>
        /// A state saved on one model cannot be restored onto another - the RAM banks do
        /// not line up - so that is refused here rather than left to throw out of the
        /// snapshot service. Callers can name the mismatch from <see cref="Peek"/>.
        /// </remarks>
        public bool TryLoad(
            int slot,
            SpectrumMachine machine,
            SpectrumInterface1MediaState? interface1Media,
            out SpectrumReplayPosition position)
        {
            ArgumentNullException.ThrowIfNull(machine);

            position = default;
            SpectrumStateSlotEntry? entry = Peek(slot);
            if (entry == null || entry.Snapshot.Model != machine.Model)
            {
                return false;
            }

            SpectrumMachineSnapshotService.Restore(machine, entry.Snapshot, interface1Media);
            position = entry.Position;
            return true;
        }

        /// <summary>Forgets every slot, for when the machine is replaced.</summary>
        public void Clear() => Array.Clear(_slots);
    }
}
