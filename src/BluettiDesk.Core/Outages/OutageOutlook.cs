using System.Globalization;

namespace BluettiDesk.Core.Outages;

/// <summary>Where the schedule stands now and what it says comes next.</summary>
/// <param name="MinutesToChange">Until the next change, when there is one in the schedule.</param>
/// <param name="ChangeMinuteOfDay">Kyiv wall-clock minute of the change, on the day <paramref name="ChangeIsTomorrow"/> says.</param>
public sealed record OutageOutlook(OutageSlot Current, OutageSlot Next, int? MinutesToChange, int ChangeMinuteOfDay, bool ChangeIsTomorrow, bool TomorrowMissing)
{
    /// <summary>The outlook of one queue at <paramref name="utcNow"/>, or null when the schedule has nothing for today.</summary>
    public static OutageOutlook? For(OutageSchedule schedule, string queue, DateTime utcNow)
    {
        var today = KyivTime.DayOf(utcNow);
        if (schedule.SlotsFor(today, queue) is not { } todaySlots)
        {
            return null;
        }

        var minute = KyivTime.MinuteOfDay(utcNow);
        var slot = minute / OutageSchedule.SlotMinutes;
        var tomorrow = schedule.SlotsFor(today.AddDays(1), queue);
        var timeline = tomorrow is null ? todaySlots : [.. todaySlots, .. tomorrow];

        var current = todaySlots[slot];
        var next = slot + 1;
        while (next < timeline.Length && timeline[next] == current)
        {
            next++;
        }

        if (current == OutageSlot.Unknown || next >= timeline.Length || timeline[next] == OutageSlot.Unknown)
        {
            return new OutageOutlook(current, OutageSlot.Unknown, null, 0, next >= OutageSchedule.SlotsPerDay, next >= OutageSchedule.SlotsPerDay);
        }

        return new OutageOutlook(
            current,
            timeline[next],
            next * OutageSchedule.SlotMinutes - minute,
            next % OutageSchedule.SlotsPerDay * OutageSchedule.SlotMinutes,
            next >= OutageSchedule.SlotsPerDay,
            false);
    }

    /// <summary>For example "Відключення о 16:00 · через 1 год 55 хв".</summary>
    public string NextText
    {
        get
        {
            if (Current == OutageSlot.Unknown)
            {
                return "Сайт не дав значення для цього часу";
            }

            if (MinutesToChange is not { } minutes)
            {
                return TomorrowMissing ? "На завтра графіка ще немає" : "Далі даних у графіку немає";
            }

            return $"{OutageTexts.Event(Next)} {(ChangeIsTomorrow ? "завтра " : string.Empty)}о {OutageTexts.Time(ChangeMinuteOfDay)} · через {OutageTexts.Duration(minutes)}";
        }
    }
}

public static class OutageTexts
{
    public static string Label(OutageSlot slot) => slot switch
    {
        OutageSlot.On => "Світло є",
        OutageSlot.Off => "Світла немає",
        OutageSlot.Maybe => "Можливе відключення",
        _ => "Немає даних",
    };

    /// <summary>What happens when the schedule turns to <paramref name="slot"/>.</summary>
    public static string Event(OutageSlot slot) => slot switch
    {
        OutageSlot.On => "Світло",
        OutageSlot.Off => "Відключення",
        OutageSlot.Maybe => "Можливе відключення",
        _ => "Немає даних",
    };

    /// <summary>"08:30"; the end of the day is "24:00".</summary>
    public static string Time(int minuteOfDay) =>
        $"{minuteOfDay / 60:00}:{minuteOfDay % 60:00}";

    public static string Duration(int minutes)
    {
        var hours = minutes / 60;
        var rest = minutes % 60;
        var parts = new List<string>(2);
        if (hours > 0)
        {
            parts.Add($"{hours} год");
        }

        if (rest > 0)
        {
            parts.Add($"{rest} хв");
        }

        return parts.Count > 0 ? string.Join(' ', parts) : "менше хвилини";
    }

    /// <summary>The half hours of one kind as ranges: "07:00–11:30, 17:00–24:00".</summary>
    public static string Ranges(IReadOnlyList<OutageSlot> slots, OutageSlot kind)
    {
        var ranges = new List<string>();
        for (var start = 0; start < slots.Count;)
        {
            var end = start + 1;
            while (end < slots.Count && slots[end] == slots[start])
            {
                end++;
            }

            if (slots[start] == kind)
            {
                ranges.Add($"{Time(start * OutageSchedule.SlotMinutes)}–{Time(end * OutageSchedule.SlotMinutes)}");
            }

            start = end;
        }

        return string.Join(", ", ranges);
    }

    /// <summary>"середа, 11 листопада".</summary>
    public static string DayTitle(DateOnly day) =>
        day.ToString("dddd, d MMMM", CultureInfo.GetCultureInfo("uk-UA"));
}
