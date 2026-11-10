using System.Text.Json;
using System.Text.Json.Nodes;
using BluettiDesk.Core.Outages;

namespace BluettiDesk.Tests;

/// <summary>What <c>extract.js</c> returns, shaped like a real answer of the DTEK site (address made up).</summary>
internal static class ScheduleJson
{
    /// <summary>11 November 2026, 00:00 in Kyiv: how the site keys that day.</summary>
    public const long Nov11 = 1794348000;

    public static readonly DateOnly Day = new(2026, 11, 11);

    /// <summary>Hours 1…24 of the queue that day, as the site wrote them.</summary>
    public static readonly string[] Today =
    [
        "first", "yes", "yes", "yes", "yes", "yes", "no", "no", "no", "no", "no", "first",
        "yes", "yes", "yes", "yes", "yes", "no", "no", "no", "no", "no", "no", "no",
    ];

    public static readonly string[] Monday =
    [
        "yes", "yes", "maybe", "maybe", "maybe", "maybe", "maybe", "maybe", "maybe", "yes", "yes", "yes",
        "msecond", "maybe", "maybe", "maybe", "maybe", "maybe", "maybe", "mfirst", "yes", "yes", "yes", "maybe",
    ];

    public static string Build(
        string[]? today = null,
        string[]? tomorrow = null,
        long fetchedAt = 1794349586598,
        string checkedAt = "00:18 11.11.2026",
        string attention = "",
        bool voluntarily = false)
    {
        var fact = new JsonObject { [Nov11.ToString()] = new JsonObject { ["GPV2.1"] = Row(today ?? Today) } };
        if (tomorrow is not null)
        {
            fact[(Nov11 + 86400).ToString()] = new JsonObject { ["GPV2.1"] = Row(tomorrow) };
        }

        var week = new JsonObject();
        for (var day = 1; day <= 7; day++)
        {
            week[day.ToString()] = Row(Monday);
        }

        var result = new JsonObject
        {
            ["state"] = "done",
            ["fetchedAt"] = fetchedAt,
            ["address"] = new JsonObject { ["city"] = "с-ще Приклад", ["street"] = "вул. Тестова", ["house"] = "1" },
            ["groups"] = new JsonArray("GPV2.1"),
            ["names"] = new JsonObject { ["GPV2.1"] = "Черга 2.1" },
            ["info"] = new JsonObject { ["type"] = "", ["subType"] = "", ["start"] = "", ["end"] = "", ["cek"] = false, ["voluntarily"] = voluntarily },
            ["flags"] = new JsonObject
            {
                ["showCurOutage"] = true, ["showCurSchedule"] = true, ["showTableFact"] = true, ["showTableSchedule"] = true, ["showTablePlan"] = false,
            },
            ["addressCheckedAt"] = checkedAt,
            ["factUpdatedAt"] = "10.11.2026 22:07",
            ["fact"] = fact,
            ["plan"] = new JsonObject { ["GPV2.1"] = week },
            ["attention"] = attention,
        };
        return result.ToJsonString();
    }

    public static string[] Hours(string value) => Enumerable.Repeat(value, 24).ToArray();

    private static JsonObject Row(string[] hours)
    {
        var row = new JsonObject();
        for (var hour = 1; hour <= 24; hour++)
        {
            row[hour.ToString()] = hours[hour - 1];
        }

        return row;
    }
}

public class OutageScheduleTests
{
    private static readonly DateOnly Day = ScheduleJson.Day;

    [Fact]
    public void ReadsTheQueueAndTheDayInKyivTime()
    {
        var schedule = OutageSchedule.Parse(ScheduleJson.Build());

        Assert.Equal(new OutageQueue("GPV2.1", "Черга 2.1"), Assert.Single(schedule.Queues));
        Assert.Equal(Day, Assert.Single(schedule.Days).Key);
        Assert.Equal(new DateTime(2026, 11, 10, 22, 26, 26, 598, DateTimeKind.Utc), schedule.FetchedUtc);
        Assert.Equal("с-ще Приклад", schedule.Address.City);
        Assert.True(schedule.ScheduleShown);
    }

    [Fact]
    public void EachHourSplitsIntoTwoHalves()
    {
        var slots = OutageSchedule.Parse(ScheduleJson.Build()).SlotsFor(Day, "GPV2.1")!;

        Assert.Equal(48, slots.Length);
        Assert.Equal(OutageSlot.Off, slots[0]);   // 00:00, "first": no power in the first half
        Assert.Equal(OutageSlot.On, slots[1]);
        Assert.Equal(OutageSlot.Off, slots[12]);  // 06:00, "no"
        Assert.Equal(OutageSlot.Off, slots[22]);  // 11:00, "first"
        Assert.Equal(OutageSlot.On, slots[23]);
        Assert.Equal(OutageSlot.Off, slots[47]);
        Assert.Equal("00:00–00:30, 06:00–11:30, 17:00–24:00", OutageTexts.Ranges(slots, OutageSlot.Off));
    }

    [Fact]
    public void TheWeekPlanKeepsPossibleOutages()
    {
        var monday = OutageSchedule.Parse(ScheduleJson.Build()).WeekPlan["GPV2.1"][1];

        Assert.Equal(OutageSlot.Maybe, monday[4]);
        Assert.Equal((OutageSlot.On, OutageSlot.Maybe), (monday[24], monday[25])); // "msecond"
        Assert.Equal((OutageSlot.Maybe, OutageSlot.On), (monday[38], monday[39])); // "mfirst"
    }

    [Fact]
    public void ReadingTheSameScheduleAgainGivesTheSameHash()
    {
        var first = OutageSchedule.Parse(ScheduleJson.Build());
        var again = OutageSchedule.Parse(ScheduleJson.Build(fetchedAt: 1794350000000, checkedAt: "00:30 11.11.2026"));
        var changed = ScheduleJson.Today.ToArray();
        changed[0] = "yes";

        Assert.Equal(first.ContentHash, again.ContentHash);
        Assert.NotEqual(first.ContentHash, OutageSchedule.Parse(ScheduleJson.Build(today: changed)).ContentHash);
    }

    [Fact]
    public void TheSiteHidesTheScheduleForSomeAddresses()
    {
        Assert.False(OutageSchedule.Parse(ScheduleJson.Build(voluntarily: true)).ScheduleShown);
    }

    [Fact]
    public void ANoticeThatSchedulesDoNotApplyIsNoticed()
    {
        var schedule = OutageSchedule.Parse(ScheduleJson.Build(attention: "Наразі графіки стабілізаційних відключень не діють."));

        Assert.True(schedule.SchedulesSuspended);
    }

    [Fact]
    public void AnErrorResultIsNotASchedule()
    {
        Assert.Throws<FormatException>(() => OutageSchedule.Parse("""{"state":"error","message":"Сайт не відповів"}"""));
        Assert.Throws<FormatException>(() => OutageSchedule.Parse("not json"));
    }

    [Theory]
    [InlineData(8, 10, "Світла немає", "Світло о 11:30 · через 3 год 20 хв")]
    [InlineData(12, 0, "Світло є", "Відключення о 17:00 · через 5 год")]
    [InlineData(22, 0, "Світла немає", "На завтра графіка ще немає")]
    public void TheOutlookSaysWhatComesNext(int hour, int minute, string now, string next)
    {
        var schedule = OutageSchedule.Parse(ScheduleJson.Build());

        var outlook = OutageOutlook.For(schedule, "GPV2.1", Kyiv(hour, minute))!;

        Assert.Equal(now, OutageTexts.Label(outlook.Current));
        Assert.Equal(next, outlook.NextText);
    }

    [Fact]
    public void TheOutlookLooksIntoTomorrow()
    {
        var schedule = OutageSchedule.Parse(ScheduleJson.Build(tomorrow: ScheduleJson.Hours("yes")));

        Assert.Equal("Світло завтра о 00:00 · через 2 год", OutageOutlook.For(schedule, "GPV2.1", Kyiv(22, 0))!.NextText);
    }

    [Fact]
    public void NoOutlookForADayTheScheduleDoesNotHave()
    {
        var schedule = OutageSchedule.Parse(ScheduleJson.Build());

        Assert.Null(OutageOutlook.For(schedule, "GPV2.1", Kyiv(12, 0).AddDays(2)));
    }

    [Theory]
    [InlineData(0, "менше хвилини")]
    [InlineData(45, "45 хв")]
    [InlineData(120, "2 год")]
    [InlineData(200, "3 год 20 хв")]
    public void DurationsReadNaturally(int minutes, string text)
    {
        Assert.Equal(text, OutageTexts.Duration(minutes));
    }

    internal static DateTime Kyiv(int hour, int minute) => KyivTime.ToUtc(Day, hour * 60 + minute);
}
