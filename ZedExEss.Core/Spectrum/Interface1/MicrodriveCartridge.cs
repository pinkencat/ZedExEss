namespace ZedExEss.Spectrum.Interface1;

/// <summary>
/// Mutable Sinclair Microdrive cartridge backed by the conventional raw `.mdr`
/// sector layout.
/// </summary>
/// <remarks>
/// An MDR image stores 543 bytes per physical sector: a 15-byte sector header,
/// a 15-byte record header, 512 data bytes and one data checksum. A single
/// optional byte after the final sector stores write protection. GAP, SYNC and
/// preamble timing are properties of the rotating transport and are therefore
/// not duplicated in the file.
/// </remarks>
public sealed class MicrodriveCartridge
{
    public const int HeaderLength = 15;
    public const int DataLength = 512;
    public const int SectorLength = HeaderLength + HeaderLength + DataLength + 1;
    public const int MinimumSectorCount = 10;
    public const int MaximumSectorCount = 254;

    private readonly byte[] _data;
    private readonly byte[] _preambleState;
    private bool _writeProtected;
    private bool _modified;

    private MicrodriveCartridge(byte[] data, int sectorCount, bool writeProtected, bool formatted)
    {
        _data = data;
        SectorCount = sectorCount;
        _writeProtected = writeProtected;
        _preambleState = new byte[checked(sectorCount * 2)];
        if (formatted)
        {
            Array.Fill(_preambleState, byte.MaxValue);
        }
    }

    public int SectorCount { get; }
    public int Length => _data.Length;
    public bool WriteProtected => _writeProtected;
    public bool Modified => _modified;

    public static MicrodriveCartridge Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("MDR path cannot be empty.", nameof(path));
        }

        return Load(File.ReadAllBytes(path));
    }

    public static MicrodriveCartridge Load(ReadOnlySpan<byte> image)
    {
        int remainder = image.Length % SectorLength;
        if (remainder is not 0 and not 1)
        {
            throw new InvalidDataException("MDR length must be whole 543-byte sectors plus an optional write-protect byte.");
        }

        int dataLength = image.Length - remainder;
        int sectorCount = dataLength / SectorLength;
        ValidateSectorCount(sectorCount);

        byte[] data = image[..dataLength].ToArray();
        bool writeProtected = remainder == 1 && image[^1] != 0;
        return new MicrodriveCartridge(data, sectorCount, writeProtected, formatted: true);
    }

    public static MicrodriveCartridge CreateBlank(int sectorCount = 180)
    {
        ValidateSectorCount(sectorCount);
        byte[] data = new byte[checked(sectorCount * SectorLength)];
        Array.Fill(data, byte.MaxValue);
        return new MicrodriveCartridge(data, sectorCount, writeProtected: false, formatted: false)
        {
            _modified = true
        };
    }

    /// <summary>Creates an empty cartridge with ROM-compatible formatted sector headers.</summary>
    /// <remarks>
    /// A physical FORMAT writes sector numbers 254 down to 1 while the cartridge
    /// loops beneath the head. On cartridges shorter than 254 sectors, later passes
    /// overwrite earlier headers. Reproducing that final ordering matters because
    /// the Interface 1 ROM builds its free-sector map from these identifiers.
    /// </remarks>
    public static MicrodriveCartridge CreateFormatted(string cartridgeName, int sectorCount = 180)
    {
        ValidateSectorCount(sectorCount);
        byte[] data = new byte[checked(sectorCount * SectorLength)];
        Span<byte> label = stackalloc byte[10];
        label.Fill(0x20);
        EncodeCartridgeName(cartridgeName, label);

        int sectorsOverwrittenOnFinalPass = 254 % sectorCount;
        for (int physicalSector = 0; physicalSector < sectorCount; physicalSector++)
        {
            int sectorNumber = physicalSector < sectorsOverwrittenOnFinalPass
                ? sectorsOverwrittenOnFinalPass - physicalSector
                : sectorCount + sectorsOverwrittenOnFinalPass - physicalSector;
            int offset = physicalSector * SectorLength;

            data[offset] = 0x01; // HDFLAG: valid cartridge-sector header.
            data[offset + 1] = checked((byte)sectorNumber);
            data[offset + 2] = 0x00;
            data[offset + 3] = 0x00;
            label.CopyTo(data.AsSpan(offset + 4, label.Length));
            data[offset + 14] = CalculateMicrodriveChecksum(data.AsSpan(offset, 14));

            // An all-zero record descriptor/data area denotes a free sector. Its
            // descriptor and data checksums are consequently both zero as well.
        }

        return new MicrodriveCartridge(data, sectorCount, writeProtected: false, formatted: true)
        {
            _modified = true
        };
    }

    public void SetWriteProtected(bool writeProtected)
    {
        if (_writeProtected == writeProtected)
        {
            return;
        }

        _writeProtected = writeProtected;
        _modified = true;
    }

    public byte[] ToMdrBytes()
    {
        byte[] image = new byte[_data.Length + 1];
        _data.CopyTo(image, 0);
        image[^1] = _writeProtected ? (byte)1 : (byte)0;
        return image;
    }

    public void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("MDR path cannot be empty.", nameof(path));
        }

        File.WriteAllBytes(path, ToMdrBytes());
        _modified = false;
    }

    public byte ReadByte(int position)
    {
        return _data[NormalizePosition(position)];
    }

    public bool TryWriteByte(int position, byte value)
    {
        if (_writeProtected)
        {
            return false;
        }

        int normalized = NormalizePosition(position);
        if (_data[normalized] != value)
        {
            _data[normalized] = value;
            _modified = true;
        }

        return true;
    }

    internal byte GetPreambleState(int section)
    {
        return _preambleState[section];
    }

    internal void BeginPreamble(int section)
    {
        if (_writeProtected)
        {
            return;
        }

        _preambleState[section] = 1;
    }

    internal void ContinuePreamble(int section)
    {
        if (_writeProtected || _preambleState[section] == byte.MaxValue)
        {
            return;
        }

        _preambleState[section]++;
    }

    internal void CompletePreamble(int section)
    {
        if (_writeProtected)
        {
            return;
        }

        _preambleState[section] = byte.MaxValue;
        _modified = true;
    }

    private int NormalizePosition(int position)
    {
        int normalized = position % _data.Length;
        return normalized < 0 ? normalized + _data.Length : normalized;
    }

    private static void EncodeCartridgeName(string cartridgeName, Span<byte> destination)
    {
        string name = string.IsNullOrWhiteSpace(cartridgeName) ? "ZedExEss" : cartridgeName.Trim();
        int length = Math.Min(name.Length, destination.Length);
        for (int i = 0; i < length; i++)
        {
            char character = name[i];
            destination[i] = character is >= ' ' and <= '~' ? (byte)character : (byte)'?';
        }
    }

    /// <summary>
    /// Implements the IF1 ROM's end-around-carry checksum, which deliberately
    /// avoids producing FFh because that value also represents a failed read.
    /// </summary>
    private static byte CalculateMicrodriveChecksum(ReadOnlySpan<byte> bytes)
    {
        byte checksum = 0;
        foreach (byte value in bytes)
        {
            int sum = checksum + value;
            int withCarry = (sum & 0xFF) + 1 + (sum > 0xFF ? 1 : 0);
            byte result = (byte)withCarry;
            checksum = result == 0 ? (byte)0 : (byte)(result - 1);
        }

        return checksum;
    }

    private static void ValidateSectorCount(int sectorCount)
    {
        if (sectorCount is < MinimumSectorCount or > MaximumSectorCount)
        {
            throw new InvalidDataException(
                $"MDR images must contain between {MinimumSectorCount} and {MaximumSectorCount} sectors; found {sectorCount}.");
        }
    }
}
