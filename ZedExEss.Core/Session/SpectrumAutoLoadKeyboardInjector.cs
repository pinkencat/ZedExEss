using ZedExEss.Spectrum.Memory;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Core;

/// <summary>
/// Feeds a ROM command through the Spectrum's LAST_K protocol after a known ROM input loop
/// has been reached. Timing is measured in emulated T-states, so turbo mode changes wall-clock
/// duration without changing the command timing observed by the ROM.
/// </summary>
public sealed class SpectrumAutoLoadKeyboardInjector(
    Z80 cpu,
    SpectrumMemory memory,
    ushort readyPc,
    int? expectedRomBank,
    ReadOnlySpan<byte> command,
    int initialDelayTstates,
    int keySpacingTstates)
{
    private const ushort LastKAddress = 0x5C08;
    private const ushort FlagsAddress = 0x5C3B;
    private const byte KeyAvailableMask = 0x20;
    private readonly byte[] _command = command.ToArray();
    private readonly ulong _minimumWriteCycle = cpu.Cyc + (ulong)Math.Max(initialDelayTstates, 0);
    private readonly ulong _keySpacingTstates = (ulong)Math.Max(keySpacingTstates, 1);
    private int _offset;
    private ulong _nextWriteCycle;
    private bool _readySeen;

    public bool IsComplete { get; private set; }

    /// <summary>Attempts one protocol transition at the current instruction boundary.</summary>
    public void Tick()
    {
        if (IsComplete)
        {
            return;
        }

        if (!_readySeen)
        {
            if (cpu.PC != readyPc)
            {
                return;
            }

            if (expectedRomBank.HasValue && memory.CurrentRomBank != expectedRomBank.Value)
            {
                return;
            }

            _readySeen = true;
            _nextWriteCycle = Math.Max(cpu.Cyc, _minimumWriteCycle);
            return;
        }

        if (cpu.Cyc < _nextWriteCycle)
        {
            return;
        }

        byte flags = memory.ReadDirect(FlagsAddress);
        if ((flags & KeyAvailableMask) != 0)
        {
            return;
        }

        memory.WriteDirect(LastKAddress, _command[_offset++]);
        memory.WriteDirect(FlagsAddress, (byte)(flags | KeyAvailableMask));
        _nextWriteCycle = cpu.Cyc + _keySpacingTstates;
        IsComplete = _offset >= _command.Length;
    }
}
