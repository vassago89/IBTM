using System.Windows;
using System.Windows.Input;

namespace IBTM.UI;

public partial class PcbDetailsWindow : Window
{
    public PcbDetailsWindow(PcbDetailsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Width = System.Math.Min(1360, SystemParameters.WorkArea.Width - 40);
        Height = System.Math.Min(860, SystemParameters.WorkArea.Height - 40);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}
