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
    public sealed class SpectrumEmulator : IAudioSource
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
        private int _intLineRemaining;
        private int _intDelayRemaining;
        private Func<bool>? _beforeCpuStep;
        private Action? _afterCpuStep;
        private bool _hasBeforeCpuStep;
        private bool _hasAfterCpuStep;
        private ITapePlayback? _tapePlayback;
        private ITapeEdgeSource? _tapeEdges;
        private bool _hasTapePlayback;
        private bool _hasTapeEdges;
        private bool _hasInterruptDeadline;
        private bool _advancingTime;
        private bool _audioDrivenExecution;
        private int _audioSkippedTstates;

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
            _frameCopy = new int[_renderer.FrameBuffer.Length];
            _dirtyLineBuffer = new int[_renderer.FrameHeight];
            _lastSyncTstates = _cpu.Cyc;
            ConfigureBeforeCpuStep(beforeCpuStep);
            _cpu.ConfigureTstateConsumer(this);
        }

        public event Action? FrameCompleted;

        public bool IsPaused { get; private set; }
        public void ConfigureBeforeCpuStep(Func<bool>? beforeCpuStep)
        {
            _beforeCpuStep = beforeCpuStep;
            _hasBeforeCpuStep = beforeCpuStep != null;
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
            ulong deadline = _lastSyncTstates + (ulong)_renderer.TstatesUntilFrameEnd;
            if (_hasInterruptDeadline)
            {
                int interruptDelta = _intDelayRemaining > 0 ? _intDelayRemaining : _intLineRemaining;
                if (interruptDelta > 0)
                {
                    ulong interruptAt = _lastSyncTstates + (ulong)interruptDelta;
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
                        ulong tapeAt = _lastSyncTstates + (ulong)tapeDelta;
                        if (tapeAt < deadline)
                        {
                            deadline = tapeAt;
                        }
                    }
                }
            }

            return deadline;
        }

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
                    if (IsPaused && delta == 0)
                    {
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
            if (IsPaused)
            {
                return;
            }

            bool frameDone = false;
            while (!frameDone)
            {
                frameDone = StepCpu();
                if (IsPaused)
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
            if (IsPaused)
            {
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
                    AdvanceSubsystems(delta, renderVideo);
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
                        AdvanceSubsystems(run, renderVideo);
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

            if (delta >= _renderer.TstatesUntilFrameEnd)
            {
                // Frame completion schedules a new interrupt and frame copy; do not jump across it.
                return false;
            }

            if (_hasInterruptDeadline && _intDelayRemaining > 0)
            {
                if (_intDelayRemaining <= delta)
                {
                    return false;
                }
            }
            else if (_hasInterruptDeadline && _intLineRemaining > 0 && _intLineRemaining <= delta)
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
                    if (tapeDelta > 0 && tapeDelta <= delta)
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

            ulong frameAt = current + (ulong)_renderer.TstatesUntilFrameEnd;
            nextStop = ApplyDeadline(nextStop, frameAt <= end ? frameAt : 0);

            if (_hasTapeEdges)
            {
                ITapeEdgeSource? tapeEdges = _tapeEdges;
                if (tapeEdges?.IsPlaying == true)
                {
                    int tapeDelta = tapeEdges.PeekNextEdgeDelta();
                    if (tapeDelta > 0)
                    {
                        ulong tapeAt = current + (ulong)tapeDelta;
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
                ulong edgeAt = current + (ulong)_intDelayRemaining;
                return edgeAt <= end ? edgeAt : 0;
            }

            if (_intLineRemaining > 0)
            {
                ulong edgeAt = current + (ulong)_intLineRemaining;
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
        private void ScheduleInterrupt()
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
