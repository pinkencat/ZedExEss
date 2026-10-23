using System;
using System.Collections.Generic;
using ZedExEss.FileHandlers;
using ZedExEss.Spectrum.Core;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Replay
{
    /// <summary>Where a run has got to: the frame, and how much of it has been recorded.</summary>
    /// <remarks>
    /// The two travel together everywhere - a stored state, a slot, a step back - because
    /// putting the machine back without putting the recording back leaves a recording that
    /// describes a run the machine is not going to make.
    /// </remarks>
    public readonly record struct SpectrumReplayPosition(int Frame, int RecordedFrameCount);

    /// <summary>
    /// States kept across a whole run, so any earlier frame can be returned to.
    /// </summary>
    /// <remarks>
    /// This is the difference between an undo and a workspace. A fixed ring of the last
    /// few seconds is enough to retry a jump; building a run means going back to any
    /// frame, including one from twenty minutes ago.
    ///
    /// Two things make that affordable. The first is that consecutive states are nearly
    /// identical: a frame typically writes a few kilobytes, so RAM is stored as pages and
    /// a page that has not changed is shared with the state before it rather than copied.
    /// A stored state therefore costs what the frame actually changed, not what the
    /// machine contains, which is usually an order of magnitude less.
    ///
    /// The second is a byte budget. When it is reached the entry evicted is whichever
    /// one's removal widens the local gap least, which thins the old end of the run
    /// geometrically while recent history stays dense - the shape that suits how a run is
    /// built, since the frames being worked on are nearly always recent. The oldest entry
    /// is never evicted, so the start of the run stays reachable however long it goes on,
    /// and a window of the newest entries is never thinned.
    ///
    /// Going back further than the stored states allow is not an error: the nearest state
    /// at or before the wanted frame is restored and the caller is told which frame it
    /// landed on, so it can run forward from there.
    /// </remarks>
    public sealed class SpectrumStateHistory
    {
        /// <summary>Frames between stored states, before any thinning.</summary>
        /// <remarks>
        /// One would be ideal and costs too much. Two keeps a seek to at most one frame
        /// of re-running, which is imperceptible.
        /// </remarks>
        public const int DefaultIntervalFrames = 2;

        /// <summary>How much memory the states may occupy.</summary>
        public const long DefaultBudgetBytes = 256L * 1024 * 1024;

        /// <summary>
        /// Bytes per shared RAM page.
        /// </summary>
        /// <remarks>
        /// Small enough that a frame touching one corner of the screen does not drag a
        /// whole bank into a new state, large enough that the bookkeeping per page stays
        /// a small fraction of the page itself.
        /// </remarks>
        public const int PageBytes = 256;

        /// <summary>Newest entries that are never thinned, so recent stepping stays cheap.</summary>
        private const int ProtectedRecentEntries = 64;

        /// <summary>The object header and reference cost of holding one page.</summary>
        private const int PageOverheadBytes = 32;

        /// <summary>
        /// Everything an entry costs beyond the pages it points at.
        /// </summary>
        /// <remarks>
        /// Measured rather than guessed: four hundred states of a 128K machine with its PSG
        /// running cost 4663 bytes each of real heap against 4512 bytes of pages, and carrying
        /// the PSG's generator phase added about a hundred and fifty more. The budget is only
        /// honest if this keeps pace with what a state actually holds, so it is a number to
        /// revisit whenever the snapshot record grows.
        /// </remarks>
        private const int EntryOverheadBytes = 320;

        private readonly List<Entry> _entries = [];
        private long _bytesUsed;

        public SpectrumStateHistory(
            int intervalFrames = DefaultIntervalFrames,
            long budgetBytes = DefaultBudgetBytes)
        {
            if (intervalFrames < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(intervalFrames));
            }

            if (budgetBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(budgetBytes));
            }

            IntervalFrames = intervalFrames;
            BudgetBytes = budgetBytes;
        }

        public int IntervalFrames { get; }

        /// <summary>How much memory the states may occupy, changeable while running.</summary>
        /// <remarks>
        /// Lowering it takes effect at the next capture rather than immediately, so the
        /// machine is never left without the state it is about to be asked to go back to.
        /// </remarks>
        public long BudgetBytes { get; set; }

        public int Count => _entries.Count;

        /// <summary>Memory the stored states occupy, counting each shared page once.</summary>
        public long BytesUsed => _bytesUsed;

        public bool IsEmpty => _entries.Count == 0;

        /// <summary>The earliest frame that can still be returned to.</summary>
        public int OldestFrame => _entries.Count > 0 ? _entries[0].Position.Frame : 0;

        /// <summary>The latest frame a state was stored for.</summary>
        public int NewestFrame => _entries.Count > 0 ? _entries[^1].Position.Frame : 0;

        /// <summary>How much of the run is covered, at fifty frames a second.</summary>
        public double SecondsCovered => (NewestFrame - OldestFrame) / 50.0;

        /// <summary>What a state would cost stored whole, for reporting what sharing saves.</summary>
        public long UnsharedBytes { get; private set; }

        /// <summary>The frame a stored state belongs to, oldest first.</summary>
        /// <remarks>
        /// For showing the shape of the history and for checking that thinning leaves
        /// recent frames dense; the states themselves stay private.
        /// </remarks>
        public int FrameAt(int index) => _entries[index].Position.Frame;

        public void Clear()
        {
            _entries.Clear();
            _bytesUsed = 0;
            UnsharedBytes = 0;
        }

        /// <summary>Stores a state, if this frame is far enough past the last one stored.</summary>
        public void OnFrameEdge(
            SpectrumMachine machine,
            SpectrumReplayPosition position,
            SpectrumInterface1MediaState? interface1Media = null)
        {
            ArgumentNullException.ThrowIfNull(machine);

            if (_entries.Count > 0)
            {
                int newest = _entries[^1].Position.Frame;
                if (position.Frame - newest < IntervalFrames)
                {
                    return;
                }
            }

            SpectrumMachineSnapshot snapshot =
                SpectrumMachineSnapshotService.Capture(machine, interface1Media);
            _entries.Add(Compact(position, snapshot));
            EnforceBudget();
        }

        /// <summary>
        /// Restores the newest stored state that is not after the wanted frame.
        /// </summary>
        /// <param name="restored">
        /// Where the machine actually landed, which may be earlier than what was asked
        /// for if the states around it have been thinned away.
        /// </param>
        public bool TryRestoreAtOrBefore(
            SpectrumMachine machine,
            int targetFrame,
            out SpectrumReplayPosition restored,
            SpectrumInterface1MediaState? interface1Media = null)
        {
            ArgumentNullException.ThrowIfNull(machine);

            restored = default;
            int index = IndexAtOrBefore(targetFrame);
            if (index < 0)
            {
                return false;
            }

            Entry entry = _entries[index];
            SpectrumMachineSnapshotService.Restore(machine, Expand(entry), interface1Media);
            restored = entry.Position;
            return true;
        }

        /// <summary>
        /// Forgets everything after a frame, for when the run from there is being replaced.
        /// </summary>
        /// <remarks>
        /// Only for a retake. Seeking about inside a recording that is being played back
        /// leaves the later states perfectly valid, because playing it again reproduces
        /// them exactly.
        /// </remarks>
        public void DropAfter(int frame)
        {
            for (int index = _entries.Count - 1; index >= 0; index--)
            {
                if (_entries[index].Position.Frame <= frame)
                {
                    break;
                }

                Release(_entries[index]);
                _entries.RemoveAt(index);
            }

            if (_entries.Count == 0)
            {
                _bytesUsed = 0;
                UnsharedBytes = 0;
            }
        }

        /// <summary>
        /// Turns a snapshot into pages, sharing every one the previous state already holds.
        /// </summary>
        /// <remarks>
        /// Comparing against the state before is enough, and much cheaper than a content
        /// index: a page that stays untouched for a thousand frames is shared forward one
        /// state at a time, so all thousand end up pointing at the same array anyway.
        /// </remarks>
        private Entry Compact(SpectrumReplayPosition position, SpectrumMachineSnapshot snapshot)
        {
            int banks = snapshot.RamBankCount;
            int bankBytes = banks > 0 ? snapshot.GetRamBankSpan(0).Length : 0;
            int pagesPerBank = (bankBytes + PageBytes - 1) / PageBytes;

            var pages = new Page[banks * pagesPerBank];
            Page[]? previous = _entries.Count > 0 ? _entries[^1].Pages : null;
            if (previous != null && previous.Length != pages.Length)
            {
                // A different machine has been attached without the history being cleared.
                previous = null;
            }

            for (int bank = 0; bank < banks; bank++)
            {
                ReadOnlySpan<byte> source = snapshot.GetRamBankSpan(bank);
                for (int page = 0; page < pagesPerBank; page++)
                {
                    int slot = (bank * pagesPerBank) + page;
                    int offset = page * PageBytes;
                    ReadOnlySpan<byte> content =
                        source.Slice(offset, Math.Min(PageBytes, bankBytes - offset));

                    Page? shared = previous?[slot];
                    if (shared != null && content.SequenceEqual(shared.Data))
                    {
                        shared.References++;
                        pages[slot] = shared;
                        continue;
                    }

                    var stored = new Page(content.ToArray());
                    pages[slot] = stored;
                    _bytesUsed += stored.Data.Length + PageOverheadBytes;
                }
            }

            _bytesUsed += (pages.Length * sizeof(long)) + EntryOverheadBytes;
            UnsharedBytes += (banks * (long)bankBytes) + EntryOverheadBytes;
            return new Entry(position, StateHeader.From(snapshot, bankBytes), pages);
        }

        /// <summary>Rebuilds the whole machine state from its shared pages.</summary>
        private static SpectrumMachineSnapshot Expand(Entry entry)
        {
            StateHeader header = entry.Header;
            var banks = new byte[header.RamBankCount][];
            int pagesPerBank = header.RamBankCount > 0
                ? entry.Pages.Length / header.RamBankCount
                : 0;

            for (int bank = 0; bank < banks.Length; bank++)
            {
                var data = new byte[header.RamBankBytes];
                for (int page = 0; page < pagesPerBank; page++)
                {
                    entry.Pages[(bank * pagesPerBank) + page].Data
                        .CopyTo(data, page * PageBytes);
                }

                banks[bank] = data;
            }

            return new SpectrumMachineSnapshot(
                header.Model,
                header.Cpu,
                banks,
                header.Port7FFD,
                header.Port1FFD,
                header.UlaOutput,
                header.FrameTstate,
                header.FrameCounter,
                header.Ay,
                header.Interface1,
                header.HeldInterruptTstates);
        }

        /// <summary>Gives up an entry's claim on its pages, freeing those nothing else holds.</summary>
        private void Release(Entry entry)
        {
            foreach (Page page in entry.Pages)
            {
                if (--page.References == 0)
                {
                    _bytesUsed -= page.Data.Length + PageOverheadBytes;
                }
            }

            _bytesUsed -= (entry.Pages.Length * sizeof(long)) + EntryOverheadBytes;
            UnsharedBytes -= (entry.Header.RamBankCount * (long)entry.Header.RamBankBytes)
                + EntryOverheadBytes;
        }

        /// <summary>The last entry at or before a frame, or -1 if the history starts later.</summary>
        private int IndexAtOrBefore(int frame)
        {
            int low = 0;
            int high = _entries.Count - 1;
            int found = -1;
            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                if (_entries[middle].Position.Frame <= frame)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found;
        }

        private void EnforceBudget()
        {
            while (_bytesUsed > BudgetBytes)
            {
                int victim = ChooseVictim();
                if (victim < 0)
                {
                    // Everything left is either the start of the run or inside the
                    // protected recent window. Going over budget beats losing those.
                    return;
                }

                Release(_entries[victim]);
                _entries.RemoveAt(victim);
            }
        }

        /// <summary>
        /// The entry whose removal costs the least: the one with the closest neighbours.
        /// </summary>
        /// <remarks>
        /// Applied repeatedly this halves the density of the oldest region, then the
        /// region after it, and so on, which is what turns a fixed budget into coverage
        /// of a whole run rather than a window at the end of it.
        /// </remarks>
        private int ChooseVictim()
        {
            int limit = _entries.Count - 1 - ProtectedRecentEntries;
            int best = -1;
            int bestCost = int.MaxValue;
            for (int index = 1; index < limit; index++)
            {
                int cost = _entries[index + 1].Position.Frame - _entries[index - 1].Position.Frame;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = index;
                }
            }

            return best;
        }

        /// <summary>One page of RAM, and how many stored states are holding it.</summary>
        private sealed class Page(byte[] data)
        {
            public byte[] Data { get; } = data;

            public int References { get; set; } = 1;
        }

        /// <summary>Everything in a state except its RAM, which the pages carry.</summary>
        private sealed record StateHeader(
            SpectrumModel Model,
            Z80SnapshotState Cpu,
            byte Port7FFD,
            byte Port1FFD,
            byte UlaOutput,
            int FrameTstate,
            int FrameCounter,
            SpectrumAySnapshot? Ay,
            SpectrumInterface1Snapshot? Interface1,
            int HeldInterruptTstates,
            int RamBankCount,
            int RamBankBytes)
        {
            public static StateHeader From(SpectrumMachineSnapshot snapshot, int bankBytes) => new(
                snapshot.Model,
                snapshot.Cpu,
                snapshot.Port7FFD,
                snapshot.Port1FFD,
                snapshot.UlaOutput,
                snapshot.FrameTstate,
                snapshot.FrameCounter,
                snapshot.Ay,
                snapshot.Interface1,
                snapshot.HeldInterruptTstates,
                snapshot.RamBankCount,
                bankBytes);
        }

        private readonly record struct Entry(
            SpectrumReplayPosition Position,
            StateHeader Header,
            Page[] Pages);
    }
}
