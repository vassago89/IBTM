using System.Windows;

namespace IBTM.UI;

public partial class PcbPlacementRecoveryWindow : Window
{
    public PcbPlacementRecoveryWindow(PcbPlacementRecoveryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

}
