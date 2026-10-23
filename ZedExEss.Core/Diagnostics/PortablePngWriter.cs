using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ZedExEss.Diagnostics
{
    /// <summary>
    /// Writes the emulator's ARGB framebuffer as an RGBA PNG without a desktop
    /// imaging dependency, keeping screenshot-producing verification portable.
    /// </summary>
    internal static class PortablePngWriter
    {
        private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

        public static void WriteArgb32(string path, int[] pixels, int width, int height)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(pixels);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

            int pixelCount = checked(width * height);
            if (pixels.Length < pixelCount)
            {
                throw new ArgumentException("The framebuffer is smaller than the requested image.", nameof(pixels));
            }

            using FileStream output = File.Create(path);
            output.Write(Signature);

            Span<byte> header = stackalloc byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
            header[8] = 8; // Eight bits per channel.
            header[9] = 6; // RGBA true-colour.
            WriteChunk(output, "IHDR", header);

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                byte[] row = new byte[checked(1 + width * 4)];
                for (int y = 0; y < height; y++)
                {
                    row[0] = 0; // PNG filter type: None.
                    int source = y * width;
                    int destination = 1;
                    for (int x = 0; x < width; x++)
                    {
                        int argb = pixels[source + x];
                        row[destination++] = (byte)(argb >> 16);
                        row[destination++] = (byte)(argb >> 8);
                        row[destination++] = (byte)argb;
                        row[destination++] = (byte)(argb >> 24);
                    }

                    zlib.Write(row);
                }
            }

            WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
            WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        }

        private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            output.Write(length);

            Span<byte> typeBytes = stackalloc byte[4];
            Encoding.ASCII.GetBytes(type, typeBytes);
            output.Write(typeBytes);
            output.Write(data);

            uint crc = UpdateCrc(uint.MaxValue, typeBytes);
            crc = UpdateCrc(crc, data) ^ uint.MaxValue;
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            output.Write(crcBytes);
        }

        private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                {
                    uint mask = (uint)-(int)(crc & 1);
                    crc = (crc >> 1) ^ (0xEDB88320u & mask);
                }
            }

            return crc;
        }
    }
}
