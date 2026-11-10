using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BluettiDesk.Core.Outages;

/// <summary>What the schedule says about one half hour.</summary>
public enum OutageSlot
{
    Unknown,
    On,
    Off,
    Maybe,
}

/// <summary>A house address exactly as the form on the DTEK site offers it.</summary>
public sealed record OutageAddress(string City, string Street, string House)
{
    public static OutageAddress Empty { get; } = new(string.Empty, string.Empty, string.Empty);

    public bool IsComplete => City.Trim().Length > 0 && Street.Trim().Length > 0 && House.Trim().Length > 0;

    public override string ToString() => $"{City}, {Street}, {House}";
}

/// <summary>One queue of the schedule, such as GPV2.1 "Черга 2.1".</summary>
public sealed record OutageQueue(string Id, string Name);

/// <summary>An outage the site reports at the address right now, and the reasons it may not show a schedule.</summary>
public sealed record OutageNotice(string Type, string SubType, string Start, string End, bool ByCek, bool Voluntarily)
{
    public static OutageNotice None { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, false, false);

    public bool IsReported => SubType.Length > 0 || Start.Length > 0 || End.Length > 0;
}

/// <summary>Which parts of the page the site itself shows for the address.</summary>
public sealed record OutageFlags(bool ShowCurrentOutage, bool ShowCurrentSchedule, bool ShowTableFact, bool ShowTableSchedule, bool ShowTablePlan);

/// <summary>
/// The schedule of one address as read from the DTEK page by <c>extract.js</c>. <see cref="Days"/> is the schedule for
/// today and, once published, tomorrow; <see cref="WeekPlan"/> is the long-term plan by day of the week. Each row has 48
/// half hours from Kyiv midnight.
/// </summary>
public sealed class OutageSchedule
{
    public const int SlotsPerDay = 48;
    public const int SlotMinutes = 30;

    /// <summary>The site's wording for an emergency, when it hides the schedule tables.</summary>
    public const string EmergencySubType = "Екстренні відключення (Аварійне без застосування графіку погодинних відключень)";

    private static readonly Regex Suspended = new(@"не\s+діють", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public required DateTime FetchedUtc { get; init; }

    public required OutageAddress Address { get; init; }

    public required IReadOnlyList<OutageQueue> Queues { get; init; }

    public required OutageNotice Notice { get; init; }

    public required OutageFlags Flags { get; init; }

    /// <summary>When the site checked the address, as it writes it, for example "00:18 11.11.2026".</summary>
    public string AddressCheckedAt { get; init; } = string.Empty;

    /// <summary>When the site last changed the schedule, as it writes it, for example "10.11.2026 22:07".</summary>
    public string SiteUpdatedAt { get; init; } = string.Empty;

    /// <summary>The site's general warning, for example that the schedules do not apply now.</summary>
    public string Attention { get; init; } = string.Empty;

    public required IReadOnlyDictionary<DateOnly, IReadOnlyDictionary<string, OutageSlot[]>> Days { get; init; }

    /// <summary>Queue → day of the week (1 Monday … 7 Sunday) → 48 half hours.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<int, OutageSlot[]>> WeekPlan { get; init; }

    /// <summary>The same conditions under which the site hides its schedule table.</summary>
    public bool ScheduleShown =>
        Flags.ShowCurrentSchedule && Queues.Count > 0 && !Notice.ByCek && !Notice.Voluntarily && Notice.SubType != EmergencySubType;

    public bool WeekPlanShown => ScheduleShown && Flags.ShowTableSchedule && Flags.ShowTablePlan;

    /// <summary>The site says the schedules are not in force, though it still shows them.</summary>
    public bool SchedulesSuspended => Suspended.IsMatch(Attention);

    /// <summary>
    /// Identifies what the schedule says, leaving out when it was read and when the site checked the address, so that
    /// reading the same schedule again yields the same value.
    /// </summary>
    public string ContentHash => Hash(Canonical());

    public OutageSlot[]? SlotsFor(DateOnly day, string queue) =>
        Days.TryGetValue(day, out var rows) && rows.TryGetValue(queue, out var slots) ? slots : null;

    /// <summary>Reads the result of <c>extract.js</c> in its "done" state.</summary>
    /// <exception cref="FormatException">The text is not such a result.</exception>
    public static OutageSchedule Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (Text(root, "state") != "done")
            {
                throw new FormatException("Це не готовий графік.");
            }

            var queues = new List<OutageQueue>();
            var names = root.TryGetProperty("names", out var n) && n.ValueKind == JsonValueKind.Object ? n : default;
            if (root.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            {
                foreach (var group in groups.EnumerateArray())
                {
                    var id = group.GetString() ?? string.Empty;
                    var name = names.ValueKind == JsonValueKind.Object ? Text(names, id) : string.Empty;
                    queues.Add(new OutageQueue(id, name.Length > 0 ? name : id));
                }
            }

            var address = root.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.Object
                ? new OutageAddress(Text(a, "city"), Text(a, "street"), Text(a, "house"))
                : OutageAddress.Empty;
            var info = root.TryGetProperty("info", out var i) && i.ValueKind == JsonValueKind.Object
                ? new OutageNotice(Text(i, "type"), Text(i, "subType"), Text(i, "start"), Text(i, "end"), Flag(i, "cek"), Flag(i, "voluntarily"))
                : OutageNotice.None;
            var flags = root.TryGetProperty("flags", out var f) && f.ValueKind == JsonValueKind.Object
                ? new OutageFlags(Flag(f, "showCurOutage"), Flag(f, "showCurSchedule"), Flag(f, "showTableFact"), Flag(f, "showTableSchedule"), Flag(f, "showTablePlan"))
                : new OutageFlags(false, false, false, false, false);

            var days = new SortedDictionary<DateOnly, IReadOnlyDictionary<string, OutageSlot[]>>();
            if (root.TryGetProperty("fact", out var fact) && fact.ValueKind == JsonValueKind.Object)
            {
                foreach (var day in fact.EnumerateObject())
                {
                    if (!long.TryParse(day.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || day.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var rows = new SortedDictionary<string, OutageSlot[]>(StringComparer.Ordinal);
                    foreach (var row in day.Value.EnumerateObject())
                    {
                        rows[row.Name] = ToSlots(row.Value);
                    }

                    // The site keys a day by the Unix time of its Kyiv midnight.
                    days[KyivTime.DayOf(DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime)] = rows;
                }
            }

            var plan = new SortedDictionary<string, IReadOnlyDictionary<int, OutageSlot[]>>(StringComparer.Ordinal);
            if (root.TryGetProperty("plan", out var planElement) && planElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var queue in planElement.EnumerateObject().Where(q => q.Value.ValueKind == JsonValueKind.Object))
                {
                    var week = new SortedDictionary<int, OutageSlot[]>();
                    foreach (var day in queue.Value.EnumerateObject())
                    {
                        if (int.TryParse(day.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var weekday) && weekday is >= 1 and <= 7)
                        {
                            week[weekday] = ToSlots(day.Value);
                        }
                    }

                    plan[queue.Name] = week;
                }
            }

            var fetchedMs = root.TryGetProperty("fetchedAt", out var fetched) && fetched.TryGetInt64(out var ms) ? ms : 0;
            return new OutageSchedule
            {
                FetchedUtc = DateTimeOffset.FromUnixTimeMilliseconds(fetchedMs).UtcDateTime,
                Address = address,
                Queues = queues,
                Notice = info,
                Flags = flags,
                AddressCheckedAt = Text(root, "addressCheckedAt"),
                SiteUpdatedAt = Text(root, "factUpdatedAt"),
                Attention = Text(root, "attention"),
                Days = days,
                WeekPlan = plan,
            };
        }
        catch (JsonException ex)
        {
            throw new FormatException("Не вдалося прочитати графік.", ex);
        }
    }

    /// <summary>The site's value for an hour → the first and second half of it.</summary>
    public static (OutageSlot First, OutageSlot Second) Halves(string? value) => value switch
    {
        "yes" => (OutageSlot.On, OutageSlot.On),
        "no" => (OutageSlot.Off, OutageSlot.Off),
        "first" => (OutageSlot.Off, OutageSlot.On),
        "second" => (OutageSlot.On, OutageSlot.Off),
        "maybe" => (OutageSlot.Maybe, OutageSlot.Maybe),
        "mfirst" => (OutageSlot.Maybe, OutageSlot.On),
        "msecond" => (OutageSlot.On, OutageSlot.Maybe),
        _ => (OutageSlot.Unknown, OutageSlot.Unknown),
    };

    public static char Code(OutageSlot slot) => slot switch
    {
        OutageSlot.On => '1',
        OutageSlot.Off => '0',
        OutageSlot.Maybe => '?',
        _ => '-',
    };

    private static OutageSlot[] ToSlots(JsonElement row)
    {
        var slots = new OutageSlot[SlotsPerDay];
        for (var hour = 1; hour <= 24; hour++)
        {
            string? value = null;
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(hour.ToString(CultureInfo.InvariantCulture), out var cell) && cell.ValueKind == JsonValueKind.String)
            {
                value = cell.GetString();
            }
            else if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= hour && row[hour - 1].ValueKind == JsonValueKind.String)
            {
                value = row[hour - 1].GetString();
            }

            (slots[(hour - 1) * 2], slots[(hour - 1) * 2 + 1]) = Halves(value);
        }

        return slots;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Row(OutageSlot[] slots) => new(slots.Select(Code).ToArray());

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private string Canonical()
    {
        var text = new StringBuilder();
        text.Append("queues:").AppendJoin(';', Queues.Select(q => $"{q.Id}={q.Name}")).Append('\n');
        text.Append("notice:").AppendJoin('|', Notice.Type, Notice.SubType, Notice.Start, Notice.End, Notice.ByCek, Notice.Voluntarily).Append('\n');
        text.Append("flags:").AppendJoin('|', Flags.ShowCurrentOutage, Flags.ShowCurrentSchedule, Flags.ShowTableFact, Flags.ShowTableSchedule, Flags.ShowTablePlan).Append('\n');
        text.Append("attention:").Append(Attention).Append('\n');
        foreach (var (day, rows) in Days.OrderBy(d => d.Key))
        {
            foreach (var (queue, slots) in rows.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                text.Append(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(' ').Append(queue).Append(':').Append(Row(slots)).Append('\n');
            }
        }

        foreach (var (queue, week) in WeekPlan.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            foreach (var (weekday, slots) in week.OrderBy(w => w.Key))
            {
                text.Append("plan ").Append(queue).Append(' ').Append(weekday).Append(':').Append(Row(slots)).Append('\n');
            }
        }

        return text.ToString();
    }
}
