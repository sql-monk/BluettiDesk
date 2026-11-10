using System.Globalization;
using BluettiDesk.Core.Outages;
using Microsoft.Data.Sqlite;

namespace BluettiDesk.Core.Journal;

/// <summary>One wording of a day's schedule and how long the site kept it, in UTC.</summary>
public sealed record ScheduleVersion(DateTime FirstSeenUtc, DateTime LastSeenUtc, OutageSlot[] Slots);

/// <summary>
/// Every distinct schedule the site has shown, in the journal file next to the station's readings. Reading the same
/// schedule again only moves its <c>last_seen</c>. <c>outage_slot</c> spells each version out by half hour for SQL.
/// </summary>
public sealed class OutageScheduleStore : IDisposable
{
    private readonly object _lock = new();
    private readonly SqliteConnection _connection;

    public OutageScheduleStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 10,
        }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Execute(
            """
            CREATE TABLE IF NOT EXISTS outage_schedule (
                id INTEGER PRIMARY KEY,
                first_seen INTEGER NOT NULL,
                last_seen INTEGER NOT NULL,
                content_hash TEXT NOT NULL,
                site_updated TEXT NOT NULL,
                json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS outage_slot (
                schedule_id INTEGER NOT NULL,
                day TEXT NOT NULL,
                queue TEXT NOT NULL,
                slot INTEGER NOT NULL,
                state TEXT NOT NULL,
                PRIMARY KEY (schedule_id, day, queue, slot)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS outage_slot_day ON outage_slot (day, queue, schedule_id);
            CREATE VIEW IF NOT EXISTS outage_schedule_local AS
                SELECT id, datetime(first_seen, 'unixepoch', 'localtime') AS first_seen_local,
                       datetime(last_seen, 'unixepoch', 'localtime') AS last_seen_local, site_updated, content_hash
                FROM outage_schedule;
            """);
    }

    public static string StateName(OutageSlot slot) => slot switch
    {
        OutageSlot.On => "on",
        OutageSlot.Off => "off",
        OutageSlot.Maybe => "maybe",
        _ => "unknown",
    };

    /// <summary>Keeps a schedule just read from the site.</summary>
    /// <returns>True when it differs from the last one kept.</returns>
    public bool Save(OutageSchedule schedule, string json, DateTime utc)
    {
        var now = ToUnix(utc);
        var hash = schedule.ContentHash;
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            using (var latest = Command(transaction, "SELECT id, content_hash FROM outage_schedule ORDER BY id DESC LIMIT 1"))
            using (var reader = latest.ExecuteReader())
            {
                if (reader.Read() && reader.GetString(1) == hash)
                {
                    var id = reader.GetInt64(0);
                    reader.Close();
                    using var touch = Command(transaction, "UPDATE outage_schedule SET last_seen = @now, site_updated = @site, json = @json WHERE id = @id");
                    touch.Parameters.AddWithValue("@now", now);
                    touch.Parameters.AddWithValue("@site", schedule.SiteUpdatedAt);
                    touch.Parameters.AddWithValue("@json", json);
                    touch.Parameters.AddWithValue("@id", id);
                    touch.ExecuteNonQuery();
                    transaction.Commit();
                    return false;
                }
            }

            long scheduleId;
            using (var insert = Command(transaction, "INSERT INTO outage_schedule (first_seen, last_seen, content_hash, site_updated, json) VALUES (@now, @now, @hash, @site, @json) RETURNING id"))
            {
                insert.Parameters.AddWithValue("@now", now);
                insert.Parameters.AddWithValue("@hash", hash);
                insert.Parameters.AddWithValue("@site", schedule.SiteUpdatedAt);
                insert.Parameters.AddWithValue("@json", json);
                scheduleId = (long)insert.ExecuteScalar()!;
            }

            using (var slot = Command(transaction, "INSERT INTO outage_slot (schedule_id, day, queue, slot, state) VALUES (@id, @day, @queue, @slot, @state)"))
            {
                slot.Parameters.AddWithValue("@id", scheduleId);
                var day = slot.Parameters.Add("@day", SqliteType.Text);
                var queue = slot.Parameters.Add("@queue", SqliteType.Text);
                var index = slot.Parameters.Add("@slot", SqliteType.Integer);
                var state = slot.Parameters.Add("@state", SqliteType.Text);
                foreach (var (date, rows) in schedule.Days)
                {
                    day.Value = DayText(date);
                    foreach (var (queueId, slots) in rows)
                    {
                        queue.Value = queueId;
                        for (var i = 0; i < slots.Length; i++)
                        {
                            index.Value = i;
                            state.Value = StateName(slots[i]);
                            slot.ExecuteNonQuery();
                        }
                    }
                }
            }

            transaction.Commit();
            return true;
        }
    }

    /// <summary>The schedule read last and when, for showing something before the site answers.</summary>
    public (string Json, DateTime LastSeenUtc)? Latest()
    {
        lock (_lock)
        {
            using var command = Command(null, "SELECT json, last_seen FROM outage_schedule ORDER BY id DESC LIMIT 1");
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetString(0), FromUnix(reader.GetInt64(1))) : null;
        }
    }

    /// <summary>The wordings of one day's schedule for a queue, oldest first; consecutive identical ones are merged.</summary>
    public IReadOnlyList<ScheduleVersion> VersionsOf(DateOnly day, string queue)
    {
        var rows = new List<(long Id, DateTime First, DateTime Last, int Slot, string State)>();
        lock (_lock)
        {
            using var command = Command(
                null,
                """
                SELECT s.id, s.first_seen, s.last_seen, o.slot, o.state
                FROM outage_slot o JOIN outage_schedule s ON s.id = o.schedule_id
                WHERE o.day = @day AND o.queue = @queue
                ORDER BY s.id, o.slot
                """);
            command.Parameters.AddWithValue("@day", DayText(day));
            command.Parameters.AddWithValue("@queue", queue);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), FromUnix(reader.GetInt64(1)), FromUnix(reader.GetInt64(2)), reader.GetInt32(3), reader.GetString(4)));
            }
        }

        var versions = new List<ScheduleVersion>();
        foreach (var schedule in rows.GroupBy(r => r.Id))
        {
            var slots = new OutageSlot[OutageSchedule.SlotsPerDay];
            foreach (var row in schedule.Where(r => r.Slot is >= 0 and < OutageSchedule.SlotsPerDay))
            {
                slots[row.Slot] = Parse(row.State);
            }

            var first = schedule.First();
            if (versions.Count > 0 && versions[^1].Slots.AsSpan().SequenceEqual(slots))
            {
                versions[^1] = versions[^1] with { LastSeenUtc = first.Last };
            }
            else
            {
                versions.Add(new ScheduleVersion(first.First, first.Last, slots));
            }
        }

        return versions;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connection.Dispose();
        }
    }

    private static OutageSlot Parse(string state) => state switch
    {
        "on" => OutageSlot.On,
        "off" => OutageSlot.Off,
        "maybe" => OutageSlot.Maybe,
        _ => OutageSlot.Unknown,
    };

    private static string DayText(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static DateTime FromUnix(long seconds) => DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;

    private SqliteCommand Command(SqliteTransaction? transaction, string sql)
    {
        var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private void Execute(string sql)
    {
        using var command = Command(null, sql);
        command.ExecuteNonQuery();
    }
}
