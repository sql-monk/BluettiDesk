using BluettiDesk.Core.Journal;

namespace BluettiDesk.Core.Outages;

/// <summary>Whether the grid was there in a minute, as the station saw it.</summary>
public enum GridFact : byte
{
    Unknown,
    On,
    Off,
}

/// <summary>
/// One Kyiv day: what the schedule promised and what the station measured, minute by minute. The statistics count only
/// minutes for which the journal has readings, so a day the app was closed does not look like a day without outages.
/// </summary>
public sealed class DayComparison
{
    private DayComparison(DateOnly day, OutageSlot[]? plan, GridFact[] fact)
    {
        Day = day;
        Plan = plan;
        Fact = fact;
        for (var minute = 0; minute < fact.Length; minute++)
        {
            var planned = plan?[minute / OutageSchedule.SlotMinutes] ?? OutageSlot.Unknown;
            if (planned == OutageSlot.Off)
            {
                PlannedOffMinutes++;
            }

            switch (fact[minute])
            {
                case GridFact.On:
                    KnownMinutes++;
                    if (planned == OutageSlot.Off)
                    {
                        PowerWhilePlannedOffMinutes++;
                    }

                    break;
                case GridFact.Off:
                    KnownMinutes++;
                    ActualOffMinutes++;
                    if (planned == OutageSlot.Off)
                    {
                        MatchedOffMinutes++;
                    }
                    else
                    {
                        UnplannedOffMinutes++;
                    }

                    break;
            }
        }
    }

    public DateOnly Day { get; }

    /// <summary>48 half hours, or null when no schedule for the day was saved.</summary>
    public OutageSlot[]? Plan { get; }

    /// <summary>1440 minutes from Kyiv midnight.</summary>
    public GridFact[] Fact { get; }

    public int PlannedOffMinutes { get; }

    /// <summary>Minutes for which the journal knows whether the grid was there.</summary>
    public int KnownMinutes { get; }

    public int ActualOffMinutes { get; }

    /// <summary>Without power, as the schedule said.</summary>
    public int MatchedOffMinutes { get; }

    /// <summary>Without power while the schedule did not say so.</summary>
    public int UnplannedOffMinutes { get; }

    /// <summary>With power while the schedule said there would be none.</summary>
    public int PowerWhilePlannedOffMinutes { get; }

    public static DayComparison Build(DateOnly day, OutageSlot[]? plan, IReadOnlyList<PresenceSpan> spans) =>
        new(day, plan, FactMinutes(day, spans));

    /// <summary>The grid in each minute of a Kyiv day, judged in the middle of the minute.</summary>
    public static GridFact[] FactMinutes(DateOnly day, IReadOnlyList<PresenceSpan> spans)
    {
        var fact = new GridFact[KyivTime.MinutesPerDay];
        var index = 0;
        for (var minute = 0; minute < fact.Length; minute++)
        {
            var moment = KyivTime.ToUtc(day, minute).AddSeconds(30);
            while (index < spans.Count && spans[index].ToUtc <= moment)
            {
                index++;
            }

            if (index < spans.Count && spans[index].FromUtc <= moment)
            {
                fact[minute] = spans[index].Present ? GridFact.On : GridFact.Off;
            }
        }

        return fact;
    }

    /// <summary>For example "За графіком без світла 10 год, за даними станції 9 год 20 хв. Збіглося 8 год 50 хв…".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            parts.Add(Plan is null
                ? "Графіка на цей день не збережено."
                : $"За графіком без світла {Minutes(PlannedOffMinutes)}.");

            if (KnownMinutes == 0)
            {
                parts.Add("Даних станції за цей день немає.");
                return string.Join(' ', parts);
            }

            parts.Add($"Станція не бачила мережі {Minutes(ActualOffMinutes)}, а дані є за {OutageTexts.Duration(KnownMinutes)}.");
            if (Plan is not null)
            {
                var details = new List<string> { $"збіглося з графіком {Minutes(MatchedOffMinutes)}" };
                if (UnplannedOffMinutes > 0)
                {
                    details.Add($"поза графіком {OutageTexts.Duration(UnplannedOffMinutes)}");
                }

                if (PowerWhilePlannedOffMinutes > 0)
                {
                    details.Add($"світло було, хоч за графіком не мало, {OutageTexts.Duration(PowerWhilePlannedOffMinutes)}");
                }

                parts.Add(char.ToUpperInvariant(details[0][0]) + string.Join(", ", details)[1..] + ".");
            }

            return string.Join(' ', parts);
        }
    }

    private static string Minutes(int minutes) => minutes == 0 ? "0 хв" : OutageTexts.Duration(minutes);
}
