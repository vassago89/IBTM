using System;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;

namespace IBTM.UI;

public sealed record DoorSensorDisplay(string Name, IoInputStatus Input);

public partial class OperationViewModel
{
    public MainConveyorState? ConveyorState
    {
        get
        {
            return State.Available
                && Signals.Outputs[OutputIo.MainConveyorRun].IsOn is { } running
                ? Conveyor.GetState(running, live: false) : null;
        }
    }

    public NgConveyorState? NgConveyorState
    {
        get
        {
            return State.Available
                && Signals.Outputs[OutputIo.NgConveyorRun].IsOn is { } running
                ? NgConveyor.GetState(running) : null;
        }
    }

    public PcbPlacementState? PlacementState
    {
        get
        {
            return State.Available && PlacementPositionKnown
                && Placement.Motion.IsReady(live: false) ? Placement.GetState(live: false) : null;
        }
    }

    public HeatSinkSlot? PlacementTarget => Placement.TargetHeatSink;

    public BoltFasteningState? FasteningState
    {
        get
        {
            return State.Available && Units.BoltFastening
                && Machine.TeachingReady && FasteningPositionKnown && Fastening.Motion.IsReady(live: false)
                ? Fastening.GetState(live: false) : null;
        }
    }

    public InspectionStationState? InspectionState
    {
        get
        {
            return State.Available && Units.Inspection
                && Machine.TeachingReady && InspectionPositionKnown && NgTransfer.Motion.IsReady(live: false)
                && Signals.Outputs[OutputIo.MainConveyorRun].IsOn is { } mainRunning
                && Signals.Outputs[OutputIo.NgConveyorRun].IsOn is { } running
                ? _inspectionStation.GetState(_recipes.Current.Pcb.BoltPoints, State.RepeatEnabled,
                    holdAtShuttle: State.RepeatEnabled && !Units.NgShuttle, live: false,
                    conveyorRunning: running, mainConveyorRunning: mainRunning)
                : null;
        }
    }

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
            return NgTransfer.Motion.XyHomed && _map.InspectionDefined
                && NgTransfer.Motion.Position is { X: not null, Y: not null };
        }
    }

    public bool BoltFeederPositionKnown => _map.FasteningDefined;

    public Enum PlacementStatus
    {
        get
        {
            return PlacementDisplayState is HandlerDisplayState.Working or HandlerDisplayState.Moving
                ? PlacementState ?? (Enum)MachineDisplayState.Unavailable
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
                case true when State.Available
                    && Signals.Outputs[OutputIo.MainConveyorRun].IsOn is { } running:
                    return !State.AutomaticRunning && !running
                        ? HandlerDisplayState.Stopped
                        : ConveyorState ?? (Enum)MachineDisplayState.Unavailable;
                default:
                    return MachineDisplayState.Unavailable;
            }
        }
    }

    public MachineDisplayState MachineDisplayState
    {
        get
        {
            switch (State)
            {
                case { Available: false }:
                    return MachineDisplayState.Unavailable;
                case { SafetyReady: false }:
                    return MachineDisplayState.SafetyStop;
                case { Alarm: not MachineAlarm.None }:
                    return MachineDisplayState.Alarm;
                case { Faulted: true }:
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
            return !Machine.IsStartAllowed
                && !State.IsHoming
                && Machine.StartBlock != StartBlockReason.None;
        }
    }

    public bool FasteningStateVisible
    {
        get
        {
            return State.AutomaticRunning
                && FasteningState is not null and not BoltFasteningState.Waiting;
        }
    }

    public bool InspectionStateVisible
    {
        get
        {
            return State.AutomaticRunning
                && InspectionState is not null and not InspectionStationState.Waiting;
        }
    }

    public Enum StartBlock
    {
        get
        {
            return Machine.StartBlock == StartBlockReason.HomeRequired
                && Machine.HomeBlock != HomeBlockReason.None
                ? Machine.HomeBlock
                : Machine.StartBlock;
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
                case true when !State.AutomaticRunning:
                    return HandlerDisplayState.Stopped;
                case true when Supply.IsAtHandoff(live: false):
                    return Supply.PcbReleased
                        ? HandlerDisplayState.WaitingForPlacementZ
                        : HandlerDisplayState.WaitingForPlacement;
                case true when Supply.PcbSecured:
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
                case true when !State.AutomaticRunning:
                    return HandlerDisplayState.Stopped;
                default:
                    switch (PlacementState)
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
                case true when !State.AutomaticRunning:
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
                    return InspectionState ?? (Enum)MachineDisplayState.Unavailable;
                case StationDisplayState.WaitingForTransfer when Units.NgCarrierTransfer && InspectionWork.RouteToNg:
                    return InspectionStationState.WaitingForShuttleReady;
                case StationDisplayState.WaitingForTransfer when Units.MainConveyor
                        && ConveyorState == MainConveyorState.WaitingForRearEquipment:
                    return ConveyorState ?? (Enum)MachineDisplayState.Unavailable;
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
                case true when !State.AutomaticRunning && !NgTransfer.Motion.IsMoving:
                    return StationDisplayState.Stopped;
                case true when NgTransfer.Motion.IsMoving
                    || NgTransfer.IsTransferPending
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
            return InspectionState == InspectionStationState.TransferringNgCarrier;
        }
    }
}
