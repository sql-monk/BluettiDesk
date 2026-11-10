using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BluettiDesk.Core.Outages;

namespace BluettiDesk.Outages;

public enum StripKind
{
    Unknown,
    On,
    Off,
    Maybe,
}

/// <summary>A stretch of a day in Kyiv wall-clock minutes, [From, To).</summary>
public readonly record struct StripSegment(int From, int To, StripKind Kind)
{
    public static IReadOnlyList<StripSegment> FromSlots(IReadOnlyList<OutageSlot> slots) =>
        Merge(slots.Count, i => slots[i] switch
        {
            OutageSlot.On => StripKind.On,
            OutageSlot.Off => StripKind.Off,
            OutageSlot.Maybe => StripKind.Maybe,
            _ => StripKind.Unknown,
        }, OutageSchedule.SlotMinutes);

    /// <summary>What the station saw, up to <paramref name="untilMinute"/>: later minutes have not happened yet and are left out.</summary>
    public static IReadOnlyList<StripSegment> FromFact(IReadOnlyList<GridFact> minutes, int untilMinute) =>
        Merge(Math.Clamp(untilMinute, 0, minutes.Count), i => minutes[i] switch
        {
            GridFact.On => StripKind.On,
            GridFact.Off => StripKind.Off,
            _ => StripKind.Unknown,
        }, 1);

    private static List<StripSegment> Merge(int count, Func<int, StripKind> kindOf, int unit)
    {
        var segments = new List<StripSegment>();
        for (var i = 0; i < count; i++)
        {
            var kind = kindOf(i);
            if (segments.Count > 0 && segments[^1].Kind == kind)
            {
                segments[^1] = segments[^1] with { To = (i + 1) * unit };
            }
            else
            {
                segments.Add(new StripSegment(i * unit, (i + 1) * unit, kind));
            }
        }

        return segments;
    }
}

/// <summary>
/// A day as a horizontal strip: amber with power, slate blue without, hatched where an outage is possible, grey where
/// nothing is known. A marker shows the current time; hovering names the stretch under the pointer.
/// </summary>
public sealed class OutageStrip : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = Register(nameof(Segments), null);
    public static readonly DependencyProperty NowMinuteProperty = Register(nameof(NowMinute), -1);
    public static readonly DependencyProperty ShowAxisProperty = Register(nameof(ShowAxis), false);
    public static readonly DependencyProperty OnBrushProperty = Register(nameof(OnBrush), Brushes.Green);
    public static readonly DependencyProperty OffBrushProperty = Register(nameof(OffBrush), Brushes.Red);
    public static readonly DependencyProperty MaybeBrushProperty = Register(nameof(MaybeBrush), Brushes.Orange);
    public static readonly DependencyProperty UnknownBrushProperty = Register(nameof(UnknownBrush), Brushes.LightGray);
    public static readonly DependencyProperty MarkerBrushProperty = Register(nameof(MarkerBrush), Brushes.Black);
    public static readonly DependencyProperty TextBrushProperty = Register(nameof(TextBrush), Brushes.Gray);
    public static readonly DependencyProperty BarHeightProperty = Register(nameof(BarHeight), 28.0);
    public static readonly DependencyProperty EmptyTextProperty = Register(nameof(EmptyText), string.Empty);

    private const double AxisHeight = 18;
    private const double Gap = 1;
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative };

    public OutageStrip()
    {
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 0);
        ToolTipService.SetShowDuration(this, int.MaxValue);
        _tip.Opened += (_, _) => _tip.HorizontalOffset = Mouse.GetPosition(this).X + 12;
    }

    public IReadOnlyList<StripSegment>? Segments { get => (IReadOnlyList<StripSegment>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    /// <summary>Kyiv minute of the day to mark, or -1.</summary>
    public int NowMinute { get => (int)GetValue(NowMinuteProperty); set => SetValue(NowMinuteProperty, value); }

    public bool ShowAxis { get => (bool)GetValue(ShowAxisProperty); set => SetValue(ShowAxisProperty, value); }

    public Brush OnBrush { get => (Brush)GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }

    public Brush OffBrush { get => (Brush)GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }

    public Brush MaybeBrush { get => (Brush)GetValue(MaybeBrushProperty); set => SetValue(MaybeBrushProperty, value); }

    public Brush UnknownBrush { get => (Brush)GetValue(UnknownBrushProperty); set => SetValue(UnknownBrushProperty, value); }

    public Brush MarkerBrush { get => (Brush)GetValue(MarkerBrushProperty); set => SetValue(MarkerBrushProperty, value); }

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    public double BarHeight { get => (double)GetValue(BarHeightProperty); set => SetValue(BarHeightProperty, value); }

    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    public static string Describe(StripKind kind) => kind switch
    {
        StripKind.On => "Світло є",
        StripKind.Off => "Світла немає",
        StripKind.Maybe => "Можливе відключення",
        _ => "Немає даних",
    };

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, BarHeight + (ShowAxis ? AxisHeight : 0));

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var bar = BarHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));

        if (Segments is not { Count: > 0 } segments)
        {
            if (EmptyText.Length > 0)
            {
                var text = Text(EmptyText, 12);
                dc.DrawText(text, new Point(0, (bar - text.Height) / 2));
            }
        }
        else
        {
            foreach (var segment in segments)
            {
                var left = X(segment.From) + Gap;
                var right = X(segment.To) - Gap;
                if (right - left < 0.5)
                {
                    right = left + 0.5;
                }

                var brush = segment.Kind switch
                {
                    StripKind.On => OnBrush,
                    StripKind.Off => OffBrush,
                    StripKind.Maybe => MaybeBrush,
                    _ => UnknownBrush,
                };
                dc.DrawRoundedRectangle(brush, null, new Rect(left, 0, right - left, bar), 3, 3);
            }
        }

        if (NowMinute >= 0)
        {
            var x = X(NowMinute);
            var halo = new Pen(Background(), 4);
            halo.Freeze();
            var pen = new Pen(MarkerBrush, 2);
            pen.Freeze();
            dc.DrawLine(halo, new Point(x, -3), new Point(x, bar + 3));
            dc.DrawLine(pen, new Point(x, -3), new Point(x, bar + 3));
        }

        if (ShowAxis)
        {
            for (var hour = 0; hour <= 24; hour += 3)
            {
                var label = Text($"{hour:00}", 11);
                var x = X(hour * 60);
                var left = hour == 0 ? x : hour == 24 ? x - label.Width : x - label.Width / 2;
                dc.DrawText(label, new Point(left, bar + 3));
            }
        }

        double X(int minute) => minute / (double)KyivTime.MinutesPerDay * width;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        var minute = (int)Math.Clamp(position.X / Math.Max(1, ActualWidth) * KyivTime.MinutesPerDay, 0, KyivTime.MinutesPerDay - 1);
        var segment = Segments?.FirstOrDefault(s => s.From <= minute && minute < s.To);
        _tip.Content = segment is { } found && found.To > found.From
            ? $"{OutageTexts.Time(found.From)}–{OutageTexts.Time(found.To)} · {Describe(found.Kind)}"
            : "Немає даних";
        _tip.HorizontalOffset = position.X + 12;
        _tip.VerticalOffset = -28;
    }

    private static DependencyProperty Register(string name, object? fallback) =>
        DependencyProperty.Register(name, fallback?.GetType() == typeof(int) ? typeof(int) : fallback?.GetType() == typeof(double) ? typeof(double) : fallback?.GetType() == typeof(bool) ? typeof(bool) : fallback is Brush ? typeof(Brush) : fallback is string ? typeof(string) : typeof(IReadOnlyList<StripSegment>), typeof(OutageStrip),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Brush Background() => (Brush?)TryFindResource("CardBackgroundFillColorDefaultBrush") ?? Brushes.White;

    private FormattedText Text(string text, double size) =>
        new(text, CultureInfo.GetCultureInfo("uk-UA"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
