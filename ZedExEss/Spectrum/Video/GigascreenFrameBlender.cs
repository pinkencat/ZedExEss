namespace ZedExEss.Spectrum.Video;

/// <summary>
/// Combines consecutive complete Spectrum frames to reproduce 50 Hz page-flipped gigascreen
/// colour pairs. Instances own exactly one history frame and one output frame; hosts should keep
/// no instance while the feature is disabled so ordinary presentation has no allocation or copy
/// overhead beyond its existing path.
/// </summary>
public sealed class GigascreenFrameBlender
{
    private readonly int[] _previous;
    private readonly int[] _blended;
    private bool _hasPrevious;

    public GigascreenFrameBlender(int pixelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelCount);
        _previous = new int[pixelCount];
        _blended = new int[pixelCount];
    }

    public int PixelCount => _previous.Length;

    /// <summary>
    /// Returns the current frame unchanged on the first call, then returns a reusable buffer
    /// containing the mean RGB value of the current and preceding frames.
    /// </summary>
    public int[] Compose(int[] current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Length != PixelCount)
        {
            throw new ArgumentException("Framebuffer size does not match the blender.", nameof(current));
        }

        if (!_hasPrevious)
        {
            Array.Copy(current, _previous, current.Length);
            _hasPrevious = true;
            return current;
        }

        Blend(current, _previous, _blended);
        Array.Copy(current, _previous, current.Length);
        return _blended;
    }

    /// <summary>Discards frame history, for example after reset, model change, or re-enabling.</summary>
    public void Reset()
    {
        _hasPrevious = false;
    }

    /// <summary>Blends opaque BGRA pixels without cross-channel carry.</summary>
    public static void Blend(ReadOnlySpan<int> current, ReadOnlySpan<int> previous, Span<int> destination)
    {
        if (current.Length != previous.Length || destination.Length < current.Length)
        {
            throw new ArgumentException("Gigascreen buffers must have matching lengths.");
        }

        for (int i = 0; i < current.Length; i++)
        {
            int a = current[i];
            int b = previous[i];
            int rgb = (a & b & 0x00FFFFFF) + (((a ^ b) & 0x00FEFEFE) >> 1);
            destination[i] = unchecked((int)0xFF000000) | rgb;
        }
    }
}
