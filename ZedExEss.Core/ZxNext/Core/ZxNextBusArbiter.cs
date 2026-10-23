using System.Runtime.CompilerServices;
using ZedExEss.ZxNext.Dma;

namespace ZedExEss.ZxNext.Core;

/// <summary>Tracks ownership of the Next's shared CPU/DMA bus on the clk28 timeline.</summary>
public sealed class ZxNextBusArbiter
{
    private readonly ZxNextDmaDevice _dma;

    internal ZxNextBusArbiter(ZxNextDmaDevice dma)
    {
        _dma = dma ?? throw new ArgumentNullException(nameof(dma));
        Reset();
    }

    public ZxNextBusOwner Owner { get; private set; }
    public bool DmaRequestPending => _dma.BusRequestAsserted;
    public bool CpuBusAcknowledged => Owner == ZxNextBusOwner.Dma;
    public ulong CpuOwnedMasterTicks { get; private set; }
    public ulong DmaOwnedMasterTicks { get; private set; }
    public ulong DmaGrantCount { get; private set; }
    public ulong CpuTstatesAtLastDmaGrant { get; private set; }

    internal bool TryGrantDma(ulong cpuTstates)
    {
        if (!_dma.BusRequestAsserted)
        {
            return false;
        }

        if (Owner != ZxNextBusOwner.Dma)
        {
            Owner = ZxNextBusOwner.Dma;
            DmaGrantCount++;
            CpuTstatesAtLastDmaGrant = cpuTstates;
        }

        return true;
    }

    internal void ReleaseDma()
    {
        if (Owner == ZxNextBusOwner.Dma)
        {
            Owner = ZxNextBusOwner.Cpu;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordCpuMasterTicks(ulong masterTicks)
    {
        if (Owner != ZxNextBusOwner.Cpu)
        {
            throw new InvalidOperationException("The CPU advanced while DMA owned the shared bus.");
        }

        CpuOwnedMasterTicks += masterTicks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordDmaMasterTicks(ulong masterTicks)
    {
        if (Owner != ZxNextBusOwner.Dma)
        {
            throw new InvalidOperationException("DMA advanced without a shared-bus grant.");
        }

        DmaOwnedMasterTicks += masterTicks;
    }

    /// <summary>Captures who owns the bus and the grant tallies.</summary>
    public ZxNextBusArbiterState CaptureState() => new(
        Owner,
        CpuOwnedMasterTicks,
        DmaOwnedMasterTicks,
        DmaGrantCount,
        CpuTstatesAtLastDmaGrant);

    /// <summary>
    /// Puts ownership back, which has to agree with the DMA controller's own state.
    /// </summary>
    /// <remarks>
    /// Restored after the controller for that reason: this says DMA holds the bus, and the
    /// first thing the machine does with a held bus is ask the controller which endpoint
    /// cycle it owes.
    /// </remarks>
    public void RestoreState(ZxNextBusArbiterState state)
    {
        Owner = state.Owner;
        CpuOwnedMasterTicks = state.CpuOwnedMasterTicks;
        DmaOwnedMasterTicks = state.DmaOwnedMasterTicks;
        DmaGrantCount = state.DmaGrantCount;
        CpuTstatesAtLastDmaGrant = state.CpuTstatesAtLastDmaGrant;
    }

    internal void Reset()
    {
        Owner = ZxNextBusOwner.Cpu;
        CpuOwnedMasterTicks = 0;
        DmaOwnedMasterTicks = 0;
        DmaGrantCount = 0;
        CpuTstatesAtLastDmaGrant = 0;
    }
}

public enum ZxNextBusOwner : byte
{
    Cpu,
    Dma
}
