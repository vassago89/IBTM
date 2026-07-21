using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace IBTM.Presentation.Converters;

public abstract class OneWayValueConverter : IValueConverter
{
    public abstract object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture);

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) => Binding.DoNothing;
}

[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToVisibilityConverter : OneWayValueConverter
{
    public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;
}

[ValueConversion(typeof(bool), typeof(double))]
public class BoolToOpacityConverter : OneWayValueConverter
{
    public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.28;
}

[ValueConversion(typeof(object), typeof(Visibility))]
public class EnumMatchToVisibilityConverter : OneWayValueConverter
{
    public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString()
            ? Visibility.Visible
            : Visibility.Collapsed;
}

public class HalfConverter : OneWayValueConverter
{
    public static HalfConverter Instance { get; } = new();

    public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double number ? number / 2.0 : 0.0;
}

[ValueConversion(typeof(bool), typeof(Brush))]
public class NgSlotFillConverter : OneWayValueConverter
{
    private static readonly Brush Filled = CreateBrush(0xFF, 0x6B, 0x6B);
    private static readonly Brush Empty = CreateBrush(0x3A, 0x3A, 0x3A);

    public override object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Filled : Empty;

    private static Brush CreateBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
