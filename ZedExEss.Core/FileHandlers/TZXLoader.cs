using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Text;
using System;
using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.FileHandlers
{
    /// <summary>
    /// One constant-level interval in the decoded tape signal.
    /// </summary>
    /// <remarks>
    /// EndsWithEdge is false for pauses and the final run of direct recordings.
    /// IsData/IsLong are hints for accelerators; normal playback only needs level
    /// and duration.
    /// </remarks>
public readonly struct TapePulse(bool level, int tstates, bool isData = false, bool isLong = false, bool endsWithEdge = true)
    {
    public readonly bool Level = level;
    public readonly int TStates = tstates;
    public readonly bool IsData = isData;
    public readonly bool IsLong = isLong;
    public readonly bool EndsWithEdge = endsWithEdge;
    public readonly TapeAccelerationPulseFlags AccelerationFlags = !isData
        ? TapeAccelerationPulseFlags.None
        : isLong
            ? TapeAccelerationPulseFlags.LengthLong
            : TapeAccelerationPulseFlags.LengthShort;
    }
    /// <summary>Little-endian integer readers used by the TZX wire format.</summary>
    static class Bin
{
    public static ushort U16(this BinaryReader br)
        => (ushort)(br.ReadByte() | (br.ReadByte() << 8));
    public static uint U24(this BinaryReader br)
        => (uint)(br.ReadByte() | (br.ReadByte() << 8) | (br.ReadByte() << 16));
    public static uint U32(this BinaryReader br)
        => (uint)(br.ReadByte() | (br.ReadByte() << 8) | (br.ReadByte() << 16) | (br.ReadByte() << 24));
}
/// <summary>
/// Parsed TZX/TAP block that can contribute signal intervals to the playback stream.
/// Metadata and control-flow blocks intentionally append no pulses.
/// </summary>
public interface ITzxBlock { void AppendPulses(List<TapePulse> sink); }
/// <summary>TZX 0x12 pure-tone block.</summary>
sealed class Tone(BinaryReader br) : ITzxBlock
{
    readonly ushort _dur = br.U16(); readonly ushort _count = br.U16();
        public void AppendPulses(List<TapePulse> sink)
    {
        for (int i = 0; i < _count; i++)
        {
            sink.Add(new TapePulse(TzxLoader.Level, _dur));
            TzxLoader.Level = !TzxLoader.Level;
        }
    }

    public int RepCount => _count;
}
/// <summary>TZX 0x13 arbitrary pulse sequence.</summary>
sealed class PulseSeq : ITzxBlock
{
readonly ushort[] _p;
public PulseSeq(BinaryReader br) { int n = br.ReadByte(); _p = new ushort[n]; for (int i = 0; i < n; i++) _p[i] = br.U16(); }
public void AppendPulses(List<TapePulse> sink) { foreach (var t in _p) { sink.Add(new TapePulse(TzxLoader.Level, t)); TzxLoader.Level = !TzxLoader.Level; } }
public int PulsesCount => _p.Length;
}
/// <summary>TZX 0x10 ROM-format pilot, sync and data block.</summary>
sealed class StdData : ITzxBlock
{
    readonly ushort _pause; readonly byte[] _data;
    public StdData(BinaryReader br) { _pause = br.U16(); var len = br.U16(); _data = br.ReadBytes(len); }
    static void Half(List<TapePulse> s, int t) { s.Add(new TapePulse(TzxLoader.Level, t)); TzxLoader.Level = !TzxLoader.Level; }
    static void HalfData(List<TapePulse> s, int t, bool isLong)
    {
        s.Add(new TapePulse(TzxLoader.Level, t, isData: true, isLong: isLong));
        TzxLoader.Level = !TzxLoader.Level;
    }
    public void AppendPulses(List<TapePulse> sink)
    {
        int pilot = ((_data.Length > 0) && ((_data[0] & 0x80) == 0)) ? 8063 : 3223; for (int i = 0; i < pilot; i++) Half(sink, 2168);
        //int pilot = ((_data.Length > 0) && ((_data[0] & 0x80) == 0)) ? 8063 : 3223; for (int i = 0; i < pilot; i++) Half(sink, 2168);
        Half(sink, 667); Half(sink, 735);
        foreach (var b in _data)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                bool isLong = ((b >> bit) & 1) != 0;
                int t = isLong ? 1710 : 855;
                HalfData(sink, t, isLong);
                HalfData(sink, t, isLong);
            }
        }
        Half(sink, 855); Half(sink, 855); // tail so ROM latches last byte
        if (_pause != 0) sink.Add(new TapePulse(TzxLoader.Level, _pause * 3500, endsWithEdge: false));
    }

    public int DataLength => _data.Length;
    public bool IsHeader => _data.Length > 0 && _data[0] == 0x00;
    public byte FileType => _data.Length >= 2 ? _data[1] : (byte)0xFF;
    public string Name => _data.Length >= 12 ? Encoding.ASCII.GetString(_data, 2, 10).TrimEnd() : "";
    public ushort PayloadLen => _data.Length >= 14 ? (ushort)(_data[12] | (_data[13] << 8)) : (ushort)0;
    public ushort LoadAddress => _data.Length >= 16 ? (ushort)(_data[14] | (_data[15] << 8)) : (ushort)0;
    public byte[] Data => _data;
}
/// <summary>TZX 0x11 data block with explicitly supplied pilot, sync and bit timings.</summary>
sealed class Turbo : ITzxBlock
{
    readonly ushort P, S0, S1, B0, B1, Pause; readonly ushort Cnt; readonly byte TailBits; readonly byte[] Data;
    public Turbo(BinaryReader br) { P = br.U16(); S0 = br.U16(); S1 = br.U16(); B0 = br.U16(); B1 = br.U16(); Cnt = br.U16(); TailBits = br.ReadByte(); Pause = br.U16(); var n = br.U24(); Data = br.ReadBytes((int)n); }
    static void Half(List<TapePulse> s, int t) { s.Add(new TapePulse(TzxLoader.Level, t)); TzxLoader.Level = !TzxLoader.Level; }
    static void HalfData(List<TapePulse> s, int t, bool isData, bool isLong)
    {
        s.Add(new TapePulse(TzxLoader.Level, t, isData: isData, isLong: isLong));
        TzxLoader.Level = !TzxLoader.Level;
    }
    public void AppendPulses(List<TapePulse> sink)
    {
        for (int i = 0; i < Cnt; i++) Half(sink, P); Half(sink, S0); Half(sink, S1);
        int tailBits = TailBits == 0 ? 8 : TailBits;
        if (Data.Length > 0)
        {
            int full = Data.Length - 1;
            for (int i = 0; i < full; i++) EmitByte(Data[i], sink);
            if (tailBits < 8) EmitBits(Data[^1], tailBits, sink); else EmitByte(Data[^1], sink);
        }
        if (Pause != 0) sink.Add(new TapePulse(TzxLoader.Level, Pause * 3500, endsWithEdge: false));
    }
    void EmitByte(byte b, List<TapePulse> s) => EmitBits(b, 8, s);
    void EmitBits(byte b, int n, List<TapePulse> s)
    {
        bool hasDistinct = B0 != B1;
        int longPulse = hasDistinct ? Math.Max(B0, B1) : 0;
        for (int i = 7; i >= 8 - n; i--)
        {
            int t = ((b >> i) & 1) != 0 ? B1 : B0;
            bool isLong = hasDistinct && t == longPulse;
            HalfData(s, t, hasDistinct, isLong);
            HalfData(s, t, hasDistinct, isLong);
        }
    }
    public int DataLength => Data.Length;
    public ushort DataPulse0 => B0;
    public ushort DataPulse1 => B1;
}
/// <summary>TZX 0x14 data-only block; no pilot or sync pulses are implied.</summary>
sealed class PureData : ITzxBlock
{
    readonly ushort B0, B1, Pause; readonly byte Bits; readonly uint N; readonly byte[] Data;
    public PureData(BinaryReader br) { B0 = br.U16(); B1 = br.U16(); Bits = br.ReadByte(); Pause = br.U16(); N = br.U24(); Data = br.ReadBytes((int)N); }
    static void Half(List<TapePulse> s, int t) { s.Add(new TapePulse(TzxLoader.Level, t)); TzxLoader.Level = !TzxLoader.Level; }
    static void HalfData(List<TapePulse> s, int t, bool isData, bool isLong)
    {
        s.Add(new TapePulse(TzxLoader.Level, t, isData: isData, isLong: isLong));
        TzxLoader.Level = !TzxLoader.Level;
    }
    public void AppendPulses(List<TapePulse> sink)
    {
        int bits = Bits == 0 ? 8 : Bits;
        if (Data.Length > 0)
        {
            int full = Data.Length - 1;
            for (int i = 0; i < full; i++) EmitByte(Data[i], sink);
            if (bits < 8) EmitBits(Data[^1], bits, sink); else EmitByte(Data[^1], sink);
        }
        if (Pause != 0) sink.Add(new TapePulse(TzxLoader.Level, Pause * 3500, endsWithEdge: false)); // no extra half-cell
    }
    void EmitByte(byte b, List<TapePulse> s) => EmitBits(b, 8, s);
    void EmitBits(byte b, int n, List<TapePulse> s)
    {
        bool hasDistinct = B0 != B1;
        int longPulse = hasDistinct ? Math.Max(B0, B1) : 0;
        for (int i = 7; i >= 8 - n; i--)
        {
            int t = ((b >> i) & 1) != 0 ? B1 : B0;
            bool isLong = hasDistinct && t == longPulse;
            HalfData(s, t, hasDistinct, isLong);
            HalfData(s, t, hasDistinct, isLong);
        }
    }
    public int DataLength => (int)N;
    public ushort DataPulse0 => B0;
    public ushort DataPulse1 => B1;
}
/// <summary>Internal Speedlock header representation used while importing converted tape data.</summary>
sealed class SpeedlockHeader(BinaryReader br) : ITzxBlock
{
    readonly ushort P = br.U16(), S = br.U16(), B0 = br.U16(), B1 = br.U16(), Cnt = br.U16(), Pause = br.U16();

        public void AppendPulses(List<TapePulse> sink) { for (int i = 0; i < Cnt; i++) { sink.Add(new TapePulse(TzxLoader.Level, P)); TzxLoader.Level = !TzxLoader.Level; } sink.Add(new TapePulse(TzxLoader.Level, S)); TzxLoader.Level = !TzxLoader.Level; sink.Add(new TapePulse(TzxLoader.Level, S)); TzxLoader.Level = !TzxLoader.Level; if (Pause != 0) sink.Add(new TapePulse(TzxLoader.Level, Pause * 3500, endsWithEdge: false)); }
    public ushort PilotPulseCount => Cnt;
}
/// <summary>Internal Speedlock data representation with explicit loader timings.</summary>
sealed class SpeedlockData : ITzxBlock
{
    readonly ushort P, S, B0, B1, Cnt, Pause; readonly uint N; readonly byte[] Data;
    public SpeedlockData(BinaryReader br) { P = br.U16(); S = br.U16(); B0 = br.U16(); B1 = br.U16(); Cnt = br.U16(); Pause = br.U16(); N = br.U24(); Data = br.ReadBytes((int)N); }
    static void Half(List<TapePulse> s, int t) { s.Add(new TapePulse(TzxLoader.Level, t)); TzxLoader.Level = !TzxLoader.Level; }
    static void HalfData(List<TapePulse> s, int t, bool isData, bool isLong)
    {
        s.Add(new TapePulse(TzxLoader.Level, t, isData: isData, isLong: isLong));
        TzxLoader.Level = !TzxLoader.Level;
    }
    public void AppendPulses(List<TapePulse> sink)
    {
        for (int i = 0; i < Cnt; i++)
        {
            sink.Add(new TapePulse(TzxLoader.Level, P));
            TzxLoader.Level = !TzxLoader.Level;
        }
        sink.Add(new TapePulse(TzxLoader.Level, S));
        TzxLoader.Level = !TzxLoader.Level;
        sink.Add(new TapePulse(TzxLoader.Level, S));
        TzxLoader.Level = !TzxLoader.Level;
        foreach (var b in Data)
        {
            bool hasDistinct = B0 != B1;
            int longPulse = hasDistinct ? Math.Max(B0, B1) : 0;
            for (int bit = 7; bit >= 0; bit--)
            {
                int t = ((b >> bit) & 1) != 0 ? B1 : B0;
                bool isLong = hasDistinct && t == longPulse;
                HalfData(sink, t, hasDistinct, isLong);
                HalfData(sink, t, hasDistinct, isLong);
            }
        }
        if (Pause != 0) sink.Add(new TapePulse(TzxLoader.Level, Pause * 3500, endsWithEdge: false));
    }
    public int DataLen => (int)N;
    public ushort DataPulse0 => B0;
    public ushort DataPulse1 => B1;
}
/// <summary>CSW run-length pulse data converted from sample counts into Spectrum T-states.</summary>
sealed class CswBlock : ITzxBlock
{
    private const float TimingScale = 2.5f;
    private readonly int _cpuHz;
    private readonly int _sampleRate;
    private readonly byte _compression;
    private readonly bool _initialLevel;
    private readonly byte[] _data;
    private readonly ushort _pauseMs;
    private readonly uint _pulseCount;

    public CswBlock(int cpuHz, int sampleRate, byte compression, bool initialLevel, byte[] data, ushort pauseMs = 0, uint pulseCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cpuHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _cpuHz = cpuHz;
        _sampleRate = sampleRate;
        _compression = compression;
        _initialLevel = initialLevel;
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _pauseMs = pauseMs;
        _pulseCount = pulseCount;
    }

    public int DataLength => _data.Length;
    public uint PulseCount => _pulseCount;
    public int SampleRate => _sampleRate;
    public byte Compression => _compression;
    public void AppendPulses(List<TapePulse> sink)
    {
        byte[] rleData = DecodeCswData(_data, _compression);

        TzxLoader.Level = _initialLevel;
        bool level = TzxLoader.Level;

        long acc = 0;
        long scaledCpuHz = (long)(_cpuHz * TimingScale);
        for (int i = 0; i < rleData.Length; i++)
        {
            uint count = rleData[i];
            if (count == 0)
            {
                if (i + 4 >= rleData.Length)
                {
                    break;
                }

                count = (uint)(rleData[i + 1]
                    | (rleData[i + 2] << 8)
                    | (rleData[i + 3] << 16)
                    | (rleData[i + 4] << 24));
                i += 4;
            }

            if (count == 0)
            {
                continue;
            }

            acc += (long)count * scaledCpuHz;
            int tstates = (int)(acc / _sampleRate);
            acc %= _sampleRate;
            if (tstates <= 0)
            {
                tstates = 1;
            }

            sink.Add(new TapePulse(level, tstates));
            level = !level;
        }

        TzxLoader.Level = level;
        if (_pauseMs != 0)
        {
            sink.Add(new TapePulse(TzxLoader.Level, _pauseMs * 3500, endsWithEdge: false));
        }
    }
    private static byte[] DecodeCswData(byte[] data, byte compression)
    {
        if (compression is 0 or 1)
        {
            return data;
        }

        if (compression != 2)
        {
            throw new InvalidDataException($"Unsupported CSW compression {compression}.");
        }

        try
        {
            return DecompressZlib(data);
        }
        catch (InvalidDataException)
        {
            return DecompressDeflate(data);
        }
    }
    private static byte[] DecompressZlib(byte[] data)
    {
        using var input = new MemoryStream(data, false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }
    private static byte[] DecompressDeflate(byte[] data)
    {
        using var input = new MemoryStream(data, false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
}
/// <summary>TZX 0x15 sampled digital level stream, coalesced into constant-level runs.</summary>
sealed class DirectRecording : ITzxBlock
{
    readonly ushort _tstatesPerSample;
    readonly ushort _pause;
    readonly byte _lastByteBits;
    readonly byte[] _data;

    public DirectRecording(BinaryReader br)
    {
        _tstatesPerSample = br.U16();
        _pause = br.U16();
        _lastByteBits = br.ReadByte();
        uint length = br.U24();
        _data = br.ReadBytes((int)length);
    }

    public int DataLength => _data.Length;
    public ushort TstatesPerSample => _tstatesPerSample;
    public void AppendPulses(List<TapePulse> sink)
    {
        if (_data.Length == 0 || _tstatesPerSample == 0)
        {
            if (_pause != 0)
            {
                sink.Add(new TapePulse(TzxLoader.Level, _pause * 3500, endsWithEdge: false));
            }

            return;
        }

        bool haveRun = false;
        bool runLevel = false;
        int runSamples = 0;
        for (int i = 0; i < _data.Length; i++)
        {
            int bits = i == _data.Length - 1 ? UsedBits(_lastByteBits) : 8;
            byte value = _data[i];
            for (int bit = 7; bit >= 8 - bits; bit--)
            {
                bool level = ((value >> bit) & 1) != 0;
                if (!haveRun)
                {
                    haveRun = true;
                    runLevel = level;
                    runSamples = 1;
                    continue;
                }

                if (level == runLevel)
                {
                    runSamples++;
                    continue;
                }

                AddRun(sink, runLevel, runSamples, endsWithEdge: true);
                runLevel = level;
                runSamples = 1;
            }
        }

        if (haveRun)
        {
            AddRun(sink, runLevel, runSamples, endsWithEdge: false);
            TzxLoader.Level = runLevel;
        }

        if (_pause != 0)
        {
            sink.Add(new TapePulse(TzxLoader.Level, _pause * 3500, endsWithEdge: false));
        }
    }
    private void AddRun(List<TapePulse> sink, bool level, int samples, bool endsWithEdge)
    {
        if (samples <= 0)
        {
            return;
        }

        int tstates = checked(samples * _tstatesPerSample);
        sink.Add(new TapePulse(level, tstates, endsWithEdge: endsWithEdge));
    }
    private static int UsedBits(byte value) => value is 0 or > 8 ? 8 : value;
}
/// <summary>TZX 0x18 CSW recording wrapper.</summary>
sealed class TzxCswRecording : ITzxBlock
{
    readonly uint _blockLength;
    readonly CswBlock _csw;

    public TzxCswRecording(BinaryReader br, int cpuHz)
    {
        _blockLength = br.U32();
        if (_blockLength < 10)
        {
            throw new InvalidDataException("Invalid TZX CSW block length.");
        }

        long payloadStart = br.BaseStream.Position;
        ushort pause = br.U16();
        int sampleRate = (int)br.U24();
        byte compression = br.ReadByte();
        uint pulseCount = br.U32();
        int dataLength = checked((int)(_blockLength - 10));
        byte[] data = br.ReadBytes(dataLength);
        br.BaseStream.Position = payloadStart + _blockLength;
        _csw = new CswBlock(cpuHz, sampleRate, compression, TzxLoader.Level, data, pause, pulseCount);
    }

    public uint BlockLength => _blockLength;
    public int DataLength => _csw.DataLength;
    public uint PulseCount => _csw.PulseCount;
    public void AppendPulses(List<TapePulse> sink) => _csw.AppendPulses(sink);
}
/// <summary>TZX 0x20 pause; a zero duration is a stop-tape marker.</summary>
sealed class PauseBlk(BinaryReader br) : ITzxBlock
{
    readonly ushort Ms = br.U16();

        public void AppendPulses(List<TapePulse> sink) { if (Ms == 0) return; sink.Add(new TapePulse(TzxLoader.Level, Ms * 3500, endsWithEdge: false)); }
    public int PauseMs => Ms;
}
/// <summary>TZX 0x21 group label used by tape browsers.</summary>
sealed class GroupStart : ITzxBlock { public readonly string Name; public GroupStart(BinaryReader br) { int l = br.ReadByte(); Name = Encoding.ASCII.GetString(br.ReadBytes(l)); } public void AppendPulses(List<TapePulse> sink) { } }
/// <summary>TZX 0x22 group terminator.</summary>
sealed class GroupEnd : ITzxBlock { public void AppendPulses(List<TapePulse> sink) { } }
/// <summary>TZX 0x28 interactive selection table.</summary>
sealed class SelectBlock : ITzxBlock
{
    public readonly List<(short Offset, string Description)> Selections = [];

    public SelectBlock(BinaryReader br)
    {
        ushort length = br.U16();
        long end = br.BaseStream.Position + length;
        if (br.BaseStream.Position < end)
        {
            int count = br.ReadByte();
            for (int i = 0; i < count && br.BaseStream.Position < end; i++)
            {
                short offset = (short)br.U16();
                int textLength = br.ReadByte();
                string description = Encoding.ASCII.GetString(br.ReadBytes(Math.Min(textLength, (int)(end - br.BaseStream.Position))));
                Selections.Add((offset, description));
            }
        }

        br.BaseStream.Position = end;
    }
    public void AppendPulses(List<TapePulse> sink) { }
}
/// <summary>TZX 0x2A conditional stop marker, active only for 48K-class playback.</summary>
sealed class StopIf48K : ITzxBlock
{
    public readonly uint Length;

    public StopIf48K(BinaryReader br)
    {
        Length = br.U32();
        if (Length > 0)
        {
            _ = br.ReadBytes((int)Length);
        }
    }
    public void AppendPulses(List<TapePulse> sink) { /* UI can react on block boundary */ }
}
/// <summary>TZX 0x2B explicit signal-level change.</summary>
sealed class SetLevel : ITzxBlock
{
    readonly bool L;
    public SetLevel(BinaryReader br)
    {
        uint length = br.U32();
        L = length > 0 && br.ReadByte() != 0;
        if (length > 1)
        {
            _ = br.ReadBytes((int)length - 1);
        }
    }
    public void AppendPulses(List<TapePulse> sink) { TzxLoader.Level = L; }
}
// The following blocks describe a tape or embed auxiliary data. They remain in
// Blocks for the browser but do not contribute signal pulses.
/// <summary>TZX 0x30 text description.</summary>
sealed class TextDescription : ITzxBlock
{
    public readonly string Text;
    public TextDescription(BinaryReader br) { int length = br.ReadByte(); Text = Encoding.ASCII.GetString(br.ReadBytes(length)); }
    public void AppendPulses(List<TapePulse> sink) { }
}
/// <summary>TZX 0x31 timed display message.</summary>
sealed class MessageBlock : ITzxBlock
{
    public readonly byte DisplaySeconds;
    public readonly string Message;
    public MessageBlock(BinaryReader br) { DisplaySeconds = br.ReadByte(); int length = br.ReadByte(); Message = Encoding.ASCII.GetString(br.ReadBytes(length)); }
    public void AppendPulses(List<TapePulse> sink) { }
}
/// <summary>TZX 0x32 archive metadata.</summary>
sealed class ArchiveInfo : ITzxBlock
{
    public readonly byte[] Data;
    public ArchiveInfo(BinaryReader br) { ushort length = br.U16(); Data = br.ReadBytes(length); }
    public void AppendPulses(List<TapePulse> sink) { }
}
/// <summary>TZX 0x33 hardware compatibility declarations.</summary>
sealed class HardwareTypeInfo : ITzxBlock
{
    public readonly byte[] Data;
    public HardwareTypeInfo(BinaryReader br) { int count = br.ReadByte(); Data = br.ReadBytes(count * 3); }
    public void AppendPulses(List<TapePulse> sink) { }
}
/// <summary>Deprecated TZX 0x34 emulation metadata, retained for stream alignment.</summary>
sealed class DeprecatedEmulationInfo : ITzxBlock
{
    public DeprecatedEmulationInfo(BinaryReader br) { _ = br.ReadBytes(8); }
    public void AppendPulses(List<TapePulse> sink) { }
}
/// <summary>TZX 0x35 application-defined metadata.</summary>
sealed class CustomInfoBlock : ITzxBlock
{
    public readonly string Identifier;
    public readonly byte[] Data;
    public CustomInfoBlock(BinaryReader br)
    {
        Identifier = Encoding.ASCII.GetString(br.ReadBytes(16)).TrimEnd();
        uint length = br.U32();
        Data = br.ReadBytes((int)length);
    }
    public void AppendPulses(List<TapePulse> sink) { }
}
/// <summary>TZX 0x5A concatenation marker.</summary>
sealed class GlueBlock : ITzxBlock
{
    public GlueBlock(BinaryReader br) { _ = br.ReadBytes(9); }
    public void AppendPulses(List<TapePulse> sink) { }
}
/// <summary>TZX 0x40 embedded snapshot payload.</summary>
sealed class SnapshotBlock : ITzxBlock
{
    public readonly byte SnapshotType;
    public readonly byte[] Data;
    public SnapshotBlock(BinaryReader br) { SnapshotType = br.ReadByte(); uint length = br.U24(); Data = br.ReadBytes((int)length); }
    public void AppendPulses(List<TapePulse> sink) { }
}
/// <summary>Deprecated C64 recording block, parsed and preserved but not played.</summary>
sealed class DeprecatedC64Block : ITzxBlock
{
    public readonly byte Id;
    public readonly byte[] Data;
    public DeprecatedC64Block(BinaryReader br, byte id)
    {
        Id = id;
        uint length = br.U32();
        if (length < 4)
        {
            throw new InvalidDataException($"Invalid deprecated C64 TZX block {id:X2} length.");
        }

        Data = br.ReadBytes(checked((int)length - 4));
    }
    public void AppendPulses(List<TapePulse> sink) { }
}
/// <summary>Length-bounded unsupported block retained so later blocks remain aligned.</summary>
sealed class UnknownTzxBlock : ITzxBlock
{
    public readonly byte Id;
    public readonly byte[] Data;
    public UnknownTzxBlock(BinaryReader br, byte id, long blockEnd)
    {
        Id = id;
        long remaining = Math.Max(0, blockEnd - br.BaseStream.Position);
        Data = br.ReadBytes((int)remaining);
    }
    public void AppendPulses(List<TapePulse> sink) { }
}
/// <summary>Legacy metadata skipper used by compatibility parsing paths.</summary>
sealed class SkipInfo : ITzxBlock { public SkipInfo(BinaryReader br, byte id) { uint len = id switch { 0x30 => (uint)br.ReadByte(), 0x31 => (uint)br.ReadByte(), 0x32 => br.U16(), 0x33 => (uint)br.ReadByte() * 3u, 0x35 => br.U24(), 0x5A => br.U24(), _ => 0 }; _ = br.ReadBytes((int)len); } public void AppendPulses(List<TapePulse> sink) { } }
/// <summary>Maps TZX block IDs to parsers without reading beyond the caller-supplied boundary.</summary>
static class Factory
{
    public static ITzxBlock ReadOne(BinaryReader br, byte id, int cpuHz, long blockEnd)
    => id switch
    {
        0x10 => new StdData(br),
        0x11 => new Turbo(br),
        0x12 => new Tone(br),
        0x13 => new PulseSeq(br),
        0x14 => new PureData(br),
        0x15 => new DirectRecording(br),
        0x16 => new DeprecatedC64Block(br, id),
        0x17 => new DeprecatedC64Block(br, id),
        0x18 => new TzxCswRecording(br, cpuHz),
        0x19 => new GenData(br),
        0x20 => new PauseBlk(br),
        0x21 => new GroupStart(br),
        0x22 => new GroupEnd(),
        0x23 => new JumpBlock(br),
        0x24 => new LoopStart(br),
        0x25 => new LoopEnd(),
        0x26 => new CallSequenceBlock(br),
        0x27 => new ReturnBlock(),
        0x28 => new SelectBlock(br),
        0x2A => new StopIf48K(br),
        0x2B => new SetLevel(br),
        0x30 => new TextDescription(br),
        0x31 => new MessageBlock(br),
        0x32 => new ArchiveInfo(br),
        0x33 => new HardwareTypeInfo(br),
        0x34 => new DeprecatedEmulationInfo(br),
        0x35 => new CustomInfoBlock(br),
        0x40 => new SnapshotBlock(br),
        0x5A => new GlueBlock(br),
        _ => new UnknownTzxBlock(br, id, blockEnd),
    };
}
/// <summary>TZX 0x19 generalized-data symbol alphabets and streams.</summary>
sealed class GenData : ITzxBlock
{
    readonly ushort _pause;
    readonly PilotRun[] _pilotRuns;
    readonly SymbolDef[] _pilotSymbols;
    readonly uint _dataSymbolCount;
    readonly int _dataBitsPerSymbol;
    readonly SymbolDef[] _dataSymbols;
    readonly byte[] _dataStream;

    public GenData(BinaryReader br)
    {
        uint blockLength = br.U32();
        long end = br.BaseStream.Position + blockLength;
        _pause = br.U16();
        uint pilotRunCount = br.U32();
        byte maxPilotPulses = br.ReadByte();
        int pilotAlphabetCount = ExpandAlphabetCount(br.ReadByte());
        _dataSymbolCount = br.U32();
        byte maxDataPulses = br.ReadByte();
        int dataAlphabetCount = ExpandAlphabetCount(br.ReadByte());

        if (pilotRunCount > 0)
        {
            _pilotSymbols = ReadSymbols(br, pilotAlphabetCount, maxPilotPulses);
            _pilotRuns = new PilotRun[pilotRunCount];
            for (int i = 0; i < _pilotRuns.Length; i++)
            {
                _pilotRuns[i] = new PilotRun(br.ReadByte(), br.U16());
            }
        }
        else
        {
            _pilotSymbols = [];
            _pilotRuns = [];
        }

        if (_dataSymbolCount > 0)
        {
            _dataSymbols = ReadSymbols(br, dataAlphabetCount, maxDataPulses);
            _dataBitsPerSymbol = BitsRequired(dataAlphabetCount);
            long dataBits = checked((long)_dataSymbolCount * _dataBitsPerSymbol);
            _dataStream = br.ReadBytes(checked((int)((dataBits + 7) / 8)));
        }
        else
        {
            _dataSymbols = [];
            _dataStream = [];
            _dataBitsPerSymbol = 0;
        }

        br.BaseStream.Position = end;
    }

    public int DataLength => _dataStream.Length;
    public int PilotSymbolCount => _pilotRuns.Length;
    public void AppendPulses(List<TapePulse> sink)
    {
        foreach (PilotRun run in _pilotRuns)
        {
            if (run.Symbol >= _pilotSymbols.Length)
            {
                continue;
            }

            for (int i = 0; i < run.Repetitions; i++)
            {
                AppendSymbol(sink, _pilotSymbols[run.Symbol], isData: false);
            }
        }

        int bitOffset = 0;
        for (uint i = 0; i < _dataSymbolCount; i++)
        {
            int symbol = ReadBits(_dataStream, bitOffset, _dataBitsPerSymbol);
            bitOffset += _dataBitsPerSymbol;
            if (symbol < _dataSymbols.Length)
            {
                AppendSymbol(sink, _dataSymbols[symbol], isData: true);
            }
        }

        if (_pause != 0)
        {
            sink.Add(new TapePulse(TzxLoader.Level, _pause * 3500, endsWithEdge: false));
        }
    }
    private static SymbolDef[] ReadSymbols(BinaryReader br, int count, int maxPulses)
    {
        var symbols = new SymbolDef[count];
        for (int i = 0; i < count; i++)
        {
            byte flags = br.ReadByte();
            var pulses = new ushort[maxPulses];
            for (int p = 0; p < pulses.Length; p++)
            {
                pulses[p] = br.U16();
            }

            symbols[i] = new SymbolDef(flags, pulses);
        }

        return symbols;
    }
    private static void AppendSymbol(List<TapePulse> sink, SymbolDef symbol, bool isData)
    {
        ApplyStartingPolarity(symbol.Flags);
        foreach (ushort pulse in symbol.Pulses)
        {
            if (pulse == 0)
            {
                break;
            }

            sink.Add(new TapePulse(TzxLoader.Level, pulse, isData: isData));
            TzxLoader.Level = !TzxLoader.Level;
        }
    }
    private static void ApplyStartingPolarity(byte flags)
    {
        switch (flags & 0x03)
        {
            case 0:
                break;
            case 1:
                TzxLoader.Level = !TzxLoader.Level;
                break;
            case 2:
                TzxLoader.Level = false;
                break;
            case 3:
                TzxLoader.Level = true;
                break;
        }
    }
    private static int ReadBits(byte[] data, int offset, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            int bitIndex = offset + i;
            int source = bitIndex >> 3;
            value <<= 1;
            if (source < data.Length)
            {
                value |= (data[source] >> (7 - (bitIndex & 7))) & 1;
            }
        }

        return value;
    }
    private static int ExpandAlphabetCount(byte value) => value == 0 ? 256 : value;
    private static int BitsRequired(int symbolCount)
    {
        int bits = 0;
        int capacity = 1;
        while (capacity < symbolCount)
        {
            bits++;
            capacity <<= 1;
        }

        return bits;
    }
    private readonly struct SymbolDef(byte flags, ushort[] pulses)
    {
        public byte Flags { get; } = flags;
        public ushort[] Pulses { get; } = pulses;
    }
    private readonly struct PilotRun(byte symbol, ushort repetitions)
    {
        public byte Symbol { get; } = symbol;
        public ushort Repetitions { get; } = repetitions;
    }
}
/// <summary>TZX 0x24 loop start and repeat count.</summary>
sealed class LoopStart(BinaryReader br) : ITzxBlock { public readonly ushort Count = br.U16(); public void AppendPulses(List<TapePulse> sink) { } }
/// <summary>TZX 0x25 loop terminator.</summary>
sealed class LoopEnd : ITzxBlock { public void AppendPulses(List<TapePulse> sink) { } }
/// <summary>TZX 0x23 relative block jump.</summary>
sealed class JumpBlock(BinaryReader br) : ITzxBlock { public readonly short Offset = (short)br.U16(); public void AppendPulses(List<TapePulse> sink) { } }
/// <summary>TZX 0x26 sequence of relative block calls.</summary>
sealed class CallSequenceBlock : ITzxBlock
{
    public readonly short[] Offsets;
    public CallSequenceBlock(BinaryReader br)
    {
        ushort count = br.U16();
        Offsets = new short[count];
        for (int i = 0; i < Offsets.Length; i++)
        {
            Offsets[i] = (short)br.U16();
        }
    }
    public void AppendPulses(List<TapePulse> sink) { }
}
/// <summary>TZX 0x27 return from a block-call sequence.</summary>
sealed class ReturnBlock : ITzxBlock { public void AppendPulses(List<TapePulse> sink) { } }
/// <summary>Reason playback stopped without an explicit user request.</summary>
public enum TapeStopReason
{
    EndOfTape,
    PauseZero,
    StopIf48K
}
/// <summary>Decoded standard block exposed to the ROM flash-load trap.</summary>
public readonly struct TapeStandardBlock(int index, byte[] data)
    {
    private readonly byte[] _data = data;

        public int Index { get; } = index;
        public ReadOnlySpan<byte> Data => _data;
    public byte Flag => _data.Length > 0 ? _data[0] : (byte)0;
    public ReadOnlySpan<byte> Payload => _data.Length > 2 ? _data.AsSpan(1, _data.Length - 2) : ReadOnlySpan<byte>.Empty;
    public bool HasValidChecksum
    {
        get
        {
            if (_data.Length < 2)
            {
                return false;
            }

            byte acc = 0;
            for (int i = 0; i < _data.Length - 1; i++)
            {
                acc ^= _data[i];
            }

            return acc == _data[^1];
        }
    }
}
/// <summary>
/// Parses TAP, TZX and CSW images into a block-indexed pulse stream and drives EAR at CPU T-state resolution.
/// </summary>
/// <remarks>
/// The pulse list is shared by ordinary playback, the tape browser and edge
/// acceleration. Block-to-pulse indices must therefore remain stable when
/// metadata and control-flow blocks produce no pulses.
/// </remarks>
public sealed class TzxLoader : ITapePlayback, ITapeEdgeSource
{
    readonly IEarInputSink _ear;
    readonly int _cpuHz;
    readonly bool _stopOn48k;
    readonly List<TapePulse> _pulses = new(1 << 20);
    readonly Dictionary<int, TapeStopReason> _stopMarkers = [];
    // Decode-time signal level shared by block AppendPulses implementations. Load
    // entry points reset it before constructing a new pulse stream.
    public static bool Level { get; set; } = false;

    int _pulseIndex; int _pulseRemaining; public bool _playing; int _edgeSkipTstates;
    bool _nextEdgeAccelerated;
    bool _edgeSeen;
    bool _lastEdgeAccelerated;
    int _lastEdgePulseLength;
    bool _lastEdgeIsData;
    bool _lastEdgeIsLong;
    int _lastEdgePulseIndex = -1;
    int _currentBlockIndex = -1;
    // Cached same-level run ahead of the current pulse. PeekNextEdgeDelta is on
    // the emulator's per-tstate scheduling path, so it must not rescan the pulse
    // list on every call; the run only changes when the current pulse index moves
    // or the pulse list is rebuilt.
    int _edgeRunStartPulse = -1;
    int _edgeRunExtraTstates;
    bool _edgeRunEndsWithEdge;
    long[]? _pulseTstatePrefix;
    long[]? _blockStartTstates;
    long[]? _blockDurations;
    readonly List<ITzxBlock> _blocks = []; readonly List<int> _blockStartPulse = [];

    public IReadOnlyList<ITzxBlock> Blocks => _blocks;
    public event EventHandler<int>? BlockIndexChanged; private int _lastBlock = -1;
    public event EventHandler<TapeStopReason>? PlaybackStopped;
    public bool IsPlaying => _playing;
    public bool EdgeSeen => _edgeSeen;

    public int CurrentBlockIndex => _pulses.Count == 0 ? -1 : _currentBlockIndex;
    public int CurrentPulseIndex => _pulseIndex;
    public int CurrentPulseOffset => CurrentBlockIndex < 0 ? 0 : _pulseIndex - _blockStartPulse[CurrentBlockIndex];
    public int CurrentBlockPulseCount => (CurrentBlockIndex < 0 || CurrentBlockIndex + 1 >= _blockStartPulse.Count) ? (_pulses.Count - _blockStartPulse[Math.Max(0, CurrentBlockIndex)]) : (_blockStartPulse[CurrentBlockIndex + 1] - _blockStartPulse[CurrentBlockIndex]);
    public double CurrentBlockProgress { get { int i = CurrentBlockIndex; if (i < 0) return 0; int start = _blockStartPulse[i]; int end = (i + 1 < _blockStartPulse.Count) ? _blockStartPulse[i + 1] : _pulses.Count; int pos = Math.Clamp(_pulseIndex, start, end); int len = end - start; return len > 0 ? (double)(pos - start) / len : 1.0; } }
    public double CurrentBlockElapsedSeconds
    {
        get
        {
            int i = CurrentBlockIndex;
            if (i < 0 || _pulseTstatePrefix == null || _blockStartTstates == null)
            {
                return 0;
            }

            if (_pulseIndex < 0 || _pulseIndex >= _pulses.Count)
            {
                return 0;
            }

            long start = _blockStartTstates[i];
            long current = _pulseTstatePrefix[_pulseIndex] + (_pulses[_pulseIndex].TStates - _pulseRemaining);
            long elapsed = current - start;
            if (elapsed < 0)
            {
                elapsed = 0;
            }

            return elapsed / (double)_cpuHz;
        }
    }
    public double CurrentTapeElapsedSeconds
    {
        get
        {
            if (_pulseTstatePrefix == null || _pulseIndex < 0 || _pulseIndex >= _pulses.Count)
            {
                return 0;
            }

            long current = _pulseTstatePrefix[_pulseIndex] + (_pulses[_pulseIndex].TStates - _pulseRemaining);
            if (current < 0)
            {
                current = 0;
            }

            return current / (double)_cpuHz;
        }
    }
    public double CurrentBlockDurationSeconds
    {
        get
        {
            int i = CurrentBlockIndex;
            if (i < 0 || _blockDurations == null)
            {
                return 0;
            }

            long duration = _blockDurations[i];
            return duration > 0 ? duration / (double)_cpuHz : 0;
        }
    }
    public TzxLoader(IEarInputSink ear) : this(ear, 3500000)
    {
    }

    public TzxLoader(IEarInputSink ear, int cpuHz, bool stopOn48k = false)
    {
        _ear = ear;
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cpuHz);
            _cpuHz = cpuHz;
        _stopOn48k = stopOn48k;
    }
    public void StartPlaying()
    {
        if (_pulses.Count == 0) return;
        _pulseIndex = 0;
        _pulseRemaining = _pulses[0].TStates;
        _edgeSkipTstates = 0;
        _nextEdgeAccelerated = false;
        _edgeSeen = false;
        _lastEdgeAccelerated = false;
        _lastEdgePulseIndex = -1;
        _currentBlockIndex = 0;
        _lastBlock = 0;
        _ear.SetEarLevel(_pulses[0].Level);
        _playing = true;
        StopAtMarkerIfNeeded(_pulseIndex);
    }
    public void Play()
    {
        if (_pulses.Count == 0) return;
        if (_pulseIndex < 0 || _pulseIndex >= _pulses.Count)
        {
            _pulseIndex = 0;
            _pulseRemaining = _pulses[0].TStates;
            _ear.SetEarLevel(_pulses[0].Level);
            _currentBlockIndex = 0;
            _lastBlock = 0;
        }
        else
        {
            SetCurrentBlockIndex(FindBlockIndexForPulse(_pulseIndex), raiseEvent: false);
        }

        _playing = true;
        _edgeSkipTstates = 0;
        _nextEdgeAccelerated = false;
        _edgeSeen = false;
        _lastEdgeAccelerated = false;
        StopAtMarkerIfNeeded(_pulseIndex);
    }
    public void Stop()
    {
        _playing = false;
        _edgeSeen = false;
        _ear.SetEarLevel(false);
    }
    public void Reset()
    {
        _playing = false;
        if (_pulses.Count == 0) return;
        _pulseIndex = 0;
        _pulseRemaining = _pulses[0].TStates;
        _edgeSkipTstates = 0;
        _nextEdgeAccelerated = false;
        _edgeSeen = false;
        _lastEdgeAccelerated = false;
        _lastEdgePulseIndex = -1;
        _currentBlockIndex = 0;
        _lastBlock = 0;
        _ear.SetEarLevel(false);
    }
    public void Step(int tStates)
    {
        if (!_playing || _pulses.Count == 0 || tStates <= 0) return; int remain = tStates;
        if (_edgeSkipTstates > 0)
        {
            int skip = Math.Min(remain, _edgeSkipTstates);
            _edgeSkipTstates -= skip;
            remain -= skip;
            if (remain <= 0)
            {
                return;
            }
        }
        while (remain > 0 && _pulseIndex < _pulses.Count)
        {
            int take = Math.Min(remain, _pulseRemaining);
            _pulseRemaining -= take;
            remain -= take;
            if (_pulseRemaining > 0) break;
            int next = _pulseIndex + 1;
            if (next >= _pulses.Count)
            {
                CompleteFinalPulseEdge();
                StopPlayback(TapeStopReason.EndOfTape, driveEarLow: false, clearEdgeSeen: false);
                break;
            }

            if (StopAtMarkerIfNeeded(next))
            {
                break;
            }

            var prevPulse = _pulses[_pulseIndex];
            _lastEdgePulseLength = prevPulse.TStates;
            _lastEdgeIsData = prevPulse.IsData;
            _lastEdgeIsLong = prevPulse.IsLong;
            _lastEdgeAccelerated = _nextEdgeAccelerated;
            _nextEdgeAccelerated = false;
            _lastEdgePulseIndex = _pulseIndex;
            _edgeSeen = true;

            _pulseIndex = next;
            var p = _pulses[_pulseIndex];
            _pulseRemaining = p.TStates;
            _ear.SetEarLevel(p.Level);
            UpdateCurrentBlockIndexAfterPulseAdvance();
        }
    }
    public int PeekNextEdgeDelta()
    {
        if (!_playing || _pulses.Count == 0 || _pulseIndex < 0 || _pulseIndex >= _pulses.Count || _pulseRemaining <= 0)
        {
            return 0;
        }

        if (_edgeRunStartPulse != _pulseIndex)
        {
            ComputeEdgeRun();
        }

        return _edgeRunEndsWithEdge ? _pulseRemaining + _edgeRunExtraTstates : 0;
    }

    /// <summary>
    /// Scans forward from the current pulse over any same-level neighbours and
    /// records how many extra tstates they contribute before the next level
    /// transition. Steps inside the current pulse only shrink _pulseRemaining, so
    /// the cached remainder stays valid until the pulse index itself moves.
    /// </summary>
    private void ComputeEdgeRun()
    {
        _edgeRunStartPulse = _pulseIndex;
        _edgeRunExtraTstates = 0;
        bool currentLevel = _pulses[_pulseIndex].Level;
        int next = _pulseIndex + 1;
        while (next < _pulses.Count)
        {
            if (_pulses[next].Level != currentLevel)
            {
                _edgeRunEndsWithEdge = true;
                return;
            }

            _edgeRunExtraTstates += _pulses[next].TStates;
            next++;
        }

        _edgeRunEndsWithEdge = _pulses[^1].EndsWithEdge;
    }
    public int AdvanceToNextEdge(bool skipTime)
    {
        if (!_playing || _pulses.Count == 0 || _pulseRemaining <= 0)
        {
            return 0;
        }

        int delta = _pulseRemaining;
        if (skipTime)
        {
            // Edge-loading uses time skipping; don't defer playback by adding to edgeSkipTstates.
        }
        _pulseRemaining = 0;
        int next = _pulseIndex + 1;
        bool currentLevel = _pulses[_pulseIndex].Level;
        int edgePulseIndex = _pulseIndex;
        bool crossedNonTransitionBoundary = false;

        while (true)
        {
            if (next >= _pulses.Count)
            {
                CompleteFinalPulseEdge();
                StopPlayback(TapeStopReason.EndOfTape, driveEarLow: false, clearEdgeSeen: false);
                return delta;
            }

            if (StopAtMarkerIfNeeded(next))
            {
                _nextEdgeAccelerated = false;
                return delta;
            }

            var p = _pulses[next];
            if (p.Level != currentLevel)
            {
                var prevPulse = _pulses[edgePulseIndex];
                _lastEdgePulseLength = delta;
                _lastEdgeIsData = !crossedNonTransitionBoundary && prevPulse.IsData;
                _lastEdgeIsLong = !crossedNonTransitionBoundary && prevPulse.IsLong;
                _lastEdgeAccelerated = _nextEdgeAccelerated;
                _nextEdgeAccelerated = false;
                _lastEdgePulseIndex = edgePulseIndex;

                _pulseIndex = next;
                _pulseRemaining = p.TStates;
                _ear.SetEarLevel(p.Level);
                UpdateCurrentBlockIndexAfterPulseAdvance();

                return delta;
            }

            crossedNonTransitionBoundary = true;
            _pulseIndex = next;
            edgePulseIndex = next;
            delta += p.TStates;
            next++;
            UpdateCurrentBlockIndexAfterPulseAdvance();
        }
    }
    private void CompleteFinalPulseEdge()
    {
        if (_pulseIndex < 0 || _pulseIndex >= _pulses.Count)
        {
            return;
        }

        var pulse = _pulses[_pulseIndex];
        if (!pulse.EndsWithEdge)
        {
            return;
        }

        _lastEdgePulseLength = pulse.TStates;
        _lastEdgeIsData = pulse.IsData;
        _lastEdgeIsLong = pulse.IsLong;
        _lastEdgeAccelerated = _nextEdgeAccelerated;
        _nextEdgeAccelerated = false;
        _lastEdgePulseIndex = _pulseIndex;
        _edgeSeen = true;
        _ear.SetEarLevel(!pulse.Level);
    }
    public void ClearEdgeSeen()
    {
        _edgeSeen = false;
    }
    public bool TryGetDataPulseTimings(out int shortPulse, out int longPulse)
    {
        shortPulse = 0;
        longPulse = 0;
        if (_blocks.Count == 0)
        {
            return false;
        }

        int index = CurrentBlockIndex;
        if (index < 0 || index >= _blocks.Count)
        {
            return false;
        }

        ITzxBlock blk = _blocks[index];
        switch (blk)
        {
            case StdData:
            case TapBlock:
                shortPulse = 855;
                longPulse = 1710;
                return true;
            case Turbo turbo:
                shortPulse = Math.Min(turbo.DataPulse0, turbo.DataPulse1);
                longPulse = Math.Max(turbo.DataPulse0, turbo.DataPulse1);
                return shortPulse != longPulse;
            case PureData pure:
                shortPulse = Math.Min(pure.DataPulse0, pure.DataPulse1);
                longPulse = Math.Max(pure.DataPulse0, pure.DataPulse1);
                return shortPulse != longPulse;
            case SpeedlockData speedlock:
                shortPulse = Math.Min(speedlock.DataPulse0, speedlock.DataPulse1);
                longPulse = Math.Max(speedlock.DataPulse0, speedlock.DataPulse1);
                return shortPulse != longPulse;
            default:
                return false;
        }
    }
    public bool TryGetCurrentPulseInfo(out int tstates, out bool isData, out bool isLong)
    {
        tstates = 0;
        isData = false;
        isLong = false;
        if (_pulses.Count == 0 || _pulseIndex < 0 || _pulseIndex >= _pulses.Count)
        {
            return false;
        }

        TapePulse pulse = _pulses[_pulseIndex];
        tstates = pulse.TStates;
        isData = pulse.IsData;
        isLong = pulse.IsLong;
        return true;
    }
    public bool TryGetCurrentAccelerationFlags(out TapeAccelerationPulseFlags flags)
    {
        flags = TapeAccelerationPulseFlags.None;
        if (_pulses.Count == 0 || _pulseIndex < 0 || _pulseIndex >= _pulses.Count)
        {
            return false;
        }

        flags = _pulses[_pulseIndex].AccelerationFlags;
        return true;
    }
    public bool TryGetSemanticReadState(out TapeSemanticReadState state)
    {
        state = default;
        if (!_playing || _pulses.Count == 0 || _pulseIndex < 0 || _pulseIndex >= _pulses.Count)
        {
            return false;
        }

        TapePulse pulse = _pulses[_pulseIndex];
        state = new TapeSemanticReadState(
            _pulseIndex,
            pulse.AccelerationFlags,
            pulse.Level,
            pulse.AccelerationFlags == TapeAccelerationPulseFlags.None ? 0 : PeekNextSemanticEdgeDelta());
        return true;
    }
    public bool TryAdvanceSemanticEdge(TapeSemanticReadState expectedState, out TapeSemanticEdgeResult result)
    {
        result = default;
        if (!_playing
            || _pulses.Count == 0
            || _pulseIndex < 0
            || _pulseIndex >= _pulses.Count
            || _pulseIndex != expectedState.PulseIndex
            || expectedState.NextEdgeDelta <= 0)
        {
            return false;
        }

        TapePulse source = _pulses[_pulseIndex];
        if (source.AccelerationFlags == TapeAccelerationPulseFlags.None
            || source.AccelerationFlags != expectedState.Flags)
        {
            return false;
        }

        int previousLastEdgePulse = _lastEdgePulseIndex;
        int sourcePulseIndex = _pulseIndex;
        bool earHighBefore = source.Level;
        _nextEdgeAccelerated = true;
        int elapsed = AdvanceToNextEdge(skipTime: true);

        // A stop marker can terminate playback before the requested transition.
        // In that case no semantic result exists and the CPU-side routine must be
        // allowed to observe the stop normally rather than receiving a fake edge.
        if (elapsed <= 0 || !_lastEdgeAccelerated || _lastEdgePulseIndex == previousLastEdgePulse)
        {
            _nextEdgeAccelerated = false;
            return false;
        }

        bool hasDestination = _playing && _pulseIndex >= 0 && _pulseIndex < _pulses.Count;
        TapeAccelerationPulseFlags destinationFlags = hasDestination
            ? _pulses[_pulseIndex].AccelerationFlags
            : TapeAccelerationPulseFlags.None;
        bool earHighAfter = hasDestination ? _pulses[_pulseIndex].Level : !earHighBefore;
        result = new TapeSemanticEdgeResult(
            elapsed,
            sourcePulseIndex,
            _pulseIndex,
            source.AccelerationFlags,
            destinationFlags,
            earHighBefore,
            earHighAfter,
            _playing);
        return true;
    }
    private int PeekNextSemanticEdgeDelta()
    {
        if (!_playing || _pulseRemaining <= 0 || _pulseIndex < 0 || _pulseIndex >= _pulses.Count)
        {
            return 0;
        }

        int delta = _pulseRemaining;
        bool currentLevel = _pulses[_pulseIndex].Level;
        for (int next = _pulseIndex + 1; next < _pulses.Count; next++)
        {
            // A stop marker is observable before the interval beginning at this
            // pulse. Semantic loading must never jump through it merely because
            // the neighbouring pulse happens to retain the same signal level.
            if (_stopMarkers.ContainsKey(next))
            {
                return 0;
            }

            TapePulse pulse = _pulses[next];
            if (pulse.Level != currentLevel)
            {
                return delta;
            }

            delta += pulse.TStates;
        }

        return _pulses[^1].EndsWithEdge ? delta : 0;
    }
    public bool TryGetPreviousPulseInfo(out int tstates, out bool isData)
    {
        tstates = 0;
        isData = false;
        if (_pulses.Count == 0)
        {
            return false;
        }

        int index = _pulseIndex - 1;
        if (index < 0 || index >= _pulses.Count)
        {
            return false;
        }

        var pulse = _pulses[index];
        tstates = pulse.TStates;
        isData = pulse.IsData;
        return true;
    }
    public bool TryGetLastEdgeInfo(out int tstates, out bool isData, out bool isLong, out bool fromSemanticAcceleration)
    {
        tstates = 0;
        isData = false;
        isLong = false;
        fromSemanticAcceleration = false;
        if (_lastEdgePulseIndex < 0)
        {
            return false;
        }

        tstates = _lastEdgePulseLength;
        isData = _lastEdgeIsData;
        isLong = _lastEdgeIsLong;
        fromSemanticAcceleration = _lastEdgeAccelerated;
        return true;
    }
    public void MarkNextEdgeSemanticallyAccelerated()
    {
        _nextEdgeAccelerated = true;
    }
    public void FastAdvance(int delta)
    {
        _pulseRemaining -= delta;
        if (_pulseRemaining == 0)
        {
            _pulseIndex++;
            if (_pulseIndex < _pulses.Count)
            {
                var p = _pulses[_pulseIndex];
                _pulseRemaining = p.TStates;
                _ear.SetEarLevel(p.Level);
            }
            else
            {
                CompleteFinalPulseEdge();
                _playing = false;
            }
        }
    }
    public void LoadTape(string path)
    {
        Level = false; _pulses.Clear(); _blocks.Clear(); _blockStartPulse.Clear(); _stopMarkers.Clear(); _currentBlockIndex = -1; _lastBlock = -1; _edgeRunStartPulse = -1;
        using var fs = File.OpenRead(path); using var br = new BinaryReader(fs);
        if (Encoding.ASCII.GetString(br.ReadBytes(7)) != "ZXTape!") throw new InvalidDataException("Not a TZX file");
        br.ReadBytes(3); // 1Ah, major, minor

        // Collect raw block positions so jumps/loops/calls operate on the real TZX block list.
        var blockOffsets = new List<long>(); var blockIds = new List<byte>(); var blockEnds = new List<long>();
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            long bstart = br.BaseStream.Position;
            byte id = br.ReadByte();
            long totalLength = GetTzxBlockTotalLength(br, bstart, id);
            long bend = checked(bstart + totalLength);
            if (totalLength < 1 || bend > br.BaseStream.Length)
            {
                throw new InvalidDataException($"Invalid TZX block {id:X2} length at offset {bstart}.");
            }

            blockOffsets.Add(bstart);
            blockIds.Add(id);
            blockEnds.Add(bend);
            br.BaseStream.Position = bend;
        }

        // Traversal with support for Loop/Jump/Call/Return (minimal, safe)
        int idx = 0; int watchdog = 0; var loopStack = new Stack<(int count, int startIdx)>();
        while (idx < blockOffsets.Count && watchdog < 100000)
        {
            watchdog++;
            long pos = blockOffsets[idx]; byte id = blockIds[idx]; br.BaseStream.Position = pos + 1; // after id
            _blockStartPulse.Add(_pulses.Count);
            switch (id)
            {
                case 0x10: { br.BaseStream.Position = pos + 1; var blk = new StdData(br); blk.AppendPulses(_pulses); _blocks.Add(blk); idx++; break; }
                case 0x11: { br.BaseStream.Position = pos + 1; var blk = new Turbo(br); blk.AppendPulses(_pulses); _blocks.Add(blk); idx++; break; }
                case 0x12: { br.BaseStream.Position = pos + 1; var blk = new Tone(br); blk.AppendPulses(_pulses); _blocks.Add(blk); idx++; break; }
                case 0x13: { br.BaseStream.Position = pos + 1; var blk = new PulseSeq(br); blk.AppendPulses(_pulses); _blocks.Add(blk); idx++; break; }
                case 0x14: { br.BaseStream.Position = pos + 1; var blk = new PureData(br); blk.AppendPulses(_pulses); _blocks.Add(blk); idx++; break; }
                case 0x15: { br.BaseStream.Position = pos + 1; var blk = new DirectRecording(br); blk.AppendPulses(_pulses); _blocks.Add(blk); idx++; break; }
                case 0x16: { br.BaseStream.Position = pos + 1; var blk = new DeprecatedC64Block(br, id); blk.AppendPulses(_pulses); _blocks.Add(blk); idx++; break; }
                case 0x17: { br.BaseStream.Position = pos + 1; var blk = new DeprecatedC64Block(br, id); blk.AppendPulses(_pulses); _blocks.Add(blk); idx++; break; }
                case 0x18: { br.BaseStream.Position = pos + 1; var blk = new TzxCswRecording(br, _cpuHz); blk.AppendPulses(_pulses); _blocks.Add(blk); idx++; break; }
                case 0x19: { br.BaseStream.Position = pos + 1; var blk = new GenData(br); blk.AppendPulses(_pulses); _blocks.Add(blk); idx++; break; }
                case 0x20:
                    {
                        br.BaseStream.Position = pos + 1;
                        var blk = new PauseBlk(br);
                        if (blk.PauseMs == 0)
                        {
                            AddStopMarker(TapeStopReason.PauseZero);
                        }
                        else
                        {
                            blk.AppendPulses(_pulses);
                        }
                        _blocks.Add(blk);
                        idx++;
                        break;
                    }
                case 0x21: { br.BaseStream.Position = pos + 1; var blk = new GroupStart(br); _blocks.Add(blk); idx++; break; }
                case 0x22: { br.BaseStream.Position = pos + 1; var blk = new GroupEnd(); _blocks.Add(blk); idx++; break; }
                case 0x24: { br.BaseStream.Position = pos + 1; var ls = new LoopStart(br); loopStack.Push((ls.Count, idx + 1)); _blocks.Add(ls); idx++; break; }
                case 0x25: { br.BaseStream.Position = pos + 1; var le = new LoopEnd(); if (loopStack.Count > 0) { var (count, startIdx) = loopStack.Pop(); if (count > 1) { loopStack.Push((count - 1, startIdx)); idx = startIdx; } else idx++; } else idx++; _blocks.Add(le); break; }
                case 0x23:
                    { // Jump relative
                        short off = (short)ReadU16At(br, pos + 1); int next = idx + 1; int target = next + off; if (target < 0 || target >= blockOffsets.Count) idx = next; else idx = target; break;
                    }
                case 0x26:
                    { // Call sequence (list of rel offsets)
                        ushort n = ReadU16At(br, pos + 1); int ret = idx + 1; for (int i = 0; i < n; i++)
                        {
                            short off = (short)ReadU16At(br, pos + 3 + 2 * i); int target = ret + off; if (target >= 0 && target < blockOffsets.Count)
                            {
                                int j = target; while (j < blockOffsets.Count && blockIds[j] != 0x27)
                                { // until RETURN
                                    long p = blockOffsets[j]; byte id2 = blockIds[j]; br.BaseStream.Position = p + 1; // process inline
                                    _blockStartPulse.Add(_pulses.Count);
                                    if (id2 == 0x27) { break; }
                                    var blk2 = Factory.ReadOne(br, id2, _cpuHz, blockEnds[j]); blk2.AppendPulses(_pulses); _blocks.Add(blk2); j++;
                                }
                            }
                        }
                        idx = ret; break;
                    }
                case 0x27: { idx++; break; } // Return (should be handled by inline walk above)
                case 0x28: { br.BaseStream.Position = pos + 1; var blk = new SelectBlock(br); _blocks.Add(blk); idx++; break; }
                case 0x2A:
                    {
                        br.BaseStream.Position = pos + 1;
                        var blk = new StopIf48K(br);
                        if (_stopOn48k)
                        {
                            AddStopMarker(TapeStopReason.StopIf48K);
                        }
                        _blocks.Add(blk);
                        idx++;
                        break;
                    }
                case 0x2B: { br.BaseStream.Position = pos + 1; var blk = new SetLevel(br); blk.AppendPulses(_pulses); _blocks.Add(blk); idx++; break; }
                default: { br.BaseStream.Position = pos + 1; var skip = Factory.ReadOne(br, id, _cpuHz, blockEnds[idx]); _blocks.Add(skip); idx++; break; }
            }
        }

        BuildTimingIndex();
        if (_pulses.Count > 0)
        {
            _pulseIndex = 0;
            _pulseRemaining = _pulses[0].TStates;
            _edgeSkipTstates = 0;
            _currentBlockIndex = 0;
            _lastBlock = 0;
            _ear.SetEarLevel(false);
        }
    }
    private void BuildTimingIndex()
    {
        int count = _pulses.Count;
        _pulseTstatePrefix = new long[count + 1];
        long sum = 0;
        for (int i = 0; i < count; i++)
        {
            sum += _pulses[i].TStates;
            _pulseTstatePrefix[i + 1] = sum;
        }

        int blocks = _blockStartPulse.Count;
        _blockStartTstates = new long[blocks];
        _blockDurations = new long[blocks];
        for (int i = 0; i < blocks; i++)
        {
            int startPulse = _blockStartPulse[i];
            int endPulse = (i + 1 < blocks) ? _blockStartPulse[i + 1] : count;
            long start = _pulseTstatePrefix[startPulse];
            long end = _pulseTstatePrefix[endPulse];
            _blockStartTstates[i] = start;
            _blockDurations[i] = end - start;
        }
    }
    private int FindBlockIndexForPulse(int pulseIndex)
    {
        if (_blockStartPulse.Count == 0 || pulseIndex < 0)
        {
            return -1;
        }

        int index = _blockStartPulse.BinarySearch(pulseIndex);
        if (index < 0)
        {
            index = ~index - 1;
        }

        if (index < 0)
        {
            return -1;
        }

        if (index >= _blockStartPulse.Count)
        {
            return _blockStartPulse.Count - 1;
        }

        return index;
    }
    private void UpdateCurrentBlockIndexAfterPulseAdvance()
    {
        if (_blockStartPulse.Count == 0)
        {
            SetCurrentBlockIndex(-1, raiseEvent: false);
            return;
        }

        int index = _currentBlockIndex;
        if (index < 0)
        {
            index = FindBlockIndexForPulse(_pulseIndex);
        }
        else
        {
            while (index + 1 < _blockStartPulse.Count && _pulseIndex >= _blockStartPulse[index + 1])
            {
                index++;
            }

            while (index > 0 && _pulseIndex < _blockStartPulse[index])
            {
                index--;
            }
        }

        SetCurrentBlockIndex(index, raiseEvent: true);
    }
    private void SetCurrentBlockIndex(int index, bool raiseEvent)
    {
        if (index == _currentBlockIndex)
        {
            return;
        }

        _currentBlockIndex = index;
        _lastBlock = index;
        if (raiseEvent)
        {
            BlockIndexChanged?.Invoke(this, index);
        }
    }
    static byte ReadU8At(BinaryReader br, long pos) { long save = br.BaseStream.Position; br.BaseStream.Position = pos; byte v = br.ReadByte(); br.BaseStream.Position = save; return v; }
    static ushort ReadU16At(BinaryReader br, long pos) { long save = br.BaseStream.Position; br.BaseStream.Position = pos; ushort v = (ushort)(br.ReadByte() | (br.ReadByte() << 8)); br.BaseStream.Position = save; return v; }
    static uint ReadU24At(BinaryReader br, long pos) { long save = br.BaseStream.Position; br.BaseStream.Position = pos; uint v = (uint)(br.ReadByte() | (br.ReadByte() << 8) | (br.ReadByte() << 16)); br.BaseStream.Position = save; return v; }
    static uint ReadU32At(BinaryReader br, long pos) { long save = br.BaseStream.Position; br.BaseStream.Position = pos; uint v = (uint)(br.ReadByte() | (br.ReadByte() << 8) | (br.ReadByte() << 16) | (br.ReadByte() << 24)); br.BaseStream.Position = save; return v; }
    static long GetTzxBlockTotalLength(BinaryReader br, long pos, byte id)
    {
        return id switch
        {
            0x10 => 1L + 4 + ReadU16At(br, pos + 3),
            0x11 => 1L + 18 + ReadU24At(br, pos + 0x10),
            0x12 => 1L + 4,
            0x13 => 1L + 1 + (ReadU8At(br, pos + 1) * 2L),
            0x14 => 1L + 10 + ReadU24At(br, pos + 0x08),
            0x15 => 1L + 8 + ReadU24At(br, pos + 0x06),
            0x16 => 1L + ReadU32At(br, pos + 1),
            0x17 => 1L + ReadU32At(br, pos + 1),
            0x18 => 1L + 4 + ReadU32At(br, pos + 1),
            0x19 => 1L + 4 + ReadU32At(br, pos + 1),
            0x20 => 1L + 2,
            0x21 => 1L + 1 + ReadU8At(br, pos + 1),
            0x22 => 1L,
            0x23 => 1L + 2,
            0x24 => 1L + 2,
            0x25 => 1L,
            0x26 => 1L + 2 + (ReadU16At(br, pos + 1) * 2L),
            0x27 => 1L,
            0x28 => 1L + 2 + ReadU16At(br, pos + 1),
            0x2A => 1L + 4 + ReadU32At(br, pos + 1),
            0x2B => 1L + 4 + ReadU32At(br, pos + 1),
            0x30 => 1L + 1 + ReadU8At(br, pos + 1),
            0x31 => 1L + 2 + ReadU8At(br, pos + 2),
            0x32 => 1L + 2 + ReadU16At(br, pos + 1),
            0x33 => 1L + 1 + (ReadU8At(br, pos + 1) * 3L),
            0x34 => 1L + 8,
            0x35 => 1L + 16 + 4 + ReadU32At(br, pos + 17),
            0x40 => 1L + 4 + ReadU24At(br, pos + 2),
            0x5A => 1L + 9,
            _ => 1L + 4 + ReadU32At(br, pos + 1)
        };
    }
    private readonly struct CswHeader(int sampleRate, byte compression, bool initialLevel, uint pulseCount)
        {
            public int SampleRate { get; } = sampleRate;
            public byte Compression { get; } = compression;
            public bool InitialLevel { get; } = initialLevel;
            public uint PulseCount { get; } = pulseCount;
        }
    private static CswHeader ReadCswHeader(BinaryReader br)
    {
        byte[] sig = br.ReadBytes(22);
        string signature = Encoding.ASCII.GetString(sig);
        if (signature != "Compressed Square Wave")
        {
            throw new InvalidDataException("Not a CSW file.");
        }

        byte marker = br.ReadByte();
        byte major;
        if (marker == 0x1A)
        {
            major = br.ReadByte();
            _ = br.ReadByte();
        }
        else
        {
            major = marker;
            _ = br.ReadByte();
        }

        uint sampleRate = br.ReadUInt32();
        if (sampleRate == 0 || sampleRate > int.MaxValue)
        {
            throw new InvalidDataException("Invalid CSW sample rate.");
        }

        byte compression = br.ReadByte();
        byte flags = br.ReadByte();
        bool initialLevel = (flags & 0x01) != 0;

        uint pulseCount = 0;
        long remaining = br.BaseStream.Length - br.BaseStream.Position;
        if (remaining >= 4)
        {
            uint first = br.ReadUInt32();
            if (major >= 2 && first <= remaining - 4)
            {
                uint extLen = first;
                if (extLen > 0)
                {
                    if (extLen > br.BaseStream.Length - br.BaseStream.Position)
                    {
                        throw new InvalidDataException("Invalid CSW header extension length.");
                    }

                    br.BaseStream.Position += extLen;
                }

                if (br.BaseStream.Length - br.BaseStream.Position >= 4)
                {
                    pulseCount = br.ReadUInt32();
                }
            }
            else
            {
                pulseCount = first;
            }
        }

        return new CswHeader((int)sampleRate, compression, initialLevel, pulseCount);
    }
    public void LoadCswFile(string path)
    {
        Level = false;
        _blocks.Clear(); _pulses.Clear(); _blockStartPulse.Clear(); _stopMarkers.Clear(); _currentBlockIndex = -1; _lastBlock = -1; _edgeRunStartPulse = -1;

        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        var header = ReadCswHeader(br);
        byte[] data = br.ReadBytes((int)(fs.Length - fs.Position));

        var csw = new CswBlock(_cpuHz, header.SampleRate, header.Compression, header.InitialLevel, data, pauseMs: 0, header.PulseCount);
        _blocks.Add(csw);
        _blockStartPulse.Add(_pulses.Count);
        csw.AppendPulses(_pulses);

        BuildTimingIndex();
        if (_pulses.Count > 0)
        {
            _pulseIndex = 0;
            _pulseRemaining = _pulses[0].TStates;
            _edgeSkipTstates = 0;
            _currentBlockIndex = 0;
            _lastBlock = 0;
            _ear.SetEarLevel(false);
        }
    }

    // Convenience: TAP quick loader kept for your UI
    public void LoadTapFile(string path)
    {
        _blocks.Clear(); _pulses.Clear(); _blockStartPulse.Clear(); _stopMarkers.Clear(); _currentBlockIndex = -1; _lastBlock = -1; _edgeRunStartPulse = -1;
        var all = File.ReadAllBytes(path); int idx = 0; while (idx + 2 <= all.Length) { ushort len = (ushort)(all[idx] | (all[idx + 1] << 8)); idx += 2; if (idx + len > all.Length) break; var payload = new byte[len]; Array.Copy(all, idx, payload, 0, len); idx += len; var tap = new TapBlock(payload, pauseAfterMs: 500); _blocks.Add(tap); _blockStartPulse.Add(_pulses.Count); tap.AppendPulses(_pulses); }
        BuildTimingIndex();
        if (_pulses.Count > 0)
        {
            _pulseIndex = 0;
            _pulseRemaining = _pulses[0].TStates;
            _edgeSkipTstates = 0;
            _currentBlockIndex = 0;
            _lastBlock = 0;
            _ear.SetEarLevel(false);
        }
    }
    public void JumpToBlock(int i)
    {
        JumpToBlockPulse(i, 0, play: true);
    }
    public void JumpToBlockPulse(int blockIndex, int pulseOffset, bool play)
    {
        if (blockIndex < 0 || blockIndex >= _blockStartPulse.Count)
        {
            return;
        }

        int start = _blockStartPulse[blockIndex];
        int end = blockIndex + 1 < _blockStartPulse.Count ? _blockStartPulse[blockIndex + 1] : _pulses.Count;
        if (start < 0 || start >= _pulses.Count || end <= start)
        {
            return;
        }

        _pulseIndex = Math.Clamp(start + pulseOffset, start, end - 1);
        _pulseRemaining = _pulses[_pulseIndex].TStates;
        _edgeSkipTstates = 0;
        _nextEdgeAccelerated = false;
        _edgeSeen = false;
        _lastEdgeAccelerated = false;
        _lastEdgePulseIndex = -1;
        SetCurrentBlockIndex(blockIndex, raiseEvent: true);

        if (play)
        {
            _ear.SetEarLevel(_pulses[_pulseIndex].Level);
            _playing = true;
            StopAtMarkerIfNeeded(_pulseIndex);
        }
        else
        {
            Stop();
        }
    }
    public void SkipPastBlock(int blockIndex)
    {
        int next = blockIndex + 1;
        if (next >= _blockStartPulse.Count)
        {
            StopPlayback(TapeStopReason.EndOfTape);
            return;
        }

        JumpToNextPlayableBlock(next, play: true);
    }
    public bool JumpToNextPlayableBlock(int startBlockIndex, bool play)
    {
        if (_blockStartPulse.Count == 0 || _pulses.Count == 0)
        {
            if (play)
            {
                StopPlayback(TapeStopReason.EndOfTape);
            }

            return false;
        }

        int index = Math.Max(0, startBlockIndex);
        while (index < _blockStartPulse.Count)
        {
            int start = _blockStartPulse[index];
            int end = index + 1 < _blockStartPulse.Count ? _blockStartPulse[index + 1] : _pulses.Count;

            SetCurrentBlockIndex(index, raiseEvent: true);
            if (play && StopAtMarkerIfNeeded(start))
            {
                return true;
            }

            if (end > start && start < _pulses.Count)
            {
                JumpToBlockPulse(index, 0, play);
                return true;
            }

            index++;
        }

        if (play)
        {
            StopPlayback(TapeStopReason.EndOfTape);
        }

        return false;
    }
    public bool TryGetNextStandardBlock(int startIndex, byte flag, out TapeStandardBlock block)
    {
        block = default;
        if (_blocks.Count == 0)
        {
            return false;
        }

        int start = Math.Clamp(startIndex, 0, _blocks.Count - 1);
        for (int i = start; i < _blocks.Count; i++)
        {
            ITzxBlock blk = _blocks[i];
            byte[]? data = blk switch
            {
                StdData std => std.Data,
                TapBlock tap => tap.Data,
                _ => null
            };

            if (data == null || data.Length < 2)
            {
                continue;
            }

            if (data[0] != flag)
            {
                continue;
            }

            block = new TapeStandardBlock(i, data);
            return true;
        }

        return false;
    }
    private void AddStopMarker(TapeStopReason reason)
    {
        _stopMarkers.TryAdd(_pulses.Count, reason);
    }
    private bool StopAtMarkerIfNeeded(int pulseIndex)
    {
        if (!_stopMarkers.TryGetValue(pulseIndex, out TapeStopReason reason))
        {
            return false;
        }

        _stopMarkers.Remove(pulseIndex);
        StopPlayback(reason);
        return true;
    }
    private void StopPlayback(TapeStopReason reason, bool driveEarLow = true, bool clearEdgeSeen = true)
    {
        Debug.WriteLine($"TZX playback stopped: {reason}");
        _playing = false;
        if (clearEdgeSeen)
        {
            _edgeSeen = false;
        }
        if (driveEarLow)
        {
            _ear.SetEarLevel(false);
        }
        PlaybackStopped?.Invoke(this, reason);
    }
}

/// <summary>Adapts a length-prefixed TAP payload to ROM-format TZX pulse generation.</summary>
sealed class TapBlock(byte[] payload, ushort pauseAfterMs = 100) : ITzxBlock
{
    private readonly byte[] _data = payload; public ushort PauseAfterMs { get; } = pauseAfterMs;

        static void Half(List<TapePulse> s, int t) { s.Add(new TapePulse(TzxLoader.Level, t)); TzxLoader.Level = !TzxLoader.Level; }
    static void HalfData(List<TapePulse> s, int t, bool isLong)
    {
        s.Add(new TapePulse(TzxLoader.Level, t, isData: true, isLong: isLong));
        TzxLoader.Level = !TzxLoader.Level;
    }
    public void AppendPulses(List<TapePulse> sink)
    {
        int pilot = ((_data[0] & 0x80) == 0) ? 8063 : 3223; for (int i = 0; i < pilot; i++) Half(sink, 2168);
        Half(sink, 667); Half(sink, 735);
        foreach (var b in _data)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                bool isLong = ((b >> bit) & 1) != 0;
                int t = isLong ? 1710 : 855;
                HalfData(sink, t, isLong);
                HalfData(sink, t, isLong);
            }
        }
        Half(sink, 855); Half(sink, 855);
        if (PauseAfterMs > 0) sink.Add(new TapePulse(TzxLoader.Level, PauseAfterMs * 3500, endsWithEdge: false));
    }
    public bool IsHeader => (_data.Length >= 1 && _data[0] == 0x00);
    public byte FileType => _data.Length >= 2 ? _data[1] : (byte)0xFF;
    public string Name => _data.Length >= 12 ? Encoding.ASCII.GetString(_data, 2, 10).TrimEnd() : "";
    public ushort PayloadLen => _data.Length >= 14 ? (ushort)(_data[12] | (_data[13] << 8)) : (ushort)0;
    public ushort LoadAddress => _data.Length >= 16 ? (ushort)(_data[14] | (_data[15] << 8)) : (ushort)0;
    public int DataLength => _data.Length;
    public byte[] Data => _data;

}
/// <summary>Display metadata projected from a parsed block for the tape browser.</summary>
public class BlockInfo
{
    public int Index { get; init; }
    public int DisplayIndex => Index + 1;
    public string Type { get; init; }
    public int Length { get; init; }
    public string Name { get; init; } = "";
    public bool IsHeader { get; }
    public byte FileType { get; }
    public string FileName { get; }
    public ushort PayloadLen { get; }
    public ushort LoadAddress { get; }

    public BlockInfo(int idx, ITzxBlock blk)
    {
        Index = idx;

        // Keep UI names stable even when implementation class names change.
        Type = blk switch
        {
            StdData => "StandardData",
            Turbo => "TurboData",
            Tone => "PureTone",
            PulseSeq => "PulseSequence",
            DirectRecording => "DirectRecording",
            PauseBlk => "PauseBlock",
            GenData => "GeneralizedData",
            SelectBlock => "SelectBlock",
            StopIf48K => "StopIf48K",
            SetLevel => "SetSignalLevel",
            TextDescription => "TextDescription",
            MessageBlock => "Message",
            ArchiveInfo => "ArchiveInfo",
            HardwareTypeInfo => "HardwareType",
            DeprecatedEmulationInfo => "EmulationInfo",
            CustomInfoBlock => "CustomInfo",
            GlueBlock => "Glue",
            SnapshotBlock => "Snapshot",
            DeprecatedC64Block => "DeprecatedC64",
            UnknownTzxBlock => "Unknown",
            SpeedlockData => "SpeedlockData",
            SpeedlockHeader => "SpeedlockHeader",
            CswBlock => "CSW",
            TzxCswRecording => "CSW",
            // Old classes (your previous implementation)
            TapBlock => "StandardData",
            _ => blk.GetType().Name
        };

        // Unified length for the browser.
        Length = blk switch
        {
            // New classes
            StdData sd => sd.DataLength,
            Turbo t => t.DataLength,
            Tone pt => pt.RepCount,
            PulseSeq ps => ps.PulsesCount,
            DirectRecording dr => dr.DataLength,
            PureData pd => pd.DataLength,
            PauseBlk pb => pb.PauseMs,
            LoopStart ls => ls.Count,
            TapBlock tap => tap.DataLength,
            SpeedlockData sld => sld.DataLen,
            SpeedlockHeader slh => slh.PilotPulseCount,
            CswBlock cb => cb.PulseCount == 0 ? cb.DataLength : (cb.PulseCount > int.MaxValue ? int.MaxValue : (int)cb.PulseCount),
            TzxCswRecording tcr => tcr.PulseCount == 0 ? tcr.DataLength : (tcr.PulseCount > int.MaxValue ? int.MaxValue : (int)tcr.PulseCount),
            GenData gd => gd.DataLength,
            SelectBlock sb => sb.Selections.Count,
            TextDescription td => td.Text.Length,
            MessageBlock mb => mb.Message.Length,
            ArchiveInfo ai => ai.Data.Length,
            HardwareTypeInfo hti => hti.Data.Length / 3,
            CustomInfoBlock ci => ci.Data.Length,
            SnapshotBlock ss => ss.Data.Length,
            DeprecatedC64Block c64 => c64.Data.Length,
            UnknownTzxBlock unknown => unknown.Data.Length,

            // Old classes
            _ => 0
        };

        // Header metadata for Standard blocks (TAP/TZX headers)
        if (blk is StdData nsd && nsd.IsHeader)
        {
            IsHeader = true;
            FileType = nsd.FileType;
            PayloadLen = nsd.PayloadLen;
            LoadAddress = nsd.LoadAddress;

            var typeName = FileType switch
            {
                0x00 => "Program:",
                0x01 => "Number Array:",
                0x02 => "Character Array:",
                0x03 => "Bytes:",
                _ => ""
            };
            if (FileType == 0x03 && LoadAddress == 16384) typeName = "Screen$";

            Name = nsd.Name;
            FileName = $"{typeName} {nsd.Name}".Trim();
            if (FileType == 0x03) FileName += $" @({LoadAddress:X4})";
        }
        else if (blk is TapBlock tap && tap.IsHeader)
        {
            // TAP file header blocks
            IsHeader = true;
            FileType = tap.FileType;
            PayloadLen = tap.PayloadLen;
            LoadAddress = tap.LoadAddress;

            var typeName = FileType switch
            {
                0x00 => "Program:",
                0x01 => "Number Array:",
                0x02 => "Character Array:",
                0x03 => "Bytes:",
                _ => ""
            };
            if (FileType == 0x03 && LoadAddress == 16384) typeName = "Screen$";

            Name = tap.Name;
            FileName = $"{typeName} {tap.Name}".Trim();
            if (FileType == 0x03) FileName += $" @({LoadAddress:X4})";
        }
        else
        {
            // Non-header blocks
            IsHeader = false;
            FileType = 0;
            FileName = "";
            PayloadLen = 0;
            LoadAddress = 0;
        }
    }
}
}
