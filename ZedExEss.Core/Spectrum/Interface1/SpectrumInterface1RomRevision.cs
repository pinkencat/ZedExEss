namespace ZedExEss.Spectrum.Interface1;

/// <summary>Selects one of the host-supplied Sinclair Interface 1 firmware revisions.</summary>
public enum SpectrumInterface1RomRevision
{
    Revision1,
    Revision2
}

/// <summary>Defines the Spectrum models to which a Sinclair Interface 1 can be attached.</summary>
public static class SpectrumInterface1Compatibility
{
    public static bool IsSupported(Spectrum.Core.SpectrumModel model)
    {
        return model is Spectrum.Core.SpectrumModel.Spectrum16K
            or Spectrum.Core.SpectrumModel.Spectrum48K
            or Spectrum.Core.SpectrumModel.Spectrum128K
            or Spectrum.Core.SpectrumModel.SpectrumPlus2;
    }

    public static string GetRomFileName(SpectrumInterface1RomRevision revision)
    {
        return revision switch
        {
            SpectrumInterface1RomRevision.Revision1 => "if1-1.rom",
            SpectrumInterface1RomRevision.Revision2 => "if1-2.rom",
            _ => throw new ArgumentOutOfRangeException(nameof(revision), revision, null)
        };
    }
}
