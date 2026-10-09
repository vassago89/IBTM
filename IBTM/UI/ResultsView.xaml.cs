using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using IBTM.Storage;

namespace IBTM.UI;

public partial class ResultsView : UserControl
{
    private PcbResultsWindow? _pcbDetails;

    public event EventHandler? ReinspectionRequested;

    public ResultsView()
    {
        InitializeComponent();
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ResultsViewModel viewModel)
            return;
        CollectionViewSource.GetDefaultView(viewModel.Records).Filter =
            record => viewModel.IsRecordVisible((PcbRecord)record);
        viewModel.PropertyChanged += OnFilterChanged;
    }

    private void OnFilterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ResultsViewModel viewModel
            && e.PropertyName is nameof(ResultsViewModel.HistorySearch)
                or nameof(ResultsViewModel.HistoryDate) or nameof(ResultsViewModel.HistoryResult))
            CollectionViewSource.GetDefaultView(viewModel.Records).Refresh();
    }

    private void OnOpenResultsClick(object sender, RoutedEventArgs e)
    {
        OpenSelectedResult();
    }

    private void OnRecordDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGrid grid && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(grid, source) is DataGridRow)
            OpenSelectedResult();
    }

    private void OnRecordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OpenSelectedResult();
            e.Handled = true;
        }
    }

    private void OnReinspectClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ResultsViewModel { SelectedRecord: not null })
            ReinspectionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenSelectedResult()
    {
        if (DataContext is not ResultsViewModel { SelectedRecord: { } record } viewModel)
            return;
        viewModel.PcbDetails.Record = record;
        if (_pcbDetails is not null)
        {
            if (_pcbDetails.WindowState == WindowState.Minimized)
                _pcbDetails.WindowState = WindowState.Normal;
            _pcbDetails.Activate();
            return;
        }
        _pcbDetails = new(viewModel.PcbDetails) { Owner = Window.GetWindow(this) };
        _pcbDetails.Closed += OnPcbDetailsClosed;
        _pcbDetails.Show();
    }

    private void OnPcbDetailsClosed(object? sender, EventArgs e)
    {
        if (sender is PcbResultsWindow window)
            window.Closed -= OnPcbDetailsClosed;
        _pcbDetails = null;
        if (DataContext is ResultsViewModel viewModel)
            viewModel.PcbDetails.Record = null;
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ResultsViewModel viewModel)
            viewModel.PropertyChanged -= OnFilterChanged;
        _pcbDetails?.Close();
    }
}
