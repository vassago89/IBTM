using IBTM.ViewModels;
using System.Windows;

namespace IBTM;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
