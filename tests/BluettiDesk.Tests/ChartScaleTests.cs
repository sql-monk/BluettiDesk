using System.Globalization;
using BluettiDesk.Core.Journal;
using BluettiDesk.Ui;

namespace BluettiDesk.Tests;

public class ChartScaleTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0);

    private static ChartData Data(params double[] values) => new(
        Now.AddHours(-1),
        Now,
        [new ChartSeries("x", "В", values.Select((v, i) => new TimePoint(Now.AddMinutes(-values.Length + i), v)).ToList())],
        TimeSpan.FromMinutes(1));

    [Fact]
    public void PowerKeepsItsMinimumTop()
    {
        var scale = ChartScale.For(Data(0, 62) with { AxisMin = 0, MinTop = 100 });

        Assert.Equal((0d, 100d, 25d), (scale.Min, scale.Max, scale.Step));
        Assert.Equal(new[] { 0d, 25d, 50d, 75d, 100d }, scale.Ticks());
    }

    [Fact]
    public void ABatteryVoltageZoomsInInsteadOfLookingFlat()
    {
        var scale = ChartScale.For(Data(32.5, 33.1));

        Assert.True(scale.Min > 30, $"Min was {scale.Min}");
        Assert.True(scale.Min <= 32.5 && scale.Max >= 33.1);
    }

    [Fact]
    public void ValuesNearZeroKeepZeroOnTheAxis()
    {
        Assert.Equal(0, ChartScale.For(Data(5, 400)).Min);
    }

    [Fact]
    public void AVoltageWithLimitsKeepsItsNormalRangeInView()
    {
        var scale = ChartScale.For(Data(229, 231) with { Limits = BluettiDesk.Core.Station.StationLimits.MainsVoltage });

        Assert.True(scale.Min <= 185 && scale.Max >= 253, $"{scale.Min}..{scale.Max}");
        Assert.True(scale.Min > 100, $"Min was {scale.Min}");
    }

    [Fact]
    public void PercentUsesTheWholeRange()
    {
        var scale = ChartScale.For(Data(34, 35) with { AxisMin = 0, AxisMax = 100 });

        Assert.Equal((0d, 100d), (scale.Min, scale.Max));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(1d)]
    [InlineData(230d)]
    public void AFlatLineStillGetsAValidAxis(double value)
    {
        var scale = ChartScale.For(Data(value, value));

        Assert.True(scale.Max > scale.Min);
        Assert.True(scale.Min <= value && scale.Max >= value);
    }

    [Fact]
    public void NegativeValuesAreCovered()
    {
        var scale = ChartScale.For(Data(-5, 3));

        Assert.True(scale.Min <= -5 && scale.Max >= 3);
    }

    [Theory]
    [InlineData(0d, 1d)]
    [InlineData(1d, 1d)]
    [InlineData(25d, 27d)]
    public void WholeNumberValuesGetWholeNumberTicks(double low, double high)
    {
        var scale = ChartScale.For(Data(low, high) with { MinStep = 1 });

        Assert.True(scale.Min <= low && scale.Max >= high);
        Assert.All(scale.Ticks(), t => Assert.Equal(Math.Round(t), t));
    }

    [Fact]
    public void NoDataStillDrawsAnAxis()
    {
        var scale = ChartScale.For(null);

        Assert.True(scale.Max > scale.Min);
        Assert.NotEmpty(scale.Ticks());
    }

    [Theory]
    [InlineData(0, 100, "25")]
    [InlineData(0, 10, "2,5")]
    [InlineData(32, 33, "32,25")]
    public void TickLabelsShowOnlyTheDigitsTheStepNeeds(double low, double high, string expected)
    {
        var scale = ChartScale.Nice(low, high);

        Assert.Equal(expected, scale.FormatTick(scale.Min + scale.Step, CultureInfo.GetCultureInfo("uk-UA")));
    }
}
