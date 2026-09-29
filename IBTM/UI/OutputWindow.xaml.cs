using System.Windows;

namespace IBTM.UI;

public partial class OutputWindow : Window
{
    public OutputWindow(OutputViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
