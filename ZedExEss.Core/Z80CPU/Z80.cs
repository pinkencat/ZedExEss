using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;

namespace ZedExEss.Z80CPU;

/// <summary>
/// Spectrum-specialized public Z80 type retained for source compatibility.
/// The inherited generic core is closed over concrete Spectrum buses, allowing
/// the JIT to generate a direct hot path without runtime machine-family tests.
/// </summary>
public sealed class Z80(SpectrumMemory memory, SpectrumPortBus ports)
    : Z80Core<SpectrumMemory, SpectrumPortBus>(memory, ports);
