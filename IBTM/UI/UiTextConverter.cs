using System;
using System.Globalization;
using System.Windows.Data;
using IBTM.Core;

namespace IBTM.UI;

public sealed class UiTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            Enum enumValue => UiText.Get(enumValue),
            string text => UiText.Get(text),
            _ => value,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
