using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IBTM.UI;

public partial class PcbResultsWindow : Window
{
    private TorqueCurvesWindow? _torqueCurves;

    public PcbResultsWindow(PcbResultsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var workArea = SystemParameters.WorkArea;
        MinWidth = System.Math.Min(MinWidth, workArea.Width - 32);
        MinHeight = System.Math.Min(MinHeight, workArea.Height - 32);
        Width = System.Math.Min(1600, workArea.Width - 32);
        Height = System.Math.Min(960, workArea.Height - 32);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    private void OnTorqueCurveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: PcbBoltResultView { Result.TorqueCurve: not null } row }
            || DataContext is not PcbResultsViewModel { Record: { } record } viewModel)
            return;
        viewModel.SelectedBoltStage = row;
        _torqueCurves?.Close();
        _torqueCurves = new(new(null,
            [new(row.BoltId, row.BoltLabel, record.HeatSink, row.Result.ToolHead ?? row.Head, row.Result)]))
        {
            Owner = this,
            Title = Title + " · " + row.BoltLabel,
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

    protected override void OnClosed(EventArgs e)
    {
        _torqueCurves?.Close();
        base.OnClosed(e);
    }
}
