using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace IBTM.UI;

public partial class TeachingView : UserControl
{
    private readonly double _xamlMilliseconds;

    public TeachingView()
    {
        var started = Stopwatch.GetTimestamp();
        InitializeComponent();
        _xamlMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        var loadedAt = Stopwatch.GetTimestamp();
        if (DataContext is TeachingViewModel viewModel)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
                viewModel.ReportViewReady(_xamlMilliseconds, Stopwatch.GetElapsedTime(loadedAt).TotalMilliseconds));
        }
    }
}
