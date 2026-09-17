using System.Windows;

namespace IBTM.UI;

public partial class OutputWindow : Window
{
    public OutputWindow(OutputWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
