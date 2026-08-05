using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZedExEss.Spectrum.Basic
{
    /// <summary>
    /// Converts typed Sinclair BASIC source into the tokenised byte stream stored after PROG.
    /// </summary>
    /// <remarks>
    /// Keywords are matched longest-first outside strings and REM text. Numeric text is retained
    /// for LIST while a 0x0E marker and five-byte Spectrum number are appended for the ROM's
    /// evaluator. Each returned line already includes its terminating 0x0D.
    /// </remarks>
    public static class SpectrumBasicTokenizer
    {
        private const byte NumberMarker = 0x0E;
        public static bool TryTokenizeProgram(string source, out byte[] program, out string error)
        {
            return TryTokenizeProgram(source, allow128Tokens: false, out program, out error);
        }
        public static bool TryTokenizeProgram(string source, bool allow128Tokens, out byte[] program, out string error)
        {
            program = [];
            error = string.Empty;

            var lines = new List<TokenizedLine>();
            var usedLineNumbers = new HashSet<int>();
            string[] sourceLines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < sourceLines.Length; i++)
            {
                string rawLine = sourceLines[i];
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                if (!TryTokenizeLine(rawLine, i + 1, allow128Tokens, out TokenizedLine line, out error))
                {
                    return false;
                }

                if (!usedLineNumbers.Add(line.Number))
                {
                    error = $"Duplicate BASIC line number {line.Number}.";
                    return false;
                }

                lines.Add(line);
            }

            // The ROM expects the program area to be ordered even if source was pasted out of order.
            lines.Sort(static (left, right) => left.Number.CompareTo(right.Number));
            var output = new List<byte>();
            for (int i = 0; i < lines.Count; i++)
            {
                TokenizedLine line = lines[i];
                if (line.Body.Length > ushort.MaxValue)
                {
                    error = $"Line {line.Number} is too long.";
                    return false;
                }

                output.Add((byte)(line.Number >> 8));
                output.Add((byte)(line.Number & 0xFF));
                output.Add((byte)(line.Body.Length & 0xFF));
                output.Add((byte)(line.Body.Length >> 8));
                output.AddRange(line.Body);
            }

            program = output.ToArray();
            return true;
        }
        private static bool TryTokenizeLine(string rawLine, int sourceLineNumber, bool allow128Tokens, out TokenizedLine line, out string error)
        {
            line = default;
            error = string.Empty;
            string text = rawLine.TrimStart();
            int index = 0;
            while (index < text.Length && char.IsDigit(text[index]))
            {
                index++;
            }

            if (index == 0)
            {
                error = $"Source line {sourceLineNumber} does not start with a BASIC line number.";
                return false;
            }

            if (!int.TryParse(text[..index], NumberStyles.None, CultureInfo.InvariantCulture, out int lineNumber)
                || lineNumber < 0
                || lineNumber > 9999)
            {
                error = $"Source line {sourceLineNumber} has an invalid BASIC line number.";
                return false;
            }

            string bodyText = index < text.Length ? text[index..].TrimStart() : string.Empty;
            if (!TryTokenizeBody(bodyText, lineNumber, allow128Tokens, out byte[] body, out error))
            {
                return false;
            }

            line = new TokenizedLine(lineNumber, body);
            return true;
        }
        private static bool TryTokenizeBody(string text, int lineNumber, bool allow128Tokens, out byte[] body, out string error)
        {
            body = [];
            error = string.Empty;
            var output = new List<byte>();
            bool inString = false;
            bool inRem = false;
            int index = 0;

            while (index < text.Length)
            {
                char ch = text[index];

                if (ch == '{' && TryParseByteEscape(text, index, out byte escaped, out int escapeLength))
                {
                    output.Add(escaped);
                    index += escapeLength;
                    continue;
                }

                if (ch == '"')
                {
                    inString = !inString;
                    output.Add((byte)'"');
                    index++;
                    continue;
                }

                if (!inString && !inRem && TryMatchKeyword(text, index, allow128Tokens, out byte token, out int tokenLength))
                {
                    output.Add(token);
                    if (token == 0xEA)
                    {
                        inRem = true;
                    }

                    index += tokenLength;
                    if (HasImplicitDisplaySpace(token))
                    {
                        while (index < text.Length && (text[index] == ' ' || text[index] == '\t'))
                        {
                            index++;
                        }
                    }

                    continue;
                }

                if (!inString && !inRem && IsNumberStart(text, index) && TryReadNumberLiteral(text, index, out string literal, out double value))
                {
                    AddAsciiLiteral(output, literal, lineNumber);
                    output.Add(NumberMarker);
                    output.AddRange(SpectrumNumberEncoder.Encode(value));
                    index += literal.Length;
                    continue;
                }

                if (!TryMapSourceChar(ch, out byte mapped))
                {
                    error = $"Line {lineNumber} contains unsupported character U+{(int)ch:X4}. Use {{0xNN}} for raw Spectrum bytes.";
                    return false;
                }

                output.Add(mapped);
                index++;
            }

            output.Add(0x0D);
            body = output.ToArray();
            return true;
        }
        private static bool TryMatchKeyword(string text, int index, bool allow128Tokens, out byte token, out int length)
        {
            foreach (SpectrumBasicTokens.KeywordEntry entry in SpectrumBasicTokens.GetKeywords(allow128Tokens))
            {
                if (MatchesKeyword(text, index, entry.Text))
                {
                    token = entry.Token;
                    length = entry.Text.Length;
                    return true;
                }
            }

            token = 0;
            length = 0;
            return false;
        }
        private static bool MatchesKeyword(string text, int index, string keyword)
        {
            if (index + keyword.Length > text.Length)
            {
                return false;
            }

            bool startsWithIdentifier = IsIdentifierChar(keyword[0]);
            if (startsWithIdentifier && index > 0 && IsIdentifierChar(text[index - 1]))
            {
                return false;
            }

            for (int i = 0; i < keyword.Length; i++)
            {
                char expected = keyword[i];
                char actual = text[index + i];
                if (expected == ' ')
                {
                    if (actual != ' ')
                    {
                        return false;
                    }
                }
                else if (char.ToUpperInvariant(actual) != expected)
                {
                    return false;
                }
            }

            int end = index + keyword.Length;
            char last = keyword[^1];
            return !IsIdentifierChar(last) || end >= text.Length || !IsIdentifierChar(text[end]);
        }
        private static bool IsIdentifierChar(char ch)
        {
            return char.IsLetterOrDigit(ch) || ch == '$' || ch == '_';
        }
        private static bool HasImplicitDisplaySpace(byte token)
        {
            return token >= 0xA3 && token is not 0xC7 and not 0xC8 and not 0xC9;
        }
        private static bool IsNumberStart(string text, int index)
        {
            char ch = text[index];
            if (char.IsDigit(ch))
            {
                return true;
            }

            return ch == '.' && index + 1 < text.Length && char.IsDigit(text[index + 1]);
        }
        private static bool TryReadNumberLiteral(string text, int index, out string literal, out double value)
        {
            int start = index;
            bool sawDigit = false;
            while (index < text.Length && char.IsDigit(text[index]))
            {
                sawDigit = true;
                index++;
            }

            if (index < text.Length && text[index] == '.')
            {
                index++;
                while (index < text.Length && char.IsDigit(text[index]))
                {
                    sawDigit = true;
                    index++;
                }
            }

            if (!sawDigit)
            {
                literal = string.Empty;
                value = 0;
                return false;
            }

            if (index < text.Length && (text[index] == 'E' || text[index] == 'e'))
            {
                int exponentStart = index;
                index++;
                if (index < text.Length && (text[index] == '+' || text[index] == '-'))
                {
                    index++;
                }

                int exponentDigitsStart = index;
                while (index < text.Length && char.IsDigit(text[index]))
                {
                    index++;
                }

                if (exponentDigitsStart == index)
                {
                    index = exponentStart;
                }
            }

            literal = text[start..index];
            return double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        private static bool TryParseByteEscape(string text, int index, out byte value, out int length)
        {
            value = 0;
            length = 0;
            int end = text.IndexOf('}', index + 1);
            if (end < 0)
            {
                return false;
            }

            string inner = text[(index + 1)..end].Trim();
            if (inner.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                inner = inner[2..];
            }
            else if (inner.StartsWith('$') || inner.StartsWith('#'))
            {
                inner = inner[1..];
            }

            if (!byte.TryParse(inner, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }

            length = end - index + 1;
            return true;
        }
        private static void AddAsciiLiteral(List<byte> output, string literal, int lineNumber)
        {
            for (int i = 0; i < literal.Length; i++)
            {
                if (!TryMapSourceChar(literal[i], out byte mapped))
                {
                    throw new InvalidOperationException($"Line {lineNumber} contains an invalid numeric literal character.");
                }

                output.Add(mapped);
            }
        }
        private static bool TryMapSourceChar(char ch, out byte value)
        {
            if (ch == '\t')
            {
                value = 0x20;
                return true;
            }

            if (ch >= 0x20 && ch <= 0x7E)
            {
                value = (byte)ch;
                return true;
            }

            if (ch == '£')
            {
                value = 0x60;
                return true;
            }

            value = 0;
            return false;
        }
        private readonly struct TokenizedLine(int number, byte[] body)
        {
            public int Number { get; } = number;
            public byte[] Body { get; } = body;
        }
    }
    /// <summary>Encodes CLR numbers in the five-byte format used by the Spectrum calculator.</summary>
    /// <remarks>
    /// Small integral values use the ROM's exponent-zero compact representation; all others use
    /// a biased exponent and sign/mantissa form. The printable source digits are stored separately
    /// by the tokenizer and are therefore not part of this payload.
    /// </remarks>
    internal static class SpectrumNumberEncoder
    {
        public static byte[] Encode(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return [0, 0, 0, 0, 0];
            }

            double rounded = Math.Round(value);
            if (Math.Abs(value - rounded) < 1e-10 && rounded >= -65535 && rounded <= 65535)
            {
                int integer = (int)Math.Abs(rounded);
                return [
                    0x00,
                    rounded < 0 ? (byte)0xFF : (byte)0x00,
                    (byte)(integer & 0xFF),
                    (byte)(integer >> 8),
                    0x00
                ];
            }

            if (value == 0.0)
            {
                return [0, 0, 0, 0, 0];
            }

            bool negative = value < 0;
            double abs = Math.Abs(value);
            int exponent = (int)Math.Floor(Math.Log(abs, 2.0)) + 1;
            double mantissa = abs / Math.Pow(2.0, exponent);
            double fraction = Math.Clamp(mantissa - 0.5, 0.0, 0.49999999976716936);
            uint scaled = (uint)Math.Round(fraction * 4294967296.0);
            if (scaled >= 0x80000000)
            {
                scaled = 0x7FFFFFFF;
            }

            return [
                (byte)(exponent + 128),
                (byte)((negative ? 0x80u : 0x00u) | ((scaled >> 24) & 0x7Fu)),
                (byte)(scaled >> 16),
                (byte)(scaled >> 8),
                (byte)scaled
            ];
        }
    }
}
