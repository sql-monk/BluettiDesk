using System.Globalization;
using BluettiDesk.Core.Journal;

namespace BluettiDesk.Ui;

public sealed record ChartSeries(string Name, string Unit, IReadOnlyList<TimePoint> Points);

/// <param name="MaxGap">Points further apart than this are not joined: the station was not being read in between.</param>
public sealed record ChartData(DateTime From, DateTime To, IReadOnlyList<ChartSeries> Series, TimeSpan MaxGap)
{
    public double? AxisMin { get; init; }

    public double? AxisMax { get; init; }

    /// <summary>Keeps the axis from zooming into noise when the values are tiny.</summary>
    public double MinTop { get; init; }

    /// <summary>Whole-number values, like an on/off flag, should not get ticks such as 0,25.</summary>
    public double MinStep { get; init; }
}

/// <summary>The value axis: round numbers from <see cref="Min"/> to <see cref="Max"/>.</summary>
public readonly record struct ChartScale(double Min, double Max, double Step)
{
    private static readonly double[] RoundSteps = [1, 2, 2.5, 5, 10];

    public static ChartScale For(ChartData? data)
    {
        var points = data?.Series.SelectMany(s => s.Points).ToList() ?? [];
        var low = points.Count > 0 ? points.Min(p => p.Value) : 0;
        var high = points.Count > 0 ? points.Max(p => p.Value) : 0;

        // Keep zero on the axis unless the values all sit high above it, like a battery voltage.
        if (low >= 0 && low < high / 2)
        {
            low = 0;
        }

        return Nice(data?.AxisMin ?? low, data?.AxisMax ?? Math.Max(high, data?.MinTop ?? 0), data?.MinStep ?? 0);
    }

    public static ChartScale Nice(double low, double high, double minStep = 0)
    {
        if (high <= low)
        {
            var margin = low == 0 ? 1 : Math.Abs(low) * 0.05;
            low -= low == 0 ? 0 : margin;
            high += margin;
        }

        var rawStep = (high - low) / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
        var step = Math.Max(minStep, RoundSteps.First(s => s * magnitude >= rawStep * 0.999) * magnitude);
        return new ChartScale(Math.Floor(low / step + 1e-9) * step, Math.Ceiling(high / step - 1e-9) * step, step);
    }

    public IEnumerable<double> Ticks()
    {
        var count = (int)Math.Round((Max - Min) / Step);
        for (var i = 0; i <= count; i++)
        {
            yield return Min + i * Step;
        }
    }

    public string FormatTick(double value, CultureInfo culture)
    {
        var decimals = 0;
        while (decimals < 4 && Math.Abs(Step * Math.Pow(10, decimals) - Math.Round(Step * Math.Pow(10, decimals))) > 1e-9)
        {
            decimals++;
        }

        return value.ToString("F" + decimals, culture);
    }
}
