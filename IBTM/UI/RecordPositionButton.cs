using System.Windows;
using System.Windows.Controls;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public sealed class RecordPositionButton : Button
{
    protected override void OnClick()
    {
        if (DataContext is not TeachingViewModel { SelectedPoint: { } point } teaching)
            return;

        var replacement = point.Position.Mode switch
        {
            TeachMode.Image => "Replace the recorded X/Y, reference image and light level with the current values.",
            TeachMode.XYOnly => "Replace the recorded X/Y with the current coordinates.",
            TeachMode.ZOnly => "Replace the recorded Z with the current coordinate.",
            _ => "Replace the recorded X/Y/Z with the current coordinates.",
        };
        var confirmed = WarningDialog.Confirm(
            Window.GetWindow(this),
            point.Position.Mode == TeachMode.Image ? "Save position and image?" : "Record current position?",
            replacement,
            $"Unit      {teaching.SelectedTeachingUnit.GetDescription()}\nPoint     {point.Name}\nRecorded (mm)  {point.PositionLabel}",
            Content?.ToString() ?? "Record Position");
        if (!confirmed
            || !IsEnabled
            || !ReferenceEquals(DataContext, teaching)
            || !ReferenceEquals(teaching.SelectedPoint, point))
            return;

        // Raise Click and execute the bound command only after confirmation.
        base.OnClick();
    }
}
