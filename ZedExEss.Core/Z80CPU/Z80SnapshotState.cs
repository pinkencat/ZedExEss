namespace ZedExEss.Z80CPU;

/// <summary>
/// Instruction-boundary state required to suspend and resume the Z80 without
/// running an instruction or touching the machine buses.
/// </summary>
/// <remarks>
/// The public register fields are accompanied by the undocumented Q latches and
/// interrupt latches. SZX stores the architecturally useful subset in Z80R; the
/// remaining fields can be retained by ZedExEss's private extension chunk.
/// </remarks>
public sealed record Z80SnapshotState(
    ulong Cycles,
    ushort PC,
    ushort SP,
    ushort IX,
    ushort IY,
    ushort MemPtr,
    byte A,
    byte F,
    byte B,
    byte C,
    byte D,
    byte E,
    byte H,
    byte L,
    byte AlternateA,
    byte AlternateF,
    byte AlternateB,
    byte AlternateC,
    byte AlternateD,
    byte AlternateE,
    byte AlternateH,
    byte AlternateL,
    byte I,
    byte R,
    byte InterruptMode,
    bool Iff1,
    bool Iff2,
    bool Halted,
    byte IffDelay,
    byte InterruptData,
    bool IntPending,
    bool NmiPending,
    byte Q,
    byte LastQ);
