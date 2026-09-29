using System;
using System.Windows;

namespace IBTM.UI;

public partial class LogWindow : Window
{
    private readonly LogViewModel _viewModel;

    public LogWindow(LogViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = _viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
