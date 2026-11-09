using System.Globalization;

namespace BluettiDesk.Core.Station;

public enum Severity
{
    Normal,
    Warning,
    Critical,
}

/// <summary>
/// Where a value is normal, and the wider range outside which it is dangerous. A value below <see cref="AbsentBelow"/>
/// means the source is not there at all, like a grid that is down, and is not judged.
/// </summary>
public sealed record Limits(double WarnLow, double WarnHigh, double CriticalLow, double CriticalHigh)
{
    public double AbsentBelow { get; init; } = double.NegativeInfinity;

    /// <summary>How far back inside a range a value has to come before its level drops, so that it does not flicker at a limit.</summary>
    public double Hysteresis { get; init; }

    public Severity Classify(double value, Severity previous = Severity.Normal)
    {
        var now = Raw(value, 0);
        if (now >= previous)
        {
            return now;
        }

        var strict = Raw(value, Hysteresis);
        return strict > now ? (Severity)Math.Min((int)strict, (int)previous) : now;
    }

    /// <summary>The same limits in other units, such as millivolts instead of volts.</summary>
    public Limits Scaled(double factor) => new(WarnLow * factor, WarnHigh * factor, CriticalLow * factor, CriticalHigh * factor)
    {
        AbsentBelow = AbsentBelow * factor,
        Hysteresis = Hysteresis * factor,
    };

    public bool IsHigh(double value) => value > (WarnLow + WarnHigh) / 2;

    private Severity Raw(double value, double narrowing) =>
        value < AbsentBelow ? Severity.Normal
        : value < CriticalLow + narrowing || value > CriticalHigh - narrowing ? Severity.Critical
        : value < WarnLow + narrowing || value > WarnHigh - narrowing ? Severity.Warning
        : Severity.Normal;
}

/// <summary>
/// Thresholds of the checks. Mains follows what the station does rather than ДСТУ EN 50160 (207–253 V), which flagged
/// grids it works on: it charged at 1500 W from 189.7 V and at 258 V, and dropped the grid at 263.8 V. The owner marks low and
/// high mains at 190 and 253 V; above 253 V the station also passes the grid to its AC output. Where it gives up below 190 V has
/// not been seen; 170 V is a guess.
/// Frequency: 50 Hz ±1 % normally, 47–52 Hz at the outside. Cells and temperatures: the usual limits of LiFePO4.
/// </summary>
public static class StationLimits
{
    public const double MainsPresentVoltage = 80;
    public const int LowSoc = 20;
    public const int CriticalSoc = 10;

    /// <summary>Below this charge the cells drift apart on their own, so a wide spread says nothing there.</summary>
    public const int BalanceCheckMinSoc = 15;

    public const double MaxCellSpreadVolts = 0.1;

    public static Limits MainsVoltage { get; } = new(190, 253, 170, 260) { AbsentBelow = MainsPresentVoltage, Hysteresis = 2 };

    public static Limits MainsFrequency { get; } = new(49.5, 50.5, 47, 52) { AbsentBelow = 40, Hysteresis = 0.1 };

    public static Limits CellVoltage { get; } = new(2.9, 3.65, 2.7, 3.75) { Hysteresis = 0.02 };

    public static Limits Temperature { get; } = new(0, 45, -20, 55) { Hysteresis = 1 };
}

public enum AlertKind
{
    MainsVoltage,
    MainsFrequency,
    OutputVoltage,
    OutputFrequency,
    BatteryLow,
    CellVoltage,
    CellBalance,
    Temperature,
}

public sealed record StationAlert(AlertKind Kind, Severity Severity, string Title, string Detail);

/// <summary>The level of every checked value, and what is wrong in words.</summary>
public sealed record StationHealth
{
    public static StationHealth Unknown { get; } = new();

    public Severity MainsVoltage { get; init; }
    public Severity MainsFrequency { get; init; }
    public Severity OutputVoltage { get; init; }
    public Severity OutputFrequency { get; init; }
    public Severity Charge { get; init; }
    public Severity Temperature { get; init; }
    public Severity CellBalance { get; init; }
    public IReadOnlyList<Severity> Cells { get; init; } = [];
    public IReadOnlyList<StationAlert> Alerts { get; init; } = [];

    public Severity Worst => Alerts.Count == 0 ? Severity.Normal : Alerts.Max(a => a.Severity);
}

/// <summary>
/// Judges the measured values against <see cref="StationLimits"/>. The station has its own alarm flags, but their meaning
/// is not documented, so the checks rely on what is measured.
/// </summary>
public static class StationChecks
{
    private static readonly CultureInfo Ukrainian = CultureInfo.GetCultureInfo("uk-UA");

    public static StationHealth Evaluate(StationStatus s, StationHealth? previous = null)
    {
        previous ??= StationHealth.Unknown;
        var alerts = new List<StationAlert>();
        var mainsPresent = s.AcInputVoltage >= StationLimits.MainsPresentVoltage;
        var outputLive = s.AcOutputOn && s.AcOutputVoltage >= StationLimits.MainsPresentVoltage;

        var mainsVoltage = StationLimits.MainsVoltage.Classify(s.AcInputVoltage, previous.MainsVoltage);
        var mainsFrequency = mainsPresent ? StationLimits.MainsFrequency.Classify(s.AcInputFrequency, previous.MainsFrequency) : Severity.Normal;
        var outputVoltage = outputLive ? StationLimits.MainsVoltage.Classify(s.AcOutputVoltage, previous.OutputVoltage) : Severity.Normal;
        var outputFrequency = outputLive ? StationLimits.MainsFrequency.Classify(s.AcOutputFrequency, previous.OutputFrequency) : Severity.Normal;

        if (mainsVoltage != Severity.Normal)
        {
            alerts.Add(MainsVoltageAlert(s, mainsVoltage, outputLive && outputVoltage == Severity.Normal));
        }

        if (mainsFrequency != Severity.Normal)
        {
            alerts.Add(new StationAlert(
                AlertKind.MainsFrequency,
                mainsFrequency,
                $"Частота мережі поза нормою: {Hertz(s.AcInputFrequency)}",
                $"Норма {Range(StationLimits.MainsFrequency, "0.0")} Гц."));
        }

        if (outputVoltage != Severity.Normal)
        {
            var passedThrough = mainsPresent && s.AcInputWatts > 0 && Math.Abs(s.AcInputVoltage - s.AcOutputVoltage) < 5;
            alerts.Add(new StationAlert(
                AlertKind.OutputVoltage,
                outputVoltage,
                $"{(StationLimits.MainsVoltage.IsHigh(s.AcOutputVoltage) ? "Підвищена" : "Знижена")} напруга на виході AC: {Volts(s.AcOutputVoltage)}",
                $"Норма {Range(StationLimits.MainsVoltage, "0")} В." + (passedThrough ? " Схоже, станція пропускає напругу мережі на вихід." : string.Empty)));
        }

        if (outputFrequency != Severity.Normal)
        {
            alerts.Add(new StationAlert(
                AlertKind.OutputFrequency,
                outputFrequency,
                $"Частота на виході AC поза нормою: {Hertz(s.AcOutputFrequency)}",
                $"Норма {Range(StationLimits.MainsFrequency, "0.0")} Гц."));
        }

        var charge = s.Activity == BatteryActivity.Charging ? Severity.Normal : s.SocPercent switch
        {
            <= StationLimits.CriticalSoc => Severity.Critical,
            <= StationLimits.LowSoc => Severity.Warning,
            _ => Severity.Normal,
        };
        if (charge != Severity.Normal)
        {
            alerts.Add(ChargeAlert(s, charge, mainsPresent));
        }

        var cells = new List<Severity>();
        var temperature = Severity.Normal;
        var balance = Severity.Normal;
        if (s.Battery is { } battery)
        {
            for (var i = 0; i < battery.CellVoltages.Count; i++)
            {
                var before = i < previous.Cells.Count ? previous.Cells[i] : Severity.Normal;
                cells.Add(StationLimits.CellVoltage.Classify(battery.CellVoltages[i], before));
            }

            if (CellAlert(battery, cells) is { } cellAlert)
            {
                alerts.Add(cellAlert);
            }

            var spread = battery.CellVoltages.Count > 1 ? battery.CellVoltages.Max() - battery.CellVoltages.Min() : 0;
            if (s.SocPercent >= StationLimits.BalanceCheckMinSoc && spread > StationLimits.MaxCellSpreadVolts)
            {
                balance = Severity.Warning;
                alerts.Add(new StationAlert(
                    AlertKind.CellBalance,
                    balance,
                    $"Великий розкид напруги комірок: {Math.Round(spread * 1000)} мВ",
                    $"Зазвичай він не більший за {StationLimits.MaxCellSpreadVolts * 1000:0} мВ. Можливо, батарея розбалансована."));
            }

            if (battery.Temperatures.Count > 0)
            {
                var hottest = battery.Temperatures.Max();
                var coldest = battery.Temperatures.Min();
                var hot = StationLimits.Temperature.Classify(hottest, previous.Temperature);
                var cold = StationLimits.Temperature.Classify(coldest, previous.Temperature);
                temperature = (Severity)Math.Max((int)hot, (int)cold);
                if (hot != Severity.Normal && StationLimits.Temperature.IsHigh(hottest))
                {
                    alerts.Add(new StationAlert(
                        AlertKind.Temperature,
                        hot,
                        $"Батарея нагрілась: {hottest} °C",
                        $"Норма до {StationLimits.Temperature.WarnHigh:0} °C. Зменште навантаження або дайте станції охолонути."));
                }
                else if (cold != Severity.Normal)
                {
                    alerts.Add(new StationAlert(
                        AlertKind.Temperature,
                        cold,
                        $"Батарея холодна: {coldest} °C",
                        "Нижче 0 °C батареї LiFePO4 не можна заряджати."));
                }
            }
        }

        return new StationHealth
        {
            MainsVoltage = mainsVoltage,
            MainsFrequency = mainsFrequency,
            OutputVoltage = outputVoltage,
            OutputFrequency = outputFrequency,
            Charge = charge,
            Temperature = temperature,
            CellBalance = balance,
            Cells = cells,
            Alerts = alerts.OrderByDescending(a => a.Severity).ToList(),
        };
    }

    private static StationAlert MainsVoltageAlert(StationStatus s, Severity severity, bool outputNormal)
    {
        var high = StationLimits.MainsVoltage.IsHigh(s.AcInputVoltage);
        var title = (high, severity) switch
        {
            (true, Severity.Critical) => "Перенапруга в мережі",
            (true, _) => "Підвищена напруга в мережі",
            (false, Severity.Critical) => "Дуже низька напруга в мережі",
            _ => "Знижена напруга в мережі",
        };

        var detail = $"Норма {Range(StationLimits.MainsVoltage, "0")} В.";
        if (s.AcInputWatts == 0)
        {
            detail += outputNormal
                ? $" Станція не бере енергію з мережі, вихід AC живиться від батареї ({Volts(s.AcOutputVoltage)})."
                : " Станція не бере енергію з мережі.";
        }

        return new StationAlert(AlertKind.MainsVoltage, severity, $"{title}: {Volts(s.AcInputVoltage)}", detail);
    }

    private static StationAlert ChargeAlert(StationStatus s, Severity severity, bool mainsPresent)
    {
        var detail = s.MinutesRemaining > 0 ? $"Залишилось ≈ {Duration(s.MinutesRemaining)}." : string.Empty;
        if (!mainsPresent)
        {
            detail = (detail + " Мережі немає.").Trim();
        }

        var title = severity == Severity.Critical ? "Батарея майже розряджена" : "Низький заряд";
        return new StationAlert(AlertKind.BatteryLow, severity, $"{title}: {s.SocPercent} %", detail);
    }

    /// <summary>One alert for the cell furthest out of its range.</summary>
    private static StationAlert? CellAlert(BatteryDetails battery, List<Severity> cells)
    {
        var worst = Enumerable.Range(0, cells.Count)
            .Where(i => cells[i] != Severity.Normal)
            .OrderByDescending(i => cells[i])
            .ThenByDescending(i => Math.Abs(battery.CellVoltages[i] - 3.3))
            .Select(i => (int?)i)
            .FirstOrDefault();
        if (worst is not { } index)
        {
            return null;
        }

        var volts = battery.CellVoltages[index];
        var high = StationLimits.CellVoltage.IsHigh(volts);
        return new StationAlert(
            AlertKind.CellVoltage,
            cells[index],
            $"Комірка {index + 1} {(high ? "перезаряджена" : "розряджена")}: {volts.ToString("0.000", Ukrainian)} В",
            $"Норма {Range(StationLimits.CellVoltage, "0.00")} В.");
    }

    public static string Range(Limits limits, string format) =>
        $"{limits.WarnLow.ToString(format, Ukrainian)}–{limits.WarnHigh.ToString(format, Ukrainian)}";

    private static string Volts(double value) => $"{value.ToString("0.0", Ukrainian)} В";

    private static string Hertz(double value) => $"{value.ToString("0.0", Ukrainian)} Гц";

    private static string Duration(int minutes)
    {
        var hours = minutes / 60;
        var rest = minutes % 60;
        return hours == 0 ? $"{rest} хв" : rest == 0 ? $"{hours} год" : $"{hours} год {rest} хв";
    }
}

/// <summary>
/// Picks the alerts worth a notification: a critical one when it starts, but not again for the same kind within
/// the quiet period, so that a voltage hovering at a limit does not flood the screen.
/// </summary>
public sealed class AlertAnnouncer(TimeSpan quiet)
{
    private readonly HashSet<AlertKind> _active = [];
    private readonly Dictionary<AlertKind, DateTime> _lastAnnounced = [];

    public IReadOnlyList<StationAlert> Update(IReadOnlyList<StationAlert> alerts, DateTime now)
    {
        var critical = alerts.Where(a => a.Severity == Severity.Critical).ToList();
        var announce = critical
            .Where(a => !_active.Contains(a.Kind) && (!_lastAnnounced.TryGetValue(a.Kind, out var last) || now - last >= quiet))
            .ToList();

        _active.Clear();
        _active.UnionWith(critical.Select(a => a.Kind));
        foreach (var alert in announce)
        {
            _lastAnnounced[alert.Kind] = now;
        }

        return announce;
    }
}
