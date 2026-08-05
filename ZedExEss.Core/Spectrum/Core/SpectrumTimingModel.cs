using System;

namespace ZedExEss.Spectrum.Core
{
    /// <summary>
    /// CPU-frame timing profile used by contention, interrupt and frame scheduling code.
    /// </summary>
    /// <remarks>
    /// Values are expressed in CPU T-states. The profile describes the complete
    /// hardware frame; the separately derived ULA geometry chooses which portion is
    /// visible in the host window without changing those timings.
    /// </remarks>
    public readonly struct SpectrumTimingModel(
        int tstatesPerLine,
        int linesPerFrame,
        int leftBorderTstates,
        int displayStartTstate,
        int horizontalScreenTstates,
        int topLeftPixelTstate,
        int displayBorderHeightLines,
        int interruptPulseTstates,
        int interruptAssertOffsetTstates,
        bool floatingBusEnabled,
        bool ioWritesLatchAtEndOfCycle = false)
    {
        public const int DisplayHeightLines = 192;
        public const int DisplayBorderWidthCols = 4;
        public const int VisibleBorderHeightLines = 24; // DISPLAY_BORDER_HEIGHT

        public int TstatesPerLine { get; } = tstatesPerLine;
        public int LinesPerFrame { get; } = linesPerFrame;
        public int TstatesPerFrame { get; } = tstatesPerLine * linesPerFrame;
        public int LeftBorderTstates { get; } = leftBorderTstates;
        public int DisplayStartTstate { get; } = displayStartTstate;
        public int HorizontalScreenTstates { get; } = horizontalScreenTstates;
        public int TopLeftPixelTstate { get; } = topLeftPixelTstate;
        public int DisplayBorderHeightLines { get; } = displayBorderHeightLines;
        public int InterruptPulseTstates { get; } = interruptPulseTstates;
        public int InterruptAssertOffsetTstates { get; } = interruptAssertOffsetTstates;
        public int LineTime0 { get; } = topLeftPixelTstate - (VisibleBorderHeightLines * tstatesPerLine) - (DisplayBorderWidthCols * 4);
        public bool FloatingBusEnabled { get; } = floatingBusEnabled;
        public bool IoWritesLatchAtEndOfCycle { get; } = ioWritesLatchAtEndOfCycle;
        public static SpectrumTimingModel ForModel(SpectrumModel model)
        {
            // Keep model constants in one place. Renderer, floating bus and
            // contention code derive their coordinate systems from this profile.
            bool floatingBusEnabled = SpectrumModelTraits.HasFloatingBus(model);

            return model switch
            {
                SpectrumModel.Spectrum16K => new SpectrumTimingModel(224, 312, 24, 24, 128, 14336, 64, 32, 24, floatingBusEnabled),
                SpectrumModel.Spectrum48K => new SpectrumTimingModel(224, 312, 24, 24, 128, 14336, 64, 32, 24, floatingBusEnabled),
                SpectrumModel.Spectrum128K => new SpectrumTimingModel(228, 311, 24, 24, 128, 14362, 63, 36, 26, floatingBusEnabled),
                SpectrumModel.SpectrumPlus2 => new SpectrumTimingModel(228, 311, 24, 24, 128, 14362, 63, 36, 26, floatingBusEnabled),
                SpectrumModel.SpectrumPlus2A => new SpectrumTimingModel(228, 311, 24, 24, 128, 14365, 63, 32, 23, floatingBusEnabled),
                SpectrumModel.SpectrumPlus3 => new SpectrumTimingModel(228, 311, 24, 24, 128, 14365, 63, 32, 23, floatingBusEnabled),
                SpectrumModel.Pentagon128 => new SpectrumTimingModel(224, 320, 36, 69, 128, 17989, 80, 36, 0, floatingBusEnabled, ioWritesLatchAtEndOfCycle: true),
                SpectrumModel.Scorpion256 => new SpectrumTimingModel(224, 312, 24, 24, 128, 14336, 64, 36, 24, floatingBusEnabled),
                _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Unsupported Spectrum model.")
            };
        }
    }
}
