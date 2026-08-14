namespace ZedExEss.Spectrum.Interface1;

/// <summary>
/// Host-neutral byte endpoint connected to the Interface 1 RS232 socket.
/// </summary>
/// <remarks>
/// The Interface 1 device performs the ULA's bit framing. Implementations therefore
/// exchange complete bytes and modem-control state rather than emulated port samples.
/// A desktop host can back this contract with a file, pipe, pseudo-terminal or socket.
/// </remarks>
public interface ISpectrumInterface1Rs232Endpoint
{
    /// <summary>DTR input presented by the attached data-terminal equipment.</summary>
    bool DataTerminalReady { get; }

    /// <summary>Receives the Spectrum's CTS output level.</summary>
    void SetClearToSend(bool asserted);

    /// <summary>Returns the next byte waiting to enter the Spectrum.</summary>
    bool TryReadByte(out byte value);

    /// <summary>Accepts one completely decoded byte transmitted by the Spectrum.</summary>
    void WriteByte(byte value);
}

/// <summary>
/// Thread-safe in-memory RS232 endpoint useful for host adapters and deterministic tests.
/// </summary>
public sealed class SpectrumInterface1Rs232Buffer : ISpectrumInterface1Rs232Endpoint
{
    private readonly object _sync = new();
    private readonly Queue<byte> _received = new();
    private readonly Queue<byte> _transmitted = new();
    private bool _dataTerminalReady = true;
    private bool _clearToSend;

    public bool DataTerminalReady
    {
        get
        {
            lock (_sync)
            {
                return _dataTerminalReady;
            }
        }
        set
        {
            lock (_sync)
            {
                _dataTerminalReady = value;
            }
        }
    }

    public bool ClearToSend
    {
        get
        {
            lock (_sync)
            {
                return _clearToSend;
            }
        }
    }

    public void SetClearToSend(bool asserted)
    {
        lock (_sync)
        {
            _clearToSend = asserted;
        }
    }

    public void QueueReceived(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            foreach (byte value in data)
            {
                _received.Enqueue(value);
            }
        }
    }

    public bool TryReadByte(out byte value)
    {
        lock (_sync)
        {
            return _received.TryDequeue(out value);
        }
    }

    public void WriteByte(byte value)
    {
        lock (_sync)
        {
            _transmitted.Enqueue(value);
        }
    }

    public bool TryDequeueTransmitted(out byte value)
    {
        lock (_sync)
        {
            return _transmitted.TryDequeue(out value);
        }
    }
}
