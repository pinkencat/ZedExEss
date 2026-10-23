using ZedExEss.Z80CPU;
using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Memory;

namespace ZedExEss.FileHandlers;

/// <summary>Restores a 48K SNA/SNX snapshot into a ZX Spectrum Next machine.</summary>
public static class ZxNextSnapshotLoader
{
    public const int HeaderLength = 27;
    public const int RamLength = 48 * 1024;
    public const int SnapshotLength = HeaderLength + RamLength;

    public static void Load48K(ZxNextMachine machine, byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Load48K(machine, snapshot.AsSpan());
    }

    public static void Load48K(ZxNextMachine machine, ReadOnlySpan<byte> snapshot)
    {
        ArgumentNullException.ThrowIfNull(machine);
        if (snapshot.Length != SnapshotLength)
        {
            throw new InvalidDataException(
                $"A 48K SNA/SNX snapshot must be exactly {SnapshotLength} bytes.");
        }

        machine.Reset(ZxNextResetKind.Soft);
        machine.MemoryBus.DisableBootRom();
        machine.Paging.WritePort7Ffd(0);
        machine.MemoryBus.SetMmuPage(0, ZxNextMemoryBus.RomPageSentinel);
        machine.MemoryBus.SetMmuPage(1, ZxNextMemoryBus.RomPageSentinel);
        machine.MemoryBus.SetMmuPage(2, 10);
        machine.MemoryBus.SetMmuPage(3, 11);
        machine.MemoryBus.SetMmuPage(4, 4);
        machine.MemoryBus.SetMmuPage(5, 5);
        machine.MemoryBus.SetMmuPage(6, 0);
        machine.MemoryBus.SetMmuPage(7, 1);

        LoadBank(machine, snapshot.Slice(HeaderLength, 0x4000), bank: 5);
        LoadBank(machine, snapshot.Slice(HeaderLength + 0x4000, 0x4000), bank: 2);
        LoadBank(machine, snapshot.Slice(HeaderLength + 0x8000, 0x4000), bank: 0);

        int index = 0;
        byte i = snapshot[index++];
        byte alternateL = snapshot[index++];
        byte alternateH = snapshot[index++];
        byte alternateE = snapshot[index++];
        byte alternateD = snapshot[index++];
        byte alternateC = snapshot[index++];
        byte alternateB = snapshot[index++];
        byte alternateF = snapshot[index++];
        byte alternateA = snapshot[index++];
        byte l = snapshot[index++];
        byte h = snapshot[index++];
        byte e = snapshot[index++];
        byte d = snapshot[index++];
        byte c = snapshot[index++];
        byte b = snapshot[index++];
        ushort iy = ReadWord(snapshot, ref index);
        ushort ix = ReadWord(snapshot, ref index);
        byte iffState = snapshot[index++];
        byte r = snapshot[index++];
        byte f = snapshot[index++];
        byte a = snapshot[index++];
        ushort sp = ReadWord(snapshot, ref index);
        byte interruptMode = snapshot[index++];
        byte border = snapshot[index];

        ushort pc = (ushort)(machine.MemoryBus.ReadDirect(sp)
            | (machine.MemoryBus.ReadDirect((ushort)(sp + 1)) << 8));
        sp += 2;
        Z80SnapshotState initial = machine.Cpu.CaptureSnapshotState();
        machine.Cpu.RestoreSnapshotState(initial with
        {
            Cycles = 0,
            PC = pc,
            SP = sp,
            IX = ix,
            IY = iy,
            MemPtr = 0,
            A = a,
            F = f,
            B = b,
            C = c,
            D = d,
            E = e,
            H = h,
            L = l,
            AlternateA = alternateA,
            AlternateF = alternateF,
            AlternateB = alternateB,
            AlternateC = alternateC,
            AlternateD = alternateD,
            AlternateE = alternateE,
            AlternateH = alternateH,
            AlternateL = alternateL,
            I = i,
            R = r,
            InterruptMode = (byte)(interruptMode & 0x03),
            Iff1 = (iffState & 0x04) != 0,
            Iff2 = (iffState & 0x04) != 0,
            Halted = false,
            IffDelay = 0,
            InterruptData = 0,
            IntPending = false,
            NmiPending = false,
            Q = 0,
            LastQ = 0
        });
        machine.PortBus.WriteUncontended(0x00FE, (byte)(border & 0x07));
        machine.Video.RenderFrame(0);
    }

    private static void LoadBank(ZxNextMachine machine, ReadOnlySpan<byte> data, int bank)
    {
        for (int offset = 0; offset < data.Length; offset++)
        {
            int page = (bank * 2) + (offset / ZxNextPhysicalMemory.PageSize);
            machine.MemoryBus.WritePhysicalRam(
                page,
                offset & (ZxNextPhysicalMemory.PageSize - 1),
                data[offset]);
        }
    }

    private static ushort ReadWord(ReadOnlySpan<byte> data, ref int index)
    {
        ushort value = (ushort)(data[index] | (data[index + 1] << 8));
        index += 2;
        return value;
    }
}
