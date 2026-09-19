using System;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;

namespace IBTM.UI;

public sealed record DoorSensorDisplay(string Name, IoInputStatus Input);

public partial class OperationViewModel
{
    public bool SupplyPositionKnown
    {
        get
        {
            return Supply.Motion.XyHomed && _map.SupplyDefined
                && Supply.Motion.Position is { X: not null, Y: not null, Z: not null };
        }
    }

    public bool PlacementPositionKnown
    {
        get
        {
            return Placement.Motion.XyHomed && _map.PlacementDefined
                && Placement.Motion.Position is { X: not null, Y: not null, Z: not null };
        }
    }

    public bool FasteningPositionKnown
    {
        get
        {
            return Fastening.Motion.XyHomed && _map.FasteningDefined
                && Fastening.Motion.Position is { X: not null, Y: not null, Z: not null };
        }
    }

    public bool InspectionPositionKnown
    {
        get
        {
            return InspectionGantry.Motion.XyHomed && _map.InspectionDefined
                && InspectionGantry.Motion.Position is { X: not null, Y: not null };
        }
    }

    public bool BoltFeederPositionKnown => _map.FasteningDefined;

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
            switch (true)
            {
                case true when !Units.MainConveyor:
                    return HandlerDisplayState.Disabled;
                case true when State.Display.Available
                    && Signals.Outputs[OutputIo.MainConveyorRun].IsOn is { } running:
                    return !State.Display.AutomaticRunning && !running
                        ? HandlerDisplayState.Stopped
                        : State.Display.ConveyorState;
                default:
                    return MachineDisplayState.Unavailable;
            }
        }
    }

    public MachineDisplayState MachineDisplayState
    {
        get
        {
            switch (State.Display)
            {
                case { Available: false }:
                    return MachineDisplayState.Unavailable;
                case { SafetyReady: false }:
                    return MachineDisplayState.SafetyStop;
                case { Alarm: not MachineAlarm.None }:
                    return MachineDisplayState.Alarm;
                case { MotionFaulted: true }:
                    return MachineDisplayState.MotionFault;
                case { IsHoming: true }:
                    return MachineDisplayState.Homing;
                case { ServoPowerOn: false }:
                    return MachineDisplayState.ServoOff;
                case { Homed: false }:
                    return MachineDisplayState.HomeRequired;
                case { IsRunning: true }:
                    return MachineDisplayState.Running;
                default:
                    return MachineDisplayState.Ready;
            }
        }
    }

    public bool StartBlocked
    {
        get
        {
            return !State.Display.IsStartAllowed
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
            switch (true)
            {
                case true when !Units.PcbSupply:
                    return HandlerDisplayState.Disabled;
                case true when Alarm is MachineAlarm.PcbSupply:
                    return HandlerDisplayState.IoAlarm;
                case true when !SupplyPositionKnown:
                    return HandlerDisplayState.PositionUnknown;
                case true when Supply.Motion.IsMoving:
                    return HandlerDisplayState.Moving;
                case true when !State.Display.AutomaticRunning:
                    return HandlerDisplayState.Stopped;
                case true when State.Display.SupplyAtHandoff:
                    return Supply.PcbReleased
                        ? HandlerDisplayState.WaitingForPlacementLift
                        : HandlerDisplayState.WaitingForPlacement;
                case true when PcbSupplyPcbDetected:
                    return HandlerDisplayState.Working;
                default:
                    return Supply.UpstreamCarrierAvailable
                        ? HandlerDisplayState.CarrierAvailable
                        : HandlerDisplayState.WaitingForCarrier;
            }
        }
    }

    public HandlerDisplayState PlacementDisplayState
    {
        get
        {
            switch (true)
            {
                case true when !Units.PcbPlacement:
                    return HandlerDisplayState.Disabled;
                case true when Alarm is MachineAlarm.PcbPlacement:
                    return HandlerDisplayState.IoAlarm;
                case true when !PlacementPositionKnown:
                    return HandlerDisplayState.PositionUnknown;
                case true when Placement.Motion.IsMoving:
                    return HandlerDisplayState.Moving;
                case true when !State.Display.AutomaticRunning:
                    return HandlerDisplayState.Stopped;
                default:
                    switch (State.Display.PlacementState)
                    {
                        case PcbPlacementState.WaitingForSupply:
                            return HandlerDisplayState.WaitingForSupply;
                        case PcbPlacementState.WaitingForSupplyRelease:
                            return HandlerDisplayState.WaitingForSupplyRelease;
                        case PcbPlacementState.WaitingForCarrier:
                            return HandlerDisplayState.WaitingForMainCarrier;
                        default:
                            return HandlerDisplayState.Working;
                    }
            }
        }
    }

    public StationDisplayState BoltDisplayState
    {
        get
        {
            switch (true)
            {
                case true when !Units.BoltFastening:
                    return StationDisplayState.Disabled;
                case true when Alarm is MachineAlarm.PickupBoltFeeder
                    or MachineAlarm.ShootingBoltFeeder
                    or MachineAlarm.BoltFastening:
                    return StationDisplayState.IoAlarm;
                case true when !FasteningPositionKnown:
                    return StationDisplayState.PositionUnknown;
                case true when Fastening.Motion.IsMoving || State.BoltTestRunning:
                    return StationDisplayState.Working;
                case true when !State.Display.AutomaticRunning:
                    return StationDisplayState.Stopped;
                case true when !BoltFasteningWork.Station.CarrierPresent:
                    return StationDisplayState.WaitingForCarrier;
                case true when BoltFasteningWork.Completed:
                    return StationDisplayState.WaitingForTransfer;
                default:
                    return FasteningStateVisible
                        ? StationDisplayState.Working
                        : StationDisplayState.HeatSinkDetected;
            }
        }
    }

    public Enum InspectionStatus
    {
        get
        {
            switch (InspectionDisplayState)
            {
                case StationDisplayState.Working when InspectionStateVisible:
                    return State.Display.InspectionState;
                case StationDisplayState.WaitingForTransfer when Units.NgCarrierTransfer && InspectionWork.RouteToNg:
                    return InspectionStationState.WaitingForShuttleReady;
                case StationDisplayState.WaitingForTransfer when Units.MainConveyor
                        && State.Display.ConveyorState == MainConveyorState.WaitingForRearEquipment:
                    return State.Display.ConveyorState;
                default:
                    return InspectionDisplayState;
            }
        }
    }

    public StationDisplayState InspectionDisplayState
    {
        get
        {
            switch (true)
            {
                case true when !Units.Inspection && !Units.NgCarrierTransfer:
                    return StationDisplayState.Disabled;
                case true when Alarm is MachineAlarm.Inspection or MachineAlarm.NgCarrierTransfer:
                    return StationDisplayState.IoAlarm;
                case true when !InspectionPositionKnown:
                    return StationDisplayState.PositionUnknown;
                case true when !State.Display.AutomaticRunning && !InspectionGantry.Motion.IsMoving:
                    return StationDisplayState.Stopped;
                case true when InspectionGantry.Motion.IsMoving
                    || NgTransfer.CarrierDetected
                    || InspectionTransferWorking:
                    return StationDisplayState.Working;
                case true when !InspectionWork.Station.CarrierPresent:
                    return StationDisplayState.WaitingForCarrier;
                case true when InspectionWork.Completed:
                    return StationDisplayState.WaitingForTransfer;
                default:
                    return InspectionStateVisible
                        ? StationDisplayState.Working
                        : StationDisplayState.HeatSinkDetected;
            }
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
