using System;
using System.Collections.Generic;

namespace ZedExEss.Spectrum.Interface1;

/// <summary>A packed block and where it has to be loaded for it to unpack over itself.</summary>
/// <param name="Data">The packed bytes.</param>
/// <param name="LoadOffset">
/// How far past the start of the destination the packed bytes must be placed. Loading them
/// any lower would let the unpacker overwrite compressed bytes it has not read yet.
/// </param>
public sealed record MicrodrivePackedBlock(byte[] Data, int LoadOffset);

/// <summary>
/// Packs a block of memory so that it can be unpacked over itself.
/// </summary>
/// <remarks>
/// A 128K machine holds 131,072 bytes and a cartridge 130,048, so something has to give,
/// and there is nowhere to unpack to: every bank is itself part of the snapshot. So a
/// block is loaded into the top of the region it belongs in and unpacked downwards over
/// its own compressed form.
///
/// That works as long as the writer never catches the reader. Where they are relative to
/// each other is decided entirely by the packed data, so packing walks its own output and
/// records the worst moment - <see cref="MicrodrivePackedBlock.LoadOffset"/> - and the
/// loader puts the block that far in. A block whose worst moment leaves it too big to sit
/// inside its own destination cannot be unpacked in place at all, and is reported as
/// unpackable so the caller stores it whole.
///
/// The encoding is plain LZSS: a flag bit per token, a literal byte or a two-byte
/// reference back into what has already been unpacked. Runs come out of it for free, as a
/// reference one byte back. Measured against a run-length scheme on real Spectrum memory
/// it is worth about a third: 66% of the original where run-length managed 87%.
/// </remarks>
public static class MicrodriveCompression
{
    private const int WindowSize = 4096;
    private const int ShortestMatch = 3;
    private const int LongestMatch = 18;

    /// <summary>Packs a block, or returns null when it cannot usefully be unpacked in place.</summary>
    public static MicrodrivePackedBlock? TryPack(ReadOnlySpan<byte> data)
    {
        var packed = new List<byte>(data.Length);
        var tokens = new List<(bool Literal, int Length)>();

        // One chain of earlier positions per three-byte sequence, so the search for a
        // match does not have to walk the whole window every time.
        var latest = new Dictionary<int, int>();
        var previous = new int[data.Length];
        Array.Fill(previous, -1);

        int position = 0;
        while (position < data.Length)
        {
            int bestLength = 0;
            int bestDistance = 0;

            if (position + ShortestMatch <= data.Length)
            {
                int key = data[position] | (data[position + 1] << 8) | (data[position + 2] << 16);
                int candidate = latest.GetValueOrDefault(key, -1);
                int limit = Math.Max(0, position - WindowSize);

                while (candidate >= limit && bestLength < LongestMatch)
                {
                    int length = 0;
                    while (length < LongestMatch
                        && position + length < data.Length
                        && data[candidate + length] == data[position + length])
                    {
                        length++;
                    }

                    if (length > bestLength)
                    {
                        bestLength = length;
                        bestDistance = position - candidate;
                    }

                    candidate = previous[candidate];
                }
            }

            int consumed = bestLength >= ShortestMatch ? bestLength : 1;
            for (int i = 0; i < consumed; i++)
            {
                int at = position + i;
                if (at + ShortestMatch > data.Length)
                {
                    break;
                }

                int key = data[at] | (data[at + 1] << 8) | (data[at + 2] << 16);
                previous[at] = latest.GetValueOrDefault(key, -1);
                latest[key] = at;
            }

            if (bestLength >= ShortestMatch)
            {
                tokens.Add((false, bestLength));
                int offset = bestDistance - 1;
                packed.Add(0);                                  // patched with the flags
                packed.Add((byte)(offset & 0xFF));
                packed.Add((byte)(((offset >> 8) << 4) | (bestLength - ShortestMatch)));
            }
            else
            {
                tokens.Add((true, 1));
                packed.Add(0);
                packed.Add(data[position]);
            }

            position += consumed;
        }

        return Assemble(packed, tokens, data.Length);
    }

    /// <summary>
    /// Interleaves the flag bytes and works out how far in the block has to be loaded.
    /// </summary>
    /// <remarks>
    /// Tokens are emitted above with a spare byte in front of each; this drops those and
    /// puts one flag byte in front of every eight, which is the shape the unpacker reads.
    /// Walking the result in the same order the unpacker will tells us the furthest the
    /// writer ever gets ahead of the reader, which is what decides where the block loads.
    /// </remarks>
    private static MicrodrivePackedBlock? Assemble(
        List<byte> emitted,
        List<(bool Literal, int Length)> tokens,
        int originalLength)
    {
        var packed = new List<byte>(emitted.Count);
        int read = 0;
        int written = 0;
        int worst = 0;

        int source = 0;
        for (int start = 0; start < tokens.Count; start += 8)
        {
            int count = Math.Min(8, tokens.Count - start);
            byte flags = 0;
            for (int i = 0; i < count; i++)
            {
                if (tokens[start + i].Literal)
                {
                    flags |= (byte)(1 << i);
                }
            }

            packed.Add(flags);
            read++;

            for (int i = 0; i < count; i++)
            {
                (bool literal, int length) = tokens[start + i];
                source++;                                   // the spare byte
                if (literal)
                {
                    packed.Add(emitted[source++]);
                    read++;
                }
                else
                {
                    packed.Add(emitted[source++]);
                    packed.Add(emitted[source++]);
                    read += 2;
                }

                written += length;
                worst = Math.Max(worst, written - read);
            }
        }

        // It has to end up smaller, and it has to fit inside its own destination once
        // pushed far enough in to be safe.
        if (packed.Count >= originalLength || worst + packed.Count > originalLength)
        {
            return null;
        }

        return new MicrodrivePackedBlock([.. packed], worst);
    }

    /// <summary>Unpacks a block, for checking what was packed.</summary>
    public static byte[] Unpack(ReadOnlySpan<byte> packed, int length)
    {
        var data = new byte[length];
        int read = 0;
        int write = 0;

        while (write < length)
        {
            byte flags = packed[read++];
            for (int bit = 0; bit < 8 && write < length; bit++)
            {
                if ((flags & (1 << bit)) != 0)
                {
                    data[write++] = packed[read++];
                    continue;
                }

                int low = packed[read++];
                int high = packed[read++];
                int distance = (low | ((high >> 4) << 8)) + 1;
                int run = (high & 0x0F) + ShortestMatch;
                for (int i = 0; i < run && write < length; i++)
                {
                    data[write] = data[write - distance];
                    write++;
                }
            }
        }

        return data;
    }

    /// <summary>
    /// The Z80 that unpacks a block ending at the top of memory.
    /// </summary>
    /// <remarks>
    /// Entered with HL on the packed bytes and DE on the first byte of the destination. It
    /// stops when the destination pointer wraps to zero, which is what happens after the
    /// last byte of a block ending at FFFF - the address a paged bank appears at. The
    /// routine keeps its working bytes inside itself, so it needs no stack beyond the one
    /// word LDIR does not use, and every jump is absolute so that assembling it by hand
    /// cannot go quietly wrong.
    /// </remarks>
    public static byte[] UnpackRoutine(ushort address)
    {
        var code = new List<byte>();
        var patches = new List<(int Operand, string Label)>();
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);

        void Jump(byte opcode, string label)
        {
            code.Add(opcode);
            patches.Add((code.Count, label));
            code.Add(0);
            code.Add(0);
        }

        void Absolute(byte[] opcode, string label)
        {
            code.AddRange(opcode);
            patches.Add((code.Count, label));
            code.Add(0);
            code.Add(0);
        }

        code.Add(0xAF);                                  // XOR A
        Absolute([0x32], "count");                       // LD (count),A

        labels["loop"] = code.Count;
        code.Add(0x7A);                                  // LD A,D
        code.Add(0xB3);                                  // OR E
        code.Add(0xC8);                                  // RET Z          ; wrapped: done
        Absolute([0x3A], "count");                       // LD A,(count)
        code.Add(0xB7);                                  // OR A
        Jump(0xC2, "haveBits");                          // JP NZ,haveBits
        code.Add(0x7E);                                  // LD A,(HL)
        code.Add(0x23);                                  // INC HL
        Absolute([0x32], "flags");                       // LD (flags),A
        code.Add(0x3E);                                  // LD A,8
        code.Add(0x08);

        labels["haveBits"] = code.Count;
        code.Add(0x3D);                                  // DEC A
        Absolute([0x32], "count");                       // LD (count),A
        Absolute([0x3A], "flags");                       // LD A,(flags)
        code.Add(0x0F);                                  // RRCA           ; oldest flag out
        Absolute([0x32], "flags");                       // LD (flags),A
        Jump(0xD2, "match");                             // JP NC,match

        code.Add(0x7E);                                  // LD A,(HL)      ; a literal byte
        code.Add(0x23);                                  // INC HL
        code.Add(0x12);                                  // LD (DE),A
        code.Add(0x13);                                  // INC DE
        Jump(0xC3, "loop");                              // JP loop

        labels["match"] = code.Count;
        code.Add(0x7E);                                  // LD A,(HL)      ; distance, low
        code.Add(0x23);                                  // INC HL
        code.Add(0x4F);                                  // LD C,A
        code.Add(0x7E);                                  // LD A,(HL)      ; length and high
        code.Add(0x23);                                  // INC HL
        code.Add(0x47);                                  // LD B,A
        code.Add(0xE6);                                  // AND $0F
        code.Add(0x0F);
        code.Add(0xC6);                                  // ADD A,3
        code.Add(ShortestMatch);
        Absolute([0x32], "length");                      // LD (length),A
        code.Add(0x78);                                  // LD A,B
        code.Add(0xE6);                                  // AND $F0
        code.Add(0xF0);
        code.Add(0x0F);                                  // RRCA
        code.Add(0x0F);                                  // RRCA
        code.Add(0x0F);                                  // RRCA
        code.Add(0x0F);                                  // RRCA
        code.Add(0x47);                                  // LD B,A
        code.Add(0x03);                                  // INC BC         ; distance
        code.Add(0xE5);                                  // PUSH HL
        code.Add(0x62);                                  // LD H,D
        code.Add(0x6B);                                  // LD L,E
        code.Add(0xB7);                                  // OR A           ; clear carry
        code.Add(0xED);                                  // SBC HL,BC      ; back into the output
        code.Add(0x42);
        Absolute([0x3A], "length");                      // LD A,(length)
        code.Add(0x06);                                  // LD B,0
        code.Add(0x00);
        code.Add(0x4F);                                  // LD C,A
        code.Add(0xED);                                  // LDIR
        code.Add(0xB0);
        code.Add(0xE1);                                  // POP HL
        Jump(0xC3, "loop");                              // JP loop

        labels["flags"] = code.Count;
        code.Add(0x00);
        labels["count"] = code.Count;
        code.Add(0x00);
        labels["length"] = code.Count;
        code.Add(0x00);

        foreach ((int operand, string label) in patches)
        {
            int target = address + labels[label];
            code[operand] = (byte)(target & 0xFF);
            code[operand + 1] = (byte)(target >> 8);
        }

        return [.. code];
    }
}
