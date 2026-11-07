namespace BluettiDesk.Core.Station;

/// <summary>One live BLE connection, with the Windows specifics kept out of the protocol code.</summary>
public interface IStationLink : IAsyncDisposable
{
    event Action<byte[]>? NotificationReceived;

    event Action? ConnectionLost;

    /// <summary>Turns notifications on. The station sends its challenge right after this.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    Task WriteAsync(byte[] data, CancellationToken cancellationToken);
}

/// <summary>Finds the station and opens a link to it.</summary>
public interface IStationLinkFactory
{
    Task<IStationLink> OpenAsync(CancellationToken cancellationToken);
}
