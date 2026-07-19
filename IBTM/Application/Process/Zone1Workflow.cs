
namespace IBTM.Application.Process;

public sealed class Zone1Workflow(
    MachineOperations machine,
    ProcessStageRunner stages,
    ProcessEventHub events)
{
    private const int Zone = 1;

    public async Task RunAsync(Recipe recipe, CancellationToken cancellationToken)
    {
        await stages.RunAsync(
            ProcessStage.Zone1_WaitShuttle,
            cancellationToken,
            async token =>
            {
                events.Log(
                    "[Zone1] Waiting for shuttle + prev equip lane sensor",
                    ProcessStage.Zone1_WaitShuttle);
                await machine.WaitForSensorAsync(IoMap.Zone1_Sensor, timeout: null, token);
                await machine.WaitForSensorAsync(IoMap.PrevRearLaneSensor, timeout: null, token);
                events.Log(
                    "[Zone1] Shuttle + prev equip ready",
                    ProcessStage.Zone1_WaitShuttle);
            });

        await stages.RunAsync(
            ProcessStage.Zone1_StopAlignLift,
            cancellationToken,
            token => machine.StopAlignLiftAsync(
                IoMap.Zone1_Stopper,
                IoMap.Zone1_Align,
                IoMap.Zone1_Lift,
                token));

        await stages.RunAsync(
            ProcessStage.Zone1_PickPlace,
            cancellationToken,
            async token =>
            {
                await PickAndPlaceAsync(
                    recipe.Zone1_PcbPick1,
                    recipe.Zone1_PcbPlace1,
                    "#1",
                    token);
                await PickAndPlaceAsync(
                    recipe.Zone1_PcbPick2,
                    recipe.Zone1_PcbPlace2,
                    "#2",
                    token);
                events.Log(
                    "[Zone1] 2 PCBs placed on carriers",
                    ProcessStage.Zone1_PickPlace);
            });

        await stages.RunAsync(
            ProcessStage.Zone1_Release,
            cancellationToken,
            token => machine.ReleaseAsync(
                IoMap.Zone1_Stopper,
                IoMap.Zone1_Align,
                IoMap.Zone1_Lift,
                token));
    }

    private async Task PickAndPlaceAsync(
        AxisPos pickPosition,
        AxisPos placePosition,
        string label,
        CancellationToken cancellationToken)
    {
        events.Log(
            $"[Zone1] {label} PCB pick → carrier",
            ProcessStage.Zone1_PickPlace);
        await machine.PickAndPlaceAsync(
            Zone,
            pickPosition,
            placePosition,
            IoMap.Zone1_Gripper,
            cancellationToken);
        events.PcbWasPlaced();
    }
}
