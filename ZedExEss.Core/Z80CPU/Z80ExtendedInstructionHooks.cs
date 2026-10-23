namespace ZedExEss.Z80CPU;

public partial class Z80Core<TMemory, TPorts>
    where TMemory : class, IZ80MemoryBus
    where TPorts : class, IZ80PortBus
{
    /// <summary>
    /// Returns the complete instruction duration for a family-specific ED opcode,
    /// or zero when the ordinary Z80 decoder owns the encoding.
    /// </summary>
    protected virtual int GetExtendedEdOpcodeCycles(byte opcode) => 0;

    /// <summary>Executes an ED opcode accepted by <see cref="GetExtendedEdOpcodeCycles"/>.</summary>
    protected virtual void ExecuteExtendedEdOpcode(byte opcode)
    {
        throw new InvalidOperationException($"No extended ED implementation exists for {opcode:X2}.");
    }

    /// <summary>Writes an instruction-produced flags byte and updates the hidden Q latch.</summary>
    protected void SetInstructionFlags(byte flags) => F = flags;
}
