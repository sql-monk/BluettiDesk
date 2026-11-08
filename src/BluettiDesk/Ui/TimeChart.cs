using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BluettiDesk.Core.Journal;
using BluettiDesk.Core.Station;

namespace BluettiDesk.Ui;

/// <summary>One or two series over time on a single axis, with a crosshair and tooltip on hover.</summary>
public sealed class TimeChart : FrameworkElement
{
    private const double LeftGutter = 52;
    private const double RightGutter = 100;
    private const double TopGutter = 10;
    private const double BottomGutter = 24;
    private const double LineThickness = 2;
    private const double MarkerRadius = 4;
    private const double LabelSize = 12;
    private const double MinLabelGap = 16;
    private const double ZoneOpacity = 0.16;
    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA");
    private static readonly Typeface LabelFace = new("Segoe UI");

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(ChartData), typeof(TimeChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(TimeChart), new FrameworkPropertyMetadata("Даних поки немає", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PrimaryBrushProperty = RenderBrush(nameof(PrimaryBrush));
    public static readonly DependencyProperty SecondaryBrushProperty = RenderBrush(nameof(SecondaryBrush));
    public static readonly DependencyProperty TextBrushProperty = RenderBrush(nameof(TextBrush));
    public static readonly DependencyProperty MutedBrushProperty = RenderBrush(nameof(MutedBrush));
    public static readonly DependencyProperty GridBrushProperty = RenderBrush(nameof(GridBrush));
    public static readonly DependencyProperty TooltipBrushProperty = RenderBrush(nameof(TooltipBrush));
    public static readonly DependencyProperty WarningBrushProperty = RenderBrush(nameof(WarningBrush));
    public static readonly DependencyProperty CriticalBrushProperty = RenderBrush(nameof(CriticalBrush));

    private Point? _pointer;

    public ChartData? Data { get => (ChartData?)GetValue(DataProperty); set => SetValue(DataProperty, value); }

    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    public Brush? PrimaryBrush { get => (Brush?)GetValue(PrimaryBrushProperty); set => SetValue(PrimaryBrushProperty, value); }

    public Brush? SecondaryBrush { get => (Brush?)GetValue(SecondaryBrushProperty); set => SetValue(SecondaryBrushProperty, value); }

    public Brush? TextBrush { get => (Brush?)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    public Brush? MutedBrush { get => (Brush?)GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }

    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public Brush? TooltipBrush { get => (Brush?)GetValue(TooltipBrushProperty); set => SetValue(TooltipBrushProperty, value); }

    /// <summary>Tints the band between the normal and the dangerous limit.</summary>
    public Brush? WarningBrush { get => (Brush?)GetValue(WarningBrushProperty); set => SetValue(WarningBrushProperty, value); }

    /// <summary>Tints what lies beyond the dangerous limit.</summary>
    public Brush? CriticalBrush { get => (Brush?)GetValue(CriticalBrushProperty); set => SetValue(CriticalBrushProperty, value); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _pointer = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent, not nothing: the empty parts of the plot still have to receive the mouse.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        var plot = new Rect(
            LeftGutter,
            TopGutter,
            Math.Max(0, ActualWidth - LeftGutter - RightGutter),
            Math.Max(0, ActualHeight - TopGutter - BottomGutter));
        if (plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        var data = Data;
        var now = DateTime.Now;
        var from = data?.From ?? now.AddMinutes(-30);
        var to = data?.To ?? now;
        var scale = ChartScale.For(data);

        DrawValueAxis(dc, plot, scale);
        if (data?.Limits is { } limits)
        {
            DrawLimits(dc, plot, scale, limits, data);
        }

        DrawTimeAxis(dc, plot, from, to);

        if (data is null || data.Series.All(s => s.Points.Count == 0))
        {
            dc.DrawText(Text(EmptyText, MutedBrush), new Point(plot.Left + 12, plot.Top + plot.Height / 2 - 18));
            return;
        }

        for (var i = 0; i < data.Series.Count; i++)
        {
            DrawSeries(dc, plot, data, scale, data.Series[i], SeriesBrush(i));
        }

        DrawEndLabels(dc, plot, data, scale);
        DrawHover(dc, plot, data, scale);
    }

    private static DependencyProperty RenderBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(TimeChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static double XOf(Rect plot, DateTime time, DateTime from, DateTime to) =>
        plot.Left + (time - from).TotalSeconds / Math.Max(1, (to - from).TotalSeconds) * plot.Width;

    private static double YOf(Rect plot, double value, ChartScale scale) =>
        plot.Bottom - (value - scale.Min) / (scale.Max - scale.Min) * plot.Height;

    private static TimeSpan TimeStep(TimeSpan window) => window.TotalHours switch
    {
        <= 1 => TimeSpan.FromMinutes(10),
        <= 7 => TimeSpan.FromHours(1),
        <= 25 => TimeSpan.FromHours(4),
        <= 72 => TimeSpan.FromHours(12),
        <= 240 => TimeSpan.FromDays(1),
        <= 960 => TimeSpan.FromDays(5),
        <= 2880 => TimeSpan.FromDays(15),
        <= 8760 => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(90),
    };

    private static string FormatValue(double value) =>
        value.ToString(Math.Abs(value) >= 100 ? "0" : Math.Abs(value) >= 10 ? "0.#" : "0.##", Ukrainian);

    private static string WithUnit(double value, string unit) =>
        unit.Length == 0 ? FormatValue(value) : $"{FormatValue(value)} {unit}";

    private Brush? SeriesBrush(int index) => index == 0 ? PrimaryBrush : SecondaryBrush;

    private void DrawValueAxis(DrawingContext dc, Rect plot, ChartScale scale)
    {
        var grid = new Pen(GridBrush, 1);
        grid.Freeze();
        foreach (var tick in scale.Ticks())
        {
            var y = Math.Round(YOf(plot, tick, scale)) + 0.5;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(scale.FormatTick(tick, Ukrainian), MutedBrush);
            dc.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }
    }

    /// <summary>
    /// Tints the warning and danger zones above and below the normal range and dashes its edges, so a line that leaves
    /// the range stands out without changing its colour. A danger zone is as deep as a warning zone, or deeper where the
    /// data goes further, so an empty zone does not fill the chart. Nothing is tinted where the source is absent.
    /// </summary>
    private void DrawLimits(DrawingContext dc, Rect plot, ChartScale scale, Limits limits, ChartData data)
    {
        var floor = Math.Max(scale.Min, limits.AbsentBelow);
        var present = data.Series.SelectMany(s => s.Points).Select(p => p.Value).Where(v => v >= limits.AbsentBelow).ToList();
        var highest = present.Count > 0 ? present.Max() : limits.CriticalHigh;
        var lowest = present.Count > 0 ? present.Min() : limits.CriticalLow;

        Zone(limits.CriticalHigh, Math.Max(highest, 2 * limits.CriticalHigh - limits.WarnHigh), CriticalBrush);
        Zone(limits.WarnHigh, limits.CriticalHigh, WarningBrush);
        Zone(limits.CriticalLow, limits.WarnLow, WarningBrush);
        Zone(Math.Min(lowest, 2 * limits.CriticalLow - limits.WarnLow), limits.CriticalLow, CriticalBrush);

        var edge = new Pen(MutedBrush, 1) { DashStyle = new DashStyle([4, 3], 0) };
        edge.Freeze();
        foreach (var (value, above) in new[] { (limits.WarnHigh, true), (limits.WarnLow, false) })
        {
            if (value <= floor || value > scale.Max)
            {
                continue;
            }

            // On the left, away from the latest values, and on a backing so that a line passing by does not hide it.
            var y = Math.Round(YOf(plot, value, scale)) + 0.5;
            dc.DrawLine(edge, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(FormatValue(value), MutedBrush);
            var box = new Rect(plot.Left + 4, above ? y - label.Height - 2 : y + 2, label.Width + 6, label.Height);
            dc.DrawRoundedRectangle(TooltipBrush, null, box, 3, 3);
            dc.DrawText(label, new Point(box.Left + 3, box.Top));
        }

        void Zone(double from, double to, Brush? brush)
        {
            var low = Math.Max(from, floor);
            var high = Math.Min(to, scale.Max);
            if (brush is null || high <= low)
            {
                return;
            }

            var tint = brush.CloneCurrentValue();
            tint.Opacity = ZoneOpacity;
            tint.Freeze();
            dc.DrawRectangle(tint, null, new Rect(new Point(plot.Left, YOf(plot, high, scale)), new Point(plot.Right, YOf(plot, low, scale))));
        }
    }

    private void DrawTimeAxis(DrawingContext dc, Rect plot, DateTime from, DateTime to)
    {
        var step = TimeStep(to - from);
        var format = step < TimeSpan.FromDays(1) ? "HH:mm" : "dd.MM";
        var tick = from.Date;
        while (tick < from)
        {
            tick += step;
        }

        for (; tick <= to; tick += step)
        {
            var label = Text(tick.ToString(format, Ukrainian), MutedBrush);
            var x = XOf(plot, tick, from, to);
            var left = Math.Clamp(x - label.Width / 2, plot.Left, plot.Right - label.Width);
            dc.DrawText(label, new Point(left, plot.Bottom + 6));
        }
    }

    private void DrawSeries(DrawingContext dc, Rect plot, ChartData data, ChartScale scale, ChartSeries series, Brush? brush)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < series.Points.Count; i++)
            {
                var sample = series.Points[i];
                var point = new Point(XOf(plot, sample.Time, data.From, data.To), YOf(plot, sample.Value, scale));
                if (i == 0 || sample.Time - series.Points[i - 1].Time > data.MaxGap)
                {
                    context.BeginFigure(point, false, false);
                }

                // On a new figure this is a zero-length line: with round caps, a lone point between two gaps still shows.
                context.LineTo(point, true, true);
            }
        }

        geometry.Freeze();
        var pen = new Pen(brush, LineThickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        dc.PushClip(new RectangleGeometry(new Rect(plot.Left - LineThickness, plot.Top - LineThickness, plot.Width + 2 * LineThickness, plot.Height + 2 * LineThickness)));
        dc.DrawGeometry(null, pen, geometry);
        dc.Pop();
    }

    /// <summary>Names the lines at their ends. When the ends nearly touch, the legend and tooltip carry the names instead.</summary>
    private void DrawEndLabels(DrawingContext dc, Rect plot, ChartData data, ChartScale scale)
    {
        var ends = data.Series
            .Where(s => s.Points.Count > 0 && data.To - s.Points[^1].Time <= data.MaxGap * 2)
            .Select(s => (Series: s, Last: s.Points[^1].Value, Y: YOf(plot, s.Points[^1].Value, scale)))
            .ToList();

        if (ends.Count == 2 && Math.Abs(ends[0].Y - ends[1].Y) < MinLabelGap)
        {
            return;
        }

        foreach (var end in ends)
        {
            var value = WithUnit(end.Last, end.Series.Unit);
            var label = Text(data.Series.Count > 1 ? $"{end.Series.Name} {value}" : value, TextBrush);
            dc.DrawText(label, new Point(plot.Right + 8, end.Y - label.Height / 2));
        }
    }

    private void DrawHover(DrawingContext dc, Rect plot, ChartData data, ChartScale scale)
    {
        if (_pointer is not { } pointer || !plot.Contains(pointer))
        {
            return;
        }

        var seconds = (pointer.X - plot.Left) / plot.Width * (data.To - data.From).TotalSeconds;
        var pointerTime = data.From.AddSeconds(seconds);
        var reach = Math.Max(data.MaxGap.TotalSeconds, (data.To - data.From).TotalSeconds * 12 / plot.Width);

        var nearest = data.Series
            .Select(s => s.Points.Count == 0 ? (TimePoint?)null : s.Points.MinBy(p => Math.Abs((p.Time - pointerTime).Ticks)))
            .ToList();
        var rows = new List<(ChartSeries Series, TimePoint Point, Brush? Brush)>();
        for (var i = 0; i < nearest.Count; i++)
        {
            if (nearest[i] is { } point && Math.Abs((point.Time - pointerTime).TotalSeconds) <= reach)
            {
                rows.Add((data.Series[i], point, SeriesBrush(i)));
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        var x = XOf(plot, rows[0].Point.Time, data.From, data.To);
        var crosshair = new Pen(MutedBrush, 1);
        crosshair.Freeze();
        dc.DrawLine(crosshair, new Point(x, plot.Top), new Point(x, plot.Bottom));

        var ring = new Pen(TooltipBrush, 2);
        ring.Freeze();
        foreach (var row in rows)
        {
            dc.DrawEllipse(row.Brush, ring, new Point(XOf(plot, row.Point.Time, data.From, data.To), YOf(plot, row.Point.Value, scale)), MarkerRadius, MarkerRadius);
        }

        DrawTooltip(dc, plot, data, rows, x);
    }

    private void DrawTooltip(DrawingContext dc, Rect plot, ChartData data, List<(ChartSeries Series, TimePoint Point, Brush? Brush)> rows, double x)
    {
        const double Padding = 10;
        const double KeyWidth = 14;
        var timeFormat = data.To - data.From <= TimeSpan.FromHours(1) ? "HH:mm:ss" : "dd.MM HH:mm";
        var title = Text(rows[0].Point.Time.ToString(timeFormat, Ukrainian), MutedBrush);
        var labels = rows
            .Select(r => Text(data.Series.Count > 1 ? $"{r.Series.Name}  {WithUnit(r.Point.Value, r.Series.Unit)}" : WithUnit(r.Point.Value, r.Series.Unit), TextBrush))
            .ToList();

        var width = Math.Max(title.Width, KeyWidth + labels.Max(l => l.Width)) + 2 * Padding;
        var lineHeight = title.Height + 2;
        var height = (labels.Count + 1) * lineHeight + 2 * Padding;
        var left = x + 12 + width > plot.Right ? x - 12 - width : x + 12;
        var top = plot.Top + 4;

        dc.DrawRoundedRectangle(TooltipBrush, new Pen(GridBrush, 1), new Rect(left, top, width, height), 6, 6);
        dc.DrawText(title, new Point(left + Padding, top + Padding));
        for (var i = 0; i < labels.Count; i++)
        {
            var rowY = top + Padding + (i + 1) * lineHeight;
            dc.DrawEllipse(rows[i].Brush, null, new Point(left + Padding + 4, rowY + labels[i].Height / 2), MarkerRadius, MarkerRadius);
            dc.DrawText(labels[i], new Point(left + Padding + KeyWidth, rowY));
        }
    }

    private FormattedText Text(string text, Brush? brush) => new(
        text,
        Ukrainian,
        FlowDirection.LeftToRight,
        LabelFace,
        LabelSize,
        brush ?? Brushes.Gray,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
