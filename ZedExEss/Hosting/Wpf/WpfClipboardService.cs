using System.Windows;
using ZedExEss.Hosting;

namespace ZedExEss.Hosting.Wpf;

/// <summary>WPF clipboard adapter used by modeless tools such as the debugger.</summary>
internal sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text) => Clipboard.SetText(text);
}
