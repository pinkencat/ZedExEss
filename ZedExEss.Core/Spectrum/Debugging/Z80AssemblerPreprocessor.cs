using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ZedExEss.Spectrum.Debugging
{
    /// <summary>One line of source after expansion, and where it came from.</summary>
    /// <remarks>
    /// <see cref="LineNumber"/> is always a line the user can see - the invocation, not a
    /// position inside a macro body - because an error pointing into text nobody wrote is
    /// the thing that makes macro assemblers unpleasant. <see cref="Origin"/> carries the
    /// rest of the story for the diagnostic to mention.
    /// </remarks>
    public readonly record struct Z80PreprocessedLine(int LineNumber, string Text, string? Origin);

    /// <summary>
    /// Expands macros, repetition, conditionals and includes before anything is assembled.
    /// </summary>
    /// <remarks>
    /// A separate pass rather than something the assembler does inline, for one structural
    /// reason: the assembler's first pass fixes addresses by walking lines and adding up
    /// instruction widths, and expansion changes how many instructions exist. Doing both at
    /// once would mean the width of a line could not be known when its address was needed.
    /// Expanding first leaves the two-pass assembler exactly as it was, working on a longer
    /// list of lines that each remember where they came from.
    ///
    /// Conditions are evaluated here, so they can only use constants this pass has already
    /// read - an <c>EQU</c> above the <c>IF</c>, or a macro argument. A condition cannot
    /// depend on a label's address, because addresses are not known until the pass after
    /// this one. That is the usual arrangement and the usual limitation.
    /// </remarks>
    public static class Z80AssemblerPreprocessor
    {
        /// <summary>How deep macros, repeats and includes may nest before it is a mistake.</summary>
        private const int MaximumDepth = 32;

        /// <summary>A ceiling on expansion, so a runaway REPT fails instead of hanging.</summary>
        private const int MaximumExpandedLines = 200_000;

        public static List<Z80PreprocessedLine> Expand(
            string source,
            Func<string, string?>? includeResolver,
            List<Z80AssemblyDiagnostic> diagnostics)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(diagnostics);

            var state = new ExpansionState(includeResolver, diagnostics);
            var output = new List<Z80PreprocessedLine>();
            state.Run(SplitLines(source, origin: null), output, depth: 0);
            return output;
        }

        /// <summary>True when the source contains anything this pass has to act on.</summary>
        /// <remarks>
        /// Lets the assembler skip the pass entirely for the short patches it is mostly
        /// used for, so the common case pays nothing for a feature it is not using.
        /// </remarks>
        public static bool NeedsExpansion(string source)
        {
            ArgumentNullException.ThrowIfNull(source);

            foreach (string directive in Directives)
            {
                if (source.Contains(directive, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static readonly string[] Directives =
        [
            "MACRO", "ENDM", "REPT", "ENDR", "IF", "ELSE", "ENDIF", "INCLUDE"
        ];

        private static List<RawLine> SplitLines(string source, string? origin)
        {
            string[] raw = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var lines = new List<RawLine>(raw.Length);
            for (int index = 0; index < raw.Length; index++)
            {
                lines.Add(new RawLine(index + 1, raw[index], origin));
            }

            return lines;
        }

        private sealed record MacroDefinition(string Name, List<string> Parameters, List<RawLine> Body);

        private readonly record struct RawLine(int LineNumber, string Text, string? Origin);

        private sealed class ExpansionState(
            Func<string, string?>? includeResolver,
            List<Z80AssemblyDiagnostic> diagnostics)
        {
            private readonly Dictionary<string, MacroDefinition> _macros =
                new(StringComparer.OrdinalIgnoreCase);

            private readonly Dictionary<string, int> _constants =
                new(StringComparer.OrdinalIgnoreCase);

            private int _expansionCounter;

            public void Run(List<RawLine> lines, List<Z80PreprocessedLine> output, int depth)
            {
                if (depth > MaximumDepth)
                {
                    Fail(lines.Count > 0 ? lines[0] : default, "Expansion nested too deeply.");
                    return;
                }

                for (int index = 0; index < lines.Count; index++)
                {
                    if (diagnostics.Count > 0 && diagnostics.Count >= Z80InlineAssembler.MaximumDiagnostics)
                    {
                        return;
                    }

                    if (output.Count > MaximumExpandedLines)
                    {
                        Fail(lines[index], "Expansion produced too many lines; check a REPT count.");
                        return;
                    }

                    RawLine line = lines[index];
                    string text = StripComment(line.Text).Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    string keyword = FirstWord(text);

                    if (keyword.Equals("MACRO", StringComparison.OrdinalIgnoreCase))
                    {
                        index = DefineMacro(lines, index, text);
                        continue;
                    }

                    if (keyword.Equals("REPT", StringComparison.OrdinalIgnoreCase))
                    {
                        index = ExpandRepeat(lines, index, text, output, depth);
                        continue;
                    }

                    if (keyword.Equals("IF", StringComparison.OrdinalIgnoreCase)
                        || keyword.Equals("IFDEF", StringComparison.OrdinalIgnoreCase)
                        || keyword.Equals("IFNDEF", StringComparison.OrdinalIgnoreCase))
                    {
                        index = ExpandConditional(lines, index, text, keyword, output, depth);
                        continue;
                    }

                    if (keyword.Equals("INCLUDE", StringComparison.OrdinalIgnoreCase))
                    {
                        ExpandInclude(line, text, output, depth);
                        continue;
                    }

                    if (_macros.TryGetValue(keyword, out MacroDefinition? macro))
                    {
                        ExpandMacro(line, text, macro, output, depth);
                        continue;
                    }

                    // An EQU whose value is a plain number is usable by a later condition.
                    RememberConstant(text);
                    output.Add(new Z80PreprocessedLine(line.LineNumber, line.Text, line.Origin));
                }
            }

            /// <summary>Reads a macro body up to its ENDM, returning the line it ended on.</summary>
            private int DefineMacro(List<RawLine> lines, int index, string text)
            {
                string header = text[5..].Trim();
                string name = FirstWord(header);
                if (name.Length == 0)
                {
                    Fail(lines[index], "MACRO needs a name.");
                    return index;
                }

                var parameters = header[name.Length..]
                    .Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();

                var body = new List<RawLine>();
                int nesting = 1;
                int cursor = index + 1;
                for (; cursor < lines.Count; cursor++)
                {
                    string inner = StripComment(lines[cursor].Text).Trim();
                    string word = FirstWord(inner);
                    if (word.Equals("MACRO", StringComparison.OrdinalIgnoreCase))
                    {
                        nesting++;
                    }
                    else if (word.Equals("ENDM", StringComparison.OrdinalIgnoreCase))
                    {
                        nesting--;
                        if (nesting == 0)
                        {
                            break;
                        }
                    }

                    body.Add(lines[cursor]);
                }

                if (nesting != 0)
                {
                    Fail(lines[index], $"MACRO '{name}' has no ENDM.");
                    return lines.Count;
                }

                _macros[name] = new MacroDefinition(name, parameters, body);
                return cursor;
            }

            /// <summary>
            /// Substitutes arguments, renames the body's local labels, and expands it.
            /// </summary>
            /// <remarks>
            /// The renaming is what makes a macro usable twice. Labels written as locals in
            /// the body get a per-invocation suffix, so two invocations inside one enclosing
            /// label do not define the same symbol - which is the failure that makes people
            /// give up on macros and hand-number their labels instead.
            /// </remarks>
            private void ExpandMacro(
                RawLine line,
                string text,
                MacroDefinition macro,
                List<Z80PreprocessedLine> output,
                int depth)
            {
                string argumentText = text[macro.Name.Length..].Trim();
                List<string> arguments = SplitArguments(argumentText);
                if (arguments.Count != macro.Parameters.Count)
                {
                    Fail(
                        line,
                        $"Macro '{macro.Name}' takes {macro.Parameters.Count} argument(s), "
                            + $"not {arguments.Count}.");
                    return;
                }

                int invocation = ++_expansionCounter;
                HashSet<string> bodyLocals = CollectLocalLabels(macro.Body);

                var expanded = new List<RawLine>(macro.Body.Count);
                foreach (RawLine bodyLine in macro.Body)
                {
                    string substituted = bodyLine.Text;
                    for (int parameter = 0; parameter < macro.Parameters.Count; parameter++)
                    {
                        substituted = ReplaceToken(
                            substituted,
                            macro.Parameters[parameter],
                            arguments[parameter]);
                    }

                    foreach (string local in bodyLocals)
                    {
                        substituted = ReplaceToken(
                            substituted,
                            local,
                            local + "__" + invocation.ToString(CultureInfo.InvariantCulture));
                    }

                    // The invocation is what the user can point at, so that is the line
                    // number carried forward; the origin says which macro it came from.
                    expanded.Add(new RawLine(
                        line.LineNumber,
                        substituted,
                        $"macro {macro.Name}, body line {bodyLine.LineNumber}"));
                }

                Run(expanded, output, depth + 1);
            }

            private int ExpandRepeat(
                List<RawLine> lines,
                int index,
                string text,
                List<Z80PreprocessedLine> output,
                int depth)
            {
                string countText = text[4..].Trim();
                if (!TryEvaluate(countText, out int count) || count is < 0 or > 65536)
                {
                    Fail(lines[index], $"Invalid REPT count '{countText}'.");
                    count = 0;
                }

                List<RawLine> body = ReadBlock(lines, index, "REPT", "ENDR", out int end);
                if (end < 0)
                {
                    Fail(lines[index], "REPT has no ENDR.");
                    return lines.Count;
                }

                for (int repetition = 0; repetition < count; repetition++)
                {
                    int invocation = ++_expansionCounter;
                    HashSet<string> locals = CollectLocalLabels(body);
                    var expanded = new List<RawLine>(body.Count);
                    foreach (RawLine bodyLine in body)
                    {
                        string substituted = bodyLine.Text;
                        foreach (string local in locals)
                        {
                            substituted = ReplaceToken(
                                substituted,
                                local,
                                local + "__" + invocation.ToString(CultureInfo.InvariantCulture));
                        }

                        expanded.Add(new RawLine(
                            bodyLine.LineNumber,
                            substituted,
                            $"REPT repetition {repetition + 1}"));
                    }

                    Run(expanded, output, depth + 1);
                }

                return end;
            }

            private int ExpandConditional(
                List<RawLine> lines,
                int index,
                string text,
                string keyword,
                List<Z80PreprocessedLine> output,
                int depth)
            {
                string conditionText = text[keyword.Length..].Trim();
                bool taken;
                if (keyword.Equals("IFDEF", StringComparison.OrdinalIgnoreCase))
                {
                    taken = _constants.ContainsKey(conditionText);
                }
                else if (keyword.Equals("IFNDEF", StringComparison.OrdinalIgnoreCase))
                {
                    taken = !_constants.ContainsKey(conditionText);
                }
                else if (TryEvaluate(conditionText, out int value))
                {
                    taken = value != 0;
                }
                else
                {
                    Fail(
                        lines[index],
                        $"Condition '{conditionText}' could not be evaluated here; it may only "
                            + "use constants defined above it.");
                    taken = false;
                }

                List<RawLine> whenTrue = [];
                List<RawLine> whenFalse = [];
                int nesting = 1;
                bool inElse = false;
                int cursor = index + 1;
                for (; cursor < lines.Count; cursor++)
                {
                    string inner = StripComment(lines[cursor].Text).Trim();
                    string word = FirstWord(inner);
                    if (word.StartsWith("IF", StringComparison.OrdinalIgnoreCase))
                    {
                        nesting++;
                    }
                    else if (word.Equals("ENDIF", StringComparison.OrdinalIgnoreCase))
                    {
                        nesting--;
                        if (nesting == 0)
                        {
                            break;
                        }
                    }
                    else if (nesting == 1 && word.Equals("ELSE", StringComparison.OrdinalIgnoreCase))
                    {
                        inElse = true;
                        continue;
                    }

                    (inElse ? whenFalse : whenTrue).Add(lines[cursor]);
                }

                if (nesting != 0)
                {
                    Fail(lines[index], "IF has no ENDIF.");
                    return lines.Count;
                }

                Run(taken ? whenTrue : whenFalse, output, depth + 1);
                return cursor;
            }

            private void ExpandInclude(
                RawLine line,
                string text,
                List<Z80PreprocessedLine> output,
                int depth)
            {
                string name = text[7..].Trim().Trim('"');
                if (name.Length == 0)
                {
                    Fail(line, "INCLUDE needs a file name.");
                    return;
                }

                if (includeResolver == null)
                {
                    Fail(line, $"INCLUDE '{name}' is not available here; no source can be loaded.");
                    return;
                }

                string? included = includeResolver(name);
                if (included == null)
                {
                    Fail(line, $"INCLUDE '{name}' could not be read.");
                    return;
                }

                Run(SplitLines(included, name), output, depth + 1);
            }

            /// <summary>Reads a block up to its terminator, without consuming nested ones.</summary>
            private static List<RawLine> ReadBlock(
                List<RawLine> lines,
                int index,
                string opener,
                string closer,
                out int end)
            {
                var body = new List<RawLine>();
                int nesting = 1;
                for (int cursor = index + 1; cursor < lines.Count; cursor++)
                {
                    string word = FirstWord(StripComment(lines[cursor].Text).Trim());
                    if (word.Equals(opener, StringComparison.OrdinalIgnoreCase))
                    {
                        nesting++;
                    }
                    else if (word.Equals(closer, StringComparison.OrdinalIgnoreCase))
                    {
                        nesting--;
                        if (nesting == 0)
                        {
                            end = cursor;
                            return body;
                        }
                    }

                    body.Add(lines[cursor]);
                }

                end = -1;
                return body;
            }

            /// <summary>The local labels a body defines, which have to become unique.</summary>
            private static HashSet<string> CollectLocalLabels(List<RawLine> body)
            {
                var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (RawLine line in body)
                {
                    string text = StripComment(line.Text).Trim();
                    int colon = text.IndexOf(':');
                    if (colon <= 1 || text[0] != '.')
                    {
                        continue;
                    }

                    locals.Add(text[..colon].Trim());
                }

                return locals;
            }

            private void RememberConstant(string text)
            {
                int equ = text.IndexOf(" EQU ", StringComparison.OrdinalIgnoreCase);
                if (equ <= 0)
                {
                    return;
                }

                string name = text[..equ].Trim().TrimEnd(':').Trim();
                string valueText = text[(equ + 5)..].Trim();
                if (name.Length > 0 && TryEvaluate(valueText, out int value))
                {
                    _constants[name] = value;
                }
            }

            /// <summary>Evaluates a condition or count against the constants seen so far.</summary>
            private bool TryEvaluate(string text, out int value) =>
                Z80InlineAssembler.TryEvaluateConstant(text, _constants, out value);

            private void Fail(RawLine line, string message)
            {
                string described = line.Origin == null
                    ? message
                    : $"{message} (in {line.Origin})";
                diagnostics.Add(new Z80AssemblyDiagnostic(
                    line.LineNumber,
                    1,
                    described,
                    line.Text?.Trim() ?? string.Empty));
            }
        }

        private static string FirstWord(string text)
        {
            int index = 0;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            return text[..index];
        }

        private static List<string> SplitArguments(string text)
        {
            var arguments = new List<string>();
            if (text.Length == 0)
            {
                return arguments;
            }

            bool inQuote = false;
            int start = 0;
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] == '"')
                {
                    inQuote = !inQuote;
                }
                else if (text[index] == ',' && !inQuote)
                {
                    arguments.Add(text[start..index].Trim());
                    start = index + 1;
                }
            }

            arguments.Add(text[start..].Trim());
            return arguments;
        }

        /// <summary>
        /// Replaces whole-token occurrences of a name, matching case exactly.
        /// </summary>
        /// <remarks>
        /// Two deliberate restrictions, both because substitution that is too eager corrupts
        /// a macro body silently while substitution that is too timid fails loudly.
        ///
        /// Whole tokens only: a plain string replace would turn the parameter <c>a</c> into
        /// its argument inside <c>address</c>.
        ///
        /// And case-sensitive, which matters more than it looks. A parameter named <c>a</c>
        /// matched case-insensitively also matches the register <c>A</c>, so <c>LD A,a</c>
        /// becomes <c>LD 1,1</c> - an instruction that does not exist, from a macro that
        /// looked reasonable. Registers are written upper case and parameters lower case by
        /// long convention, so exact matching separates them. The cost is that a body
        /// referring to <c>VALUE</c> will not pick up a parameter named <c>value</c>, which
        /// fails as an unknown symbol rather than as a wrong instruction.
        /// </remarks>
        private static string ReplaceToken(string text, string token, string replacement)
        {
            if (token.Length == 0)
            {
                return text;
            }

            var result = new System.Text.StringBuilder(text.Length);
            int index = 0;
            while (index < text.Length)
            {
                int at = text.IndexOf(token, index, StringComparison.Ordinal);
                if (at < 0)
                {
                    result.Append(text, index, text.Length - index);
                    break;
                }

                bool boundedBefore = at == 0 || !IsTokenCharacter(text[at - 1]);
                int after = at + token.Length;
                bool boundedAfter = after >= text.Length || !IsTokenCharacter(text[after]);

                result.Append(text, index, at - index);
                if (boundedBefore && boundedAfter)
                {
                    result.Append(replacement);
                }
                else
                {
                    result.Append(text, at, token.Length);
                }

                index = after;
            }

            return result.ToString();
        }

        private static bool IsTokenCharacter(char ch) =>
            char.IsLetterOrDigit(ch) || ch is '_' or '.' or '@' or '$';

        private static string StripComment(string line)
        {
            bool inQuote = false;
            for (int index = 0; index < line.Length; index++)
            {
                if (line[index] == '"')
                {
                    inQuote = !inQuote;
                }
                else if (!inQuote && line[index] == ';')
                {
                    return line[..index];
                }
            }

            return line;
        }
    }
}
