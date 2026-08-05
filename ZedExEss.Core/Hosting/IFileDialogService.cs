namespace ZedExEss.Hosting;

/// <summary>
/// Provides file and folder selection without coupling consumers to WPF, Avalonia, or a
/// platform-specific dialog implementation.
/// </summary>
public interface IFileDialogService
{
    Task<string?> OpenFileAsync(FileDialogOptions options);

    Task<string?> SaveFileAsync(FileDialogOptions options);

    Task<string?> OpenFolderAsync(string title);
}
