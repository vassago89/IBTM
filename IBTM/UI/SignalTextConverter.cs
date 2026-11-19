using System;
using System.Globalization;
using System.Windows.Data;

namespace IBTM.UI;

public sealed class SignalTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            true => "ON",
            false => "OFF",
            _ => "—",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
