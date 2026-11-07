using BluettiDesk.Core.Station;

namespace BluettiDesk.Tests;

public class StationMonitorTests
{
    [Fact]
    public async Task ReconnectsAfterTheLinkDrops()
    {
        var first = new FakeStation();
        var second = new FakeStation(sameKeysAs: first);
        var factory = new QueueFactory(first, second);
        var monitor = new StationMonitor(factory, first.Keys);
        var connected = new SemaphoreSlim(0);
        monitor.ConnectionChanged += state =>
        {
            if (state.Phase == ConnectionPhase.Connected)
            {
                connected.Release();
            }
        };

        monitor.Start();
        Assert.True(await connected.WaitAsync(TimeSpan.FromSeconds(5)));

        first.DropConnection();

        Assert.True(await connected.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal(2, factory.Opened);
        Assert.True(first.Disposed);

        await monitor.StopAsync();
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task EachSweepHandsOverEveryRegister()
    {
        var station = new FakeStation();
        station.Registers[11014] = 52636;
        var monitor = new StationMonitor(new QueueFactory(station), station.Keys);
        var swept = new TaskCompletionSource<RegisterImage>();
        monitor.SweepCompleted += (_, image) => swept.TrySetResult(image);

        monitor.Start();
        var image = await swept.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.StopAsync();

        Assert.Equal(710, image.Values.Count());
        Assert.Equal(52636, image[11014]);
    }

    [Fact]
    public async Task AWindowRefusedDuringASweepDoesNotCostTheConnection()
    {
        var station = new FakeStation();
        station.RejectReadsAt.Add(720);
        station.Registers[6006] = 94;
        var monitor = new StationMonitor(new QueueFactory(station), station.Keys);
        var swept = new TaskCompletionSource<RegisterImage>();
        var phases = new List<ConnectionPhase>();
        monitor.SweepCompleted += (_, image) => swept.TrySetResult(image);
        monitor.ConnectionChanged += state => phases.Add(state.Phase);

        monitor.Start();
        var image = await swept.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await monitor.StopAsync();

        Assert.Equal(94, image[6006]);
        Assert.DoesNotContain(ConnectionPhase.Waiting, phases);
    }

    [Fact]
    public async Task StoppingWhileWaitingForAStationEndsPromptly()
    {
        var monitor = new StationMonitor(new NeverFactory());
        var stopped = new TaskCompletionSource();
        monitor.ConnectionChanged += state =>
        {
            if (state.Phase == ConnectionPhase.Stopped)
            {
                stopped.TrySetResult();
            }
        };

        monitor.Start();
        await Task.Delay(50);
        await monitor.StopAsync();

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AFailedAttemptIsExplained()
    {
        var monitor = new StationMonitor(new FailingFactory());
        var explained = new TaskCompletionSource<string?>();
        monitor.ConnectionChanged += state =>
        {
            if (state.Phase == ConnectionPhase.Waiting)
            {
                explained.TrySetResult(state.Error);
            }
        };

        monitor.Start();
        var error = await explained.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await monitor.StopAsync();

        Assert.Equal("Станцію не знайдено.", error);
    }

    private sealed class QueueFactory(params IStationLink[] links) : IStationLinkFactory
    {
        public int Opened { get; private set; }

        public Task<IStationLink> OpenAsync(CancellationToken cancellationToken) => Task.FromResult(links[Opened++]);
    }

    private sealed class NeverFactory : IStationLinkFactory
    {
        public async Task<IStationLink> OpenAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    private sealed class FailingFactory : IStationLinkFactory
    {
        public Task<IStationLink> OpenAsync(CancellationToken cancellationToken) =>
            Task.FromException<IStationLink>(new Core.Protocol.ProtocolException("Станцію не знайдено."));
    }
}
