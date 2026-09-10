using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Core;
using IBTM.Conveyor;
using IBTM.Device;
using IBTM.Inspection;
using IBTM.Inspection.Training;
using IBTM.NgConveyor;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.UI;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Trait("Category", "MachineFlow")]
    [Fact]
    public async Task BoltDryRunResumesCylinderAndPickupZStrokesWithoutVacuum()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.BoltFastening.Motion.ZSpeed = 30;
        using var services = CreateServices(settings);
        services.GetRequiredService<Recipe>().Pcb.BoltPoints =
            [new() { Number = 1, Head = FasteningHead.Pickup, X = 10, Y = 10 }];
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var route = services.GetRequiredService<BoltRouteDryRun>();
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        manual.SelectedDryRun = DryRunTarget.BoltRoute;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        await services.GetRequiredService<BoltFasteningWork>().Station.SeatAsync(CancellationToken.None);

        var stops = new Queue<BoltRouteState>([
            BoltRouteState.LoweringForPickup, BoltRouteState.MovingToPickupZ,
            BoltRouteState.MovingToSafeZ, BoltRouteState.LoweringAtPoint, BoltRouteState.RaisingHeads]);
        void InterruptStroke()
        {
            if (!stops.TryPeek(out var next) || route.State != next) return;
            if (next is BoltRouteState.MovingToPickupZ or BoltRouteState.MovingToSafeZ
                && gantry.Feedback.GetPosition().Z is not (> 1 and < 9)) return;
            stops.Dequeue();
            manual.RunDryRunCommand.Cancel();
        }
        var adcFrames = 0;
        services.GetRequiredService<IAdcBus>().FrameTransferred += (_, _) => adcFrames++;
        io.OutputChanged += (output, on) =>
        {
            if (output != OutputIo.PickupHeadDown) return;
            if (!on) Assert.Equal(settings.BoltFastening.SafeZ, gantry.Feedback.GetPosition().Z, 2);
            InterruptStroke();
        };
        gantry.Feedback.PositionChanged += (_, _, _) => InterruptStroke();
        gantry.Feedback.MovingChanged += _ =>
        {
            if (gantry.Feedback.IsMovingHorizontal) Assert.True(gantry.CanMoveHorizontal);
        };
        while (stops.Count > 0)
        {
            var count = stops.Count;
            await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
            await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(count - 1, stops.Count);
            Assert.False(gantry.Feedback.IsMoving);
            Assert.Equal(0, route.CompletedPasses);
        }
        route.Changed += () => { if (route.CompletedPasses == 2) manual.RunDryRunCommand.Cancel(); };
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(2, route.CompletedPasses);
        Assert.True(gantry.AtSafeZ);
        Assert.True(gantry.CanMoveHorizontal);
        Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.False(io.GetInput(InputIo.PickupHeadVacuumDetected));
        Assert.Equal(0, adcFrames);
        Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
        await machine.ShutdownAsync();
    }

    [Trait("Category", "MachineFlow")]
    [Fact]
    public async Task NgConveyorRoundTripLowersShuttleBeforeReverseAndResumesBetweenSensors()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgConveyor);
        settings.Units.NgShuttle = true;
        settings.Units.NgCarrierTransfer = true;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var route = services.GetRequiredService<NgConveyorDryRun>();
        var conveyor = services.GetRequiredService<NgCarrierConveyor>();
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        manual.SelectedDryRun = DryRunTarget.NgConveyor;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        var stopFirstReverse = true;
        var lowerPickup = false;
        var unsafeTravel = false;
        var raisedWhileRunning = false;
        var stopAtPass = 2;
        route.Changed += () => { if (route.CompletedPasses == stopAtPass) machine.Stop(); };
        io.OutputChanged += (output, value) =>
        {
            raisedWhileRunning |= output == OutputIo.NgShuttleDown && !value && conveyor.RunCommandOn;
            if (output != OutputIo.NgConveyorRun || !value) return;
            unsafeTravel |= !io.GetInput(InputIo.NgShuttleDown) || io.GetInput(InputIo.NgShuttleUp);
            if (stopFirstReverse && io.GetOutput(OutputIo.NgConveyorReverse))
            {
                stopFirstReverse = false;
                machine.Stop();
            }
            if (lowerPickup) io.SetInput(InputIo.NgCarrierPickupUp, false);
        };
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(1, route.CompletedPasses);
        Assert.Equal(NgConveyorDestination.Shuttle, route.Destination);
        Assert.False(conveyor.RunCommandOn);

        // Resume a stopped carrier between P1 and P3: no input is currently ON.
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.NgConveyorPosition1Occupied, false);
        Assert.Equal(0, conveyor.CarrierCount);
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        var resumed = manual.RunDryRunCommand.ExecuteAsync(null);
        try
        {
            await VirtualTest.WaitForOutputAsync(io, OutputIo.NgConveyorRun, true);
            Assert.True(io.GetOutput(OutputIo.NgConveyorReverse));
            io.SetInput(InputIo.NgShuttleCarrierDetected, true);
            await VirtualTest.WaitForOutputAsync(io, OutputIo.NgConveyorRun, false);
            await VirtualTest.WaitForOutputAsync(io, OutputIo.NgShuttleDown, false);
            io.SetInput(InputIo.NgShuttleDown, false);
            io.SetInput(InputIo.NgShuttleUp, true);
            await resumed.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { machine.Stop(); await resumed; }
        Assert.Equal(2, route.CompletedPasses);
        Assert.True(io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.True(io.GetInput(InputIo.NgShuttleUp));

        io.AutoResponseEnabled = true;
        stopAtPass = 4;
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(4, route.CompletedPasses);
        Assert.True(io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.True(io.GetInput(InputIo.NgShuttleUp));
        Assert.False(unsafeTravel);
        Assert.False(raisedWhileRunning);
        Assert.False(state.IsError, state.AlarmDetail);

        stopAtPass = -1;
        lowerPickup = true;
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(conveyor.RunCommandOn);
        Assert.Equal(NgConveyorDryRunState.WaitingForPickup, route.State);
        await machine.ShutdownAsync();
    }

    [Trait("Category", "MachineFlow")]
    [Fact]
    public async Task PlacementOpensAndRaisesIpmBeforePressingAndResumesWithoutReopening()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.PcbSupply = true;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var handler = services.GetRequiredService<PcbPlacementHandler>();
        var placer = services.GetRequiredService<PcbPlacer>();
        var work = services.GetRequiredService<PcbPlacementWork>();
        var recipe = services.GetRequiredService<Recipe>().PcbPlacement;
        recipe.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 10 };
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await handler.MoveToXYAsync(20, 100);
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.PcbPlacementIpmGripperClose, true);
        io.SetOutput(OutputIo.PcbPlacementIpmDown, true);
        foreach (var input in new[] { InputIo.PcbPlacementCarrierPresent,
            InputIo.PcbPlacementBackupPlateUp, InputIo.PcbPlacementStopperDown,
            InputIo.PcbPlacementHeatSink1Present, InputIo.PcbPlacementPcbDetected,
            InputIo.PcbPlacementIpmGripperClosed, InputIo.PcbPlacementHandlerRotated,
            InputIo.PcbPlacementIpmDown }) io.SetInput(input, true);
        foreach (var input in new[] { InputIo.PcbPlacementBackupPlateDown,
            InputIo.PcbPlacementStopperUp, InputIo.PcbPlacementIpmGripperOpen,
            InputIo.PcbPlacementHandlerUnrotated, InputIo.PcbPlacementIpmUp }) io.SetInput(input, false);

        // XY alone is not a placement position, even with PCB detection and vacuum OFF.
        Assert.DoesNotContain(placer.State(recipe), new[] { PcbPlacementState.PressingPcb, PcbPlacementState.RecordingPlacement });
        await handler.MoveZAsync(10);
        Assert.DoesNotContain(placer.State(recipe), new[] { PcbPlacementState.PressingPcb, PcbPlacementState.RecordingPlacement });
        io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        io.SetInput(InputIo.PcbPlacementHandlerDown, true);

        var outputs = new List<(OutputIo, bool)>();
        using var closing = new CancellationTokenSource();
        using var pressing = new CancellationTokenSource();
        io.OutputChanged += (output, on) =>
        {
            if (output is not (OutputIo.PcbPlacementIpmDown or OutputIo.PcbPlacementIpmGripperClose)) return;
            outputs.Add((output, on));
            var feedback = io.GetOutputFeedback(output)!;
            io.SetInput(on ? feedback.OffInput : feedback.OnInput, false);
            if (output == OutputIo.PcbPlacementIpmDown && on)
            {
                pressing.Cancel(); // Stop between Up and Down feedback.
                return;
            }
            io.SetInput(on ? feedback.OnInput : feedback.OffInput, true);
            if (output == OutputIo.PcbPlacementIpmGripperClose && on) closing.Cancel();
        };

        Assert.Equal(PcbPlacementState.OpeningGripper, placer.State(recipe));
        await placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, CancellationToken.None)!;
        Assert.Equal(PcbPlacementState.RaisingIpm, placer.State(recipe));
        await placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, CancellationToken.None)!;
        Assert.Equal(PcbPlacementState.PressingPcb, placer.State(recipe));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, closing.Token)!);
        Assert.Equal(PlacementGripperState.Closed, handler.IpmGripper);
        Assert.Equal(PcbPlacementState.PressingPcb, placer.State(recipe));
        Assert.Empty(work.Assemblies);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, pressing.Token)!);
        Assert.Equal(PlacementCylinderState.Between, handler.IpmLift);
        Assert.Equal(PcbPlacementState.PressingPcb, placer.State(recipe));
        Assert.Empty(work.Assemblies);
        io.SetInput(InputIo.PcbPlacementIpmDown, true);
        Assert.Equal(PcbPlacementState.RecordingPlacement, placer.State(recipe));
        await placer.PlaceStepAsync(recipe, HeatSinkSlot.HeatSink1, CancellationToken.None)!;
        Assert.Single(work.Assemblies);
        Assert.Equal(new[] { (OutputIo.PcbPlacementIpmGripperClose, false),
            (OutputIo.PcbPlacementIpmDown, false), (OutputIo.PcbPlacementIpmGripperClose, true),
            (OutputIo.PcbPlacementIpmDown, true) }, outputs);
        await machine.ShutdownAsync();
    }

    [Trait("Category", "MachineFlow")]
    [Theory]
    [InlineData(HeatSinkSlot.HeatSink2)]
    public async Task PcbReturnReversesHandoffAndResumesWithoutReleasingTheReceivingHandler(HeatSinkSlot heatSink)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.PcbSupply = true;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        var supply = services.GetRequiredService<PcbSupplyHandler>();
        var buffer = services.GetRequiredService<BufferStage>();
        var work = services.GetRequiredService<PcbPlacementWork>();
        var recipe = services.GetRequiredService<Recipe>();
        recipe.PcbPlacement.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 10 };
        recipe.PcbPlacement.HeatSink2PcbPlacementPosition = new() { X = 40, Y = 100, Z = 10 };
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        var returning = services.GetRequiredService<PcbReturn>();
        manual.SelectedDryRun = DryRunTarget.PcbReturn;
        manual.SelectedDryRunHeatSink = heatSink;
        await machine.InitializeAsync();
        Assert.False(manual.RunDryRunCommand.CanExecute(null));
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PcbPlacementCarrierPresent, true);
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, heatSink == HeatSinkSlot.HeatSink1);
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, heatSink == HeatSinkSlot.HeatSink2);
        await work.Station.SeatAsync(CancellationToken.None);

        // Place one real simulated PCB through the existing forward operation first.
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        await placement.SetVacuumAsync(true);
        await placement.SetIpmGripperAsync(true);
        using var placed = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        work.Changed += () => { if (work.Completed) placed.Cancel(); };
        await services.GetRequiredService<PcbPlacer>().RunAsync(recipe.PcbPlacement, placed.Token);
        Assert.True(work.Completed);
        Assert.Equal(PlacementPcbState.None, placement.Pcb);
        Assert.Equal(PcbSupplyPcbState.None, supply.Pcb);
        if (heatSink == HeatSinkSlot.HeatSink2)
        {
            await placement.MoveToXYAsync(20, 100);
            await placement.SetRotatedAsync(false);
            await placement.MoveToXYAsync(0, 0);
        }

        settings.PcbSupply.Motion.HorizontalSpeed = 200;
        settings.PcbSupply.Motion.ZSpeed = 100;
        var stopped = 0;
        var movedBeforeRelease = false;
        var movedWithHeadDown = false;
        var carriedWithIpmRaised = false;
        var changedYInside = false;
        var releasedBeforeSupplySecured = false;
        var enteredWithOpenDown = false;
        var rotatedAwayFromTeaching = false;
        var stoppedAtPickup = false;
        io.OutputChanged += (output, _) =>
        {
            if (output == OutputIo.PcbPlacementHandlerRotate)
                rotatedAwayFromTeaching |= !placement.IsAtXY(recipe.PcbPlacement.HeatSink1PcbPlacementPosition)
                    || !placement.AtHorizontalZ || !placement.CanMoveHorizontal;
        };
        placement.Feedback.PositionChanged += (x, y, z) =>
        {
            movedWithHeadDown |= placement.Feedback.IsMovingHorizontal && !placement.CanMoveHorizontal;
            carriedWithIpmRaised |= placement.Feedback.IsMovingHorizontal
                && placement.Pcb == PlacementPcbState.Secured
                && placement.IpmLift != PlacementCylinderState.Down;
            var target = heatSink == HeatSinkSlot.HeatSink1
                ? recipe.PcbPlacement.HeatSink1PcbPlacementPosition : recipe.PcbPlacement.HeatSink2PcbPlacementPosition;
            if (returning.Destination == PcbReturnDestination.HeatSink
                && Math.Abs(x - target.X) < 0.05 && Math.Abs(y - target.Y) < 0.05
                && z > 0)
                enteredWithOpenDown |= placement.IpmGripper == PlacementGripperState.Open
                    && placement.IpmLift == PlacementCylinderState.Down;
        };
        supply.Feedback.PositionChanged += (x, y, z) =>
        {
            if (returning.Destination != PcbReturnDestination.Supply) return;
            changedYInside |= x >= 60 && Math.Abs(y - 30) > 0.05;
            movedBeforeRelease |= supply.Pcb == PcbSupplyPcbState.Secured
                && (!placement.AtHorizontalZ || !placement.CanMoveHorizontal);
            if (stopped == 0 && x > 65 && Math.Abs(z - 20) < 0.05
                || stopped == 1 && Math.Abs(x - 80) < 0.05 && z is > 11 and < 18)
            {
                stopped++;
                machine.Stop();
            }
        };
        io.InputChanged += (input, value) =>
        {
            if (!stoppedAtPickup && input == InputIo.PcbPlacementVacuumDetected && value
                && returning.Destination == PcbReturnDestination.HeatSink)
            {
                stoppedAtPickup = true;
                machine.Stop();
            }
            if (input != InputIo.PcbPlacementVacuumDetected || value
                || returning.Destination != PcbReturnDestination.Supply) return;
            releasedBeforeSupplySecured |= supply.Pcb != PcbSupplyPcbState.Secured;
            if (stopped == 2) { stopped++; machine.Stop(); }
        };

        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(stoppedAtPickup);
        Assert.True(placement.VacuumDetected);
        Assert.NotEqual(PlacementPcbState.Secured, placement.Pcb);
        await WaitUntilAsync(() => manual.DryRunPcb == heatSink);
        manual.SelectedDryRunHeatSink = heatSink == HeatSinkSlot.HeatSink1
            ? HeatSinkSlot.HeatSink2 : HeatSinkSlot.HeatSink1;
        Assert.Equal(heatSink, manual.DryRunPcb);

        for (var pass = 0; pass < 4; pass++)
        {
            await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
            var run = manual.RunDryRunCommand.ExecuteAsync(null);
            try { await run.WaitAsync(TimeSpan.FromSeconds(8)); }
            finally { machine.Stop(); await run; }
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.False(supply.Feedback.IsMoving);
            Assert.False(placement.Feedback.IsMoving);
            Assert.False(buffer.Conflict);
            if (pass < 3) Assert.Equal(pass + 1, stopped);
        }

        Assert.Equal(PcbReturnState.Completed, returning.State);
        Assert.Equal(1, returning.CompletedReturns);
        Assert.Null(returning.HeatSink);
        Assert.Equal(PcbSupplyPcbState.Secured, supply.Pcb);
        Assert.Equal(PcbSupplyRotationState.Unrotated, supply.Rotation);
        Assert.Equal((0, settings.PcbSupply.CarrierY, settings.PcbSupply.RotationZ), supply.Feedback.GetPosition());
        Assert.Equal(PlacementPcbState.None, placement.Pcb);
        Assert.False(buffer.PcbPresent);
        Assert.False(work.Completed);
        Assert.Empty(work.Assemblies);
        Assert.True(enteredWithOpenDown);
        Assert.False(movedBeforeRelease);
        Assert.False(movedWithHeadDown);
        Assert.False(carriedWithIpmRaised);
        Assert.False(changedYInside);
        Assert.False(releasedBeforeSupplySecured);
        Assert.False(rotatedAwayFromTeaching);
        Assert.False(io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
        await machine.ShutdownAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainConveyorDryRunUsesManualCommandsAndStopsIfAnEnabledHeadLowers(bool transferEnabled)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.NgCarrierTransfer = transferEnabled;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        var dryRun = services.GetRequiredService<MainConveyorDryRun>();
        manual.SelectedDryRun = DryRunTarget.MainConveyor;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await services.GetRequiredService<InspectionWork>().Station.SeatAsync(CancellationToken.None);
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        var stopAfter = 2;
        dryRun.Changed += () => { if (dryRun.CompletedPasses == stopAfter) manual.RunDryRunCommand.Cancel(); };
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(2, dryRun.CompletedPasses);
        Assert.True(io.GetInput(InputIo.MainConveyorEntryCarrierDetected));
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(state.IsError);

        // A second start continues forward from the front sensor, not back to Station 3.
        stopAfter = -1;
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        io.OutputChanged += (output, value) =>
        {
            if (output != OutputIo.MainConveyorRun || !value) return;
            if (transferEnabled) io.SetInput(InputIo.NgCarrierPickupUp, false);
            else machine.Stop();
        };
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        Assert.False(io.GetOutput(OutputIo.MainConveyorReverse));
        Assert.Equal(MainConveyorDestination.Station1, dryRun.Destination);
        Assert.False(state.IsError);
        if (transferEnabled)
        {
            await WaitUntilAsync(() => !manual.RunDryRunCommand.CanExecute(null));
            Assert.Equal(MainConveyorDryRunState.Unavailable, manual.DryRunState);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InspectionDryRunReportsMissingTeachingAndUnreadableBarcodes(bool missingTeaching)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var camera = services.GetRequiredService<VirtualCamera>();
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        manual.SelectedDryRun = DryRunTarget.Inspection;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        if (missingTeaching)
            services.GetRequiredService<Recipe>().Pcb.DataMatrix = null;
        else
        {
            var frame = camera.Capture(500, 0);
            camera.SourceImage = frame with { Pixels = new byte[frame.Pixels.Length] };
        }
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(MachineAlarm.Inspection, state.Alarm);
        Assert.Contains(missingTeaching ? "Teach" : "Data Matrix could not be read", state.AlarmMessage);
        Assert.False(services.GetRequiredService<InspectionGantry>().Feedback.IsMoving);
        Assert.True(work.CarrierSeated);
        Assert.False(work.Completed);
        Assert.All(work.Assemblies, assembly =>
        {
            Assert.Null(assembly.PcbBarcode);
            Assert.Empty(assembly.BoltPresenceResults);
        });
        await machine.ShutdownAsync();
    }

    [Theory]
    [InlineData(NgTransferDestination.Station)]
    [InlineData(NgTransferDestination.Shuttle)]
    public async Task NgTransferUsesTheSameLiveReleaseStatesInBothDirections(NgTransferDestination destination)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var move = services.GetRequiredService<NgCarrierMove>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await services.GetRequiredService<InspectionWork>().Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        await services.GetRequiredService<InspectionGantry>().MoveToAsync(destination == NgTransferDestination.Station
            ? settings.NgCarrierTransfer.CarrierPickupPosition : settings.NgCarrierTransfer.ShuttlePlacePosition, 1_000);
        io.AutoResponseEnabled = false;
        var destinationSensor = destination == NgTransferDestination.Station
            ? InputIo.InspectionCarrierPresent : InputIo.NgShuttleCarrierDetected;
        io.SetInput(InputIo.NgCarrierDetected, true);
        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, true);
        Assert.Equal(NgTransferState.WaitingForDestination,
            move.State(destination, canPickUp: true, canReceive: false));
        io.SetInput(destinationSensor, true);
        AssertState(NgTransferState.WaitingForDestination);

        // The descending held carrier can enter the support sensor before Down.
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        AssertState(NgTransferState.LoweringAtDestination);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        AssertState(NgTransferState.Opening);
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        AssertState(NgTransferState.Opening);
        io.SetInput(InputIo.NgCarrierGripperOpen, true);
        io.SetInput(destinationSensor, false);
        AssertState(NgTransferState.WaitingForPlacement);
        io.SetInput(destinationSensor, true);
        AssertState(NgTransferState.Raising);
        io.SetInput(InputIo.NgCarrierPickupDown, false);
        io.SetInput(InputIo.NgCarrierPickupUp, true);
        AssertState(NgTransferState.Completed);
        await machine.ShutdownAsync();

        void AssertState(NgTransferState expected)
        {
            Assert.Equal(expected, move.State(destination, canPickUp: false));
            Assert.Equal(expected, move.State(destination, canPickUp: true));
        }
    }

}
