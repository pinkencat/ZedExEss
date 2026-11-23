using ZedExEss.Spectrum.Abstractions;
using ZedExEss.ZxNext.Ports;

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

    public bool Enabled => _clock.CpuOnContendedClock
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

        // zxnext.vhd's port_contend: $7FFD as decoded (port_7ffd_active, in 128K or +3
        // timing only) and the ULA+ ports while their enable is set.
        ZxNextUlaTimingProfile profile = _timing.Profile;
        bool plus3 = profile.IsPlus3;
        bool pagingTiming = !profile.IsPentagon && (profile.MachineTiming & 0x03) is 2 or 3;
        return (pagingTiming
                && _registers.IsInternalPortEnabled(1)
                && ZxNextPortBus.IsPort7Ffd(port, plus3, _registers.IsInternalPortEnabled(3)))
            || ((port == 0xBF3B || port == 0xFF3B) && _registers.IsInternalPortEnabled(24));
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

    /// <remarks>
    /// 48K/128K contention holds the generated CPU clock high at the start of the machine cycle
    /// (zxula.vhd's <c>o_cpu_contend</c>). The +3 personality instead drives the T80's WAIT
    /// (<c>o_cpu_wait_n</c>, while MREQ is low), which it samples at the end of T2: the core
    /// decides 2 hc (one T-state) after the 48K/128K point. That is measured, not derived: Klive's
    /// simulated-core probes (its fidelity plan's F3-4, probe group C, 40 of 40) put a contended
    /// read on bank 7 at the core's maximum wait from T 22,903, and its 48K/128K pattern agrees
    /// with ours at every point the CPU can sample. This model sampled two T-states after the
    /// start until 23 November 2026, a T-state early.
    /// </remarks>
    private ulong GetMemorySampleTick(ulong cycleStartMasterTick)
    {
        return _timing.Profile.IsPlus3
            ? checked(cycleStartMasterTick + (ulong)_clock.MasterTicksPerCpuTstate)
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
