using System;using ZedExEss.Spectrum.Core;

namespace ZedExEss.Spectrum.Video
{
    /// <summary>
    /// Immutable ULA geometry and interrupt timing values for a Spectrum model.
    /// </summary>
    /// <remarks>
    /// VisibleTop/BottomBorderLines define host cropping only. FirstDisplayLine,
    /// TstatesPerLine and TstatesPerFrame remain the physical model timings.
    /// </remarks>
    public sealed class SpectrumUlaTiming
    {
        public const int VisibleTopBorderLines = 48;
        public const int VisibleBottomBorderLines = 48;

        private SpectrumUlaTiming(
            int tstatesPerLine,
            int linesPerFrame,
            int firstDisplayLine,
            int displayLines,
            int visibleStartTstate,
            int visibleLineTstates,
            int displayStartTstate,
            int displayTstates,
            int interruptPulseTstates,
            int interruptDelayTstates,
            int interruptStartOffsetTstates,
            int displayFetchAdvanceTstates)
        {
            TstatesPerLine = tstatesPerLine;
            LinesPerFrame = linesPerFrame;
            FirstDisplayLine = firstDisplayLine;
            DisplayLines = displayLines;
            VisibleStartTstate = visibleStartTstate;
            VisibleLineTstates = visibleLineTstates;
            DisplayStartTstate = displayStartTstate;
            DisplayTstates = displayTstates;
            HorizontalBlankTstates = tstatesPerLine - visibleLineTstates;
            InterruptPulseTstates = interruptPulseTstates;
            InterruptDelayTstates = interruptDelayTstates;
            InterruptStartOffsetTstates = interruptStartOffsetTstates;
            DisplayFetchAdvanceTstates = displayFetchAdvanceTstates;
            FrameWidth = VisibleLineTstates * 2;
            VisibleFirstLine = firstDisplayLine - VisibleTopBorderLines;
            FrameHeight = VisibleTopBorderLines + displayLines + VisibleBottomBorderLines;
            DisplayWidth = displayTstates * 2;
            BorderLeftPixels = (displayStartTstate - visibleStartTstate) * 2;
            BorderRightPixels = ((visibleStartTstate + visibleLineTstates) - displayStartTstate - displayTstates) * 2;
            TstatesPerFrame = tstatesPerLine * linesPerFrame;
        }

        public int TstatesPerLine { get; }
        public int LinesPerFrame { get; }
        public int FirstDisplayLine { get; }
        public int VisibleFirstLine { get; }
        public int DisplayLines { get; }
        public int VisibleStartTstate { get; }
        public int DisplayStartTstate { get; }
        public int DisplayTstates { get; }
        public int TstatesPerFrame { get; }
        public int HorizontalBlankTstates { get; }
        public int VisibleLineTstates { get; }
        public int FrameWidth { get; }
        public int FrameHeight { get; }
        public int DisplayWidth { get; }
        public int BorderLeftPixels { get; }
        public int BorderRightPixels { get; }
        public int InterruptPulseTstates { get; }
        public int InterruptDelayTstates { get; }
        public int InterruptStartOffsetTstates { get; }
        public int DisplayFetchAdvanceTstates { get; }
        public static SpectrumUlaTiming ForModel(SpectrumModel model)
        {
            SpectrumTimingModel timingModel = SpectrumTimingModel.ForModel(model);
            int visibleBorder = SpectrumTimingModel.DisplayBorderWidthCols * 4;
            int visibleStart = timingModel.DisplayStartTstate - visibleBorder;
            int visibleLine = visibleBorder + timingModel.HorizontalScreenTstates + visibleBorder;
            // The CPU sees INT after the model-specific ULA-to-CPU propagation delay.
            int delay = model == SpectrumModel.Spectrum16K || model == SpectrumModel.Spectrum48K ? 1 : 3;
            int interruptStartOffset = timingModel.InterruptAssertOffsetTstates - delay;

            return new SpectrumUlaTiming(
                timingModel.TstatesPerLine,
                timingModel.LinesPerFrame,
                timingModel.DisplayBorderHeightLines,
                SpectrumTimingModel.DisplayHeightLines,
                visibleStart,
                visibleLine,
                timingModel.DisplayStartTstate,
                timingModel.HorizontalScreenTstates,
                timingModel.InterruptPulseTstates,
                delay,
                interruptStartOffset,
                2);
        }
    }
}
