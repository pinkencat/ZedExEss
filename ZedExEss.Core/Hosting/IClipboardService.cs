namespace ZedExEss.Hosting;

/// <summary>Minimal clipboard boundary required by the debugger export UI.</summary>
public interface IClipboardService
{
    void SetText(string text);
}
