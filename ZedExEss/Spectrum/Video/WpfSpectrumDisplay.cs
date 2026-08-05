using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZedExEss.Spectrum.Abstractions;

namespace ZedExEss.Spectrum.Video
{
    /// <summary>
    /// WPF writeable-bitmap presenter for full-frame and dirty-line emulator output.
    /// </summary>
    public sealed class WpfSpectrumDisplay : IFramePresenter
    {
        private readonly WriteableBitmap _bitmap;
        private readonly Int32Rect _rect;
        private readonly int _stride;
        private readonly int _width;
        private readonly int _height;

        public WpfSpectrumDisplay(int width, int height)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);

            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

            _width = width;
            _height = height;
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _rect = new Int32Rect(0, 0, width, height);
            _stride = width * 4;
        }

        public WriteableBitmap Bitmap => _bitmap;
        public int Width => _width;
        public int Height => _height;
        public void Present(int[] frameBuffer)
        {
            ArgumentNullException.ThrowIfNull(frameBuffer);

            _bitmap.WritePixels(_rect, frameBuffer, _stride, 0);
        }
        public void PresentDirty(int[] frameBuffer, int[] dirtyLines, int dirtyCount)
        {
            ArgumentNullException.ThrowIfNull(frameBuffer);

            ArgumentNullException.ThrowIfNull(dirtyLines);

            if (dirtyCount <= 0)
            {
                return;
            }

            if (dirtyCount >= _height)
            {
                _bitmap.WritePixels(_rect, frameBuffer, _stride, 0);
                return;
            }

            int max = Math.Min(dirtyCount, dirtyLines.Length);
            int i = 0;
            while (i < max)
            {
                int y = dirtyLines[i];
                if (y < 0 || y >= _height)
                {
                    i++;
                    continue;
                }

                int startY = y;
                int endY = y + 1;
                i++;

                while (i < max)
                {
                    int nextY = dirtyLines[i];
                    if (nextY != endY || nextY < 0 || nextY >= _height)
                    {
                        break;
                    }

                    endY++;
                    i++;
                }

                var rect = new Int32Rect(0, startY, _width, endY - startY);
                int offset = y * _width;
                _bitmap.WritePixels(rect, frameBuffer, _stride, offset);
            }
        }
    }
}
