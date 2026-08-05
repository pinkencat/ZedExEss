namespace ZedExEss.Hosting;

/// <summary>Describes one portable file-type choice shown by a host file picker.</summary>
public sealed class FileDialogFilter
{
    public FileDialogFilter(string name, params string[] patterns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(patterns);

        Name = name;
        Patterns = patterns;
    }

    public string Name { get; }

    /// <summary>Filename patterns such as <c>*.tzx</c> or <c>*.*</c>.</summary>
    public IReadOnlyList<string> Patterns { get; }
}

/// <summary>Host-neutral options shared by open and save file pickers.</summary>
public sealed class FileDialogOptions
{
    public string? Title { get; init; }

    public string? DefaultExtension { get; init; }

    public string? SuggestedFileName { get; init; }

    public string? InitialDirectory { get; init; }

    public bool ConfirmOverwrite { get; init; } = true;

    public IReadOnlyList<FileDialogFilter> Filters { get; init; } = [];
}
