using System.Windows;

namespace IBTM.UI;

public partial class InputWindow : Window
{
    public InputWindow(InputWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
