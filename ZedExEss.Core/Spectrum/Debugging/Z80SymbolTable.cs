using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZedExEss.Spectrum.Debugging
{
    /// <summary>
    /// Names for addresses, imported from whatever an assembler wrote out.
    /// </summary>
    /// <remarks>
    /// The point is to debug code you built elsewhere. A disassembly of somebody's game
    /// reads as `CALL $8034`; a disassembly of your own code should read as
    /// `CALL init_screen`, and you should be able to type that name into a breakpoint or an
    /// inline patch. Nothing here assembles anything - it only carries the names.
    ///
    /// The reader is deliberately format-tolerant rather than tied to one assembler. Every
    /// common symbol dump is a line per symbol carrying a name and a number in some order,
    /// so that is what is parsed, and anything unrecognised is skipped and counted rather
    /// than failing the whole file: a symbol list is a convenience, and refusing to load
    /// three thousand good names because of one odd line would not be one.
    /// </remarks>
    public sealed class Z80SymbolTable
    {
        private readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, string> _byAddress = [];

        public int Count => _byName.Count;

        /// <summary>Lines the reader could not make sense of.</summary>
        public int SkippedLines { get; private set; }

        public IReadOnlyDictionary<string, int> ByName => _byName;

        /// <summary>The name for an address, if one was imported.</summary>
        /// <remarks>
        /// One name per address. When several share one, the first wins, because assemblers
        /// emit aliases in definition order and the first is usually the real name.
        /// </remarks>
        public bool TryGetName(int address, out string name) => _byAddress.TryGetValue(address, out name!);

        public bool TryGetAddress(string name, out int address) => _byName.TryGetValue(name, out address);

        public void Add(string name, int address)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            _byName[name] = address;
            _byAddress.TryAdd(address, name);
        }

        public void Clear()
        {
            _byName.Clear();
            _byAddress.Clear();
            SkippedLines = 0;
        }

        /// <summary>
        /// Reads a symbol dump, accepting the shapes common assemblers produce.
        /// </summary>
        /// <remarks>
        /// Handles <c>name: EQU $1234</c> and <c>name = $1234</c> as sjasmplus writes with
        /// <c>--sym</c>, <c>name EQU 1234h</c>, the bare <c>name 1234</c> and the
        /// address-first <c>1234 name</c> that plain <c>.lbl</c> files use. Comments after
        /// <c>;</c> or <c>//</c> are ignored.
        ///
        /// Not handled: sjasmplus <c>.sld</c>, which carries source file and line numbers as
        /// well as addresses. That is the input for stepping through source rather than for
        /// naming addresses, and wants its own reader.
        /// </remarks>
        public static Z80SymbolTable Parse(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            var table = new Z80SymbolTable();
            foreach (string rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = StripComment(rawLine).Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (!TryParseLine(line, out string name, out int address))
                {
                    table.SkippedLines++;
                    continue;
                }

                table.Add(name, address);
            }

            return table;
        }

        private static string StripComment(string line)
        {
            int semicolon = line.IndexOf(';');
            if (semicolon >= 0)
            {
                line = line[..semicolon];
            }

            int slashes = line.IndexOf("//", StringComparison.Ordinal);
            return slashes >= 0 ? line[..slashes] : line;
        }

        private static bool TryParseLine(string line, out string name, out int address)
        {
            name = string.Empty;
            address = 0;

            // Reduce every separator an assembler might use to whitespace, then look at
            // what is left: a name and a number, in one order or the other.
            string normalized = line
                .Replace(":", " ")
                .Replace("=", " ")
                .Replace(",", " ");

            string[] parts = normalized.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var words = new List<string>(parts.Length);
            foreach (string part in parts)
            {
                // EQU is punctuation here, not a name.
                if (!part.Equals("EQU", StringComparison.OrdinalIgnoreCase)
                    && !part.Equals("DEFL", StringComparison.OrdinalIgnoreCase))
                {
                    words.Add(part);
                }
            }

            if (words.Count != 2)
            {
                return false;
            }

            if (TryParseAddress(words[1], out address) && IsName(words[0]))
            {
                name = words[0];
                return true;
            }

            if (TryParseAddress(words[0], out address) && IsName(words[1]))
            {
                name = words[1];
                return true;
            }

            return false;
        }

        /// <summary>A name has to start like an identifier, or a hex number could pass for one.</summary>
        private static bool IsName(string text)
        {
            if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] is '_' or '.'))
            {
                return false;
            }

            foreach (char ch in text)
            {
                if (!char.IsLetterOrDigit(ch) && ch is not ('_' or '.' or '@' or '$'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryParseAddress(string text, out int address)
        {
            address = 0;
            if (text.Length == 0)
            {
                return false;
            }

            if (text[0] == '$' && text.Length > 1)
            {
                return int.TryParse(
                    text[1..],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out address);
            }

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && text.Length > 2)
            {
                return int.TryParse(
                    text[2..],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out address);
            }

            if (text[^1] is 'h' or 'H' && text.Length > 1)
            {
                return int.TryParse(
                    text[..^1],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out address);
            }

            // A bare run of hex digits is how .lbl files write addresses; decimal is how
            // plenty of map files do. Hex wins when it could be either, because a symbol
            // dump full of four-digit numbers is a dump of addresses.
            if (text.Length == 4
                && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address))
            {
                return true;
            }

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out address);
        }
    }
}
