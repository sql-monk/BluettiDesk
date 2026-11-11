namespace BluettiDesk.Core.Station;

/// <summary>
/// How long the AC180P runs on its battery. Fitted to its journal: twelve stretches on battery at 40–130 W of load,
/// from 100 % down to as low as 2 %. Each percent gave about 13.5 Wh from the battery, and the battery delivered about
/// 30 W more than the outputs drew, which is what the inverter itself takes. The station's own "minutes left" agrees with
/// this within a few percent.
/// </summary>
public static class BatteryRuntime
{
    public const double WattHoursPerPercent = 13.5;
    public const double InverterWatts = 30;

    /// <summary>With the AC output off the inverter is off as well. Not measured: the station has always run with AC on.</summary>
    public const double IdleWatts = 10;

    /// <summary>Minutes until the charge runs out at <paramref name="loadWatts"/> on the outputs.</summary>
    public static int Minutes(int socPercent, double loadWatts, bool acOutputOn)
    {
        var draw = Math.Max(0, loadWatts) + (acOutputOn ? InverterWatts : IdleWatts);
        return (int)Math.Floor(Math.Clamp(socPercent, 0, 100) * WattHoursPerPercent / draw * 60);
    }
}
