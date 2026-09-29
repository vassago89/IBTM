using System.Windows;
using System.Windows.Input;

namespace IBTM.UI;

public partial class PcbDetailsWindow : Window
{
    public PcbDetailsWindow(PcbDetailsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var workArea = SystemParameters.WorkArea;
        MinWidth = System.Math.Min(MinWidth, workArea.Width - 32);
        MinHeight = System.Math.Min(MinHeight, workArea.Height - 32);
        Width = System.Math.Min(1600, workArea.Width - 32);
        Height = System.Math.Min(960, workArea.Height - 32);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}
