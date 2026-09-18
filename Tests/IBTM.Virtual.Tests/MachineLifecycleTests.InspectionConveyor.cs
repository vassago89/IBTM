using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InspectionParksThenDischargesOrRaisesBeforeOtherTransfers(bool ng, bool rearReady)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = true;
        settings.Units.NgCarrierTransfer = ng;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var recipe = services.GetRequiredService<Recipe>();
        PrepareCarrierTeaching(settings, recipe);
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var station = services.GetRequiredService<InspectionStation>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var pickup = services.GetRequiredService<NgCarrierTransfer>();
        services.GetRequiredService<VirtualCamera>().BoltsPresent = !ng;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, rearReady);
        // Start with an occupied, raised S3; the main sequence must lower it for inspection.
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        work.Assembly(HeatSinkSlot.HeatSink1).RecordBarcode("PCB-1");
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
        await ConveyorStation.PcbPlacement(io).SeatAsync(CancellationToken.None);

        var inspected = false;
        var returned = false;
        var raises = new ConcurrentQueue<bool>();
        var conveyorSteps = new ConcurrentQueue<string>();
        conveyor.Trace += conveyorSteps.Enqueue;
        var beltStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discharged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        station.Trace += message =>
        {
            if (message.Contains(": InspectingBolt ", StringComparison.Ordinal))
            {
                Assert.True(work.AtInspectionPosition);
                Assert.False(conveyor.RunCommandOn);
                Assert.Equal(MainConveyorState.WaitingForInspection, conveyor.State);
                inspected = true;
            }
            if (inspected && message.Contains(": ReturningToNgPickup ", StringComparison.Ordinal))
            {
                Assert.False(work.Completed);
                Assert.False(conveyor.RunCommandOn);
                returned = true;
            }
        };
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.InspectionBackupPlateUp && on)
            {
                Assert.True(work.Completed);
                Assert.True(work.IsTransferAtWaitingPosition());
                raises.Enqueue(true);
            }
            if (output == OutputIo.NgCarrierPickupDown && on && work.CarrierPresent)
            {
                Assert.True(ng);
                Assert.True(work.Completed);
                Assert.True(work.CarrierSeated);
            }
            if (output != OutputIo.MainConveyorRun || !on)
                return;
            Assert.True(inspected);
            Assert.True(returned);
            Assert.True(work.Completed);
            if (conveyor.State == MainConveyorState.DischargingInspectionCarrier)
            {
                Assert.False(ng);
                Assert.True(work.IsTransferAtWaitingPosition());
                discharged.TrySetResult();
            }
            else
            {
                Assert.Equal(MainConveyorState.MovingPcbPlacementToBoltFastening, conveyor.State);
                Assert.Equal(StationCylinderState.Up, work.BackupPlate);
            }
            beltStarted.TrySetResult();
        };

        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => beltStarted.Task.IsCompleted, TimeSpan.FromSeconds(5)),
                $"State={conveyor.State}; alarm={services.GetRequiredService<MachineState>().AlarmMessage}; "
                    + string.Join(" | ", conveyorSteps));
            if (!ng && rearReady)
            {
                Assert.Empty(raises);
                Assert.True(discharged.Task.IsCompletedSuccessfully);
            }
            else
            {
                Assert.Single(raises);
                if (ng)
                {
                    Assert.True(await VirtualTest.WaitUntilAsync(
                        () => pickup.CarrierDetected && pickup.IsRaised,
                        TimeSpan.FromSeconds(3)));
                }
                else
                {
                    io.SetInput(InputIo.MainConveyorReadyFromRear, true);
                    Assert.True(await VirtualTest.WaitUntilAsync(
                        () => discharged.Task.IsCompleted, TimeSpan.FromSeconds(5)),
                        $"State={conveyor.State}; completed={work.Completed}; seated={work.CarrierSeated}; "
                            + $"parked={work.IsTransferAtWaitingPosition()}; ready={conveyor.DownstreamReady}; "
                            + $"alarm={services.GetRequiredService<MachineState>().AlarmMessage}; "
                            + string.Join(" | ", conveyorSteps));
                }
            }
            Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InspectionParksWhileIdleAndRejectsResultsIfConveyorStarts()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var recipe = services.GetRequiredService<Recipe>();
        PrepareCarrierTeaching(settings, recipe);
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var station = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await services.GetRequiredService<InspectionGantry>().MoveToAsync(new() { X = 12, Y = 7 }, 10_000);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beltForcedOn = false;
        station.Trace += message =>
        {
            if (message.Contains(": InspectingBolt ", StringComparison.Ordinal))
            {
                beltForcedOn = true;
                io.SetOutput(OutputIo.MainConveyorRun, true);
            }
            if (beltForcedOn && message.Contains("WaitingForInspectionPosition", StringComparison.Ordinal))
                interrupted.TrySetResult();
        };
        using var stop = new CancellationTokenSource();
        var run = station.RunAsync(recipe.Pcb.GetBolts().ToArray(), stop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => work.IsTransferAtWaitingPosition(), TimeSpan.FromSeconds(2)));
            Assert.Equal(InspectionStationState.Waiting, station.State(recipe.Pcb.GetBolts().ToArray()));
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            var assembly = work.Assembly(HeatSinkSlot.HeatSink1);
            assembly.RecordBarcode("PCB-1");
            await work.Station.PrepareToReceiveAsync(CancellationToken.None);
            await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(beltForcedOn);
            Assert.False(work.Completed);
            Assert.Empty(assembly.BoltPresenceResults);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }
}
