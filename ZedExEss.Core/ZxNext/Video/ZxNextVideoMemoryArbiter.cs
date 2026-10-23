using ZedExEss.ZxNext.Memory;

using System.Runtime.CompilerServices;

namespace ZedExEss.ZxNext.Video;

/// <summary>
/// Models the read-only bank-5/bank-7 BRAM port shared by the ULA and tilemap.
/// </summary>
/// <remarks>
/// The pinned core gives a newly scheduled ULA read priority over a simultaneous
/// tilemap request. A granted read is published one clk28 cycle later; a denied
/// tilemap client must retain and resubmit its request, just as tilemap.vhd does.
/// Layer 2 deliberately does not use this arbiter because it has its own external
/// SRAM read port.
/// </remarks>
public sealed class ZxNextVideoMemoryArbiter
{
    private readonly ZxNextMemoryBus _memory;
    private ZxNextVideoMemoryOwner _pendingOwner;
    private byte _pendingData;

    internal ZxNextVideoMemoryArbiter(ZxNextMemoryBus memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        Reset();
    }

    public ZxNextVideoMemoryOwner ScheduledOwner { get; private set; }

    /// <summary>True when the preceding edge scheduled a read still to be published.</summary>
    internal bool HasPendingResponse => _pendingOwner != ZxNextVideoMemoryOwner.None;
    public bool UlaResponseValid { get; private set; }
    public byte UlaResponseData { get; private set; }
    public bool TilemapResponseValid { get; private set; }
    public byte TilemapResponseData { get; private set; }
    public ulong UlaGrantCount { get; private set; }
    public ulong TilemapGrantCount { get; private set; }
    public ulong TilemapStallCount { get; private set; }

    /// <summary>Advances the shared BRAM port by one clk28 service edge.</summary>
    internal void Clock(
        ulong masterTick,
        ZxNextVideoMemoryRequest? ulaRequest,
        ZxNextVideoMemoryRequest? tilemapRequest)
    {
        BeginClock();
        ScheduleClock(masterTick, ulaRequest, tilemapRequest);
    }

    /// <summary>Publishes the preceding edge's response before clients select new requests.</summary>
    internal void BeginClock()
    {
        PublishPendingResponse();
        ScheduledOwner = ZxNextVideoMemoryOwner.None;
        _pendingOwner = ZxNextVideoMemoryOwner.None;
    }

    /// <summary>Arbitrates requests selected after the preceding response was consumed.</summary>
    internal void ScheduleClock(
        ulong masterTick,
        ZxNextVideoMemoryRequest? ulaRequest,
        ZxNextVideoMemoryRequest? tilemapRequest)
    {
        if (ulaRequest is ZxNextVideoMemoryRequest ula)
        {
            ValidateRequest(ula);
            Schedule(ZxNextVideoMemoryOwner.Ula, ula, masterTick);
            UlaGrantCount++;
            if (tilemapRequest.HasValue)
            {
                ValidateRequest(tilemapRequest.Value);
                TilemapStallCount++;
            }

            return;
        }

        if (tilemapRequest is ZxNextVideoMemoryRequest tilemap)
        {
            ValidateRequest(tilemap);
            Schedule(ZxNextVideoMemoryOwner.Tilemap, tilemap, masterTick);
            TilemapGrantCount++;
        }
    }

    /// <summary>
    /// Schedules requests produced by the renderer's bounded ULA and tilemap address
    /// generators. Public diagnostic injection continues through ScheduleClock so an
    /// invalid synthetic request still fails immediately.
    /// </summary>
    internal void ScheduleTrustedClock(
        ulong masterTick,
        ZxNextVideoMemoryRequest? ulaRequest,
        ZxNextVideoMemoryRequest? tilemapRequest)
    {
        if (ulaRequest is ZxNextVideoMemoryRequest ula)
        {
            Schedule(ZxNextVideoMemoryOwner.Ula, ula, masterTick);
            UlaGrantCount++;
            if (tilemapRequest.HasValue)
            {
                TilemapStallCount++;
            }

            return;
        }

        if (tilemapRequest is ZxNextVideoMemoryRequest tilemap)
        {
            Schedule(ZxNextVideoMemoryOwner.Tilemap, tilemap, masterTick);
            TilemapGrantCount++;
        }
    }

    /// <summary>BeginClock variant returning the tilemap response without property reads.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void BeginClock(out bool tilemapResponseValid, out byte tilemapResponseData)
    {
        ZxNextVideoMemoryOwner pending = _pendingOwner;
        bool ula = pending == ZxNextVideoMemoryOwner.Ula;
        bool tilemap = pending == ZxNextVideoMemoryOwner.Tilemap;
        UlaResponseValid = ula;
        TilemapResponseValid = tilemap;
        if (ula)
        {
            UlaResponseData = _pendingData;
        }

        if (tilemap)
        {
            TilemapResponseData = _pendingData;
        }

        tilemapResponseValid = tilemap;
        tilemapResponseData = TilemapResponseData;
        ScheduledOwner = ZxNextVideoMemoryOwner.None;
        _pendingOwner = ZxNextVideoMemoryOwner.None;
    }

    /// <summary>
    /// Packed trusted schedule. Returns whether the tilemap was granted, so the caller
    /// needs neither a nullable request nor a follow-up ScheduledOwner read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ScheduleTrustedPacked(ulong masterTick, int ulaPacked, int tilemapPacked)
    {
        if (ulaPacked >= 0)
        {
            SchedulePacked(ZxNextVideoMemoryOwner.Ula, ulaPacked, masterTick);
            UlaGrantCount++;
            if (tilemapPacked >= 0)
            {
                TilemapStallCount++;
            }

            return false;
        }

        if (tilemapPacked >= 0)
        {
            SchedulePacked(ZxNextVideoMemoryOwner.Tilemap, tilemapPacked, masterTick);
            TilemapGrantCount++;
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SchedulePacked(ZxNextVideoMemoryOwner owner, int packed, ulong masterTick)
    {
        ScheduledOwner = owner;
        _pendingOwner = owner;
        int offset = packed & 0xFFFF;
        int firstPage = (packed & 0x10000) == 0 ? 10 : 14;
        _pendingData = _memory.ReadDisplayRam(
            firstPage + (offset >> 13),
            offset & (ZxNextPhysicalMemory.PageSize - 1),
            masterTick);
    }

    internal void Reset()
    {
        ScheduledOwner = ZxNextVideoMemoryOwner.None;
        _pendingOwner = ZxNextVideoMemoryOwner.None;
        _pendingData = 0;
        UlaResponseValid = false;
        UlaResponseData = 0;
        TilemapResponseValid = false;
        TilemapResponseData = 0;
        UlaGrantCount = 0;
        TilemapGrantCount = 0;
        TilemapStallCount = 0;
    }

    private void PublishPendingResponse()
    {
        UlaResponseValid = _pendingOwner == ZxNextVideoMemoryOwner.Ula;
        TilemapResponseValid = _pendingOwner == ZxNextVideoMemoryOwner.Tilemap;
        if (UlaResponseValid)
        {
            UlaResponseData = _pendingData;
        }

        if (TilemapResponseValid)
        {
            TilemapResponseData = _pendingData;
        }
    }

    private void Schedule(
        ZxNextVideoMemoryOwner owner,
        ZxNextVideoMemoryRequest request,
        ulong masterTick)
    {
        ScheduledOwner = owner;
        _pendingOwner = owner;
        int firstPage = request.Bank == ZxNextVideoMemoryBank.Bank5 ? 10 : 14;
        int page = firstPage + (request.Offset / ZxNextPhysicalMemory.PageSize);
        int pageOffset = request.Offset & (ZxNextPhysicalMemory.PageSize - 1);
        _pendingData = _memory.ReadDisplayRam(page, pageOffset, masterTick);
    }

    private static void ValidateRequest(ZxNextVideoMemoryRequest request)
    {
        int length = request.Bank == ZxNextVideoMemoryBank.Bank5
            ? 16 * 1024
            : 8 * 1024;
        if ((uint)request.Offset >= (uint)length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"{request.Bank} offset ${request.Offset:X} is outside its video BRAM window.");
        }
    }
}

public readonly record struct ZxNextVideoMemoryRequest(
    ZxNextVideoMemoryBank Bank,
    int Offset);

public enum ZxNextVideoMemoryBank : byte
{
    Bank5,
    Bank7
}

public enum ZxNextVideoMemoryOwner : byte
{
    None,
    Ula,
    Tilemap
}
