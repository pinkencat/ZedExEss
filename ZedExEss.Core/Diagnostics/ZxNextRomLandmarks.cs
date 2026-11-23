using ZedExEss.ZxNext.Core;

namespace ZedExEss.Diagnostics;

/// <summary>Places in the supplied NextZXOS ROMs that the boot diagnostics wait for.</summary>
internal static class ZxNextRomLandmarks
{
    /// <summary>
    /// True while the CPU is parked on ROM 0's keyboard-wait HALT.
    /// </summary>
    /// <remarks>
    /// The HALT is at <c>$0CB5</c> in NextZXOS 2.10a (System/Next 26.11a, the distribution
    /// the core 3.03.03 target ships with) and at <c>$0C8F</c> in 2.09 (24.06), which is kept
    /// so the older tree still boots. A halted CPU reports either the HALT or the address
    /// after it, so both count.
    /// </remarks>
    public static bool IsAtKeyWait(ZxNextMachine machine) =>
        machine.Cpu.IsHalted && machine.Cpu.PC is 0x0CB5 or 0x0CB6 or 0x0C8F or 0x0C90;

    /// <summary>
    /// True for the step that leaves the keyboard wait with a key: the <c>JR Z</c> after
    /// <c>BIT 5,(FLAGS)</c> falling through instead of looping back to the HALT
    /// (<c>$0CBB</c> to <c>$0CBD</c> in 2.10a, <c>$0C95</c> to <c>$0C97</c> in 2.09).
    /// </summary>
    public static bool LeftKeyWaitWithKey(ushort previousPc, ushort pc) =>
        (previousPc == 0x0CBB && pc == 0x0CBD) || (previousPc == 0x0C95 && pc == 0x0C97);

    /// <summary>
    /// True when the mapped ROM holds the keyboard-wait HALT followed by its <c>LD HL,nn</c>
    /// at one of the known addresses, so a wait at that PC is the ROM's and not a stray HALT.
    /// </summary>
    public static bool MappedRomHasKeyWait(ZxNextMachine machine) =>
        HasHaltThenLoadHl(machine, 0x0CB5) || HasHaltThenLoadHl(machine, 0x0C8F);

    private static bool HasHaltThenLoadHl(ZxNextMachine machine, ushort address) =>
        machine.MemoryBus.Read(address) == 0x76
        && machine.MemoryBus.Read((ushort)(address + 1)) == 0x21;
}
