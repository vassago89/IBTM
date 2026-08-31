using System.Windows;

namespace IBTM.UI;

public partial class Station2RecoveryWindow : Window
{
    public Station2RecoveryWindow(BoltRecoveryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnApply(object sender, RoutedEventArgs e) =>
        DialogResult = true;
}
