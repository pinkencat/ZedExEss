using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.ZxNext.Core;

/// <summary>
/// Reproduces the pinned core's synthetic legacy-ULA wait window on the clk28 timeline.
/// </summary>
/// <remarks>
/// The Next uses dual-port video RAM, so these waits are compatibility behaviour rather
/// than physical RAM arbitration. zxnext.vhd enables them only at 3.5 MHz and classifies
/// the mapped 8 KiB physical page according to the selected legacy timing personality.
/// </remarks>
public sealed class ZxNextContentionController : IContentionProfile
{
    private readonly ZxNextClockCoordinator _clock;
    private readonly ZxNextRegisterFile _registers;
    private readonly ZxNextUlaTiming _timing;

    internal ZxNextContentionController(
        ZxNextClockCoordinator clock,
        ZxNextRegisterFile registers,
        ZxNextUlaTiming timing)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _registers = registers ?? throw new ArgumentNullException(nameof(registers));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
    }

    public bool Enabled => _clock.CpuSpeed == ZxNextCpuSpeed.Mhz3_5
        && !_registers.ContentionDisabled
        && !_timing.Profile.IsPentagon;

    public int TstatesPerFrame => checked((int)(_timing.Profile.MasterTicksPerFrame
        / ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate));

    public int GetMemoryDelay(ulong tstate) => GetMemoryDelayAtCurrentTick();

    public int GetNoMreqDelay(ulong tstate) =>
        _timing.Profile.IsPlus3 ? 0 : GetClockGateDelayAtCurrentTick();

    public bool IsUlaPort(ushort port) => IsPortContended(port);

    public bool IsPhysicalPageContended(byte physicalPage)
    {
        if (physicalPage >= 16)
        {
            return false;
        }

        byte timing = _timing.Profile.MachineTiming;
        if ((timing & 0x04) != 0)
        {
            return false;
        }

        return (timing & 0x03) switch
        {
            2 => (physicalPage & 0x02) != 0, // Odd 16 KiB banks.
            3 => (physicalPage & 0x08) != 0, // +3 banks 4-7.
            _ => (physicalPage & 0x0E) == 0x0A // 48K bank 5 only.
        };
    }

    public bool IsPortContended(ushort port)
    {
        if ((port & 0x0001) == 0)
        {
            return true;
        }

        // zxnext.vhd qualifies 7FFD contention with the active 128K/+3 timing
        // hardware, while the enabled ULA+ aliases use their full 16-bit ports.
        bool pagingTiming = !_timing.Profile.IsPentagon
            && (_timing.Profile.MachineTiming & 0x03) is 2 or 3;
        return (pagingTiming && (port & 0xC003) == 0x4001)
            || port == 0xBF3B
            || port == 0xFF3B;
    }

    /// <summary>Returns the source-derived delay for deterministic timing diagnostics.</summary>
    public int GetMemoryDelayAtMasterTick(ulong absoluteMasterTick, byte physicalPage)
    {
        if (!Enabled || !IsPhysicalPageContended(physicalPage))
        {
            return 0;
        }

        return CountWaitStates(GetMemorySampleTick(absoluteMasterTick));
    }

    private int GetMemoryDelayAtCurrentTick()
    {
        if (!Enabled)
        {
            return 0;
        }

        return CountWaitStates(GetMemorySampleTick(_clock.MasterTicks));
    }

    private int GetClockGateDelayAtCurrentTick()
    {
        if (!Enabled)
        {
            return 0;
        }

        return CountWaitStates(_clock.MasterTicks);
    }

    private ulong GetMemorySampleTick(ulong cycleStartMasterTick)
    {
        // 48K/128K contention holds the generated CPU clock high at the start of
        // the machine cycle. The +3 personality instead drives T80 WAIT: MREQ is
        // asserted after T1 and WAIT is sampled at the end of T2.
        return _timing.Profile.IsPlus3
            ? checked(cycleStartMasterTick
                + (2UL * (ulong)_clock.MasterTicksPerCpuTstate))
            : cycleStartMasterTick;
    }

    private int CountWaitStates(ulong absoluteMasterTick)
    {
        int waits = 0;
        ulong tick = absoluteMasterTick;
        while (waits < 8 && IsWaitAsserted(tick))
        {
            waits++;
            tick += ZxNextReferenceBaseline.MasterTicksPerBaseCpuTstate;
        }

        return waits;
    }

    private bool IsWaitAsserted(ulong absoluteMasterTick)
    {
        ZxNextUlaTimingProfile profile = _timing.Profile;
        ulong elapsed = absoluteMasterTick >= _timing.FrameStartMasterTick
            ? absoluteMasterTick - _timing.FrameStartMasterTick
            : absoluteMasterTick;
        ulong rasterPixel = (elapsed % profile.MasterTicksPerFrame)
            / ZxNextUlaTimingProfile.MasterTicksPerRasterPixel;
        int horizontal = (int)(rasterPixel % (ulong)profile.PixelsPerLine);
        int vertical = (int)(rasterPixel / (ulong)profile.PixelsPerLine);
        int ulaHorizontal = Mod(
            horizontal - profile.ContentionCounterStartHorizontal,
            profile.PixelsPerLine);
        int ulaVertical = Mod(vertical - profile.FirstDisplayLine, profile.LinesPerFrame);
        if (ulaHorizontal >= ZxNextUlaTimingProfile.DisplayWidth
            || ulaVertical >= ZxNextUlaTimingProfile.DisplayHeight)
        {
            return false;
        }

        int adjusted = (ulaHorizontal + 1) & 0x0F;
        bool standardWait = (adjusted & 0x0C) != 0;
        bool plus3ExtraWait = profile.IsPlus3 && (adjusted & 0x0E) == 0;
        return standardWait || plus3ExtraWait;
    }

    private static int Mod(int value, int modulus)
    {
        int result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
