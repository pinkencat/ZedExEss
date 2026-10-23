namespace ZedExEss.ZxNext.Core;

/// <summary>Immutable geometry derived from the pinned zxula_timing.vhd profile.</summary>
public readonly record struct ZxNextUlaTimingProfile(
    byte MachineTiming,
    bool SixtyHertz,
    int PixelsPerLine,
    int LinesPerFrame,
    int DisplayStartHorizontal,
    int FirstDisplayLine,
    int InterruptHorizontal,
    int InterruptVertical,
    int InterruptPulseCpuTstates)
{
    public const int MasterTicksPerRasterPixel = 4;
    public const int FallingEdgeMasterTick = 2;
    public const int DisplayWidth = 256;
    public const int DisplayHeight = 192;
    public const int VisibleWidth = 320;
    public const int VisibleHeight = 256;
    // zxula_timing resets hc_ula when the raw counter equals active-12. Since both
    // counters update on the same CLK7 edge, hc_ula=0 is observable one pixel later,
    // at active-11. The zxula capture phases below are expressed on that delayed
    // counter (pixel at 9/13, attribute at 11/15).
    public const int EvenPixelFetchLeadPixels = 2;
    public const int OddPixelFetchLeadPixels = 6;
    public const int EvenAttributeFetchLeadPixels = 0;
    public const int OddAttributeFetchLeadPixels = 4;
    public const int ContentionCounterLeadPixels = 11;

    public bool IsPentagon => (MachineTiming & 0x04) != 0;
    public bool IsPlus3 => !IsPentagon && (MachineTiming & 0x03) == 3;
    public int VisibleStartHorizontal => DisplayStartHorizontal - 32;
    public int VisibleFirstLine => FirstDisplayLine - 32;
    public int ContentionCounterStartHorizontal =>
        DisplayStartHorizontal - ContentionCounterLeadPixels;
    public int LineInterruptHorizontal => DisplayStartHorizontal - 12 + DisplayWidth;
    public ulong MasterTicksPerFrame => checked(
        (ulong)PixelsPerLine * (ulong)LinesPerFrame * MasterTicksPerRasterPixel);
    public ulong InterruptOffsetMasterTicks => checked(
        (((ulong)InterruptVertical * (ulong)PixelsPerLine
            + (ulong)InterruptHorizontal + 1UL)
        * MasterTicksPerRasterPixel)
        + FallingEdgeMasterTick);

    /// <summary>
    /// Returns the registered line-interrupt assertion offset for the selected
    /// copper/active line, or null when that line does not exist in this profile.
    /// </summary>
    public ulong? GetLineInterruptOffsetMasterTicks(ushort line)
    {
        int selectedLine = line & 0x01FF;
        if (selectedLine >= LinesPerFrame)
        {
            return null;
        }

        // zxula_timing compares cvc with line-1 while hc_ula is 255. cvc is
        // reset to zero at the ULA active-line origin; selecting zero wraps to
        // the final counter value so it still occurs before copper line zero.
        int precedingCopperLine = selectedLine == 0 ? LinesPerFrame - 1 : selectedLine - 1;
        int rawVertical = (FirstDisplayLine + precedingCopperLine) % LinesPerFrame;
        return checked(
            (((ulong)rawVertical * (ulong)PixelsPerLine
                + (ulong)LineInterruptHorizontal + 1UL)
            * MasterTicksPerRasterPixel)
            + FallingEdgeMasterTick);
    }

    public static ZxNextUlaTimingProfile Create(byte machineTiming, bool sixtyHertzRequested)
    {
        bool pentagon = (machineTiming & 0x04) != 0;
        bool sixtyHertz = sixtyHertzRequested && !pentagon;
        if (pentagon)
        {
            return new ZxNextUlaTimingProfile(
                machineTiming,
                SixtyHertz: false,
                PixelsPerLine: 448,
                LinesPerFrame: 320,
                DisplayStartHorizontal: 128,
                FirstDisplayLine: 80,
                InterruptHorizontal: 439,
                InterruptVertical: 319,
                InterruptPulseCpuTstates: 36);
        }

        if ((machineTiming & 0x03) == 2)
        {
            return new ZxNextUlaTimingProfile(
                machineTiming,
                sixtyHertz,
                PixelsPerLine: 456,
                LinesPerFrame: sixtyHertz ? 264 : 311,
                DisplayStartHorizontal: 136,
                FirstDisplayLine: sixtyHertz ? 40 : 64,
                InterruptHorizontal: 128,
                InterruptVertical: sixtyHertz ? 0 : 1,
                InterruptPulseCpuTstates: 36);
        }

        if ((machineTiming & 0x03) == 3)
        {
            return new ZxNextUlaTimingProfile(
                machineTiming,
                sixtyHertz,
                PixelsPerLine: 456,
                LinesPerFrame: sixtyHertz ? 264 : 311,
                DisplayStartHorizontal: 136,
                FirstDisplayLine: sixtyHertz ? 40 : 64,
                InterruptHorizontal: 126,
                InterruptVertical: sixtyHertz ? 0 : 1,
                InterruptPulseCpuTstates: 32);
        }

        return new ZxNextUlaTimingProfile(
            machineTiming,
            sixtyHertz,
            PixelsPerLine: 448,
            LinesPerFrame: sixtyHertz ? 264 : 312,
            DisplayStartHorizontal: 128,
            FirstDisplayLine: sixtyHertz ? 40 : 64,
            InterruptHorizontal: 116,
            InterruptVertical: 0,
            InterruptPulseCpuTstates: 32);
    }
}
