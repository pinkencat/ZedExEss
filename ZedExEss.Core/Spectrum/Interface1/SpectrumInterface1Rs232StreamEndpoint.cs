using System.Collections.Concurrent;

namespace ZedExEss.Spectrum.Interface1;

/// <summary>
/// Bridges the byte-oriented Interface 1 RS232 socket to host streams.
/// </summary>
/// <remarks>
/// Receive streams are drained on a background task into a lock-free queue, so an
/// emulated F7h port read never waits for host file, pipe or pseudo-terminal I/O.
/// Transmit writes occur only after the Interface 1 ULA has decoded a complete byte;
/// this keeps stream calls out of the per-bit port hot path.
/// </remarks>
public sealed class SpectrumInterface1Rs232StreamEndpoint : ISpectrumInterface1Rs232Endpoint, IDisposable
{
    private sealed class ReceiveAttachment(Stream stream, bool ownsStream, string? name)
    {
        public Stream Stream { get; } = stream;
        public bool OwnsStream { get; } = ownsStream;
        public string? Name { get; } = name;
        public CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed class TransmitAttachment(Stream stream, bool ownsStream, string? name)
    {
        public Stream Stream { get; } = stream;
        public bool OwnsStream { get; } = ownsStream;
        public string? Name { get; } = name;
    }

    private readonly object _sync = new();
    private readonly object _transmitSync = new();
    private readonly ConcurrentQueue<byte> _received = new();
    private ReceiveAttachment? _receive;
    private TransmitAttachment? _transmit;
    private bool _clearToSend;
    private bool _disposed;

    /// <summary>Raised when an attached host stream fails and is detached.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>
    /// Raised when the current receive stream reaches an orderly end-of-stream.
    /// File hosts may simply remain attached at EOF; reconnecting pipe/device hosts
    /// use this notification to establish a replacement stream.
    /// </summary>
    public event Action<string?>? ReceiveEnded;

    /// <summary>The display name of the current receive stream, if any.</summary>
    public string? ReceiveName
    {
        get
        {
            lock (_sync)
            {
                return _receive?.Name;
            }
        }
    }

    /// <summary>The display name of the current transmit stream, if any.</summary>
    public string? TransmitName
    {
        get
        {
            lock (_sync)
            {
                return _transmit?.Name;
            }
        }
    }

    public bool ReceiveAttached
    {
        get
        {
            lock (_sync)
            {
                return _receive != null;
            }
        }
    }

    public bool TransmitAttached
    {
        get
        {
            lock (_sync)
            {
                return _transmit != null;
            }
        }
    }

    public bool DataTerminalReady
    {
        get
        {
            lock (_sync)
            {
                return _receive != null || _transmit != null;
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

    /// <summary>Opens a binary file whose bytes will be received by the Spectrum.</summary>
    public void AttachReceiveFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        AttachReceive(stream, ownsStream: true, fullPath);
    }

    /// <summary>Creates a binary file which receives bytes sent by the Spectrum.</summary>
    public void AttachTransmitFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        AttachTransmit(stream, ownsStream: true, fullPath);
    }

    /// <summary>
    /// Connects an arbitrary readable stream. Pipes and pseudo-terminals can use this
    /// entry point without introducing platform-specific dependencies into the core.
    /// </summary>
    public void AttachReceive(Stream stream, bool ownsStream = false, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The RS232 receive stream is not readable.", nameof(stream));
        }

        var replacement = new ReceiveAttachment(stream, ownsStream, name);
        ReceiveAttachment? previous;
        lock (_sync)
        {
            ThrowIfDisposed();
            previous = _receive;
            _receive = replacement;
            ClearReceiveQueue();
        }

        CloseReceiveAttachment(previous);
        _ = Task.Run(() => PumpReceiveAsync(replacement));
    }

    /// <summary>Connects an arbitrary writable stream for bytes sent by the Spectrum.</summary>
    public void AttachTransmit(Stream stream, bool ownsStream = false, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("The RS232 transmit stream is not writable.", nameof(stream));
        }

        var replacement = new TransmitAttachment(stream, ownsStream, name);
        TransmitAttachment? previous;
        lock (_sync)
        {
            ThrowIfDisposed();
            previous = _transmit;
            _transmit = replacement;
        }

        CloseTransmitAttachment(previous);
    }

    public void DetachReceive()
    {
        ReceiveAttachment? attachment;
        lock (_sync)
        {
            attachment = _receive;
            _receive = null;
            ClearReceiveQueue();
        }

        CloseReceiveAttachment(attachment);
    }

    internal void DetachReceiveIfNamed(string name)
    {
        ReceiveAttachment? attachment = null;
        lock (_sync)
        {
            if (string.Equals(_receive?.Name, name, StringComparison.Ordinal))
            {
                attachment = _receive;
                _receive = null;
                ClearReceiveQueue();
            }
        }

        CloseReceiveAttachment(attachment);
    }

    public void DetachTransmit()
    {
        TransmitAttachment? attachment;
        lock (_sync)
        {
            attachment = _transmit;
            _transmit = null;
        }

        CloseTransmitAttachment(attachment);
    }

    internal void DetachTransmitIfNamed(string name)
    {
        TransmitAttachment? attachment = null;
        lock (_sync)
        {
            if (string.Equals(_transmit?.Name, name, StringComparison.Ordinal))
            {
                attachment = _transmit;
                _transmit = null;
            }
        }

        CloseTransmitAttachment(attachment);
    }

    public bool TryReadByte(out byte value) => _received.TryDequeue(out value);

    public void WriteByte(byte value)
    {
        TransmitAttachment? attachment;
        lock (_sync)
        {
            attachment = _transmit;
        }

        if (attachment == null)
        {
            return;
        }

        try
        {
            lock (_transmitSync)
            {
                attachment.Stream.WriteByte(value);
                attachment.Stream.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            RemoveFaultedTransmit(attachment, ex);
        }
    }

    public void Dispose()
    {
        ReceiveAttachment? receive;
        TransmitAttachment? transmit;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            receive = _receive;
            transmit = _transmit;
            _receive = null;
            _transmit = null;
            ClearReceiveQueue();
        }

        CloseReceiveAttachment(receive);
        CloseTransmitAttachment(transmit);
    }

    private async Task PumpReceiveAsync(ReceiveAttachment attachment)
    {
        byte[] buffer = new byte[4096];
        try
        {
            while (true)
            {
                int count = await attachment.Stream
                    .ReadAsync(buffer.AsMemory(), attachment.Cancellation.Token)
                    .ConfigureAwait(false);
                if (count == 0)
                {
                    bool current;
                    lock (_sync)
                    {
                        current = ReferenceEquals(_receive, attachment);
                    }

                    if (current)
                    {
                        ReceiveEnded?.Invoke(attachment.Name);
                    }

                    return;
                }

                lock (_sync)
                {
                    // A cancelled read can still complete while its replacement is being
                    // attached. Never allow bytes from that old stream into the new session.
                    if (!ReferenceEquals(_receive, attachment))
                    {
                        return;
                    }

                    for (int index = 0; index < count; index++)
                    {
                        _received.Enqueue(buffer[index]);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (attachment.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            RemoveFaultedReceive(attachment, ex);
        }
    }

    private void RemoveFaultedReceive(ReceiveAttachment attachment, Exception exception)
    {
        bool removed;
        lock (_sync)
        {
            removed = ReferenceEquals(_receive, attachment);
            if (removed)
            {
                _receive = null;
                ClearReceiveQueue();
            }
        }

        CloseReceiveAttachment(attachment);
        if (removed)
        {
            Faulted?.Invoke(exception);
        }
    }

    private void RemoveFaultedTransmit(TransmitAttachment attachment, Exception exception)
    {
        bool removed;
        lock (_sync)
        {
            removed = ReferenceEquals(_transmit, attachment);
            if (removed)
            {
                _transmit = null;
            }
        }

        CloseTransmitAttachment(attachment);
        if (removed)
        {
            Faulted?.Invoke(exception);
        }
    }

    private void ClearReceiveQueue()
    {
        while (_received.TryDequeue(out _))
        {
        }
    }

    private static void CloseReceiveAttachment(ReceiveAttachment? attachment)
    {
        if (attachment == null)
        {
            return;
        }

        attachment.Cancellation.Cancel();
        if (attachment.OwnsStream)
        {
            attachment.Stream.Dispose();
        }

        attachment.Cancellation.Dispose();
    }

    private static void CloseTransmitAttachment(TransmitAttachment? attachment)
    {
        if (attachment?.OwnsStream == true)
        {
            attachment.Stream.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
