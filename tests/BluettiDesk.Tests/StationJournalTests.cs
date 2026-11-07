using System.Globalization;
using BluettiDesk.Core.Journal;
using BluettiDesk.Core.Station;
using Microsoft.Data.Sqlite;

namespace BluettiDesk.Tests;

public sealed class StationJournalTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);
    private static readonly LogField Soc = Ac180pLog.Fields.Single(f => f.Column == "soc_percent");
    private static readonly LogField BatteryVolts = Ac180pLog.Fields.Single(f => f.Column == "battery_v");

    private readonly string _folder = Directory.CreateTempSubdirectory("bluettidesk-tests-").FullName;

    private string DbPath => Path.Combine(_folder, "journal.db");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void AReadingCanBeReadBack()
    {
        using var journal = new StationJournal(DbPath);

        journal.Record(Start, Image(soc: 37, acOut: 65));

        var points = journal.QueryAverages(Soc, Start, Start.AddMinutes(1), TimeSpan.FromSeconds(10));
        Assert.Equal(37, Assert.Single(points).Value);
        Assert.Equal(Start.ToLocalTime(), points[0].Time);
        Assert.Equal(65, journal.QueryPower(Start, Start.AddMinutes(1), TimeSpan.FromSeconds(10))[0].OutputWatts);
    }

    [Fact]
    public void ScaledValuesAreStoredInTheirUnits()
    {
        using var journal = new StationJournal(DbPath);

        journal.Record(Start, Image(soc: 37, acOut: 65));

        var volts = journal.QueryAverages(BatteryVolts, Start, Start.AddMinutes(1), TimeSpan.FromSeconds(10));
        Assert.Equal(32.62, Assert.Single(volts).Value, 3);
    }

    [Fact]
    public void BucketsAverageTheirReadings()
    {
        using var journal = new StationJournal(DbPath);
        journal.Record(Start, Image(soc: 30, acOut: 100));
        journal.Record(Start.AddSeconds(10), Image(soc: 40, acOut: 200));
        journal.Record(Start.AddMinutes(1), Image(soc: 50, acOut: 300));

        var points = journal.QueryAverages(Soc, Start, Start.AddMinutes(2), TimeSpan.FromMinutes(1));

        Assert.Equal(new[] { 35d, 50d }, points.Select(p => p.Value));
    }

    [Fact]
    public void QueriesStayInsideThePeriod()
    {
        using var journal = new StationJournal(DbPath);
        journal.Record(Start, Image(soc: 10, acOut: 0));
        journal.Record(Start.AddHours(1), Image(soc: 20, acOut: 0));
        journal.Record(Start.AddHours(2), Image(soc: 30, acOut: 0));

        var points = journal.QueryAverages(Soc, Start.AddMinutes(30), Start.AddMinutes(90), TimeSpan.FromSeconds(10));

        Assert.Equal(20, Assert.Single(points).Value);
    }

    [Fact]
    public void ReadingsSurviveReopening()
    {
        using (var journal = new StationJournal(DbPath))
        {
            journal.Record(Start, Image(soc: 37, acOut: 65));
        }

        using var reopened = new StationJournal(DbPath);

        Assert.Equal(1, reopened.GetInfo().SampleCount);
        Assert.Equal(Start.ToLocalTime(), reopened.GetInfo().FirstSample);
    }

    [Fact]
    public void RegistersAreWrittenOnlyWhenTheyChange()
    {
        using (var journal = new StationJournal(DbPath))
        {
            journal.Record(Start, Image(soc: 37, acOut: 65));
            journal.Record(Start.AddMinutes(1), Image(soc: 37, acOut: 70));
        }

        Assert.Equal(new long[] { 37 }, RegisterHistory(102));
        Assert.Equal(new long[] { 65, 70 }, RegisterHistory(142));
    }

    [Fact]
    public void RegisterChangesAreWrittenAtTheirOwnPace()
    {
        using (var journal = new StationJournal(DbPath))
        {
            journal.Record(Start, Image(soc: 37, acOut: 65));
            journal.Record(Start.AddSeconds(10), Image(soc: 37, acOut: 70));
        }

        Assert.Equal(new long[] { 65 }, RegisterHistory(142));
    }

    [Fact]
    public void AfterReopeningOnlyRealChangesAreWritten()
    {
        using (var journal = new StationJournal(DbPath))
        {
            journal.Record(Start, Image(soc: 37, acOut: 65));
        }

        using (var journal = new StationJournal(DbPath))
        {
            journal.Record(Start.AddMinutes(5), Image(soc: 36, acOut: 65));
        }

        Assert.Equal(new long[] { 65 }, RegisterHistory(142));
        Assert.Equal(new long[] { 37, 36 }, RegisterHistory(102));
    }

    [Fact]
    public void ANewFieldIsAddedToAnExistingFile()
    {
        LogField[] older = [Soc];
        using (var journal = new StationJournal(DbPath, older))
        {
            journal.Record(Start, Image(soc: 37, acOut: 65));
        }

        LogField[] newer = [Soc, BatteryVolts];
        using var upgraded = new StationJournal(DbPath, newer);
        upgraded.Record(Start.AddSeconds(10), Image(soc: 36, acOut: 65));

        Assert.Equal(2, upgraded.GetInfo().SampleCount);
        Assert.Equal(32.62, Assert.Single(upgraded.QueryAverages(BatteryVolts, Start, Start.AddMinutes(1), TimeSpan.FromSeconds(10))).Value, 3);
    }

    [Fact]
    public void TheFileDescribesItself()
    {
        using (var journal = new StationJournal(DbPath))
        {
            journal.Record(Start, Image(soc: 37, acOut: 65));
        }

        Assert.Equal("Заряд", Scalar("SELECT title FROM field WHERE column_name = 'soc_percent'"));
        Assert.Equal(37L, Scalar("SELECT soc_percent FROM sample_local"));
        Assert.Equal(Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Scalar("SELECT local_time FROM sample_local"));
    }

    [Fact]
    public void ExportFollowsTheRegionalFormatForExcel()
    {
        var csv = Path.Combine(_folder, "out.csv");
        using var journal = new StationJournal(DbPath);
        journal.Record(Start, Image(soc: 37, acOut: 65));
        journal.Record(Start.AddHours(2), Image(soc: 36, acOut: 65));

        var rows = journal.ExportCsv(csv, Start, Start.AddHours(1), CultureWith(listSeparator: ";", decimalSeparator: ","));

        Assert.Equal(1, rows);
        var lines = File.ReadAllLines(csv);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Час;Заряд, %;", lines[0]);
        var header = lines[0].Split(';');
        var cells = lines[1].Split(';');
        Assert.Equal(header.Length, cells.Length);
        Assert.Equal("37", cells[Array.IndexOf(header, "Заряд, %")]);
        Assert.Equal("32,62", cells[Array.IndexOf(header, "Напруга батареї, В")]);
    }

    [Fact]
    public void ExportQuotesTitlesThatContainTheSeparator()
    {
        var csv = Path.Combine(_folder, "out.csv");
        using var journal = new StationJournal(DbPath);
        journal.Record(Start, Image(soc: 37, acOut: 65));

        journal.ExportCsv(csv, Start, Start.AddHours(1), CultureWith(listSeparator: ",", decimalSeparator: "."));

        Assert.StartsWith("Час,\"Заряд, %\",", File.ReadAllLines(csv)[0]);
    }

    [Fact]
    public void ExportStartsWithAByteOrderMarkSoExcelReadsCyrillic()
    {
        var csv = Path.Combine(_folder, "out.csv");
        using var journal = new StationJournal(DbPath);

        journal.ExportCsv(csv, Start, Start.AddHours(1), CultureInfo.InvariantCulture);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(csv)[..3]);
    }

    [Fact]
    public void QueryingAFieldOutsideTheListIsRefused()
    {
        using var journal = new StationJournal(DbPath);
        var injected = new LogField("1); DROP TABLE sample; --", "x", string.Empty, true, _ => 0);

        Assert.Throws<ArgumentException>(() => journal.QueryAverages(injected, Start, Start, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void AFailedWriteIsReportedInsteadOfThrown()
    {
        var journal = new StationJournal(DbPath);
        string? reported = null;
        journal.HealthChanged += error => reported = error;
        journal.Dispose();

        journal.Record(Start, Image(soc: 37, acOut: 65));

        Assert.NotNull(reported);
    }

    private static CultureInfo CultureWith(string listSeparator, string decimalSeparator)
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.TextInfo.ListSeparator = listSeparator;
        culture.NumberFormat.NumberDecimalSeparator = decimalSeparator;
        return culture;
    }

    private static RegisterImage Image(ushort soc, ushort acOut)
    {
        var image = new RegisterImage();
        image.Store(100, new ushort[] { 3262, 28, soc, 2 });
        image.Store(142, new[] { acOut });
        return image;
    }

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private long[] RegisterHistory(int address)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM register_change WHERE address = @a ORDER BY ts";
        command.Parameters.AddWithValue("@a", address);
        using var reader = command.ExecuteReader();
        var values = new List<long>();
        while (reader.Read())
        {
            values.Add(reader.GetInt64(0));
        }

        return [.. values];
    }
}
