using System;
using System.ComponentModel;
using System.Windows;

namespace IBTM.UI;

public partial class MotionWindow : Window
{
    private readonly MotionWindowViewModel _viewModel;
    private bool _closeApproved;

    public MotionWindow(MotionWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Activate();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeApproved)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        base.OnClosing(e);
        if (_viewModel.IsClosing)
            return;
        if (await _viewModel.TryCloseAsync())
        {
            _closeApproved = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        else
        {
            MessageBox.Show(this, _viewModel.CloseError, "Motion Shutdown Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Deactivate();
        base.OnClosed(e);
    }
}
