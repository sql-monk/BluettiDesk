using System.Windows.Threading;
using BluettiDesk.Core.Station;
using BluettiDesk.Ui;

namespace BluettiDesk.Tests;

/// <summary>
/// The paths that could cut power to whatever is plugged in. They run against a fake station, because
/// trying them on a real one would do exactly that.
/// </summary>
public sealed class ControlSafetyTests : IAsyncDisposable
{
    private readonly FakeStation _station = new();
    private readonly List<string> _questions = [];
    private readonly Thread _uiThread;
    private readonly Dispatcher _dispatcher;
    private readonly StationMonitor _monitor;
    private readonly MainViewModel _viewModel;
    private bool _answer;

    public ControlSafetyTests()
    {
        Dispatcher? dispatcher = null;
        using var started = new ManualResetEventSlim();
        _uiThread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            started.Set();
            Dispatcher.Run();
        });
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        started.Wait();
        _dispatcher = dispatcher!;

        _monitor = new StationMonitor(new SingleLinkFactory(_station), _station.Keys);
        _viewModel = _dispatcher.Invoke(() => new MainViewModel(_monitor, _dispatcher, question =>
        {
            _questions.Add(question);
            return _answer;
        }));
    }

    public async ValueTask DisposeAsync()
    {
        await _monitor.StopAsync();
        _dispatcher.InvokeShutdown();
        _uiThread.Join();
    }

    [Fact]
    public async Task DecliningTheQuestionLeavesAcOnAndSendsNothing()
    {
        await ConnectWithLoadAsync(acWatts: 65);

        await OnUiAsync(() => _viewModel.AcOutputOn = false);
        await Eventually(() => _questions.Count == 1);
        await Settle();

        Assert.Empty(_station.Writes);
        Assert.Equal((ushort)1, _station.Registers[Ac180p.AcOutputSwitch]);
        Assert.True(await OnUiAsync(() => _viewModel.AcOutputOn));
    }

    [Fact]
    public async Task TheQuestionNamesTheLoadAboutToLosePower()
    {
        await ConnectWithLoadAsync(acWatts: 65);

        await OnUiAsync(() => _viewModel.AcOutputOn = false);
        await Eventually(() => _questions.Count == 1);

        Assert.Contains("65 Вт", _questions[0]);
    }

    [Fact]
    public async Task ConfirmingTurnsAcOffAndTheSwitchFollowsTheStation()
    {
        _answer = true;
        await ConnectWithLoadAsync(acWatts: 65);

        await OnUiAsync(() => _viewModel.AcOutputOn = false);

        await Eventually(() => _station.Writes.Contains((Ac180p.AcOutputSwitch, 0)));
        await EventuallyOnUi(() => !_viewModel.AcOutputOn);
    }

    [Fact]
    public async Task NoQuestionWhenNothingIsDrawingPower()
    {
        await ConnectWithLoadAsync(acWatts: 0);

        await OnUiAsync(() => _viewModel.AcOutputOn = false);

        await Eventually(() => _station.Writes.Contains((Ac180p.AcOutputSwitch, 0)));
        Assert.Empty(_questions);
    }

    [Fact]
    public async Task DcWithALoadAsksToo()
    {
        await ConnectWithLoadAsync(acWatts: 0, dcWatts: 12);

        await OnUiAsync(() => _viewModel.DcOutputOn = false);
        await Eventually(() => _questions.Count == 1);
        await Settle();

        Assert.Empty(_station.Writes);
    }

    [Fact]
    public async Task TurningOnNeverAsks()
    {
        _station.Registers[Ac180p.AcOutputSwitch] = 0;
        await StartAndWaitConnectedAsync();

        await OnUiAsync(() => _viewModel.AcOutputOn = true);

        await Eventually(() => _station.Writes.Contains((Ac180p.AcOutputSwitch, 1)));
        Assert.Empty(_questions);
    }

    [Fact]
    public async Task ARefusedCommandIsReportedAndTheSwitchStaysPut()
    {
        _station.RejectWrites = true;
        await ConnectWithLoadAsync(acWatts: 0);

        await OnUiAsync(() => _viewModel.AcOutputOn = false);

        await EventuallyOnUi(() => _viewModel.ErrorText is not null);
        Assert.Contains("Команду не виконано", await OnUiAsync(() => _viewModel.ErrorText));
        Assert.True(await OnUiAsync(() => _viewModel.AcOutputOn));
    }

    [Fact]
    public async Task ChargingModeWritesTheChosenValueOnce()
    {
        await ConnectWithLoadAsync(acWatts: 0);

        await OnUiAsync(() => _viewModel.ChargingMode = ChargingMode.Turbo);

        await Eventually(() => _station.Writes.Contains((Ac180p.ChargingModeRegister, 2)));
        await EventuallyOnUi(() => _viewModel.ChargingMode == ChargingMode.Turbo);
        Assert.Single(_station.Writes);
    }

    [Fact]
    public async Task ControlsAreOffWhileDisconnected()
    {
        await ConnectWithLoadAsync(acWatts: 0);
        Assert.True(await OnUiAsync(() => _viewModel.CanControl));

        await _monitor.StopAsync();

        await EventuallyOnUi(() => !_viewModel.CanControl);
    }

    [Fact]
    public async Task ReadingTheStationNeverWritesToIt()
    {
        await ConnectWithLoadAsync(acWatts: 65);

        await Task.Delay(TimeSpan.FromSeconds(2.5));

        Assert.Empty(_station.Writes);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Умова не виконалась вчасно.");
            await Task.Delay(10);
        }
    }

    /// <summary>Long enough for a wrongly sent command to have reached the station.</summary>
    private static Task Settle() => Task.Delay(TimeSpan.FromMilliseconds(200));

    private async Task ConnectWithLoadAsync(int acWatts, int dcWatts = 0)
    {
        _station.Registers[142] = (ushort)acWatts;
        _station.Registers[140] = (ushort)dcWatts;
        _station.Registers[Ac180p.AcOutputSwitch] = 1;
        _station.Registers[Ac180p.DcOutputSwitch] = 1;
        await StartAndWaitConnectedAsync();
    }

    private async Task StartAndWaitConnectedAsync()
    {
        _monitor.Start();
        await EventuallyOnUi(() => _viewModel.CanControl);
    }

    private async Task EventuallyOnUi(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!await OnUiAsync(condition))
        {
            Assert.True(DateTime.UtcNow < deadline, "Умова не виконалась вчасно.");
            await Task.Delay(10);
        }
    }

    private async Task<T> OnUiAsync<T>(Func<T> read) => await _dispatcher.InvokeAsync(read);

    private async Task OnUiAsync(Action action) => await _dispatcher.InvokeAsync(action);

    private sealed class SingleLinkFactory(IStationLink link) : IStationLinkFactory
    {
        public Task<IStationLink> OpenAsync(CancellationToken cancellationToken) => Task.FromResult(link);
    }
}
