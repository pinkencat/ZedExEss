using System;
using System.Collections.Generic;
using System.Diagnostics;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Tape
{
    /// <summary>
    /// Observes the specific CPU-side shape used by many custom tape loaders:
    /// a stable EAR-port polling loop that recently masks A with bit 5 or bit 6.
    /// This detector is deliberately non-mutating; it proves candidates before a
    /// later skipper is allowed to advance CPU/tape time.
    /// </summary>
    public sealed class TapePollingLoopDetector(Z80 cpu, SpectrumMemory memory) : IZ80TapeAccelerationHook
    {
        private const int ArmCount = 8;
        private const int MaxLoopDeltaTstates = 0x60;
        private const int MaxSkipIterations = 8192;
        private const int MaxCachedProfiles = 64;
        private const int SignatureBytesBeforePc = 16;
        private const int SignatureBytesAfterPc = 8;
        private const int SignatureRevalidateInterval = 64;
        private readonly Z80 _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        private readonly SpectrumMemory _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        private readonly HashSet<ulong> _reportedCandidates = [];
        private readonly Dictionary<ushort, LoopProfile> _profiles = [];
        private ITapeEdgeSource? _edgeSource;
        private bool _enabled;
        private bool _andTapeMaskSeen;
        private byte _lastMask;
        private bool _lastValid;
        private ushort _lastPc;
        private ulong _lastTstates;
        private byte _lastA;
        private byte _lastB;
        private byte _lastC;
        private byte _lastD;
        private byte _lastE;
        private byte _lastH;
        private byte _lastL;
        private byte _lastR;
        private int _stableCount;
        private bool _autoPlayRequestedForCurrentLoop;
        private int _trustedSignaturePc = -1;
        private int _trustedSignatureHits;

        public bool TraceCandidates { get; set; }
        public bool SkippingEnabled { get; set; } = true;
        public bool AutoPlayDetectionEnabled { get; set; }
        public Action? LoaderPollingDetected { get; set; }
        /// <summary>
        /// Gives a more specific accelerator first refusal on an EAR read. Returning
        /// true means that accelerator owns this read, so the generic polling-loop
        /// fallback must not also mutate CPU or tape state. Note a claimed read is
        /// not always compressed: the semantic accelerator claims seed reads it then
        /// lets run at real speed to prime its pulse classifier, and those must not
        /// be time-skipped either — acting on them here would double-advance state.
        /// </summary>
        public Func<ushort, byte, bool>? PrimaryReadClaim { get; set; }
        public long SkipEvents { get; private set; }
        public long SkippedLoopIterations { get; private set; }
        public long SkippedTstates { get; private set; }
        public long ProfileHits { get; private set; }
        public ulong LastSkipCpuTstate { get; private set; }
        public void Configure(ITapeEdgeSource? edgeSource)
        {
            _edgeSource = edgeSource;
            ResetState();
            _reportedCandidates.Clear();
            _profiles.Clear();
            SkipEvents = 0;
            SkippedLoopIterations = 0;
            SkippedTstates = 0;
            ProfileHits = 0;
            LastSkipCpuTstate = 0;
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
                ResetState();
            }
        }
        public void NotifyAndOperand(byte operandValue)
        {
            if (!_enabled)
            {
                return;
            }

            if (operandValue == 0x20 || operandValue == 0x40)
            {
                _andTapeMaskSeen = true;
                _lastMask = operandValue;
            }
        }
        public void BeforeInAImmediate(ushort opcodePc, byte portLow)
        {
            ObservePortRead(opcodePc, portLow, allowSemanticClaim: true);
        }
        public void BeforeInRegC(ushort opcodePc, byte portLow)
        {
            // ED-prefixed reads can never carry the DB FE signature; offering
            // them to the semantic accelerator would only churn its mode cache.
            ObservePortRead(opcodePc, portLow, allowSemanticClaim: false);
        }
        private void ObservePortRead(ushort opcodePc, byte portLow, bool allowSemanticClaim)
        {
            bool sawTapeMask = _andTapeMaskSeen;
            byte mask = _lastMask;
            _andTapeMaskSeen = false;

            if (!_enabled)
            {
                return;
            }

            ITapeEdgeSource? edgeSource = _edgeSource;
            if (edgeSource == null)
            {
                ResetState();
                return;
            }

            bool tapePlaying = edgeSource.IsPlaying;
            if (!tapePlaying && !AutoPlayDetectionEnabled)
            {
                ResetState();
                return;
            }

            if (tapePlaying
                && allowSemanticClaim
                && (portLow & 0x01) == 0
                && PrimaryReadClaim?.Invoke(opcodePc, portLow) == true)
            {
                // The exact-signature accelerator will complete its prepared action
                // when the real ULA read samples EAR. Discard only transient fallback
                // observations; trusted profiles remain available for later loaders.
                ResetObservedLoop();
                return;
            }

            if ((portLow & 0x01) != 0 || !sawTapeMask)
            {
                ResetState();
                return;
            }

            if (tapePlaying && edgeSource.EdgeSeen)
            {
                ResetState();
                return;
            }

            if (tapePlaying && SkippingEnabled && TryGetTrustedProfile(opcodePc, portLow, mask, out LoopProfile profile))
            {
                int skipped = TrySkipPollingLoop(edgeSource, profile.CounterRegister, profile.Step, profile.LoopDelta, profile.RDelta);
                if (skipped > 0)
                {
                    ProfileHits++;
                    Capture(opcodePc, _cpu.Cyc);
                    return;
                }
            }

            bool matched = false;
            ulong tstates = _cpu.Cyc;

            if (_lastValid && _lastPc == opcodePc && _lastA == _cpu.A)
            {
                ulong delta = tstates - _lastTstates;
                if (delta > 0
                    && delta <= MaxLoopDeltaTstates
                    && TryFindLoopCounter(out Register8 changedRegister, out int step))
                {
                    matched = true;
                    _stableCount++;
                    int loopDelta = (int)delta;
                    int rDelta = (_cpu.R - _lastR) & 0x7F;

                    if (!tapePlaying && AutoPlayDetectionEnabled && _stableCount >= ArmCount)
                    {
                        RequestAutoPlayForCurrentLoop();
                    }
                    else if (tapePlaying && _stableCount >= ArmCount && SkippingEnabled)
                    {
                        CacheTrustedProfile(opcodePc, portLow, mask, changedRegister, step, loopDelta, rDelta);
                        _ = TrySkipPollingLoop(edgeSource, changedRegister, step, loopDelta, rDelta);
                    }

                    if (_stableCount == ArmCount)
                    {
                        if (TraceCandidates)
                        {
                            ReportCandidate(edgeSource, opcodePc, portLow, mask, changedRegister, step, loopDelta);
                        }
                    }
                }
            }

            if (!matched)
            {
                _stableCount = 0;
                _autoPlayRequestedForCurrentLoop = false;
            }

            Capture(opcodePc, _cpu.Cyc);
        }
        private void RequestAutoPlayForCurrentLoop()
        {
            if (_autoPlayRequestedForCurrentLoop)
            {
                return;
            }

            _autoPlayRequestedForCurrentLoop = true;
            LoaderPollingDetected?.Invoke();
        }
        private bool TryGetTrustedProfile(ushort opcodePc, byte portLow, byte mask, out LoopProfile profile)
        {
            if (_profiles.TryGetValue(opcodePc, out profile)
                && profile.PortLow == portLow
                && profile.Mask == mask)
            {
                // Hashing 26 bytes on every read of a hot loop costs more than the
                // skip it authorises. While consecutive reads stay on the same
                // instruction the code around it cannot have been swapped without
                // the loop itself doing the writing, so re-validate the signature
                // only when the streak breaks or every SignatureRevalidateInterval
                // reads, which bounds how long a self-modifying loader could be
                // skipped with a stale profile.
                if (_trustedSignaturePc == opcodePc
                    && _lastValid
                    && _lastPc == opcodePc
                    && _trustedSignatureHits < SignatureRevalidateInterval)
                {
                    _trustedSignatureHits++;
                    return true;
                }

                if (profile.Signature == BuildLoopSignature(opcodePc))
                {
                    _trustedSignaturePc = opcodePc;
                    _trustedSignatureHits = 0;
                    return true;
                }
            }

            _trustedSignaturePc = -1;
            profile = default;
            return false;
        }
        private void CacheTrustedProfile(
            ushort opcodePc,
            byte portLow,
            byte mask,
            Register8 changedRegister,
            int step,
            int loopDelta,
            int rDelta)
        {
            if (_profiles.Count >= MaxCachedProfiles && !_profiles.ContainsKey(opcodePc))
            {
                _profiles.Clear();
            }

            _profiles[opcodePc] = new LoopProfile(
                portLow,
                mask,
                changedRegister,
                step,
                loopDelta,
                rDelta,
                BuildLoopSignature(opcodePc));
            _trustedSignaturePc = -1;
        }
        private uint BuildLoopSignature(ushort opcodePc)
        {
            uint hash = 2166136261u;
            int start = opcodePc - SignatureBytesBeforePc;
            int count = SignatureBytesBeforePc + SignatureBytesAfterPc + 2;
            for (int i = 0; i < count; i++)
            {
                byte value = _memory.ReadDirect(unchecked((ushort)(start + i)));
                hash ^= value;
                hash *= 16777619u;
            }

            return hash;
        }
        private void ReportCandidate(
            ITapeEdgeSource edgeSource,
            ushort opcodePc,
            byte portLow,
            byte mask,
            Register8 changedRegister,
            int step,
            int loopDelta)
        {
            int edgeDelta = edgeSource.PeekNextEdgeDelta();
            int predictedLoops = edgeDelta > 0 ? Math.Max(1, edgeDelta / loopDelta) : 0;
            ulong key = ((ulong)opcodePc << 48)
                | ((ulong)edgeSource.CurrentBlockIndex & 0xFFFFUL) << 32
                | ((ulong)edgeSource.CurrentPulseIndex & 0xFFFFUL) << 16
                | ((ulong)mask << 8)
                | (byte)changedRegister;

            if (!_reportedCandidates.Add(key))
            {
                return;
            }

            Debug.WriteLine(
                $"tape loop candidate pc={opcodePc:X4} port={_cpu.A:X2}{portLow:X2} mask={mask:X2} " +
                $"loop={loopDelta}T counter={changedRegister}{(step > 0 ? "+1" : "-1")} " +
                $"edge={edgeDelta}T predictedLoops={predictedLoops} block={edgeSource.CurrentBlockIndex} pulse={edgeSource.CurrentPulseIndex}");
        }

        /// <summary>
        /// Finds the loop's timeout counter. A step of 0 marks a counter-less loop:
        /// every register identical between reads. Those are pure edge waits (pilot
        /// hunts, pause waits) and are only trusted with interrupts disabled, since
        /// with no counter and no ISR nothing but the tape level can end the loop.
        /// </summary>
        private bool TryFindLoopCounter(out Register8 changedRegister, out int step)
        {
            changedRegister = default;
            step = 0;
            int changes = 0;

            return ConsiderRegister(_lastB, _cpu.B, Register8.B, ref changes, ref changedRegister, ref step)
                && ConsiderRegister(_lastC, _cpu.C, Register8.C, ref changes, ref changedRegister, ref step)
                && ConsiderRegister(_lastD, _cpu.D, Register8.D, ref changes, ref changedRegister, ref step)
                && ConsiderRegister(_lastE, _cpu.E, Register8.E, ref changes, ref changedRegister, ref step)
                && ConsiderRegister(_lastH, _cpu.H, Register8.H, ref changes, ref changedRegister, ref step)
                && ConsiderRegister(_lastL, _cpu.L, Register8.L, ref changes, ref changedRegister, ref step)
                && (changes == 1 || (changes == 0 && !_cpu.InterruptsEnabled));
        }
        private int TrySkipPollingLoop(
            ITapeEdgeSource edgeSource,
            Register8 counterRegister,
            int step,
            int loopDelta,
            int rDelta)
        {
            if (loopDelta <= 0 || loopDelta > MaxLoopDeltaTstates)// || _cpu.InterruptsEnabled)
            {
                return 0;
            }

            // A counter-less profile was armed with interrupts disabled; if the
            // loader has since enabled them an ISR could end the loop, so the
            // profile can no longer prove the loop only exits on a tape edge.
            if (step == 0 && _cpu.InterruptsEnabled)
            {
                return 0;
            }

            int skipped = 0;
            while (skipped < MaxSkipIterations)
            {
                if (!edgeSource.IsPlaying || edgeSource.EdgeSeen)
                {
                    break;
                }

                int loopsBeforeCounterWrap = int.MaxValue;
                byte counter = 0;
                if (step != 0)
                {
                    counter = GetRegister(counterRegister);
                    loopsBeforeCounterWrap = GetLoopCountBeforeCounterWrap(counter, step);
                    if (loopsBeforeCounterWrap <= 0)
                    {
                        break;
                    }
                }

                int edgeDelta = edgeSource.PeekNextEdgeDelta();
                if (edgeDelta <= 0)
                {
                    break;
                }

                int loopsToEdge = Math.Max(1, (edgeDelta + loopDelta - 1) / loopDelta);
                int loopBudget = MaxSkipIterations - skipped;
                int loopsToSkip = Math.Min(loopBudget, Math.Min(loopsBeforeCounterWrap, loopsToEdge));
                if (loopsToSkip <= 0)
                {
                    break;
                }

                _cpu.AdvanceTapeLoadSkipTime(loopsToSkip * loopDelta);
                if (step != 0)
                {
                    SetRegister(counterRegister, unchecked((byte)(counter + (step * loopsToSkip))));
                }

                AdvanceRefreshRegister(rDelta, loopsToSkip);
                skipped += loopsToSkip;

                if (edgeSource.EdgeSeen)
                {
                    break;
                }
            }

            if (skipped > 0)
            {
                SkipEvents++;
                SkippedLoopIterations += skipped;
                SkippedTstates += (long)skipped * loopDelta;
                LastSkipCpuTstate = _cpu.Cyc;
            }

            return skipped;
        }
        private static int GetLoopCountBeforeCounterWrap(byte counter, int step)
        {
            return step > 0 ? 0xFF - counter : counter - 0x01;
        }
        private byte GetRegister(Register8 register)
        {
            return register switch
            {
                Register8.B => _cpu.B,
                Register8.C => _cpu.C,
                Register8.D => _cpu.D,
                Register8.E => _cpu.E,
                Register8.H => _cpu.H,
                _ => _cpu.L
            };
        }
        private void SetRegister(Register8 register, byte value)
        {
            switch (register)
            {
                case Register8.B:
                    _cpu.B = value;
                    break;
                case Register8.C:
                    _cpu.C = value;
                    break;
                case Register8.D:
                    _cpu.D = value;
                    break;
                case Register8.E:
                    _cpu.E = value;
                    break;
                case Register8.H:
                    _cpu.H = value;
                    break;
                default:
                    _cpu.L = value;
                    break;
            }
        }
        private void AdvanceRefreshRegister(int delta, int loopCount)
        {
            if (delta <= 0 || loopCount <= 0)
            {
                return;
            }

            int advance = (delta * loopCount) & 0x7F;
            _cpu.R = (byte)((_cpu.R & 0x80) | ((_cpu.R + advance) & 0x7F));
        }
        private static bool ConsiderRegister(
            byte previous,
            byte current,
            Register8 register,
            ref int changes,
            ref Register8 changedRegister,
            ref int step)
        {
            if (previous == current)
            {
                return true;
            }

            int diff = (current - previous) & 0xFF;
            if (diff == 1)
            {
                changes++;
                changedRegister = register;
                step = 1;
                return changes <= 1;
            }

            if (diff == 0xFF)
            {
                changes++;
                changedRegister = register;
                step = -1;
                return changes <= 1;
            }

            return false;
        }
        private void Capture(ushort opcodePc, ulong tstates)
        {
            _lastValid = true;
            _lastPc = opcodePc;
            _lastTstates = tstates;
            _lastA = _cpu.A;
            _lastB = _cpu.B;
            _lastC = _cpu.C;
            _lastD = _cpu.D;
            _lastE = _cpu.E;
            _lastH = _cpu.H;
            _lastL = _cpu.L;
            _lastR = _cpu.R;
        }
        private void ResetState()
        {
            _andTapeMaskSeen = false;
            _lastMask = 0;
            _trustedSignaturePc = -1;
            ResetObservedLoop();
        }
        private void ResetObservedLoop()
        {
            _lastValid = false;
            _stableCount = 0;
            _autoPlayRequestedForCurrentLoop = false;
        }
        private enum Register8
        {
            B,
            C,
            D,
            E,
            H,
            L
        }
        private readonly struct LoopProfile(
            byte portLow,
            byte mask,
            Register8 counterRegister,
            int step,
            int loopDelta,
            int rDelta,
            uint signature)
        {
            public byte PortLow { get; } = portLow;
            public byte Mask { get; } = mask;
            public Register8 CounterRegister { get; } = counterRegister;
            public int Step { get; } = step;
            public int LoopDelta { get; } = loopDelta;
            public int RDelta { get; } = rDelta;
            public uint Signature { get; } = signature;
        }
    }
}
