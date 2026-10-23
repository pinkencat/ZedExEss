using System;
using ZedExEss.Spectrum.Debugging;

namespace ZedExEss.Spectrum.Core
{
    /// <summary>How wide an instruction is, and whether it calls somewhere.</summary>
    public readonly record struct SpectrumInstructionShape(int Length, bool IsCallLike);

    /// <summary>Why a source-level step stopped, and how far it got.</summary>
    public readonly record struct SourceStepOutcome(
        bool ReachedNewLine,
        int Instructions,
        string Summary);

    /// <summary>
    /// Steps a machine until it reaches a different line of somebody's source.
    /// </summary>
    /// <remarks>
    /// One source line is many instructions, so a source-level step is a loop over
    /// instruction steps that watches the source map. The loop, the stopping rules and the
    /// cap live here rather than in either host, because both need identical behaviour and
    /// the rules are the whole feature - the hosts supply only "read PC" and "step once",
    /// which is all that differs between a Spectrum, a ZX8x and a Next.
    ///
    /// These are step-<i>into</i> semantics: a line containing a CALL will stop at the first
    /// mapped line inside the callee, because that genuinely is the next source line
    /// executed. Staying in the current routine instead needs the temporary-breakpoint
    /// machinery that instruction-level step-over uses, which is a separate thing.
    /// </remarks>
    public static class SpectrumSourceStepper
    {
        /// <summary>
        /// Instructions one source step will execute before giving up.
        /// </summary>
        /// <remarks>
        /// A line can contain a call into something that runs for a long time, or a loop that
        /// never leaves the line at all. Stopping with a count beats appearing to hang, and
        /// the machine is left wherever it got to, which is a legitimate place to look at.
        /// </remarks>
        public const int DefaultMaximumInstructions = 200_000;

        /// <summary>
        /// Steps one instruction, or over a call, without returning until it is done.
        /// </summary>
        /// <remarks>
        /// The debugger's own step-over resumes the machine and waits for a temporary
        /// breakpoint, which is right for a button press and useless inside a loop. This does
        /// the same thing synchronously so a source-level step can be built out of it: step,
        /// and if that was a call, keep stepping until execution comes back.
        ///
        /// "Comes back" means the program counter reaches the instruction after the call
        /// <i>and</i> the stack has unwound at least to where it was. The stack half matters
        /// for recursion: without it, a callee that calls itself would look like it had
        /// returned the first time an inner invocation finished. A conditional call that was
        /// not taken satisfies both immediately, which is correct.
        ///
        /// Returns false only when the machine would not step at all. Running out of the
        /// allowance returns true and leaves the machine where it got to, which is somewhere
        /// the user can look at rather than a failure.
        /// </remarks>
        public static bool StepOverInstruction(
            Func<ushort> readPc,
            Func<ushort> readStackPointer,
            Func<ushort, SpectrumInstructionShape> measure,
            Func<bool> stepOnce,
            int maximumInstructions = DefaultMaximumInstructions)
        {
            ArgumentNullException.ThrowIfNull(readPc);
            ArgumentNullException.ThrowIfNull(readStackPointer);
            ArgumentNullException.ThrowIfNull(measure);
            ArgumentNullException.ThrowIfNull(stepOnce);
            if (maximumInstructions < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumInstructions));
            }

            ushort start = readPc();
            ushort stackBeforeCall = readStackPointer();
            SpectrumInstructionShape shape = measure(start);

            if (!stepOnce())
            {
                return false;
            }

            if (!shape.IsCallLike)
            {
                return true;
            }

            ushort returnAddress = unchecked((ushort)(start + shape.Length));
            for (int instructions = 1; instructions <= maximumInstructions; instructions++)
            {
                if (readPc() == returnAddress && readStackPointer() >= stackBeforeCall)
                {
                    return true;
                }

                if (!stepOnce())
                {
                    return false;
                }
            }

            return true;
        }

        public static SourceStepOutcome StepLine(
            SpectrumDebuggerSymbolState symbols,
            Func<ushort> readPc,
            Func<bool> stepOnce,
            int maximumInstructions = DefaultMaximumInstructions)
        {
            ArgumentNullException.ThrowIfNull(symbols);
            ArgumentNullException.ThrowIfNull(readPc);
            ArgumentNullException.ThrowIfNull(stepOnce);
            if (maximumInstructions < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumInstructions));
            }

            if (!symbols.HasSourceMap)
            {
                return new SourceStepOutcome(false, 0, "No source map is loaded.");
            }

            bool startedMapped = symbols.TryGetLocation(readPc(), out Z80SourceLocation start);

            for (int instructions = 1; instructions <= maximumInstructions; instructions++)
            {
                if (!stepOnce())
                {
                    return new SourceStepOutcome(
                        false,
                        instructions - 1,
                        $"Stopped after {instructions - 1} instruction(s); the machine would not step.");
                }

                bool mapped = symbols.TryGetLocation(readPc(), out Z80SourceLocation now);
                if (!mapped)
                {
                    // Code with no source of its own - a ROM routine, or a gap between
                    // mapped lines - is passed through rather than stopped at.
                    continue;
                }

                if (startedMapped && now.File == start.File && now.Line == start.Line)
                {
                    // Still the same line: a loop written on one line, or simply more
                    // instructions belonging to it.
                    continue;
                }

                return new SourceStepOutcome(
                    true,
                    instructions,
                    $"{now.File}:{now.Line} after {instructions} instruction(s).");
            }

            return new SourceStepOutcome(
                false,
                maximumInstructions,
                $"Gave up after {maximumInstructions} instruction(s) without reaching another "
                    + "mapped source line.");
        }
    }
}
