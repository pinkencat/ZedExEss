using ZedExEss.Z80CPU;

namespace ZedExEss.Zx8x.Core;

/// <summary>Z80 core closed over concrete ZX80/ZX81 memory and port buses.</summary>
public sealed class Zx8xCpu : Z80Core<Zx8xCpuMemoryBus, Zx8xCpuPortBus>
{
    public Zx8xCpu(Zx8xCpuMemoryBus memory, Zx8xCpuPortBus ports)
        : base(memory, ports)
    {
        memory.AttachCpu(this);
        ports.AttachCpu(this);

        // With the no-op ZX8x contention provider enabled, the shared core samples
        // input at T4 and latches output after T1 instead of treating the complete
        // four-T-state I/O cycle as an indivisible operation.
        ConfigureIoContention(enabled: true);
    }
}
