namespace ZedExEss.Spectrum.Interface1;

/// <summary>A change of the physical ZX Net wire level at an emulated T-state.</summary>
public readonly record struct SpectrumInterface1NetworkTransition(
    ulong Tstate,
    long SourceStationId,
    bool LineHigh);

/// <summary>
/// A change made by one station before the outputs of all stations are combined.
/// Bridges carry these source transitions rather than the aggregate wire level so
/// simultaneous peers retain the real wired-OR behaviour at the receiving end.
/// </summary>
public readonly record struct SpectrumInterface1NetworkOutputTransition(
    ulong Tstate,
    long SourceStationId,
    bool DrivesHigh);

/// <summary>
/// Shared electrical model of the Interface 1's single bidirectional ZX Net wire.
/// </summary>
/// <remarks>
/// The cable rests at zero volts. An Interface 1 output stage can pull the wire high,
/// so simultaneous stations combine as a wired OR. The ULA output is inverted by the
/// Interface 1 transistor stage: writing zero to F7h drives the wire high, while writing
/// one releases it. This class deliberately models levels and timestamps only; packet,
/// scout and checksum handling remains the responsibility of the real Interface 1 ROM.
/// </remarks>
public sealed class SpectrumInterface1NetworkBus
{
    private const int TransitionHistoryCapacity = 4096;

    private readonly object _sync = new();
    private readonly Dictionary<long, StationState> _stations = [];
    private readonly Queue<SpectrumInterface1NetworkTransition> _transitions = new();
    private long _nextStationId;
    private bool _lineHigh;

    /// <summary>
    /// Raised only when a station changes its physical output. The callback is made
    /// after releasing the bus lock, allowing a transport to enqueue the transition
    /// without adding network or file I/O to the emulated port-write hot path.
    /// </summary>
    public event Action<SpectrumInterface1NetworkOutputTransition>? StationOutputChanged;

    /// <summary>Current physical wire level. False is the disconnected/idle level.</summary>
    public bool LineHigh
    {
        get
        {
            lock (_sync)
            {
                return _lineHigh;
            }
        }
    }

    public int StationCount
    {
        get
        {
            lock (_sync)
            {
                return _stations.Count;
            }
        }
    }

    /// <summary>Creates one electrical attachment to this shared wire.</summary>
    public SpectrumInterface1NetworkStation AttachStation(string? name = null)
    {
        lock (_sync)
        {
            long id = ++_nextStationId;
            _stations.Add(id, new StationState(name ?? $"Station {id}"));
            return new SpectrumInterface1NetworkStation(this, id, name ?? $"Station {id}");
        }
    }

    /// <summary>
    /// Returns the retained aggregate line transitions in chronological insertion order.
    /// The bounded history is intended for diagnostics and future transport bridges; it is
    /// not used to decode network packets.
    /// </summary>
    public IReadOnlyList<SpectrumInterface1NetworkTransition> CopyTransitions()
    {
        lock (_sync)
        {
            return _transitions.ToArray();
        }
    }

    internal bool Sample(long stationId, ulong tstate)
    {
        lock (_sync)
        {
            StationState station = GetStation(stationId);
            station.LastTstate = Math.Max(station.LastTstate, tstate);
            return SampleLineAt(tstate);
        }
    }

    /// <summary>
    /// Finds the first already-recorded transition after <paramref name="tstate"/>
    /// at which every station has released the wire. A missing result means that
    /// the line is still high at the newest point known to this process; it does
    /// not invent a pulse end which a peer has not generated yet.
    /// </summary>
    internal bool TryGetNextRestingTstate(long stationId, ulong tstate, out ulong restingTstate)
    {
        lock (_sync)
        {
            _ = GetStation(stationId);
            if (!SampleLineAt(tstate))
            {
                restingTstate = tstate;
                return true;
            }

            ulong cursor = tstate;
            while (TryGetNextOutputTransitionTstate(cursor, out ulong transitionAt))
            {
                if (!SampleLineAt(transitionAt))
                {
                    restingTstate = transitionAt;
                    return true;
                }

                cursor = transitionAt;
            }

            restingTstate = 0;
            return false;
        }
    }

    internal void SetOutput(long stationId, bool ulaOutputHigh, bool networkSelected, ulong tstate)
    {
        SpectrumInterface1NetworkOutputTransition? notification = null;
        lock (_sync)
        {
            StationState station = GetStation(stationId);
            station.LastTstate = Math.Max(station.LastTstate, tstate);

            // Q1/Q2 invert the ULA output. A zero written while COMMS DATA selects
            // the network therefore drives the physical wire high.
            bool drivesHigh = networkSelected && !ulaOutputHigh;
            if (station.DrivesHigh == drivesHigh)
            {
                return;
            }

            station.DrivesHigh = drivesHigh;
            station.RecordOutput(tstate, drivesHigh);
            UpdateAggregateLine(tstate, stationId);
            notification = new SpectrumInterface1NetworkOutputTransition(tstate, stationId, drivesHigh);
        }

        StationOutputChanged?.Invoke(notification.Value);
    }

    internal void Detach(long stationId)
    {
        lock (_sync)
        {
            if (!_stations.Remove(stationId, out StationState? station))
            {
                return;
            }

            if (station.DrivesHigh)
            {
                UpdateAggregateLine(station.LastTstate, stationId);
            }
        }
    }

    private StationState GetStation(long stationId)
    {
        return _stations.TryGetValue(stationId, out StationState? station)
            ? station
            : throw new ObjectDisposedException(nameof(SpectrumInterface1NetworkStation));
    }

    private bool SampleLineAt(ulong tstate)
    {
        foreach (StationState candidate in _stations.Values)
        {
            if (candidate.DrivesHighAt(tstate))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetNextOutputTransitionTstate(ulong after, out ulong transitionAt)
    {
        transitionAt = ulong.MaxValue;
        bool found = false;
        foreach (StationState station in _stations.Values)
        {
            if (station.TryGetNextTransitionTstate(after, out ulong candidate) && candidate < transitionAt)
            {
                transitionAt = candidate;
                found = true;
            }
        }

        if (!found)
        {
            transitionAt = 0;
        }

        return found;
    }

    private void UpdateAggregateLine(ulong tstate, long sourceStationId)
    {
        bool lineHigh = false;
        foreach (StationState station in _stations.Values)
        {
            lineHigh |= station.DrivesHigh;
        }

        if (_lineHigh == lineHigh)
        {
            return;
        }

        _lineHigh = lineHigh;
        if (_transitions.Count == TransitionHistoryCapacity)
        {
            _transitions.Dequeue();
        }

        _transitions.Enqueue(new SpectrumInterface1NetworkTransition(tstate, sourceStationId, lineHigh));
    }

    private sealed class StationState(string name)
    {
        // More than 4,000 electrical changes is roughly half a second of continuous
        // ZX Net traffic. Retaining twice that allows two lockstep CPUs to differ by
        // many scheduler quanta without growing indefinitely during long transfers.
        private const int OutputHistoryCapacity = 8192;
        private const int OutputHistoryTrimCount = 4096;
        private readonly List<OutputTransition> _outputHistory = [];

        public string Name { get; } = name;
        public bool DrivesHigh { get; set; }
        public ulong LastTstate { get; set; }

        public void RecordOutput(ulong tstate, bool drivesHigh)
        {
            if (_outputHistory.Count > 0 && tstate < _outputHistory[^1].Tstate)
            {
                throw new InvalidOperationException(
                    $"ZX Net station '{Name}' moved backwards from T-state {_outputHistory[^1].Tstate} to {tstate}.");
            }

            if (_outputHistory.Count > 0 && tstate == _outputHistory[^1].Tstate)
            {
                _outputHistory[^1] = new OutputTransition(tstate, drivesHigh);
            }
            else
            {
                _outputHistory.Add(new OutputTransition(tstate, drivesHigh));
            }

            if (_outputHistory.Count > OutputHistoryCapacity)
            {
                _outputHistory.RemoveRange(0, OutputHistoryTrimCount);
            }
        }

        public bool DrivesHighAt(ulong tstate)
        {
            int low = 0;
            int high = _outputHistory.Count - 1;
            int found = -1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                if (_outputHistory[middle].Tstate <= tstate)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found >= 0 && _outputHistory[found].DrivesHigh;
        }

        public bool TryGetNextTransitionTstate(ulong after, out ulong tstate)
        {
            int low = 0;
            int high = _outputHistory.Count - 1;
            int found = _outputHistory.Count;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                if (_outputHistory[middle].Tstate > after)
                {
                    found = middle;
                    high = middle - 1;
                }
                else
                {
                    low = middle + 1;
                }
            }

            if (found >= _outputHistory.Count)
            {
                tstate = 0;
                return false;
            }

            tstate = _outputHistory[found].Tstate;
            return true;
        }

        private readonly record struct OutputTransition(ulong Tstate, bool DrivesHigh);
    }
}

/// <summary>One Interface 1 attachment to a <see cref="SpectrumInterface1NetworkBus"/>.</summary>
public sealed class SpectrumInterface1NetworkStation : IDisposable
{
    private SpectrumInterface1NetworkBus? _bus;
    private readonly long _id;

    internal SpectrumInterface1NetworkStation(
        SpectrumInterface1NetworkBus bus,
        long id,
        string name)
    {
        _bus = bus;
        _id = id;
        Name = name;
    }

    public string Name { get; }
    public bool IsAttached => _bus != null;
    internal long Id => _id;

    public bool Sample(ulong tstate)
    {
        SpectrumInterface1NetworkBus bus = _bus
            ?? throw new ObjectDisposedException(nameof(SpectrumInterface1NetworkStation));
        return bus.Sample(_id, tstate);
    }

    /// <summary>
    /// Returns the first known time after <paramref name="tstate"/> at which the
    /// shared wire is low. This is used by the IF1 WAIT input; peers remain
    /// responsible for generating the transition which releases the CPU.
    /// </summary>
    public bool TryGetNextRestingTstate(ulong tstate, out ulong restingTstate)
    {
        SpectrumInterface1NetworkBus bus = _bus
            ?? throw new ObjectDisposedException(nameof(SpectrumInterface1NetworkStation));
        return bus.TryGetNextRestingTstate(_id, tstate, out restingTstate);
    }

    /// <summary>Applies the ULA F7h latch and COMMS DATA selector at a precise bus time.</summary>
    public void SetOutput(bool ulaOutputHigh, bool networkSelected, ulong tstate)
    {
        SpectrumInterface1NetworkBus bus = _bus
            ?? throw new ObjectDisposedException(nameof(SpectrumInterface1NetworkStation));
        bus.SetOutput(_id, ulaOutputHigh, networkSelected, tstate);
    }

    public void Dispose()
    {
        SpectrumInterface1NetworkBus? bus = Interlocked.Exchange(ref _bus, null);
        bus?.Detach(_id);
    }
}
