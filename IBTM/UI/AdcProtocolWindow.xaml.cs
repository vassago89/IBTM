using System;
using System.ComponentModel;
using System.Windows;

namespace IBTM.UI;

public partial class AdcProtocolWindow : Window
{
    private readonly AdcProtocolViewModel _viewModel;
    private bool _closeApproved;

    public AdcProtocolWindow(AdcProtocolViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
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
            MessageBox.Show(this, _viewModel.CloseError, "ADC Shutdown Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
