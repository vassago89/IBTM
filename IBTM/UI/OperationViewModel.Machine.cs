using System;
using System.ComponentModel;
using System.Windows;

namespace IBTM.UI;

public partial class OperationViewModel
{
    private static string FormatPosition(
        (double X, double Y, double Z) position) =>
        $"X {position.X:F3}   Y {position.Y:F3}   Z {position.Z:F3}";

    private static string FormatXyPosition(
        (double X, double Y, double Z) position) =>
        $"X {position.X:F3}   Y {position.Y:F3}";

    private void OnPcbSupplyPositionChanged(double x, double y, double z) =>
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(PcbSupplyPosition));
            OnPropertyChanged(nameof(PcbSupplyMapLeft));
            OnPropertyChanged(nameof(PcbSupplyMapTop));
        });

    private void OnPcbPlacementPositionChanged(double x, double y, double z) =>
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(PcbPlacementPosition));
            OnPropertyChanged(nameof(PcbPlacementMapLeft));
            OnPropertyChanged(nameof(PcbPlacementMapTop));
        });

    private void OnBoltFasteningPositionChanged(double x, double y, double z) =>
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(BoltFasteningPosition));
            OnPropertyChanged(nameof(BoltFasteningMapLeft));
            OnPropertyChanged(nameof(BoltFasteningMapTop));
        });

    private void OnInspectionGantryPositionChanged(double x, double y, double z) =>
        RunOnUi(() =>
        {
            OnPropertyChanged(nameof(InspectionGantryPosition));
            OnPropertyChanged(nameof(InspectionGantryMapLeft));
            OnPropertyChanged(nameof(InspectionGantryMapTop));
        });

    private void OnMachineStateChanged() =>
        RunOnUi(() =>
        {
            OnPropertyChanged(new PropertyChangedEventArgs(null));
            NotifyCanExecuteChanged();
        });

    private static void RunOnUi(Action action) =>
        Application.Current.Dispatcher.BeginInvoke(action);
}
