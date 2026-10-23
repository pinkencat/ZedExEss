using ZedExEss.Spectrum.DivMmc;

namespace ZedExEss.Spectrum.Core;

/// <summary>
/// Owns the SD card backing a DivMMC expansion independently of any one machine instance.
/// </summary>
/// <remarks>
/// Model changes rebuild the machine and therefore the DivMMC device, but mounted media must
/// survive that rebuild. This class keeps the image/folder card alive and reconnects it to each
/// newly-created device. Folder-backed cards are flushed before replacement or ejection.
/// </remarks>
public sealed class SpectrumDivMmcMediaState : IDisposable
{
    private SpectrumDivMmcSdCard? _card;
    private SpectrumDivMmcDevice? _device;

    public string? Path { get; private set; }
    public bool IsFolderBacked { get; private set; }
    public bool IsWriteProtected => _card?.WriteProtected == true;
    public bool IsAttached => _card != null;

    /// <summary>Mounts an image or projected host folder, replacing any previous card.</summary>
    public void Attach(string path, bool folderBacked, bool writeProtected = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        SpectrumDivMmcSdCard replacement = folderBacked
            ? SpectrumDivMmcSdCard.OpenFolderBacked(path, writeProtected)
            : SpectrumDivMmcSdCard.Open(path, writeProtected);

        try
        {
            Eject();
            _card = replacement;
            Path = System.IO.Path.GetFullPath(path);
            IsFolderBacked = folderBacked;
            _device?.AttachSdCard(_card);
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Connects the currently mounted card to a newly-created DivMMC device. Passing null
    /// disconnects the old device while retaining the media for a later model/device rebuild.
    /// </summary>
    public void ConnectDevice(SpectrumDivMmcDevice? device)
    {
        if (ReferenceEquals(_device, device))
        {
            return;
        }

        _device?.AttachSdCard(null);
        _device = device;
        _device?.AttachSdCard(_card);
    }

    public void Eject()
    {
        _device?.AttachSdCard(null);

        SpectrumDivMmcSdCard? card = _card;
        _card = null;
        Path = null;
        IsFolderBacked = false;
        if (card == null)
        {
            return;
        }

        // Report folder writeback failures to the caller. Dispose remains a best-effort cleanup
        // and removes the temporary image even when exporting the folder failed.
        try
        {
            card.FlushFolderBacking();
        }
        finally
        {
            card.Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            Eject();
        }
        catch
        {
            // Application shutdown must continue even if a folder cannot be written back.
        }

        _device = null;
    }
}
