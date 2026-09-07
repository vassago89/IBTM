using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using IBTM.Device;

namespace IBTM.UI;

public sealed class TeachingOutputConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values[0] is OutputIo signal
        && values[1] is IReadOnlyDictionary<OutputIo, TeachingOutput> outputs
        && outputs.TryGetValue(signal, out var output) ? output : null;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
