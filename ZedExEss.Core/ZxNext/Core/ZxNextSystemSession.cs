using System.Text;
using ZedExEss.Spectrum.DivMmc;
using ZedExEss.ZxNext.Firmware;

namespace ZedExEss.ZxNext.Core;

/// <summary>Owns a desktop-ready Next machine and its disposable System/Next projection.</summary>
public sealed class ZxNextSystemSession : IDisposable
{
    private bool _disposed;

    private ZxNextSystemSession(
        ZxNextSystemAssets assets,
        ZxNextMachine machine,
        SpectrumDivMmcSdCard systemCard)
    {
        Assets = assets;
        Machine = machine;
        SystemCard = systemCard;
        _lastSeenWriteCount = systemCard.WriteCount;
        machine.FrameCompleted += OnMachineFrameCompleted;
    }

    /// <summary>
    /// Frames of quiet after a write before the folder is brought up to date.
    /// </summary>
    /// <remarks>
    /// Two seconds. Merging on every block written would be absurd - a single saved file
    /// is hundreds of blocks - so the card is left to finish what it is doing first. The
    /// cost of waiting is that an emulator killed within two seconds of a save loses it,
    /// which is the same bargain any editor with an autosave makes.
    /// </remarks>
    public const int AutoFlushSettleFrames = 100;

    private ulong _lastSeenWriteCount;
    private int _quietFrames;
    private bool _autoFlushRaised;

    /// <summary>
    /// Raised on the emulation thread when the guest has finished writing and the folder
    /// should be brought up to date.
    /// </summary>
    /// <remarks>
    /// An event rather than a flush done here, because merging reads the whole projected
    /// image and the host has to stop the machine around it - which the session has no
    /// business deciding for itself.
    /// </remarks>
    public event Action? AutoFlushDue;

    private void OnMachineFrameCompleted()
    {
        ulong writes = SystemCard.WriteCount;
        if (writes != _lastSeenWriteCount)
        {
            _lastSeenWriteCount = writes;
            _quietFrames = 0;
            _autoFlushRaised = false;
            return;
        }

        if (_autoFlushRaised || !SystemCard.HasUnflushedChanges)
        {
            return;
        }

        if (++_quietFrames >= AutoFlushSettleFrames)
        {
            _autoFlushRaised = true;
            AutoFlushDue?.Invoke();
        }
    }

    public ZxNextSystemAssets Assets { get; }
    public ZxNextMachine Machine { get; }
    public SpectrumDivMmcSdCard SystemCard { get; }

    public static ZxNextSystemSession Create(
        string systemDirectory,
        string bootRomPath,
        int sampleRate = 44_100,
        bool persistChanges = false)
        => Create(systemDirectory, bootRomPath, overlay: null, sampleRate, persistChanges);

    /// <summary>
    /// Boots System/Next from a host folder projected as the SD card.
    /// </summary>
    /// <param name="persistChanges">
    /// Whether what the guest writes is merged back into the folder when the session
    /// ends. Off by default so that a diagnostic run, which boots the same tree dozens of
    /// times, cannot leave anything behind in the user's NextZXOS install; the desktop
    /// hosts turn it on because for them the folder *is* the SD card.
    /// </param>
    public static ZxNextSystemSession Create(
        string systemDirectory,
        string bootRomPath,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? overlay,
        int sampleRate = 44_100,
        bool persistChanges = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootRomPath);
        ZxNextSystemAssets assets = ZxNextSystemAssets.LoadFromDirectory(
            systemDirectory,
            bootRomPath);
        if (!assets.HasBootLoaderRom)
        {
            throw new InvalidDataException(
                "A compiled 8 KiB loader ROM or bootrom.vhd is required for System/Next boot.");
        }

        ZxNextMachine machine = ZxNextMachineFactory.CreateFoundationMachine(assets, sampleRate);
        var bootProfileOverlay = new Dictionary<string, ReadOnlyMemory<byte>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["machines/next/config.ini"] = Encoding.ASCII.GetBytes("timing=7\r\n")
        };
        if (overlay is not null)
        {
            foreach ((string path, ReadOnlyMemory<byte> data) in overlay)
            {
                bootProfileOverlay[path] = data;
            }
        }
        SpectrumDivMmcSdCard? card = null;
        try
        {
            card = SpectrumDivMmcSdCard.OpenFolderBacked(
                assets.RootDirectory,
                writeProtected: false,
                overlayFiles: bootProfileOverlay,
                writeBackChanges: persistChanges);
            machine.DivMmc.AttachSdCard(0, card);
            return new ZxNextSystemSession(assets, machine, card);
        }
        catch
        {
            machine.DivMmc.AttachSdCard(0, null);
            card?.Dispose();
            throw;
        }
    }

    /// <summary>True when the guest has written to the card and the folder is behind.</summary>
    public bool HasUnsavedCardChanges => SystemCard.HasUnflushedChanges;

    /// <summary>
    /// Merges anything the guest has written back into the projected folder.
    /// </summary>
    /// <remarks>
    /// Does nothing unless the session was created to persist and the guest has actually
    /// written, so a host can call it whenever it likes.
    /// </remarks>
    public SpectrumFatMergeResult FlushSdCard()
    {
        SystemCard.FlushFolderBacking();
        return SystemCard.LastFlushResult;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Machine.FrameCompleted -= OnMachineFrameCompleted;
        Machine.SetPaused(true);
        Machine.DivMmc.AttachSdCard(0, null);
        SystemCard.Dispose();
    }
}
