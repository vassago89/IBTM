using System;
using System.Globalization;
using System.Windows.Data;
using IBTM.Core;

namespace IBTM.UI;

public sealed class EnumDescriptionConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is Enum enumValue
            ? enumValue.GetDescription()
            : value;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
