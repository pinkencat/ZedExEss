using ZedExEss.Spectrum.Disk.Beta;
using ZedExEss.Spectrum.Disk.Plus3;

namespace ZedExEss.Spectrum.Core;

/// <summary>Portable ownership of the images and source paths mounted in two +3 and TR-DOS drives.</summary>
public sealed class SpectrumDiskMediaState
{
    private readonly Plus3DiskImage?[] _plus3Images = new Plus3DiskImage?[2];
    private readonly string?[] _plus3Paths = new string?[2];
    private readonly TrdDiskImage?[] _trdImages = new TrdDiskImage?[2];
    private readonly string?[] _trdPaths = new string?[2];

    public Plus3DiskImage? GetPlus3Image(int drive) => _plus3Images[ValidateDrive(drive)];
    public string? GetPlus3Path(int drive) => _plus3Paths[ValidateDrive(drive)];
    public TrdDiskImage? GetTrdImage(int drive) => _trdImages[ValidateDrive(drive)];
    public string? GetTrdPath(int drive) => _trdPaths[ValidateDrive(drive)];

    /// <summary>Loads and mounts a +3 DSK image in the requested portable drive slot.</summary>
    public Plus3DiskImage LoadPlus3(int drive, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Plus3DiskImage image = Plus3DiskImage.Load(path);
        SetPlus3(drive, image, path);
        return image;
    }

    /// <summary>Loads and mounts a raw TRD or compact SCL image in a Beta 128 drive slot.</summary>
    public TrdDiskImage LoadTrd(int drive, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        TrdDiskImage image = Path.GetExtension(path).Equals(".scl", StringComparison.OrdinalIgnoreCase)
            ? TrdDiskImage.LoadScl(path)
            : TrdDiskImage.Load(path);
        SetTrd(drive, image, path);
        return image;
    }

    public void SetPlus3(int drive, Plus3DiskImage? image, string? path)
    {
        drive = ValidateDrive(drive);
        _plus3Images[drive] = image;
        _plus3Paths[drive] = image == null ? null : path;
    }

    public void SetTrd(int drive, TrdDiskImage? image, string? path)
    {
        drive = ValidateDrive(drive);
        _trdImages[drive] = image;
        _trdPaths[drive] = image == null ? null : path;
    }

    /// <summary>
    /// Flushes a dirty writable +3 image before removing it from portable session state.
    /// </summary>
    public void EjectPlus3(int drive, bool saveDirtyImage = true)
    {
        drive = ValidateDrive(drive);
        Plus3DiskImage? image = _plus3Images[drive];
        if (saveDirtyImage && image?.IsDirty == true)
        {
            image.Save();
        }

        SetPlus3(drive, null, null);
    }

    /// <summary>Removes a TR-DOS image; raw TRD sector writes are persisted by the image itself.</summary>
    public void EjectTrd(int drive)
    {
        SetTrd(ValidateDrive(drive), null, null);
    }

    private static int ValidateDrive(int drive)
    {
        if ((uint)drive >= 2)
        {
            throw new ArgumentOutOfRangeException(nameof(drive), drive, "Drive must be 0 or 1.");
        }

        return drive;
    }
}
