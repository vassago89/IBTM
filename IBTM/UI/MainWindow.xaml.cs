using System.ComponentModel;
using System.Windows;
using IBTM.Core;

namespace IBTM.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closeApproved;

    public MainWindow(MainViewModel viewModel, DiagnosticWindowManager windows)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        windows.Owner = this;
    }

    private async void OnReinspectionRequested(object? sender, System.EventArgs e)
    {
        if (!_viewModel.NavigateCommand.CanExecute(AppPage.Inspection))
            return;
        await _viewModel.NavigateCommand.ExecuteAsync(AppPage.Inspection);
        if (_viewModel.CurrentPage is InspectionTeachingViewModel inspection)
            inspection.SelectedTab = InspectionTeachingTab.History;
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
            if (MessageBox.Show(this, _viewModel.CloseError, UiText.Get("Shutdown Incomplete"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            _viewModel.ApproveUnconfirmedExit();
        }
        if (Application.Current is App app)
            await app.CompleteExitAsync();
        _closeApproved = true;
        _ = Dispatcher.BeginInvoke(Close);
    }
}
