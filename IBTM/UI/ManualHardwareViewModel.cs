using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class ManualHardwareViewModel : ObservableObject
{
    private readonly MachineState _state;
    private readonly MachineController _machine;
    private volatile bool _active;
    private int _stateRefreshQueued;
    [ObservableProperty] private DryRunTarget _selectedDryRun;
    [ObservableProperty] private HeatSinkSlot _selectedDryRunHeatSink;

    public ManualHardwareViewModel(
        IoSignals signals,
        MachineState state,
        MachineController machine)
    {
        _state = state;
        _machine = machine;
        Conveyors = [
            new(signals.Outputs[OutputIo.MainConveyorRun], machine),
            new(signals.Outputs[OutputIo.NgConveyorRun], machine),
        ];

        state.DisplayChanged += OnMachineStateChanged;
    }

    public OutputControlRow[] Conveyors { get; }
    public DryRunTarget[] DryRunTargets { get; } = Enum.GetValues<DryRunTarget>();
    public HeatSinkSlot[] DryRunHeatSinks { get; } = Enum.GetValues<HeatSinkSlot>();
    public bool IsInspectionDryRun => SelectedDryRun == DryRunTarget.Inspection;
    public Enum DryRunState => SelectedDryRun switch
    {
        DryRunTarget.MainConveyor => _state.Display.MainConveyorDryRunState,
        DryRunTarget.Inspection => _state.Display.InspectionDryRunState,
        DryRunTarget.PcbReturn => _state.Display.PcbReturnState,
        DryRunTarget.PcbRoundTrip => _state.Display.PcbDryRunState,
        DryRunTarget.NgConveyor => _state.Display.NgConveyorDryRunState,
        DryRunTarget.BoltRoute => _state.Display.BoltRouteState,
        _ => _state.Display.NgTransferDryRunState,
    };
    public Enum DryRunDestination => SelectedDryRun switch
    {
        DryRunTarget.MainConveyor => _state.Display.MainConveyorDestination,
        DryRunTarget.Inspection => _state.Display.InspectionDryRunDirection,
        DryRunTarget.PcbReturn => _state.Display.PcbReturnDestination,
        DryRunTarget.PcbRoundTrip => _state.Display.PcbDryRunDirection,
        DryRunTarget.NgConveyor => _state.Display.NgConveyorDestination,
        DryRunTarget.BoltRoute => _state.Display.BoltRouteDirection,
        _ => _state.Display.NgTransferDestination,
    };
    public int DryRunPasses => SelectedDryRun switch
    {
        DryRunTarget.MainConveyor => _state.Display.MainConveyorDryRunPasses,
        DryRunTarget.Inspection => _state.Display.InspectionDryRunPasses,
        DryRunTarget.PcbReturn => _state.Display.PcbReturnCount,
        DryRunTarget.PcbRoundTrip => _state.Display.PcbDryRunCycles,
        DryRunTarget.NgConveyor => _state.Display.NgConveyorDryRunPasses,
        DryRunTarget.BoltRoute => _state.Display.BoltRoutePasses,
        _ => _state.Display.NgTransferDryRunTransfers,
    };
    public HeatSinkSlot? DryRunPcb => SelectedDryRun switch
    {
        DryRunTarget.PcbReturn => _state.Display.PcbReturnHeatSink,
        DryRunTarget.PcbRoundTrip => _state.Display.PcbDryRunDirection == PcbDryRunDirection.Ready
            ? null : _state.Display.PcbDryRunHeatSink,
        DryRunTarget.BoltRoute => _state.Display.BoltRouteTarget?.HeatSink,
        _ => _state.Display.InspectionDryRunPcb,
    };
    public int? DryRunBolt => SelectedDryRun == DryRunTarget.BoltRoute
        ? _state.Display.BoltRouteTarget?.Number : _state.Display.InspectionDryRunBolt;
    public FasteningHead? DryRunHead => _state.Display.BoltRouteTarget?.Head;
    public FasteningPass? DryRunFasteningPass => _state.Display.BoltRoutePass;
    public string? DryRunBarcode => _state.Display.InspectionDryRunBarcode;
    public bool? DryRunBoltPresent => _state.Display.InspectionDryRunBoltPresent;
    [RelayCommand(CanExecute = nameof(CanRunDryRun), IncludeCancelCommand = true)]
    private Task RunDryRunAsync(CancellationToken cancellationToken) =>
        _machine.RunDryRunAsync(SelectedDryRun, cancellationToken, SelectedDryRunHeatSink);

    private bool CanRunDryRun() => _machine.CanRunDryRun(SelectedDryRun);

    partial void OnSelectedDryRunChanged(DryRunTarget value)
    {
        OnPropertyChanged(nameof(IsInspectionDryRun));
        RefreshDryRun();
    }

    private void RefreshDryRun()
    {
        OnPropertyChanged(nameof(DryRunState));
        OnPropertyChanged(nameof(DryRunDestination));
        OnPropertyChanged(nameof(DryRunPasses));
        OnPropertyChanged(nameof(DryRunPcb));
        OnPropertyChanged(nameof(DryRunBolt));
        OnPropertyChanged(nameof(DryRunHead));
        OnPropertyChanged(nameof(DryRunFasteningPass));
        OnPropertyChanged(nameof(DryRunBarcode));
        OnPropertyChanged(nameof(DryRunBoltPresent));
        RunDryRunCommand.NotifyCanExecuteChanged();
    }

    public void Activate()
    {
        _active = true;
        _state.RequestDisplayRefresh();
        OnMachineStateChanged();
    }

    public void Deactivate()
    {
        _active = false;
        foreach (var row in Conveyors) row.ToggleCommand.Cancel();
        RunDryRunCommand.Cancel();
    }

    public Task ShutdownAsync() =>
        CommandShutdown.StopAsync(Deactivate, [.. Conveyors.Select(row => row.ToggleCommand), RunDryRunCommand]);

    private void OnMachineStateChanged()
    {
        if (!_active)
        {
            return;
        }

        if (Interlocked.Exchange(ref _stateRefreshQueued, 1) != 0)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _stateRefreshQueued, 0);
            if (!_active)
            {
                return;
            }

            RefreshDryRun();
        });
    }

}
