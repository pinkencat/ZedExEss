using System.IO.Pipes;

namespace ZedExEss.Spectrum.Interface1;

public enum SpectrumInterface1Rs232ConnectionKind
{
    NamedPipe,
    Device
}

public enum SpectrumInterface1Rs232ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

/// <summary>
/// Maintains a live duplex RS232 stream and reconnects it without involving the CPU thread.
/// </summary>
/// <remarks>
/// Named pipes use <see cref="NamedPipeClientStream"/> on every supported .NET desktop.
/// Device paths are opened as asynchronous duplex files, which covers Unix pseudo-terminals
/// such as /dev/pts/* and serial TTY devices without adding a platform-specific dependency.
/// </remarks>
public sealed class SpectrumInterface1Rs232ConnectionManager : IDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    private readonly object _sync = new();
    private readonly SpectrumInterface1Rs232StreamEndpoint _endpoint;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _connectionLost;
    private Stream? _activeStream;
    private SpectrumInterface1Rs232ConnectionState _state;
    private SpectrumInterface1Rs232ConnectionKind? _kind;
    private string? _target;
    private string? _lastError;
    private bool _disposed;

    public SpectrumInterface1Rs232ConnectionManager(SpectrumInterface1Rs232StreamEndpoint endpoint)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _endpoint.Faulted += OnEndpointFaulted;
        _endpoint.ReceiveEnded += OnReceiveEnded;
    }

    public event Action? StatusChanged;

    public SpectrumInterface1Rs232ConnectionState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public SpectrumInterface1Rs232ConnectionKind? Kind
    {
        get
        {
            lock (_sync)
            {
                return _kind;
            }
        }
    }

    public string? Target
    {
        get
        {
            lock (_sync)
            {
                return _target;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_sync)
            {
                return _lastError;
            }
        }
    }

    public bool IsActive => State != SpectrumInterface1Rs232ConnectionState.Disconnected;

    public void ConnectNamedPipe(string pipeName, bool autoReconnect = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        Start(SpectrumInterface1Rs232ConnectionKind.NamedPipe, pipeName.Trim(), autoReconnect);
    }

    public void ConnectDevice(string path, bool autoReconnect = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Start(SpectrumInterface1Rs232ConnectionKind.Device, Path.GetFullPath(path.Trim()), autoReconnect);
    }

    public void Disconnect()
    {
        CancellationTokenSource? cancellation;
        TaskCompletionSource? connectionLost;
        Stream? stream;
        lock (_sync)
        {
            cancellation = _cancellation;
            connectionLost = _connectionLost;
            stream = _activeStream;
            _cancellation = null;
            _connectionLost = null;
            _activeStream = null;
            _kind = null;
            _target = null;
            _lastError = null;
            _state = SpectrumInterface1Rs232ConnectionState.Disconnected;
        }

        cancellation?.Cancel();
        connectionLost?.TrySetResult();
        _endpoint.DetachReceive();
        _endpoint.DetachTransmit();
        stream?.Dispose();
        StatusChanged?.Invoke();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Disconnect();
        _endpoint.Faulted -= OnEndpointFaulted;
        _endpoint.ReceiveEnded -= OnReceiveEnded;
    }

    private void Start(
        SpectrumInterface1Rs232ConnectionKind kind,
        string target,
        bool autoReconnect)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        Disconnect();
        var cancellation = new CancellationTokenSource();
        lock (_sync)
        {
            _cancellation = cancellation;
            _kind = kind;
            _target = target;
            _lastError = null;
            _state = SpectrumInterface1Rs232ConnectionState.Connecting;
        }

        StatusChanged?.Invoke();
        _ = Task.Run(() => RunConnectionLoopAndDisposeAsync(kind, target, autoReconnect, cancellation));
    }

    private async Task RunConnectionLoopAndDisposeAsync(
        SpectrumInterface1Rs232ConnectionKind kind,
        string target,
        bool autoReconnect,
        CancellationTokenSource owner)
    {
        try
        {
            await RunConnectionLoopAsync(kind, target, autoReconnect, owner).ConfigureAwait(false);
        }
        finally
        {
            owner.Dispose();
        }
    }

    private async Task RunConnectionLoopAsync(
        SpectrumInterface1Rs232ConnectionKind kind,
        string target,
        bool autoReconnect,
        CancellationTokenSource owner)
    {
        bool firstAttempt = true;
        CancellationToken cancellationToken = owner.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            SetState(
                firstAttempt
                    ? SpectrumInterface1Rs232ConnectionState.Connecting
                    : SpectrumInterface1Rs232ConnectionState.Reconnecting,
                error: null,
                owner);

            Stream? stream = null;
            string attachmentName = $"rs232-live:{Guid.NewGuid():N}";
            try
            {
                stream = await OpenStreamAsync(kind, target, cancellationToken).ConfigureAwait(false);
                var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_sync)
                {
                    if (!ReferenceEquals(_cancellation, owner))
                    {
                        stream.Dispose();
                        return;
                    }

                    _activeStream = stream;
                    _connectionLost = lost;
                }

                _endpoint.AttachReceive(stream, ownsStream: false, attachmentName);
                _endpoint.AttachTransmit(stream, ownsStream: false, attachmentName);
                SetState(SpectrumInterface1Rs232ConnectionState.Connected, error: null, owner);
                await lost.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                SetState(SpectrumInterface1Rs232ConnectionState.Reconnecting, ex.Message, owner);
            }
            finally
            {
                _endpoint.DetachReceiveIfNamed(attachmentName);
                _endpoint.DetachTransmitIfNamed(attachmentName);
                lock (_sync)
                {
                    if (ReferenceEquals(_activeStream, stream))
                    {
                        _activeStream = null;
                        _connectionLost = null;
                    }
                }

                stream?.Dispose();
            }

            if (!autoReconnect)
            {
                SetDisconnectedIfOwner(owner);
                return;
            }

            firstAttempt = false;
            SetReconnectingIfOwner(owner);
            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task<Stream> OpenStreamAsync(
        SpectrumInterface1Rs232ConnectionKind kind,
        string target,
        CancellationToken cancellationToken)
    {
        if (kind == SpectrumInterface1Rs232ConnectionKind.NamedPipe)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                target,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new FileStream(
            target,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.Asynchronous);
    }

    private void OnEndpointFaulted(Exception exception)
    {
        TaskCompletionSource? lost;
        lock (_sync)
        {
            if (_state == SpectrumInterface1Rs232ConnectionState.Disconnected)
            {
                return;
            }

            _lastError = exception.Message;
            lost = _connectionLost;
        }

        lost?.TrySetResult();
        StatusChanged?.Invoke();
    }

    private void OnReceiveEnded(string? attachmentName)
    {
        if (attachmentName?.StartsWith("rs232-live:", StringComparison.Ordinal) != true)
        {
            return;
        }

        TaskCompletionSource? lost;
        lock (_sync)
        {
            lost = _connectionLost;
        }

        lost?.TrySetResult();
    }

    private void SetState(
        SpectrumInterface1Rs232ConnectionState state,
        string? error,
        CancellationTokenSource owner)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_cancellation, owner))
            {
                return;
            }

            _state = state;
            _lastError = error;
        }

        StatusChanged?.Invoke();
    }

    private void SetDisconnectedIfOwner(CancellationTokenSource owner)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_cancellation, owner))
            {
                return;
            }

            _cancellation = null;
            _kind = null;
            _target = null;
            _state = SpectrumInterface1Rs232ConnectionState.Disconnected;
        }

        StatusChanged?.Invoke();
    }

    private void SetReconnectingIfOwner(CancellationTokenSource owner)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_cancellation, owner))
            {
                return;
            }

            _state = SpectrumInterface1Rs232ConnectionState.Reconnecting;
        }

        StatusChanged?.Invoke();
    }
}
