using System;
using System.Threading.Tasks;
using IBTM.Core.Machine;
using IBTM.Core.Process;
using IBTM.Orchestration;
using IBTM.Stations.BoltFastening;
using IBTM.Stations.Inspection;
using IBTM.Stations.PcbPlacement;
using IBTM.Transport;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class VirtualOrchestrationTests
{
    [Fact]
    public async Task PipelineCompletesWithConcurrentStationWorkers()
    {
        var io = new VirtualIoService();
        using var pcbPlacementMotion = new VirtualMotionService();
        using var boltFasteningMotion = new VirtualMotionService();
        using var inspectionMotion = new VirtualMotionService();
        var conveyorServo = new VirtualConveyorServo(io);
        using var conveyor = new Conveyor(
            conveyorServo,
            io,
            new ConveyorSettings { Velocity = 100 });
        var events = new ProcessEvents();
        using var pcbPlacement = new PcbPlacementStation(
            pcbPlacementMotion,
            io,
            new StationMotionSettings { SpeedXY = 10_000, SpeedZ = 10_000 },
            events);
        using var boltFastening = new BoltFasteningStation(
            boltFasteningMotion,
            io,
            new BoltFasteningOptions
            {
                Motion = new StationMotionSettings { SpeedXY = 10_000, SpeedZ = 10_000 },
            },
            events,
            new VirtualFiducialService(),
            new VirtualBoltService());
        using var inspection = new InspectionStation(
            inspectionMotion,
            io,
            new InspectionOptions
            {
                Motion = new StationMotionSettings { SpeedXY = 10_000, SpeedZ = 10_000 },
            },
            events,
            new VirtualInspectionService());
        using var orchestrator = new ProcessOrchestrator(
            io,
            conveyor,
            pcbPlacement,
            boltFastening,
            inspection,
            events)
        {
            CurrentRecipe = new Recipe
            {
                BoltFastening = new BoltFasteningRecipe { BoltPoints = [] },
            },
        };
        var cycleCompleted = new TaskCompletionSource<ProductionStats>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        events.StatsUpdated += stats => cycleCompleted.TrySetResult(stats);

        orchestrator.Initialize();
        var running = orchestrator.StartAsync();
        var stats = await cycleCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        orchestrator.Stop();
        await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, stats.TotalCount);
        Assert.False(conveyorServo.IsRunning);
        Assert.False(io.GetOutput(Conveyor.UpstreamMachineReadyOutputChannel));
        Assert.False(io.GetOutput(Conveyor.DownstreamBoardAvailableOutputChannel));
    }
}
