using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using BluettiDesk.Core.Journal;
using BluettiDesk.Core.Station;

namespace BluettiDesk.Ui;

public enum JournalRange
{
    ThirtyMinutes,
    SixHours,
    Day,
    Week,
    Month,
    All,
}

/// <summary>The "Журнал" tab: charts of a chosen period, and the way out to Excel.</summary>
public sealed class JournalViewModel : INotifyPropertyChanged
{
    private const int TargetPoints = 600;
    private const double MinTopWatts = 10;
    private const double OverviewMinTopWatts = 100;
    private const string Watts = "Вт";
    private const string Percent = "%";
    private const int Megabyte = 1024 * 1024;
    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA");
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    private readonly StationJournal _journal;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, string?> _askExportPath;
    private readonly DispatcherTimer _refresh;
    private JournalRange _range = JournalRange.Day;
    private LogField _field;
    private ChartData? _powerChart;
    private ChartData? _fieldChart;
    private string _infoText = string.Empty;
    private string _statusText = string.Empty;
    private string _bucketText = string.Empty;
    private int _reloadVersion;
    private bool _isActive;

    public JournalViewModel(StationJournal journal, Dispatcher dispatcher, Func<string, string?> askExportPath)
    {
        _journal = journal;
        _dispatcher = dispatcher;
        _askExportPath = askExportPath;
        _field = journal.Fields[0];
        _refresh = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = RefreshInterval };
        _refresh.Tick += (_, _) => _ = ReloadAsync();
        ExportCommand = new RelayCommand(() => _ = ExportAsync());
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand ExportCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public IReadOnlyList<LogField> Fields => _journal.Fields;

    public JournalRange SelectedRange
    {
        get => _range;
        set
        {
            if (_range != value)
            {
                _range = value;
                _ = ReloadAsync();
            }
        }
    }

    public LogField SelectedField
    {
        get => _field;
        set
        {
            if (value is not null && _field != value)
            {
                _field = value;
                _ = ReloadAsync();
            }
        }
    }

    public ChartData? PowerChart => _powerChart;

    public ChartData? FieldChart => _fieldChart;

    public string InfoText => _infoText;

    public string StatusText => _statusText;

    public string BucketText => _bucketText;

    /// <summary>Charts are only worth querying while the tab is on screen.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            _refresh.IsEnabled = value;
            if (value)
            {
                _ = ReloadAsync();
            }
        }
    }

    private static string DescribeBucket(TimeSpan bucket) =>
        bucket.TotalMinutes < 1 ? $"{bucket.TotalSeconds:0} с" : bucket.TotalHours < 1 ? $"{bucket.TotalMinutes:0} хв" : $"{bucket.TotalHours:0.#} год";

    private static TimeSpan BucketFor(TimeSpan window)
    {
        var seconds = Math.Max(StationJournal.SampleInterval.TotalSeconds, Math.Ceiling(window.TotalSeconds / TargetPoints));
        return TimeSpan.FromSeconds(seconds);
    }

    private static ChartData WithAxisRules(ChartData data, LogField field)
    {
        var whole = data with { MinStep = field.IsInteger ? 1 : 0 };
        return field.Unit switch
        {
            Percent => whole with { AxisMin = 0, AxisMax = 100 },
            Watts => whole with { AxisMin = 0, MinTop = MinTopWatts },
            _ => whole,
        };
    }

    private (DateTime From, DateTime To) PeriodUtc(JournalInfo info)
    {
        var to = DateTime.UtcNow;
        return _range switch
        {
            JournalRange.ThirtyMinutes => (to.AddMinutes(-30), to),
            JournalRange.SixHours => (to.AddHours(-6), to),
            JournalRange.Day => (to.AddDays(-1), to),
            JournalRange.Week => (to.AddDays(-7), to),
            JournalRange.Month => (to.AddDays(-30), to),
            _ => (info.FirstSample?.ToUniversalTime() ?? to.AddMinutes(-30), to),
        };
    }

    private async Task ReloadAsync()
    {
        var version = ++_reloadVersion;
        var range = _range;
        var field = _field;
        try
        {
            var result = await Task.Run(() =>
            {
                var info = _journal.GetInfo();
                var (from, to) = PeriodUtc(info);
                var bucket = BucketFor(to - from);
                return (Info: info, From: from.ToLocalTime(), To: to.ToLocalTime(), Bucket: bucket,
                    Power: _journal.QueryPower(from, to, bucket),
                    Values: _journal.QueryAverages(field, from, to, bucket));
            });

            // A newer choice has been made while this one was loading.
            if (version != _reloadVersion)
            {
                return;
            }

            var maxGap = TimeSpan.FromTicks(Math.Max(result.Bucket.Ticks * 3, TimeSpan.FromSeconds(45).Ticks));
            _powerChart = new ChartData(
                result.From,
                result.To,
                [
                    new ChartSeries("Вхід", Watts, result.Power.Select(p => new TimePoint(p.Time, p.InputWatts)).ToList()),
                    new ChartSeries("Вихід", Watts, result.Power.Select(p => new TimePoint(p.Time, p.OutputWatts)).ToList()),
                ],
                maxGap)
            {
                AxisMin = 0,
                MinTop = OverviewMinTopWatts,
            };
            _fieldChart = WithAxisRules(new ChartData(result.From, result.To, [new ChartSeries(field.Title, field.Unit, result.Values)], maxGap), field);
            _bucketText = result.Bucket > StationJournal.SampleInterval ? $"Точка на графіку: середнє за {DescribeBucket(result.Bucket)}" : string.Empty;
            _infoText = result.Info.FirstSample is { } first
                ? $"Журнал: {(double)result.Info.SizeBytes / Megabyte:0.#} МБ, ведеться з {first.ToString("dd.MM.yyyy HH:mm", Ukrainian)}"
                : "Журнал поки порожній";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            SetStatus($"Не вдалося прочитати журнал: {ex.Message}");
        }
    }

    private async Task ExportAsync()
    {
        var path = _askExportPath($"bluetti-{DateTime.Now:yyyy-MM-dd}.csv");
        if (path is null)
        {
            return;
        }

        try
        {
            var culture = CultureInfo.CurrentCulture;
            var rows = await Task.Run(() =>
            {
                var (from, to) = PeriodUtc(_journal.GetInfo());
                return _journal.ExportCsv(path, from, to, culture);
            });
            SetStatus($"Експортовано рядків: {rows}. Файл: {path}");
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            SetStatus($"Не вдалося експортувати: {ex.Message}");
        }
    }

    private void OpenFolder()
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(_journal.FilePath))!;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void SetStatus(string text)
    {
        _statusText = text;
        _dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText))));
    }
}
