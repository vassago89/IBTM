using System.Windows;

namespace IBTM.UI;

public partial class InputWindow : Window
{
    public InputWindow(InputViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
