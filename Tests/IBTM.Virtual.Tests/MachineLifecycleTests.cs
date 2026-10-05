using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.BoltFeeder;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.NgConveyor;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.UI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static IBTM.Virtual.Tests.MachineTestSupport;
using static IBTM.Virtual.Tests.VirtualTestSupport;

namespace IBTM.Virtual.Tests;

[Collection("Machine integration")]
public sealed class MachineLifecycleTests
{
    [Fact]
    public async Task ScreenResetRefreshesStartReviewAndRequiresNewOperatorConfirmation()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.BoltFastening = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var review = services.GetRequiredService<OperationViewModel>();
        var bolt = new BoltPoint { HeatSink = HeatSinkSlot.HeatSink1, Head = FasteningHead.Shooting };
        services.GetRequiredService<RecipeManager>().Current.Pcb.BoltPoints.Add(bolt);
        await machine.InitializeAsync();
        try
        {
            await WaitUntilAsync(() => review.IsResetAllowed);
            await review.CheckStartCommand.ExecuteAsync(null);
            Assert.Equal(StartCheckState.Empty, machine.StartChecks[StartArea.Station1]);
            Assert.Empty(review.PlacementResumeTargets);
            Assert.Empty(review.FasteningResumeBolts);
            io.SetInputs((InputIo.PcbPlacementHeatSink1Present, true), (InputIo.BoltFasteningHeatSink1Present, true));
            var placed = review.Placement.Station.GetAssembly(HeatSinkSlot.HeatSink1);
            placed.IsPlacementCompleted = true;
            var fastened = review.Fastening.Station.GetAssembly(HeatSinkSlot.HeatSink1);
            fastened.RecordBolt(bolt.Head, bolt.Id, new(true, 1.2));
            state.SetError(MachineAlarm.Inspection);
            review.IsPlacementResumeConfirmed = true;
            review.IsFasteningResumeConfirmed = true;

            await review.ResetCommand.ExecuteAsync(null);

            Assert.False(state.IsError, state.AlarmDetail);
            Assert.Equal(StartCheckState.UnfinishedCarrier, machine.StartChecks[StartArea.Station1]);
            Assert.Equal(StartCheckState.UnfinishedCarrier, machine.StartChecks[StartArea.Station2]);
            Assert.True(Assert.Single(review.PlacementResumeTargets).IsCompleted);
            Assert.True(Assert.Single(review.FasteningResumeBolts).Result!.IsComplete);
            Assert.Same(placed, review.Placement.Station.GetAssembly(HeatSinkSlot.HeatSink1));
            Assert.Same(fastened, review.Fastening.Station.GetAssembly(HeatSinkSlot.HeatSink1));
            Assert.False(review.IsPlacementResumeConfirmed);
            Assert.False(review.IsFasteningResumeConfirmed);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task AllStandbyMovesEnabledStationsInOrderAndPreservesPlacementRecords()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.PcbPlacement = true;
        settings.Units.BoltFastening = true;
        settings.Units.Inspection = true;
        settings.BoltFastening.FirstFasteningHead = FasteningHead.Pickup;
        settings.PcbSupply.TravelZ = 3;
        settings.PcbSupply.HandoffPosition.Z = 7;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var review = services.GetRequiredService<OperationViewModel>();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.PcbSupply.Pcb1PickPosition = new() { X = 10, Y = 20, Z = 15 };
        recipe.PcbPlacement.HeatSink1PcbPlacementPosition = new() { X = 40, Y = 50, Z = 15 };
        recipe.PcbPlacement.HeatSink2PcbPlacementPosition = new() { X = 40, Y = 70, Z = 15 };
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(default);
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            var motions = services.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>();
            var order = new ConcurrentQueue<MotionGroup>();
            foreach (var (group, motion) in motions)
                motion.MovingChanged += moving => { if (moving) order.Enqueue(group); };
            io.SetInputs((InputIo.PcbPlacementHeatSink1Present, true), (InputIo.PcbPlacementHeatSink2Present, true));
            var station = services.GetRequiredService<PcbPlacer>().Station;
            var job = station.CurrentJob;
            var assembly = station.GetAssembly(HeatSinkSlot.HeatSink1);
            assembly.IsPlacementCompleted = true;
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
            review.SelectStartAreaCommand.Execute(StartArea.Station3);
            review.IsPlacementResumeConfirmed = true;
            review.IsFasteningResumeConfirmed = true;

            await review.MoveAllToStandbyCommand.ExecuteAsync(null);

            Assert.False(state.IsError, state.AlarmDetail);
            Assert.Null(review.StartActionMessage);
            Assert.Equal(new[] { MotionGroup.PcbPlacementHandler, MotionGroup.PcbSupply,
                MotionGroup.BoltFastening, MotionGroup.InspectionGantry }, order.Distinct().ToArray());
            Assert.Equal((80d, 70d, 10d), motions[MotionGroup.PcbPlacementHandler].Position);
            Assert.Equal((10d, 20d, 3d), motions[MotionGroup.PcbSupply].Position);
            Assert.Equal((100d, 50d, 0d), motions[MotionGroup.BoltFastening].Position);
            Assert.Equal((5d, 20d, 0d), motions[MotionGroup.InspectionGantry].Position);
            Assert.True(io.GetInput(InputIo.PcbSupplyGripperOpen));
            Assert.False(io.GetInput(InputIo.PcbSupplyIpmFixerForward)); // The actual hardware has one fixer sensor.
            Assert.True(io.GetInput(InputIo.PcbSupplyRotated));
            Assert.Same(job, station.CurrentJob);
            Assert.True(assembly.IsPlacementCompleted);
            Assert.False(station.Completed);
            Assert.False(review.IsPlacementResumeConfirmed);
            Assert.False(review.IsFasteningResumeConfirmed);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task AllStandbyRequiresHomingAndRejectsHeldPcbBeforeAnyOutput()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.PcbPlacement = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => machine.MoveAllToStandbyAsync(default));
            Assert.Contains("HOME", error.Message);
            await machine.HomeAsync(default);
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
            io.SetInput(InputIo.PcbSupplyPcbDetected, true);
            var outputs = new ConcurrentQueue<OutputIo>();
            io.OutputChanged += (output, value) => outputs.Enqueue(output);
            error = await Assert.ThrowsAsync<InvalidOperationException>(() => machine.MoveAllToStandbyAsync(default));
            Assert.Contains("Supply is holding", error.Message);
            Assert.Empty(outputs);
            Assert.True(io.GetOutput(OutputIo.PcbSupplyGripperClosed));
            Assert.False(state.IsError, state.AlarmDetail);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllStandbyStopsBeforeNextStationWhenCancelledOrCylinderTimesOut(bool timeout)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.PcbSupply = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var review = services.GetRequiredService<OperationViewModel>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(default);
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
            io.AutoResponseEnabled = false;
            if (timeout)
                settings.Options.TimeoutMilliseconds = 100;
            var motions = services.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>();
            var supplyPosition = motions[MotionGroup.PcbSupply].Position;
            var moving = review.MoveAllToStandbyCommand.ExecuteAsync(null);
            await WaitForOutputAsync(io, OutputIo.PcbPlacementIpmDown, false);
            Assert.False(moving.IsCompleted);
            if (!timeout)
                await review.StopCommand.ExecuteAsync(null);
            await moving.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(supplyPosition, motions[MotionGroup.PcbSupply].Position);
            Assert.True(io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
            Assert.Equal(timeout ? MachineAlarm.PcbPlacement : MachineAlarm.None, state.Alarm);
            Assert.False(string.IsNullOrWhiteSpace(review.StartActionMessage));
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllStandbyCancelsOnTransientMaterialDetectionOrManualModeLoss(bool changeMode)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.PcbSupply = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var review = services.GetRequiredService<OperationViewModel>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(default);
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
            io.AutoResponseEnabled = false;
            var moving = review.MoveAllToStandbyCommand.ExecuteAsync(null);
            await WaitForOutputAsync(io, OutputIo.PcbPlacementIpmDown, false);
            if (changeMode)
            {
                io.SetInput(InputIo.AutoMode, false);
                io.SetInput(InputIo.AutoMode, true);
            }
            else
            {
                io.SetInput(InputIo.PcbPlacementPcbDetected, true);
                io.SetInput(InputIo.PcbPlacementPcbDetected, false);
            }
            await moving.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
            Assert.Equal(UiText.Get(changeMode ? "Preparation stopped. Check machine status."
                : "Clear the Placement PCB and vacuum before starting a new run."), review.StartActionMessage);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(FasteningHead.Pickup, false)]
    [InlineData(FasteningHead.Shooting, false)]
    [InlineData(FasteningHead.Pickup, true)]
    public async Task FeederMaintenanceWaitsForMotionCleanupAndSafetyStillPreempts(FasteningHead head, bool emergencyStop)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.PickupBoltFeeder = head == FasteningHead.Pickup;
        settings.Units.ShootingBoltFeeder = head == FasteningHead.Shooting;
        settings.BoltFeeder.PickupTimeoutMilliseconds = 80;
        settings.BoltFeeder.ShootingTimeoutMilliseconds = 80;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placer = services.GetRequiredService<PcbPlacer>();
        var motion = services.GetRequiredService<IReadOnlyDictionary<MotionGroup, IXyMotion>>()[MotionGroup.PcbPlacementHandler];
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        await motion.MoveAxisAsync(MotionAxis.Z, 0, 10_000);
        settings.PcbPlacementHandler.Motion.ZSpeed = 5;
        io.SetInput(head == FasteningHead.Pickup ? InputIo.PickupFeederBoltDetected : InputIo.ShootingFeederBoltDetected, false);
        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => state.PendingStop is not null, TimeSpan.FromSeconds(3)), state.AlarmDetail);
            Assert.False(run.IsCompleted);
            Assert.True(placer.IsRunning);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(io.GetOutput(OutputIo.Buzzer));
            if (emergencyStop)
                io.SetInput(InputIo.EmergencyStop1Pressed, true);
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(emergencyStop ? MachineAlarm.EmergencyStop
                : head == FasteningHead.Pickup ? MachineAlarm.PickupBoltFeeder : MachineAlarm.ShootingBoltFeeder, state.Alarm);
            Assert.Null(state.PendingStop);
            Assert.False(placer.IsRunning);
            Assert.False(motion.IsMoving);
            if (!emergencyStop)
                Assert.Equal(settings.PcbPlacementHandler.HandoffPosition.Z, motion.Position.Z);
            await VirtualTestSupport.WaitForOutputAsync(io, OutputIo.Buzzer, true);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionAllowsBothModesAndSelectorChangeStops(
        bool manual)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = true;
        settings.Units.NgConveyor = true;
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        Task run = Task.CompletedTask;
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            VirtualTestSupport.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            var placement = services.GetRequiredService<PcbPlacer>().Station;
            placement.Complete(placement.CurrentJob);

            // The physical selector is ON in TEACHING/MANUAL, OFF in AUTO.
            io.SetInput(InputIo.AutoMode, manual);
            if (manual)
            {
                io.SetInput(InputIo.Door1Open, false);
                Assert.True(state.DoorInterlockReady);
            }
            await WaitUntilAsync(() => machine.IsStartAllowed);
            run = machine.StartAsync();
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => io.GetOutput(OutputIo.MainConveyorRun), TimeSpan.FromSeconds(3)),
                state.AlarmDetail);
            Assert.True(state.AutomaticRunning);

            if (manual)
            {
                io.SetInput(InputIo.Door1Open, true);
                io.SetInput(InputIo.Door1Open, false);
                Assert.Equal(MachineAlarm.None, state.Alarm);
                Assert.True(state.AutomaticRunning);
                io.SetInput(InputIo.Door1Open, true);
            }
            io.SetInput(InputIo.AutoMode, !manual);
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            await WaitUntilAsync(() => machine.IsStartAllowed);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task CameraFailureDuringMaintenanceFinishingStopsImmediately()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.PickupBoltFeeder = true;
        settings.BoltFeeder.PickupTimeoutMilliseconds = 80;
        var camera = new HeldCamera();
        await using var services = new ServiceCollection()
            .AddSingleton(_ => OpenMachineStore())
            .AddVirtualApplication(settings)
            .AddSingleton<ICamera>(camera)
            .BuildServiceProvider();
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var inspection = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        await inspection.Station.PrepareToReceiveAsync(default);
        state.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(MachineState.AutomaticRunning) && state.AutomaticRunning)
                io.SetInput(InputIo.InspectionHeatSink1Present, true);
        };
        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => camera.Capturing.Task.IsCompleted, TimeSpan.FromSeconds(5)),
                $"Start={machine.StartBlock}, step={inspection.Step}, alarm={state.AlarmDetail}");
            io.SetInput(InputIo.PickupFeederBoltDetected, false);
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => state.PendingStop is not null, TimeSpan.FromSeconds(2)));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            camera.Image.TrySetException(new IOException("Camera disconnected during capture."));
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(MachineAlarm.Inspection, state.Alarm);
            Assert.Contains("Camera disconnected", state.AlarmDetail);
            Assert.False(inspection.Station.Completed);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(FasteningHead.Pickup)]
    [InlineData(FasteningHead.Shooting)]
    public async Task FeederMaintenanceEndsPendingBoltWaitWithoutCompletingCarrier(FasteningHead head)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PickupBoltFeeder = head == FasteningHead.Pickup;
        settings.Units.ShootingBoltFeeder = head == FasteningHead.Shooting;
        settings.BoltFeeder.PickupTimeoutMilliseconds = 300;
        settings.BoltFeeder.ShootingTimeoutMilliseconds = 300;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        PrepareCarrierTeaching(settings, recipe);
        foreach (var bolt in recipe.Pcb.BoltPoints)
        {
            bolt.Head = head;
            settings.BoltFastening.InitializeBoltPosition(bolt, settings.CarrierReference);
        }
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        station.StepChanged += () =>
        {
            if (station.Step is BoltFasteningState.Fastening)
                io.SetInput(head == FasteningHead.Pickup ? InputIo.PickupFeederBoltDetected : InputIo.ShootingFeederBoltDetected, false);
        };
        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => station.Step is BoltFasteningState.Waiting, TimeSpan.FromSeconds(3)), state.AlarmDetail);
            io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
            await station.Station.SeatAsync(default);
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(head == FasteningHead.Pickup ? MachineAlarm.PickupBoltFeeder : MachineAlarm.ShootingBoltFeeder, state.Alarm);
            Assert.False(station.Station.Completed);
            Assert.Equal(settings.BoltFastening.SafeZ, station.Motion.Feedback.Position.Z);
            Assert.True(station.IsHorizontalMoveAllowed);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            machine.CheckStartMaterials();
            Assert.Equal(StartCheckState.UnfinishedCarrier, machine.StartChecks[StartArea.Station2]);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task PickupMaintenanceFinishesShootingAndResumesOnlyPickup()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PickupBoltFeeder = true;
        settings.Units.ShootingBoltFeeder = true;
        settings.BoltFeeder.PickupTimeoutMilliseconds = 300;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        var first = new BoltPoint { Head = FasteningHead.Shooting, X = 10, Y = 10 };
        var second = new BoltPoint { Head = FasteningHead.Shooting, X = 20, Y = 10 };
        var last = new BoltPoint { Head = FasteningHead.Shooting, X = 30, Y = 10 };
        var pickup = new BoltPoint { Head = FasteningHead.Pickup, X = 40, Y = 10 };
        recipe.Pcb.BoltPoints = [first, second, last, pickup];
        foreach (var bolt in recipe.Pcb.BoltPoints)
            settings.BoltFastening.InitializeBoltPosition(bolt, settings.CarrierReference);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var feeder = services.GetRequiredService<BoltFeederUnit>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        var started = new ConcurrentQueue<Guid>();
        var pickupTableLowered = false;
        io.OutputChanged += (output, on) =>
        {
            if (output is OutputIo.ShootingBoltStart or OutputIo.PickupBoltStart && on)
            {
                started.Enqueue(station.ActiveBolt!.Id);
                if (station.ActiveBolt.Id == first.Id)
                    io.SetInput(InputIo.PickupFeederBoltDetected, false);
            }
            if (output == OutputIo.PickupTableDown && on)
                pickupTableLowered = true;
            if (output == OutputIo.PickupHeadVacuumPump && on)
                io.SetInput(InputIo.PickupHeadVacuumDetected, true);
        };
        var run = machine.StartAsync();
        try
        {
            Assert.True(await WaitUntilAsync(() => station.Step is BoltFasteningState.Waiting,
                TimeSpan.FromSeconds(3)), state.AlarmDetail);
            SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            await station.Station.SeatAsync(default);
            var job = station.Station.CurrentJob;
            Assert.True(await WaitUntilAsync(() => state.PendingStop is not null,
                TimeSpan.FromSeconds(3)), state.AlarmDetail);
            Assert.True(conveyor.IsTransferPaused);
            Assert.True(feeder.IsRunning);
            Assert.NotNull(feeder.PickupEmptyAlarm);
            Assert.Null(feeder.ShootingEmptyAlarm);
            // Replenishment alone must not skip the maintenance stop at pickup entry.
            io.SetInput(InputIo.PickupFeederBoltDetected, true);
            await run.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal(MachineAlarm.PickupBoltFeeder, state.Alarm);
            Assert.Equal(new[] { first.Id, second.Id, last.Id }, started.ToArray());
            Assert.False(pickupTableLowered);
            Assert.False(station.Station.Completed);
            Assert.Same(job, station.Station.CurrentJob);
            var assembly = Assert.Single(station.Station.Assemblies);
            var completed = assembly.ShootingBoltResults.ToDictionary();
            Assert.Equal(3, completed.Count);
            Assert.All(completed.Values, result => Assert.True(result.IsComplete));
            Assert.Empty(assembly.PickupBoltResults);
            Assert.True(station.IsHorizontalMoveAllowed);
            Assert.Equal(settings.BoltFastening.SafeZ, station.Motion.Feedback.Position.Z);
            Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));

            await machine.ResetAsync();
            machine.CheckStartMaterials();
            var startupEdgeInjected = false;
            services.GetRequiredService<PcbPlacer>().Trace += detail =>
            {
                if (detail != "PcbPlacer: run started.")
                    return;
                // I/O can change after cycle subscription but before the feeder starts its new run.
                Assert.False(feeder.IsRunning);
                Assert.NotNull(feeder.PickupEmptyAlarm);
                io.SetInput(InputIo.PickupFeederBoltDetected, false);
                io.SetInput(InputIo.PickupFeederBoltDetected, true);
                startupEdgeInjected = true;
            };
            run = machine.StartAsync(resumeFastening: job);
            Assert.True(await WaitUntilAsync(() => station.Station.Completed,
                TimeSpan.FromSeconds(6)), state.AlarmDetail);
            Assert.True(startupEdgeInjected);
            Assert.Null(state.PendingStop);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.Equal(new[] { first.Id, second.Id, last.Id, pickup.Id }, started.ToArray());
            Assert.Single(assembly.PickupBoltResults);
            foreach (var (id, result) in completed)
                Assert.Same(result, assembly.ShootingBoltResults[id]);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(FasteningHead.Shooting, PickupFasteningMode.SingleStage)]
    [InlineData(FasteningHead.Pickup, PickupFasteningMode.TwoStage)]
    public async Task MaintenanceFinishesMeasuredBoltAndKeepsCompletedCarrier(
        FasteningHead emptyFeeder, PickupFasteningMode pickupMode)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PickupBoltFeeder = true;
        settings.Units.ShootingBoltFeeder = true;
        settings.BoltFastening.PickupFasteningMode = pickupMode;
        if (emptyFeeder == FasteningHead.Pickup)
            settings.BoltFeeder.PickupTimeoutMilliseconds = 30;
        else
            settings.BoltFeeder.ShootingTimeoutMilliseconds = 30;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        PrepareCarrierTeaching(settings, recipe);
        foreach (var bolt in recipe.Pcb.BoltPoints)
        {
            bolt.Head = FasteningHead.Pickup;
            settings.BoltFastening.InitializeBoltPosition(bolt, settings.CarrierReference);
        }
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        io.SetInput(InputIo.ShootingFeederBoltDetected, true);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupHeadVacuumPump && on)
                io.SetInput(InputIo.PickupHeadVacuumDetected, true);
            if (output == OutputIo.PickupBoltStart && on)
                io.SetInput(emptyFeeder == FasteningHead.Pickup
                    ? InputIo.PickupFeederBoltDetected : InputIo.ShootingFeederBoltDetected, false);
        };
        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => station.Step is BoltFasteningState.Waiting, TimeSpan.FromSeconds(3)), state.AlarmDetail);
            io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
            await station.Station.SeatAsync(default);
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(emptyFeeder == FasteningHead.Pickup
                ? MachineAlarm.PickupBoltFeeder : MachineAlarm.ShootingBoltFeeder, state.Alarm);
            Assert.True(station.Station.Completed, state.AlarmDetail);
            var result = Assert.Single(Assert.Single(station.Station.Assemblies).PickupBoltResults).Value;
            Assert.True(result.IsComplete);
            Assert.Equal(pickupMode == PickupFasteningMode.TwoStage
                ? BoltFasteningStage.Final : BoltFasteningStage.Single, result.Stage);
            Assert.Equal(settings.BoltFastening.SafeZ, station.Motion.Feedback.Position.Z);
            Assert.True(station.IsHorizontalMoveAllowed);
            machine.CheckStartMaterials();
            Assert.Equal(StartCheckState.Completed, machine.StartChecks[StartArea.Station2]);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task MaintenanceFinishesPlacementCarrierAndLeavesRestartableMaterial()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.PcbPlacement = true;
        settings.Units.PickupBoltFeeder = true;
        settings.BoltFeeder.PickupTimeoutMilliseconds = 80;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.PcbSupply.Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 };
        recipe.PcbSupply.Pcb2PickPosition = new() { X = 30, Y = 10, Z = 5 };
        recipe.PcbPlacement.HeatSink1PcbPlacementPosition = new() { X = 50, Y = 60, Z = 15 };
        recipe.PcbPlacement.HeatSink2PcbPlacementPosition = new() { X = 90, Y = 60, Z = 15 };
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placer = services.GetRequiredService<PcbPlacer>();
        var supplier = services.GetRequiredService<PcbSupplier>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        io.SetInput(InputIo.AutoMode, false);
        var receipts = 0;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementVacuumEjector && on)
            {
                receipts++;
                io.SetInput(InputIo.PickupFeederBoltDetected, false);
            }
        };
        state.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(MachineState.AutomaticRunning) && state.AutomaticRunning)
                io.SetInputs((InputIo.PcbPlacementHeatSink1Present, true), (InputIo.PcbPlacementHeatSink2Present, true));
        };
        var run = machine.StartAsync();
        try
        {
            Assert.True(await WaitUntilAsync(() => state.AutomaticRunning, TimeSpan.FromSeconds(2)), state.AlarmDetail);
            await placer.Station.SeatAsync(default);
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(MachineAlarm.PickupBoltFeeder, state.Alarm);
            Assert.Equal(2, receipts);
            Assert.True(placer.Station.Completed, state.AlarmDetail);
            Assert.Equal(2, placer.Station.Assemblies.Count());
            Assert.False(placer.PcbSecured);
            machine.CheckStartMaterials();
            Assert.Equal(StartCheckState.Completed, machine.StartChecks[StartArea.Station1]);
            Assert.Equal(StartCheckState.Empty, machine.StartChecks[StartArea.Placement]);
            Assert.Contains(machine.StartChecks[StartArea.Supply], new[] { StartCheckState.Empty, StartCheckState.HandoffReady });
            Assert.Equal(settings.PcbPlacementHandler.HandoffPosition.Z, placer.Motion.Feedback.Position.Z);
            Assert.Equal(settings.PcbPlacementHandler.HandoffPosition.X, placer.Motion.Feedback.Position.X);
            Assert.Equal(60, placer.Motion.Feedback.Position.Y);
            Assert.False(placer.Motion.Feedback.IsMoving);
            Assert.False(supplier.Motion.Feedback.IsMoving);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task MaintenanceKeepsSupplyPcbAtHandoffWhenPlacementHasNoCarrier()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.PcbPlacement = true;
        settings.Units.MainConveyor = true;
        settings.Units.PickupBoltFeeder = true;
        settings.BoltFeeder.PickupTimeoutMilliseconds = 80;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.PcbSupply.Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 };
        recipe.PcbSupply.Pcb2PickPosition = new() { X = 30, Y = 10, Z = 5 };
        recipe.PcbPlacement.HeatSink1PcbPlacementPosition = new() { X = 50, Y = 60, Z = 15 };
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placer = services.GetRequiredService<PcbPlacer>();
        var supplier = services.GetRequiredService<PcbSupplier>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        // Supply may prepare a PCB, but Placement must wait for its carrier outside the handoff.
        io.InputChanged += (input, on) =>
        {
            if (input == InputIo.MainConveyorAvailableFromFront2 && on)
                io.SetInput(input, false);
        };
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        io.SetInput(InputIo.AutoMode, false);
        var run = machine.StartAsync();
        try
        {
            Assert.True(await WaitUntilAsync(
                () => placer.Step is PcbPlacementState.WaitingForCarrier && supplier.Handoff == PcbSupplyHandoff.Holding,
                TimeSpan.FromSeconds(5)), state.AlarmDetail);
            io.SetInput(InputIo.PickupFeederBoltDetected, false);

            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(MachineAlarm.PickupBoltFeeder, state.Alarm);
            Assert.Null(state.PendingStop);
            Assert.False(state.AutomaticRunning);
            Assert.False(placer.Station.CarrierPresent);
            Assert.False(placer.PcbSecured);
            Assert.True(supplier.PcbSecured);
            Assert.False(placer.Motion.Feedback.IsMoving);
            Assert.Equal(settings.PcbPlacementHandler.HandoffPosition.Z, placer.Motion.Feedback.Position.Z);
            Assert.Equal(settings.PcbPlacementHandler.HandoffPosition.X, placer.Motion.Feedback.Position.X);
            Assert.Equal(60, placer.Motion.Feedback.Position.Y);
            Assert.False(io.GetOutput(OutputIo.PcbPlacementVacuumEjector));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));

            await machine.ResetAsync();
            Assert.Equal(MachineAlarm.None, state.Alarm);
            machine.CheckStartMaterials();
            Assert.Equal(StartCheckState.Empty, machine.StartChecks[StartArea.Placement]);
            Assert.Equal(StartCheckState.HandoffReady, machine.StartChecks[StartArea.Supply]);
            Assert.False(state.AutomaticRunning);
            Assert.False(placer.PcbSecured);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task PlacementVacuumMaintenanceKeepsPlacementAlarmAndSupplyGrip()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.PcbPlacement = true;
        settings.Options.TimeoutMilliseconds = 600;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.PcbSupply.Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 };
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supplier = services.GetRequiredService<PcbSupplier>();
        var placer = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        io.SetInput(InputIo.AutoMode, false);
        io.InputChanged += (input, on) =>
        {
            if (input == InputIo.PcbPlacementVacuumDetected && on)
                io.SetInput(input, false);
        };
        var run = machine.StartAsync();
        try
        {
            Assert.True(await WaitUntilAsync(() => state.AutomaticRunning, TimeSpan.FromSeconds(2)), state.AlarmDetail);
            io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
            await placer.Station.SeatAsync(default);
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(MachineAlarm.PcbPlacement, state.Alarm);
            Assert.True(supplier.IsHandoffRestartAllowed, state.AlarmDetail);
            Assert.True(supplier.PcbSecured);
            Assert.False(placer.Station.Completed);
            Assert.False(placer.Motion.Feedback.IsMoving);
            machine.CheckStartMaterials();
            Assert.Equal(StartCheckState.HandoffReady, machine.StartChecks[StartArea.Supply]);
            Assert.Equal(StartCheckState.MaterialRemaining, machine.StartChecks[StartArea.Placement]);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task MaintenanceLetsSupplyReachHandoffBeforeStopping()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.PickupBoltFeeder = true;
        settings.BoltFeeder.PickupTimeoutMilliseconds = 30;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.PcbSupply.Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 };
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supplier = services.GetRequiredService<PcbSupplier>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        io.SetInput(InputIo.AutoMode, false);
        supplier.StepChanged += () =>
        {
            if (supplier.Step is PcbSupplyState.PickingPcb)
                io.SetInput(InputIo.PickupFeederBoltDetected, false);
        };
        var run = machine.StartAsync();
        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(MachineAlarm.PickupBoltFeeder, state.Alarm);
            Assert.True(supplier.IsHandoffRestartAllowed, state.AlarmDetail);
            Assert.True(supplier.PcbSecured);
            machine.CheckStartMaterials();
            Assert.Equal(StartCheckState.HandoffReady, machine.StartChecks[StartArea.Supply]);
            Assert.False(supplier.Motion.Feedback.IsMoving);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    private sealed class HeldCamera : ICamera
    {
        public HeldCamera()
        {
            Capturing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Image = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource Capturing { get; }
        public TaskCompletionSource<ImageFrame> Image { get; }
        public event Action<ImageFrame>? FrameReady { add { } remove { } }
        public event Action<Exception>? LiveViewFailed { add { } remove { } }
        public bool IsLiveView => false;
        public (int Width, int Height) FrameSize => (640, 480);
        public void Initialize() { }
        public void StartLiveView() { }
        public void StopLiveView() { }
        public async Task<ImageFrame> CaptureAsync(CancellationToken cancellationToken = default)
        {
            Capturing.TrySetResult();
            return await Image.Task.WaitAsync(cancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledPlacementAndFasteningTransferCarrierThroughBothStations(bool raisedAtStart)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var fastening = services.GetRequiredService<BoltFasteningStation>().Station;
        var inspection = services.GetRequiredService<InspectionStation>();
        var fasteningPlate = new ConcurrentQueue<bool>();
        var workOutputs = new ConcurrentQueue<OutputIo>();
        await machine.InitializeAsync();
        if (raisedAtStart)
        {
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementBackupPlateUp, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementStopperUp, true);
        }
        io.SetInputs(
            (InputIo.MainConveyorAvailableFromFront2, false),
            (InputIo.MainConveyorReadyFromRear, false),
            (InputIo.NgCarrierDetected, false),
            (InputIo.PcbPlacementHeatSink1Present, false),
            (InputIo.PcbPlacementHeatSink2Present, true));
        var placement = services.GetRequiredService<PcbPlacer>().Station;
        placement.Complete(placement.CurrentJob);
        // Match the equipment case: HS2 only and the empty next station raised.
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionStopperUp, true);
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.BoltFasteningBackupPlateUp)
                fasteningPlate.Enqueue(value);
            if (value && output is OutputIo.PcbPlacementHandlerDown or OutputIo.PcbPlacementVacuumEjector
                or OutputIo.ShootBolt or OutputIo.ShootingHeadDown or OutputIo.PickupHeadDown
                or OutputIo.NgCarrierPickupDown or OutputIo.NgCarrierGripperClose)
                workOutputs.Enqueue(output);
        };
        Assert.False(services.GetRequiredService<UnitSettings>().BoltFastening);
        Assert.True(machine.IsStartAllowed);
        var run = machine.StartAsync();
        try
        {
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(() => inspection.Station.Completed, TimeSpan.FromSeconds(5)),
                $"Conveyor={conveyor.Step}, FasteningCompleted={fastening.Completed}, "
                    + $"FasteningSeated={fastening.CarrierSeated}, InspectionCanReceive={inspection.IsReceiveAllowed}, "
                    + $"Alarm={state.AlarmMessage}");
            Assert.Equal(new[] { true, false }, fasteningPlate);
            Assert.False(fastening.CarrierPresent);
            Assert.False(inspection.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink1));
            Assert.True(inspection.Station.IsHeatSinkPresent(HeatSinkSlot.HeatSink2));
            Assert.False(io.GetInput(InputIo.NgCarrierDetected));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.True(state.AutomaticRunning);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.True(inspection.Station.Completed);
            Assert.Empty(inspection.Station.Assemblies);
            Assert.Empty(workOutputs);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InspectionRequiresBoltsAsWellAsDataMatrixTeaching()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        TeachInspectionFovs(recipe);
        var machine = services.GetRequiredService<MachineController>();
        Assert.False(machine.TeachingReady);

        recipe.Pcb.BoltPoints.Add(new() { Id = VirtualTestSupport.BoltId(1), X = 10, Y = 10 });
        TeachInspectionFovs(recipe);
        Assert.True(machine.TeachingReady);
        settings.Units.Inspection = false;
        recipe.Pcb.BoltPoints.Clear();
        Assert.True(machine.TeachingReady);
    }

    [Theory]
    [InlineData(MachineUnit.BoltFastening)]
    [InlineData(MachineUnit.Inspection)]
    public async Task UntaughtPresentHeatSinkDoesNotCompleteProduction(MachineUnit unit)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(unit);
        settings.Units.ShootingBoltFeeder = unit == MachineUnit.BoltFastening;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        PrepareCarrierTeaching(settings, recipe);
        foreach (var bolt in recipe.Pcb.BoltPoints.Where(bolt => bolt.HeatSink == HeatSinkSlot.HeatSink2).ToArray())
            recipe.Pcb.BoltPoints.Remove(bolt);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        ConveyorStation work = unit == MachineUnit.Inspection
            ? services.GetRequiredService<InspectionStation>().Station
            : services.GetRequiredService<BoltFasteningStation>().Station;
        (InputIo HeatSink, OutputIo Plate, OutputIo Stopper) station = unit == MachineUnit.Inspection
            ? (InputIo.InspectionHeatSink2Present,
                OutputIo.InspectionBackupPlateUp, OutputIo.InspectionStopperUp)
            : (InputIo.BoltFasteningHeatSink2Present,
                OutputIo.BoltFasteningBackupPlateUp, OutputIo.BoltFasteningStopperUp);
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            io.AutoResponseEnabled = false;
            var plate = io.GetOutputFeedback(station.Plate)!;
            var stopper = io.GetOutputFeedback(station.Stopper)!;
            var inspecting = unit == MachineUnit.Inspection;
            io.SetInput(plate.OffInput!.Value, inspecting);
            io.SetInput(plate.OnInput, !inspecting);
            io.SetInput(stopper.OnInput, inspecting);
            io.SetInput(stopper.OffInput!.Value, !inspecting);
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(() => machine.IsStartAllowed, TimeSpan.FromSeconds(2)),
                $"START blocked: {machine.StartBlock}; busy={state.IsRunning}; alarm={state.AlarmDetail}");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var run = machine.StartAsync(stop.Token);
            await WaitUntilAsync(() => state.AutomaticRunning);
            io.SetInput(station.HeatSink, true);
            await run;
            Assert.False(work.Completed);
            Assert.Equal(unit == MachineUnit.Inspection ? MachineAlarm.Inspection : MachineAlarm.BoltFastening, state.Alarm);
            Assert.Contains("no taught bolts", state.AlarmMessage);
            Assert.Empty(work.Assemblies);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(PlatePreparationOutcome.Arrived)]
    [InlineData(PlatePreparationOutcome.Stopped)]
    [InlineData(PlatePreparationOutcome.TimedOut)]
    public async Task StartPreparesEmptyBackupPlatesBeforeStartingUnits(PlatePreparationOutcome outcome)
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.MainConveyor),
            Options = new() { TimeoutMilliseconds = 500 },
        };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await WaitUntilAsync(() => machine.IsStartAllowed);
        io.AutoResponseEnabled = false;
        OutputIo[] plates =
        [
            OutputIo.PcbPlacementBackupPlateUp,
            OutputIo.BoltFasteningBackupPlateUp,
            OutputIo.InspectionBackupPlateUp,
        ];
        foreach (var plate in plates)
        {
            var feedback = io.GetOutputFeedback(plate)!;
            io.SetOutput(plate, true);
            io.SetInput(feedback.OnInput, true);
            io.SetInput(feedback.OffInput!.Value, false);
        }
        var conveyorStarted = false;
        // Teaching suppresses SMEMA, so observe automatic start directly.
        state.Changed += () => conveyorStarted |= state.AutomaticRunning;

        var run = machine.StartAsync();
        try
        {
            await WaitUntilAsync(() => plates.All(plate => !io.GetOutput(plate)));
            foreach (var plate in plates.Take(2))
            {
                var feedback = io.GetOutputFeedback(plate)!;
                io.SetInput(feedback.OnInput, false);
                io.SetInput(feedback.OffInput!.Value, true);
            }
            Assert.False(conveyorStarted);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));

            if (outcome == PlatePreparationOutcome.Arrived)
            {
                var feedback = io.GetOutputFeedback(plates[2])!;
                io.SetInput(feedback.OnInput, false);
                io.SetInput(feedback.OffInput!.Value, true);
                Assert.True(
                    await VirtualTestSupport.WaitUntilAsync(() => conveyorStarted, TimeSpan.FromSeconds(2)),
                    $"Automatic={state.AutomaticRunning}, Alarm={state.AlarmMessage}, "
                        + $"Conveyor={services.GetRequiredService<MainConveyor>().Step}, "
                        + $"Teaching={io.GetInput(InputIo.AutoMode)}");
                Assert.True(state.AutomaticRunning);
                Assert.All(plates, plate => Assert.False(io.GetOutput(plate)));
                machine.Stop();
            }
            else if (outcome == PlatePreparationOutcome.Stopped)
            {
                machine.Stop();
            }

            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(outcome == PlatePreparationOutcome.Arrived, conveyorStarted);
            Assert.Equal(outcome == PlatePreparationOutcome.TimedOut ? MachineAlarm.MainConveyor : MachineAlarm.None, state.Alarm);
            if (outcome == PlatePreparationOutcome.TimedOut)
                Assert.Contains("Inspection Backup Plate Down=ON timeout", state.AlarmMessage);
            Assert.False(state.IsRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(HeatSinkSlot.HeatSink1)]
    [InlineData(HeatSinkSlot.HeatSink2)]
    public async Task InspectionRecordsNgAndFinishesEachPcbBeforeTheNext(HeatSinkSlot unreadPcb)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [
            new() { Id = VirtualTestSupport.BoltId(4), HeatSink = HeatSinkSlot.HeatSink2, X = 30, Y = 20 },
            new() { Id = VirtualTestSupport.BoltId(3), HeatSink = HeatSinkSlot.HeatSink1, X = 10, Y = 20 },
            new() { Id = VirtualTestSupport.BoltId(2), HeatSink = HeatSinkSlot.HeatSink2, X = 30, Y = 10 },
            new() { Id = VirtualTestSupport.BoltId(1), HeatSink = HeatSinkSlot.HeatSink1, X = 10, Y = 10 },
        ];
        TeachInspectionFovs(recipe);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var work = services.GetRequiredService<InspectionStation>();
        var camera = services.GetRequiredService<VirtualCamera>();
        var io = services.GetRequiredService<VirtualIoService>();
        settings.CarrierReference.UpperLeftLocatingPin = null;
        settings.CarrierReference.LowerRightLocatingPin = null;
        foreach (var bolt in recipe.Pcb.BoltPoints)
        {
            bolt.FasteningX = null;
            bolt.FasteningY = null;
        }
        Assert.True(machine.TeachingReady);
        settings.Units.BoltFastening = true;
        settings.Units.ShootingBoltFeeder = true;
        Assert.False(machine.TeachingReady);
        settings.Units.BoltFastening = false;
        settings.Units.ShootingBoltFeeder = false;
        var boltFov = recipe.CarrierImages.First(fov => fov.BoltId is not null);
        var boltRegion = boltFov.Region;
        boltFov.Region = null;
        Assert.False(machine.TeachingReady);
        boltFov.Region = boltRegion;
        Assert.True(machine.TeachingReady);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);

        var inspector = services.GetRequiredService<InspectionStation>();
        var barcodeResult = await inspector.ReadBarcodeAsync(HeatSinkSlot.HeatSink2, CancellationToken.None);
        Assert.True(barcodeResult.Success);
        Assert.Equal("PCB-2", barcodeResult.Barcode);
        Assert.True(VirtualTestSupport.IsAt(inspector.Motion.Feedback, recipe.GetInspectionPosition(inspector.GetBarcodeFov(HeatSinkSlot.HeatSink2))));

        var barcodeImage = barcodeResult.Frame;
        var blankImage = barcodeImage with { Pixels = new byte[barcodeImage.Pixels.Length] };
        var view = services.GetRequiredService<OperationViewModel>();
        var transfer = services.GetRequiredService<InspectionStation>();
        var captures = new List<(HeatSinkSlot Pcb, Guid? Bolt)>();
        inspector.Trace += message =>
        {
            if (!message.StartsWith("InspectionStation: InspectingPoint ", StringComparison.Ordinal))
                return;
            if (inspector.ActiveBolt is null)
            {
                Assert.Null(inspector.ActiveBolt);
                Assert.Contains("Data Matrix", message);
                camera.SourceImage = inspector.ActivePcb == unreadPcb ? blankImage : null;
            }
            else
            {
                camera.SourceImage = inspector.ActiveBolt?.Id == VirtualTestSupport.BoltId(3) ? blankImage : null;
            }
        };
        inspector.InspectionCaptured += (image, pcb, boltId) =>
        {
            captures.Add((pcb, boltId));
            var fov = recipe.CarrierImages.Single(fov => fov.HeatSink == pcb
                && (boltId is null ? fov.IsBarcode : !fov.IsBarcode && fov.BoltId == boltId));
            Assert.Equal((recipe.GetInspectionPosition(fov).X, recipe.GetInspectionPosition(fov).Y, 0d), transfer.Motion.Feedback.Position);
            Assert.False(transfer.Motion.Feedback.IsMoving);
            Assert.Equal(pcb, inspector.ActivePcb);
            Assert.Equal(boltId, inspector.ActiveBolt?.Id);
            Assert.Equal($"{pcb.GetDescription()} · " + (boltId is null ? "Data Matrix" : $"Bolt {recipe.Pcb.GetBoltOrdinal(boltId.Value)}"),
                view.InspectionImageCaption);
            if (boltId is null && pcb != unreadPcb)
                Assert.Equal(pcb == HeatSinkSlot.HeatSink1 ? "PCB-1" : "PCB-2",
                    DataMatrixReader.Read(image, fov.Region!, recipe.BoltInspection.GetDataMatrix(pcb))?.Text);
        };
        await work.Station.PrepareToReceiveAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => state.FeedbackReadiness.Homed);
        Assert.True(machine.IsStartAllowed);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = machine.StartAsync(stop.Token);
        try
        {
            await WaitUntilAsync(() => state.AutomaticRunning);
            io.SetInputs((InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, true));
            Assert.True(work.IsAtInspectionPosition);
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => work.Station.Completed, TimeSpan.FromSeconds(3)),
                $"Alarm={state.Alarm}; inspection={inspector.NextStep}; captures={string.Join(", ", captures)}; {state.AlarmDetail}");
            Assert.Equal(new (HeatSinkSlot, Guid?)[] {
                (HeatSinkSlot.HeatSink1, null), (HeatSinkSlot.HeatSink1, VirtualTestSupport.BoltId(3)), (HeatSinkSlot.HeatSink1, VirtualTestSupport.BoltId(1)),
                (HeatSinkSlot.HeatSink2, null), (HeatSinkSlot.HeatSink2, VirtualTestSupport.BoltId(4)), (HeatSinkSlot.HeatSink2, VirtualTestSupport.BoltId(2)),
            }, captures);
            foreach (var pcb in Enum.GetValues<HeatSinkSlot>())
            {
                var assembly = work.Station.GetAssembly(pcb);
                Assert.Equal(pcb == unreadPcb ? AssemblyResult.Ng : AssemblyResult.Ok, assembly.PcbBarcodeResult);
                Assert.Equal(pcb == unreadPcb ? null : pcb == HeatSinkSlot.HeatSink1 ? "PCB-1" : "PCB-2", assembly.PcbBarcode);
                Assert.Equal(2, assembly.BoltPresenceResults.Count);
                Assert.Equal(pcb == unreadPcb || pcb == HeatSinkSlot.HeatSink1 ? AssemblyResult.Ng : AssemblyResult.Ok,
                    assembly.InspectionResult);
            }
            Assert.True(work.Station.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults[VirtualTestSupport.BoltId(1)]);
            Assert.False(work.Station.GetAssembly(HeatSinkSlot.HeatSink1).BoltPresenceResults[VirtualTestSupport.BoltId(3)]);
            Assert.All(work.Station.GetAssembly(HeatSinkSlot.HeatSink2).BoltPresenceResults.Values, Assert.True);
            Assert.Equal("NG · Not Read", unreadPcb == HeatSinkSlot.HeatSink1 ? view.InspectionPcb1Barcode : view.InspectionPcb2Barcode);
            Assert.True(work.HasNg);
            Assert.True(work.RouteToNg);
            Assert.True(state.AutomaticRunning);
            Assert.False(run.IsCompleted);
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
            Assert.Null(state.AlarmMessage);
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task ManualShootingLatchesUntilOffOrMachineStop()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.ShootingTubeBoltDetected, true);
        io.SetInput(InputIo.ShootingHeadVacuumDetected, false);
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        var shoot = TeachingRows(teaching)[OutputIo.ShootBolt];
        var outputs = new List<OutputIo>();
        io.OutputChanged += (output, _) => outputs.Add(output);
        await WaitUntilAsync(() => shoot.IsToggleOutputAllowed);
        await shoot.ToggleOutputCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(io.GetOutput(OutputIo.ShootBolt));
        Assert.True(state.ManualSetupEnabled);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);

        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        teaching.Deactivate();
        Assert.True(io.GetOutput(OutputIo.ShootBolt));
        Assert.Single(outputs);

        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        await WaitUntilAsync(() => shoot.IsToggleOutputAllowed);
        await shoot.ToggleOutputCommand.ExecuteAsync(null);
        Assert.False(io.GetOutput(OutputIo.ShootBolt));
        Assert.All(outputs, output => Assert.Equal(OutputIo.ShootBolt, output));

        await WaitUntilAsync(() => shoot.IsToggleOutputAllowed);
        await shoot.ToggleOutputCommand.ExecuteAsync(null);
        machine.Stop();
        Assert.False(io.GetOutput(OutputIo.ShootBolt));

        await WaitUntilAsync(() => shoot.IsToggleOutputAllowed);
        await shoot.ToggleOutputCommand.ExecuteAsync(null);
        io.SetInput(InputIo.AutoMode, false);
        Assert.False(io.GetOutput(OutputIo.ShootBolt));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await WaitUntilAsync(() => !shoot.IsToggleOutputAllowed);
    }

    [Fact]
    public async Task StoppedNgTransferStartsWithHeldCarrierAndPlacesItOnShuttle()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.MainConveyor = true;
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionStation>();
        services.GetRequiredService<UnitSettings>().NgConveyor = true;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        await services.GetRequiredService<InspectionStation>().Station.PrepareToReceiveAsync(CancellationToken.None);
        services.GetRequiredService<VirtualCamera>().BoltsPresent = false;
        // Presence and a closed empty gripper must not skip the first pickup descent.
        await services.GetRequiredService<InspectionStation>().SetGripperOpenAsync(false);
        io.SetInput(InputIo.NgCarrierDetected, true);
        var pickupDescents = 0;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgCarrierPickupDown && on)
                pickupDescents++;
        };
        io.SetInput(InputIo.AutoMode, false);
        void StopDuringTransfer(double x, double y, double z)
        {
            if (x > 40 && io.GetInput(InputIo.NgCarrierDetected))
            {
                machine.Stop();
            }
        }

        gantry.Motion.Feedback.PositionChanged += StopDuringTransfer;
        using var initialTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var initialRun = machine.StartAsync(initialTimeout.Token);
        await WaitUntilAsync(() => state.AutomaticRunning);
        VirtualTestSupport.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        await initialRun;
        gantry.Motion.Feedback.PositionChanged -= StopDuringTransfer;
        Assert.False(initialTimeout.IsCancellationRequested,
            $"Inspection={gantry.NextStep}, Main={services.GetRequiredService<MainConveyor>().Step}, "
            + $"Completed={services.GetRequiredService<InspectionStation>().Station.Completed}, NG={services.GetRequiredService<InspectionStation>().HasNg}, "
            + $"Position={gantry.Motion.Feedback.Position}, Lift={gantry.Lift}, Gripper={gantry.Gripper}, Alarm={state.AlarmDetail}");
        var stoppedX = gantry.Motion.Feedback.Position.X;
        Assert.InRange(stoppedX, 40, 149);
        Assert.Equal(1, pickupDescents);
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.True(io.GetInput(InputIo.NgCarrierGripperClosed));
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        Assert.Equal(MachineAlarm.None, state.Alarm);

        Assert.Equal(StartBlockReason.None, machine.StartBlock);
        await machine.ResetAsync();
        Assert.False(state.IsError);
        Assert.Equal(StartBlockReason.None, machine.StartBlock);
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        var restarted = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => io.GetInput(InputIo.NgShuttleCarrierDetected)
                    && !io.GetInput(InputIo.NgCarrierDetected)
                    && io.GetInput(InputIo.NgCarrierGripperOpen), TimeSpan.FromSeconds(3)),
                $"START={machine.StartBlock}; Inspection={gantry.Step}; pending={gantry.IsTransferPending}; "
                    + $"position={gantry.Motion.Feedback.Position}; lift={gantry.Lift}; gripper={gantry.Gripper}; "
                    + $"shuttle={io.GetInput(InputIo.NgShuttleCarrierDetected)}; held={io.GetInput(InputIo.NgCarrierDetected)}; "
                    + $"S3={gantry.Station.CarrierPresent}; {state.AlarmDetail}");
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            machine.Stop();
            await restarted.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(StationCylinderState.Down)]
    [InlineData(StationCylinderState.Between)]
    [InlineData(StationCylinderState.Up)]
    public async Task ReleasedNgCarrierIsNotGrippedAgainOnRestart(StationCylinderState lift)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var station = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(settings.NgCarrierTransfer.ShuttlePlacePosition, 10_000);
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.NgCarrierDetected, true);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        io.SetInput(InputIo.NgCarrierPickupDown, lift == StationCylinderState.Down);
        io.SetInput(InputIo.NgCarrierPickupUp, lift == StationCylinderState.Up);

        Assert.Equal(
            lift == StationCylinderState.Up
                ? InspectionStationState.Waiting
                : InspectionStationState.PreparingTransfer,
            station.NextStep);

        using var stop = new CancellationTokenSource();
        var run = station.RunAsync(stop.Token);
        Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));
        stop.Cancel();
        await run;

        if (lift != StationCylinderState.Up)
            await VerifyTransferReleaseAsync();
        // The carrier may leave the pickup sensor before the gripper reaches Open.
        io.SetInput(InputIo.NgCarrierDetected, false);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        Assert.Equal(InspectionStationState.PreparingTransfer, station.NextStep);
        await VerifyTransferReleaseAsync();

        async Task VerifyTransferReleaseAsync()
        {
            var move = services.GetRequiredService<InspectionStation>();
            using var moveStop = new CancellationTokenSource();
            var moveTask = move.RunAsync(moveStop.Token);
            try
            {
                Assert.Equal(InspectionStationState.PreparingTransfer, move.NextStep);
                Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));
            }
            finally
            {
                moveStop.Cancel();
                await moveTask;
            }
        }
    }

    [Fact]
    public async Task BinaryInspectionStartsWithoutTrainingOrModel()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTestSupport.OpenMachineStore())
            .AddVirtualApplication(settings)
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        services.GetRequiredService<RecipeManager>().Current
            .Pcb.BoltPoints.Add(new BoltPoint { Id = VirtualTestSupport.BoltId(1), X = 10, Y = 10 });
        TeachInspectionFovs(services.GetRequiredService<RecipeManager>().Current);

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(machine.IsManualMotionReady(MotionGroup.InspectionGantry));

        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => machine.IsStartAllowed);
        var run = machine.StartAsync();
        try
        {
            await WaitUntilAsync(() => state.AutomaticRunning);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }

        io.SetInput(InputIo.AutoMode, true);
        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(machine.IsManualMotionReady(MotionGroup.InspectionGantry));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InspectionAndConveyorAgreeOnBypassRoute(bool inspectionEnabled, bool ng)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = inspectionEnabled;
        settings.Units.NgConveyor = inspectionEnabled;
        await using var services = CreateServices(settings);
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionStation>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var inspection = services.GetRequiredService<InspectionStation>();
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        if (inspectionEnabled)
        {
            await services.GetRequiredService<InspectionStation>().MoveToCarrierAsync(
                NgTransferDestination.Station, CancellationToken.None);
        }
        io.SetInput(InputIo.AutoMode, false);
        VirtualTestSupport.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        await work.Station.SeatAsync(CancellationToken.None);
        var assembly = work.Station.GetAssembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBoltPresence(VirtualTestSupport.BoltId(1), !ng);
        assembly.CompleteInspection();
        work.Station.Complete(work.Station.CurrentJob);

        var expectNg = inspectionEnabled && ng;
        Assert.Equal(expectNg, inspection.NextStep == InspectionStationState.PickingCarrier);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var run = inspectionEnabled && !ng ? inspection.RunAsync(stop.Token) : Task.CompletedTask;
        try
        {
            if (inspectionEnabled && !ng)
                await WaitUntilAsync(() => inspection.IsRearDischargeReady);
            Assert.Equal(!expectNg,
                conveyor.GetNextStep(io.GetOutput(OutputIo.MainConveyorRun)) == MainConveyorState.DischargingInspectionCarrier);
        }
        finally
        {
            stop.Cancel();
            await run;
        }
        await machine.ShutdownAsync();
    }

    [Theory]
    [InlineData(FasteningHead.Pickup)]
    [InlineData(FasteningHead.Shooting)]
    public async Task ManualBoltTestIsAvailableAfterInterruptedProduction(FasteningHead selected)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PickupBoltFeeder = selected == FasteningHead.Pickup;
        settings.Units.ShootingBoltFeeder = false; // Supply is outside this result-ownership test.
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new()
        {
            Id = VirtualTestSupport.BoltId(1), Head = selected, X = 0, Y = 0, FasteningX = 0, FasteningY = 0,
        }];
        var bus = services.GetRequiredKeyedService<IAdcBus>(selected);
        var slave = selected == FasteningHead.Pickup
            ? settings.Hantas.PickupSlaveAddress
            : settings.Hantas.ShootingSlaveAddress;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;
        io.SetInputs(
            (InputIo.BoltFasteningHeatSink1Present, true),
            (InputIo.BoltFasteningBackupPlateUp, true),
            (InputIo.BoltFasteningBackupPlateDown, false),
            (InputIo.BoltFasteningStopperUp, false),
            (InputIo.BoltFasteningStopperDown, true),
            (InputIo.PickupHeadUp, true),
            (InputIo.PickupHeadDown, false),
            (InputIo.ShootingHeadUp, true),
            (InputIo.ShootingHeadDown, false),
            (InputIo.PickupHeadVacuumDetected, true),
            (InputIo.ShootingHeadVacuumDetected, true),
            (InputIo.PickupTableDown, selected == FasteningHead.Pickup),
            (InputIo.PickupTableUp, selected == FasteningHead.Shooting));
        using var stop = new CancellationTokenSource();
        void StopWhenStarted(OutputIo output, bool on)
        {
            if (on && output == (selected == FasteningHead.Pickup ? OutputIo.PickupBoltStart : OutputIo.ShootingBoltStart))
                stop.Cancel();
        }

        io.OutputChanged += StopWhenStarted;
        try
        {
            await station.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            stop.Cancel();
            io.OutputChanged -= StopWhenStarted;
        }
        var interruptedEvent = (await bus.ReadFasteningResultAsync(slave)).EventCount;

        settings.Units.PickupBoltFeeder = false;
        settings.Units.ShootingBoltFeeder = false;

        using var diagnostics = new AdcProtocolViewModel(
            services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Pickup),
            services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Shooting), services.GetRequiredService<IIoService>(), settings.Hantas, machine, state)
        {
            SelectedHead = selected,
            SlaveText = slave.ToString(),
        };
        await diagnostics.StartCommand.ExecuteAsync(null);
        Assert.StartsWith("OK", diagnostics.ResultMessage);
        Assert.True(services.GetRequiredService<MachineState>().ManualSetupEnabled);
        Assert.True(machine.IsUseAdcProtocolAllowed);
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Equal(interruptedEvent + 1, (await bus.ReadFasteningResultAsync(slave)).EventCount);

        state.SetError(MachineAlarm.BoltFastening);
        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Equal(StartBlockReason.None, machine.StartBlock);
        Assert.True(services.GetRequiredService<MachineState>().ManualSetupEnabled);
        Assert.Equal(interruptedEvent + 1, (await bus.ReadFasteningResultAsync(slave)).EventCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdcDiagnosticCancellationTurnsStartOffAndReleasesMachineLock(bool diagnosticStop)
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgConveyor) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var io = new VirtualIoService(VirtualTestSupport.Outputs(), new());
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        bus.BindIo(io, FasteningHead.Pickup);
        using var diagnostics = new AdcProtocolViewModel(bus, new VirtualAdcBus(), io, settings.Hantas, machine, state);
        var testing = diagnostics.StartCommand.ExecuteAsync(null);
        Assert.True(state.IsRunning);
        Assert.Throws<InvalidOperationException>(() => diagnostics.SelectedHead = FasteningHead.Shooting);
        Assert.True(await VirtualTestSupport.WaitUntilAsync(
            () => io.GetOutput(OutputIo.PickupBoltStart), TimeSpan.FromSeconds(2)));
        Assert.True(io.GetOutput(OutputIo.PickupBoltStart));
        if (diagnosticStop)
            await diagnostics.StopCommand.ExecuteAsync(null);
        else
            machine.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => testing.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.Equal(1, bus.StopWrites);
        Assert.False(state.IsRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdcDiagnosticCloseAwaitsTheCommandAndReportsStopFailure(bool failStop)
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgConveyor) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var io = new VirtualIoService(VirtualTestSupport.Outputs(), new());
        using var bus = new AdcControllerStub { SuppressCompletion = true };
        bus.BindIo(io, FasteningHead.Pickup);
        using var diagnostics = new AdcProtocolViewModel(bus, new VirtualAdcBus(), io, settings.Hantas, machine, state);
        var testing = diagnostics.StartCommand.ExecuteAsync(null);
        Assert.True(await VirtualTestSupport.WaitUntilAsync(
            () => io.GetOutput(OutputIo.PickupBoltStart), TimeSpan.FromSeconds(2)));
        if (failStop)
            bus.StopWriteFailure = new IOException("START OFF failed while closing diagnostics.");

        Assert.Equal(!failStop, await diagnostics.TryCloseAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(testing.IsCompleted);
        Assert.False(state.IsRunning);
        Assert.False(state.BoltTestRunning);
        Assert.Equal(1, bus.StopWrites);
        if (failStop)
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => testing);
            Assert.Contains(bus.StopWriteFailure!, failure.InnerExceptions);
            Assert.Contains(bus.StopWriteFailure!.Message, diagnostics.CloseError);
            Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => testing);
            Assert.Null(diagnostics.CloseError);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
    }

    [Fact]
    public async Task AdcDiagnosticStopDuringQueryAlsoTurnsStartOff()
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgConveyor) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var io = new VirtualIoService(VirtualTestSupport.Outputs(), new());
        using var pickup = new VirtualAdcBus();
        using var shooting = new VirtualAdcBus();
        using var diagnostics = new AdcProtocolViewModel(pickup, shooting, io, settings.Hantas, machine, state);
        diagnostics.SelectedPort = "Virtual";
        await diagnostics.ToggleConnectionCommand.ExecuteAsync(null);
        // A diagnostic query does not own START, but the STOP button must still turn it off.
        io.SetOutput(OutputIo.PickupBoltStart, true);
        var reading = diagnostics.CaptureDeviceInformationCommand.ExecuteAsync(null);
        Assert.False(reading.IsCompleted);

        await diagnostics.StopCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
        Assert.False(state.IsRunning);
    }

    [Fact]
    public async Task AdcDiagnosticsSelectsTheMatchingPortAndDisconnectsOnlyThatHead()
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgConveyor) };
        settings.Hantas.PickupBaudRate = 19200;
        settings.Hantas.ShootingBaudRate = 38400;
        settings.Hantas.PickupSlaveAddress = 1;
        settings.Hantas.ShootingSlaveAddress = 1;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var pickup = services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Pickup);
        var shooting = services.GetRequiredKeyedService<IAdcBus>(FasteningHead.Shooting);
        await machine.InitializeAsync();
        using var diagnostics = new AdcProtocolViewModel(
            pickup, shooting, services.GetRequiredService<IIoService>(), settings.Hantas, machine, services.GetRequiredService<MachineState>());
        try
        {
            diagnostics.SelectedPort = "Virtual";
            await diagnostics.ToggleConnectionCommand.ExecuteAsync(null);
            Assert.True(pickup.IsOpen);
            Assert.False(shooting.IsOpen);
            Assert.Equal(19200, pickup.BaudRate);
            await pickup.WriteRegisterAsync(1, (ushort)AdcRemoteRegister.Preset, 7);
            diagnostics.SelectedHead = FasteningHead.Shooting;
            Assert.Equal("Connect", diagnostics.ConnectionAction);
            Assert.Equal(38400, diagnostics.SelectedBaudRate);
            Assert.Equal("1", diagnostics.SlaveText);
            diagnostics.SelectedPort = "Virtual";
            await diagnostics.ToggleConnectionCommand.ExecuteAsync(null);
            await diagnostics.SelectPresetCommand.ExecuteAsync(null);
            await diagnostics.ReadResultCommand.ExecuteAsync(null);
            Assert.StartsWith("Last result:", diagnostics.ResultMessage);
            Assert.Equal((ushort)1, (await shooting.ReadControllerStatusAsync(1)).Status!.Preset);
            Assert.Equal((ushort)7, (await pickup.ReadControllerStatusAsync(1)).Status!.Preset);
            Assert.True(pickup.IsOpen);
            Assert.Equal(38400, shooting.BaudRate);
            await diagnostics.ExecuteRegisterCommand.ExecuteAsync(null);
            Assert.Contains(" = ", diagnostics.RegisterResult);
            var connectionStatus = diagnostics.ConnectionStatus;
            diagnostics.AddressText = "invalid";
            await diagnostics.ExecuteRegisterCommand.ExecuteAsync(null);
            Assert.Equal("Address must be 0–65535.", diagnostics.RegisterResult);
            diagnostics.AddressText = ((ushort)AdcResultRegister.EventCount).ToString();
            diagnostics.CountText = "-1";
            await diagnostics.ExecuteRegisterCommand.ExecuteAsync(null);
            Assert.Equal("Check register count.", diagnostics.RegisterResult);
            diagnostics.RegisterAccess = AdcFunctionCode.WriteSingleRegister;
            diagnostics.AddressText = ((ushort)AdcRemoteRegister.Preset).ToString();
            diagnostics.ValueText = "65536";
            await diagnostics.ExecuteRegisterCommand.ExecuteAsync(null);
            Assert.Equal("Value must be 0–65535.", diagnostics.RegisterResult);
            Assert.Equal(connectionStatus, diagnostics.ConnectionStatus);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            diagnostics.ValueText = "2";
            await diagnostics.ExecuteRegisterCommand.ExecuteAsync(null);
            Assert.Equal((ushort)2, (await shooting.ReadControllerStatusAsync(1)).Status!.Preset);
            await diagnostics.ToggleConnectionCommand.ExecuteAsync(null);
            Assert.False(shooting.IsOpen);
            Assert.True(pickup.IsOpen);
            diagnostics.SelectedHead = FasteningHead.Pickup;
            Assert.Equal("Disconnect", diagnostics.ConnectionAction);
            Assert.Equal(19200, diagnostics.SelectedBaudRate);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task AutomaticAlarmWaitsForHeadStopAndKeepsFirstCause()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
        settings.Units.ShootingBoltFeeder = true;
        settings.BoltFastening.ShootingArrivalDelaySeconds = 0.5;
        var head = new StoppingBoltHead();
        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTestSupport.OpenMachineStore())
            .AddVirtualApplication(settings)
            .AddKeyedSingleton<IBoltHead>(FasteningHead.Shooting, head)
            .BuildServiceProvider();
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateDown, false);
        io.SetInput(InputIo.BoltFasteningStopperUp, false);
        io.SetInput(InputIo.BoltFasteningStopperDown, true);
        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => machine.IsStartAllowed);

        var run = machine.StartAsync();
        try
        {
            await WaitUntilAsync(() => state.AutomaticRunning);
            io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
            VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            Assert.True(services.GetRequiredService<BoltFasteningStation>().Station.CarrierSeated);
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(() => head.Started.Task.IsCompleted, TimeSpan.FromSeconds(3)),
                $"Fastening did not start: block={machine.StartBlock}, alarm={state.Alarm}, "
                    + $"station={services.GetRequiredService<BoltFasteningStation>().NextStep}. {state.AlarmDetail}");
            io.SetInput(InputIo.AirPressureHigh, false);
            await head.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(run.IsCompleted);
            Assert.True(state.AutomaticRunning);
            Assert.True(state.IsRunning);
            Assert.False(machine.IsResetAllowed);
            Assert.Equal(MachineAlarm.AirPressureLow, state.Alarm);

            head.Stopped.SetException(new InvalidOperationException("Head stop failed."));
            await run.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(state.IsRunning);
            Assert.Equal(MachineAlarm.AirPressureLow, state.Alarm);
            Assert.Null(state.AlarmMessage);
        }
        finally
        {
            machine.Stop();
            head.Stopped.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DisabledTransferStillBlocksShuttleUntilPickupIsRaised()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgConveyor),
        };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var shuttle = services.GetRequiredService<NgCarrierConveyor>();
        var gantry = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, true);
        io.SetInput(InputIo.NgCarrierDetected, true);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        io.SetInput(InputIo.AutoMode, false);

        var run = machine.StartAsync();
        try
        {
            await WaitUntilAsync(() => shuttle.Step is NgConveyorState.WaitingForTransferRelease);
            Assert.Equal(NgConveyorState.WaitingForTransferRelease, shuttle.Step);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));
            Assert.False(gantry.Motion.Feedback.GetAxisState(MotionAxis.X).ServoOn);
            Assert.False(gantry.Motion.Feedback.GetAxisState(MotionAxis.X).Homed);

            io.SetInput(InputIo.NgCarrierGripperClosed, false);
            io.SetInput(InputIo.NgCarrierGripperOpen, true);
            Assert.Equal(NgConveyorState.WaitingForTransferRelease, shuttle.Step);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));

            io.SetInput(InputIo.NgCarrierPickupDown, false);
            io.SetInput(InputIo.NgCarrierPickupUp, true);
            await ((IIoService)io).WaitForInputAsync(InputIo.NgShuttleDown, true);
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.False(gantry.Motion.Feedback.GetAxisState(MotionAxis.X).ServoOn);
            Assert.False(gantry.Motion.Feedback.GetAxisState(MotionAxis.X).Homed);
        }
        finally
        {
            machine.Stop();
            await run;
        }
    }

    [Fact]
    public async Task AutomaticStartWaitsForCanceledManualScopeToFinish()
    {
        await using var services = CreateServices(
            new MachineSettings
            {
                Units = EnableOnly(MachineUnit.MainConveyor),
            });
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var operations = services.GetRequiredService<OperationCancellation>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.IsStartAllowed);

        using var manual = operations.Link();
        Assert.True(state.IsRunning);
        Assert.False(machine.IsStartAllowed);
        await machine.StartAsync();
        Assert.False(state.AutomaticRunning);

        machine.Stop();
        Assert.True(manual.IsCancellationRequested);
        Assert.True(state.IsRunning);
        Assert.False(machine.IsStartAllowed);
        manual.Dispose();
        Assert.False(state.IsRunning);
        Assert.True(machine.IsStartAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringMotionInitializationSkipsLaterUnitsAndCanRetry(bool failAfterStop)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        settings.Units.PcbPlacement = true;
        settings.Units.Inspection = true;
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var supply = probes[MotionGroup.PcbSupply];
        var failure = new IOException("Motion initialization failed after STOP.");
        void StopAfterSupplyInitialization()
        {
            supply.Motion.StateChanged -= StopAfterSupplyInitialization;
            machine.Stop();
            if (failAfterStop)
                throw failure;
        }

        supply.Motion.StateChanged += StopAfterSupplyInitialization;
        try
        {
            if (failAfterStop)
                await machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));
            else
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(failAfterStop ? 0 : 1, supply.InitializationCalls);
            Assert.All(
                probes.Where(item => item.Key != MotionGroup.PcbSupply),
                item => Assert.Equal(0, item.Value.InitializationCalls));
            Assert.False(state.IsRunning);
            if (failAfterStop)
            {
                Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
                Assert.Contains(failure.Message, state.AlarmDetail);
                Assert.Single(services.GetRequiredService<ApplicationLog>().Snapshot(),
                    entry => entry.Detail?.Contains(failure.Message) == true);
                return;
            }
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.DoesNotContain(
                services.GetRequiredService<ApplicationLog>().Snapshot(),
                entry => entry.Level == "ERROR");

            await machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, supply.InitializationCalls);
            Assert.Equal(1, probes[MotionGroup.PcbPlacementHandler].InitializationCalls);
            Assert.Equal(1, probes[MotionGroup.InspectionGantry].InitializationCalls);
            Assert.Equal(0, probes[MotionGroup.BoltFastening].InitializationCalls);
            Assert.Equal(MachineAlarm.None, state.Alarm);
        }
        finally
        {
            supply.Motion.StateChanged -= StopAfterSupplyInitialization;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StopDuringHardwareReadinessPreventsStartAndAllowsRestart()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
        settings.Units.ShootingBoltFeeder = true;
        var head = new WaitingBoltHead();
        await using var services = new ServiceCollection().AddSingleton(_ => VirtualTestSupport.OpenMachineStore())
            .AddVirtualApplication(settings)
            .AddKeyedSingleton<IBoltHead>(FasteningHead.Shooting, head)
            .AddKeyedSingleton<IBoltHead>(FasteningHead.Pickup, head)
            .BuildServiceProvider();
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        head.WaitForReadiness = true;
        var readinessChecks = head.ReadinessChecks;

        var starting = machine.StartAsync();
        await head.ReadinessEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.AutomaticRunning);
        Assert.True(state.IsRunning);
        await WaitUntilAsync(() => io.GetOutput(OutputIo.TowerLampYellow));
        Assert.False(io.GetOutput(OutputIo.TowerLampGreen));
        Assert.False(machine.IsStartAllowed);
        Assert.False(machine.IsHomeAllowed);
        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(readinessChecks + 1, head.ReadinessChecks);

        machine.Stop();
        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);

        head.ReadinessReleased.TrySetResult();
        var resumed = machine.StartAsync();
        Assert.True(await WaitUntilAsync(() => state.AutomaticRunning, TimeSpan.FromSeconds(2)),
            $"block={machine.StartBlock}; alarm={state.Alarm}; detail={state.AlarmDetail}; run={resumed.Status}; error={resumed.Exception}");
        await WaitUntilAsync(() => io.GetOutput(OutputIo.TowerLampGreen));
        machine.Stop();
        await resumed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsRunning);
        Assert.False(state.IsError, state.AlarmDetail);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    public async Task StopOrFailureDuringFirstUnitOutputPreventsLaterStarts(
        bool failure,
        bool stopBeforeFailure,
        bool motionFailure,
        bool combinedFailure)
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.MainConveyor),
        };
        settings.Units.ShootingBoltFeeder = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        var stopped = false;
        var conveyorStarted = false;
        Exception error = motionFailure
            ? new MotionException("Feeder start", new IOException("Motion controller disconnected."))
            : new InvalidOperationException("Feeder start failed.");
        if (combinedFailure)
            error = new AggregateException(new IOException("Output cleanup failed."), new AggregateException(error));
        io.OutputChanged += (output, value) =>
        {
            conveyorStarted |= output == OutputIo.MainConveyorReadyToFront2 && value;
            if (output == OutputIo.ShootingFeederOff && !value)
            {
                stopped = true;
                if (!failure || stopBeforeFailure)
                    machine.Stop();
                if (failure)
                    throw error;
            }
        };

        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stopped);
        Assert.False(conveyorStarted);
        Assert.False(state.IsRunning);
        var expectedAlarm = motionFailure
            ? MachineAlarm.MotionUnavailable
            : failure ? MachineAlarm.ShootingBoltFeeder : MachineAlarm.None;
        Assert.Equal(expectedAlarm, state.Alarm);
        Assert.Equal(failure ? error.Message : null, state.AlarmMessage);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
        if (failure)
        {
            var entries = services.GetRequiredService<ApplicationLog>().Snapshot();
            Assert.Contains(entries, entry => entry.Message == $"Automatic unit ShootingBoltFeeder failed. {error.Message}");
            var alarm = Assert.Single(entries, entry => entry.Detail == error.ToString());
            Assert.Equal($"Machine alarm: {expectedAlarm}.", alarm.Message);
            Assert.Equal(error.ToString(), state.AlarmDetail);
        }
    }

    [Fact]
    public async Task UnitFailureDuringSafetyStopKeepsFirstAlarmAndLogsFailure()
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.MainConveyor) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var log = services.GetRequiredService<ApplicationLog>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        var failure = new IOException("Conveyor cleanup failed after the air pressure trip.");
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.MainConveyorReadyToFront2 || !on)
                return;
            io.SetInput(InputIo.AirPressureHigh, false);
            throw failure;
        };

        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.AirPressureLow, state.Alarm);
        Assert.False(state.IsRunning);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        var entry = Assert.Single(log.Snapshot(), entry => entry.Detail == failure.ToString());
        Assert.Contains("Automatic unit MainConveyor", entry.Message);
        Assert.Contains("AirPressureLow", entry.Message);
    }

    [Fact]
    public async Task DoorTripStopsAndResetsFromLiveHardwareState()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();

        Assert.False(machine.IsStartAllowed);
        Assert.True(machine.IsHomeAllowed);
        Assert.False(machine.IsResetAllowed);

        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.FeedbackReadiness.Homed);

        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.IsStartAllowed);
        await WaitUntilAsync(() => machine.IsStartAllowed);
        var firstRun = machine.StartAsync();
        Assert.True(await VirtualTestSupport.WaitUntilAsync(
            () => state.AutomaticRunning, TimeSpan.FromSeconds(2)),
            $"{state.Alarm}: {state.AlarmDetail}");

        io.SetInput(InputIo.Door1Open, false);
        await firstRun.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.DoorOpen, state.Alarm);
        Assert.False(state.IsRunning);
        Assert.False(state.FeedbackReadiness.ServosOn);

        io.SetInput(InputIo.AutoMode, true);
        io.SetInput(InputIo.ResetButton, true);
        await WaitUntilAsync(() => !state.IsError);
        io.SetInput(InputIo.ResetButton, false);

        Assert.True(state.FeedbackReadiness.ServosOn);
        Assert.True(state.FeedbackReadiness.Homed);

        io.SetInput(InputIo.Door1Open, true);
        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => machine.IsStartAllowed);
        var secondRun = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);

        machine.Stop();
        await secondRun.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(state.IsRunning);
    }

    [Fact]
    public async Task UnitTimeoutStopsWithItsOwnAlarmAndCanRestart()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.ShootingBoltFeeder),
        };
        settings.Units.MainConveyor = true;
        settings.BoltFeeder.ShootingTimeoutMilliseconds = 50;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.ShootingBoltFeeder, state.Alarm);
        Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
        Assert.False(state.IsRunning);
        Assert.True(machine.IsResetAllowed);

        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);

        settings.BoltFeeder.ShootingTimeoutMilliseconds = 500;
        var resumed = machine.StartAsync();
        await ((IIoService)io).WaitForInputAsync(InputIo.ShootingFeederBoltDetected, true);

        machine.Stop();
        await resumed.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(state.IsRunning);
    }

    [Fact]
    public async Task IoCommunicationFailureStopsAndCanReset()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.MainConveyor),
        };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        var run = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);

        io.IsReady = false;
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
        Assert.False(state.IsRunning);

        Assert.Contains("disconnected", state.AlarmDetail);

        io.IsReady = true;
        Assert.True(machine.IsResetAllowed);
        io.SetInput(InputIo.ResetButton, true);
        await WaitUntilAsync(() => state.Alarm == MachineAlarm.None);
        io.SetInput(InputIo.ResetButton, false);

        Assert.Equal(MachineAlarm.None, state.Alarm);
        await WaitUntilAsync(() => machine.IsStartAllowed);
        var resumed = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);
        machine.Stop();
        await resumed.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(state.IsRunning);
    }

    [Fact]
    public async Task MotionAlarmStopsAndCanReset()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var motion = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.BoltFastening);

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        var run = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);

        motion.SetAlarm(MotionAxis.X, true);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(state.IsRunning);

        await machine.ResetAsync();

        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(state.FeedbackReadiness.Faulted);
        Assert.True(state.FeedbackReadiness.ServosOn);
        var resumed = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);
        machine.Stop();
        await resumed.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(state.IsRunning);
    }

    public enum PlatePreparationOutcome
    {
        Arrived,
        Stopped,
        TimedOut,
    }

    [Fact]
    public async Task SupplyServoChangeDoesNotRefreshUnrelatedStationTargets()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var view = services.GetRequiredService<OperationViewModel>();
        var motion = (VirtualMotionService)services.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbSupply);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.FeedbackReadiness.Homed && !state.IsRunning);
        view.Activate();
        var notifications = new ConcurrentQueue<string?>();
        view.PropertyChanged += (_, e) => notifications.Enqueue(e.PropertyName);
        try
        {
            motion.SetServo(MotionAxis.X, false);
            await WaitUntilAsync(() => !state.FeedbackReadiness.ServosOn
                && notifications.Contains(nameof(OperationViewModel.MachineDisplayState)));
            Assert.Equal(MachineDisplayState.ServoOff, view.MachineDisplayState);
            Assert.Contains(nameof(OperationViewModel.SupplyDisplayState), notifications);
            Assert.DoesNotContain(nameof(OperationViewModel.BoltTargets), notifications);
            Assert.DoesNotContain(nameof(OperationViewModel.InspectionTargets), notifications);
            Assert.DoesNotContain(nameof(OperationViewModel.ModeText), notifications);
            Assert.Contains(nameof(OperationViewModel.Alarm), notifications);
        }
        finally
        {
            view.Deactivate();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task UnchangedFeedbackDoesNotRefreshTheViewAndInputChangesNotifyImmediately()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        var view = services.GetRequiredService<OperationViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        view.Activate();
        await view.LoadOlderPcbsCommand.ExecutionTask!;
        var notifications = 0;
        var samples = 0;
        void OnViewChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Interlocked.Increment(ref notifications);
        }
        void OnSampled(MotionGroup group, MotionFeedbackSample sample)
        {
            Interlocked.Increment(ref samples);
        }
        view.PropertyChanged += OnViewChanged;
        feedback.Sampled += OnSampled;
        try
        {
            await Task.Delay(650);
            Assert.True(Volatile.Read(ref samples) > 0);
            Assert.Equal(0, Volatile.Read(ref notifications));
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
            Assert.True(Volatile.Read(ref notifications) > 0);
            Assert.True(view.Signals.Inputs[InputIo.MainConveyorEntryCarrierDetected].IsOn);

            await machine.StopAsync();
            var stopped = Volatile.Read(ref notifications);
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, false);
            Assert.True(Volatile.Read(ref notifications) > stopped);
            Assert.False(view.Signals.Inputs[InputIo.MainConveyorEntryCarrierDetected].IsOn);
        }
        finally
        {
            view.PropertyChanged -= OnViewChanged;
            feedback.Sampled -= OnSampled;
            view.Deactivate();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DisplayUsesAcquiredFeedbackForAllStationsAndNeverFallsBackToHardware()
    {
        var settings = FlowSettings();
        var readingDisplay = new AsyncLocal<bool>();
        var unexpectedRead = new InvalidOperationException("Display attempted a hardware read.");
        void BeforeHardwareRead()
        {
            if (readingDisplay.Value)
                throw unexpectedRead;
        }

        await using var services = CreateMotionScopeServices(settings, out var probes, registrations =>
            registrations.AddSingleton<IIoService>(provider =>
            {
                var wrapper = System.Reflection.DispatchProxy.Create<IIoService, IoTests.OutputReadProbe>();
                var probe = (IoTests.OutputReadProbe)wrapper;
                probe.Io = provider.GetRequiredService<VirtualIoService>();
                probe.BeforeRead = BeforeHardwareRead;
                return wrapper;
            }));
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.FeedbackReadiness.Homed);
        await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
        foreach (var probe in probes.Values)
            probe.BeforeHardwareRead = BeforeHardwareRead;
        try
        {
            foreach (var station in new (InputIo Carrier, InputIo PlateDown, InputIo PlateUp,
                InputIo StopperUp, InputIo StopperDown, InputIo HeatSink)[]
            {
                (InputIo.PcbPlacementCarrierPresent,
                    InputIo.PcbPlacementBackupPlateDown, InputIo.PcbPlacementBackupPlateUp,
                    InputIo.PcbPlacementStopperUp, InputIo.PcbPlacementStopperDown,
                    InputIo.PcbPlacementHeatSink1Present),
                (InputIo.BoltFasteningCarrierPresent,
                    InputIo.BoltFasteningBackupPlateDown, InputIo.BoltFasteningBackupPlateUp,
                    InputIo.BoltFasteningStopperUp, InputIo.BoltFasteningStopperDown,
                    InputIo.BoltFasteningHeatSink1Present),
                (InputIo.InspectionCarrierPresent,
                    InputIo.InspectionBackupPlateDown, InputIo.InspectionBackupPlateUp,
                    InputIo.InspectionStopperUp, InputIo.InspectionStopperDown,
                    InputIo.InspectionHeatSink1Present),
            })
            {
                io.SetInput(station.Carrier, true);
                io.SetInput(station.PlateDown, false);
                io.SetInput(station.PlateUp, true);
                io.SetInput(station.StopperUp, false);
                io.SetInput(station.StopperDown, true);
                io.SetInput(station.HeatSink, true);
            }

            Assert.True(machine.TeachingReady);
            Assert.True(services.GetRequiredService<BoltFasteningStation>().Station.CarrierSeated);
            Assert.True(services.GetRequiredService<InspectionStation>().Station.CarrierSeated);
            state.AutomaticRunning = true;
            readingDisplay.Value = true;
            var display = services.GetRequiredService<OperationViewModel>();
            Assert.True(state.Available);
            // A machine-level flag cannot invent an executing step in an idle unit.
            Assert.Null(display.FasteningState);
            Assert.Null(display.InspectionState);
            Assert.Null(display.Placement.ActivePcb);
            Assert.Null(display.BoltFasteningActiveBolt);
            Assert.Null(services.GetRequiredService<BoltFasteningStation>().ActiveBolt);
            Assert.Null(services.GetRequiredService<InspectionStation>().ActiveBolt);
            Assert.Null(services.GetRequiredService<InspectionStation>().ActivePcb);
            Assert.Same(unexpectedRead, Assert.Throws<InvalidOperationException>(() =>
                VirtualTestSupport.IsAt(services.GetRequiredService<PcbSupplier>().Motion.Feedback, settings.PcbSupply.HandoffPosition)));
            Assert.Same(unexpectedRead, Assert.Throws<InvalidOperationException>(
                () => services.GetRequiredService<IIoService>().GetOutput(OutputIo.MainConveyorRun)));

            readingDisplay.Value = false;
            io.SetInput(InputIo.InspectionBackupPlateUp, false);
            io.SetInput(InputIo.InspectionBackupPlateDown, true);
            io.SetInput(InputIo.InspectionStopperDown, false);
            io.SetInput(InputIo.InspectionStopperUp, true);
            var work = services.GetRequiredService<InspectionStation>();
            work.RequestInspection(work.Station.CurrentJob);
            readingDisplay.Value = true;
            Assert.Null(display.ConveyorState);
            Assert.Null(display.InspectionState);
            Assert.Null(display.InspectionActivePcb);
            _ = display.InspectionActiveBolt;

            // Unavailable sampled feedback must remain unknown instead of reading the SDK.
            services.GetRequiredService<PcbPlacer>().Motion.InvalidateFeedback(new IOException("Lost sample."));
            Assert.Null(display.PlacementState);
        }
        finally
        {
            readingDisplay.Value = false;
            state.AutomaticRunning = false;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(MotionFeedbackFault.Alarm)]
    [InlineData(MotionFeedbackFault.ServoOff)]
    [InlineData(MotionFeedbackFault.HomeLost)]
    [InlineData(MotionFeedbackFault.ReadFailure)]
    public async Task AutomaticFeedbackStopsOnSilentMotionFaultWithoutAView(MotionFeedbackFault fault)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.MainConveyor = true;
        await using var services = CreateMotionScopeServices(settings, out var probes);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var active = probes[MotionGroup.InspectionGantry];
        foreach (var probe in probes.Where(item => item.Key != MotionGroup.InspectionGantry).Select(
            item => item.Value))
        {
            probe.ReportReady = true;
            probe.AllowStop = true;
            probe.FailHardwareCalls = true; // Disabled hardware must not be sampled, even while AUTO polls.
        }

        Task? run = null;
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            io.SetInput(InputIo.AutoMode, false);
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(() => machine.IsStartAllowed, TimeSpan.FromSeconds(2)),
                $"START blocked: {machine.StartBlock}; busy={state.IsRunning}; alarm={state.AlarmDetail}");
            run = machine.StartAsync();
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(() => state.AutomaticRunning, TimeSpan.FromSeconds(2)),
                $"AUTO did not start: block={machine.StartBlock}, alarm={state.Alarm}. {state.AlarmDetail}");
            Assert.False(run.IsCompleted);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            if (fault == MotionFeedbackFault.ReadFailure)
                // Isolate the monitor: a simultaneous command read failure has its own unit alarm.
                active.DiagnosticReadError = new IOException("Unavailable diagnostic feedback.");
            else
                active.OverrideState = (_, value) => fault switch
                {
                    MotionFeedbackFault.Alarm => value with { Alarm = true },
                    MotionFeedbackFault.HomeLost => value with { Homed = false },
                    MotionFeedbackFault.ServoOff => value with { ServoOn = false },
                    _ => throw new ArgumentOutOfRangeException(nameof(fault)),
                };
            // No StateChanged, DI changes, UI timer or explicit refresh request accompanies this fault.
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
            await WaitUntilAsync(
                () => !services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.All(
                probes.Where(item => item.Key != MotionGroup.InspectionGantry),
                item => Assert.Equal(0, item.Value.HardwareCalls));
            active.DiagnosticReadError = null;
            active.OverrideState = null;
            await WaitUntilAsync(() => !state.FeedbackReadiness.Faulted);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm); // Recovery never restarts AUTO.
        }
        finally
        {
            active.DiagnosticReadError = null;
            active.OverrideState = null;
            // Shutdown verifies every axis, including disabled groups.
            foreach (var probe in probes.Values)
                probe.FailHardwareCalls = false;
            await machine.ShutdownAsync();
            if (run is not null)
                await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task HomeUsesAcquiredFeedbackAndStopsOnSilentReadFailure()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.InspectionGantry.Motion.HorizontalHome.SearchSpeed = 1;
        await using var services = CreateDisplayServices(out var motion, settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await motion.Motion.MoveAxisAsync(MotionAxis.X, 50, 10_000);
        var homing = machine.HomeAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => state.IsHoming && motion.Motion.IsMoving);
            var refreshThread = Environment.CurrentManagedThreadId;
            var directReads = 0;
            motion.BeforeRead = () =>
            {
                if (Environment.CurrentManagedThreadId == refreshThread)
                    directReads++;
            };
            state.Refresh();
            motion.BeforeRead = null;
            var failure = new IOException("Home monitor feedback became unreadable.");
            motion.DiagnosticReadError = failure;
            // No DI/SDK event or explicit state refresh accompanies the failed sample.
            await homing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, directReads);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
            Assert.Contains(failure.Message, state.AlarmDetail);
            Assert.False(motion.Motion.IsMoving);
            Assert.False(state.IsHoming);
        }
        finally
        {
            motion.BeforeRead = null;
            motion.DiagnosticReadError = null;
            machine.Stop();
            await homing.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task HomeAndAutomaticStartIgnorePreStartSample()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateDisplayServices(out var motion, settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        await machine.InitializeAsync();
        Assert.False(state.FeedbackReadiness.Homed);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var released = new ManualResetEventSlim();
        var blocked = 0;
        motion.AfterDiagnosticStateRead = axis =>
        {
            if (axis != MotionAxis.X || Interlocked.Exchange(ref blocked, 1) != 0)
                return;
            entered.TrySetResult();
            released.Wait();
        };
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveAutomaticSample(MotionGroup group, MotionFeedbackSample sample)
        {
            if (state.AutomaticRunning
                && group == MotionGroup.InspectionGantry
                && sample.Readiness.Homed)
                sampled.TrySetResult();
        }

        Task? run = null;
        feedback.Sampled += ObserveAutomaticSample;
        try
        {
            // The X sample says not homed, but its delivery is delayed across Home and Start.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.HomeAsync(CancellationToken.None);
            Assert.False(machine.IsStartAllowed);
            run = machine.StartAsync();
            await WaitUntilAsync(() => state.AutomaticRunning);
            released.Set();
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(state.AutomaticRunning);
            Assert.False(run.IsCompleted);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.True(state.FeedbackReadiness.Homed);

            var refreshThread = Environment.CurrentManagedThreadId;
            var reads = 0;
            motion.BeforeRead = () =>
            {
                if (Environment.CurrentManagedThreadId == refreshThread)
                    reads++;
            };
            try
            {
                state.Refresh();
                Assert.Equal(0, reads); // State notifications reuse the monitor; no second native scan.
                Assert.True(state.AutomaticRunning);
            }
            finally
            {
                motion.BeforeRead = null;
            }
        }
        finally
        {
            released.Set();
            motion.AfterDiagnosticStateRead = null;
            feedback.Sampled -= ObserveAutomaticSample;
            await machine.ShutdownAsync();
            if (run is not null)
                await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DisplayReadFailureIsVisibleAndDoesNotReplaceLiveAdmissionChecks()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        Assert.True(state.Available);
        var view = services.GetRequiredService<OperationViewModel>();
        var messages = new ConcurrentQueue<string?>();
        var modes = new ConcurrentQueue<string>();
        view.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationViewModel.AlarmMessage))
                messages.Enqueue(view.AlarmMessage);
            if (e.PropertyName == nameof(OperationViewModel.ModeText))
                modes.Enqueue(view.ModeText);
        };
        var error = new IOException("Display feedback unavailable.");
        feedback.BeforeRead = () => throw error;
        feedback.DiagnosticReadError = error;
        // A ready display is not permission to operate when the actual read fails.
        Assert.Throws<IOException>(() => services.GetRequiredService<MachineFeedbackMonitor>().ReadLiveReadiness());
        await WaitUntilAsync(() => !state.Available && messages.Contains(error.Message)
            && modes.Contains("UNKNOWN"));
        Assert.Same(error, state.ReadError);
        Assert.Equal(
            MachineDisplayState.Unavailable,
            view.ConveyorStatus);
        Assert.False(machine.IsHomeAllowed);
        Assert.False(machine.IsStartAllowed);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.All(
            services.GetRequiredService<InspectionStation>().Motion.Axes.Values,
            axis => Assert.Equal(AxisCondition.Unavailable, axis.Condition));

        var nextError = new IOException(error.Message, new InvalidOperationException());
        feedback.DiagnosticReadError = nextError;
        feedback.BeforeRead = () => throw nextError;
        await WaitUntilAsync(() => ReferenceEquals(nextError, state.ReadError));

        feedback.BeforeRead = null;
        feedback.DiagnosticReadError = null;
        await WaitUntilAsync(() => state.Available && messages.Contains(null)
            && modes.Contains("MANUAL"));
        Assert.Null(state.ReadError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualServoFailureStaysAtTheCommandBoundary(bool emergencyStop)
    {
        await using var services = CreateServices(FlowSettings());
        await services.GetRequiredService<MachineController>().InitializeAsync();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var manual = services.GetRequiredService<MotionDiagnosticsViewModel>();
        var row = manual.Axes.Single(
            axis => axis.Group == MotionGroup.InspectionGantry && axis.Axis == MotionAxis.X);
        var motion = services.GetRequiredService<InspectionStation>().Motion.Feedback;
        void FailOnce()
        {
            motion.StateChanged -= FailOnce;
            if (emergencyStop)
            {
                io.SetInput(InputIo.EmergencyStop1Pressed, true);
                Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
            }

            throw new IOException("Servo feedback failed.");
        }

        motion.StateChanged += FailOnce;

        Assert.True(row.IsToggleServoAllowed);
        row.ToggleServoCommand.Execute(null);
        Assert.Equal(emergencyStop ? MachineAlarm.EmergencyStop : MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Contains("Servo feedback failed", state.AlarmDetail);
        await WaitUntilAsync(() => row.Diagnostics.Sample.State?.ServoOn == false);
        Assert.False(row.Diagnostics.Sample.State?.ServoOn);
    }

    [Fact]
    public async Task ManualCommandAfterShutdownDoesNotEscapeTheBoundary()
    {
        await using var services = CreateServices(FlowSettings());
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.PcbSupply;
        var operations = services.GetRequiredService<OperationCancellation>();
        await operations.ShutdownAsync();
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);

        Assert.False(services.GetRequiredService<PcbSupplier>().Motion.Feedback.IsMoving);
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
    }

    public enum MotionFeedbackFault
    {
        Alarm,
        ServoOff,
        HomeLost,
        ReadFailure,
    }

    [Theory]
    [InlineData(InputIo.PickupFeederBoltDetected, MachineAlarm.PickupBoltFeeder)]
    [InlineData(InputIo.ShootingFeederBoltDetected, MachineAlarm.ShootingBoltFeeder)]
    public async Task SharedFeederReportsEmptySideDespiteOtherFeederChanges(InputIo emptyInput, MachineAlarm alarm)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PickupBoltFeeder);
        settings.Units.ShootingBoltFeeder = true;
        settings.BoltFeeder.PickupTimeoutMilliseconds = 200;
        settings.BoltFeeder.ShootingTimeoutMilliseconds = 200;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.ShootingEscapeForward, false);
        io.SetInput(InputIo.ShootingEscapeBackward, true);
        var otherInput = emptyInput == InputIo.PickupFeederBoltDetected
            ? InputIo.ShootingFeederBoltDetected : InputIo.PickupFeederBoltDetected;
        io.SetInput(emptyInput, false);
        io.SetInput(otherInput, true);
        Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = machine.StartAsync(stop.Token);
        try
        {
            // Repeated edges from one feeder cannot extend the other feeder's empty deadline.
            for (var change = 0; change < 20 && !state.IsError; change++)
            {
                io.SetInput(otherInput, false);
                io.SetInput(otherInput, true);
                await Task.Delay(30);
            }
            Assert.Equal(alarm, state.Alarm);
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(io.GetOutput(OutputIo.ShootingFeederOff));
            Assert.Contains(emptyInput.GetDescription(), state.AlarmDetail);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task FeedersOffKeepPickupMotionAndStartBothIoHeads(
        bool pickupEnabled, bool shootingEnabled)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PickupBoltFeeder = pickupEnabled;
        settings.Units.ShootingBoltFeeder = shootingEnabled;
        var pickupFeeding = pickupEnabled;
        var shootingFeeding = shootingEnabled;
        // Leave time for the simulated 200 ms vacuum response before reaching Safe Z.
        if (pickupFeeding)
            settings.BoltFastening.Motion.ZSpeed = 20;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [
            new() { Id = VirtualTestSupport.BoltId(1), Head = FasteningHead.Shooting, X = 10, Y = 10,
                FasteningX = 10.5, FasteningY = 9.5, FasteningZOffset = 0.25 },
            new() { Id = VirtualTestSupport.BoltId(2), Head = FasteningHead.Pickup, X = 20, Y = 10,
                FasteningX = 21, FasteningY = 11, FasteningZOffset = -0.5 },
            new() { Id = VirtualTestSupport.BoltId(3), Head = FasteningHead.Pickup, X = 30, Y = 10,
                FasteningX = 30, FasteningY = 10 },
        ];
        settings.BoltFastening.ShootingHead.FasteningZ = 8;
        settings.BoltFastening.PickupHead.FasteningZ = 12;
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var outputs = new ConcurrentQueue<(OutputIo Output, bool On)>();
        var starts = new ConcurrentQueue<(FasteningHead Head, double X, double Y, double Z)>();
        var descents = new ConcurrentQueue<(FasteningHead Head, double X, double Y, double Z)>();
        var pickups = new ConcurrentQueue<(double X, double Y, double Z)>();
        var visitedPickupFeeder = false;
        var feederRan = false;
        services.GetRequiredService<BoltFeederUnit>().Trace += message => feederRan = true;
        await machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PickupFeederBoltDetected, pickupFeeding);
        io.SetInput(InputIo.ShootingFeederBoltDetected, shootingFeeding);
        io.SetInput(InputIo.AutoMode, false);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.BoltFasteningBackupPlateUp, true);
        // Standalone fastening starts from plate UP even if the stopper is still UP.
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.BoltFasteningStopperUp, true);
        gantry.Motion.Feedback.PositionChanged += (x, y, _) =>
        {
            if (Math.Abs(x - settings.BoltFastening.PickupPosition.X) < 0.01
                && Math.Abs(y - settings.BoltFastening.PickupPosition.Y) < 0.01)
                visitedPickupFeeder = true;
        };
        io.OutputChanged += (output, on) =>
        {
            outputs.Enqueue((output, on));
            if (on && output == OutputIo.PickupHeadVacuumPump)
            {
                Assert.True(VirtualTestSupport.IsAt(gantry.Motion.Feedback, settings.BoltFastening.PickupPosition));
                Assert.Equal(StationCylinderState.Up, gantry.PickupHeadPosition);
                Assert.Equal(StationCylinderState.Up, gantry.ShootingHeadPosition);
                Assert.Equal(StationCylinderState.Down, gantry.PickupTablePosition);
                var position = gantry.Motion.Feedback.Position;
                pickups.Enqueue((position.X, position.Y, position.Z));
            }
            if (on && output is OutputIo.ShootingBoltStart or OutputIo.PickupBoltStart)
            {
                Assert.True(gantry.IsHorizontalMoveAllowed); // START precedes cylinder descent.
                var shooting = output == OutputIo.ShootingBoltStart;
                Assert.True(io.GetOutput(shooting ? OutputIo.ShootingBoltPreset1 : OutputIo.PickupBoltPreset1));
                Assert.False(io.GetOutput(shooting ? OutputIo.ShootingBoltPreset2 : OutputIo.PickupBoltPreset2));
                Assert.False(io.GetOutput(shooting ? OutputIo.ShootingBoltPreset3 : OutputIo.PickupBoltPreset3));
                var position = gantry.Motion.Feedback.Position;
                starts.Enqueue((output == OutputIo.ShootingBoltStart ? FasteningHead.Shooting : FasteningHead.Pickup,
                    position.X, position.Y, position.Z));
            }
            if (on && output is OutputIo.ShootingHeadDown or OutputIo.PickupHeadDown)
            {
                var position = gantry.Motion.Feedback.Position;
                Assert.True(io.GetOutput(output == OutputIo.ShootingHeadDown
                    ? OutputIo.ShootingBoltStart : OutputIo.PickupBoltStart));
                descents.Enqueue((output == OutputIo.ShootingHeadDown ? FasteningHead.Shooting : FasteningHead.Pickup,
                    position.X, position.Y, position.Z));
            }
        };
        work.Changed += () =>
        {
            if (work.Completed)
                machine.Stop();
        };
        try
        {
            Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var run = machine.StartAsync(timeout.Token);
            await WaitUntilAsync(() => state.AutomaticRunning);
            if (!shootingFeeding)
                io.SetInput(InputIo.ShootingTubeBoltDetected, true); // Disabled supply does not wait for the tube.
            io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
            await run.WaitAsync(TimeSpan.FromSeconds(12));
            Assert.True(state.Alarm == MachineAlarm.None, state.AlarmDetail);
            Assert.True(work.Completed, services.GetRequiredService<BoltFasteningStation>().NextStep.ToString());
            var assembly = Assert.Single(work.Assemblies);
            Assert.Equal(shootingFeeding ? BoltResultSource.Controller : BoltResultSource.DryRun,
                Assert.Single(assembly.ShootingBoltResults).Value.Source);
            Assert.Equal(2, assembly.PickupBoltResults.Count);
            Assert.All(assembly.PickupBoltResults.Values, result =>
                Assert.Equal(pickupFeeding ? BoltResultSource.Controller : BoltResultSource.DryRun, result.Source));
            Assert.All(assembly.ShootingBoltResults.Values.Concat(assembly.PickupBoltResults.Values), result =>
                Assert.Equal(result.Source == BoltResultSource.Controller, result.Torque is not null));
            Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);
            var positions = new[]
            {
                (FasteningHead.Shooting, 10.5, 9.5, 8.25),
                (FasteningHead.Pickup, 21d, 11d, 11.5),
                (FasteningHead.Pickup, 30d, 10d, 12d),
            };
            Assert.Equal(positions, descents.ToArray());
            Assert.Equal(positions, starts.ToArray());
            var operation = services.GetRequiredService<OperationViewModel>();
            Assert.All(operation.BoltTargets, bolt => Assert.Equal(
                (bolt.Head == FasteningHead.Pickup ? pickupFeeding : shootingFeeding)
                    ? BoltTargetState.Ok : BoltTargetState.Ng,
                bolt.State));
            Assert.True(visitedPickupFeeder);
            if (pickupFeeding)
                Assert.Equal(new[] { (100d, 50d, 10d), (100d, 50d, 10d) }, pickups.ToArray());
            else
                Assert.Empty(pickups);
            Assert.Equal(pickupEnabled, settings.Units.PickupBoltFeeder);
            Assert.Equal(shootingEnabled, settings.Units.ShootingBoltFeeder);
            Assert.Equal(pickupFeeding || shootingFeeding, feederRan);
            if (!pickupFeeding)
            {
                Assert.False(io.GetInput(InputIo.PickupFeederBoltDetected));
                Assert.False(io.GetInput(InputIo.PickupHeadVacuumDetected));
            }
            if (!shootingFeeding)
                Assert.DoesNotContain(outputs, command =>
                    command.On && command.Output is OutputIo.ShootingHeadVacuumPump
                        or OutputIo.ShootBolt or OutputIo.ShootingEscapeForward
                    || !command.On && command.Output == OutputIo.ShootingFeederOff);
            Assert.False(io.GetOutput(OutputIo.PickupBoltStart));
            Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
            Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            Assert.True(io.GetInput(InputIo.PickupHeadUp));
            Assert.True(io.GetInput(InputIo.ShootingHeadUp));
            recipe.Pcb.BoltPoints[0].FasteningX = null;
            Assert.False(machine.TeachingReady); // Feeder OFF still requires taught fastening coordinates.
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(FasteningHead.Shooting, DryRunEnd.Completed)]
    [InlineData(FasteningHead.Shooting, DryRunEnd.Cancelled)]
    [InlineData(FasteningHead.Shooting, DryRunEnd.MissingUpFeedback)]
    [InlineData(FasteningHead.Pickup, DryRunEnd.Completed)]
    public async Task FasteningWithoutDownFeedbackStillRequiresUpFeedbackAndStopsOnCancellation(
        FasteningHead head, DryRunEnd end)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        var stopDuringDescent = end == DryRunEnd.Cancelled;
        var missingUpFeedback = end == DryRunEnd.MissingUpFeedback;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Id = VirtualTestSupport.BoltId(1), Head = head, X = 10, Y = 10 }];
        var machine = services.GetRequiredService<MachineController>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        if (head == FasteningHead.Pickup)
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupTableDown, true, CancellationToken.None);
        settings.Options.TimeoutMilliseconds = 100;
        io.AutoResponseEnabled = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var descended = false;
        var raising = false;
        var (start, cylinder, up, down) = head == FasteningHead.Pickup
            ? (OutputIo.PickupBoltStart, OutputIo.PickupHeadDown,
                InputIo.PickupHeadUp, InputIo.PickupHeadDown)
            : (OutputIo.ShootingBoltStart, OutputIo.ShootingHeadDown,
                InputIo.ShootingHeadUp, InputIo.ShootingHeadDown);
        io.OutputChanged += (output, on) =>
        {
            if (output != cylinder)
                return;
            if (on)
            {
                Assert.True(io.GetOutput(start));
                descended = true;
                io.SetInputs((up, false), (down, false));
                if (stopDuringDescent)
                    stop.Cancel();
            }
            else if (descended)
            {
                raising = true;
                io.SetInput(up, !missingUpFeedback);
            }
        };
        work.Changed += () =>
        {
            if (work.Completed)
                stop.Cancel();
        };
        try
        {
            var run = station.RunAsync(stop.Token);
            if (missingUpFeedback)
                await Assert.ThrowsAsync<IoTimeoutException>(() => run);
            else
                await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(descended);
            Assert.False(io.GetInput(down));
            Assert.Equal(!stopDuringDescent, raising);
            Assert.Equal(!stopDuringDescent && !missingUpFeedback, work.Completed);
            var assembly = Assert.Single(work.Assemblies);
            var results = head == FasteningHead.Pickup ? assembly.PickupBoltResults : assembly.ShootingBoltResults;
            if (stopDuringDescent)
                Assert.Empty(results);
            else
                Assert.Equal(BoltResultSource.DryRun,
                    Assert.Single(results).Value.Source);
            Assert.False(io.GetOutput(start));
        }
        finally
        {
            stop.Cancel();
            await machine.ShutdownAsync();
        }
    }

    public enum DryRunEnd
    {
        Completed,
        Cancelled,
        MissingUpFeedback,
    }

    [Fact]
    public async Task DisabledPickupFeederKeepsLiftInterlockBeforeNewCarrier()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.Pcb.BoltPoints = [new() { Id = VirtualTestSupport.BoltId(1), Head = FasteningHead.Pickup, X = 20, Y = 10 }];
        var machine = services.GetRequiredService<MachineController>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PickupFeederBoltDetected, false);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var pickups = 0;
        var starts = 0;
        var vacuumRequested = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PickupBoltStart && on)
            {
                Assert.True(station.IsHorizontalMoveAllowed);
                starts++;
            }
            vacuumRequested |= output == OutputIo.PickupHeadVacuumPump && on;
        };
        var atPickup = false;
        station.Motion.Feedback.StateChanged += () =>
        {
            var wasAtPickup = atPickup;
            atPickup = VirtualTestSupport.IsAt(station.Motion.Feedback, settings.BoltFastening.PickupPosition);
            if (atPickup && !wasAtPickup)
            {
                Assert.Equal(StationCylinderState.Up, station.PickupHeadPosition);
                pickups++;
                if (pickups == 1)
                {
                    settings.Options.TimeoutMilliseconds = 100;
                    io.AutoResponseEnabled = false;
                    // External loss of UP feedback must still block travel after pickup.
                    io.SetInputs((InputIo.PickupHeadUp, false), (InputIo.PickupHeadDown, true));
                }
            }
        };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<IoTimeoutException>(() => station.RunAsync(timeout.Token));
            Assert.Equal(1, pickups);
            Assert.Equal(0, starts);
            Assert.False(vacuumRequested);
            Assert.Equal((settings.BoltFastening.PickupPosition.X, settings.BoltFastening.PickupPosition.Y),
                (station.Motion.Feedback.Position.X, station.Motion.Feedback.Position.Y));
            Assert.Equal(settings.BoltFastening.SafeZ, station.Motion.Feedback.Position.Z);
            Assert.Equal(StationCylinderState.Down, station.PickupHeadPosition);
            Assert.False(io.GetInput(InputIo.PickupHeadVacuumDetected));
            Assert.Empty(work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults);

            settings.Options.TimeoutMilliseconds = 2_000;
            io.AutoResponseEnabled = true;
            VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            work.ClearJob();
            await station.SetVacuumAsync(FasteningHead.Pickup, false, CancellationToken.None);
            VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            work.Changed += () =>
            {
                if (work.Completed)
                    stop.Cancel();
            };
            await station.RunAsync(stop.Token);
            Assert.True(work.Completed);
            Assert.Equal(2, pickups);
            Assert.False(vacuumRequested);
            Assert.False(io.GetInput(InputIo.PickupHeadVacuumDetected));
            Assert.Equal(1, starts); // Only the new carrier reaches fastening START.
            Assert.Equal(AssemblyResult.Ng, work.GetAssembly(HeatSinkSlot.HeatSink1).FasteningResult);
            Assert.Equal(BoltResultSource.DryRun,
                Assert.Single(work.GetAssembly(HeatSinkSlot.HeatSink1).PickupBoltResults).Value.Source);
            Assert.True(station.IsHorizontalMoveAllowed);
            Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Trait("Category", "MachineFlow")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Station3ConfigurationRunsWithoutUpstreamHardware(bool missingBolts)
    {
        var settings = new MachineSettings
        {
            Units = new()
            {
                MainConveyor = true,
                Inspection = true,
                NgConveyor = true,
                PcbSupply = false,
                PcbPlacement = false,
                BoltFastening = false,
                PickupBoltFeeder = false,
                ShootingBoltFeeder = false,
            },
        };
        FastHomes(settings);
        settings.InspectionGantry.Motion = FastMotion();
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        settings.NgConveyor.CarrierStopDelaySeconds = 0;
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 100 };
        settings.NgCarrierTransfer.CarrierPickupPosition = new() { X = 5, Y = 20 };
        settings.NgCarrierTransfer.ShuttlePlacePosition = new() { X = 100, Y = 20 };
        settings.NgCarrierTransfer.WaitingPosition = new() { X = 5, Y = 20 };
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        PrepareCarrierTeaching(settings, recipe);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var inspection = services.GetRequiredService<InspectionStation>();
        IMotionFeedback[] disabledMotions = [
            services.GetRequiredService<PcbSupplier>().Motion.Feedback,
            services.GetRequiredService<PcbPlacer>().Motion.Feedback,
            services.GetRequiredService<BoltFasteningStation>().Motion.Feedback,
        ];
        var disabledOutputs = settings.PcbSupplyHardware.Outputs.Keys.Concat(
            settings.PcbPlacementHandlerHardware.Outputs.Keys)
            .Concat(settings.BoltFasteningHardware.Outputs.Keys)
            .Concat(settings.BoltFeederHardware.Outputs.Keys)
            .ToHashSet();
        var unexpectedOutputs = new ConcurrentBag<OutputIo>();
        var adcFrames = 0;
        foreach (var head in new[] { FasteningHead.Pickup, FasteningHead.Shooting })
            services.GetRequiredKeyedService<IAdcBus>(head).FrameTransferred += (_, _) => Interlocked.Increment(
                ref adcFrames);
        io.OutputChanged += (output, value) =>
        {
            if (disabledOutputs.Contains(output) && (output == OutputIo.ShootingFeederOff ? !value : value))
                unexpectedOutputs.Add(output);
        };
        var arrived = 0;
        var entered = false;
        var exited = false;
        io.InputChanged += (input, value) =>
        {
            if (value)
            {
                Interlocked.Or(
                    ref arrived,
                    input switch
                    {
                        InputIo.PcbPlacementHeatSink1Present => 1,
                        InputIo.BoltFasteningHeatSink1Present => 2,
                        InputIo.InspectionHeatSink1Present => 4,
                        _ => 0,
                    });
            }

            if (input == InputIo.MainConveyorEntryCarrierDetected && value)
                entered = true;
            if (input == InputIo.MainConveyorAvailableFromFront2 && value && entered)
                io.SetInput(input, false);
            if (input == InputIo.MainConveyorReadyFromRear && !value && entered)
                exited = true;
        };
        if (missingBolts)
        {
            var camera = services.GetRequiredService<VirtualCamera>();
            camera.BoltsPresent = false;
        }

        await machine.InitializeAsync();
        Assert.True(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => machine.IsStartAllowed);
        var run = machine.StartAsync();
        try
        {
            Assert.True(
                await VirtualTestSupport.WaitUntilAsync(() => inspection.Station.Completed, TimeSpan.FromSeconds(10)),
                $"Arrived={arrived}; Inspection={inspection.Step}; Main={services.GetRequiredService<MainConveyor>().Step}; {state.AlarmDetail}");
            Assert.Equal(7, arrived);
            Assert.Equal(missingBolts, inspection.HasNg);
            Assert.Equal(2, inspection.Station.Assemblies.Count());
            Assert.All(
                inspection.Station.Assemblies,
                assembly =>
                {
                    Assert.Equal(
                        assembly.HeatSink == HeatSinkSlot.HeatSink1 ? "PCB-1" : "PCB-2",
                        assembly.PcbBarcode);
                    Assert.Empty(assembly.ShootingBoltResults);
                    Assert.Empty(assembly.PickupBoltResults);
                    Assert.Equal(!missingBolts, Assert.Single(assembly.BoltPresenceResults).Value);
                });
            if (missingBolts)
            {
                Assert.True(
                    await VirtualTestSupport.WaitUntilAsync(
                        () => io.GetInput(InputIo.NgConveyorPosition1Occupied)
                            && io.GetInput(InputIo.NgShuttleUp)
                            && !io.GetOutput(OutputIo.NgConveyorRun),
                        TimeSpan.FromSeconds(5)),
                    $"Inspection={inspection.Step}; NG={services.GetRequiredService<NgCarrierConveyor>().Step}; {state.AlarmDetail}");
                Assert.False(exited);
            }
            else
            {
                Assert.False(exited);
                io.SetInput(InputIo.MainConveyorReadyFromRear, true);
                await WaitUntilAsync(() => exited);
            }
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }

        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Empty(unexpectedOutputs);
        Assert.Equal(0, adcFrames);
        Assert.All(
            disabledMotions,
            motion =>
                Assert.All(
                    motion.Axes,
                    axis =>
                    {
                        Assert.False(motion.GetAxisState(axis).ServoOn);
                        Assert.False(motion.GetAxisState(axis).Homed);
                    }));
    }

    [Fact]
    public async Task InspectionSeatsAtPickupThenUsesSeparateWaitingPosition()
    {
        await using var services = CreateInspectionServices(enableConveyor: true);
        services.GetRequiredService<UnitSettings>().BoltFastening = true;
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionStation>();
        var inspection = services.GetRequiredService<InspectionStation>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        var transferSettings = services.GetRequiredService<NgCarrierTransferSettings>();
        transferSettings.WaitingPosition = new() { X = 15, Y = 35 };
        var waitingPosition = transferSettings.WaitingPosition;
        var seatedAtPickup = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.InspectionBackupPlateUp && on)
            {
                Assert.True(VirtualTestSupport.IsAt(gantry.Motion.Feedback, transferSettings.CarrierPickupPosition!));
                Assert.False(VirtualTestSupport.IsAt(gantry.Motion.Feedback, waitingPosition));
                seatedAtPickup = true;
            }
        };
        var barcodePosition = recipe.CarrierImages.Single(image => image.IsBarcode
            && image.HeatSink == HeatSinkSlot.HeatSink1).Center!;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await work.Station.PrepareToReceiveAsync(CancellationToken.None);
        await gantry.MoveToAsync(barcodePosition);
        Assert.False(VirtualTestSupport.IsAt(gantry.Motion.Feedback, waitingPosition));
        var waitedAfterSeating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inspection.Trace += message =>
        {
            if (message.Contains(": WaitingForConveyor ", StringComparison.Ordinal))
                waitedAfterSeating.TrySetResult();
        };
        var barcodeCaptured = false;
        inspection.InspectionCaptured += (image, pcb, bolt) =>
        {
            if (bolt is null)
            {
                Assert.True(VirtualTestSupport.IsAt(gantry.Motion.Feedback, barcodePosition));
                barcodeCaptured = true;
            }
        };
        work.RequestCarrierSeating(work.Station.CurrentJob);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = inspection.RunAsync(stop.Token);
        try
        {
            await waitedAfterSeating.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(StationCylinderState.Up, work.Station.BackupPlate);
            Assert.True(seatedAtPickup);
            Assert.True(VirtualTestSupport.IsAt(gantry.Motion.Feedback, waitingPosition));
            Assert.False(barcodeCaptured);
            Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
            Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));

            await work.Station.PrepareToReceiveAsync(stop.Token);
            work.RequestInspection(work.Station.CurrentJob);
            Assert.True(await VirtualTestSupport.WaitUntilAsync(() => work.Station.Completed, TimeSpan.FromSeconds(2)));
            Assert.True(barcodeCaptured);
            Assert.True(VirtualTestSupport.IsAt(gantry.Motion.Feedback, waitingPosition));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task InspectionSeatingCancelledDuringPickupTravelDoesNotRaisePlate()
    {
        await using var services = CreateInspectionServices(enableConveyor: true);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionStation>();
        var station = services.GetRequiredService<InspectionStation>();
        var transfer = services.GetRequiredService<InspectionStation>();
        var settings = services.GetRequiredService<InspectionGantrySettings>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await work.Station.PrepareToReceiveAsync(CancellationToken.None);
        await transfer.MoveToAsync(new() { X = 100, Y = 100 }, 10_000);
        settings.Motion.HorizontalSpeed = 1;
        var raised = false;
        io.OutputChanged += (output, on) => raised |= output == OutputIo.InspectionBackupPlateUp && on;
        using var stop = new CancellationTokenSource();
        work.RequestCarrierSeating(work.Station.CurrentJob);
        var run = station.RunAsync(stop.Token);
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => transfer.Motion.Feedback.IsMoving, TimeSpan.FromSeconds(2)));
            Assert.Equal(InspectionStationState.SeatingCarrier, station.NextStep);
            Assert.False(raised);
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(raised);
            Assert.False(work.CarrierSeatingRequested);
            Assert.Equal(StationCylinderState.Down, work.Station.BackupPlate);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InspectionParksThenDischargesOrRaisesBeforeOtherTransfers(bool ng, bool rearReady)
    {
        await using var services = CreateInspectionServices(enableConveyor: true);
        var machine = services.GetRequiredService<MachineController>();
        var machineState = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionStation>();
        var station = services.GetRequiredService<InspectionStation>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var pickup = services.GetRequiredService<InspectionStation>();
        var transferSettings = services.GetRequiredService<NgCarrierTransferSettings>();
        transferSettings.WaitingPosition = new() { X = 15, Y = 35 };
        var waitingPosition = transferSettings.WaitingPosition;
        services.GetRequiredService<VirtualCamera>().BoltsPresent = !ng;
        services.GetRequiredService<UnitSettings>().NgConveyor = true;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, rearReady);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        // Raise S3 before arrival; the main sequence must lower it for inspection.
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionStopperUp, false);
        var inspectedAssembly = work.Station.GetAssembly(HeatSinkSlot.HeatSink1);
        inspectedAssembly.PcbBarcode = "PCB-1";

        var inspected = false;
        var returned = false;
        var raises = 0;
        var conveyorSteps = new ConcurrentQueue<string>();
        conveyor.Trace += conveyorSteps.Enqueue;
        var beltStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discharged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        station.Trace += message =>
        {
            if (!inspected && station.ActiveBolt is not null
                && message.StartsWith("InspectionStation: InspectingPoint ", StringComparison.Ordinal))
            {
                Assert.True(work.IsAtInspectionPosition);
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                Assert.Equal(MainConveyorState.WaitingForInspection, conveyor.Step);
                inspected = true;
                // A carrier arriving after inspection was requested must wait for it to finish.
                io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            }
            if (inspected && message.Contains(": CompletingInspection ", StringComparison.Ordinal))
            {
                Assert.False(work.Station.Completed);
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                returned = true;
            }
        };
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.InspectionBackupPlateUp && on)
            {
                Assert.True(work.Station.Completed);
                Assert.Equal(InspectionStationState.SeatingCarrier, station.NextStep);
                Assert.Equal(MainConveyorState.WaitingForInspectionTransfer, conveyor.Step);
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                Assert.True(VirtualTestSupport.IsAt(pickup.Motion.Feedback, services.GetRequiredService<NgCarrierTransferSettings>().CarrierPickupPosition!));
                Interlocked.Increment(ref raises);
            }
            if (output == OutputIo.NgCarrierPickupDown && on && work.Station.CarrierPresent)
            {
                Assert.True(ng);
                Assert.True(work.Station.Completed);
                Assert.True(work.Station.CarrierSeated);
            }
            if (output != OutputIo.MainConveyorRun || !on)
                return;
            Assert.True(inspected);
            Assert.True(returned);
            // NG pickup can already have removed the completed carrier from S3.
            Assert.Equal(ng ? AssemblyResult.Ng : AssemblyResult.Ok, inspectedAssembly.InspectionResult);
            if (!ng)
                Assert.True(work.Station.Completed);
            if (conveyor.Step is MainConveyorState.DischargingInspectionCarrier)
            {
                Assert.False(ng);
                Assert.True(work.IsRearDischargeReady);
                Assert.True(VirtualTestSupport.IsAt(pickup.Motion.Feedback, waitingPosition));
                discharged.TrySetResult();
            }
            else
            {
                Assert.Equal(MainConveyorState.MovingPcbPlacementToBoltFastening, conveyor.Step);
                Assert.Equal(StationCylinderState.Up, work.Station.BackupPlate);
            }
            beltStarted.TrySetResult();
        };

        var run = machine.StartAsync();
        try
        {
            await WaitUntilAsync(() => machineState.AutomaticRunning);
            inspectedAssembly = work.Station.GetAssembly(HeatSinkSlot.HeatSink1);
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => beltStarted.Task.IsCompleted, TimeSpan.FromSeconds(5)),
                $"State={conveyor.Step}; alarm={machineState.AlarmMessage}; "
                    + string.Join(" | ", conveyorSteps));
            if (!ng && rearReady)
            {
                Assert.Equal(0, Volatile.Read(ref raises));
                Assert.True(discharged.Task.IsCompletedSuccessfully);
            }
            else
            {
                Assert.Equal(1, Volatile.Read(ref raises));
                if (ng)
                {
                    Assert.True(await VirtualTestSupport.WaitUntilAsync(
                        () => io.GetInput(InputIo.NgCarrierDetected) && pickup.IsRaised,
                        TimeSpan.FromSeconds(3)));
                }
                else
                {
                    io.SetInput(InputIo.MainConveyorReadyFromRear, true);
                    Assert.True(await VirtualTestSupport.WaitUntilAsync(
                        () => discharged.Task.IsCompleted, TimeSpan.FromSeconds(5)),
                        $"State={conveyor.Step}; completed={work.Station.Completed}; seated={work.Station.CarrierSeated}; "
                            + $"parked={work.IsRearDischargeReady}; ready={conveyor.DownstreamReady}; "
                            + $"alarm={machineState.AlarmMessage}; "
                            + string.Join(" | ", conveyorSteps));
                }
            }
            Assert.Equal(MachineAlarm.None, machineState.Alarm);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectionArrivalYieldsToStation2RefillAndFrontReceiving(bool carrierWaitingAtS1)
    {
        await using var services = CreateInspectionServices(enableConveyor: true);
        var machine = services.GetRequiredService<MachineController>();
        var machineState = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var inspection = services.GetRequiredService<InspectionStation>();
        var work = services.GetRequiredService<InspectionStation>();
        var fastening = services.GetRequiredService<BoltFasteningStation>().Station;
        var placement = services.GetRequiredService<PcbPlacer>().Station;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);
        await fastening.SeatAsync(CancellationToken.None);
        fastening.Complete(fastening.CurrentJob);
        var arrivingJob = fastening.CurrentJob;
        var arrivingAssembly = fastening.GetAssembly(HeatSinkSlot.HeatSink2);
        arrivingAssembly.PcbBarcode = "S2-CARRIER";
        if (carrierWaitingAtS1)
        {
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            await placement.SeatAsync(CancellationToken.None);
            placement.Complete(placement.CurrentJob);
        }
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);

        var expectedMoves = carrierWaitingAtS1
            ? new[]
            {
                MainConveyorState.MovingBoltFasteningToInspection,
                MainConveyorState.MovingPcbPlacementToBoltFastening,
                MainConveyorState.ReceivingFrontCarrier,
            }
            : new[]
            {
                MainConveyorState.MovingBoltFasteningToInspection,
                MainConveyorState.ReceivingFrontCarrier,
                MainConveyorState.MovingPcbPlacementToBoltFastening,
            };
        var moves = new ConcurrentQueue<MainConveyorState>();
        var trace = new ConcurrentQueue<string>();
        conveyor.Trace += trace.Enqueue;
        inspection.Trace += trace.Enqueue;
        var plateMovesBeforeInspection = new ConcurrentQueue<bool>();
        var inspected = false;
        inspection.Trace += message =>
        {
            if (inspected || inspection.ActiveBolt is null
                || !message.StartsWith("InspectionStation: InspectingPoint ", StringComparison.Ordinal))
                return;
            Assert.True(work.InspectionRequested);
            Assert.True(work.IsAtInspectionPosition);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.True(fastening.CarrierSeated);
            Assert.Equal(carrierWaitingAtS1, placement.CarrierSeated);
            Assert.Equal(arrivingJob.Id, work.Station.CurrentJob.Id);
            Assert.Same(arrivingAssembly, work.Station.GetAssembly(HeatSinkSlot.HeatSink2));
            Assert.Equal("PCB-2", arrivingAssembly.PcbBarcode);
            Assert.Equal(new[] { true, false }, plateMovesBeforeInspection);
            Assert.Equal(expectedMoves, moves);
            inspected = true;
        };
        io.OutputChanged += (output, on) =>
        {
            if (inspected)
                return;
            if (output == OutputIo.InspectionBackupPlateUp)
            {
                Assert.False(work.InspectionRequested);
                Assert.False(work.Station.Completed);
                if (on)
                {
                    Assert.Equal(InspectionStationState.SeatingCarrier, inspection.NextStep);
                    Assert.Equal(MainConveyorState.WaitingForInspectionTransfer, conveyor.Step);
                    Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                    Assert.True(VirtualTestSupport.IsAt(
                        services.GetRequiredService<InspectionStation>().Motion.Feedback,
                        services.GetRequiredService<NgCarrierTransferSettings>().CarrierPickupPosition!));
                }
                plateMovesBeforeInspection.Enqueue(on);
            }
            if (output != OutputIo.MainConveyorRun || !on)
                return;
            var state = Assert.IsType<MainConveyorState>(conveyor.Step);
            moves.Enqueue(state);
            if (state == MainConveyorState.MovingBoltFasteningToInspection)
                return;
            Assert.True(work.Station.CarrierSeated);
            Assert.False(work.InspectionRequested);
            Assert.False(work.Station.Completed);
        };

        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => work.Station.Completed, TimeSpan.FromSeconds(8)),
                $"Alarm={machineState.AlarmMessage}; " + string.Join(" | ", trace));
            Assert.True(inspected);
            Assert.Equal(MachineAlarm.None, machineState.Alarm);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            await machine.ShutdownAsync();
        }
        Assert.False(work.InspectionRequested);
    }

    [Fact]
    public async Task InspectionParksWhileIdleAndRejectsResultsIfConveyorStarts()
    {
        await using var services = CreateInspectionServices(enableConveyor: false);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionStation>();
        var station = services.GetRequiredService<InspectionStation>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var waitingPosition = services.GetRequiredService<NgCarrierTransferSettings>().WaitingPosition!;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 12, Y = 7 }, 10_000);
        var beltForcedOn = false;
        station.Trace += message =>
        {
            if (station.ActiveBolt is not null
                && message.StartsWith("InspectionStation: InspectingPoint ", StringComparison.Ordinal))
            {
                beltForcedOn = true;
                io.SetOutput(OutputIo.MainConveyorRun, true);
            }
        };
        using var stop = new CancellationTokenSource();
        var run = station.RunAsync(stop.Token);
        try
        {
            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => work.IsRearDischargeReady, TimeSpan.FromSeconds(2)));
            Assert.True(VirtualTestSupport.IsAt(gantry.Motion.Feedback, waitingPosition));
            Assert.Equal(InspectionStationState.Waiting, station.NextStep);
            io.SetInput(InputIo.InspectionHeatSink1Present, true);
            var assembly = work.Station.GetAssembly(HeatSinkSlot.HeatSink1);
            assembly.PcbBarcode = "PCB-1";
            await work.Station.PrepareToReceiveAsync(CancellationToken.None);
            await Assert.ThrowsAsync<MotionInterlockException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(beltForcedOn);
            Assert.False(work.Station.Completed);
            Assert.False(work.IsRearDischargeReady);
            Assert.Empty(assembly.BoltPresenceResults);
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            await machine.ShutdownAsync();
        }
    }

    private static ServiceProvider CreateInspectionServices(bool enableConveyor)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.MainConveyor = enableConveyor;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        return services;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetPreservesSeatedCarrierAndAllowsStartingItsTransfer(bool stoppedAutomatically)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<PcbPlacer>().Station;
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
            await work.SeatAsync(CancellationToken.None);
            var job = work.CurrentJob;
            var assembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
            work.Complete(job);
            var completed = work.Completed;
            if (stoppedAutomatically)
            {
                var run = machine.StartAsync();
                try
                {
                    await WaitUntilAsync(() => state.AutomaticRunning);
                }
                finally
                {
                    machine.Stop();
                    await run.WaitAsync(TimeSpan.FromSeconds(3));
                }
            }

            state.SetError(MachineAlarm.MainConveyor);
            var plateWrites = 0;
            io.OutputChanged += (output, _) =>
            {
                if (output is OutputIo.PcbPlacementBackupPlateUp or OutputIo.PcbPlacementStopperUp)
                    plateWrites++;
            };
            Assert.True(machine.IsResetAllowed);
            await machine.ResetAsync();

            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Null(state.AlarmDetail);
            Assert.True(work.CarrierSeated);
            Assert.True(io.GetOutput(OutputIo.PcbPlacementBackupPlateUp));
            Assert.Equal(0, plateWrites);
            Assert.Same(job, work.CurrentJob);
            Assert.Same(assembly, Assert.Single(work.Assemblies));
            Assert.Equal(completed, work.Completed);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.True(machine.IsStartAllowed);

            settings.Units.Inspection = false;
            settings.Units.MainConveyor = true;
            var restarted = machine.StartAsync();
            try
            {
                await VirtualTestSupport.WaitForOutputAsync(io, OutputIo.MainConveyorRun, true);
                Assert.Equal(StationCylinderState.Down, work.BackupPlate);
                Assert.True(state.AutomaticRunning);
            }
            finally
            {
                machine.Stop();
                await restarted.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StartAllowsSecuredSupplyWaitingAtHandoffInNormalMode()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supplier = services.GetRequiredService<PcbSupplier>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            await supplier.PrepareHandoffAsync(CancellationToken.None);
            io.AutoResponseEnabled = false;
            io.SetInputs((InputIo.PcbSupplyPcbDetected, true),
                (InputIo.PcbSupplyGripperClosed, true), (InputIo.PcbSupplyGripperOpen, false),
                (InputIo.PcbSupplyIpmFixerForward, true));
            Assert.True(supplier.IsHandoffRestartAllowed);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var admitted = false;
            state.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(MachineState.AutomaticRunning) && state.AutomaticRunning)
                {
                    admitted = true;
                    stop.Cancel();
                }
            };
            await machine.StartAsync(stop.Token);
            Assert.True(admitted, $"START={machine.StartBlock}; alarm={state.AlarmDetail}");
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.True(supplier.PcbSecured);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(true, false, false, StartCheckState.Empty)]
    [InlineData(true, false, true, StartCheckState.Empty)]
    [InlineData(false, true, false, StartCheckState.MaterialRemaining)]
    [InlineData(false, false, false, StartCheckState.MaterialRemaining)]
    public async Task StartReviewDistinguishesVisibleSupplyPcbFromHeldPcb(
        bool gripperOpen, bool gripperClosed, bool fixerForward, StartCheckState expected)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(default);
            await WaitUntilAsync(() => machine.IsStartAllowed);
            io.SetInputs((InputIo.PcbSupplyPcbDetected, true),
                (InputIo.PcbSupplyGripperOpen, gripperOpen),
                (InputIo.PcbSupplyGripperClosed, gripperClosed),
                (InputIo.PcbSupplyIpmFixerForward, fixerForward),
                (InputIo.PcbSupplyRotated, true), (InputIo.PcbSupplyUnrotated, false));
            machine.CheckStartMaterials();
            Assert.Equal(expected, machine.StartChecks[StartArea.Supply]);

            using var stop = new CancellationTokenSource();
            var admitted = false;
            state.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(MachineState.AutomaticRunning) && state.AutomaticRunning)
                {
                    admitted = true;
                    stop.Cancel();
                }
            };
            await machine.StartAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(expected == StartCheckState.Empty, admitted);
            Assert.Equal(expected, machine.StartChecks[StartArea.Supply]);
            Assert.False(state.IsError, state.AlarmDetail);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StartReviewExcludesDisabledUnitsAndAllowsConveyorStartWithTheirMaterials()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var review = services.GetRequiredService<OperationViewModel>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(default);
            io.SetInputs(
                (InputIo.PcbSupplyPcbDetected, true),
                (InputIo.PcbPlacementPcbDetected, true),
                (InputIo.PcbPlacementVacuumDetected, true),
                (InputIo.PickupHeadVacuumDetected, true),
                (InputIo.ShootingHeadVacuumDetected, true),
                (InputIo.ShootingTubeBoltDetected, true),
                (InputIo.PcbPlacementHeatSink1Present, true),
                (InputIo.BoltFasteningHeatSink1Present, true),
                (InputIo.InspectionHeatSink1Present, true));
            var writes = 0;
            io.OutputChanged += (output, on) => writes++;

            await review.CheckStartCommand.ExecuteAsync(null);

            Assert.All(machine.StartChecks.Values, check => Assert.Equal(StartCheckState.Disabled, check));
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.True(review.IsStartReviewAllowed);
            Assert.False(state.AutomaticRunning);
            Assert.Equal(0, writes);

            var admitted = false;
            state.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(MachineState.AutomaticRunning) && state.AutomaticRunning)
                {
                    admitted = true;
                    review.ConfirmStartCommand.Cancel();
                }
            };
            await review.ConfirmStartCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(admitted);
            Assert.Equal(MachineAlarm.None, state.Alarm);

            // An explicitly repeated check reads a unit again after it is enabled.
            settings.Units.BoltFastening = true;
            machine.CheckStartMaterials();
            Assert.Equal(StartCheckState.MaterialRemaining, machine.StartChecks[StartArea.PickupHead]);
            Assert.Equal(StartCheckState.MaterialRemaining, machine.StartChecks[StartArea.ShootingHead]);
            Assert.Equal(StartCheckState.UnfinishedCarrier, machine.StartChecks[StartArea.Station2]);
            Assert.Equal(StartCheckState.Disabled, machine.StartChecks[StartArea.Supply]);
            Assert.Equal(StartCheckState.Disabled, machine.StartChecks[StartArea.Station1]);
            Assert.Equal(StartCheckState.Disabled, machine.StartChecks[StartArea.Station3]);
            Assert.False(review.IsStartReviewAllowed);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StartReviewReportsEveryBlockedAreaWithoutMovingOrClearingJobs()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>().Station;
        var fastening = services.GetRequiredService<BoltFasteningStation>().Station;
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            await WaitUntilAsync(() => machine.IsStartAllowed);
            io.SetInputs((InputIo.PcbSupplyPcbDetected, true), (InputIo.PcbPlacementVacuumDetected, true),
                (InputIo.PcbSupplyGripperClosed, true), (InputIo.PcbSupplyGripperOpen, false),
                (InputIo.ShootingTubeBoltDetected, true), (InputIo.PcbPlacementHeatSink1Present, true),
                (InputIo.BoltFasteningHeatSink1Present, true));
            fastening.Complete(fastening.CurrentJob);
            var placementJob = placement.CurrentJob;
            var fasteningJob = fastening.CurrentJob;
            var writes = 0;
            io.OutputChanged += (output, on) => writes++;

            machine.CheckStartMaterials();

            Assert.Equal(StartCheckState.MaterialRemaining, machine.StartChecks[StartArea.Supply]);
            Assert.Equal(StartCheckState.MaterialRemaining, machine.StartChecks[StartArea.Placement]);
            Assert.Equal(StartCheckState.MaterialRemaining, machine.StartChecks[StartArea.ShootingHead]);
            Assert.Equal(StartCheckState.UnfinishedCarrier, machine.StartChecks[StartArea.Station1]);
            Assert.Equal(StartCheckState.Completed, machine.StartChecks[StartArea.Station2]);
            Assert.Equal(StartCheckState.Empty, machine.StartChecks[StartArea.PickupHead]);
            Assert.Equal(StartCheckState.Empty, machine.StartChecks[StartArea.Station3]);
            Assert.Equal(0, writes);
            Assert.False(state.AutomaticRunning);

            io.SetInputs((InputIo.PcbSupplyPcbDetected, false), (InputIo.PcbPlacementVacuumDetected, false),
                (InputIo.ShootingTubeBoltDetected, false), (InputIo.PcbPlacementHeatSink1Present, false));
            Assert.Equal(StartCheckState.UnfinishedCarrier, machine.StartChecks[StartArea.Station1]);
            machine.CheckStartMaterials();
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.Same(placementJob, placement.CurrentJob);
            Assert.Same(fasteningJob, fastening.CurrentJob);
            Assert.True(fastening.Completed);

            // A clear review is not permission to ignore a change before final START.
            io.SetInput(InputIo.ShootingTubeBoltDetected, true);
            await machine.StartAsync();
            Assert.False(state.AutomaticRunning);
            Assert.Equal(StartBlockReason.MaterialRemaining, machine.StartBlock);
            Assert.Equal(StartCheckState.MaterialRemaining, machine.StartChecks[StartArea.ShootingHead]);
            Assert.Same(placementJob, placement.CurrentJob);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartReviewCylinderTogglesSelectedStationFromLiveOutput(bool stopper)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgConveyor);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var review = services.GetRequiredService<OperationViewModel>();
        (StartArea Area, OutputIo Output)[] cylinders = [
            (StartArea.Station1, stopper ? OutputIo.PcbPlacementStopperUp : OutputIo.PcbPlacementBackupPlateUp),
            (StartArea.Station2, stopper ? OutputIo.BoltFasteningStopperUp : OutputIo.BoltFasteningBackupPlateUp),
            (StartArea.Station3, stopper ? OutputIo.InspectionStopperUp : OutputIo.InspectionBackupPlateUp),
        ];
        await machine.InitializeAsync();
        try
        {
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            foreach (var cylinder in cylinders)
                await ((IIoService)io).SetOutputAndWaitAsync(cylinder.Output, false);
            foreach (var (area, output) in cylinders)
            {
                review.SelectStartAreaCommand.Execute(area);
                var command = review.StartOutputs.Single(row => row.Io.Signal == output).ToggleOutputCommand;
                var station = Assert.IsType<ConveyorStation>(review.StartStation);
                var job = station.CurrentJob;
                await command.ExecuteAsync(null);
                Assert.Equal(StationCylinderState.Up, stopper ? station.Stopper : station.BackupPlate);
                foreach (var other in cylinders.Where(cylinder => cylinder.Output != output))
                    Assert.False(io.GetOutput(other.Output));
                await command.ExecuteAsync(null);
                Assert.False(io.GetOutput(output));
                await ((IIoService)io).SetOutputAndWaitAsync(output, true);
                await command.ExecuteAsync(null); // Toggle must read an external change to the current DO.
                Assert.Equal(StationCylinderState.Down, stopper ? station.Stopper : station.BackupPlate);
                Assert.Same(job, station.CurrentJob);
            }

            review.SelectStartAreaCommand.Execute(StartArea.Supply);
            Assert.DoesNotContain(review.StartOutputs, row => cylinders.Any(cylinder => cylinder.Output == row.Io.Signal));
            Assert.DoesNotContain(review.StartOutputs, row => row.Io.Signal == OutputIo.PcbSupplyReadyToFront1);
            Assert.All(cylinders, cylinder => Assert.False(io.GetOutput(cylinder.Output)));
            review.SelectStartAreaCommand.Execute(StartArea.Station1);
            var blockedCommand = review.StartOutputs.Single(row => row.Io.Signal == cylinders[0].Output).ToggleOutputCommand;
            state.AutomaticRunning = true;
            await blockedCommand.ExecuteAsync(null);
            Assert.False(io.GetOutput(cylinders[0].Output));
            state.AutomaticRunning = false;
            io.SetInput(InputIo.AutoMode, false);
            await blockedCommand.ExecuteAsync(null);
            Assert.False(io.GetOutput(cylinders[0].Output));
        }
        finally
        {
            state.AutomaticRunning = false;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartReviewCylinderWaitCancelsAndReportsMissingFeedback(bool stopper)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgConveyor);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var review = services.GetRequiredService<OperationViewModel>();
        var output = stopper ? OutputIo.InspectionStopperUp : OutputIo.InspectionBackupPlateUp;
        review.SelectStartAreaCommand.Execute(StartArea.Station3);
        var command = review.StartOutputs.Single(row => row.Io.Signal == output).ToggleOutputCommand;
        await machine.InitializeAsync();
        try
        {
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            await ((IIoService)io).SetOutputAndWaitAsync(output, false);
            io.AutoResponseEnabled = false;
            review.SelectStartAreaCommand.Execute(StartArea.Station3);
            var raising = command.ExecuteAsync(null);
            await WaitForOutputAsync(io, output, true);
            Assert.False(raising.IsCompleted);
            Assert.Equal(StationCylinderState.Down, stopper ? review.StartStation!.Stopper : review.StartStation!.BackupPlate);
            review.SelectStartAreaCommand.Execute(StartArea.Station1);
            if (stopper)
                await review.StopCommand.ExecuteAsync(null);
            else
                command.Cancel();
            await raising.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(io.GetOutput(output)); // Cancellation must not reverse the cylinder.
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.Equal(MachineAlarm.None, state.Alarm);

            review.SelectStartAreaCommand.Execute(StartArea.Station3);
            settings.Options.TimeoutMilliseconds = 50;
            io.SetOutput(output, false);
            await command.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(MachineAlarm.MainConveyor, state.Alarm);
            Assert.Equal(StationCylinderState.Down, stopper ? review.StartStation!.Stopper : review.StartStation!.BackupPlate);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(StartArea.Station1)]
    [InlineData(StartArea.Station2)]
    [InlineData(StartArea.Station3)]
    public async Task StartReviewClearsOnlySelectedStationResultsAndKeepsCarrierIdentity(StartArea area)
    {
        var settings = FlowSettings();
        settings.PcbHistory.Directory = Path.Combine(Path.GetTempPath(), $"PCB-clear-{Guid.NewGuid():N}");
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>().Station;
        var fastening = services.GetRequiredService<BoltFasteningStation>().Station;
        var inspection = services.GetRequiredService<InspectionStation>().Station;
        var station = area switch
        {
            StartArea.Station1 => placement,
            StartArea.Station2 => fastening,
            _ => inspection,
        };
        var history = services.GetRequiredService<PcbHistoryWriter>();
        var store = services.GetRequiredService<MachineStore>();
        await machine.InitializeAsync();
        try
        {
            io.SetInputs((InputIo.PcbPlacementHeatSink1Present, true), (InputIo.PcbPlacementHeatSink2Present, true),
                (InputIo.BoltFasteningHeatSink1Present, true), (InputIo.BoltFasteningHeatSink2Present, true),
                (InputIo.InspectionHeatSink1Present, true), (InputIo.InspectionHeatSink2Present, true),
                (InputIo.PickupHeadVacuumDetected, true));
            var job = station.CurrentJob;
            var assemblies = Enum.GetValues<HeatSinkSlot>().Select(station.GetAssembly).ToArray();
            foreach (var assembly in assemblies)
            {
                assembly.IsPlacementCompleted = true;
                assembly.PcbBarcode = $"PCB-{assembly.HeatSink}";
                assembly.RecordBolt(FasteningHead.Shooting, BoltId(1), new(true, 8));
                assembly.RecordBolt(FasteningHead.Pickup, BoltId(2), new(false, 2) { MinimumTurns = 3 });
                assembly.CompleteFastening();
                assembly.RecordBoltPresence(BoltId(1), false);
                assembly.CompleteInspection();
                assembly.RecordInspectionCapture(new(null, DateTimeOffset.Now,
                    new ImageFrame(1, 1, 3, [1, 2, 3]), new PixelRegion(0, 0, 1, 1), true, assembly.PcbBarcode));
            }
            await history.FlushAsync();
            var numbers = assemblies.Select(assembly => assembly.PcbNumber).ToArray();
            var writes = 0;
            io.OutputChanged += (output, on) => writes++;

            machine.ChangeCarrierWork(area, job, CarrierWorkAction.Complete);

            Assert.True(station.Completed);
            Assert.All(assemblies, assembly => Assert.Equal(AssemblyResult.Ng, assembly.Result));
            Assert.Equal(StartCheckState.Completed, machine.StartChecks[area]);
            Assert.Equal(StartCheckState.MaterialRemaining, machine.StartChecks[StartArea.PickupHead]);

            machine.ChangeCarrierWork(area, job, CarrierWorkAction.Clear);
            await history.FlushAsync();

            Assert.Same(job, station.CurrentJob);
            Assert.True(station.CarrierPresent);
            Assert.False(station.Completed);
            Assert.True(station.IsRestartAllowed);
            Assert.Equal(StartCheckState.ReworkReady, machine.StartChecks[area]);
            foreach (var other in new[] { placement, fastening, inspection }.Where(other => other != station))
            {
                Assert.False(other.Completed);
                Assert.False(other.IsRestartAllowed);
                Assert.Empty(other.Assemblies);
            }
            Assert.Equal(numbers, assemblies.Select(assembly => assembly.PcbNumber));
            var records = store.LoadPcbs(settings.PcbHistory.Directory);
            Assert.Equal(2, records.Count);
            foreach (var assembly in assemblies)
            {
                Assert.Same(assembly, station.GetAssembly(assembly.HeatSink));
                Assert.Equal(area != StartArea.Station1, assembly.IsPlacementCompleted);
                var record = records.Single(record => record.Number == assembly.PcbNumber);
                if (area == StartArea.Station2)
                {
                    Assert.Empty(record.ShootingBoltResults);
                    Assert.Empty(record.PickupBoltResults);
                    Assert.Null(record.TurnsResult);
                    Assert.Equal(AssemblyResult.Pending, record.FasteningResult);
                }
                else
                {
                    Assert.Single(record.ShootingBoltResults);
                    Assert.Single(record.PickupBoltResults);
                    Assert.Equal(AssemblyResult.Pending, record.TurnsResult);
                    Assert.Equal(AssemblyResult.Ng, record.FasteningResult);
                }
                if (area == StartArea.Station3)
                {
                    Assert.Null(record.PcbBarcode);
                    Assert.Equal(AssemblyResult.Pending, record.PcbBarcodeResult);
                    Assert.Empty(record.BoltPresenceResults);
                    Assert.Equal(AssemblyResult.Pending, record.InspectionResult);
                    Assert.Empty(store.LoadPcbImages(record));
                }
                else
                {
                    Assert.Equal($"PCB-{assembly.HeatSink}", record.PcbBarcode);
                    Assert.Single(record.BoltPresenceResults);
                    Assert.Equal(AssemblyResult.Ng, record.InspectionResult);
                    Assert.Single(store.LoadPcbImages(record));
                }
            }
            Assert.Equal(0, writes);
            io.SetInputs((InputIo.PcbPlacementHeatSink1Present, false), (InputIo.PcbPlacementHeatSink2Present, false),
                (InputIo.BoltFasteningHeatSink1Present, false), (InputIo.BoltFasteningHeatSink2Present, false),
                (InputIo.InspectionHeatSink1Present, false), (InputIo.InspectionHeatSink2Present, false));
            Assert.Throws<InvalidOperationException>(() => machine.ChangeCarrierWork(area, job, CarrierWorkAction.Clear));
            station.ClearJob();
            Assert.Throws<InvalidOperationException>(() => machine.ChangeCarrierWork(area, job, CarrierWorkAction.Clear));
            using var running = services.GetRequiredService<OperationCancellation>().TryBegin();
            Assert.NotNull(running);
            Assert.Throws<InvalidOperationException>(() => machine.ChangeCarrierWork(StartArea.Station1, placement.CurrentJob, CarrierWorkAction.Complete));
            Assert.False(placement.Completed);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ConfirmedPlacementResumeKeepsFirstPcbAndStartsAtSecondHeatSink()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        var review = services.GetRequiredService<OperationViewModel>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            await WaitUntilAsync(() => machine.IsStartAllowed);
            SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            await placement.Station.SeatAsync(CancellationToken.None);
            var job = placement.Station.CurrentJob;
            var assembly = placement.Station.GetAssembly(HeatSinkSlot.HeatSink1);
            assembly.IsPlacementCompleted = true;
            await review.CheckStartCommand.ExecuteAsync(null);
            Assert.Equal(StartArea.Station1, review.SelectedStartArea);
            Assert.Equal(new[] { new PlacementResumeRow(HeatSinkSlot.HeatSink1, true),
                new PlacementResumeRow(HeatSinkSlot.HeatSink2, false) }, review.PlacementResumeTargets);
            Assert.True(review.IsPlacementResumeAvailable);
            Assert.False(review.IsStartReviewAllowed);
            await review.ConfirmStartCommand.ExecuteAsync(null);
            await machine.StartAsync();
            Assert.False(state.AutomaticRunning);
            Assert.False(placement.IsRunning);

            HeatSinkSlot? selected = null;
            placement.StepChanged += () =>
            {
                if (placement.Step is PcbPlacementState.MovingToHandoff)
                {
                    selected = placement.ActivePcb;
                    machine.Stop();
                }
            };
            review.IsPlacementResumeConfirmed = true;
            Assert.True(review.IsStartReviewAllowed);
            await review.ConfirmStartCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(HeatSinkSlot.HeatSink2, selected);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.Same(job, placement.Station.CurrentJob);
            Assert.Same(assembly, Assert.Single(placement.Station.Assemblies));
            Assert.True(assembly.IsPlacementCompleted);
            Assert.False(placement.Station.Completed);
            Assert.False(review.IsPlacementResumeConfirmed);
            await review.CheckStartCommand.ExecuteAsync(null);
            Assert.False(review.IsStartReviewAllowed); // Each START needs a new confirmation.
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StartPreparationSupplyReleaseWaitsForFixerBeforeOpeningGripper()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var review = services.GetRequiredService<OperationViewModel>();
        await machine.InitializeAsync();
        try
        {
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
            io.AutoResponseEnabled = false;
            var commands = new ConcurrentQueue<OutputIo>();
            io.OutputChanged += (output, on) => commands.Enqueue(output);
            review.SelectStartAreaCommand.Execute(StartArea.Supply);
            review.IsPlacementResumeConfirmed = true;
            review.IsFasteningResumeConfirmed = true;
            var releasing = review.PrepareStartAreaCommand.ExecuteAsync(StartPreparationAction.ReleaseMaterial);
            await WaitForOutputAsync(io, OutputIo.PcbSupplyIpmFixerForward, false);
            Assert.False(releasing.IsCompleted);
            Assert.True(io.GetOutput(OutputIo.PcbSupplyGripperClosed));
            Assert.False(review.IsPlacementResumeConfirmed);
            Assert.False(review.IsFasteningResumeConfirmed);
            review.SelectStartAreaCommand.Execute(StartArea.Station2); // Keep the area captured at button press.
            io.SetInputs((InputIo.PcbSupplyIpmFixerForward, false), (InputIo.PcbSupplyIpmFixerBackward, true));
            await WaitForOutputAsync(io, OutputIo.PcbSupplyGripperClosed, false);
            Assert.False(releasing.IsCompleted);
            io.SetInputs((InputIo.PcbSupplyGripperClosed, false), (InputIo.PcbSupplyGripperOpen, true));
            await releasing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(new[] { OutputIo.PcbSupplyIpmFixerForward, OutputIo.PcbSupplyGripperClosed }, commands.ToArray());
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.Null(review.StartActionMessage);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartPreparationStopsBeforeNextOutputOnCancelOrMissingFeedback(bool timeout)
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var review = services.GetRequiredService<OperationViewModel>();
        await machine.InitializeAsync();
        try
        {
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
            io.AutoResponseEnabled = false;
            if (timeout)
                settings.Options.TimeoutMilliseconds = 100;
            review.SelectStartAreaCommand.Execute(StartArea.Supply);
            var releasing = review.PrepareStartAreaCommand.ExecuteAsync(StartPreparationAction.ReleaseMaterial);
            await WaitForOutputAsync(io, OutputIo.PcbSupplyIpmFixerForward, false);
            if (!timeout)
                await review.StopCommand.ExecuteAsync(null);
            await releasing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(io.GetOutput(OutputIo.PcbSupplyGripperClosed));
            Assert.False(io.GetOutput(OutputIo.PcbSupplyIpmFixerForward)); // Cancellation never reverses the cylinder.
            Assert.Equal(timeout ? MachineAlarm.PcbSupply : MachineAlarm.None, state.Alarm);
            Assert.False(string.IsNullOrWhiteSpace(review.StartActionMessage));
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StartPreparationMovesCarrierSupportAndStopperIndependentlyAfterRaisingTools()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        try
        {
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
            var job = placement.Station.CurrentJob;
            var assembly = placement.Station.GetAssembly(HeatSinkSlot.HeatSink1);
            assembly.IsPlacementCompleted = true;
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerDown, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementBackupPlateUp, false);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementStopperUp, false);
            var writes = new ConcurrentQueue<(OutputIo Output, bool On)>();
            io.OutputChanged += (output, on) =>
            {
                if (output == OutputIo.PcbPlacementBackupPlateUp)
                {
                    Assert.Equal(StationCylinderState.Up, placement.Lift);
                    Assert.Equal(StationCylinderState.Up, placement.IpmLift);
                }
                writes.Enqueue((output, on));
            };
            var motion = placement.Motion.Position;
            Assert.True(await machine.PrepareStartAreaAsync(StartArea.Station1, StartPreparationAction.ToggleCarrierSupport, CancellationToken.None));
            Assert.True(placement.Station.CarrierSeated);
            Assert.Equal(StationCylinderState.Down, placement.Station.Stopper);
            Assert.Equal(new[] {
                (OutputIo.PcbPlacementHandlerDown, false), (OutputIo.PcbPlacementIpmDown, false),
                (OutputIo.PcbPlacementBackupPlateUp, true),
            }, writes.ToArray());
            writes.Clear();
            Assert.True(await machine.PrepareStartAreaAsync(StartArea.Station1, StartPreparationAction.ToggleStopper, CancellationToken.None));
            Assert.True(placement.Station.CarrierSeated);
            Assert.Equal(StationCylinderState.Up, placement.Station.Stopper);
            Assert.Equal(new[] { (OutputIo.PcbPlacementStopperUp, true) }, writes.ToArray());
            writes.Clear();
            Assert.True(await machine.PrepareStartAreaAsync(StartArea.Station1, StartPreparationAction.ToggleCarrierSupport, CancellationToken.None));
            Assert.Equal(StationCylinderState.Down, placement.Station.BackupPlate);
            Assert.Equal(StationCylinderState.Up, placement.Station.Stopper);
            Assert.Equal(new[] { (OutputIo.PcbPlacementBackupPlateUp, false) }, writes.ToArray());
            writes.Clear();
            io.AutoResponseEnabled = false;
            var loweringStopper = machine.PrepareStartAreaAsync(StartArea.Station1, StartPreparationAction.ToggleStopper, CancellationToken.None);
            await WaitForOutputAsync(io, OutputIo.PcbPlacementStopperUp, false);
            Assert.False(loweringStopper.IsCompleted);
            Assert.Equal(StationCylinderState.Down, placement.Station.BackupPlate);
            Assert.Equal(new[] { (OutputIo.PcbPlacementStopperUp, false) }, writes.ToArray());
            io.SetInputs((InputIo.PcbPlacementStopperUp, false), (InputIo.PcbPlacementStopperDown, true));
            Assert.True(await loweringStopper.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(StationCylinderState.Down, placement.Station.BackupPlate);
            Assert.Equal(StationCylinderState.Down, placement.Station.Stopper);
            Assert.Same(job, placement.Station.CurrentJob);
            Assert.Same(assembly, Assert.Single(placement.Station.Assemblies));
            Assert.True(assembly.IsPlacementCompleted);
            Assert.False(placement.Station.Completed);
            Assert.Equal(motion, placement.Motion.Position);
            Assert.False(state.IsError, state.AlarmDetail);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StartPreparationFasteningReleasesBothHeadsAndRaisesTableLast()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupHeadDown, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingHeadDown, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupTableDown, true);
            io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
            io.SetOutput(OutputIo.ShootingHeadVacuumPump, true);
            var writes = new ConcurrentQueue<(OutputIo Output, bool On)>();
            io.OutputChanged += (output, on) => writes.Enqueue((output, on));
            Assert.True(await machine.PrepareStartAreaAsync(StartArea.Station2, StartPreparationAction.ReleaseMaterial, CancellationToken.None));
            Assert.False(io.GetInput(InputIo.PickupHeadVacuumDetected));
            Assert.False(io.GetInput(InputIo.ShootingHeadVacuumDetected));
            Assert.False(io.GetOutput(OutputIo.ShootBolt));
            Assert.True(io.GetOutput(OutputIo.PickupHeadDown)); // Grip release does not raise the tooling.
            Assert.True(io.GetOutput(OutputIo.ShootingHeadDown));
            Assert.DoesNotContain(writes, write => write.On);
            writes.Clear();
            Assert.True(await machine.PrepareStartAreaAsync(StartArea.Station2, StartPreparationAction.RaiseTooling, CancellationToken.None));
            Assert.Equal(new[] { (OutputIo.PickupHeadDown, false), (OutputIo.ShootingHeadDown, false),
                (OutputIo.PickupTableDown, false) }, writes.ToArray());
            Assert.True(io.GetInput(InputIo.PickupTableUp));
            Assert.False(state.IsError, state.AlarmDetail);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(StartPreparationAction.ToggleCarrierSupport, false)]
    [InlineData(StartPreparationAction.ToggleStopper, true)]
    public async Task StartPreparationCylinderToggleUsesLiveFeedbackAndRejectsUnknownPosition(
        StartPreparationAction action, bool conflictingSensors)
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var (output, up, down) = action == StartPreparationAction.ToggleCarrierSupport
            ? (OutputIo.PcbPlacementBackupPlateUp, InputIo.PcbPlacementBackupPlateUp, InputIo.PcbPlacementBackupPlateDown)
            : (OutputIo.PcbPlacementStopperUp, InputIo.PcbPlacementStopperUp, InputIo.PcbPlacementStopperDown);
        await machine.InitializeAsync();
        try
        {
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            io.AutoResponseEnabled = false;
            io.SetOutput(output, false);
            io.SetInputs((up, conflictingSensors), (down, conflictingSensors));
            var writes = new ConcurrentQueue<OutputIo>();
            io.OutputChanged += (signal, value) => writes.Enqueue(signal);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => machine.PrepareStartAreaAsync(
                StartArea.Station1, action, CancellationToken.None));
            Assert.Contains("Up/Down sensors", error.Message);
            Assert.Empty(writes);

            // The physical cylinder is UP even though its previous UP command is OFF.
            io.SetInputs((up, true), (down, false));
            var lowering = machine.PrepareStartAreaAsync(StartArea.Station1, action, CancellationToken.None);
            Assert.False(lowering.IsCompleted);
            Assert.False(io.GetOutput(output));
            io.SetInputs((up, false), (down, true));
            Assert.True(await lowering.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Empty(writes);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StartPreparationRejectsAutomaticModeAndCancelsWhenManualModeIsLost()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
            io.AutoResponseEnabled = false;
            io.SetInput(InputIo.AutoMode, false);
            Assert.False(await machine.PrepareStartAreaAsync(StartArea.Supply, StartPreparationAction.ReleaseMaterial, CancellationToken.None));
            Assert.True(io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
            io.SetInput(InputIo.AutoMode, true);
            var releasing = machine.PrepareStartAreaAsync(StartArea.Supply, StartPreparationAction.ReleaseMaterial, CancellationToken.None);
            await WaitForOutputAsync(io, OutputIo.PcbSupplyIpmFixerForward, false);
            io.SetInput(InputIo.AutoMode, false);
            Assert.False(await releasing.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(io.GetOutput(OutputIo.PcbSupplyGripperClosed));
            Assert.False(state.IsError, state.AlarmDetail);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task PlacementResumeCancelsIfTargetsChangeDuringStartupPreparation()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<PcbPlacer>().Station;
        var review = services.GetRequiredService<OperationViewModel>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            await WaitUntilAsync(() => machine.IsStartAllowed);
            SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            await station.SeatAsync(CancellationToken.None);
            var assembly = station.GetAssembly(HeatSinkSlot.HeatSink1);
            assembly.IsPlacementCompleted = true;
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerDown, true);
            await review.CheckStartCommand.ExecuteAsync(null);
            review.IsPlacementResumeConfirmed = true;
            Assert.True(review.IsStartReviewAllowed);
            var changed = false;
            io.OutputChanged += (output, on) =>
            {
                if (!changed && output == OutputIo.PcbPlacementHandlerDown && !on)
                {
                    changed = true;
                    io.SetInput(InputIo.PcbPlacementHeatSink2Present, false);
                    io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
                }
            };
            var started = false;
            state.PropertyChanged += (sender, args) =>
            {
                if (state.AutomaticRunning)
                {
                    started = true;
                    machine.Stop();
                }
            };
            await review.ConfirmStartCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(changed);
            Assert.False(started);
            Assert.False(review.IsPlacementResumeConfirmed);
            Assert.True(assembly.IsPlacementCompleted);
            Assert.False(station.Completed);
            Assert.False(state.IsError, state.AlarmDetail);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StartPreparationRetainsIpmSupportAndCarrierSupportWhenPlacementStillDetectsPcb()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        try
        {
            await WaitUntilAsync(() => state.ManualSetupEnabled);
            io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
            await placement.Station.SeatAsync(CancellationToken.None);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerDown, true);
            io.SetInput(InputIo.PcbPlacementPcbDetected, true);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => machine.PrepareStartAreaAsync(
                StartArea.Station1, StartPreparationAction.ToggleCarrierSupport, CancellationToken.None));
            Assert.Contains("IPM support is retained", error.Message);
            Assert.Equal(StationCylinderState.Up, placement.Lift);
            Assert.Equal(StationCylinderState.Down, placement.IpmLift);
            Assert.True(placement.Station.CarrierSeated);
            Assert.False(state.IsError);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);

            io.SetInput(InputIo.PcbPlacementPcbDetected, false);
            Assert.True(await machine.PrepareStartAreaAsync(StartArea.Station1, StartPreparationAction.ToggleCarrierSupport, CancellationToken.None));
            Assert.Equal(StationCylinderState.Up, placement.IpmLift);
            Assert.Equal(StationCylinderState.Down, placement.Station.BackupPlate);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task PlacementResumeConfirmationRejectsChangedCarrierAndHeldPcb()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<PcbPlacer>().Station;
        var review = services.GetRequiredService<OperationViewModel>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            await WaitUntilAsync(() => machine.IsStartAllowed);
            SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
            await station.SeatAsync(CancellationToken.None);
            var job = station.CurrentJob;
            await review.CheckStartCommand.ExecuteAsync(null);
            Assert.False(review.IsPlacementResumeAvailable); // No confirmed placement to skip.
            station.GetAssembly(HeatSinkSlot.HeatSink1).IsPlacementCompleted = true;
            await review.CheckStartCommand.ExecuteAsync(null);
            review.IsPlacementResumeConfirmed = true;
            await review.CheckStartCommand.ExecuteAsync(null);
            Assert.False(review.IsPlacementResumeConfirmed);

            var writes = 0;
            io.OutputChanged += (output, on) => writes++;
            review.IsPlacementResumeConfirmed = true;
            io.SetInputs((InputIo.PcbPlacementBackupPlateUp, false), (InputIo.PcbPlacementBackupPlateDown, true));
            Assert.False(review.IsPlacementResumeConfirmed);
            Assert.False(review.IsPlacementResumeAvailable);
            await machine.StartAsync(resumePlacement: job);
            Assert.Equal(0, writes);
            io.SetInputs((InputIo.PcbPlacementBackupPlateUp, true), (InputIo.PcbPlacementBackupPlateDown, false));
            Assert.True(review.IsPlacementResumeAvailable);
            Assert.False(review.IsPlacementResumeConfirmed);

            review.IsPlacementResumeConfirmed = true;
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, false);
            Assert.False(review.IsPlacementResumeAvailable);
            Assert.False(review.IsPlacementResumeConfirmed);
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);

            review.IsPlacementResumeConfirmed = true;
            io.SetInput(InputIo.PcbPlacementPcbDetected, true);
            await review.ConfirmStartCommand.ExecuteAsync(null);
            Assert.False(state.AutomaticRunning);
            Assert.False(review.IsPlacementResumeConfirmed);
            Assert.Equal(StartBlockReason.MaterialRemaining, machine.StartBlock);
            Assert.Equal(0, writes);
            io.SetInput(InputIo.PcbPlacementPcbDetected, false);

            SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, false);
            io.SetInput(InputIo.PcbPlacementHeatSink2Present, false);
            station.ClearJob();
            SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
            station.GetAssembly(HeatSinkSlot.HeatSink1).IsPlacementCompleted = true;
            Assert.False(review.IsPlacementResumeAvailable);
            await machine.StartAsync(resumePlacement: job);
            Assert.False(state.AutomaticRunning);
            Assert.Equal(0, writes);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StartReviewClearAllowsOneStartAndDoesNotBypassHeldMaterial()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>().Station;
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            await WaitUntilAsync(() => machine.IsStartAllowed);
            io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
            var job = station.CurrentJob;
            machine.CheckStartMaterials();
            Assert.Equal(StartBlockReason.UnfinishedCarrier, machine.StartBlock);

            machine.ChangeCarrierWork(StartArea.Station2, job, CarrierWorkAction.Clear);
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            io.SetInput(InputIo.PickupHeadVacuumDetected, true);
            await machine.StartAsync();
            Assert.False(state.AutomaticRunning);
            Assert.Equal(StartBlockReason.MaterialRemaining, machine.StartBlock);
            Assert.True(station.IsRestartAllowed);

            io.SetInput(InputIo.PickupHeadVacuumDetected, false);
            using var stop = new CancellationTokenSource();
            state.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(MachineState.AutomaticRunning) && state.AutomaticRunning)
                    stop.Cancel();
            };
            await machine.StartAsync(stop.Token);
            Assert.True(stop.IsCancellationRequested);
            Assert.False(station.IsRestartAllowed);
            Assert.Same(job, station.CurrentJob);

            // A second stop needs a new operator decision; clear is not a permanent bypass.
            await machine.StartAsync();
            Assert.False(state.AutomaticRunning);
            Assert.Equal(StartBlockReason.UnfinishedCarrier, machine.StartBlock);
            machine.ChangeCarrierWork(StartArea.Station2, job, CarrierWorkAction.Clear);
            Assert.True(station.IsRestartAllowed);
            machine.ChangeCarrierWork(StartArea.Station2, job, CarrierWorkAction.Complete);
            Assert.False(station.IsRestartAllowed);
            Assert.True(station.Completed);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task StartChecksHeldMaterialsOnlyWhenPressed()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(CancellationToken.None);
            await WaitUntilAsync(() => machine.IsStartAllowed);
            // Completed carriers, NG shuttle/conveyor loads and feeder stock may remain.
            io.SetInputs(
                (InputIo.PcbSupplyGripperClosed, true),
                (InputIo.PcbSupplyGripperOpen, false),
                (InputIo.PcbPlacementHeatSink1Present, true),
                (InputIo.BoltFasteningHeatSink1Present, true),
                (InputIo.InspectionHeatSink1Present, true),
                (InputIo.NgShuttleCarrierDetected, true),
                (InputIo.NgConveyorPosition1Occupied, true),
                (InputIo.PickupFeederBoltDetected, true),
                (InputIo.ShootingFeederBoltDetected, true));
            ConveyorStation[] stations = [services.GetRequiredService<PcbPlacer>().Station,
                services.GetRequiredService<BoltFasteningStation>().Station,
                services.GetRequiredService<InspectionStation>().Station];
            foreach (var station in stations)
                station.Complete(station.CurrentJob);
            var jobs = stations.Select(station => station.CurrentJob).ToArray();
            Assert.True(machine.IsStartAllowed, machine.StartBlock.ToString());
            var writes = 0;
            void CountWrite(OutputIo output, bool value)
            {
                writes++;
            }
            io.OutputChanged += CountWrite;
            foreach (var input in new[]
            {
                InputIo.PcbSupplyPcbDetected, InputIo.PcbPlacementPcbDetected,
                InputIo.PcbPlacementVacuumDetected, InputIo.PickupHeadVacuumDetected,
                InputIo.ShootingHeadVacuumDetected, InputIo.ShootingTubeBoltDetected,
            })
            {
                var previousBlock = machine.StartBlock;
                io.SetInput(input, true);
                Assert.Equal(previousBlock, machine.StartBlock);
                Assert.True(machine.IsStartAllowed); // The operator can request the check.
                await machine.StartAsync();
                Assert.Equal(StartBlockReason.MaterialRemaining, machine.StartBlock);
                Assert.False(state.AutomaticRunning);
                Assert.Equal(0, writes);
                io.SetInput(input, false);
                Assert.Equal(StartBlockReason.MaterialRemaining, machine.StartBlock);
                // Removing and replacing material before START does not acknowledge it.
                io.SetInput(input, true);
                await machine.StartAsync();
                Assert.Equal(StartBlockReason.MaterialRemaining, machine.StartBlock);
                Assert.Equal(0, writes);
                io.SetInput(input, false);
            }
            io.OutputChanged -= CountWrite;
            state.SetError(MachineAlarm.PcbPlacement);
            await machine.ResetAsync();
            Assert.False(state.IsError);
            Assert.Equal(StartBlockReason.MaterialRemaining, machine.StartBlock);

            using var stop = new CancellationTokenSource();
            state.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(MachineState.AutomaticRunning) && state.AutomaticRunning)
                    stop.Cancel();
            };
            await machine.StartAsync(stop.Token);
            Assert.True(stop.IsCancellationRequested); // Admission succeeded before any unit work.
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            for (var i = 0; i < stations.Length; i++)
            {
                Assert.Same(jobs[i], stations[i].CurrentJob);
                Assert.True(stations[i].Completed);
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(InputIo.PcbPlacementHeatSink1Present)]
    [InlineData(InputIo.BoltFasteningHeatSink1Present)]
    [InlineData(InputIo.InspectionHeatSink1Present)]
    public async Task StartRejectsUnfinishedCarrierEvenAfterRemovalAndReplacement(InputIo carrier)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(carrier switch
        {
            InputIo.PcbPlacementHeatSink1Present => MachineUnit.PcbPlacement,
            InputIo.BoltFasteningHeatSink1Present => MachineUnit.BoltFastening,
            _ => MachineUnit.Inspection,
        });
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = carrier switch
        {
            InputIo.PcbPlacementHeatSink1Present => services.GetRequiredService<PcbPlacer>().Station,
            InputIo.BoltFasteningHeatSink1Present => services.GetRequiredService<BoltFasteningStation>().Station,
            _ => services.GetRequiredService<InspectionStation>().Station,
        };
        await machine.InitializeAsync();
        try
        {
            await machine.HomeAsync(default);
            await WaitUntilAsync(() => machine.IsStartAllowed);
            io.SetInput(carrier, true);
            var job = station.CurrentJob;
            var assembly = station.GetAssembly(HeatSinkSlot.HeatSink1);
            var writes = 0;
            void CountWrite(OutputIo output, bool value)
            {
                writes++;
            }
            io.OutputChanged += CountWrite;
            io.SetInput(carrier, false);
            io.SetInput(carrier, true);
            Assert.Same(job, station.CurrentJob);
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            await machine.StartAsync();
            Assert.Equal(StartBlockReason.UnfinishedCarrier, machine.StartBlock);
            Assert.False(state.AutomaticRunning);
            Assert.Equal(0, writes);

            io.SetInput(carrier, false);
            Assert.Same(job, station.CurrentJob);
            Assert.Same(assembly, Assert.Single(station.Assemblies));
            Assert.Equal(StartBlockReason.UnfinishedCarrier, machine.StartBlock);
            io.SetInput(carrier, true);
            await machine.StartAsync();
            Assert.Equal(StartBlockReason.UnfinishedCarrier, machine.StartBlock);
            Assert.Equal(0, writes);
            Assert.Same(job, station.CurrentJob);
            io.OutputChanged -= CountWrite;

            io.SetInput(carrier, false);
            using var stop = new CancellationTokenSource();
            state.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(MachineState.AutomaticRunning) && state.AutomaticRunning)
                    stop.Cancel();
            };
            await machine.StartAsync(stop.Token);
            Assert.True(stop.IsCancellationRequested);
            Assert.Equal(StartBlockReason.None, machine.StartBlock);
            Assert.NotSame(job, station.CurrentJob);
            Assert.Empty(station.Assemblies);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task EmergencyInputStopsConveyorBeforeReadingUnrelatedMotionFeedback()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = true;
        await using var services = CreateDisplayServices(out var feedback, settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        var run = machine.StartAsync();
        var checkingTrip = new AsyncLocal<bool>();
        var readsBeforeStop = 0;
        try
        {
            await WaitUntilAsync(() => state.AutomaticRunning || run.IsCompleted);
            Assert.True(state.AutomaticRunning, $"{machine.StartBlock}: {state.AlarmDetail}");
            io.SetOutput(OutputIo.MainConveyorRun, true);
            feedback.BeforeRead = () =>
            {
                if (checkingTrip.Value && io.GetOutput(OutputIo.MainConveyorRun))
                    Interlocked.Increment(ref readsBeforeStop);
            };
            checkingTrip.Value = true;
            io.SetInput(InputIo.EmergencyStop1Pressed, true);
            checkingTrip.Value = false;
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, readsBeforeStop);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
        }
        finally
        {
            checkingTrip.Value = false;
            feedback.BeforeRead = null;
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ConcurrentManualAdmissionOnlyStartsOneDeviceCommand()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var settings = services.GetRequiredService<InspectionGantrySettings>();
        await feedback.Motion.MoveToXYAsync(10, 0, 10_000);
        settings.Motion.HorizontalHome.SearchSpeed = 10_000;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var pauseAdmission = new AsyncLocal<bool>();
        var starts = 0;
        feedback.BeforeRead = () =>
        {
            if (pauseAdmission.Value)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
        };
        void CountStart(bool moving)
        {
            if (moving)
                Interlocked.Increment(ref starts);
        }
        feedback.Motion.MovingChanged += CountStart;

        var first = Task.Run(() =>
        {
            pauseAdmission.Value = true;
            return machine.HomeAsync(MotionGroup.InspectionGantry, default, MotionAxis.X);
        });
        Task second = Task.CompletedTask;
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))));
            second = machine.HomeAsync(MotionGroup.InspectionGantry, default, MotionAxis.X);
            await second.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(0, Volatile.Read(ref starts));
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, Volatile.Read(ref starts));
        }
        finally
        {
            release.Set();
            feedback.BeforeRead = null;
            feedback.Motion.MovingChanged -= CountStart;
            machine.Stop();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ManualInspectionGantryIoFailureUsesInspectionUnitAlarm()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        try
        {
            Assert.True(machine.IsManualMotionReady(MotionGroup.InspectionGantry));
            machine.ReportManualFailure(
                machine.GetMotionAlarm(MotionGroup.InspectionGantry),
                new IOException("Manual gantry I/O failure."));
            Assert.Equal(MachineAlarm.Inspection, state.Alarm);
            Assert.Equal("Manual gantry I/O failure.", state.AlarmMessage);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(ManualCommandFailure.Motion)]
    [InlineData(ManualCommandFailure.Canceled)]
    [InlineData(ManualCommandFailure.Safety)]
    [InlineData(ManualCommandFailure.Programming)]
    public async Task ManualCommandHandlesCombinedDeviceFailuresWithoutHidingProgrammingErrors(ManualCommandFailure failureKind)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        using var cancellation = new CancellationTokenSource();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        Exception operationFailure = failureKind switch
        {
            ManualCommandFailure.Motion or ManualCommandFailure.Safety => new MotionException("Manual move", new IOException("Motion feedback failed.")),
            ManualCommandFailure.Canceled => new OperationCanceledException(cancellation.Token),
            _ => new InvalidOperationException("Invalid command state."),
        };
        Exception cleanupFailure = failureKind == ManualCommandFailure.Programming
            ? new InvalidOperationException("Invalid cleanup state.")
            : new IOException("Cleanup output failed.");
        var failure = new AggregateException(operationFailure, new AggregateException(cleanupFailure));
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        try
        {
            Assert.True(machine.IsManualMotionReady(MotionGroup.InspectionGantry));
            if (failureKind == ManualCommandFailure.Programming)
            {
                Assert.False(MachineController.IsDeviceFailure(failure));
                Assert.Equal(MachineAlarm.None, state.Alarm);
            }
            else
            {
                Assert.True(MachineController.IsDeviceFailure(failure));
                io.SetOutput(OutputIo.MainConveyorReadyToFront2, true);
                if (failureKind == ManualCommandFailure.Canceled)
                    cancellation.Cancel();
                if (failureKind == ManualCommandFailure.Safety)
                    io.SetInput(InputIo.EmergencyStop1Pressed, true);
                machine.ReportManualFailure(machine.GetMotionAlarm(MotionGroup.InspectionGantry), failure);
                Assert.Equal(
                    failureKind switch
                    {
                        ManualCommandFailure.Safety => MachineAlarm.EmergencyStop,
                        ManualCommandFailure.Motion => MachineAlarm.MotionUnavailable,
                        _ => MachineAlarm.Inspection,
                    },
                    state.Alarm);
                Assert.Contains(operationFailure.ToString(), state.AlarmDetail);
                Assert.Contains(cleanupFailure.ToString(), state.AlarmDetail);
                Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
            }
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, FasteningHead.Shooting)]
    [InlineData(true, FasteningHead.Shooting)]
    [InlineData(false, FasteningHead.Pickup)]
    [InlineData(true, FasteningHead.Pickup)]
    public async Task ConfirmedFasteningResumeRetainsResultsAndRunsNextCarrierNormally(
        bool stopAfterLastResult, FasteningHead firstHead)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.BoltFastening.FirstFasteningHead = firstHead;
        settings.BoltFastening.PickupPosition = new() { X = 30, Y = 40, Z = 10 };
        await using var services = CreateServices(settings);
        var recipes = services.GetRequiredService<RecipeManager>();
        var recorded = new BoltPoint { Head = FasteningHead.Pickup, X = 5, Y = 5 };
        var first = new BoltPoint { X = 10, Y = 10 };
        var last = new BoltPoint { X = 20, Y = 20 };
        var absent = new BoltPoint { HeatSink = HeatSinkSlot.HeatSink2, X = 30, Y = 30 };
        recipes.Current.Pcb.BoltPoints = [recorded, last, first, absent];
        recipes.Current.Pcb.FasteningOrder = [first.Id, last.Id, recorded.Id, absent.Id];
        foreach (var bolt in recipes.Current.Pcb.BoltPoints)
            settings.BoltFastening.InitializeBoltPosition(bolt, settings.CarrierReference);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var review = services.GetRequiredService<OperationViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        await station.Station.SeatAsync(CancellationToken.None);
        var job = station.Station.CurrentJob;
        var assembly = station.Station.GetAssembly(HeatSinkSlot.HeatSink1);
        var priorResult = new BoltResult(false, 8);
        assembly.RecordBolt(recorded.Head, recorded.Id, priorResult);
        var started = new ConcurrentQueue<Guid>();
        var stopDuringReturn = stopAfterLastResult && firstHead == FasteningHead.Pickup;
        var returnInterrupted = false;
        void OnOutput(OutputIo output, bool on)
        {
            if (output is OutputIo.ShootingBoltStart or OutputIo.PickupBoltStart && on)
                started.Enqueue(station.ActiveBolt!.Id);
        }
        void StopAfterResult(HeatSinkAssembly updated)
        {
            if (!stopDuringReturn
                && updated.ShootingBoltResults.ContainsKey(stopAfterLastResult ? last.Id : first.Id))
                machine.Stop();
        }
        void StopDuringReturn(double x, double y, double z)
        {
            if (stopDuringReturn && !returnInterrupted
                && station.Step is BoltFasteningState.MovingToStandby
                && station.Motion.Feedback.IsMovingHorizontal
                && assembly.ShootingBoltResults.ContainsKey(last.Id))
            {
                Assert.Equal(0, z);
                Assert.False(station.Station.Completed);
                Assert.Equal(StationCylinderState.Up, station.PickupTablePosition);
                returnInterrupted = true;
                machine.Stop();
            }
        }
        io.OutputChanged += OnOutput;
        assembly.ResultsChanged += StopAfterResult;
        station.Motion.Feedback.PositionChanged += StopDuringReturn;
        try
        {
            await WaitUntilAsync(() => machine.IsStartAllowed);
            await review.CheckStartCommand.ExecuteAsync(null);
            Assert.Equal(StartArea.Station2, review.SelectedStartArea);
            Assert.False(review.IsFasteningResumeConfirmed);
            Assert.False(review.IsStartReviewAllowed);
            Assert.Equal(2, review.RemainingFasteningCount);
            Assert.Equal(3, review.FasteningResumeBolts.Count);
            Assert.Same(priorResult, (firstHead == FasteningHead.Pickup
                ? review.FasteningResumeBolts.First() : review.FasteningResumeBolts.Last()).Result);
            await review.ConfirmStartCommand.ExecuteAsync(null);
            await machine.StartAsync(); // An unfinished carrier is never admitted without confirmation.
            Assert.Empty(started);
            Assert.False(state.AutomaticRunning);

            review.IsFasteningResumeConfirmed = true;
            Assert.True(review.IsStartReviewAllowed);
            await review.ConfirmStartCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(6));
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.False(station.Station.Completed);
            Assert.Same(job, station.Station.CurrentJob);
            Assert.False(review.IsFasteningResumeConfirmed);
            Assert.Equal(stopDuringReturn, returnInterrupted);
            var firstResult = assembly.ShootingBoltResults[first.Id];
            assembly.ResultsChanged -= StopAfterResult;

            await review.CheckStartCommand.ExecuteAsync(null);
            Assert.False(review.IsStartReviewAllowed);
            Assert.Same(firstResult, review.FasteningResumeBolts[firstHead == FasteningHead.Pickup ? 1 : 0].Result);
            Assert.Equal(stopAfterLastResult ? 0 : 1, review.RemainingFasteningCount);
            review.IsFasteningResumeConfirmed = true;
            var resumed = review.ConfirmStartCommand.ExecuteAsync(null);
            try
            {
                Assert.True(await WaitUntilAsync(() => station.Station.Completed && station.Step is BoltFasteningState.Waiting,
                    TimeSpan.FromSeconds(6)), state.AlarmDetail);
                Assert.True(state.AutomaticRunning);
                Assert.False(resumed.IsCompleted);
                Assert.Equal(new[] { first.Id, last.Id }, started.ToArray());
                Assert.Same(priorResult, assembly.PickupBoltResults[recorded.Id]);
                Assert.Same(firstResult, assembly.ShootingBoltResults[first.Id]);
                Assert.Equal(AssemblyResult.Ng, assembly.FasteningResult);
                var standby = firstHead == FasteningHead.Pickup
                    ? settings.BoltFastening.PickupPosition : settings.BoltFastening.GetBoltPosition(first);
                var standbyZ = firstHead == FasteningHead.Pickup ? 0 : settings.BoltFastening.SafeZ;
                Assert.Equal((standby.X, standby.Y, standbyZ),
                    (station.Motion.Position.X, station.Motion.Position.Y, station.Motion.Position.Z));

                SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
                station.Station.ClearJob();
                Assert.Equal((standby.X, standby.Y, standbyZ), station.Motion.Feedback.Position);
                Assert.False(station.Motion.Feedback.IsMoving);
                SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
                Assert.True(await WaitUntilAsync(() => station.Station.Completed, TimeSpan.FromSeconds(6)), state.AlarmDetail);
                Assert.NotSame(job, station.Station.CurrentJob);
                Assert.Equal(firstHead == FasteningHead.Pickup
                    ? new[] { first.Id, last.Id, recorded.Id, first.Id, last.Id }
                    : [first.Id, last.Id, first.Id, last.Id, recorded.Id], started.ToArray());
                Assert.True(state.AutomaticRunning);
            }
            finally
            {
                machine.Stop();
                await resumed.WaitAsync(TimeSpan.FromSeconds(3));
            }
        }
        finally
        {
            assembly.ResultsChanged -= StopAfterResult;
            io.OutputChanged -= OnOutput;
            station.Motion.Feedback.PositionChanged -= StopDuringReturn;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task FasteningResumeConfirmationCannotBypassChangedCarrierOrOtherStartupBlocks()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.Units.PcbPlacement = true;
        await using var services = CreateServices(settings);
        var bolt = new BoltPoint { FasteningX = 10, FasteningY = 10 };
        services.GetRequiredService<RecipeManager>().Current.Pcb.BoltPoints = [bolt];
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>().Station;
        var review = services.GetRequiredService<OperationViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        await station.SeatAsync(CancellationToken.None);
        var reviewedJob = station.CurrentJob;
        var assembly = station.GetAssembly(HeatSinkSlot.HeatSink1);
        var recorded = new BoltResult(false, 8);
        assembly.RecordBolt(bolt.Head, bolt.Id, recorded);
        var writes = 0;
        void CountWrite(OutputIo output, bool value)
        {
            writes++;
        }
        io.OutputChanged += CountWrite;
        try
        {
            await WaitUntilAsync(() => machine.IsStartAllowed);
            await review.CheckStartCommand.ExecuteAsync(null);
            review.IsFasteningResumeConfirmed = true;
            Assert.True(review.IsStartReviewAllowed);
            await review.CheckStartCommand.ExecuteAsync(null);
            Assert.False(review.IsFasteningResumeConfirmed);
            Assert.False(review.IsStartReviewAllowed);
            // DOWN, neither sensor and contradictory feedback must all revoke resume confirmation.
            foreach (var (up, down) in new[] { (false, true), (false, false), (true, true) })
            {
                review.IsFasteningResumeConfirmed = true;
                io.SetInputs((InputIo.BoltFasteningBackupPlateUp, up), (InputIo.BoltFasteningBackupPlateDown, down));
                Assert.False(review.IsFasteningResumeAvailable);
                Assert.False(review.IsFasteningResumeConfirmed);
                Assert.False(review.IsStartReviewAllowed);
                await machine.StartAsync(resumeFastening: reviewedJob).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(state.AutomaticRunning);
                Assert.Equal(0, writes);
                io.SetInputs((InputIo.BoltFasteningBackupPlateUp, true), (InputIo.BoltFasteningBackupPlateDown, false));
                Assert.True(review.IsFasteningResumeAvailable);
                Assert.False(review.IsFasteningResumeConfirmed);
                Assert.False(review.IsStartReviewAllowed);
                Assert.Same(reviewedJob, station.CurrentJob);
                Assert.Same(recorded, assembly.ShootingBoltResults[bolt.Id]);
                Assert.Same(recorded, Assert.Single(review.FasteningResumeBolts).Result);
            }
            review.IsFasteningResumeConfirmed = true;
            io.SetInput(InputIo.BoltFasteningHeatSink2Present, true);
            Assert.False(review.IsFasteningResumeAvailable); // Reviewed PCB targets no longer match.
            Assert.False(review.IsFasteningResumeConfirmed);
            io.SetInput(InputIo.BoltFasteningHeatSink2Present, false);
            Assert.False(review.IsFasteningResumeConfirmed);
            review.IsFasteningResumeConfirmed = true;
            var original = station.CurrentJob;
            SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            station.ClearJob();
            SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            Assert.False(review.IsFasteningResumeAvailable);
            Assert.False(review.IsStartReviewAllowed);
            await machine.StartAsync(resumeFastening: original);
            Assert.False(state.AutomaticRunning);

            foreach (var blocker in new[] { InputIo.PickupHeadVacuumDetected, InputIo.PcbPlacementHeatSink1Present })
            {
                await review.CheckStartCommand.ExecuteAsync(null);
                review.IsFasteningResumeConfirmed = true;
                Assert.True(review.IsStartReviewAllowed);
                io.SetInput(blocker, true);
                await review.ConfirmStartCommand.ExecuteAsync(null);
                Assert.False(state.AutomaticRunning);
                Assert.False(review.IsFasteningResumeConfirmed);
                Assert.False(review.IsStartReviewAllowed);
                io.SetInput(blocker, false);
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await machine.StartAsync(cancelled.Token, station.CurrentJob);
            Assert.False(state.IsError);
            Assert.Equal(0, writes);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            io.OutputChanged -= CountWrite;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FasteningResumeStopsOnCancelOrModeChangeWithoutInventingAResult(bool changeMode)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.BoltFastening.DryRunMilliseconds = 1_000;
        await using var services = CreateServices(settings);
        services.GetRequiredService<RecipeManager>().Current.Pcb.BoltPoints = [new() { FasteningX = 10, FasteningY = 10 }];
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>().Station;
        var review = services.GetRequiredService<OperationViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        await station.SeatAsync(CancellationToken.None);
        var interrupted = false;
        void OnOutput(OutputIo output, bool on)
        {
            if (output != OutputIo.ShootingBoltStart || !on)
                return;
            interrupted = true;
            if (changeMode)
                io.SetInput(InputIo.AutoMode, false);
            else
                review.ConfirmStartCommand.Cancel();
        }
        io.OutputChanged += OnOutput;
        try
        {
            await WaitUntilAsync(() => machine.IsStartAllowed);
            await review.CheckStartCommand.ExecuteAsync(null);
            review.IsFasteningResumeConfirmed = true;
            await review.ConfirmStartCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(interrupted);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.False(state.AutomaticRunning);
            Assert.False(io.GetOutput(OutputIo.ShootingBoltStart));
            Assert.False(station.Completed);
            Assert.Empty(station.GetAssembly(HeatSinkSlot.HeatSink1).ShootingBoltResults);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            await review.CheckStartCommand.ExecuteAsync(null);
            Assert.False(review.IsFasteningResumeConfirmed);
            Assert.False(review.IsStartReviewAllowed);
            Assert.Equal(1, review.RemainingFasteningCount);
        }
        finally
        {
            io.OutputChanged -= OnOutput;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualBoltTestReportsLateFailureWithoutReplacingEmergencyStop(bool emergencyStop)
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgConveyor) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var failure = new IOException("Bolt head STOP output failed.");
        await machine.InitializeAsync();
        try
        {
            using var bus = new AdcControllerStub
            {
                StopWriteFailure = failure,
                Started = () =>
                {
                    Assert.True(state.BoltTestRunning);
                    if (emergencyStop)
                        io.SetInput(InputIo.EmergencyStop1Pressed, true);
                },
            };
            var headIo = new VirtualIoService(VirtualTestSupport.Outputs(), new());
            bus.BindIo(headIo, FasteningHead.Pickup);
            using var diagnostics = new AdcProtocolViewModel(bus, new VirtualAdcBus(), headIo, settings.Hantas, machine, state);
            var taskFailure = await Record.ExceptionAsync(() => diagnostics.StartCommand.ExecuteAsync(null));
            Assert.NotNull(taskFailure);
            Assert.Contains(failure.Message, taskFailure.ToString());
            if (!emergencyStop)
                Assert.Contains($"OK  {UiText.Get("Result torque")}", diagnostics.ResultMessage);
            Assert.Contains("failed", diagnostics.ResultMessage);
            Assert.Equal(emergencyStop ? MachineAlarm.EmergencyStop : MachineAlarm.BoltFastening, state.Alarm);
            Assert.Contains(failure.Message, state.AlarmDetail);
            Assert.False(state.BoltTestRunning);
            Assert.False(state.IsRunning);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoltTestStartupFailureClearsRunningState(bool reverse)
    {
        var settings = new MachineSettings { Units = EnableOnly(MachineUnit.NgConveyor) };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var failure = new IOException("Feedback failed while entering bolt test.");
        void FailWhenTestingStarts()
        {
            if (!state.BoltTestRunning)
                return;
            state.Changed -= FailWhenTestingStarts;
            throw failure;
        }

        using var bus = new AdcControllerStub();
        using var diagnostics = new AdcProtocolViewModel(bus, new VirtualAdcBus(), services.GetRequiredService<IIoService>(), settings.Hantas, machine, state);
        state.Changed += FailWhenTestingStarts;
        try
        {
            var command = reverse ? diagnostics.ReverseCommand : diagnostics.StartCommand;
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => command.ExecuteAsync(null)));

            Assert.False(state.BoltTestRunning);
            Assert.False(state.IsRunning);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
            Assert.Contains(failure.Message, state.AlarmDetail);
            Assert.Equal(0, bus.StartWrites);
        }
        finally
        {
            state.Changed -= FailWhenTestingStarts;
            state.BoltTestRunning = false;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(MotionAxis.X)]
    [InlineData(MotionAxis.Z)]
    public async Task PlacementStopsWhenHandlerUpFeedbackIsLost(MotionAxis axis)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.PcbPlacementHandler.Motion.ZSpeed = 10;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var move = axis == MotionAxis.Z
                ? placement.MoveAxisAsync(axis, 10, stop.Token)
                : placement.JogAsync(axis, 10, stop.Token);
            await WaitUntilAsync(() => placement.Motion.Feedback.IsMoving);
            io.SetInput(InputIo.PcbPlacementHandlerUp, false);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            Assert.Equal(MachineAlarm.PcbPlacement, state.Alarm);
            Assert.False(placement.Motion.Feedback.IsMoving);
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.MoveAxisAsync(MotionAxis.Z, 10));
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.JogAsync(MotionAxis.X, 10));
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.AdjustAxisAsync(MotionAxis.X, 10, 10));
            await Assert.ThrowsAsync<MotionInterlockException>(
                () => placement.MoveAxisAsync(MotionAxis.Z, settings.PcbPlacementHandler.HandoffPosition.Z));
        }
        finally
        {
            stop.Cancel();
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(MachineUnit.PcbPlacement, MotionGroup.PcbPlacementHandler, InputIo.PcbPlacementHandlerUp, MachineAlarm.PcbPlacement)]
    [InlineData(MachineUnit.BoltFastening, MotionGroup.BoltFastening, InputIo.PickupHeadUp, MachineAlarm.BoltFastening)]
    [InlineData(MachineUnit.Inspection, MotionGroup.InspectionGantry, InputIo.NgCarrierPickupUp, MachineAlarm.NgCarrierTransfer)]
    public async Task MonitoredExternalMovementChecksRaisedCylinderInterlocks(
        MachineUnit unit, MotionGroup group, InputIo raised, MachineAlarm expectedAlarm)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(unit);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var probe = probes[group];
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            io.SetInput(raised, false);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            var stopsBeforeMove = probe.StopCalls;
            // The controller starts moving without a local command or a device event.
            probe.OverrideState = (_, value) => value with { InMotion = true, InPosition = false };

            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => state.Alarm == expectedAlarm && probe.StopCalls > stopsBeforeMove,
                TimeSpan.FromSeconds(2)));
            Assert.Contains("Up", state.AlarmDetail);
        }
        finally
        {
            probe.OverrideState = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task CylinderInterlockDuringEmergencyStopKeepsTheEmergencyAlarm()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(default);
        void TripWhileMotionHasNotFinished(bool moving)
        {
            if (!moving || !placement.Motion.Feedback.IsMovingHorizontal)
                return;
            io.SetInput(InputIo.EmergencyStop1Pressed, true);
            Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
            Assert.True(placement.Motion.Feedback.IsMovingHorizontal);
            io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        }

        placement.Motion.Feedback.MovingChanged += TripWhileMotionHasNotFinished;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                placement.MoveToXYAsync(new() { X = 20, Y = 20 }));

            Assert.Equal(MachineAlarm.EmergencyStop, state.Alarm);
            Assert.Contains("handler lift Up", state.AlarmDetail);
            Assert.False(placement.Motion.Feedback.IsMoving);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            placement.Motion.Feedback.MovingChanged -= TripWhileMotionHasNotFinished;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task MonitoredHorizontalMovementChecksHeadClearanceWhileZIsAlreadyMoving()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var probe = probes[MotionGroup.BoltFastening];
        var horizontal = false;
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            io.SetInput(InputIo.PickupHeadUp, false);
            probe.OverrideState = (axis, feedback) => feedback with
            {
                InMotion = axis == MotionAxis.Z || Volatile.Read(ref horizontal),
                InPosition = false,
            };
            await WaitUntilAsync(() => station.Motion.IsMoving);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            var stopsBeforeHorizontal = probe.StopCalls;

            Volatile.Write(ref horizontal, true);

            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => state.Alarm == MachineAlarm.BoltFastening && probe.StopCalls > stopsBeforeHorizontal,
                TimeSpan.FromSeconds(2)));
        }
        finally
        {
            probe.OverrideState = null;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownRequiresCurrentStoppedFeedbackEvenWithoutAnOwnedMotion(bool unreadable)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var feedback = services.GetRequiredService<MachineFeedbackMonitor>();
        await machine.InitializeAsync();
        var probe = probes[MotionGroup.BoltFastening];
        if (unreadable)
            probe.FailHardwareCalls = true;
        else
            probe.OverrideState = (_, state) => state with { InMotion = true };
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        try
        {
            var failure = await Record.ExceptionAsync(machine.ShutdownAsync);

            Assert.NotNull(failure);
            Assert.Contains(nameof(MotionGroup.BoltFastening), failure.ToString());
            Assert.True(feedback.Completion.IsCompleted);
        }
        finally
        {
            probe.FailHardwareCalls = false;
            probe.OverrideState = null;
            // A retry must read the now-stopped hardware, even though acquisition has ended.
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledStartAndHomeFinishWithoutEscapingOrTouchingHardware(bool home)
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var writes = 0;
        io.OutputChanged += (output, value) => writes++;
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var escaped = await Record.ExceptionAsync(() => home
            ? machine.HomeAsync(stop.Token) : machine.StartAsync(stop.Token));

        Assert.Null(escaped);
        Assert.Equal(0, writes);
        Assert.False(state.IsHoming);
        Assert.False(state.AutomaticRunning);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringCommandAdmissionPreventsStartOrHome(bool home)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateDisplayServices(out var feedback, settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        if (!home)
            await machine.HomeAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var started = false;
        var homeStarted = false;
        state.Changed += () =>
        {
            if (state.AutomaticRunning)
            {
                started = true;
                stop.Cancel();
            }
        };
        feedback.BeforeHome = () => homeStarted = true;
        feedback.BeforeRead = () =>
        {
            feedback.BeforeRead = null;
            machine.Stop();
        };
        try
        {
            await (home ? machine.HomeAsync(stop.Token) : machine.StartAsync(stop.Token));

            Assert.False(started);
            Assert.False(homeStarted);
            Assert.False(state.IsError);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            feedback.BeforeRead = null;
            feedback.BeforeHome = null;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MachineStartAndHomeReportAdmissionReadFailureWithoutStarting(bool home)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateDisplayServices(out var feedback, settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        if (!home)
            await machine.HomeAsync(CancellationToken.None);
        Assert.True(
            await VirtualTestSupport.WaitUntilAsync(
                () => home ? machine.IsHomeAllowed : machine.IsStartAllowed, TimeSpan.FromSeconds(2)),
            $"Command blocked: home={machine.HomeBlock}, start={machine.StartBlock}, alarm={state.AlarmDetail}");
        var failure = new IOException("Motion feedback failed while admitting the command.");
        feedback.BeforeRead = () =>
        {
            feedback.BeforeRead = null;
            throw failure;
        };
        try
        {
            var escaped = await Record.ExceptionAsync(() => home
                ? machine.HomeAsync(CancellationToken.None)
                : machine.StartAsync());

            Assert.Null(escaped);
            Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
            Assert.Contains(failure.Message, state.AlarmDetail);
            Assert.False(state.IsHoming);
            Assert.False(state.AutomaticRunning);
            Assert.False(state.IsRunning);
            Assert.False(feedback.Motion.IsMoving);
            Assert.True(machine.IsResetAllowed);
        }
        finally
        {
            feedback.BeforeRead = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task IndividualHomeReportsReadFailureBeforeMotionStarts()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var manual = services.GetRequiredService<MotionDiagnosticsViewModel>();
        var axis = manual.Axes.Single(
            row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
        feedback.BeforeRead = () => throw new IOException("Home feedback read failed.");

        await axis.HomeCommand.ExecuteAsync(null);

        Assert.Equal(MachineAlarm.HomeFailed, state.Alarm);
        Assert.Contains("Home feedback read failed.", state.AlarmDetail);
        Assert.False(state.IsHoming);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.False(services.GetRequiredService<InspectionStation>().Motion.Feedback.IsMoving);
    }

    [Fact]
    public async Task StopDuringServoFeedbackReadPreventsServoEnable()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        feedback.Motion.SetServo(MotionAxis.X, false);
        feedback.BeforeRead = () =>
        {
            feedback.BeforeRead = null;
            machine.Stop();
        };
        try
        {
            machine.ToggleServo(MotionGroup.InspectionGantry, MotionAxis.X);

            Assert.False(feedback.Motion.GetAxisState(MotionAxis.X).ServoOn);
            Assert.False(services.GetRequiredService<MachineState>().IsError);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            feedback.BeforeRead = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task IndividualHomeCancelsWhenAvailabilityReadFailsDuringNotification()
    {
        var settings = FlowSettings();
        settings.InspectionGantry.Motion.HorizontalHome.SearchSpeed = 1;
        await using var services = CreateDisplayServices(out var feedback, settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await services.GetRequiredService<MachineFeedbackMonitor>().StopAsync();
        await feedback.Motion.MoveAxisAsync(MotionAxis.X, 10, 1_000);
        var homing = machine.HomeAsync(MotionGroup.InspectionGantry, CancellationToken.None, MotionAxis.X);
        await WaitUntilAsync(() => state.IsHoming && feedback.Motion.GetAxisState(MotionAxis.X).InMotion);
        var notificationThread = Environment.CurrentManagedThreadId;
        var failure = new IOException("Manual HOME availability read failed.");
        feedback.BeforeRead = () =>
        {
            if (Environment.CurrentManagedThreadId != notificationThread)
                return;
            feedback.BeforeRead = null;
            throw failure;
        };
        try
        {
            Assert.Null(Record.Exception(state.Refresh));
            await homing.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
            Assert.Contains(failure.Message, state.AlarmDetail);
            Assert.False(feedback.Motion.IsMoving);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            feedback.BeforeRead = null;
            machine.Stop();
            await homing;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMotionStartFeedbackDoesNotBlockShutdown(bool jog)
    {
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            operations,
            hasY: false,
            hasZ: false);
        var failure = new InvalidOperationException("Motion feedback unavailable.");
        motion.Initialize();
        motion.StateChanged += () =>
        {
            if (motion.IsMoving)
            {
                throw failure;
            }
        };

        var actual = jog
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => motion.JogAsync(MotionAxis.X, 1))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => motion.MoveAxisAsync(MotionAxis.X, 100, 1));

        Assert.Same(failure, actual);
        await operations.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(motion.IsMoving);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownWaitsForFinalMotionFeedback(bool jog)
    {
        var operations = new OperationCancellation();
        using var motion = new VirtualMotionService(
            new MotionSettings(),
            operations,
            hasY: false,
            hasZ: false);
        using var releaseFeedback = new ManualResetEventSlim();
        var feedbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        motion.Initialize();
        motion.PositionChanged += (_, _, _) =>
        {
            if (!motion.IsMoving)
            {
                feedbackEntered.TrySetResult();
                releaseFeedback.Wait(TimeSpan.FromSeconds(5));
            }
        };

        var moving = jog ? motion.JogAsync(MotionAxis.X, 1) : motion.MoveAxisAsync(MotionAxis.X, 100, 1);

        var shutdown = Task.Run(() => operations.ShutdownAsync());
        try
        {
            await feedbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(motion.IsMoving);
            Assert.False(shutdown.IsCompleted);
            Assert.False(moving.IsCompleted);
        }
        finally
        {
            releaseFeedback.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
        }
    }

    [Fact]
    public async Task ManualJogFaultStopsTheMachineAndAllowsReset()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.Inspection),
        };
        FastHomes(settings);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);

        var fault = new InvalidOperationException("Jog feedback failed.");
        var failed = 0;
        gantry.Motion.Feedback.PositionChanged += (_, _, _) =>
        {
            if (gantry.Motion.Feedback.IsMoving && Interlocked.Exchange(ref failed, 1) == 0)
            {
                io.SetOutput(OutputIo.NgConveyorRun, true);
                throw fault;
            }
        };
        var teaching = services.GetRequiredService<TeachingViewModel>();
        teaching.SelectedTeachingUnit = HardwareArea.InspectionGantry;
        await WaitUntilAsync(() => teaching.IsJogXAllowed);
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Contains(fault.Message, state.AlarmDetail);
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));

        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Null(state.AlarmDetail);
        using var stopped = new CancellationTokenSource();
        var jog = gantry.JogAsync(MotionAxis.X, 10, stopped.Token);
        Assert.True(gantry.Motion.Feedback.IsMoving);
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => jog.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartRaisesEmptyCylindersAndBlocksUnownedHeldPcb(bool holdingPcb)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Options.UseDoorInterlock = false;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await placement.SetLiftDownAsync(true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
        io.SetInput(InputIo.PcbPlacementPcbDetected, holdingPcb);
        io.SetInput(InputIo.AutoMode, false);
        var started = false;
        var readyBeforeStarting = false;
        void StopAtAutomaticStart()
        {
            if (!state.AutomaticRunning || started)
                return;
            started = true;
            readyBeforeStarting = placement.Lift == StationCylinderState.Up
                && placement.IpmLift == (holdingPcb ? StationCylinderState.Down : StationCylinderState.Up);
            machine.Stop();
        }

        state.Changed += StopAtAutomaticStart;
        try
        {
            await WaitUntilAsync(() => machine.IsStartAllowed);
            await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(3));
            if (holdingPcb)
            {
                Assert.False(started);
                Assert.Equal(StartBlockReason.MaterialRemaining, machine.StartBlock);
                Assert.True(io.GetInput(InputIo.PcbPlacementPcbDetected));
                Assert.True(io.GetOutput(OutputIo.PcbPlacementIpmDown));
                return;
            }
            Assert.True(started);
            Assert.True(readyBeforeStarting);
            Assert.Equal(holdingPcb, io.GetOutput(OutputIo.PcbPlacementIpmDown));
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.IsRunning);
        }
        finally
        {
            state.Changed -= StopAtAutomaticStart;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(MotionGroup.PcbPlacementHandler, InputIo.PcbPlacementHandlerUp, InputIo.PcbPlacementHandlerDown)]
    [InlineData(MotionGroup.BoltFastening, InputIo.PickupHeadUp, InputIo.PickupHeadDown)]
    [InlineData(MotionGroup.BoltFastening, InputIo.ShootingHeadUp, InputIo.ShootingHeadDown)]
    public async Task MotionRequiresRaisedCylindersWhileFasteningZCanRetract(
        MotionGroup group,
        InputIo up,
        InputIo down)
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.Motion.HorizontalSpeed = 10;
        settings.BoltFastening.Motion.HorizontalSpeed = 10;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        var fastening = services.GetRequiredService<BoltFasteningStation>();
        var isPlacement = group == MotionGroup.PcbPlacementHandler;
        var feedback = isPlacement ? placement.Motion.Feedback : fastening.Motion.Feedback;
        Task MoveXY()
        {
            return isPlacement
                ? placement.MoveToXYAsync(new() { X = 20, Y = 20 })
                : fastening.MoveToXYAsync(20, 20);
        }

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;

        io.SetInput(up, false);
        io.SetInput(down, true);
        if (isPlacement)
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.MoveAxisAsync(MotionAxis.Z, 1));
        else
            await fastening.MoveZAsync(1);
        Assert.Equal(isPlacement ? 0 : 1, feedback.Position.Z);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await Assert.ThrowsAsync<MotionInterlockException>(MoveXY);
        await Assert.ThrowsAsync<MotionInterlockException>(
            () => isPlacement
                ? placement.HomeAxisAsync(MotionAxis.X)
                : fastening.HomeAxisAsync(MotionAxis.X));
        if (isPlacement)
            await Assert.ThrowsAsync<MotionInterlockException>(() => placement.JogAsync(MotionAxis.Y, 10));

        io.SetInput(up, true);
        await Assert.ThrowsAsync<MotionInterlockException>(MoveXY);
        io.SetInput(down, false);
        var move = MoveXY();
        await WaitUntilAsync(() => feedback.IsMovingHorizontal);
        io.SetInput(up, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.False(feedback.IsMoving);
        Assert.False(feedback.IsMovingHorizontal);
        Assert.Equal(isPlacement ? MachineAlarm.PcbPlacement : MachineAlarm.BoltFastening, state.Alarm);
        Assert.Contains(isPlacement ? "axis movement" : "horizontal movement", state.AlarmDetail);
        Assert.Contains("Between", state.AlarmDetail);
    }

    [Fact]
    public async Task PlacementHoldingPcbBlocksIpmRaiseForHomeButNotHorizontalTravel()
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.Motion.HorizontalSpeed = 100;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacer>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
        await WaitUntilAsync(() => state.FeedbackReadiness.Homed);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        Assert.False(machine.IsHomeAllowed);
        Assert.Equal(HomeBlockReason.PlacementHoldingPcb, machine.HomeBlock);
        Assert.Equal(StationCylinderState.Up, placement.Lift);
        Assert.True(io.GetInput(InputIo.PcbPlacementIpmDown));

        var move = placement.MoveToXYAsync(new() { X = 20, Y = 20 });
        await WaitUntilAsync(() => placement.Motion.Feedback.IsMovingHorizontal);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false);
        await move;

        Assert.Equal(HomeBlockReason.None, machine.HomeBlock);
        Assert.Equal(
            (20, 20, settings.PcbPlacementHandler.HandoffPosition.Z),
            placement.Motion.Feedback.Position);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task ManualBoltPickupAndReturnPreserveOrderAndVacuumOnStopAndRetry()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.BoltFastening.SafeZ = 5;
        settings.BoltFastening.PickupPosition = new() { X = 40, Y = 30, Z = 12 };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(14);
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.BoltPickup);
        await WaitUntilAsync(() => teaching.IsMoveToPointAllowed);

        // Hold the table feedback; both heads must remain raised throughout pickup.
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupTableDown, false);
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        var vacuumChanged = false;
        var movedXyWithHeadDown = false;
        var loweredAt = new ConcurrentQueue<(double X, double Y, double Z)>();
        var tableDescents = 0;
        io.OutputChanged += (output, value) =>
        {
            if (output is OutputIo.PickupHeadVacuumPump or OutputIo.ShootingHeadVacuumPump)
                vacuumChanged = true;
            if (value && output is OutputIo.PickupHeadDown or OutputIo.ShootingHeadDown)
                loweredAt.Enqueue(gantry.Motion.Feedback.Position);
            if (output == OutputIo.PickupTableDown && value)
            {
                Assert.True(gantry.IsHorizontalMoveAllowed);
                Assert.Equal(settings.BoltFastening.SafeZ, gantry.Motion.Feedback.Position.Z);
                Interlocked.Increment(ref tableDescents);
            }
        };
        gantry.Motion.Feedback.PositionChanged += (_, _, _) =>
            movedXyWithHeadDown |= gantry.Motion.Feedback.IsMovingHorizontal
                && !gantry.IsHorizontalMoveAllowed;

        var move = teaching.MoveToPointCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => tableDescents == 1);
        Assert.Equal((0, 0, 5), gantry.Motion.Feedback.Position);
        Assert.False(move.IsCompleted);
        Assert.False(io.GetInput(InputIo.PickupTableDown));
        teaching.JogStopCommand.Execute(null);
        await move.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((0, 0, 5), gantry.Motion.Feedback.Position);
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.True(io.GetOutput(OutputIo.PickupTableDown)); // Stop keeps pneumatic outputs.

        await WaitUntilAsync(() => teaching.IsMoveToPointAllowed);
        var retry = teaching.MoveToPointCommand.ExecuteAsync(null);
        Assert.False(retry.IsCompleted);
        io.SetInputs((InputIo.PickupTableUp, false), (InputIo.PickupTableDown, true));
        await retry.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((40, 30, 12), gantry.Motion.Feedback.Position);
        Assert.Equal(StationCylinderState.Up, gantry.PickupHeadPosition);
        Assert.Equal(StationCylinderState.Up, gantry.ShootingHeadPosition);
        await WaitUntilAsync(() => teaching.IsReturnFromPickupAllowed);
        var stopAtSafeZ = true;
        gantry.Motion.Feedback.PositionChanged += (_, _, z) =>
        {
            if (stopAtSafeZ
                && Math.Abs(z - settings.BoltFastening.SafeZ) <= VirtualTestSupport.PositionToleranceMillimeters)
            {
                stopAtSafeZ = false;
                teaching.JogStopCommand.Execute(null);
            }
        };
        await teaching.ReturnFromPickupCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(stopAtSafeZ);
        Assert.Equal((40, 30, 5), gantry.Motion.Feedback.Position);
        Assert.True(gantry.IsHorizontalMoveAllowed);
        Assert.False(gantry.Motion.Feedback.IsMoving);

        var returning = teaching.ReturnFromPickupCommand.ExecuteAsync(null);
        await returning.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((40, 30, 5), gantry.Motion.Feedback.Position);
        Assert.True(gantry.IsHorizontalMoveAllowed);
        await WaitUntilAsync(() => teaching.IsMoveToPointAllowed);
        Assert.True(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
        Assert.False(vacuumChanged);
        Assert.False(movedXyWithHeadDown);
        Assert.Empty(loweredAt);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualPickupAndReturnReportCylinderTimeout(bool returning)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        var teaching = services.GetRequiredService<TeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(
            point => point.Position.Target == TeachingTarget.BoltPickup);
        if (returning)
        {
            await gantry.MoveToTeachingPositionAsync(teaching.SelectedPoint.Position, settings.BoltFastening.PickupPosition);
            // Feedback can change outside the command; an UP request is not confirmation.
            io.SetInputs((InputIo.PickupHeadUp, false), (InputIo.PickupHeadDown, true));
        }
        io.AutoResponseEnabled = false;
        settings.Options.TimeoutMilliseconds = 50;

        await (returning ? teaching.ReturnFromPickupCommand : teaching.MoveToPointCommand).ExecuteAsync(null);

        Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
        Assert.False(gantry.Motion.Feedback.IsMoving);
        Assert.Equal(settings.BoltFastening.SafeZ, gantry.Motion.Feedback.Position.Z);
        Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FasteningAdjustmentStopsOnModeOrServoLoss(bool autoMode)
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(10);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingHeadDown, true);
        var motion = services.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
        var jog = motion.JogAsync(MotionAxis.X, 1);
        await WaitUntilAsync(() => gantry.Motion.Feedback.Position.X > 0);
        if (autoMode)
            io.SetInput(InputIo.AutoMode, false);
        else
            motion.SetServo(MotionAxis.X, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => jog.WaitAsync(TimeSpan.FromSeconds(2)));
        var stopped = gantry.Motion.Feedback.Position;
        Assert.Equal(MotionCommand.None, gantry.Motion.Feedback.Command);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gantry.AdjustAxisAsync(MotionAxis.X, 20, 1));
        Assert.Equal(stopped, gantry.Motion.Feedback.Position);
        Assert.True(io.GetInput(InputIo.ShootingHeadDown));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualOperationObservesServoLossWithoutADriverEvent(bool otherGroupAlreadyOff)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.PcbPlacement = true;
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            await WaitUntilAsync(() => state.Ready);
            if (otherGroupAlreadyOff)
            {
                probes[MotionGroup.PcbPlacementHandler].OverrideState = (_, feedback) => feedback with { ServoOn = false };
                await WaitUntilAsync(() => !state.FeedbackReadiness.ServosOn);
            }
            using var operation = machine.BeginManualOperation(
                () => machine.IsManualMotionReady(MotionGroup.InspectionGantry), CancellationToken.None);
            Assert.NotNull(operation);
            Assert.False(operation.IsCancellationRequested);

            // A drive's external servo change is seen only by the shared monitor.
            probes[MotionGroup.InspectionGantry].OverrideState = (_, feedback) => feedback with { ServoOn = false };

            Assert.True(await VirtualTestSupport.WaitUntilAsync(
                () => operation.IsCancellationRequested, TimeSpan.FromSeconds(2)));
        }
        finally
        {
            foreach (var probe in probes.Values)
                probe.OverrideState = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task CylinderFeedbackIsRecheckedBetweenTravelZAndXy()
    {
        var settings = FlowSettings();
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(10);
        var before = gantry.Motion.Feedback.Position;
        io.AutoResponseEnabled = false;
        gantry.Motion.Feedback.MovingChanged += moving =>
        {
            if (moving && !gantry.Motion.Feedback.IsMovingHorizontal)
                io.SetInput(InputIo.PickupHeadUp, false);
        };

        await Assert.ThrowsAsync<MotionInterlockException>(() => gantry.MoveToXYAsync(20, 20));

        var after = gantry.Motion.Feedback.Position;
        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y, after.Y);
        Assert.Equal(settings.BoltFastening.SafeZ, after.Z);
        Assert.False(gantry.Motion.Feedback.IsMoving);
    }

    [Theory]
    [InlineData(MachineUnit.PcbSupply, MotionGroup.PcbSupply)]
    [InlineData(MachineUnit.PcbPlacement, MotionGroup.PcbPlacementHandler)]
    [InlineData(MachineUnit.BoltFastening, MotionGroup.BoltFastening)]
    [InlineData(MachineUnit.Inspection, MotionGroup.InspectionGantry)]
    public async Task OnlyEnabledMotionsAreInitializedReadResetAndHomed(
        MachineUnit unit,
        MotionGroup group)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(unit);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var manual = services.GetRequiredService<MotionDiagnosticsViewModel>();
        foreach (var (candidate, probe) in probes)
        {
            if (candidate == group)
                continue;
            // Also simulate an initialized drive that was subsequently disabled.
            probe.ReportReady = true;
            probe.FailHardwareCalls = true;
        }

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(state.Available);
        Assert.False(state.FeedbackReadiness.Faulted);
        Assert.True(machine.IsHomeAllowed);
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.Ready);
        Assert.True(state.Ready);
        Assert.True(machine.IsManualMotionReady(group));

        probes[group].Motion.SetServo(MotionAxis.X, false);
        await WaitUntilAsync(() => machine.IsResetAllowed);
        await machine.ResetAsync();
        await WaitUntilAsync(() => state.Ready);
        Assert.True(state.Ready);
        Assert.Equal(1, probes[group].ResetCalls);

        if (group == MotionGroup.BoltFastening)
        {
            var teaching = services.GetRequiredService<TeachingViewModel>();
            teaching.SelectedTeachingUnit = HardwareArea.BoltFastening;
            teaching.StepDistance = 0.1;
            await WaitUntilAsync(() => teaching.IsStepXPlusAllowed);
            var before = probes[group].Motion.Position;
            await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
            Assert.Equal(before.X + 0.1, probes[group].Motion.Position.X, precision: 6);
        }

        if (group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler)
        {
            var placer = services.GetRequiredService<PcbPlacer>();
            Assert.NotEqual(PcbPlacementState.ReceivingPcb,
                placer.Phase);
        }

        foreach (var row in manual.Axes.Where(row => row.Group != group))
        {
            Assert.False(row.IsToggleServoAllowed);
            Assert.False(row.IsHomeAllowed);
            Assert.Null(row.Diagnostics.Sample.State);
            // Bypassing CanExecute still must not command a disabled drive.
            row.ToggleServoCommand.Execute(null);
            await row.HomeCommand.ExecuteAsync(null);
        }

        Assert.All(
            probes.Where(item => item.Key != group),
            item => Assert.Equal(0, item.Value.HardwareCalls));
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetDoesNotClearMachineAlarmWhenMotionFeedbackRemainsFaulted(bool alarmRemains)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var probe = probes[MotionGroup.PcbSupply];
        await machine.InitializeAsync();
        probe.OverrideState = (_, feedback) => feedback with { Alarm = alarmRemains, ServoOn = alarmRemains };
        await WaitUntilAsync(() => machine.IsResetAllowed);

        await machine.ResetAsync();

        Assert.Equal(1, probe.ResetCalls);
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Contains("not confirmed by hardware feedback", state.AlarmDetail);
        Assert.False(state.Ready);
    }

    [Fact]
    public async Task StopDuringResetFinalFeedbackKeepsTheExistingAlarm()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var probe = probes[MotionGroup.PcbSupply];
        await machine.InitializeAsync();
        state.SetError(MachineAlarm.PcbSupply, new InvalidOperationException("Original alarm."));
        var stopped = false;
        probe.BeforeAxisStateRead = () =>
        {
            probe.BeforeAxisStateRead = null;
            Assert.Equal(1, probe.ResetCalls);
            stopped = true;
            machine.Stop();
        };
        try
        {
            await machine.ResetAsync();

            Assert.True(stopped);
            Assert.Equal(MachineAlarm.PcbSupply, state.Alarm);
            Assert.Contains("Original alarm.", state.AlarmDetail);
        }
        finally
        {
            probe.BeforeAxisStateRead = null;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDuringFinalStartOrHomeFeedbackDoesNotRaiseAMotionAlarm(bool home)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var probe = probes[MotionGroup.InspectionGantry];
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var reads = 0;
        var stopped = false;
        probe.BeforeAxisStateRead = () =>
        {
            // Let initial admission see ready axes, then STOP during the final live check.
            if (++reads <= probe.Motion.Axes.Count)
                return;
            probe.BeforeAxisStateRead = null;
            stopped = true;
            machine.Stop();
            probe.OverrideState = (_, feedback) => feedback with { ServoOn = false };
        };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (home)
                await machine.HomeAsync(timeout.Token);
            else
                await machine.StartAsync(timeout.Token);

            Assert.True(stopped);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.AutomaticRunning);
            Assert.False(state.IsHoming);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            probe.BeforeAxisStateRead = null;
            probe.OverrideState = null;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task DisablingAFailedMotionAllowsResetWithoutHidingAnEnabledMotionFailure()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        probes[MotionGroup.PcbSupply].FailHardwareCalls = true;

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(state.Ready);
        var failedCalls = probes[MotionGroup.PcbSupply].HardwareCalls;

        settings.Units.PcbSupply = false;
        settings.Units.PcbPlacement = true;
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        await machine.ResetAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await WaitUntilAsync(() => state.Ready);
        Assert.Equal(failedCalls, probes[MotionGroup.PcbSupply].HardwareCalls);
        // Re-enabling the same faulty hardware makes it mandatory again.
        settings.Units.PcbSupply = true;
        await WaitUntilAsync(() => state.FeedbackReadiness.Faulted);
        Assert.False(state.Ready);
        var placementResets = probes[MotionGroup.PcbPlacementHandler].ResetCalls;
        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Equal(placementResets + 1, probes[MotionGroup.PcbPlacementHandler].ResetCalls);
    }

    public enum ManualCommandFailure
    {
        Motion,
        Canceled,
        Safety,
        Programming,
    }

    [Theory]
    [InlineData(InputIo.InspectionHeatSink1Present)]
    [InlineData(InputIo.InspectionBackupPlateUp)]
    public async Task NgPickupRechecksSourceAfterDescent(
        InputIo lostInput)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.NgConveyor = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var transfer = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await transfer.SeatStationAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        // Detection can remain ON even though the pickup has not gripped anything.
        io.SetInput(InputIo.NgCarrierDetected, true);
        var lost = false;
        var closed = false;
        var raisedAfterLoss = false;
        void LoseSourceDuringDescent(OutputIo output, bool on)
        {
            if (output == OutputIo.NgCarrierPickupDown && on && !lost)
            {
                lost = true;
                io.SetInput(lostInput, false);
            }
            closed |= lost && output == OutputIo.NgCarrierGripperClose && on;
            raisedAfterLoss |= lost && output == OutputIo.NgCarrierPickupDown && !on;
        }

        io.OutputChanged += LoseSourceDuringDescent;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            Assert.False(await transfer.ExecuteTransferAsync(
                InspectionStationState.PickingCarrier, timeout.Token));
            Assert.True(lost);
            Assert.False(closed);
            Assert.False(raisedAfterLoss);
            Assert.False(transfer.IsTransferPending);
            Assert.Equal(NgTransferGripperState.Open, transfer.Gripper);

            io.OutputChanged -= LoseSourceDuringDescent;
            io.SetInput(lostInput, true);
            Assert.True(await transfer.ExecuteTransferAsync(
                InspectionStationState.PickingCarrier, timeout.Token));
            Assert.True(transfer.IsTransferPending);
            Assert.True(transfer.IsRaised);
            Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);
        }
        finally
        {
            io.OutputChanged -= LoseSourceDuringDescent;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NgTransferStopsBeforeLoweringAfterGripFeedbackLoss(bool opens)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.InspectionGantry.Motion.HorizontalSpeed = 200;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var move = services.GetRequiredService<InspectionStation>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var pickup = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await move.ExecuteTransferAsync(InspectionStationState.PickingCarrier, timeout.Token);
        var lost = false;
        var lowered = false;
        gantry.Motion.Feedback.PositionChanged += (x, y, z) =>
        {
            if (!lost && gantry.Motion.Feedback.IsMoving && x > 30)
            {
                lost = true;
                io.SetInputs((InputIo.NgCarrierGripperClosed, false), (InputIo.NgCarrierGripperOpen, opens));
            }
        };
        io.OutputChanged += (output, on) => lowered |= output == OutputIo.NgCarrierPickupDown && on;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => move.ExecuteTransferAsync(InspectionStationState.PlacingCarrier, timeout.Token));
            Assert.True(lost);
            Assert.False(lowered);
            Assert.False(gantry.Motion.Feedback.IsMoving);
            Assert.True(pickup.IsTransferPending);
            var restarted = false;
            io.OutputChanged += (output, on) => restarted |= output is OutputIo.NgCarrierGripperClose
                or OutputIo.NgCarrierPickupDown;
            gantry.Motion.Feedback.MovingChanged += moving => restarted |= moving;
            await Assert.ThrowsAsync<InvalidOperationException>(() => move.ExecuteTransferAsync(InspectionStationState.PlacingCarrier, timeout.Token));
            Assert.False(restarted);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task SmemaTestInputsRequireTeachingAndClearWhenTheSelectorTurnsOff()
    {
        await using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supply = services.GetRequiredService<PcbSupplier>();
        var conveyor = services.GetRequiredService<IBTM.Conveyor.MainConveyor>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        try
        {
            Assert.True(io.GetInput(InputIo.AutoMode)); // Teaching/manual contact ON.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
            io.SetInput(InputIo.MainConveyorReadyFromRear, true);
            Assert.False(supply.UpstreamCarrierAvailable);
            Assert.False(conveyor.UpstreamCarrierAvailable);
            Assert.False(conveyor.DownstreamReady);
            supply.TestUpstreamCarrierAvailable = true;
            conveyor.TestUpstreamCarrierAvailable = true;
            conveyor.TestDownstreamReady = true;
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            Assert.True(supply.UpstreamCarrierAvailable);
            Assert.True(conveyor.UpstreamCarrierAvailable);
            Assert.True(conveyor.DownstreamReady);

            io.SetInput(InputIo.AutoMode, false);
            Assert.False(supply.TestUpstreamCarrierAvailable);
            Assert.False(conveyor.TestUpstreamCarrierAvailable);
            Assert.False(conveyor.TestDownstreamReady);
            Assert.False(supply.UpstreamCarrierAvailable);
            Assert.False(conveyor.UpstreamCarrierAvailable);
            Assert.False(conveyor.DownstreamReady);

            // Direct calls cannot enable TEST while the teaching switch is OFF.
            supply.TestUpstreamCarrierAvailable = true;
            conveyor.TestUpstreamCarrierAvailable = true;
            conveyor.TestDownstreamReady = true;
            Assert.False(supply.TestUpstreamCarrierAvailable);
            Assert.False(conveyor.TestUpstreamCarrierAvailable);
            Assert.False(conveyor.TestDownstreamReady);

            // Real SMEMA remains usable in AUTO.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, true);
            io.SetInput(InputIo.MainConveyorReadyFromRear, true);
            Assert.True(supply.UpstreamCarrierAvailable);
            Assert.True(conveyor.UpstreamCarrierAvailable);
            Assert.True(conveyor.DownstreamReady);
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
            io.SetInput(InputIo.MainConveyorReadyFromRear, false);
            io.SetInput(InputIo.AutoMode, true);
            Assert.False(supply.UpstreamCarrierAvailable);
            Assert.False(conveyor.UpstreamCarrierAvailable);
            Assert.False(conveyor.DownstreamReady);
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task FasteningCompletionKeepsInterruptedCarrierHistory()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        await using var services = CreateServices(settings);
        services.GetRequiredService<RecipeManager>().Current.Pcb.BoltPoints =
            [new() { Id = VirtualTestSupport.BoltId(1), Head = FasteningHead.Shooting, X = 0, Y = 0, FasteningX = 0, FasteningY = 0 }];
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var station = services.GetRequiredService<BoltFasteningStation>();
        var work = services.GetRequiredService<BoltFasteningStation>().Station;
        var gantry = services.GetRequiredService<BoltFasteningStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var previousAssembly = work.GetAssembly(HeatSinkSlot.HeatSink1);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var replaced = false;
        void ReplaceAfterResult(HeatSinkAssembly assembly)
        {
            if (replaced || !assembly.ShootingBoltResults.ContainsKey(VirtualTestSupport.BoltId(1)))
                return;
            replaced = true;
            VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, false);
            VirtualTestSupport.SetCarrier(io, InputIo.BoltFasteningHeatSink1Present, true);
            stop.Cancel();
        }

        previousAssembly.ResultsChanged += ReplaceAfterResult;
        try
        {
            await station.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(replaced);
            Assert.True(previousAssembly.ShootingBoltResults[VirtualTestSupport.BoltId(1)].Success);
            Assert.Same(previousAssembly, Assert.Single(work.Assemblies));
            Assert.False(work.Completed);
            Assert.False(gantry.Motion.Feedback.IsMoving);
        }
        finally
        {
            previousAssembly.ResultsChanged -= ReplaceAfterResult;
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplyDoesNotAdvanceTheNewCarrierWhenAnOldPickupFinishes(bool testSignal)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supply = services.GetRequiredService<PcbSupplier>();
        var handler = services.GetRequiredService<PcbSupplier>();
        var motion = handler.Motion.Feedback;
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 10, Z = 5 },
        };
        services.GetRequiredService<RecipeManager>().Current.PcbSupply = recipe;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.PcbSupplyRotated, true);
        io.SetInput(InputIo.PcbSupplyUnrotated, false);
        io.SetInput(InputIo.PcbSupplyPcbDetected, false);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
        io.SetInput(InputIo.AutoMode, testSignal);
        void SetCarrierAvailable(bool available)
        {
            if (testSignal)
                handler.TestUpstreamCarrierAvailable = available;
            else
                io.SetInput(InputIo.PcbSupplyAvailableFromFront1, available);
        }
        SetCarrierAvailable(true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var firstSlotVisits = 0;
        var atFirstSlot = false;
        var skippedFirstSlot = false;
        void ChangeCarrierAtPickup(double x, double y, double z)
        {
            var atPickup = Math.Abs(x - 10) < 0.01
                && Math.Abs(y - recipe.Pcb1PickPosition.Y!.Value) < 0.01
                && Math.Abs(z - 5) < 0.01;
            if (atPickup && !atFirstSlot)
            {
                firstSlotVisits++;
                if (firstSlotVisits == 1)
                {
                    SetCarrierAvailable(false);
                    SetCarrierAvailable(true);
                }
                else
                    stop.Cancel();
            }
            atFirstSlot = atPickup;
            if (firstSlotVisits == 1 && x > 15)
            {
                skippedFirstSlot = true;
                stop.Cancel();
            }
        }

        motion.PositionChanged += ChangeCarrierAtPickup;
        try
        {
            await supply.RunAsync(services.GetRequiredService<PcbPlacer>(), stop.Token).WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(skippedFirstSlot);
            Assert.Equal(2, firstSlotVisits);
            Assert.False(motion.IsMoving);
            Assert.Equal(!testSignal, io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
        }
        finally
        {
            motion.PositionChanged -= ChangeCarrierAtPickup;
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task NgPickupMovesXyTogetherAndResumesBeforeGripping()
    {
        await using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var move = services.GetRequiredService<InspectionStation>();
        var settings = services.GetRequiredService<NgCarrierTransferSettings>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 50, Y = 60 }, 10_000);

        settings.CarrierPickupPosition = null;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => move.ExecuteTransferAsync(InspectionStationState.PickingCarrier, CancellationToken.None)!);
        Assert.Empty(feedback.AxisMoves);
        Assert.Equal((50, 60, 0), gantry.Motion.Feedback.Position);

        settings.CarrierPickupPosition = new() { X = 5, Y = 20 };
        services.GetRequiredService<InspectionGantrySettings>().Motion.HorizontalSpeed = 100;
        settings.ShuttlePlacePosition = new() { X = 150, Y = 80 };
        var pickupPosition = settings.CarrierPickupPosition!;
        using var cancellation = new CancellationTokenSource();
        void StopDuringPickupMove(double x, double y, double z)
        {
            if (x > pickupPosition.X && x < 49
                && y > pickupPosition.Y && y < 59)
                cancellation.Cancel();
        }
        gantry.Motion.Feedback.PositionChanged += StopDuringPickupMove;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => move.ExecuteTransferAsync(InspectionStationState.PickingCarrier, cancellation.Token)!);
        gantry.Motion.Feedback.PositionChanged -= StopDuringPickupMove;
        Assert.Empty(feedback.AxisMoves);
        var stopped = gantry.Motion.Feedback.Position;
        Assert.InRange(stopped.X, pickupPosition.X + 0.01, 49.99);
        Assert.InRange(stopped.Y, pickupPosition.Y + 0.01, 59.99);
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.False(io.GetInput(InputIo.NgCarrierDetected));

        feedback.AxisMoves.Clear();
        Assert.False(await move.ExecuteTransferAsync(InspectionStationState.PickingCarrier, CancellationToken.None));
        Assert.Empty(feedback.AxisMoves);
        Assert.True(VirtualTestSupport.IsAt(gantry.Motion.Feedback, pickupPosition));
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.Equal(settings.CarrierPickupPosition!.X, gantry.Motion.Feedback.Position.X);

        VirtualTestSupport.SetCarrier(io, InputIo.InspectionHeatSink1Present, true);
        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        Assert.False(move.IsTransferPending);
        await move.ExecuteTransferAsync(InspectionStationState.PickingCarrier, CancellationToken.None)!;
        Assert.Equal(InspectionStationState.PlacingCarrier, move.NextStep);
        Assert.True(io.GetInput(InputIo.NgCarrierPickupUp));
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        Assert.Equal(5, gantry.Motion.Feedback.Position.X);
        Assert.Empty(feedback.AxisMoves);

        feedback.AxisMoves.Clear();
        var axesMovedTogether = false;
        void ObserveShuttleMove(double x, double y, double z)
        {
            axesMovedTogether |= x > pickupPosition.X && x < settings.ShuttlePlacePosition.X
                && y > pickupPosition.Y && y < settings.ShuttlePlacePosition.Y;
        }
        gantry.Motion.Feedback.PositionChanged += ObserveShuttleMove;
        try
        {
            await move.ExecuteTransferAsync(InspectionStationState.PlacingCarrier, CancellationToken.None)!;
        }
        finally
        {
            gantry.Motion.Feedback.PositionChanged -= ObserveShuttleMove;
        }
        Assert.True(axesMovedTogether);
        Assert.Empty(feedback.AxisMoves);
        Assert.True(VirtualTestSupport.IsAt(gantry.Motion.Feedback, settings.ShuttlePlacePosition));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NgTransferReleasesOnlyAfterMoveAndSupportedDescent(
        bool loseSupport)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.NgConveyor = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var transfer = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await transfer.Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await transfer.ExecuteTransferAsync(InspectionStationState.PickingCarrier, stop.Token);
        var target = settings.NgCarrierTransfer.ShuttlePlacePosition;
        var lowered = false;
        var opened = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgCarrierPickupDown && on)
            {
                Assert.Equal((target.X, target.Y),
                    (transfer.Motion.Feedback.Position.X, transfer.Motion.Feedback.Position.Y));
                Assert.False(transfer.Motion.Feedback.IsMoving);
                Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);
                lowered = true;
                if (loseSupport)
                    io.SetInput(InputIo.NgShuttleUp, false);
            }
            if (output == OutputIo.NgCarrierGripperClose && !on)
            {
                Assert.True(lowered);
                Assert.Equal(StationCylinderState.Down, transfer.Lift);
                Assert.True(io.GetInput(InputIo.NgShuttleCarrierDetected));
                opened = true;
            }
        };
        try
        {
            var placing = transfer.ExecuteTransferAsync( InspectionStationState.PlacingCarrier, stop.Token);
            if (loseSupport)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => placing);
                Assert.True(lowered);
                Assert.False(opened);
                Assert.True(transfer.IsTransferPending);
                Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);
            }
            else
            {
                await placing;
                Assert.True(opened);
                Assert.False(transfer.IsTransferPending);
                Assert.Equal(NgTransferGripperState.Open, transfer.Gripper);
                Assert.True(transfer.IsRaised);
            }
        }
        finally
        {
            await machine.ShutdownAsync();
        }
    }
    private sealed class StoppingBoltHead : IBoltHead
    {
        public StoppingBoltHead()
        {
            Started = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Stopping = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Stopped = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TaskCompletionSource Started { get; }
        public TaskCompletionSource Stopping { get; }
        public TaskCompletionSource Stopped { get; }

        public AdcStatusMonitor? Monitor => null;

        public Task<ushort> ReadTorqueCompensationAsync(ushort preset, CancellationToken cancellationToken = default)
        {
            return Task.FromResult((ushort)100);
        }

        public Task CheckReadyAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public async Task<BoltResult> TightenAsync(
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? feedAsync = null,
            int dryRunMilliseconds = 0,
            Action<BoltResult>? resultReceived = null,
            ushort? torqueCompensationPercent = null,
            int feedDelayMilliseconds = 0)
        {
            Started.SetResult();
            try
            {
                if (feedAsync is not null)
                    await feedAsync(cancellationToken);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new(true, 1);
            }
            finally
            {
                Stopping.SetResult();
                await Stopped.Task;
            }
        }
    }

    private sealed class WaitingBoltHead : IBoltHead
    {
        public WaitingBoltHead()
        {
            ReadinessEntered = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ReadinessReleased = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public bool WaitForReadiness { get; set; }
        public int ReadinessChecks { get; private set; }
        public TaskCompletionSource ReadinessEntered { get; }
        public TaskCompletionSource ReadinessReleased { get; }

        public AdcStatusMonitor? Monitor => null;

        public Task<ushort> ReadTorqueCompensationAsync(ushort preset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult((ushort)100);
        }

        public Task CheckReadyAsync(CancellationToken cancellationToken = default)
        {
            ReadinessChecks++;
            if (!WaitForReadiness)
            {
                return Task.CompletedTask;
            }

            ReadinessEntered.TrySetResult();
            return ReadinessReleased.Task.WaitAsync(cancellationToken);
        }

        public Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ResetAsync(CancellationToken cancellationToken = default)
        {
            return CheckReadyAsync(cancellationToken);
        }

        public Task<BoltResult> TightenAsync(
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? feedAsync = null,
            int dryRunMilliseconds = 0,
            Action<BoltResult>? resultReceived = null,
            ushort? torqueCompensationPercent = null,
            int feedDelayMilliseconds = 0)
        {
            throw new NotSupportedException();
        }

}
}
