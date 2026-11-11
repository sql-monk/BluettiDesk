using BluettiDesk.Core.Journal;
using BluettiDesk.Core.Outages;
using BluettiDesk.Core.Station;

namespace BluettiDesk.Tests;

/// <summary>Whether the charge lasts until the grid is back, by the schedule; and how a day of fact reads against it.</summary>
public sealed class BackupForecastTests
{
    private const string Queue = "GPV2.1";

    // Today in the fixture: no power 00:00–00:30, 06:00–11:30 and 17:00–24:00.
    private static readonly OutageSchedule TodayOnly = OutageSchedule.Parse(ScheduleJson.Build());
    private static readonly string[] AllDay = Enumerable.Repeat("yes", 24).ToArray();

    [Fact]
    public void TheRuntimeModelMatchesTheJournal()
    {
        // 54 % at 85 W: 729 Wh from the battery at 115 W.
        Assert.Equal(380, BatteryRuntime.Minutes(54, 85, acOutputOn: true));

        // A real stretch on battery: 100 % to 18 % at 125 W took 7.3 h.
        Assert.InRange(BatteryRuntime.Minutes(82, 125, acOutputOn: true), 0.95 * 438, 1.05 * 438);
        Assert.Equal(0, BatteryRuntime.Minutes(0, 85, acOutputOn: true));
    }

    [Fact]
    public void WithTheGridThereTheNextOutageIsWhatCounts()
    {
        var lasts = BackupForecast.For(TodayOnly, Queue, OutageScheduleTests.Kyiv(3, 13), gridPresent: true, runtimeMinutes: 380)!;

        Assert.Equal(BackupCase.NextOutage, lasts.Case);
        Assert.Equal(OutageScheduleTests.Kyiv(6, 0), lasts.FromUtc);
        Assert.Equal(OutageScheduleTests.Kyiv(11, 30), lasts.UntilUtc);
        Assert.True(lasts.Reaches);
        Assert.Equal(50, lasts.MarginMinutes);
        Assert.Equal("Витримає відключення 06:00–11:30", lasts.Title);
        Assert.Equal("запас ≈ 50 хв", lasts.MarginText);
        Assert.Equal("Без мережі заряду 54 % при споживанні ≈ 85 Вт вистачить на ≈ 6 год 20 хв. Станція ще заряджається, тож запас зросте.", lasts.Detail(54, 85.4, charging: true));

        var short_ = BackupForecast.For(TodayOnly, Queue, OutageScheduleTests.Kyiv(3, 13), gridPresent: true, runtimeMinutes: 200)!;

        Assert.False(short_.Reaches);
        Assert.Equal(-130, short_.MarginMinutes);
        Assert.Equal("Не витримає відключення 06:00–11:30", short_.Title);
        Assert.Equal("бракує ≈ 2 год 10 хв", short_.MarginText);
    }

    [Fact]
    public void WithoutTheGridTheChargeHasToLastUntilTheScheduleBringsItBack()
    {
        var forecast = BackupForecast.For(TodayOnly, Queue, OutageScheduleTests.Kyiv(7, 0), gridPresent: false, runtimeMinutes: 200)!;

        Assert.Equal(BackupCase.OnBattery, forecast.Case);
        Assert.Equal(OutageScheduleTests.Kyiv(10, 20), forecast.EmptyUtc);
        Assert.Equal(-70, forecast.MarginMinutes);
        Assert.Equal("Не дотягне до світла о 11:30", forecast.Title);
        Assert.Equal("бракує ≈ 1 год 10 хв", forecast.MarginText);
        Assert.Equal("Заряду 40 % при споживанні ≈ 120 Вт вистачить на ≈ 3 год 20 хв, до 10:20.", forecast.Detail(40, 120, charging: false));
    }

    [Fact]
    public void AnOutageRunningPastTheKnownScheduleIsComparedWithWhatIsKnown()
    {
        var evening = OutageScheduleTests.Kyiv(18, 0);

        var unknown = BackupForecast.For(TodayOnly, Queue, evening, gridPresent: false, runtimeMinutes: 600)!;
        Assert.Null(unknown.UntilUtc);
        Assert.Null(unknown.Reaches);
        Assert.Equal("Коли дадуть світло, графік ще не каже", unknown.Title);
        Assert.Null(unknown.MarginText);

        var tooShort = BackupForecast.For(TodayOnly, Queue, evening, gridPresent: false, runtimeMinutes: 120)!;
        Assert.False(tooShort.Reaches);
        Assert.Equal("Не дотягне до кінця відключення", tooShort.Title);
        Assert.Equal("бракує щонайменше ≈ 4 год", tooShort.MarginText);

        var withTomorrow = OutageSchedule.Parse(ScheduleJson.Build(tomorrow: AllDay));
        var lasts = BackupForecast.For(withTomorrow, Queue, evening, gridPresent: false, runtimeMinutes: 600)!;
        Assert.Equal("Дотягне до світла завтра о 00:00", lasts.Title);
        Assert.Equal("запас ≈ 4 год", lasts.MarginText);
    }

    [Fact]
    public void TheEveningOutageEndsAtMidnight()
    {
        var withTomorrow = OutageSchedule.Parse(ScheduleJson.Build(tomorrow: AllDay));

        var ahead = BackupForecast.For(withTomorrow, Queue, OutageScheduleTests.Kyiv(12, 0), gridPresent: true, runtimeMinutes: 480)!;
        Assert.Equal("Витримає відключення 17:00–24:00", ahead.Title);
        Assert.Equal("запас ≈ 1 год", ahead.MarginText);

        // The schedule says the outage has begun, yet the grid is still there.
        var begun = BackupForecast.For(withTomorrow, Queue, OutageScheduleTests.Kyiv(18, 0), gridPresent: true, runtimeMinutes: 400)!;
        Assert.Equal(OutageScheduleTests.Kyiv(18, 0), begun.FromUtc);
        Assert.Equal("Витримає відключення до 24:00", begun.Title);
    }

    [Fact]
    public void NoGridWhileTheSchedulePromisesItIsOutsideTheSchedule()
    {
        var forecast = BackupForecast.For(TodayOnly, Queue, OutageScheduleTests.Kyiv(14, 0), gridPresent: false, runtimeMinutes: 300)!;

        Assert.Equal(BackupCase.Unplanned, forecast.Case);
        Assert.Null(forecast.Reaches);
        Assert.Equal("Світла немає поза графіком", forecast.Title);
        Assert.Equal(
            "Заряду 50 % при споживанні ≈ 100 Вт вистачить на ≈ 5 год, до 19:00. За графіком світло мало бути, тож коли його дадуть, невідомо.",
            forecast.Detail(50, 100, charging: false));
    }

    [Fact]
    public void ADayWithoutOutagesHasNothingToCarry()
    {
        var schedule = OutageSchedule.Parse(ScheduleJson.Build(today: AllDay, tomorrow: AllDay));

        var forecast = BackupForecast.For(schedule, Queue, OutageScheduleTests.Kyiv(12, 0), gridPresent: true, runtimeMinutes: 300)!;

        Assert.Equal(BackupCase.NoOutage, forecast.Case);
        Assert.Equal("Відключень у графіку попереду немає", forecast.Title);
        Assert.Null(forecast.MarginText);
    }

    [Fact]
    public void EveryMinuteOfFactIsReadAgainstTheSchedule()
    {
        var plan = TodayOnly.SlotsFor(ScheduleJson.Day, Queue)!;
        PresenceSpan[] fact =
        [
            new(OutageScheduleTests.Kyiv(0, 0), OutageScheduleTests.Kyiv(6, 10), true),
            new(OutageScheduleTests.Kyiv(6, 10), OutageScheduleTests.Kyiv(11, 40), false),
            new(OutageScheduleTests.Kyiv(11, 40), OutageScheduleTests.Kyiv(12, 0), true),
        ];

        var day = DayComparison.Build(ScheduleJson.Day, plan, fact);

        Assert.Equal(FactVerdict.UnexpectedOn, day.Verdicts[10]);         // 00:10, the schedule said no power
        Assert.Equal(FactVerdict.On, day.Verdicts[3 * 60]);
        Assert.Equal(FactVerdict.Off, day.Verdicts[8 * 60]);
        Assert.Equal(FactVerdict.UnplannedOff, day.Verdicts[11 * 60 + 35]);
        Assert.Equal(FactVerdict.Unknown, day.Verdicts[13 * 60]);

        // Without a schedule the minutes only say whether the grid was there.
        Assert.Equal(FactVerdict.On, DayComparison.Build(ScheduleJson.Day, null, fact).Verdicts[10]);
    }

    [Fact]
    public void AnOutageDuringAPossibleOneIsNeitherMatchedNorUnplanned()
    {
        var today = ScheduleJson.Today.ToArray();
        today[6] = "maybe"; // 06:00–07:00
        var plan = OutageSchedule.Parse(ScheduleJson.Build(today: today)).SlotsFor(ScheduleJson.Day, Queue)!;
        PresenceSpan[] fact = [new(OutageScheduleTests.Kyiv(6, 0), OutageScheduleTests.Kyiv(8, 0), false)];

        var day = DayComparison.Build(ScheduleJson.Day, plan, fact);

        Assert.Equal(60, day.OffWhileMaybeMinutes);
        Assert.Equal(60, day.MatchedOffMinutes);
        Assert.Equal(0, day.UnplannedOffMinutes);
        Assert.Equal(FactVerdict.Off, day.Verdicts[6 * 60 + 30]);
        Assert.Contains("у час можливих відключень 1 год", day.Summary);
    }
}
