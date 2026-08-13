using ZedExEss.Zx8x.Core;
using ZedExEss.Zx8x.Memory;

namespace ZedExEss.Zx8x.Media;

/// <summary>Describes a ZX80 <c>.o</c> or ZX81 <c>.p</c>/<c>.81</c> program image.</summary>
public enum Zx8xProgramImageFormat
{
    Zx80O,
    Zx81P,
    Zx81_81
}

/// <summary>Reports the memory range restored by a ZX8x program-image load.</summary>
public sealed record Zx8xProgramImageLoadResult(
    Zx8xProgramImageFormat Format,
    ushort LoadAddress,
    int LoadedBytes,
    int IgnoredTrailingBytes);

/// <summary>
/// Restores the raw RAM images produced by the ZX80 and ZX81 ROM SAVE routines.
/// </summary>
/// <remarks>
/// ZX80 O files contain memory from 4000h; ZX81 P and 81 files begin at 4009h.
/// These formats do not contain CPU state. The register values below reproduce the
/// state reached after a successful ROM load so execution can safely resume in BASIC.
/// An 81 file is a P image with optional trailing transfer garbage, which is removed
/// using the E_LINE pointer contained in the restored system variables.
/// </remarks>
public static class Zx8xProgramImageLoader
{
    private const ushort RamBase = 0x4000;
    private const ushort Zx80LoadAddress = 0x4000;
    private const ushort Zx81LoadAddress = 0x4009;
    private const int Zx80ELineOffset = 0x000A;
    private const int Zx81ELineOffset = 0x4014 - Zx81LoadAddress;

    public static Zx8xProgramImageLoadResult LoadFile(Zx8xMachine machine, string path)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Load(machine, File.ReadAllBytes(path), Path.GetExtension(path));
    }

    public static Zx8xProgramImageLoadResult Load(
        Zx8xMachine machine,
        ReadOnlySpan<byte> image,
        string extension)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (image.IsEmpty)
        {
            throw new InvalidDataException("The ZX80/ZX81 program image is empty.");
        }

        (Zx8xProgramImageFormat format, Zx8xModel requiredModel, ushort loadAddress, int eLineOffset) =
            ParseFormat(extension);
        if (machine.Model != requiredModel)
        {
            throw new InvalidOperationException(
                $"A {format} image requires {Zx8xModelDescriptors.ForModel(requiredModel).DisplayName}, " +
                $"but the active machine is {machine.Descriptor.DisplayName}.");
        }

        int logicalLength = GetLogicalLength(image, loadAddress, eLineOffset);
        int capacity = machine.Memory.RamSizeBytes - (loadAddress - RamBase);
        if (logicalLength > capacity)
        {
            throw new InvalidDataException(
                $"The image requires {logicalLength} bytes at {loadAddress:X4}h, but the selected " +
                $"{machine.Memory.RamSizeBytes / 1024} KiB RAM configuration provides only {capacity} bytes there. " +
                "Select a larger ZX80/ZX81 RAM expansion and try again.");
        }

        // Validate everything before altering the running machine. A malformed or
        // incompatible image therefore leaves the current program intact.
        machine.Reset();
        machine.Memory.ClearRam();
        if (requiredModel == Zx8xModel.Zx81)
        {
            InitialiseZx81LowSystemArea(machine.Memory);
        }

        for (int offset = 0; offset < logicalLength; offset++)
        {
            machine.Memory.Write((ushort)(loadAddress + offset), image[offset]);
        }

        RestorePostLoadState(machine);
        return new Zx8xProgramImageLoadResult(
            format,
            loadAddress,
            logicalLength,
            image.Length - logicalLength);
    }

    public static Zx8xModel GetRequiredModel(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        return ParseFormat(extension).RequiredModel;
    }

    private static (Zx8xProgramImageFormat Format, Zx8xModel RequiredModel, ushort LoadAddress, int ELineOffset)
        ParseFormat(string extension)
    {
        string normalized = extension.StartsWith('.') ? extension.ToLowerInvariant() : "." + extension.ToLowerInvariant();
        return normalized switch
        {
            ".o" => (Zx8xProgramImageFormat.Zx80O, Zx8xModel.Zx80, Zx80LoadAddress, Zx80ELineOffset),
            ".p" => (Zx8xProgramImageFormat.Zx81P, Zx8xModel.Zx81, Zx81LoadAddress, Zx81ELineOffset),
            ".81" => (Zx8xProgramImageFormat.Zx81_81, Zx8xModel.Zx81, Zx81LoadAddress, Zx81ELineOffset),
            _ => throw new NotSupportedException($"Unsupported ZX80/ZX81 program-image type: {extension}")
        };
    }

    private static int GetLogicalLength(ReadOnlySpan<byte> image, ushort loadAddress, int eLineOffset)
    {
        if (image.Length < eLineOffset + 2)
        {
            throw new InvalidDataException("The program image is too short to contain its E_LINE system variable.");
        }

        ushort eLine = (ushort)(image[eLineOffset] | (image[eLineOffset + 1] << 8));
        int logicalLength = eLine - loadAddress;
        if (logicalLength <= eLineOffset + 1)
        {
            throw new InvalidDataException($"The program image contains an invalid E_LINE address ({eLine:X4}h).");
        }

        if (logicalLength > image.Length)
        {
            throw new InvalidDataException(
                $"The program image ends early: E_LINE requires {logicalLength} bytes, but the file contains {image.Length}.");
        }

        return logicalLength;
    }

    private static void InitialiseZx81LowSystemArea(Zx8xMemory memory)
    {
        int ramTop = RamBase + memory.RamSizeBytes - 1;
        int stackPointer = ramTop - 3;
        byte[] lowSystemArea =
        [
            0xFF, 0x80,
            (byte)stackPointer, (byte)(stackPointer >> 8),
            (byte)(ramTop + 1), (byte)((ramTop + 1) >> 8),
            0x00, 0xFE, 0xFF
        ];

        for (int i = 0; i < lowSystemArea.Length; i++)
        {
            memory.Write((ushort)(RamBase + i), lowSystemArea[i]);
        }
    }

    private static void RestorePostLoadState(Zx8xMachine machine)
    {
        int ramTop = RamBase + machine.Memory.RamSizeBytes - 1;
        ushort stackPointer = (ushort)(ramTop - 3);
        byte[] stack = [0x76, 0x06, 0x00, 0x3E];
        for (int i = 0; i < stack.Length; i++)
        {
            machine.Memory.Write((ushort)(stackPointer + i), stack[i]);
        }

        Zx8xCpu cpu = machine.Cpu;
        cpu.A = 0x0B;
        cpu.SetFlags(0x85);
        cpu.B = 0x00;
        cpu.C = 0xFF;
        cpu.D = 0x43;
        cpu.E = 0x99;
        cpu.H = 0xC3;
        cpu.L = 0x99;
        cpu.A_ = 0xE2;
        cpu.F_ = 0xA1;
        cpu.B_ = 0x81;
        cpu.C_ = 0x02;
        cpu.D_ = 0x00;
        cpu.E_ = 0x2B;
        cpu.H_ = 0x00;
        cpu.L_ = 0x00;
        cpu.I = machine.Model == Zx8xModel.Zx80 ? (byte)0x0E : (byte)0x1E;
        cpu.R = 0xDD;
        cpu.IX = 0x0281;
        cpu.IY = RamBase;
        cpu.SP = stackPointer;
        cpu.PC = machine.Model == Zx8xModel.Zx80 ? (ushort)0x0283 : (ushort)0x0207;
        cpu.MemPtr = 0;
        // A ROM LOAD returns through the ZX8x main loop with IM 1 selected. The
        // raw image contains no CPU state, and Load() resets the machine before
        // restoring RAM, so leaving the reset default (IM 0) changes the timing
        // of the refresh-generated display interrupt by two T-states per line.
        cpu.SetInterruptState(1, iff1: false, iff2: false);
        cpu.SetHalted(false);

        // A restored ZX81 program normally returns to SLOW mode after LOAD.
        if (machine.Model == Zx8xModel.Zx81)
        {
            machine.Io.WritePort(0x00FE, 0);
        }
    }
}
