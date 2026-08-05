using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbSupply;
using IBTM.Stations.PcbPlacement;
using Microsoft.Extensions.DependencyInjection;

namespace IBTM;

public sealed class PcbBufferService(
    Recipe recipe,
    EquipmentState state,
    IIoService io,
    BufferStage buffer,
    PcbSupplyHandler supplyHandler,
    PcbPlacementStation placementStation,
    [FromKeyedServices(MotionGroup.PcbSupply)] MotionService supplyMotion,
    [FromKeyedServices(MotionGroup.PcbPlacement)] MotionService placementMotion)
{
    public bool CanRecover =>
        state.BufferRecoveryRequired
        && state.Ready
        && state.SafetyReady
        && !state.IsError
        && !state.EquipmentRunning;

    public bool PcbPresent =>
        io.GetInput(InputIo.PcbBufferPcbPresent);

    public bool CanSupplyToBuffer
    {
        get
        {
            var supply = supplyMotion.GetPosition();
            var placement = placementMotion.GetPosition();
            return state.CanOperate
                   && !state.PcbSupplyMoving
                   && !state.PcbPlacementMoving
                   && supplyHandler.PcbPresent
                   && supplyHandler.Rotation == PcbSupplyRotation.Rotated
                   && !PcbPresent
                   && buffer.CanEnter(
                       supply.X,
                       placement.X,
                       placement.Y);
        }
    }

    public bool CanBufferToPlacement
    {
        get
        {
            var supply = supplyMotion.GetPosition();
            var placement = placementMotion.GetPosition();
            return state.CanOperate
                   && !state.PcbSupplyMoving
                   && !state.PcbPlacementMoving
                   && PcbPresent
                   && !placementStation.PcbPresent
                   && buffer.CanEnter(
                       supply.X,
                       placement.X,
                       placement.Y);
        }
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var owner = buffer.Owner;
        try
        {
            if (owner == BufferOwner.Supply)
            {
                await supplyHandler.MoveClearAsync(cancellationToken);
                buffer.ExitSupply(supplyMotion.GetPosition().X);
            }
            else
            {
                await placementStation.MoveClearAsync(
                    recipe.PcbPlacement.Fiducial1Position,
                    cancellationToken);
                var position = placementMotion.GetPosition();
                buffer.ExitPlacement(position.X, position.Y);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IoFeedbackTimeoutException)
        {
            state.SetError(owner == BufferOwner.Supply
                ? EquipmentAlarm.Supply
                : EquipmentAlarm.Placement);
        }
        finally
        {
            if (buffer.Owner == owner)
            {
                buffer.Cancel();
            }
        }
    }

    public Task SupplyToBufferAsync() =>
        RunAsync(BufferOwner.Supply);

    public Task BufferToPlacementAsync() =>
        RunAsync(BufferOwner.Placement);

    private async Task RunAsync(BufferOwner owner)
    {
        var entered = false;
        try
        {
            var supply = supplyMotion.GetPosition();
            var placement = placementMotion.GetPosition();
            var cancellationToken = await buffer.EnterAsync(
                owner,
                supply.X,
                placement.X,
                placement.Y);
            entered = true;

            if (owner == BufferOwner.Supply)
            {
                await supplyHandler.PlaceOnBufferAsync(cancellationToken);
                buffer.ExitSupply(supplyMotion.GetPosition().X);
            }
            else
            {
                await placementStation.PickFromBufferAsync(
                    recipe.PcbPlacement.Fiducial1Position,
                    cancellationToken);
                var position = placementMotion.GetPosition();
                buffer.ExitPlacement(position.X, position.Y);
            }
        }
        catch (OperationCanceledException)
            when (buffer.CancellationRequested)
        {
        }
        catch (IoFeedbackTimeoutException)
        {
            state.SetError(owner == BufferOwner.Supply
                ? EquipmentAlarm.Supply
                : EquipmentAlarm.Placement);
        }
        finally
        {
            if (entered && buffer.Owner == owner)
            {
                buffer.Cancel();
            }
        }
    }
}
