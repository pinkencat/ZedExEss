namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Receives completed emulator frames without coupling the core to a UI toolkit.
    /// </summary>
    /// <remarks>
    /// Implementations may upload the complete frame or coalesce dirty scanlines.
    /// Presentation is deliberately passive: it must never advance emulation time.
    /// </remarks>
    public interface IFramePresenter
    {
        int Width { get; }
        int Height { get; }

        void Present(int[] frameBuffer);
        void PresentDirty(int[] frameBuffer, int[] dirtyLines, int dirtyCount);
    }
}
