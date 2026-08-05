using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ZedExEss.Spectrum.Memory;

namespace ZedExEss.AvaloniaHost;

/// <summary>Validates a batch of logical-address memory patches before returning it to the host.</summary>
internal sealed partial class PokeWindow : Window
{
    private readonly TextBox _input;
    private readonly TextBlock _status;

    public PokeWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _input = FindRequiredControl<TextBox>("PokeInput");
        _status = FindRequiredControl<TextBlock>("StatusText");
        FindRequiredControl<Button>("ApplyButton").Click += OnApply;
        FindRequiredControl<Button>("CancelButton").Click += (_, _) => Close(false);
        Opened += (_, _) => _input.Focus();
    }

    public IReadOnlyList<SpectrumPokeEntry> Pokes { get; private set; } = [];

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        if (!SpectrumPokeParser.TryParse(_input.Text, out IReadOnlyList<SpectrumPokeEntry> pokes, out string error))
        {
            _status.Text = error;
            return;
        }

        Pokes = pokes;
        Close(true);
    }

    private T FindRequiredControl<T>(string name) where T : Control =>
        this.FindControl<T>(name)
        ?? throw new InvalidOperationException($"{name} was not created by XAML.");
}
