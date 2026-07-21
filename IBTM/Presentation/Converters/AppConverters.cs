using System;
using System.Globalization;
using System.Windows.Data;

namespace IBTM.Presentation.Converters;

public sealed class HalfConverter : IValueConverter
{
    public static HalfConverter Instance { get; } = new();

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        (double)value / 2.0;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) => Binding.DoNothing;
}

public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        (int)value == int.Parse((string)parameter, CultureInfo.InvariantCulture);

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is true
            ? int.Parse((string)parameter, CultureInfo.InvariantCulture)
            : Binding.DoNothing;
}
