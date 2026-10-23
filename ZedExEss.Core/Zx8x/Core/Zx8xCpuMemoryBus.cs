using System.Runtime.CompilerServices;
using ZedExEss.Z80CPU;
using ZedExEss.Zx8x.Memory;

namespace ZedExEss.Zx8x.Core;

/// <summary>
/// ZX8x specialization of the Z80 memory bus, including M1 display-file opcode
/// substitution. Ordinary reads remain the responsibility of <see cref="Zx8xMemory"/>.
/// </summary>
public sealed class Zx8xCpuMemoryBus(Zx8xMemory memory) : IZ80MemoryBus, IZ80RefreshObserver
{
    private readonly Zx8xMemory _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    private Zx8xCpu? _cpu;
    private IZx8xDisplayFetchSink? _displaySink;
    private bool _hasDisplaySink;
    private bool _refreshIntAssertionPending;

    internal void AttachCpu(Zx8xCpu cpu)
    {
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
    }

    public void ConfigureDisplaySink(IZx8xDisplayFetchSink? sink)
    {
        _displaySink = sink;
        _hasDisplaySink = sink != null;
    }

    /// <summary>
    /// Makes a refresh-generated /INT assertion visible after the instruction
    /// whose M1 refresh produced it. The Z80 samples INT before that refresh
    /// address has reached the external bus, so accepting it at the same
    /// boundary makes each ZX8x display scanline four T-states too short.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CommitRefreshInterruptAssertion()
    {
        if (!_refreshIntAssertionPending)
        {
            return;
        }

        _refreshIntAssertionPending = false;
        _cpu!.Z80SetINTLine(true, 0xFF);
    }

    internal void ResetRefreshInterruptLine()
    {
        _refreshIntAssertionPending = false;
        _cpu?.Z80SetINTLine(false, 0xFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Read(ushort address) => _memory.Read(address);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteCpu(ushort address, byte value) => _memory.Write(address, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte FetchOpcode(ushort address)
    {
        byte value = _memory.Read(address);
        if ((address & 0x8000) == 0 || (value & 0x40) != 0)
        {
            return value;
        }

        // With A15 high, a display byte whose bit 6 is clear is consumed by the
        // video hardware and the CPU data bus receives a NOP. Bytes with bit 6 set
        // remain real opcodes; 76h therefore terminates a display line via HALT.
        if (_hasDisplaySink)
        {
            Zx8xCpu cpu = _cpu ?? throw new InvalidOperationException("ZX8x CPU bus is not attached to a CPU.");
            var fetch = new Zx8xDisplayFetch(cpu.Cyc, address, value, cpu.I, cpu.R);
            _displaySink!.OnDisplayFetch(in fetch);
        }

        return 0x00;
    }

    /// <summary>
    /// Loading R does not itself place the new value on the refresh-address bus.
    /// The external interrupt level is therefore updated by the next M1 refresh,
    /// not by the LD R instruction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnRefreshRegisterLoaded(byte refreshRegister)
    {
        // Deliberately empty. See the method summary above.
    }

    /// <summary>
    /// Drives the ZX80/ZX81 maskable-interrupt input from refresh address A6.
    /// A6 is physically wired to /INT, so the request remains level-sensitive.
    /// Deassertion can take effect immediately, but a newly asserted level is
    /// committed after this instruction: the CPU's sampling point precedes the
    /// refresh phase which placed the new R value on A0-A6.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnRefresh(byte refreshRegister)
    {
        bool bit6 = (refreshRegister & 0x40) != 0;
        if (bit6)
        {
            _refreshIntAssertionPending = false;
            _cpu!.Z80SetINTLine(false, 0xFF);
            return;
        }

        _refreshIntAssertionPending = true;
    }
}
