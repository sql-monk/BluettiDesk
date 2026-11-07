using BluettiDesk.Core.Protocol;

namespace BluettiDesk.Core.Station;

/// <summary>Encrypted request/response over one link. One request is in flight at a time.</summary>
public sealed class StationClient : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    private readonly IStationLink _link;
    private readonly SecureChannel _channel;
    private readonly object _channelLock = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Pending? _pending;
    private volatile bool _lost;

    public StationClient(IStationLink link, HandshakeKeys? keys = null)
    {
        _link = link;
        _channel = new SecureChannel(keys);
        _link.NotificationReceived += OnNotification;
        _link.ConnectionLost += OnConnectionLost;
    }

    public bool IsLost => _lost;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _link.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _ready.Task.WaitAsync(HandshakeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ProtocolException("Станція не завершила обмін ключами.");
        }
    }

    public async Task<ushort[]> ReadAsync(ushort address, ushort quantity, CancellationToken cancellationToken)
    {
        var reply = await ExchangeAsync(Modbus.BuildRead(address, quantity), r => Modbus.IsReadResponse(r, quantity), cancellationToken).ConfigureAwait(false);
        return Modbus.ParseRead(reply, quantity);
    }

    public async Task WriteAsync(ushort address, ushort value, CancellationToken cancellationToken)
    {
        var request = Modbus.BuildWrite(address, value);
        var reply = await ExchangeAsync(request, r => Modbus.IsWriteResponse(r, request), cancellationToken).ConfigureAwait(false);
        Modbus.ThrowIfException(reply, Modbus.WriteSingle);
    }

    public async ValueTask DisposeAsync()
    {
        _link.NotificationReceived -= OnNotification;
        _link.ConnectionLost -= OnConnectionLost;
        _ready.TrySetCanceled();
        await _link.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<byte[]> ExchangeAsync(byte[] request, Func<byte[], bool> matches, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var pending = new Pending(matches);
        try
        {
            if (_lost)
            {
                throw new ProtocolException(LostMessage);
            }

            byte[] sealedFrame;
            lock (_channelLock)
            {
                _channel.ResetFraming();
                _pending = pending;
                sealedFrame = _channel.Seal(request);
            }

            await _link.WriteAsync(sealedFrame, cancellationToken).ConfigureAwait(false);
            return await pending.Reply.Task.WaitAsync(ReplyTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ProtocolException("Станція не відповіла вчасно.");
        }
        finally
        {
            lock (_channelLock)
            {
                _pending = null;
            }

            _requestGate.Release();
        }
    }

    private const string LostMessage = "Зв'язок зі станцією втрачено.";

    private void OnNotification(byte[] data)
    {
        try
        {
            ChannelResult result;
            Pending? pending;
            bool ready;
            lock (_channelLock)
            {
                result = _channel.Receive(data);
                pending = _pending;
                ready = _channel.IsReady;
            }

            if (result.ToSend is { } reply)
            {
                _ = SendHandshakeReplyAsync(reply);
            }

            if (ready)
            {
                _ready.TrySetResult();
            }

            // A reply that arrives after its request timed out must not answer the next request.
            if (result.Payload is { } payload && pending is not null && pending.Matches(payload))
            {
                pending.Reply.TrySetResult(payload);
            }
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    private async Task SendHandshakeReplyAsync(byte[] reply)
    {
        try
        {
            await _link.WriteAsync(reply, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
    }

    private void OnConnectionLost()
    {
        _lost = true;
        var error = new ProtocolException(LostMessage);
        _ready.TrySetException(error);

        Pending? pending;
        lock (_channelLock)
        {
            pending = _pending;
        }

        pending?.Reply.TrySetException(error);
    }

    private sealed class Pending(Func<byte[], bool> matches)
    {
        public TaskCompletionSource<byte[]> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Matches(byte[] payload) => matches(payload);
    }
}
