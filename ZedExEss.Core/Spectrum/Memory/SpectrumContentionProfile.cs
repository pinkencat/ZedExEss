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
        private readonly byte[] _memoryDelays;
        private readonly byte[] _noMreqDelays;
        private readonly bool _ulaPortsContended;

        private SpectrumContentionProfile(int tstatesPerFrame, byte[] memoryDelays, byte[] noMreqDelays, bool ulaPortsContended)
        {
            _tstatesPerFrame = tstatesPerFrame;
            _memoryDelays = memoryDelays;
            _noMreqDelays = noMreqDelays;
            _ulaPortsContended = ulaPortsContended;
        }

        public int TstatesPerFrame => _tstatesPerFrame;
        public int GetMemoryDelay(ulong tstate)
        {
            int index = (int)(tstate % (ulong)_tstatesPerFrame);
            return _memoryDelays[index];
        }
        public int GetNoMreqDelay(ulong tstate)
        {
            int index = (int)(tstate % (ulong)_tstatesPerFrame);
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
                    byte[] memory = BuildDelayTable(timing, Pattern65432100, 1);
                    byte[] noMreq = BuildDelayTable(timing, Pattern65432100, 1);
                    return new SpectrumContentionProfile(timing.TstatesPerFrame, memory, noMreq, ulaPortsContended);
                }

                case SpectrumModel.SpectrumPlus2A:
                case SpectrumModel.SpectrumPlus3:
                {
                    byte[] memory = BuildDelayTable(timing, Pattern76543210, 4);
                    byte[] noMreq = new byte[timing.TstatesPerFrame];
                    return new SpectrumContentionProfile(timing.TstatesPerFrame, memory, noMreq, ulaPortsContended);
                }

                case SpectrumModel.Pentagon128:
                case SpectrumModel.Scorpion256:
                {
                    byte[] memory = new byte[timing.TstatesPerFrame];
                    byte[] noMreq = new byte[timing.TstatesPerFrame];
                    return new SpectrumContentionProfile(timing.TstatesPerFrame, memory, noMreq, ulaPortsContended);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported Spectrum model.");
            }
        }
        private static byte[] BuildDelayTable(ContentionTiming timing, int[] pattern, int offset)
        {
            var delays = new byte[timing.TstatesPerFrame];
            for (int t = 0; t < timing.TstatesPerFrame; t++)
            {
                delays[t] = (byte)GetDelayForTstate(timing, pattern, offset, t);
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
