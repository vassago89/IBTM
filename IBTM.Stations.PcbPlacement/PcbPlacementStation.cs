using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbSupply;
using IBTM.Transport;

namespace IBTM.Stations.PcbPlacement;

public sealed class PcbPlacementStation(
    MotionService motion,
    IIoService io,
    PcbHandoff handoff,
    PcbPlacementSettings settings,
    ProcessEvents events,
    PcbAligner aligner)
{
    public CarrierJigPositioner CarrierJigPositioner { get; } = new(
        io,
        InputIo.PcbPlacementCarrierJigPresent,
        OutputIo.PcbPlacementStopperUp,
        OutputIo.PcbPlacementBackupPlateUp);

    public void Initialize()
    {
        motion.Initialize();
        aligner.Initialize();
        CarrierJigPositioner.Initialize();
    }

    public async Task<CarrierJigState> ProcessAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken)
    {
        await events.RunStageAsync(
            ProcessStage.PositionPcbPlacementCarrierJig,
            cancellationToken,
            CarrierJigPositioner.PositionAsync);

        return await events.RunStageAsync(
            ProcessStage.PlacePcb,
            cancellationToken,
            async token =>
            {
                var pcb1Present = await PlacePcbAsync(
                    recipe,
                    recipe.Pcb1PlacePosition,
                    token);
                var pcb2Present = await PlacePcbAsync(
                    recipe,
                    recipe.Pcb2PlacePosition,
                    token);
                return new CarrierJigState(pcb1Present, pcb2Present);
            });
    }

    public void Stop() => motion.Stop();

    public void EmergencyStop() => motion.EmergencyStop();

    private async Task<bool> PlacePcbAsync(
        PcbPlacementRecipe recipe,
        AxisPos placePosition,
        CancellationToken cancellationToken)
    {
        if (!await handoff.TakeAsync(cancellationToken))
        {
            return false;
        }

        var pickPosition = settings.HandoffPickPosition;
        await motion.MoveToAsync(
            pickPosition.X,
            pickPosition.Y,
            pickPosition.Z,
            cancellationToken);
        await io.SetOutputAndWaitAsync(
            OutputIo.PcbPlacementGripper,
            true,
            cancellationToken);
        handoff.ConfirmPickup();
        await io.WaitForInputAsync(
            InputIo.PcbSupplyGripperClosed,
            false,
            cancellationToken);

        var firstFiducial = recipe.Fiducial1Position;
        await motion.MoveToAsync(
            firstFiducial.X,
            firstFiducial.Y,
            firstFiducial.Z,
            cancellationToken);
        handoff.ConfirmClear();

        if (!io.GetInput(InputIo.PcbPlacementPcbPresent))
        {
            await ReleaseAsync(cancellationToken);
            return false;
        }

        var correction = await events.RunStageAsync(
            ProcessStage.AlignPcb,
            cancellationToken,
            token => aligner.AlignFromFirstFiducialAsync(recipe, token));
        if (correction is null)
        {
            throw new InvalidOperationException(
                "PCB is detected, but its fiducials were not found.");
        }

        await motion.MoveToAsync(
            placePosition.X + correction.OffsetX,
            placePosition.Y + correction.OffsetY,
            placePosition.Z,
            cancellationToken);

        await ReleaseAsync(cancellationToken);
        return true;
    }

    private async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        await io.SetOutputAndWaitAsync(
            OutputIo.PcbPlacementGripper,
            false,
            cancellationToken);
        await motion.MoveToSafeZAsync(cancellationToken);
    }
}
