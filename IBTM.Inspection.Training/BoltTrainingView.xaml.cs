using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IBTM.Inspection.Training;

public partial class BoltTrainingView : UserControl
{
    public BoltTrainingView() => InitializeComponent();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;
        MaskEditor.Clear();
        e.Handled = true;
    }

    private void ClearMask(object sender, RoutedEventArgs e) =>
        MaskEditor.Clear();

    private void ClosePolygon(object sender, RoutedEventArgs e) =>
        MaskEditor.ClosePolygon();

    private void UndoPoint(object sender, RoutedEventArgs e) =>
        MaskEditor.UndoPoint();

    private void FitImage(object sender, RoutedEventArgs e) => MaskEditor.FitImage();
    private void FitRegion(object sender, RoutedEventArgs e) => MaskEditor.FitRegion();

    private void SelectSample(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is BoltTrainingViewModel model && ((ComboBox)sender).SelectedItem is BoltSampleInfo sample
            && model.OpenSampleCommand.CanExecute(sample))
            model.OpenSampleCommand.Execute(sample);
    }
}
