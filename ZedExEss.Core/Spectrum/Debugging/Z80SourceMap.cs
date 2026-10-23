using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ZedExEss.Spectrum.Debugging
{
    /// <summary>A place in somebody's source that produced code.</summary>
    /// <remarks>
    /// <see cref="Page"/> is the memory bank the record was written for, or -1 when the
    /// file did not say. It is carried but not used for matching: a debugger comparing
    /// against PC wants the logical address, and paged code can have several banks claiming
    /// one address. Getting that right needs the bank the machine is actually running, which
    /// is a later problem than reading the file.
    /// </remarks>
    public readonly record struct Z80SourceLocation(string File, int Line, int Page);

    /// <summary>
    /// Which source line produced the code at each address, and the reverse.
    /// </summary>
    /// <remarks>
    /// This is what separates "names instead of numbers" from actually stepping through the
    /// source you wrote. The forward direction answers "what am I looking at" for a
    /// debugger following PC; the reverse answers "where do I put a breakpoint" for someone
    /// pointing at a line.
    /// </remarks>
    public sealed class Z80SourceMap
    {
        private readonly Dictionary<int, Z80SourceLocation> _byAddress = [];
        private readonly Dictionary<string, int> _byLine = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _byAddress.Count;

        /// <summary>Every source file that contributed code, in name order.</summary>
        public IReadOnlyCollection<string> Files => _files;

        /// <summary>The source line that produced the code at an address.</summary>
        public bool TryGetLocation(int address, out Z80SourceLocation location) =>
            _byAddress.TryGetValue(address, out location);

        /// <summary>
        /// The address a source line produced, if it produced any.
        /// </summary>
        /// <remarks>
        /// The first address recorded for the line wins. One line can emit several - a macro
        /// invocation, or a multi-statement line - and the first is where execution of that
        /// line begins, which is what a breakpoint on it should mean.
        /// </remarks>
        public bool TryGetAddress(string file, int line, out int address) =>
            _byLine.TryGetValue(Key(file, line), out address);

        internal void Add(int address, Z80SourceLocation location)
        {
            _byAddress.TryAdd(address, location);
            _byLine.TryAdd(Key(location.File, location.Line), address);
            _files.Add(location.File);
        }

        private static string Key(string file, int line) =>
            $"{file}:{line.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>What came out of a source-level debugging file.</summary>
    public sealed record Z80SldImport(
        Z80SourceMap SourceMap,
        Z80SymbolTable Symbols,
        int TraceRecords,
        int LabelRecords,
        int SkippedLines,
        string? Version)
    {
        public bool IsUsable => TraceRecords > 0 || Symbols.Count > 0;
    }

    /// <summary>
    /// Reads sjasmplus source-level debugging (<c>.sld</c>) files.
    /// </summary>
    /// <remarks>
    /// The format is a line per record with fields separated by <c>|</c>:
    /// <c>source file | source line | definition file | definition line | page | value |
    /// type | data</c>. Metadata lines begin with an empty field - the
    /// <c>|SLD.data.version|</c> marker and the <c>||K|KEYWORDS|...</c> line that
    /// <c>SLDOPT COMMENT</c> produces - and are not records.
    ///
    /// Only two record types matter here. <c>T</c> says code was emitted at an address for a
    /// source line, which is the whole source map. <c>L</c> and <c>D</c> name a label or a
    /// constant, which go into an ordinary symbol table so the disassembler and the inline
    /// assembler can use them exactly as they use an imported <c>.sym</c>.
    ///
    /// Deliberately tolerant: an unrecognised or malformed line is counted and skipped
    /// rather than failing the file, and the counts are reported. This reader was written
    /// against the documented record shape rather than against a file produced by a
    /// toolchain, so a wrong assumption should show up as "understood 0 trace records"
    /// instead of as a confidently wrong source map.
    /// </remarks>
    public static class Z80SldReader
    {
        public static Z80SldImport Parse(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            var map = new Z80SourceMap();
            var symbols = new Z80SymbolTable();
            int traces = 0;
            int labels = 0;
            int skipped = 0;
            string? version = null;

            foreach (string rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = rawLine.TrimEnd();
                if (line.Length == 0)
                {
                    continue;
                }

                string[] fields = line.Split('|');

                // Metadata rather than a record: the leading field is empty.
                if (fields.Length >= 2 && fields[0].Length == 0)
                {
                    if (fields[1].StartsWith("SLD.data.version", StringComparison.OrdinalIgnoreCase)
                        && fields.Length >= 3)
                    {
                        version = fields[2].Trim();
                    }

                    continue;
                }

                if (fields.Length < 7)
                {
                    skipped++;
                    continue;
                }

                string type = fields[6].Trim();
                if (type.Length == 0)
                {
                    skipped++;
                    continue;
                }

                if (!TryParseInt(fields[5], out int value))
                {
                    skipped++;
                    continue;
                }

                int page = TryParseInt(fields[4], out int parsedPage) ? parsedPage : -1;
                string data = fields.Length > 7 ? fields[7] : string.Empty;

                switch (char.ToUpperInvariant(type[0]))
                {
                    case 'T':
                        string file = fields[0].Trim();
                        if (file.Length == 0 || !TryParseInt(fields[1], out int sourceLine))
                        {
                            skipped++;
                            continue;
                        }

                        map.Add(value, new Z80SourceLocation(file, sourceLine, page));
                        traces++;
                        break;

                    case 'L':
                    case 'D':
                        if (TryExtractLabel(data, out string name))
                        {
                            symbols.Add(name, value);
                            labels++;
                        }
                        else
                        {
                            skipped++;
                        }

                        break;

                    default:
                        // Keywords, device and page records carry nothing this needs.
                        break;
                }
            }

            return new Z80SldImport(map, symbols, traces, labels, skipped, version);
        }

        /// <summary>
        /// Pulls a usable name out of a label record's data field.
        /// </summary>
        /// <remarks>
        /// sjasmplus writes the module path and the label as comma-separated parts, with
        /// empty parts where there is no module, so the parts are joined with dots and the
        /// empties dropped. Anything that does not come out looking like an identifier is
        /// refused, because a symbol table full of punctuation is worse than an empty one.
        /// </remarks>
        private static bool TryExtractLabel(string data, out string name)
        {
            name = string.Empty;
            if (data.Length == 0)
            {
                return false;
            }

            string[] parts = data.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                return false;
            }

            string candidate = string.Join('.', parts.Where(static part => !part.Contains(' ')));
            if (candidate.Length == 0 || !(char.IsLetter(candidate[0]) || candidate[0] is '_' or '.'))
            {
                return false;
            }

            foreach (char ch in candidate)
            {
                if (!char.IsLetterOrDigit(ch) && ch is not ('_' or '.' or '@' or '$'))
                {
                    return false;
                }
            }

            name = candidate;
            return true;
        }

        private static bool TryParseInt(string text, out int value) =>
            int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
