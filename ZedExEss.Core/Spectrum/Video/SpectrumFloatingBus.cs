using ZedExEss.Spectrum.Abstractions;
using ZedExEss.Spectrum.Core;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Spectrum.Video
{
    /// <summary>
    /// Floating bus sampler for models where unhandled port reads expose ULA fetch data.
    /// </summary>
    public sealed class SpectrumFloatingBus(SpectrumModel model, SpectrumMemory screen) : IFloatingBus
    {
        private readonly SpectrumTimingModel _timing = SpectrumTimingModel.ForModel(model);
        private readonly SpectrumMemory _screen = screen ?? throw new ArgumentNullException(nameof(screen));
        private readonly int[] _pixelRowOffsets = BuildPixelRowOffsets(SpectrumTimingModel.DisplayHeightLines);
        private readonly int[] _attrRowOffsets = BuildAttributeRowOffsets(SpectrumTimingModel.DisplayHeightLines);
        public byte Read(ushort port, ulong tstate)
        {
            if (!_timing.FloatingBusEnabled)
            {
                return 0xFF;
            }

            long frameT = (long)(tstate % (ulong)_timing.TstatesPerFrame);
            frameT -= _timing.InterruptAssertOffsetTstates;
            frameT %= _timing.TstatesPerFrame;
            if (frameT < 0)
            {
                frameT += _timing.TstatesPerFrame;
            }

            long line = (frameT - _timing.LineTime0) / _timing.TstatesPerLine;

            if (line < SpectrumTimingModel.VisibleBorderHeightLines ||
                line >= SpectrumTimingModel.VisibleBorderHeightLines + SpectrumTimingModel.DisplayHeightLines)
            {
                return 0xFF;
            }

            long tstatesThroughLine = frameT - _timing.LineTime0 +
                (_timing.LeftBorderTstates - (SpectrumTimingModel.DisplayBorderWidthCols * 4));
            tstatesThroughLine %= _timing.TstatesPerLine;
            if (tstatesThroughLine < 0)
            {
                tstatesThroughLine += _timing.TstatesPerLine;
            }

            int leftBorder = _timing.LeftBorderTstates;
            int rightEdge = leftBorder + _timing.HorizontalScreenTstates;
            if (tstatesThroughLine < leftBorder ||
                tstatesThroughLine >= rightEdge)
            {
                return 0xFF;
            }

            int displayLine = (int)(line - SpectrumTimingModel.VisibleBorderHeightLines);
            int column = (int)((tstatesThroughLine - leftBorder) / 8) * 2;
            int phase = (int)(tstatesThroughLine & 0x07);

            // During each eight-T-state fetch group the ULA exposes pixel and
            // attribute bytes on specific phases. At every other phase the bus has
            // decayed and an unhandled read returns FF.
            switch (phase)
            {
                case 5:
                    column++;
                    goto case 3;
                case 3:
                    return _screen.ReadScreen((ushort)(0x5800 + _attrRowOffsets[displayLine] + column));

                case 4:
                    column++;
                    goto case 2;
                case 2:
                    return _screen.ReadScreen((ushort)(0x4000 + _pixelRowOffsets[displayLine] + column));

                default:
                    return 0xFF;
            }
        }
        private static int[] BuildPixelRowOffsets(int displayLines)
        {
            var offsets = new int[displayLines];
            for (int y = 0; y < displayLines; y++)
            {
                offsets[y] = ((y & 0xC0) << 5) | ((y & 0x07) << 8) | ((y & 0x38) << 2);
            }

            return offsets;
        }
        private static int[] BuildAttributeRowOffsets(int displayLines)
        {
            var offsets = new int[displayLines];
            for (int y = 0; y < displayLines; y++)
            {
                offsets[y] = (y >> 3) * 32;
            }

            return offsets;
        }
    }
}
