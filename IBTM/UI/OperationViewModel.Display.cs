using System;
using IBTM.Device;

namespace IBTM.UI;

public partial class OperationViewModel
{
    public Enum MachineStatus =>
        _state.IsError ? _state.Alarm : MachineDisplayState;

    public MachineDisplayState MachineDisplayState
    {
        get
        {
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

            if (!_state.ServosOn)
            {
                return MachineDisplayState.ServoOff;
            }

            if (!_state.Homed)
            {
                return MachineDisplayState.HomeRequired;
            }

            if (_state.ManualMode)
            {
                return MachineDisplayState.ManualMode;
            }

            return _state.IsRunning
                ? MachineDisplayState.Running
                : MachineDisplayState.Ready;
        }
    }

    public HandlerDisplayState SupplyDisplayState
    {
        get
        {
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

            return HandlerDisplayState.WaitingForPcb;
        }
    }

    public StationDisplayState BoltDisplayState =>
        _state.Alarm is MachineAlarm.PickupBoltFeeder
            or MachineAlarm.LinearBoltFeeder
            or MachineAlarm.BoltFastening
            ? StationDisplayState.IoAlarm
            : BoltFasteningMoving || BoltHead1Down || BoltHead2Down
                ? StationDisplayState.Working
                : BoltFasteningHasHousing
                    ? StationDisplayState.HousingDetected
                    : StationDisplayState.NoHousing;

    public StationDisplayState InspectionDisplayState =>
        _state.Alarm == MachineAlarm.Inspection
            ? StationDisplayState.IoAlarm
            : InspectionGantryMoving
                ? StationDisplayState.Working
                : _inspectionWork.Completed
                    ? _inspectionWork.HasNg
                        ? StationDisplayState.CarrierNg
                        : StationDisplayState.CarrierOk
                    : InspectionHasHousing
                        ? StationDisplayState.HousingDetected
                        : StationDisplayState.NoHousing;

    private double MapAxis(
        double position,
        MachineAxis axis,
        double start,
        double end)
    {
        var hardware = _axes[axis];
        var minimum = hardware.Minimum;
        var maximum = hardware.Maximum;
        var ratio = Math.Clamp(
            (position - minimum) / (maximum - minimum),
            0,
            1);
        return start + (ratio * (end - start));
    }

}
