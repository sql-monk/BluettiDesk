using BluettiDesk.Core.Protocol;
using BluettiDesk.Core.Station;

namespace BluettiDesk.Tests;

public class StationClientTests
{
    private static async Task<(FakeStation Station, StationClient Client)> ConnectedAsync(Action<FakeStation>? configure = null)
    {
        var station = new FakeStation();
        configure?.Invoke(station);
        var client = new StationClient(station, station.Keys);
        await client.ConnectAsync(CancellationToken.None);
        return (station, client);
    }

    [Fact]
    public async Task CompletesKeyExchangeAndReadsRegisters()
    {
        var (station, client) = await ConnectedAsync();
        station.Registers[102] = 37;

        var values = await client.ReadAsync(101, 3, CancellationToken.None);

        Assert.Equal(new ushort[] { 0, 37, 0 }, values);
    }

    [Fact]
    public async Task ReassemblesRepliesSplitAcrossNotifications()
    {
        var (station, client) = await ConnectedAsync(s => s.NotificationSize = 20);
        station.Registers[149] = 7;

        var values = await client.ReadAsync(100, 50, CancellationToken.None);

        Assert.Equal((ushort)7, values[49]);
    }

    [Fact]
    public async Task WritesRegisterThroughTheChannel()
    {
        var (station, client) = await ConnectedAsync();

        await client.WriteAsync(2012, 1, CancellationToken.None);

        Assert.Equal((ushort)1, station.Registers[2012]);
    }

    [Fact]
    public async Task RefusesAStationWithABadSignature()
    {
        var station = new FakeStation { TamperWithStationSignature = true };
        var client = new StationClient(station, station.Keys);

        var error = await Assert.ThrowsAsync<ProtocolException>(() => client.ConnectAsync(CancellationToken.None));

        Assert.Contains("Підпис", error.Message);
    }

    [Fact]
    public async Task LostConnectionFailsTheRequestInFlight()
    {
        var (station, client) = await ConnectedAsync(s => s.SilentAfterHandshake = true);

        var read = client.ReadAsync(100, 1, CancellationToken.None);
        station.DropConnection();

        await Assert.ThrowsAsync<ProtocolException>(() => read);
        Assert.True(client.IsLost);
    }

    [Fact]
    public async Task DisposingClosesTheLink()
    {
        var (station, client) = await ConnectedAsync();

        await client.DisposeAsync();

        Assert.True(station.Disposed);
    }
}
