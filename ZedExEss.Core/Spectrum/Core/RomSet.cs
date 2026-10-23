using System;
using System.Collections.Generic;
using System.IO;

namespace ZedExEss.Spectrum.Core
{
    /// <summary>
    /// Immutable collection of 16 KB ROM banks loaded for the selected machine model.
    /// </summary>
    public sealed class RomSet
    {
        private readonly byte[][] _banks;

        private RomSet(byte[][] banks, int bankSizeBytes)
        {
            _banks = banks;
            BankSizeBytes = bankSizeBytes;
        }

        public int BankCount => _banks.Length;
        public int BankSizeBytes { get; }
        public ReadOnlyMemory<byte> GetBank(int index)
        {
            return _banks[index];
        }
        internal byte[] GetBankBytes(int index)
        {
            return _banks[index];
        }
        public static RomSet LoadFromFiles(IReadOnlyList<string> paths, int bankSizeBytes = 16 * 1024)
        {
            ArgumentNullException.ThrowIfNull(paths);

            if (paths.Count == 0)
            {
                throw new ArgumentException("At least one ROM path is required.", nameof(paths));
            }

            var banks = new byte[paths.Count][];
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentException("ROM path cannot be empty.", nameof(paths));
                }

                byte[] data = File.ReadAllBytes(path);
                if (data.Length != bankSizeBytes)
                {
                    throw new InvalidDataException($"ROM {path} is {data.Length} bytes; expected {bankSizeBytes}.");
                }

                banks[i] = data;
            }

            return new RomSet(banks, bankSizeBytes);
        }
        public static RomSet LoadFromCombinedFile(string path, int bankCount, int bankSizeBytes = 16 * 1024)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("ROM path cannot be empty.", nameof(path));
            }

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bankCount);

            byte[] data = File.ReadAllBytes(path);
            int expectedLength = checked(bankCount * bankSizeBytes);
            if (data.Length != expectedLength)
            {
                throw new InvalidDataException($"ROM {path} is {data.Length} bytes; expected {expectedLength} for {bankCount} banks.");
            }

            var banks = new byte[bankCount][];
            for (int i = 0; i < bankCount; i++)
            {
                banks[i] = new byte[bankSizeBytes];
                Buffer.BlockCopy(data, i * bankSizeBytes, banks[i], 0, bankSizeBytes);
            }

            return new RomSet(banks, bankSizeBytes);
        }
        public static RomSet CreateBlank(int bankCount, int bankSizeBytes = 16 * 1024)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bankCount);

            var banks = new byte[bankCount][];
            for (int i = 0; i < bankCount; i++)
            {
                banks[i] = new byte[bankSizeBytes];
            }

            return new RomSet(banks, bankSizeBytes);
        }
    }
}
