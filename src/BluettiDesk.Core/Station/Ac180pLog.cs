namespace BluettiDesk.Core.Station;

/// <summary>One named value in the journal: a column of the <c>sample</c> table.</summary>
public sealed record LogField(string Column, string Title, string Unit, bool IsInteger, Func<RegisterImage, double> Read)
{
    public string DisplayName => Unit.Length == 0 ? Title : $"{Title}, {Unit}";
}

/// <summary>
/// Every register with a known meaning. Registers without one still reach the journal, through its
/// <c>register_change</c> table. Energy counters (150-159 and others) stay out: they read zero on the AC180P.
/// </summary>
public static class Ac180pLog
{
    private const int CellCount = 10;
    private const int FirstCell = 6302;
    private const int TemperatureOffset = 40;

    public static IReadOnlyList<LogField> Fields { get; } = Build();

    private static List<LogField> Build()
    {
        List<LogField> fields =
        [
            U16("soc_percent", "Заряд", "%", 102),
            U16("battery_state", "Стан батареї (0 очікування, 1 заряд, 2 розряд, 3 підтримка)", "", 103),
            U16("minutes_to_full", "Час до повного заряду", "хв", 104),
            U16("minutes_to_empty", "Час до розряду", "хв", 105),
            Scaled("battery_v", "Напруга батареї", "В", 100, 0.01),
            Scaled("battery_a", "Струм батареї", "А", 101, 0.1),
            U16("health_percent", "Здоров'я батареї", "%", 6006),
            U16("cycle_count", "Цикли заряду", "", 6008),
            Temperature("temp_1_c", "Температура батареї 1", 6312),
            Temperature("temp_2_c", "Температура батареї 2", 6313),
            new("cell_spread_mv", "Розкид напруги комірок", "мВ", true, CellSpread),

            S32("ac_in_w", "Вхід AC", "Вт", 146),
            U32("dc_in_w", "Вхід DC", "Вт", 144),
            S32("ac_out_w", "Вихід AC", "Вт", 142),
            U32("dc_out_w", "Вихід DC", "Вт", 140),
            S32("inverter_total_w", "Інвертор, сумарно", "Вт", 148),

            Scaled("ac_in_v", "Вхід AC: напруга", "В", 1314, 0.1),
            Scaled("ac_in_a", "Вхід AC: струм", "А", 1315, 0.1),
            Scaled("ac_in_hz", "Вхід AC: частота", "Гц", 1300, 0.1),
            S16("ac_in_phase_w", "Вхід AC: потужність фази", "Вт", 1313),

            Scaled("dc_in_v", "Вхід DC: напруга", "В", 1213, 0.1),
            Scaled("dc_in_a", "Вхід DC: струм", "А", 1214, 0.1),
            U16("dc_in_port_w", "Вхід DC: потужність порту", "Вт", 1212),
            LowByte("dc_in_status", "Вхід DC: код стану", 1210),
            LowByte("dc_in_type", "Вхід DC: код типу джерела", 1211),

            Scaled("ac_out_v", "Вихід AC: напруга", "В", 1431, 0.1),
            Scaled("ac_out_a", "Вихід AC: струм", "А", 1432, 0.1),
            U16("ac_out_phase_w", "Вихід AC: потужність фази", "Вт", 1430),
            U32("ac_load_total_w", "Вихід AC: навантаження, сумарно", "Вт", 1420),

            LowByte("inverter_status", "Інвертор: код стану", 1509),
            U16("inverter_w", "Інвертор: потужність", "Вт", 1510),
            Scaled("inverter_v", "Інвертор: напруга", "В", 1511, 0.1),
            Scaled("inverter_a", "Інвертор: струм", "А", 1512, 0.1),
            Scaled("inverter_hz", "Інвертор: частота", "Гц", 1500, 0.1),

            U32("dc_load_total_w", "Вихід DC: навантаження, сумарно", "Вт", 1400),
            U16("dc_5v_w", "Вихід 5 В: потужність", "Вт", 1404),
            Scaled("dc_5v_a", "Вихід 5 В: струм", "А", 1405, 0.1),
            U16("dc_12v_w", "Вихід 12 В: потужність", "Вт", 1406),
            Scaled("dc_12v_a", "Вихід 12 В: струм", "А", 1407, 0.1),
            U16("dc_24v_w", "Вихід 24 В: потужність", "Вт", 1408),
            Scaled("dc_24v_a", "Вихід 24 В: струм", "А", 1409, 0.1),
            Scaled("dc_out_v", "Вихід DC: напруга", "В", 1412, 0.1),
            Scaled("dc_out_a", "Вихід DC: струм", "А", 1413, 0.1),

            U16("ac_out_on", "Вихід AC увімкнено", "", Ac180p.AcOutputSwitch),
            U16("dc_out_on", "Вихід DC увімкнено", "", Ac180p.DcOutputSwitch),
            U16("dc_eco_on", "ECO DC увімкнено", "", 2014),
            U16("dc_eco_hours", "ECO DC: час до вимкнення", "год", 2015),
            U16("dc_eco_w", "ECO DC: поріг", "Вт", 2016),
            U16("ac_eco_on", "ECO AC увімкнено", "", 2017),
            U16("ac_eco_hours", "ECO AC: час до вимкнення", "год", 2018),
            U16("ac_eco_w", "ECO AC: поріг", "Вт", 2019),
            U16("charging_mode", "Режим заряду (0 стандарт, 1 тихий, 2 турбо)", "", Ac180p.ChargingModeRegister),
            U16("power_lifting_on", "Power Lifting увімкнено", "", Ac180p.PowerLiftingSwitch),
        ];

        for (var i = 0; i < CellCount; i++)
        {
            fields.Add(U16($"cell_{i + 1}_mv", $"Комірка {i + 1}", "мВ", FirstCell + i));
        }

        return fields;
    }

    private static double CellSpread(RegisterImage r)
    {
        int min = int.MaxValue, max = int.MinValue;
        for (var i = 0; i < CellCount; i++)
        {
            int cell = r[FirstCell + i];
            min = Math.Min(min, cell);
            max = Math.Max(max, cell);
        }

        return max - min;
    }

    private static LogField U16(string column, string title, string unit, int address) =>
        new(column, title, unit, true, r => r[address]);

    private static LogField S16(string column, string title, string unit, int address) =>
        new(column, title, unit, true, r => (short)r[address]);

    // The 32-bit values of this station keep their low word first.
    private static LogField U32(string column, string title, string unit, int address) =>
        new(column, title, unit, true, r => (uint)(r[address] | (r[address + 1] << 16)));

    private static LogField S32(string column, string title, string unit, int address) =>
        new(column, title, unit, true, r => r[address] | (r[address + 1] << 16));

    private static LogField Scaled(string column, string title, string unit, int address, double scale) =>
        new(column, title, unit, false, r => Math.Round(r[address] * scale, 3));

    private static LogField LowByte(string column, string title, int address) =>
        new(column, title, string.Empty, true, r => r[address] & 0xFF);

    private static LogField Temperature(string column, string title, int address) =>
        new(column, title, "°C", true, r => (r[address] & 0xFF) - TemperatureOffset);
}
