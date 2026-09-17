using System.Windows;

namespace IBTM.UI;

public static class DialogResultBinding
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(bool?), typeof(DialogResultBinding), new PropertyMetadata(null, OnValueChanged));

    public static bool? GetValue(DependencyObject target)
    {
        return (bool?)target.GetValue(ValueProperty);
    }

    public static void SetValue(DependencyObject target, bool? value)
    {
        target.SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is Window window && e.NewValue is bool result)
            window.DialogResult = result;
    }
}
