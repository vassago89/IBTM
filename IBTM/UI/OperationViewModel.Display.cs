using IBTM.BoltFastening;
using IBTM.Device;
using IBTM.Inspection;

namespace IBTM.UI;

public partial class OperationViewModel
{
    public MachineDisplayState MachineDisplayState =>
        _machineDisplay.DisplayState;

    public bool StartBlocked =>
        !_machineDisplay.CanStart
        && !_machineDisplay.IsHoming
        && StartBlock != StartBlockReason.None;

    public bool BoltProcessStateVisible =>
        BoltFasteningProcessState != BoltFasteningProcessState.Waiting
        && (_machineDisplay.AutomaticRunning
            || Fastening.Motion.IsMoving
            || PickupHeadDown
            || ShootingHeadDown);

    public bool InspectionProcessStateVisible =>
        InspectionProcessState != InspectionProcessState.Waiting
        && (_machineDisplay.AutomaticRunning
            || InspectionGantry.Motion.IsMoving
            || NgCarrierDetected);

    public StartBlockReason StartBlock => _machineDisplay.StartBlock;

    public HandlerDisplayState SupplyDisplayState
    {
        get
        {
            if (!PcbSupplyEnabled)
            {
                return HandlerDisplayState.Disabled;
            }

            if (Alarm == MachineAlarm.PcbSupply)
            {
                return HandlerDisplayState.IoAlarm;
            }

            if (Supply.Motion.IsMoving)
            {
                return HandlerDisplayState.Moving;
            }

            if (PcbSupplyPcbDetected)
            {
                return HandlerDisplayState.PcbDetected;
            }

            return Supply.UpstreamCarrierAvailable
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

            if (Alarm == MachineAlarm.PcbPlacement)
            {
                return HandlerDisplayState.IoAlarm;
            }

            if (Placement.Motion.IsMoving)
            {
                return HandlerDisplayState.Moving;
            }

            if (PcbPlacementPcbDetected)
            {
                return HandlerDisplayState.PcbDetected;
            }

            if (PcbBufferPcbPresent)
            {
                return HandlerDisplayState.BufferPcbAvailable;
            }

            return HandlerDisplayState.WaitingForBufferPcb;
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

            if (Alarm is MachineAlarm.PickupBoltFeeder
                or MachineAlarm.ShootingBoltFeeder
                or MachineAlarm.BoltFastening)
            {
                return StationDisplayState.IoAlarm;
            }

            if (!BoltFasteningCarrierPresent)
            {
                return StationDisplayState.WaitingForCarrier;
            }

            if (!BoltFasteningHeatSink1Present
                && !BoltFasteningHeatSink2Present)
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
            if (!_units.Inspection)
            {
                return StationDisplayState.Disabled;
            }

            if (Alarm == MachineAlarm.Inspection)
            {
                return StationDisplayState.IoAlarm;
            }

            if (InspectionGantry.Motion.IsMoving || NgCarrierDetected)
            {
                return StationDisplayState.Working;
            }

            if (!InspectionCarrierPresent)
            {
                return StationDisplayState.WaitingForCarrier;
            }

            if (!InspectionHeatSink1Present
                && !InspectionHeatSink2Present)
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
