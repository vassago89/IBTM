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

        var inspectionBolt = Command == teaching.RecordInspectionBoltPositionCommand;
        var mode = inspectionBolt ? TeachMode.XYOnly : point.Position.Mode;
        var replacement = mode switch
        {
            _ when point.UsesFiducialAlignment && (mode == TeachMode.XYOnly
                || point.Position.Target == TeachingTarget.DataMatrix && point.Coordinates is null)
                => UiText.Get("Measure both fiducials, then return here to record XY"),
            TeachMode.Image => UiText.Get("Update Image + Light"),
            TeachMode.XYOnly => UiText.Get("Replace the recorded X/Y with the current coordinates."),
            TeachMode.ZOnly => UiText.Get("Replace the recorded Z with the current coordinate."),
            _ => UiText.Get("Replace the recorded X/Y/Z with the current coordinates."),
        };
        var confirmed = WarningDialog.Confirm(
            Window.GetWindow(this),
            mode == TeachMode.Image ? UiText.Get("Update Image + Light") : UiText.Get("Record current position?"),
            replacement,
            UiText.Format($"Unit      {UiText.Get(teaching.SelectedTeachingUnit)}\nPoint     {point.Name}\nRecorded (mm)  {point.PositionLabel}"),
            Content?.ToString() ?? UiText.Get("Record Position"));
        if (!confirmed
            || !IsEnabled
            || !ReferenceEquals(DataContext, teaching)
            || !ReferenceEquals(teaching.SelectedPoint, point))
            return;

        // Raise Click and execute the bound command only after confirmation.
        base.OnClick();
    }
}
