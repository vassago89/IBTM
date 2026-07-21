using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Machine;
using IBTM.Core.Process;
using IBTM.Device;

namespace IBTM.Stations.Inspection;

public sealed class InspectionStation : IDisposable
{
    public const int ShuttlePresentInputChannel = 30;
    public const int StopperChannel = 31;
    public const int AlignChannel = 32;
    public const int LiftChannel = 33;
    public const int GripperChannel = 34;
    public const int LaserChannel = 35;
    public const int SmemaReadyInputChannel = 40;
    public const int BoardAvailableChannel = 41;

    private readonly StationOperations _machine;
    private readonly ProcessEvents _events;
    private readonly IInspectionService _inspection;
    private readonly InspectionOptions _options;

    public InspectionStation(
        IMotionService motion,
        IIoService io,
        InspectionOptions options,
        ProcessEvents events,
        IInspectionService inspection)
    {
        _machine = new StationOperations(3, motion, io, options.Motion, events);
        _events = events;
        _inspection = inspection;
        _options = options;
    }

    public int NgStackCount { get; private set; }
    public int NgStackCapacity => _options.NgStackMaxCount;

    public void Initialize() => _machine.Initialize();

    public async Task<InspectionResult> RunAsync(
        InspectionRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _events.RunStageAsync(
            InspectionStages.WaitShuttle,
            cancellationToken,
            token => _machine.WaitForInputAsync(ShuttlePresentInputChannel, token));

        await _events.RunStageAsync(
            InspectionStages.StopAlignLift,
            cancellationToken,
            token => _machine.StopAlignLiftAsync(
                StopperChannel,
                AlignChannel,
                LiftChannel,
                token));

        var result = await _events.RunStageAsync(
            InspectionStages.Inspect,
            cancellationToken,
            token => InspectAsync(recipe, token));

        if (result == InspectionResult.Good)
        {
            await RunGoodRouteAsync(cancellationToken);
        }
        else
        {
            await RunNgRouteAsync(recipe, cancellationToken);
        }

        return result;
    }

    public void ResetNgStack()
    {
        NgStackCount = 0;
        _events.NgStack(0, alarm: false);
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
        await _events.RunStageAsync(
            InspectionStages.NgTransfer,
            cancellationToken,
            async token =>
            {
                await _machine.PickAndPlaceAsync(
                    recipe.NgPickupPosition,
                    recipe.NgPlacePosition,
                    GripperChannel,
                    token);

                NgStackCount++;
                var alarm = NgStackCount >= NgStackCapacity;
                _events.NgStack(NgStackCount, alarm);
            });

        await ReleaseAsync(cancellationToken);
    }

    private async Task RunGoodRouteAsync(CancellationToken cancellationToken)
    {
        await _events.RunStageAsync(
            InspectionStages.SmemaWait,
            cancellationToken,
            token => _machine.WaitForInputAsync(SmemaReadyInputChannel, token));

        await ReleaseAsync(cancellationToken);

        await _events.RunStageAsync(
            InspectionStages.Discharge,
            cancellationToken,
            token => _machine.PulseOutputAsync(
                    BoardAvailableChannel,
                    TimeSpan.FromMilliseconds(500),
                    token));
    }

    private Task ReleaseAsync(CancellationToken cancellationToken) =>
        _events.RunStageAsync(
            InspectionStages.Release,
            cancellationToken,
            token => _machine.ReleaseAsync(
                StopperChannel,
                AlignChannel,
                LiftChannel,
                token));
}
