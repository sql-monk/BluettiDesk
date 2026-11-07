using System.Globalization;
using System.Text;

namespace BluettiDesk.Core.Station;

/// <summary>
/// Register map of the AC180P. Addresses come from bluetti-bt-lib and VoltKeeper; the scales were checked
/// against a sweep of a real unit. README.md lists which values are confirmed and which are inferred.
/// </summary>
public static class Ac180p
{
    public const ushort AcOutputSwitch = 2011;
    public const ushort DcOutputSwitch = 2012;
    public const ushort ChargingModeRegister = 2020;
    public const ushort PowerLiftingSwitch = 2021;

    private const int TemperatureOffset = 40;
    private const int MaxCells = 32;
    private const int MaxTemperatureSensors = 8;

    /// <summary>Read every poll. The station rejects a read that leaves one of its register windows.</summary>
    public static readonly RegisterRange[] LiveRanges =
    [
        new(100, 50), new(1210, 5), new(1300, 16), new(1430, 3), new(1500, 1), new(2011, 11),
    ];

    /// <summary>
    /// Everything the unit answers to, found by sweeping addresses 0..19999 (710 registers), at most 50 per request.
    /// Read less often than <see cref="LiveRanges"/>, and it feeds the journal.
    /// </summary>
    public static readonly RegisterRange[] AllRanges =
    [
        new(0, 20), new(100, 50), new(150, 10), new(720, 40), new(1100, 50), new(1200, 50), new(1250, 40),
        new(1300, 30), new(1400, 40), new(1500, 30), new(2000, 50), new(2050, 10), new(2200, 40), new(3000, 20),
        new(6000, 30), new(6100, 50), new(6150, 50), new(6300, 50), new(11000, 50),
    ];

    public static StationStatus Decode(RegisterImage r)
    {
        var activity = r[103] switch
        {
            1 => BatteryActivity.Charging,
            2 => BatteryActivity.Discharging,
            3 => BatteryActivity.Floating,
            _ => BatteryActivity.Idle,
        };
        var mode = (ChargingMode)r[ChargingModeRegister];

        return new StationStatus
        {
            Model = SwappedText(r, 110, 6),
            SerialNumber = Serial(r, 116),
            SocPercent = r[102],
            Activity = activity,

            // Minutes, not the 0.1 h of other models: 37 % of 1440 Wh at 91 W lasts about 350 min, and the unit reported 310-330.
            MinutesRemaining = activity == BatteryActivity.Charging ? r[104] : r[105],

            // 10 cells at 3.26 V add up to raw 3262, so the scale is 0.01 V rather than the 0.1 V of other models.
            BatteryVoltage = r[100] * 0.01,
            BatteryCurrent = r[101] * 0.1,
            AcInputWatts = Math.Max(0, Int32LowWordFirst(r, 146)),
            DcInputWatts = Math.Max(0, Int32LowWordFirst(r, 144)),
            AcOutputWatts = Math.Max(0, Int32LowWordFirst(r, 142)),
            DcOutputWatts = Math.Max(0, Int32LowWordFirst(r, 140)),
            AcInputVoltage = r[1314] * 0.1,
            AcInputCurrent = r[1315] * 0.1,
            AcInputFrequency = r[1300] * 0.1,
            DcInputVoltage = r[1213] * 0.1,
            DcInputCurrent = r[1214] * 0.1,
            AcOutputVoltage = r[1431] * 0.1,
            AcOutputCurrent = r[1432] * 0.1,
            AcOutputFrequency = r[1500] * 0.1,
            AcOutputOn = r[AcOutputSwitch] == 1,
            DcOutputOn = r[DcOutputSwitch] == 1,
            PowerLiftingOn = r[PowerLiftingSwitch] == 1,
            ChargingMode = Enum.IsDefined(mode) ? mode : null,
            Battery = DecodeBattery(r),
        };
    }

    private static BatteryDetails? DecodeBattery(RegisterImage r)
    {
        int cellCount = r[6300];
        if (cellCount is 0 or > MaxCells)
        {
            return null;
        }

        var cells = new double[cellCount];
        for (var i = 0; i < cellCount; i++)
        {
            cells[i] = r[6302 + i] / 1000.0;
        }

        var temperatures = new int[Math.Min((int)r[6301], MaxTemperatureSensors)];
        for (var i = 0; i < temperatures.Length; i++)
        {
            temperatures[i] = (r[6302 + cellCount + i] & 0xFF) - TemperatureOffset;
        }

        return new BatteryDetails
        {
            CellVoltages = cells,
            Temperatures = temperatures,
            HealthPercent = r[6006],
            CycleCount = r[6008],
            InverterFirmware = [Version(r[1114], r[1115]), Version(r[1117], r[1118])],
            BatteryFirmware = Version(r[6175], r[6176]),
        };
    }

    private static string Version(ushort low, ushort high) =>
        ((low + ((long)high << 16)) / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    private static int Int32LowWordFirst(RegisterImage r, int address) => r[address] | (r[address + 1] << 16);

    private static string Serial(RegisterImage r, int address)
    {
        var value = r[address] + ((long)r[address + 1] << 16) + ((long)r[address + 2] << 32) + ((long)r[address + 3] << 48);
        return value == 0 ? string.Empty : value.ToString(CultureInfo.InvariantCulture);
    }

    private static string SwappedText(RegisterImage r, int address, int registers)
    {
        var text = new StringBuilder(registers * 2);
        for (var i = 0; i < registers; i++)
        {
            var word = r[address + i];
            text.Append((char)(word & 0xFF)).Append((char)(word >> 8));
        }

        return text.ToString().TrimEnd('\0');
    }
}

public readonly record struct RegisterRange(ushort Address, ushort Quantity);

/// <summary>Latest known value of every register read so far. Registers never read are zero.</summary>
public sealed class RegisterImage
{
    private readonly Dictionary<int, ushort> _values = [];

    public ushort this[int address] => _values.GetValueOrDefault(address);

    public IEnumerable<KeyValuePair<int, ushort>> Values => _values;

    public void Store(ushort address, IReadOnlyList<ushort> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            _values[address + i] = values[i];
        }
    }

    public RegisterImage Clone()
    {
        var copy = new RegisterImage();
        foreach (var (address, value) in _values)
        {
            copy._values[address] = value;
        }

        return copy;
    }
}

public enum BatteryActivity
{
    Idle,
    Charging,
    Discharging,
    Floating,
}

/// <summary>Values as defined by bluetti-bt-lib for the AC180P.</summary>
public enum ChargingMode : ushort
{
    Standard = 0,
    Silent = 1,
    Turbo = 2,
}

public sealed record StationStatus
{
    public string Model { get; init; } = string.Empty;
    public string SerialNumber { get; init; } = string.Empty;
    public int SocPercent { get; init; }
    public BatteryActivity Activity { get; init; }
    public int MinutesRemaining { get; init; }
    public double BatteryVoltage { get; init; }
    public double BatteryCurrent { get; init; }
    public int AcInputWatts { get; init; }
    public int DcInputWatts { get; init; }
    public int AcOutputWatts { get; init; }
    public int DcOutputWatts { get; init; }
    public double AcInputVoltage { get; init; }
    public double AcInputCurrent { get; init; }
    public double AcInputFrequency { get; init; }
    public double DcInputVoltage { get; init; }
    public double DcInputCurrent { get; init; }
    public double AcOutputVoltage { get; init; }
    public double AcOutputCurrent { get; init; }
    public double AcOutputFrequency { get; init; }
    public bool AcOutputOn { get; init; }
    public bool DcOutputOn { get; init; }
    public bool PowerLiftingOn { get; init; }
    public ChargingMode? ChargingMode { get; init; }
    public BatteryDetails? Battery { get; init; }
}

public sealed record BatteryDetails
{
    public required IReadOnlyList<double> CellVoltages { get; init; }
    public required IReadOnlyList<int> Temperatures { get; init; }

    /// <summary>Inferred, not documented: register 6006, right after the state of charge.</summary>
    public int HealthPercent { get; init; }

    /// <summary>Inferred, not documented: register 6008.</summary>
    public int CycleCount { get; init; }

    public required IReadOnlyList<string> InverterFirmware { get; init; }
    public required string BatteryFirmware { get; init; }
}
