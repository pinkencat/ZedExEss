using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ZedExEss.FileHandlers;
using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Input;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Replay;
using ZedExEss.Spectrum.Video;

namespace ZedExEss.Diagnostics;

/// <summary>Settings for the input recording and rewind checks.</summary>
public sealed class ReplayVerificationOptions
{
    public string? OutputPath { get; init; }

    /// <summary>Directory holding 48.rom.</summary>
    public string? RomDirectory { get; init; }
}

/// <summary>
/// Verifies that a recorded run replays identically, that the file survives a round trip,
/// that divergence is caught rather than ignored, and that rewind puts the machine and any
/// recording in progress back to the same point.
/// </summary>
public static class ReplayVerificationRunner
{
    /// <summary>Frames to record. Long enough to cover several keyboard scans.</summary>
    private const int RecordedFrames = 60;

    public static int Run(ReplayVerificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string outputPath = Path.GetFullPath(options.OutputPath ?? "replay-verification.log");
        string? outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8) { AutoFlush = true };
        int failed = 0;

        writer.WriteLine("Replay verification");
        writer.WriteLine($"Output: {outputPath}");
        writer.WriteLine();

        string romDirectory = options.RomDirectory ?? Path.Combine(AppContext.BaseDirectory, "ROMs");
        string machineRom = Path.Combine(romDirectory, "48.rom");

        Check("A recorded run replays identically", () => VerifyReplay(machineRom, writer), ref failed);
        Check("A recording survives a file round trip", () => VerifyFileRoundTrip(machineRom), ref failed);
        Check("Divergence is reported, not ignored", () => VerifyDesyncDetected(machineRom), ref failed);
        Check("Rewind returns the machine to an earlier state", () => VerifyRewind(machineRom, writer), ref failed);
        Check("Rewind drops the frames it re-takes", () => VerifyRewindTrimsRecording(machineRom), ref failed);
        Check("Contention timings are switchable", () => VerifyContentionTimings(machineRom), ref failed);
        Check("Replay runs the machine unaided", () => VerifyAcceleratorsSuspended(machineRom), ref failed);
        Check("A host running whole frames sees every one", () => VerifyHostDrivenPlayback(machineRom), ref failed);
        Check("A host driven by its sound card sees every one", () => VerifyAudioDrivenPlayback(machineRom), ref failed);
        Check("Frame advance runs one frame and presents it", () => VerifyFrameAdvance(machineRom), ref failed);
        Check("Stepping back one frame lands exactly", () => VerifyStepBackOneFrame(machineRom), ref failed);
        Check("The history reaches any earlier frame", () => VerifyHistorySeek(machineRom, writer), ref failed);
        Check("Old states thin out under a budget", () => VerifyHistoryBudget(machineRom, writer), ref failed);
        Check("Rewinding keeps the PSG's generator phase",
            () => VerifyRewindKeepsPsgPhase(machineRom, writer), ref failed);
        Check("Unchanged memory is shared between states", () => VerifyHistorySharing(machineRom, writer), ref failed);
        Check("Frame advance yields to a breakpoint", () => VerifyFrameAdvanceStopsAtBreak(machineRom), ref failed);
        Check("A loaded state drops the frames it re-takes", () => VerifyStateSlotRetake(machineRom), ref failed);
        Check("A state slot refuses another model", () => VerifyStateSlotModelMismatch(machineRom), ref failed);

        writer.WriteLine();
        writer.WriteLine(failed == 0 ? "Result: PASS" : $"Result: FAIL ({failed} failed checks)");
        return failed == 0 ? 0 : 1;

        void Check(string name, Action action, ref int failureCount)
        {
            try
            {
                action();
                writer.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failureCount++;
                writer.WriteLine($"FAIL {name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Records a run with keys held, then replays it with the keyboard untouched. The
    /// replayed machine must end up in exactly the state the recorded one did, which it
    /// can only do if the recorded input is what the CPU actually sees.
    /// </summary>
    private static void VerifyReplay(string machineRom, StreamWriter writer)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        var session = new SpectrumReplaySession();
        session.AttachMachine(machine);

        SettleIntoBasic(machine);
        session.StartRecording();

        // The first frame only arms the recording; the frames after it are the recording.
        machine.Emulator.RunFrame(presentFrame: false);
        RunWithKeys(machine, session, RecordedFrames);
        session.Stop();

        Require(
            session.FrameCount == RecordedFrames,
            $"Expected {RecordedFrames} frames, got {session.FrameCount}.");
        RzxRecording recording = session.BuildRecording();
        long recorded = Fingerprint(machine);
        writer.WriteLine($"  recorded {recording.Frames.Count} frames, fingerprint {recorded:X16}");
        writer.WriteLine("  first fetch counts: "
            + string.Join(" ", recording.Frames.Take(5).Select(f => f.FetchCount)));

        // Replay on a machine that has been nowhere near those keys.
        SpectrumMachine replayMachine = CreateMachine(machineRom);
        var replay = new SpectrumReplaySession();
        replay.AttachMachine(replayMachine);
        replay.StartPlayback(recording);
        Require(replay.Mode == SpectrumReplayMode.Playing, "Playback should have started.");

        // Step rather than run frames, so replay stops on the last recorded frame's
        // boundary and not wherever the next ULA edge happens to be.
        int budget = 0;
        while (replay.Mode == SpectrumReplayMode.Playing && budget++ < 4_000_000)
        {
            replayMachine.Emulator.StepInstruction();
        }

        Require(replay.DesyncReason == null, $"Replay diverged: {replay.DesyncReason}");
        Require(
            replay.PlaybackFrame == recording.Frames.Count,
            $"Replay reached frame {replay.PlaybackFrame} of {recording.Frames.Count}.");

        long replayed = Fingerprint(replayMachine);
        Require(
            replayed == recorded,
            $"The replayed machine ended at {replayed:X16}, the recorded one at {recorded:X16}.");
    }

    private static void VerifyFileRoundTrip(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        var session = new SpectrumReplaySession();
        session.AttachMachine(machine);

        SettleIntoBasic(machine);
        session.StartRecording();
        machine.Emulator.RunFrame(presentFrame: false);
        RunWithKeys(machine, session, RecordedFrames);
        session.Stop();

        RzxRecording original = session.BuildRecording();
        string path = Path.Combine(Path.GetTempPath(), $"zedexess-replay-{Guid.NewGuid():N}.rzx");
        try
        {
            RzxFile.Write(path, original);
            RzxRecording reloaded = RzxFile.Read(path);

            Require(
                reloaded.Frames.Count == original.Frames.Count,
                $"Expected {original.Frames.Count} frames, read {reloaded.Frames.Count}.");
            Require(
                reloaded.SnapshotData.Length == original.SnapshotData.Length,
                "The snapshot did not survive the round trip.");
            Require(reloaded.SnapshotExtension == "szx", $"Unexpected snapshot type {reloaded.SnapshotExtension}.");

            for (int i = 0; i < original.Frames.Count; i++)
            {
                RzxFrame expected = original.Frames[i];
                RzxFrame actual = reloaded.Frames[i];
                Require(
                    actual.FetchCount == expected.FetchCount,
                    $"Frame {i} fetch count {actual.FetchCount} should be {expected.FetchCount}.");
                Require(
                    actual.InputValues.Length == expected.InputValues.Length,
                    $"Frame {i} should hold {expected.InputValues.Length} inputs, not {actual.InputValues.Length}.");
            }
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// A recording that no longer matches the machine has to stop and say so. Silently
    /// carrying on would make a replay look successful while showing something else.
    /// </summary>
    private static void VerifyDesyncDetected(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        var session = new SpectrumReplaySession();
        session.AttachMachine(machine);

        SettleIntoBasic(machine);
        session.StartRecording();
        machine.Emulator.RunFrame(presentFrame: false);
        RunWithKeys(machine, session, RecordedFrames);
        session.Stop();

        RzxRecording recording = session.BuildRecording();
        // Take an input away from a frame. The fetch count cannot be the thing damaged
        // here: replay uses it to decide where the frame ends, so changing it changes the
        // run rather than contradicting it. The input stream is what has to line up.
        var damaged = new List<RzxFrame>(recording.Frames);
        RzxFrame original = damaged[2];
        damaged[2] = original with { InputValues = original.InputValues[..^1], RepeatsPrevious = false };

        SpectrumMachine replayMachine = CreateMachine(machineRom);
        var replay = new SpectrumReplaySession();
        replay.AttachMachine(replayMachine);
        replay.StartPlayback(new RzxRecording
        {
            SnapshotExtension = recording.SnapshotExtension,
            SnapshotData = recording.SnapshotData,
            Frames = damaged
        });

        for (int frame = 0; frame < RecordedFrames; frame++)
        {
            replayMachine.Emulator.RunFrame(presentFrame: false);
        }

        Require(replay.DesyncReason != null, "The damaged recording should have been reported as diverging.");
        Require(replay.Mode == SpectrumReplayMode.Idle, "Playback should have stopped at the divergence.");

        // Stopping must hand the machine back its own interrupt. Replay silences the ULA
        // and delivers the interrupt itself; leaving it silenced would strand a machine
        // that never scans the keyboard again, which looks like a hang rather than a
        // recording that ended.
        ushort frames = 0x5C78;
        byte before = replayMachine.Memory.ReadDirect(frames);
        for (int frame = 0; frame < 10; frame++)
        {
            replayMachine.Emulator.RunFrame(presentFrame: false);
        }

        Require(
            replayMachine.Memory.ReadDirect(frames) != before,
            "The machine stopped taking interrupts after the recording diverged.");
    }

    private static void VerifyRewind(string machineRom, StreamWriter writer)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        var session = new SpectrumReplaySession();
        session.AttachMachine(machine);

        SettleIntoBasic(machine);

        for (int frame = 0; frame < 100; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        int depth = session.RewindDepth;
        Require(depth >= 19, $"Two seconds should leave plenty of states, not {depth}.");
        Require(
            session.RewindSecondsAvailable > 1.8,
            $"Expected about two seconds of history, got {session.RewindSecondsAvailable:F1}.");

        long before = Fingerprint(machine);
        Require(session.StepBack(), "Stepping back should have worked.");
        long stepped = Fingerprint(machine);
        Require(stepped != before, "Stepping back should have changed the machine state.");
        Require(session.RewindDepth < depth,
            "Stepping back should drop the states covering the frames being re-taken.");
        Require(session.History.NewestFrame <= session.CurrentFrame,
            "The history should hold nothing after the frame stepped back to.");

        // Running the same tenth of a second again must land in the same place: the
        // restored state has to be complete, not approximately right.
        for (int frame = 0; frame < SpectrumReplaySession.FramesPerStepBack; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        long replayed = Fingerprint(machine);
        writer.WriteLine($"  depth {depth}, before {before:X16}, after re-running {replayed:X16}");
        Require(
            replayed == before,
            $"Re-running the step should reach {before:X16}, not {replayed:X16}.");

        while (session.StepBack())
        {
        }

        // Stepping back stops at the start of the history rather than running out of
        // states: the beginning of the run stays reachable however far back it goes.
        Require(
            session.CurrentFrame - session.OldestFrameAvailable < SpectrumReplaySession.FramesPerStepBack,
            $"Stepping back stopped at frame {session.CurrentFrame} with history reaching "
                + $"back to {session.OldestFrameAvailable}.");
        Require(session.RewindDepth >= 1, "The start of the run should still be stored.");
    }

    /// <summary>
    /// Stepping back during a recording has to drop the frames being re-taken, or the
    /// recording would describe a run the machine never made.
    /// </summary>
    private static void VerifyRewindTrimsRecording(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        var session = new SpectrumReplaySession();
        session.AttachMachine(machine);

        SettleIntoBasic(machine);
        session.StartRecording();
        machine.Emulator.RunFrame(presentFrame: false);
        for (int frame = 0; frame < 50; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        int recorded = session.FrameCount;
        Require(Math.Abs(recorded - 50) <= 1, $"Expected about 50 recorded frames, got {recorded}.");

        Require(session.StepBack(), "Stepping back should have worked.");
        Require(session.StepBack(), "Stepping back twice should have worked.");

        int trimmed = session.FrameCount;
        Require(
            trimmed <= recorded - (2 * SpectrumReplaySession.FramesPerStepBack),
            $"Two steps back should drop at least {2 * SpectrumReplaySession.FramesPerStepBack} frames, but {recorded - trimmed} went.");
        Require(session.Mode == SpectrumReplayMode.Recording, "Recording should continue after a retake.");

        // And recording carries on from there rather than from where it left off.
        for (int frame = 0; frame < SpectrumReplaySession.FramesPerStepBack; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        Require(
            session.FrameCount == trimmed + SpectrumReplaySession.FramesPerStepBack,
            "Recording should resume from the point stepped back to.");
    }

    /// <summary>
    /// Replays a recording made elsewhere and reports how far it got.
    /// </summary>
    /// <remarks>
    /// A recording from another emulator is the real test of whether replay agrees with
    /// it about frames and inputs, so this reports the frame it stopped at and why rather
    /// than only passing or failing.
    /// </remarks>
    public static int Diagnose(
        string recordingPath,
        string? romDirectory,
        string? outputPath,
        SpectrumContentionTiming? contentionTiming = null,
        bool traceFrames = false,
        int detailFrame = -1,
        bool audioDriven = false,
        bool traceTstates = false,
        string? exportPath = null,
        bool runFrames = false)
    {
        ArgumentNullException.ThrowIfNull(recordingPath);

        string resolved = Path.GetFullPath(outputPath ?? "rzx-diagnosis.log");
        using var writer = new StreamWriter(resolved, false, Encoding.UTF8) { AutoFlush = true };

        RzxRecording recording = RzxFile.Read(recordingPath);
        SpectrumModel model = SpectrumReplaySession.DetectModel(recording);
        writer.WriteLine($"Recording: {Path.GetFileName(recordingPath)}");
        writer.WriteLine($"Creator: {recording.Creator}");
        writer.WriteLine($"Snapshot: .{recording.SnapshotExtension} ({recording.SnapshotData.Length} bytes), model {model}");
        writer.WriteLine($"Frames: {recording.Frames.Count}");

        SpectrumContentionTiming timing = contentionTiming ?? SpectrumContentionTiming.Early;
        writer.WriteLine($"Contention: {timing}");

        SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = model,
            Roms = LoadRoms(model, romDirectory),
            RenderEnabled = false,
            ContentionTiming = timing
        });

        var session = new SpectrumReplaySession { RewindEnabled = false };
        if (detailFrame >= 0)
        {
            int frameWindowStart = detailFrame - 40;
            ulong lastAccepted = 0;
            session.FrameObserver = (frameIndex, consumed, recorded, iff1) =>
            {
                ulong accepted = machine.Cpu.InterruptsAccepted;
                if (frameIndex >= frameWindowStart && frameIndex <= detailFrame)
                {
                    writer.WriteLine(
                        $"    FRAME {frameIndex} inputs {consumed}/{recorded} iff1={iff1} "
                            + $"intTaken={accepted - lastAccepted} PC=${machine.Cpu.PC:X4}");
                }

                lastAccepted = accepted;
            };

            int windowStart = detailFrame - 40;
            session.InputObserver = (frameIndex, pc, port, value, fetchOffset) =>
            {
                if (frameIndex < windowStart || frameIndex > detailFrame)
                {
                    return;
                }

                RzxFrame recorded = recording.Frames[frameIndex];
                writer.WriteLine(
                    $"    input frame {frameIndex} at PC ${pc:X4} port ${port:X4} bus ${value:X2} "
                        + $"after {fetchOffset} of {recorded.FetchCount} opcodes "
                        + $"(recorded {recorded.InputValues.Length} inputs)");
            };
        }
        session.AttachMachine(machine);
        session.StartPlayback(recording);

        int frameTstatesReference = SpectrumUlaTiming.ForModel(model).TstatesPerFrame;
        var costs = new Dictionary<int, OpcodeCost>();
        short[] audioBuffer = new short[882];

        // Fast-forward pulls the machine through a time stretcher, which asks for samples
        // in far larger and less regular blocks than the audio device does.
        Spectrum.Abstractions.IAudioSource audioSource = audioDriven
            ? new Spectrum.Audio.TimeStretchAudioSource(
                machine.Emulator,
                Spectrum.Audio.SpectrumAudioTiming.DefaultSampleRate,
                4)
            : machine.Emulator;
        if (runFrames)
        {
            return DiagnoseByFrames(machine, session, recording, writer);
        }

        if (!string.IsNullOrWhiteSpace(exportPath))
        {
            ExportForReference(machine, recording, exportPath, writer);
            return 0;
        }

        var visits = new Dictionary<ushort, int>();
        bool trace = traceFrames;
        int[] opcodeCounts = new int[512];
        ulong startCycles = machine.Cpu.Cyc;
        ulong previousCycles = startCycles;
        ulong previousWaits = machine.Cpu.WaitStates;
        int previousFrame = 0;
        long shortest = long.MaxValue;
        BusTally? busTally = null;
        int interruptsEnabledFrames = 0;
        long longest = 0;
        for (int frame = 0; frame < recording.Frames.Count; frame++)
        {
            if (session.Mode != SpectrumReplayMode.Playing)
            {
                break;
            }

            if (audioDriven)
            {
                // Drive the machine the way the host does when it is playing sound, which
                // is also how fast-forward runs: the audio device asks for a block of
                // samples and the emulator runs for however long that takes.
                audioSource.ReadSamples(audioBuffer, 0, audioBuffer.Length);
                if (session.PlaybackFrame > previousFrame)
                {
                    previousCycles = machine.Cpu.Cyc;
                    previousFrame = session.PlaybackFrame;
                }

                continue;
            }

            // Step to the recording's own frame boundary rather than ours: measuring
            // between our frame edges mixes whole and part frames together and makes the
            // per-frame figure meaningless.
            bool detailing = previousFrame == detailFrame;
            if (detailing)
            {
                busTally = new BusTally();
                machine.Cpu.ConfigureBusObserver(busTally);
            }

            while (session.Mode == SpectrumReplayMode.Playing
                && session.PlaybackFrame == previousFrame)
            {
                if (trace)
                {
                    CountOpcode(machine, opcodeCounts);
                }

                if (detailing)
                {
                    int key = ClassifyOpcode(machine);
                    ushort pc = machine.Cpu.PC;
                    visits[pc] = visits.TryGetValue(pc, out int seen) ? seen + 1 : 1;
                    ulong beforeInstruction = machine.Cpu.Cyc;
                    machine.Emulator.StepInstruction();
                    RecordCost(costs, key, (int)(machine.Cpu.Cyc - beforeInstruction));
                    continue;
                }

                machine.Emulator.StepInstruction();
            }

            if (trace && session.PlaybackFrame > previousFrame)
            {
                long spentHere = (long)(machine.Cpu.Cyc - previousCycles);
                long drift = spentHere - frameTstatesReference;

                // Only the frames that matter: a few T-states either way is just the
                // frame ending on an instruction boundary where the recording ended on a
                // T-state boundary.
                if (Math.Abs(drift) > 40 || previousFrame < 3 || previousFrame > 275)
                {
                    writer.WriteLine(
                        $"  frame {previousFrame}: {recording.Frames[previousFrame].FetchCount} opcodes, "
                            + $"{spentHere} T ({drift:+#;-#;0}), OUT(n) {opcodeCounts[0xD3]}, "
                            + $"IN(n) {opcodeCounts[0xDB]}, ED {opcodeCounts[0xED]}"
                            + $" [LDIR {opcodeCounts[256 + 0xB0]}, LDDR {opcodeCounts[256 + 0xB8]}, "
                            + $"OTIR {opcodeCounts[256 + 0xB3]}, INIR {opcodeCounts[256 + 0xB2]}, "
                            + $"CPIR {opcodeCounts[256 + 0xB1]}, LDI {opcodeCounts[256 + 0xA0]}, "
                            + $"OUTI {opcodeCounts[256 + 0xA3]}, LD A,R {opcodeCounts[256 + 0x5F]}, "
                            + $"LD A,I {opcodeCounts[256 + 0x57]}]");
                }

                if (previousFrame == detailFrame)
                {
                    machine.Cpu.ConfigureBusObserver(null);
                    busTally?.Report(writer);
                    busTally = null;
                    ReportCosts(writer, costs, detailFrame);
                    foreach (KeyValuePair<ushort, int> visit in visits)
                    {
                        writer.WriteLine($"    VISIT {visit.Key:X4} {visit.Value}");
                    }

                    visits.Clear();
                }

                Array.Clear(opcodeCounts);
            }

            if (machine.Cpu.Iff1)
            {
                interruptsEnabledFrames++;
            }

            if (session.PlaybackFrame > previousFrame)
            {
                long spent = (long)(machine.Cpu.Cyc - previousCycles) / (session.PlaybackFrame - previousFrame);
                if (traceTstates)
                {
                    // One line per completed frame, unfiltered: what separates a clock
                    // that is uniformly slow from one that loses time in bursts is the
                    // shape of this column, not its average.
                    writer.WriteLine(
                        $"T {previousFrame} {spent} {spent - frameTstatesReference:+#;-#;0} "
                            + $"{recording.Frames[previousFrame].FetchCount} "
                            + $"{machine.Cpu.WaitStates - previousWaits} "
                            + $"{machine.Cpu.PC:x4} {(machine.Cpu.Iff1 ? 1 : 0)} "
                            + $"{machine.Cpu.AF:x4} {machine.Cpu.BC:x4} {machine.Cpu.DE:x4} "
                            + $"{machine.Cpu.HL:x4} {machine.Cpu.IX:x4} {machine.Cpu.IY:x4} "
                            + $"{machine.Cpu.SP:x4} {machine.Cpu.R & 0x7f:x2}");
                }

                previousWaits = machine.Cpu.WaitStates;

                shortest = Math.Min(shortest, spent);
                longest = Math.Max(longest, spent);
                previousCycles = machine.Cpu.Cyc;
                previousFrame = session.PlaybackFrame;
            }
        }

        // How long this emulator took to run the opcodes the recording counts out. A
        // recording made at the machine's own frame rate should come to one frame's worth
        // of T-states each time; anything else says the two clocks are drifting apart,
        // which is what eventually moves an input into the wrong frame.
        int frameTstates = frameTstatesReference;
        if (previousFrame > 0)
        {
            // Measure only the frames that actually completed: the part-frame replay
            // stopped in the middle of would otherwise be averaged in as a whole one.
            double perFrame = (previousCycles - startCycles) / (double)previousFrame;
            writer.WriteLine(
                $"Replayed {perFrame:F0} T-states per recorded frame ({frameTstates} in a real frame, "
                    + $"{100.0 * perFrame / frameTstates:F2}%), shortest {shortest}, longest {longest}");
        }

        if (visits.Count > 0)
        {
            ushort hottest = 0;
            int most = 0;
            foreach (KeyValuePair<ushort, int> visit in visits)
            {
                if (visit.Value > most)
                {
                    most = visit.Value;
                    hottest = visit.Key;
                }
            }

            var bytes = new System.Text.StringBuilder();
            for (int i = 0; i < 8; i++)
            {
                bytes.Append($"{machine.Memory.ReadDirect((ushort)(hottest + i)):X2} ");
            }

            writer.WriteLine($"Hottest address ${hottest:X4} x{most}: {bytes}");

            writer.WriteLine(
                $"  BC={machine.Cpu.BC:X4} DE={machine.Cpu.DE:X4} HL={machine.Cpu.HL:X4} PC={machine.Cpu.PC:X4}");
        }

        // The frame replay stopped inside never reaches its end-of-frame report, and that
        // is exactly the frame worth looking at.
        foreach (KeyValuePair<ushort, int> visit in visits)
        {
            writer.WriteLine($"    VISIT {visit.Key:X4} {visit.Value}");
        }

        writer.WriteLine(
            $"Interrupts enabled at the end of {interruptsEnabledFrames} frames, "
                + $"{machine.Cpu.InterruptsAccepted} accepted in total");
        double percent = recording.Frames.Count == 0
            ? 0
            : 100.0 * session.PlaybackFrame / recording.Frames.Count;
        writer.WriteLine(
            $"Reached frame {session.PlaybackFrame} of {recording.Frames.Count} ({percent:F1}%, {session.PlaybackFrame / 50.0:F1}s)");
        if (session.DesyncReason != null)
        {
            writer.WriteLine($"Stopped: {session.DesyncReason}");
        }

        bool complete = session.DesyncReason == null && session.PlaybackFrame >= recording.Frames.Count;
        writer.WriteLine(complete ? "Result: PASS" : "Result: FAIL");
        return complete ? 0 : 1;
    }

    /// <summary>What one opcode cost, over every time it ran in a frame.</summary>
    /// <remarks>
    /// The spread is the interesting part: an instruction that always costs the same is
    /// running uncontended, so its cost can be checked straight against the published
    /// timing. One whose cost varies is being contended, and only its shape can be judged.
    /// </remarks>

    /// <summary>
    /// Counts the bus cycles of a single frame by where they landed.
    /// </summary>
    /// <remarks>
    /// A frame's wait-state total says how long the machine was held but not what held
    /// it. Splitting the cycles by page, and by whether they were real accesses or the
    /// internal cycles an instruction spends parked on an address, says whether a
    /// disagreement over a frame's length is about which accesses are contended or about
    /// how much each one costs.
    /// </remarks>
    private sealed class BusTally : ZedExEss.Z80CPU.IZ80BusObserver
    {
        private readonly long[] _accesses = new long[4];
        private readonly long[] _accessCycles = new long[4];
        private readonly long[] _internal = new long[4];
        private long _ports;

        public void OnContend(ushort address, int cycles)
        {
            int page = address >> 14;
            if (cycles == 1)
            {
                _internal[page]++;
                return;
            }

            _accesses[page]++;
            _accessCycles[page] += cycles;
        }

        public void OnRead(ushort address, byte value)
        {
        }

        public void OnWrite(ushort address, byte value)
        {
        }

        public void OnPortContend(ushort port, int cycles) => _ports += cycles;

        public void OnPortRead(ushort port, byte value)
        {
        }

        public void OnPortWrite(ushort port, byte value)
        {
        }

        public void Report(StreamWriter writer)
        {
            for (int page = 0; page < 4; page++)
            {
                if (_accesses[page] == 0 && _internal[page] == 0)
                {
                    continue;
                }

                writer.WriteLine(
                    $"    BUS {page * 0x4000:X4}-{(page * 0x4000) + 0x3FFF:X4} "
                        + $"accesses {_accesses[page]} ({_accessCycles[page]} T), "
                        + $"internal cycles {_internal[page]}");
            }

            writer.WriteLine($"    BUS ports {_ports} T");
        }
    }


    /// <summary>
    /// Writes the machine and the recording in the flat form the reference harness reads.
    /// </summary>
    /// <remarks>
    /// The harness runs the reference's own CPU over the same recording so the two can be
    /// compared frame by frame. Handing it a memory image taken after the snapshot has
    /// been restored keeps snapshot decoding out of it: it needs a machine to start from,
    /// not a file format.
    /// </remarks>
    private static void ExportForReference(
        SpectrumMachine machine,
        FileHandlers.RzxRecording recording,
        string path,
        StreamWriter writer)
    {
        using FileStream file = File.Create(path);
        using var output = new BinaryWriter(file);

        output.Write(new[] { (byte)'Z', (byte)'X', (byte)'R', (byte)'F' });
        output.Write((uint)recording.Frames.Count);

        Z80CPU.Z80 cpu = machine.Cpu;
        foreach (ushort value in new[]
        {
            cpu.AF, cpu.BC, cpu.DE, cpu.HL, cpu.AF_, cpu.BC_, cpu.DE_, cpu.HL_,
            cpu.IX, cpu.IY, cpu.SP, cpu.PC, cpu.MemPtr
        })
        {
            output.Write(value);
        }

        output.Write(cpu.I);
        output.Write(cpu.R);
        output.Write((byte)(cpu.Iff1 ? 1 : 0));
        output.Write((byte)(cpu.Iff2 ? 1 : 0));
        output.Write(cpu.InterruptModeValue);
        output.Write((byte)(cpu.IsHalted ? 1 : 0));
        output.Write((uint)(cpu.Cyc % (ulong)SpectrumUlaTiming.ForModel(machine.Model).TstatesPerFrame));

        for (int address = 0; address < 0x10000; address++)
        {
            output.Write(machine.Memory.ReadDirect((ushort)address));
        }

        foreach (FileHandlers.RzxFrame frame in recording.Frames)
        {
            output.Write(frame.FetchCount);
            output.Write((ushort)frame.InputValues.Length);
            output.Write(frame.InputValues);
        }

        writer.WriteLine($"Exported {recording.Frames.Count} frames to {path}");
        Console.WriteLine($"Exported {recording.Frames.Count} frames to {path}");
    }


    /// <summary>
    /// Replays the way the host does: whole frames, presented as they complete.
    /// </summary>
    /// <remarks>
    /// The other paths here step one instruction at a time, which is the right way to
    /// measure a recording but not the way anything actually plays one. A host calls
    /// RunFrame, and RunFrame only returns when the display reaches its frame edge - so
    /// a recorded frame that ends before that edge, and re-anchors the beam, leaves the
    /// host with nothing to present. That is invisible to an instruction-stepped test
    /// and very visible to somebody watching a blank window.
    /// </remarks>
    private static int DiagnoseByFrames(
        SpectrumMachine machine,
        SpectrumReplaySession session,
        FileHandlers.RzxRecording recording,
        StreamWriter writer)
    {
        int presented = 0;
        int calls = 0;
        int stalled = 0;
        machine.Emulator.FrameCompleted += () => presented++;

        // Drive it the way the desktop hosts do: a sound device pulling a frame of
        // samples at a time.
        int samplesPerFrame = Spectrum.Audio.SpectrumAudioTiming.DefaultSampleRate / 50;
        var audioBuffer = new short[samplesPerFrame];

        while (session.Mode == SpectrumReplayMode.Playing && calls < recording.Frames.Count + 16)
        {
            int before = presented;
            ulong cycles = machine.Cpu.Cyc;
            machine.Emulator.ReadSamples(audioBuffer, 0, audioBuffer.Length);
            calls++;

            if (presented == before)
            {
                stalled++;
            }

            if (machine.Cpu.Cyc == cycles)
            {
                writer.WriteLine($"FAIL the machine stopped advancing after {calls} frames.");
                break;
            }
        }

        writer.WriteLine($"RunFrame calls: {calls}");
        writer.WriteLine($"Frames presented: {presented}");
        writer.WriteLine($"Calls presenting nothing: {stalled}");
        writer.WriteLine($"Replay reached frame {session.PlaybackFrame} of {recording.Frames.Count}");
        if (session.DesyncReason != null)
        {
            writer.WriteLine($"Stopped: {session.DesyncReason}");
        }

        bool healthy = presented > 0 && stalled == 0;
        writer.WriteLine($"Result: {(healthy ? "PASS" : "FAIL")}");
        Console.WriteLine(
            $"RunFrame replay: {presented} frames presented over {calls} calls, {stalled} blank.");
        return healthy ? 0 : 1;
    }

    private sealed class OpcodeCost
    {
        public int Count;
        public long Total;
        public int Shortest = int.MaxValue;
        public int Longest;
    }

    /// <summary>Identifies the instruction at PC, prefix included.</summary>
    private static int ClassifyOpcode(SpectrumMachine machine)
    {
        byte first = machine.Memory.ReadDirect(machine.Cpu.PC);
        if (first is 0xED or 0xCB or 0xDD or 0xFD)
        {
            return (first << 8) | machine.Memory.ReadDirect((ushort)(machine.Cpu.PC + 1));
        }

        return first;
    }

    private static void RecordCost(Dictionary<int, OpcodeCost> costs, int key, int tstates)
    {
        if (!costs.TryGetValue(key, out OpcodeCost? cost))
        {
            cost = new OpcodeCost();
            costs[key] = cost;
        }

        cost.Count++;
        cost.Total += tstates;
        cost.Shortest = Math.Min(cost.Shortest, tstates);
        cost.Longest = Math.Max(cost.Longest, tstates);
    }

    private static void ReportCosts(StreamWriter writer, Dictionary<int, OpcodeCost> costs, int frame)
    {
        writer.WriteLine($"  --- frame {frame} instruction costs (by total T-states) ---");
        var ordered = new List<KeyValuePair<int, OpcodeCost>>(costs);
        ordered.Sort((left, right) => right.Value.Total.CompareTo(left.Value.Total));
        foreach (KeyValuePair<int, OpcodeCost> entry in ordered)
        {
            OpcodeCost cost = entry.Value;
            string name = entry.Key > 0xFF ? $"{entry.Key >> 8:X2} {entry.Key & 0xFF:X2}" : $"{entry.Key:X2}   ";
            writer.WriteLine(
                $"    {name}  x{cost.Count,-6} total {cost.Total,-8} avg {cost.Total / (double)cost.Count,6:F2}"
                    + $"  min {cost.Shortest,3}  max {cost.Longest,3}");
        }

        costs.Clear();
    }

    /// <summary>Tallies the opcode about to run, following any prefix.</summary>
    private static void CountOpcode(SpectrumMachine machine, int[] counts)
    {
        byte opcode = machine.Memory.ReadDirect(machine.Cpu.PC);

        // ED-prefixed opcodes are tallied in their own half of the table: 0x5F is LD A,R
        // after a prefix but LD E,A without one, and counting them together would make
        // ordinary register moves look like reads of the refresh register.
        if (opcode == 0xED)
        {
            counts[0xED]++;
            counts[256 + machine.Memory.ReadDirect((ushort)(machine.Cpu.PC + 1))]++;
            return;
        }

        counts[opcode]++;
    }

    private static RomSet LoadRoms(SpectrumModel model, string? romDirectory)
    {
        string[] names = model switch
        {
            SpectrumModel.Spectrum16K or SpectrumModel.Spectrum48K => ["48.rom"],
            SpectrumModel.Spectrum128K => ["128_0.rom", "128_1.rom"],
            SpectrumModel.SpectrumPlus2 => ["plus2_0.rom", "plus2_1.rom"],
            SpectrumModel.SpectrumPlus2A or SpectrumModel.SpectrumPlus3 =>
                ["plus3-0.rom", "plus3-1.rom", "plus3-2.rom", "plus3-3.rom"],
            _ => throw new NotSupportedException($"Replay does not support {model}.")
        };

        string root = romDirectory ?? Path.Combine(AppContext.BaseDirectory, "ROMs");
        string[] paths = new string[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            paths[i] = Path.Combine(root, names[i]);
        }

        return RomSet.LoadFromFiles(paths);
    }

    /// <summary>
    /// Proves the early and late contention models are actually different machines to run
    /// on, rather than a setting that only changes a label.
    /// </summary>

    /// <summary>
    /// Replays frames that end before the beam does, the way a host does, and requires
    /// one presented frame for each.
    /// </summary>
    /// <remarks>
    /// Every other check here steps single instructions, which is the right way to
    /// measure a replay and not the way one is ever watched. A host calls RunFrame, and
    /// RunFrame returns when the display reaches its frame edge - but a replayed frame
    /// moves that edge, so a recording whose frames end just before it leaves the host
    /// inside RunFrame with nothing to present, for as long as the recording lasts.
    ///
    /// The frames here are deliberately short of a full one, because that is what real
    /// recordings look like: a recording made elsewhere has no reason to end its frames
    /// exactly where this emulator's beam does, and one built by recording from this
    /// emulator would, which is why it has to be constructed rather than captured.
    /// </remarks>
    private static void VerifyHostDrivenPlayback(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine replayMachine = CreateMachine(machineRom);
        var replay = new SpectrumReplaySession();
        replay.AttachMachine(replayMachine);
        FileHandlers.RzxRecording recording = BuildShortFrameRecording(machineRom);
        replay.StartPlayback(recording);
        Require(replay.Mode == SpectrumReplayMode.Playing, "Playback should have started.");

        int presented = 0;
        replayMachine.Emulator.FrameCompleted += () => presented++;

        int calls = 0;
        int blank = 0;
        while (replay.Mode == SpectrumReplayMode.Playing && calls < recording.Frames.Count + 4)
        {
            int before = presented;
            ulong cycles = replayMachine.Cpu.Cyc;
            replayMachine.Emulator.RunFrame(presentFrame: true);
            calls++;

            if (presented == before)
            {
                blank++;
            }

            if (replayMachine.Cpu.Cyc - cycles > 3UL * 69888UL)
            {
                blank++;
                break;
            }
        }

        Require(replay.DesyncReason == null, $"Replay diverged: {replay.DesyncReason}");
        Require(blank == 0, $"{blank} of {calls} frame runs presented nothing.");
        Require(
            presented == calls,
            $"{calls} frame runs presented {presented} frames; they should match.");
        Require(
            calls == recording.Frames.Count,
            $"{recording.Frames.Count} recorded frames needed {calls} frame runs to play.");
    }

    /// <summary>
    /// Replays with the sound card pulling samples, which is how the desktop hosts
    /// actually drive the machine, and requires a frame for every recorded one.
    /// </summary>
    /// <remarks>
    /// This is a separate check from the one above because it is a separate driver:
    /// RunFrame and ReadSamples decide independently when a frame is ready to show, and
    /// fixing one says nothing about the other. The hosts use this one.
    /// </remarks>
    private static void VerifyAudioDrivenPlayback(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine replayMachine = CreateMachine(machineRom);
        var replay = new SpectrumReplaySession();
        replay.AttachMachine(replayMachine);
        FileHandlers.RzxRecording recording = BuildShortFrameRecording(machineRom);
        replay.StartPlayback(recording);

        int presented = 0;
        replayMachine.Emulator.FrameCompleted += () => presented++;

        // Roughly one frame of audio per pull, as a sound device asks for it.
        int samplesPerFrame = Spectrum.Audio.SpectrumAudioTiming.DefaultSampleRate / 50;
        var buffer = new short[samplesPerFrame];

        int pulls = 0;
        while (replay.Mode == SpectrumReplayMode.Playing && pulls < recording.Frames.Count + 4)
        {
            replayMachine.Emulator.ReadSamples(buffer, 0, buffer.Length);
            pulls++;
        }

        Require(replay.DesyncReason == null, $"Replay diverged: {replay.DesyncReason}");
        Require(
            presented >= recording.Frames.Count - 1,
            $"{pulls} sample pulls covering {replay.PlaybackFrame} recorded frames "
                + $"presented only {presented} frames.");
    }

    /// <summary>
    /// Builds a recording whose frames end before the beam reaches the bottom, which is
    /// what a recording made on another emulator looks like.
    /// </summary>
    /// <remarks>
    /// It has to be constructed rather than captured: a recording made by this emulator
    /// ends its frames exactly on its own beam edge, so it cannot show the fault this
    /// pair of checks exists for.
    /// </remarks>
    private static FileHandlers.RzxRecording BuildShortFrameRecording(string machineRom)
    {
        // DI, then a two-byte branch to itself: no interrupts, no ports, and one opcode
        // fetch every twelve T-states, so a frame length can be asked for exactly.
        const ushort Program = 0x8000;
        const int FetchesPerFrame = 5700;   // ~68400 T, comfortably inside a frame
        const int Frames = 12;

        SpectrumMachine machine = CreateMachine(machineRom);
        machine.Memory.WriteDirect(Program, 0xF3);
        machine.Memory.WriteDirect((ushort)(Program + 1), 0x18);
        machine.Memory.WriteDirect((ushort)(Program + 2), 0xFE);
        machine.Cpu.PC = Program;

        var source = new SpectrumReplaySession();
        source.AttachMachine(machine);
        source.StartRecording();

        // The first frame only arms the recording; a few more give it something to hold
        // a snapshot for. Only the snapshot is kept - the frames are built by hand.
        for (int i = 0; i < 4; i++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        source.Stop();
        FileHandlers.RzxRecording captured = source.BuildRecording();

        var frames = new List<FileHandlers.RzxFrame>();
        for (int i = 0; i < Frames; i++)
        {
            frames.Add(new FileHandlers.RzxFrame((ushort)FetchesPerFrame, [], false));
        }

        return new FileHandlers.RzxRecording
        {
            SnapshotExtension = captured.SnapshotExtension,
            SnapshotData = captured.SnapshotData,
            Frames = frames,
            StartTstates = captured.StartTstates
        };
    }

    private static void VerifyContentionTimings(string machineRom)
    {
        RequireRom(machineRom);

        long early = TimeContendedWrites(machineRom, SpectrumContentionTiming.Early);
        long late = TimeContendedWrites(machineRom, SpectrumContentionTiming.Late);
        Require(
            early != late,
            $"Both contention models took {early} T-states, so the setting does nothing.");

        // A one T-state shift of the window, not a different amount of contention.
        Require(
            Math.Abs(early - late) < early / 10,
            $"The two models should differ slightly, not by {early} against {late}.");
    }

    /// <summary>Runs a loop of writes into contended screen memory and times it.</summary>
    private static long TimeContendedWrites(string machineRom, SpectrumContentionTiming timing)
    {
        SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum48K,
            Roms = RomSet.LoadFromFiles([machineRom]),
            RenderEnabled = false,
            ContentionTiming = timing
        });

        // LD (HL),A / INC L / JR loop, running from uncontended RAM into the screen.
        ushort address = 0x8000;
        foreach (byte value in new byte[] { 0x77, 0x2C, 0x18, 0xFC })
        {
            machine.Memory.WriteDirect(address++, value);
        }

        machine.Cpu.PC = 0x8000;
        machine.Cpu.H = 0x40;
        machine.Cpu.L = 0x00;

        ulong start = machine.Cpu.Cyc;
        for (int i = 0; i < 30000; i++)
        {
            machine.Emulator.StepInstruction();
        }

        return (long)(machine.Cpu.Cyc - start);
    }

    /// <summary>
    /// A replay has to run the machine unaided: the tape accelerators skip T-states when
    /// they think a loader is polling, and the repetitive port reads a recording hands
    /// back look exactly like one.
    /// </summary>
    private static void VerifyAcceleratorsSuspended(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        var session = new SpectrumReplaySession();
        session.AttachMachine(machine);

        SettleIntoBasic(machine);
        session.StartRecording();
        machine.Emulator.RunFrame(presentFrame: false);
        RunWithKeys(machine, session, 4);
        session.Stop();
        RzxRecording recording = session.BuildRecording();

        SpectrumMachine replayMachine = CreateMachine(machineRom);
        replayMachine.EarInput.EdgeLoadingEnabled = true;
        replayMachine.EarInput.SemanticAccelerationEnabled = true;
        replayMachine.EarInput.AutoPlayEnabled = true;

        var replay = new SpectrumReplaySession();
        replay.AttachMachine(replayMachine);
        replay.StartPlayback(recording);

        Require(!replayMachine.EarInput.EdgeLoadingEnabled, "Polling acceleration should be off while replaying.");
        Require(
            !replayMachine.EarInput.SemanticAccelerationEnabled,
            "Semantic acceleration should be off while replaying.");
        Require(!replayMachine.EarInput.AutoPlayEnabled, "Auto tape play should be off while replaying.");

        replay.Stop();

        // And the user's own settings come back afterwards.
        Require(replayMachine.EarInput.EdgeLoadingEnabled, "Polling acceleration should be restored.");
        Require(replayMachine.EarInput.SemanticAccelerationEnabled, "Semantic acceleration should be restored.");
        Require(replayMachine.EarInput.AutoPlayEnabled, "Auto tape play should be restored.");
    }


    /// <summary>
    /// Frame advance has to move the machine exactly one frame while it stays paused,
    /// and it has to tell the host so the picture keeps up.
    /// </summary>
    /// <remarks>
    /// The presentation half is the part worth asserting. A paused host has shut its
    /// audio driver down, so nothing else is going to raise FrameCompleted; if advance
    /// only ran the CPU, the machine would move and the screen would not, which looks
    /// exactly like a hang.
    /// </remarks>
    private static void VerifyFrameAdvance(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        SettleIntoBasic(machine);

        int presented = 0;
        machine.Emulator.FrameCompleted += () => presented++;
        machine.Emulator.SetPaused(true);

        int framesBefore = machine.Renderer.FrameCounter;
        ulong cyclesBefore = machine.Cpu.Cyc;

        Require(machine.Emulator.StepFrame(), "Frame advance should have reached a frame edge.");

        Require(machine.Emulator.IsPaused, "Frame advance left the machine running.");
        Require(machine.Renderer.FrameCounter == framesBefore + 1,
            $"Frame advance ran {machine.Renderer.FrameCounter - framesBefore} frames, not one.");
        Require(presented == 1, $"Frame advance presented {presented} frames, not one.");

        // A whole frame of work, not an instruction that happened to cross the edge.
        ulong elapsed = machine.Cpu.Cyc - cyclesBefore;
        Require(elapsed > 60_000 && elapsed < 80_000,
            $"Frame advance covered {elapsed} T-states, which is not one 48K frame.");

        // And it keeps working, one frame at a time.
        for (int frame = 0; frame < 5; frame++)
        {
            Require(machine.Emulator.StepFrame(), $"Frame advance {frame + 2} should have worked.");
        }

        Require(machine.Renderer.FrameCounter == framesBefore + 6,
            "Repeated frame advances should each move exactly one frame.");
        Require(presented == 6, $"Six advances presented {presented} frames.");
        Require(machine.Emulator.IsPaused, "The machine should still be paused at the end.");
    }

    /// <summary>
    /// A breakpoint inside the frame has to win, or advancing a frame would run straight
    /// past the thing the user stopped to look at.
    /// </summary>
    private static void VerifyFrameAdvanceStopsAtBreak(string machineRom)
    {
        RequireRom(machineRom);

        // The debugger breaks by pausing the emulator from inside its own step hook, so
        // that is what is imitated here: the machine is what the hook has to reach.
        SpectrumMachine? built = null;
        bool armed = false;
        bool broke = false;
        int stepsUntilBreak = 200;
        SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum48K,
            Roms = RomSet.LoadFromFiles([machineRom]),
            RenderEnabled = false,
            BeforeCpuStep = () =>
            {
                // Settling the machine runs millions of instructions, so the break is
                // armed afterwards and fires once, the way a breakpoint being set does.
                if (!armed || broke || --stepsUntilBreak > 0)
                {
                    return false;
                }

                broke = true;
                built!.Emulator.SetPaused(true);
                return true;
            }
        });
        built = machine;

        SettleIntoBasic(machine);
        machine.Emulator.SetPaused(true);
        armed = true;

        int framesBefore = machine.Renderer.FrameCounter;
        int presented = 0;
        machine.Emulator.FrameCompleted += () => presented++;

        Require(!machine.Emulator.StepFrame(),
            "Frame advance reported a whole frame despite breaking part way through it.");
        Require(machine.Emulator.IsPaused, "The break should have left the machine paused.");
        Require(machine.Renderer.FrameCounter == framesBefore,
            "Frame advance passed the frame edge even though it broke first.");
        Require(presented == 0, "A frame that never finished should not have been presented.");
        Require(broke, "The break hook did not fire.");

        // Advancing again from the break carries on into the same frame and finishes it,
        // so a break costs the user nothing but the stop.
        Require(machine.Emulator.StepFrame(), "Advancing again should finish the broken frame.");
        Require(machine.Renderer.FrameCounter == framesBefore + 1,
            "The frame after the break should have completed exactly once.");
        Require(presented == 1, $"The resumed frame presented {presented} times.");
    }

    /// <summary>
    /// Loading a saved state during a recording has to put the recording back to the same
    /// point and count the retake, exactly as stepping back does.
    /// </summary>
    private static void VerifyStateSlotRetake(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        var session = new SpectrumReplaySession();
        var slots = new SpectrumStateSlots();
        session.AttachMachine(machine);

        SettleIntoBasic(machine);
        session.StartRecording();
        machine.Emulator.RunFrame(presentFrame: false);
        Require(session.ReRecordCount == 0, "A new recording should start with no re-records.");

        for (int frame = 0; frame < 20; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        int savedAt = session.FrameCount;
        SpectrumReplayPosition savedPosition = session.Position;
        slots.Save(3, machine, null, savedPosition);
        Require(slots.IsOccupied(3), "The state should have been saved.");
        long fingerprint = Fingerprint(machine);

        for (int frame = 0; frame < 20; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        Require(session.FrameCount > savedAt, "The recording should have grown past the saved point.");

        Require(slots.TryLoad(3, machine, null, out SpectrumReplayPosition restoredPosition),
            "The saved state should have loaded.");
        Require(restoredPosition.RecordedFrameCount == savedAt,
            $"The slot reported frame {restoredPosition.RecordedFrameCount}, "
                + $"not the {savedAt} it was saved at.");
        session.OnMachineMovedBack(machine, restoredPosition);

        Require(Fingerprint(machine) == fingerprint, "The machine did not come back to the saved state.");
        Require(session.FrameCount == savedAt,
            $"The recording kept {session.FrameCount} frames instead of trimming to {savedAt}.");
        Require(session.ReRecordCount == 1, $"The retake counted {session.ReRecordCount} re-records, not one.");
        Require(session.Mode == SpectrumReplayMode.Recording, "Recording should continue after a retake.");

        // Recording resumes from the restored point rather than from where it left off.
        for (int frame = 0; frame < 5; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        Require(session.FrameCount == savedAt + 5, "Recording should resume from the loaded state.");

        // Loading the same slot again is another retake, and the slot survives being used.
        Require(slots.TryLoad(3, machine, null, out _), "A slot should be reusable.");
        session.OnMachineMovedBack(machine, savedPosition);
        Require(session.ReRecordCount == 2, "A second retake should have been counted.");
    }

    /// <summary>
    /// A state saved on one model cannot be loaded onto another, and saying so beats
    /// throwing out of the snapshot service or, worse, half-restoring the machine.
    /// </summary>
    private static void VerifyStateSlotModelMismatch(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        SettleIntoBasic(machine);

        var slots = new SpectrumStateSlots();
        slots.Save(0, machine, null, default);

        Require(!slots.IsOccupied(1), "An unused slot should be empty.");
        Require(!slots.TryLoad(1, machine, null, out _), "An empty slot should not load.");
        Require(slots.Peek(0)?.Snapshot.Model == SpectrumModel.Spectrum48K,
            "The slot should report the model it holds.");

        SpectrumMachine other = SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum128K,
            Roms = RomSet.LoadFromFiles([machineRom, machineRom]),
            RenderEnabled = false
        });

        long before = Fingerprint(other);
        Require(!slots.TryLoad(0, other, null, out _),
            "A 48K state should not load into a 128K machine.");
        Require(Fingerprint(other) == before, "The refused load still disturbed the machine.");

        slots.Clear();
        Require(!slots.IsOccupied(0), "Clearing should empty every slot.");
    }


    /// <summary>
    /// One frame back has to land on that frame and no other, because it is the partner
    /// of frame advance: press one then the other and nothing should have moved.
    /// </summary>
    private static void VerifyStepBackOneFrame(string machineRom)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        var session = new SpectrumReplaySession();
        session.AttachMachine(machine);

        SettleIntoBasic(machine);
        for (int frame = 0; frame < 40; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        int frameBefore = session.CurrentFrame;
        long before = Fingerprint(machine);

        machine.Emulator.SetPaused(true);
        Require(machine.Emulator.StepFrame(presentFrame: false), "Frame advance should have worked.");
        Require(session.CurrentFrame == frameBefore + 1,
            $"Advancing moved to frame {session.CurrentFrame}, not {frameBefore + 1}.");
        Require(Fingerprint(machine) != before, "Advancing a frame changed nothing.");

        Require(session.StepBackFrames(1), "Stepping back one frame should have worked.");
        Require(session.CurrentFrame == frameBefore,
            $"Stepping back landed on frame {session.CurrentFrame}, not {frameBefore}.");
        Require(Fingerprint(machine) == before,
            "Advance then step back should leave the machine exactly as it was.");
    }

    /// <summary>
    /// The point of keeping states across the whole run: any earlier frame can be
    /// returned to, not just the last few seconds.
    /// </summary>
    /// <remarks>
    /// The state restored has to be complete, so the test is not that the machine looks
    /// plausible but that running the same frames again arrives at exactly the same
    /// place - checked from three different distances back.
    /// </remarks>
    private static void VerifyHistorySeek(string machineRom, StreamWriter writer)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        var session = new SpectrumReplaySession();
        session.AttachMachine(machine);

        SettleIntoBasic(machine);
        int start = session.CurrentFrame;

        // Twelve seconds, which no fixed few-second ring could reach the beginning of.
        for (int frame = 0; frame < 600; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        int end = session.CurrentFrame;
        long atEnd = Fingerprint(machine);
        Require(session.RewindSecondsAvailable > 11.0,
            $"Twelve seconds of running left only {session.RewindSecondsAvailable:F1}s of history.");
        Require(session.OldestFrameAvailable <= start + SpectrumStateHistory.DefaultIntervalFrames,
            $"The history starts at frame {session.OldestFrameAvailable}, not near {start}.");

        foreach (int target in new[] { end - 500, end - 250, end - 7 })
        {
            Require(session.TrySeekToFrame(target), $"Seeking to frame {target} failed.");

            int landed = session.CurrentFrame;
            Require(landed == target, $"Seeking to frame {target} landed on {landed}.");

            for (int frame = landed; frame < end; frame++)
            {
                machine.Emulator.RunFrame(presentFrame: false);
            }

            Require(session.CurrentFrame == end,
                $"Re-running reached frame {session.CurrentFrame}, not {end}.");
            Require(Fingerprint(machine) == atEnd,
                $"Re-running from frame {landed} did not reproduce the machine.");
        }

        writer.WriteLine(
            $"  history {session.RewindDepth} states over {session.RewindSecondsAvailable:F1}s, "
                + $"{session.History.BytesUsed / 1024}KB against "
                + $"{session.History.UnsharedBytes / 1024}KB stored whole");

        Require(!session.TrySeekToFrame(session.OldestFrameAvailable - 1),
            "Seeking before the start of the history should fail rather than guess.");
    }

    /// <summary>
    /// Going back has to put the PSG back mid-note, not at the start of one.
    /// </summary>
    /// <remarks>
    /// The registers of an AY are not the whole chip. Restore them alone and every tone starts
    /// again at the beginning of its square wave, the noise shift register goes back to its
    /// seed and the envelope to step zero, so a sustained note clicks on every rewind - which,
    /// in a tool whose whole purpose is rewinding constantly, is the difference between usable
    /// and not.
    ///
    /// The comparison is made on the chip rather than on the emulator's sample stream, because
    /// the host's spill buffer is not machine state: it holds samples already produced, a
    /// restore deliberately leaves it alone, and comparing it would compare how much of it each
    /// path happened to have left. Rendering from two mirror chips loaded with the two captured
    /// phases is the same renderer the emulator uses, and it answers the question that matters:
    /// would this be audible.
    /// </remarks>
    private static void VerifyRewindKeepsPsgPhase(string machineRom, StreamWriter writer)
    {
        RequireRom(machineRom);

        // A 128K machine, because that is the one with a PSG. The 48K image is loaded into both
        // ROM slots as the other checks here do: what the ROM does is irrelevant, having an AY
        // and a second bank is the point.
        SpectrumMachine machine = SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum128K,
            Roms = RomSet.LoadFromFiles([machineRom, machineRom]),
            RenderEnabled = false
        });
        var session = new SpectrumReplaySession();
        session.AttachMachine(machine);

        SettleIntoBasic(machine);
        Require(machine.AyChip != null, "A 128K machine without a PSG cannot prove anything here.");

        // A tone, noise and a shaped envelope, all running, so every generator has phase to
        // lose. A silent chip would pass this check with no restore at all.
        machine.AyChip!.WriteRegister(0, 0x80);     // channel A period, fine
        machine.AyChip.WriteRegister(1, 0x01);      // channel A period, coarse
        machine.AyChip.WriteRegister(6, 0x0F);      // noise period
        machine.AyChip.WriteRegister(7, 0x30);      // mixer: tone and noise on A
        machine.AyChip.WriteRegister(8, 0x10);      // channel A follows the envelope
        machine.AyChip.WriteRegister(11, 0x00);     // envelope period
        machine.AyChip.WriteRegister(12, 0x08);
        machine.AyChip.WriteRegister(13, 0x0E);     // envelope shape: repeating

        // Driven by sample pulls rather than by whole frames, because a PSG only advances when
        // somebody asks for audio: a machine nobody is listening to leaves its generators
        // exactly where they were, which would make every assertion below vacuous.
        int samplesPerFrame = Spectrum.Audio.SpectrumAudioTiming.DefaultSampleRate / 50;
        var pull = new short[samplesPerFrame];
        for (int frame = 0; frame < 24; frame++)
        {
            machine.Emulator.ReadSamples(pull, 0, pull.Length);
        }

        SpectrumMachineSnapshot before = SpectrumMachineSnapshotService.Capture(machine);
        Require(before.Ay?.Phase != null, "A live capture did not record the PSG's phase.");
        AySnapshotState phaseBefore = before.Ay!.Phase!;
        Require(phaseBefore.NoiseRng != 1 || phaseBefore.ToneCounterA != 0 || phaseBefore.EnvelopeStep != 0,
            "The PSG never got going, so there is no phase here to lose.");

        int target = session.CurrentFrame;
        for (int frame = 0; frame < 10; frame++)
        {
            machine.Emulator.ReadSamples(pull, 0, pull.Length);
        }

        Require(!AyPhaseMatches(machine.AyChip.CaptureState(), phaseBefore),
            "Ten frames of audio did not move the PSG on, so going back to it would prove nothing.");

        SpectrumMachineSnapshotService.Restore(machine, before);
        AySnapshotState after = machine.AyChip.CaptureState();

        Require(after.Registers.AsSpan().SequenceEqual(phaseBefore.Registers),
            "Restoring did not put the PSG's registers back.");
        Require(AyPhaseMatches(after, phaseBefore),
            "Restoring restarted the PSG's generators instead of continuing them: "
                + DescribeAyPhaseDifference(phaseBefore, after));

        // And that this is audible rather than bookkeeping: the same renderer, from the two
        // phases, has to produce the same samples.
        short[] expected = RenderAyPhase(phaseBefore, 2048);
        short[] actual = RenderAyPhase(after, 2048);
        Require(actual.AsSpan().SequenceEqual(expected),
            "A restored PSG renders different audio from the one that was captured.");

        // The greenzone stores these same snapshots, so a rewind through it has to arrive with
        // generators that are running rather than ones that have just been reset. The exact
        // phase at an arbitrary stored frame is not known here, but the reset values are.
        for (int frame = 0; frame < 12; frame++)
        {
            machine.Emulator.ReadSamples(pull, 0, pull.Length);
        }

        int stored = session.History.FrameAt(Math.Max(0, session.RewindDepth - 3));
        Require(session.TrySeekToFrame(stored), $"Seeking back to stored frame {stored} failed.");
        AySnapshotState rewound = machine.AyChip.CaptureState();
        Require(rewound.NoiseRng != 1 || rewound.EnvelopeStep != 0 || rewound.ToneCounterA != 0,
            "A rewind left the PSG's generators at their reset values, which is the click this "
                + "is here to prevent.");

        // The other half of the contract: a snapshot with no phase in it - which is every
        // snapshot that came out of an SZX file, because the format cannot express one - still
        // restores through the registers, and says so by starting its generators again.
        var fileLike = new SpectrumMachineSnapshot(
            before.Model,
            before.Cpu,
            [.. Enumerable.Range(0, before.RamBankCount).Select(before.CopyRamBank)],
            before.Port7FFD,
            before.Port1FFD,
            before.UlaOutput,
            before.FrameTstate,
            before.FrameCounter,
            new SpectrumAySnapshot(before.Ay!.SelectedRegister, before.Ay.Registers),
            before.Interface1,
            before.HeldInterruptTstates);
        SpectrumMachineSnapshotService.Restore(machine, fileLike);
        AySnapshotState fromFile = machine.AyChip.CaptureState();
        Require(fromFile.Registers.AsSpan().SequenceEqual(phaseBefore.Registers),
            "A register-only restore lost the registers as well.");
        Require(!AyPhaseMatches(fromFile, phaseBefore),
            "A register-only restore was expected to start the generators again, and did not.");

        writer.WriteLine(
            $"  restored PSG -> noise ${after.NoiseRng:X5}, envelope step {after.EnvelopeStep}, "
                + $"tone phase {after.ToneCounterA}; rewound to frame {stored}");
    }

    /// <summary>Compares two PSG captures ignoring the registers.</summary>
    private static bool AyPhaseMatches(AySnapshotState left, AySnapshotState right) =>
        left with { Registers = right.Registers } == right;

    /// <summary>Names the first generator whose phase differs, for a failure message.</summary>
    private static string DescribeAyPhaseDifference(AySnapshotState expected, AySnapshotState actual)
    {
        if (actual.NoiseRng != expected.NoiseRng)
        {
            return $"the noise shift register is ${actual.NoiseRng:X5}, not ${expected.NoiseRng:X5}";
        }

        if (actual.EnvelopeStep != expected.EnvelopeStep || actual.EnvelopeVolume != expected.EnvelopeVolume)
        {
            return $"the envelope is at step {actual.EnvelopeStep}/volume {actual.EnvelopeVolume}, "
                + $"not {expected.EnvelopeStep}/{expected.EnvelopeVolume}";
        }

        if (actual.ToneCounterA != expected.ToneCounterA)
        {
            return $"channel A's square wave is at {actual.ToneCounterA}, not {expected.ToneCounterA}";
        }

        return "its counters differ";
    }

    /// <summary>Renders from a captured phase without disturbing any running machine.</summary>
    private static short[] RenderAyPhase(AySnapshotState phase, int samples)
    {
        var mirror = new AY38912();
        mirror.RestoreState(phase);
        var rendered = new short[samples * 2];
        mirror.RenderSamples(samples, rendered, 0);
        return rendered;
    }

    /// <summary>
    /// The budget is what makes a whole run affordable, so exceeding it is a failure and
    /// so is spending it all on the recent past.
    /// </summary>
    /// <remarks>
    /// Thinning has to leave the run's beginning reachable and the frames being worked on
    /// exact, which is the whole design: coarse where nobody is looking, dense where the
    /// work happens.
    /// </remarks>
    private static void VerifyHistoryBudget(string machineRom, StreamWriter writer)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        var session = new SpectrumReplaySession();
        session.AttachMachine(machine);

        // Small enough that a few seconds of running has to start thinning. Sharing
        // pages between states made this an order of magnitude smaller than it was:
        // a budget that used to force thinning now holds the whole run comfortably.
        const long Budget = 768L * 1024;
        session.History.BudgetBytes = Budget;

        SettleIntoBasic(machine);
        int start = session.CurrentFrame;
        for (int frame = 0; frame < 1500; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        int end = session.CurrentFrame;
        Require(session.History.BytesUsed <= Budget,
            $"The history grew to {session.History.BytesUsed} bytes against a {Budget} budget.");

        // The start of the run survives however long it goes on.
        Require(session.OldestFrameAvailable <= start + SpectrumStateHistory.DefaultIntervalFrames,
            $"Thinning lost the beginning: the history now starts at {session.OldestFrameAvailable}.");
        Require(session.RewindDepth > 8, $"Only {session.RewindDepth} states survived thinning.");

        // Recent frames are still stored at full density.
        int newest = session.History.FrameAt(session.RewindDepth - 1);
        int previous = session.History.FrameAt(session.RewindDepth - 2);
        Require(newest - previous <= SpectrumStateHistory.DefaultIntervalFrames,
            $"The newest states are {newest - previous} frames apart, not the interval.");

        // Older ones are not, which is where the room for the recent ones came from.
        int oldGap = session.History.FrameAt(1) - session.History.FrameAt(0);
        Require(oldGap > SpectrumStateHistory.DefaultIntervalFrames,
            $"The oldest states are still {oldGap} frames apart, so nothing was thinned.");

        writer.WriteLine(
            $"  budget {Budget / 1024}KB held {session.RewindDepth} states over "
                + $"{end - start} frames; oldest gap {oldGap}, newest gap {newest - previous}");

        // And a coarse old state is still a real one: it restores and runs forward.
        long atEnd = Fingerprint(machine);
        Require(session.TrySeekToFrame(start + ((end - start) / 2)), "Seeking into the thinned region failed.");
        for (int frame = session.CurrentFrame; frame < end; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        Require(Fingerprint(machine) == atEnd, "A thinned state did not restore correctly.");
    }


    /// <summary>
    /// A frame changes a few kilobytes of a machine that holds tens of them, so a stored
    /// state has to cost what changed rather than what the machine contains.
    /// </summary>
    /// <remarks>
    /// This is the difference between a history that covers a run and one that fills
    /// memory in a minute, so the saving is asserted rather than assumed - and asserted
    /// alongside a restore, because sharing pages between states is only worth anything
    /// if the states still come back whole.
    /// </remarks>
    private static void VerifyHistorySharing(string machineRom, StreamWriter writer)
    {
        RequireRom(machineRom);
        SpectrumMachine machine = CreateMachine(machineRom);
        var session = new SpectrumReplaySession();
        session.AttachMachine(machine);

        SettleIntoBasic(machine);
        int start = session.CurrentFrame;
        for (int frame = 0; frame < 500; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        long shared = session.History.BytesUsed;
        long whole = session.History.UnsharedBytes;
        Require(shared < whole / 4,
            $"Sharing saved almost nothing: {shared} bytes against {whole} stored whole.");

        writer.WriteLine(
            $"  sharing held {session.RewindDepth} states in {shared / 1024}KB instead of "
                + $"{whole / 1024}KB ({whole / (double)Math.Max(1, shared):F1}x), "
                + $"{shared / Math.Max(1, session.RewindDepth)} bytes a state");

        // Shared or not, a state has to restore exactly.
        int end = session.CurrentFrame;
        long atEnd = Fingerprint(machine);
        Require(session.TrySeekToFrame(start + 100), "Seeking into the shared states failed.");
        Require(Fingerprint(machine) != atEnd, "Seeking back did not move the machine.");
        for (int frame = session.CurrentFrame; frame < end; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }

        Require(Fingerprint(machine) == atEnd, "A state built from shared pages did not restore.");

        // Dropping states has to hand their pages back, or the saving leaks away.
        long beforeDrop = session.History.BytesUsed;
        session.History.DropAfter(start + 50);
        Require(session.History.BytesUsed < beforeDrop,
            $"Dropping states freed nothing: still {session.History.BytesUsed} bytes.");
        Require(session.History.UnsharedBytes > 0 || session.RewindDepth == 0,
            "The unshared total went out of step with the states held.");

        session.History.Clear();
        Require(session.History.BytesUsed == 0,
            $"Clearing left {session.History.BytesUsed} bytes behind.");
    }

    private static SpectrumMachine CreateMachine(string machineRom) =>
        SpectrumMachineFactory.Create(new SpectrumMachineOptions
        {
            Model = SpectrumModel.Spectrum48K,
            Roms = RomSet.LoadFromFiles([machineRom]),
            RenderEnabled = false
        });

    /// <summary>Runs the ROM up to its main loop so recordings start from a live machine.</summary>
    private static void SettleIntoBasic(SpectrumMachine machine)
    {
        for (int frame = 0; frame < 120; frame++)
        {
            machine.Emulator.RunFrame(presentFrame: false);
        }
    }

    /// <summary>
    /// Records the requested number of frames with keys going up and down, so the
    /// recorded input actually varies.
    /// </summary>
    /// <remarks>
    /// Stepped instruction by instruction rather than frame by frame: a recorded frame
    /// ends at its interrupt, which is not where the ULA's frame edge falls, so counting
    /// <see cref="SpectrumEmulator.RunFrame"/> calls would stop somewhere else entirely.
    /// Stopping exactly on the last frame's boundary is what lets the machine state be
    /// compared against the replay's.
    /// </remarks>
    private static void RunWithKeys(SpectrumMachine machine, SpectrumReplaySession session, int frames)
    {
        int applied = -1;
        while (session.FrameCount < frames)
        {
            int frame = session.FrameCount;
            if (frame != applied)
            {
                applied = frame;
                machine.Keyboard.SetKeyState(SpectrumKey.A, (frame / 5) % 2 == 0);
                machine.Keyboard.SetKeyState(SpectrumKey.Enter, frame % 17 == 0);
            }

            machine.Emulator.StepInstruction();
        }

        machine.Keyboard.SetKeyState(SpectrumKey.A, false);
        machine.Keyboard.SetKeyState(SpectrumKey.Enter, false);
    }

    /// <summary>A cheap whole-machine fingerprint: registers plus every RAM bank.</summary>
    private static long Fingerprint(SpectrumMachine machine)
    {
        unchecked
        {
            long hash = 1469598103934665603;
            void Mix(long value)
            {
                hash = (hash ^ value) * 1099511628211;
            }

            Mix(machine.Cpu.PC);
            Mix(machine.Cpu.SP);
            Mix(machine.Cpu.AF);
            Mix(machine.Cpu.BC);
            Mix(machine.Cpu.DE);
            Mix(machine.Cpu.HL);
            Mix(machine.Cpu.IX);
            Mix(machine.Cpu.IY);
            Mix(machine.Cpu.R);

            for (int bank = 0; bank < machine.Memory.RamBankCount; bank++)
            {
                byte[] data = machine.Memory.CopyRamBank(bank);
                for (int i = 0; i < data.Length; i++)
                {
                    Mix(data[i]);
                }
            }

            return hash;
        }
    }

    private static void RequireRom(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"48K ROM not found: {path}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temporary file is not worth failing a verification run over.
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
