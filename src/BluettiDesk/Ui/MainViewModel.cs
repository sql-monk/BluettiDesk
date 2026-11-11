using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using BluettiDesk.Core.Journal;
using BluettiDesk.Core.Outages;
using BluettiDesk.Core.Station;
using BluettiDesk.Outages;

namespace BluettiDesk.Ui;

/// <summary>
/// What the window shows. Every value comes from the latest report of the station: a switch only
/// changes once the station has confirmed it, never because it was clicked.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private const string NoValue = "—";
    private const int OutagesTab = 1;
    private const int JournalTab = 2;
    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA");
    private static readonly TimeSpan CommandErrorDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AlertQuietPeriod = TimeSpan.FromMinutes(15);

    /// <summary>The load of the forecast is the mean of this long, so that a kettle does not swing it.</summary>
    private static readonly TimeSpan LoadWindow = TimeSpan.FromMinutes(10);

    private readonly StationMonitor _monitor;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, bool> _confirm;
    private readonly IAutoStart? _autoStart;
    private readonly StationJournal? _journal;
    private readonly PowerHistory _history = new();
    private readonly AlertAnnouncer _announcer = new(AlertQuietPeriod);
    private StationStatus? _status;
    private StationHealth _health = StationHealth.Unknown;
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
        string? journalOpenError = null,
        OutageViewModel? outages = null)
    {
        Outages = outages;
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

        if (outages is not null)
        {
            outages.Changed += RefreshBindings;
        }

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

    /// <summary>A critical alert has just begun. Raised at most once per kind in <see cref="AlertQuietPeriod"/>.</summary>
    public event Action<StationAlert>? AlertRaised;

    public JournalViewModel? Journal { get; }

    /// <summary>The outage schedule of the chosen address, when the app reads it.</summary>
    public OutageViewModel? Outages { get; }

    public bool HasJournal => Journal is not null;

    public bool HasOutages => Outages is not null;

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

            if (Outages is { } outages)
            {
                outages.IsActive = value == OutagesTab;
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
            + (Health.Alerts.Count > 0 ? $"\n{Health.Alerts[0].Title}" : string.Empty)
        : $"{TitleText}: {ConnectionText.ToLower(Ukrainian)}";

    /// <summary>What is out of range right now.</summary>
    public IReadOnlyList<AlertView> Alerts => Health.Alerts.Select(a => new AlertView(a.Title, a.Detail, Key(a.Severity))).ToList();

    public bool HasAlerts => Health.Alerts.Count > 0;

    public string TitleText => _status is { Model.Length: > 0 } status ? status.Model : "AC180P";

    public string SerialText => _status is { SerialNumber.Length: > 0 } status ? $"SN {status.SerialNumber}" : string.Empty;

    public double SocValue => _status?.SocPercent ?? 0;

    public string SocText => _status is { } status ? status.SocPercent.ToString(Ukrainian) : NoValue;

    public string SocStatusKey => _status?.SocPercent switch
    {
        null => "Off",
        <= StationLimits.CriticalSoc => "Critical",
        <= StationLimits.LowSoc => "Warning",
        _ => "Good",
    };

    public string ActivityText => _status is not { } status
        ? NoValue
        : status.SocPercent <= StationLimits.LowSoc && status.Activity != BatteryActivity.Charging
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

    public bool MainsPresent => _status is { } status && status.AcInputVoltage >= StationLimits.MainsPresentVoltage;

    public bool MainsAbsent => _status is { } status && status.AcInputVoltage < StationLimits.MainsPresentVoltage;

    public string AcInputVoltageText => _status is { } status ? $"{Number(status.AcInputVoltage)} В" : string.Empty;

    public string AcInputCurrentText => _status is { } status ? $" · {Number(status.AcInputCurrent)} А · " : string.Empty;

    public string AcInputFrequencyText => _status is { } status ? $"{Number(status.AcInputFrequency)} Гц" : string.Empty;

    public string AcInputVoltageKey => Key(Health.MainsVoltage);

    public string AcInputFrequencyKey => Key(Health.MainsFrequency);

    public string AcInputCardKey => Key(Worst(Health.MainsVoltage, Health.MainsFrequency));

    public string DcInputDetail => _status is not { } status
        ? string.Empty
        : status.DcInputWatts == 0
            ? "Не підключено"
            : $"{Number(status.DcInputVoltage)} В · {Number(status.DcInputCurrent)} А";

    public bool AcOutputLive => _status is { AcOutputOn: true };

    public bool AcOutputOff => _status is { AcOutputOn: false };

    public string AcOutputVoltageText => _status is { } status ? $"{Number(status.AcOutputVoltage)} В" : string.Empty;

    public string AcOutputCurrentText => _status is { } status ? $" · {Number(status.AcOutputCurrent)} А · " : string.Empty;

    public string AcOutputFrequencyText => _status is { } status ? $"{Number(status.AcOutputFrequency)} Гц" : string.Empty;

    public string AcOutputVoltageKey => Key(Health.OutputVoltage);

    public string AcOutputFrequencyKey => Key(Health.OutputFrequency);

    public string AcOutputCardKey => Key(Worst(Health.OutputVoltage, Health.OutputFrequency));

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

    public bool AcEcoOn
    {
        get => _status?.AcEco.On ?? false;
        set => RequestEco(Ac180p.AcEcoSwitch, value, "AC", _status?.AcEco, _status is { AcOutputOn: true } s ? s.AcOutputWatts : 0);
    }

    public EcoTimeout? AcEcoTimeout
    {
        get => _status?.AcEco.Timeout;
        set => WriteEcoTimeout(Ac180p.AcEcoTimeoutRegister, value, _status?.AcEco.Timeout);
    }

    public string AcEcoText => EcoText(_status?.AcEco);

    public bool DcEcoOn
    {
        get => _status?.DcEco.On ?? false;
        set => RequestEco(Ac180p.DcEcoSwitch, value, "DC", _status?.DcEco, _status is { DcOutputOn: true } s ? s.DcOutputWatts : 0);
    }

    public EcoTimeout? DcEcoTimeout
    {
        get => _status?.DcEco.Timeout;
        set => WriteEcoTimeout(Ac180p.DcEcoTimeoutRegister, value, _status?.DcEco.Timeout);
    }

    public string DcEcoText => EcoText(_status?.DcEco);

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

    public string TemperatureKey => Key(Health.Temperature);

    public string CellSpreadText => _status?.Battery is { } battery
        ? $"{Math.Round((battery.CellVoltages.Max() - battery.CellVoltages.Min()) * 1000)} мВ"
        : NoValue;

    public string CellSpreadKey => Key(Health.CellBalance);

    public IReadOnlyList<CellView> CellVoltages => _status?.Battery is { } battery
        ? battery.CellVoltages
            .Select((v, i) => new CellView(v.ToString("0.000", Ukrainian), Key(i < Health.Cells.Count ? Health.Cells[i] : Severity.Normal)))
            .ToList()
        : [];

    public string BatteryCardKey => Key(Health.Cells.Aggregate(Worst(Health.Temperature, Health.CellBalance), Worst));

    public string FirmwareText => _status?.Battery is { } battery
        ? $"Прошивка: {string.Join(" · ", battery.InverterFirmware)} · BMS {battery.BatteryFirmware}"
        : string.Empty;

    // ---------- today's schedule and the charge, on the overview ----------

    /// <summary>Only while the app follows the schedule of an address.</summary>
    public bool BackupVisible => Outages?.FollowedSchedule is not null;

    public string TodayTitle => $"Сьогодні, {OutageTexts.DayTitle(KyivTime.DayOf(DateTime.UtcNow))}";

    public IReadOnlyList<StripSegment> TodaySegments =>
        Outages?.FollowedSchedule is { } schedule && schedule.SlotsFor(KyivTime.DayOf(DateTime.UtcNow), schedule.Queues[0].Id) is { } slots
            ? StripSegment.FromSlots(slots)
            : [];

    public int TodayNowMinute => KyivTime.MinuteOfDay(DateTime.UtcNow);

    public string BackupKey => Backup()?.Forecast?.Reaches switch
    {
        true => "Good",
        false => "Critical",
        _ => "Off",
    };

    public string BackupTitle => Backup() switch
    {
        null => "Немає даних станції",
        { Forecast: { } forecast } => forecast.Title,
        _ => "Графік не каже, що зараз",
    };

    public string? BackupMargin => Backup()?.Forecast?.MarginText;

    public string BackupDetail => Backup() switch
    {
        null => "Чи вистачить заряду до ввімкнення світла, буде видно, коли станція на зв'язку.",
        { Forecast: { } forecast } state => forecast.Detail(state.Soc, state.Load, state.Charging),
        var state => $"Заряду {state.Soc} % при споживанні ≈ {state.Load.ToString("0", Ukrainian)} Вт вистачить на ≈ {OutageTexts.Duration(state.Runtime)}.",
    };

    /// <summary>The line under today's strip: how far the charge carries, and the stretch it does not cover.</summary>
    public ReachSpan? TodayReach
    {
        get
        {
            if (Backup()?.Forecast is not { Case: not BackupCase.NoOutage } forecast)
            {
                return null;
            }

            var today = KyivTime.DayOf(forecast.NowUtc);
            var from = KyivTime.MinuteFrom(today, forecast.FromUtc);
            if (from >= KyivTime.MinutesPerDay)
            {
                return null;
            }

            var empty = KyivTime.MinuteFrom(today, forecast.EmptyUtc);
            var back = forecast.UntilUtc ?? forecast.KnownUntilUtc;
            var lasts = forecast.Case == BackupCase.NextOutage
                ? $"Якщо світло зникне о {OutageTexts.Time(KyivTime.MinuteOfDay(forecast.FromUtc))}, заряду вистачить {forecast.Upto(forecast.EmptyUtc)}"
                : $"Заряду вистачить {forecast.Upto(forecast.EmptyUtc)}";
            return forecast.Reaches == false
                ? new ReachSpan(from, empty, KyivTime.MinuteFrom(today, back), lasts,
                    $"Станція вже розряджена, а світла ще немає: з {OutageTexts.Time(KyivTime.MinuteOfDay(forecast.EmptyUtc))} {forecast.Upto(back)}")
                : new ReachSpan(from, empty, empty, lasts, string.Empty);
        }
    }

    public void Start()
    {
        _monitor.Start();
        if (_journal is { } journal)
        {
            _ = LoadRecentHistoryAsync(journal);
        }
    }

    /// <summary>
    /// The forecast as of now: the present charge at the mean load of the last minutes, against the schedule. Null while
    /// there is no station data to forecast from.
    /// </summary>
    private BackupState? Backup()
    {
        if (Outages?.FollowedSchedule is not { } schedule || !IsConnected || _status is not { } status)
        {
            return null;
        }

        var load = _history.MeanOutput(DateTime.Now, LoadWindow) ?? status.AcOutputWatts + status.DcOutputWatts;
        var runtime = BatteryRuntime.Minutes(status.SocPercent, load, status.AcOutputOn);
        return new BackupState(
            BackupForecast.For(schedule, schedule.Queues[0].Id, DateTime.UtcNow, MainsPresent, runtime),
            status.SocPercent,
            load,
            runtime,
            status.Activity == BatteryActivity.Charging);
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

    private static string Hours(EcoTimeout? timeout) => timeout is { } value ? $"{(int)value} год" : "кілька годин";

    private static string EcoText(EcoSetting? eco) => eco switch
    {
        null => NoValue,
        { On: false } => "Вимкнено",
        { ThresholdWatts: > 0 } => $"Вимикає вихід, якщо навантаження менше {eco.ThresholdWatts} Вт довше ніж:",
        _ => "Вимикає вихід, якщо навантаження мале довше ніж:",
    };

    private static string Key(Severity severity) => severity.ToString();

    private static Severity Worst(Severity a, Severity b) => a > b ? a : b;

    /// <summary>The checks of the latest report, or none while disconnected: the last values may be stale.</summary>
    private StationHealth Health => IsConnected ? _health : StationHealth.Unknown;

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

    /// <summary>
    /// ECO cuts the output on its own later. Asks first when what is plugged in now draws less than the threshold,
    /// because exactly that load would lose power once the timeout runs out.
    /// </summary>
    private void RequestEco(ushort register, bool turnOn, string name, EcoSetting? eco, int loadWatts)
    {
        if (turnOn && eco is { } setting && loadWatts > 0 && loadWatts < setting.ThresholdWatts
            && !_confirm($"Зараз на виході {name} {loadWatts} Вт, менше за поріг ECO {setting.ThresholdWatts} Вт. Якщо так триватиме {Hours(setting.Timeout)}, станція сама вимкне вихід {name} і знеструмить підключені прилади.\n\nУвімкнути ECO для виходу {name}?"))
        {
            RefreshBindings();
            return;
        }

        _ = WriteAsync(register, turnOn ? (ushort)1 : (ushort)0);
    }

    private void WriteEcoTimeout(ushort register, EcoTimeout? value, EcoTimeout? current)
    {
        if (value is { } timeout && timeout != current)
        {
            _ = WriteAsync(register, (ushort)timeout);
        }
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
        _health = StationChecks.Evaluate(status, _health);
        _lastUpdate = DateTime.Now;
        foreach (var alert in _announcer.Update(Health.Alerts, _lastUpdate.Value))
        {
            AlertRaised?.Invoke(alert);
        }

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
        if (!IsConnected)
        {
            _announcer.Update([], DateTime.Now);
        }

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

public sealed record AlertView(string Title, string Detail, string Key);

internal sealed record BackupState(BackupForecast? Forecast, int Soc, double Load, int Runtime, bool Charging);

public sealed record CellView(string Text, string Key);
