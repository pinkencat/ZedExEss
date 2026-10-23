using System.IO;
using System;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Video;
using ZedExEss.Z80CPU;

namespace ZedExEss.FileHandlers
{
    /// <summary>
    /// Restores 48K and 128K SNA snapshots into an already-created compatible machine.
    /// </summary>
    /// <remarks>
    /// A 48K SNA stores PC on the emulated stack; the 128K extension stores it
    /// explicitly after the first three RAM pages. Loading therefore must finish
    /// memory restoration before recovering the 48K PC.
    /// </remarks>
    public static class SnapshotLoader
    {
        private const int HeaderLength = 27;
        private const int Ram48Length = 49152;
        private const int Sna48Length = HeaderLength + Ram48Length;
        private const int Sna128MinimumLength = Sna48Length + 4 + (5 * 0x4000);
        public static SpectrumModel DetectModel(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path cannot be null or empty", nameof(path));
            }

            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new ArgumentException("File does not exist", nameof(path));
            }

            if (info.Length < Sna48Length)
            {
                throw new ArgumentException("Invalid SNA file format", nameof(path));
            }

            return info.Length >= Sna128MinimumLength
                ? SpectrumModel.Spectrum128K
                : SpectrumModel.Spectrum48K;
        }
        public static void LoadSna(Z80 cpu, SpectrumMemory memory, SpectrumUlaRenderer renderer, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("Path cannot be null or empty", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new ArgumentException("File does not exist", nameof(path));
            }

            var data = File.ReadAllBytes(path);
            if (data.Length < Sna48Length)
            {
                throw new ArgumentException("Invalid SNA file format", nameof(path));
            }

            int index = 0;
            // Header order is fixed by the SNA format and deliberately does not mirror
            // the CPU class's register layout.
            cpu.I = data[index++];
            cpu.L_ = data[index++];
            cpu.H_ = data[index++];
            cpu.E_ = data[index++];
            cpu.D_ = data[index++];
            cpu.C_ = data[index++];
            cpu.B_ = data[index++];
            cpu.F_ = data[index++];
            cpu.A_ = data[index++];
            cpu.L = data[index++];
            cpu.H = data[index++];
            cpu.E = data[index++];
            cpu.D = data[index++];
            cpu.C = data[index++];
            cpu.B = data[index++];
            byte iyl = data[index++];
            byte iyh = data[index++];
            byte ixl = data[index++];
            byte ixh = data[index++];
            byte IFF2 = data[index++];
            cpu.R = data[index++];
            byte f = data[index++];
            cpu.A = data[index++];
            byte spl = data[index++];
            byte sph = data[index++];
            byte interruptMode = data[index++];
            byte borderColour = data[index++];

            cpu.IY = (ushort)((iyh << 8) | iyl);
            cpu.IX = (ushort)((ixh << 8) | ixl);
            cpu.SP = (ushort)((sph << 8) | spl);
            cpu.SetF(f);
            bool iff = (IFF2 & 0x04) != 0;
            cpu.SetInterruptState(interruptMode, iff, iff);
            cpu.SetHalted(false);

            int ramOffset = HeaderLength;
            bool is128k = data.Length >= Sna128MinimumLength;

            if (is128k && SupportsPaging(memory.Model))
            {
                int extraOffset = ramOffset + 49152;
                ushort pc = (ushort)(data[extraOffset] | (data[extraOffset + 1] << 8));
                byte port7ffd = data[extraOffset + 2];
                cpu.PC = pc;

                int pagedBank = port7ffd & 0x07;
                memory.LoadRamBank(5, data.AsSpan(ramOffset, 0x4000));
                memory.LoadRamBank(2, data.AsSpan(ramOffset + 0x4000, 0x4000));
                memory.LoadRamBank(pagedBank, data.AsSpan(ramOffset + 0x8000, 0x4000));

                int bankDataOffset = extraOffset + 4;
                int[] bankOrder = [0, 1, 3, 4, 6, 7];
                foreach (int bank in bankOrder)
                {
                    if (bank == pagedBank)
                    {
                        continue;
                    }

                    if (bankDataOffset + 0x4000 > data.Length)
                    {
                        break;
                    }

                    memory.LoadRamBank(bank, data.AsSpan(bankDataOffset, 0x4000));
                    bankDataOffset += 0x4000;
                }

                memory.WritePort7FFD(port7ffd);
            }
            else
            {
                Load48kMemory(memory, data.AsSpan(ramOffset, 49152));

                // The original SNA writer simulated PUSH PC because the 48K header
                // has no PC field. Recover it without adding emulated bus cycles.
                byte pcl = memory.ReadDirect(cpu.SP);
                byte pch = memory.ReadDirect((ushort)(cpu.SP + 1));
                cpu.SP += 2;
                cpu.PC = (ushort)((pch << 8) | pcl);
            }

            renderer.BorderColorIndex = (byte)(borderColour & 0x07);
        }
        private static void Load48kMemory(SpectrumMemory memory, ReadOnlySpan<byte> data)
        {
            if (data.Length != 49152)
            {
                throw new ArgumentException("SNA RAM payload must be 49152 bytes.", nameof(data));
            }

            if (SupportsPaging(memory.Model))
            {
                memory.WritePort7FFD(0x00);
                memory.LoadRamBank(5, data.Slice(0, 0x4000));
                memory.LoadRamBank(2, data.Slice(0x4000, 0x4000));
                memory.LoadRamBank(0, data.Slice(0x8000, 0x4000));
                return;
            }

            for (int i = 0; i < data.Length; i++)
            {
                memory.WriteDirect((ushort)(0x4000 + i), data[i]);
            }
        }
        private static bool SupportsPaging(SpectrumModel model)
        {
            return SpectrumModelTraits.SupportsPaging(model);
        }
    }
}
