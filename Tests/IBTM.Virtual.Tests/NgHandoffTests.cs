using System;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.Storage;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class NgHandoffTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShuttleWaitsForCommandedReleaseAndRaisedOpenPickup(bool repeat)
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var io = system.Io;
        var transfer = system.Transfer;
        io.SetInput(InputIo.NgCarrierDetected, true);
        Assert.False(transfer.IsTransferPending); // Presence alone never establishes pickup ownership.
        await transfer.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.PickingCarrier, CancellationToken.None);
        await transfer.MoveToAsync(new() { X = 10, Y = 10 });
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.True(transfer.IsTransferPending);
        Assert.True(transfer.IsRaised);
        Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);

        var lowering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.NgShuttleDown || !on)
                return;
            Assert.False(transfer.IsTransferPending);
            Assert.True(transfer.IsRaised);
            Assert.Equal(NgTransferGripperState.Open, transfer.Gripper);
            lowering.TrySetResult();
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = repeat ? system.Shuttle.RunRepeatAsync(useConveyor: false, stop.Token)
            : system.Shuttle.RunAsync(stop.Token);
        try
        {
            Assert.Equal(NgShuttleState.WaitingForCarrierPickupUp, system.Shuttle.State);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));
            await Assert.ThrowsAsync<MotionInterlockException>(() => system.Shuttle.SetDownAsync(true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => system.Shuttle.CycleAsync(stop.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => system.Shuttle.ReturnFromConveyorAsync(stop.Token));

            // Unexpected Open feedback is not a commanded handoff.
            io.SetInputs((InputIo.NgCarrierGripperClosed, false), (InputIo.NgCarrierGripperOpen, true));
            Assert.True(transfer.IsTransferPending);
            Assert.Equal(NgShuttleState.WaitingForCarrierPickupUp, system.Shuttle.State);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));

            io.SetInputs((InputIo.NgCarrierPickupUp, false), (InputIo.NgCarrierPickupDown, true));
            var placing = transfer.ExecuteAsync(NgTransferDestination.Shuttle, NgTransferState.PlacingCarrier, stop.Token);
            Assert.False(transfer.IsTransferPending); // Only the completed release command clears it.
            Assert.False(placing.IsCompleted);
            Assert.Equal(NgShuttleState.WaitingForCarrierPickupUp, system.Shuttle.State);

            // Contradictory gripper feedback must still block even after release and ascent.
            io.SetInput(InputIo.NgCarrierGripperClosed, true);
            io.SetInputs((InputIo.NgCarrierPickupDown, false), (InputIo.NgCarrierPickupUp, true));
            await placing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(NgShuttleState.WaitingForCarrierPickupUp, system.Shuttle.State);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));
            io.SetInput(InputIo.NgCarrierGripperClosed, false);
            await lowering.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            stop.Cancel();
            if (repeat)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(1)));
            else
                await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task ShuttleReceiveChangeWakesWaitingInspectionTransfer()
    {
        var system = await CreateAsync();
        using var motion = system.Motion;
        var io = system.Io;
        io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        io.SetInput(InputIo.NgCarrierEjectButton, true);
        Assert.False(system.Shuttle.IsReceiveAllowed(useConveyor: true));
        var lowering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgCarrierPickupDown && on)
                lowering.TrySetResult();
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = system.Inspection.RunAsync([], stop.Token);
        try
        {
            Assert.False(run.IsCompleted);
            Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
            // No transfer or carrier sensor changes: releasing the button alone must wake the loop.
            io.SetInput(InputIo.NgCarrierEjectButton, false);
            Assert.True(system.Shuttle.IsReceiveAllowed(useConveyor: true));
            await lowering.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task<(VirtualIoService Io, VirtualMotionService Motion, NgCarrierTransfer Transfer,
        NgShuttle Shuttle, InspectionStation Inspection)> CreateAsync()
    {
        var io = new VirtualIoService(Outputs(new NgCarrierTransferHardwareSettings(),
            new NgShuttleHardwareSettings(), new NgConveyorHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        var units = new UnitSettings { MainConveyor = false, Inspection = false };
        var settings = new NgCarrierTransferSettings { PickupSafeX = 0, ShuttlePlacePosition = new() { X = 10, Y = 10 } };
        var motionSettings = new InspectionGantrySettings();
        var operations = new OperationCancellation();
        var motion = new VirtualMotionService(motionSettings.Motion, operations, hasZ: false);
        motion.Initialize();
        await motion.HomeAsync(MotionAxis.X, 1_000);
        await motion.HomeAsync(MotionAxis.Y, 1_000);
        var transfer = new NgCarrierTransfer(io, motion, operations, motionSettings, settings, units);
        var conveyor = new NgCarrierConveyor(io, new());
        var shuttle = new NgShuttle(io, conveyor, transfer);
        var recipes = new RecipeManager(OpenMachineStore(), new());
        var work = new InspectionWork(io, transfer, settings, recipes, units);
        var inspection = new InspectionStation(work, transfer, shuttle, units,
            new VirtualCamera(() => (0, 0, 0), () => []), new VirtualLightController(), new(), recipes);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await transfer.Station.SeatAsync(CancellationToken.None);
        return (io, motion, transfer, shuttle, inspection);
    }
}
