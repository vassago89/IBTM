using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IBTM.UI;

public sealed class MillisecondsToSecondsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is int milliseconds
            ? (milliseconds / 1000m).ToString("0.###", culture)
            : DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text
            || !decimal.TryParse(text, NumberStyles.Float, culture, out var seconds)
            || seconds < 0 || seconds > int.MaxValue / 1000m)
            return DependencyProperty.UnsetValue;

        // Keep the stored integer milliseconds exact; do not silently round a setting.
        var milliseconds = seconds * 1000m;
        if (milliseconds != decimal.Truncate(milliseconds))
            return DependencyProperty.UnsetValue;
        return (int)milliseconds;
    }
}
