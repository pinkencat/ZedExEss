using System.Runtime.CompilerServices;
using System;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Audio;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Spectrum.Ports;
using ZedExEss.Spectrum.Video;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Core
{
    /// <summary>
    /// Owns frame execution and keeps CPU time, ULA rendering, tape edges and audio production in lock-step.
    /// </summary>
    /// <remarks>
    /// The CPU's absolute T-state counter is the only clock. CPU bus callbacks synchronise devices
    /// up to that value, while a deadline scheduler splits long advances at frame, interrupt and
    /// tape-edge boundaries. Realtime audio and turbo runners therefore differ only in how much
    /// execution they request, not in the machine timing path they exercise.
    /// </remarks>
    public sealed class SpectrumEmulator : IAudioSource, IZ80TstateConsumer
    {
        private const int FullFrameDirtyLineThresholdDivisor = 2;
        private readonly Z80 _cpu;
        private readonly SpectrumMemory _memory;
        private readonly SpectrumPortBus _ports;
        private readonly SpectrumUlaRenderer _renderer;
        private readonly SpectrumAudioRenderer _audio;
        private readonly object _frameLock = new();
        private readonly int[] _frameCopy;
        private readonly int[] _dirtyLineBuffer;
        private int _dirtyLineCount;
        private bool _fullFrameDirty;
        private bool _frameReady;
        private ulong _lastSyncTstates;
        private bool _frameCompletedPending;

        /// <summary>Set when a replayed frame ends, so the host has something to show.</summary>
        private bool _framePresentationPending;

        /// <summary>
        /// While replaying, a frame is presented when the recording says one ended, not
        /// when the beam reaches the bottom.
        /// </summary>
        /// <remarks>
        /// The two disagree: a recorded frame can be a little shorter or a little longer
        /// than the display frame, so honouring both would present twice for some frames
        /// and not at all for others, and playback speed would follow the difference
        /// rather than the recording.
        /// </remarks>
        public bool PresentOnReplayFramesOnly { get; set; }
        private int _intLineRemaining;
        private int _intDelayRemaining;
        private Func<bool>? _beforeCpuStep;
        private Action? _afterCpuStep;
        private bool _hasBeforeCpuStep;
        // A turbo clone runs its CPU faster while its ULA keeps its own rate, so CPU
        // T-states and ULA T-states stop being the same unit. One is the ratio between
        // them; the carry holds the CPU T-states not yet worth a whole ULA tick.
        private int _cpuTstatesPerUlaTstate = 1;
        private readonly int _baseCpuClockHz;
        private Audio.GeneralSound.GeneralSoundCard? _generalSound;
        private int _ulaCarry;
        private bool _hasAfterCpuStep;
        private ITapePlayback? _tapePlayback;
        private ITapeEdgeSource? _tapeEdges;
        private bool _hasTapePlayback;
        private bool _hasTapeEdges;
        private bool _hasInterruptDeadline;
        private bool _advancingTime;
        private bool _audioDrivenExecution;
        private int _audioSkippedTstates;
        private bool _cpuHeldByPeripheralWait;

        public ITapePlayback? TapePlayback
        {
            get => _tapePlayback;
            set
            {
                // Cache interface availability separately so the hot scheduler path
                // can avoid repeated casts/null checks when no tape is attached.
                _tapePlayback = value;
                _tapeEdges = value as ITapeEdgeSource;
                _hasTapePlayback = value != null;
                _hasTapeEdges = _tapeEdges != null;
            }
        }

        public SpectrumEmulator(
            Z80 cpu,
            SpectrumMemory memory,
            SpectrumPortBus ports,
            SpectrumUlaRenderer renderer,
            SpectrumAudioRenderer audio,
            Func<bool>? beforeCpuStep = null)
        {
            _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _ports = ports ?? throw new ArgumentNullException(nameof(ports));
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _audio = audio ?? throw new ArgumentNullException(nameof(audio));
            _baseCpuClockHz = audio.CpuClockHz;
            _frameCopy = new int[_renderer.FrameBuffer.Length];
            _dirtyLineBuffer = new int[_renderer.FrameHeight];
            _lastSyncTstates = _cpu.Cyc;
            ConfigureBeforeCpuStep(beforeCpuStep);
            _cpu.ConfigureTstateConsumer(this);
        }

        public event Action? FrameCompleted;

        /// <summary>Held while the machine is being run, so its state can be replaced safely.</summary>
        private readonly object _executionLock = new();

        /// <summary>
        /// Runs an action with the machine guaranteed to be standing still.
        /// </summary>
        /// <remarks>
        /// The sound card drives the CPU from its own thread, so anything that replaces
        /// machine state - loading a saved state, stepping back through the rewind
        /// history - must not land in the middle of an instruction. Pausing is not enough
        /// on its own: it stops the next instruction, not the one already executing.
        /// </remarks>
        public void RunExclusive(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);

            lock (_executionLock)
            {
                action();
            }
        }

        /// <summary>
        /// Called at the first instruction boundary after every ULA frame edge, whether
        /// or not the frame is presented.
        /// </summary>
        /// <remarks>
        /// <see cref="FrameCompleted"/> only fires for frames that are drawn, so it skips
        /// frames during fast-forward and headless runs. Anything that has to see every
        /// frame - an input recording, the rewind buffer - belongs here.
        ///
        /// The edge itself lands in the middle of whatever instruction was executing, so
        /// this fires once that instruction has finished: a snapshot taken here restores
        /// to a state the CPU can actually resume from.
        /// </remarks>
        public Action? FrameEdgeObserver { get; set; }

        /// <summary>
        /// Stops the ULA asserting the frame interrupt.
        /// </summary>
        /// <remarks>
        /// Set while replaying a recording, where the interrupt is delivered at the fetch
        /// count the recording gives rather than at our own frame edge. Two sources would
        /// mean two interrupts a frame.
        /// </remarks>
        public bool SuppressFrameInterrupts { get; set; }

        /// <summary>
        /// True once the ULA has crossed a frame edge that has not yet been acted on.
        /// </summary>
        /// <remarks>
        /// Lets a recorder close its frame at the same point in the instruction cycle that
        /// replay opens one - at the CPU boundary, before interrupts are sampled. Closing
        /// at different points would make a recording describe a run its own replay could
        /// not reproduce.
        /// </remarks>
        public bool FrameEdgePending => _frameCompletedPending;

        public bool IsPaused { get; private set; }
        public void ConfigureBeforeCpuStep(Func<bool>? beforeCpuStep)
        {
            _beforeCpuStep = beforeCpuStep;
            _hasBeforeCpuStep = beforeCpuStep != null;
        }
        /// <summary>
        /// CPU T-states per ULA T-state. One on stock hardware; two on a 7 MHz clone.
        /// </summary>
        /// <remarks>
        /// Only meaningful for a machine whose ULA has no contention table and no
        /// floating bus, because both index a fixed CPU-T-state grid that a faster CPU
        /// would walk at the wrong rate. Scorpion and Pentagon qualify; the Sinclair
        /// models do not, which is why this stays at one for them.
        /// </remarks>
        public int CpuTstatesPerUlaTstate
        {
            get => _cpuTstatesPerUlaTstate;
            set
            {
                ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
                if (value == _cpuTstatesPerUlaTstate)
                {
                    return;
                }

                // Publish everything owed at the old rate before the unit changes.
                SyncToCpu();
                _cpuTstatesPerUlaTstate = value;
                _ulaCarry = 0;
                _ports.ConfigureUlaClockRatio(value);
                _audio.SetCpuClockHz(_baseCpuClockHz * value);
            }
        }

        /// <summary>Converts a distance in ULA T-states into one in CPU T-states.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong ToCpuDistance(int ulaTstates)
        {
            if (_cpuTstatesPerUlaTstate == 1)
            {
                return (ulong)ulaTstates;
            }

            // The carry is already banked, so the next ULA tick is that much closer.
            long distance = ((long)ulaTstates * _cpuTstatesPerUlaTstate) - _ulaCarry;
            return distance > 0 ? (ulong)distance : 0UL;
        }

        /// <summary>Converts an elapsed CPU span into whole ULA T-states, banking the remainder.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ConsumeUlaTstates(int cpuTstates)
        {
            if (_cpuTstatesPerUlaTstate == 1)
            {
                return cpuTstates;
            }

            int total = _ulaCarry + cpuTstates;
            _ulaCarry = total % _cpuTstatesPerUlaTstate;
            return total / _cpuTstatesPerUlaTstate;
        }

        /// <summary>Attaches a General Sound card so it runs alongside the machine.</summary>
        /// <remarks>
        /// Advanced in ULA T-states rather than CPU ones: the card has its own crystal
        /// and does not speed up when a turbo clone's CPU does.
        /// </remarks>
        public void AttachGeneralSound(Audio.GeneralSound.GeneralSoundCard? card)
        {
            _generalSound = card;
        }

        public void ConfigureCpuStepHooks(Func<bool>? beforeCpuStep, Action? afterCpuStep)
        {
            ConfigureBeforeCpuStep(beforeCpuStep);
            _afterCpuStep = afterCpuStep;
            _hasAfterCpuStep = afterCpuStep != null;
        }
        public void SetPaused(bool paused)
        {
            IsPaused = paused;
        }

        /// <summary>
        /// Re-bases the scheduler after CPU/device state has been restored without
        /// allowing pending events from the discarded timeline to leak through.
        /// </summary>
        /// <summary>
        /// T-states the INT line still has to run, or is still waiting to assert.
        /// </summary>
        /// <remarks>
        /// A snapshot taken at a frame boundary catches the machine with an interrupt
        /// pending, and a restore that drops it runs one interrupt short - which is the
        /// difference between a save state that resumes and one that merely looks right.
        /// SZX carries this as chHoldIntReqCycles, so it survives a file as well as a
        /// rewind step. The delay before assertion is stored as a negative count.
        /// </remarks>
        public int HeldInterruptTstates
        {
            get
            {
                if (_intDelayRemaining > 0)
                {
                    return -_intDelayRemaining;
                }

                return _intLineRemaining > 0 ? _intLineRemaining : 0;
            }
        }

        /// <summary>Re-arms an interrupt that was pending when a snapshot was taken.</summary>
        public void RestoreHeldInterrupt(int heldTstates)
        {
            if (heldTstates == 0)
            {
                return;
            }

            if (heldTstates < 0)
            {
                _intDelayRemaining = -heldTstates;
                _intLineRemaining = _renderer.InterruptPulseTstates;
                _hasInterruptDeadline = true;
                return;
            }

            _intDelayRemaining = 0;
            _intLineRemaining = heldTstates;
            _hasInterruptDeadline = true;
            _cpu.Z80SetINTLine(true, 0xff);
        }

        public void ResetSynchronizationAfterSnapshotRestore()
        {
            _memory.ClearPendingWrites();
            _ports.ClearPendingWrites();
            _lastSyncTstates = _cpu.Cyc;
            _frameCompletedPending = false;
            _intLineRemaining = 0;
            _intDelayRemaining = 0;
            _hasInterruptDeadline = false;
            _advancingTime = false;
            _audioSkippedTstates = 0;
            _cpu.Z80SetINTLine(false);

            lock (_frameLock)
            {
                _frameReady = false;
                _dirtyLineCount = 0;
                _fullFrameDirty = true;
            }
        }

        public bool VideoEnabled
        {
            get => _renderer.RenderEnabled;
            set => _renderer.RenderEnabled = value;
        }

        /// <summary>
        /// Enables deadline-driven scheduler batching while the unthrottled tape
        /// runner exclusively owns CPU execution.
        /// </summary>
        internal bool FastTapeCpuBatchingEnabled
        {
            set => _cpu.ConfigureInstructionTstateBatching(value);
        }

        /// <summary>
        /// Returns the next point which can change instruction-boundary CPU state.
        /// Tape edges and queued writes are synchronised lazily at their exact
        /// timestamps; frame and INT edges must be visible before the next opcode.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong GetNextCpuBoundarySyncTstate()
        {
            ulong deadline = _lastSyncTstates + ToCpuDistance(_renderer.TstatesUntilFrameEnd);
            if (_hasInterruptDeadline)
            {
                int interruptDelta = _intDelayRemaining > 0 ? _intDelayRemaining : _intLineRemaining;
                if (interruptDelta > 0)
                {
                    ulong interruptAt = _lastSyncTstates + ToCpuDistance(interruptDelta);
                    if (interruptAt < deadline)
                    {
                        deadline = interruptAt;
                    }
                }
            }

            if (_hasTapeEdges)
            {
                ITapeEdgeSource? tapeEdges = _tapeEdges;
                if (tapeEdges?.IsPlaying == true)
                {
                    int tapeDelta = tapeEdges.PeekNextEdgeDelta();
                    if (tapeDelta > 0)
                    {
                        ulong tapeAt = _lastSyncTstates + ToCpuDistance(tapeDelta);
                        if (tapeAt < deadline)
                        {
                            deadline = tapeAt;
                        }
                    }
                }
            }

            return deadline;
        }

        ulong IZ80TstateConsumer.GetNextCpuBoundarySyncTstate() => GetNextCpuBoundarySyncTstate();

        public bool ForceFullFrameCopy { get; set; }

        /// <summary>
        /// Audio-driven execution path. The host asks for samples; the emulator runs just enough CPU
        /// to produce them and still publishes completed frames as they occur.
        /// </summary>
        public int ReadSamples(short[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            if (offset < 0 || count < 0 || offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (IsPaused)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            lock (_executionLock)
            {
                return ReadSamplesCore(buffer, offset, count);
            }
        }

        private int ReadSamplesCore(short[] buffer, int offset, int count)
        {
            int written = _audio.DrainSpill(buffer, offset, count);
            if (written >= count)
            {
                return count;
            }

            int total = written;
            _audioDrivenExecution = true;
            try
            {
                while (total < count)
                {
                    bool frameDone = StepCpu(out int delta);
                    if (delta == 0 && (IsPaused || _cpuHeldByPeripheralWait))
                    {
                        // A debugger pause or an externally released peripheral WAIT
                        // cannot produce audio time. Return silence rather than spin on
                        // the producer thread while the releasing event is pending.
                        Array.Clear(buffer, offset + total, count - total);
                        return count;
                    }

                    total += _audio.RenderSamples(GetAudibleTstates(delta), buffer, offset + total, count - total);

                    if (frameDone)
                    {
                        CopyFrame();
                        FrameCompleted?.Invoke();
                    }
                }
            }
            finally
            {
                _audioDrivenExecution = false;
            }

            return count;
        }
        public void RunFrame()
        {
            RunFrame(presentFrame: true);
        }

        /// <summary>
        /// Runs until the next ULA frame boundary. Turbo/headless paths can skip presentation without
        /// changing CPU, contention, tape or interrupt timing.
        /// </summary>
        public void RunFrame(bool presentFrame)
        {
            lock (_executionLock)
            {
                RunFrameCore(presentFrame);
            }
        }

        private void RunFrameCore(bool presentFrame)
        {
            if (IsPaused)
            {
                return;
            }

            bool frameDone = false;
            while (!frameDone)
            {
                ulong before = _cpu.Cyc;
                frameDone = StepCpu();

                if (IsPaused || (_cpuHeldByPeripheralWait && _cpu.Cyc == before))
                {
                    return;
                }
            }

            if (presentFrame)
            {
                CopyFrame();
                FrameCompleted?.Invoke();
            }
        }

        /// <summary>
        /// Copies the most recently completed frame as a full image. Used by simpler presenters and tests.
        /// </summary>
        public bool TryCopyFrame(int[] destination)
        {
            ArgumentNullException.ThrowIfNull(destination);

            if (destination.Length != _frameCopy.Length)
            {
                throw new ArgumentException("Destination buffer size mismatch.", nameof(destination));
            }

            lock (_frameLock)
            {
                if (!_frameReady)
                {
                    return false;
                }

                Array.Copy(_frameCopy, destination, _frameCopy.Length);
                ClearPendingDirtyLines();
                return true;
            }
        }

        /// <summary>
        /// Copies only the dirty lines when possible; promotes to a full-frame copy when too much changed
        /// or when a previous frame was superseded before the UI consumed it.
        /// </summary>
        public bool TryCopyFrame(int[] destination, int[] dirtyLines, out int dirtyCount)
        {
            ArgumentNullException.ThrowIfNull(destination);

            ArgumentNullException.ThrowIfNull(dirtyLines);

            if (destination.Length != _frameCopy.Length)
            {
                throw new ArgumentException("Destination buffer size mismatch.", nameof(destination));
            }

            lock (_frameLock)
            {
                if (!_frameReady)
                {
                    dirtyCount = 0;
                    return false;
                }

                if (_dirtyLineCount <= 0)
                {
                    _frameReady = false;
                    dirtyCount = 0;
                    return false;
                }

                int height = _renderer.FrameHeight;
                if (_fullFrameDirty || _dirtyLineCount >= height)
                {
                    Array.Copy(_frameCopy, destination, _frameCopy.Length);
                    dirtyCount = height;
                    ClearPendingDirtyLines();
                    return true;
                }

                int width = _renderer.FrameWidth;
                int linesToCopy = Math.Min(_dirtyLineCount, dirtyLines.Length);
                for (int i = 0; i < linesToCopy; i++)
                {
                    int line = _dirtyLineBuffer[i];
                    if (line < 0 || line >= height)
                    {
                        continue;
                    }

                    int offset = line * width;
                    Array.Copy(_frameCopy, offset, destination, offset, width);
                    dirtyLines[i] = line;
                }

                dirtyCount = linesToCopy;
                ClearPendingDirtyLines();
                return true;
            }
        }
        private void CopyFrame()
        {
            lock (_frameLock)
            {
                int[] source = _renderer.FrameBuffer;
                int height = _renderer.FrameHeight;
                bool replacingUnpresentedFrame = _frameReady;

                // If the UI is behind, mark the replacement as full-frame dirty.
                // Otherwise stale dirty-line state can leave old pixels on screen at startup or under load.
                if (ForceFullFrameCopy || replacingUnpresentedFrame)
                {
                    Array.Copy(source, _frameCopy, _frameCopy.Length);
                    _dirtyLineCount = height;
                    _fullFrameDirty = true;
                    _frameReady = true;
                    _renderer.ClearDirtyLines();
                    return;
                }

                int rendererDirtyLineCount = _renderer.DirtyLineCount;
                if (_renderer.FullFrameDirty || rendererDirtyLineCount >= height)
                {
                    Array.Copy(source, _frameCopy, _frameCopy.Length);
                    _dirtyLineCount = height;
                    _fullFrameDirty = true;
                    _frameReady = true;
                    _renderer.ClearDirtyLines();
                    return;
                }

                if (rendererDirtyLineCount >= height / FullFrameDirtyLineThresholdDivisor)
                {
                    // WPF per-line WritePixels overhead dominates once a large fraction of the frame changed.
                    Array.Copy(source, _frameCopy, _frameCopy.Length);
                    _dirtyLineCount = height;
                    _fullFrameDirty = true;
                    _frameReady = true;
                    _renderer.ClearDirtyLines();
                    return;
                }

                int linesToCopy = _renderer.CopyDirtyLines(_dirtyLineBuffer);
                if (linesToCopy <= 0)
                {
                    _frameReady = false;
                    _renderer.ClearDirtyLines();
                    return;
                }

                int width = _renderer.FrameWidth;
                for (int i = 0; i < linesToCopy; i++)
                {
                    int line = _dirtyLineBuffer[i];
                    if (line < 0 || line >= height)
                    {
                        continue;
                    }

                    int offset = line * width;
                    Array.Copy(source, offset, _frameCopy, offset, width);
                }

                _dirtyLineCount = linesToCopy;
                _fullFrameDirty = false;
                _frameReady = true;
                _renderer.ClearDirtyLines();
            }
        }
        private void ClearPendingDirtyLines()
        {
            _dirtyLineCount = 0;
            _fullFrameDirty = false;
            _frameReady = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool StepCpu(out int delta)
        {
            ulong before = _cpu.Cyc;
            ExecuteCpuStep();

            delta = (int)(_cpu.Cyc - before);
            return ConsumeFrameDoneFlag();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool StepCpu()
        {
            ExecuteCpuStep();
            return ConsumeFrameDoneFlag();
        }
        /// <summary>
        /// Runs to the next frame edge and stops there, leaving the pause state as it
        /// was found.
        /// </summary>
        /// <remarks>
        /// This is the frame-at-a-time control a recording is built with, so it has to
        /// work while the machine is paused - which is the whole reason it cannot just
        /// call <see cref="RunFrame(bool)"/>, which returns immediately when paused.
        /// The frame is drawn and <see cref="FrameCompleted"/> raised, so a host that
        /// has stopped its audio driver still sees the picture move.
        ///
        /// A breakpoint inside the frame wins: the machine is left where it broke, still
        /// paused, and false comes back to say the frame did not finish.
        /// </remarks>
        public bool StepFrame(bool presentFrame = true)
        {
            lock (_executionLock)
            {
                return StepFrameCore(presentFrame);
            }
        }

        private bool StepFrameCore(bool presentFrame)
        {
            bool wasPaused = IsPaused;
            IsPaused = false;

            bool frameDone = false;
            while (!frameDone)
            {
                ulong before = _cpu.Cyc;
                ExecuteCpuStep();
                frameDone = ConsumeFrameDoneFlag();

                if (IsPaused)
                {
                    // A debugger break took the machine mid-frame. Leave it there.
                    return frameDone;
                }

                if (_cpuHeldByPeripheralWait && _cpu.Cyc == before)
                {
                    // A peripheral is holding WAIT and no time can pass, so stepping
                    // again would spin instead of reaching the edge.
                    break;
                }
            }

            IsPaused = wasPaused;
            if (frameDone && presentFrame)
            {
                CopyFrame();
                FrameCompleted?.Invoke();
            }

            return frameDone;
        }

        /// <summary>
        /// Draws what the renderer currently holds, without running the machine.
        /// </summary>
        /// <remarks>
        /// Loading a state or stepping backwards moves the machine without producing a
        /// frame edge, so without this nothing would reach the screen until emulation was
        /// resumed - which is exactly when the user needs to see where they have landed.
        /// The copy is forced to be a whole frame because the renderer's dirty lines
        /// describe the frame that was abandoned, not the one being restored.
        /// </remarks>
        public void PresentCurrentFrame()
        {
            bool forced = ForceFullFrameCopy;
            ForceFullFrameCopy = true;
            try
            {
                CopyFrame();
            }
            finally
            {
                ForceFullFrameCopy = forced;
            }

            FrameCompleted?.Invoke();
        }

        public bool StepInstruction()
        {
            bool wasPaused = IsPaused;
            IsPaused = false;
            ExecuteCpuStep();
            if (!IsPaused)
            {
                IsPaused = wasPaused;
            }

            return ConsumeFrameDoneFlag();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExecuteCpuStep()
        {
            _cpuHeldByPeripheralWait = false;
            if (IsPaused)
            {
                return;
            }

            // WAIT is an electrical CPU input, not an I/O result. Test it before
            // debugger/trap hooks and before the next opcode begins. If a local peer
            // has already recorded the release edge, ApplyPeripheralWait advances the
            // shared CPU clock exactly to it; otherwise this execution slot yields.
            if (_ports.ApplyPeripheralWait())
            {
                _cpuHeldByPeripheralWait = true;
                return;
            }

            if (_hasBeforeCpuStep)
            {
                Func<bool>? beforeCpuStep = _beforeCpuStep;
                if (beforeCpuStep != null && beforeCpuStep())
                {
                    // The hook has handled this slot itself. Debugger breaks set IsPaused explicitly
                    // through their caller; non-debug traps such as flashload must be able to skip
                    // the intercepted ROM instruction and keep execution running.
                    return;
                }
            }

            _cpu.Z80Step();
            if (_hasAfterCpuStep)
            {
                _afterCpuStep?.Invoke();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ConsumeFrameDoneFlag()
        {
            bool frameDone = _frameCompletedPending;
            _frameCompletedPending = false;
            if (frameDone)
            {
                // The beam edge is still the beam edge, whatever is driving the machine:
                // a recording being made closes its frames here.
                FrameEdgeObserver?.Invoke();
            }

            if (PresentOnReplayFramesOnly)
            {
                // While replaying, the recording says where a frame ends, and the beam
                // has been put back to the top to suit it.
                frameDone = false;
            }

            if (_framePresentationPending)
            {
                _framePresentationPending = false;
                frameDone = true;
            }

            return frameDone;
        }
        public void SyncToCpu()
        {
            if (_advancingTime)
            {
                return;
            }

            // Called by devices that need all pending ULA/memory/port effects visible
            // before they perform an externally observable action.
            AdvanceToTstate(_cpu.Cyc, renderVideo: true);
        }
        internal void OnTstatesConsumed(int tstates)
        {
            AdvanceToTstate(_cpu.Cyc, renderVideo: true);
        }
        void IZ80TstateConsumer.OnTstatesConsumed(int tstates) => OnTstatesConsumed(tstates);

        internal void OnTapeLoadTstatesSkipped(int tstates)
        {
            if (tstates <= 0)
            {
                return;
            }

            if (_audioDrivenExecution)
            {
                _audioSkippedTstates = Math.Min(int.MaxValue, _audioSkippedTstates + tstates);
            }

            AdvanceToTstate(_cpu.Cyc, renderVideo: false);
        }
        void IZ80TstateConsumer.OnTapeLoadTstatesSkipped(int tstates) => OnTapeLoadTstatesSkipped(tstates);
        private int GetAudibleTstates(int delta)
        {
            if (delta <= 0 || _audioSkippedTstates <= 0)
            {
                return delta;
            }

            int skipped = Math.Min(delta, _audioSkippedTstates);
            _audioSkippedTstates -= skipped;
            return delta - skipped;
        }
        private void AdvanceToTstate(ulong end, bool renderVideo)
        {
            if (end <= _lastSyncTstates || _advancingTime)
            {
                return;
            }

            ulong current = _lastSyncTstates;
            ulong span = end - current;
            if (span <= int.MaxValue)
            {
                int delta = (int)span;
                if (CanFastAdvance(current, end, delta))
                {
                    // Common case: no frame edge, no interrupt edge, no pending write and no tape edge.
                    // Keep this path tiny because it runs after most CPU timing increments.
                    int ulaDelta = ConsumeUlaTstates(delta);
                    if (ulaDelta > 0)
                    {
                        AdvanceSubsystems(ulaDelta, renderVideo);
                    }

                    _lastSyncTstates = end;
                    return;
                }
            }

            _advancingTime = true;
            try
            {
                while (true)
                {
                    // Pending writes must be applied before choosing the next deadline because their
                    // visible state can affect floating-bus reads, border colour and screen memory.
                    FlushPendingWrites(current);
                    ulong nextStop = GetNextScheduledStop(current, end);

                    int run = (int)(nextStop - current);
                    if (run > 0)
                    {
                        int ulaRun = ConsumeUlaTstates(run);
                        if (ulaRun > 0)
                        {
                            AdvanceSubsystems(ulaRun, renderVideo);
                        }
                    }

                    current = nextStop;
                    if (current >= end)
                    {
                        FlushPendingWrites(end);
                        break;
                    }
                }

                _lastSyncTstates = end;
            }
            finally
            {
                _advancingTime = false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool CanFastAdvance(ulong current, ulong end, int delta)
        {
            if (delta <= 0)
            {
                return false;
            }

            if ((ulong)delta >= ToCpuDistance(_renderer.TstatesUntilFrameEnd))
            {
                // Frame completion schedules a new interrupt and frame copy; do not jump across it.
                return false;
            }

            if (_hasInterruptDeadline && _intDelayRemaining > 0)
            {
                if (ToCpuDistance(_intDelayRemaining) <= (ulong)delta)
                {
                    return false;
                }
            }
            else if (_hasInterruptDeadline
                && _intLineRemaining > 0
                && ToCpuDistance(_intLineRemaining) <= (ulong)delta)
            {
                return false;
            }

            if (_memory.TryPeekPendingScreenWrite(out ulong memAt) && memAt <= end)
            {
                // Multicolour effects depend on writes becoming visible at the exact beam-relative time.
                return false;
            }

            if (_ports.TryPeekPendingWrite(out ulong portAt) && portAt <= end)
            {
                return false;
            }

            if (_hasTapeEdges)
            {
                ITapeEdgeSource? tapeEdges = _tapeEdges;
                if (tapeEdges?.IsPlaying == true)
                {
                    int tapeDelta = tapeEdges.PeekNextEdgeDelta();
                    if (tapeDelta > 0 && ToCpuDistance(tapeDelta) <= (ulong)delta)
                    {
                        // Tape input changes are edge events, not a sampled audio stream.
                        return false;
                    }
                }
            }

            return true;
        }
        private ulong GetNextScheduledStop(ulong current, ulong end)
        {
            ulong nextStop = end;

            // The scheduler advances in larger chunks but stops exactly on observable edges.
            nextStop = ApplyDeadline(nextStop, GetNextPendingWriteAt(current, end));
            if (_hasInterruptDeadline)
            {
                nextStop = ApplyDeadline(nextStop, GetNextInterruptEdgeAt(current, end));
            }

            ulong frameAt = current + ToCpuDistance(_renderer.TstatesUntilFrameEnd);
            nextStop = ApplyDeadline(nextStop, frameAt <= end ? frameAt : 0);

            if (_hasTapeEdges)
            {
                ITapeEdgeSource? tapeEdges = _tapeEdges;
                if (tapeEdges?.IsPlaying == true)
                {
                    int tapeDelta = tapeEdges.PeekNextEdgeDelta();
                    if (tapeDelta > 0)
                    {
                        ulong tapeAt = current + ToCpuDistance(tapeDelta);
                        nextStop = ApplyDeadline(nextStop, tapeAt <= end ? tapeAt : 0);
                    }
                }
            }

            return nextStop;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong ApplyDeadline(ulong currentStop, ulong candidate)
        {
            return candidate != 0 && candidate < currentStop ? candidate : currentStop;
        }
        private ulong GetNextInterruptEdgeAt(ulong current, ulong end)
        {
            if (_intDelayRemaining > 0)
            {
                ulong edgeAt = current + ToCpuDistance(_intDelayRemaining);
                return edgeAt <= end ? edgeAt : 0;
            }

            if (_intLineRemaining > 0)
            {
                ulong edgeAt = current + ToCpuDistance(_intLineRemaining);
                return edgeAt <= end ? edgeAt : 0;
            }

            return 0;
        }
        private void AdvanceSubsystems(int tstates, bool renderVideo)
        {
            if (_hasInterruptDeadline)
            {
                AdvanceIntLine(tstates);
            }

            if (_renderer.Advance(tstates, renderVideo && _renderer.RenderEnabled))
            {
                // The ULA frame edge is also the INT scheduling point.
                _frameCompletedPending = true;
                ScheduleInterrupt();
            }

            if (_hasTapePlayback)
            {
                _tapePlayback?.Step(tstates);
            }

            _generalSound?.AdvanceHostTstates(tstates);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FlushPendingWrites(ulong tstates)
        {
            _memory.FlushPendingScreenWrites(tstates);
            _ports.FlushPendingWrites(tstates);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong GetNextPendingWriteAt(ulong current, ulong end)
        {
            ulong next = 0;

            if (_memory.TryPeekPendingScreenWrite(out ulong memAt) && memAt > current && memAt <= end)
            {
                next = memAt;
            }

            if (_ports.TryPeekPendingWrite(out ulong portAt) && portAt > current && portAt <= end)
            {
                if (next == 0 || portAt < next)
                {
                    next = portAt;
                }
            }

            return next;
        }
        /// <summary>
        /// Raises the frame interrupt from somewhere other than the ULA's frame edge.
        /// </summary>
        /// <remarks>
        /// Used while replaying, where the recording says where the frame ended. It goes
        /// through the same pulse the ULA would drive, so an interrupt raised while the
        /// program has interrupts disabled is missed exactly as it was when the recording
        /// was made - which is the difference between replaying a game that turns
        /// interrupts off and diverging from it within seconds.
        /// </remarks>
        /// <summary>
        /// Raises the frame interrupt at once, skipping the ULA's start delay.
        /// </summary>
        /// <remarks>
        /// A recording counts opcodes "till the next interrupt", so the point it names is
        /// the interrupt itself, not the frame edge that precedes it. Adding the ULA's
        /// delay on top would deliver it late by that much, and a program that turns
        /// interrupts off inside its handler can miss one entirely as a result.
        /// </remarks>
        /// <summary>
        /// Raises the frame interrupt at once, without the ULA's start delay.
        /// </summary>
        /// <remarks>
        /// A recording counts opcodes "till the next interrupt", so the count names the
        /// interrupt itself, not the frame edge that precedes it. Adding the delay again
        /// would deliver every interrupt late, and a program that turns interrupts off
        /// inside its handler misses one entirely as a result.
        /// </remarks>
        /// <summary>
        /// Puts the ULA beam back to the top of a frame.
        /// </summary>
        /// <remarks>
        /// On the machine a recording was made on, the frame boundary and the beam were
        /// the same event. Replaying from a fetch count lets the two drift apart, and
        /// because contention depends on where the beam is, the drift feeds back: frames
        /// run long, the beam slips further, and the contention error grows with it.
        /// </remarks>
        public void AlignFrameStart()
        {
            _renderer.RestoreTiming(0, _renderer.FrameCounter + 1);

            // A recorded frame is a frame as far as anything watching is concerned. Most
            // recordings end their frames a few T-states before the beam would have
            // reached the bottom, so waiting for that edge - which this call is about to
            // move - leaves a host running whole frames with nothing to present, and it
            // stays inside RunFrame until some later frame happens to run long.
            _framePresentationPending = true;
            _frameCompletedPending = false;
        }

        public void RaiseFrameInterrupt()
        {
            _intDelayRemaining = 0;
            _intLineRemaining = _renderer.InterruptPulseTstates;
            _hasInterruptDeadline = true;
            _cpu.Z80SetINTLine(true, 0xff);
        }


        private void ScheduleInterrupt()
        {
            if (SuppressFrameInterrupts)
            {
                _intDelayRemaining = 0;
                _intLineRemaining = 0;
                _hasInterruptDeadline = false;
                return;
            }

            AssertFrameInterrupt();
        }

        private void AssertFrameInterrupt()
        {
            // Some models assert INT after a frame-start delay. Keep the delay and pulse width
            // explicit because timing tests measure both separately.
            _intDelayRemaining = _renderer.InterruptDelayTstates + _renderer.InterruptStartOffsetTstates;
            _intLineRemaining = _renderer.InterruptPulseTstates;
            _hasInterruptDeadline = _intDelayRemaining > 0 || _intLineRemaining > 0;
            if (_intDelayRemaining == 0)
            {
                _cpu.Z80SetINTLine(true, 0xff);
            }
            else
            {
                _cpu.Z80SetINTLine(false);
            }
        }
        private void AdvanceIntLine(int tstates)
        {
            int remaining = tstates;
            while (remaining > 0)
            {
                if (_intDelayRemaining > 0)
                {
                    int step = Math.Min(remaining, _intDelayRemaining);
                    _intDelayRemaining -= step;
                    remaining -= step;
                    if (_intDelayRemaining == 0)
                    {
                        _cpu.Z80SetINTLine(true, 0xff);
                    }

                    continue;
                }

                if (_intLineRemaining <= 0)
                {
                    break;
                }

                int pulseStep = Math.Min(remaining, _intLineRemaining);
                _intLineRemaining -= pulseStep;
                remaining -= pulseStep;
                if (_intLineRemaining == 0)
                {
                    _cpu.Z80SetINTLine(false);
                    _hasInterruptDeadline = _intDelayRemaining > 0 || _intLineRemaining > 0;
                }
            }
        }
    }
}
