namespace ZedExEss.Spectrum.Abstractions
{
    /// <summary>
    /// Pulse-length hints consumed by a semantic tape-loader accelerator.
    /// These deliberately mirror libspectrum's LENGTH_SHORT/LENGTH_LONG flags:
    /// an interval with neither flag must be allowed to run normally.
    /// </summary>
    [Flags]
    public enum TapeAccelerationPulseFlags : byte
    {
        None = 0,
        LengthShort = 1,
        LengthLong = 2
    }

    /// <summary>
    /// Immutable snapshot taken when the CPU claims a recognised EAR read.
    /// The pulse index lets the accelerator cheaply detect an ordinary edge
    /// occurring between instruction decode and the ULA sampling point.
    /// </summary>
    public readonly record struct TapeSemanticReadState(
        int PulseIndex,
        TapeAccelerationPulseFlags Flags,
        bool EarHigh,
        int NextEdgeDelta);

    /// <summary>
    /// State transition produced by one semantic edge advance. Returning all
    /// classifier and EAR information here avoids a chain of virtual queries on
    /// the hottest part of accelerated custom-loader playback.
    /// </summary>
    public readonly record struct TapeSemanticEdgeResult(
        int ElapsedTstates,
        int SourcePulseIndex,
        int DestinationPulseIndex,
        TapeAccelerationPulseFlags SourceFlags,
        TapeAccelerationPulseFlags DestinationFlags,
        bool EarHighBefore,
        bool EarHighAfter,
        bool IsPlaying);

    /// <summary>
    /// Timestamped tape edge stream used by normal playback and edge acceleration.
    /// </summary>
    public interface ITapeEdgeSource
    {
        bool IsPlaying { get; }
        bool EdgeSeen { get; }
        int CurrentBlockIndex { get; }
        int CurrentPulseIndex { get; }
        int PeekNextEdgeDelta();
        int AdvanceToNextEdge(bool skipTime);
        void ClearEdgeSeen();
        bool TryGetDataPulseTimings(out int shortPulse, out int longPulse);
        bool TryGetCurrentPulseInfo(out int tstates, out bool isData, out bool isLong);
        bool TryGetCurrentAccelerationFlags(out TapeAccelerationPulseFlags flags);
        bool TryGetSemanticReadState(out TapeSemanticReadState state);
        bool TryAdvanceSemanticEdge(TapeSemanticReadState expectedState, out TapeSemanticEdgeResult result);
        bool TryGetPreviousPulseInfo(out int tstates, out bool isData);
        bool TryGetLastEdgeInfo(out int tstates, out bool isData, out bool isLong, out bool fromSemanticAcceleration);

        /// <summary>
        /// Marks an edge generated directly by a semantic loader accelerator.
        /// Reaching an ordinary scheduled edge by skipping polling-loop time must
        /// not set this marker.
        /// </summary>
        void MarkNextEdgeSemanticallyAccelerated();
    }
}
