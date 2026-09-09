using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace IBTM.UI;

public partial class MotionWindow : Window
{
    private readonly MotionWindowViewModel _viewModel;
    private readonly MachineState _state;
    private bool _closing;
    private bool _shutdownCompleted;
    private int _refreshQueued;

    public MotionWindow(MotionWindowViewModel viewModel, MachineState state)
    {
        _viewModel = viewModel;
        _state = state;
        InitializeComponent();
        DataContext = viewModel;
        state.DisplayChanged += OnDisplayChanged;
        viewModel.Refresh();
        state.RequestDisplayRefresh();
    }

    private void OnDisplayChanged()
    {
        if (_closing || Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_closing) _viewModel.Refresh();
        });
    }

    public Task ShutdownAsync()
    {
        IsEnabled = false;
        return _viewModel.ShutdownAsync();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownCompleted) { base.OnClosing(e); return; }
        e.Cancel = true;
        base.OnClosing(e);
        if (_closing) return;
        _closing = true;
        try
        {
            await ShutdownAsync();
            _shutdownCompleted = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
        catch (Exception exception)
        {
            _closing = false;
            IsEnabled = true;
            System.Diagnostics.Trace.TraceError("Motion window shutdown failed. {0}", exception);
            MessageBox.Show(this, exception.Message, "Motion Shutdown Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _state.DisplayChanged -= OnDisplayChanged;
        base.OnClosed(e);
    }
}
