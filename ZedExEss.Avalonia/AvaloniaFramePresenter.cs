using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.AvaloniaHost;

/// <summary>Uploads the core BGRA framebuffer into an Avalonia writeable bitmap.</summary>
internal sealed class AvaloniaFramePresenter : IFramePresenter, IDisposable
{
    public AvaloniaFramePresenter(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        Width = width;
        Height = height;
        Bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
    }

    public int Width { get; }
    public int Height { get; }
    public WriteableBitmap Bitmap { get; }

    public void Present(int[] frameBuffer)
    {
        ArgumentNullException.ThrowIfNull(frameBuffer);
        if (frameBuffer.Length != Width * Height)
        {
            throw new ArgumentException("Framebuffer size does not match the presenter.", nameof(frameBuffer));
        }

        using ILockedFramebuffer locked = Bitmap.Lock();
        for (int y = 0; y < Height; y++)
        {
            IntPtr destination = IntPtr.Add(locked.Address, y * locked.RowBytes);
            Marshal.Copy(frameBuffer, y * Width, destination, Width);
        }
    }

    public void PresentDirty(int[] frameBuffer, int[] dirtyLines, int dirtyCount)
    {
        ArgumentNullException.ThrowIfNull(frameBuffer);
        ArgumentNullException.ThrowIfNull(dirtyLines);
        if (frameBuffer.Length != Width * Height)
        {
            throw new ArgumentException("Framebuffer size does not match the presenter.", nameof(frameBuffer));
        }

        if (dirtyCount <= 0)
        {
            return;
        }

        if (dirtyCount >= Height)
        {
            Present(frameBuffer);
            return;
        }

        using ILockedFramebuffer locked = Bitmap.Lock();
        int count = Math.Min(dirtyCount, dirtyLines.Length);
        for (int i = 0; i < count; i++)
        {
            int line = dirtyLines[i];
            if ((uint)line >= (uint)Height)
            {
                continue;
            }

            IntPtr destination = IntPtr.Add(locked.Address, line * locked.RowBytes);
            Marshal.Copy(frameBuffer, line * Width, destination, Width);
        }
    }

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}
