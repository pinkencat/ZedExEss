using System;

namespace ZedExEss.Spectrum.Audio
{
    /// <summary>Toolkit-neutral reduction of a captured window to a drawable envelope.</summary>
    /// <remarks>
    /// A trace never needs more geometry than the control has pixel columns. Both hosts
    /// used to hand every captured sample to the drawing layer, which meant a thousand
    /// segments per trace per refresh for detail no display could show. Reducing to one
    /// min/max pair per column keeps every transient visible - a spike between two
    /// columns still widens that column's extent - at a cost fixed by the control width.
    /// </remarks>
    public static class AudioScopeTrace
    {
        /// <summary>The widest envelope any single reduction will produce.</summary>
        public const int MaximumColumns = 4096;

        /// <summary>
        /// Reduces <paramref name="samples"/> to at most <paramref name="columns"/>
        /// min/max pairs. Returns the number of columns written.
        /// </summary>
        public static int Reduce(
            ReadOnlySpan<short> samples,
            int columns,
            Span<short> minima,
            Span<short> maxima)
        {
            int limit = Math.Min(columns, Math.Min(minima.Length, maxima.Length));
            if (samples.Length == 0 || limit <= 0)
            {
                return 0;
            }

            int produced = Math.Min(limit, samples.Length);
            for (int column = 0; column < produced; column++)
            {
                // Spread the remainder evenly instead of leaving a short final column.
                int start = (int)((long)column * samples.Length / produced);
                int end = (int)((long)(column + 1) * samples.Length / produced);
                if (end <= start)
                {
                    end = start + 1;
                }

                short low = samples[start];
                short high = low;
                for (int i = start + 1; i < end; i++)
                {
                    short value = samples[i];
                    if (value < low)
                    {
                        low = value;
                    }
                    else if (value > high)
                    {
                        high = value;
                    }
                }

                minima[column] = low;
                maxima[column] = high;
            }

            return produced;
        }

        /// <summary>
        /// Returns the vertical scale for one trace, in output units per sample unit.
        /// </summary>
        /// <remarks>
        /// <paramref name="floor"/> stops a silent channel from being amplified into
        /// visible noise, which is what auto-scaling on its own would do.
        /// </remarks>
        public static double GetVerticalScale(
            ReadOnlySpan<short> minima,
            ReadOnlySpan<short> maxima,
            int columns,
            double halfHeight,
            int floor = 512)
        {
            int peak = floor;
            for (int column = 0; column < columns; column++)
            {
                int low = Math.Abs((int)minima[column]);
                if (low > peak)
                {
                    peak = low;
                }

                int high = Math.Abs((int)maxima[column]);
                if (high > peak)
                {
                    peak = high;
                }
            }

            return halfHeight / peak;
        }
    }
}
