using System;
using System.Collections.Generic;
using System.Text;

namespace ZedExEss.Spectrum.Basic
{
    /// <summary>
    /// Converts tokenised ZX Spectrum BASIC program bytes back into editable typed source.
    /// </summary>
    /// <remarks>
    /// Hidden five-byte numeric values following 0x0E are omitted because their printable literal
    /// precedes the marker. Spacing is reconstructed only for tokens whose ROM listing form has an
    /// implicit trailing space; raw control/graphics bytes remain round-trippable as {0xNN}.
    /// </remarks>
    public static class SpectrumBasicDetokenizer
    {
        private const byte NumberMarker = 0x0E;
        public static bool TryDetokenizeProgram(ReadOnlySpan<byte> program, out string source, out string error)
        {
            return TryDetokenizeProgram(program, allow128Tokens: false, out source, out error);
        }
        public static bool TryDetokenizeProgram(ReadOnlySpan<byte> program, bool allow128Tokens, out string source, out string error)
        {
            source = string.Empty;
            error = string.Empty;
            var lines = new List<string>();
            int offset = 0;

            while (offset < program.Length)
            {
                if (program.Length - offset < 4)
                {
                    error = "BASIC program ends in a partial line header.";
                    return false;
                }

                int lineNumber = (program[offset] << 8) | program[offset + 1];
                int bodyLength = program[offset + 2] | (program[offset + 3] << 8);
                offset += 4;
                if (bodyLength <= 0 || offset + bodyLength > program.Length)
                {
                    error = $"BASIC line {lineNumber} has an invalid length.";
                    return false;
                }

                ReadOnlySpan<byte> body = program.Slice(offset, bodyLength);
                if (body[^1] != 0x0D)
                {
                    error = $"BASIC line {lineNumber} is missing its terminator.";
                    return false;
                }

                lines.Add($"{lineNumber} {DetokenizeBody(body[..^1], allow128Tokens)}");
                offset += bodyLength;
            }

            source = string.Join(Environment.NewLine, lines);
            return true;
        }
        private static string DetokenizeBody(ReadOnlySpan<byte> body, bool allow128Tokens)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < body.Length; i++)
            {
                byte value = body[i];
                if (value == NumberMarker && i + 5 < body.Length)
                {
                    i += 5;
                    continue;
                }

                if (SpectrumBasicTokens.TryGetText(value, allow128Tokens, out string tokenText))
                {
                    builder.Append(tokenText);
                    if (HasImplicitDisplaySpace(value) && NeedsDisplaySpaceAfterToken(body, i))
                    {
                        builder.Append(' ');
                    }

                    continue;
                }

                if (value >= 0x20 && value <= 0x7E)
                {
                    builder.Append(value == 0x60 ? '\u00A3' : (char)value);
                    continue;
                }

                builder.Append(CultureInvariantByteEscape(value));
            }

            return builder.ToString();
        }
        private static string CultureInvariantByteEscape(byte value)
        {
            return "{0x" + value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "}";
        }
        private static bool HasImplicitDisplaySpace(byte token)
        {
            return token >= 0xA3 && token is not 0xC7 and not 0xC8 and not 0xC9;
        }
        private static bool NeedsDisplaySpaceAfterToken(ReadOnlySpan<byte> body, int tokenIndex)
        {
            int nextIndex = tokenIndex + 1;
            if (nextIndex >= body.Length)
            {
                return false;
            }

            byte next = body[nextIndex];
            return next != 0x20
                && next != 0x0D
                && next != (byte)','
                && next != (byte)';'
                && next != (byte)':'
                && next != (byte)')';
        }
    }
}
