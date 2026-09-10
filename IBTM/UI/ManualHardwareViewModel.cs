using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class ManualHardwareViewModel : ObservableObject
{
    private readonly MachineController _machine;
    private volatile bool _active;
    private int _commandRefreshQueued;
    [ObservableProperty]
    private DryRunTarget _selectedDryRun;
    [ObservableProperty]
    private HeatSinkSlot _selectedDryRunHeatSink;

    public ManualHardwareViewModel(IoSignals signals, MachineState state, MachineController machine)
    {
        State = state;
        _machine = machine;
        Conveyors = [
            new(signals.Outputs[OutputIo.MainConveyorRun], machine),
            new(signals.Outputs[OutputIo.NgConveyorRun], machine),
        ];

        state.DisplayChanged += OnMachineStateChanged;
    }

    public MachineState State { get; }
    public ManualConveyorRow[] Conveyors { get; }
    public DryRunTarget[] DryRunTargets { get; } = Enum.GetValues<DryRunTarget>();
    public HeatSinkSlot[] DryRunHeatSinks { get; } = Enum.GetValues<HeatSinkSlot>();

    public bool IsInspectionDryRun
    {
        get
        {
            return SelectedDryRun == DryRunTarget.Inspection;
        }
    }

    public Enum DryRunState
    {
        get
        {
            return SelectedDryRun switch
            {
                DryRunTarget.MainConveyor => State.Display.MainConveyorDryRunState,
                DryRunTarget.Inspection => State.Display.InspectionDryRunState,
                DryRunTarget.PcbReturn => State.Display.PcbReturnState,
                DryRunTarget.PcbRoundTrip => State.Display.PcbDryRunState,
                DryRunTarget.NgConveyor => State.Display.NgConveyorDryRunState,
                DryRunTarget.BoltRoute => State.Display.BoltRouteState,
                _ => State.Display.NgTransferDryRunState,
            };
        }
    }

    public Enum DryRunDestination
    {
        get
        {
            return SelectedDryRun switch
            {
                DryRunTarget.MainConveyor => State.Display.MainConveyorDestination,
                DryRunTarget.Inspection => State.Display.InspectionDryRunDirection,
                DryRunTarget.PcbReturn => State.Display.PcbReturnDestination,
                DryRunTarget.PcbRoundTrip => State.Display.PcbDryRunDirection,
                DryRunTarget.NgConveyor => State.Display.NgConveyorDestination,
                DryRunTarget.BoltRoute => State.Display.BoltRouteDirection,
                _ => State.Display.NgTransferDestination,
            };
        }
    }

    public int DryRunPasses
    {
        get
        {
            return SelectedDryRun switch
            {
                DryRunTarget.MainConveyor => State.Display.MainConveyorDryRunPasses,
                DryRunTarget.Inspection => State.Display.InspectionDryRunPasses,
                DryRunTarget.PcbReturn => State.Display.PcbReturnCount,
                DryRunTarget.PcbRoundTrip => State.Display.PcbDryRunCycles,
                DryRunTarget.NgConveyor => State.Display.NgConveyorDryRunPasses,
                DryRunTarget.BoltRoute => State.Display.BoltRoutePasses,
                _ => State.Display.NgTransferDryRunTransfers,
            };
        }
    }

    public HeatSinkSlot? DryRunPcb
    {
        get
        {
            return SelectedDryRun switch
            {
                DryRunTarget.PcbReturn => State.Display.PcbReturnHeatSink,
                DryRunTarget.PcbRoundTrip
                    => State.Display.PcbDryRunDirection == PcbDryRunDirection.Ready
                        ? null
                        : State.Display.PcbDryRunHeatSink,
                DryRunTarget.BoltRoute => State.Display.BoltRouteTarget?.HeatSink,
                _ => State.Display.InspectionDryRunPcb,
            };
        }
    }

    public int? DryRunBolt
    {
        get
        {
            return SelectedDryRun == DryRunTarget.BoltRoute
                ? State.Display.BoltRouteTarget?.Number
                : State.Display.InspectionDryRunBolt;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunDryRun), IncludeCancelCommand = true)]
    private Task RunDryRunAsync(CancellationToken cancellationToken)
    {
        return _machine.RunDryRunAsync(SelectedDryRun, cancellationToken, SelectedDryRunHeatSink);
    }

    private bool CanRunDryRun()
    {
        return _machine.CanRunDryRun(SelectedDryRun);
    }

    partial void OnSelectedDryRunChanged(DryRunTarget value)
    {
        OnPropertyChanged(nameof(IsInspectionDryRun));
        RefreshDryRun();
        RunDryRunCommand.NotifyCanExecuteChanged();
    }

    private void RefreshDryRun()
    {
        OnPropertyChanged(nameof(DryRunState));
        OnPropertyChanged(nameof(DryRunDestination));
        OnPropertyChanged(nameof(DryRunPasses));
        OnPropertyChanged(nameof(DryRunPcb));
        OnPropertyChanged(nameof(DryRunBolt));
    }

    public void Activate()
    {
        _active = true;
        State.RequestDisplayRefresh();
        OnMachineStateChanged();
    }

    public void Deactivate()
    {
        _active = false;
        foreach (var row in Conveyors)
            row.RunCommand.Cancel();
        RunDryRunCommand.Cancel();
    }

    public Task ShutdownAsync()
    {
        return CommandShutdown.StopAsync(
            Deactivate,
            [.. Conveyors.Select(row => row.RunCommand), RunDryRunCommand]);
    }

    private void OnMachineStateChanged()
    {
        if (!_active)
        {
            return;
        }

        RefreshDryRun();
        if (Interlocked.Exchange(ref _commandRefreshQueued, 1) != 0)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(
            () =>
            {
                Interlocked.Exchange(ref _commandRefreshQueued, 0);
                if (!_active)
                {
                    return;
                }

                RunDryRunCommand.NotifyCanExecuteChanged();
            });
    }

}
