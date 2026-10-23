using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ZedExEss.FileHandlers;

/// <summary>One frame of a recording: how far the CPU ran, and what it read.</summary>
/// <remarks>
/// A frame with no input values of its own repeats the previous frame's, which is how a
/// recording of a game that polls the same keys every frame stays small.
/// </remarks>
public sealed record RzxFrame(ushort FetchCount, byte[] InputValues, bool RepeatsPrevious);

/// <summary>A complete input recording, with the machine state it starts from.</summary>
public sealed class RzxRecording
{
    public required string SnapshotExtension { get; init; }

    public required byte[] SnapshotData { get; init; }

    public required IReadOnlyList<RzxFrame> Frames { get; init; }

    public uint StartTstates { get; init; }

    public string Creator { get; init; } = "ZedExEss";
}

/// <summary>
/// Reads and writes RZX input recordings.
/// </summary>
/// <remarks>
/// An RZX holds a snapshot and then, for every frame, the number of opcode fetches the
/// CPU made and every byte it read from a port. Replaying it means running the same
/// machine from the same state and handing back the same bytes, so the run is reproduced
/// exactly without storing a single keypress.
/// </remarks>
public static class RzxFile
{
    private const byte CreatorBlock = 0x10;
    private const byte SnapshotBlock = 0x30;
    private const byte InputRecordingBlock = 0x80;

    /// <summary>Frames whose input list is identical to the previous frame's.</summary>
    private const ushort RepeatPreviousFrame = 0xFFFF;

    private const uint ExternalSnapshotFlag = 0x01;

    /// <summary>Input-block flag: the frames themselves are encrypted.</summary>
    private const uint ProtectedFlag = 0x01;
    private const uint CompressedFlag = 0x02;

    public static void Write(string path, RzxRecording recording)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(recording);

        using FileStream stream = File.Create(path);
        Write(stream, recording);
    }

    public static void Write(Stream stream, RzxRecording recording)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(recording);

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(new[] { (byte)'R', (byte)'Z', (byte)'X', (byte)'!' });
        writer.Write((byte)0);
        writer.Write((byte)13);
        writer.Write(0u);

        WriteCreatorBlock(writer, recording.Creator);
        WriteSnapshotBlock(writer, recording);
        WriteInputBlock(writer, recording);
    }

    public static RzxRecording Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }

    public static RzxRecording Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RZX!")
        {
            throw new InvalidDataException("Not an RZX recording.");
        }

        reader.ReadByte();
        reader.ReadByte();

        // Bit 0 of the header flags says the data is signed, not that it is encrypted:
        // a signed recording reads exactly like an unsigned one, and only the signature
        // block - which is skipped - carries anything extra.
        reader.ReadUInt32();

        string creator = "unknown";
        string extension = string.Empty;
        byte[] snapshot = [];
        List<RzxFrame> frames = [];
        uint startTstates = 0;
        bool haveStartTstates = false;

        // Where a snapshot turned up after frames had already been read. A recording
        // may be written as several snapshot-and-input pairs, and one that appears
        // part way through is a resynchronisation point rather than a starting state.
        int lastSnapshotFrame = -1;

        while (stream.Position < stream.Length)
        {
            long blockStart = stream.Position;
            byte id = reader.ReadByte();
            uint length = reader.ReadUInt32();
            if (length < 5 || blockStart + length > stream.Length)
            {
                throw new InvalidDataException($"Invalid RZX block {id:X2} at offset {blockStart}.");
            }

            switch (id)
            {
                case CreatorBlock:
                    creator = Encoding.ASCII.GetString(reader.ReadBytes(20)).TrimEnd('\0', ' ');
                    break;

                case SnapshotBlock:
                {
                    (string blockExtension, byte[] blockSnapshot) =
                        ReadSnapshotBlock(reader, blockStart, length);

                    if (snapshot.Length == 0)
                    {
                        extension = blockExtension;
                        snapshot = blockSnapshot;
                    }
                    else
                    {
                        lastSnapshotFrame = frames.Count;
                    }

                    break;
                }

                case InputRecordingBlock:
                {
                    (List<RzxFrame> blockFrames, uint blockTstates) =
                        ReadInputBlock(reader, blockStart, length);

                    // Later blocks continue the recording rather than replace it; only
                    // the first says where in the frame the machine started.
                    if (!haveStartTstates)
                    {
                        startTstates = blockTstates;
                        haveStartTstates = true;
                    }

                    frames.AddRange(blockFrames);
                    break;
                }
            }

            stream.Position = blockStart + length;
        }

        if (snapshot.Length == 0)
        {
            throw new InvalidDataException("The recording has no snapshot to start from.");
        }

        // A snapshot sitting between two runs of frames has to be applied when playback
        // reaches it, which this does not do. One that follows the last frame is just
        // the state the recording finished in, and can be ignored.
        if (lastSnapshotFrame >= 0 && lastSnapshotFrame < frames.Count)
        {
            throw new InvalidDataException(
                $"The recording restarts from a new snapshot at frame {lastSnapshotFrame}, "
                    + "which is not supported.");
        }

        return new RzxRecording
        {
            SnapshotExtension = extension,
            SnapshotData = snapshot,
            Frames = frames,
            StartTstates = startTstates,
            Creator = creator
        };
    }

    private static void WriteCreatorBlock(BinaryWriter writer, string creator)
    {
        writer.Write(CreatorBlock);
        writer.Write(29u);

        var name = new byte[20];
        byte[] ascii = Encoding.ASCII.GetBytes(creator);
        Array.Copy(ascii, name, Math.Min(ascii.Length, name.Length));
        writer.Write(name);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
    }

    private static void WriteSnapshotBlock(BinaryWriter writer, RzxRecording recording)
    {
        byte[] compressed = Compress(recording.SnapshotData);
        var extension = new byte[4];
        byte[] ascii = Encoding.ASCII.GetBytes(recording.SnapshotExtension);
        Array.Copy(ascii, extension, Math.Min(ascii.Length, extension.Length));

        writer.Write(SnapshotBlock);
        writer.Write((uint)(17 + compressed.Length));
        writer.Write(CompressedFlag);
        writer.Write(extension);
        writer.Write((uint)recording.SnapshotData.Length);
        writer.Write(compressed);
    }

    private static void WriteInputBlock(BinaryWriter writer, RzxRecording recording)
    {
        using var frameStream = new MemoryStream();
        using (var frameWriter = new BinaryWriter(frameStream, Encoding.ASCII, leaveOpen: true))
        {
            foreach (RzxFrame frame in recording.Frames)
            {
                frameWriter.Write(frame.FetchCount);
                if (frame.RepeatsPrevious)
                {
                    frameWriter.Write(RepeatPreviousFrame);
                    continue;
                }

                frameWriter.Write((ushort)frame.InputValues.Length);
                frameWriter.Write(frame.InputValues);
            }
        }

        byte[] compressed = Compress(frameStream.ToArray());
        writer.Write(InputRecordingBlock);
        writer.Write((uint)(18 + compressed.Length));
        writer.Write((uint)recording.Frames.Count);
        writer.Write((byte)0);
        writer.Write(recording.StartTstates);
        writer.Write(CompressedFlag);
        writer.Write(compressed);
    }

    private static (string Extension, byte[] Data) ReadSnapshotBlock(
        BinaryReader reader,
        long blockStart,
        uint length)
    {
        uint flags = reader.ReadUInt32();
        string extension = Encoding.ASCII.GetString(reader.ReadBytes(4)).TrimEnd('\0', ' ');
        uint uncompressedLength = reader.ReadUInt32();
        if ((flags & ExternalSnapshotFlag) != 0)
        {
            throw new InvalidDataException("The recording refers to an external snapshot file.");
        }

        int payloadLength = (int)(blockStart + length - reader.BaseStream.Position);
        byte[] payload = reader.ReadBytes(payloadLength);
        return (
            extension,
            (flags & CompressedFlag) != 0 ? Decompress(payload, uncompressedLength) : payload);
    }

    private static (List<RzxFrame> Frames, uint StartTstates) ReadInputBlock(
        BinaryReader reader,
        long blockStart,
        uint length)
    {
        uint frameCount = reader.ReadUInt32();
        reader.ReadByte();
        uint startTstates = reader.ReadUInt32();
        uint flags = reader.ReadUInt32();
        if ((flags & ProtectedFlag) != 0)
        {
            throw new InvalidDataException("The recording's frames are encrypted.");
        }

        int payloadLength = (int)(blockStart + length - reader.BaseStream.Position);
        byte[] payload = reader.ReadBytes(payloadLength);
        if ((flags & CompressedFlag) != 0)
        {
            payload = Decompress(payload, 0);
        }

        var frames = new List<RzxFrame>((int)Math.Min(frameCount, 1 << 20));
        using var frameStream = new MemoryStream(payload, writable: false);
        using var frameReader = new BinaryReader(frameStream, Encoding.ASCII);
        byte[] previous = [];
        while (frameStream.Position + 4 <= frameStream.Length && frames.Count < frameCount)
        {
            ushort fetches = frameReader.ReadUInt16();
            ushort inputCount = frameReader.ReadUInt16();
            if (inputCount == RepeatPreviousFrame)
            {
                frames.Add(new RzxFrame(fetches, previous, RepeatsPrevious: true));
                continue;
            }

            byte[] values = frameReader.ReadBytes(inputCount);
            previous = values;
            frames.Add(new RzxFrame(fetches, values, RepeatsPrevious: false));
        }

        return (frames, startTstates);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data, uint expectedLength)
    {
        using var input = new MemoryStream(data, writable: false);
        using var inflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedLength > 0 ? (int)expectedLength : data.Length * 4);
        inflate.CopyTo(output);
        return output.ToArray();
    }
}
