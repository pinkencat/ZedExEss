using System;using ZedExEss.Spectrum.Core; using ZedExEss.Spectrum.Memory;

namespace ZedExEss.Spectrum.Video
{
    /// <summary>
    /// Beam-position renderer for the ULA display, including border colour changes and multicolour attribute timing.
    /// </summary>
    /// <remarks>
    /// Rendering advances in CPU T-state order rather than by completed scanline.
    /// Pixel and attribute bytes are latched when the emulated beam fetches them, so
    /// writes made later on the same line cannot retroactively change earlier pixels.
    /// </remarks>
    public sealed class SpectrumUlaRenderer
    {
        private const int FlashToggleMask = 0x20;
        private const int PixelBytesPerLine = 32;
        private readonly SpectrumUlaTiming _timing;
        private readonly SpectrumMemory _screen;
        private readonly int[] _frameBuffer;
        private readonly int[] _pixelRowOffsets;
        private readonly int[] _attrRowOffsets;
        private readonly int[] _pixelCache;
        private readonly int[] _palette;
        private readonly byte[] _latchedPixelBytes;
        private readonly byte[] _latchedAttributes;
        // One latch bit per display byte distinguishes a fetched zero byte from a
        // byte that has not yet been fetched in this frame.
        private readonly bool[] _latchedDisplayBytes;
        private readonly int[] _dirtyLines;
        private readonly int[] _dirtyLineGenerations;
        private int _beamTstate;
        private int _frameCounter;
        private int _dirtyLineCount;
        private int _dirtyGeneration = 1;
        private bool _fullFrameDirty;
        private bool _flashPhase;
        private byte _borderColorIndex;

        public SpectrumUlaRenderer(SpectrumModel model, SpectrumMemory screenMemory)
        {
            _timing = SpectrumUlaTiming.ForModel(model);
            _screen = screenMemory ?? throw new ArgumentNullException(nameof(screenMemory));
            _frameBuffer = new int[_timing.FrameWidth * _timing.FrameHeight];
            _palette = BuildPalette();
            _pixelCache = BuildPixelCache(_palette);
            _pixelRowOffsets = BuildPixelRowOffsets(_timing.DisplayLines);
            _attrRowOffsets = BuildAttributeRowOffsets(_timing.DisplayLines);
            _latchedPixelBytes = new byte[_timing.DisplayLines * PixelBytesPerLine];
            _latchedAttributes = new byte[_latchedPixelBytes.Length];
            _latchedDisplayBytes = new bool[_latchedPixelBytes.Length];
            _dirtyLines = new int[_timing.FrameHeight];
            _dirtyLineGenerations = new int[_timing.FrameHeight];
        }

        public int FrameWidth => _timing.FrameWidth;
        public int FrameHeight => _timing.FrameHeight;
        public int[] FrameBuffer => _frameBuffer;
        public int DirtyLineCount => _fullFrameDirty ? _timing.FrameHeight : _dirtyLineCount;
        public bool FullFrameDirty => _fullFrameDirty;
        public int TstatesUntilFrameEnd => _timing.TstatesPerFrame - _beamTstate;
        public int InterruptPulseTstates => _timing.InterruptPulseTstates;
        public int InterruptDelayTstates => _timing.InterruptDelayTstates;
        public int InterruptStartOffsetTstates => _timing.InterruptStartOffsetTstates;
        public bool RenderEnabled { get; set; } = true;

        public byte BorderColorIndex
        {
            get => _borderColorIndex;
            set => _borderColorIndex = (byte)(value & 0x07);
        }
        public int CopyDirtyLines(int[] destination)
        {
            ArgumentNullException.ThrowIfNull(destination);

            if (_fullFrameDirty)
            {
                return _timing.FrameHeight;
            }

            int count = Math.Min(_dirtyLineCount, destination.Length);
            Array.Copy(_dirtyLines, destination, count);
            return count;
        }
        public void ClearDirtyLines()
        {
            _dirtyLineCount = 0;
            _fullFrameDirty = false;
            // Generation stamps make clearing O(1). The backing array is only cleared on wrap.
            AdvanceDirtyGeneration();
        }

        /// <summary>
        /// Advances the ULA beam by the supplied T-states and renders every visible portion crossed.
        /// </summary>
        public bool Advance(int tstates)
        {
            return Advance(tstates, RenderEnabled);
        }

        /// <summary>
        /// Advances beam/frame timing, optionally suppressing pixel generation for fast-forward paths.
        /// </summary>
        public bool Advance(int tstates, bool renderPixels)
        {
            if (tstates <= 0)
            {
                return false;
            }

            if (!renderPixels)
            {
                // Even with rendering disabled, beam/frame/flash timing must continue to advance.
                int newBeam = _beamTstate + tstates;
                if (newBeam >= _timing.TstatesPerFrame)
                {
                    _beamTstate = newBeam - _timing.TstatesPerFrame;
                    _frameCounter++;
                    _flashPhase = (_frameCounter & FlashToggleMask) != 0;
                    ClearDisplayLatches();
                    return true;
                }

                _beamTstate = newBeam;
                return false;
            }

            bool frameCompleted = false;
            int remaining = tstates;

            while (remaining > 0)
            {
                int line = _beamTstate / _timing.TstatesPerLine;
                int tstateInLine = _beamTstate - (line * _timing.TstatesPerLine);
                int run = Math.Min(remaining, _timing.TstatesPerLine - tstateInLine);

                RenderLineSegment(line, tstateInLine, run);

                _beamTstate += run;
                remaining -= run;

                if (_beamTstate >= _timing.TstatesPerFrame)
                {
                    _beamTstate = 0;
                    frameCompleted = true;
                    _frameCounter++;
                    _flashPhase = (_frameCounter & FlashToggleMask) != 0;
                    ClearDisplayLatches();
                }
            }

            return frameCompleted;
        }
        private void RenderLineSegment(int line, int tstateStart, int tstates)
        {
            if (line < 0 || line >= _timing.LinesPerFrame)
            {
                return;
            }

            int originalStart = tstateStart;
            int originalEnd = tstateStart + tstates;
            // Fetches are latched before pixels are drawn so writes later in the segment cannot
            // retroactively change bytes the ULA has already fetched.
            LatchDisplayFetches(line, originalStart, originalEnd);

            int visibleStart = _timing.VisibleStartTstate;
            int visibleEnd = visibleStart + _timing.VisibleLineTstates;
            int segmentEnd = originalEnd;
            if (line < _timing.VisibleFirstLine ||
                line >= _timing.VisibleFirstLine + _timing.FrameHeight ||
                segmentEnd <= visibleStart ||
                tstateStart >= visibleEnd)
            {
                return;
            }

            tstateStart = Math.Max(tstateStart, visibleStart);
            segmentEnd = Math.Min(segmentEnd, visibleEnd);
            int outputLine = line - _timing.VisibleFirstLine;
            int lineOffset = outputLine * _timing.FrameWidth;
            int clippedTstates = segmentEnd - tstateStart;
            if (clippedTstates <= 0)
            {
                return;
            }

            if (line < _timing.FirstDisplayLine || line >= _timing.FirstDisplayLine + _timing.DisplayLines)
            {
                FillBorder(outputLine, lineOffset, tstateStart, clippedTstates);
                return;
            }

            int displayStart = _timing.DisplayStartTstate;
            int displayEnd = displayStart + _timing.DisplayTstates;

            int leftEnd = Math.Min(segmentEnd, displayStart);
            if (leftEnd > tstateStart)
            {
                FillBorder(outputLine, lineOffset, tstateStart, leftEnd - tstateStart);
            }

            int displaySegmentStart = Math.Max(tstateStart, displayStart);
            int displaySegmentEnd = Math.Min(segmentEnd, displayEnd);
            if (displaySegmentEnd > displaySegmentStart)
            {
                RenderDisplaySegment(line, outputLine, lineOffset, displaySegmentStart, displaySegmentEnd - displaySegmentStart);
            }

            int rightStart = Math.Max(tstateStart, displayEnd);
            if (segmentEnd > rightStart)
            {
                FillBorder(outputLine, lineOffset, rightStart, segmentEnd - rightStart);
            }

        }
        private void FillBorder(int outputLine, int lineOffset, int tstateStart, int tstates)
        {
            // Two pixels are output per T-state in the renderer's 2x horizontal representation.
            int pixelStart = (tstateStart - _timing.VisibleStartTstate) * 2;
            int pixelCount = tstates * 2;
            if (pixelCount <= 0)
            {
                return;
            }

            int startIndex = lineOffset + pixelStart;
            if (startIndex < 0 || startIndex >= _frameBuffer.Length)
            {
                return;
            }

            int maxCount = Math.Min(_frameBuffer.Length - startIndex, _timing.FrameWidth - pixelStart);
            if (pixelCount > maxCount)
            {
                pixelCount = maxCount;
            }
            int color = _palette[_borderColorIndex];
            FillFrameSpan(outputLine, startIndex, pixelCount, color);
        }
        private void RenderDisplaySegment(int line, int outputLine, int lineOffset, int tstateStart, int tstates)
        {
            // Display bytes are still rendered in 8-pixel chunks, but the segment may start or end
            // mid-byte when CPU writes or border changes split the beam position.
            int displayLine = line - _timing.FirstDisplayLine;
            int startPixel = (tstateStart - _timing.DisplayStartTstate) * 2;
            int pixelCount = tstates * 2;
            int endPixel = startPixel + pixelCount;

            int byteStart = startPixel >> 3;
            int byteEnd = (endPixel - 1) >> 3;
            int pixelBaseOffset = lineOffset + _timing.BorderLeftPixels;
            int pixelRowOffset = _pixelRowOffsets[displayLine];
            int attrRowOffset = _attrRowOffsets[displayLine];

            for (int byteIndex = byteStart; byteIndex <= byteEnd; byteIndex++)
            {
                int bytePixelStart = byteIndex << 3;
                int copyStart = 0;
                int copyLength = 8;

                if (byteIndex == byteStart && startPixel > bytePixelStart)
                {
                    copyStart = startPixel - bytePixelStart;
                    copyLength -= copyStart;
                }

                int bytePixelEnd = bytePixelStart + 8;
                if (byteIndex == byteEnd && endPixel < bytePixelEnd)
                {
                    copyLength = endPixel - bytePixelStart - copyStart;
                }

                if (copyLength <= 0)
                {
                    continue;
                }

                ushort pixelAddress = (ushort)(0x4000 + pixelRowOffset + byteIndex);
                ushort attrAddress = (ushort)(0x5800 + attrRowOffset + byteIndex);
                int latchIndex = displayLine * PixelBytesPerLine + byteIndex;

                if (!_latchedDisplayBytes[latchIndex])
                {
                    // If the beam jumps straight into the visible byte without a prior latch pass,
                    // fetch on demand at the current ULA-visible memory state.
                    LatchDisplayByte(latchIndex, pixelAddress, attrAddress);
                }

                byte pixelByte = _latchedPixelBytes[latchIndex];
                byte attr = _latchedAttributes[latchIndex];

                int pairIndex = GetPairIndex(attr);
                int cacheIndex = ((pairIndex << 8) | pixelByte) * 8 + copyStart;
                int destIndex = pixelBaseOffset + bytePixelStart + copyStart;

                CopyFrameSpan(outputLine, cacheIndex, destIndex, copyLength);
            }
        }
        private void FillFrameSpan(int line, int startIndex, int count, int color)
        {
            if (count <= 0)
            {
                return;
            }

            if (!IsDirtyLine(line))
            {
                int end = startIndex + count;
                for (int i = startIndex; i < end; i++)
                {
                    if (_frameBuffer[i] != color)
                    {
                        MarkDirtyLine(line);
                        break;
                    }
                }
            }

            Array.Fill(_frameBuffer, color, startIndex, count);
        }
        private void CopyFrameSpan(int line, int sourceIndex, int destIndex, int count)
        {
            if (count <= 0)
            {
                return;
            }

            if (!IsDirtyLine(line))
            {
                int sourceEnd = sourceIndex + count;
                for (int src = sourceIndex, dst = destIndex; src < sourceEnd; src++, dst++)
                {
                    if (_pixelCache[src] != _frameBuffer[dst])
                    {
                        MarkDirtyLine(line);
                        break;
                    }
                }
            }

            Array.Copy(_pixelCache, sourceIndex, _frameBuffer, destIndex, count);
        }
        private void MarkDirtyLine(int line)
        {
            if ((uint)line >= (uint)_dirtyLineGenerations.Length || IsDirtyLine(line))
            {
                return;
            }

            // Keep line numbers compact for WPF WritePixels. Overflow promotes to full-frame dirty.
            _dirtyLineGenerations[line] = _dirtyGeneration;
            if (_dirtyLineCount < _dirtyLines.Length)
            {
                _dirtyLines[_dirtyLineCount] = line;
            }

            _dirtyLineCount++;
            if (_dirtyLineCount >= _dirtyLines.Length)
            {
                _fullFrameDirty = true;
            }
        }
        private bool IsDirtyLine(int line)
        {
            return _dirtyLineGenerations[line] == _dirtyGeneration;
        }
        private void AdvanceDirtyGeneration()
        {
            _dirtyGeneration++;
            if (_dirtyGeneration != int.MaxValue)
            {
                return;
            }

            Array.Clear(_dirtyLineGenerations, 0, _dirtyLineGenerations.Length);
            _dirtyGeneration = 1;
        }
        private void LatchDisplayFetches(int line, int tstateStart, int segmentEnd)
        {
            if (line < _timing.FirstDisplayLine || line >= _timing.FirstDisplayLine + _timing.DisplayLines)
            {
                return;
            }

            if (segmentEnd <= tstateStart)
            {
                return;
            }

            int displayLine = line - _timing.FirstDisplayLine;
            int fetchBase = _timing.DisplayStartTstate - _timing.DisplayFetchAdvanceTstates;
            // The ULA fetches one pixel/attribute byte pair every four T-states, ahead of
            // the point where those pixels appear. This is the key multicolour timing path.
            int firstByte = DivCeiling(tstateStart - fetchBase, 4);
            int lastByte = (segmentEnd - 1 - fetchBase) / 4;

            if (firstByte < 0)
            {
                firstByte = 0;
            }

            if (lastByte >= PixelBytesPerLine)
            {
                lastByte = PixelBytesPerLine - 1;
            }

            if (firstByte > lastByte)
            {
                return;
            }

            int pixelRowOffset = _pixelRowOffsets[displayLine];
            int attrRowOffset = _attrRowOffsets[displayLine];
            int latchBase = displayLine * PixelBytesPerLine;

            for (int byteIndex = firstByte; byteIndex <= lastByte; byteIndex++)
            {
                int latchIndex = latchBase + byteIndex;
                if (_latchedDisplayBytes[latchIndex])
                {
                    continue;
                }

                ushort pixelAddress = (ushort)(0x4000 + pixelRowOffset + byteIndex);
                ushort attrAddress = (ushort)(0x5800 + attrRowOffset + byteIndex);
                LatchDisplayByte(latchIndex, pixelAddress, attrAddress);
            }
        }
        private void LatchDisplayByte(int latchIndex, ushort pixelAddress, ushort attrAddress)
        {
            // Read through the screen shadow so delayed CPU writes only become visible once
            // SpectrumMemory has flushed them at the scheduled T-state.
            _latchedPixelBytes[latchIndex] = _screen.ReadScreen(pixelAddress);
            _latchedAttributes[latchIndex] = _screen.ReadScreen(attrAddress);
            _latchedDisplayBytes[latchIndex] = true;
        }
        private static int DivCeiling(int value, int divisor)
        {
            if (value <= 0)
            {
                return 0;
            }

            return (value + divisor - 1) / divisor;
        }
        private void ClearDisplayLatches()
        {
            Array.Clear(_latchedDisplayBytes, 0, _latchedDisplayBytes.Length);
        }
        private int GetPairIndex(byte attr)
        {
            int ink = attr & 0x07;
            int paper = (attr >> 3) & 0x07;
            bool bright = (attr & 0x40) != 0;
            bool flash = (attr & 0x80) != 0;

            if (bright)
            {
                ink |= 0x08;
                paper |= 0x08;
            }

            if (flash && _flashPhase)
            {
                (paper, ink) = (ink, paper);
            }

            return (paper << 4) | ink;
        }
        private static int[] BuildPixelRowOffsets(int displayLines)
        {
            var offsets = new int[displayLines];
            for (int y = 0; y < displayLines; y++)
            {
                offsets[y] = ((y & 0xC0) << 5) | ((y & 0x07) << 8) | ((y & 0x38) << 2);
            }

            return offsets;
        }
        private static int[] BuildAttributeRowOffsets(int displayLines)
        {
            var offsets = new int[displayLines];
            for (int y = 0; y < displayLines; y++)
            {
                offsets[y] = (y >> 3) * PixelBytesPerLine;
            }

            return offsets;
        }
        private static int[] BuildPalette()
        {
            var palette = new int[16];

            palette[0] = MakeColor(0x00, 0x00, 0x00);
            palette[1] = MakeColor(0x00, 0x00, 0xC0);
            palette[2] = MakeColor(0xC0, 0x00, 0x00);
            palette[3] = MakeColor(0xC0, 0x00, 0xC0);
            palette[4] = MakeColor(0x00, 0xC0, 0x00);
            palette[5] = MakeColor(0x00, 0xC0, 0xC0);
            palette[6] = MakeColor(0xC0, 0xC0, 0x00);
            palette[7] = MakeColor(0xC0, 0xC0, 0xC0);

            palette[8] = MakeColor(0x00, 0x00, 0x00);
            palette[9] = MakeColor(0x00, 0x00, 0xFF);
            palette[10] = MakeColor(0xFF, 0x00, 0x00);
            palette[11] = MakeColor(0xFF, 0x00, 0xFF);
            palette[12] = MakeColor(0x00, 0xFF, 0x00);
            palette[13] = MakeColor(0x00, 0xFF, 0xFF);
            palette[14] = MakeColor(0xFF, 0xFF, 0x00);
            palette[15] = MakeColor(0xFF, 0xFF, 0xFF);

            return palette;
        }
        private static int[] BuildPixelCache(int[] palette)
        {
            var cache = new int[256 * 256 * 8];

            for (int pairIndex = 0; pairIndex < 256; pairIndex++)
            {
                int ink = pairIndex & 0x0F;
                int paper = (pairIndex >> 4) & 0x0F;

                for (int pixelByte = 0; pixelByte < 256; pixelByte++)
                {
                    int offset = ((pairIndex << 8) | pixelByte) * 8;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        bool set = (pixelByte & (0x80 >> bit)) != 0;
                        cache[offset + bit] = set ? palette[ink] : palette[paper];
                    }
                }
            }

            return cache;
        }
        private static int MakeColor(byte r, byte g, byte b)
        {
            return (0xFF << 24) | (r << 16) | (g << 8) | b;
        }
    }
}
