namespace BluettiDesk.Core.Outages;

/// <summary>
/// Outage schedules are written in Kyiv wall-clock time: a day has 48 half hours from midnight, whatever the clock
/// of this computer says.
/// </summary>
public static class KyivTime
{
    public const int MinutesPerDay = 24 * 60;

    public static TimeZoneInfo Zone { get; } = FindZone();

    public static DateTime FromUtc(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static DateOnly DayOf(DateTime utc) => DateOnly.FromDateTime(FromUtc(utc));

    /// <summary>Minutes since Kyiv midnight of <paramref name="utc"/>.</summary>
    public static int MinuteOfDay(DateTime utc)
    {
        var local = FromUtc(utc);
        return local.Hour * 60 + local.Minute;
    }

    /// <summary>The moment a wall-clock minute of a Kyiv day begins. A minute skipped by the spring clock change maps to the hour after it.</summary>
    public static DateTime ToUtc(DateOnly day, int minuteOfDay)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue).AddMinutes(minuteOfDay), DateTimeKind.Unspecified);
        if (Zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Europe/Kyiv", "Europe/Kiev", "FLE Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Local;
    }
}
