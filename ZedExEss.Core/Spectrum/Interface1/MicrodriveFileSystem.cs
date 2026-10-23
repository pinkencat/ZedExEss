using System;
using System.Collections.Generic;
using System.Text;

namespace ZedExEss.Spectrum.Interface1;

/// <summary>What a Microdrive file holds, as the first byte of its header says.</summary>
public enum MicrodriveFileKind : byte
{
    BasicProgram = 0,
    NumberArray = 1,
    CharacterArray = 2,
    Code = 3
}

/// <summary>One file found in a cartridge's catalogue.</summary>
public sealed record MicrodriveFileEntry(
    string Name,
    MicrodriveFileKind Kind,
    int Length,
    ushort Start,
    ushort LengthWithoutVariables,
    ushort AutostartLine,
    int RecordCount);

/// <summary>
/// Reads and writes named files in a cartridge, in the layout the Interface 1 ROM uses.
/// </summary>
/// <remarks>
/// The cartridge itself is a run of raw sectors; the ROM imposes a file structure on top
/// of them, and that structure is what this understands. Each sector carries a record
/// descriptor naming the file it belongs to, its record number, and how many of the 512
/// data bytes are used; the first record of a file begins with a nine-byte header giving
/// the type, length and addresses, and the last record is marked as the end.
///
/// The layout here was taken from cartridges written by the real ROM rather than from a
/// description of it: a saved screen reports a record length of 6921 for 6912 bytes of
/// data, which is what pins the header at nine bytes and shows that it is counted in the
/// first record's length.
/// </remarks>
public static class MicrodriveFileSystem
{
    /// <summary>Bytes of the descriptor that precede a file's data in its first record.</summary>
    public const int FileHeaderLength = 9;

    /// <summary>Set on every record belonging to a file.</summary>
    private const byte RecordInUse = 0x04;

    /// <summary>Set on a file's last record.</summary>
    private const byte RecordEndOfFile = 0x02;

    private const int NameLength = 10;
    private const int DescriptorOffset = MicrodriveCartridge.HeaderLength;
    private const int DescriptorLength = MicrodriveCartridge.HeaderLength;
    private const int DataOffset = DescriptorOffset + DescriptorLength;

    /// <summary>Writes a machine-code file, as SAVE ... CODE would.</summary>
    public static void WriteCode(
        MicrodriveCartridge cartridge,
        string name,
        ReadOnlySpan<byte> data,
        ushort startAddress)
    {
        Write(cartridge, name, MicrodriveFileKind.Code, data, startAddress, 0xFFFF, 0xFFFF);
    }

    /// <summary>
    /// Writes a BASIC program, optionally one that runs itself when loaded.
    /// </summary>
    /// <param name="autostartLine">
    /// The line to jump to once loaded. Anything above 9999 means "do not run", which is
    /// how the ROM records a program saved without a line number.
    /// </param>
    public static void WriteBasicProgram(
        MicrodriveCartridge cartridge,
        string name,
        ReadOnlySpan<byte> program,
        ushort autostartLine = 0xFFFF,
        ushort programAddress = 0x5D05)
    {
        Write(
            cartridge,
            name,
            MicrodriveFileKind.BasicProgram,
            program,
            programAddress,
            (ushort)program.Length,
            autostartLine);
    }

    /// <summary>Lists the files on a cartridge, in the order their first records appear.</summary>
    public static IReadOnlyList<MicrodriveFileEntry> Catalogue(MicrodriveCartridge cartridge)
    {
        ArgumentNullException.ThrowIfNull(cartridge);

        var lengths = new Dictionary<string, int>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var headers = new Dictionary<string, MicrodriveFileEntry>(StringComparer.Ordinal);
        var order = new List<string>();

        for (int sector = 0; sector < cartridge.SectorCount; sector++)
        {
            int offset = sector * MicrodriveCartridge.SectorLength;
            byte flags = cartridge.ReadByte(offset + DescriptorOffset);
            if ((flags & RecordInUse) == 0)
            {
                continue;
            }

            string name = ReadName(cartridge, offset);
            int length = cartridge.ReadByte(offset + DescriptorOffset + 2)
                | (cartridge.ReadByte(offset + DescriptorOffset + 3) << 8);

            lengths[name] = lengths.GetValueOrDefault(name) + length;
            counts[name] = counts.GetValueOrDefault(name) + 1;

            if (cartridge.ReadByte(offset + DescriptorOffset + 1) != 0)
            {
                continue;
            }

            order.Add(name);
            headers[name] = new MicrodriveFileEntry(
                name,
                (MicrodriveFileKind)cartridge.ReadByte(offset + DataOffset),
                0,
                ReadWord(cartridge, offset + DataOffset + 3),
                ReadWord(cartridge, offset + DataOffset + 5),
                ReadWord(cartridge, offset + DataOffset + 7),
                0);
        }

        var files = new List<MicrodriveFileEntry>(order.Count);
        foreach (string name in order)
        {
            MicrodriveFileEntry header = headers[name];
            files.Add(header with
            {
                Length = lengths[name] - FileHeaderLength,
                RecordCount = counts[name]
            });
        }

        return files;
    }

    /// <summary>Reads a file's contents back, without its nine-byte header.</summary>
    public static byte[]? TryReadFile(MicrodriveCartridge cartridge, string name)
    {
        ArgumentNullException.ThrowIfNull(cartridge);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string wanted = FormatName(name);
        var records = new SortedDictionary<int, (int Offset, int Length)>();
        for (int sector = 0; sector < cartridge.SectorCount; sector++)
        {
            int offset = sector * MicrodriveCartridge.SectorLength;
            byte flags = cartridge.ReadByte(offset + DescriptorOffset);
            if ((flags & RecordInUse) == 0 || ReadName(cartridge, offset) != wanted)
            {
                continue;
            }

            int record = cartridge.ReadByte(offset + DescriptorOffset + 1);
            int length = cartridge.ReadByte(offset + DescriptorOffset + 2)
                | (cartridge.ReadByte(offset + DescriptorOffset + 3) << 8);
            records[record] = (offset, length);
        }

        if (records.Count == 0)
        {
            return null;
        }

        var content = new List<byte>();
        foreach ((int record, (int offset, int length)) in records)
        {
            int start = record == 0 ? FileHeaderLength : 0;
            for (int i = start; i < length; i++)
            {
                content.Add(cartridge.ReadByte(offset + DataOffset + i));
            }
        }

        return [.. content];
    }

    private static void Write(
        MicrodriveCartridge cartridge,
        string name,
        MicrodriveFileKind kind,
        ReadOnlySpan<byte> data,
        ushort start,
        ushort lengthWithoutVariables,
        ushort autostartLine)
    {
        ArgumentNullException.ThrowIfNull(cartridge);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (cartridge.WriteProtected)
        {
            throw new InvalidOperationException("The cartridge is write protected.");
        }

        // The header shares the first record with the start of the data, so the file
        // occupies its own length plus those nine bytes.
        var payload = new byte[FileHeaderLength + data.Length];
        payload[0] = (byte)kind;
        WriteWord(payload, 1, (ushort)data.Length);
        WriteWord(payload, 3, start);
        WriteWord(payload, 5, lengthWithoutVariables);
        WriteWord(payload, 7, autostartLine);
        data.CopyTo(payload.AsSpan(FileHeaderLength));

        int records = Math.Max(1, (payload.Length + MicrodriveCartridge.DataLength - 1)
            / MicrodriveCartridge.DataLength);

        int[] free = FindFreeSectors(cartridge, records);
        if (free.Length < records)
        {
            throw new InvalidOperationException(
                $"'{name}' needs {records} free sectors; the cartridge has {free.Length}.");
        }

        string stored = FormatName(name);
        for (int record = 0; record < records; record++)
        {
            int offset = free[record] * MicrodriveCartridge.SectorLength;
            int position = record * MicrodriveCartridge.DataLength;
            int length = Math.Min(MicrodriveCartridge.DataLength, payload.Length - position);

            Span<byte> descriptor = stackalloc byte[DescriptorLength];
            descriptor[0] = (byte)(RecordInUse | (record == records - 1 ? RecordEndOfFile : 0));
            descriptor[1] = (byte)record;
            descriptor[2] = (byte)(length & 0xFF);
            descriptor[3] = (byte)(length >> 8);
            Encoding.ASCII.GetBytes(stored.PadRight(NameLength), descriptor[4..14]);
            descriptor[14] = MicrodriveCartridge.Checksum(descriptor[..14]);

            for (int i = 0; i < descriptor.Length; i++)
            {
                Require(cartridge.TryWriteByte(offset + DescriptorOffset + i, descriptor[i]));
            }

            Span<byte> block = stackalloc byte[MicrodriveCartridge.DataLength];
            payload.AsSpan(position, length).CopyTo(block);
            for (int i = 0; i < block.Length; i++)
            {
                Require(cartridge.TryWriteByte(offset + DataOffset + i, block[i]));
            }

            Require(cartridge.TryWriteByte(
                offset + DataOffset + MicrodriveCartridge.DataLength,
                MicrodriveCartridge.Checksum(block)));
        }
    }

    private static void Require(bool written)
    {
        if (!written)
        {
            throw new InvalidOperationException("The cartridge refused a write.");
        }
    }

    private static int[] FindFreeSectors(MicrodriveCartridge cartridge, int wanted)
    {
        var free = new List<int>(wanted);
        for (int sector = 0; sector < cartridge.SectorCount && free.Count < wanted; sector++)
        {
            int offset = sector * MicrodriveCartridge.SectorLength;

            // A sector belongs to the cartridge only if its own header survived
            // formatting, and is available only if no record has claimed it.
            if ((cartridge.ReadByte(offset) & 0x01) == 0)
            {
                continue;
            }

            if ((cartridge.ReadByte(offset + DescriptorOffset) & RecordInUse) == 0)
            {
                free.Add(sector);
            }
        }

        return [.. free];
    }

    private static string ReadName(MicrodriveCartridge cartridge, int sectorOffset)
    {
        Span<char> name = stackalloc char[NameLength];
        for (int i = 0; i < NameLength; i++)
        {
            name[i] = (char)cartridge.ReadByte(sectorOffset + DescriptorOffset + 4 + i);
        }

        return new string(name).TrimEnd();
    }

    private static ushort ReadWord(MicrodriveCartridge cartridge, int position) =>
        (ushort)(cartridge.ReadByte(position) | (cartridge.ReadByte(position + 1) << 8));

    private static void WriteWord(byte[] target, int position, ushort value)
    {
        target[position] = (byte)(value & 0xFF);
        target[position + 1] = (byte)(value >> 8);
    }

    /// <summary>Pads or trims a name to the ten characters a record descriptor holds.</summary>
    private static string FormatName(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length > NameLength)
        {
            trimmed = trimmed[..NameLength];
        }

        return trimmed;
    }
}
