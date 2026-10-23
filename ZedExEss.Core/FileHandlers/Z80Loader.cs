using System.Collections.Generic;
using System.IO;
using System;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Video;
using ZedExEss.Z80CPU;

namespace ZedExEss.FileHandlers
{
    /// <summary>
    /// Reads v1-v3 .z80 snapshots, including compressed 16K pages and model paging registers.
    /// </summary>
    /// <remarks>
    /// Model detection is kept separate so the UI can create the correct memory
    /// topology before state restoration. Page numbers in extended snapshots are
    /// translated to physical banks rather than copied through the current mapping.
    /// </remarks>
    public static class Z80Loader
    {
        public static SpectrumModel DetectModel(string path)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            byte[] h = br.ReadBytes(30);
            if (h.Length < 30)
            {
                throw new InvalidDataException("Invalid Z80 header.");
            }

            ushort savedPC = (ushort)(h[6] | (h[7] << 8));
            if (savedPC != 0)
            {
                return SpectrumModel.Spectrum48K;
            }

            if (fs.Position + 2 > fs.Length)
            {
                return SpectrumModel.Spectrum48K;
            }

            ushort extLen = br.ReadUInt16();
            if (extLen < 3 || fs.Position + extLen > fs.Length)
            {
                return SpectrumModel.Spectrum48K;
            }

            byte[] ext = br.ReadBytes(extLen);
            return ModelFromHardwareMode(extLen, ext[2]);
        }

        /// <summary>
        /// Load a .z80 snapshot (v1, v2 or v3).
        /// </summary>
        public static void LoadZ80(this Z80 cpu, SpectrumMemory memory, SpectrumUlaRenderer renderer, string path)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            byte[] h = br.ReadBytes(30);
            if (h.Length < 30)
            {
                throw new InvalidDataException("Invalid Z80 header.");
            }

            cpu.A = h[0];
            cpu.SetF(h[1]);
            cpu.C = h[2];
            cpu.B = h[3];
            cpu.L = h[4];
            cpu.H = h[5];
            ushort savedPC = (ushort)(h[6] | (h[7] << 8));
            cpu.SP = (ushort)(h[8] | (h[9] << 8));
            cpu.I = h[10];
            cpu.R = (byte)((h[11] & 0x7F) | ((h[12] & 0x01) << 7));

            byte flags1 = h[12];
            bool v1Compressed = (flags1 & 0x20) != 0;
            byte border = (byte)((flags1 >> 1) & 0x07);

            cpu.E = h[13];
            cpu.D = h[14];
            cpu.C_ = h[15];
            cpu.B_ = h[16];
            cpu.E_ = h[17];
            cpu.D_ = h[18];
            cpu.L_ = h[19];
            cpu.H_ = h[20];
            cpu.A_ = h[21];
            cpu.F_ = h[22];

            cpu.IY = (ushort)(h[23] | (h[24] << 8));
            cpu.IX = (ushort)(h[25] | (h[26] << 8));
            cpu.SetInterruptState((byte)(h[29] & 0x03), h[27] != 0, h[28] != 0);
            cpu.SetHalted(false);

            if (savedPC != 0)
            {
                cpu.PC = savedPC;

                byte[] ram48 = v1Compressed
                    ? DecompressZ80(br, 49152)
                    : br.ReadBytes(49152);

                Load48kMemory(memory, ram48);
            }
            else
            {
                ushort extLen = br.ReadUInt16();
                byte[] ext = br.ReadBytes(extLen);
                if (ext.Length < 2)
                {
                    throw new InvalidDataException("Invalid Z80 extended header.");
                }

                cpu.PC = (ushort)(ext[0] | (ext[1] << 8));
                byte port7ffd = ext.Length > 3 ? ext[3] : (byte)0;
                byte port1ffd = GetPort1ffd(ext);

                while (br.BaseStream.Position < fs.Length)
                {
                    ushort blockLen = br.ReadUInt16();
                    byte pageNum = br.ReadByte();

                    bool isCompressed = blockLen != 0xFFFF;
                    int dataLen = isCompressed ? blockLen : 16384;
                    byte[] rawData = br.ReadBytes(dataLen);

                    byte[] pageData = isCompressed
                        ? DecompressBlock(rawData, 16384)
                        : rawData;

                    if (SupportsPaging(memory.Model) && pageNum >= 3 && pageNum <= 10)
                    {
                        int bankIndex = pageNum - 3;
                        memory.LoadRamBank(bankIndex, pageData);
                    }
                    else
                    {
                        ushort baseAddr = pageNum switch
                        {
                            4 => 0x8000,
                            5 => 0xC000,
                            8 => 0x4000,
                            _ => (ushort)(pageNum * 0x4000)
                        };

                        for (int i = 0; i < 16384; i++)
                        {
                            memory.WriteDirect((ushort)(baseAddr + i), pageData[i]);
                        }
                    }
                }

                if (SupportsPaging(memory.Model))
                {
                    memory.WritePort7FFD(port7ffd);
                    if (SpectrumModelTraits.SupportsPlus3Paging(memory.Model))
                    {
                        memory.WritePort1FFD(port1ffd);
                    }
                }
            }

            renderer.BorderColorIndex = border;
        }
        private static byte[] DecompressBlock(byte[] data, int expected)
        {
            var mem = new List<byte>(expected);
            int i = 0;
            while (mem.Count < expected && i < data.Length)
            {
                if (i + 3 < data.Length && data[i] == 0xED && data[i + 1] == 0xED)
                {
                    byte count = data[i + 2];
                    byte val = data[i + 3];
                    for (int c = 0; c < count && mem.Count < expected; c++)
                    {
                        mem.Add(val);
                    }
                    i += 4;
                }
                else
                {
                    mem.Add(data[i]);
                    i++;
                }
            }

            while (mem.Count < expected)
            {
                mem.Add(0);
            }

            return mem.ToArray();
        }
        private static byte[] DecompressZ80(BinaryReader br, int expected)
        {
            var mem = new List<byte>(expected);
            while (mem.Count < expected && br.BaseStream.Position < br.BaseStream.Length)
            {
                byte b = br.ReadByte();
                if (b == 0xED && br.BaseStream.Position < br.BaseStream.Length)
                {
                    byte next = br.ReadByte();
                    if (next == 0xED)
                    {
                        byte count = br.ReadByte();
                        byte val = br.ReadByte();
                        if (count == 0 && val == 0)
                        {
                            break;
                        }

                        for (int i = 0; i < count; i++)
                        {
                            mem.Add(val);
                        }
                    }
                    else
                    {
                        mem.Add(b);
                        if (mem.Count < expected)
                        {
                            mem.Add(next);
                        }
                    }
                }
                else
                {
                    mem.Add(b);
                }
            }

            while (mem.Count < expected && br.BaseStream.Position < br.BaseStream.Length)
            {
                mem.Add(br.ReadByte());
            }

            while (mem.Count < expected)
            {
                mem.Add(0);
            }

            return mem.ToArray();
        }
        private static byte GetPort1ffd(byte[] extendedHeader)
        {
            return extendedHeader.Length > 29 ? extendedHeader[29] : (byte)0;
        }
        private static SpectrumModel ModelFromHardwareMode(int extendedHeaderLength, byte hardwareMode)
        {
            if (extendedHeaderLength == 23)
            {
                return hardwareMode switch
                {
                    3 or 4 => SpectrumModel.Spectrum128K,
                    _ => SpectrumModel.Spectrum48K
                };
            }

            return hardwareMode switch
            {
                4 or 5 or 6 => SpectrumModel.Spectrum128K,
                7 or 8 => SpectrumModel.SpectrumPlus3,
                9 => SpectrumModel.Pentagon128,
                10 => SpectrumModel.Scorpion256,
                12 => SpectrumModel.SpectrumPlus2,
                13 => SpectrumModel.SpectrumPlus2A,
                _ => SpectrumModel.Spectrum48K
            };
        }
        private static void Load48kMemory(SpectrumMemory memory, byte[] data)
        {
            if (data.Length != 49152)
            {
                throw new ArgumentException("Z80 v1 RAM payload must be 49152 bytes.", nameof(data));
            }

            if (SupportsPaging(memory.Model))
            {
                memory.WritePort7FFD(0x00);
                memory.LoadRamBank(5, data.AsSpan(0, 0x4000));
                memory.LoadRamBank(2, data.AsSpan(0x4000, 0x4000));
                memory.LoadRamBank(0, data.AsSpan(0x8000, 0x4000));
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
