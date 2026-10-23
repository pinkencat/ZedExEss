using System.Buffers.Binary;
using System.Text;
using ZedExEss.Z80CPU;
using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Memory;

namespace ZedExEss.FileHandlers;

/// <summary>Transactional loader for standard NEX bank and loading-screen images.</summary>
/// <remarks>
/// The parser consumes and validates the complete image before changing the
/// machine. Appended-file variants are rejected explicitly until their payload
/// contract is implemented; V1.0-V1.3 screen and bank payloads use the same order
/// as the supplied System/Next <c>.nexload</c> command.
/// </remarks>
public static class ZxNextNexLoader
{
    public const int HeaderLength = 512;
    public const int BankLength = 16 * 1024;
    private const int BankFlagsOffset = 18;
    private const int BankFlagCount = 112;

    private static readonly int[] PayloadBankOrder = CreatePayloadBankOrder();

    public static ZxNextNexLoadResult Load(ZxNextMachine machine, byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Load(machine, image.AsSpan());
    }

    public static ZxNextNexLoadResult Load(ZxNextMachine machine, ReadOnlySpan<byte> image)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ParsedNex parsed = Parse(image);

        if (!parsed.DontResetNextRegisters)
        {
            machine.Reset(ZxNextResetKind.Soft);
            ApplyNexLoaderDefaults(machine);
        }

        machine.MemoryBus.DisableBootRom();
        machine.Paging.UnlockPaging();
        machine.Paging.WritePort1Ffd(0);
        machine.Paging.WritePort7Ffd(0);
        machine.MemoryBus.SetMmuPage(0, ZxNextMemoryBus.RomPageSentinel);
        machine.MemoryBus.SetMmuPage(1, ZxNextMemoryBus.RomPageSentinel);
        machine.MemoryBus.SetMmuPage(2, 10);
        machine.MemoryBus.SetMmuPage(3, 11);
        machine.MemoryBus.SetMmuPage(4, 4);
        machine.MemoryBus.SetMmuPage(5, 5);

        ApplyLoadingScreen(machine, parsed.LoadingScreen);

        foreach ((int bank, byte[] data) in parsed.Banks)
        {
            LoadBank(machine, bank, data);
        }

        int entryPage = parsed.EntryBank * 2;
        machine.MemoryBus.SetMmuPage(6, (byte)entryPage);
        machine.MemoryBus.SetMmuPage(7, (byte)(entryPage + 1));
        Z80SnapshotState initial = machine.Cpu.CaptureSnapshotState();
        // NextZXOS .nexload reaches the program through RST $20 after loading
        // HL with PC. With no retained file handle it also supplies BC=0, while
        // IY continues to identify the ROM system variables for immediate RST $08
        // calls (Warhawk performs M_GETSETDRV before completing its initialization).
        byte entryAccumulator = (byte)(
            (parsed.ProgramCounter >> 8) | (parsed.ProgramCounter & 0xFF));
        machine.Cpu.RestoreSnapshotState(initial with
        {
            Cycles = 0,
            PC = parsed.ProgramCounter,
            SP = parsed.StackPointer,
            IY = 0x5C3A,
            MemPtr = 0,
            A = entryAccumulator,
            F = GetOrFlags(entryAccumulator),
            B = 0,
            C = 0,
            H = (byte)(parsed.ProgramCounter >> 8),
            L = (byte)parsed.ProgramCounter,
            Iff1 = false,
            Iff2 = false,
            Halted = false,
            IffDelay = 0,
            InterruptData = 0,
            IntPending = false,
            NmiPending = false,
            Q = 0,
            LastQ = 0
        });
        machine.PortBus.WriteUncontended(0x00FE, parsed.BorderColor);
        machine.Video.RenderFrame(0);
        return new ZxNextNexLoadResult(
            parsed.Version,
            parsed.ProgramCounter,
            parsed.StackPointer,
            parsed.EntryBank,
            parsed.Banks.Count,
            parsed.DontResetNextRegisters);
    }

    private static byte GetOrFlags(byte value)
    {
        byte flags = (byte)(value & 0xA8);
        if (value == 0)
        {
            flags |= 0x40;
        }

        byte folded = value;
        folded ^= (byte)(folded >> 4);
        folded ^= (byte)(folded >> 2);
        folded ^= (byte)(folded >> 1);
        if ((folded & 1) == 0)
        {
            flags |= 0x04;
        }

        return flags;
    }

    private static ParsedNex Parse(ReadOnlySpan<byte> image)
    {
        if (image.Length < HeaderLength)
        {
            throw new InvalidDataException("The NEX image is shorter than its 512-byte header.");
        }

        if (!image[..4].SequenceEqual("Next"u8))
        {
            throw new InvalidDataException("The NEX image does not begin with the 'Next' signature.");
        }

        string version = Encoding.ASCII.GetString(image.Slice(4, 4));
        if (version is not ("V1.0" or "V1.1" or "V1.2" or "V1.3"))
        {
            throw new NotSupportedException($"NEX version '{version}' is not supported.");
        }

        byte loadingScreenFlags = image[10];
        if ((loadingScreenFlags & 0x60) != 0)
        {
            throw new NotSupportedException(
                $"NEX loading-screen flags ${loadingScreenFlags:X2} contain reserved mode bits.");
        }

        ushort appendedFiles = BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(16, 2));
        if (appendedFiles != 0)
        {
            throw new NotSupportedException(
                "NEX images with appended files are not implemented by the host loader.");
        }

        var banks = new List<(int Bank, byte[] Data)>();
        int payloadOffset = HeaderLength;
        ParsedLoadingScreen? loadingScreen = ParseLoadingScreen(
            image,
            loadingScreenFlags,
            ref payloadOffset);
        foreach (int bank in PayloadBankOrder)
        {
            if (image[BankFlagsOffset + bank] == 0)
            {
                continue;
            }

            if (payloadOffset + BankLength > image.Length)
            {
                throw new InvalidDataException(
                    $"NEX bank {bank} is flagged but its 16 KiB payload is truncated.");
            }

            banks.Add((bank, image.Slice(payloadOffset, BankLength).ToArray()));
            payloadOffset += BankLength;
        }

        if (banks.Count != image[9])
        {
            throw new InvalidDataException(
                $"NEX header declares {image[9]} banks but flags {banks.Count}.");
        }

        if (payloadOffset != image.Length)
        {
            throw new InvalidDataException(
                $"NEX image contains {image.Length - payloadOffset} unclaimed payload bytes.");
        }

        byte entryBank = version is "V1.2" or "V1.3" ? image[139] : (byte)0;
        if (entryBank >= BankFlagCount)
        {
            throw new InvalidDataException($"NEX entry bank {entryBank} is outside the supported range.");
        }

        return new ParsedNex(
            version,
            BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(14, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(image.Slice(12, 2)),
            entryBank,
            image[134] != 0,
            (byte)(image[11] & 0x07),
            loadingScreen,
            banks);
    }

    private static ParsedLoadingScreen? ParseLoadingScreen(
        ReadOnlySpan<byte> image,
        byte flags,
        ref int payloadOffset)
    {
        if ((flags & 0x1F) == 0)
        {
            return null;
        }

        byte[]? palette = null;
        if ((flags & 0x80) == 0 && (flags & 0x1A) == 0)
        {
            palette = ReadPayload(image, ref payloadOffset, 512, "loading-screen palette");
        }

        byte[]? layer2 = (flags & 0x01) != 0
            ? ReadPayload(image, ref payloadOffset, 3 * BankLength, "Layer 2 loading screen")
            : null;
        byte[]? ula = (flags & 0x02) != 0
            ? ReadPayload(image, ref payloadOffset, 0x1B00, "ULA loading screen")
            : null;
        byte[]? loRes = (flags & 0x04) != 0
            ? ReadPayload(image, ref payloadOffset, 0x3000, "LoRes loading screen")
            : null;
        byte[]? hiRes = (flags & 0x08) != 0
            ? ReadPayload(image, ref payloadOffset, 0x3000, "HiRes loading screen")
            : null;
        byte[]? hiColor = (flags & 0x10) != 0
            ? ReadPayload(image, ref payloadOffset, 0x3000, "HiColor loading screen")
            : null;

        return new ParsedLoadingScreen(flags, palette, layer2, ula, loRes, hiRes, hiColor);
    }

    private static byte[] ReadPayload(
        ReadOnlySpan<byte> image,
        ref int payloadOffset,
        int length,
        string role)
    {
        if (payloadOffset + length > image.Length)
        {
            throw new InvalidDataException($"The NEX {role} payload is truncated.");
        }

        byte[] payload = image.Slice(payloadOffset, length).ToArray();
        payloadOffset += length;
        return payload;
    }

    private static void ApplyNexLoaderDefaults(ZxNextMachine machine)
    {
        machine.PortBus.WriteNextRegister(0x07, 0x03);
        machine.PortBus.WriteNextRegister(0x12, 9);
        machine.PortBus.WriteNextRegister(0x13, 12);
        machine.PortBus.WriteNextRegister(0x14, 0xE3);
        machine.PortBus.WriteNextRegister(0x15, 0x01);
        machine.PortBus.WriteNextRegister(0x16, 0);
        machine.PortBus.WriteNextRegister(0x17, 0);
        machine.PortBus.WriteNextRegister(0x4A, 0);
        machine.PortBus.WriteNextRegister(0x4B, 0xE3);
    }

    private static void ApplyLoadingScreen(
        ZxNextMachine machine,
        ParsedLoadingScreen? loadingScreen)
    {
        if (loadingScreen is null)
        {
            return;
        }

        if (loadingScreen.Palette is not null)
        {
            machine.PortBus.WriteNextRegister(
                0x43,
                (loadingScreen.Flags & 0x04) != 0 ? (byte)0x01 : (byte)0x10);
            machine.PortBus.WriteNextRegister(0x40, 0);
            foreach (byte value in loadingScreen.Palette)
            {
                machine.PortBus.WriteNextRegister(0x44, value);
            }
        }

        if (loadingScreen.Layer2 is not null)
        {
            for (int bank = 0; bank < 3; bank++)
            {
                LoadBank(
                    machine,
                    9 + bank,
                    loadingScreen.Layer2.AsSpan(bank * BankLength, BankLength));
            }

            machine.PortBus.WriteUncontended(0x123B, 0x02);
        }

        ApplyLegacyLoadingScreen(machine, loadingScreen.Ula);
        ApplyLegacyLoadingScreen(machine, loadingScreen.LoRes);
        ApplyLegacyLoadingScreen(machine, loadingScreen.HiRes);
        ApplyLegacyLoadingScreen(machine, loadingScreen.HiColor);
    }

    private static void ApplyLegacyLoadingScreen(ZxNextMachine machine, byte[]? data)
    {
        if (data is null)
        {
            return;
        }

        LoadBankSegment(machine, bank: 5, data);
        machine.PortBus.WriteUncontended(0x123B, 0x00);
    }

    private static void LoadBank(ZxNextMachine machine, int bank, ReadOnlySpan<byte> data)
    {
        for (int offset = 0; offset < data.Length; offset++)
        {
            int page = (bank * 2) + (offset / ZxNextPhysicalMemory.PageSize);
            if (!machine.MemoryBus.WritePhysicalRam(
                    page,
                    offset & (ZxNextPhysicalMemory.PageSize - 1),
                    data[offset]))
            {
                throw new InvalidDataException(
                    $"NEX bank {bank} is not installed in the configured Next RAM.");
            }
        }
    }

    private static void LoadBankSegment(ZxNextMachine machine, int bank, ReadOnlySpan<byte> data)
    {
        if (data.Length > BankLength)
        {
            throw new ArgumentException("A partial NEX bank payload cannot exceed 16 KiB.", nameof(data));
        }

        for (int offset = 0; offset < data.Length; offset++)
        {
            int page = (bank * 2) + (offset / ZxNextPhysicalMemory.PageSize);
            if (!machine.MemoryBus.WritePhysicalRam(
                    page,
                    offset & (ZxNextPhysicalMemory.PageSize - 1),
                    data[offset]))
            {
                throw new InvalidDataException(
                    $"NEX bank {bank} is not installed in the configured Next RAM.");
            }
        }
    }

    private static int[] CreatePayloadBankOrder()
    {
        var order = new int[BankFlagCount];
        int index = 0;
        order[index++] = 5;
        order[index++] = 2;
        order[index++] = 0;
        order[index++] = 1;
        order[index++] = 3;
        order[index++] = 4;
        order[index++] = 6;
        order[index++] = 7;
        for (int bank = 8; bank < BankFlagCount; bank++)
        {
            order[index++] = bank;
        }

        return order;
    }

    private sealed record ParsedNex(
        string Version,
        ushort ProgramCounter,
        ushort StackPointer,
        byte EntryBank,
        bool DontResetNextRegisters,
        byte BorderColor,
        ParsedLoadingScreen? LoadingScreen,
        IReadOnlyList<(int Bank, byte[] Data)> Banks);

    private sealed record ParsedLoadingScreen(
        byte Flags,
        byte[]? Palette,
        byte[]? Layer2,
        byte[]? Ula,
        byte[]? LoRes,
        byte[]? HiRes,
        byte[]? HiColor);
}

public sealed record ZxNextNexLoadResult(
    string Version,
    ushort ProgramCounter,
    ushort StackPointer,
    byte EntryBank,
    int LoadedBankCount,
    bool PreservedNextRegisters);
