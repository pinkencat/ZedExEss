using System;
using System.Collections.Generic;
using System.IO;
using ZedExEss.Spectrum.Debugging;

namespace ZedExEss.Spectrum.Core
{
    /// <summary>What happened when a symbol file was loaded, ready to show a user.</summary>
    public readonly record struct SpectrumSymbolLoadResult(
        bool Loaded,
        string Summary,
        int Count,
        int SkippedLines);

    /// <summary>
    /// Owns the imported symbol names and keeps the debugger's tools pointed at them.
    /// </summary>
    /// <remarks>
    /// Both hosts need identical behaviour here - load a file, name addresses in the
    /// disassembly, accept the names in the assembler, be able to forget them again - so the
    /// behaviour lives here and each host contributes only a button and a label.
    ///
    /// The disassembler and assembler are handed in rather than created, because the debugger
    /// windows already own the instances their views are built around; pointing those at the
    /// table is the whole job.
    /// </remarks>
    public sealed class SpectrumDebuggerSymbolState
    {
        private readonly Z80Disassembler _disassembler;
        private readonly Z80InlineAssembler _assembler;
        private string? _cachedSourceFile;
        private string[]? _cachedSourceLines;

        public SpectrumDebuggerSymbolState(
            Z80Disassembler disassembler,
            Z80InlineAssembler assembler)
        {
            _disassembler = disassembler ?? throw new ArgumentNullException(nameof(disassembler));
            _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
        }

        /// <summary>The names currently in use, or null when none are loaded.</summary>
        public Z80SymbolTable? Symbols { get; private set; }

        /// <summary>Where the names came from, for showing which file is in use.</summary>
        public string? SourcePath { get; private set; }

        /// <summary>Which source line produced each address, when a .sld was loaded.</summary>
        public Z80SourceMap? SourceMap { get; private set; }

        public bool HasSourceMap => SourceMap is { Count: > 0 };

        /// <summary>The source line the given address came from, if it is known.</summary>
        public bool TryGetLocation(int address, out Z80SourceLocation location)
        {
            Z80SourceMap? map = SourceMap;
            if (map != null)
            {
                return map.TryGetLocation(address, out location);
            }

            location = default;
            return false;
        }

        public bool HasSymbols => Symbols is { Count: > 0 };

        /// <summary>A short description of what is loaded, for a status label.</summary>
        public string Description
        {
            get
            {
                if (Symbols == null && SourceMap == null)
                {
                    return "No symbols loaded";
                }

                string file = Path.GetFileName(SourcePath) ?? "symbols";
                string names = $"{Symbols?.Count ?? 0} name(s)";
                string lines = HasSourceMap ? $", {SourceMap!.Count} source line(s)" : string.Empty;
                string skipped = Symbols is { SkippedLines: > 0 }
                    ? $", {Symbols.SkippedLines} line(s) skipped"
                    : string.Empty;
                return $"{file}: {names}{lines}{skipped}";
            }
        }

        /// <summary>
        /// Reads a symbol file and points the disassembler and assembler at it.
        /// </summary>
        /// <remarks>
        /// A file that parses to nothing is treated as a failure rather than as an empty
        /// success: it almost always means the wrong file was picked, and silently loading
        /// zero names would leave the disassembly looking unchanged with no explanation.
        /// </remarks>
        public SpectrumSymbolLoadResult Load(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            Z80SymbolTable table;
            try
            {
                table = Z80SymbolTable.Parse(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SpectrumSymbolLoadResult(
                    false,
                    $"Could not read '{Path.GetFileName(path)}': {ex.Message}",
                    0,
                    0);
            }

            if (table.Count == 0)
            {
                return new SpectrumSymbolLoadResult(
                    false,
                    $"No symbols found in '{Path.GetFileName(path)}'; "
                        + $"{table.SkippedLines} line(s) were not recognised.",
                    0,
                    table.SkippedLines);
            }

            Apply(table, path);
            return new SpectrumSymbolLoadResult(true, Description, table.Count, table.SkippedLines);
        }

        /// <summary>
        /// Reads a source-level debugging file, which brings a source map as well as names.
        /// </summary>
        /// <remarks>
        /// Treated as a failure when it yields neither names nor source lines, on the same
        /// reasoning as an empty symbol file: it means the wrong file was chosen, and a
        /// silent no-op would be indistinguishable from a working load.
        /// </remarks>
        public SpectrumSymbolLoadResult LoadSourceLevel(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            Z80SldImport import;
            try
            {
                import = Z80SldReader.Parse(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SpectrumSymbolLoadResult(
                    false,
                    $"Could not read '{Path.GetFileName(path)}': {ex.Message}",
                    0,
                    0);
            }

            if (!import.IsUsable)
            {
                return new SpectrumSymbolLoadResult(
                    false,
                    $"'{Path.GetFileName(path)}' held no source lines or names that could be "
                        + $"read; {import.SkippedLines} record(s) were not recognised.",
                    0,
                    import.SkippedLines);
            }

            Apply(import.Symbols, path);
            SourceMap = import.SourceMap;
            return new SpectrumSymbolLoadResult(
                true,
                Description,
                import.Symbols.Count,
                import.SkippedLines);
        }

        /// <summary>Uses an already-parsed table, for callers that read the text themselves.</summary>
        public void Apply(Z80SymbolTable table, string? sourcePath)
        {
            ArgumentNullException.ThrowIfNull(table);

            Symbols = table;
            SourcePath = sourcePath;
            _cachedSourceFile = null;
            _cachedSourceLines = null;

            // A plain symbol file carries no source lines, so any map from an earlier .sld
            // would now be describing different code.
            SourceMap = null;
            _disassembler.Symbols = table;
            _assembler.Symbols = table;
        }

        /// <summary>
        /// The source around the line an address came from, ready to show.
        /// </summary>
        /// <remarks>
        /// Returns the text and a header naming the file and line, or false when there is no
        /// mapping for the address or the file cannot be found. Both hosts want exactly this,
        /// including the arrow marking the current line, so it is produced once here rather
        /// than twice in markup.
        /// </remarks>
        public bool TryGetSourceWindow(
            int address,
            int radius,
            out string header,
            out string text)
        {
            header = string.Empty;
            text = string.Empty;
            if (radius < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(radius));
            }

            if (!TryGetLocation(address, out Z80SourceLocation location))
            {
                return false;
            }

            header = $"{location.File}:{location.Line}";
            string[]? lines = TryReadSource(location.File);
            if (lines == null)
            {
                text = $"({location.File} could not be found next to the debugging file)";
                return true;
            }

            int first = Math.Max(1, location.Line - radius);
            int last = Math.Min(lines.Length, location.Line + radius);
            var builder = new System.Text.StringBuilder();
            for (int line = first; line <= last; line++)
            {
                // The arrow is what makes the window readable at a glance; the line number
                // is what makes it match the editor the code was written in.
                builder.Append(line == location.Line ? "> " : "  ");
                builder.Append(line.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(5));
                builder.Append("  ");
                builder.AppendLine(lines[line - 1]);
            }

            text = builder.ToString();
            return true;
        }

        /// <summary>
        /// Finds and caches a source file named by the debugging data.
        /// </summary>
        /// <remarks>
        /// The names in a debugging file are whatever the assembler was given, so they are
        /// usually relative to the directory it ran in. Trying the name as written and then
        /// beside the debugging file covers the ordinary case without asking the user to
        /// locate anything. Cached because a source view is refreshed on every single step.
        /// </remarks>
        private string[]? TryReadSource(string file)
        {
            if (_cachedSourceFile == file)
            {
                return _cachedSourceLines;
            }

            _cachedSourceFile = file;
            _cachedSourceLines = null;

            foreach (string candidate in EnumerateCandidatePaths(file))
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        _cachedSourceLines = File.ReadAllLines(candidate);
                        break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable candidate is the same as a missing one here.
                }
            }

            return _cachedSourceLines;
        }

        private IEnumerable<string> EnumerateCandidatePaths(string file)
        {
            yield return file;

            string? beside = SourcePath == null ? null : Path.GetDirectoryName(SourcePath);
            if (!string.IsNullOrEmpty(beside))
            {
                yield return Path.Combine(beside, file);
                yield return Path.Combine(beside, Path.GetFileName(file));
            }
        }

        /// <summary>
        /// Finds a file the source refers to, beside the symbol file if need be.
        /// </summary>
        /// <remarks>
        /// The loaded symbol or debugging file is the closest thing the debugger has to a
        /// project root, so it is what relative names are resolved against - exactly as they
        /// already are for source files. With no symbol file loaded only an absolute or
        /// working-directory-relative name can be found, which is honest: there is nothing
        /// else to measure from.
        /// </remarks>
        public bool TryResolveFile(string name, out string path)
        {
            path = string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            foreach (string candidate in EnumerateCandidatePaths(name))
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        path = candidate;
                        return true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreachable candidate is the same as a missing one.
                }
            }

            return false;
        }

        /// <summary>
        /// Reads a file the source INCLUDEs, or null when it cannot be found.
        /// </summary>
        /// <remarks>
        /// Shaped to be handed straight to the assembler's resolver, so neither host has to
        /// contain file handling of its own.
        /// </remarks>
        public string? ReadProjectText(string name)
        {
            if (!TryResolveFile(name, out string path))
            {
                return null;
            }

            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Reads a file the source INCBINs, or null when it cannot be found.</summary>
        public byte[]? ReadProjectBytes(string name)
        {
            if (!TryResolveFile(name, out string path))
            {
                return null;
            }

            try
            {
                return File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Goes back to showing plain addresses.</summary>
        public void Clear()
        {
            Symbols = null;
            SourcePath = null;
            SourceMap = null;
            _cachedSourceFile = null;
            _cachedSourceLines = null;
            _disassembler.Symbols = null;
            _assembler.Symbols = null;
        }
    }
}
