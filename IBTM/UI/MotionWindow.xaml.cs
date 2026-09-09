using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace IBTM.UI;

public partial class MotionWindow : Window
{
    private readonly MotionWindowViewModel _viewModel;
    private readonly MachineState _state;
    private bool _closing;
    private bool _shutdownCompleted;
    private int _refreshQueued;
    private readonly DispatcherTimer _refreshTimer;

    public MotionWindow(MotionWindowViewModel viewModel, MachineState state)
    {
        _viewModel = viewModel;
        _state = state;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _refreshTimer.Tick += OnRefreshTick;
        InitializeComponent();
        DataContext = viewModel;
        state.DisplayChanged += OnDisplayChanged;
        viewModel.Refresh();
        state.RequestDisplayRefresh();
        _refreshTimer.Start();
    }

    // Only request a shared worker scan here; native reads never run on the UI timer.
    private void OnRefreshTick(object? sender, EventArgs e) => _state.RequestDisplayRefresh();

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
        _refreshTimer.Stop();
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
            _refreshTimer.Start();
            System.Diagnostics.Trace.TraceError("Motion window shutdown failed. {0}", exception);
            MessageBox.Show(this, exception.Message, "Motion Shutdown Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;
        _state.DisplayChanged -= OnDisplayChanged;
        base.OnClosed(e);
    }
}
