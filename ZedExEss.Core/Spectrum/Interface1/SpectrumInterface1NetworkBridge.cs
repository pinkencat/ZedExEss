using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ZedExEss.Spectrum.Interface1;

public enum SpectrumInterface1NetworkBridgeState
{
    Disconnected,
    Connecting,
    Listening,
    Connected
}

/// <summary>
/// Joins a session-owned ZX Net wire to another emulator process over TCP.
/// </summary>
/// <remarks>
/// The bridge carries timestamped physical output transitions, not decoded ZX Net
/// packets. Both Interface 1 ROMs therefore continue to perform scout, packet,
/// checksum and retry handling. A small emulated-time lead absorbs ordinary host
/// scheduling and TCP latency while preserving every pulse width at the peer.
/// </remarks>
public sealed class SpectrumInterface1NetworkBridge : IDisposable
{
    public const int DefaultPort = 33501;
    // A short jitter allowance. Larger fixed offsets make a complete scout/acknowledge
    // round trip exceed the Interface 1 ROM's response window.
    public const ulong TransportLeadTstates = 1_000;

    private const int HandshakeSize = 16;
    private const int TransitionFrameSize = 10;
    private static ReadOnlySpan<byte> ProtocolMagic => "ZXN1"u8;

    private readonly object _sync = new();
    private readonly SpectrumInterface1NetworkBus _bus;
    private readonly SpectrumInterface1NetworkStation _remoteStation;
    private readonly Func<ulong> _clock;
    private CancellationTokenSource? _runOwner;
    private TcpClient? _activeClient;
    private Channel<SpectrumInterface1NetworkOutputTransition>? _outgoing;
    private SpectrumInterface1NetworkBridgeState _state;
    private string? _target;
    private string? _lastError;
    private ulong _localEpoch;
    private ulong _remoteEpoch;
    private ulong _lastRemoteSourceTstate;
    private ulong _lastRemoteTstate;
    private bool _disposed;

    public SpectrumInterface1NetworkBridge(
        SpectrumInterface1NetworkBus bus,
        Func<ulong> clock)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _remoteStation = _bus.AttachStation("Remote ZX Net bridge");
        _bus.StationOutputChanged += OnStationOutputChanged;
    }

    public event Action? StatusChanged;

    public SpectrumInterface1NetworkBridgeState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
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

    public bool IsActive => State != SpectrumInterface1NetworkBridgeState.Disconnected;

    public void Connect(string host, int port = DefaultPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ValidatePort(port);
        Start(
            SpectrumInterface1NetworkBridgeState.Connecting,
            $"{host.Trim()}:{port}",
            owner => RunClientAsync(host.Trim(), port, owner));
    }

    public void Listen(int port = DefaultPort)
    {
        ValidatePort(port);
        Start(
            SpectrumInterface1NetworkBridgeState.Listening,
            $"0.0.0.0:{port}",
            owner => RunListenerAsync(port, owner));
    }

    public void Disconnect()
    {
        CancellationTokenSource? owner;
        TcpClient? client;
        lock (_sync)
        {
            owner = _runOwner;
            client = _activeClient;
            _runOwner = null;
            _activeClient = null;
            _outgoing?.Writer.TryComplete();
            _outgoing = null;
            _state = SpectrumInterface1NetworkBridgeState.Disconnected;
            _target = null;
            _lastError = null;
        }

        owner?.Cancel();
        client?.Dispose();
        ReleaseRemoteLine();
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
        _bus.StationOutputChanged -= OnStationOutputChanged;
        _remoteStation.Dispose();
    }

    private void Start(
        SpectrumInterface1NetworkBridgeState initialState,
        string target,
        Func<CancellationTokenSource, Task> run)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        Disconnect();
        var owner = new CancellationTokenSource();
        lock (_sync)
        {
            _runOwner = owner;
            _state = initialState;
            _target = target;
            _lastError = null;
        }

        StatusChanged?.Invoke();
        _ = Task.Run(async () =>
        {
            try
            {
                await run(owner).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested)
            {
                // User disconnect.
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
            {
                SetFault(ex.Message, owner);
            }
            finally
            {
                SetDisconnectedIfOwner(owner);
                owner.Dispose();
            }
        });
    }

    private async Task RunClientAsync(string host, int port, CancellationTokenSource owner)
    {
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, owner.Token).ConfigureAwait(false);
        await RunConnectionAsync(client, owner).ConfigureAwait(false);
    }

    private async Task RunListenerAsync(int port, CancellationTokenSource owner)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start(1);
        try
        {
            while (!owner.IsCancellationRequested)
            {
                SetState(SpectrumInterface1NetworkBridgeState.Listening, owner);
                TcpClient client = await listener.AcceptTcpClientAsync(owner.Token).ConfigureAwait(false);
                client.NoDelay = true;
                using (client)
                {
                    await RunConnectionAsync(client, owner).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task RunConnectionAsync(TcpClient client, CancellationTokenSource owner)
    {
        NetworkStream stream = client.GetStream();
        ulong localEpoch = _clock();
        byte[] localHandshake = CreateHandshake(localEpoch);
        await stream.WriteAsync(localHandshake, owner.Token).ConfigureAwait(false);
        await stream.FlushAsync(owner.Token).ConfigureAwait(false);

        var remoteHandshake = new byte[HandshakeSize];
        await stream.ReadExactlyAsync(remoteHandshake, owner.Token).ConfigureAwait(false);
        ulong remoteEpoch = ParseHandshake(remoteHandshake);
        var outgoing = Channel.CreateUnbounded<SpectrumInterface1NetworkOutputTransition>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        lock (_sync)
        {
            if (!ReferenceEquals(_runOwner, owner))
            {
                return;
            }

            _activeClient = client;
            _outgoing = outgoing;
            _localEpoch = localEpoch;
            _remoteEpoch = remoteEpoch;
            _lastRemoteSourceTstate = remoteEpoch;
            _lastRemoteTstate = localEpoch + TransportLeadTstates;
            _state = SpectrumInterface1NetworkBridgeState.Connected;
            _lastError = null;
        }

        StatusChanged?.Invoke();
        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
        Task read = ReadTransitionsAsync(stream, connectionCancellation.Token);
        Task write = WriteTransitionsAsync(stream, outgoing.Reader, connectionCancellation.Token);
        await Task.WhenAny(read, write).ConfigureAwait(false);
        connectionCancellation.Cancel();
        outgoing.Writer.TryComplete();
        try
        {
            await Task.WhenAll(read, write).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
        {
            // The companion task was cancelled after the connection ended.
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeClient, client))
                {
                    _activeClient = null;
                    _outgoing = null;
                }
            }

            ReleaseRemoteLine();
        }
    }

    private async Task ReadTransitionsAsync(Stream stream, CancellationToken cancellationToken)
    {
        var frame = new byte[TransitionFrameSize];
        while (true)
        {
            await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
            if (frame[0] != 1 || frame[1] > 1)
            {
                throw new InvalidDataException("The ZX Net peer sent an invalid transition frame.");
            }

            ulong remoteTstate = BinaryPrimitives.ReadUInt64BigEndian(frame.AsSpan(2));
            ApplyRemoteOutput(remoteTstate, frame[1] != 0);
        }
    }

    private static async Task WriteTransitionsAsync(
        Stream stream,
        ChannelReader<SpectrumInterface1NetworkOutputTransition> reader,
        CancellationToken cancellationToken)
    {
        var frame = new byte[TransitionFrameSize];
        frame[0] = 1;
        await foreach (SpectrumInterface1NetworkOutputTransition transition in reader.ReadAllAsync(cancellationToken))
        {
            frame[1] = transition.DrivesHigh ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), transition.Tstate);
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ApplyRemoteOutput(ulong remoteTstate, bool drivesHigh)
    {
        if (remoteTstate < _lastRemoteSourceTstate)
        {
            // A peer can reset or replace its emulated machine without dropping the
            // desktop bridge. Start a new epoch beyond all transitions already queued
            // locally instead of collapsing the reset machine's pulses onto one time.
            _remoteEpoch = remoteTstate;
            _localEpoch = Math.Max(_clock(), _lastRemoteTstate);
        }

        _lastRemoteSourceTstate = remoteTstate;
        ulong delta = remoteTstate >= _remoteEpoch ? remoteTstate - _remoteEpoch : 0;
        ulong mappedDelta = delta > ulong.MaxValue - TransportLeadTstates
            ? ulong.MaxValue
            : delta + TransportLeadTstates;
        ulong mapped = mappedDelta > ulong.MaxValue - _localEpoch
            ? ulong.MaxValue
            : _localEpoch + mappedDelta;
        if (mapped < _lastRemoteTstate)
        {
            mapped = _lastRemoteTstate;
        }

        _lastRemoteTstate = mapped;
        _remoteStation.SetOutput(!drivesHigh, networkSelected: true, mapped);
    }

    private void ReleaseRemoteLine()
    {
        if (!_remoteStation.IsAttached)
        {
            return;
        }

        ulong releaseAt = Math.Max(_lastRemoteTstate, _clock());
        _lastRemoteTstate = releaseAt;
        _remoteStation.SetOutput(ulaOutputHigh: true, networkSelected: true, releaseAt);
    }

    private void OnStationOutputChanged(SpectrumInterface1NetworkOutputTransition transition)
    {
        if (transition.SourceStationId == _remoteStation.Id)
        {
            return;
        }

        Channel<SpectrumInterface1NetworkOutputTransition>? outgoing;
        lock (_sync)
        {
            outgoing = _state == SpectrumInterface1NetworkBridgeState.Connected
                ? _outgoing
                : null;
        }

        outgoing?.Writer.TryWrite(transition);
    }

    private static byte[] CreateHandshake(ulong epoch)
    {
        var bytes = new byte[HandshakeSize];
        ProtocolMagic.CopyTo(bytes);
        bytes[4] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), epoch);
        return bytes;
    }

    private static ulong ParseHandshake(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != HandshakeSize || !bytes[..4].SequenceEqual(ProtocolMagic) || bytes[4] != 1)
        {
            throw new InvalidDataException("The TCP peer is not a compatible ZedExEss ZX Net bridge.");
        }

        return BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
    }

    private void SetState(SpectrumInterface1NetworkBridgeState state, CancellationTokenSource owner)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_runOwner, owner))
            {
                return;
            }

            _state = state;
        }

        StatusChanged?.Invoke();
    }

    private void SetFault(string error, CancellationTokenSource owner)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_runOwner, owner))
            {
                return;
            }

            _lastError = error;
        }

        StatusChanged?.Invoke();
    }

    private void SetDisconnectedIfOwner(CancellationTokenSource owner)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_runOwner, owner))
            {
                return;
            }

            _runOwner = null;
            _activeClient = null;
            _outgoing = null;
            _state = SpectrumInterface1NetworkBridgeState.Disconnected;
        }

        ReleaseRemoteLine();
        StatusChanged?.Invoke();
    }

    private static void ValidatePort(int port)
    {
        if ((uint)(port - 1) >= 65_535u)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "TCP port must be between 1 and 65535.");
        }
    }
}
