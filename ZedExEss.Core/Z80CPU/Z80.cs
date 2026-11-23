using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;

namespace ZedExEss.Z80CPU;

/// <summary>
/// Spectrum-specialized public Z80 type retained for source compatibility.
/// The inherited generic core is closed over concrete Spectrum buses, allowing
/// the JIT to generate a direct hot path without runtime machine-family tests.
/// </summary>
public sealed class Z80 : Z80Core<SpectrumMemory, SpectrumPortBus>
{
    public Z80(SpectrumMemory memory, SpectrumPortBus ports)
        : base(memory, ports)
    {
        // The ULA holds INT low for its whole pulse (32 T on the 48K, 36 on the 128K) and
        // the Z80 samples it as a level, so a handler that enables interrupts and returns
        // inside the pulse is entered again: IM 1 with EI; RET at $0038 is 13 + 4 + 10 = 27 T
        // and runs twice a frame on a 48K. Fuse 1.9.1 agrees (z80_interrupt accepts while
        // tstates < interrupt_length, and EI retries one instruction later).
        MaskableInterruptIsLevel = true;
    }
}
