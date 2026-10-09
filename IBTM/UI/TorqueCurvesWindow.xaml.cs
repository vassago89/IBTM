using System;
using System.Windows;
using System.Windows.Input;

namespace IBTM.UI;

public partial class TorqueCurvesWindow : Window
{
    public TorqueCurvesWindow(PcbBoltResultView result)
    {
        InitializeComponent();
        DataContext = result;
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width - 32);
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height - 32);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}
