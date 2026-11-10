using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using BluettiDesk.Core.Journal;
using BluettiDesk.Core.Outages;
using BluettiDesk.Ui;

namespace BluettiDesk.Outages;

/// <summary>One strip with its caption: a queue of a day, a day of the week, or a day of the comparison.</summary>
public sealed record StripRow(string? Title, IReadOnlyList<StripSegment> Segments, int NowMinute, string Detail, bool IsCurrent = false);

/// <summary>A past day: the schedule it had and what the station saw.</summary>
public sealed record ComparisonRow(string Title, IReadOnlyList<StripSegment> Plan, IReadOnlyList<StripSegment> Fact, int NowMinute, string Summary, string? Versions);

/// <summary>
/// The "Відключення" tab: the outage schedule of the chosen address from the DTEK site, every version of it kept in
/// the journal, and each day of it next to what the station measured.
/// </summary>
public sealed class OutageViewModel : INotifyPropertyChanged
{
    private const int ComparisonDays = 7;
    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA");
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(40);
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromDays(7);
    private static readonly string[] WeekdayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Нд"];

    private readonly string _settingsPath;
    private readonly string _dataFolder;
    private readonly DtekSiteReader _site;
    private readonly OutageScheduleStore? _store;
    private readonly StationJournal? _journal;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _clock;
    private OutageSettings _settings;
    private OutageSchedule? _schedule;
    private DateTime? _scheduleSeenUtc;
    private StreetCatalog? _catalog;
    private string? _catalogSite;
    private bool _catalogLoading;
    private IReadOnlyList<string>? _houses;
    private (string City, string Street)? _housesFor;
    private bool _housesLoading;
    private bool _editing;
    private DtekSite _editSite;
    private string _cityText = string.Empty;
    private string _streetText = string.Empty;
    private string _houseText = string.Empty;
    private IReadOnlyList<ComparisonRow> _comparison = [];
    private string? _storeError;
    private bool _isActive;
    private bool _refreshing;
    private bool _checkAnnounced;
    private int _comparisonVersion;

    public OutageViewModel(
        OutageSettings settings,
        string settingsPath,
        string dataFolder,
        DtekSiteReader site,
        OutageScheduleStore? store,
        StationJournal? journal,
        Dispatcher dispatcher)
    {
        _settings = settings;
        _settingsPath = settingsPath;
        _dataFolder = dataFolder;
        _site = site;
        _store = store;
        _journal = journal;
        _dispatcher = dispatcher;
        _editSite = settings.Site;
        _site.StateChanged += () => _dispatcher.BeginInvoke(OnSiteStateChanged);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = RefreshInterval };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _clock = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _clock.Tick += (_, _) =>
        {
            Notify(string.Empty);
            if (_isActive && DateTime.Now.Minute % 5 == 0)
            {
                _ = LoadComparisonAsync();
            }
        };

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        PassCheckCommand = new RelayCommand(() => _ = PassCheckAsync());
        OpenSiteCommand = new RelayCommand(() => _ = _site.OpenSiteAsync(_settings.Site));
        EditAddressCommand = new RelayCommand(BeginEdit);
        SaveAddressCommand = new RelayCommand(SaveAddress);
        CancelEditCommand = new RelayCommand(() => IsEditing = false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The site wants a person to pass its check; raised once until a schedule comes through again.</summary>
    public event Action? CheckRequired;

    public ICommand RefreshCommand { get; }

    public ICommand PassCheckCommand { get; }

    public ICommand OpenSiteCommand { get; }

    public ICommand EditAddressCommand { get; }

    public ICommand SaveAddressCommand { get; }

    public ICommand CancelEditCommand { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (value)
            {
                _ = LoadComparisonAsync();
                if (IsEditing && _catalog is null)
                {
                    _ = LoadCatalogAsync();
                }
            }
        }
    }

    // ---------- the address ----------

    public bool HasAddress => _settings.Address.IsComplete;

    public string AddressText => HasAddress ? _settings.Address.ToString() : "Адресу не вибрано";

    public string SiteText => _settings.Site.Name;

    public bool IsEditing
    {
        get => _editing || !HasAddress;
        private set
        {
            _editing = value;
            Notify(string.Empty);
        }
    }

    public bool CanCancelEdit => HasAddress;

    public bool ShowEditButton => HasAddress && !IsEditing;

    public bool CanRefresh => HasAddress && !IsEditing && !IsBusy;

    public IReadOnlyList<DtekSite> Sites => DtekSite.All;

    public DtekSite EditSite
    {
        get => _editSite;
        set
        {
            if (value is not null && value != _editSite)
            {
                _editSite = value;
                _houses = null;
                _housesFor = null;
                Notify(string.Empty);
                _ = LoadCatalogAsync();
            }
        }
    }

    public string CityText
    {
        get => _cityText;
        set
        {
            _cityText = value ?? string.Empty;
            Notify(nameof(CityText), nameof(StreetOptions), nameof(EditorHint), nameof(CanSave), nameof(StreetEnabled));
        }
    }

    public string StreetText
    {
        get => _streetText;
        set
        {
            _streetText = value ?? string.Empty;
            Notify(nameof(StreetText), nameof(EditorHint), nameof(CanSave), nameof(HouseEnabled));
            if (StreetKnown)
            {
                _ = LoadHousesAsync();
            }
        }
    }

    public string HouseText
    {
        get => _houseText;
        set
        {
            _houseText = value ?? string.Empty;
            Notify(nameof(HouseText), nameof(EditorHint), nameof(CanSave));
        }
    }

    public IReadOnlyCollection<string>? CityOptions => _catalog?.Cities;

    public IReadOnlyList<string>? StreetOptions => _catalog?.StreetsOf(_cityText.Trim());

    public IReadOnlyList<string>? HouseOptions => HousesHere?.Where(h => h != "-").ToList();

    public bool StreetEnabled => _catalog is null || CityKnown;

    public bool HouseEnabled => _catalog is null || StreetKnown;

    public string EditorHint
    {
        get
        {
            if (_catalogLoading)
            {
                return "Завантажую з сайту ДТЕК перелік населених пунктів і вулиць…";
            }

            if (_catalog is null)
            {
                return "Переліку адрес із сайту ДТЕК поки немає. Назви треба писати точно так, як їх пропонує форма на сайті.";
            }

            if (!CityKnown)
            {
                return "Почніть вводити назву населеного пункту і виберіть її зі списку.";
            }

            if (!StreetKnown)
            {
                return "Почніть вводити вулицю і виберіть її зі списку.";
            }

            if (_housesLoading)
            {
                return "Завантажую номери будинків цієї вулиці…";
            }

            if (HousesHere is { } houses && houses.Contains("-"))
            {
                return "Уся вулиця в одній черзі, тож номер будинку може бути будь-який.";
            }

            if (HousesHere is null)
            {
                return "Номерів будинків із сайту немає. Впишіть номер так, як на сайті.";
            }

            return HouseKnown ? "Адресу знайдено на сайті ДТЕК." : "Виберіть номер будинку зі списку.";
        }
    }

    public bool CanSave =>
        _cityText.Trim().Length > 0 && _streetText.Trim().Length > 0 && _houseText.Trim().Length > 0
        && (_catalog is null || (CityKnown && StreetKnown))
        && (HousesHere is null || HouseKnown);

    // ---------- the site ----------

    public bool IsBusy => _refreshing || _catalogLoading || _housesLoading || _site.State == SiteState.Working;

    public bool NeedsCheck => _site.State == SiteState.CheckRequired;

    public string? StatusText
    {
        get
        {
            var since = _scheduleSeenUtc is { } seen ? $" Показано графік, отриманий {Stamp(seen)}." : string.Empty;
            var text = _site.State switch
            {
                SiteState.Working => _refreshing ? "Оновлюю графік із сайту ДТЕК…" : "Звертаюся до сайту ДТЕК…",
                SiteState.Offline or SiteState.Error => _site.Message + since,
                SiteState.CheckRequired => _site.Message + " Натисніть «Пройти перевірку»." + since,
                _ when _scheduleSeenUtc is { } last && DateTime.UtcNow - last > StaleAfter => "Графік давно не оновлювався." + since,
                _ => null,
            };
            return _storeError is null ? text : $"{text} {_storeError}".Trim();
        }
    }

    // ---------- the schedule ----------

    public bool HasSchedule => _schedule is not null && HasAddress && !IsEditing;

    public string? AttentionText => HasSchedule && _schedule!.Attention.Length > 0 ? _schedule.Attention : null;

    public string? OutageTitle => HasSchedule && _schedule!.Flags.ShowCurrentOutage && _schedule.Notice.IsReported && !_schedule.Notice.ByCek
        ? "За вашою адресою зараз відсутня електроенергія"
        : null;

    public string? OutageText
    {
        get
        {
            if (!HasSchedule || !_schedule!.Flags.ShowCurrentOutage)
            {
                return null;
            }

            var notice = _schedule.Notice;
            if (notice.ByCek)
            {
                return "Відключення за цією адресою виконує ПрАТ «ЦЕК». Подробиці є на сайті ДТЕК.";
            }

            if (!notice.IsReported)
            {
                return null;
            }

            var reason = notice.Type == "1" ? "планові ремонтні роботи" : notice.SubType.Length > 0 ? notice.SubType : "не вказано";
            var lines = new List<string>
            {
                $"Причина: {reason}",
                $"Час початку: {(notice.Start.Length > 0 ? notice.Start : "не вказано")}",
                $"Орієнтовне відновлення: {(notice.End.Length > 0 ? $"до {notice.End}" : "не вказано")}",
            };
            if (_schedule.AddressCheckedAt.Length > 0)
            {
                lines.Add($"Дані станом на {_schedule.AddressCheckedAt}");
            }

            return string.Join('\n', lines);
        }
    }

    public string? HiddenText
    {
        get
        {
            if (!HasSchedule)
            {
                return null;
            }

            if (!_schedule!.ScheduleShown)
            {
                return "Сайт ДТЕК зараз не показує графік для цієї адреси.";
            }

            return _schedule.Queues.Count > 1
                ? "Будинок має складну схему підключення: квартири можуть відключатися за різними графіками. Нижче показано всі лінії."
                : null;
        }
    }

    public bool ScheduleVisible => HasSchedule && _schedule!.ScheduleShown;

    public string QueueText => _schedule is { Queues.Count: > 0 } s ? string.Join(", ", s.Queues.Select(q => q.Name)) : string.Empty;

    private OutageOutlook? Outlook => _schedule is { Queues.Count: 1 } s && s.ScheduleShown ? OutageOutlook.For(s, s.Queues[0].Id, DateTime.UtcNow) : null;

    public bool HeroVisible => ScheduleVisible && Outlook is not null;

    public string HeroKind => Outlook?.Current.ToString() ?? nameof(OutageSlot.Unknown);

    public string HeroKicker
    {
        get
        {
            var caption = _schedule is not { } s ? string.Empty
                : s.Notice.IsReported ? "За графіком, але на сайті зафіксовано відключення"
                : s.SchedulesSuspended ? "За графіком, що зараз не діє"
                : "За графіком зараз";
            return $"{caption}, {KyivTime.FromUtc(DateTime.UtcNow):HH:mm}";
        }
    }

    public string HeroTitle => Outlook is { } o ? OutageTexts.Label(o.Current) : string.Empty;

    public string HeroNext => Outlook?.NextText ?? string.Empty;

    public string TodayTitle => $"Сьогодні, {OutageTexts.DayTitle(Today)}";

    public IReadOnlyList<StripRow> TodayRows => DayRows(Today, KyivTime.MinuteOfDay(DateTime.UtcNow));

    public string TomorrowTitle => $"Завтра, {OutageTexts.DayTitle(Today.AddDays(1))}";

    public IReadOnlyList<StripRow> TomorrowRows => DayRows(Today.AddDays(1), -1);

    public bool TomorrowEmpty => ScheduleVisible && TomorrowRows.Count == 0;

    public bool TodayEmpty => ScheduleVisible && TodayRows.Count == 0;

    public bool WeekVisible => ScheduleVisible && _schedule!.WeekPlanShown;

    public IReadOnlyList<StripRow> WeekRows
    {
        get
        {
            if (!WeekVisible)
            {
                return [];
            }

            var today = (int)Today.DayOfWeek is 0 ? 7 : (int)Today.DayOfWeek;
            var rows = new List<StripRow>();
            foreach (var queue in _schedule!.Queues)
            {
                if (!_schedule.WeekPlan.TryGetValue(queue.Id, out var week))
                {
                    continue;
                }

                for (var day = 1; day <= 7; day++)
                {
                    var title = _schedule.Queues.Count > 1 ? $"{WeekdayNames[day - 1]} · {queue.Name}" : WeekdayNames[day - 1];
                    rows.Add(new StripRow(title, week.TryGetValue(day, out var slots) ? StripSegment.FromSlots(slots) : [], -1, string.Empty, day == today));
                }
            }

            return rows;
        }
    }

    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (_schedule is { SiteUpdatedAt.Length: > 0 } s)
            {
                parts.Add($"Графік на сайті оновлено: {s.SiteUpdatedAt}");
            }

            if (_scheduleSeenUtc is { } seen)
            {
                parts.Add($"Отримано: {Stamp(seen)}");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>One line for the overview: what the schedule says now and next.</summary>
    public string? OverviewText => HasSchedule && Outlook is { } o ? $"За графіком: {OutageTexts.Label(o.Current).ToLower(Ukrainian)} · {o.NextText}" : null;

    // ---------- schedule and fact ----------

    public IReadOnlyList<ComparisonRow> Comparison => _comparison;

    public bool HasComparison => _comparison.Count > 0;

    public bool ComparisonEmpty => CanCompare && _comparison.Count == 0;

    public bool CanCompare => _journal is not null && _store is not null;

    public string ComparisonHint => !CanCompare
        ? "Журнал не ведеться, тож порівняти графік із фактом не вийде."
        : "Верхня смуга: останній графік на сайті для цього дня. Нижня: чи бачила станція мережу. Сірим позначено час, коли застосунок не був підключений до станції.";

    private static DateOnly Today => KyivTime.DayOf(DateTime.UtcNow);

    public void Start()
    {
        if (_store is not null)
        {
            try
            {
                if (_store.Latest() is { } latest)
                {
                    var schedule = OutageSchedule.Parse(latest.Json);
                    if (schedule.Address == _settings.Address)
                    {
                        _schedule = schedule;
                        _scheduleSeenUtc = latest.LastSeenUtc;
                    }
                }
            }
            catch (Exception ex) when (ex is FormatException or Microsoft.Data.Sqlite.SqliteException)
            {
                _storeError = $"Збережений графік не прочитано: {ex.Message}";
            }
        }

        _clock.Start();
        _refreshTimer.Start();
        if (HasAddress)
        {
            _ = RefreshAsync();
        }
        else
        {
            // Nothing to follow yet: get the site's list of addresses ready for choosing one.
            _ = LoadCatalogAsync();
        }

        Notify(string.Empty);
    }

    public async Task RefreshAsync()
    {
        if (!HasAddress || _refreshing)
        {
            return;
        }

        _refreshing = true;
        Notify(string.Empty);
        try
        {
            var settings = _settings;
            var json = await _site.ReadScheduleAsync(settings.Site, settings.Address);
            if (json is not null && settings == _settings)
            {
                var schedule = OutageSchedule.Parse(json);
                var now = DateTime.UtcNow;
                _schedule = schedule;
                _scheduleSeenUtc = now;
                _checkAnnounced = false;
                if (_store is { } store)
                {
                    try
                    {
                        await Task.Run(() => store.Save(schedule, json, now));
                        _storeError = null;
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex)
                    {
                        _storeError = $"Графік не збережено в журнал: {ex.Message}";
                    }
                }

                _ = LoadComparisonAsync();
                if (CatalogStale(settings.Site))
                {
                    _ = LoadCatalogAsync(settings.Site, forceSite: true);
                }
            }
        }
        catch (FormatException ex)
        {
            _storeError = ex.Message;
        }
        finally
        {
            _refreshing = false;
            Notify(string.Empty);
        }
    }

    private static string Stamp(DateTime utc) => utc.ToLocalTime().ToString("dd.MM HH:mm", Ukrainian);

    private bool CityKnown => _catalog?.Contains(_cityText.Trim()) == true;

    private bool StreetKnown => CityKnown && _catalog!.StreetsOf(_cityText.Trim()).Contains(_streetText.Trim());

    private IReadOnlyList<string>? HousesHere => _housesFor == (_cityText.Trim(), _streetText.Trim()) ? _houses : null;

    private bool HouseKnown => HousesHere is { } houses && (houses.Contains("-") || houses.Contains(_houseText.Trim()));

    private string CatalogPath(DtekSite site) => Path.Combine(_dataFolder, $"{site.Key}-streets.json");

    private bool CatalogStale(DtekSite site)
    {
        var path = CatalogPath(site);
        return !File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > CatalogLifetime;
    }

    private IReadOnlyList<StripRow> DayRows(DateOnly day, int nowMinute)
    {
        if (!ScheduleVisible || !_schedule!.Flags.ShowTableFact)
        {
            return [];
        }

        var rows = new List<StripRow>();
        foreach (var queue in _schedule.Queues)
        {
            if (_schedule.SlotsFor(day, queue.Id) is not { } slots)
            {
                continue;
            }

            var off = OutageTexts.Ranges(slots, OutageSlot.Off);
            var maybe = OutageTexts.Ranges(slots, OutageSlot.Maybe);
            var unknown = OutageTexts.Ranges(slots, OutageSlot.Unknown);
            var details = new List<string>();
            if (off.Length > 0)
            {
                details.Add($"Світла немає: {off}");
            }

            if (maybe.Length > 0)
            {
                details.Add($"Можливо: {maybe}");
            }

            if (unknown.Length > 0)
            {
                details.Add($"Немає даних: {unknown}");
            }

            rows.Add(new StripRow(
                _schedule.Queues.Count > 1 ? $"Лінія {rows.Count + 1} · {queue.Name}" : null,
                StripSegment.FromSlots(slots),
                nowMinute,
                details.Count > 0 ? string.Join('\n', details) : "Відключень у графіку немає"));
        }

        return rows;
    }

    private void BeginEdit()
    {
        _cityText = _settings.Address.City;
        _streetText = _settings.Address.Street;
        _houseText = _settings.Address.House;
        _editSite = _settings.Site;
        IsEditing = true;
        _ = LoadCatalogAsync();
        if (StreetKnown)
        {
            _ = LoadHousesAsync();
        }
    }

    private void SaveAddress()
    {
        if (!CanSave)
        {
            return;
        }

        var settings = new OutageSettings(_editSite.Url, new OutageAddress(_cityText.Trim(), _streetText.Trim(), _houseText.Trim()));
        try
        {
            settings.Save(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _storeError = $"Адресу не збережено: {ex.Message}";
        }

        if (settings != _settings)
        {
            _settings = settings;
            _schedule = null;
            _scheduleSeenUtc = null;
            _comparison = [];
        }

        IsEditing = false;
        _ = RefreshAsync();
    }

    private Task LoadCatalogAsync() => LoadCatalogAsync(_editSite, forceSite: false);

    /// <summary>The site's settlements and streets, from the copy next to the journal while it is fresh, otherwise from the site.</summary>
    private async Task LoadCatalogAsync(DtekSite site, bool forceSite)
    {
        if (_catalogLoading || (!forceSite && _catalogSite == site.Url && _catalog is not null))
        {
            return;
        }

        var path = CatalogPath(site);
        if (!forceSite && File.Exists(path))
        {
            try
            {
                var cached = await Task.Run(() => StreetCatalog.Parse(File.ReadAllText(path)));
                SetCatalog(site, cached);
                if (!CatalogStale(site))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
            }
        }

        _catalogLoading = _catalog is null;
        Notify(string.Empty);
        try
        {
            if (await _site.ReadStreetsAsync(site) is { } json)
            {
                var catalog = StreetCatalog.Parse(json);
                SetCatalog(site, catalog);
                try
                {
                    Directory.CreateDirectory(_dataFolder);
                    await File.WriteAllTextAsync(path, json);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (FormatException)
        {
        }
        finally
        {
            _catalogLoading = false;
            Notify(string.Empty);
        }
    }

    private void SetCatalog(DtekSite site, StreetCatalog catalog)
    {
        if (site.Url != _editSite.Url && _editing)
        {
            return;
        }

        _catalog = catalog;
        _catalogSite = site.Url;
        Notify(string.Empty);
    }

    private async Task LoadHousesAsync()
    {
        var key = (_cityText.Trim(), _streetText.Trim());
        if (_housesFor == key || _housesLoading)
        {
            return;
        }

        _housesLoading = true;
        Notify(string.Empty);
        try
        {
            var houses = await _site.ReadHousesAsync(_editSite, key.Item1, key.Item2);
            if (houses is not null)
            {
                _houses = houses
                    .OrderBy(h => int.TryParse(new string(h.TakeWhile(char.IsDigit).ToArray()), out var n) ? n : int.MaxValue)
                    .ThenBy(h => h, StringComparer.Ordinal)
                    .ToList();
                _housesFor = key;
            }
        }
        finally
        {
            _housesLoading = false;
            Notify(string.Empty);
        }

        // The street changed while its houses were loading.
        if (StreetKnown && _housesFor != (_cityText.Trim(), _streetText.Trim()))
        {
            _ = LoadHousesAsync();
        }
    }

    private async Task PassCheckAsync()
    {
        if (await _site.ShowCheckAsync(_editing || !HasAddress ? _editSite : _settings.Site))
        {
            if (IsEditing)
            {
                await LoadCatalogAsync();
            }
            else
            {
                await RefreshAsync();
            }
        }
    }

    private async Task LoadComparisonAsync()
    {
        if (!CanCompare || _schedule is not { Queues.Count: > 0 } schedule)
        {
            return;
        }

        var version = ++_comparisonVersion;
        var queue = schedule.Queues[0].Id;
        var journal = _journal!;
        var store = _store!;
        var now = DateTime.UtcNow;
        var today = KyivTime.DayOf(now);
        try
        {
            var rows = await Task.Run(() =>
            {
                var result = new List<ComparisonRow>();
                for (var back = 0; back < ComparisonDays; back++)
                {
                    var day = today.AddDays(-back);
                    var from = KyivTime.ToUtc(day, 0);
                    var to = KyivTime.ToUtc(day.AddDays(1), 0);
                    var versions = store.VersionsOf(day, queue);
                    var spans = journal.QueryMainsPresence(from, to < now ? to : now);
                    if (versions.Count == 0 && spans.Count == 0)
                    {
                        continue;
                    }

                    var plan = versions.Count > 0 ? versions[^1].Slots : null;
                    var comparison = DayComparison.Build(day, plan, spans);
                    var nowMinute = back == 0 ? KyivTime.MinuteOfDay(now) : -1;
                    result.Add(new ComparisonRow(
                        back == 0 ? $"Сьогодні, {OutageTexts.DayTitle(day)}" : back == 1 ? $"Учора, {OutageTexts.DayTitle(day)}" : CapitalFirst(OutageTexts.DayTitle(day)),
                        plan is null ? [] : StripSegment.FromSlots(plan),
                        StripSegment.FromFact(comparison.Fact, back == 0 ? nowMinute : KyivTime.MinutesPerDay),
                        nowMinute,
                        comparison.Summary,
                        versions.Count > 1
                            ? $"Графік на цей день змінювався: {versions.Count} {Versions(versions.Count)}, остання від {Stamp(versions[^1].FirstSeenUtc)}."
                            : null));
                }

                return result;
            });

            if (version == _comparisonVersion)
            {
                _comparison = rows;
                Notify(nameof(Comparison), nameof(HasComparison), nameof(ComparisonEmpty));
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _storeError = $"Журнал не прочитано: {ex.Message}";
            Notify(nameof(StatusText));
        }
    }

    private static string Versions(int count) =>
        count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? "версії" : count % 10 == 1 && count % 100 != 11 ? "версія" : "версій";

    private static string CapitalFirst(string text) => text.Length == 0 ? text : char.ToUpper(text[0], Ukrainian) + text[1..];

    private void OnSiteStateChanged()
    {
        if (_site.State == SiteState.CheckRequired && !_checkAnnounced)
        {
            _checkAnnounced = true;
            CheckRequired?.Invoke();
        }

        Notify(string.Empty);
    }

    private void Notify(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
