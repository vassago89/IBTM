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
        var transfer = system.Inspection;
        io.SetInput(InputIo.NgCarrierDetected, true);
        Assert.False(transfer.IsTransferPending); // Presence alone never establishes pickup ownership.
        await transfer.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None);
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
        var run = repeat ? system.Conveyor.RunRepeatAsync(stop.Token)
            : system.Conveyor.RunAsync(stop.Token);
        try
        {
            Assert.Equal(NgConveyorState.WaitingForTransferRelease, system.Conveyor.State);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));
            await Assert.ThrowsAsync<MotionInterlockException>(() => system.Conveyor.SetShuttleDownAsync(true));
            await Assert.ThrowsAsync<InvalidOperationException>(() => system.Conveyor.ReturnFromConveyorAsync(stop.Token));

            // Unexpected Open feedback is not a commanded handoff.
            io.SetInputs((InputIo.NgCarrierGripperClosed, false), (InputIo.NgCarrierGripperOpen, true));
            Assert.True(transfer.IsTransferPending);
            Assert.Equal(NgConveyorState.WaitingForTransferRelease, system.Conveyor.State);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));

            io.SetInputs((InputIo.NgCarrierPickupUp, false), (InputIo.NgCarrierPickupDown, true));
            var placing = transfer.ExecuteTransferAsync(NgTransferDestination.Shuttle, InspectionStationState.PlacingCarrier, stop.Token);
            Assert.False(transfer.IsTransferPending); // Only the completed release command clears it.
            Assert.False(placing.IsCompleted);
            Assert.Equal(NgConveyorState.WaitingForTransferRelease, system.Conveyor.State);

            // Contradictory gripper feedback must still block even after release and ascent.
            io.SetInput(InputIo.NgCarrierGripperClosed, true);
            io.SetInputs((InputIo.NgCarrierPickupDown, false), (InputIo.NgCarrierPickupUp, true));
            await placing.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(NgConveyorState.WaitingForTransferRelease, system.Conveyor.State);
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
        Assert.False(system.Conveyor.IsReceiveAllowed());
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
            var assembly = system.Work.GetAssembly(HeatSinkSlot.HeatSink1);
            assembly.RecordBoltPresence(1, false);
            assembly.CompleteInspection();
            system.Work.Complete(system.Work.CurrentJob);
            // No transfer or carrier sensor changes: releasing the button alone must wake the loop.
            io.SetInput(InputIo.NgCarrierEjectButton, false);
            Assert.True(system.Conveyor.IsReceiveAllowed());
            await lowering.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task<(VirtualIoService Io, VirtualMotionService Motion, InspectionStation Inspection,
        NgCarrierConveyor Conveyor, InspectionWork Work)> CreateAsync()
    {
        var io = new VirtualIoService(Outputs(new NgCarrierTransferHardwareSettings(),
            new NgShuttleHardwareSettings(), new NgConveyorHardwareSettings(), new ConveyorHardwareSettings()), new());
        io.Initialize();
        var units = new UnitSettings { MainConveyor = false };
        var settings = new NgCarrierTransferSettings { PickupSafeX = 0, ShuttlePlacePosition = new() { X = 10, Y = 10 } };
        var motionSettings = new InspectionGantrySettings();
        var operations = new OperationCancellation();
        var motion = new VirtualMotionService(motionSettings.Motion, operations, hasZ: false);
        motion.Initialize();
        await motion.HomeAsync(MotionAxis.X, 1_000);
        await motion.HomeAsync(MotionAxis.Y, 1_000);
        var work = new InspectionWork(io, motion, settings, units);
        var conveyor = new NgCarrierConveyor(io, new(), work, units);
        var recipes = new RecipeManager(OpenMachineStore(), new());
        var inspection = new InspectionStation(work, conveyor, operations, motionSettings, settings, io, units,
            new VirtualCamera(() => (0, 0, 0), () => []), new VirtualLightController(), new(), recipes);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await inspection.Station.SeatAsync(CancellationToken.None);
        return (io, motion, inspection, conveyor, work);
    }
}
