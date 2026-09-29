using System.Windows;
using System.Windows.Controls;
using IBTM.Core;

namespace IBTM.UI;

public sealed class MoveToPositionButton : Button
{
    protected override void OnClick()
    {
        if (DataContext is not TeachingViewModel { SelectedPoint: { } point } teaching
            || Command?.CanExecute(CommandParameter) != true)
            return;

        var unit = teaching.SelectedTeachingUnit;
        var target = point.PositionLabel;
        var command = Command;
        var confirmed = WarningDialog.Confirm(Window.GetWindow(this),
            UiText.Get("Move machine axes?"),
            UiText.Get("Keep the travel path clear before moving."),
            UiText.Format($"Unit    {UiText.Get(unit)}\nPoint   {point.Name}\nTarget  {target} mm\nSpeed   Configured axis speed"),
            UiText.Get("Move to Selected Point"));
        if (!confirmed || !IsEnabled
            || !ReferenceEquals(DataContext, teaching)
            || !ReferenceEquals(teaching.SelectedPoint, point)
            || teaching.SelectedTeachingUnit != unit
            || point.PositionLabel != target
            || !ReferenceEquals(Command, command)
            || !command.CanExecute(CommandParameter))
            return;

        base.OnClick();
    }
}
