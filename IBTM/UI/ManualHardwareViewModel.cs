using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class ManualHardwareViewModel : ObservableObject
{
    private readonly MachineController _machine;
    private volatile bool _active;
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

    public DryRunDisplay? DryRun
    {
        get
        {
            return State.Display.DryRuns.GetValueOrDefault(SelectedDryRun);
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private Task RunDryRunAsync(CancellationToken cancellationToken)
    {
        return _machine.RunDryRunAsync(SelectedDryRun, cancellationToken, SelectedDryRunHeatSink);
    }

    public bool CanRunDryRun
    {
        get
        {
            return State.Display.ManualControlsEnabled && DryRun?.Ready == true;
        }
    }

    partial void OnSelectedDryRunChanged(DryRunTarget value)
    {
        OnPropertyChanged(nameof(IsInspectionDryRun));
        RefreshDryRun();
    }

    private void RefreshDryRun()
    {
        OnPropertyChanged(nameof(CanRunDryRun));
        OnPropertyChanged(nameof(DryRun));
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
        if (_active)
            RefreshDryRun();
    }
}
