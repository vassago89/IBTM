using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Stations.PcbPlacement;

internal sealed class PcbPlacementStation : IPcbPlacementStation, IDisposable
{
    private readonly StationOperations _machine;
    private readonly ProcessStageRunner _stages;
    private readonly ProcessEventHub _events;

    public PcbPlacementStation(
        [FromKeyedServices(PcbPlacementModule.ServiceKey)] IMotionService motion,
        IIOService io,
        PcbPlacementOptions options,
        MachineRuntimeSettings runtime,
        ProcessStageRunner stages,
        ProcessEventHub events)
    {
        _machine = new StationOperations(1, motion, io, options.Motion, runtime, events);
        _stages = stages;
        _events = events;
    }

    public void Initialize() => _machine.Initialize(0, 1, 2);

    public async Task RunAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _stages.RunAsync(
            PcbPlacementStages.WaitShuttle,
            cancellationToken,
            async token =>
            {
                await _machine.WaitForSignalAsync(token);
                await _machine.WaitForSignalAsync(token);
            });

        await _stages.RunAsync(
            PcbPlacementStages.StopAlignLift,
            cancellationToken,
            token => _machine.StopAlignLiftAsync(
                PcbPlacementChannels.Stopper,
                PcbPlacementChannels.Align,
                PcbPlacementChannels.Lift,
                token));

        await _stages.RunAsync(
            PcbPlacementStages.PickPlace,
            cancellationToken,
            async token =>
            {
                await PickAndPlaceAsync(recipe.PcbPick1, recipe.PcbPlace1, token);
                await PickAndPlaceAsync(recipe.PcbPick2, recipe.PcbPlace2, token);
            });

        await _stages.RunAsync(
            PcbPlacementStages.Release,
            cancellationToken,
            token => _machine.ReleaseAsync(
                PcbPlacementChannels.Stopper,
                PcbPlacementChannels.Align,
                PcbPlacementChannels.Lift,
                token));
    }

    public void Stop() => _machine.Stop();

    public void EmergencyStop() => _machine.EmergencyStop();

    public void Dispose() => _machine.Dispose();

    private async Task PickAndPlaceAsync(
        AxisPos pickPosition,
        AxisPos placePosition,
        CancellationToken cancellationToken)
    {
        await _machine.PickAndPlaceAsync(
            pickPosition,
            placePosition,
            PcbPlacementChannels.Gripper,
            cancellationToken);
        _events.PcbWasPlaced();
    }
}
