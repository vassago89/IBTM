using System.Windows;
using System.Windows.Controls;
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
        var result = MessageBox.Show(
            Window.GetWindow(this),
            $"Record the current axis position for \"{point.Name}\"?\n\n"
                + $"Recorded position: {point.PositionLabel}\n\n{replacement}",
            "Record Position",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.OK
            || !IsEnabled
            || !ReferenceEquals(DataContext, teaching)
            || !ReferenceEquals(teaching.SelectedPoint, point))
            return;

        // Raise Click and execute the bound command only after confirmation.
        base.OnClick();
    }
}
