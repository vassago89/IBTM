using System;
using System.Windows;
using System.Windows.Controls;
using IBTM.Storage;

namespace IBTM.UI;

public partial class OperationView : UserControl
{
    private PcbResultsWindow? _pcbDetails;

    public OperationView()
    {
        InitializeComponent();
    }

    private void OnPcbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not PcbRecord record
            || DataContext is not OperationViewModel viewModel)
            return;
        try
        {
            viewModel.PcbDetails.Record = record;
            if (_pcbDetails is not null)
                return;
            _pcbDetails = new(viewModel.PcbDetails);
            _pcbDetails.Closed += OnPcbDetailsClosed;
            _pcbDetails.Owner = Window.GetWindow(this);
            _pcbDetails.Show();
        }
        catch
        {
            var failedWindow = _pcbDetails;
            _pcbDetails = null;
            viewModel.PcbDetails.Record = null;
            if (failedWindow is not null)
            {
                failedWindow.Closed -= OnPcbDetailsClosed;
                failedWindow.Close();
            }
            throw;
        }
    }

    private void OnPcbDetailsClosed(object? sender, EventArgs e)
    {
        if (sender is PcbResultsWindow window)
            window.Closed -= OnPcbDetailsClosed;
        _pcbDetails = null;
        if (DataContext is OperationViewModel viewModel)
            viewModel.PcbDetails.Record = null;
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        _pcbDetails?.Close();
    }
}
