using System.Windows;

namespace IBTM.UI;

public partial class Station1RecoveryWindow : Window
{
    public Station1RecoveryWindow(
        PcbPlacementRecoveryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnApply(object sender, RoutedEventArgs e) =>
        DialogResult = true;
}
