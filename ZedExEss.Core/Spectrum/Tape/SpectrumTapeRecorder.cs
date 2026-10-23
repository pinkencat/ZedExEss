using System;
using System.Collections.Generic;
using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Tape
{
    /// <summary>
    /// Records what the machine writes to its MIC line and turns it back into tape blocks.
    /// </summary>
    /// <remarks>
    /// This is the other half of tape support: the machine has always been able to load,
    /// and this is what lets anything it saves - a BASIC program, a Multiface snapshot,
    /// the output of a utility - leave the emulator as a file.
    ///
    /// Capture is edge based rather than sampled, so the pulse train is exactly what the
    /// machine produced. Decoding is a separate pass over that train, which means a saver
    /// this does not understand still survives: its pulses are kept verbatim and written
    /// to a TZX, rather than being thrown away because they were not ROM format.
    /// </remarks>
    public sealed class SpectrumTapeRecorder : ITapeSink
    {
        /// <summary>Pilot tone pulse, in T-states, as the ROM saver writes it.</summary>
        public const int PilotPulse = 2168;
        public const int FirstSyncPulse = 667;
        public const int SecondSyncPulse = 735;
        public const int ZeroBitPulse = 855;
        public const int OneBitPulse = 1710;

        /// <summary>Machine T-states per second, used to turn gaps into TZX pauses.</summary>
        private const int TstatesPerSecond = 3_500_000;

        /// <summary>
        /// Shortest run of pilot pulses accepted as a lead-in. The ROM writes thousands,
        /// but turbo savers cut it right down, and this only has to be long enough not to
        /// fire on data that happens to look like pilot.
        /// </summary>
        private const int MinimumPilotPulses = 32;

        /// <summary>Roughly ten minutes of continuous tone; a runaway save stops here.</summary>
        private const int MaximumPulses = 4_000_000;

        private readonly List<int> _pulses = [];
        private readonly List<SpectrumTapeRecordedBlock> _blocks = [];
        private Func<ulong>? _cpuTstates;
        private Func<int>? _cpuTstatesPerUlaTstate;
        private ulong _lastEdgeTstate;
        private bool _level;
        private bool _levelKnown;
        private bool _decoded;

        /// <summary>True while MIC writes are being captured.</summary>
        public bool IsRecording { get; private set; }

        /// <summary>True once the pulse buffer filled up and capture stopped early.</summary>
        public bool Overflowed { get; private set; }

        /// <summary>True when there is something worth saving.</summary>
        public bool HasRecording => _blocks.Count > 0 || _pulses.Count > 0;

        /// <summary>Blocks recovered so far. Decoding runs on demand.</summary>
        public IReadOnlyList<SpectrumTapeRecordedBlock> Blocks
        {
            get
            {
                Decode();
                return _blocks;
            }
        }

        /// <summary>Standard blocks only - what a TAP can hold.</summary>
        public int StandardBlockCount
        {
            get
            {
                int count = 0;
                foreach (SpectrumTapeRecordedBlock block in Blocks)
                {
                    if (block.IsStandard)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// Supplies the clock. Pulse lengths are measured in ULA T-states so that a
        /// machine running its CPU faster than its ULA still records at tape speed.
        /// </summary>
        public void Attach(Func<ulong> cpuTstates, Func<int> cpuTstatesPerUlaTstate)
        {
            _cpuTstates = cpuTstates;
            _cpuTstatesPerUlaTstate = cpuTstatesPerUlaTstate;

            // A rebuilt machine starts its clock again from zero, so the next edge has to
            // re-establish where the line is resting rather than measure against a
            // timestamp from the machine that has just gone.
            _levelKnown = false;
            _lastEdgeTstate = cpuTstates?.Invoke() ?? 0;
        }

        public void Start()
        {
            if (IsRecording)
            {
                return;
            }

            IsRecording = true;
            _levelKnown = false;
            _lastEdgeTstate = _cpuTstates?.Invoke() ?? 0;
        }

        public void Stop()
        {
            IsRecording = false;
            _levelKnown = false;
        }

        public void Clear()
        {
            _pulses.Clear();
            _blocks.Clear();
            _decoded = true;
            Overflowed = false;
            _levelKnown = false;
        }

        /// <summary>Takes one MIC level change from the ULA.</summary>
        void ITapeSink.SetLevel(bool high) => SetLevel(high);

        public void SetLevel(bool high)
        {
            if (!IsRecording)
            {
                return;
            }

            ulong now = _cpuTstates?.Invoke() ?? 0;
            if (!_levelKnown)
            {
                // The first write only establishes where the line is resting. There is no
                // pulse until it moves.
                _levelKnown = true;
                _level = high;
                _lastEdgeTstate = now;
                return;
            }

            if (high == _level)
            {
                return;
            }

            _level = high;
            ulong elapsed = now - _lastEdgeTstate;
            _lastEdgeTstate = now;

            int ratio = _cpuTstatesPerUlaTstate?.Invoke() ?? 1;
            if (ratio > 1)
            {
                elapsed /= (ulong)ratio;
            }

            if (_pulses.Count >= MaximumPulses)
            {
                Overflowed = true;
                IsRecording = false;
                return;
            }

            _pulses.Add(elapsed > int.MaxValue ? int.MaxValue : (int)elapsed);
            _decoded = false;
        }

        /// <summary>
        /// Adds a block the saver produced directly, bypassing the pulse train.
        /// </summary>
        /// <remarks>
        /// Used by the ROM save trap, where the block is taken from memory rather than
        /// listened to. Any pulses captured up to this point are decoded first so the
        /// recording keeps its order.
        /// </remarks>
        public void AddStandardBlock(ReadOnlySpan<byte> data, int pauseMilliseconds)
        {
            if (data.Length < 2)
            {
                return;
            }

            Decode();
            _blocks.Add(SpectrumTapeRecordedBlock.Standard(data.ToArray(), pauseMilliseconds));
        }

        /// <summary>Decodes whatever has been captured since the last pass.</summary>
        public void Decode()
        {
            if (_decoded)
            {
                return;
            }

            _decoded = true;
            int index = 0;
            int rawStart = 0;
            while (index < _pulses.Count)
            {
                if (!TryFindPilot(index, out int pilotStart, out int afterPilot))
                {
                    break;
                }

                if (!TryReadBlock(afterPilot, out byte[] data, out int afterBlock, out int pauseMilliseconds))
                {
                    // The pilot led nowhere, so step past it and keep looking rather than
                    // giving up on everything that follows.
                    index = afterPilot;
                    continue;
                }

                EmitRaw(rawStart, pilotStart);
                _blocks.Add(SpectrumTapeRecordedBlock.Standard(data, pauseMilliseconds));
                index = afterBlock;
                rawStart = afterBlock;
            }

            EmitRaw(rawStart, _pulses.Count);
            _pulses.Clear();
        }

        /// <summary>Builds a TAP image. Only standard blocks fit in one.</summary>
        public byte[] BuildTap()
        {
            var output = new List<byte>(4096);
            foreach (SpectrumTapeRecordedBlock block in Blocks)
            {
                if (!block.IsStandard)
                {
                    continue;
                }

                output.Add((byte)(block.Data.Length & 0xFF));
                output.Add((byte)(block.Data.Length >> 8));
                output.AddRange(block.Data);
            }

            return [.. output];
        }

        /// <summary>
        /// Builds a TZX image, which keeps the pauses and anything that did not decode.
        /// </summary>
        public byte[] BuildTzx()
        {
            var output = new List<byte>(4096)
            {
                (byte)'Z', (byte)'X', (byte)'T', (byte)'a', (byte)'p', (byte)'e', (byte)'!', 0x1A, 1, 20
            };

            foreach (SpectrumTapeRecordedBlock block in Blocks)
            {
                if (block.IsStandard)
                {
                    output.Add(0x10);
                    AppendWord(output, block.PauseMilliseconds);
                    AppendWord(output, block.Data.Length);
                    output.AddRange(block.Data);
                    continue;
                }

                AppendPulseSequences(output, block.Pulses);
            }

            return [.. output];
        }

        /// <summary>
        /// Writes raw pulses as pulse-sequence blocks, which preserve them exactly.
        /// </summary>
        /// <remarks>
        /// A sequence block holds at most 255 pulses and each length is 16 bit, so long
        /// gaps become pause blocks and long runs are split. Direct recording would be
        /// the obvious alternative but it resamples, and the point of keeping these is
        /// that they are the pulses the machine actually produced.
        /// </remarks>
        private static void AppendPulseSequences(List<byte> output, IReadOnlyList<int> pulses)
        {
            var run = new List<int>(255);
            foreach (int pulse in pulses)
            {
                if (pulse > ushort.MaxValue)
                {
                    FlushRun(output, run);
                    int milliseconds = pulse / (TstatesPerSecond / 1000);
                    if (milliseconds > 0)
                    {
                        output.Add(0x20);
                        AppendWord(output, Math.Min(milliseconds, ushort.MaxValue));
                    }

                    continue;
                }

                run.Add(pulse);
                if (run.Count == 255)
                {
                    FlushRun(output, run);
                }
            }

            FlushRun(output, run);
        }

        private static void FlushRun(List<byte> output, List<int> run)
        {
            if (run.Count == 0)
            {
                return;
            }

            output.Add(0x13);
            output.Add((byte)run.Count);
            foreach (int pulse in run)
            {
                AppendWord(output, pulse);
            }

            run.Clear();
        }

        private static void AppendWord(List<byte> output, int value)
        {
            output.Add((byte)(value & 0xFF));
            output.Add((byte)((value >> 8) & 0xFF));
        }

        private void EmitRaw(int start, int end)
        {
            // A stray edge or two is the machine clicking the speaker, not a recording.
            if (end - start < 8)
            {
                return;
            }

            _blocks.Add(SpectrumTapeRecordedBlock.Raw(_pulses.GetRange(start, end - start).ToArray()));
        }

        /// <summary>Finds the next run of pilot pulses long enough to be a lead-in.</summary>
        private bool TryFindPilot(int from, out int start, out int end)
        {
            start = -1;
            end = -1;
            int index = from;
            while (index < _pulses.Count)
            {
                if (!Near(_pulses[index], PilotPulse))
                {
                    index++;
                    continue;
                }

                int runStart = index;
                while (index < _pulses.Count && Near(_pulses[index], PilotPulse))
                {
                    index++;
                }

                if (index - runStart >= MinimumPilotPulses)
                {
                    start = runStart;
                    end = index;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Reads the sync pair and the data bits that follow a pilot.</summary>
        private bool TryReadBlock(int from, out byte[] data, out int end, out int pauseMilliseconds)
        {
            data = [];
            end = from;
            pauseMilliseconds = 1000;

            if (from + 1 >= _pulses.Count
                || !Near(_pulses[from], FirstSyncPulse)
                || !Near(_pulses[from + 1], SecondSyncPulse))
            {
                return false;
            }

            int index = from + 2;
            var bytes = new List<byte>(256);
            int bits = 0;
            byte current = 0;
            while (index + 1 < _pulses.Count)
            {
                int first = _pulses[index];
                if (!TryClassifyBit(first, out bool one))
                {
                    break;
                }

                // Both halves of a bit are the same length; a mismatch means the data
                // ended and this is the saver's tail pulse.
                if (!TryClassifyBit(_pulses[index + 1], out bool second) || second != one)
                {
                    break;
                }

                current = (byte)((current << 1) | (one ? 1 : 0));
                index += 2;
                if (++bits == 8)
                {
                    bytes.Add(current);
                    bits = 0;
                    current = 0;
                }
            }

            if (bytes.Count < 2)
            {
                return false;
            }

            // Anything left over is a partial byte the saver never finished.
            end = index;
            if (end < _pulses.Count && _pulses[end] > OneBitPulse * 2)
            {
                pauseMilliseconds = Math.Clamp(_pulses[end] / (TstatesPerSecond / 1000), 1, ushort.MaxValue);
                end++;
            }

            data = [.. bytes];
            return true;
        }

        private static bool TryClassifyBit(int pulse, out bool one)
        {
            if (Near(pulse, ZeroBitPulse))
            {
                one = false;
                return true;
            }

            if (Near(pulse, OneBitPulse))
            {
                one = true;
                return true;
            }

            one = false;
            return false;
        }

        /// <summary>
        /// Accepts a pulse within 40% of the nominal length.
        /// </summary>
        /// <remarks>
        /// Wide enough for the speed variation of a saver written in BASIC or running
        /// with interrupts on, narrow enough that the zero and one bands do not meet.
        /// </remarks>
        private static bool Near(int pulse, int expected) =>
            pulse >= expected - (expected * 2 / 5) && pulse <= expected + (expected * 2 / 5);
    }
}
