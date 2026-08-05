using ZedExEss.Spectrum.Video;

namespace ZedExEss.AvaloniaHost;

/// <summary>Owns optional presentation transforms that sit outside emulated ULA timing.</summary>
public sealed partial class MainWindow
{
    private void ConfigureGigascreenPresentation()
    {
        if (_machine != null)
        {
            // Blending requires every completed frame. The normal path may retain dirty-line
            // state internally, so request complete frame snapshots only while this is enabled.
            _machine.Emulator.ForceFullFrameCopy = _gigascreenBlendEnabled;
        }

        int[]? frameBuffer = _frameBuffer;
        if (!_gigascreenBlendEnabled || frameBuffer == null)
        {
            _gigascreenBlender = null;
            return;
        }

        if (_gigascreenBlender?.PixelCount != frameBuffer.Length)
        {
            _gigascreenBlender = new GigascreenFrameBlender(frameBuffer.Length);
        }
        else
        {
            _gigascreenBlender.Reset();
        }
    }
}
