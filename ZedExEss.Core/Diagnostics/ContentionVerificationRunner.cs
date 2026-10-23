using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Diagnostics;

/// <summary>Settings for the per-instruction contention checks.</summary>
public sealed class ContentionVerificationOptions
{
    public string? OutputPath { get; init; }

    /// <summary>Directory holding 48.rom.</summary>
    public string? RomDirectory { get; init; }
}

/// <summary>
/// Checks what each instruction actually costs against the cycle sequence the reference
/// implementation performs.
/// </summary>
/// <remarks>
/// Instruction timing on its own is covered by the Z80 test suites, and it passes: every
/// opcode costs its published number of T-states. What those suites do not cover is
/// <em>which</em> cycles of an instruction are contended, and that is where an input
/// recording made on another emulator stops matching - a single extra contended cycle in
/// a loop is invisible for a few seconds and then fatal.
///
/// The expected costs here are built by walking the same cycle sequence the reference
/// performs, using this emulator's own contention table, which was compared against the
/// reference's byte for byte. So a failure means the sequence differs - a cycle contended
/// on the wrong address, or one too many - not that the table is wrong.
/// </remarks>
public static class ContentionVerificationRunner
{
    /// <summary>Where the test code runs: high RAM, which is never contended.</summary>
    private const ushort CodeAddress = 0x8000;

    /// <summary>A contended address, for the stack and for screen writes.</summary>
    private const ushort ContendedAddress = 0x5000;

    private const ushort StackAddress = 0x7000;

    /// <summary>Enough passes to cross the contention pattern in every phase.</summary>
    private const int Passes = 4000;

    public static int Run(ContentionVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string outputPath = Path.GetFullPath(options.OutputPath ?? "contention-verification.log");
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        int failed = 0;

        writer.WriteLine("Instruction contention verification");
        writer.WriteLine($"Output: {outputPath}");
        writer.WriteLine();

        string romDirectory = options.RomDirectory ?? Path.Combine(AppContext.BaseDirectory, "ROMs");
        string machineRom = Path.Combine(romDirectory, "48.rom");
        if (!File.Exists(machineRom))
        {
            writer.WriteLine($"FAIL 48K ROM not found: {machineRom}");
            writer.WriteLine("Result: FAIL (1 failed checks)");
            return 1;
        }

        Check("LD (HL),A writing contended memory", () => VerifyWriteToContended(machineRom), ref failed);
        Check("LD (IX+d),A writing contended memory", () => VerifyIndexedWrite(machineRom), ref failed);
        Check("CALL nn pushing onto a contended stack", () => VerifyCall(machineRom), ref failed);
        Check("RET popping from a contended stack", () => VerifyRet(machineRom), ref failed);
        Check("SET b,(IY+d) on contended memory", () => VerifyIndexedBitSet(machineRom), ref failed);
        Check("BIT b,(IY+d) on contended memory", () => VerifyIndexedBitTest(machineRom), ref failed);
        Check("LDIR copying into contended memory", () => VerifyBlockCopy(machineRom), ref failed);
        Check("OUT to the ULA port", () => VerifyUlaPortWrite(machineRom), ref failed);
        Check("IN from the ULA port", () => VerifyUlaPortRead(machineRom), ref failed);
        Check("Code running from contended memory", () => VerifyContendedCode(machineRom), ref failed);
        Check("Opcode fetch counts match the M1 cycles", () => VerifyFetchCounts(machineRom), ref failed);
        Check("Contention table matches the reference formula", VerifyContentionTable, ref failed);

        writer.WriteLine();
        writer.WriteLine(failed == 0 ? "Result: PASS" : $"Result: FAIL ({failed} failed checks)");
        return failed == 0 ? 0 : 1;

        void Check(string name, Func<string?> action, ref int failureCount)
        {
            try
            {
                string? failure = action();
                if (failure == null)
                {
                    writer.WriteLine($"PASS {name}");
                    return;
                }

                failureCount++;
                writer.WriteLine($"FAIL {name}: {failure}");
            }
            catch (Exception ex)
            {
                failureCount++;
                writer.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }
    }


    /// <summary>
    /// Rebuilds the reference's contention table from its own formula and compares every
    /// T-state of a frame against ours.
    /// </summary>
    /// <remarks>
    /// The other checks here walk the reference's cycle sequences but price them with our
    /// table, so they establish that an instruction contends the right cycles and say
    /// nothing about whether the table those cycles are priced from is itself right. This
    /// check closes that gap: the expected values are computed from the reference's
    /// contend_delay_common and its published 48K geometry, so nothing of ours is used to
    /// produce them.
    ///
    /// The two clocks do not share an origin - the reference counts from the interrupt,
    /// this emulator from its own frame start - so rather than assume a conversion the
    /// comparison slides one against the other and reports the alignment that fits best.
    /// An exact match at a non-zero shift would mean the table is right but sampled at the
    /// wrong point in the frame, which is a different bug from a wrong table.
    /// </remarks>
    private static string? VerifyContentionTable()
    {
        // Transcribed from the reference: 48K geometry, and the pattern with the offset
        // its own source pairs that pattern with.
        const int TstatesPerLine = 224;
        const int TstatesPerFrame = 69888;
        const int LeftBorder = 24;
        const int HorizontalScreen = 128;
        const int BorderHeight = 24;
        const int DisplayHeight = 192;
        const int BorderWidthCols = 4;
        const int TopLeftPixel = 14336;
        const int PatternOffset = 1;
        int[] pattern = [5, 4, 3, 2, 1, 0, 0, 6];

        int lineTime0 = TopLeftPixel - (BorderHeight * TstatesPerLine) - (4 * BorderWidthCols);

        var reference = new int[TstatesPerFrame];
        for (int time = 0; time < TstatesPerFrame; time++)
        {
            reference[time] = ReferenceDelay(time);
        }

        var profile = SpectrumContentionProfile.Create(SpectrumModel.Spectrum48K)!;
        profile.PhaseOrigin = 0;
        profile.Timing = SpectrumContentionTiming.Early;

        var ours = new int[TstatesPerFrame];
        for (int time = 0; time < TstatesPerFrame; time++)
        {
            ours[time] = profile.GetMemoryDelay((ulong)time);
        }

        int bestShift = 0;
        int bestMatches = -1;
        for (int shift = -TstatesPerLine; shift <= TstatesPerLine; shift++)
        {
            int matches = 0;
            for (int time = 0; time < TstatesPerFrame; time++)
            {
                int index = ((time + shift) % TstatesPerFrame + TstatesPerFrame) % TstatesPerFrame;
                if (ours[index] == reference[time])
                {
                    matches++;
                }
            }

            if (matches > bestMatches)
            {
                bestMatches = matches;
                bestShift = shift;
            }
        }

        long referenceTotal = 0;
        long ourTotal = 0;
        for (int time = 0; time < TstatesPerFrame; time++)
        {
            referenceTotal += reference[time];
            ourTotal += ours[time];
        }

        // Our table is indexed from the top of the frame and the reference's from the
        // interrupt, so the two are expected to sit exactly that far apart.
        int expectedShift = SpectrumTimingModel.ForModel(SpectrumModel.Spectrum48K)
            .InterruptAssertOffsetTstates;

        if (bestMatches == TstatesPerFrame && bestShift == expectedShift)
        {
            return null;
        }

        if (bestMatches == TstatesPerFrame)
        {
            return $"the table is right but {bestShift} T-states out of phase, "
                + $"expected {expectedShift} (totals {ourTotal} vs {referenceTotal}).";
        }

        int firstDifference = -1;
        for (int time = 0; time < TstatesPerFrame; time++)
        {
            if (ours[time] != reference[time])
            {
                firstDifference = time;
                break;
            }
        }

        return $"{TstatesPerFrame - bestMatches} of {TstatesPerFrame} T-states differ at the "
            + $"best alignment (shift {bestShift}); first difference at T{firstDifference}, "
            + $"want {(firstDifference < 0 ? 0 : reference[firstDifference])} "
            + $"got {(firstDifference < 0 ? 0 : ours[firstDifference])}; "
            + $"frame totals {ourTotal} vs {referenceTotal}.";

        int ReferenceDelay(int time)
        {
            long line = (time - lineTime0) / TstatesPerLine;
            if (time < lineTime0 || line < BorderHeight || line >= BorderHeight + DisplayHeight)
            {
                return 0;
            }

            long throughLine = time - lineTime0 + (LeftBorder - (BorderWidthCols * 4));
            throughLine %= TstatesPerLine;

            if (throughLine < LeftBorder - PatternOffset)
            {
                return 0;
            }

            if (throughLine >= LeftBorder + HorizontalScreen - PatternOffset)
            {
                return 0;
            }

            return pattern[throughLine % 8];
        }
    }

    /// <summary>
    /// LD (HL),A: fetch, then one contended write. The simplest case, and the control -
    /// if this disagrees, the harness is wrong rather than the instruction.
    /// </summary>
    private static string? VerifyWriteToContended(string machineRom)
    {
        SpectrumMachine machine = CreateMachine(machineRom);
        Write(machine, CodeAddress, 0x77); // LD (HL),A

        return Compare(machine, "LD (HL),A", cpu =>
        {
            cpu.PC = CodeAddress;
            cpu.SetHL(ContendedAddress);
            return
            [
                (CodeAddress, 4),
                (ContendedAddress, 3)
            ];
        });
    }

    /// <summary>
    /// LD (IX+d),A: two fetches, the displacement read, five internal cycles and the
    /// write. The reference contends those five internal cycles on the address of the
    /// displacement byte - not on the address being written to.
    /// </summary>
    private static string? VerifyIndexedWrite(string machineRom)
    {
        SpectrumMachine machine = CreateMachine(machineRom);
        Write(machine, CodeAddress, 0xDD);
        Write(machine, (ushort)(CodeAddress + 1), 0x77);
        Write(machine, (ushort)(CodeAddress + 2), 0x00); // LD (IX+0),A

        return Compare(machine, "LD (IX+0),A", cpu =>
        {
            cpu.PC = CodeAddress;
            cpu.IX = ContendedAddress;
            return
            [
                (CodeAddress, 4),
                ((ushort)(CodeAddress + 1), 4),
                ((ushort)(CodeAddress + 2), 3),
                ((ushort)(CodeAddress + 2), 1),
                ((ushort)(CodeAddress + 2), 1),
                ((ushort)(CodeAddress + 2), 1),
                ((ushort)(CodeAddress + 2), 1),
                ((ushort)(CodeAddress + 2), 1),
                (ContendedAddress, 3)
            ];
        });
    }

    /// <summary>
    /// CALL nn: fetch, two operand reads, one internal cycle on the operand's high byte,
    /// then two writes as the return address goes onto the stack.
    /// </summary>
    private static string? VerifyCall(string machineRom)
    {
        SpectrumMachine machine = CreateMachine(machineRom);
        Write(machine, CodeAddress, 0xCD);
        Write(machine, (ushort)(CodeAddress + 1), 0x00);
        Write(machine, (ushort)(CodeAddress + 2), 0x90); // CALL $9000

        return Compare(machine, "CALL nn", cpu =>
        {
            cpu.PC = CodeAddress;
            cpu.SP = StackAddress;
            return
            [
                (CodeAddress, 4),
                ((ushort)(CodeAddress + 1), 3),
                ((ushort)(CodeAddress + 2), 3),
                ((ushort)(CodeAddress + 2), 1),
                ((ushort)(StackAddress - 1), 3),
                ((ushort)(StackAddress - 2), 3)
            ];
        });
    }

    /// <summary>
    /// SET b,(IY+d): both prefixes, the displacement, the opcode byte and its two internal
    /// cycles, then a read, one more internal cycle and a write on the target.
    /// </summary>
    /// <remarks>
    /// The two cycles after the opcode byte belong to the instruction stream; the one
    /// between the read and the write belongs to the address being modified. Charging
    /// either in the wrong place bills contention at the wrong moment.
    /// </remarks>
    private static string? VerifyIndexedBitSet(string machineRom)
    {
        SpectrumMachine machine = CreateMachine(machineRom);
        Write(machine, CodeAddress, 0xFD);
        Write(machine, (ushort)(CodeAddress + 1), 0xCB);
        Write(machine, (ushort)(CodeAddress + 2), 0x00);
        Write(machine, (ushort)(CodeAddress + 3), 0xFE); // SET 7,(IY+0)

        return Compare(machine, "SET 7,(IY+0)", cpu =>
        {
            cpu.PC = CodeAddress;
            cpu.IY = ContendedAddress;
            return
            [
                (CodeAddress, 4),
                ((ushort)(CodeAddress + 1), 4),
                ((ushort)(CodeAddress + 2), 3),
                ((ushort)(CodeAddress + 3), 3),
                ((ushort)(CodeAddress + 3), 1),
                ((ushort)(CodeAddress + 3), 1),
                (ContendedAddress, 3),
                (ContendedAddress, 1),
                (ContendedAddress, 3)
            ];
        });
    }

    /// <summary>BIT b,(IY+d): the same, without the write.</summary>
    private static string? VerifyIndexedBitTest(string machineRom)
    {
        SpectrumMachine machine = CreateMachine(machineRom);
        Write(machine, CodeAddress, 0xFD);
        Write(machine, (ushort)(CodeAddress + 1), 0xCB);
        Write(machine, (ushort)(CodeAddress + 2), 0x00);
        Write(machine, (ushort)(CodeAddress + 3), 0x7E); // BIT 7,(IY+0)

        return Compare(machine, "BIT 7,(IY+0)", cpu =>
        {
            cpu.PC = CodeAddress;
            cpu.IY = ContendedAddress;
            return
            [
                (CodeAddress, 4),
                ((ushort)(CodeAddress + 1), 4),
                ((ushort)(CodeAddress + 2), 3),
                ((ushort)(CodeAddress + 3), 3),
                ((ushort)(CodeAddress + 3), 1),
                ((ushort)(CodeAddress + 3), 1),
                (ContendedAddress, 3),
                (ContendedAddress, 1)
            ];
        });
    }

    /// <summary>
    /// OUT (n),A to the ULA port: one cycle before the write, then the ULA's own two, then
    /// one more. The ULA port is contended wherever the port address happens to point.
    /// </summary>
    /// <remarks>
    /// Port contention has two halves. The first depends on whether the port address, read
    /// as if it were a memory address, lands in a contended page - so OUT (254),A with A
    /// in $40-$7F contends where the same instruction with A=0 does not. The second
    /// depends only on the port being the ULA's.
    /// </remarks>
    private static string? VerifyUlaPortWrite(string machineRom)
    {
        SpectrumMachine machine = CreateMachine(machineRom);
        Write(machine, CodeAddress, 0xD3);
        Write(machine, (ushort)(CodeAddress + 1), 0xFE); // OUT ($FE),A

        // A = 0, so the port address is $00FE: an uncontended page, but still the ULA.
        return Compare(machine, "OUT ($FE),A", cpu =>
        {
            cpu.PC = CodeAddress;
            cpu.A = 0x00;
            return
            [
                (CodeAddress, 4),
                ((ushort)(CodeAddress + 1), 3),
                Step(0x00FE, 1, contend: false),
                Step(0x00FE, 2, contend: true),
                Step(0x00FE, 1, contend: false)
            ];
        });
    }

    /// <summary>IN A,(n) from the ULA port: the same halves, without the trailing cycle.</summary>
    private static string? VerifyUlaPortRead(string machineRom)
    {
        SpectrumMachine machine = CreateMachine(machineRom);
        Write(machine, CodeAddress, 0xDB);
        Write(machine, (ushort)(CodeAddress + 1), 0xFE); // IN A,($FE)

        return Compare(machine, "IN A,($FE)", cpu =>
        {
            cpu.PC = CodeAddress;
            cpu.A = 0x00;
            return
            [
                (CodeAddress, 4),
                ((ushort)(CodeAddress + 1), 3),
                Step(0x00FE, 1, contend: false),
                Step(0x00FE, 3, contend: true)
            ];
        });
    }

    /// <summary>
    /// A repeating LDIR iteration: both fetches, the read, the write, and seven internal
    /// cycles - two always, five more because it repeats - all on the destination.
    /// </summary>
    /// <remarks>
    /// Each internal cycle is contended separately rather than as one delay for the
    /// group, which is what makes a block copy into the screen so expensive.
    /// </remarks>
    private static string? VerifyBlockCopy(string machineRom)
    {
        const ushort source = 0x9000;
        SpectrumMachine machine = CreateMachine(machineRom);
        Write(machine, CodeAddress, 0xED);
        Write(machine, (ushort)(CodeAddress + 1), 0xB0); // LDIR

        return Compare(machine, "LDIR", cpu =>
        {
            cpu.PC = CodeAddress;
            cpu.SetHL(source);
            cpu.SetDE(ContendedAddress);
            cpu.SetBC(0x0100);
            return
            [
                (CodeAddress, 4),
                ((ushort)(CodeAddress + 1), 4),
                (source, 3),
                (ContendedAddress, 3),
                (ContendedAddress, 1),
                (ContendedAddress, 1),
                (ContendedAddress, 1),
                (ContendedAddress, 1),
                (ContendedAddress, 1),
                (ContendedAddress, 1),
                (ContendedAddress, 1)
            ];
        });
    }

    /// <summary>
    /// Instructions fetched from contended memory, where the opcode fetch itself is
    /// contended rather than only the data it touches.
    /// </summary>
    /// <remarks>
    /// Every test above runs its code from uncontended RAM, which leaves the M1 fetch
    /// untested. A game that runs from the lower 16K contends on every instruction, so an
    /// error here is multiplied by the whole instruction stream rather than by one opcode.
    /// </remarks>
    private static string? VerifyContendedCode(string machineRom)
    {
        const ushort contendedCode = 0x6000;
        var failures = new List<string>();

        // NOP: nothing but the fetch.
        SpectrumMachine machine = CreateMachine(machineRom);
        Write(machine, contendedCode, 0x00);
        string? failure = Compare(machine, "NOP in contended memory", cpu =>
        {
            cpu.PC = contendedCode;
            return [(contendedCode, 4)];
        });

        if (failure != null)
        {
            failures.Add(failure);
        }

        // LD A,n: the fetch and an operand read, both contended.
        machine = CreateMachine(machineRom);
        Write(machine, contendedCode, 0x3E);
        Write(machine, (ushort)(contendedCode + 1), 0x00);
        failure = Compare(machine, "LD A,n in contended memory", cpu =>
        {
            cpu.PC = contendedCode;
            return
            [
                (contendedCode, 4),
                ((ushort)(contendedCode + 1), 3)
            ];
        });

        if (failure != null)
        {
            failures.Add(failure);
        }

        // INC HL: the fetch plus two internal cycles, which belong to the refresh
        // address rather than to HL.
        machine = CreateMachine(machineRom);
        Write(machine, contendedCode, 0x23);
        failure = Compare(machine, "INC HL in contended memory", cpu =>
        {
            cpu.PC = contendedCode;
            cpu.SetHL(ContendedAddress);
            return
            [
                (contendedCode, 4),
                (RefreshAddress(cpu), 1),
                (RefreshAddress(cpu), 1)
            ];
        });

        if (failure != null)
        {
            failures.Add(failure);
        }

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    /// <summary>
    /// One bus cycle of an instruction: where it is addressed, how long it takes, and
    /// whether the ULA contends it.
    /// </summary>
    /// <remarks>
    /// Memory cycles contend when the address is in the contended bank, which the
    /// conversion below decides. I/O cycles do not follow that rule - half of a port
    /// access contends on the port's page and half on the port being the ULA's - so those
    /// are stated explicitly.
    /// </remarks>
    private readonly record struct CycleStep(ushort Address, int Cycles, bool Contend)
    {
        public static implicit operator CycleStep((ushort Address, int Cycles) step) =>
            new(step.Address, step.Cycles, step.Address is >= 0x4000 and < 0x8000);
    }

    private static CycleStep Step(ushort address, int cycles, bool contend) =>
        new(address, cycles, contend);

    /// <summary>The address the CPU puts on the bus during a refresh cycle.</summary>
    private static ushort RefreshAddress(Z80CPU.Z80 cpu) => (ushort)((cpu.I << 8) | cpu.R);

    /// <summary>RET: fetch, then two reads from the stack.</summary>
    private static string? VerifyRet(string machineRom)
    {
        SpectrumMachine machine = CreateMachine(machineRom);
        Write(machine, CodeAddress, 0xC9);

        return Compare(machine, "RET", cpu =>
        {
            cpu.PC = CodeAddress;
            cpu.SP = StackAddress;
            return
            [
                (CodeAddress, 4),
                (StackAddress, 3),
                ((ushort)(StackAddress + 1), 3)
            ];
        });
    }

    /// <summary>
    /// Checks how far the refresh register moves for each instruction.
    /// </summary>
    /// <remarks>
    /// One M1 cycle, one increment. A prefixed instruction has one per prefix, but the
    /// fourth byte of a DDCB is read as data rather than fetched, so it does not count -
    /// getting that wrong is invisible in ordinary code and fatal to an input recording,
    /// which is measured in exactly this quantity.
    /// </remarks>
    private static string? VerifyFetchCounts(string machineRom)
    {
        (string Name, byte[] Bytes, int Expected)[] cases =
        [
            ("NOP", [0x00], 1),
            ("LD A,n", [0x3E, 0x00], 1),
            ("BIT 0,A", [0xCB, 0x47], 2),
            ("INC IX", [0xDD, 0x23], 2),
            ("LD (IX+0),A", [0xDD, 0x77, 0x00], 2),
            ("SET 7,(IX+0)", [0xDD, 0xCB, 0x00, 0xFE], 2),
            ("RES 0,(IY+1)", [0xFD, 0xCB, 0x01, 0x86], 2),
            ("LD A,R", [0xED, 0x5F], 2)
        ];

        var failures = new List<string>();
        foreach ((string name, byte[] bytes, int expected) in cases)
        {
            SpectrumMachine machine = CreateMachine(machineRom);
            for (int i = 0; i < bytes.Length; i++)
            {
                Write(machine, (ushort)(CodeAddress + i), bytes[i]);
            }

            machine.Cpu.PC = CodeAddress;
            machine.Cpu.IX = ContendedAddress;
            machine.Cpu.IY = ContendedAddress;

            byte before = machine.Cpu.R;
            ulong beforeFetches = machine.Cpu.M1Fetches;
            machine.Emulator.StepInstruction();

            int refresh = (machine.Cpu.R - before) & 0x7F;
            long fetches = (long)(machine.Cpu.M1Fetches - beforeFetches);
            if (refresh != expected || fetches != expected)
            {
                failures.Add($"{name} moved R by {refresh} and the counter by {fetches}, not {expected}");
            }
        }

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    /// <summary>
    /// Runs one instruction repeatedly, comparing its real cost against the cost the
    /// reference sequence predicts from the same starting T-state.
    /// </summary>
    private static string? Compare(
        SpectrumMachine machine,
        string name,
        Func<Z80CPU.Z80, CycleStep[]> prepare)
    {
        SpectrumContentionProfile contention = machine.Contention
            ?? throw new InvalidOperationException("The machine has no contention profile.");

        long actualTotal = 0;
        long expectedTotal = 0;
        var mismatches = new List<string>();

        for (int pass = 0; pass < Passes; pass++)
        {
            CycleStep[] steps = prepare(machine.Cpu);
            ulong start = machine.Cpu.Cyc;
            long expected = ExpectedCost(contention, start, steps);

            machine.Emulator.StepInstruction();
            long actual = (long)(machine.Cpu.Cyc - start);

            actualTotal += actual;
            expectedTotal += expected;
            if (actual != expected && mismatches.Count < 4)
            {
                mismatches.Add($"at T-state {start % 69888}: {actual} against {expected}");
            }
        }

        if (mismatches.Count == 0)
        {
            return null;
        }

        return $"{name} cost {actualTotal} T over {Passes} passes, the reference sequence "
            + $"{expectedTotal} ({actualTotal - expectedTotal:+#;-#;0}); "
            + string.Join("; ", mismatches);
    }

    /// <summary>
    /// Walks a cycle sequence, charging contention before each access whose address is in
    /// contended memory. This is what the reference implementation does.
    /// </summary>
    private static long ExpectedCost(
        SpectrumContentionProfile contention,
        ulong start,
        CycleStep[] steps)
    {
        ulong now = start;
        foreach (CycleStep step in steps)
        {
            if (step.Contend)
            {
                now += (ulong)contention.GetMemoryDelay(now);
            }

            now += (ulong)step.Cycles;
        }

        return (long)(now - start);
    }

    private static void Write(SpectrumMachine machine, ushort address, byte value) =>
        machine.Memory.WriteDirect(address, value);

    private static SpectrumMachine CreateMachine(string machineRom) =>
        SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum48K,
            Roms = RomSet.LoadFromFiles([machineRom]),
            RenderEnabled = false
        });
}
