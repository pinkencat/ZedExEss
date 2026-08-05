using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ZedExEss.Hosting;

namespace ZedExEss.AvaloniaHost;

/// <summary>Adapts Avalonia's storage provider to the host-neutral dialog contract.</summary>
internal sealed class AvaloniaFileDialogService(Window owner) : IFileDialogService
{
    public async Task<string?> OpenFileAsync(FileDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<IStorageFile> files = await owner.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = options.Title,
                AllowMultiple = false,
                FileTypeFilter = CreateFileTypes(options.Filters)
            });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(FileDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IStorageFile? file = await owner.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = options.Title,
                DefaultExtension = options.DefaultExtension,
                SuggestedFileName = options.SuggestedFileName,
                FileTypeChoices = CreateFileTypes(options.Filters)
            });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> OpenFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> folders = await owner.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private static IReadOnlyList<FilePickerFileType> CreateFileTypes(
        IReadOnlyList<FileDialogFilter> filters)
    {
        return filters
            .Select(filter => new FilePickerFileType(filter.Name)
            {
                Patterns = filter.Patterns
            })
            .ToArray();
    }
}
