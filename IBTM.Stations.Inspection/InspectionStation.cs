using Microsoft.Extensions.DependencyInjection;

namespace IBTM.Stations.Inspection;

internal sealed class InspectionStation : IInspectionStation, IDisposable
{
    private readonly StationOperations _machine;
    private readonly ProcessStageRunner _stages;
    private readonly ProcessEventHub _events;
    private readonly IInspectionService _inspection;
    private readonly InspectionOptions _options;

    public InspectionStation(
        [FromKeyedServices(InspectionModule.ServiceKey)] IMotionService motion,
        IIOService io,
        InspectionOptions options,
        MachineRuntimeSettings runtime,
        ProcessStageRunner stages,
        ProcessEventHub events,
        IInspectionService inspection)
    {
        _machine = new StationOperations(3, motion, io, options.Motion, runtime, events);
        _stages = stages;
        _events = events;
        _inspection = inspection;
        _options = options;
    }

    public int NgStackCount { get; private set; }
    public int NgStackCapacity => _options.NgStackMaxCount;

    public void Initialize() => _machine.Initialize(6, 7, 8);

    public async Task<InspectionResult> RunAsync(
        InspectionRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _stages.RunAsync(
            InspectionStages.WaitShuttle,
            cancellationToken,
            token => _machine.WaitForSignalAsync(token));

        await _stages.RunAsync(
            InspectionStages.StopAlignLift,
            cancellationToken,
            token => _machine.StopAlignLiftAsync(
                InspectionChannels.Stopper,
                InspectionChannels.Align,
                InspectionChannels.Lift,
                token));

        var result = await _stages.RunAsync(
            InspectionStages.Inspect,
            cancellationToken,
            token => InspectAsync(recipe, token));

        if (result == InspectionResult.Good)
        {
            await RunGoodRouteAsync(cancellationToken);
        }
        else if (result == InspectionResult.Ng)
        {
            await RunNgRouteAsync(recipe, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("Inspection returned no routing result.");
        }

        return result;
    }

    public void ResetNgStack()
    {
        NgStackCount = 0;
        _events.NgStack(new NgStackState(0, Alarm: false));
    }

    public void Stop() => _machine.Stop();

    public void EmergencyStop() => _machine.EmergencyStop();

    public void Dispose() => _machine.Dispose();

    private async Task<InspectionResult> InspectAsync(
        InspectionRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _machine.MoveToPositionAsync(recipe.InspectPosition, cancellationToken);

        var outcome = await _inspection.InspectAsync(cancellationToken);
        _events.Inspection(outcome);
        return outcome.Result;
    }

    private async Task RunNgRouteAsync(
        InspectionRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _stages.RunAsync(
            InspectionStages.NgTransfer,
            cancellationToken,
            async token =>
            {
                await _machine.PickAndPlaceAsync(
                    recipe.NgPickupPosition,
                    recipe.NgPlacePosition,
                    InspectionChannels.Gripper,
                    token);

                NgStackCount++;
                var alarm = NgStackCount >= NgStackCapacity;
                _events.NgStack(new NgStackState(NgStackCount, alarm));
            });

        await ReleaseAsync(cancellationToken);
    }

    private async Task RunGoodRouteAsync(CancellationToken cancellationToken)
    {
        await _stages.RunAsync(
            InspectionStages.SmemaWait,
            cancellationToken,
            token => _machine.WaitForSignalAsync(token));

        await ReleaseAsync(cancellationToken);

        await _stages.RunAsync(
            InspectionStages.Discharge,
            cancellationToken,
            token => _machine.PulseOutputAsync(
                    InspectionChannels.BoardAvailable,
                    TimeSpan.FromMilliseconds(500),
                    token));
    }

    private Task ReleaseAsync(CancellationToken cancellationToken) =>
        _stages.RunAsync(
            InspectionStages.Release,
            cancellationToken,
            token => _machine.ReleaseAsync(
                InspectionChannels.Stopper,
                InspectionChannels.Align,
                InspectionChannels.Lift,
                token));
}
