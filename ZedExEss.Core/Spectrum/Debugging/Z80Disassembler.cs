using System;
using System.Collections.Generic;
using System.Linq;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Spectrum.Debugging
{
    /// <summary>A decoded instruction together with the exact bytes consumed from memory.</summary>
    public readonly struct Z80DisassembledInstruction(ushort address, byte[] bytes, string text, int length, bool isCallLike)
    {
        public ushort Address { get; } = address;
        public byte[] Bytes { get; } = bytes;
        public string Text { get; } = text;
        public int Length { get; } = length;
        public bool IsCallLike { get; } = isCallLike;
    }
    /// <summary>Decodes the complete Z80 prefix space without changing emulated state.</summary>
    /// <remarks>
    /// Reads use the debugger's direct-memory path: disassembling must not add contention,
    /// trigger peripheral automapping, or advance the machine clock. Unknown ED encodings are
    /// retained as data-like mnemonics so callers can always advance by a defined byte count.
    /// </remarks>
    public sealed class Z80Disassembler
    {
        private static readonly string[] Reg8 = ["B", "C", "D", "E", "H", "L", "(HL)", "A"];
        private static readonly string[] Reg16 = ["BC", "DE", "HL", "SP"];
        private static readonly string[] Reg16Af = ["BC", "DE", "HL", "AF"];
        private static readonly string[] Conditions = ["NZ", "Z", "NC", "C", "PO", "PE", "P", "M"];
        private static readonly string[] Alu = ["ADD A", "ADC A", "SUB", "SBC A", "AND", "XOR", "OR", "CP"];
        private static readonly string[] Rot = ["RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL"];
        private static readonly Dictionary<byte, string> EdInstructions = new()
        {
            [0x44] = "NEG", [0x4C] = "NEG", [0x54] = "NEG", [0x5C] = "NEG", [0x64] = "NEG", [0x6C] = "NEG", [0x74] = "NEG", [0x7C] = "NEG",
            [0x45] = "RETN", [0x55] = "RETN", [0x5D] = "RETN", [0x65] = "RETN", [0x6D] = "RETN", [0x75] = "RETN", [0x7D] = "RETN",
            [0x4D] = "RETI",
            [0x46] = "IM 0", [0x4E] = "IM 0", [0x66] = "IM 0", [0x6E] = "IM 0",
            [0x56] = "IM 1", [0x76] = "IM 1",
            [0x5E] = "IM 2", [0x7E] = "IM 2",
            [0x47] = "LD I,A", [0x4F] = "LD R,A", [0x57] = "LD A,I", [0x5F] = "LD A,R",
            [0x67] = "RRD", [0x6F] = "RLD",
            [0xA0] = "LDI", [0xA1] = "CPI", [0xA2] = "INI", [0xA3] = "OUTI",
            [0xA8] = "LDD", [0xA9] = "CPD", [0xAA] = "IND", [0xAB] = "OUTD",
            [0xB0] = "LDIR", [0xB1] = "CPIR", [0xB2] = "INIR", [0xB3] = "OTIR",
            [0xB8] = "LDDR", [0xB9] = "CPDR", [0xBA] = "INDR", [0xBB] = "OTDR"
        };
        public Z80DisassembledInstruction Disassemble(SpectrumMemory memory, ushort address)
        {
            ArgumentNullException.ThrowIfNull(memory);

            byte op = Read(memory, address);
            string text;
            int length = 1;
            bool callLike = false;

            if (op == 0xCB)
            {
                byte cb = Read(memory, (ushort)(address + 1));
                text = DecodeCb(cb, Reg8);
                length = 2;
            }
            else if (op == 0xED)
            {
                byte ed = Read(memory, (ushort)(address + 1));
                text = DecodeEd(memory, address, ed, out length);
            }
            else if (op is 0xDD or 0xFD)
            {
                text = DecodeIndexed(memory, address, op == 0xDD ? "IX" : "IY", out length, out callLike);
            }
            else
            {
                text = DecodeBase(memory, address, op, "HL", Reg8, Reg16, Reg16Af, out length, out callLike);
            }

            byte[] bytes = ReadBytes(memory, address, length);
            return new Z80DisassembledInstruction(address, bytes, text, length, callLike);
        }
        public IReadOnlyList<Z80DisassemblyLine> DisassembleWindow(SpectrumMemory memory, ushort start, ushort currentPc, int count, SpectrumDebuggerController debugger)
        {
            var lines = new List<Z80DisassemblyLine>(count);
            ushort pc = start;
            for (int i = 0; i < count; i++)
            {
                Z80DisassembledInstruction instruction = Disassemble(memory, pc);
                SpectrumMemoryMapping mapping = memory.GetMapping(pc);
                bool hasBreakpoint = debugger.Breakpoints.Any(bp => bp.Type == DebuggerBreakType.Execute && bp.MatchesAddress(pc, mapping));
                lines.Add(new Z80DisassemblyLine(pc, instruction.Bytes, instruction.Text, instruction.Length, pc == currentPc, hasBreakpoint, mapping));
                pc = unchecked((ushort)(pc + Math.Max(1, instruction.Length)));
            }

            return lines;
        }
        public int GetInstructionLength(SpectrumMemory memory, ushort address) => Disassemble(memory, address).Length;
        private static string DecodeBase(SpectrumMemory memory, ushort address, byte op, string hl, string[] r, string[] rp, string[] rpAf, out int length, out bool callLike)
        {
            length = 1;
            callLike = false;

            if (op < 0x40)
            {
                int x = op >> 6;
                int y = (op >> 3) & 7;
                int z = op & 7;
                int p = y >> 1;
                int q = y & 1;

                if (x == 0)
                {
                    switch (z)
                    {
                        case 0:
                            if (y is >= 2 and <= 7)
                            {
                                length = 2;
                            }

                            return y switch
                            {
                                0 => "NOP",
                                1 => $"EX AF,AF'",
                                2 => $"DJNZ {Rel(address, Read(memory, (ushort)(address + 1)))}",
                                3 => $"JR {Rel(address, Read(memory, (ushort)(address + 1)))}",
                                _ => $"JR {Conditions[y - 4]},{Rel(address, Read(memory, (ushort)(address + 1)))}"
                            };
                        case 1:
                            if (q == 0)
                            {
                                length = 3;
                                return $"LD {rp[p]},{Word(memory, address + 1):X4}";
                            }

                            return $"ADD {hl},{rp[p]}";
                        case 2:
                            return y switch
                            {
                                0 => "LD (BC),A",
                                1 => "LD A,(BC)",
                                2 => "LD (DE),A",
                                3 => "LD A,(DE)",
                                4 => Len3(out length, $"LD ({Word(memory, address + 1):X4}),{hl}"),
                                5 => Len3(out length, $"LD {hl},({Word(memory, address + 1):X4})"),
                                6 => Len3(out length, $"LD ({Word(memory, address + 1):X4}),A"),
                                _ => Len3(out length, $"LD A,({Word(memory, address + 1):X4})")
                            };
                        case 3:
                            return q == 0 ? $"INC {rp[p]}" : $"DEC {rp[p]}";
                        case 4:
                            return $"INC {r[y]}";
                        case 5:
                            return $"DEC {r[y]}";
                        case 6:
                            length = 2;
                            return $"LD {r[y]},{Read(memory, (ushort)(address + 1)):X2}";
                        case 7:
                            return y switch
                            {
                                0 => "RLCA",
                                1 => "RRCA",
                                2 => "RLA",
                                3 => "RRA",
                                4 => "DAA",
                                5 => "CPL",
                                6 => "SCF",
                                _ => "CCF"
                            };
                    }
                }
            }

            if (op < 0x80)
            {
                return op == 0x76 ? "HALT" : $"LD {r[(op >> 3) & 7]},{r[op & 7]}";
            }

            if (op < 0xC0)
            {
                return $"{Alu[(op >> 3) & 7]} {r[op & 7]}";
            }

            int y2 = (op >> 3) & 7;
            int z2 = op & 7;
            int p2 = y2 >> 1;
            int q2 = y2 & 1;

            switch (z2)
            {
                case 0:
                    return $"RET {Conditions[y2]}";
                case 1:
                    if (q2 == 0)
                    {
                        return $"POP {rpAf[p2]}";
                    }

                    return p2 switch
                    {
                        0 => "RET",
                        1 => "EXX",
                        2 => $"JP ({hl})",
                        _ => $"LD SP,{hl}"
                    };
                case 2:
                    length = 3;
                    return $"JP {Conditions[y2]},{Word(memory, address + 1):X4}";
                case 3:
                    switch (y2)
                    {
                        case 0:
                            length = 3;
                            return $"JP {Word(memory, address + 1):X4}";
                        case 1:
                            length = 3;
                            return $"CB {Read(memory, (ushort)(address + 1)):X2}";
                        case 2:
                            length = 2;
                            return $"OUT ({Read(memory, (ushort)(address + 1)):X2}),A";
                        case 3:
                            length = 2;
                            return $"IN A,({Read(memory, (ushort)(address + 1)):X2})";
                        case 4:
                            return $"EX (SP),{hl}";
                        case 5:
                            return $"EX DE,{hl}";
                        case 6:
                            return "DI";
                        default:
                            return "EI";
                    }
                case 4:
                    length = 3;
                    callLike = true;
                    return $"CALL {Conditions[y2]},{Word(memory, address + 1):X4}";
                case 5:
                    if (q2 == 0)
                    {
                        return $"PUSH {rpAf[p2]}";
                    }

                    if (p2 == 0)
                    {
                        length = 3;
                        callLike = true;
                        return $"CALL {Word(memory, address + 1):X4}";
                    }

                    return $"{op:X2}";
                case 6:
                    length = 2;
                    return $"{Alu[y2]} {Read(memory, (ushort)(address + 1)):X2}";
                case 7:
                    callLike = true;
                    return $"RST {y2 * 8:X2}";
            }

            return $"DB {op:X2}";
        }
        private static string DecodeCb(byte op, string[] r)
        {
            int x = op >> 6;
            int y = (op >> 3) & 7;
            int z = op & 7;
            return x switch
            {
                0 => $"{Rot[y]} {r[z]}",
                1 => $"BIT {y},{r[z]}",
                2 => $"RES {y},{r[z]}",
                _ => $"SET {y},{r[z]}"
            };
        }
        private static string DecodeEd(SpectrumMemory memory, ushort address, byte op, out int length)
        {
            length = 2;
            if (EdInstructions.TryGetValue(op, out string? text))
            {
                return text;
            }

            int y = (op >> 3) & 7;
            int z = op & 7;
            int p = y >> 1;
            int q = y & 1;
            string[] rp = ["BC", "DE", "HL", "SP"];
            string reg = Reg8[y] == "(HL)" ? "0" : Reg8[y];

            if (z == 0 && op >= 0x40 && op <= 0x7F)
            {
                return $"IN {reg},(C)";
            }

            if (z == 1 && op >= 0x40 && op <= 0x7F)
            {
                return $"OUT (C),{reg}";
            }

            if (z == 2 && op >= 0x40 && op <= 0x7F)
            {
                return q == 0 ? $"SBC HL,{rp[p]}" : $"ADC HL,{rp[p]}";
            }

            if (z == 3 && op >= 0x40 && op <= 0x7F)
            {
                length = 4;
                return q == 0
                    ? $"LD ({Word(memory, address + 2):X4}),{rp[p]}"
                    : $"LD {rp[p]},({Word(memory, address + 2):X4})";
            }

            return $"ED {op:X2}";
        }
        private static string DecodeIndexed(SpectrumMemory memory, ushort address, string index, out int length, out bool callLike)
        {
            byte op = Read(memory, (ushort)(address + 1));
            length = 2;
            callLike = false;

            if (op == 0xCB)
            {
                sbyte displacement = unchecked((sbyte)Read(memory, (ushort)(address + 2)));
                byte cb = Read(memory, (ushort)(address + 3));
                string operand = $"({index}{Signed(displacement)})";
                string[] r = ["B", "C", "D", "E", "H", "L", operand, "A"];
                length = 4;
                return DecodeCb(cb, r);
            }

            string[] rix = ["B", "C", "D", "E", $"{index}H", $"{index}L", $"({index}+d)", "A"];
            string[] rp = ["BC", "DE", index, "SP"];
            string[] rpAf = ["BC", "DE", index, "AF"];
            if (op is 0x34 or 0x35)
            {
                sbyte d = unchecked((sbyte)Read(memory, (ushort)(address + 2)));
                length = 3;
                return op == 0x34 ? $"INC ({index}{Signed(d)})" : $"DEC ({index}{Signed(d)})";
            }

            if (op == 0x36)
            {
                sbyte d = unchecked((sbyte)Read(memory, (ushort)(address + 2)));
                byte n = Read(memory, (ushort)(address + 3));
                length = 4;
                return $"LD ({index}{Signed(d)}),{n:X2}";
            }

            if (op is >= 0x80 and <= 0xBF && (op & 0x07) == 0x06)
            {
                sbyte d = unchecked((sbyte)Read(memory, (ushort)(address + 2)));
                length = 3;
                return $"{Alu[(op >> 3) & 7]} ({index}{Signed(d)})";
            }

            if (op is >= 0x40 and < 0xC0)
            {
                if (op == 0x76)
                {
                    return "HALT";
                }

                int dst = (op >> 3) & 7;
                int src = op & 7;
                if (dst == 6 || src == 6)
                {
                    sbyte d = unchecked((sbyte)Read(memory, (ushort)(address + 2)));
                    string[] r = ["B", "C", "D", "E", $"{index}H", $"{index}L", $"({index}{Signed(d)})", "A"];
                    length = 3;
                    return $"LD {r[dst]},{r[src]}";
                }
            }

            string text = DecodeBase(memory, (ushort)(address + 1), op, index, rix, rp, rpAf, out int baseLength, out callLike);
            length = baseLength + 1;
            if (text.Contains("+d", StringComparison.Ordinal))
            {
                sbyte d = unchecked((sbyte)Read(memory, (ushort)(address + 2)));
                text = text.Replace("+d", Signed(d), StringComparison.Ordinal);
                length++;
            }

            return text;
        }
        private static byte[] ReadBytes(SpectrumMemory memory, ushort address, int length)
        {
            byte[] bytes = new byte[length];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Read(memory, (ushort)(address + i));
            }

            return bytes;
        }
        private static byte Read(SpectrumMemory memory, ushort address) => memory.ReadDirect(address);
        private static ushort Word(SpectrumMemory memory, int address)
        {
            byte lo = Read(memory, unchecked((ushort)address));
            byte hi = Read(memory, unchecked((ushort)(address + 1)));
            return (ushort)(lo | (hi << 8));
        }
        private static string Rel(ushort address, byte displacement)
        {
            ushort target = unchecked((ushort)(address + 2 + (sbyte)displacement));
            return $"{target:X4}";
        }
        private static string Signed(sbyte displacement) => displacement < 0 ? $"-{(-displacement):X2}" : $"+{displacement:X2}";
        private static string Len3(out int length, string text)
        {
            length = 3;
            return text;
        }
    }
}
