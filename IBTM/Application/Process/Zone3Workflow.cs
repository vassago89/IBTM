
namespace IBTM.Application.Process;

public sealed class Zone3Workflow(
    MachineOperations machine,
    ProcessStageRunner stages,
    ProcessEventHub events,
    IInspectionService inspection,
    MachineConfig config)
{
    private const int Zone = 3;

    public int NgStackCount { get; private set; }
    public int NgStackCapacity => config.NgStackMaxCount;

    public async Task<InspectionResult> RunAsync(
        Recipe recipe,
        CancellationToken cancellationToken)
    {
        await stages.RunAsync(
            ProcessStage.Zone3_WaitShuttle,
            cancellationToken,
            async token =>
            {
                events.Log("[Zone3] Waiting for shuttle", ProcessStage.Zone3_WaitShuttle);
                await machine.WaitForSensorAsync(IoMap.Zone3_Sensor, timeout: null, token);
                events.Log("[Zone3] Shuttle arrived", ProcessStage.Zone3_WaitShuttle);
            });

        await stages.RunAsync(
            ProcessStage.Zone3_StopAlignLift,
            cancellationToken,
            token => machine.StopAlignLiftAsync(
                IoMap.Zone3_Stopper,
                IoMap.Zone3_Align,
                IoMap.Zone3_Lift,
                token));

        var result = await stages.RunAsync(
            ProcessStage.Zone3_Inspect,
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
        events.NgStack(0);
        events.Log("NG stack count reset", ProcessStage.Idle);
    }

    private async Task<InspectionResult> InspectAsync(
        Recipe recipe,
        CancellationToken cancellationToken)
    {
        events.Log("[Zone3] Camera inspection start", ProcessStage.Zone3_Inspect);
        await machine.MoveToPositionAsync(
            Zone,
            recipe.Zone3_InspectPos,
            cancellationToken);

        var outcome = await inspection.InspectAsync(cancellationToken);
        events.Inspection(outcome);
        events.Log(
            $"[Zone3] Inspection result: {outcome.Result.ToString().ToUpperInvariant()}",
            ProcessStage.Zone3_Inspect,
            outcome.Result == InspectionResult.Good ? LogLevel.Info : LogLevel.Warning);
        return outcome.Result;
    }

    private async Task RunNgRouteAsync(Recipe recipe, CancellationToken cancellationToken)
    {
        await stages.RunAsync(
            ProcessStage.Zone3_NgTransfer,
            cancellationToken,
            async token =>
            {
                events.Log("[Zone3] NG → rear stack transfer", ProcessStage.Zone3_NgTransfer);
                await machine.PickAndPlaceAsync(
                    Zone,
                    recipe.Zone3_NgPickupPos,
                    recipe.Zone3_NgPlacePos,
                    IoMap.Zone3_Gripper,
                    token);

                NgStackCount++;
                events.NgStack(NgStackCount);
                events.Log(
                    $"[Zone3] NG stacked ({NgStackCount}/{NgStackCapacity})",
                    ProcessStage.Zone3_NgTransfer,
                    LogLevel.Warning);

                if (NgStackCount >= NgStackCapacity)
                {
                    events.Log(
                        $"[Zone3] NG stack {NgStackCount} full. Operator check required.",
                        ProcessStage.Zone3_NgTransfer,
                        LogLevel.Error);
                    events.NgAlarm(NgStackCount);
                }
            });

        await stages.RunAsync(
            ProcessStage.Zone3_Release,
            cancellationToken,
            token => machine.ReleaseAsync(
                IoMap.Zone3_Stopper,
                IoMap.Zone3_Align,
                IoMap.Zone3_Lift,
                token));
    }

    private async Task RunGoodRouteAsync(CancellationToken cancellationToken)
    {
        await stages.RunAsync(
            ProcessStage.Zone3_SmemaWait,
            cancellationToken,
            async token =>
            {
                events.Log("[Zone3] Waiting for SMEMA signal", ProcessStage.Zone3_SmemaWait);
                await machine.WaitForSensorAsync(
                    IoMap.Smema_MachineReady,
                    TimeSpan.FromSeconds(config.SmemaTimeoutSec),
                    token);
                events.Log("[Zone3] SMEMA ready", ProcessStage.Zone3_SmemaWait);
            });

        await stages.RunAsync(
            ProcessStage.Zone3_Release,
            cancellationToken,
            token => machine.ReleaseAsync(
                IoMap.Zone3_Stopper,
                IoMap.Zone3_Align,
                IoMap.Zone3_Lift,
                token));

        await stages.RunAsync(
            ProcessStage.Zone3_Discharge,
            cancellationToken,
            async token =>
            {
                events.Log("[Zone3] Discharging", ProcessStage.Zone3_Discharge);
                await machine.PulseOutputAsync(
                    IoMap.Smema_BoardAvailable,
                    TimeSpan.FromMilliseconds(500),
                    token);
                events.Log("[Zone3] Discharge complete", ProcessStage.Zone3_Discharge);
            });
    }
}
