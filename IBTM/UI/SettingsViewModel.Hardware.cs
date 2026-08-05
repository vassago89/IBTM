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
        _conveyor.Run(Settings.ConveyorVelocity);
        StatusMessage =
            $"Conveyor running at {Settings.ConveyorVelocity:F1} mm/s";
        NotifyHardwareCommands();
    }

    private bool CanRunConveyor() =>
        MachineReady
        && SafetyReady
        && !IsHoming
        && !_state.EquipmentRunning;

    [RelayCommand]
    private void StopConveyor()
    {
        _conveyor.Stop();
        _io.SetOutput(OutputIo.ConveyorUpstreamMachineReady, false);
        _io.SetOutput(OutputIo.ConveyorDownstreamBoardAvailable, false);
        StatusMessage = "Conveyor stopped";
        NotifyHardwareCommands();
    }

    [RelayCommand]
    private void RefreshHardware()
    {
        RefreshHardwareState();
        StatusMessage = "Hardware state refreshed";
    }

    private void RefreshHardwareState()
    {
        foreach (var row in InputMappings)
        {
            row.State =
                _io.GetInput((InputIo)row.Signal) ? "ON" : "OFF";
        }

        foreach (var row in AxisMappings)
        {
            row.AxisDisplayState =
                GetAxisDisplayState(GetAxisState((MachineAxis)row.Signal));
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleServo))]
    private void ToggleServo(HardwareMappingRow row)
    {
        var machineAxis = (MachineAxis)row.Signal;
        var turnOn = !GetAxisState(machineAxis).ServoOn;

        if (machineAxis == MachineAxis.Conveyor)
        {
            _conveyor.SetServo(turnOn);
        }
        else
        {
            var (motion, axis) = GetMotionAxis(machineAxis);
            motion.SetServo(axis, turnOn);
        }

        row.AxisDisplayState =
            GetAxisDisplayState(GetAxisState(machineAxis));
        _state.Refresh();
        StatusMessage =
            $"{machineAxis} servo {(turnOn ? "on" : "off")}";
    }

    private bool CanToggleServo(HardwareMappingRow? row)
    {
        if (row?.CanServo != true || IsHoming || !EquipmentStopped)
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
        var result = $"{machineAxis} homed";
        IsHoming = true;
        try
        {
            var (motion, axis) = GetMotionAxis(machineAxis);
            var velocity = axis == MotionAxis.Z
                ? Settings.Home.ZSpeed
                : Settings.Home.HorizontalSpeed;

            StatusMessage = $"Homing {machineAxis}";
            row.AxisDisplayState = AxisDisplayState.Moving;
            if (!await motion.HomeAsync(axis, velocity, cancellationToken))
            {
                result = $"{machineAxis} homing failed";
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            result = "Homing stopped";
        }
        finally
        {
            IsHoming = false;
            RefreshHardwareState();
            _state.Refresh();
            StatusMessage = result;
        }
    }

    private bool CanHomeAxis(HardwareMappingRow? row) =>
        SafetyReady
        && !IsHoming
        && EquipmentStopped
        && row?.CanHome == true
        && GetAxisState((MachineAxis)row.Signal).ServoOn;

    [RelayCommand(CanExecute = nameof(CanStopHoming))]
    private void StopHoming()
    {
        HomeAxisCommand.Cancel();
        StopAllMotion();
    }

    private bool CanStopHoming() => IsHoming;

    public void Deactivate()
    {
        StopHoming();
        _conveyor.Stop();
    }

    private AxisState GetAxisState(MachineAxis axis)
    {
        if (axis == MachineAxis.Conveyor)
        {
            return _conveyor.GetAxisState();
        }

        var (motion, motionAxis) = GetMotionAxis(axis);
        return motion.GetAxisState(motionAxis);
    }

    private (MotionService Motion, MotionAxis Axis) GetMotionAxis(
        MachineAxis axis) =>
        axis switch
        {
            MachineAxis.PcbSupplyX =>
                (_motions[MotionGroup.PcbSupply], MotionAxis.X),
            MachineAxis.PcbSupplyZ =>
                (_motions[MotionGroup.PcbSupply], MotionAxis.Z),
            MachineAxis.PcbPlacementX =>
                (_motions[MotionGroup.PcbPlacement], MotionAxis.X),
            MachineAxis.PcbPlacementY =>
                (_motions[MotionGroup.PcbPlacement], MotionAxis.Y),
            MachineAxis.PcbPlacementZ =>
                (_motions[MotionGroup.PcbPlacement], MotionAxis.Z),
            MachineAxis.BoltFasteningX =>
                (_motions[MotionGroup.BoltFastening], MotionAxis.X),
            MachineAxis.BoltFasteningY =>
                (_motions[MotionGroup.BoltFastening], MotionAxis.Y),
            MachineAxis.BoltFasteningZ =>
                (_motions[MotionGroup.BoltFastening], MotionAxis.Z),
            MachineAxis.InspectionX =>
                (_motions[MotionGroup.Inspection], MotionAxis.X),
            MachineAxis.InspectionY =>
                (_motions[MotionGroup.Inspection], MotionAxis.Y),
            MachineAxis.InspectionZ =>
                (_motions[MotionGroup.Inspection], MotionAxis.Z),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };

    private static AxisDisplayState GetAxisDisplayState(AxisState state) =>
        state switch
        {
            { Emergency: true } => AxisDisplayState.Emergency,
            { Alarm: true } => AxisDisplayState.Alarm,
            { PositiveLimit: true } => AxisDisplayState.PositiveLimit,
            { NegativeLimit: true } => AxisDisplayState.NegativeLimit,
            { ServoOn: false } => AxisDisplayState.ServoOff,
            { InPosition: false } => AxisDisplayState.Moving,
            { Homed: false } => AxisDisplayState.HomeRequired,
            _ => AxisDisplayState.Ready,
        };

    private void StopAllMotion()
    {
        foreach (var motion in _motions.Values)
        {
            motion.Stop();
        }
    }

    private void OnEquipmentStateChanged()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(MachineReady));
            NotifyHardwareCommands();
        });
    }

    private void OnInputChanged(InputIo input, bool value)
    {
        if (!value
            && input is InputIo.EmergencyStopReleased
                or InputIo.DoorClosed
                or InputIo.AirPressureOk)
        {
            HomeAxisCommand.Cancel();
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var row = InputMappings.First(
                row => row.Signal.Equals(input));
            row.State = value ? "ON" : "OFF";
            OnPropertyChanged(nameof(SafetyReady));
            NotifyHardwareCommands();
            if (!SafetyReady)
            {
                StatusMessage = "Safety interlock is open";
            }
        });
    }

    private bool EquipmentStopped => !_state.EquipmentRunning;

    private void NotifyHardwareCommands()
    {
        RunConveyorCommand.NotifyCanExecuteChanged();
        HomeAxisCommand.NotifyCanExecuteChanged();
        ToggleServoCommand.NotifyCanExecuteChanged();
    }
}
