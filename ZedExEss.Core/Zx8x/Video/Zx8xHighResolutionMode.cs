namespace ZedExEss.Zx8x.Video;

/// <summary>Selects the source placed on the ZX8x video shift-register input.</summary>
public enum Zx8xHighResolutionMode
{
    /// <summary>
    /// Original Sinclair character generation. The display byte selects one of
    /// 64 glyphs and the ULA line counter selects its raster row. Software-only
    /// pseudo-hires works in this mode by resetting that counter every scanline.
    /// </summary>
    Sinclair,

    /// <summary>
    /// Wilf Rigter's WRX modification. RAM responds during the refresh part of
    /// each display M1 cycle, making the live IR address an arbitrary pixel byte
    /// while I is 20h or above. Lower I values retain Sinclair character video.
    /// </summary>
    Wrx
}
