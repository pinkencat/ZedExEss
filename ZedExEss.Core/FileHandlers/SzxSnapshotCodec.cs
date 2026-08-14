using System.IO.Compression;
using System.Text;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Interface1;
using ZedExEss.Spectrum.Video;
using ZedExEss.Z80CPU;

namespace ZedExEss.FileHandlers;

/// <summary>
/// Reads and writes ZX-State (SZX) whole-machine snapshots.
/// </summary>
/// <remarks>
/// Standard chunks provide interoperability. ZEXC preserves CPU latches which
/// Z80R omits, and ZEI1 preserves complete in-memory Microdrive media/transport
/// state because the historical MDRV chunk cannot represent it losslessly.
/// Unknown chunks are skipped as required by SZX's extensible design.
/// </remarks>
public static class SzxSnapshotCodec
{
    private const int RamPageSize = 0x4000;
    private const int MaximumChunkSize = 64 * 1024 * 1024;
    private const ushort RampCompressed = 0x0001;
    private const ushort If1Enabled = 0x0001;
    private const ushort If1Paged = 0x0004;
    private const byte Z80FlagEiLast = 0x01;
    private const byte Z80FlagHalted = 0x02;
    private const byte Z80FlagFSet = 0x04;

    public static SpectrumModel DetectModel(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
        ReadAndValidateHeader(reader, out _, out _, out SpectrumModel model);
        return model;
    }

    public static SpectrumMachineSnapshot Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }

    public static void Save(string path, SpectrumMachineSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        using FileStream stream = File.Create(path);
        Write(stream, snapshot);
    }

    public static SpectrumMachineSnapshot Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        ReadAndValidateHeader(reader, out byte major, out byte minor, out SpectrumModel model);
        ushort version = (ushort)((major << 8) | minor);

        Z80SnapshotState? cpu = null;
        CpuExtension? cpuExtension = null;
        byte port7ffd = 0;
        byte port1ffd = 0;
        byte ulaOutput = 0;
        int frameTstate = 0;
        int frameCounter = 0;
        SpectrumAySnapshot? ay = null;
        bool standardIf1Present = false;
        bool standardIf1Paged = false;
        SpectrumInterface1Snapshot? interface1 = null;
        var pages = new Dictionary<int, byte[]>();

        while (stream.Position < stream.Length)
        {
            if (stream.Length - stream.Position < 8)
            {
                throw new InvalidDataException("Truncated SZX chunk header.");
            }

            string id = Encoding.ASCII.GetString(ReadExactly(reader, 4));
            uint rawLength = reader.ReadUInt32();
            if (rawLength > MaximumChunkSize || rawLength > stream.Length - stream.Position)
            {
                throw new InvalidDataException($"Invalid SZX {id} chunk length {rawLength}.");
            }

            byte[] body = ReadExactly(reader, checked((int)rawLength));
            switch (id)
            {
                case "Z80R":
                    if (cpu != null)
                    {
                        throw new InvalidDataException("SZX contains more than one Z80R chunk.");
                    }

                    cpu = ReadZ80(body, version, out frameTstate);
                    break;
                case "SPCR":
                    ReadSpectrumRegisters(body, version, out ulaOutput, out port7ffd, out port1ffd);
                    break;
                case "RAMP":
                    ReadRamPage(body, pages);
                    break;
                case "AY\0\0":
                    ay = ReadAy(body);
                    break;
                case "IF1\0":
                    ReadStandardInterface1(body, out standardIf1Present, out standardIf1Paged);
                    break;
                case "ZEXC":
                    cpuExtension = ReadCpuExtension(body);
                    break;
                case "ZEI1":
                    interface1 = ReadInterface1Extension(body);
                    break;
            }
        }

        if (cpu == null)
        {
            throw new InvalidDataException("SZX snapshot has no Z80R CPU block.");
        }

        if (cpuExtension != null)
        {
            cpu = cpu with
            {
                Cycles = cpuExtension.Cycles,
                IffDelay = cpuExtension.IffDelay,
                InterruptData = cpuExtension.InterruptData,
                IntPending = cpuExtension.IntPending,
                NmiPending = cpuExtension.NmiPending,
                Q = cpuExtension.Q,
                LastQ = cpuExtension.LastQ
            };
            frameCounter = cpuExtension.FrameCounter;
        }

        if (interface1 == null && standardIf1Present)
        {
            interface1 = CreateEmptyInterface1Snapshot(standardIf1Paged);
        }

        byte[][] ramBanks = BuildInternalRamBanks(model, pages);
        int frameLength = SpectrumUlaTiming.ForModel(model).TstatesPerFrame;
        frameTstate %= frameLength;

        return new SpectrumMachineSnapshot(
            model,
            cpu,
            ramBanks,
            port7ffd,
            port1ffd,
            ulaOutput,
            frameTstate,
            frameCounter,
            ay,
            interface1);
    }

    public static void Write(Stream stream, SpectrumMachineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(snapshot);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("ZXST"));
        writer.Write((byte)1);
        writer.Write((byte)5);
        writer.Write(ModelToSzxId(snapshot.Model));
        writer.Write((byte)0);

        WriteChunk(writer, "Z80R", body => WriteZ80(body, snapshot));
        WriteChunk(writer, "SPCR", body => WriteSpectrumRegisters(body, snapshot));
        for (int bank = 0; bank < snapshot.RamBankCount; bank++)
        {
            int page = InternalBankToSzxPage(snapshot.Model, bank);
            WriteChunk(writer, "RAMP", body =>
            {
                body.Write((ushort)0);
                body.Write((byte)page);
                body.Write(snapshot.GetRamBankSpan(bank));
            });
        }

        if (snapshot.Ay != null)
        {
            WriteChunk(writer, "AY\0\0", body =>
            {
                body.Write((byte)0x02); // Built-in 128K-style AY.
                body.Write(snapshot.Ay.SelectedRegister);
                body.Write(snapshot.Ay.Registers);
            });
        }

        if (snapshot.Interface1 != null)
        {
            WriteChunk(writer, "IF1\0", body =>
            {
                ushort flags = If1Enabled;
                if (snapshot.Interface1.Device?.IsPaged == true)
                {
                    flags |= If1Paged;
                }

                body.Write(flags);
                body.Write((byte)SpectrumInterface1Device.DriveCount);
                body.Write(new byte[3 + (8 * sizeof(uint))]);
                body.Write((ushort)0); // Standard IF1 v2 ROM; no custom ROM payload.
            });
        }

        WriteChunk(writer, "ZEXC", body => WriteCpuExtension(body, snapshot));
        if (snapshot.Interface1 != null)
        {
            WriteChunk(writer, "ZEI1", body => WriteInterface1Extension(body, snapshot.Interface1));
        }
    }

    private static void ReadAndValidateHeader(
        BinaryReader reader,
        out byte major,
        out byte minor,
        out SpectrumModel model)
    {
        if (reader.BaseStream.Length - reader.BaseStream.Position < 8 ||
            Encoding.ASCII.GetString(ReadExactly(reader, 4)) != "ZXST")
        {
            throw new InvalidDataException("File is not an SZX snapshot.");
        }

        major = reader.ReadByte();
        minor = reader.ReadByte();
        if (major != 1)
        {
            throw new InvalidDataException($"Unsupported SZX version {major}.{minor}.");
        }

        model = SzxIdToModel(reader.ReadByte());
        _ = reader.ReadByte(); // Machine flags; alternate timings are not currently selected by our profiles.
    }

    private static Z80SnapshotState ReadZ80(byte[] body, ushort version, out int frameTstate)
    {
        if (body.Length != 37)
        {
            throw new InvalidDataException("SZX Z80R chunk must be 37 bytes.");
        }

        using var reader = CreateReader(body);
        byte f = reader.ReadByte();
        byte a = reader.ReadByte();
        ushort bc = reader.ReadUInt16();
        ushort de = reader.ReadUInt16();
        ushort hl = reader.ReadUInt16();
        byte alternateF = reader.ReadByte();
        byte alternateA = reader.ReadByte();
        ushort alternateBc = reader.ReadUInt16();
        ushort alternateDe = reader.ReadUInt16();
        ushort alternateHl = reader.ReadUInt16();
        ushort ix = reader.ReadUInt16();
        ushort iy = reader.ReadUInt16();
        ushort sp = reader.ReadUInt16();
        ushort pc = reader.ReadUInt16();
        byte i = reader.ReadByte();
        byte r = reader.ReadByte();
        bool iff1 = reader.ReadByte() != 0;
        bool iff2 = reader.ReadByte() != 0;
        byte im = reader.ReadByte();
        frameTstate = checked((int)reader.ReadUInt32());
        _ = reader.ReadByte();
        byte flags = reader.ReadByte();
        ushort memPtr = version >= 0x0104 ? reader.ReadUInt16() : (ushort)0;

        return new Z80SnapshotState(
            (ulong)frameTstate,
            pc,
            sp,
            ix,
            iy,
            memPtr,
            a,
            f,
            (byte)(bc >> 8),
            (byte)bc,
            (byte)(de >> 8),
            (byte)de,
            (byte)(hl >> 8),
            (byte)hl,
            alternateA,
            alternateF,
            (byte)(alternateBc >> 8),
            (byte)alternateBc,
            (byte)(alternateDe >> 8),
            (byte)alternateDe,
            (byte)(alternateHl >> 8),
            (byte)alternateHl,
            i,
            r,
            im,
            iff1,
            iff2,
            (flags & Z80FlagHalted) != 0,
            (byte)((flags & Z80FlagEiLast) != 0 ? 1 : 0),
            0,
            false,
            false,
            (byte)((flags & Z80FlagFSet) != 0 ? f : 0),
            0);
    }

    private static void WriteZ80(BinaryWriter writer, SpectrumMachineSnapshot snapshot)
    {
        Z80SnapshotState cpu = snapshot.Cpu;
        writer.Write(cpu.F);
        writer.Write(cpu.A);
        writer.Write(Pair(cpu.B, cpu.C));
        writer.Write(Pair(cpu.D, cpu.E));
        writer.Write(Pair(cpu.H, cpu.L));
        writer.Write(cpu.AlternateF);
        writer.Write(cpu.AlternateA);
        writer.Write(Pair(cpu.AlternateB, cpu.AlternateC));
        writer.Write(Pair(cpu.AlternateD, cpu.AlternateE));
        writer.Write(Pair(cpu.AlternateH, cpu.AlternateL));
        writer.Write(cpu.IX);
        writer.Write(cpu.IY);
        writer.Write(cpu.SP);
        writer.Write(cpu.PC);
        writer.Write(cpu.I);
        writer.Write(cpu.R);
        writer.Write((byte)(cpu.Iff1 ? 1 : 0));
        writer.Write((byte)(cpu.Iff2 ? 1 : 0));
        writer.Write(cpu.InterruptMode);
        writer.Write((uint)snapshot.FrameTstate);
        writer.Write((byte)Math.Max(0, 48 - snapshot.FrameTstate));
        byte flags = 0;
        if (cpu.IffDelay != 0) flags |= Z80FlagEiLast;
        if (cpu.Halted) flags |= Z80FlagHalted;
        if (cpu.Q != 0) flags |= Z80FlagFSet;
        writer.Write(flags);
        writer.Write(cpu.MemPtr);
    }

    private static void ReadSpectrumRegisters(
        byte[] body,
        ushort version,
        out byte ulaOutput,
        out byte port7ffd,
        out byte port1ffd)
    {
        if (body.Length != 8)
        {
            throw new InvalidDataException("SZX SPCR chunk must be 8 bytes.");
        }

        byte border = (byte)(body[0] & 0x07);
        port7ffd = body[1];
        port1ffd = body[2];
        ulaOutput = version >= 0x0101 ? (byte)(border | (body[3] & 0xF8)) : border;
    }

    private static void WriteSpectrumRegisters(BinaryWriter writer, SpectrumMachineSnapshot snapshot)
    {
        writer.Write((byte)(snapshot.UlaOutput & 0x07));
        writer.Write(snapshot.Port7FFD);
        writer.Write(snapshot.Port1FFD);
        writer.Write(snapshot.UlaOutput);
        writer.Write(0u);
    }

    private static void ReadRamPage(byte[] body, IDictionary<int, byte[]> pages)
    {
        if (body.Length < 3)
        {
            throw new InvalidDataException("SZX RAMP chunk is too short.");
        }

        ushort flags = (ushort)(body[0] | (body[1] << 8));
        int page = body[2];
        if (!pages.TryAdd(page, DecodeRamPayload(body.AsSpan(3), (flags & RampCompressed) != 0)))
        {
            throw new InvalidDataException($"SZX contains duplicate RAM page {page}.");
        }
    }

    private static byte[] DecodeRamPayload(ReadOnlySpan<byte> payload, bool compressed)
    {
        if (!compressed)
        {
            if (payload.Length != RamPageSize)
            {
                throw new InvalidDataException("Uncompressed SZX RAM page must contain 16384 bytes.");
            }

            return payload.ToArray();
        }

        using var input = new MemoryStream(payload.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var output = new MemoryStream(RamPageSize);
        zlib.CopyTo(output);
        if (output.Length != RamPageSize)
        {
            throw new InvalidDataException("Compressed SZX RAM page did not expand to 16384 bytes.");
        }

        return output.ToArray();
    }

    private static SpectrumAySnapshot ReadAy(byte[] body)
    {
        if (body.Length != 18)
        {
            throw new InvalidDataException("SZX AY chunk must be 18 bytes.");
        }

        return new SpectrumAySnapshot(body[1], body.AsSpan(2, 16).ToArray());
    }

    private static void ReadStandardInterface1(byte[] body, out bool present, out bool paged)
    {
        if (body.Length < 40)
        {
            throw new InvalidDataException("SZX IF1 chunk is too short.");
        }

        ushort flags = (ushort)(body[0] | (body[1] << 8));
        present = (flags & If1Enabled) != 0;
        paged = (flags & If1Paged) != 0;
    }

    private static void WriteCpuExtension(BinaryWriter writer, SpectrumMachineSnapshot snapshot)
    {
        Z80SnapshotState cpu = snapshot.Cpu;
        writer.Write((ushort)1);
        writer.Write(cpu.Cycles);
        writer.Write(snapshot.FrameCounter);
        writer.Write(cpu.IffDelay);
        writer.Write(cpu.InterruptData);
        writer.Write((byte)((cpu.IntPending ? 1 : 0) | (cpu.NmiPending ? 2 : 0)));
        writer.Write(cpu.Q);
        writer.Write(cpu.LastQ);
    }

    private static CpuExtension ReadCpuExtension(byte[] body)
    {
        if (body.Length != 19)
        {
            throw new InvalidDataException("Unsupported ZEXC CPU extension length.");
        }

        using var reader = CreateReader(body);
        ushort version = reader.ReadUInt16();
        if (version != 1)
        {
            throw new InvalidDataException($"Unsupported ZEXC version {version}.");
        }

        ulong cycles = reader.ReadUInt64();
        int frameCounter = reader.ReadInt32();
        byte iffDelay = reader.ReadByte();
        byte interruptData = reader.ReadByte();
        byte flags = reader.ReadByte();
        return new CpuExtension(
            cycles,
            frameCounter,
            iffDelay,
            interruptData,
            (flags & 1) != 0,
            (flags & 2) != 0,
            reader.ReadByte(),
            reader.ReadByte());
    }

    private static void WriteInterface1Extension(BinaryWriter writer, SpectrumInterface1Snapshot snapshot)
    {
        writer.Write((ushort)1);
        writer.Write(snapshot.Device != null);
        if (snapshot.Device != null)
        {
            SpectrumInterface1DeviceState device = snapshot.Device;
            writer.Write(device.IsPaged);
            writer.Write(device.Control);
            writer.Write(device.NetworkOutput);
            writer.Write(device.MotorMask);
            writer.Write((byte)device.Activity);
            writer.Write((byte)device.Drives.Count);
            foreach (MicrodriveTransportState drive in device.Drives)
            {
                writer.Write(drive.HeadPosition);
                writer.Write(drive.Transferred);
                writer.Write(drive.MaximumTransfer);
                writer.Write(drive.Gap);
                writer.Write(drive.Sync);
                writer.Write(drive.LastByte);
            }
        }

        writer.Write((byte)snapshot.Media.Slots.Count);
        foreach (SpectrumInterface1MediaSlotState slot in snapshot.Media.Slots)
        {
            WriteNullableString(writer, slot.BackingPath);
            writer.Write(slot.Cartridge != null);
            if (slot.Cartridge == null)
            {
                continue;
            }

            MicrodriveCartridgeState cartridge = slot.Cartridge;
            writer.Write(cartridge.SectorCount);
            writer.Write((byte)((cartridge.WriteProtected ? 1 : 0) | (cartridge.Modified ? 2 : 0)));
            WriteByteArray(writer, cartridge.CopyData());
            WriteByteArray(writer, cartridge.CopyPreambleState());
        }
    }

    private static SpectrumInterface1Snapshot ReadInterface1Extension(byte[] body)
    {
        using var reader = CreateReader(body);
        ushort version = reader.ReadUInt16();
        if (version != 1)
        {
            throw new InvalidDataException($"Unsupported ZEI1 version {version}.");
        }

        SpectrumInterface1DeviceState? device = null;
        if (reader.ReadBoolean())
        {
            bool paged = reader.ReadBoolean();
            byte control = reader.ReadByte();
            byte network = reader.ReadByte();
            byte motor = reader.ReadByte();
            byte activityValue = reader.ReadByte();
            if (!Enum.IsDefined(typeof(MicrodriveActivityState), (int)activityValue))
            {
                throw new InvalidDataException("Invalid Interface 1 activity state.");
            }

            int driveCount = reader.ReadByte();
            if (driveCount != SpectrumInterface1Device.DriveCount)
            {
                throw new InvalidDataException("ZEI1 must contain eight drive transports.");
            }

            var drives = new MicrodriveTransportState[driveCount];
            for (int drive = 0; drive < drives.Length; drive++)
            {
                drives[drive] = new MicrodriveTransportState(
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    reader.ReadByte());
            }

            device = new SpectrumInterface1DeviceState(
                paged,
                control,
                network,
                motor,
                (MicrodriveActivityState)activityValue,
                drives);
        }

        int slotCount = reader.ReadByte();
        if (slotCount != SpectrumInterface1Device.DriveCount)
        {
            throw new InvalidDataException("ZEI1 must contain eight media slots.");
        }

        var slots = new SpectrumInterface1MediaSlotState[slotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            string? path = ReadNullableString(reader);
            MicrodriveCartridgeState? cartridge = null;
            if (reader.ReadBoolean())
            {
                int sectors = reader.ReadInt32();
                byte flags = reader.ReadByte();
                byte[] data = ReadByteArray(reader, 16 * 1024 * 1024);
                byte[] preamble = ReadByteArray(reader, 1024 * 1024);
                cartridge = new MicrodriveCartridgeState(
                    sectors,
                    data,
                    preamble,
                    (flags & 1) != 0,
                    (flags & 2) != 0);
            }

            slots[slot] = new SpectrumInterface1MediaSlotState(path, cartridge);
        }

        if (reader.BaseStream.Position != reader.BaseStream.Length)
        {
            throw new InvalidDataException("ZEI1 contains unexpected trailing data.");
        }

        return new SpectrumInterface1Snapshot(new SpectrumInterface1MediaSnapshot(slots), device);
    }

    private static SpectrumInterface1Snapshot CreateEmptyInterface1Snapshot(bool paged)
    {
        var slots = new SpectrumInterface1MediaSlotState[SpectrumInterface1Device.DriveCount];
        var drives = new MicrodriveTransportState[SpectrumInterface1Device.DriveCount];
        for (int drive = 0; drive < drives.Length; drive++)
        {
            slots[drive] = new SpectrumInterface1MediaSlotState(null, null);
            drives[drive] = new MicrodriveTransportState(0, 0, MicrodriveCartridge.HeaderLength, 15, 15, 0xFF);
        }

        var device = new SpectrumInterface1DeviceState(
            paged,
            0,
            0,
            0,
            MicrodriveActivityState.Idle,
            drives);
        return new SpectrumInterface1Snapshot(new SpectrumInterface1MediaSnapshot(slots), device);
    }

    private static byte[][] BuildInternalRamBanks(SpectrumModel model, IReadOnlyDictionary<int, byte[]> pages)
    {
        int bankCount = SpectrumModelTraits.RamBankCount(model);
        var banks = new byte[bankCount][];
        for (int bank = 0; bank < bankCount; bank++)
        {
            int page = InternalBankToSzxPage(model, bank);
            if (!pages.TryGetValue(page, out byte[]? data))
            {
                throw new InvalidDataException($"SZX snapshot is missing RAM page {page} required by {model}.");
            }

            banks[bank] = data;
        }

        return banks;
    }

    private static int InternalBankToSzxPage(SpectrumModel model, int bank)
    {
        return model switch
        {
            SpectrumModel.Spectrum16K when bank == 0 => 5,
            SpectrumModel.Spectrum48K => bank switch
            {
                0 => 5,
                1 => 2,
                2 => 0,
                _ => throw new ArgumentOutOfRangeException(nameof(bank))
            },
            _ => bank
        };
    }

    private static byte ModelToSzxId(SpectrumModel model) => model switch
    {
        SpectrumModel.Spectrum16K => 0,
        SpectrumModel.Spectrum48K => 1,
        SpectrumModel.Spectrum128K => 2,
        SpectrumModel.SpectrumPlus2 => 3,
        SpectrumModel.SpectrumPlus2A => 4,
        SpectrumModel.SpectrumPlus3 => 5,
        SpectrumModel.Pentagon128 => 7,
        SpectrumModel.Scorpion256 => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported SZX machine.")
    };

    private static SpectrumModel SzxIdToModel(byte id) => id switch
    {
        0 => SpectrumModel.Spectrum16K,
        1 => SpectrumModel.Spectrum48K,
        2 => SpectrumModel.Spectrum128K,
        3 => SpectrumModel.SpectrumPlus2,
        4 => SpectrumModel.SpectrumPlus2A,
        5 => SpectrumModel.SpectrumPlus3,
        7 => SpectrumModel.Pentagon128,
        10 => SpectrumModel.Scorpion256,
        _ => throw new InvalidDataException($"SZX machine ID {id} is not supported by this emulator.")
    };

    private static void WriteChunk(BinaryWriter writer, string id, Action<BinaryWriter> writeBody)
    {
        if (id.Length != 4)
        {
            throw new ArgumentException("SZX chunk IDs must contain four bytes.", nameof(id));
        }

        using var bodyStream = new MemoryStream();
        using (var body = new BinaryWriter(bodyStream, Encoding.UTF8, leaveOpen: true))
        {
            writeBody(body);
        }

        writer.Write(Encoding.ASCII.GetBytes(id));
        writer.Write(checked((uint)bodyStream.Length));
        writer.Write(bodyStream.GetBuffer(), 0, checked((int)bodyStream.Length));
    }

    private static BinaryReader CreateReader(byte[] body)
    {
        return new BinaryReader(new MemoryStream(body, writable: false), Encoding.UTF8, leaveOpen: false);
    }

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        byte[] data = reader.ReadBytes(count);
        if (data.Length != count)
        {
            throw new EndOfStreamException();
        }

        return data;
    }

    private static ushort Pair(byte high, byte low) => (ushort)((high << 8) | low);

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value != null);
        if (value == null)
        {
            return;
        }

        WriteByteArray(writer, Encoding.UTF8.GetBytes(value));
    }

    private static string? ReadNullableString(BinaryReader reader)
    {
        return reader.ReadBoolean()
            ? Encoding.UTF8.GetString(ReadByteArray(reader, 1024 * 1024))
            : null;
    }

    private static void WriteByteArray(BinaryWriter writer, byte[] data)
    {
        writer.Write(data.Length);
        writer.Write(data);
    }

    private static byte[] ReadByteArray(BinaryReader reader, int maximumLength)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > maximumLength || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("Invalid ZedExEss SZX extension array length.");
        }

        return ReadExactly(reader, length);
    }

    private sealed record CpuExtension(
        ulong Cycles,
        int FrameCounter,
        byte IffDelay,
        byte InterruptData,
        bool IntPending,
        bool NmiPending,
        byte Q,
        byte LastQ);
}
