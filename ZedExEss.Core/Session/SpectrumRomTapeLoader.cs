using ZedExEss.FileHandlers;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Core;

/// <summary>
/// Implements the standard-ROM LD_BYTES trap shared by desktop hosts. It validates the actual
/// ROM routine signature before injecting a standard tape block, so paging unrelated ROM code
/// over the same logical address cannot trigger the trap accidentally.
/// </summary>
public static class SpectrumRomTapeLoader
{
    private static ReadOnlySpan<byte> LdBytesPrefix => [0x08, 0x15, 0xF3, 0x3E, 0x0F, 0xD3, 0xFE, 0x21];
    private static ReadOnlySpan<byte> LdBytesSuffix => [0xE5, 0xDB, 0xFE, 0x1F, 0xE6, 0x20, 0xF6, 0x02];

    /// <summary>Returns true when the current instruction was replaced by a successful trap.</summary>
    public static bool TryFlashLoad(SpectrumMachine machine, TzxLoader loader)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(loader);

        Z80 cpu = machine.Cpu;
        SpectrumMemory memory = machine.Memory;
        if (!IsLdBytesEntry(cpu, memory))
        {
            return false;
        }

        int blockIndex = loader.CurrentBlockIndex;
        if (!TryGetStandardBlockAt(loader, blockIndex, out TapeStandardBlock block))
        {
            return false;
        }

        ushort requestedLength = (ushort)((cpu.D << 8) | cpu.E);
        if (block.Data.Length != requestedLength + 2)
        {
            return false;
        }

        if (loader.IsPlaying)
        {
            loader.Stop();
        }

        ExecuteFlashLoad(cpu, memory, block.Data, requestedLength);
        cpu.PC = 0x05E2;
        cpu.SetHalted(false);

        int nextBlock = block.Index + 1;
        if (nextBlock < loader.Blocks.Count)
        {
            loader.JumpToNextPlayableBlock(nextBlock, play: true);
        }

        return true;
    }

    /// <summary>
    /// Returns the first standard-format payload without exposing the loader's internal block
    /// implementation types across the core/frontend assembly boundary.
    /// </summary>
    public static bool TryGetFirstStandardBlock(TzxLoader loader, out byte[] data)
    {
        ArgumentNullException.ThrowIfNull(loader);
        foreach (ITzxBlock block in loader.Blocks)
        {
            switch (block)
            {
                case StdData standard:
                    data = standard.Data;
                    return true;
                case TapBlock tap:
                    data = tap.Data;
                    return true;
            }
        }

        data = [];
        return false;
    }

    /// <summary>Identifies the unmodified Spectrum ROM LD_BYTES entry window.</summary>
    public static bool IsLdBytesEntry(Z80 cpu, SpectrumMemory memory)
    {
        ushort pc = cpu.PC;
        if (pc < 0x0558 || pc > 0x0567)
        {
            return false;
        }

        const ushort start = 0x0557;
        ReadOnlySpan<byte> prefix = LdBytesPrefix;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (memory.ReadDirect((ushort)(start + i)) != prefix[i])
            {
                return false;
            }
        }

        ReadOnlySpan<byte> suffix = LdBytesSuffix;
        for (int i = 0; i < suffix.Length; i++)
        {
            if (memory.ReadDirect((ushort)(start + 10 + i)) != suffix[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetStandardBlockAt(
        TzxLoader loader,
        int index,
        out TapeStandardBlock block)
    {
        block = default;
        if (loader.Blocks.Count == 0)
        {
            return false;
        }

        int start = Math.Clamp(index, 0, loader.Blocks.Count - 1);
        for (int i = start; i < loader.Blocks.Count; i++)
        {
            switch (loader.Blocks[i])
            {
                case StdData standard:
                    block = new TapeStandardBlock(i, standard.Data);
                    return true;
                case TapBlock tap:
                    block = new TapeStandardBlock(i, tap.Data);
                    return true;
            }
        }

        return false;
    }

    private static void ExecuteFlashLoad(
        Z80 cpu,
        SpectrumMemory memory,
        ReadOnlySpan<byte> data,
        ushort requestedLength)
    {
        int length = data.Length;
        int read = Math.Min(length - 1, requestedLength);
        if (length == 0)
        {
            cpu.L = 1;
            cpu.F_ = 1;
            ClearCarry(cpu);
            return;
        }

        bool verify = (cpu.F_ & 0x01) == 0;
        byte flag = cpu.A_;
        cpu.A = 0;
        byte parity = data[0];
        cpu.L = parity;

        if (requestedLength == 0)
        {
            cpu.B = 0xB0;
            cpu.A = parity;
            cpu.SetFlags(CpFlags(cpu.A, 1));
            cpu.C = 1;
            cpu.H = parity;
            return;
        }

        cpu.A_ = 0x01;
        cpu.F_ = 0x45;
        bool error = parity != flag;
        int bytesRead = 0;

        if (!error)
        {
            if (read > 0)
            {
                cpu.L = data[read];
            }

            if (verify)
            {
                for (int i = 0; i < read; i++)
                {
                    byte value = data[i + 1];
                    parity ^= value;
                    if (value != memory.ReadDirect((ushort)(cpu.IX + i)))
                    {
                        cpu.L = value;
                        error = true;
                        break;
                    }

                    bytesRead = i + 1;
                }
            }
            else
            {
                for (int i = 0; i < read; i++)
                {
                    byte value = data[i + 1];
                    parity ^= value;
                    memory.WriteDirect((ushort)(cpu.IX + i), value);
                    bytesRead = i + 1;
                }
            }

            if (!error)
            {
                if (requestedLength == bytesRead && read + 1 < length)
                {
                    parity ^= data[read + 1];
                    cpu.A = parity;
                    cpu.SetFlags(CpFlags(cpu.A, 1));
                    cpu.B = 0xB0;
                }
                else
                {
                    cpu.B = 0;
                    cpu.L = 1;
                    error = true;
                }
            }
        }

        if (error)
        {
            ClearCarry(cpu);
        }

        cpu.C = 1;
        cpu.H = parity;
        ushort newDe = (ushort)(requestedLength - bytesRead);
        cpu.D = (byte)(newDe >> 8);
        cpu.E = (byte)newDe;
        cpu.IX = (ushort)(cpu.IX + bytesRead);
    }

    private static void ClearCarry(Z80 cpu)
    {
        cpu.SetFlags((byte)(cpu.GetFlags() & 0xFE));
    }

    private static byte CpFlags(byte a, byte value)
    {
        int result = a - value;
        byte r = (byte)result;
        byte flags = 0x02;
        if ((r & 0x80) != 0) flags |= 0x80;
        if (r == 0) flags |= 0x40;
        if (((a ^ value ^ r) & 0x10) != 0) flags |= 0x10;
        if (((a ^ value) & (a ^ r) & 0x80) != 0) flags |= 0x04;
        if (result < 0) flags |= 0x01;
        flags |= (byte)(r & 0x28);
        return flags;
    }

    private readonly record struct TapeStandardBlock(int Index, byte[] Data);
}
