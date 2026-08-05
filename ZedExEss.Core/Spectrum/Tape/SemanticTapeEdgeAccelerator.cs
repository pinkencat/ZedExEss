using System;
using System.Collections.Generic;
using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Memory;
using ZedExEss.Z80CPU;

namespace ZedExEss.Spectrum.Tape
{
    /// <summary>
    /// Performs semantic compression of recognised tape edge-finding
    /// subroutines. It does not advance emulated time: instead it reproduces the
    /// register/flag/RET result of a recognised routine and asks the tape source
    /// for the next edge immediately. Unknown code is never changed and is left to
    /// <see cref="TapePollingLoopDetector"/>.
    /// </summary>
    public sealed class SemanticTapeEdgeAccelerator(Z80 cpu, SpectrumMemory memory)
    {
        private const int MaxCachedSignatures = 256;
        private const int SignatureByteCount = 32;
        private const int ActiveSignatureRevalidationInterval = 64;
        private readonly Z80 _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        private readonly SpectrumMemory _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        private readonly Dictionary<ushort, DetectionCacheEntry> _signatureCache = [];
        private ITapeEdgeSource? _edgeSource;
        private AccelerationMode _mode;
        private ushort _modePc;
        private ushort _modeScanStart;
        private uint _modeSignature;
        private int _modeReadsUntilRevalidation;
        private int _lastPulseIndex = -1;
        private TapeAccelerationPulseFlags _lengthFlags1;
        private TapeAccelerationPulseFlags _lengthFlags2;
        private TapeSemanticReadState _preparedState;
        private bool _readPrepared;

        public long MatchedReads { get; private set; }
        public long AccelerationCount { get; private set; }
        public ulong LastAccelerationCpuTstate { get; private set; }

        /// <summary>
        /// Diagnostic sink for the first <see cref="TraceLimit"/> prepared reads.
        /// Wired by the headless tape-game verification runner; null in normal use.
        /// </summary>
        public Action<string>? TraceSink { get; set; }
        public int TraceLimit { get; set; } = 200;
        public int TraceFromPulse { get; set; }
        private int _traceCount;
        public void Configure(ITapeEdgeSource? edgeSource, Func<bool>? earHighProvider)
        {
            _edgeSource = edgeSource;
            _ = earHighProvider; // The atomic tape snapshot now supplies the EAR level.
            ResetRuntimeState();
            _signatureCache.Clear();
            MatchedReads = 0;
            AccelerationCount = 0;
            LastAccelerationCpuTstate = 0;
        }

        /// <summary>
        /// Called after the immediate port byte of IN A,(n) has been fetched, but
        /// before the real port read. A true result reserves that read for this
        /// accelerator; <see cref="TryAcceleratePreparedRead"/> then acts at the
        /// ULA/EAR sampling point.
        /// </summary>
        public bool TryClaimRead(ushort opcodePc, byte portLow)
        {
            _readPrepared = false;

            ITapeEdgeSource? edgeSource = _edgeSource;
            if (edgeSource == null || !edgeSource.IsPlaying || (portLow & 0x01) != 0)
            {
                ResetRuntimeState();
                return false;
            }

            if (!edgeSource.TryGetSemanticReadState(out TapeSemanticReadState state)
                || state.Flags == TapeAccelerationPulseFlags.None)
            {
                // Exact routine recognition alone is not enough to own the read. At
                // a data-to-tail/pause/pilot boundary the previous data pulse may
                // still be classified, but compressing again would manufacture an
                // edge for a loader which should instead time out or acquire a new
                // leader. Leave all non-data phases to the polling fallback.
                ClearPulseClassification();
                return false;
            }

            UpdateLengthFromState(edgeSource, state);

            ushort currentPc = _cpu.PC;
            if (_mode != AccelerationMode.None && currentPc != _modePc)
            {
                ClearActiveMode();
            }

            if (_mode == AccelerationMode.None)
            {
                ActivateCachedAcceleration(currentPc);
            }
            else if (--_modeReadsUntilRevalidation <= 0)
            {
                // We keep a recognised PC armed indefinitely. We additionally
                // revalidate its code at a low cadence so a self-modifying loader
                // cannot leave a stale semantic shortcut active, without hashing
                // the routine on every edge.
                uint signature = MemorySignature(_modeScanStart);
                if (signature != _modeSignature)
                {
                    ClearActiveMode();
                    ActivateCachedAcceleration(currentPc);
                }
                else
                {
                    _modeReadsUntilRevalidation = ActiveSignatureRevalidationInterval;
                }
            }

            if (_mode == AccelerationMode.None)
            {
                return false;
            }

            // BeforeInAImmediate can only be reached from DB nn. Once the exact
            // routine has been recognised, checking the PC relationship is enough;
            // periodic signature validation guards self-modifying code without an
            // additional emulated-memory read on every tape edge.
            if (unchecked((ushort)(opcodePc + 2)) != currentPc)
            {
                ClearActiveMode();
                return false;
            }

            _preparedState = state;
            _readPrepared = true;
            MatchedReads++;
            return true;
        }

        /// <summary>
        /// Completes a previously claimed read at the point where the ULA samples
        /// EAR. The first read of a classified data interval seeds the two-stage
        /// pulse classifier; subsequent recognised reads are compressed edge-by-edge.
        /// </summary>
        public bool TryAcceleratePreparedRead()
        {
            if (!_readPrepared)
            {
                return false;
            }

            _readPrepared = false;
            ITapeEdgeSource? edgeSource = _edgeSource;
            if (edgeSource == null || !edgeSource.IsPlaying || _mode == AccelerationMode.None)
            {
                ResetRuntimeState();
                return false;
            }

            TapeSemanticReadState state = _preparedState;
            if (edgeSource.CurrentPulseIndex != state.PulseIndex)
            {
                // An ordinary edge occurred in the few tstates between decoding the
                // IN and sampling EAR.
                if (!edgeSource.TryGetSemanticReadState(out state))
                {
                    ClearPulseClassification();
                    return false;
                }

                UpdateLengthFromState(edgeSource, state);
            }

            if (state.Flags == TapeAccelerationPulseFlags.None)
            {
                // The mid-instruction edge moved playback into a tail/pause/pilot
                // interval; compressing another edge would manufacture one for a
                // loader which should time out or hunt a new leader instead.
                ClearPulseClassification();
                return false;
            }

            bool accelerated = false;
            if (_lengthFlags1 != TapeAccelerationPulseFlags.None
                && state.NextEdgeDelta > 0
                && edgeSource.TryAdvanceSemanticEdge(state, out TapeSemanticEdgeResult edgeResult))
            {
                // Communicates the measured pulse length through B. Increasing
                // and decreasing counters use opposite terminal values.
                bool pulseIsLong = (_lengthFlags1 & TapeAccelerationPulseFlags.LengthLong) != 0;
                bool setBHigh = pulseIsLong ^ (_mode == AccelerationMode.Decreasing);
                byte bBefore = _cpu.B;
                byte cBefore = _cpu.C;
                _cpu.B = setBHigh ? (byte)0xFE : (byte)0x00;

                // Only bit 5 is synthesized here. Bit 6 must be
                // preserved because the real IN which follows this hook supplies
                // the new EAR level to Search/Digital Integration style routines.
                bool earHigh = edgeResult.EarHighBefore;
                _cpu.C = (byte)((_cpu.C & ~0x20) | (earHigh ? 0x00 : 0x20));
                _cpu.SetFlags((byte)(_cpu.GetFlags() | 0x01));

                ReturnFromAcceleratedRoutine();
                if (ShouldTrace())
                {
                    Trace(
                        $"accel cyc={_cpu.Cyc} pc={_modePc:X4} ret={_cpu.PC:X4} " +
                        $"b={bBefore:X2}>{_cpu.B:X2} c={cBefore:X2}>{_cpu.C:X2} ear={(earHigh ? 1 : 0)} " +
                        $"long1={(pulseIsLong ? 1 : 0)} pulse={edgeResult.SourcePulseIndex}>{edgeResult.DestinationPulseIndex}");
                }

                // The edge source performs the transition and classification as one
                // operation, so no follow-up interface queries are needed here.
                _lengthFlags2 = edgeResult.DestinationFlags;
                _lastPulseIndex = edgeResult.DestinationPulseIndex;
                accelerated = true;
            }
            else
            {
                if (ShouldTrace())
                {
                    Trace(
                        $"seed  cyc={_cpu.Cyc} pc={_modePc:X4} known1={(_lengthFlags1 != TapeAccelerationPulseFlags.None ? 1 : 0)} " +
                        $"known2={(_lengthFlags2 != TapeAccelerationPulseFlags.None ? 1 : 0)} " +
                        $"long2={((_lengthFlags2 & TapeAccelerationPulseFlags.LengthLong) != 0 ? 1 : 0)} " +
                        $"pulse={state.PulseIndex}");
                }
            }

            // Deliberately delays edge classification by one read. This avoids
            // skipping the edge immediately after one reached by normal execution.
            _lengthFlags1 = _lengthFlags2;

            if (accelerated)
            {
                AccelerationCount++;
                LastAccelerationCpuTstate = _cpu.Cyc;
            }

            return accelerated;
        }
        private bool ShouldTrace()
        {
            return TraceSink != null
                && _traceCount < TraceLimit
                && (_edgeSource?.CurrentPulseIndex ?? 0) >= TraceFromPulse;
        }

        private void Trace(string line)
        {
            _traceCount++;
            TraceSink!(line);
        }
        private void ReturnFromAcceleratedRoutine()
        {
            ushort sp = _cpu.SP;
            byte lo = _memory.ReadDirect(sp);
            byte hi = _memory.ReadDirect(unchecked((ushort)(sp + 1)));
            _cpu.SP = unchecked((ushort)(sp + 2));
            _cpu.PC = (ushort)(lo | (hi << 8));
        }
        private void UpdateLengthFromState(ITapeEdgeSource edgeSource, TapeSemanticReadState state)
        {
            if (state.PulseIndex == _lastPulseIndex)
            {
                return;
            }

            bool fromSemanticAcceleration = false;
            if (edgeSource.TryGetLastEdgeInfo(out _, out _, out _, out bool lastEdgeSemanticallyAccelerated))
            {
                fromSemanticAcceleration = lastEdgeSemanticallyAccelerated;
            }

            if (!fromSemanticAcceleration)
            {
                // A normally reached edge invalidates the first pipeline stage. This
                // is a guard against immediately skipping a second edge.
                _lengthFlags1 = TapeAccelerationPulseFlags.None;
            }

            // libspectrum's length flags describe the interval now scheduled, not
            // the interval which ended at the previous edge.
            _lengthFlags2 = state.Flags;
            _lastPulseIndex = state.PulseIndex;
        }
        private void ActivateCachedAcceleration(ushort currentPc)
        {
            ushort scanStart = unchecked((ushort)(currentPc - 6));
            uint signature = MemorySignature(scanStart);
            _mode = GetCachedAcceleration(currentPc, scanStart, signature);
            if (_mode == AccelerationMode.None)
            {
                return;
            }

            _modePc = currentPc;
            _modeScanStart = scanStart;
            _modeSignature = signature;
            _modeReadsUntilRevalidation = ActiveSignatureRevalidationInterval;
        }

        private AccelerationMode GetCachedAcceleration(ushort currentPc, ushort scanStart, uint signature)
        {
            if (_signatureCache.TryGetValue(currentPc, out DetectionCacheEntry cached))
            {
                if (cached.ScanStart == scanStart && cached.Signature == signature)
                {
                    return cached.Mode;
                }

                _signatureCache.Remove(currentPc);
            }

            AccelerationMode mode = DetectAcceleration(scanStart, currentPc);
            // Cache misses as well as hits. Unknown custom loaders may execute this
            // read millions of times before the polling fallback proves the loop;
            // rescanning 32+ bytes on every IN would erase much of the speed-up.
            if (_signatureCache.Count >= MaxCachedSignatures)
            {
                _signatureCache.Clear();
            }

            _signatureCache[currentPc] = new DetectionCacheEntry(scanStart, signature, mode);

            return mode;
        }

        /// <summary>
        /// Exact state machine used by the loader detector. It recognises ROM
        /// variants plus Search, Speedlock, Digital Integration, Alkatraz,
        /// Bleepload, Microsphere, Paul Owens and Dinaload edge routines.
        /// </summary>
        private AccelerationMode DetectAcceleration(ushort pc, ushort currentPc)
        {
            int state = 0;
            int count = 0;
            ushort target = unchecked((ushort)(currentPc - 4));
            byte targetLo = (byte)target;
            byte targetHi = (byte)(target >> 8);

            while (count < 64)
            {
                byte value = _memory.ReadDirect(pc);
                pc = unchecked((ushort)(pc + 1));
                count++;

                switch (state)
                {
                    case 0:
                        state = value switch { 0x03 => 28, 0x04 => 1, _ => 13 };
                        break;
                    case 1:
                        if (value == 0x20) state = 40;
                        else if (value == 0xC8) state = 2;
                        else return AccelerationMode.None;
                        break;
                    case 2:
                        if (value == 0x3E) state = 3; else return AccelerationMode.None;
                        break;
                    case 3:
                        if (value is 0x00 or 0x7F or 0xFF) state = 4;
                        else return AccelerationMode.None;
                        break;
                    case 4:
                        if (value == 0xDB) state = 5; else return AccelerationMode.None;
                        break;
                    case 5:
                        if (value == 0xFE) state = 6; else return AccelerationMode.None;
                        break;
                    case 6:
                        if (value == 0x1F) state = 7;
                        else if (value == 0xA9) state = 24;
                        else return AccelerationMode.None;
                        break;
                    case 7:
                        if (value is 0x00 or 0xA7 or 0xC8 or 0xD0) state = 8;
                        else if (value == 0xA9) state = 9;
                        else return AccelerationMode.None;
                        break;
                    case 8:
                        if (value == 0xA9) state = 9; else return AccelerationMode.None;
                        break;
                    case 9:
                        if (value == 0xE6) state = 10; else return AccelerationMode.None;
                        break;
                    case 10:
                        if (value == 0x20) state = 11; else return AccelerationMode.None;
                        break;
                    case 11:
                        if (value == 0x28) state = 12; else return AccelerationMode.None;
                        break;
                    case 12:
                        return value == unchecked((byte)(0x100 - count))
                            ? AccelerationMode.Increasing
                            : AccelerationMode.None;

                    // Digital Integration loader.
                    case 13:
                        state = 14;
                        break;
                    case 14:
                        if (value == 0x05) state = 15; else return AccelerationMode.None;
                        break;
                    case 15:
                        if (value == 0xC8) state = 16; else return AccelerationMode.None;
                        break;
                    case 16:
                        if (value == 0xDB) state = 17; else return AccelerationMode.None;
                        break;
                    case 17:
                        if (value == 0xFE) state = 18; else return AccelerationMode.None;
                        break;
                    case 18:
                        if (value == 0xA9) state = 19; else return AccelerationMode.None;
                        break;
                    case 19:
                        if (value == 0xE6) state = 20; else return AccelerationMode.None;
                        break;
                    case 20:
                        if (value == 0x40) state = 21; else return AccelerationMode.None;
                        break;
                    case 21:
                        if (value == 0xCA) state = 22; else return AccelerationMode.None;
                        break;
                    case 22:
                        if (value == targetLo) state = 23; else return AccelerationMode.None;
                        break;
                    case 23:
                        return value == targetHi ? AccelerationMode.Decreasing : AccelerationMode.None;

                    // Search loader variants.
                    case 24:
                        if (value == 0xE6) state = 25; else return AccelerationMode.None;
                        break;
                    case 25:
                        if (value == 0x40) state = 26; else return AccelerationMode.None;
                        break;
                    case 26:
                        if (value == 0x28) state = 12;
                        else if (value == 0xD8) state = 27;
                        else return AccelerationMode.None;
                        break;
                    case 27:
                        if (value == 0x00) state = 11; else return AccelerationMode.None;
                        break;

                    // Alkatraz.
                    case 28:
                        if (value == 0xC3) state = 29; else return AccelerationMode.None;
                        break;
                    case 29:
                        state = 30;
                        break;
                    case 30:
                        state = 31;
                        break;
                    case 31:
                        if (value == 0xDB) state = 32; else return AccelerationMode.None;
                        break;
                    case 32:
                        if (value == 0xFE) state = 33; else return AccelerationMode.None;
                        break;
                    case 33:
                        if (value == 0x1F) state = 34; else return AccelerationMode.None;
                        break;
                    case 34:
                        if (value == 0xC8) state = 35; else return AccelerationMode.None;
                        break;
                    case 35:
                        if (value == 0xA9) state = 36; else return AccelerationMode.None;
                        break;
                    case 36:
                        if (value == 0xE6) state = 37; else return AccelerationMode.None;
                        break;
                    case 37:
                        if (value == 0x20) state = 38; else return AccelerationMode.None;
                        break;
                    case 38:
                        if (value == 0x28) state = 39; else return AccelerationMode.None;
                        break;
                    case 39:
                        return value is 0xF1 or 0xF3
                            ? AccelerationMode.Increasing
                            : AccelerationMode.None;

                    // Variant Alkatraz.
                    case 40:
                        if (value == 0x01) state = 41; else return AccelerationMode.None;
                        break;
                    case 41:
                        if (value == 0xC9) state = 31; else return AccelerationMode.None;
                        break;
                    default:
                        return AccelerationMode.None;
                }
            }

            return AccelerationMode.None;
        }
        private uint MemorySignature(ushort start)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < SignatureByteCount; i++)
            {
                hash ^= _memory.ReadDirect(unchecked((ushort)(start + i)));
                hash *= 16777619u;
            }

            return hash;
        }
        private void ResetRuntimeState()
        {
            ClearActiveMode();
            _lastPulseIndex = -1;
            ClearPulseClassification();
            _preparedState = default;
            _readPrepared = false;
        }
        private void ClearPulseClassification()
        {
            _lengthFlags1 = TapeAccelerationPulseFlags.None;
            _lengthFlags2 = TapeAccelerationPulseFlags.None;
        }
        private void ClearActiveMode()
        {
            _mode = AccelerationMode.None;
            _modePc = 0;
            _modeScanStart = 0;
            _modeSignature = 0;
            _modeReadsUntilRevalidation = 0;
        }
        private readonly record struct DetectionCacheEntry(
            ushort ScanStart,
            uint Signature,
            AccelerationMode Mode);
        private enum AccelerationMode
        {
            None,
            Increasing,
            Decreasing
        }
    }

}
