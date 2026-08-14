using ZedExEss.Spectrum.Interface1;

namespace ZedExEss.Spectrum.Core;

/// <summary>
/// Owns the eight Microdrive cartridges independently of a replaceable Spectrum machine.
/// </summary>
/// <remarks>
/// Interface 1 devices contain transient motor and head state and are rebuilt with the machine.
/// Cartridge bytes, write protection and source paths belong to the desktop session, so this
/// object reconnects them to each compatible replacement device without reloading the images.
/// Public drive indexes are zero based; the emulated Interface 1 API uses Sinclair drives 1-8.
/// </remarks>
public sealed class SpectrumInterface1MediaState
{
    private readonly MicrodriveCartridge?[] _cartridges = new MicrodriveCartridge?[SpectrumInterface1Device.DriveCount];
    private readonly string?[] _paths = new string?[SpectrumInterface1Device.DriveCount];
    private SpectrumInterface1Device? _device;
    private SpectrumInterface1NetworkStation? _networkStation;

    public SpectrumInterface1MediaState(SpectrumInterface1NetworkBus? networkBus = null)
    {
        NetworkBus = networkBus ?? new SpectrumInterface1NetworkBus();
    }

    /// <summary>
    /// Session-owned ZX Net wire. Supplying the same bus to multiple media states joins
    /// their Interface 1 devices without adding any packet-level emulator shortcut.
    /// </summary>
    public SpectrumInterface1NetworkBus NetworkBus { get; }

    /// <summary>The current machine's attachment to <see cref="NetworkBus"/>, if any.</summary>
    public SpectrumInterface1NetworkStation? NetworkStation => _networkStation;

    public MicrodriveCartridge? GetCartridge(int drive) => _cartridges[ValidateDrive(drive)];
    public string? GetPath(int drive) => _paths[ValidateDrive(drive)];
    public bool IsAttached(int drive) => GetCartridge(drive) != null;

    /// <summary>
    /// Captures all mounted cartridge bytes, host paths and (when connected) the
    /// exact Interface 1 transport state. The snapshot is independent of later
    /// media writes.
    /// </summary>
    public SpectrumInterface1Snapshot CaptureSnapshot()
    {
        var slots = new SpectrumInterface1MediaSlotState[_cartridges.Length];
        for (int drive = 0; drive < _cartridges.Length; drive++)
        {
            slots[drive] = new SpectrumInterface1MediaSlotState(
                _paths[drive],
                _cartridges[drive]?.CaptureState());
        }

        return new SpectrumInterface1Snapshot(
            new SpectrumInterface1MediaSnapshot(slots),
            _device?.CaptureState());
    }

    /// <summary>
    /// Restores media first, reconnects it to the current device, then restores
    /// rotating-head state. Discarded dirty state is not flushed to the host: a
    /// snapshot restore must not write the future state being rewound.
    /// </summary>
    public void RestoreSnapshot(SpectrumInterface1Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Media);
        if (snapshot.Device != null && _device == null)
        {
            throw new InvalidOperationException(
                "The captured Interface 1 device state cannot be restored while no device is connected.");
        }

        var restoredCartridges = new MicrodriveCartridge?[_cartridges.Length];
        var restoredPaths = new string?[_paths.Length];
        for (int drive = 0; drive < restoredCartridges.Length; drive++)
        {
            SpectrumInterface1MediaSlotState slot = snapshot.Media.GetSlot(drive);
            restoredCartridges[drive] = slot.Cartridge == null
                ? null
                : MicrodriveCartridge.FromState(slot.Cartridge);
            restoredPaths[drive] = slot.BackingPath;
        }

        if (_device != null)
        {
            _device.AttachNetworkStation(null);
            _networkStation?.Dispose();
            _networkStation = null;
            for (int drive = 0; drive < _cartridges.Length; drive++)
            {
                _device.EjectCartridge(drive + 1);
            }
        }

        Array.Copy(restoredCartridges, _cartridges, _cartridges.Length);
        Array.Copy(restoredPaths, _paths, _paths.Length);

        if (_device == null)
        {
            return;
        }

        _networkStation = NetworkBus.AttachStation("Local Interface 1");
        _device.AttachNetworkStation(_networkStation);

        for (int drive = 0; drive < _cartridges.Length; drive++)
        {
            if (_cartridges[drive] != null)
            {
                _device.InsertCartridge(drive + 1, _cartridges[drive]!);
            }
        }

        if (snapshot.Device != null)
        {
            _device.RestoreState(snapshot.Device);
        }
        else
        {
            _device.Reset();
        }
    }

    /// <summary>Loads an MDR image and mounts it in the selected persistent drive slot.</summary>
    public MicrodriveCartridge Attach(int drive, string path)
    {
        drive = ValidateDrive(drive);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        MicrodriveCartridge cartridge = MicrodriveCartridge.Load(path);
        SetCartridge(drive, cartridge, Path.GetFullPath(path));
        return cartridge;
    }

    /// <summary>Creates an empty formatted writable cartridge without assigning a host path.</summary>
    public MicrodriveCartridge Create(int drive, int sectorCount = 180, string? cartridgeName = null)
    {
        drive = ValidateDrive(drive);
        MicrodriveCartridge cartridge = MicrodriveCartridge.CreateFormatted(cartridgeName ?? "ZedExEss", sectorCount);
        SetCartridge(drive, cartridge, null);
        return cartridge;
    }

    /// <summary>Saves the selected cartridge and remembers the new path for later flushes.</summary>
    public void SaveAs(int drive, string path)
    {
        drive = ValidateDrive(drive);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        MicrodriveCartridge cartridge = _cartridges[drive]
            ?? throw new InvalidOperationException($"Microdrive {drive + 1} has no cartridge.");

        string fullPath = Path.GetFullPath(path);
        cartridge.Save(fullPath);
        _paths[drive] = fullPath;
    }

    /// <summary>Saves a modified cartridge to its existing path, if it has one.</summary>
    public bool Save(int drive)
    {
        drive = ValidateDrive(drive);
        MicrodriveCartridge? cartridge = _cartridges[drive];
        string? path = _paths[drive];
        if (cartridge == null || path == null)
        {
            return false;
        }

        cartridge.Save(path);
        return true;
    }

    /// <summary>
    /// Flushes every modified cartridge which has a backing path. Hosts call this only
    /// after stopping CPU execution, ensuring a coherent image rather than a snapshot
    /// taken halfway through an Interface 1 record write.
    /// </summary>
    public void FlushAll()
    {
        for (int drive = 0; drive < _cartridges.Length; drive++)
        {
            MicrodriveCartridge? cartridge = _cartridges[drive];
            string? path = _paths[drive];
            if (cartridge?.Modified == true && path != null)
            {
                cartridge.Save(path);
            }
        }
    }

    public void SetWriteProtected(int drive, bool writeProtected)
    {
        drive = ValidateDrive(drive);
        MicrodriveCartridge cartridge = _cartridges[drive]
            ?? throw new InvalidOperationException($"Microdrive {drive + 1} has no cartridge.");
        cartridge.SetWriteProtected(writeProtected);
    }

    /// <summary>
    /// Removes a cartridge. Dirty media with an existing path is flushed before it is detached;
    /// an unsaved in-memory cartridge remains the caller's responsibility.
    /// </summary>
    public MicrodriveCartridge? Eject(int drive, bool saveDirtyImage = true)
    {
        drive = ValidateDrive(drive);
        MicrodriveCartridge? cartridge = _cartridges[drive];
        if (saveDirtyImage && cartridge?.Modified == true && _paths[drive] != null)
        {
            cartridge.Save(_paths[drive]!);
        }

        _device?.EjectCartridge(drive + 1);
        _cartridges[drive] = null;
        _paths[drive] = null;
        return cartridge;
    }

    /// <summary>
    /// Moves all mounted cartridges to a newly-created Interface 1 device. Passing null retains
    /// the media while disconnecting a model which cannot host Interface 1.
    /// </summary>
    public void ConnectDevice(SpectrumInterface1Device? device)
    {
        if (ReferenceEquals(_device, device))
        {
            return;
        }

        if (_device != null)
        {
            _device.AttachNetworkStation(null);
            _networkStation?.Dispose();
            _networkStation = null;
            for (int drive = 0; drive < _cartridges.Length; drive++)
            {
                _device.EjectCartridge(drive + 1);
            }
        }

        _device = device;
        if (_device == null)
        {
            return;
        }

        _networkStation = NetworkBus.AttachStation("Local Interface 1");
        _device.AttachNetworkStation(_networkStation);

        for (int drive = 0; drive < _cartridges.Length; drive++)
        {
            if (_cartridges[drive] != null)
            {
                _device.InsertCartridge(drive + 1, _cartridges[drive]!);
            }
        }
    }

    public int GetFirstEmptyDrive()
    {
        for (int drive = 0; drive < _cartridges.Length; drive++)
        {
            if (_cartridges[drive] == null)
            {
                return drive;
            }
        }

        return -1;
    }

    private void SetCartridge(int drive, MicrodriveCartridge cartridge, string? path)
    {
        MicrodriveCartridge? previous = _cartridges[drive];
        string? previousPath = _paths[drive];
        if (previous?.Modified == true && previousPath != null)
        {
            previous.Save(previousPath);
        }

        _device?.EjectCartridge(drive + 1);
        _cartridges[drive] = cartridge;
        _paths[drive] = path;
        _device?.InsertCartridge(drive + 1, cartridge);
    }

    private static int ValidateDrive(int drive)
    {
        if ((uint)drive >= SpectrumInterface1Device.DriveCount)
        {
            throw new ArgumentOutOfRangeException(nameof(drive), drive, "Microdrive must be between 0 and 7.");
        }

        return drive;
    }
}
