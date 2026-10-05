using System;
using System.Windows;

namespace IBTM.UI;

public partial class TorqueCurvesWindow : Window
{
    public TorqueCurvesWindow(TorqueCurvesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32);
    }
}
