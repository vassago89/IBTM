using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;

namespace IBTM;

public enum PcbReturnDestination
{
    [Description("Heat sink")]
    HeatSink,
    [Description("Supply")]
    Supply,
}

public enum PcbReturnState
{
    [Description("Select heat sink and run")]
    Ready,
    [Description("Waiting for seated carrier")]
    WaitingForCarrier,
    [Description("Waiting for selected heat sink")]
    WaitingForHeatSink,
    [Description("Empty Supply before picking another PCB")]
    WaitingForEmptySupply,
    [Description("Raising handler")]
    RaisingHandler,
    [Description("Raising Z")]
    RaisingZ,
    [Description("Rotating for pickup")]
    RotatingForPickup,
    [Description("Opening IPM gripper")]
    OpeningGripper,
    [Description("Lowering IPM")]
    LoweringIpm,
    [Description("Moving to PCB")]
    MovingToPcb,
    [Description("Lowering to PCB")]
    LoweringToPcb,
    [Description("Lowering handler")]
    LoweringHandler,
    [Description("Waiting for PCB detection")]
    WaitingForPcb,
    [Description("Applying vacuum")]
    ApplyingVacuum,
    [Description("Closing IPM gripper")]
    ClosingGripper,
    [Description("PCB secured")]
    PickupComplete,
    [Description("Raising IPM")]
    RaisingIpm,
    [Description("Moving to rotation position")]
    MovingToRotationPosition,
    [Description("Unrotating for buffer")]
    UnrotatingForBuffer,
    [Description("Waiting for handoff clearance")]
    WaitingForBuffer,
    [Description("Moving to buffer")]
    MovingToBuffer,
    [Description("Lowering to buffer")]
    LoweringToBuffer,
    [Description("Waiting for buffer PCB detection")]
    WaitingForBufferPcb,
    [Description("Preparing Supply below buffer")]
    PreparingSupply,
    [Description("Entering Supply at Clear Z")]
    EnteringSupply,
    [Description("Raising Supply to handoff")]
    RaisingSupply,
    [Description("Waiting for Supply PCB detection")]
    WaitingForSupplyPcb,
    [Description("Securing PCB in Supply")]
    SecuringSupply,
    [Description("Releasing placement vacuum")]
    ReleasingVacuum,
    [Description("Returning Supply with PCB")]
    WithdrawingSupply,
    [Description("PCB returned to Supply")]
    Completed,
}

// Coordinates two peer units; each unit still owns its motion and IO commands.
public sealed class PcbReturn(
    PcbSupplyHandler supply,
    PcbPlacementHandler placement,
    BufferStage buffer,
    PcbPlacementWork work,
    Recipe recipe) : AutoUnit
{
    private event Action? ProgressChanged;
    // Route intent survives Stop. Material and actuator state always come from feedback.
    public PcbReturnDestination Destination { get; private set; }
    public HeatSinkSlot? HeatSink { get; private set; }
    public int CompletedReturns { get; private set; }

    public override event Action? Changed
    {
        add
        {
            ProgressChanged += value;
            supply.Changed += value;
            placement.Changed += value;
            buffer.StateChanged += value;
            work.Changed += value;
        }

        remove
        {
            ProgressChanged -= value;
            supply.Changed -= value;
            placement.Changed -= value;
            buffer.StateChanged -= value;
            work.Changed -= value;
        }
    }

    private AxisPosition PcbPosition
    {
        get
        {
            return HeatSink == HeatSinkSlot.HeatSink1
                ? recipe.PcbPlacement.HeatSink1PcbPlacementPosition
                : recipe.PcbPlacement.HeatSink2PcbPlacementPosition;
        }
    }

    private bool Picking
    {
        get
        {
            return Destination == PcbReturnDestination.HeatSink
                || supply.Pcb == PcbSupplyPcbState.None
                && placement.Pcb == PlacementPcbState.None
                && !buffer.PcbPresent
                && !buffer.SupplyInside
                && placement.CanMoveHorizontal
                && placement.AtHorizontalZ;
        }
    }

    public PcbReturnState State
    {
        get
        {
            return Picking ? PickupState() : HandoffState();
        }
    }

    internal void Begin(HeatSinkSlot heatSink)
    {
        HeatSink = heatSink;
        Destination = PcbReturnDestination.HeatSink;
        ProgressChanged?.Invoke();
    }

    public async Task RunAsync(HeatSinkSlot heatSink, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Picking)
        {
            if (Destination != PcbReturnDestination.HeatSink)
                HeatSink = heatSink;
            else
                HeatSink ??= heatSink;
            Destination = PcbReturnDestination.HeatSink;
            ProgressChanged?.Invoke();
        }

        if (State != PcbReturnState.Completed)
        {
            await RunLoopAsync(ExecuteAsync, cancellationToken, () => State == PcbReturnState.Completed);
            if (cancellationToken.IsCancellationRequested
                || State != PcbReturnState.Completed)
                return;
            CompletedReturns++;
        }

        HeatSink = null;
        ProgressChanged?.Invoke();
    }

    private PcbReturnState PickupState()
    {
        if (placement.Pcb == PlacementPcbState.Secured)
            return PcbReturnState.PickupComplete;
        if (supply.Pcb != PcbSupplyPcbState.None)
            return PcbReturnState.WaitingForEmptySupply;
        if (!work.CarrierSeated)
            return PcbReturnState.WaitingForCarrier;
        if (HeatSink is not { } heatSink)
            return PcbReturnState.Ready;
        if (!work.HeatSinkPresent(heatSink))
            return PcbReturnState.WaitingForHeatSink;
        var atPcb = placement.IsAtXY(PcbPosition);
        if (!atPcb || placement.Rotation != PlacementRotationState.Rotated)
        {
            if (placement.Lift != PlacementCylinderState.Up)
                return PcbReturnState.RaisingHandler;
            if (!placement.AtHorizontalZ)
                return PcbReturnState.RaisingZ;
        }

        if (placement.Rotation != PlacementRotationState.Rotated)
            return placement.IsAtXY(recipe.PcbPlacement.HeatSink1PcbPlacementPosition)
                ? PcbReturnState.RotatingForPickup
                : PcbReturnState.MovingToRotationPosition;
        if (atPcb
            && placement.IsAtZ(PcbPosition)
            && placement.Pcb != PlacementPcbState.None
            && placement.VacuumDetected)
            return PcbReturnState.ClosingGripper;
        if (placement.IpmGripper != PlacementGripperState.Open)
            return PcbReturnState.OpeningGripper;
        if (placement.IpmLift != PlacementCylinderState.Down)
            return PcbReturnState.LoweringIpm;
        if (!atPcb)
            return PcbReturnState.MovingToPcb;
        if (!placement.IsAtZ(PcbPosition))
            return PcbReturnState.LoweringToPcb;
        if (placement.Lift != PlacementCylinderState.Down)
            return PcbReturnState.LoweringHandler;
        if (placement.Pcb == PlacementPcbState.None)
            return PcbReturnState.WaitingForPcb;
        return PcbReturnState.ApplyingVacuum;
    }

    private PcbReturnState HandoffState()
    {
        if (supply.Pcb == PcbSupplyPcbState.Secured)
        {
            if (placement.VacuumDetected)
                return buffer.SupplyAtHandoff && buffer.PlacementAtHandoff
                    ? PcbReturnState.ReleasingVacuum
                    : PcbReturnState.WaitingForBuffer;
            if (placement.IpmGripper != PlacementGripperState.Open)
                return PcbReturnState.OpeningGripper;
            if (placement.IpmLift != PlacementCylinderState.Up)
                return PcbReturnState.RaisingIpm;
            if (placement.Lift != PlacementCylinderState.Up)
                return PcbReturnState.RaisingHandler;
            if (!placement.AtHorizontalZ)
                return PcbReturnState.RaisingZ;
            return !buffer.SupplyInside
                && supply.IsAtRotationZ
                && supply.Rotation == PcbSupplyRotationState.Unrotated
                ? PcbReturnState.Completed
                : PcbReturnState.WithdrawingSupply;
        }

        if (placement.Pcb != PlacementPcbState.Secured)
            return placement.Pcb == PlacementPcbState.None
                ? PcbReturnState.WaitingForPcb
                : !placement.VacuumDetected
                    ? PcbReturnState.ApplyingVacuum
                    : PcbReturnState.ClosingGripper;

        if (!placement.AtBufferXY || placement.Rotation != PlacementRotationState.Unrotated)
        {
            if (placement.IpmLift != PlacementCylinderState.Down)
                return PcbReturnState.LoweringIpm;
            if (placement.Lift != PlacementCylinderState.Up)
                return PcbReturnState.RaisingHandler;
            if (!placement.AtHorizontalZ)
                return PcbReturnState.RaisingZ;
            if (placement.Rotation != PlacementRotationState.Unrotated)
                return placement.IsAtXY(recipe.PcbPlacement.HeatSink1PcbPlacementPosition)
                    ? PcbReturnState.UnrotatingForBuffer
                    : PcbReturnState.MovingToRotationPosition;
            return buffer.CanPlacementReturn
                ? PcbReturnState.MovingToBuffer
                : PcbReturnState.WaitingForBuffer;
        }

        if (!placement.AtBufferZ)
            return buffer.CanPlacementReturn
                ? PcbReturnState.LoweringToBuffer
                : PcbReturnState.WaitingForBuffer;
        if (placement.Lift != PlacementCylinderState.Down)
            return PcbReturnState.LoweringHandler;
        if (placement.IpmLift != PlacementCylinderState.Down)
            return PcbReturnState.LoweringIpm;
        if (!buffer.PcbPresent)
            return PcbReturnState.WaitingForBufferPcb;
        if (!buffer.CanSupplyReturn)
            return PcbReturnState.WaitingForBuffer;
        if (!buffer.SupplyAtHandoff)
        {
            if (supply.OnReturnHandoffPath)
                return PcbReturnState.RaisingSupply;
            if (!buffer.SupplyInside && !supply.AtReturnEntryZ)
                return PcbReturnState.PreparingSupply;
            return supply.AtReturnEntryZ
                ? PcbReturnState.EnteringSupply
                : PcbReturnState.WaitingForBuffer;
        }

        return supply.Pcb == PcbSupplyPcbState.None
            ? PcbReturnState.WaitingForSupplyPcb
            : PcbReturnState.SecuringSupply;
    }

    private Task ExecuteAsync(CancellationToken token)
    {
        switch (State)
        {
            case PcbReturnState.RaisingHandler:
                return placement.RaiseAsync(token);
            case PcbReturnState.RaisingZ:
                return placement.MoveToHorizontalZAsync(token);
            case PcbReturnState.RotatingForPickup:
                return placement.SetRotatedAsync(true, token);
            case PcbReturnState.OpeningGripper:
                return placement.SetIpmGripperAsync(false, token);
            case PcbReturnState.LoweringIpm:
                return placement.SetIpmLiftDownAsync(true, token);
            case PcbReturnState.MovingToPcb:
                return placement.MoveToXYAsync(PcbPosition.X, PcbPosition.Y, token);
            case PcbReturnState.LoweringToPcb:
                return placement.MoveZAsync(PcbPosition.Z, token);
            case PcbReturnState.LoweringHandler:
                return placement.SetLiftDownAsync(true, token);
            case PcbReturnState.WaitingForPcb:
                return placement.WaitForPcbAsync(token);
            case PcbReturnState.ApplyingVacuum:
                return placement.SetVacuumAsync(true, token);
            case PcbReturnState.ClosingGripper:
                return placement.SetIpmGripperAsync(true, token);
            case PcbReturnState.PickupComplete:
                work.PrepareRecovery([(HeatSink!.Value, false)]);
                Destination = PcbReturnDestination.Supply;
                ProgressChanged?.Invoke();
                return Task.CompletedTask;
            case PcbReturnState.RaisingIpm:
                return placement.SetIpmLiftDownAsync(false, token);
            case PcbReturnState.MovingToRotationPosition:
                var rotation = recipe.PcbPlacement.HeatSink1PcbPlacementPosition;
                return placement.MoveToXYAsync(rotation.X, rotation.Y, token);
            case PcbReturnState.UnrotatingForBuffer:
                return placement.SetRotatedAsync(false, token);
            case PcbReturnState.MovingToBuffer:
                return placement.MoveAboveBufferAsync(token);
            case PcbReturnState.LoweringToBuffer:
                return placement.LowerToBufferAsync(token);
            case PcbReturnState.WaitingForBufferPcb:
                return buffer.WaitForPcbAsync(token);
            case PcbReturnState.PreparingSupply:
                return supply.PrepareReturnEntryAsync(token);
            case PcbReturnState.EnteringSupply:
                return supply.EnterAtClearZAsync(token);
            case PcbReturnState.RaisingSupply:
                return supply.MoveToHandoffZAsync(token);
            case PcbReturnState.WaitingForSupplyPcb:
                return supply.WaitForPcbAsync(token);
            case PcbReturnState.SecuringSupply:
                return supply.SecurePcbAsync(token);
            case PcbReturnState.ReleasingVacuum:
                return placement.SetVacuumAsync(false, token);
            case PcbReturnState.WithdrawingSupply:
                return supply.ReturnWithPcbAsync(token);
            default:
                return WaitForChangeAsync(token);
        }
    }
}
