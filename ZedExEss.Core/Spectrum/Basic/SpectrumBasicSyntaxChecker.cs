using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZedExEss.Spectrum.Basic
{
    /// <summary>
    /// Lightweight source validator that catches malformed statements before memory injection.
    /// </summary>
    /// <remarks>
    /// This is intentionally a guardrail rather than a replacement for the ROM parser. It catches
    /// structural mistakes that would certainly create unusable token streams while leaving full
    /// expression grammar and runtime type checking to Sinclair BASIC.
    /// </remarks>
    public static class SpectrumBasicSyntaxChecker
    {
        private static readonly string[] StatementStarters =
        [
            "BEEP", "BORDER", "BRIGHT", "CAT", "CIRCLE", "CLEAR", "CLOSE #", "CLS", "CONT", "CONTINUE",
            "COPY", "DATA", "DEF FN", "DIM", "DRAW", "ERASE", "FLASH", "FOR", "FORMAT", "GO SUB",
            "GO TO", "GOSUB", "GOTO", "IF", "INK", "INPUT", "INVERSE", "LET", "LIST", "LLIST", "LOAD",
            "LPRINT", "MERGE", "MOVE", "NEW", "NEXT", "OPEN #", "OUT", "OVER", "PAPER", "PAUSE",
            "PLOT", "POKE", "PRINT", "RANDOMIZE", "RAND", "READ", "REM", "RESTORE", "RETURN", "RUN",
            "SAVE", "STOP", "VERIFY"
        ];
        private static readonly string[] StatementStarters128 = [.. StatementStarters, "PLAY", "SPECTRUM"];
        public static bool TryValidateSource(string source, out string error)
        {
            return TryValidateSource(source, allow128Tokens: false, out error);
        }
        public static bool TryValidateSource(string source, bool allow128Tokens, out string error)
        {
            error = string.Empty;
            string[] lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string rawLine = lines[i];
                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    continue;
                }

                if (!TryValidateLine(rawLine, i + 1, allow128Tokens, out error))
                {
                    return false;
                }
            }

            return true;
        }
        private static bool TryValidateLine(string rawLine, int sourceLineNumber, bool allow128Tokens, out string error)
        {
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
                || lineNumber > 65535)
            {
                error = $"Source line {sourceLineNumber} has an invalid BASIC line number.";
                return false;
            }

            string body = index < text.Length ? text[index..].TrimStart() : string.Empty;
            if (!ValidateLineCharacters(body, lineNumber, out error))
            {
                return false;
            }

            foreach (string statement in SplitStatements(body))
            {
                if (!TryValidateStatement(statement, lineNumber, allow128Tokens, out error))
                {
                    return false;
                }
            }

            return true;
        }
        private static bool ValidateLineCharacters(string body, int lineNumber, out string error)
        {
            error = string.Empty;
            bool inString = false;
            int parenDepth = 0;

            for (int i = 0; i < body.Length; i++)
            {
                char ch = body[i];
                if (ch == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString && ch == '(')
                {
                    parenDepth++;
                    continue;
                }

                if (!inString && ch == ')')
                {
                    parenDepth--;
                    if (parenDepth < 0)
                    {
                        error = $"Line {lineNumber} has a closing parenthesis without a matching opening parenthesis.";
                        return false;
                    }
                }

                if (ch == '{' && LooksLikeRawByteEscape(body, i, out bool valid, out int length))
                {
                    if (!valid)
                    {
                        error = $"Line {lineNumber} has an invalid raw byte escape. Use {{0xNN}}.";
                        return false;
                    }

                    i += length - 1;
                }
            }

            if (inString)
            {
                error = $"Line {lineNumber} has an unterminated string.";
                return false;
            }

            if (parenDepth != 0)
            {
                error = $"Line {lineNumber} has unbalanced parentheses.";
                return false;
            }

            return true;
        }
        private static IEnumerable<string> SplitStatements(string body)
        {
            bool inString = false;
            bool inRem = false;
            int start = 0;
            for (int i = 0; i < body.Length; i++)
            {
                char ch = body[i];
                if (ch == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString && !inRem && StartsWithWord(body, i, "REM"))
                {
                    inRem = true;
                    continue;
                }

                if (!inString && !inRem && ch == ':')
                {
                    yield return body[start..i];
                    start = i + 1;
                }
            }

            yield return body[start..];
        }
        private static bool TryValidateStatement(string statement, int lineNumber, bool allow128Tokens, out string error)
        {
            error = string.Empty;
            string text = statement.Trim();
            if (text.Length == 0)
            {
                return true;
            }

            string? starter = MatchStatementStarter(text, allow128Tokens);
            if (starter == null)
            {
                error = $"Line {lineNumber} contains a statement that does not start with a recognised BASIC command: {text}";
                return false;
            }

            string rest = text[starter.Length..].TrimStart();
            string normalizedStarter = starter.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);
            if ((normalizedStarter == "GOTO" || normalizedStarter == "GOSUB") && rest.Length == 0)
            {
                error = $"Line {lineNumber} has {starter} without a target expression.";
                return false;
            }

            if (starter.Equals("IF", StringComparison.OrdinalIgnoreCase) && !ContainsThenOutsideString(rest))
            {
                error = $"Line {lineNumber} has IF without THEN.";
                return false;
            }

            if (starter.Equals("LET", StringComparison.OrdinalIgnoreCase) && !ContainsOutsideString(rest, '='))
            {
                error = $"Line {lineNumber} has LET without '='.";
                return false;
            }

            return true;
        }
        private static string? MatchStatementStarter(string text, bool allow128Tokens)
        {
            string[] starters = allow128Tokens ? StatementStarters128 : StatementStarters;
            for (int i = 0; i < starters.Length; i++)
            {
                string starter = starters[i];
                if (StartsWithWord(text, 0, starter))
                {
                    return starter;
                }
            }

            return null;
        }
        private static bool StartsWithWord(string text, int index, string word)
        {
            if (index + word.Length > text.Length)
            {
                return false;
            }

            for (int i = 0; i < word.Length; i++)
            {
                if (char.ToUpperInvariant(text[index + i]) != word[i])
                {
                    return false;
                }
            }

            int end = index + word.Length;
            return end >= text.Length || !IsIdentifierChar(text[end]);
        }
        private static bool IsIdentifierChar(char ch)
        {
            return char.IsLetterOrDigit(ch) || ch == '$' || ch == '_';
        }
        private static bool ContainsThenOutsideString(string text)
        {
            bool inString = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString && StartsWithWord(text, i, "THEN"))
                {
                    return true;
                }
            }

            return false;
        }
        private static bool ContainsOutsideString(string text, char needle)
        {
            bool inString = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (!inString && text[i] == needle)
                {
                    return true;
                }
            }

            return false;
        }
        private static bool LooksLikeRawByteEscape(string text, int index, out bool valid, out int length)
        {
            valid = false;
            length = 0;
            int end = text.IndexOf('}', index + 1);
            if (end < 0)
            {
                return false;
            }

            string inner = text[(index + 1)..end].Trim();
            if (!inner.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && !inner.StartsWith('$')
                && !inner.StartsWith('#'))
            {
                return false;
            }

            if (inner.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                inner = inner[2..];
            }
            else
            {
                inner = inner[1..];
            }

            valid = byte.TryParse(inner, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
            length = end - index + 1;
            return true;
        }
    }
}
