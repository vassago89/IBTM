using System.Windows;
using System.Windows.Controls;

namespace IBTM.Inspection.Training;

public partial class BoltTrainingView : UserControl
{
    public BoltTrainingView() => InitializeComponent();

    private void ClearMask(object sender, RoutedEventArgs e) =>
        MaskEditor.Clear();
}
