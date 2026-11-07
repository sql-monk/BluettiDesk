using BluettiDesk.Core.Protocol;

namespace BluettiDesk.Core.Station;

public enum ConnectionPhase
{
    Stopped,
    Connecting,
    Connected,
    Waiting,
}

/// <param name="Error">Why the last attempt failed, for showing to the user.</param>
public readonly record struct ConnectionState(ConnectionPhase Phase, string? Error = null);

/// <summary>Keeps one connection alive: connects, polls, reconnects with backoff, and carries user commands.</summary>
public sealed class StationMonitor(IStationLinkFactory factory, HandshakeKeys? keys = null)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly RegisterImage _image = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private volatile StationClient? _client;

    public event Action<StationStatus>? StatusUpdated;

    public event Action<ConnectionState>? ConnectionChanged;

    /// <summary>Every register has just been read. Handlers run on the polling loop, so they must not throw or linger.</summary>
    public event Action<DateTime, RegisterImage>? SweepCompleted;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
    }

    public async Task StopAsync()
    {
        var stop = _stop;
        var loop = _loop;
        _stop = null;
        _loop = null;
        if (stop is null || loop is null)
        {
            return;
        }

        await stop.CancelAsync().ConfigureAwait(false);
        await loop.ConfigureAwait(false);
        stop.Dispose();
        ConnectionChanged?.Invoke(new ConnectionState(ConnectionPhase.Stopped));
    }

    /// <summary>Writes a register and publishes the state the station reports back afterwards.</summary>
    public async Task WriteAsync(ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        var client = _client ?? throw new ProtocolException("Немає зв'язку зі станцією.");
        await client.WriteAsync(address, value, cancellationToken).ConfigureAwait(false);
        await ReadRangesAsync(client, Ac180p.LiveRanges, skipRejected: false, cancellationToken).ConfigureAwait(false);
        Publish();
    }

    private async Task RunAsync(CancellationToken token)
    {
        var backoff = MinBackoff;
        string? lastError = null;
        try
        {
            while (true)
            {
                ConnectionChanged?.Invoke(new ConnectionState(ConnectionPhase.Connecting, lastError));
                try
                {
                    var link = await factory.OpenAsync(token).ConfigureAwait(false);
                    await using var client = new StationClient(link, keys);
                    await client.ConnectAsync(token).ConfigureAwait(false);
                    await PollAsync(client, () =>
                    {
                        backoff = MinBackoff;
                        lastError = null;
                    }, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    lastError = ex.Message;
                }

                token.ThrowIfCancellationRequested();
                ConnectionChanged?.Invoke(new ConnectionState(ConnectionPhase.Waiting, lastError));
                await Task.Delay(backoff, token).ConfigureAwait(false);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Asked to stop; disposing the client has already closed the link.
        }
    }

    private async Task PollAsync(StationClient client, Action onHealthy, CancellationToken token)
    {
        var nextSweep = DateTime.MinValue;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (client.IsLost)
                {
                    throw new ProtocolException("Зв'язок зі станцією втрачено.");
                }

                var sweep = DateTime.UtcNow >= nextSweep;
                if (sweep)
                {
                    nextSweep = DateTime.UtcNow + SweepInterval;
                    await ReadRangesAsync(client, Ac180p.AllRanges, skipRejected: true, token).ConfigureAwait(false);
                }
                else
                {
                    await ReadRangesAsync(client, Ac180p.LiveRanges, skipRejected: false, token).ConfigureAwait(false);
                }

                if (_client is null)
                {
                    _client = client;
                    onHealthy();
                    ConnectionChanged?.Invoke(new ConnectionState(ConnectionPhase.Connected));
                }

                Publish();
                if (sweep)
                {
                    PublishSweep();
                }

                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
        }
        finally
        {
            _client = null;
        }
    }

    /// <param name="skipRejected">
    /// A full sweep reads windows nobody depends on, and the station might refuse one in some state it was
    /// never swept in. That must not cost the connection.
    /// </param>
    private async Task ReadRangesAsync(StationClient client, IEnumerable<RegisterRange> ranges, bool skipRejected, CancellationToken token)
    {
        foreach (var range in ranges)
        {
            ushort[] values;
            try
            {
                values = await client.ReadAsync(range.Address, range.Quantity, token).ConfigureAwait(false);
            }
            catch (ModbusException) when (skipRejected)
            {
                continue;
            }

            lock (_image)
            {
                _image.Store(range.Address, values);
            }
        }
    }

    private void Publish()
    {
        StationStatus status;
        lock (_image)
        {
            status = Ac180p.Decode(_image);
        }

        StatusUpdated?.Invoke(status);
    }

    private void PublishSweep()
    {
        var handlers = SweepCompleted;
        if (handlers is null)
        {
            return;
        }

        RegisterImage snapshot;
        lock (_image)
        {
            snapshot = _image.Clone();
        }

        handlers(DateTime.UtcNow, snapshot);
    }
}
