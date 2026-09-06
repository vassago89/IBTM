using System;
using IBTM.BoltFastening;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;

namespace IBTM.UI;

public partial class OperationViewModel
{
    public bool SupplyPositionKnown => XyHomed(Supply.Feedback) && SupplyMapDefined;
    public bool PlacementPositionKnown => XyHomed(Placement.Feedback) && PlacementMapDefined;
    public bool FasteningPositionKnown => XyHomed(Fastening.Feedback) && FasteningMapDefined;
    public bool InspectionPositionKnown => XyHomed(InspectionGantry.Feedback) && InspectionMapDefined;
    public bool BoltFeederPositionKnown => FasteningMapDefined;

    private static bool XyHomed(IMotionFeedback motion) =>
        motion.GetAxisState(MotionAxis.X).Homed && motion.GetAxisState(MotionAxis.Y).Homed;

    private bool SupplyMapDefined => _map.SupplyDefined;
    private bool PlacementMapDefined => _map.PlacementDefined;
    private bool FasteningMapDefined => _map.FasteningDefined;
    private bool InspectionMapDefined => _map.InspectionDefined;

    public Enum PlacementStatus => PlacementDisplayState is HandlerDisplayState.Working or HandlerDisplayState.Moving
        ? PlacementState : PlacementDisplayState;
    public Enum ConveyorStatus => !MainConveyorEnabled ? HandlerDisplayState.Disabled
        : !_machineDisplay.AutomaticRunning && !ConveyorRunning ? HandlerDisplayState.Stopped
        : MainConveyorState;

    public MachineDisplayState MachineDisplayState =>
        _machineDisplay.DisplayState;

    public bool StartBlocked =>
        !_machineDisplay.CanStart
        && !_machineDisplay.IsHoming
        && StartBlock != StartBlockReason.None;

    public bool FasteningStateVisible =>
        FasteningState != BoltFasteningState.Waiting
        && (_machineDisplay.AutomaticRunning
            || Fastening.Motion.IsMoving
            || PickupHeadDown
            || ShootingHeadDown);

    public bool InspectionStateVisible =>
        InspectionState != InspectionStationState.Waiting
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

            if (!SupplyPositionKnown) return HandlerDisplayState.PositionUnknown;
            if (!_machineDisplay.AutomaticRunning) return HandlerDisplayState.Stopped;

            if (Supply.Motion.IsMoving)
            {
                return HandlerDisplayState.Moving;
            }

            if (_buffer.SupplyAtHandoff)
            {
                return _buffer.PcbPresent ? HandlerDisplayState.WaitingForPlacement
                    : HandlerDisplayState.WaitingForBufferPcb;
            }

            if (PcbSupplyPcbSecured && !_buffer.CanSupplyEnter)
                return HandlerDisplayState.WaitingForBuffer;
            if (PcbSupplyPcbDetected) return HandlerDisplayState.Working;

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

            if (!PlacementPositionKnown) return HandlerDisplayState.PositionUnknown;
            if (!_machineDisplay.AutomaticRunning) return HandlerDisplayState.Stopped;

            if (Placement.Motion.IsMoving)
            {
                return HandlerDisplayState.Moving;
            }

            return PlacementState switch
            {
                PcbPlacementState.WaitingForBufferPcb => HandlerDisplayState.WaitingForBufferPcb,
                PcbPlacementState.WaitingForCarrier => HandlerDisplayState.WaitingForMainCarrier,
                PcbPlacementState.WaitingForSupplyExit => HandlerDisplayState.WaitingForSupply,
                _ => HandlerDisplayState.Working,
            };
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

            if (!FasteningPositionKnown) return StationDisplayState.PositionUnknown;

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
                return StationDisplayState.WaitingForTransfer;
            }

            return FasteningStateVisible
                ? StationDisplayState.Working
                : StationDisplayState.HeatSinkDetected;
        }
    }

    public StationDisplayState InspectionDisplayState
    {
        get
        {
            if (!_units.Inspection && !_units.NgCarrierTransfer)
            {
                return StationDisplayState.Disabled;
            }

            if (Alarm is MachineAlarm.Inspection or MachineAlarm.NgCarrierTransfer)
            {
                return StationDisplayState.IoAlarm;
            }

            if (!InspectionPositionKnown) return StationDisplayState.PositionUnknown;

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
                return StationDisplayState.WaitingForTransfer;
            }

            return InspectionStateVisible
                ? StationDisplayState.Working
                : StationDisplayState.HeatSinkDetected;
        }
    }

}
