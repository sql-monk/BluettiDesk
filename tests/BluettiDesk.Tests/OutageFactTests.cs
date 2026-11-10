using BluettiDesk.Core.Journal;
using BluettiDesk.Core.Outages;
using BluettiDesk.Core.Station;

namespace BluettiDesk.Tests;

/// <summary>The schedule against what the station measured, and how both are kept.</summary>
public sealed class OutageFactTests : IDisposable
{
    private static readonly DateOnly Day = ScheduleJson.Day;
    private readonly string _folder = Directory.CreateTempSubdirectory("bluettidesk-outages-").FullName;

    private string DbPath => Path.Combine(_folder, "journal.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void TheGridIsJudgedByTheMeasuredVoltage()
    {
        var start = OutageScheduleTests.Kyiv(10, 0);
        using (var journal = new StationJournal(DbPath))
        {
            for (var s = 0; s < 60; s += 10)
            {
                journal.Record(start.AddSeconds(s), Mains(231));
            }

            for (var s = 60; s <= 100; s += 10)
            {
                journal.Record(start.AddSeconds(s), Mains(2.1));
            }

            // Five minutes without readings: the app was closed.
            journal.Record(start.AddSeconds(400), Mains(229));

            var spans = journal.QueryMainsPresence(start, start.AddSeconds(430));

            Assert.Equal(
                [
                    new PresenceSpan(start, start.AddSeconds(60), true),
                    new PresenceSpan(start.AddSeconds(60), start.AddSeconds(160), false),
                    new PresenceSpan(start.AddSeconds(400), start.AddSeconds(430), true),
                ],
                spans);
        }
    }

    [Fact]
    public void ADayComparesMinuteByMinute()
    {
        var plan = OutageSchedule.Parse(ScheduleJson.Build()).SlotsFor(Day, "GPV2.1")!;
        PresenceSpan[] fact =
        [
            new(OutageScheduleTests.Kyiv(0, 0), OutageScheduleTests.Kyiv(6, 10), true),
            new(OutageScheduleTests.Kyiv(6, 10), OutageScheduleTests.Kyiv(11, 40), false),
            new(OutageScheduleTests.Kyiv(11, 40), OutageScheduleTests.Kyiv(12, 0), true),
        ];

        var day = DayComparison.Build(Day, plan, fact);

        Assert.Equal(780, day.PlannedOffMinutes);      // 00:00–00:30, 06:00–11:30, 17:00–24:00
        Assert.Equal(720, day.KnownMinutes);           // the journal covers the morning only
        Assert.Equal(330, day.ActualOffMinutes);
        Assert.Equal(320, day.MatchedOffMinutes);      // 06:10–11:30
        Assert.Equal(10, day.UnplannedOffMinutes);     // 11:30–11:40
        Assert.Equal(40, day.PowerWhilePlannedOffMinutes); // 00:00–00:30 and 06:00–06:10
        Assert.Equal(GridFact.Unknown, day.Fact[13 * 60]);
        Assert.Equal(
            "За графіком без світла 13 год. Станція не бачила мережі 5 год 30 хв, а дані є за 12 год. "
            + "Збіглося з графіком 5 год 20 хв, поза графіком 10 хв, світло було, хоч за графіком не мало, 40 хв.",
            day.Summary);
    }

    [Fact]
    public void ADayWithoutReadingsSaysSo()
    {
        var day = DayComparison.Build(Day, null, []);

        Assert.Equal(0, day.KnownMinutes);
        Assert.Equal("Графіка на цей день не збережено. Даних станції за цей день немає.", day.Summary);
    }

    [Fact]
    public void TheSameScheduleIsKeptOnce()
    {
        using var store = new OutageScheduleStore(DbPath);
        var json = ScheduleJson.Build();
        var again = ScheduleJson.Build(fetchedAt: 1794350000000, checkedAt: "00:30 11.11.2026");
        var first = OutageScheduleTests.Kyiv(0, 30);

        Assert.True(store.Save(OutageSchedule.Parse(json), json, first));
        Assert.False(store.Save(OutageSchedule.Parse(again), again, first.AddMinutes(15)));

        var version = Assert.Single(store.VersionsOf(Day, "GPV2.1"));
        Assert.Equal(first, version.FirstSeenUtc);
        Assert.Equal(first.AddMinutes(15), version.LastSeenUtc);
        Assert.Equal(first.AddMinutes(15), store.Latest()!.Value.LastSeenUtc);
    }

    [Fact]
    public void EveryChangeOfADayIsKept()
    {
        using var store = new OutageScheduleStore(DbPath);
        var changed = ScheduleJson.Today.ToArray();
        changed[0] = "yes";
        var time = OutageScheduleTests.Kyiv(0, 30);

        Save(store, ScheduleJson.Build(), time);
        Save(store, ScheduleJson.Build(today: changed), time.AddHours(1));

        // Tomorrow appears, today stays as it was: still the second version of today.
        Save(store, ScheduleJson.Build(today: changed, tomorrow: ScheduleJson.Hours("yes")), time.AddHours(2));

        var versions = store.VersionsOf(Day, "GPV2.1");
        Assert.Equal(2, versions.Count);
        Assert.Equal(OutageSlot.Off, versions[0].Slots[0]);
        Assert.Equal(OutageSlot.On, versions[1].Slots[0]);
        Assert.Equal(time.AddHours(2), versions[1].LastSeenUtc);
        Assert.Single(store.VersionsOf(Day.AddDays(1), "GPV2.1"));
    }

    [Fact]
    public void TheJournalAndTheScheduleShareOneFile()
    {
        using var journal = new StationJournal(DbPath);
        using var store = new OutageScheduleStore(DbPath);
        var json = ScheduleJson.Build();

        journal.Record(OutageScheduleTests.Kyiv(1, 0), Mains(230));
        store.Save(OutageSchedule.Parse(json), json, OutageScheduleTests.Kyiv(1, 0));

        Assert.Single(journal.QueryMainsPresence(OutageScheduleTests.Kyiv(0, 0), OutageScheduleTests.Kyiv(2, 0)));
        Assert.NotNull(store.Latest());
    }

    [Fact]
    public void SuggestionsFindAPlaceByAnyPartOfItsName()
    {
        var catalog = StreetCatalog.Parse("""{"м. Бровари":["вул. Київська","вул. Шевченка"],"м. Вишгород":["вул. Шевченка"],"с. Демидів":["Без вулиці"]}""");

        Assert.Equal(["м. Бровари"], StreetCatalog.Suggest(catalog.Cities, "бровар"));
        Assert.Equal(["м. Вишгород"], StreetCatalog.Suggest(catalog.Cities, "ВИШ"));
        Assert.Equal(["вул. Шевченка"], StreetCatalog.Suggest(catalog.StreetsOf("м. Бровари"), "шевч"));
        Assert.Empty(catalog.StreetsOf("м. Київ"));
        Assert.Equal(3, StreetCatalog.Suggest(catalog.Cities, " ").Count);
    }

    [Fact]
    public void SettingsKeepTheChosenAddress()
    {
        var path = Path.Combine(_folder, "outages.json");
        var settings = new OutageSettings(DtekSite.All[1].Url, new OutageAddress("м. Київ", "вул. Хрещатик", "1"));

        settings.Save(path);

        Assert.Equal(settings, OutageSettings.Load(path));
        Assert.Equal(OutageSettings.Empty, OutageSettings.Load(Path.Combine(_folder, "missing.json")));
        Assert.Equal("dtek-krem", DtekSite.Default.Key);
    }

    private static void Save(OutageScheduleStore store, string json, DateTime utc) => store.Save(OutageSchedule.Parse(json), json, utc);

    private static RegisterImage Mains(double volts)
    {
        var image = new RegisterImage();
        image.Store(1314, [(ushort)Math.Round(volts * 10)]);
        return image;
    }
}
