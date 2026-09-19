using System.Windows;
using System.Windows.Controls;

namespace IBTM.UI;

// WPF selection/capture stays in the control; text and pause state are bindable.
public sealed class LogTextBox : TextBox
{
    public static readonly DependencyProperty SelectionTextProperty;

    public static readonly DependencyProperty IsPausedProperty;

    static LogTextBox()
    {
        SelectionTextProperty = DependencyProperty.Register(
            nameof(SelectionText), typeof(string), typeof(LogTextBox),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
        IsPausedProperty = DependencyProperty.Register(
            nameof(IsPaused), typeof(bool), typeof(LogTextBox),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    }

    public string SelectionText
    {
        get => (string)GetValue(SelectionTextProperty);
        set => SetValue(SelectionTextProperty, value);
    }

    public bool IsPaused
    {
        get => (bool)GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    protected override void OnSelectionChanged(RoutedEventArgs e)
    {
        base.OnSelectionChanged(e);
        SetCurrentValue(SelectionTextProperty, SelectedText);
        if (IsKeyboardFocusWithin && SelectionLength > 0)
            SetCurrentValue(IsPausedProperty, true);
    }
}
