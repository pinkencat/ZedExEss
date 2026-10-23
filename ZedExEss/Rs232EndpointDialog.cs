using System.Windows;
using System.Windows.Controls;

namespace ZedExEss;

/// <summary>Small reusable prompt for a named-pipe name or pseudo-terminal path.</summary>
internal sealed class Rs232EndpointDialog : Window
{
    private readonly TextBox _value;

    public Rs232EndpointDialog(Window owner, string title, string prompt, string initialValue = "")
    {
        Owner = owner;
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        _value = new TextBox
        {
            Text = initialValue,
            Margin = new Thickness(0, 0, 0, 12)
        };
        panel.Children.Add(_value);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var connect = new Button
        {
            Content = "Connect",
            IsDefault = true,
            MinWidth = 86,
            Margin = new Thickness(0, 0, 6, 0)
        };
        connect.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_value.Text))
            {
                DialogResult = true;
            }
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 86
        };
        buttons.Children.Add(connect);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) =>
        {
            _value.Focus();
            _value.SelectAll();
        };
    }

    public string? EnteredValue => DialogResult == true ? _value.Text.Trim() : null;
}
