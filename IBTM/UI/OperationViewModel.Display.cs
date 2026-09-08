using System;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;

namespace IBTM.UI;

public partial class OperationViewModel
{
    public bool SupplyPositionKnown => Supply.Motion.XyHomed && _map.SupplyDefined;
    public bool PlacementPositionKnown => Placement.Motion.XyHomed && _map.PlacementDefined;
    public bool FasteningPositionKnown => Fastening.Motion.XyHomed && _map.FasteningDefined;
    public bool InspectionPositionKnown => InspectionGantry.Motion.XyHomed && _map.InspectionDefined;
    public bool BoltFeederPositionKnown => _map.FasteningDefined;

    public Enum PlacementStatus => PlacementDisplayState is HandlerDisplayState.Working or HandlerDisplayState.Moving
        ? PlacementState : PlacementDisplayState;
    public Enum ConveyorStatus => !MainConveyorEnabled ? HandlerDisplayState.Disabled
        : !_machineDisplay.AutomaticRunning && !ConveyorRunning ? HandlerDisplayState.Stopped
        : MainConveyorState;

    public MachineDisplayState MachineDisplayState => _machineDisplay switch
    {
        { Available: false } => MachineDisplayState.Unavailable,
        { SafetyReady: false } => MachineDisplayState.SafetyStop,
        { Alarm: not MachineAlarm.None } => MachineDisplayState.Alarm,
        { MotionFaulted: true } => MachineDisplayState.MotionFault,
        { IsHoming: true } => MachineDisplayState.Homing,
        { ServoPowerOn: false } => MachineDisplayState.ServoOff,
        { Homed: false } => MachineDisplayState.HomeRequired,
        { IsRunning: true } => MachineDisplayState.Running,
        _ => MachineDisplayState.Ready,
    };

    public bool StartBlocked =>
        !_machineDisplay.CanStart
        && !_machineDisplay.IsHoming
        && _machineDisplay.StartBlock != StartBlockReason.None;

    public bool FasteningStateVisible =>
        _machineDisplay.AutomaticRunning
        && FasteningState != BoltFasteningState.Waiting;

    public bool InspectionStateVisible =>
        _machineDisplay.AutomaticRunning
        && InspectionState != InspectionStationState.Waiting;

    public HomeBlockReason HomeBlock => _machineDisplay.HomeBlock;
    public Enum StartBlock =>
        _machineDisplay.StartBlock == StartBlockReason.HomeRequired
            && HomeBlock != HomeBlockReason.None
                ? HomeBlock : _machineDisplay.StartBlock;

    public HandlerDisplayState SupplyDisplayState
    {
        get
        {
            if (!PcbSupplyEnabled)
            {
                return HandlerDisplayState.Disabled;
            }

            if (Alarm is MachineAlarm.PcbSupply)
            {
                return HandlerDisplayState.IoAlarm;
            }

            if (!SupplyPositionKnown) return HandlerDisplayState.PositionUnknown;
            if (Supply.Motion.IsMoving)
            {
                return HandlerDisplayState.Moving;
            }

            if (!_machineDisplay.AutomaticRunning) return HandlerDisplayState.Stopped;

            if (_machineDisplay.SupplyAtHandoff)
            {
                return _buffer.PcbPresent ? HandlerDisplayState.WaitingForPlacement
                    : HandlerDisplayState.WaitingForBufferPcb;
            }

            if (PcbSupplyPcbSecured && !_machineDisplay.CanSupplyEnter)
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

            if (Alarm is MachineAlarm.PcbPlacement)
            {
                return HandlerDisplayState.IoAlarm;
            }

            if (!PlacementPositionKnown) return HandlerDisplayState.PositionUnknown;
            if (Placement.Motion.IsMoving)
            {
                return HandlerDisplayState.Moving;
            }

            if (!_machineDisplay.AutomaticRunning) return HandlerDisplayState.Stopped;

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

            if (Fastening.Motion.IsMoving || _state.BoltTestRunning)
                return StationDisplayState.Working;
            if (!_machineDisplay.AutomaticRunning) return StationDisplayState.Stopped;

            if (!BoltFasteningCarrierPresent)
            {
                return StationDisplayState.WaitingForCarrier;
            }

            if (_boltFasteningWork.Completed)
            {
                return StationDisplayState.WaitingForTransfer;
            }

            if (!BoltFasteningHeatSink1Present
                && !BoltFasteningHeatSink2Present)
            {
                return StationDisplayState.EmptyCarrier;
            }

            return FasteningStateVisible
                ? StationDisplayState.Working
                : StationDisplayState.HeatSinkDetected;
        }
    }

    public Enum InspectionStatus => InspectionDisplayState switch
    {
        StationDisplayState.Working when InspectionStateVisible => InspectionState,
        StationDisplayState.WaitingForTransfer when _units.NgCarrierTransfer && _inspectionWork.RouteToNg
            => InspectionStationState.WaitingForShuttleReady,
        StationDisplayState.WaitingForTransfer when _units.MainConveyor
            && MainConveyorState == MainConveyorState.WaitingForRearEquipment => MainConveyorState,
        _ => InspectionDisplayState,
    };

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

            if (!_machineDisplay.AutomaticRunning && !InspectionGantry.Motion.IsMoving)
                return StationDisplayState.Stopped;

            if (InspectionGantry.Motion.IsMoving
                || NgCarrierDetected
                || InspectionTransferWorking)
            {
                return StationDisplayState.Working;
            }

            if (!InspectionCarrierPresent)
            {
                return StationDisplayState.WaitingForCarrier;
            }

            if (_inspectionWork.Completed)
            {
                return StationDisplayState.WaitingForTransfer;
            }

            if (!InspectionHeatSink1Present
                && !InspectionHeatSink2Present)
            {
                return StationDisplayState.EmptyCarrier;
            }

            return InspectionStateVisible
                ? StationDisplayState.Working
                : StationDisplayState.HeatSinkDetected;
        }
    }

    private bool InspectionTransferWorking => InspectionState is
        InspectionStationState.MovingTransferToCarrier
        or InspectionStationState.LoweringTransferAtCarrier
        or InspectionStationState.ClosingTransferGripper
        or InspectionStationState.WaitingForCarrierGrip
        or InspectionStationState.RaisingCarrierTransfer
        or InspectionStationState.MovingTransferToShuttle
        or InspectionStationState.LoweringTransferAtShuttle
        or InspectionStationState.OpeningTransferGripper
        or InspectionStationState.WaitingForShuttleCarrier;

}
