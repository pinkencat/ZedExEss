namespace ZedExEss.Spectrum.Core;

/// <summary>Serializable tape position used when replacing the emulated machine.</summary>
public sealed record SpectrumTapeSessionState(
    string Path,
    int BlockIndex,
    int PulseOffset,
    bool WasPlaying);
