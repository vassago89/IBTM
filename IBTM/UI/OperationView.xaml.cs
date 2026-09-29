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
            viewModel.SelectedPcb = record;
            if (_pcbDetails is not null)
                return;
            _pcbDetails = new(viewModel.PcbDetails);
            _pcbDetails.Closed += OnPcbDetailsClosed;
            _pcbDetails.Owner = Window.GetWindow(this);
            _pcbDetails.Show();
        }
        catch (Exception exception)
        {
            var failedWindow = _pcbDetails;
            _pcbDetails = null;
            if (failedWindow is not null)
                failedWindow.Closed -= OnPcbDetailsClosed;
            try
            {
                failedWindow?.Close();
            }
            catch (Exception closeException)
            {
                throw new AggregateException("PCB details could not be opened or closed.", exception, closeException);
            }
            finally
            {
                viewModel.ClosePcbDetailsCommand.Execute(null);
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
            viewModel.ClosePcbDetailsCommand.Execute(null);
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        _pcbDetails?.Close();
    }
}
