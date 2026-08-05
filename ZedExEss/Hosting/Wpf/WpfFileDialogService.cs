using Microsoft.Win32;
using System.Windows;
using ZedExEss.Hosting;

namespace ZedExEss.Hosting.Wpf;

/// <summary>WPF implementation of the portable file-picker boundary.</summary>
internal sealed class WpfFileDialogService : IFileDialogService
{
    public Task<string?> OpenFileAsync(FileDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dialog = new OpenFileDialog
        {
            Title = options.Title ?? string.Empty,
            Filter = BuildFilter(options.Filters),
            DefaultExt = options.DefaultExtension ?? string.Empty,
            FileName = options.SuggestedFileName ?? string.Empty,
            InitialDirectory = options.InitialDirectory ?? string.Empty
        };

        return Task.FromResult(Show(dialog) ? dialog.FileName : null);
    }

    public Task<string?> SaveFileAsync(FileDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var dialog = new SaveFileDialog
        {
            Title = options.Title ?? string.Empty,
            Filter = BuildFilter(options.Filters),
            DefaultExt = options.DefaultExtension ?? string.Empty,
            FileName = options.SuggestedFileName ?? string.Empty,
            InitialDirectory = options.InitialDirectory ?? string.Empty,
            OverwritePrompt = options.ConfirmOverwrite
        };

        return Task.FromResult(Show(dialog) ? dialog.FileName : null);
    }

    public Task<string?> OpenFolderAsync(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return Task.FromResult(Show(dialog) ? dialog.FolderName : null);
    }

    private static string BuildFilter(IReadOnlyList<FileDialogFilter> filters)
    {
        return string.Join('|', filters.Select(filter =>
            $"{filter.Name} ({string.Join(';', filter.Patterns)})|{string.Join(';', filter.Patterns)}"));
    }

    private static bool Show(CommonDialog dialog)
    {
        Window? owner = Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive);
        return owner == null ? dialog.ShowDialog() == true : dialog.ShowDialog(owner) == true;
    }
}
