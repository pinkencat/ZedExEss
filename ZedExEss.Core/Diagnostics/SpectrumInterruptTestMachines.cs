using ZedExEss.Spectrum.Core;

namespace ZedExEss.Diagnostics;

/// <summary>
/// Spectrum machines running a known IM 1 program from a ROM built for the purpose, for the
/// checks about when the CPU takes INT.
/// </summary>
/// <remarks>
/// A real ROM's handler at $0038 is far longer than any ULA pulse, so a short one has to be
/// put there; every ROM bank gets the same contents, so whichever one a model pages in at
/// reset runs it. The program parks the CPU in a HALT loop, so the interrupt is taken at a
/// HALT boundary less than 4 T into the pulse, and keeps its stack at $BFFE, uncontended on
/// every model, so nothing but the instructions decides where the handler ends.
/// </remarks>
internal static class SpectrumInterruptTestMachines
{
    /// <summary>Where the handler returns to: the JR after the HALT.</summary>
    internal const ushort LoopReturn = 0x000B;

    /// <summary>The handler's address.</summary>
    internal const ushort Handler = 0x0038;

    /// <summary>EI; RET: with the acknowledge 13 + 4 + 10 = 27 T, so it ends inside every pulse.</summary>
    internal static readonly byte[] ShortHandler = [0xFB, 0xC9];

    /// <summary>
    /// EI, four NOPs, RET: the first NOP ends inside the pulse, so the handler is interrupted
    /// inside itself, once.
    /// </summary>
    internal static readonly byte[] NestedHandler = [0xFB, 0x00, 0x00, 0x00, 0x00, 0xC9];

    /// <summary>Four NOPs, EI, RET: 43 T, past every pulse.</summary>
    internal static readonly byte[] LongHandler = [0x00, 0x00, 0x00, 0x00, 0xFB, 0xC9];

    /// <summary>
    /// NOP, EI, RET NZ (taken): exactly 32 T, so the RET ends past a 32 T pulse wherever the
    /// HALT left the CPU, and inside a 36 T one.
    /// </summary>
    internal static readonly byte[] PulseLengthHandler = [0x00, 0xFB, 0xC0];

    internal static SpectrumMachine Create(SpectrumModel model, byte[] handler) =>
        SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = model,
            Roms = BuildRom(model, handler),
            RenderEnabled = false
        });

    private static RomSet BuildRom(SpectrumModel model, byte[] handler)
    {
        // DI; LD SP,$C000; LD A,1; OR A (NZ, for RET NZ); IM 1; EI; HALT; JR to the HALT.
        byte[] program = [0xF3, 0x31, 0x00, 0xC0, 0x3E, 0x01, 0xB7, 0xED, 0x56, 0xFB, 0x76, 0x18, 0xFD];
        RomSet roms = RomSet.CreateBlank(SpectrumModelTraits.RomBankCount(model));
        for (int bank = 0; bank < roms.BankCount; bank++)
        {
            byte[] rom = roms.GetBankBytes(bank);
            program.CopyTo(rom, 0);
            handler.CopyTo(rom, Handler);
        }

        return roms;
    }
}
