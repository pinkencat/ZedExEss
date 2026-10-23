using System;using ZedExEss.Spectrum.Abstractions; using ZedExEss.Spectrum.Core;

namespace ZedExEss.Spectrum.Memory
{
    /// <summary>
    /// Precomputed per-frame contention tables for memory, no-MREQ cycles and ULA port access.
    /// </summary>
    /// <remarks>
    /// Precomputation keeps the CPU hot path to one modulo and one array lookup. The
    /// two patterns describe the ULA's eight-T-state fetch cadence; clone models that
    /// do not contend RAM receive zero-filled tables.
    /// </remarks>
    public sealed class SpectrumContentionProfile : IContentionProfile
    {
        private static readonly int[] Pattern65432100 = [5, 4, 3, 2, 1, 0, 0, 6];
        private static readonly int[] Pattern76543210 = [5, 4, 3, 2, 1, 0, 7, 6];

        private readonly int _tstatesPerFrame;
        private readonly byte[] _earlyMemoryDelays;
        private readonly byte[] _earlyNoMreqDelays;
        private readonly byte[] _lateMemoryDelays;
        private readonly byte[] _lateNoMreqDelays;
        private readonly bool _ulaPortsContended;
        private byte[] _memoryDelays;
        private byte[] _noMreqDelays;
        private SpectrumContentionTiming _timing;

        private SpectrumContentionProfile(
            int tstatesPerFrame,
            byte[] earlyMemoryDelays,
            byte[] earlyNoMreqDelays,
            byte[] lateMemoryDelays,
            byte[] lateNoMreqDelays,
            bool ulaPortsContended)
        {
            _tstatesPerFrame = tstatesPerFrame;
            _earlyMemoryDelays = earlyMemoryDelays;
            _earlyNoMreqDelays = earlyNoMreqDelays;
            _lateMemoryDelays = lateMemoryDelays;
            _lateNoMreqDelays = lateNoMreqDelays;
            _memoryDelays = earlyMemoryDelays;
            _noMreqDelays = earlyNoMreqDelays;
            _ulaPortsContended = ulaPortsContended;
        }

        /// <summary>
        /// Which of the two contention phases the ULA uses.
        /// </summary>
        /// <remarks>
        /// Real machines differ by a single T-state in when the ULA starts stealing the
        /// bus, and emulators differ in which of the two they model. Switching swaps a
        /// precomputed table rather than testing a flag, so the CPU's hot path is
        /// unchanged either way.
        /// </remarks>
        public SpectrumContentionTiming Timing
        {
            get => _timing;
            set
            {
                if (_timing == value)
                {
                    return;
                }

                _timing = value;
                bool late = value == SpectrumContentionTiming.Late;
                _memoryDelays = late ? _lateMemoryDelays : _earlyMemoryDelays;
                _noMreqDelays = late ? _lateNoMreqDelays : _earlyNoMreqDelays;
            }
        }

        public int TstatesPerFrame => _tstatesPerFrame;
        /// <summary>
        /// The CPU cycle that counts as the start of a frame for contention purposes.
        /// </summary>
        /// <remarks>
        /// Normally zero: the clock and the display share an origin. Replaying a recording
        /// takes its frame boundaries from a fetch count instead, so the clock drifts
        /// against the display - and since contention depends on where the beam is, that
        /// drift charges accesses for the wrong part of the screen. Re-anchoring here each
        /// frame keeps the two together.
        /// </remarks>
        public ulong PhaseOrigin { get; set; }

        public int GetMemoryDelay(ulong tstate)
        {
            int index = (int)((tstate - PhaseOrigin) % (ulong)_tstatesPerFrame);
            return _memoryDelays[index];
        }
        public int GetNoMreqDelay(ulong tstate)
        {
            int index = (int)((tstate - PhaseOrigin) % (ulong)_tstatesPerFrame);
            return _noMreqDelays[index];
        }
        public bool IsUlaPort(ushort port)
        {
            return _ulaPortsContended && (port & 0x0001) == 0;
        }
        public static SpectrumContentionProfile Create(SpectrumModel model)
        {
            SpectrumTimingModel timingModel = SpectrumTimingModel.ForModel(model);
            ContentionTiming timing = new(timingModel);
            bool ulaPortsContended = SpectrumModelTraits.HasUlaPortContention(model);

            switch (model)
            {
                case SpectrumModel.Spectrum16K:
                case SpectrumModel.Spectrum48K:
                case SpectrumModel.Spectrum128K:
                case SpectrumModel.SpectrumPlus2:
                {
                    return new SpectrumContentionProfile(
                        timing.TstatesPerFrame,
                        BuildDelayTable(timing, Pattern65432100, 1, 0),
                        BuildDelayTable(timing, Pattern65432100, 1, 0),
                        BuildDelayTable(timing, Pattern65432100, 1, 1),
                        BuildDelayTable(timing, Pattern65432100, 1, 1),
                        ulaPortsContended);
                }

                case SpectrumModel.SpectrumPlus2A:
                case SpectrumModel.SpectrumPlus3:
                {
                    byte[] noMreq = new byte[timing.TstatesPerFrame];
                    return new SpectrumContentionProfile(
                        timing.TstatesPerFrame,
                        BuildDelayTable(timing, Pattern76543210, 4, 0),
                        noMreq,
                        BuildDelayTable(timing, Pattern76543210, 4, 1),
                        noMreq,
                        ulaPortsContended);
                }

                case SpectrumModel.Pentagon128:
                case SpectrumModel.Scorpion256:
                {
                    byte[] memory = new byte[timing.TstatesPerFrame];
                    byte[] noMreq = new byte[timing.TstatesPerFrame];
                    return new SpectrumContentionProfile(
                        timing.TstatesPerFrame, memory, noMreq, memory, noMreq, ulaPortsContended);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported Spectrum model.");
            }
        }
        /// <summary>
        /// Builds one frame of delays. <paramref name="shift"/> moves the whole
        /// contention window later by that many T-states, which is the only difference
        /// between the early and late models.
        /// </summary>
        private static byte[] BuildDelayTable(ContentionTiming timing, int[] pattern, int offset, int shift)
        {
            var delays = new byte[timing.TstatesPerFrame];
            for (int t = 0; t < timing.TstatesPerFrame; t++)
            {
                delays[t] = (byte)GetDelayForTstate(timing, pattern, offset, t - shift);
            }

            return delays;
        }
        private static int GetDelayForTstate(ContentionTiming timing, int[] pattern, int offset, long time)
        {
            // Convert the scheduler's frame-relative clock into the contention
            // coordinate system whose origin is the interrupt assertion point.
            time = timing.ToTstateContended(time);
            long line = (time - timing.LineTime0) / timing.TstatesPerLine;
            if (line < SpectrumTimingModel.VisibleBorderHeightLines ||
                line >= SpectrumTimingModel.VisibleBorderHeightLines + SpectrumTimingModel.DisplayHeightLines)
            {
                return 0;
            }

            long tstatesThroughLine = time - timing.LineTime0 + timing.FetchWindowAdjustment;
            tstatesThroughLine %= timing.TstatesPerLine;
            if (tstatesThroughLine < 0)
            {
                tstatesThroughLine += timing.TstatesPerLine;
            }

            int leftBorder = timing.LeftBorderTstates;
            int rightEdge = leftBorder + timing.HorizontalScreenTstates;

            if (tstatesThroughLine < leftBorder - offset)
            {
                return 0;
            }

            if (tstatesThroughLine >= rightEdge - offset)
            {
                return 0;
            }

            return pattern[(int)(tstatesThroughLine & 7)];
        }
        private readonly struct ContentionTiming(SpectrumTimingModel timing)
        {
            public int TstatesPerLine { get; } = timing.TstatesPerLine;
            public int TstatesPerFrame { get; } = timing.TstatesPerFrame;
            public int HorizontalScreenTstates { get; } = timing.HorizontalScreenTstates;
            public int LeftBorderTstates { get; } = timing.LeftBorderTstates;
            public int InterruptAssertOffsetTstates { get; } = timing.InterruptAssertOffsetTstates;
            public int FetchWindowAdjustment { get; } = timing.LeftBorderTstates - (SpectrumTimingModel.DisplayBorderWidthCols * 4);
            public int LineTime0 { get; } = timing.LineTime0;
            public long ToTstateContended(long frameTstate)
            {
                long tstate = frameTstate - InterruptAssertOffsetTstates;
                tstate %= TstatesPerFrame;
                if (tstate < 0)
                {
                    tstate += TstatesPerFrame;
                }

                return tstate;
            }
        }
    }
}
