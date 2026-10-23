using System;
using System.Collections.Generic;
using System.IO;
using ZedExEss.FileHandlers;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Spectrum.Replay
{
    public enum SpectrumReplayMode
    {
        Idle,
        Recording,
        Playing
    }

    /// <summary>
    /// Records and replays RZX input recordings, and keeps the rewind history that goes
    /// with making one.
    /// </summary>
    /// <remarks>
    /// A recording stores no keypresses. It stores, per frame, how many opcodes the CPU
    /// fetched and every byte it read from a port; replaying it means running the same
    /// machine from the same snapshot and handing those bytes back in the same order. If
    /// the machine diverges by so much as one input, the run stops and says so, which is
    /// what makes a recording that plays to the end a statement about the emulator's
    /// timing rather than only about the game.
    /// </remarks>
    public sealed class SpectrumReplaySession
    {
        /// <summary>
        /// Frames may differ slightly in fetch count without meaning anything is wrong:
        /// where an interrupt lands inside an instruction moves the boundary by one.
        /// </summary>
        private const int FetchCountTolerance = 4;

        private readonly List<RzxFrame> _frames = [];
        private readonly List<byte> _frameInputs = [];
        private readonly SpectrumStateHistory _history = new();
        private int _currentFrame;

        private SpectrumMachine? _machine;
        private byte[] _startSnapshot = [];
        private ulong _frameStartFetches;
        private int _playbackFrame;
        private int _playbackInput;
        private byte[] _previousInputs = [];
        private bool _startPending;
        private bool _frameEdgeSeen;
        private bool _suspendedEdgeLoading;
        private bool _suspendedSemanticAcceleration;
        private bool _suspendedAutoPlay;
        private bool _acceleratorsSuspended;

        public SpectrumReplayMode Mode { get; private set; }

        /// <summary>Set when replay stopped because the machine diverged.</summary>
        public string? DesyncReason { get; private set; }

        /// <summary>
        /// Raised when playback ends, whether it finished or diverged.
        /// </summary>
        /// <remarks>
        /// Fired from the emulation thread, so a host has to marshal it. Without this a
        /// replay that stops early looks like the machine simply stopping responding, with
        /// the reason sitting unread until something else happens to refresh the UI.
        /// </remarks>
        public event Action<string>? PlaybackEnded;

        /// <summary>
        /// Reports every port read while replaying: frame, program counter, port and the
        /// value handed back. For diagnosing where a replayed run stops matching.
        /// </summary>
        public Action<int, ushort, ushort, byte, long>? InputObserver { get; set; }

        /// <summary>
        /// Reports each replayed frame as it closes: frame, inputs consumed, inputs
        /// recorded, and whether the CPU had interrupts enabled at the boundary.
        /// </summary>
        public Action<int, int, int, bool>? FrameObserver { get; set; }

        /// <summary>
        /// How many times recorded frames have been thrown away and retaken.
        /// </summary>
        /// <remarks>
        /// The conventional measure of how much work a run took, and the reason it is
        /// counted rather than merely allowed: a run with no re-records was played, and
        /// one with thousands was built.
        /// </remarks>
        public int ReRecordCount { get; private set; }

        /// <summary>Frames recorded so far, or in the recording being replayed.</summary>
        public int FrameCount => Mode == SpectrumReplayMode.Playing ? _playbackFrames.Count : _frames.Count;

        /// <summary>Which frame replay has reached.</summary>
        public int PlaybackFrame => _playbackFrame;

        /// <summary>Keeps a rolling history so emulation can be stepped backwards.</summary>
        public bool RewindEnabled { get; set; } = true;

        public int RewindDepth => _history.Count;

        /// <summary>How many frames have passed since the session started following this machine.</summary>
        public int CurrentFrame => _currentFrame;

        /// <summary>Where the run has got to, which is what a stored state has to record.</summary>
        public SpectrumReplayPosition Position => new(_currentFrame, _frames.Count);

        /// <summary>The states kept for going back, and how far back they reach.</summary>
        public SpectrumStateHistory History => _history;

        /// <summary>The earliest frame that can still be returned to.</summary>
        public int OldestFrameAvailable => _history.OldestFrame;

        public double RewindSecondsAvailable => _history.SecondsCovered;

        private List<RzxFrame> _playbackFrames = [];

        /// <summary>
        /// Points the session at a machine, taking over its port reads and frame edges.
        /// </summary>
        public void AttachMachine(SpectrumMachine machine)
        {
            ArgumentNullException.ThrowIfNull(machine);

            if (_machine != null && !ReferenceEquals(_machine, machine))
            {
                _machine.Cpu.PortReadObserver = null;
                _machine.Cpu.BoundaryObserver = null;
                _machine.Emulator.FrameEdgeObserver = null;
                _machine.Emulator.SuppressFrameInterrupts = false;
                _machine.Emulator.PresentOnReplayFramesOnly = false;
            }

            _machine = machine;
            machine.Emulator.FrameEdgeObserver = OnFrameEdge;
            machine.Cpu.BoundaryObserver = OnInstructionBoundary;
            machine.Cpu.PortReadObserver = OnPortRead;

            // A different machine is a different history: neither the rewind states nor a
            // recording made against the old one mean anything now.
            _history.Clear();
            _currentFrame = 0;
            if (Mode != SpectrumReplayMode.Idle)
            {
                Stop();
            }
        }

        public void Detach()
        {
            if (_machine == null)
            {
                return;
            }

            _machine.Cpu.PortReadObserver = null;
            _machine.Cpu.BoundaryObserver = null;
            _machine.Emulator.FrameEdgeObserver = null;
            _machine.Emulator.SuppressFrameInterrupts = false;
                _machine.Emulator.PresentOnReplayFramesOnly = false;
            _machine = null;
        }

        /// <summary>
        /// Arms recording, which begins at the next frame boundary.
        /// </summary>
        /// <remarks>
        /// Not at once: a recording is a list of whole frames, and starting halfway
        /// through one would make the first entry a fragment that replay - which restores
        /// the snapshot and then runs whole frames - could never reproduce.
        /// </remarks>
        public void StartRecording()
        {
            _ = _machine ?? throw new InvalidOperationException("No machine is attached.");

            Stop();
            _frames.Clear();
            _frameInputs.Clear();
            _previousInputs = [];
            ReRecordCount = 0;
            DesyncReason = null;
            _startSnapshot = [];
            _startPending = true;
            _frameEdgeSeen = false;
        }

        /// <summary>True once armed, until the first whole frame begins.</summary>
        public bool IsWaitingToRecord => _startPending;

        /// <summary>
        /// The machine a recording needs, so a host can build it before replaying.
        /// </summary>
        /// <remarks>
        /// Recordings carry whichever snapshot format their emulator wrote - Spectaculator
        /// writes .z80, we write .szx - and the snapshot decides the model, not the
        /// machine that happens to be running.
        /// </remarks>
        public static SpectrumModel DetectModel(RzxRecording recording)
        {
            ArgumentNullException.ThrowIfNull(recording);

            return WithSnapshotFile(recording, (path, extension) => extension switch
            {
                "szx" => SzxSnapshotCodec.Load(path).Model,
                "z80" => Z80Loader.DetectModel(path),
                "sna" => SnapshotLoader.DetectModel(path),
                _ => throw new InvalidOperationException(
                    $"The recording starts from a .{extension} snapshot, which cannot be loaded.")
            });
        }

        /// <summary>Restores the recording's starting state and begins replaying it.</summary>
        public void StartPlayback(RzxRecording recording)
        {
            ArgumentNullException.ThrowIfNull(recording);
            SpectrumMachine machine = _machine
                ?? throw new InvalidOperationException("No machine is attached.");

            Stop();
            SpectrumModel required = DetectModel(recording);
            if (required != machine.Model)
            {
                throw new InvalidOperationException(
                    $"The recording was made on a {required}, not a {machine.Model}.");
            }

            RestoreSnapshot(machine, recording);
            if (machine.Contention != null)
            {
                machine.Contention.PhaseOrigin = ContentionPhaseOrigin(machine);
            }

            // The recording says where in the frame it began. The snapshot inside it
            // carries its own idea of that, and for a .z80 - which has no T-state field
            // at all - that idea is a guess, so the recording's own figure wins.
            int frameTstates = Video.SpectrumUlaTiming.ForModel(machine.Model).TstatesPerFrame;
            machine.Renderer.RestoreTiming(
                (int)(recording.StartTstates % (uint)frameTstates),
                machine.Renderer.FrameCounter);

            _playbackFrames = [.. recording.Frames];
            _playbackFrame = 0;
            _playbackInput = 0;
            DesyncReason = null;
            _frameStartFetches = machine.Cpu.M1Fetches;
            _history.Clear();
            _currentFrame = 0;
            SuspendTapeAccelerators(machine);
            Mode = _playbackFrames.Count > 0 ? SpectrumReplayMode.Playing : SpectrumReplayMode.Idle;

            // From here the recording owns the frame boundary, so the ULA must stop
            // raising its own interrupt.
            machine.Emulator.SuppressFrameInterrupts = Mode == SpectrumReplayMode.Playing;
            machine.Emulator.PresentOnReplayFramesOnly = Mode == SpectrumReplayMode.Playing;
        }

        public void Stop()
        {
            Mode = SpectrumReplayMode.Idle;
            _startPending = false;
            if (_machine != null)
            {
                _machine.Emulator.SuppressFrameInterrupts = false;
                _machine.Emulator.PresentOnReplayFramesOnly = false;
                RestoreTapeAccelerators(_machine);
            }
        }

        /// <summary>
        /// Turns off the tape loader accelerators for the duration of a replay.
        /// </summary>
        /// <remarks>
        /// A replay has to run the machine unaided. The accelerators watch the EAR port
        /// and skip T-states when they think a loader is polling - and the recorded input
        /// this hands back is exactly the kind of repetitive port read that looks like
        /// one. Auto-play goes further and can start the tape, which hands execution to a
        /// second runner thread entirely. Either turns the replayed run into a different
        /// run from the one that was recorded.
        /// </remarks>
        private void SuspendTapeAccelerators(SpectrumMachine machine)
        {
            if (_acceleratorsSuspended)
            {
                return;
            }

            _suspendedEdgeLoading = machine.EarInput.EdgeLoadingEnabled;
            _suspendedSemanticAcceleration = machine.EarInput.SemanticAccelerationEnabled;
            _suspendedAutoPlay = machine.EarInput.AutoPlayEnabled;
            _acceleratorsSuspended = true;

            machine.EarInput.EdgeLoadingEnabled = false;
            machine.EarInput.SemanticAccelerationEnabled = false;
            machine.EarInput.AutoPlayEnabled = false;
        }

        private void RestoreTapeAccelerators(SpectrumMachine machine)
        {
            if (!_acceleratorsSuspended)
            {
                return;
            }

            _acceleratorsSuspended = false;
            machine.EarInput.EdgeLoadingEnabled = _suspendedEdgeLoading;
            machine.EarInput.SemanticAccelerationEnabled = _suspendedSemanticAcceleration;
            machine.EarInput.AutoPlayEnabled = _suspendedAutoPlay;
        }

        public void ClearRecording()
        {
            Stop();
            _frames.Clear();
            _frameInputs.Clear();
            _startSnapshot = [];
            ReRecordCount = 0;
            DesyncReason = null;
        }

        /// <summary>True when there is a recording worth writing out.</summary>
        public bool HasRecording => _frames.Count > 0 && _startSnapshot.Length > 0;

        /// <summary>Packages what has been recorded so far.</summary>
        public RzxRecording BuildRecording()
        {
            if (!HasRecording)
            {
                throw new InvalidOperationException("Nothing has been recorded.");
            }

            return new RzxRecording
            {
                SnapshotExtension = "szx",
                SnapshotData = _startSnapshot,
                Frames = [.. _frames]
            };
        }

        /// <summary>
        /// Steps the machine back through the rewind history, dropping any frames that
        /// were recorded after the point being returned to.
        /// </summary>
        public bool StepBack() => StepBackFrames(FramesPerStepBack);

        /// <summary>How far <see cref="StepBack"/> goes: a tenth of a second.</summary>
        public const int FramesPerStepBack = 5;

        /// <summary>
        /// Goes back the given number of frames, or as near to it as the stored states
        /// allow.
        /// </summary>
        /// <remarks>
        /// Older states are thinned, so a step back into the distant past lands on
        /// whatever state survived at or before the wanted frame rather than exactly on
        /// it. Recent ones are kept whole, so single-frame stepping is exact.
        /// </remarks>
        public bool StepBackFrames(int frames)
        {
            if (frames < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(frames));
            }

            return TrySeekToFrame(_currentFrame - frames);
        }

        /// <summary>
        /// Puts the machine back to exactly the frame asked for.
        /// </summary>
        /// <remarks>
        /// States are kept every few frames rather than at every one, so the nearest
        /// state at or before the target is restored and the remaining frames are run
        /// again. That is what makes stepping back the exact inverse of frame advance
        /// instead of landing a frame or two early, and it is safe to do because nothing
        /// new is being typed while the machine is seeking: the same frames run again
        /// reach the same place. After thinning the run forward is longer, which costs
        /// time and not accuracy.
        /// </remarks>
        public bool TrySeekToFrame(int frame)
        {
            SpectrumMachine? machine = _machine;
            if (machine == null)
            {
                return false;
            }

            // Asking for the frame the machine is already on would restore the state
            // stored at it and go nowhere, so a step back always has to move.
            int target = Math.Min(frame, _currentFrame - 1);
            if (!_history.TryRestoreAtOrBefore(machine, target, out SpectrumReplayPosition restored))
            {
                return false;
            }

            OnMachineMovedBack(machine, restored);

            // Guarded rather than open-ended: a frame that will not complete - a
            // breakpoint, a peripheral holding WAIT - must not spin here.
            int remaining = target - restored.Frame + 2;
            while (_currentFrame < target && remaining-- > 0)
            {
                if (!machine.Emulator.StepFrame(presentFrame: false))
                {
                    break;
                }
            }

            return true;
        }

        /// <summary>
        /// Re-anchors the session after something has put the machine back to an earlier
        /// point: a rewind step, or a saved state being loaded.
        /// </summary>
        /// <remarks>
        /// A recording in progress has to forget the frames after that point, or it stops
        /// describing the machine it is attached to. Dropping frames is what a re-record
        /// is, and the count of them is part of the run rather than a detail of the host,
        /// so it is kept here.
        /// </remarks>
        /// <param name="recordedFrameCount">
        /// How many frames the recording held when the machine was last at this point.
        /// </param>
        public void OnMachineMovedBack(SpectrumMachine machine, SpectrumReplayPosition position)
        {
            ArgumentNullException.ThrowIfNull(machine);

            int recordedFrameCount = position.RecordedFrameCount;
            _currentFrame = position.Frame;

            // States after this point describe a run that is about to be replaced. While
            // a recording is only being played back they stay valid, because playing it
            // again reproduces them exactly.
            if (Mode != SpectrumReplayMode.Playing)
            {
                _history.DropAfter(position.Frame);
            }

            if (Mode == SpectrumReplayMode.Recording && recordedFrameCount <= _frames.Count)
            {
                // The retake replaces everything from here on, so the recording has to
                // forget it too.
                if (recordedFrameCount < _frames.Count)
                {
                    ReRecordCount++;
                }

                _frames.RemoveRange(recordedFrameCount, _frames.Count - recordedFrameCount);
                _frameInputs.Clear();
                _previousInputs = _frames.Count > 0 ? _frames[^1].InputValues : [];
            }

            _frameStartFetches = machine.Cpu.M1Fetches;
        }

        /// <summary>Called at each frame boundary by the emulator.</summary>
        private void OnFrameEdge()
        {
            SpectrumMachine? machine = _machine;
            if (machine == null)
            {
                return;
            }

            _currentFrame++;

            if (_startPending)
            {
                // This is a frame boundary, so the machine state here is exactly what
                // replay will restore to and run forward from.
                _startPending = false;
                _startSnapshot = CaptureSnapshotBytes(machine);
                _frameStartFetches = machine.Cpu.M1Fetches;
                _frameInputs.Clear();
                Mode = SpectrumReplayMode.Recording;
                return;
            }

            if (RewindEnabled && Mode != SpectrumReplayMode.Playing)
            {
                _history.OnFrameEdge(machine, Position);
            }
        }

        /// <summary>
        /// Ends a recorded frame at its interrupt, which is what a frame count measures.
        /// </summary>
        /// <remarks>
        /// Not at the ULA's frame edge, which comes first by the interrupt's start delay:
        /// replay recreates the interrupt, so a recording that counted to the edge would
        /// describe a run its own replay could not reproduce. When the program has
        /// interrupts off the opportunity passes instead, and the frame closes once the
        /// line drops, so a recording still holds one entry per frame.
        /// </remarks>
        private void CloseRecordedFrameIfDue(SpectrumMachine machine)
        {
            if (machine.Emulator.FrameEdgePending)
            {
                _frameEdgeSeen = true;
            }

            if (!_frameEdgeSeen)
            {
                return;
            }

            if (!(machine.Cpu.InterruptPending && machine.Cpu.Iff1)
                && machine.Emulator.HeldInterruptTstates != 0)
            {
                // Still counting down to assert, or asserted and not yet allowed through.
                return;
            }

            _frameEdgeSeen = false;
            CloseRecordedFrame(machine);
        }

        private void CloseRecordedFrame(SpectrumMachine machine)
        {
            ulong fetches = machine.Cpu.M1Fetches - _frameStartFetches;
            _frameStartFetches = machine.Cpu.M1Fetches;

            byte[] inputs = [.. _frameInputs];
            _frameInputs.Clear();

            bool repeats = inputs.Length > 0 && SameInputs(inputs, _previousInputs);
            _frames.Add(new RzxFrame(
                fetches > ushort.MaxValue ? ushort.MaxValue : (ushort)fetches,
                repeats ? _previousInputs : inputs,
                repeats));
            if (!repeats)
            {
                _previousInputs = inputs;
            }
        }

        /// <summary>
        /// Ends a replayed frame once the CPU has fetched as many opcodes as the recording
        /// says, and delivers the interrupt that ended it.
        /// </summary>
        /// <remarks>
        /// The recording's fetch count is the frame boundary, not ours. Following our own
        /// would mean the two drift apart the moment this emulator's frame differs from
        /// the one that made the recording by a single instruction - which it did, about
        /// three seconds into a real recording made elsewhere.
        /// </remarks>
        private void OnInstructionBoundary()
        {
            if (Mode == SpectrumReplayMode.Recording)
            {
                CloseRecordedFrameIfDue(_machine!);
                return;
            }

            if (Mode != SpectrumReplayMode.Playing)
            {
                return;
            }

            SpectrumMachine machine = _machine!;
            RzxFrame frame = _playbackFrames[_playbackFrame];
            if (machine.Cpu.M1Fetches - _frameStartFetches < frame.FetchCount)
            {
                return;
            }

            // Reading fewer inputs than were recorded means the run has diverged just as
            // surely as reading too many: the next frame would start against a stream
            // that no longer lines up.
            if (_playbackInput < frame.InputValues.Length)
            {
                Desync(
                    $"frame {_playbackFrame} read {_playbackInput} of {frame.InputValues.Length} recorded "
                        + $"inputs, ending at PC ${machine.Cpu.PC:X4}");
                return;
            }

            FrameObserver?.Invoke(_playbackFrame, _playbackInput, frame.InputValues.Length, machine.Cpu.Iff1);

            // Advance by the recorded count rather than to the current one: an
            // instruction that straddles the boundary must not shorten the next frame.
            _frameStartFetches += frame.FetchCount;
            _playbackFrame++;
            _playbackInput = 0;
            // The recording's frame boundary was also the machine's frame edge, so put
            // both the beam and the contention phase back to the top of a frame before
            // raising the interrupt that ends it. Left to free-run they drift against the
            // code, and accesses get charged for the wrong part of the display.
            machine.Emulator.AlignFrameStart();
            if (machine.Contention != null)
            {
                machine.Contention.PhaseOrigin = ContentionPhaseOrigin(machine);
            }

            // A recorded frame's interrupt is offered once, at the boundary, and lost if
            // the machine cannot take it there and then. On a running machine the ULA
            // holds INT asserted for a few dozen T-states and the processor picks it up
            // as soon as it is willing, but a recording has no line to hold: the emulator
            // that made it generated one interrupt per frame and asked once. Holding it
            // open here lets code that spends the boundary with interrupts disabled - the
            // ROM's beeper, say - accept an interrupt on the EI that ends its loop, run a
            // keyboard scan the recording never made, and consume input meant for the
            // next frame.
            //
            // Interrupts being disabled is the only thing that loses one. An EI on the
            // boundary itself does not: the processor still delays acceptance by an
            // instruction, which is its own business, and the interrupt is delivered a
            // moment later rather than thrown away. Refusing to raise it in that case
            // strands any frame whose last instruction is an EI, and its handler never
            // runs at all.
            if (machine.Cpu.Iff1)
            {
                machine.Emulator.RaiseFrameInterrupt();
            }

            if (_playbackFrame >= _playbackFrames.Count)
            {
                Mode = SpectrumReplayMode.Idle;
                machine.Emulator.SuppressFrameInterrupts = false;
                machine.Emulator.PresentOnReplayFramesOnly = false;
                PlaybackEnded?.Invoke($"Replay finished after {_playbackFrame} frames");
            }
        }

        /// <summary>Called by the CPU for every value an IN instruction sampled.</summary>
        private byte OnPortRead(ushort port, byte actual)
        {
            switch (Mode)
            {
                case SpectrumReplayMode.Recording:
                    _frameInputs.Add(actual);
                    return actual;

                case SpectrumReplayMode.Playing:
                    RzxFrame frame = _playbackFrames[_playbackFrame];
                    InputObserver?.Invoke(
                        _playbackFrame,
                        _machine!.Cpu.PC,
                        port,
                        actual,
                        (long)(_machine.Cpu.M1Fetches - _frameStartFetches));
                    if (_playbackInput >= frame.InputValues.Length)
                    {
                        Desync(
                            $"frame {_playbackFrame} wanted input {_playbackInput + 1} of "
                                + $"{frame.InputValues.Length} recorded (port ${port:X4}, "
                                + $"{_machine!.Cpu.M1Fetches - _frameStartFetches} of {frame.FetchCount} opcodes in)");
                        return actual;
                    }

                    return frame.InputValues[_playbackInput++];

                default:
                    return actual;
            }
        }

        /// <summary>
        /// Abandons playback, and hands the machine back its own interrupt.
        /// </summary>
        /// <remarks>
        /// Restoring the interrupt is the whole point of doing this in one place: while
        /// replaying, the recording delivers the frame interrupt and the ULA is silenced.
        /// A divergence that left it silenced would leave a machine that never takes an
        /// interrupt again - no keyboard scan, no response to anything, apparently dead
        /// rather than merely stopped.
        /// </remarks>
        private void Desync(string reason)
        {
            DesyncReason = reason;
            Mode = SpectrumReplayMode.Idle;
            if (_machine != null)
            {
                _machine.Emulator.SuppressFrameInterrupts = false;
                _machine.Emulator.PresentOnReplayFramesOnly = false;
            }

            PlaybackEnded?.Invoke($"Replay stopped: {reason}");
        }

        private static bool SameInputs(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Applies whichever snapshot format the recording carries.</summary>
        /// <summary>
        /// Where the contention table's frame begins, given the machine is at the
        /// interrupt that ends a recorded frame.
        /// </summary>
        /// <remarks>
        /// The table is indexed from the top of the machine's frame, and the interrupt
        /// does not happen there: it is asserted a model-specific number of T-states in.
        /// Anchoring the phase to the interrupt itself would charge every access for a
        /// point that far earlier in the display, which averages away over a block copy
        /// that sweeps the whole pattern and does not over a tight loop that keeps
        /// returning to the same few phases.
        /// </remarks>
        private static ulong ContentionPhaseOrigin(SpectrumMachine machine)
        {
            int offset = Core.SpectrumTimingModel.ForModel(machine.Model)
                .InterruptAssertOffsetTstates;
            return machine.Cpu.Cyc - (ulong)offset;
        }

        private static void RestoreSnapshot(SpectrumMachine machine, RzxRecording recording)
        {
            if (string.Equals(recording.SnapshotExtension, "szx", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new MemoryStream(recording.SnapshotData, writable: false);
                SpectrumMachineSnapshotService.Restore(machine, SzxSnapshotCodec.Read(stream));
                return;
            }

            WithSnapshotFile(recording, (path, extension) =>
            {
                if (extension == "z80")
                {
                    machine.Cpu.LoadZ80(machine.Memory, machine.Renderer, path);
                }
                else
                {
                    SnapshotLoader.LoadSna(machine.Cpu, machine.Memory, machine.Renderer, path);
                }

                return true;
            });

            machine.Emulator.ResetSynchronizationAfterSnapshotRestore();
        }

        /// <summary>
        /// Hands the embedded snapshot to the loaders as a file.
        /// </summary>
        /// <remarks>
        /// The .z80 and .sna loaders read from a path rather than a stream, and a
        /// recording is opened once, so a temporary file is cheaper than reworking two
        /// loaders that everything else already depends on.
        /// </remarks>
        private static T WithSnapshotFile<T>(RzxRecording recording, Func<string, string, T> read)
        {
            string extension = recording.SnapshotExtension.ToLowerInvariant();
            string path = Path.Combine(
                Path.GetTempPath(),
                $"zedexess-rzx-{Guid.NewGuid():N}.{(extension.Length == 0 ? "bin" : extension)}");
            try
            {
                File.WriteAllBytes(path, recording.SnapshotData);
                return read(path, extension);
            }
            finally
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // A leftover temporary file is not worth failing playback over.
                }
            }
        }

        private static byte[] CaptureSnapshotBytes(SpectrumMachine machine)
        {
            SpectrumMachineSnapshot snapshot = SpectrumMachineSnapshotService.Capture(machine);
            using var stream = new MemoryStream();
            SzxSnapshotCodec.Write(stream, snapshot);
            return stream.ToArray();
        }
    }
}
