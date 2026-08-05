using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ZedExEss.Hosting;

namespace ZedExEss.AvaloniaHost;

/// <summary>Avalonia editor over the portable BASIC editor session.</summary>
internal sealed partial class BasicProgramWindow : Window
{
    private readonly SpectrumBasicEditorSession _editor;
    private readonly TextBox _input;
    private readonly TextBlock _status;
    private bool _loading;

    public BasicProgramWindow(SpectrumBasicEditorSession editor)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        AvaloniaXamlLoader.Load(this);
        _input = FindRequiredControl<TextBox>("BasicInput");
        _status = FindRequiredControl<TextBlock>("StatusText");
        FindRequiredControl<Button>("InjectButton").Click += OnInject;
        FindRequiredControl<Button>("ReloadButton").Click += OnReload;
        FindRequiredControl<Button>("CloseButton").Click += (_, _) => Close();
        _input.TextChanged += OnTextChanged;
        Reload();
        Opened += (_, _) => _input.Focus();
    }

    private void OnReload(object? sender, RoutedEventArgs e) => Reload();

    private void OnInject(object? sender, RoutedEventArgs e)
    {
        _editor.SetSource(_input.Text ?? string.Empty);
        _editor.Inject(out _);
        _status.Text = _editor.Status;
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _editor.SetSource(_input.Text ?? string.Empty);
        _status.Text = _editor.Status;
    }

    private void Reload()
    {
        _loading = true;
        try
        {
            _editor.Reload();
            _input.Text = _editor.Source;
            _status.Text = _editor.Status;
        }
        finally
        {
            _loading = false;
        }
    }

    private T FindRequiredControl<T>(string name) where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"{name} was not created by XAML.");
    }
}
