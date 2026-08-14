namespace ZedExEss.Spectrum.Interface1;

/// <summary>
/// Deep-copied logical contents and write state of one Microdrive cartridge.
/// </summary>
/// <remarks>
/// The preamble array is runtime media state rather than part of an MDR file. It
/// must nevertheless be captured because restoring halfway through FORMAT or SAVE
/// must reproduce the same GAP/SYNC response as the original cartridge.
/// </remarks>
public sealed class MicrodriveCartridgeState
{
    private readonly byte[] _data;
    private readonly byte[] _preambleState;

    public MicrodriveCartridgeState(
        int sectorCount,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> preambleState,
        bool writeProtected,
        bool modified)
    {
        if (sectorCount is < MicrodriveCartridge.MinimumSectorCount or > MicrodriveCartridge.MaximumSectorCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount));
        }

        int expectedDataLength = checked(sectorCount * MicrodriveCartridge.SectorLength);
        if (data.Length != expectedDataLength)
        {
            throw new ArgumentException(
                $"Cartridge state must contain exactly {expectedDataLength} data bytes.",
                nameof(data));
        }

        int expectedPreambleLength = checked(sectorCount * 2);
        if (preambleState.Length != expectedPreambleLength)
        {
            throw new ArgumentException(
                $"Cartridge state must contain exactly {expectedPreambleLength} preamble entries.",
                nameof(preambleState));
        }

        SectorCount = sectorCount;
        _data = data.ToArray();
        _preambleState = preambleState.ToArray();
        WriteProtected = writeProtected;
        Modified = modified;
    }

    public int SectorCount { get; }
    public bool WriteProtected { get; }
    public bool Modified { get; }

    /// <summary>Returns a copy suitable for a snapshot serializer.</summary>
    public byte[] CopyData() => _data.ToArray();

    /// <summary>Returns a copy suitable for a snapshot serializer.</summary>
    public byte[] CopyPreambleState() => _preambleState.ToArray();

    internal ReadOnlySpan<byte> DataSpan => _data;
    internal ReadOnlySpan<byte> PreambleSpan => _preambleState;
}
