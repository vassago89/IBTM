using System;
using System.Windows;
using System.Windows.Data;

namespace IBTM.UI;

public partial class StartConfirmationWindow : Window
{
    private static readonly DependencyProperty s_automaticRunningProperty;
    private readonly OperationViewModel _viewModel;

    static StartConfirmationWindow()
    {
        s_automaticRunningProperty = DependencyProperty.Register(
            nameof(MachineState.AutomaticRunning), typeof(bool), typeof(StartConfirmationWindow),
            new PropertyMetadata(false, OnAutomaticRunningChanged));
    }

    public StartConfirmationWindow(OperationViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        // Binding delivers the production state change on this window's dispatcher.
        SetBinding(s_automaticRunningProperty,
            new Binding($"{nameof(OperationViewModel.State)}.{nameof(MachineState.AutomaticRunning)}"));
        MaxWidth = SystemParameters.WorkArea.Width;
        MaxHeight = SystemParameters.WorkArea.Height;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        CancelButton.Focus();
        await _viewModel.CheckStartCommand.ExecuteAsync(null);
    }

    private static void OnAutomaticRunningChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var window = (StartConfirmationWindow)sender;
        if ((bool)e.NewValue && window.IsVisible)
            window.DialogResult = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.IsFasteningResumeConfirmed = false;
        _viewModel.CheckStartCommand.Cancel();
        _viewModel.ChangeCarrierWorkCommand.Cancel();
        _viewModel.SetStartBackupPlateCommand.Cancel();
        if (DialogResult != true)
            _viewModel.ConfirmStartCommand.Cancel();
    }
}
