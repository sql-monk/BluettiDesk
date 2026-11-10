using System.Globalization;
using System.Text;
using BluettiDesk.Core.Station;
using Microsoft.Data.Sqlite;

namespace BluettiDesk.Core.Journal;

/// <param name="Time">Local time.</param>
public readonly record struct TimePoint(DateTime Time, double Value);

public readonly record struct PowerPoint(DateTime Time, double InputWatts, double OutputWatts);

public readonly record struct RangePoint(DateTime Time, double Min, double Max);

public sealed record JournalInfo(DateTime? FirstSample, long SampleCount, long SizeBytes);

/// <summary>A stretch of time over which the grid was either there or not, in UTC.</summary>
public readonly record struct PresenceSpan(DateTime FromUtc, DateTime ToUtc, bool Present);

/// <summary>
/// The long-term record, one SQLite file. <c>sample</c> holds every named value, <c>register_change</c>
/// holds each register the station answers to, written only when it changes. Times are Unix seconds, UTC.
/// </summary>
public sealed class StationJournal : IDisposable
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RegisterInterval = TimeSpan.FromMinutes(1);

    /// <summary>A reading speaks for at most this long: a longer silence means the app was closed or the station out of reach.</summary>
    public static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(60);

    private const string MainsColumn = "ac_in_v";

    private readonly string _path;
    private readonly object _writeLock = new();
    private readonly SqliteConnection _writer;
    private readonly SqliteCommand _insertSample;
    private readonly SqliteParameter _sampleTime;
    private readonly SqliteParameter[] _sampleValues;
    private readonly Dictionary<int, ushort> _lastRegisters = [];
    private DateTime _nextRegisterWrite = DateTime.MinValue;
    private string? _lastError;

    public StationJournal(string path, IReadOnlyList<LogField>? fields = null)
    {
        _path = path;
        Fields = fields ?? Ac180pLog.Fields;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        _writer = new SqliteConnection(ConnectionString(SqliteOpenMode.ReadWriteCreate));
        _writer.Open();
        Execute(_writer, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        CreateSchema();
        LoadLastRegisters();

        _insertSample = _writer.CreateCommand();
        _insertSample.CommandText =
            $"INSERT OR REPLACE INTO sample (ts, {string.Join(", ", Fields.Select(f => f.Column))}) " +
            $"VALUES (@ts, {string.Join(", ", Fields.Select((_, i) => $"@v{i}"))})";
        _sampleTime = _insertSample.Parameters.Add("@ts", SqliteType.Integer);
        _sampleValues = Fields.Select((f, i) => _insertSample.Parameters.Add($"@v{i}", f.IsInteger ? SqliteType.Integer : SqliteType.Real)).ToArray();
    }

    /// <summary>Raised with a message when writing fails, and with null once it works again.</summary>
    public event Action<string?>? HealthChanged;

    public IReadOnlyList<LogField> Fields { get; }

    public string FilePath => _path;

    /// <summary>Saves one reading. Never throws: a full disk must not cost the connection to the station.</summary>
    public void Record(DateTime utc, RegisterImage registers)
    {
        try
        {
            lock (_writeLock)
            {
                using var transaction = _writer.BeginTransaction();
                WriteSample(transaction, utc, registers);
                var changes = utc >= _nextRegisterWrite ? WriteRegisterChanges(transaction, utc, registers) : null;
                transaction.Commit();

                if (changes is not null)
                {
                    foreach (var (address, value) in changes)
                    {
                        _lastRegisters[address] = value;
                    }

                    _nextRegisterWrite = utc + RegisterInterval;
                }
            }

            ReportHealth(null);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ReportHealth(ex.Message);
        }
    }

    public JournalInfo GetInfo()
    {
        using var connection = OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(ts), COUNT(*) FROM sample";
        using var reader = command.ExecuteReader();
        reader.Read();
        DateTime? first = reader.IsDBNull(0) ? null : ToLocal(reader.GetInt64(0));
        var size = new[] { _path, _path + "-wal" }.Where(File.Exists).Sum(p => new FileInfo(p).Length);
        return new JournalInfo(first, reader.GetInt64(1), size);
    }

    /// <summary>Average of one field per time bucket, oldest first.</summary>
    public IReadOnlyList<TimePoint> QueryAverages(LogField field, DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        // The column name goes into the SQL text, so only names from our own list may get there.
        if (!Fields.Contains(field))
        {
            throw new ArgumentException($"Unknown field: {field.Column}", nameof(field));
        }

        // No value is not zero: rows older than a field have nothing in its column, and must not draw as 0.
        var points = new List<TimePoint>();
        Query($"AVG({field.Column})", fromUtc, toUtc, bucket, reader =>
        {
            if (!reader.IsDBNull(1))
            {
                points.Add(new TimePoint(ToLocal(reader.GetInt64(0)), reader.GetDouble(1)));
            }
        });
        return points;
    }

    /// <summary>Lowest and highest value of one field per time bucket, oldest first: an average would hide a short spike.</summary>
    public IReadOnlyList<RangePoint> QueryRange(LogField field, DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        if (!Fields.Contains(field))
        {
            throw new ArgumentException($"Unknown field: {field.Column}", nameof(field));
        }

        var points = new List<RangePoint>();
        Query($"MIN({field.Column}), MAX({field.Column})", fromUtc, toUtc, bucket, reader =>
        {
            if (!reader.IsDBNull(1) && !reader.IsDBNull(2))
            {
                points.Add(new RangePoint(ToLocal(reader.GetInt64(0)), reader.GetDouble(1), reader.GetDouble(2)));
            }
        });
        return points;
    }

    /// <summary>Average input and output power per time bucket, oldest first.</summary>
    public IReadOnlyList<PowerPoint> QueryPower(DateTime fromUtc, DateTime toUtc, TimeSpan bucket)
    {
        var points = new List<PowerPoint>();
        Query("AVG(ac_in_w + dc_in_w), AVG(ac_out_w + dc_out_w)", fromUtc, toUtc, bucket, reader =>
        {
            if (!reader.IsDBNull(1) && !reader.IsDBNull(2))
            {
                points.Add(new PowerPoint(ToLocal(reader.GetInt64(0)), reader.GetDouble(1), reader.GetDouble(2)));
            }
        });
        return points;
    }

    /// <summary>
    /// When the grid was there and when not, oldest first, judged by the measured input voltage. Time not covered by
    /// readings is in no span.
    /// </summary>
    public IReadOnlyList<PresenceSpan> QueryMainsPresence(DateTime fromUtc, DateTime toUtc)
    {
        var spans = new List<PresenceSpan>();
        if (!Fields.Any(f => f.Column == MainsColumn))
        {
            return spans;
        }

        using var connection = OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT ts, {MainsColumn} FROM sample WHERE ts >= @from AND ts < @to AND {MainsColumn} IS NOT NULL ORDER BY ts";
        command.Parameters.AddWithValue("@from", ToUnix(fromUtc - MaxSampleGap));
        command.Parameters.AddWithValue("@to", ToUnix(toUtc));
        using var reader = command.ExecuteReader();

        (DateTime Time, bool Present)? previous = null;
        while (reader.Read())
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)).UtcDateTime;
            var present = reader.GetDouble(1) >= StationLimits.MainsPresentVoltage;
            if (previous is { } last)
            {
                Add(last.Time, Min(time, last.Time + MaxSampleGap), last.Present);
            }

            previous = (time, present);
        }

        if (previous is { } final)
        {
            Add(final.Time, Min(toUtc, final.Time + MaxSampleGap), final.Present);
        }

        return spans;

        void Add(DateTime from, DateTime to, bool present)
        {
            from = from < fromUtc ? fromUtc : from;
            if (to <= from)
            {
                return;
            }

            if (spans.Count > 0 && spans[^1].Present == present && spans[^1].ToUtc == from)
            {
                spans[^1] = spans[^1] with { ToUtc = to };
            }
            else
            {
                spans.Add(new PresenceSpan(from, to, present));
            }
        }

        static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    }

    /// <summary>Writes the named values of a period as CSV that Excel opens correctly under <paramref name="culture"/>.</summary>
    /// <returns>The number of rows written.</returns>
    public long ExportCsv(string path, DateTime fromUtc, DateTime toUtc, CultureInfo culture)
    {
        var separator = culture.TextInfo.ListSeparator;
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(string.Join(separator, new[] { "Час" }.Concat(Fields.Select(f => f.DisplayName)).Select(t => Quote(t, separator))));

        using var connection = OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT ts, {string.Join(", ", Fields.Select(f => f.Column))} FROM sample WHERE ts >= @from AND ts < @to ORDER BY ts";
        command.Parameters.AddWithValue("@from", ToUnix(fromUtc));
        command.Parameters.AddWithValue("@to", ToUnix(toUtc));
        using var reader = command.ExecuteReader();

        long rows = 0;
        var cells = new string[Fields.Count + 1];
        while (reader.Read())
        {
            cells[0] = ToLocal(reader.GetInt64(0)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            for (var i = 1; i < cells.Length; i++)
            {
                cells[i] = reader.IsDBNull(i) ? string.Empty : reader.GetDouble(i).ToString(culture);
            }

            writer.WriteLine(string.Join(separator, cells));
            rows++;
        }

        return rows;
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _insertSample.Dispose();
            _writer.Dispose();
        }
    }

    private static long ToUnix(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

    private static DateTime ToLocal(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;

    private static string Quote(string text, string separator) =>
        text.Contains(separator, StringComparison.Ordinal) || text.Contains('"') ? $"\"{text.Replace("\"", "\"\"")}\"" : text;

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string ConnectionString(SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = _path,
        Mode = mode,
        Pooling = false,
    }.ToString();

    private SqliteConnection OpenReader()
    {
        var connection = new SqliteConnection(ConnectionString(SqliteOpenMode.ReadOnly));
        connection.Open();
        return connection;
    }

    private void CreateSchema()
    {
        using var transaction = _writer.BeginTransaction();
        Execute(_writer, "CREATE TABLE IF NOT EXISTS sample (ts INTEGER PRIMARY KEY)", transaction);

        // A newer version may log more fields than the file has columns for.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var columns = _writer.CreateCommand())
        {
            columns.Transaction = transaction;
            columns.CommandText = "SELECT name FROM pragma_table_info('sample')";
            using var reader = columns.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(0));
            }
        }

        foreach (var field in Fields.Where(f => !existing.Contains(f.Column)))
        {
            Execute(_writer, $"ALTER TABLE sample ADD COLUMN {field.Column} {(field.IsInteger ? "INTEGER" : "REAL")}", transaction);
        }

        Execute(
            _writer,
            """
            CREATE TABLE IF NOT EXISTS register_change (
                ts INTEGER NOT NULL,
                address INTEGER NOT NULL,
                value INTEGER NOT NULL,
                PRIMARY KEY (address, ts)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS field (
                column_name TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                unit TEXT NOT NULL
            );
            CREATE VIEW IF NOT EXISTS sample_local AS
                SELECT datetime(ts, 'unixepoch', 'localtime') AS local_time, * FROM sample;
            CREATE VIEW IF NOT EXISTS register_change_local AS
                SELECT datetime(ts, 'unixepoch', 'localtime') AS local_time, address, value FROM register_change;
            """,
            transaction);

        using (var describe = _writer.CreateCommand())
        {
            describe.Transaction = transaction;
            describe.CommandText = "INSERT OR REPLACE INTO field (column_name, title, unit) VALUES (@c, @t, @u)";
            var column = describe.Parameters.Add("@c", SqliteType.Text);
            var title = describe.Parameters.Add("@t", SqliteType.Text);
            var unit = describe.Parameters.Add("@u", SqliteType.Text);
            foreach (var field in Fields)
            {
                column.Value = field.Column;
                title.Value = field.Title;
                unit.Value = field.Unit;
                describe.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private void LoadLastRegisters()
    {
        using var command = _writer.CreateCommand();

        // With MAX(), SQLite takes the bare column from the row that holds the maximum.
        command.CommandText = "SELECT address, value, MAX(ts) FROM register_change GROUP BY address";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            _lastRegisters[reader.GetInt32(0)] = (ushort)reader.GetInt32(1);
        }
    }

    private void WriteSample(SqliteTransaction transaction, DateTime utc, RegisterImage registers)
    {
        _insertSample.Transaction = transaction;
        _sampleTime.Value = ToUnix(utc);
        for (var i = 0; i < _sampleValues.Length; i++)
        {
            _sampleValues[i].Value = Fields[i].Read(registers);
        }

        _insertSample.ExecuteNonQuery();
    }

    private List<KeyValuePair<int, ushort>> WriteRegisterChanges(SqliteTransaction transaction, DateTime utc, RegisterImage registers)
    {
        var changes = registers.Values
            .Where(r => !_lastRegisters.TryGetValue(r.Key, out var last) || last != r.Value)
            .ToList();

        using var command = _writer.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO register_change (ts, address, value) VALUES (@ts, @a, @v)";
        command.Parameters.AddWithValue("@ts", ToUnix(utc));
        var address = command.Parameters.Add("@a", SqliteType.Integer);
        var value = command.Parameters.Add("@v", SqliteType.Integer);
        foreach (var change in changes)
        {
            address.Value = change.Key;
            value.Value = (int)change.Value;
            command.ExecuteNonQuery();
        }

        return changes;
    }

    private void Query(string expressions, DateTime fromUtc, DateTime toUtc, TimeSpan bucket, Action<SqliteDataReader> onRow)
    {
        using var connection = OpenReader();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ts / @bucket * @bucket AS slot, {expressions} FROM sample WHERE ts >= @from AND ts < @to GROUP BY slot ORDER BY slot";
        command.Parameters.AddWithValue("@bucket", Math.Max(1, (long)bucket.TotalSeconds));
        command.Parameters.AddWithValue("@from", ToUnix(fromUtc));
        command.Parameters.AddWithValue("@to", ToUnix(toUtc));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            onRow(reader);
        }
    }

    private void ReportHealth(string? error)
    {
        if (_lastError != error)
        {
            _lastError = error;
            HealthChanged?.Invoke(error);
        }
    }
}
