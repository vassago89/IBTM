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
                ? State.Display.PlacementState
                : PlacementDisplayState;
        }
    }

    public Enum ConveyorStatus
    {
        get
        {
            return !Units.MainConveyor
                ? HandlerDisplayState.Disabled
                : !State.Display.AutomaticRunning && !State.Display.ConveyorRunning
                    ? HandlerDisplayState.Stopped
                    : State.Display.ConveyorState;
        }
    }

    public MachineDisplayState MachineDisplayState
    {
        get
        {
            return State.Display switch
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
            return !State.Display.CanStart
                && !State.Display.IsHoming
                && State.Display.StartBlock != StartBlockReason.None;
        }
    }

    public bool FasteningStateVisible
    {
        get
        {
            return State.Display.AutomaticRunning
                && State.Display.FasteningState != BoltFasteningState.Waiting;
        }
    }

    public bool InspectionStateVisible
    {
        get
        {
            return State.Display.AutomaticRunning
                && State.Display.InspectionState != InspectionStationState.Waiting;
        }
    }

    public Enum StartBlock
    {
        get
        {
            return State.Display.StartBlock == StartBlockReason.HomeRequired
                && State.Display.HomeBlock != HomeBlockReason.None
                ? State.Display.HomeBlock
                : State.Display.StartBlock;
        }
    }

    public HandlerDisplayState SupplyDisplayState
    {
        get
        {
            if (!Units.PcbSupply)
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

            if (!State.Display.AutomaticRunning)
                return HandlerDisplayState.Stopped;

            if (State.Display.SupplyAtHandoff)
            {
                return _buffer.PcbPresent
                    ? HandlerDisplayState.WaitingForPlacement
                    : HandlerDisplayState.WaitingForBufferPcb;
            }

            if (PcbSupplyPcbSecured && !State.Display.CanSupplyEnter)
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
            if (!Units.PcbPlacement)
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

            if (!State.Display.AutomaticRunning)
                return HandlerDisplayState.Stopped;

            return State.Display.PlacementState switch
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
            if (!Units.BoltFastening)
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

            if (Fastening.Motion.IsMoving || State.BoltTestRunning)
                return StationDisplayState.Working;
            if (!State.Display.AutomaticRunning)
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
                StationDisplayState.Working when InspectionStateVisible => State.Display.InspectionState,
                StationDisplayState.WaitingForTransfer
                    when Units.NgCarrierTransfer && _inspectionWork.RouteToNg
                    => InspectionStationState.WaitingForShuttleReady,
                StationDisplayState.WaitingForTransfer
                    when Units.MainConveyor
                        && State.Display.ConveyorState == MainConveyorState.WaitingForRearEquipment
                    => State.Display.ConveyorState,
                _ => InspectionDisplayState,
            };
        }
    }

    public StationDisplayState InspectionDisplayState
    {
        get
        {
            if (!Units.Inspection && !Units.NgCarrierTransfer)
            {
                return StationDisplayState.Disabled;
            }

            if (Alarm is MachineAlarm.Inspection or MachineAlarm.NgCarrierTransfer)
            {
                return StationDisplayState.IoAlarm;
            }

            if (!InspectionPositionKnown)
                return StationDisplayState.PositionUnknown;

            if (!State.Display.AutomaticRunning && !InspectionGantry.Motion.IsMoving)
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
            return State.Display.InspectionState is InspectionStationState.MovingTransferToCarrier
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
