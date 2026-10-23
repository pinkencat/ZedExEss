namespace ZedExEss.ZxNext.Core;

/// <summary>
/// Identifiers and initial configuration pinned by the ZX Spectrum Next source-baseline ADR.
/// </summary>
public static class ZxNextReferenceBaseline
{
    public const string CoreVersion = "3.02.03";
    public const string FpgaRepositoryCommit = "b047011d82bf3086f2b7db76451c6254506f5fe8";
    public const string SystemRepositoryCommit = "f287c48bf6af3528e32977b913bac2c589d9f973";

    /// <summary>NextReg 0x00 identity reserved for emulators.</summary>
    public const byte MachineId = 0x08;

    /// <summary>Initial mode-0 clk28 frequency selected by NextReg 0x11.</summary>
    public const int MasterClockHz = 28_000_000;

    public const int BaseCpuClockHz = MasterClockHz / 8;
    /// <summary>
    /// Base fitted memory. The hardware reserves 256 KiB of this address space,
    /// leaving 768 KiB available as MMU-addressable RAM.
    /// </summary>
    public const int InitialRamBytes = 1024 * 1024;
    public const int MasterTicksPerBaseCpuTstate = 8;
}
