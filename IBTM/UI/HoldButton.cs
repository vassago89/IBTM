using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IBTM.UI;

public sealed class HoldButton : Button
{
    public static readonly DependencyProperty PressCommandProperty;

    public static readonly DependencyProperty ReleaseCommandProperty;

    private bool _holding;

    static HoldButton()
    {
        PressCommandProperty = DependencyProperty.Register(
            nameof(PressCommand),
            typeof(ICommand),
            typeof(HoldButton),
            new PropertyMetadata(null, OnPressCommandChanged));
        ReleaseCommandProperty = DependencyProperty.Register(
            nameof(ReleaseCommand),
            typeof(ICommand),
            typeof(HoldButton));
    }

    public ICommand? PressCommand
    {
        get => (ICommand?)GetValue(PressCommandProperty);
        set => SetValue(PressCommandProperty, value);
    }

    public ICommand? ReleaseCommand
    {
        get => (ICommand?)GetValue(ReleaseCommandProperty);
        set => SetValue(ReleaseCommandProperty, value);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (PressCommand?.CanExecute(CommandParameter) != true || !CaptureMouse())
        {
            return;
        }

        _holding = true;
        // CanExecute admits a new jog; it becomes false while this jog is running.
        Command = null;
        PressCommand.Execute(CommandParameter);
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        EndHold();
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (!_holding)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (position.X < 0
            || position.X > ActualWidth
            || position.Y < 0
            || position.Y > ActualHeight)
        {
            EndHold();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        EndHold();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndHold();
    }

    protected override void OnClick()
    {
    }

    private static void OnPressCommandChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs e)
    {
        ((HoldButton)sender).Command = ((HoldButton)sender)._holding ? null : (ICommand?)e.NewValue;
    }

    private void EndHold()
    {
        if (!_holding)
        {
            return;
        }

        _holding = false;
        ReleaseCommand?.Execute(CommandParameter);
        Command = PressCommand;
        ReleaseMouseCapture();
    }
}
