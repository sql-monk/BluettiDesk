using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using BluettiDesk.Core.Journal;
using BluettiDesk.Core.Station;

namespace BluettiDesk.Ui;

/// <summary>
/// What the window shows. Every value comes from the latest report of the station: a switch only
/// changes once the station has confirmed it, never because it was clicked.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private const string NoValue = "—";
    private const int LowSoc = 20;
    private const int CriticalSoc = 10;
    private const double MinMainsVoltage = 80;
    private const int JournalTab = 1;
    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA");
    private static readonly TimeSpan CommandErrorDuration = TimeSpan.FromSeconds(10);

    private readonly StationMonitor _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, bool> _confirm;
    private readonly IAutoStart? _autoStart;
    private readonly StationJournal? _journal;
    private readonly PowerHistory _history = new();
    private StationStatus? _status;
    private ConnectionState _connection = new(ConnectionPhase.Stopped);
    private ChartData _liveChart;
    private DateTime? _lastUpdate;
    private string? _commandError;
    private string? _journalError;
    private bool _commandRunning;
    private bool _stopRequested;
    private int _selectedTab;

    public MainViewModel(
        StationMonitor monitor,
        Dispatcher dispatcher,
        Func<string, bool> confirm,
        IAutoStart? autoStart = null,
        StationJournal? journal = null,
        Func<string, string?>? askExportPath = null,
        string? journalOpenError = null)
    {
        _monitor = monitor;
        _dispatcher = dispatcher;
        _confirm = confirm;
        _autoStart = autoStart;
        _journal = journal;
        _liveChart = _history.ToChartData(DateTime.Now);
        _journalError = journalOpenError is null ? null : $"Журнал не ведеться: {journalOpenError}";
        _monitor.StatusUpdated += status => _dispatcher.BeginInvoke(() => OnStatus(status));
        _monitor.ConnectionChanged += state => _dispatcher.BeginInvoke(() => OnConnection(state));
        ToggleConnectionCommand = new RelayCommand(ToggleConnection);

        if (journal is not null)
        {
            journal.HealthChanged += error => _dispatcher.BeginInvoke(() =>
            {
                _journalError = error is null ? null : $"Журнал не пишеться: {error}";
                RefreshBindings();
            });
            Journal = new JournalViewModel(journal, dispatcher, askExportPath ?? (_ => null));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public JournalViewModel? Journal { get; }

    public bool HasJournal => Journal is not null;

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            _selectedTab = value;
            if (Journal is { } journal)
            {
                journal.IsActive = value == JournalTab;
            }
        }
    }

    public ChartData LiveChart => _liveChart;

    public ICommand ToggleConnectionCommand { get; }

    public bool IsConnected => _connection.Phase == ConnectionPhase.Connected;

    public bool CanControl => IsConnected && !_commandRunning && _status is not null;

    public double DashboardOpacity => IsConnected ? 1 : 0.55;

    public string ConnectionText => _connection.Phase switch
    {
        ConnectionPhase.Connected => "Підключено",
        ConnectionPhase.Connecting => "Підключення…",
        ConnectionPhase.Waiting => "Немає зв'язку",
        _ => "Відключено",
    };

    public string ConnectionStatusKey => _connection.Phase switch
    {
        ConnectionPhase.Connected => "Good",
        ConnectionPhase.Connecting or ConnectionPhase.Waiting => "Warning",
        _ => "Off",
    };

    public string ConnectionButtonText => _connection.Phase == ConnectionPhase.Stopped ? "Підключити" : "Відключити";

    public string LastUpdateText => _lastUpdate is { } time ? $"оновлено о {time.ToString("HH:mm:ss", Ukrainian)}" : string.Empty;

    public string? ErrorText => _commandError ?? _journalError ?? (IsConnected ? null : _connection.Error);

    public string TrayText => _status is { } status && IsConnected
        ? $"{TitleText}: {status.SocPercent} %, вихід {status.AcOutputWatts + status.DcOutputWatts} Вт"
        : $"{TitleText}: {ConnectionText.ToLower(Ukrainian)}";

    public string TitleText => _status is { Model.Length: > 0 } status ? status.Model : "AC180P";

    public string SerialText => _status is { SerialNumber.Length: > 0 } status ? $"SN {status.SerialNumber}" : string.Empty;

    public double SocValue => _status?.SocPercent ?? 0;

    public string SocText => _status is { } status ? status.SocPercent.ToString(Ukrainian) : NoValue;

    public string SocStatusKey => _status?.SocPercent switch
    {
        null => "Off",
        <= CriticalSoc => "Critical",
        <= LowSoc => "Warning",
        _ => "Good",
    };

    public string ActivityText => _status is not { } status
        ? NoValue
        : status.SocPercent <= LowSoc && status.Activity != BatteryActivity.Charging
            ? $"{Activity(status.Activity)} · низький заряд"
            : Activity(status.Activity);

    public string RemainingText => _status is not { } status || status.MinutesRemaining == 0
        ? string.Empty
        : status.Activity switch
        {
            BatteryActivity.Charging => $"до повного ≈ {Duration(status.MinutesRemaining)}",
            BatteryActivity.Discharging => $"залишилось ≈ {Duration(status.MinutesRemaining)}",
            _ => string.Empty,
        };

    public string BatteryElectricText => _status is { } status
        ? $"{Number(status.BatteryVoltage)} В · {Number(status.BatteryCurrent)} А"
        : string.Empty;

    public string AcInputPower => Watts(_status?.AcInputWatts);

    public string DcInputPower => Watts(_status?.DcInputWatts);

    public string AcOutputPower => Watts(_status?.AcOutputWatts);

    public string DcOutputPower => Watts(_status?.DcOutputWatts);

    public string AcInputDetail => _status is not { } status
        ? string.Empty
        : status.AcInputVoltage < MinMainsVoltage
            ? "Немає живлення"
            : $"{Number(status.AcInputVoltage)} В · {Number(status.AcInputCurrent)} А · {Number(status.AcInputFrequency)} Гц";

    public string DcInputDetail => _status is not { } status
        ? string.Empty
        : status.DcInputWatts == 0
            ? "Не підключено"
            : $"{Number(status.DcInputVoltage)} В · {Number(status.DcInputCurrent)} А";

    public string AcOutputDetail => _status is not { } status
        ? string.Empty
        : status.AcOutputOn
            ? $"{Number(status.AcOutputVoltage)} В · {Number(status.AcOutputCurrent)} А · {Number(status.AcOutputFrequency)} Гц"
            : "Вимкнено";

    public string DcOutputDetail => _status is not { } status ? string.Empty : status.DcOutputOn ? "Увімкнено" : "Вимкнено";

    public bool AcOutputOn
    {
        get => _status?.AcOutputOn ?? false;
        set => RequestOutput(Ac180p.AcOutputSwitch, value, "AC", _status?.AcOutputWatts ?? 0);
    }

    public bool DcOutputOn
    {
        get => _status?.DcOutputOn ?? false;
        set => RequestOutput(Ac180p.DcOutputSwitch, value, "DC", _status?.DcOutputWatts ?? 0);
    }

    public bool PowerLiftingOn
    {
        get => _status?.PowerLiftingOn ?? false;
        set => _ = WriteAsync(Ac180p.PowerLiftingSwitch, value ? (ushort)1 : (ushort)0);
    }

    public string PowerLiftingText => PowerLiftingOn ? "Увімкнено" : "Вимкнено";

    public ChargingMode? ChargingMode
    {
        get => _status?.ChargingMode;
        set
        {
            if (value is { } mode && mode != _status?.ChargingMode)
            {
                _ = WriteAsync(Ac180p.ChargingModeRegister, (ushort)mode);
            }
        }
    }

    public bool AutoStartEnabled
    {
        get => _autoStart?.IsEnabled ?? false;
        set
        {
            try
            {
                _autoStart?.SetEnabled(value);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                ShowCommandError($"Не вдалося змінити автозапуск: {ex.Message}");
            }

            RefreshBindings();
        }
    }

    public string AutoStartText => AutoStartEnabled ? "Запускається у фоні, значок у треї" : "Вимкнено";

    public string HealthText => _status?.Battery is { } battery ? $"{battery.HealthPercent} %" : NoValue;

    public string CyclesText => _status?.Battery is { } battery ? battery.CycleCount.ToString(Ukrainian) : NoValue;

    public string TemperatureText => _status?.Battery is { Temperatures.Count: > 0 } battery
        ? $"{battery.Temperatures.Min()}–{battery.Temperatures.Max()} °C"
        : NoValue;

    public string CellSpreadText => _status?.Battery is { } battery
        ? $"{Math.Round((battery.CellVoltages.Max() - battery.CellVoltages.Min()) * 1000)} мВ"
        : NoValue;

    public IReadOnlyList<string> CellVoltages => _status?.Battery is { } battery
        ? battery.CellVoltages.Select(v => v.ToString("0.000", Ukrainian)).ToList()
        : [];

    public string FirmwareText => _status?.Battery is { } battery
        ? $"Прошивка: {string.Join(" · ", battery.InverterFirmware)} · BMS {battery.BatteryFirmware}"
        : string.Empty;

    public void Start()
    {
        _monitor.Start();
        if (_journal is { } journal)
        {
            _ = LoadRecentHistoryAsync(journal);
        }
    }

    private static string Activity(BatteryActivity activity) => activity switch
    {
        BatteryActivity.Charging => "Заряджається",
        BatteryActivity.Discharging => "Розряджається",
        BatteryActivity.Floating => "Заряд підтримується",
        _ => "Очікування",
    };

    private static string Duration(int minutes)
    {
        var hours = minutes / 60;
        var rest = minutes % 60;
        return hours == 0 ? $"{rest} хв" : rest == 0 ? $"{hours} год" : $"{hours} год {rest} хв";
    }

    private static string Number(double value) => value.ToString("0.0", Ukrainian);

    private static string Watts(int? watts) => watts is { } value ? $"{value} Вт" : NoValue;

    /// <summary>So that the overview chart is not empty after every restart.</summary>
    private async Task LoadRecentHistoryAsync(StationJournal journal)
    {
        var to = DateTime.UtcNow;
        try
        {
            var older = await Task.Run(() => journal.QueryPower(to - PowerHistory.Length, to, StationJournal.SampleInterval));
            _history.Prepend(older);
            _liveChart = _history.ToChartData(DateTime.Now);
            RefreshBindings();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException)
        {
            // The chart simply starts empty; the journal reports its own health when it writes.
        }
    }

    private void RequestOutput(ushort register, bool turnOn, string name, int loadWatts)
    {
        if (!turnOn && loadWatts > 0 && !_confirm($"На виході {name} зараз навантаження {loadWatts} Вт. Вимкнення знеструмить підключені прилади.\n\nВимкнути вихід {name}?"))
        {
            RefreshBindings();
            return;
        }

        _ = WriteAsync(register, turnOn ? (ushort)1 : (ushort)0);
    }

    private async Task WriteAsync(ushort register, ushort value)
    {
        _commandError = null;
        _commandRunning = true;
        RefreshBindings();
        try
        {
            await _monitor.WriteAsync(register, value);
        }
        catch (Exception ex)
        {
            ShowCommandError($"Команду не виконано: {ex.Message}");
        }
        finally
        {
            _commandRunning = false;
            RefreshBindings();
        }
    }

    private void ShowCommandError(string message)
    {
        _commandError = message;
        _ = ClearCommandErrorLaterAsync(message);
    }

    private async Task ClearCommandErrorLaterAsync(string message)
    {
        await Task.Delay(CommandErrorDuration);
        if (_commandError == message)
        {
            _commandError = null;
            RefreshBindings();
        }
    }

    private void OnStatus(StationStatus status)
    {
        _status = status;
        _lastUpdate = DateTime.Now;
        _history.Add(new PowerPoint(
            _lastUpdate.Value,
            status.AcInputWatts + status.DcInputWatts,
            status.AcOutputWatts + status.DcOutputWatts));
        _liveChart = _history.ToChartData(_lastUpdate.Value);
        RefreshBindings();
    }

    private void OnConnection(ConnectionState state)
    {
        _connection = state;
        RefreshBindings();
    }

    private async void ToggleConnection()
    {
        if (_connection.Phase == ConnectionPhase.Stopped)
        {
            _monitor.Start();
            return;
        }

        if (_stopRequested)
        {
            return;
        }

        _stopRequested = true;
        try
        {
            await _monitor.StopAsync();
        }
        finally
        {
            _stopRequested = false;
        }
    }

    /// <summary>Also moves a clicked switch back when the command was declined or failed.</summary>
    private void RefreshBindings() =>
        _dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)));
}
