using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Core.Machine;
using IBTM.Core.Process;
using IBTM.Device;

namespace IBTM.Stations.PcbPlacement;

public sealed class PcbPlacementStation : IDisposable
{
    public const int ShuttlePresentInputChannel = 10;
    public const int StopperChannel = 11;
    public const int AlignChannel = 12;
    public const int LiftChannel = 13;
    public const int GripperChannel = 14;
    public const int LaserChannel = 15;
    public const int PcbAvailableInputChannel = 16;

    private readonly StationOperations _machine;
    private readonly ProcessEvents _events;

    public PcbPlacementStation(
        IMotionService motion,
        IIoService io,
        ZoneMotionParams motionParams,
        ProcessEvents events)
    {
        _machine = new StationOperations(1, motion, io, motionParams, events);
        _events = events;
    }

    public void Initialize() => _machine.Initialize();

    public async Task RunAsync(
        PcbPlacementRecipe recipe,
        CancellationToken cancellationToken)
    {
        await _events.RunStageAsync(
            PcbPlacementStages.WaitShuttle,
            cancellationToken,
            async token =>
            {
                await _machine.WaitForInputAsync(ShuttlePresentInputChannel, token);
                await _machine.WaitForInputAsync(PcbAvailableInputChannel, token);
            });

        await _events.RunStageAsync(
            PcbPlacementStages.StopAlignLift,
            cancellationToken,
            token => _machine.StopAlignLiftAsync(
                StopperChannel,
                AlignChannel,
                LiftChannel,
                token));

        await _events.RunStageAsync(
            PcbPlacementStages.PickPlace,
            cancellationToken,
            async token =>
            {
                await _machine.PickAndPlaceAsync(
                    recipe.PcbPick1,
                    recipe.PcbPlace1,
                    GripperChannel,
                    token);
                await _machine.PickAndPlaceAsync(
                    recipe.PcbPick2,
                    recipe.PcbPlace2,
                    GripperChannel,
                    token);
            });

        await _events.RunStageAsync(
            PcbPlacementStages.Release,
            cancellationToken,
            token => _machine.ReleaseAsync(
                StopperChannel,
                AlignChannel,
                LiftChannel,
                token));
    }

    public void Stop() => _machine.Stop();

    public void EmergencyStop() => _machine.EmergencyStop();

    public void Dispose() => _machine.Dispose();
}
