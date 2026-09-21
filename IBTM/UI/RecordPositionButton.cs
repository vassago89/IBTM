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

        var replacement = point.Position.Mode == TeachMode.Image
            ? "This will replace this point's teaching coordinates and image."
            : "This will replace this point's teaching coordinates.";
        var confirmed = WarningDialog.Confirm(
            Window.GetWindow(this),
            "Overwrite teaching position?",
            $"{replacement}\nCheck the selected point and current axis position before recording.",
            $"Unit    {teaching.SelectedTeachingUnit.GetDescription()}\nPoint   {point.Name}\nStored  {point.PositionLabel} mm",
            "Record Position");
        if (!confirmed
            || !IsEnabled
            || !ReferenceEquals(DataContext, teaching)
            || !ReferenceEquals(teaching.SelectedPoint, point))
            return;

        // Raise Click and execute the bound command only after confirmation.
        base.OnClick();
    }
}
