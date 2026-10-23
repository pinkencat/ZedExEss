namespace ZedExEss.Zx8x.Tape;

/// <summary>Receives logical MIC transitions at their exact CPU T-state.</summary>
public interface IZx8xCassetteOutputSink
{
    void SetMicLevel(ulong tstate, bool high);
}
