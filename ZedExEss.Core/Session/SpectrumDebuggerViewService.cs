using System.Globalization;
using System.Text;
using ZedExEss.Spectrum.Debugging;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Z80CPU;

namespace ZedExEss.Hosting;

/// <summary>Toolkit-neutral debugger projections and direct-edit operations.</summary>
public sealed class SpectrumDebuggerViewService(
    SpectrumDebuggerController debugger,
    Z80Disassembler disassembler,
    Z80InlineAssembler assembler)
{
    public SpectrumDebuggerController Debugger { get; } = debugger ?? throw new ArgumentNullException(nameof(debugger));
    public Z80Disassembler Disassembler { get; } = disassembler ?? throw new ArgumentNullException(nameof(disassembler));
    public Z80InlineAssembler Assembler { get; } = assembler ?? throw new ArgumentNullException(nameof(assembler));

    public string GetRegistersText()
    {
        IZ80DebuggerCpu? cpu = Debugger.Cpu;
        if (cpu == null)
        {
            return "No machine attached";
        }

        byte flags = cpu.GetFlags();
        string flagText =
            $"{Flag(flags, 0x80, 'S')}{Flag(flags, 0x40, 'Z')}{Flag(flags, 0x20, '5')}{Flag(flags, 0x10, 'H')}" +
            $"{Flag(flags, 0x08, '3')}{Flag(flags, 0x04, 'P')}{Flag(flags, 0x02, 'N')}{Flag(flags, 0x01, 'C')}";
        return
            $"AF {cpu.AF:X4}  BC {cpu.BC:X4}  DE {cpu.DE:X4}  HL {cpu.HL:X4}{Environment.NewLine}" +
            $"AF' {cpu.AF_:X4} BC' {cpu.BC_:X4} DE' {cpu.DE_:X4} HL' {cpu.HL_:X4}{Environment.NewLine}" +
            $"IX {cpu.IX:X4}  IY {cpu.IY:X4}  SP {cpu.SP:X4}  PC {cpu.PC:X4}{Environment.NewLine}" +
            $"I {cpu.I:X2}  R {cpu.R:X2}  IM {cpu.InterruptModeValue}  IFF {Bool(cpu.Iff1)}/{Bool(cpu.Iff2)}  HALT {Bool(cpu.IsHalted)}{Environment.NewLine}" +
            $"F {flagText}  T {cpu.Cyc}  frame {Debugger.CurrentFrameTstate}  line {Debugger.CurrentLine}:{Debugger.CurrentLineTstate}";
    }

    public IReadOnlyList<Z80DisassemblyLine> GetDisassembly(ushort start, int count)
    {
        IZ80DebuggerMemory memory = Debugger.Memory
            ?? throw new InvalidOperationException("No debugger memory is attached.");
        ushort currentPc = Debugger.Cpu?.PC ?? 0;
        return Disassembler.DisassembleWindow(memory, start, currentPc, count, Debugger);
    }

    /// <summary>Builds an auditable text listing for an inclusive logical-address range.</summary>
    public bool TryBuildDisassemblyExport(ushort start, ushort end, out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;
        IZ80DebuggerMemory? memory = Debugger.Memory;
        if (memory == null)
        {
            error = "No debugger memory is attached.";
            return false;
        }

        if (end < start)
        {
            error = "Export end address must be greater than or equal to the start address.";
            return false;
        }

        var builder = new StringBuilder();
        ushort address = start;
        int guard = 0;
        while (address <= end && guard++ < 0x10000)
        {
            Z80DisassembledInstruction instruction = Disassembler.Disassemble(memory, address);
            string bytes = BitConverter.ToString(instruction.Bytes).Replace("-", " ", StringComparison.Ordinal);
            builder.Append(instruction.Address.ToString("X4", CultureInfo.InvariantCulture))
                .Append(": ")
                .Append(bytes.PadRight(14))
                .Append(' ')
                .Append(instruction.Text)
                .AppendLine();

            int length = Math.Max(1, instruction.Length);
            if (address > 0xFFFF - length)
            {
                break;
            }

            address = (ushort)(address + length);
        }

        text = builder.ToString();
        if (text.Length == 0)
        {
            error = "The export range did not produce any disassembly.";
            return false;
        }

        return true;
    }

    public string GetMemoryText(ushort start, int rows)
    {
        IZ80DebuggerMemory memory = Debugger.Memory
            ?? throw new InvalidOperationException("No debugger memory is attached.");
        var builder = new StringBuilder(rows * 72);
        Span<char> ascii = stackalloc char[16];
        for (int row = 0; row < rows; row++)
        {
            ushort address = unchecked((ushort)(start + (row * 16)));
            builder.Append(address.ToString("X4", CultureInfo.InvariantCulture)).Append(": ");
            for (int column = 0; column < 16; column++)
            {
                byte value = memory.ReadDirect(unchecked((ushort)(address + column)));
                builder.Append(value.ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                ascii[column] = value is >= 32 and < 127 ? (char)value : '.';
            }

            builder.Append(' ').Append(ascii).AppendLine();
        }

        return builder.ToString();
    }

    public string GetStackText(int words = 32)
    {
        IZ80DebuggerMemory memory = Debugger.Memory
            ?? throw new InvalidOperationException("No debugger memory is attached.");
        IZ80DebuggerCpu cpu = Debugger.Cpu
            ?? throw new InvalidOperationException("No debugger CPU is attached.");
        var builder = new StringBuilder(words * 12);
        for (int index = 0; index < words; index++)
        {
            ushort address = unchecked((ushort)(cpu.SP + (index * 2)));
            ushort value = (ushort)(memory.ReadDirect(address)
                | (memory.ReadDirect(unchecked((ushort)(address + 1))) << 8));
            builder.Append(address.ToString("X4", CultureInfo.InvariantCulture))
                .Append(": ")
                .Append(value.ToString("X4", CultureInfo.InvariantCulture))
                .AppendLine();
        }

        return builder.ToString();
    }

    public bool TryPatchBytes(ushort address, string text, out string error)
    {
        if (!TryParseBytes(text, out byte[] bytes, out error))
        {
            return false;
        }

        IZ80DebuggerMemory? memory = Debugger.Memory;
        if (memory == null)
        {
            error = "No debugger memory is attached.";
            return false;
        }

        for (int index = 0; index < bytes.Length; index++)
        {
            ushort target = unchecked((ushort)(address + index));
            if (!memory.CanWriteDirect(target))
            {
                error = $"Address {target:X4} is not writable RAM.";
                return false;
            }
        }

        for (int index = 0; index < bytes.Length; index++)
        {
            memory.WriteDirect(unchecked((ushort)(address + index)), bytes[index]);
        }

        error = string.Empty;
        return true;
    }

    public Z80AssemblyResult Assemble(ushort address, string source) => Assembler.Assemble(address, source);

    public bool TryApplyAssembly(ushort address, string source, out Z80AssemblyResult result, out string error)
    {
        result = Assembler.Assemble(address, source);
        if (!result.Success)
        {
            error = result.Error ?? "Assembly failed.";
            return false;
        }

        IZ80DebuggerMemory? memory = Debugger.Memory;
        if (memory == null)
        {
            error = "No debugger memory is attached.";
            return false;
        }

        foreach (Z80AssemblyPatch patch in result.Patches)
        {
            for (int index = 0; index < patch.Bytes.Length; index++)
            {
                ushort target = unchecked((ushort)(patch.Address + index));
                if (!memory.CanWriteDirect(target))
                {
                    error = $"Address {target:X4} is not writable RAM.";
                    return false;
                }
            }
        }

        foreach (Z80AssemblyPatch patch in result.Patches)
        {
            for (int index = 0; index < patch.Bytes.Length; index++)
            {
                memory.WriteDirect(unchecked((ushort)(patch.Address + index)), patch.Bytes[index]);
            }
        }

        error = string.Empty;
        return true;
    }

    public static bool TryParseWord(string? text, out ushort value)
    {
        string input = (text ?? string.Empty).Trim();
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            input = input[2..];
        }
        else if (input.StartsWith('$'))
        {
            input = input[1..];
        }

        return ushort.TryParse(input, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseBytes(string text, out byte[] bytes, out string error)
    {
        string[] parts = text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        bytes = new byte[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            string value = parts[index];
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                value = value[2..];
            }
            else if (value.StartsWith('$'))
            {
                value = value[1..];
            }

            if (!byte.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[index]))
            {
                bytes = [];
                error = $"Invalid byte '{parts[index]}'.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static char Flag(byte flags, byte mask, char name) => (flags & mask) != 0 ? name : '-';
    private static char Bool(bool value) => value ? '1' : '0';
}
