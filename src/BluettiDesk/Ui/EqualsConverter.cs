using System.Globalization;
using System.Windows.Data;

namespace BluettiDesk.Ui;

/// <summary>Binds one radio button of a group to an enum value.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value, parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}
