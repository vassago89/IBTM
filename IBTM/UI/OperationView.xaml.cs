using System;
using System.Windows;
using System.Windows.Controls;
using IBTM.Storage;

namespace IBTM.UI;

public partial class OperationView : UserControl
{
    private PcbDetailsWindow? _pcbDetails;

    public OperationView()
    {
        InitializeComponent();
    }

    private void OnPcbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not PcbRecord record
            || DataContext is not OperationViewModel viewModel)
            return;
        viewModel.SelectedPcb = record;
        if (_pcbDetails is not null)
            return;
        _pcbDetails = new(viewModel.PcbDetails) { Owner = Window.GetWindow(this) };
        _pcbDetails.Closed += OnPcbDetailsClosed;
        _pcbDetails.Show();
    }

    private void OnPcbDetailsClosed(object? sender, EventArgs e)
    {
        _pcbDetails = null;
        if (DataContext is OperationViewModel viewModel)
            viewModel.ClosePcbDetailsCommand.Execute(null);
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        _pcbDetails?.Close();
    }
}
