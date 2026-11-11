using System.Globalization;

namespace BluettiDesk.Core.Outages;

public enum BackupCase
{
    /// <summary>The grid is gone. <see cref="BackupForecast.UntilUtc"/> is when the schedule brings it back, or null when it does not say.</summary>
    OnBattery,

    /// <summary>The grid is gone although the schedule promised it, so nothing says when it returns.</summary>
    Unplanned,

    /// <summary>The grid is there; the battery will have to carry the next outage of the schedule.</summary>
    NextOutage,

    /// <summary>The grid is there and the known schedule has no outage ahead.</summary>
    NoOutage,
}

/// <summary>
/// Whether the charge of the station lasts until the grid returns by the schedule. The battery starts from its present
/// charge at the present load. While the grid is there the station usually keeps charging, so the real margin before an
/// outage is larger; a possible outage counts as one.
/// </summary>
/// <param name="StartUtc">When the outage begins by the schedule; it may already have begun.</param>
/// <param name="UntilUtc">When the schedule brings the grid back, or null when it ends before saying so.</param>
/// <param name="KnownUntilUtc">Where the known schedule ends.</param>
/// <param name="Possible">The outage is only a possible one all along.</param>
public sealed record BackupForecast(
    BackupCase Case,
    DateTime NowUtc,
    DateTime StartUtc,
    DateTime? UntilUtc,
    DateTime KnownUntilUtc,
    int RuntimeMinutes,
    bool Possible)
{
    private const int LongRuntime = 72 * 60;
    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA");

    /// <summary>When the battery starts carrying the load.</summary>
    public DateTime FromUtc => StartUtc > NowUtc ? StartUtc : NowUtc;

    /// <summary>When the charge runs out at the present load.</summary>
    public DateTime EmptyUtc => FromUtc.AddMinutes(RuntimeMinutes);

    /// <summary>True or false when the schedule settles it, null when it cannot.</summary>
    public bool? Reaches => Case switch
    {
        BackupCase.OnBattery or BackupCase.NextOutage => UntilUtc is { } until
            ? EmptyUtc >= until
            : EmptyUtc < KnownUntilUtc ? false : null,
        _ => null,
    };

    /// <summary>
    /// Spare minutes when the charge lasts, missing ones (negative) when it does not. For an outage whose end the schedule
    /// does not show yet, the shortfall before the end of the known schedule, so at least that much.
    /// </summary>
    public int? MarginMinutes => Reaches is null ? null : (int)Math.Floor((EmptyUtc - (UntilUtc ?? KnownUntilUtc)).TotalMinutes);

    /// <summary>The forecast of one queue at <paramref name="utcNow"/>, or null when the schedule has nothing for now.</summary>
    public static BackupForecast? For(OutageSchedule schedule, string queue, DateTime utcNow, bool gridPresent, int runtimeMinutes)
    {
        var today = KyivTime.DayOf(utcNow);
        if (schedule.SlotsFor(today, queue) is not { } todaySlots)
        {
            return null;
        }

        var tomorrow = schedule.SlotsFor(today.AddDays(1), queue);
        OutageSlot[] timeline = tomorrow is null ? todaySlots : [.. todaySlots, .. tomorrow];
        var slot = KyivTime.MinuteOfDay(utcNow) / OutageSchedule.SlotMinutes;
        if (timeline[slot] == OutageSlot.Unknown)
        {
            return null;
        }

        // The schedule is known up to its first gap.
        var known = slot;
        while (known < timeline.Length && timeline[known] != OutageSlot.Unknown)
        {
            known++;
        }

        var knownUntil = SlotStart(today, known);
        if (!gridPresent)
        {
            if (timeline[slot] == OutageSlot.On)
            {
                return new(BackupCase.Unplanned, utcNow, utcNow, null, knownUntil, runtimeMinutes, false);
            }

            var back = EndOfOutage(timeline, slot, known);
            return new(BackupCase.OnBattery, utcNow, SlotStart(today, slot), back < known ? SlotStart(today, back) : null, knownUntil, runtimeMinutes,
                OnlyPossible(timeline, slot, back));
        }

        var start = slot;
        while (start < known && timeline[start] == OutageSlot.On)
        {
            start++;
        }

        if (start >= known)
        {
            return new(BackupCase.NoOutage, utcNow, knownUntil, null, knownUntil, runtimeMinutes, false);
        }

        var end = EndOfOutage(timeline, start, known);
        return new(BackupCase.NextOutage, utcNow, SlotStart(today, start), end < known ? SlotStart(today, end) : null, knownUntil, runtimeMinutes,
            OnlyPossible(timeline, start, end));
    }

    /// <summary>For example "Дотягне до світла о 11:30" or "Не витримає відключення 17:00–24:00".</summary>
    public string Title
    {
        get
        {
            var outage = Possible ? "можливе відключення" : "відключення";
            return Case switch
            {
                BackupCase.Unplanned => "Світла немає поза графіком",
                BackupCase.NoOutage => "Відключень у графіку попереду немає",
                BackupCase.OnBattery when UntilUtc is { } until => $"{(Reaches == true ? "Дотягне" : "Не дотягне")} до світла {At(until)}",
                BackupCase.OnBattery => Reaches == false ? "Не дотягне до кінця відключення" : "Коли дадуть світло, графік ще не каже",
                BackupCase.NextOutage when UntilUtc is { } until => $"{(Reaches == true ? "Витримає" : "Не витримає")} {outage} {Span(until)}",
                _ => Reaches == false
                    ? $"Не витримає {outage} {Beginning()}"
                    : $"{char.ToUpper(outage[0], Ukrainian)}{outage[1..]} {Beginning()}, кінця в графіку ще немає",
            };
        }
    }

    /// <summary>"запас ≈ 2 год 10 хв", "бракує ≈ 40 хв", or null when nothing is compared.</summary>
    public string? MarginText
    {
        get
        {
            if (MarginMinutes is not { } margin)
            {
                return null;
            }

            if (margin >= 0)
            {
                return Rounded(margin) == 0 ? "впритул" : $"запас ≈ {OutageTexts.Duration(Rounded(margin))}";
            }

            return $"бракує {(UntilUtc is null ? "щонайменше " : string.Empty)}≈ {OutageTexts.Duration(Math.Max(5, Rounded(-margin)))}";
        }
    }

    /// <summary>How the forecast was made, for example "Заряду 54 % при споживанні ≈ 85 Вт вистачить на ≈ 8 год 40 хв, до 11:55."</summary>
    public string Detail(int socPercent, double loadWatts, bool charging)
    {
        var lasts = RuntimeMinutes >= LongRuntime ? "понад 3 доби" : $"≈ {OutageTexts.Duration(Rounded(RuntimeMinutes))}";
        var text = $"{(Case is BackupCase.NextOutage or BackupCase.NoOutage ? "Без мережі заряду" : "Заряду")} {socPercent} % при споживанні ≈ {loadWatts.ToString("0", Ukrainian)} Вт вистачить на {lasts}";
        text += Case is BackupCase.OnBattery or BackupCase.Unplanned && RuntimeMinutes < LongRuntime ? $", {Upto(EmptyUtc)}." : ".";
        return text + Case switch
        {
            BackupCase.Unplanned => " За графіком світло мало бути, тож коли його дадуть, невідомо.",
            BackupCase.OnBattery when Reaches is null => " Далі графік ще не відомий.",
            BackupCase.NextOutage or BackupCase.NoOutage when charging => " Станція ще заряджається, тож запас зросте.",
            _ => string.Empty,
        };
    }

    /// <summary>"до 11:55", "до завтра 07:10", "до 24:00".</summary>
    public string Upto(DateTime utc) =>
        DaysAfterToday(utc) == 1 && KyivTime.MinuteOfDay(utc) == 0 ? "до 24:00" : $"до {DayWord(utc)}{Clock(utc)}";

    private static int Rounded(int minutes) => (int)Math.Round(minutes / 5.0, MidpointRounding.AwayFromZero) * 5;

    private static DateTime SlotStart(DateOnly today, int slot) =>
        KyivTime.ToUtc(today.AddDays(slot / OutageSchedule.SlotsPerDay), slot % OutageSchedule.SlotsPerDay * OutageSchedule.SlotMinutes);

    private static int EndOfOutage(OutageSlot[] timeline, int from, int known)
    {
        var end = from;
        while (end < known && timeline[end] is OutageSlot.Off or OutageSlot.Maybe)
        {
            end++;
        }

        return end;
    }

    private static bool OnlyPossible(OutageSlot[] timeline, int from, int end) =>
        !timeline[from..Math.Max(from, end)].Contains(OutageSlot.Off) && end > from;

    private static string Clock(DateTime utc) => OutageTexts.Time(KyivTime.MinuteOfDay(utc));

    private int DaysAfterToday(DateTime utc) => KyivTime.DayOf(utc).DayNumber - KyivTime.DayOf(NowUtc).DayNumber;

    private string DayWord(DateTime utc) => DaysAfterToday(utc) switch
    {
        0 => string.Empty,
        1 => "завтра ",
        2 => "післязавтра ",
        _ => KyivTime.FromUtc(utc).ToString("dd.MM ", Ukrainian),
    };

    private string At(DateTime utc) => $"{DayWord(utc)}о {Clock(utc)}";

    private string Beginning() => StartUtc <= NowUtc ? "зараз" : $"{DayWord(StartUtc)}з {Clock(StartUtc)}";

    /// <summary>"06:00–11:30", "завтра 06:00–11:30", "17:00–24:00", or "до 11:30" once it has begun.</summary>
    private string Span(DateTime until)
    {
        if (StartUtc <= NowUtc)
        {
            return Upto(until);
        }

        var midnightAfter = KyivTime.DayOf(until).DayNumber - KyivTime.DayOf(StartUtc).DayNumber == 1 && KyivTime.MinuteOfDay(until) == 0;
        return $"{DayWord(StartUtc)}{Clock(StartUtc)}–{(midnightAfter ? "24:00" : Clock(until))}";
    }
}
