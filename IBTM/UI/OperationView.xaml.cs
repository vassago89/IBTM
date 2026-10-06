using System;
using System.Windows;
using System.Windows.Controls;
using IBTM.Core;
using IBTM.Storage;

namespace IBTM.UI;

public partial class OperationView : UserControl
{
    private PcbResultsWindow? _pcbDetails;
    private TorqueCurvesWindow? _torqueCurves;

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
                throw new AggregateException(UiText.Get("PCB details could not be opened or closed."), exception, closeException);
            }
            finally
            {
                viewModel.PcbDetails.Record = null;
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
        _torqueCurves?.Close();
    }

    private void OnTorqueCurvesClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not OperationViewModel viewModel)
            return;
        _torqueCurves?.Close();
        _torqueCurves = new(new(viewModel.Fastening.TorqueCurveJobId, viewModel.Fastening.TorqueCurves))
        {
            Owner = Window.GetWindow(this),
        };
        _torqueCurves.Closed += OnTorqueCurvesClosed;
        _torqueCurves.Show();
    }

    private void OnTorqueCurvesClosed(object? sender, EventArgs e)
    {
        if (sender is TorqueCurvesWindow window)
            window.Closed -= OnTorqueCurvesClosed;
        _torqueCurves = null;
    }
}
