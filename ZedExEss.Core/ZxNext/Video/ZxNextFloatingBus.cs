using ZedExEss.ZxNext.Core;
using ZedExEss.ZxNext.Memory;

using System.Runtime.CompilerServices;

namespace ZedExEss.ZxNext.Video;

/// <summary>Models the ULA fetch latch and +3 CPU-data fallback exposed by the pinned core.</summary>
public sealed class ZxNextFloatingBus
{
    private readonly ZxNextUlaTiming _timing;
    private readonly ZxNextPagingController _paging;
    private byte _ulaFetchValue = 0xFF;
    private byte _plus3CpuData = 0xFF;
    private bool _ulaDriving;

    internal ZxNextFloatingBus(ZxNextUlaTiming timing, ZxNextPagingController paging)
    {
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
        _paging = paging ?? throw new ArgumentNullException(nameof(paging));
    }

    public byte UlaFetchValue => _ulaFetchValue;
    public byte Plus3CpuData => _plus3CpuData;
    public bool UlaDriving => _ulaDriving;

    /// <summary>
    /// True when the latch already holds its released state, so a falling edge that
    /// carries no fetched byte cannot change anything the guest can observe.
    /// </summary>
    internal bool IsReleased => !_ulaDriving && _ulaFetchValue == 0xFF;

    /// <summary>Captures the latches the floating bus answers from.</summary>
    public ZxNextFloatingBusState CaptureState() => new(_ulaFetchValue, _plus3CpuData, _ulaDriving);

    /// <summary>Puts those latches back.</summary>
    public void RestoreState(ZxNextFloatingBusState state)
    {
        _ulaFetchValue = state.UlaFetchValue;
        _plus3CpuData = state.Plus3CpuData;
        _ulaDriving = state.UlaDriving;
    }

    public bool HandlesPort(ushort port)
    {
        ZxNextUlaTimingProfile profile = _timing.Profile;
        if (profile.IsPentagon)
        {
            return false;
        }

        return profile.IsPlus3
            ? (port & 0xF003) == 0x0001
            : (port & 0x00FF) == 0x00FF;
    }

    public byte Read(ushort port)
    {
        if (!HandlesPort(port))
        {
            return 0xFF;
        }

        ZxNextUlaTimingProfile profile = _timing.Profile;
        if (profile.IsPlus3)
        {
            if (_paging.PagingLocked)
            {
                return 0xFF;
            }

            return _ulaDriving ? (byte)(_ulaFetchValue | 0x01) : _plus3CpuData;
        }

        return _ulaDriving ? _ulaFetchValue : (byte)0xFF;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AdvanceUlaFallingEdge(
        int ulaHorizontal,
        int ulaVertical,
        byte? fetchedValue)
    {
        if (ulaHorizontal >= ZxNextUlaTimingProfile.DisplayWidth
            || (uint)ulaVertical >= ZxNextUlaTimingProfile.DisplayHeight)
        {
            _ulaFetchValue = 0xFF;
            _ulaDriving = false;
            return;
        }

        AdvanceDisplayUlaFallingEdge(ulaHorizontal, fetchedValue);
    }

    private void AdvanceDisplayUlaFallingEdge(int ulaHorizontal, byte? fetchedValue)
    {
        switch (ulaHorizontal & 0x0F)
        {
            case 0x01:
                _ulaFetchValue = 0xFF;
                _ulaDriving = false;
                break;

            case 0x09:
                if (fetchedValue.HasValue)
                {
                    _ulaFetchValue = fetchedValue.Value;
                    _ulaDriving = true;
                }
                break;

            case 0x0B:
            case 0x0D:
            case 0x0F:
                if (fetchedValue.HasValue)
                {
                    _ulaFetchValue = fetchedValue.Value;
                }
                break;
        }
    }

    internal void ObserveContendedCpuData(byte value) => _plus3CpuData = value;

    internal void Reset()
    {
        _ulaFetchValue = 0xFF;
        _plus3CpuData = 0xFF;
        _ulaDriving = false;
    }
}
