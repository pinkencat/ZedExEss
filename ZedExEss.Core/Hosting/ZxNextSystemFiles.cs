namespace ZedExEss.Hosting;

/// <summary>The System/Next folder and loader ROM a Next session is built from.</summary>
public sealed record ZxNextSystemFiles(string SystemDirectory, string BootRomPath)
{
    /// <summary>
    /// Asks for a replacement folder and loader ROM. Null means the user backed out and
    /// nothing should change.
    /// </summary>
    /// <remarks>
    /// The ROM is asked for second, starting in the chosen folder. Cancelling that picker
    /// keeps the current ROM when it still exists, so swapping only the distro is one
    /// folder pick and a cancel.
    /// </remarks>
    public static async Task<ZxNextSystemFiles?> ChooseAsync(
        IFileDialogService dialogs,
        string? currentBootRomPath)
    {
        ArgumentNullException.ThrowIfNull(dialogs);

        string? directory = await dialogs.OpenFolderAsync(
            "Select the extracted System/Next directory");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        bool canKeepBootRom = !string.IsNullOrWhiteSpace(currentBootRomPath)
            && File.Exists(currentBootRomPath);
        string? bootRomPath = await dialogs.OpenFileAsync(new FileDialogOptions
        {
            Title = canKeepBootRom
                ? "Select the ZX Spectrum Next loader ROM or bootrom.vhd (Cancel keeps the current one)"
                : "Select the ZX Spectrum Next loader ROM or bootrom.vhd",
            InitialDirectory = directory,
            Filters =
            [
                new FileDialogFilter("Next loader ROM", "*.bin", "*.rom", "*.vhd"),
                new FileDialogFilter("All files", "*.*")
            ]
        });
        if (string.IsNullOrWhiteSpace(bootRomPath))
        {
            if (!canKeepBootRom)
            {
                return null;
            }

            bootRomPath = currentBootRomPath!;
        }

        return new ZxNextSystemFiles(directory, bootRomPath);
    }

    /// <summary>True when these are the files the given paths already name.</summary>
    public bool Matches(string? systemDirectory, string? bootRomPath)
    {
        return SamePath(SystemDirectory, systemDirectory)
            && SamePath(BootRomPath, bootRomPath);
    }

    private static bool SamePath(string left, string? right)
    {
        if (string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            StringComparison comparison = OperatingSystem.IsLinux()
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A saved path that cannot be resolved is not the one just chosen.
            return false;
        }
    }
}
