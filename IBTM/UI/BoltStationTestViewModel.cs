using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Storage;
using Microsoft.Extensions.Logging;

namespace IBTM.UI;

public partial class BoltTestRow : ObservableObject
{
    public BoltTestRow(BoltPoint bolt, string label)
    {
        Bolt = bolt;
        Label = label;
        IsSelected = true;
        Status = "Not run";
    }

    public BoltPoint Bolt { get; }
    public string Label { get; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string Status { get; set; }
    [ObservableProperty] public partial BoltResult? Result { get; set; }
}

public partial class BoltStationTestViewModel : ObservableObject
{
    private readonly MachineController _machine;
    private readonly RecipeManager _recipes;
    private readonly BoltFasteningStation _station;
    private readonly ILogger<BoltStationTestViewModel> _log;

    public BoltStationTestViewModel(
        MachineController machine, MachineState state, UnitSettings units,
        RecipeManager recipes, BoltFasteningStation station, ILogger<BoltStationTestViewModel> log)
    {
        _machine = machine;
        State = state;
        Units = units;
        _recipes = recipes;
        _station = station;
        _log = log;
        Bolts = new();
        RunCommand = new AsyncRelayCommand(RunAsync, () => IsRunAllowed);
        StopCommand = new AsyncRelayCommand(StopAsync);
        SelectAllCommand = new RelayCommand(SelectAll, () => !RunCommand.IsRunning && !IsClosing);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => !RunCommand.IsRunning && !IsClosing);
        Message = string.Empty;
    }

    public MachineState State { get; }
    public UnitSettings Units { get; }
    public ObservableCollection<BoltTestRow> Bolts { get; }
    public IAsyncRelayCommand RunCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }
    [ObservableProperty] public partial bool IsClosing { get; private set; }
    [ObservableProperty] public partial string Message { get; private set; }
    [ObservableProperty] public partial string? Error { get; private set; }
    [ObservableProperty] public partial double? TotalRunSeconds { get; private set; }

    public int SelectedCount => Bolts.Count(row => row.IsSelected);

    public bool IsRunAllowed => !IsClosing && State.ManualSetupEnabled
        && _machine.IsManualMotionReady(MotionGroup.BoltFastening, live: false)
        && _station.Station.CarrierSeated && SelectedCount > 0
        && Bolts.Where(row => row.IsSelected).All(row => row.Bolt.IsFasteningPositionDefined
            && _station.Station.IsHeatSinkPresent(row.Bolt.HeatSink));

    public string Readiness
    {
        get
        {
            if (!State.ManualMode)
                return "Manual mode required.";
            if (RunCommand.IsRunning)
                return string.Empty;
            if (!State.ManualSetupEnabled)
                return "Stop the machine and clear alarms / safety blocks.";
            if (!_machine.IsManualMotionReady(MotionGroup.BoltFastening, live: false))
                return "Check station enablement, homing and servo feedback.";
            if (!_station.Station.CarrierSeated)
                return "Load the S2 carrier and raise the backup plate.";
            if (Bolts.Any(row => row.IsSelected && !_station.Station.IsHeatSinkPresent(row.Bolt.HeatSink)))
                return "Selected PCB not detected at S2.";
            if (Bolts.Any(row => row.IsSelected && !row.Bolt.IsFasteningPositionDefined))
                return "Selected bolt is not taught.";
            return SelectedCount == 0 ? "Select a bolt." : string.Empty;
        }
    }

    public void Activate()
    {
        IsClosing = false;
        Error = null;
        TotalRunSeconds = null;
        Message = string.Empty;
        foreach (var row in Bolts)
            row.PropertyChanged -= OnRowChanged;
        Bolts.Clear();
        foreach (var bolt in _recipes.Current.Pcb.FasteningPoints)
        {
            var row = new BoltTestRow(bolt, _recipes.Current.Pcb.GetBoltName(bolt.Id));
            row.PropertyChanged += OnRowChanged;
            Bolts.Add(row);
        }
        State.Changed += Refresh;
        _station.Changed += Refresh;
        RunCommand.PropertyChanged += OnRunChanged;
        Refresh();
    }

    public void Deactivate()
    {
        State.Changed -= Refresh;
        _station.Changed -= Refresh;
        RunCommand.PropertyChanged -= OnRunChanged;
        foreach (var row in Bolts)
            row.PropertyChanged -= OnRowChanged;
    }

    private void SelectAll()
    {
        foreach (var row in Bolts)
            row.IsSelected = true;
    }

    private void ClearSelection()
    {
        foreach (var row in Bolts)
            row.IsSelected = false;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoltTestRow.IsSelected))
            Refresh();
    }

    private void OnRunChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning))
            Refresh();
    }

    private void Refresh()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Refresh);
            return;
        }
        if (RunCommand.IsRunning && _station.ActiveBolt is { } active)
        {
            var row = Bolts.FirstOrDefault(item => item.Bolt.Id == active.Id);
            if (row is not null && row.Result is null)
                row.Status = "Running";
        }
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(Readiness));
        RunCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private void OnResultReceived(BoltPoint bolt, BoltResult result)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => OnResultReceived(bolt, result));
            return;
        }
        var row = Bolts.FirstOrDefault(item => item.Bolt.Id == bolt.Id);
        if (row is null)
            return;
        row.Result = result;
        row.Status = result.Source == BoltResultSource.DryRun ? "Dry run" : result.Success ? "OK" : "NG";
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var selected = Bolts.Where(row => row.IsSelected).ToArray();
        Error = null;
        TotalRunSeconds = null;
        foreach (var row in selected)
        {
            row.Result = null;
            row.Status = "Queued";
        }
        Message = $"Testing {selected.Length} selected bolts...";
        var started = Stopwatch.GetTimestamp();
        try
        {
            var completed = await _machine.RunSelectedBoltsAsync(
                selected.Select(row => row.Bolt.Id).ToArray(), OnResultReceived, cancellationToken);
            Message = completed ? "Test completed." : "Test stopped.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Message = "Test stopped.";
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            Message = "Test failed.";
            _log.LogError(exception, "Selected-bolt test failed.");
            _machine.ReportManualFailure(MachineAlarm.BoltFastening, exception);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started);
            TotalRunSeconds = elapsed.TotalSeconds;
            _log.LogInformation("Selected-bolt test timing: selected={Count}, elapsed={ElapsedMs:F1} ms, outcome={Outcome}.",
                selected.Length, elapsed.TotalMilliseconds, Message);
            foreach (var row in selected.Where(row => row.Result is null))
                row.Status = row.Status == "Running" ? (Error is null ? "Stopped" : "Error") : "Not run";
            Refresh();
        }
    }

    private async Task StopAsync()
    {
        try
        {
            var pending = CommandShutdown.Capture(RunCommand);
            await CommandShutdown.CancelAndWaitAsync([RunCommand], _machine.StopAsync(), pending);
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _log.LogError(exception, "Bolt station test STOP failed.");
            _machine.ReportManualFailure(MachineAlarm.StopFailed, exception);
        }
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.CancelAndWaitAsync([RunCommand, StopCommand]);
    }

    public async Task<bool> TryCloseAsync()
    {
        IsClosing = true;
        try
        {
            await ShutdownAsync();
            return true;
        }
        catch (Exception exception)
        {
            IsClosing = false;
            Error = exception.Message;
            _log.LogError(exception, "Bolt station test shutdown failed.");
            _machine.ReportManualFailure(MachineAlarm.StopFailed, exception);
            return false;
        }
    }
}
