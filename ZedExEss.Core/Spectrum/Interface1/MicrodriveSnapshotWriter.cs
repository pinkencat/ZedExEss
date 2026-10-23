using System;
using System.Collections.Generic;
using System.Globalization;
using ZedExEss.Spectrum.Core;

namespace ZedExEss.Spectrum.Interface1;

/// <summary>One thing the loader fetches, and what it does with it once fetched.</summary>
/// <param name="File">The cartridge file to load.</param>
/// <param name="LoadAddress">Where it goes, which for a packed block is not where it lives.</param>
/// <param name="Bank">The bank that must already be paged in, or -1 for a fixed window.</param>
/// <param name="Unpack">The entry point called afterwards, or null when there is nothing to do.</param>
public sealed record MicrodriveSnapshotStep(
    string File,
    ushort LoadAddress,
    int Bank,
    ushort? Unpack);

/// <summary>What was written, in the order the loader works through it.</summary>
public sealed record MicrodriveSnapshotPlan(
    IReadOnlyList<MicrodriveSnapshotStep> Steps,
    ushort RestoreEntry,
    ushort? PageFirstBank,
    int PackedBytes,
    int UnpackedBytes,
    int ScreenBytesUsed,
    int LoaderBytes);

/// <summary>
/// Writes the running machine onto a cartridge as a program that restores it.
/// </summary>
/// <remarks>
/// This is what a Multiface does, without one, and it is built the way the Multiface
/// builds it. The obstacle either way is that the Interface 1 needs part of the Spectrum's
/// memory to read the cartridge at all - system variables, a channel buffer for the "m"
/// stream, the BASIC loader and a stack - so that memory cannot simply be loaded over
/// while the loader is still using it. Leaving it alone is not an option either: it sits
/// directly above the system variables, which is exactly where a game tends to keep code,
/// data or its stack.
///
/// So the loader's workspace is stored as an ordinary block, loaded into the display file
/// out of the way, and copied down into place by a routine that also runs from the display
/// file - somewhere that is not being restored while it works. All of RAM comes back, and
/// the only thing lost is the part of the screen the restore ran from, which the restored
/// program redraws.
///
/// On a 128K the same shape holds, with two additions. Seven of the eight banks are
/// restored through the window at C000 one at a time, which is why they are packed: there
/// is nowhere to unpack them to, so each is loaded into the top of that window and unpacked
/// over itself. Bank five is the exception, because the screen, the workspace and this very
/// code all live in it, so it is restored in pieces through its own fixed window as a 48K
/// machine's memory is. The sound chip and the paging port are put back last.
///
/// The BASIC is kept as short as it can be made, because it competes for room with the
/// channel buffer the Interface 1 needs before it can read anything at all. Paging is done
/// by the same machine code that unpacks, numbers are written as VAL of a string rather
/// than in BASIC's own five-byte form, and the drive number is SGN PI - which is the trick
/// the Multiface used, for the same reason.
/// </remarks>
public static class MicrodriveSnapshotWriter
{
    /// <summary>Where the loader's workspace waits while the loader is still using it.</summary>
    private const ushort StagingAddress = 0x4000;

    /// <summary>The lowest byte the loader needs while it runs.</summary>
    private const ushort WorkAreaStart = 0x5B00;

    /// <summary>Room set aside for the code, so it can never reach the screen it restores.</summary>
    private const int CodeReserve = 0x400;

    private const ushort BankWindow = 0xC000;

    /// <summary>The Spectrum 128 system variable that shadows the write-only paging port.</summary>
    private const ushort BankShadow = 0x5B5C;

    /// <summary>The bank the screen, the workspace and this code all live in.</summary>
    private const int ResidentBank = 5;

    private const string ScreenFile = "S0";
    private const string HighFile = "S1";
    private const string CodeFile = "S2";
    private const string StagedFile = "S3";
    private const string UpperFile = "S4";
    private const string LoaderFile = "run";

    private const ushort FirstLine = 10;

    /// <summary>
    /// Where everything sits, which depends on how much room BASIC needs to load with.
    /// </summary>
    /// <remarks>
    /// Everything between the system variables and RAMTOP has to hold those variables, the
    /// Interface 1 channel buffer for the "m" stream, the BASIC program and the stack. A
    /// 128K loader needs a line per bank and so wants more of it, and asking for too little
    /// is not a subtle failure: the Interface 1 cannot allocate its channel and the load
    /// stops with "out of memory" before it has read a byte.
    /// </remarks>
    private readonly record struct Layout(ushort WorkAreaEnd)
    {
        public int WorkAreaLength => WorkAreaEnd - WorkAreaStart + 1;

        public ushort CodeAddress => (ushort)(StagingAddress + WorkAreaLength);

        public ushort ScreenRestStart => (ushort)(CodeAddress + CodeReserve);

        public int ScreenRestLength => WorkAreaStart - ScreenRestStart;

        /// <summary>Memory above the workspace, restored through its own window.</summary>
        public ushort UpperStart => (ushort)(WorkAreaEnd + 1);

        public int ScreenBytesUsed => WorkAreaLength + CodeReserve;
    }

    /// <summary>One block, while it is still being decided what to do with it.</summary>
    private sealed class Block
    {
        public required string Name { get; init; }

        public required byte[] Data { get; init; }

        public required ushort LoadAddress { get; init; }

        public ushort Destination { get; init; }

        public bool Packed { get; init; }

        /// <summary>The bank this loads into, or -1 when it loads through a fixed window.</summary>
        public int Bank { get; init; } = -1;

        /// <summary>The bank its routine pages in afterwards, ready for the next load.</summary>
        public int PageNext { get; set; } = -1;

        public ushort Thunk { get; set; }
    }

    /// <summary>Writes the loader, the restoring code and the machine's RAM to a cartridge.</summary>
    public static MicrodriveSnapshotPlan Write(MicrodriveCartridge cartridge, SpectrumMachine machine)
    {
        ArgumentNullException.ThrowIfNull(cartridge);
        ArgumentNullException.ThrowIfNull(machine);

        bool paged = machine.Model is SpectrumModel.Spectrum128K or SpectrumModel.SpectrumPlus2;
        if (!paged && machine.Model is not (SpectrumModel.Spectrum48K or SpectrumModel.Spectrum16K))
        {
            throw new NotSupportedException(
                $"Snapshots to cartridge are written for the 48K and 128K; this is a {machine.Model}.");
        }

        // A 128K loader has a line per bank, so it is given more room to run in.
        var layout = new Layout(paged ? (ushort)0x63FF : (ushort)0x61FF);

        List<Block> blocks = PlanBlocks(machine, paged, layout);
        (byte[] code, ushort? pageFirst) = BuildCode(machine, paged, layout, blocks);
        if (code.Length > CodeReserve)
        {
            throw new InvalidOperationException(
                $"The restoring code is {code.Length} bytes, past its {CodeReserve}.");
        }

        byte[] loader = BuildLoader(layout, blocks, pageFirst);

        MicrodriveFileSystem.WriteCode(cartridge, CodeFile, code, layout.CodeAddress);
        MicrodriveFileSystem.WriteCode(
            cartridge,
            ScreenFile,
            ReadBlock(machine, layout.ScreenRestStart, layout.ScreenRestLength),
            layout.ScreenRestStart);

        foreach (Block block in blocks)
        {
            MicrodriveFileSystem.WriteCode(cartridge, block.Name, block.Data, block.LoadAddress);
        }

        MicrodriveFileSystem.WriteCode(
            cartridge,
            StagedFile,
            ReadBlock(machine, WorkAreaStart, layout.WorkAreaLength),
            StagingAddress);
        MicrodriveFileSystem.WriteBasicProgram(cartridge, LoaderFile, loader, autostartLine: FirstLine);

        var steps = new List<MicrodriveSnapshotStep>
        {
            new(CodeFile, layout.CodeAddress, -1, null),
            new(ScreenFile, layout.ScreenRestStart, -1, null)
        };

        int packedBytes = 0;
        int unpackedBytes = 0;
        foreach (Block block in blocks)
        {
            steps.Add(new MicrodriveSnapshotStep(
                block.Name, block.LoadAddress, block.Bank, block.Thunk == 0 ? null : block.Thunk));
            packedBytes += block.Data.Length;
            unpackedBytes += block.Packed ? 0x4000 : block.Data.Length;
        }

        steps.Add(new MicrodriveSnapshotStep(StagedFile, StagingAddress, -1, null));

        return new MicrodriveSnapshotPlan(
            steps,
            layout.CodeAddress,
            pageFirst,
            packedBytes,
            unpackedBytes,
            layout.ScreenBytesUsed,
            loader.Length);
    }

    /// <summary>
    /// Decides what the loader will fetch, and packs whatever can be unpacked in place.
    /// </summary>
    /// <remarks>
    /// Only a block that ends at FFFF can be unpacked over itself, because that is the
    /// condition the unpacker stops on. That covers the whole of a paged bank and the top
    /// of a 48K machine's memory; the smaller pieces of bank five go on whole.
    /// </remarks>
    private static List<Block> PlanBlocks(SpectrumMachine machine, bool paged, Layout layout)
    {
        var blocks = new List<Block>();

        if (!paged)
        {
            byte[] high = ReadBlock(machine, layout.UpperStart, 0x10000 - layout.UpperStart);
            MicrodrivePackedBlock? highPacked = MicrodriveCompression.TryPack(high);
            blocks.Add(new Block
            {
                Name = HighFile,
                Data = highPacked?.Data ?? high,
                LoadAddress = (ushort)(layout.UpperStart + (highPacked?.LoadOffset ?? 0)),
                Destination = layout.UpperStart,
                Packed = highPacked != null
            });

            return blocks;
        }

        // Bank five holds the screen, the workspace and the restoring code, so it is
        // restored through its own window rather than paged over.
        blocks.Add(new Block
        {
            Name = UpperFile,
            Data = ReadBlock(machine, layout.UpperStart, 0x8000 - layout.UpperStart),
            LoadAddress = layout.UpperStart
        });

        for (int bank = 0; bank < 8; bank++)
        {
            if (bank == ResidentBank)
            {
                continue;
            }

            byte[] contents = machine.Memory.CopyRamBank(bank);
            MicrodrivePackedBlock? packed = MicrodriveCompression.TryPack(contents);
            blocks.Add(new Block
            {
                Name = $"B{bank}",
                Data = packed?.Data ?? contents,
                LoadAddress = (ushort)(BankWindow + (packed?.LoadOffset ?? 0)),
                Destination = BankWindow,
                Packed = packed != null,
                Bank = bank
            });
        }

        // Each bank's routine pages in the one the loader asks for next, which keeps the
        // paging out of BASIC entirely.
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Bank < 0)
            {
                continue;
            }

            for (int next = i + 1; next < blocks.Count; next++)
            {
                if (blocks[next].Bank >= 0)
                {
                    blocks[i].PageNext = blocks[next].Bank;
                    break;
                }
            }
        }

        return blocks;
    }

    private static byte[] ReadBlock(SpectrumMachine machine, int start, int length)
    {
        var block = new byte[length];
        for (int i = 0; i < length; i++)
        {
            block[i] = machine.Memory.ReadDirect((ushort)(start + i));
        }

        return block;
    }

    /// <summary>
    /// Builds everything that runs from the display file: the restore, the unpacker, one
    /// entry point per block that needs work doing, and the routine that pages the first
    /// bank in.
    /// </summary>
    private static (byte[] Code, ushort? PageFirst) BuildCode(
        SpectrumMachine machine,
        bool paged,
        Layout layout,
        List<Block> blocks)
    {
        var code = new List<byte>(BuildRestore(machine, paged, layout));

        ushort unpackAddress = (ushort)(layout.CodeAddress + code.Count);
        code.AddRange(MicrodriveCompression.UnpackRoutine(unpackAddress));

        foreach (Block block in blocks)
        {
            if (!block.Packed && block.PageNext < 0)
            {
                continue;
            }

            block.Thunk = (ushort)(layout.CodeAddress + code.Count);
            if (block.Packed)
            {
                EmitLoad16(code, 0x21, block.LoadAddress);   // LD HL,packed
                EmitLoad16(code, 0x11, block.Destination);   // LD DE,destination
                code.Add(0xCD);                              // CALL unpack
                code.Add((byte)(unpackAddress & 0xFF));
                code.Add((byte)(unpackAddress >> 8));
            }

            if (block.PageNext >= 0)
            {
                EmitPaging(code, block.PageNext);
            }

            code.Add(0xC9);                                  // RET
        }

        ushort? pageFirst = null;
        foreach (Block block in blocks)
        {
            if (block.Bank < 0)
            {
                continue;
            }

            pageFirst = (ushort)(layout.CodeAddress + code.Count);
            EmitPaging(code, block.Bank);
            code.Add(0xC9);                                  // RET
            break;
        }

        return ([.. code], pageFirst);
    }

    /// <summary>
    /// Selects a bank, keeping whatever ROM and lock bits the machine is running with.
    /// </summary>
    /// <remarks>
    /// The paging port cannot be read back, so the value comes from the system variable
    /// that shadows it, and goes back there too so that BASIC and the ROM stay in step.
    /// </remarks>
    private static void EmitPaging(List<byte> code, int bank)
    {
        code.Add(0x3A);                                      // LD A,(BankShadow)
        code.Add((byte)(BankShadow & 0xFF));
        code.Add((byte)(BankShadow >> 8));
        code.Add(0xE6);                                      // AND $F8
        code.Add(0xF8);
        code.Add(0xF6);                                      // OR bank
        code.Add((byte)bank);
        code.Add(0x32);                                      // LD (BankShadow),A
        code.Add((byte)(BankShadow & 0xFF));
        code.Add((byte)(BankShadow >> 8));
        EmitLoad16(code, 0x01, 0x7FFD);                      // LD BC,$7FFD
        code.Add(0xED);                                      // OUT (C),A
        code.Add(0x79);
    }

    /// <summary>
    /// Builds the routine that puts the loader's workspace back and then the machine.
    /// </summary>
    /// <remarks>
    /// It runs from the display file, so the block copy underneath it is free to overwrite
    /// the system variables, the channel buffer, the BASIC program and the stack the
    /// Interface 1 was using moments earlier. Nothing here writes outside the screen and
    /// the workspace, which rules out the usual trick of pushing the return address onto
    /// the restored stack: both accumulators come back by pointing the stack pointer at a
    /// word stored in the routine and popping it, and the last instruction is an absolute
    /// jump that leaves the restored stack exactly as it was saved.
    /// </remarks>
    private static byte[] BuildRestore(SpectrumMachine machine, bool paged, Layout layout)
    {
        Z80CPU.Z80 cpu = machine.Cpu;
        var code = new List<byte>();
        var patches = new List<(int Operand, string Label)>();
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);

        void Absolute(byte opcode, string label)
        {
            code.Add(opcode);
            patches.Add((code.Count, label));
            code.Add(0);
            code.Add(0);
        }

        code.Add(0xF3);                                      // DI

        // Put the loader's workspace back where it belongs.
        EmitLoad16(code, 0x21, StagingAddress);              // LD HL,staging
        EmitLoad16(code, 0x11, WorkAreaStart);               // LD DE,work area
        EmitLoad16(code, 0x01, (ushort)layout.WorkAreaLength);
        code.Add(0xED);                                      // LDIR
        code.Add(0xB0);

        byte[]? ay = paged ? machine.AyChip?.CopyRegisters() : null;
        if (ay != null)
        {
            Absolute(0x21, "ay");                            // LD HL,table
            code.Add(0x1E);                                  // LD E,0
            code.Add(0x00);
            labels["ayLoop"] = code.Count;
            EmitLoad16(code, 0x01, 0xFFFD);                  // LD BC,$FFFD
            code.Add(0xED);                                  // OUT (C),E   -> select
            code.Add(0x59);
            code.Add(0x06);                                  // LD B,$BF    -> BC = $BFFD
            code.Add(0xBF);
            code.Add(0x7E);                                  // LD A,(HL)
            code.Add(0xED);                                  // OUT (C),A   -> write
            code.Add(0x79);
            code.Add(0x23);                                  // INC HL
            code.Add(0x1C);                                  // INC E
            code.Add(0x7B);                                  // LD A,E
            code.Add(0xFE);                                  // CP 16
            code.Add(0x10);
            Absolute(0xC2, "ayLoop");                        // JP NZ,ayLoop
        }

        if (paged)
        {
            EmitLoad16(code, 0x01, 0x7FFD);                  // LD BC,$7FFD
            code.Add(0x3E);                                  // LD A,paging
            code.Add(machine.Memory.Port7FFD);
            code.Add(0xED);                                  // OUT (C),A
            code.Add(0x79);
        }

        Absolute(0x31, "altAf");                             // LD SP,alternate AF
        code.Add(0xF1);                                      // POP AF
        code.Add(0x08);                                      // EX AF,AF'

        EmitLoad16(code, 0x01, cpu.BC_);                     // LD BC,nn
        EmitLoad16(code, 0x11, cpu.DE_);                     // LD DE,nn
        EmitLoad16(code, 0x21, cpu.HL_);                     // LD HL,nn
        code.Add(0xD9);                                      // EXX

        code.Add(0x3E);                                      // LD A,I
        code.Add(cpu.I);
        code.Add(0xED);
        code.Add(0x47);

        // R counts every fetch made after this, so it can only ever be approximate.
        code.Add(0x3E);
        code.Add(cpu.R);
        code.Add(0xED);                                      // LD R,A
        code.Add(0x4F);

        code.Add(0x3E);                                      // LD A,border
        code.Add((byte)(machine.Renderer.BorderColorIndex & 0x07));
        code.Add(0xD3);                                      // OUT (0FEh),A
        code.Add(0xFE);

        code.Add(0xED);                                      // IM n
        code.Add(cpu.InterruptModeValue switch
        {
            0 => (byte)0x46,
            1 => (byte)0x56,
            _ => (byte)0x5E
        });

        code.Add(0xDD);                                      // LD IX,nn
        EmitLoad16(code, 0x21, cpu.IX);
        code.Add(0xFD);                                      // LD IY,nn
        EmitLoad16(code, 0x21, cpu.IY);

        EmitLoad16(code, 0x01, cpu.BC);                      // LD BC,nn
        EmitLoad16(code, 0x11, cpu.DE);                      // LD DE,nn
        EmitLoad16(code, 0x21, cpu.HL);                      // LD HL,nn

        Absolute(0x31, "mainAf");                            // LD SP,main AF
        code.Add(0xF1);                                      // POP AF

        EmitLoad16(code, 0x31, cpu.SP);                      // LD SP,nn

        if (cpu.Iff1)
        {
            code.Add(0xFB);                                  // EI
        }

        code.Add(0xC3);                                      // JP nn
        code.Add((byte)(cpu.PC & 0xFF));
        code.Add((byte)(cpu.PC >> 8));

        labels["altAf"] = code.Count;
        code.Add((byte)(cpu.AF_ & 0xFF));
        code.Add((byte)(cpu.AF_ >> 8));
        labels["mainAf"] = code.Count;
        code.Add((byte)(cpu.AF & 0xFF));
        code.Add((byte)(cpu.AF >> 8));

        if (ay != null)
        {
            labels["ay"] = code.Count;
            for (int i = 0; i < 16; i++)
            {
                code.Add(i < ay.Length ? ay[i] : (byte)0);
            }
        }

        foreach ((int operand, string label) in patches)
        {
            int target = layout.CodeAddress + labels[label];
            code[operand] = (byte)(target & 0xFF);
            code[operand + 1] = (byte)(target >> 8);
        }

        return [.. code];
    }

    private static void EmitLoad16(List<byte> code, byte opcode, ushort value)
    {
        code.Add(opcode);
        code.Add((byte)(value & 0xFF));
        code.Add((byte)(value >> 8));
    }

    /// <summary>
    /// Builds the BASIC program that loads the pieces and calls the restore.
    /// </summary>
    /// <remarks>
    /// Short on purpose: every byte of it competes with the channel buffer the Interface 1
    /// needs before it can read anything, and running out shows up as "out of memory" on
    /// the very first load rather than as anything to do with the snapshot.
    /// </remarks>
    private static byte[] BuildLoader(Layout layout, List<Block> blocks, ushort? pageFirst)
    {
        var program = new List<byte>();
        ushort line = FirstLine;

        var opening = new List<byte> { 0xFD };               // CLEAR
        EmitNumber(opening, layout.WorkAreaEnd);
        EmitLoadStatement(opening, CodeFile);
        EmitLoadStatement(opening, ScreenFile);
        if (pageFirst != null)
        {
            EmitCall(opening, pageFirst.Value);
        }

        EmitLine(program, line, opening);
        line += 10;

        foreach (Block block in blocks)
        {
            var body = new List<byte>();
            EmitLoadStatement(body, block.Name, leadingColon: false);
            if (block.Thunk != 0)
            {
                EmitCall(body, block.Thunk);
            }

            EmitLine(program, line, body);
            line += 10;
        }

        var closing = new List<byte>();
        EmitLoadStatement(closing, StagedFile, leadingColon: false);
        EmitCall(closing, layout.CodeAddress);
        EmitLine(program, line, closing);

        return [.. program];
    }

    private static void EmitLine(List<byte> program, ushort number, List<byte> body)
    {
        program.Add((byte)(number >> 8));
        program.Add((byte)(number & 0xFF));
        program.Add((byte)((body.Count + 1) & 0xFF));
        program.Add((byte)((body.Count + 1) >> 8));
        program.AddRange(body);
        program.Add(0x0D);
    }

    private static void EmitCall(List<byte> body, ushort address)
    {
        body.Add(0x3A);                                      // :
        body.Add(0xF9);                                      // RANDOMIZE
        body.Add(0xC0);                                      // USR
        EmitNumber(body, address);
    }

    private static void EmitLoadStatement(List<byte> body, string file, bool leadingColon = true)
    {
        if (leadingColon)
        {
            body.Add(0x3A);                                  // :
        }

        body.Add(0xEF);                                      // LOAD
        body.Add(0x2A);                                      // *
        EmitString(body, "m");
        body.Add(0x3B);                                      // ;
        body.Add(0xBC);                                      // SGN
        body.Add(0xA7);                                      // PI     -> drive one, in two bytes
        body.Add(0x3B);                                      // ;
        EmitString(body, file);
        body.Add(0xAF);                                      // CODE
    }

    private static void EmitString(List<byte> body, string text)
    {
        body.Add(0x22);
        foreach (char character in text)
        {
            body.Add((byte)character);
        }

        body.Add(0x22);
    }

    /// <summary>
    /// Writes a number as VAL of a string, which is shorter than BASIC's own form.
    /// </summary>
    /// <remarks>
    /// A number typed into a line is followed by five bytes holding its value, so a
    /// five-digit address costs eleven bytes. Asking for VAL of the digits costs the digits
    /// plus three, and the loader has several of them to fit into a space it is short of.
    /// </remarks>
    private static void EmitNumber(List<byte> body, int value)
    {
        body.Add(0xB0);                                      // VAL
        EmitString(body, value.ToString(CultureInfo.InvariantCulture));
    }
}
