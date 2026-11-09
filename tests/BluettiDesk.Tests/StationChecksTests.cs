using BluettiDesk.Core.Station;

namespace BluettiDesk.Tests;

public class StationChecksTests
{
    /// <summary>The station on 7 November 2026 at 16:31: grid at 289 V, not charging, the AC output on battery.</summary>
    private static readonly StationStatus Overvoltage = new()
    {
        SocPercent = 22,
        Activity = BatteryActivity.Discharging,
        MinutesRemaining = 177,
        AcInputVoltage = 289.2,
        AcInputFrequency = 50.0,
        AcInputWatts = 0,
        AcOutputOn = true,
        AcOutputVoltage = 229.7,
        AcOutputFrequency = 50.0,
        AcOutputWatts = 84,
    };

    private static readonly StationStatus Normal = Overvoltage with { AcInputVoltage = 231, AcInputWatts = 0 };

    [Fact]
    public void TodaysOvervoltageIsCritical()
    {
        var health = StationChecks.Evaluate(Overvoltage);

        Assert.Equal(Severity.Critical, health.MainsVoltage);
        Assert.Equal(Severity.Normal, health.OutputVoltage);
        var alert = Assert.Single(health.Alerts);
        Assert.Equal(AlertKind.MainsVoltage, alert.Kind);
        Assert.Equal("Перенапруга в мережі: 289,2 В", alert.Title);
        Assert.Equal("Норма 190–253 В. Станція не бере енергію з мережі, вихід AC живиться від батареї (229,7 В).", alert.Detail);
    }

    [Theory]
    [InlineData(230, Severity.Normal)]
    [InlineData(253, Severity.Normal)]
    [InlineData(200, Severity.Normal)]
    [InlineData(190, Severity.Normal)]
    [InlineData(253.1, Severity.Warning)]
    [InlineData(260, Severity.Warning)]
    [InlineData(260.1, Severity.Critical)]
    [InlineData(263.8, Severity.Critical)]
    [InlineData(189.9, Severity.Warning)]
    [InlineData(185, Severity.Warning)]
    [InlineData(170, Severity.Warning)]
    [InlineData(169.9, Severity.Critical)]
    [InlineData(81, Severity.Critical)]
    public void MainsVoltageLevels(double volts, Severity expected)
    {
        Assert.Equal(expected, StationChecks.Evaluate(Normal with { AcInputVoltage = volts }).MainsVoltage);
    }

    [Theory]
    [InlineData(258, "Підвищена напруга в мережі: 258,0 В")]
    [InlineData(180, "Знижена напруга в мережі: 180,0 В")]
    [InlineData(160, "Дуже низька напруга в мережі: 160,0 В")]
    public void TheTitleSaysWhichWay(double volts, string title)
    {
        Assert.Equal(title, StationChecks.Evaluate(Normal with { AcInputVoltage = volts }).Alerts[0].Title);
    }

    [Fact]
    public void NoGridIsNotAnAlarm()
    {
        // What the station reports with the plug out: about 2 V and 0 Hz.
        var health = StationChecks.Evaluate(Normal with { AcInputVoltage = 2.1, AcInputFrequency = 0 });

        Assert.Equal(Severity.Normal, health.MainsVoltage);
        Assert.Equal(Severity.Normal, health.MainsFrequency);
        Assert.Empty(health.Alerts);
    }

    [Fact]
    public void ANormalStationHasNoAlerts()
    {
        Assert.Empty(StationChecks.Evaluate(Normal).Alerts);
    }

    [Fact]
    public void AnOffOutputIsNotJudged()
    {
        var health = StationChecks.Evaluate(Normal with { AcOutputOn = false, AcOutputVoltage = 0, AcOutputFrequency = 0 });

        Assert.Equal(Severity.Normal, health.OutputVoltage);
        Assert.Empty(health.Alerts);
    }

    [Fact]
    public void GridPassedThroughToTheOutputIsFlaggedOnTheOutputToo()
    {
        // 16:14 that day: charging at 1100 W from a 256 V grid, and 256 V on the AC output.
        var health = StationChecks.Evaluate(Normal with { AcInputVoltage = 256, AcInputWatts = 1100, AcOutputVoltage = 256.2 });

        Assert.Equal(Severity.Warning, health.MainsVoltage);
        Assert.Equal(Severity.Warning, health.OutputVoltage);
        var output = Assert.Single(health.Alerts, a => a.Kind == AlertKind.OutputVoltage);
        Assert.Equal("Підвищена напруга на виході AC: 256,2 В", output.Title);
        Assert.Contains("пропускає напругу мережі", output.Detail);
        Assert.Equal("Норма 190–253 В.", health.Alerts.Single(a => a.Kind == AlertKind.MainsVoltage).Detail);
    }

    [Fact]
    public void MainsFrequencyIsJudged()
    {
        var health = StationChecks.Evaluate(Normal with { AcInputFrequency = 51.2 });

        Assert.Equal(Severity.Warning, health.MainsFrequency);
        Assert.Equal("Частота мережі поза нормою: 51,2 Гц", Assert.Single(health.Alerts).Title);
    }

    [Fact]
    public void ALevelHoldsNearTheLimitInsteadOfFlickering()
    {
        var mains = StationLimits.MainsVoltage;

        Assert.Equal(Severity.Warning, mains.Classify(252, Severity.Warning));
        Assert.Equal(Severity.Normal, mains.Classify(250.9, Severity.Warning));
        Assert.Equal(Severity.Normal, mains.Classify(252, Severity.Normal));
        Assert.Equal(Severity.Critical, mains.Classify(259, Severity.Critical));
        Assert.Equal(Severity.Warning, mains.Classify(257, Severity.Critical));
        Assert.Equal(Severity.Warning, mains.Classify(191, Severity.Warning));
        Assert.Equal(Severity.Normal, mains.Classify(192.1, Severity.Warning));
        Assert.Equal(Severity.Normal, mains.Classify(2, Severity.Critical));
    }

    [Fact]
    public void HysteresisCarriesOverBetweenReports()
    {
        var first = StationChecks.Evaluate(Normal with { AcInputVoltage = 254 });
        var second = StationChecks.Evaluate(Normal with { AcInputVoltage = 252 }, first);
        var third = StationChecks.Evaluate(Normal with { AcInputVoltage = 250 }, second);

        Assert.Equal(Severity.Warning, second.MainsVoltage);
        Assert.Equal(Severity.Normal, third.MainsVoltage);
    }

    [Fact]
    public void LowChargeOnBatteryIsReported()
    {
        var health = StationChecks.Evaluate(Normal with { SocPercent = 8, MinutesRemaining = 40, AcInputVoltage = 2.1, AcInputFrequency = 0 });

        var alert = Assert.Single(health.Alerts);
        Assert.Equal(Severity.Critical, alert.Severity);
        Assert.Equal("Батарея майже розряджена: 8 %", alert.Title);
        Assert.Equal("Залишилось ≈ 40 хв. Мережі немає.", alert.Detail);
    }

    [Fact]
    public void LowChargeWhileChargingIsFine()
    {
        Assert.Empty(StationChecks.Evaluate(Normal with { SocPercent = 8, Activity = BatteryActivity.Charging }).Alerts);
    }

    [Fact]
    public void AnEmptyingCellIsNamedButItsSpreadIsNotWhenTheBatteryIsNearlyEmpty()
    {
        // 11:26 that day: 2 %, one cell at 2.851 V and a spread of 321 mV, which is normal so close to empty.
        var health = StationChecks.Evaluate(Normal with
        {
            SocPercent = 2,
            Battery = Battery([3.172, 3.15, 3.14, 3.13, 3.12, 3.11, 2.851, 3.1, 3.09, 3.08]),
        });

        Assert.Equal(Severity.Warning, health.Cells[6]);
        Assert.Equal(Severity.Normal, health.Cells[0]);
        Assert.Equal("Комірка 7 розряджена: 2,851 В", Assert.Single(health.Alerts, a => a.Kind == AlertKind.CellVoltage).Title);
        Assert.DoesNotContain(health.Alerts, a => a.Kind == AlertKind.CellBalance);
    }

    [Fact]
    public void AWideSpreadInTheMiddleOfTheChargeIsReported()
    {
        var health = StationChecks.Evaluate(Normal with { SocPercent = 50, Battery = Battery([3.30, 3.30, 3.30, 3.30, 3.30, 3.30, 3.30, 3.30, 3.30, 3.18]) });

        Assert.Equal(Severity.Warning, health.CellBalance);
        Assert.Equal("Великий розкид напруги комірок: 120 мВ", Assert.Single(health.Alerts).Title);
    }

    [Theory]
    [InlineData(47, Severity.Warning, "Батарея нагрілась: 47 °C")]
    [InlineData(56, Severity.Critical, "Батарея нагрілась: 56 °C")]
    [InlineData(-2, Severity.Warning, "Батарея холодна: -2 °C")]
    public void BatteryTemperatureIsJudged(int temperature, Severity expected, string title)
    {
        var health = StationChecks.Evaluate(Normal with { Battery = Battery([3.3, 3.3], temperature, 25) });

        Assert.Equal(expected, health.Temperature);
        Assert.Equal(title, Assert.Single(health.Alerts).Title);
    }

    [Fact]
    public void TheWorstAlertComesFirst()
    {
        var health = StationChecks.Evaluate(Overvoltage with { SocPercent = 15, AcInputFrequency = 51 });

        Assert.Equal(Severity.Critical, health.Alerts[0].Severity);
        Assert.Equal(Severity.Critical, health.Worst);
        Assert.Equal(3, health.Alerts.Count);
    }

    [Fact]
    public void ACriticalAlertIsAnnouncedOnceAndNotAgainSoon()
    {
        var announcer = new AlertAnnouncer(TimeSpan.FromMinutes(15));
        var start = new DateTime(2026, 11, 7, 16, 15, 0);
        var critical = StationChecks.Evaluate(Overvoltage).Alerts;

        Assert.Single(announcer.Update(critical, start));
        Assert.Empty(announcer.Update(critical, start.AddSeconds(2)));

        // The voltage drops back for a moment and rises again: still too soon to repeat.
        Assert.Empty(announcer.Update([], start.AddMinutes(1)));
        Assert.Empty(announcer.Update(critical, start.AddMinutes(2)));

        Assert.Empty(announcer.Update([], start.AddMinutes(20)));
        Assert.Single(announcer.Update(critical, start.AddMinutes(21)));
    }

    [Fact]
    public void WarningsAreNotAnnouncedButTheirEscalationIs()
    {
        var announcer = new AlertAnnouncer(TimeSpan.FromMinutes(15));
        var start = new DateTime(2026, 11, 7, 16, 13, 0);
        var warning = StationChecks.Evaluate(Normal with { AcInputVoltage = 258 }).Alerts;

        Assert.Empty(announcer.Update(warning, start));
        var announced = Assert.Single(announcer.Update(StationChecks.Evaluate(Overvoltage).Alerts, start.AddSeconds(30)));
        Assert.Equal(AlertKind.MainsVoltage, announced.Kind);
    }

    [Fact]
    public void ScaledLimitsJudgeTheSameValueInOtherUnits()
    {
        var millivolts = StationLimits.CellVoltage.Scaled(1000);

        Assert.Equal(StationLimits.CellVoltage.Classify(2.851), millivolts.Classify(2851));
        Assert.Equal(2900, millivolts.WarnLow, 6);
    }

    private static BatteryDetails Battery(double[] cells, params int[] temperatures) => new()
    {
        CellVoltages = cells,
        Temperatures = temperatures.Length > 0 ? temperatures : [25, 26],
        InverterFirmware = [],
        BatteryFirmware = string.Empty,
    };
}
