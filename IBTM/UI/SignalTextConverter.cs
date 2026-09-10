using System;
using System.Globalization;
using System.Windows.Data;

namespace IBTM.UI;

public sealed class SignalTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool on ? on ? "ON" : "OFF" : "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
