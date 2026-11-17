using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using IBTM.Core;

namespace IBTM.UI;

public partial class ProtocolWindow : Window
{
    private readonly AdcProtocolViewModel _viewModel;
    private readonly MlcpProtocolViewModel _mlcp;
    private bool _closeApproved;

    public ProtocolWindow(AdcProtocolViewModel viewModel, MlcpProtocolViewModel mlcp)
    {
        _viewModel = viewModel;
        _mlcp = mlcp;
        InitializeComponent();
        DataContext = viewModel;
        MlcpView.DataContext = mlcp;
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
        if (_viewModel.IsClosing || _mlcp.IsClosing)
            return;
        var closed = await Task.WhenAll(_viewModel.TryCloseAsync(), _mlcp.TryCloseAsync());
        if (closed.All(success => success))
        {
            _closeApproved = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        else
        {
            _viewModel.IsClosing = false;
            _mlcp.IsClosing = false;
            _viewModel.RefreshControls();
            MessageBox.Show(this, string.Join(Environment.NewLine,
                new[] { _viewModel.CloseError, _mlcp.CloseError }.OfType<string>()), UiText.Get("Shutdown Incomplete"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        _mlcp.Dispose();
        base.OnClosed(e);
    }
}
