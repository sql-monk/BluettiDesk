using BluettiDesk.Core.Journal;

namespace BluettiDesk.Ui;

/// <summary>The last half hour for the overview chart. The journal keeps the long-term record.</summary>
public sealed class PowerHistory
{
    private const string Watts = "Вт";
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(30);
    private const double MinTopWatts = 100;

    private readonly List<PowerPoint> _points = [];

    public static TimeSpan Length => Window;

    public void Add(PowerPoint point)
    {
        _points.Add(point);
        var cutoff = point.Time - Window;
        var stale = _points.FindIndex(p => p.Time >= cutoff);
        if (stale > 0)
        {
            _points.RemoveRange(0, stale);
        }
    }

    /// <summary>Fills in what the journal knows from before the app was opened.</summary>
    public void Prepend(IEnumerable<PowerPoint> older)
    {
        var firstKnown = _points.Count > 0 ? _points[0].Time : DateTime.MaxValue;
        _points.InsertRange(0, older.Where(p => p.Time < firstKnown));
    }

    /// <summary>The mean output over the last <paramref name="span"/>, or null without readings in it.</summary>
    public double? MeanOutput(DateTime now, TimeSpan span)
    {
        var recent = _points.Where(p => p.Time > now - span && p.Time <= now).ToList();
        return recent.Count == 0 ? null : recent.Average(p => p.OutputWatts);
    }

    public ChartData ToChartData(DateTime now) => new(
        now - Window,
        now,
        [
            new ChartSeries("Вхід", Watts, _points.Select(p => new TimePoint(p.Time, p.InputWatts)).ToList()),
            new ChartSeries("Вихід", Watts, _points.Select(p => new TimePoint(p.Time, p.OutputWatts)).ToList()),
        ],
        MaxGap)
    {
        AxisMin = 0,
        MinTop = MinTopWatts,
    };
}
