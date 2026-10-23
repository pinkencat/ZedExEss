using System;
using System.Collections.Generic;
using System.Linq;

namespace ZedExEss.Spectrum.Basic
{
    /// <summary>
    /// ZX Spectrum BASIC token table and source aliases used by the editor tokenizer.
    /// </summary>
    internal static class SpectrumBasicTokens
    {
        private static readonly string?[] TokenTexts = new string?[256];
        private static readonly Dictionary<string, byte> TokenByText = new(StringComparer.OrdinalIgnoreCase);
        private static readonly KeywordEntry[] KeywordEntries;
        private static readonly string?[] TokenTexts128 = new string?[256];
        private static readonly Dictionary<string, byte> TokenByText128 = new(StringComparer.OrdinalIgnoreCase);
        private static readonly KeywordEntry[] KeywordEntries128;

        static SpectrumBasicTokens()
        {
            Add(0xA5, "RND");
            Add(0xA6, "INKEY$");
            Add(0xA7, "PI");
            Add(0xA8, "FN");
            Add(0xA9, "POINT");
            Add(0xAA, "SCREEN$");
            Add(0xAB, "ATTR");
            Add(0xAC, "AT");
            Add(0xAD, "TAB");
            Add(0xAE, "VAL$");
            Add(0xAF, "CODE");
            Add(0xB0, "VAL");
            Add(0xB1, "LEN");
            Add(0xB2, "SIN");
            Add(0xB3, "COS");
            Add(0xB4, "TAN");
            Add(0xB5, "ASN");
            Add(0xB6, "ACS");
            Add(0xB7, "ATN");
            Add(0xB8, "LN");
            Add(0xB9, "EXP");
            Add(0xBA, "INT");
            Add(0xBB, "SQR");
            Add(0xBC, "SGN");
            Add(0xBD, "ABS");
            Add(0xBE, "PEEK");
            Add(0xBF, "IN");
            Add(0xC0, "USR");
            Add(0xC1, "STR$");
            Add(0xC2, "CHR$");
            Add(0xC3, "NOT");
            Add(0xC4, "BIN");
            Add(0xC5, "OR");
            Add(0xC6, "AND");
            Add(0xC7, "<=");
            Add(0xC8, ">=");
            Add(0xC9, "<>");
            Add(0xCA, "LINE");
            Add(0xCB, "THEN");
            Add(0xCC, "TO");
            Add(0xCD, "STEP");
            Add(0xCE, "DEF FN");
            Add(0xCF, "CAT");
            Add(0xD0, "FORMAT");
            Add(0xD1, "MOVE");
            Add(0xD2, "ERASE");
            Add(0xD3, "OPEN #");
            Add(0xD4, "CLOSE #");
            Add(0xD5, "MERGE");
            Add(0xD6, "VERIFY");
            Add(0xD7, "BEEP");
            Add(0xD8, "CIRCLE");
            Add(0xD9, "INK");
            Add(0xDA, "PAPER");
            Add(0xDB, "FLASH");
            Add(0xDC, "BRIGHT");
            Add(0xDD, "INVERSE");
            Add(0xDE, "OVER");
            Add(0xDF, "OUT");
            Add(0xE0, "LPRINT");
            Add(0xE1, "LLIST");
            Add(0xE2, "STOP");
            Add(0xE3, "READ");
            Add(0xE4, "DATA");
            Add(0xE5, "RESTORE");
            Add(0xE6, "NEW");
            Add(0xE7, "BORDER");
            Add(0xE8, "CONTINUE");
            Add(0xE9, "DIM");
            Add(0xEA, "REM");
            Add(0xEB, "FOR");
            Add(0xEC, "GO TO");
            Add(0xED, "GO SUB");
            Add(0xEE, "INPUT");
            Add(0xEF, "LOAD");
            Add(0xF0, "LIST");
            Add(0xF1, "LET");
            Add(0xF2, "PAUSE");
            Add(0xF3, "NEXT");
            Add(0xF4, "POKE");
            Add(0xF5, "PRINT");
            Add(0xF6, "PLOT");
            Add(0xF7, "RUN");
            Add(0xF8, "SAVE");
            Add(0xF9, "RANDOMIZE");
            Add(0xFA, "IF");
            Add(0xFB, "CLS");
            Add(0xFC, "DRAW");
            Add(0xFD, "CLEAR");
            Add(0xFE, "RETURN");
            Add(0xFF, "COPY");

            AddAlias("GOTO", 0xEC);
            AddAlias("GO TO", 0xEC);
            AddAlias("GOSUB", 0xED);
            AddAlias("GO SUB", 0xED);
            AddAlias("RAND", 0xF9);
            AddAlias("RANDOMIZE", 0xF9);
            AddAlias("CONT", 0xE8);
            AddAlias("CONTINUE", 0xE8);

            KeywordEntries = TokenByText
                .Select(static pair => new KeywordEntry(pair.Key, pair.Value))
                .OrderByDescending(static entry => entry.Text.Length)
                .ThenBy(static entry => entry.Text, StringComparer.Ordinal)
                .ToArray();

            Array.Copy(TokenTexts, TokenTexts128, TokenTexts.Length);
            foreach (KeyValuePair<string, byte> entry in TokenByText)
            {
                TokenByText128[entry.Key] = entry.Value;
            }

            Add128(0xA3, "SPECTRUM");
            Add128(0xA4, "PLAY");

            KeywordEntries128 = TokenByText128
                .Select(static pair => new KeywordEntry(pair.Key, pair.Value))
                .OrderByDescending(static entry => entry.Text.Length)
                .ThenBy(static entry => entry.Text, StringComparer.Ordinal)
                .ToArray();
        }

        public static IReadOnlyList<KeywordEntry> Keywords => KeywordEntries;
        public static IReadOnlyList<KeywordEntry> GetKeywords(bool allow128Tokens) => allow128Tokens ? KeywordEntries128 : KeywordEntries;
        public static bool TryGetText(byte token, out string text)
        {
            return TryGetText(token, allow128Tokens: false, out text);
        }
        public static bool TryGetText(byte token, bool allow128Tokens, out string text)
        {
            text = TokenTexts[token] ?? string.Empty;
            if (allow128Tokens && text.Length == 0)
            {
                text = TokenTexts128[token] ?? string.Empty;
            }

            return text.Length > 0;
        }
        private static void Add(byte token, string text)
        {
            TokenTexts[token] = text;
            AddAlias(text, token);
        }
        private static void AddAlias(string text, byte token)
        {
            TokenByText[text] = token;
        }
        private static void Add128(byte token, string text)
        {
            TokenTexts128[token] = text;
            TokenByText128[text] = token;
        }
        /// <summary>Source spelling paired with the byte emitted for that BASIC keyword.</summary>
        internal readonly struct KeywordEntry(string text, byte token)
        {
            public string Text { get; } = text;
            public byte Token { get; } = token;
        }
    }
}
