using System.ComponentModel;
using System.Windows;

namespace IBTM.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closeApproved;

    public MainWindow(MainViewModel viewModel, DiagnosticWindows windows)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        windows.Owner = this;
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
        var stopped = await _viewModel.TryCloseAsync();
        if (!stopped)
        {
            if (MessageBox.Show(this, _viewModel.CloseError, "Shutdown Incomplete",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            _viewModel.ApproveUnconfirmedExit();
        }
        _closeApproved = true;
        _ = Dispatcher.BeginInvoke(Close);
    }
}
