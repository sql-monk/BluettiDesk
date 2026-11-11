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

    /// <summary>What the station saw, where it agrees with the schedule or there is none to compare.</summary>
    FactOn,
    FactOff,

    /// <summary>No power while the schedule promised it.</summary>
    UnplannedOff,

    /// <summary>Power while the schedule said there would be none.</summary>
    UnexpectedOn,
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
    public static IReadOnlyList<StripSegment> FromFact(IReadOnlyList<FactVerdict> minutes, int untilMinute) =>
        Merge(Math.Clamp(untilMinute, 0, minutes.Count), i => minutes[i] switch
        {
            FactVerdict.On => StripKind.FactOn,
            FactVerdict.Off => StripKind.FactOff,
            FactVerdict.UnplannedOff => StripKind.UnplannedOff,
            FactVerdict.UnexpectedOn => StripKind.UnexpectedOn,
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
/// How far the charge of the station carries, in minutes of the strip's day: it lasts over [From, Until), and the grid is
/// still missing over [Until, ShortUntil) when the charge runs out first.
/// </summary>
public sealed record ReachSpan(int From, int Until, int ShortUntil, string LastsText, string ShortText);

/// <summary>
/// A day as a horizontal strip. The schedule is amber with power, slate blue without, hatched where an outage is
/// possible and grey where nothing is known. What the station saw has its own colours, so that a past day never looks
/// like a schedule: light and dark neutrals where it agrees with the schedule, and two bright ones where it does not.
/// A marker shows the current time, an optional line under the strip how far the charge of the station carries, and
/// hovering names the stretch under the pointer.
/// </summary>
public sealed class OutageStrip : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = Register<IReadOnlyList<StripSegment>?>(nameof(Segments), null);
    public static readonly DependencyProperty NowMinuteProperty = Register(nameof(NowMinute), -1);
    public static readonly DependencyProperty ShowAxisProperty = Register(nameof(ShowAxis), false);
    public static readonly DependencyProperty UnknownAsTrackProperty = Register(nameof(UnknownAsTrack), false);
    public static readonly DependencyProperty ReachProperty = Register<ReachSpan?>(nameof(Reach), null);
    public static readonly DependencyProperty OnBrushProperty = Register<Brush>(nameof(OnBrush), Brushes.Goldenrod);
    public static readonly DependencyProperty OffBrushProperty = Register<Brush>(nameof(OffBrush), Brushes.SteelBlue);
    public static readonly DependencyProperty MaybeBrushProperty = Register<Brush>(nameof(MaybeBrush), Brushes.LightSteelBlue);
    public static readonly DependencyProperty UnknownBrushProperty = Register<Brush>(nameof(UnknownBrush), Brushes.LightGray);
    public static readonly DependencyProperty FactOnBrushProperty = Register<Brush>(nameof(FactOnBrush), Brushes.Silver);
    public static readonly DependencyProperty FactOffBrushProperty = Register<Brush>(nameof(FactOffBrush), Brushes.DimGray);
    public static readonly DependencyProperty UnplannedOffBrushProperty = Register<Brush>(nameof(UnplannedOffBrush), Brushes.OrangeRed);
    public static readonly DependencyProperty UnexpectedOnBrushProperty = Register<Brush>(nameof(UnexpectedOnBrush), Brushes.DeepSkyBlue);
    public static readonly DependencyProperty ReachBrushProperty = Register<Brush>(nameof(ReachBrush), Brushes.Gray);
    public static readonly DependencyProperty ShortBrushProperty = Register<Brush>(nameof(ShortBrush), Brushes.Red);
    public static readonly DependencyProperty MarkerBrushProperty = Register<Brush>(nameof(MarkerBrush), Brushes.Black);
    public static readonly DependencyProperty TextBrushProperty = Register<Brush>(nameof(TextBrush), Brushes.Gray);
    public static readonly DependencyProperty BarHeightProperty = Register(nameof(BarHeight), 28.0);
    public static readonly DependencyProperty EmptyTextProperty = Register(nameof(EmptyText), string.Empty);

    private const double AxisHeight = 18;
    private const double Gap = 1;
    private const double TrackHeight = 4;
    private const double ReachGap = 5;
    private const double ReachThickness = 4;
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

    /// <summary>Draws unknown stretches as a thin track instead of a bar: for what the station saw, nothing was recorded there.</summary>
    public bool UnknownAsTrack { get => (bool)GetValue(UnknownAsTrackProperty); set => SetValue(UnknownAsTrackProperty, value); }

    public ReachSpan? Reach { get => (ReachSpan?)GetValue(ReachProperty); set => SetValue(ReachProperty, value); }

    public Brush OnBrush { get => (Brush)GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }

    public Brush OffBrush { get => (Brush)GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }

    public Brush MaybeBrush { get => (Brush)GetValue(MaybeBrushProperty); set => SetValue(MaybeBrushProperty, value); }

    public Brush UnknownBrush { get => (Brush)GetValue(UnknownBrushProperty); set => SetValue(UnknownBrushProperty, value); }

    public Brush FactOnBrush { get => (Brush)GetValue(FactOnBrushProperty); set => SetValue(FactOnBrushProperty, value); }

    public Brush FactOffBrush { get => (Brush)GetValue(FactOffBrushProperty); set => SetValue(FactOffBrushProperty, value); }

    public Brush UnplannedOffBrush { get => (Brush)GetValue(UnplannedOffBrushProperty); set => SetValue(UnplannedOffBrushProperty, value); }

    public Brush UnexpectedOnBrush { get => (Brush)GetValue(UnexpectedOnBrushProperty); set => SetValue(UnexpectedOnBrushProperty, value); }

    public Brush ReachBrush { get => (Brush)GetValue(ReachBrushProperty); set => SetValue(ReachBrushProperty, value); }

    public Brush ShortBrush { get => (Brush)GetValue(ShortBrushProperty); set => SetValue(ShortBrushProperty, value); }

    public Brush MarkerBrush { get => (Brush)GetValue(MarkerBrushProperty); set => SetValue(MarkerBrushProperty, value); }

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    public double BarHeight { get => (double)GetValue(BarHeightProperty); set => SetValue(BarHeightProperty, value); }

    public string EmptyText { get => (string)GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    public static string Describe(StripKind kind) => kind switch
    {
        StripKind.On => "Світло є",
        StripKind.Off => "Світла немає",
        StripKind.Maybe => "Можливе відключення",
        StripKind.FactOn => "Світло було",
        StripKind.FactOff => "Світла не було",
        StripKind.UnplannedOff => "Світла не було, хоч за графіком мало бути",
        StripKind.UnexpectedOn => "Світло було, хоч за графіком не мало",
        _ => "Немає даних",
    };

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width, BarHeight + ReachHeight + (ShowAxis ? AxisHeight : 0));

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

                if (segment.Kind == StripKind.Unknown && UnknownAsTrack)
                {
                    dc.DrawRoundedRectangle(UnknownBrush, null, new Rect(left, (bar - TrackHeight) / 2, right - left, TrackHeight), TrackHeight / 2, TrackHeight / 2);
                    continue;
                }

                dc.DrawRoundedRectangle(BrushOf(segment.Kind), null, new Rect(left, 0, right - left, bar), 3, 3);
            }
        }

        if (Reach is { } reach)
        {
            var top = bar + ReachGap;
            Line(reach.From, reach.Until, ReachBrush);
            Line(reach.Until, reach.ShortUntil, ShortBrush);

            void Line(int from, int to, Brush brush)
            {
                from = Math.Clamp(from, 0, KyivTime.MinutesPerDay);
                to = Math.Clamp(to, 0, KyivTime.MinutesPerDay);
                if (to > from)
                {
                    var left = X(from) + Gap;
                    dc.DrawRoundedRectangle(brush, null, new Rect(left, top, Math.Max(ReachThickness, X(to) - Gap - left), ReachThickness), ReachThickness / 2, ReachThickness / 2);
                }
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
                dc.DrawText(label, new Point(left, bar + ReachHeight + 3));
            }
        }

        double X(int minute) => minute / (double)KyivTime.MinutesPerDay * width;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);
        var minute = (int)Math.Clamp(position.X / Math.Max(1, ActualWidth) * KyivTime.MinutesPerDay, 0, KyivTime.MinutesPerDay - 1);
        string text;
        if (Reach is { } reach && position.Y > BarHeight + 1 && position.Y < BarHeight + ReachHeight + 2
            && minute >= reach.From && minute < Math.Max(reach.Until, reach.ShortUntil))
        {
            text = minute < reach.Until ? reach.LastsText : reach.ShortText;
        }
        else
        {
            var segment = Segments?.FirstOrDefault(s => s.From <= minute && minute < s.To);
            text = segment is { } found && found.To > found.From
                ? $"{OutageTexts.Time(found.From)}–{OutageTexts.Time(found.To)} · {Describe(found.Kind)}"
                : "Немає даних";
        }

        _tip.Content = text;
        _tip.HorizontalOffset = position.X + 12;
        _tip.VerticalOffset = -28;
    }

    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(OutageStrip),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    private double ReachHeight => Reach is null ? 0 : ReachGap + ReachThickness;

    private Brush BrushOf(StripKind kind) => kind switch
    {
        StripKind.On => OnBrush,
        StripKind.Off => OffBrush,
        StripKind.Maybe => MaybeBrush,
        StripKind.FactOn => FactOnBrush,
        StripKind.FactOff => FactOffBrush,
        StripKind.UnplannedOff => UnplannedOffBrush,
        StripKind.UnexpectedOn => UnexpectedOnBrush,
        _ => UnknownBrush,
    };

    private Brush Background() => (Brush?)TryFindResource("CardBackgroundFillColorDefaultBrush") ?? Brushes.White;

    private FormattedText Text(string text, double size) =>
        new(text, CultureInfo.GetCultureInfo("uk-UA"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
