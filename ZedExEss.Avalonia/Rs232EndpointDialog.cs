using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ZedExEss.AvaloniaHost;

/// <summary>Small reusable prompt for a named-pipe name or pseudo-terminal path.</summary>
internal sealed class Rs232EndpointDialog : Window
{
    private readonly TextBox _value;

    public Rs232EndpointDialog(string title, string prompt, string initialValue = "")
    {
        Title = title;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _value = new TextBox { Text = initialValue };
        var connect = new Button
        {
            Content = "Connect",
            IsDefault = true,
            MinWidth = 86
        };
        connect.Click += (_, _) =>
        {
            string value = _value.Text?.Trim() ?? string.Empty;
            if (value.Length != 0)
            {
                Close(value);
            }
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 86
        };
        cancel.Click += (_, _) => Close(null);

        Content = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    _value,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { connect, cancel }
                    }
                }
            }
        };

        Opened += (_, _) =>
        {
            _value.Focus();
            _value.SelectAll();
        };
    }
}
