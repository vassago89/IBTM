using System;
using System.Windows;

namespace IBTM.UI;

public partial class StartConfirmationWindow : Window
{
    private readonly OperationViewModel _viewModel;

    public StartConfirmationWindow(OperationViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        MaxWidth = SystemParameters.WorkArea.Width;
        MaxHeight = SystemParameters.WorkArea.Height;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        CancelButton.Focus();
        await _viewModel.CheckStartCommand.ExecuteAsync(null);
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsStartReviewAllowed && !_viewModel.CheckStartCommand.IsRunning)
            DialogResult = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CheckStartCommand.Cancel();
    }
}
