using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using IBTM.Core;
using IBTM.Device;

namespace IBTM.UI;

public partial class SettingsViewModel
{
    [RelayCommand(CanExecute = nameof(CanRunConveyor))]
    private void RunConveyor()
    {
        _conveyor.Run();
        NotifyHardwareCommands();
    }

    private bool CanRunConveyor() =>
        MachineReady
        && SafetyReady
        && !IsHoming
        && !_state.IsRunning;

    [RelayCommand]
    private void StopConveyor()
    {
        _conveyor.Stop();
        _conveyor.ResetSmema();
        NotifyHardwareCommands();
    }

    [RelayCommand]
    private void RefreshHardware()
    {
        RefreshHardwareState();
    }

    private void RefreshHardwareState()
    {
        foreach (var row in InputMappings)
        {
            row.Refresh();
        }

        foreach (var row in AxisMappings)
        {
            row.Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleServo))]
    private void ToggleServo(HardwareMappingRow row)
    {
        var machineAxis = (MachineAxis)row.Signal;
        var turnOn = !GetAxisState(machineAxis).ServoOn;

        var (motion, axis) = GetMotionAxis(machineAxis);
        motion.SetServo(axis, turnOn);

        row.Refresh();
        _state.Refresh();
    }

    private bool CanToggleServo(HardwareMappingRow? row)
    {
        if (row is null || IsHoming || !MachineStopped)
        {
            return false;
        }

        var axis = (MachineAxis)row.Signal;
        return GetAxisState(axis).ServoOn || SafetyReady;
    }

    [RelayCommand(CanExecute = nameof(CanHomeAxis))]
    private async Task HomeAxisAsync(
        HardwareMappingRow row,
        CancellationToken cancellationToken)
    {
        var machineAxis = (MachineAxis)row.Signal;
        IsHoming = true;
        try
        {
            var (motion, axis) = GetMotionAxis(machineAxis);
            var velocity = axis == MotionAxis.Z
                ? Settings.Home.ZSpeed
                : Settings.Home.HorizontalSpeed;

            var homing = motion.HomeAsync(
                axis,
                velocity,
                cancellationToken);
            row.Refresh();
            await homing;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsHoming = false;
            RefreshHardwareState();
            _state.Refresh();
        }
    }

    private bool CanHomeAxis(HardwareMappingRow? row) =>
        SafetyReady
        && !IsHoming
        && MachineStopped
        && row is not null
        && row.Signal is not MachineAxis.PcbSupplyX
            and not MachineAxis.PcbSupplyY
            and not MachineAxis.PcbSupplyZ
        && BufferAllowsHome((MachineAxis)row.Signal)
        && GetAxisState((MachineAxis)row.Signal).ServoOn;

    private bool BufferAllowsHome(MachineAxis axis) =>
        axis is not MachineAxis.PcbPlacementHandlerX
            and not MachineAxis.PcbPlacementHandlerY
            and not MachineAxis.PcbPlacementHandlerZ
        || !_state.SupplyInBufferArea;

    [RelayCommand(CanExecute = nameof(CanStopHoming))]
    private void StopHoming()
    {
        HomeAxisCommand.Cancel();
    }

    private bool CanStopHoming() => IsHoming;

    public void Deactivate()
    {
        StopHoming();
        _conveyor.Stop();
    }

    private AxisState GetAxisState(MachineAxis axis)
    {
        var (motion, motionAxis) = GetMotionAxis(axis);
        return motion.GetAxisState(motionAxis);
    }

    private (IAxisMotion Motion, MotionAxis Axis) GetMotionAxis(
        MachineAxis axis) =>
        axis switch
        {
            MachineAxis.PcbSupplyX =>
                (_motions[MotionGroup.PcbSupply], MotionAxis.X),
            MachineAxis.PcbSupplyY =>
                (_motions[MotionGroup.PcbSupply], MotionAxis.Y),
            MachineAxis.PcbSupplyZ =>
                (_motions[MotionGroup.PcbSupply], MotionAxis.Z),
            MachineAxis.PcbPlacementHandlerX =>
                (_motions[MotionGroup.PcbPlacementHandler], MotionAxis.X),
            MachineAxis.PcbPlacementHandlerY =>
                (_motions[MotionGroup.PcbPlacementHandler], MotionAxis.Y),
            MachineAxis.PcbPlacementHandlerZ =>
                (_motions[MotionGroup.PcbPlacementHandler], MotionAxis.Z),
            MachineAxis.BoltFasteningX =>
                (_motions[MotionGroup.BoltFastening], MotionAxis.X),
            MachineAxis.BoltFasteningY =>
                (_motions[MotionGroup.BoltFastening], MotionAxis.Y),
            MachineAxis.BoltFasteningZ =>
                (_motions[MotionGroup.BoltFastening], MotionAxis.Z),
            MachineAxis.InspectionGantryX =>
                (_motions[MotionGroup.InspectionGantry], MotionAxis.X),
            MachineAxis.InspectionGantryY =>
                (_motions[MotionGroup.InspectionGantry], MotionAxis.Y),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };

    private void OnMachineStateChanged()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(MachineReady));
            NotifyHardwareCommands();
        });
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (!_state.SafetyReady)
        {
            HomeAxisCommand.Cancel();
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var row = InputMappings.First(
                row => row.Signal.Equals(input));
            row.Refresh();
            OnPropertyChanged(nameof(SafetyReady));
        });
    }

    private bool MachineStopped => !_state.IsRunning;

    private void NotifyHardwareCommands()
    {
        RunConveyorCommand.NotifyCanExecuteChanged();
        HomeAxisCommand.NotifyCanExecuteChanged();
        ToggleServoCommand.NotifyCanExecuteChanged();
    }
}
