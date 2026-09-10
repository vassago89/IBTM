using System.Windows;

namespace IBTM.UI;

public partial class BoltFasteningRecoveryWindow : Window
{
    public BoltFasteningRecoveryWindow(BoltFasteningRecoveryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
