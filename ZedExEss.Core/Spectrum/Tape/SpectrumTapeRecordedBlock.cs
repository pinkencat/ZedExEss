using System;
using System.Collections.Generic;

namespace ZedExEss.Spectrum.Tape
{
    /// <summary>One block recovered from what the machine wrote to its MIC line.</summary>
    /// <remarks>
    /// A block is either standard ROM format, in which case it has bytes and can go into
    /// a TAP, or it is a run of pulses that did not decode - a custom saver, or noise -
    /// which only a TZX can carry.
    /// </remarks>
    public sealed class SpectrumTapeRecordedBlock
    {
        private static readonly int[] NoPulses = [];

        private SpectrumTapeRecordedBlock(byte[] data, int[] pulses, bool checksumValid, int pauseMilliseconds)
        {
            Data = data;
            Pulses = pulses;
            ChecksumValid = checksumValid;
            PauseMilliseconds = pauseMilliseconds;
        }

        /// <summary>Flag byte, payload and checksum, or empty for a raw block.</summary>
        public byte[] Data { get; }

        /// <summary>Pulse lengths in ULA T-states, or empty for a standard block.</summary>
        public IReadOnlyList<int> Pulses { get; }

        public bool IsStandard => Data.Length > 0;

        /// <summary>False when the recorded checksum does not match the payload.</summary>
        public bool ChecksumValid { get; }

        /// <summary>Silence that followed the block, as a TZX pause.</summary>
        public int PauseMilliseconds { get; }

        /// <summary>True for a header, which is how the browser names the block.</summary>
        public bool IsHeader => IsStandard && Data.Length == 19 && Data[0] == 0x00;

        public static SpectrumTapeRecordedBlock Standard(byte[] data, int pauseMilliseconds)
        {
            ArgumentNullException.ThrowIfNull(data);
            return new SpectrumTapeRecordedBlock(data, NoPulses, ChecksumMatches(data), pauseMilliseconds);
        }

        public static SpectrumTapeRecordedBlock Raw(int[] pulses)
        {
            ArgumentNullException.ThrowIfNull(pulses);
            return new SpectrumTapeRecordedBlock([], pulses, checksumValid: false, pauseMilliseconds: 0);
        }

        /// <summary>The saver's own check byte: everything before it, XORed together.</summary>
        private static bool ChecksumMatches(byte[] data)
        {
            if (data.Length < 2)
            {
                return false;
            }

            byte parity = 0;
            for (int i = 0; i < data.Length - 1; i++)
            {
                parity ^= data[i];
            }

            return parity == data[^1];
        }
    }
}
