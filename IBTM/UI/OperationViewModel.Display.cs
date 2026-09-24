using System;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM.UI;

public sealed record DoorSensorDisplay(string Name, IoInputStatus Input);

public partial class OperationViewModel
{
    public MainConveyorState? ConveyorState
    {
        get
        {
            if (!State.Available
                || Signals.Outputs[OutputIo.MainConveyorRun].IsOn is not { } running)
                return null;
            return Conveyor.Step is MainConveyorState step ? step : Conveyor.GetNextStep(running, live: false);
        }
    }

    public NgConveyorState? NgConveyorState
    {
        get
        {
            if (!State.Available
                || Signals.Outputs[OutputIo.NgConveyorRun].IsOn is not { } running)
                return null;
            return NgConveyor.Step is NgConveyorState step ? step : NgConveyor.GetNextStep(running);
        }
    }

    public PcbPlacementState? PlacementState
    {
        get
        {
            if (!State.Available || !PlacementPositionKnown || !Placement.Motion.IsReady(live: false))
                return null;
            return Placement.State;
        }
    }

    public HeatSinkSlot? PlacementTarget => Placement.TargetHeatSink;

    public BoltFasteningState? FasteningState
    {
        get
        {
            if (!State.Available || !Units.BoltFastening || !Machine.TeachingReady
                || !FasteningPositionKnown || !Fastening.Motion.IsReady(live: false))
                return null;
            return Fastening.Step is BoltFasteningState step ? step : Fastening.GetNextStep(live: false);
        }
    }

    public InspectionStationState? InspectionState
    {
        get
        {
            if (!State.Available || !Units.Inspection || !Machine.TeachingReady
                || !InspectionPositionKnown || !Inspection.Motion.IsReady(live: false)
                || Signals.Outputs[OutputIo.MainConveyorRun].IsOn is not { } mainRunning
                || Signals.Outputs[OutputIo.NgConveyorRun].IsOn is not { } running)
                return null;
            return Inspection.Step is InspectionStationState step ? step
                : Inspection.GetNextStep(State.RepeatEnabled, live: false,
                    conveyorRunning: running, mainConveyorRunning: mainRunning);
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
            return Inspection.Motion.XyHomed && _map.InspectionDefined
                && Inspection.Motion.Position is { X: not null, Y: not null };
        }
    }

    public bool BoltFeederPositionKnown => _map.PickupFeederPosition is not null;

    public Enum SupplyStatus
    {
        get
        {
            var display = SupplyDisplayState;
            return display is HandlerDisplayState.Working or HandlerDisplayState.Moving or HandlerDisplayState.Waiting
                ? Supply.State
                : display;
        }
    }

    public Enum PlacementStatus
    {
        get
        {
            var display = PlacementDisplayState;
            return display is HandlerDisplayState.Working or HandlerDisplayState.Moving or HandlerDisplayState.Waiting
                ? PlacementState ?? (Enum)MachineDisplayState.Unavailable
                : display;
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
                default:
                    return Supply.State is PcbSupplyState.WaitingForCarrier
                        or PcbSupplyState.WaitingForCarrierExit
                        or PcbSupplyState.HandingOff
                        or PcbSupplyState.WaitingForPlacementClear
                        or PcbSupplyState.WaitingForReturnedPcb
                        or PcbSupplyState.WaitingForReturnedPcbGrip
                        or PcbSupplyState.WaitingForReturnClear
                        ? HandlerDisplayState.Waiting
                        : HandlerDisplayState.Working;
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
                    return PlacementState is PcbPlacementState.WaitingForSupply
                        or PcbPlacementState.WaitingForSupplyRelease
                        or PcbPlacementState.WaitingForCarrier
                        or PcbPlacementState.WaitingForSupplyReceipt
                        or PcbPlacementState.WaitingForSupplyGrip
                        or PcbPlacementState.WaitingForSupplyDeparture
                        ? HandlerDisplayState.Waiting
                        : HandlerDisplayState.Working;
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
                case StationDisplayState.WaitingForTransfer when Units.Inspection && InspectionWork.RouteToNg:
                    return InspectionStationState.WaitingForDestination;
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
                case true when !Units.Inspection:
                    return StationDisplayState.Disabled;
                case true when Alarm is MachineAlarm.Inspection or MachineAlarm.NgCarrierTransfer:
                    return StationDisplayState.IoAlarm;
                case true when !InspectionPositionKnown:
                    return StationDisplayState.PositionUnknown;
                case true when !State.AutomaticRunning && !Inspection.Motion.IsMoving:
                    return StationDisplayState.Stopped;
                case true when Inspection.Motion.IsMoving
                    || Inspection.IsTransferPending
                    || InspectionState is InspectionStationState.PreparingTransfer
                        or InspectionStationState.PickingCarrier or InspectionStationState.PlacingCarrier
                        or InspectionStationState.WaitingForShuttleDown:
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

}
