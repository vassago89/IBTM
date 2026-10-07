using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using IBTM.Core;

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
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
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
        FasteningHeatSinkSelector.SelectedValue = _viewModel.SelectedFasteningResumeBolt?.HeatSink ?? HeatSinkSlot.HeatSink1;
    }

    private void OnFasteningRowsFilter(object sender, FilterEventArgs e)
    {
        var heatSink = FasteningHeatSinkSelector?.SelectedValue is HeatSinkSlot selected ? selected : HeatSinkSlot.HeatSink1;
        e.Accepted = e.Item is FasteningResumeRow row && row.HeatSink == heatSink;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OperationViewModel.SelectedStartArea))
            ReviewTabs.SelectedIndex = 0;
    }

    private void OnInterruptedBoltClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: FasteningResumeRow row })
            return;
        ReviewTabs.SelectedIndex = 1;
        FasteningHeatSinkSelector.SelectedValue = row.HeatSink;
        _viewModel.SelectedFasteningResumeBolt = row;
    }

    private void OnFasteningHeatSinkChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        var view = ((CollectionViewSource)Resources["FasteningRows"]).View;
        view.Refresh();
        _viewModel.SelectedFasteningResumeBolt = view.Cast<FasteningResumeRow>()
            .FirstOrDefault(row => row.Result?.Source == BoltResultSource.Interrupted)
            ?? view.Cast<FasteningResumeRow>().FirstOrDefault();
    }

    private static void OnAutomaticRunningChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var window = (StartConfirmationWindow)sender;
        if ((bool)e.NewValue && window.IsVisible)
            window.DialogResult = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.IsFasteningResumeConfirmed = false;
        _viewModel.IsPlacementResumeConfirmed = false;
        _viewModel.CheckStartCommand.Cancel();
        _viewModel.ChangeCarrierWorkCommand.Cancel();
        _viewModel.CompletePlacementCommand.Cancel();
        _viewModel.CompleteBoltCommand.Cancel();
        _viewModel.ReworkBoltCommand.Cancel();
        _viewModel.PrepareStartAreaCommand.Cancel();
        _viewModel.MoveAllToStandbyCommand.Cancel();
        foreach (var row in _viewModel.StartOutputGroups.Values.SelectMany(rows => rows).Distinct())
            row.ToggleOutputCommand.Cancel();
        if (DialogResult != true)
            _viewModel.ConfirmStartCommand.Cancel();
    }
}
