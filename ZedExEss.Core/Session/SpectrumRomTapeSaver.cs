using System;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Tape;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Core;

/// <summary>
/// Implements the standard-ROM SA_BYTES trap, the saving counterpart of
/// <see cref="SpectrumRomTapeLoader"/>.
/// </summary>
/// <remarks>
/// Recording the MIC line works without this, but a ROM save spends five seconds writing
/// pilot tone before every block, which is a long time to watch. The trap takes the block
/// straight out of memory instead and returns through the ROM's own exit point, so the
/// BREAK check and border restore still happen exactly as they would have.
///
/// As with the load trap, the ROM routine's own bytes are checked before the trap fires:
/// another ROM paged over the same address must not be mistaken for the saver.
/// </remarks>
public static class SpectrumRomTapeSaver
{
    /// <summary>The entry point of SA_BYTES in the standard 48K BASIC ROM.</summary>
    private const ushort SaveEntry = 0x04C2;

    /// <summary>SA/LD_RET, which restores the border, checks BREAK and returns.</summary>
    private const ushort SaveReturn = 0x053F;

    private static ReadOnlySpan<byte> SaveBytesSignature =>
        [0x21, 0x3F, 0x05, 0xE5, 0x21, 0x80, 0x1F, 0xCB, 0x7F, 0x28, 0x03, 0x21, 0x98, 0x0C, 0x08, 0x13];

    /// <summary>Returns true when the save was taken over and the CPU redirected.</summary>
    public static bool TryFastSave(SpectrumMachine machine, SpectrumTapeRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(recorder);

        Z80 cpu = machine.Cpu;
        SpectrumMemory memory = machine.Memory;
        if (cpu.PC != SaveEntry || !MatchesSaveBytes(memory))
        {
            return false;
        }

        int length = (cpu.D << 8) | cpu.E;
        var block = new byte[length + 2];
        block[0] = cpu.A;

        byte parity = cpu.A;
        ushort address = cpu.IX;
        for (int i = 0; i < length; i++)
        {
            byte value = memory.ReadDirect(address);
            block[i + 1] = value;
            parity ^= value;
            address++;
        }

        block[^1] = parity;
        recorder.AddStandardBlock(block, pauseMilliseconds: 1000);

        // The caller's return address is already on the stack: SA_BYTES pushes SA/LD_RET
        // itself, so entering there directly leaves the stack exactly as the ROM would.
        cpu.PC = SaveReturn;
        cpu.SetHalted(false);
        return true;
    }

    private static bool MatchesSaveBytes(SpectrumMemory memory)
    {
        ReadOnlySpan<byte> signature = SaveBytesSignature;
        for (int i = 0; i < signature.Length; i++)
        {
            if (memory.ReadDirect((ushort)(SaveEntry + i)) != signature[i])
            {
                return false;
            }
        }

        return true;
    }
}
