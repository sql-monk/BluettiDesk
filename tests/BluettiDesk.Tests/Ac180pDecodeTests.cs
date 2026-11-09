using BluettiDesk.Core.Station;

namespace BluettiDesk.Tests;

public class Ac180pDecodeTests
{
    /// <summary>Values captured from a real AC180P on battery with a 65 W AC load (serial replaced).</summary>
    private static RegisterImage CapturedImage()
    {
        var image = new RegisterImage();
        void Set(ushort address, params ushort[] values) => image.Store(address, values);

        Set(100, 3262, 28, 37, 2, 310, 305);
        Set(110, 0x4341, 0x3831, 0x5030);
        Set(116, 0x0001, 0x0002);
        Set(142, 71);
        Set(1114, 15896, 3);
        Set(1117, 15995, 3);
        Set(1213, 1);
        Set(1314, 22);
        Set(1431, 2319, 4);
        Set(1500, 499);
        Set(2011, 1, 1);
        Set(6006, 94);
        Set(6008, 34);
        Set(6175, 39269, 1);
        Set(6300, 10, 2, 3266, 3266, 3267, 3268, 3268, 3259, 3259, 3265, 3263, 3257, 65, 67);
        return image;
    }

    [Fact]
    public void DecodesBatteryState()
    {
        var status = Ac180p.Decode(CapturedImage());

        Assert.Equal("AC180P", status.Model);
        Assert.Equal(37, status.SocPercent);
        Assert.Equal(BatteryActivity.Discharging, status.Activity);
        Assert.Equal(32.62, status.BatteryVoltage, 2);
        Assert.Equal(2.8, status.BatteryCurrent, 2);
    }

    [Fact]
    public void RemainingTimeFollowsTheDirectionOfCurrent()
    {
        var image = CapturedImage();
        Assert.Equal(305, Ac180p.Decode(image).MinutesRemaining);

        image.Store(103, new ushort[] { 1 });
        Assert.Equal(310, Ac180p.Decode(image).MinutesRemaining);
    }

    [Fact]
    public void BatteryVoltageAgreesWithTheCells()
    {
        var status = Ac180p.Decode(CapturedImage());

        Assert.Equal(status.BatteryVoltage, status.Battery!.CellVoltages.Sum(), 0);
    }

    [Fact]
    public void DecodesPowerAndOutputs()
    {
        var status = Ac180p.Decode(CapturedImage());

        Assert.Equal(71, status.AcOutputWatts);
        Assert.Equal(0, status.DcOutputWatts);
        Assert.Equal(0, status.AcInputWatts);
        Assert.Equal(231.9, status.AcOutputVoltage, 1);
        Assert.Equal(0.4, status.AcOutputCurrent, 1);
        Assert.Equal(49.9, status.AcOutputFrequency, 1);
        Assert.True(status.AcOutputOn);
        Assert.True(status.DcOutputOn);
        Assert.False(status.PowerLiftingOn);
        Assert.Equal(ChargingMode.Standard, status.ChargingMode);
    }

    [Fact]
    public void DecodesBatteryDetails()
    {
        var battery = Ac180p.Decode(CapturedImage()).Battery!;

        Assert.Equal(10, battery.CellVoltages.Count);
        Assert.Equal(3.266, battery.CellVoltages[0], 3);
        Assert.Equal(new[] { 25, 27 }, battery.Temperatures);
        Assert.Equal(94, battery.HealthPercent);
        Assert.Equal(34, battery.CycleCount);
        Assert.Equal(new[] { "2125.04", "2126.03" }, battery.InverterFirmware);
        Assert.Equal("1048.05", battery.BatteryFirmware);
    }

    [Fact]
    public void TheFullSweepCoversEveryRegisterTheUnitAnswersTo()
    {
        var covered = Ac180p.AllRanges.SelectMany(r => Enumerable.Range(r.Address, r.Quantity)).ToList();

        Assert.Equal(710, covered.Count);
        Assert.Equal(covered.Count, covered.Distinct().Count());
    }

    [Fact]
    public void EveryLoggedFieldReadsARegisterTheSweepCovers()
    {
        var covered = new RegisterImage();
        foreach (var range in Ac180p.AllRanges)
        {
            covered.Store(range.Address, Enumerable.Repeat((ushort)1, range.Quantity).ToArray());
        }

        var unreadable = Ac180pLog.Fields
            .Where(f => f.Column != "cell_spread_mv" && f.Read(covered) == f.Read(new RegisterImage()))
            .Select(f => f.Column);

        Assert.Empty(unreadable);
    }

    [Fact]
    public void LoggedFieldsDecodeCapturedValues()
    {
        var image = CapturedImage();
        double Value(string column) => Ac180pLog.Fields.Single(f => f.Column == column).Read(image);

        Assert.Equal(37, Value("soc_percent"));
        Assert.Equal(32.62, Value("battery_v"));
        Assert.Equal(2.8, Value("battery_a"));
        Assert.Equal(71, Value("ac_out_w"));
        Assert.Equal(231.9, Value("ac_out_v"));
        Assert.Equal(25, Value("temp_1_c"));
        Assert.Equal(27, Value("temp_2_c"));
        Assert.Equal(3266, Value("cell_1_mv"));
        Assert.Equal(11, Value("cell_spread_mv"));
    }

    [Fact]
    public void ColumnNamesAreSafeAndUnique()
    {
        var columns = Ac180pLog.Fields.Select(f => f.Column).ToList();

        Assert.Equal(columns.Count, columns.Distinct().Count());
        Assert.All(columns, c => Assert.Matches("^[a-z][a-z0-9_]*$", c));
    }

    [Fact]
    public void UnknownChargingModeIsNotGuessed()
    {
        var image = CapturedImage();
        image.Store(Ac180p.ChargingModeRegister, new ushort[] { 9 });

        Assert.Null(Ac180p.Decode(image).ChargingMode);
    }

    [Fact]
    public void DecodesEcoOfBothOutputs()
    {
        // As the AC180P reported on 7 November 2026, with DC ECO switched on for the test.
        var image = CapturedImage();
        image.Store(Ac180p.DcEcoSwitch, new ushort[] { 1, 4, 10, 0, 4, 25 });

        var status = Ac180p.Decode(image);

        Assert.Equal(new EcoSetting(true, EcoTimeout.FourHours, 10), status.DcEco);
        Assert.Equal(new EcoSetting(false, EcoTimeout.FourHours, 25), status.AcEco);
    }

    [Fact]
    public void UnknownEcoTimeoutIsNotGuessed()
    {
        var image = CapturedImage();
        image.Store(Ac180p.AcEcoTimeoutRegister, new ushort[] { 7 });

        Assert.Null(Ac180p.Decode(image).AcEco.Timeout);
    }

    [Fact]
    public void BatteryDetailsAreAbsentUntilRead()
    {
        Assert.Null(Ac180p.Decode(new RegisterImage()).Battery);
    }

    [Fact]
    public void EveryReadStaysInsideWhatTheStationAccepts()
    {
        // Measured on a real unit: these are the windows that answer, at most 50 registers per request.
        (int First, int Last)[] readable =
        [
            (0, 19), (100, 159), (720, 759), (1100, 1149), (1200, 1289), (1300, 1329), (1400, 1439), (1500, 1529),
            (2000, 2059), (2200, 2239), (3000, 3019), (6000, 6029), (6100, 6199), (6300, 6349), (11000, 11049),
        ];

        foreach (var range in Ac180p.LiveRanges.Concat(Ac180p.AllRanges))
        {
            Assert.Contains(readable, w => range.Address >= w.First && range.Address + range.Quantity - 1 <= w.Last);
            Assert.InRange((int)range.Quantity, 1, 50);
        }
    }
}
