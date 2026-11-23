namespace ZedExEss.ZxNext.Core;

/// <summary>
/// Identifiers and initial configuration pinned by the ZX Spectrum Next source-baseline ADR.
/// </summary>
public static class ZxNextReferenceBaseline
{
    /// <summary>The core the machine emulates and reports: 3.03.03.</summary>
    /// <remarks>
    /// Core 3.03's VHDL is not published. Its delta over 3.02 - the zxnDDMA and zero wait
    /// states at 28 MHz - comes from the documents of <see cref="SystemDistributionVersion"/>,
    /// chiefly <c>docs/zxnDDMA/zxnddma.gde</c>; everything else follows the VHDL of
    /// <see cref="VhdlReferenceVersion"/>.
    /// </remarks>
    public const string CoreVersion = "3.03.03";

    /// <summary>NextReg 0x01: major version in bits 7:4, minor in bits 3:0.</summary>
    public const byte CoreVersionMajorMinor = 0x33;

    /// <summary>NextReg 0x0E: the sub-minor version.</summary>
    public const byte CoreVersionSubMinor = 0x03;

    /// <summary>The newest published VHDL the emulation follows outside the 3.03 delta.</summary>
    public const string VhdlReferenceVersion = "3.02.03";
    public const string VhdlReferenceCommit = "b047011d82bf3086f2b7db76451c6254506f5fe8";

    /// <summary>The System/Next release that ships the target core, its ROMs and firmware.</summary>
    public const string SystemDistributionVersion = "26.11a";

    /// <summary>NextReg 0x00 identity reserved for emulators.</summary>
    public const byte MachineId = 0x08;

    /// <summary>
    /// NextReg 0x0F: the board, issue 4 (the Xilinx Artix-7 board with 2 MiB, as fitted to
    /// the second Kickstarter's machines).
    /// </summary>
    public const byte BoardIssue = 0x02;

    /// <summary>NextReg 0x10 bits 6:2: the core id the FPGA reports, 1 for the Next core.</summary>
    public const byte CoreId = 0x01;

    /// <summary>Initial mode-0 clk28 frequency selected by NextReg 0x11.</summary>
    public const int MasterClockHz = 28_000_000;

    public const int BaseCpuClockHz = MasterClockHz / 8;
    /// <summary>
    /// Fitted memory: the fully expanded 2 MiB Next. The hardware reserves 256 KiB of this
    /// address space, leaving 1792 KiB - pages 0 to 223 - available as MMU-addressable RAM.
    /// </summary>
    /// <remarks>
    /// Nothing guest-visible reports the size: no NextReg identifies it, and NextZXOS finds
    /// out by probing pages. So this decides only how many pages answer. The 1 MiB board
    /// (96 pages) is still a valid <see cref="Memory.ZxNextPhysicalMemory"/> configuration,
    /// and a snapshot taken on one restores into this machine.
    /// </remarks>
    public const int InitialRamBytes = 2 * 1024 * 1024;
    public const int MasterTicksPerBaseCpuTstate = 8;
}
