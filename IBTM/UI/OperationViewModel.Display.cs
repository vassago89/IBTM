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
    public bool SupplyPositionKnown
    {
        get
        {
            return Supply.Motion.XyHomed && _map.SupplyDefined;
        }
    }

    public bool PlacementPositionKnown
    {
        get
        {
            return Placement.Motion.XyHomed && _map.PlacementDefined;
        }
    }

    public bool FasteningPositionKnown
    {
        get
        {
            return Fastening.Motion.XyHomed && _map.FasteningDefined;
        }
    }

    public bool InspectionPositionKnown
    {
        get
        {
            return InspectionGantry.Motion.XyHomed && _map.InspectionDefined;
        }
    }

    public bool BoltFeederPositionKnown
    {
        get
        {
            return _map.FasteningDefined;
        }
    }

    public Enum PlacementStatus
    {
        get
        {
            return PlacementDisplayState is HandlerDisplayState.Working or HandlerDisplayState.Moving
                ? _state.Display.PlacementState
                : PlacementDisplayState;
        }
    }

    public Enum ConveyorStatus
    {
        get
        {
            return !MainConveyorEnabled
                ? HandlerDisplayState.Disabled
                : !_state.Display.AutomaticRunning && !_state.Display.ConveyorRunning
                    ? HandlerDisplayState.Stopped
                    : _state.Display.ConveyorState;
        }
    }

    public MachineDisplayState MachineDisplayState
    {
        get
        {
            return _state.Display switch
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
        }
    }

    public bool StartBlocked
    {
        get
        {
            return !_state.Display.CanStart
                && !_state.Display.IsHoming
                && _state.Display.StartBlock != StartBlockReason.None;
        }
    }

    public bool FasteningStateVisible
    {
        get
        {
            return _state.Display.AutomaticRunning
                && _state.Display.FasteningState != BoltFasteningState.Waiting;
        }
    }

    public bool InspectionStateVisible
    {
        get
        {
            return _state.Display.AutomaticRunning
                && _state.Display.InspectionState != InspectionStationState.Waiting;
        }
    }

    public Enum StartBlock
    {
        get
        {
            return _state.Display.StartBlock == StartBlockReason.HomeRequired
                && _state.Display.HomeBlock != HomeBlockReason.None
                ? _state.Display.HomeBlock
                : _state.Display.StartBlock;
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

            if (Alarm is MachineAlarm.PcbSupply)
            {
                return HandlerDisplayState.IoAlarm;
            }

            if (!SupplyPositionKnown)
                return HandlerDisplayState.PositionUnknown;
            if (Supply.Motion.IsMoving)
            {
                return HandlerDisplayState.Moving;
            }

            if (!_state.Display.AutomaticRunning)
                return HandlerDisplayState.Stopped;

            if (_state.Display.SupplyAtHandoff)
            {
                return _buffer.PcbPresent
                    ? HandlerDisplayState.WaitingForPlacement
                    : HandlerDisplayState.WaitingForBufferPcb;
            }

            if (PcbSupplyPcbSecured && !_state.Display.CanSupplyEnter)
                return HandlerDisplayState.WaitingForBuffer;
            if (PcbSupplyPcbDetected)
                return HandlerDisplayState.Working;

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

            if (!PlacementPositionKnown)
                return HandlerDisplayState.PositionUnknown;
            if (Placement.Motion.IsMoving)
            {
                return HandlerDisplayState.Moving;
            }

            if (!_state.Display.AutomaticRunning)
                return HandlerDisplayState.Stopped;

            return _state.Display.PlacementState switch
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

            if (!FasteningPositionKnown)
                return StationDisplayState.PositionUnknown;

            if (Fastening.Motion.IsMoving || _state.BoltTestRunning)
                return StationDisplayState.Working;
            if (!_state.Display.AutomaticRunning)
                return StationDisplayState.Stopped;

            if (!BoltFasteningCarrierPresent)
            {
                return StationDisplayState.WaitingForCarrier;
            }

            if (_boltFasteningWork.Completed)
            {
                return StationDisplayState.WaitingForTransfer;
            }

            if (!BoltFasteningHeatSink1Present && !BoltFasteningHeatSink2Present)
            {
                return StationDisplayState.EmptyCarrier;
            }

            return FasteningStateVisible
                ? StationDisplayState.Working
                : StationDisplayState.HeatSinkDetected;
        }
    }

    public Enum InspectionStatus
    {
        get
        {
            return InspectionDisplayState switch
            {
                StationDisplayState.Working when InspectionStateVisible => _state.Display.InspectionState,
                StationDisplayState.WaitingForTransfer
                    when _units.NgCarrierTransfer && _inspectionWork.RouteToNg
                    => InspectionStationState.WaitingForShuttleReady,
                StationDisplayState.WaitingForTransfer
                    when _units.MainConveyor
                        && _state.Display.ConveyorState == MainConveyorState.WaitingForRearEquipment
                    => _state.Display.ConveyorState,
                _ => InspectionDisplayState,
            };
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

            if (!InspectionPositionKnown)
                return StationDisplayState.PositionUnknown;

            if (!_state.Display.AutomaticRunning && !InspectionGantry.Motion.IsMoving)
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

            if (!InspectionHeatSink1Present && !InspectionHeatSink2Present)
            {
                return StationDisplayState.EmptyCarrier;
            }

            return InspectionStateVisible
                ? StationDisplayState.Working
                : StationDisplayState.HeatSinkDetected;
        }
    }

    private bool InspectionTransferWorking
    {
        get
        {
            return _state.Display.InspectionState is InspectionStationState.MovingTransferToCarrier
                or InspectionStationState.LoweringTransferAtCarrier
                or InspectionStationState.ClosingTransferGripper
                or InspectionStationState.WaitingForCarrierGrip
                or InspectionStationState.RaisingCarrierTransfer
                or InspectionStationState.MovingTransferToShuttle
                or InspectionStationState.LoweringTransferAtShuttle
                or InspectionStationState.OpeningTransferGripper
                or InspectionStationState.WaitingForShuttleCarrier;
        }
    }

}
