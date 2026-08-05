using System;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbSupply;

namespace IBTM.UI;

public partial class ProcessViewModel
{
    private void UpdateDisplayStates()
    {
        if (IsError)
        {
            MachineDisplayState = MachineDisplayState.Alarm;
            MachineTone = DisplayTone.Alarm;
        }
        else if (_state.Faulted)
        {
            MachineDisplayState = MachineDisplayState.MotionFault;
            MachineTone = DisplayTone.Alarm;
        }
        else if (IsHoming)
        {
            MachineDisplayState = MachineDisplayState.Homing;
            MachineTone = DisplayTone.Active;
        }
        else if (!_state.Homed)
        {
            MachineDisplayState = MachineDisplayState.HomeRequired;
            MachineTone = DisplayTone.Waiting;
        }
        else if (!_state.ServosOn)
        {
            MachineDisplayState = MachineDisplayState.ServoOff;
            MachineTone = DisplayTone.Waiting;
        }
        else if (NgCarrierFull)
        {
            MachineDisplayState = MachineDisplayState.NgCapacityReached;
            MachineTone = DisplayTone.Alarm;
        }
        else if (BufferRecoveryRequired)
        {
            MachineDisplayState = MachineDisplayState.RecoveryRequired;
            MachineTone = DisplayTone.Waiting;
        }
        else if (_state.EquipmentRunning)
        {
            MachineDisplayState = MachineDisplayState.Running;
            MachineTone = DisplayTone.Active;
        }
        else
        {
            MachineDisplayState = MachineDisplayState.Ready;
            MachineTone = DisplayTone.Ready;
        }

        if (_state.Alarm == EquipmentAlarm.Supply)
        {
            SupplyDisplayState = HandlerDisplayState.IoAlarm;
            SupplyTone = DisplayTone.Alarm;
        }
        else if (BufferOwner == BufferOwner.Supply && BufferRecoveryRequired)
        {
            SupplyDisplayState = HandlerDisplayState.RecoveryRequired;
            SupplyTone = DisplayTone.Waiting;
        }
        else if (PcbSupplyActive)
        {
            SupplyDisplayState = HandlerDisplayState.Moving;
            SupplyTone = DisplayTone.Active;
        }
        else if (_state.SupplyPcbPresent)
        {
            SupplyDisplayState = HandlerDisplayState.HoldingPcb;
            SupplyTone = DisplayTone.Active;
        }
        else if (_pcbBuffer.PcbPresent)
        {
            SupplyDisplayState = HandlerDisplayState.PcbOnBuffer;
            SupplyTone = DisplayTone.Waiting;
        }
        else if (_state.SupplyCarrierAvailable)
        {
            SupplyDisplayState = HandlerDisplayState.CarrierAvailable;
            SupplyTone = DisplayTone.Ready;
        }
        else
        {
            SupplyDisplayState = HandlerDisplayState.WaitingForCarrier;
            SupplyTone = DisplayTone.Neutral;
        }

        if (_state.Alarm == EquipmentAlarm.Placement)
        {
            PlacementDisplayState = HandlerDisplayState.IoAlarm;
            PlacementTone = DisplayTone.Alarm;
        }
        else if (BufferOwner == BufferOwner.Placement && BufferRecoveryRequired)
        {
            PlacementDisplayState = HandlerDisplayState.RecoveryRequired;
            PlacementTone = DisplayTone.Waiting;
        }
        else if (PcbPlacementActive)
        {
            PlacementDisplayState = HandlerDisplayState.Moving;
            PlacementTone = DisplayTone.Active;
        }
        else if (_state.PlacementPcbPresent)
        {
            PlacementDisplayState = HandlerDisplayState.HoldingPcb;
            PlacementTone = DisplayTone.Active;
        }
        else if (_pcbBuffer.PcbPresent)
        {
            PlacementDisplayState = HandlerDisplayState.PcbOnBuffer;
            PlacementTone = DisplayTone.Waiting;
        }
        else if (PcbPlacementOccupied)
        {
            PlacementDisplayState = HandlerDisplayState.HousingDetected;
            PlacementTone = DisplayTone.Ready;
        }
        else
        {
            PlacementDisplayState = HandlerDisplayState.WaitingForPcb;
            PlacementTone = DisplayTone.Neutral;
        }

        if (BoltFasteningMoving)
        {
            BoltDisplayState = StationDisplayState.Working;
            BoltTone = DisplayTone.Active;
        }
        else if (_state.BoltCarrierJigPresent)
        {
            BoltDisplayState = StationDisplayState.CarrierJigReady;
            BoltTone = DisplayTone.Ready;
        }
        else
        {
            BoltDisplayState = StationDisplayState.Empty;
            BoltTone = DisplayTone.Neutral;
        }

        if (NgTransferMoving)
        {
            InspectionDisplayState = StationDisplayState.Working;
            InspectionTone = DisplayTone.Active;
        }
        else if (_state.InspectionCarrierJigPresent)
        {
            InspectionDisplayState = StationDisplayState.CarrierJigReady;
            InspectionTone = DisplayTone.Ready;
        }
        else
        {
            InspectionDisplayState = StationDisplayState.Empty;
            InspectionTone = DisplayTone.Neutral;
        }

        if (NgCarrierFull)
        {
            NgHandlingDisplayState = NgHandlingDisplayState.Full;
            NgHandlingTone = DisplayTone.Alarm;
        }
        else if (NgCarrierCount > 0)
        {
            NgHandlingDisplayState = NgHandlingDisplayState.Occupied;
            NgHandlingTone = DisplayTone.Waiting;
        }
        else
        {
            NgHandlingDisplayState = NgHandlingDisplayState.Empty;
            NgHandlingTone = DisplayTone.Neutral;
        }

        OperatorMessage = GetOperatorMessage();
        NotifyDerivedState();
    }

    private void NotifyDerivedState()
    {
        OnPropertyChanged(nameof(BufferRecoveryRequired));
        OnPropertyChanged(nameof(BufferConflict));
        OnPropertyChanged(nameof(PcbSupplyActive));
        OnPropertyChanged(nameof(PcbPlacementActive));
    }

    private OperatorMessage GetOperatorMessage()
    {
        if (_settings.Options.UseEmergencyStop && !_state.EmergencyStopReleased)
        {
            return OperatorMessage.EmergencyStop;
        }

        if (_settings.Options.UseDoorInterlock && !_state.DoorClosed)
        {
            return OperatorMessage.DoorOpen;
        }

        if (_settings.Options.UseAirPressureInterlock && !_state.AirPressureOk)
        {
            return OperatorMessage.AirPressureLow;
        }

        if (_state.Faulted)
        {
            return OperatorMessage.ResetMotionAlarm;
        }

        if (_state.Alarm == EquipmentAlarm.EmergencyStop)
        {
            return OperatorMessage.EmergencyStop;
        }

        if (IsHoming)
        {
            return OperatorMessage.HomingAxes;
        }

        if (_state.Alarm == EquipmentAlarm.Supply)
        {
            return OperatorMessage.SupplyIoTimeout;
        }

        if (_state.Alarm == EquipmentAlarm.Placement)
        {
            return OperatorMessage.PlacementIoTimeout;
        }

        if (IsError)
        {
            return OperatorMessage.ResetMachineAlarm;
        }

        if (!_state.Homed)
        {
            return OperatorMessage.HomeAllAxes;
        }

        if (!_state.ServosOn)
        {
            return OperatorMessage.EnableServos;
        }

        if (NgCarrierFull)
        {
            return OperatorMessage.ClearNgCarrier;
        }

        if (BufferRecoveryRequired)
        {
            return OperatorMessage.RecoverBuffer;
        }

        if (ConveyorRunning)
        {
            return OperatorMessage.CarrierJigMoving;
        }

        if (BoltFasteningMoving)
        {
            return OperatorMessage.BoltFastening;
        }

        if (NgTransferMoving)
        {
            return OperatorMessage.Inspection;
        }

        if (BufferOwner == BufferOwner.Placement || PcbPlacementMoving)
        {
            return OperatorMessage.PlacementMoving;
        }

        if (BufferOwner == BufferOwner.Supply || PcbSupplyMoving)
        {
            return OperatorMessage.SupplyMoving;
        }

        if (_pcbBuffer.PcbPresent)
        {
            return OperatorMessage.PcbOnBuffer;
        }

        if (_state.PlacementPcbPresent)
        {
            return OperatorMessage.PcbReadyForAlignment;
        }

        if (_state.SupplyPcbPresent)
        {
            return _state.SupplyRotation == PcbSupplyRotation.Rotated
                ? OperatorMessage.PcbReadyForBuffer
                : OperatorMessage.RotateSupplyHandler;
        }

        return _state.SupplyCarrierAvailable
            ? OperatorMessage.UpstreamCarrierAvailable
            : OperatorMessage.ReadyForOperation;
    }

    private void UpdateSupplyMap((double X, double Y, double Z) position)
        => PcbSupplyMapLeft =
            MapAxis(position.X, MachineAxis.PcbSupplyX, 220, 105);

    private void UpdatePlacementMap(
        (double X, double Y, double Z) position)
    {
        PcbPlacementMapLeft = MapAxis(
            position.X,
            MachineAxis.PcbPlacementX,
            455,
            290);
        PcbPlacementMapTop = MapAxis(
            position.Y,
            MachineAxis.PcbPlacementY,
            86,
            306);
    }

    private double MapAxis(
        double position,
        MachineAxis axis,
        double start,
        double end)
    {
        var minimum = _settings.Hardware.AxisMinimums[axis];
        var maximum = _settings.Hardware.AxisMaximums[axis];
        var ratio = Math.Clamp(
            (position - minimum) / (maximum - minimum),
            0,
            1);
        return start + (ratio * (end - start));
    }

    private void UpdateBoltMap((double X, double Y, double Z) position)
    {
        BoltFasteningMapLeft =
            MapAxis(position.X, MachineAxis.BoltFasteningX, 17, 342);
        BoltFasteningMapTop =
            MapAxis(position.Y, MachineAxis.BoltFasteningY, 74, 284);
    }

    private void UpdateNgTransferMap(
        (double X, double Y, double Z) position)
    {
        NgTransferMapLeft =
            MapAxis(position.X, MachineAxis.InspectionX, -6, 184);
        NgTransferMapTop =
            MapAxis(position.Y, MachineAxis.InspectionY, 75, 255);
    }
}
