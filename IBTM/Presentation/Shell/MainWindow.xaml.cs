using System.Windows;
using IBTM.Presentation.ViewModels;

namespace IBTM.Presentation.Shell;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
