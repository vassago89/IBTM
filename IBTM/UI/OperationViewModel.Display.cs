using IBTM.BoltFastening;
using IBTM.Device;
using IBTM.Inspection;

namespace IBTM.UI;

public partial class OperationViewModel
{
    public MachineDisplayState MachineDisplayState
    {
        get
        {
            if (!_state.SafetyReady)
            {
                return MachineDisplayState.SafetyStop;
            }

            if (_state.IsError)
            {
                return MachineDisplayState.Alarm;
            }

            if (_state.Faulted)
            {
                return MachineDisplayState.MotionFault;
            }

            if (IsHoming)
            {
                return MachineDisplayState.Homing;
            }

            if (!_state.ServoMainContactorOn || !_state.ServosOn)
            {
                return MachineDisplayState.ServoOff;
            }

            if (!_state.Homed)
            {
                return MachineDisplayState.HomeRequired;
            }

            return _state.IsRunning
                ? MachineDisplayState.Running
                : MachineDisplayState.Ready;
        }
    }

    public bool StartBlocked =>
        !_state.IsRunning
        && !IsHoming
        && StartBlock != StartBlockReason.None;

    public bool BoltProcessStateVisible =>
        BoltFasteningProcessState != BoltFasteningProcessState.Waiting
        && (_state.AutomaticRunning
            || BoltFasteningMoving
            || BoltHead1Down
            || BoltHead2Down);

    public bool InspectionProcessStateVisible =>
        InspectionProcessState != InspectionProcessState.Waiting
        && (_state.AutomaticRunning
            || InspectionGantryMoving
            || NgCarrierDetected);

    public StartBlockReason StartBlock
    {
        get
        {
            if (_state.Alarm == MachineAlarm.EmergencyStop)
            {
                return StartBlockReason.EmergencyStop;
            }

            if (_state.Alarm == MachineAlarm.DoorOpen)
            {
                return StartBlockReason.DoorOpen;
            }

            if (_state.Alarm == MachineAlarm.AirPressureLow)
            {
                return StartBlockReason.AirPressure;
            }

            if (_state.Alarm == MachineAlarm.BufferConflict
                || _state.BufferConflict)
            {
                return StartBlockReason.BufferConflict;
            }

            if (_state.IsError)
            {
                return StartBlockReason.Alarm;
            }

            if (_options.UseEmergencyStop && !_state.EmergencyStopReleased)
            {
                return StartBlockReason.EmergencyStop;
            }

            if (_options.UseAirPressureInterlock && !_state.AirPressureOk)
            {
                return StartBlockReason.AirPressure;
            }

            if (_state.Faulted)
            {
                return StartBlockReason.MotionFault;
            }

            if (!_state.ServoMainContactorOn || !_state.ServosOn)
            {
                return StartBlockReason.ServoOff;
            }

            if (_options.UseDoorInterlock && !_state.DoorClosed)
            {
                return StartBlockReason.DoorOpen;
            }

            if (!_state.Homed)
            {
                return StartBlockReason.HomeRequired;
            }

            if (!_state.AutoMode)
            {
                return StartBlockReason.AutoMode;
            }

            return _units.HasEnabledUnit()
                ? StartBlockReason.None
                : StartBlockReason.NoUnitEnabled;
        }
    }

    public HandlerDisplayState SupplyDisplayState
    {
        get
        {
            if (!PcbSupplyEnabled)
            {
                return HandlerDisplayState.Disabled;
            }

            if (_state.Alarm == MachineAlarm.Supply)
            {
                return HandlerDisplayState.IoAlarm;
            }

            if (_pcbSupplyMotion.IsMoving)
            {
                return HandlerDisplayState.Moving;
            }

            if (PcbSupplyPcbDetected)
            {
                return HandlerDisplayState.PcbDetected;
            }

            return PcbSupplyAvailableFromFront1
                ? HandlerDisplayState.CarrierAvailable
                : HandlerDisplayState.WaitingForCarrier;
        }
    }

    public HandlerDisplayState PlacementDisplayState
    {
        get
        {
            if (!PcbPlacementEnabled)
            {
                return HandlerDisplayState.Disabled;
            }

            if (_state.Alarm == MachineAlarm.Placement)
            {
                return HandlerDisplayState.IoAlarm;
            }

            if (_pcbPlacementMotion.IsMoving)
            {
                return HandlerDisplayState.Moving;
            }

            if (PcbPlacementPcbDetected)
            {
                return HandlerDisplayState.PcbDetected;
            }

            if (PcbBufferPcbPresent)
            {
                return HandlerDisplayState.PcbAvailable;
            }

            return HandlerDisplayState.WaitingForPcb;
        }
    }

    public StationDisplayState BoltDisplayState
    {
        get
        {
            if (!BoltFasteningEnabled)
            {
                return StationDisplayState.Disabled;
            }

            if (_state.Alarm is MachineAlarm.PickupBoltFeeder
                or MachineAlarm.LinearBoltFeeder
                or MachineAlarm.BoltFastening)
            {
                return StationDisplayState.IoAlarm;
            }

            if (!BoltFasteningCarrierPresent)
            {
                return StationDisplayState.WaitingForCarrier;
            }

            if (!BoltFasteningHasHeatSink)
            {
                return StationDisplayState.EmptyCarrier;
            }

            if (_boltFasteningWork.Completed)
            {
                return _boltFasteningWork.HasNg
                    ? StationDisplayState.CarrierNg
                    : StationDisplayState.CarrierOk;
            }

            return BoltProcessStateVisible
                ? StationDisplayState.Working
                : StationDisplayState.HeatSinkDetected;
        }
    }

    public StationDisplayState InspectionDisplayState
    {
        get
        {
            if (!InspectionEnabled)
            {
                return StationDisplayState.Disabled;
            }

            if (_state.Alarm == MachineAlarm.Inspection)
            {
                return StationDisplayState.IoAlarm;
            }

            if (InspectionGantryMoving || NgCarrierDetected)
            {
                return StationDisplayState.Working;
            }

            if (!InspectionCarrierPresent)
            {
                return StationDisplayState.WaitingForCarrier;
            }

            if (!InspectionHasHeatSink)
            {
                return StationDisplayState.EmptyCarrier;
            }

            if (_inspectionWork.Completed)
            {
                return _inspectionWork.HasNg
                    ? StationDisplayState.CarrierNg
                    : StationDisplayState.CarrierOk;
            }

            return InspectionProcessStateVisible
                ? StationDisplayState.Working
                : StationDisplayState.HeatSinkDetected;
        }
    }

}
