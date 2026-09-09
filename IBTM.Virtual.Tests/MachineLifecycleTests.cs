using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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

public sealed class MachineLifecycleTests
{
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

    [Theory]
    [InlineData(HeatSinkLoad.HeatSink1)]
    [InlineData(HeatSinkLoad.HeatSink2)]
    [InlineData(HeatSinkLoad.Both)]
    public async Task BoltRouteVisitsBothHeadsAndPassesWithoutDrivingBolts(HeatSinkLoad load)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.BoltFastening.PickupHead.UpperLeftLocatingPin = new() { X = 50, Y = 5 };
        settings.BoltFastening.PickupHead.LowerRightLocatingPin = new() { X = 150, Y = 5 };
        using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<Recipe>();
        recipe.Pcb.BoltPoints =
        [
            new() { Number = 1, Head = FasteningHead.Pickup, X = 10, Y = 10 },
            new() { Number = 2, Head = FasteningHead.Shooting, X = 5, Y = 5 },
        ];
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var work = services.GetRequiredService<BoltFasteningWork>();
        var route = services.GetRequiredService<BoltRouteDryRun>();
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        manual.SelectedDryRun = DryRunTarget.BoltRoute;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.False(manual.RunDryRunCommand.CanExecute(null));
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, load.HasFlag(HeatSinkLoad.HeatSink1));
        io.SetInput(InputIo.BoltFasteningHeatSink2Present, load.HasFlag(HeatSinkLoad.HeatSink2));
        Assert.False(manual.RunDryRunCommand.CanExecute(null));
        await work.Station.SeatAsync(CancellationToken.None);
        var production = work.Assembly(load == HeatSinkLoad.HeatSink2 ? HeatSinkSlot.HeatSink2 : HeatSinkSlot.HeatSink1);
        production.RecordPcbBolt(2, new(false, 3, BoltResultSource.Controller));
        // A lowered head is raised before any XY movement, not treated as a completed point.
        await gantry.GetTeachingOutputs().Single(output => output.Signal == OutputIo.PickupHeadDown)
            .SetAsync(true, CancellationToken.None);

        // No bolt is available. Dry run must not wait on either feeder or shooting feedback.
        io.SetInput(InputIo.PickupHeadVacuumDetected, false);
        io.SetInput(InputIo.ShootingHeadVacuumDetected, false);
        io.SetInput(InputIo.ShootingTubeBoltDetected, true);
        io.SetInput(InputIo.ShootingEscapeBackward, false);

        var adcFrames = 0;
        services.GetRequiredService<IAdcBus>().FrameTransferred += (_, _) => adcFrames++;
        var outputs = new List<OutputIo>();
        io.OutputChanged += (output, on) => { if (on) outputs.Add(output); };
        var visited = new List<(HeatSinkSlot HeatSink, int Number, FasteningPass Pass)>();
        var pickups = new List<(HeatSinkSlot HeatSink, BoltRouteDirection Direction)>();
        var stopAfter = 2;
        route.Changed += () =>
        {
            if (route.State == BoltRouteState.AtPoint && route.ActiveBolt is { } bolt)
            {
                var target = (bolt.HeatSink, bolt.Number, route.ActivePass!.Value);
                if (visited.Count == 0 || visited[^1] != target) visited.Add(target);
                var expected = settings.BoltFastening.GetBoltPosition(bolt, settings.CarrierReference);
                var actual = gantry.Feedback.GetPosition();
                Assert.Equal(expected.X, actual.X, 2);
                Assert.Equal(expected.Y, actual.Y, 2);
                Assert.Equal(settings.BoltFastening.SafeZ, actual.Z, 2);
            }
            if (route.State == BoltRouteState.AtPickup)
            {
                Assert.Equal(FasteningPass.IpmSeating, route.ActivePass);
                var pickup = (route.ActiveBolt!.HeatSink, route.Direction);
                if (pickups.Count == 0 || pickups[^1] != pickup) pickups.Add(pickup);
                Assert.Equal(BoltCylinderState.Down, gantry.PickupHeadPosition);
                var actual = gantry.Feedback.GetPosition();
                Assert.Equal(settings.BoltFastening.PickupPosition.X, actual.X, 2);
                Assert.Equal(settings.BoltFastening.PickupPosition.Y, actual.Y, 2);
                Assert.Equal(settings.BoltFastening.PickupPosition.Z, actual.Z, 2);
            }
            if (route.CompletedPasses == stopAfter) manual.RunDryRunCommand.Cancel();
        };
        gantry.Feedback.MovingChanged += moving =>
        {
            if (gantry.Feedback.IsMovingHorizontal) Assert.True(gantry.CanMoveHorizontal);
            else if (moving)
            {
                // Every Z stroke is at the pickup, with Head 1 down. Never at a bolt point.
                var actual = gantry.Feedback.GetPosition();
                Assert.Equal(settings.BoltFastening.PickupPosition.X, actual.X, 2);
                Assert.Equal(settings.BoltFastening.PickupPosition.Y, actual.Y, 2);
                Assert.Equal(BoltCylinderState.Down, gantry.PickupHeadPosition);
            }
        };
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));

        var slots = Enum.GetValues<HeatSinkSlot>().Where(work.HeatSinkPresent).ToArray();
        var forward = slots.Select(slot => (slot, 2, FasteningPass.Pcb))
            .Concat(slots.Select(slot => (slot, 1, FasteningPass.IpmSeating)))
            .Concat(slots.Select(slot => (slot, 1, FasteningPass.IpmFinal))).ToArray();
        Assert.Equal(forward.Concat(forward.Reverse().Skip(1)), visited);
        Assert.Equal(slots.Select(slot => (slot, BoltRouteDirection.Forward))
            .Concat(slots.Reverse().Select(slot => (slot, BoltRouteDirection.Return))), pickups);
        Assert.Equal(2, route.CompletedPasses);
        Assert.Equal(0, adcFrames);
        Assert.DoesNotContain(OutputIo.ShootBolt, outputs);
        Assert.DoesNotContain(OutputIo.PickupHeadVacuumPump, outputs);
        Assert.DoesNotContain(OutputIo.ShootingHeadVacuumPump, outputs);
        Assert.DoesNotContain(OutputIo.ShootingEscapeForward, outputs);
        Assert.Equal(visited.Count(point => point.Pass != FasteningPass.Pcb) + pickups.Count,
            outputs.Count(output => output == OutputIo.PickupHeadDown));
        Assert.Equal(visited.Count(point => point.Pass == FasteningPass.Pcb),
            outputs.Count(output => output == OutputIo.ShootingHeadDown));
        Assert.True(gantry.CanMoveHorizontal);
        Assert.False(work.Completed);
        Assert.True(work.CarrierSeated);
        Assert.False(Assert.Single(production.PcbBoltResults).Value.Success);
        Assert.Empty(production.IpmSeatingResults);
        Assert.Empty(production.IpmFinalResults);

        // Cancel mid-XY, then resume the same pending point without running any ADC command.
        settings.BoltFastening.Motion.HorizontalSpeed = 30;
        stopAfter = 4;
        var interrupted = false;
        gantry.Feedback.PositionChanged += (_, _, _) =>
        {
            if (interrupted || !gantry.Feedback.IsMoving) return;
            interrupted = true;
            manual.RunDryRunCommand.Cancel();
        };
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(interrupted);
        Assert.Equal(2, route.CompletedPasses);
        Assert.False(gantry.Feedback.IsMoving);
        var pending = route.ActiveBolt;
        settings.BoltFastening.Motion.HorizontalSpeed = 10_000;
        visited.Clear();
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((pending!.HeatSink, pending.Number), (visited[0].HeatSink, visited[0].Number));
        Assert.Equal(4, route.CompletedPasses);
        Assert.Equal(0, adcFrames);
        Assert.Equal(MachineAlarm.None, state.Alarm);

        stopAfter = 6;
        var seatLost = false;
        gantry.Feedback.PositionChanged += (_, _, _) =>
        {
            if (seatLost || !gantry.Feedback.IsMoving) return;
            seatLost = true;
            io.SetInput(InputIo.BoltFasteningBackupPlateUp, false);
        };
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(seatLost);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(4, route.CompletedPasses);
        await WaitUntilAsync(() => !manual.RunDryRunCommand.CanExecute(null));
        await machine.ShutdownAsync();
    }

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

    [Theory]
    [InlineData(HeatSinkSlot.HeatSink1)]
    [InlineData(HeatSinkSlot.HeatSink2)]
    public async Task OnePcbRepeatsTheSelectedHeatSinkRouteWithoutNewSupplyOrConveyorMotion(HeatSinkSlot heatSink)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.PcbSupply = true;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supply = services.GetRequiredService<PcbSupplyHandler>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        var work = services.GetRequiredService<PcbPlacementWork>();
        var buffer = services.GetRequiredService<BufferStage>();
        var recipe = services.GetRequiredService<Recipe>();
        recipe.PcbPlacement.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 10 };
        recipe.PcbPlacement.HeatSink2PcbPlacementPosition = new() { X = 40, Y = 100, Z = 10 };
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        var route = services.GetRequiredService<PcbDryRun>();
        manual.SelectedDryRun = DryRunTarget.PcbRoundTrip;
        manual.SelectedDryRunHeatSink = heatSink;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PcbPlacementCarrierPresent, true);
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
        await work.Station.SeatAsync(CancellationToken.None);
        var selectedInput = heatSink == HeatSinkSlot.HeatSink1
            ? InputIo.PcbPlacementHeatSink1Present : InputIo.PcbPlacementHeatSink2Present;
        io.SetInput(selectedInput, false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => route.RunAsync(heatSink, CancellationToken.None));
        Assert.Equal(PcbDryRunDirection.Ready, route.Direction);
        io.SetInput(selectedInput, true);
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await supply.SecurePcbAsync();

        var stops = 0;
        var selectedPlacements = 0;
        var otherPlacements = 0;
        var unwantedOutput = false;
        var unsafeMotion = false;
        io.OutputChanged += (output, value) =>
        {
            unwantedOutput |= value && output is OutputIo.PcbSupplyReadyToFront1
                or OutputIo.MainConveyorRun or OutputIo.NgConveyorRun or OutputIo.ShootBolt;
            if (output == OutputIo.PcbPlacementVacuumEjector && !value
                && route.Direction == PcbDryRunDirection.Forward)
            {
                var expected = heatSink == HeatSinkSlot.HeatSink1
                    ? recipe.PcbPlacement.HeatSink1PcbPlacementPosition : recipe.PcbPlacement.HeatSink2PcbPlacementPosition;
                if (placement.IsAtXY(expected)) selectedPlacements++;
                else otherPlacements++;
            }
        };
        io.InputChanged += (input, value) =>
        {
            if (stops == 0 && route.Direction == PcbDryRunDirection.Forward
                && input == InputIo.PcbSupplyIpmFixerForward && !value
                || stops == 1 && route.Direction == PcbDryRunDirection.Return
                && input == InputIo.PcbPlacementVacuumDetected && !value)
            {
                stops++;
                machine.Stop();
            }
        };
        placement.Feedback.PositionChanged += (_, _, _) =>
            unsafeMotion |= placement.Feedback.IsMovingHorizontal && !placement.CanMoveHorizontal;
        route.Changed += () => { if (route.CompletedCycles == 2) machine.Stop(); };

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
            var run = manual.RunDryRunCommand.ExecuteAsync(null);
            try { await run.WaitAsync(TimeSpan.FromSeconds(20)); }
            finally { machine.Stop(); await run; }
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.False(buffer.Conflict);
            if (attempt < 2) Assert.Equal(attempt + 1, stops);
        }
        Assert.Equal(2, route.CompletedCycles);
        Assert.Equal(2, selectedPlacements);
        Assert.Equal(0, otherPlacements);
        Assert.Equal(PcbSupplyPcbState.Secured, supply.Pcb);
        Assert.Equal(PcbSupplyRotationState.Unrotated, supply.Rotation);
        Assert.Equal(PlacementPcbState.None, placement.Pcb);
        Assert.False(buffer.PcbPresent);
        Assert.False(work.Completed);
        Assert.Empty(work.Assemblies);
        Assert.False(unwantedOutput);
        Assert.False(unsafeMotion);
        await machine.ShutdownAsync();
    }

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

    [Theory]
    [InlineData(HeatSinkSlot.HeatSink1)]
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

    [Fact]
    public async Task PcbReturnBringsTheCarrierBackToStation1ThenReturnsItsPcbToSupply()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbPlacement);
        settings.Units.PcbSupply = true;
        settings.Units.MainConveyor = true;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        var supply = services.GetRequiredService<PcbSupplyHandler>();
        var buffer = services.GetRequiredService<BufferStage>();
        var work = services.GetRequiredService<PcbPlacementWork>();
        var recipe = services.GetRequiredService<Recipe>();
        var conveyor = services.GetRequiredService<MainConveyorDryRun>();
        var returning = services.GetRequiredService<PcbReturn>();
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        recipe.PcbPlacement.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 10 };
        recipe.PcbPlacement.HeatSink2PcbPlacementPosition = new() { X = 40, Y = 100, Z = 10 };
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.PcbPlacementCarrierPresent, true);
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
        await work.Station.SeatAsync(CancellationToken.None);

        // Put a PCB down normally, then carry that same simulated product to Station 3.
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        await placement.SetVacuumAsync(true);
        await placement.SetIpmGripperAsync(true);
        using var placed = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        work.Changed += () => { if (work.Completed) placed.Cancel(); };
        await services.GetRequiredService<PcbPlacer>().RunAsync(recipe.PcbPlacement, placed.Token);
        Assert.True(work.Completed);
        manual.SelectedDryRun = DryRunTarget.MainConveyor;
        void StopAtStation3() { if (conveyor.CompletedPasses == 1) machine.Stop(); }
        conveyor.Changed += StopAtStation3;
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8));
        conveyor.Changed -= StopAtStation3;
        Assert.True(io.GetInput(InputIo.InspectionCarrierPresent));
        Assert.False(state.IsError, state.AlarmDetail);

        manual.SelectedDryRun = DryRunTarget.PcbReturn;
        manual.SelectedDryRunHeatSink = HeatSinkSlot.HeatSink2;
        settings.Units.MainConveyor = false;
        await WaitUntilAsync(() => !manual.RunDryRunCommand.CanExecute(null));
        settings.Units.MainConveyor = true;

        var stops = 0;
        var arrivals = new List<InputIo>();
        var motorDirections = new List<bool>();
        var unexpectedOutput = false;
        var pickedBeforeSeated = false;
        io.InputChanged += (input, value) =>
        {
            if (!value) return;
            if (input is InputIo.MainConveyorEntryCarrierDetected or InputIo.PcbPlacementCarrierPresent
                or InputIo.BoltFasteningCarrierPresent or InputIo.InspectionCarrierPresent)
                arrivals.Add(input);
            if (stops == 0 && input == InputIo.MainConveyorEntryCarrierDetected)
            {
                stops++;
                machine.Stop();
            }
        };
        work.Changed += () =>
        {
            if (stops == 1 && work.CarrierSeated)
            {
                stops++;
                machine.Stop();
            }
        };
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.MainConveyorRun && value)
                motorDirections.Add(io.GetOutput(OutputIo.MainConveyorReverse));
            if (output == OutputIo.PcbPlacementVacuumEjector && value)
                pickedBeforeSeated |= !work.CarrierSeated;
            unexpectedOutput |= value && output is OutputIo.MainConveyorReadyToFront2
                or OutputIo.MainConveyorAvailableToRear or OutputIo.PcbSupplyReadyToFront1
                or OutputIo.ShootBolt;
        };

        for (var pass = 0; pass < 3; pass++)
        {
            await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
            var run = manual.RunDryRunCommand.ExecuteAsync(null);
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { if (!run.IsCompleted) { machine.Stop(); await run; } }
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
            if (pass < 2)
            {
                Assert.Equal(pass + 1, stops);
                Assert.Equal(PlacementPcbState.None, placement.Pcb);
                Assert.Equal(PcbSupplyPcbState.None, supply.Pcb);
            }
        }

        Assert.Equal(new[] { InputIo.MainConveyorEntryCarrierDetected, InputIo.PcbPlacementCarrierPresent }, arrivals);
        Assert.Equal(new[] { true, false }, motorDirections);
        Assert.True(conveyor.AtStation1);
        Assert.True(work.CarrierSeated);
        Assert.False(io.GetInput(InputIo.PcbPlacementHeatSink1Present));
        Assert.True(io.GetInput(InputIo.PcbPlacementHeatSink2Present));
        Assert.Equal(PcbReturnState.Completed, returning.State);
        Assert.Equal(1, returning.CompletedReturns);
        Assert.Equal(PcbSupplyPcbState.Secured, supply.Pcb);
        Assert.Equal(PcbSupplyRotationState.Unrotated, supply.Rotation);
        Assert.Equal((0, settings.PcbSupply.CarrierY, settings.PcbSupply.RotationZ), supply.Feedback.GetPosition());
        Assert.Equal(PlacementPcbState.None, placement.Pcb);
        Assert.False(buffer.PcbPresent);
        Assert.False(pickedBeforeSeated);
        Assert.False(unexpectedOutput);

        // After the operator unloads the returned PCB, a new carrier needs conveyor return again.
        io.SetInput(InputIo.PcbSupplyPcbDetected, false);
        io.SetInput(InputIo.PcbPlacementCarrierPresent, false);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        Assert.Equal(PcbReturnState.WaitingForCarrier, returning.State);
        settings.Units.MainConveyor = false;
        await WaitUntilAsync(() => !manual.RunDryRunCommand.CanExecute(null));
        await machine.ShutdownAsync();
    }

    [Fact]
    public async Task InspectionDryRunTraversesTheRouteBothWaysAndResumesThePendingPoint()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.InspectionGantry.Motion.HorizontalSpeed = 30;
        using var services = CreateServices(settings);
        services.GetRequiredService<Recipe>().Pcb.BoltPoints =
        [
            new() { Number = 1, X = 5, Y = 5 },
            new() { Number = 2, X = 15, Y = 22 },
        ];
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var dryRun = services.GetRequiredService<InspectionDryRun>();
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        manual.SelectedDryRun = DryRunTarget.Inspection;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.False(manual.RunDryRunCommand.CanExecute(null));
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        Assert.False(manual.RunDryRunCommand.CanExecute(null));
        await work.Station.SeatAsync(CancellationToken.None);
        var production = work.Assembly(HeatSinkSlot.HeatSink1);
        production.RecordBarcode("production");
        production.RecordBoltPresence(1, false);
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));

        var visited = new List<int>();
        services.GetRequiredService<BoltInspector>().Inspected += image =>
        {
            Assert.Equal(HeatSinkSlot.HeatSink1, image.HeatSink);
            visited.Add(image.BoltNumber);
        };
        var stopAfter = 2;
        dryRun.Changed += () =>
        {
            if (dryRun.CompletedPasses == stopAfter) manual.RunDryRunCommand.Cancel();
        };
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { 1, 2, 1 }, visited);
        Assert.Equal("PCB-1", dryRun.LastBarcode);
        Assert.Equal(2, dryRun.CompletedPasses);
        Assert.Equal(InspectionRouteDirection.Forward, dryRun.Direction);
        Assert.True(work.CarrierSeated);
        Assert.False(work.Completed);
        Assert.Equal("production", production.PcbBarcode);
        Assert.False(Assert.Single(production.BoltPresenceResults).Value);

        stopAfter = 4;
        var interrupted = false;
        gantry.Feedback.PositionChanged += (x, _, _) =>
        {
            if (!interrupted && dryRun.ActiveBolt == 2 && x is > 7 and < 12)
            {
                interrupted = true;
                manual.RunDryRunCommand.Cancel();
            }
        };
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(interrupted);
        Assert.Equal(2, dryRun.CompletedPasses);
        Assert.Equal(2, dryRun.ActiveBolt);
        Assert.False(gantry.Feedback.IsMoving);
        visited.Clear();
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { 2, 1 }, visited);
        Assert.Equal(4, dryRun.CompletedPasses);
        Assert.True(work.CarrierSeated);
        Assert.False(work.Completed);
        Assert.Equal("production", production.PcbBarcode);
        Assert.False(Assert.Single(production.BoltPresenceResults).Value);
        Assert.Equal(MachineAlarm.None, state.Alarm);

        stopAfter = 6;
        var seatLost = false;
        gantry.Feedback.PositionChanged += (_, _, _) =>
        {
            if (seatLost) return;
            seatLost = true;
            io.SetInput(InputIo.InspectionBackupPlateUp, false);
        };
        await WaitUntilAsync(() => manual.RunDryRunCommand.CanExecute(null));
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(seatLost);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(4, dryRun.CompletedPasses);
        await WaitUntilAsync(() => !manual.RunDryRunCommand.CanExecute(null));
        Assert.True(work.CarrierPresent);
        Assert.False(work.Completed);
        await machine.ShutdownAsync();
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

    [Fact]
    public async Task NgTransferDryRunReturnsTheCarrierAndResumesWhileHoldingIt()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        settings.NgCarrierTransfer.Speed = 200;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var transfer = services.GetRequiredService<NgCarrierTransfer>();
        var dryRun = services.GetRequiredService<NgTransferDryRun>();
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.Equal(NgTransferState.WaitingForCarrier, dryRun.State);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        Assert.Equal(NgTransferState.StationNotReady, dryRun.State);
        await services.GetRequiredService<InspectionWork>().Station.SeatAsync(CancellationToken.None);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgShuttleDown, false);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.Equal(NgTransferState.WaitingForDestination, dryRun.State);
        io.SetInput(InputIo.NgShuttleCarrierDetected, false);
        await gantry.MoveToAsync(settings.NgCarrierTransfer.ShuttlePlacePosition, 1_000);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        Assert.Equal(NgTransferState.Raising, dryRun.State);
        await WaitUntilAsync(() => state.Display.ManualControlsEnabled);

        var stopAfter = 2;
        dryRun.Changed += () =>
        {
            if (dryRun.CompletedTransfers == stopAfter) manual.RunDryRunCommand.Cancel();
        };
        gantry.Feedback.MovingChanged += moving =>
        {
            if (moving) Assert.True(transfer.IsRaised);
        };
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(2, dryRun.CompletedTransfers);
        Assert.True(io.GetInput(InputIo.InspectionCarrierPresent));
        Assert.True(io.GetInput(InputIo.InspectionHeatSink1Present));
        Assert.False(io.GetInput(InputIo.InspectionHeatSink2Present));
        Assert.False(io.GetInput(InputIo.NgShuttleCarrierDetected));
        Assert.False(transfer.CarrierDetected);
        Assert.True(transfer.IsRaised);

        stopAfter = 4;
        var interrupted = false;
        gantry.Feedback.PositionChanged += (x, _, _) =>
        {
            if (!interrupted && dryRun.Destination == NgTransferDestination.Station
                && transfer.CarrierDetected && x is > 30 and < 140)
            {
                interrupted = true;
                manual.RunDryRunCommand.Cancel();
            }
        };
        await WaitUntilAsync(() => state.Display.ManualControlsEnabled);
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(interrupted);
        Assert.Equal(3, dryRun.CompletedTransfers);
        Assert.Equal(NgTransferDestination.Station, dryRun.Destination);
        Assert.True(transfer.CarrierDetected);
        Assert.Equal(NgTransferGripperState.Closed, transfer.Gripper);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.InRange(gantry.Feedback.GetPosition().X, 30, 140);
        Assert.False(io.GetInput(InputIo.InspectionCarrierPresent));
        Assert.False(io.GetInput(InputIo.NgShuttleCarrierDetected));

        await WaitUntilAsync(() => state.Display.ManualControlsEnabled);
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, dryRun.CompletedTransfers);
        Assert.True(io.GetInput(InputIo.InspectionCarrierPresent));
        Assert.True(transfer.IsRaised);
        Assert.False(transfer.CarrierDetected);
        Assert.Equal(MachineAlarm.None, state.Alarm);

        // Retract an empty lowered pickup before reversing toward an existing shuttle carrier.
        io.SetInput(InputIo.InspectionCarrierPresent, false);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        stopAfter = 5;
        await WaitUntilAsync(() => state.Display.ManualControlsEnabled);
        await manual.RunDryRunCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(5, dryRun.CompletedTransfers);
        Assert.True(io.GetInput(InputIo.InspectionCarrierPresent));
        Assert.False(io.GetInput(InputIo.NgShuttleCarrierDetected));
        await machine.ShutdownAsync();
    }

    [Fact]
    public void RecipeAccessUsesTheLiveObjectNotTheServiceProvider()
    {
        var services = CreateServices(FlowSettings());
        var recipe = services.GetRequiredService<Recipe>();
        var getPcb = services.GetRequiredService<Func<PcbLayout>>();
        services.Dispose();

        var replacement = new Recipe();
        recipe.ReplaceWith(replacement);
        Assert.Same(replacement.Pcb, getPcb());
    }

    [Fact]
    public async Task IndividualHomeReportsReadFailureBeforeMotionStarts()
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        var axis = manual.Axes.Single(row => row.Group == MotionGroup.InspectionGantry && row.Axis == MotionAxis.X);
        feedback.BeforeRead = () => throw new IOException("Home feedback read failed.");

        await manual.HomeAxisCommand.ExecuteAsync(axis);

        Assert.Equal(MachineAlarm.HomeFailed, state.Alarm);
        Assert.Contains("Home feedback read failed.", state.AlarmDetail);
        Assert.False(state.IsHoming);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.False(services.GetRequiredService<InspectionGantry>().Feedback.IsMoving);
    }

    [Fact]
    public async Task DisplayReadsCoalesceWithoutBlockingViewsAndSurviveMachineStop()
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.Display.ManualControlsEnabled);
        using var entered = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        var readingView = new AsyncLocal<bool>();
        var blocked = 0;
        var updates = 0;
        feedback.BeforeRead = () =>
        {
            Assert.False(readingView.Value);
            if (Interlocked.Exchange(ref blocked, 1) != 0) return;
            entered.Set();
            released.Wait();
        };
        state.DisplayChanged += () => Interlocked.Increment(ref updates);
        try
        {
            state.RequestDisplayRefresh();
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2))));
            await Task.Run(() =>
            {
                readingView.Value = true;
                var manual = services.GetRequiredService<MotionWindowViewModel>();
                foreach (var row in manual.Axes)
                {
                    _ = row.Condition;
                    _ = manual.HomeAxisCommand.CanExecute(row);
                }
                var supply = services.GetRequiredService<SupplyTeachingViewModel>();
                var station = services.GetRequiredService<StationTeachingViewModel>();
                foreach (var group in Enum.GetValues<MotionGroup>())
                {
                    station.SelectedMotionGroup = group == MotionGroup.PcbSupply
                        ? MotionGroup.PcbPlacementHandler : group;
                    TeachingMotionViewModel teaching = group == MotionGroup.PcbSupply ? supply : station;
                    _ = teaching.ManualBlock;
                    _ = teaching.CanEditTeaching;
                    _ = teaching.MotionHint;
                    foreach (var direction in Enum.GetValues<TeachingDirection>())
                    {
                        _ = teaching.JogCommand.CanExecute(direction);
                        _ = teaching.StepCommand.CanExecute(direction);
                    }
                    foreach (var output in teaching.TeachingOutputs.Values)
                        _ = teaching.SetOutputOnCommand.CanExecute(output);
                }
                for (var index = 0; index < 1000; index++) state.RequestDisplayRefresh();
            }).WaitAsync(TimeSpan.FromSeconds(2));

            var io = services.GetRequiredService<VirtualIoService>();
            var light = services.GetRequiredService<IoSignals>().Outputs[OutputIo.MachineLight];
            Assert.False(light.IsOn);
            io.SetOutput(light.Signal, true);
            Assert.False(light.IsOn); // Display acquisition is still blocked.
            var row = new OutputControlRow(light, machine);
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.False(io.GetOutput(light.Signal)); // Toggle the real ON, not the displayed OFF.

            released.Set();
            await WaitUntilAsync(() => Volatile.Read(ref updates) >= 2);
            await Task.Delay(30);
            Assert.Equal(2, Volatile.Read(ref updates));

            var previous = state.Display;
            machine.Stop();
            state.RequestDisplayRefresh();
            await WaitUntilAsync(() => !ReferenceEquals(previous, state.Display));
            await machine.ShutdownAsync();
            previous = state.Display;
            state.RequestDisplayRefresh();
            await Task.Delay(30);
            Assert.Same(previous, state.Display);
        }
        finally
        {
            released.Set();
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeachingRechecksFeedbackBeforeJogOrSavingPosition(bool savePosition)
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point =>
            point.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        var point = teaching.SelectedPoint;
        var before = (point.X, point.Y, point.Z);
        feedback.BeforeRead = () => throw new IOException("Teaching feedback read failed.");

        if (savePosition) await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        else await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);

        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
        Assert.Contains("Teaching feedback read failed.", state.AlarmDetail);
        Assert.Equal(before, (point.X, point.Y, point.Z));
        Assert.False(teaching.Motion.IsMoving);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
    }

    [Theory]
    [InlineData(TeachingDirection.XPlus, nameof(IAxisMotion.MoveXAsync), 10.1, 20)]
    [InlineData(TeachingDirection.YPlus, nameof(IAxisMotion.MoveYAsync), 10, 20.1)]
    public async Task InspectionTeachingStepMovesOnlyTheSelectedAxis(
        TeachingDirection direction, string expectedMove, double x, double y)
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 10, Y = 20 }, 10_000);
        teaching.StepDistance = 0.1;
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(direction));

        await teaching.StepCommand.ExecuteAsync(direction);

        Assert.Equal(expectedMove, feedback.LastMove);
        Assert.Equal((x, y, 0), gantry.Feedback.GetPosition());

        await services.GetRequiredService<IIoService>().SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(direction));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gantry.MoveAxisAsync(MotionAxis.X, 30, 1_000));
        Assert.Equal((x, y, 0), gantry.Feedback.GetPosition());
    }

    [Theory]
    [InlineData("Alarm")]
    [InlineData("ServoOff")]
    [InlineData("ReadFailure")]
    public async Task AutomaticPollingStopsOnSilentEnabledMotionFaultWithoutMonitorWindow(string fault)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        settings.Units.MainConveyor = true;
        using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var active = probes[MotionGroup.InspectionGantry];
        foreach (var probe in probes.Where(item => item.Key != MotionGroup.InspectionGantry).Select(item => item.Value))
        {
            probe.ReportReady = true;
            probe.FailHardwareCalls = true; // Disabled hardware must not be sampled, even while AUTO polls.
        }
        Task? run = null;
        try
        {
            await machine.InitializeAsync();
            await machine.HomeAsync(CancellationToken.None);
            io.AutoResponseEnabled = false;
            io.SetInput(InputIo.AutoMode, false);
            Assert.True(machine.CanStart);
            run = machine.StartAsync();
            await WaitUntilAsync(() => state.Display.AutomaticRunning && state.Display.Homed);
            var scans = 0;
            void CountScan() => Interlocked.Increment(ref scans);
            state.DisplayChanged += CountScan;
            try
            {
                await WaitUntilAsync(() => Volatile.Read(ref scans) >= 2);
                Assert.False(run.IsCompleted);
                Assert.Equal(MachineAlarm.None, state.Alarm);
                if (fault == "ReadFailure") active.FailHardwareCalls = true;
                else active.OverrideState = value => fault == "Alarm"
                    ? value with { Alarm = true } : value with { ServoOn = false };
                // No StateChanged, DI changes, UI timer or explicit refresh request accompanies this fault.
                await run.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
                Assert.False(state.AutomaticRunning);
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
                Assert.False(io.GetOutput(OutputIo.MainConveyorAvailableToRear));
                await WaitUntilAsync(() => !services.GetRequiredService<OperationCancellation>().HasActiveOperations);
                Assert.All(probes.Where(item => item.Key != MotionGroup.InspectionGantry),
                    item => Assert.Equal(0, item.Value.HardwareCalls));
                active.FailHardwareCalls = false;
                active.OverrideState = null;
                state.RequestDisplayRefresh();
                await WaitUntilAsync(() => state.Display.Available && !state.Display.MotionFaulted);
                Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm); // Recovery never restarts AUTO.
            }
            finally { state.DisplayChanged -= CountScan; }
        }
        finally
        {
            active.FailHardwareCalls = false;
            active.OverrideState = null;
            await machine.ShutdownAsync();
            if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DisplayReadFailureIsVisibleAndDoesNotReplaceLiveAdmissionChecks()
    {
        using var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        Assert.True(state.Display.Available);
        var error = new IOException("Display feedback unavailable.");
        feedback.BeforeRead = () => throw error;

        // A ready display is not permission to operate when the actual read fails.
        Assert.Throws<IOException>(() => machine.CanHome);
        state.RequestDisplayRefresh();
        await WaitUntilAsync(() => !state.Display.Available);
        Assert.Same(error, state.Display.ReadError);
        Assert.False(state.Display.CanHome);
        Assert.False(state.Display.CanStart);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.All(services.GetRequiredService<InspectionGantry>().Motion.Axes.Values,
            axis => Assert.Equal(AxisCondition.Unavailable, axis.Condition));

        feedback.BeforeRead = null;
        state.RequestDisplayRefresh();
        await WaitUntilAsync(() => state.Display.Available);
        Assert.Null(state.Display.ReadError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisplayProgrammingErrorsAreReportedAndNotRetried(bool duringInitialization)
    {
        var services = CreateDisplayServices(out var feedback);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var error = new InvalidOperationException("Display calculation failed.");
        try
        {
            if (!duringInitialization) await machine.InitializeAsync();
            feedback.BeforeRead = () => throw error;
            if (duringInitialization)
            {
                Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(
                    () => machine.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(2))));
            }
            else
            {
                state.RequestDisplayRefresh();
                await WaitUntilAsync(() => ReferenceEquals(error, state.Display.ReadError));
            }

            feedback.BeforeRead = null;
            state.RequestDisplayRefresh();
            Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(machine.ShutdownAsync));
            Assert.Same(error, state.Display.ReadError);
        }
        finally
        {
            Assert.Same(error, Record.Exception(services.Dispose));
        }
    }

    [Fact]
    public async Task ManualConveyorStopsOnModeChangeWithoutAView()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var manual = services.GetRequiredService<ManualHardwareViewModel>();
        try
        {
            manual.RunConveyorCommand.Execute(null);
            Assert.True(conveyor.RunCommandOn);
            io.SetInput(InputIo.AutoMode, false);
            Assert.False(conveyor.RunCommandOn);
            manual.RunConveyorCommand.Execute(null);
            Assert.False(conveyor.RunCommandOn);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        }
        finally
        {
            conveyor.Stop();
        }
    }

    [Fact]
    public async Task ManualServoFailureStaysAtTheCommandBoundary()
    {
        using var services = CreateServices(FlowSettings());
        await services.GetRequiredService<MachineController>().InitializeAsync();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        var row = manual.Axes.Single(axis => axis.Group == MotionGroup.InspectionGantry && axis.Axis == MotionAxis.X);
        var motion = services.GetRequiredService<InspectionGantry>().Feedback;
        void FailOnce()
        {
            motion.StateChanged -= FailOnce;
            throw new IOException("Servo feedback failed.");
        }
        motion.StateChanged += FailOnce;

        Assert.True(manual.ToggleServoCommand.CanExecute(row));
        manual.ToggleServoCommand.Execute(row);
        Assert.Equal(MachineAlarm.MotionUnavailable, services.GetRequiredService<MachineState>().Alarm);
        Assert.Contains("Servo feedback failed", services.GetRequiredService<MachineState>().AlarmDetail);
        await WaitUntilAsync(() => !row.Feedback.ServoOn);
        Assert.False(row.Feedback.ServoOn);
    }

    [Fact]
    public async Task ManualCommandAfterShutdownDoesNotEscapeTheBoundary()
    {
        using var services = CreateServices(FlowSettings());
        var teaching = services.GetRequiredService<SupplyTeachingViewModel>();
        var operations = services.GetRequiredService<OperationCancellation>();
        await operations.ShutdownAsync();
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);

        Assert.False(services.GetRequiredService<PcbSupplyHandler>().Feedback.IsMoving);
        Assert.False(operations.HasActiveOperations);
        Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
    }

    [Theory]
    [InlineData(MotionGroup.PcbSupply)]
    [InlineData(MotionGroup.PcbPlacementHandler)]
    [InlineData(MotionGroup.InspectionGantry)]
    public async Task TeachingJogStopsWhenTeachingContextChanges(MotionGroup group)
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        TeachingMotionViewModel teaching;
        IMotionFeedback feedback;
        if (group == MotionGroup.PcbSupply)
        {
            teaching = services.GetRequiredService<SupplyTeachingViewModel>();
            feedback = services.GetRequiredService<PcbSupplyHandler>().Feedback;
        }
        else
        {
            var station = services.GetRequiredService<StationTeachingViewModel>();
            station.SelectedMotionGroup = group;
            teaching = station;
            feedback = group == MotionGroup.PcbPlacementHandler
                ? services.GetRequiredService<PcbPlacementHandler>().Feedback
                : services.GetRequiredService<InspectionGantry>().Feedback;
        }

        teaching.JogSpeed = 1;
        Action[] stops =
        [
            () => teaching.JogStopCommand.Execute(null),
            () => teaching.SelectNextPointCommand.Execute(null),
            teaching.Deactivate,
            machine.Stop,
            () => io.SetInput(InputIo.AutoMode, false),
        ];
        foreach (var stop in stops)
        {
            await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
            var jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);
            try
            {
                Assert.True(feedback.IsMoving);
                Assert.False(jog.IsCompleted);
                stop();
                await jog.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.False(feedback.IsMoving);
                await WaitUntilAsync(() => !services.GetRequiredService<OperationCancellation>().HasActiveOperations);
                Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);
            }
            finally
            {
                machine.Stop();
                await jog.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
    }

    [Fact]
    public async Task RawOutputRechecksManualAdmissionWithoutWaitingForWindowRefresh()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        var row = new OutputControlRow(
            services.GetRequiredService<IoSignals>().Outputs[OutputIo.MachineLight], machine);
        await row.ToggleCommand.ExecuteAsync(null);
        Assert.True(io.GetOutput(OutputIo.MachineLight));

        io.SetInput(InputIo.AutoMode, false);
        await row.ToggleCommand.ExecuteAsync(null);
        Assert.True(io.GetOutput(OutputIo.MachineLight));

        io.SetInput(InputIo.AutoMode, true);
        using (services.GetRequiredService<OperationCancellation>().Link())
        {
            await row.ToggleCommand.ExecuteAsync(null);
            Assert.True(io.GetOutput(OutputIo.MachineLight));
        }
        await row.ToggleCommand.ExecuteAsync(null);
        Assert.False(io.GetOutput(OutputIo.MachineLight));
    }

    [Fact]
    public async Task DiagnosticOutputsKeepLocalRotationInterlocksAndCancelFeedbackOnModeChange()
    {
        var settings = FlowSettings();
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var signals = services.GetRequiredService<IoSignals>();
        var motion = services.GetRequiredKeyedService<IAxisMotion>(MotionGroup.PcbSupply);
        await machine.InitializeAsync();
        var gripper = new OutputControlRow(signals.Outputs[OutputIo.PcbSupplyGripperClosed], machine);
        Assert.False(gripper.ToggleCommand.CanExecute(null));
        await gripper.ToggleCommand.ExecuteAsync(null);
        Assert.False(io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        await machine.HomeAsync(CancellationToken.None);
        typeof(MachineState).GetMethod("SetError", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(state, [MachineAlarm.MotionUnavailable, new IOException("Another handler is unavailable.")]);

        var rotation = new OutputControlRow(signals.Outputs[OutputIo.PcbSupplyRotate], machine);
        await WaitUntilAsync(() => rotation.ToggleCommand.CanExecute(null));
        var position = motion.GetPosition();
        var wasRotated = io.GetOutput(OutputIo.PcbSupplyRotate);
        await rotation.ToggleCommand.ExecuteAsync(null);
        Assert.Equal(!wasRotated, io.GetOutput(OutputIo.PcbSupplyRotate));
        Assert.Equal(position, motion.GetPosition());
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);

        await motion.MoveZAsync(settings.PcbSupply.RotationZ + 1, 10_000);
        await WaitUntilAsync(() => !rotation.ToggleCommand.CanExecute(null));
        position = motion.GetPosition();
        await rotation.ToggleCommand.ExecuteAsync(null);
        Assert.Equal(!wasRotated, io.GetOutput(OutputIo.PcbSupplyRotate));
        Assert.Equal(position, motion.GetPosition()); // No implicit Z recovery move.

        motion.SetServo(MotionAxis.X, false);
        await gripper.ToggleCommand.ExecuteAsync(null);
        Assert.False(io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        motion.SetServo(MotionAxis.X, true);
        await WaitUntilAsync(() => gripper.ToggleCommand.CanExecute(null));
        io.AutoResponseEnabled = false;
        var toggle = gripper.ToggleCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => gripper.FeedbackState == OutputFeedbackState.Waiting);
        Assert.True(state.IsRunning);
        io.SetInput(InputIo.AutoMode, false);
        await toggle.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(gripper.FeedbackState == OutputFeedbackState.Timeout);
        await WaitUntilAsync(() => !gripper.ToggleCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TeachingCaptureStopsOnModeChangeOrViewShutdown(bool scanCarrier, bool closeTeaching)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.InspectionGantry.Motion.HorizontalSpeed = 1;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 50, Y = 50 }, 10_000);
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.RecipeEditor.Name = $"CancelledScan-{Guid.NewGuid():N}";
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.DataMatrix);
        var selected = teaching.SelectedPoint;
        var command = scanCarrier ? teaching.CaptureCarrierImagesCommand : teaching.CaptureInspectionCommand;
        await WaitUntilAsync(() => command.CanExecute(null));
        var capture = command.ExecuteAsync(null);
        try
        {
            await WaitUntilAsync(() => gantry.Feedback.IsMoving);
            if (closeTeaching) await teaching.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
            else io.SetInput(InputIo.AutoMode, false);
            await capture.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(gantry.Feedback.IsMoving);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.Same(selected, teaching.SelectedPoint);
            Assert.False(teaching.Preview.HasImage);
            Assert.Null(teaching.CameraError);
            Assert.Equal(MachineAlarm.None, services.GetRequiredService<MachineState>().Alarm);

            io.SetInput(InputIo.AutoMode, true);
            settings.InspectionGantry.Motion.HorizontalSpeed = 10_000;
            await WaitUntilAsync(() => command.CanExecute(null));
            await command.ExecuteAsync(null);
            Assert.Null(teaching.CameraError);
            Assert.True(scanCarrier ? teaching.HasCarrierImages : teaching.Preview.HasImage);

            await gantry.MoveToAsync(new() { X = 50, Y = 50 }, 10_000);
            settings.InspectionGantry.Motion.HorizontalSpeed = 1;
            teaching.SelectedPoint = selected;
            await WaitUntilAsync(() => command.CanExecute(null));
            capture = command.ExecuteAsync(null);
            await WaitUntilAsync(() => gantry.Feedback.IsMoving);
            await machine.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await capture.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(gantry.Feedback.IsMoving);
            Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
            Assert.Null(teaching.CameraError);
        }
        finally
        {
            machine.Stop();
            await capture.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DataMatrixFailureStopsInspectionAndResetAllowsARealRead()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<Recipe>();
        recipe.Pcb.BoltPoints = [new() { Number = 1, X = 10, Y = 10 }];
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var work = services.GetRequiredService<InspectionWork>();
        var camera = services.GetRequiredService<VirtualCamera>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);

        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.DataMatrix);
        await WaitUntilAsync(() => teaching.CaptureInspectionCommand.CanExecute(null));
        await teaching.CaptureInspectionCommand.ExecuteAsync(null);
        Assert.Null(teaching.CameraError);
        Assert.Equal("PCB-2", teaching.Preview.Result);
        Assert.True(services.GetRequiredService<BoltInspector>().IsAtBarcode(HeatSinkSlot.HeatSink2));

        var frame = camera.Capture(500, 0);
        camera.SourceImage = frame with { Pixels = new byte[frame.Pixels.Length] };
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        io.SetInput(InputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.InspectionBackupPlateDown, false);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(work.CarrierSeated);
        Assert.True(machine.CanStart);
        using var failureStop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await machine.StartAsync(failureStop.Token);
        Assert.Equal(MachineAlarm.Inspection, state.Alarm);
        Assert.Contains("Data Matrix", state.AlarmDetail);
        Assert.StartsWith("Data Matrix could not be read", state.AlarmMessage);
        Assert.False(work.Completed);
        Assert.Null(work.Assembly(HeatSinkSlot.HeatSink1).PcbBarcode);
        Assert.Empty(work.Assembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
        Assert.False(services.GetRequiredService<InspectionGantry>().Feedback.IsMoving);

        camera.SourceImage = null;
        await machine.ResetAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var run = machine.StartAsync(stop.Token);
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(2)));
            Assert.Equal("PCB-1", work.Assembly(HeatSinkSlot.HeatSink1).PcbBarcode);
            Assert.Single(work.Assembly(HeatSinkSlot.HeatSink1).BoltPresenceResults);
            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.Null(state.AlarmMessage);
        }
        finally
        {
            stop.Cancel();
            await run;
        }
    }

    [Fact]
    public void HardwareDefinitionsDriveSettingsAndManualAxisLists()
    {
        var settings = FlowSettings();
        settings.PcbSupplyHardware.Axes[MachineAxis.PcbSupplyY].Maximum = 315;
        using var services = CreateServices(settings);
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        var view = services.GetRequiredService<SettingsViewModel>();
        Assert.Equal(11, manual.Axes.Length);
        foreach (var section in new (MotionSettings Settings, MotionHardwareSettings Hardware)[]
        {
            (settings.PcbSupply.Motion, settings.PcbSupplyHardware),
            (settings.PcbPlacementHandler.Motion, settings.PcbPlacementHandlerHardware),
            (settings.BoltFastening.Motion, settings.BoltFasteningHardware),
            (settings.InspectionGantry.Motion, settings.InspectionGantryHardware),
        })
        {
            view.SelectedMotionGroup = section.Hardware.Group;
            Assert.Same(section.Settings, view.CurrentMotionSettings);
            Assert.Same(section.Hardware, view.CurrentMotionHardwareSettings);
            Assert.Equal(section.Hardware.Axes.Keys,
                view.CurrentAxisMappings.Select(row => (MachineAxis)row.Signal));
            Assert.Equal(0.001, section.Hardware.MillimetersPerPulse);
            Assert.Equal(section.Hardware.GetAxis(MotionAxis.Z) is not null, view.CurrentMotionHasZ);
            foreach (var axis in section.Hardware.AxisSignals.Keys)
            {
                Assert.Single(manual.Axes, row => row.Group == section.Hardware.Group && row.Axis == axis);
            }
        }
        Assert.Equal(315, services.GetRequiredService<PcbSupplyHandler>().Feedback.GetRange(MotionAxis.Y)!.Value.Maximum);
        Assert.False(services.GetRequiredService<InspectionGantry>().Feedback.HasZ);
        var json = JsonSerializer.Serialize(settings.PcbSupplyHardware);
        Assert.DoesNotContain(nameof(MotionHardwareSettings.AxisSignals), json);
        Assert.DoesNotContain(nameof(MotionHardwareSettings.Group), json);
        var loaded = JsonSerializer.Deserialize<PcbSupplyHardwareSettings>(json)!;
        Assert.Equal(MachineAxis.PcbSupplyY, loaded.AxisSignals[MotionAxis.Y]);
        Assert.Equal(315, loaded.GetAxis(MotionAxis.Y)!.Maximum);
    }

    [Fact]
    public void TeachingIoFollowsSelectedUnitAndSharesRelatedSignals()
    {
        using var services = CreateServices(FlowSettings());
        var supply = services.GetRequiredService<SupplyTeachingViewModel>();
        var station = services.GetRequiredService<StationTeachingViewModel>();
        Assert.Equal(new[] { HardwareArea.PcbSupply, HardwareArea.PcbBuffer },
            supply.IoGroups.Select(group => group.Area));
        var buffer = supply.IoGroups.Single(group => group.Area == HardwareArea.PcbBuffer);
        var signals = services.GetRequiredService<IoSignals>();
        Assert.All(supply.IoGroups.SelectMany(group => group.Inputs),
            row => Assert.Same(signals.Inputs[row.Signal], row));
        Assert.All(supply.IoGroups.SelectMany(group => group.Outputs),
            row => Assert.Same(signals.Outputs[row.Signal], row));
        var notifications = 0;
        supply.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(supply.IoGroups)) notifications++;
        };
        supply.SelectedPoint = supply.Points.First(point => point.MotionGroup == MotionGroup.PcbPlacementHandler);
        station.SelectedMotionGroup = MotionGroup.PcbPlacementHandler;
        Assert.True(notifications > 0);
        Assert.Same(station.IoGroups, supply.IoGroups);
        Assert.Same(station.TeachingOutputs, supply.TeachingOutputs);
        Assert.Equal(HardwareArea.PcbPlacementStation, station.IoGroups[0].Area);
        Assert.Same(buffer, supply.IoGroups.Single(group => group.Area == HardwareArea.PcbBuffer));
        Assert.Contains(supply.IoGroups.SelectMany(group => group.Inputs),
            row => row.Signal == InputIo.PcbPlacementCarrierPresent);

        station.SelectedMotionGroup = MotionGroup.BoltFastening;
        Assert.Equal(new[] { HardwareArea.BoltFasteningStation, HardwareArea.BoltFastening, HardwareArea.BoltFeeder },
            station.IoGroups.Select(group => group.Area));
        Assert.Contains(station.IoGroups.SelectMany(group => group.Inputs),
            row => row.Signal == InputIo.BoltFasteningCarrierPresent);
        Assert.DoesNotContain(station.IoGroups.SelectMany(group => group.Inputs),
            row => row.Signal == InputIo.PcbPlacementCarrierPresent);

        station.SelectedMotionGroup = MotionGroup.InspectionGantry;
        Assert.Equal(new[] { HardwareArea.InspectionStation, HardwareArea.NgCarrierTransfer, HardwareArea.NgShuttle },
            station.IoGroups.Select(group => group.Area));
        Assert.Contains(station.IoGroups.SelectMany(group => group.Inputs),
            row => row.Signal == InputIo.InspectionCarrierPresent);
        Assert.DoesNotContain(OutputIo.NgShuttleDown, station.TeachingOutputs.Keys);
        Assert.Equal(new[] { OutputIo.NgCarrierPickupDown, OutputIo.NgCarrierGripperClose, OutputIo.InspectionBackupPlateUp },
            station.TeachingOutputs.Keys);
    }

    [Fact]
    public async Task TeachingOutputsWaitForFeedbackAndCancelWithoutReversingPneumatics()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<SupplyTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        var gripper = teaching.TeachingOutputs[OutputIo.PcbSupplyGripperClosed];
        await WaitUntilAsync(() => !teaching.SetOutputOnCommand.CanExecute(gripper));
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var handler = services.GetRequiredService<PcbSupplyHandler>();
        var rotation = teaching.TeachingOutputs[OutputIo.PcbSupplyRotate];
        await handler.MoveTeachingZAsync(5);
        await teaching.SetOutputOffCommand.ExecuteAsync(rotation);
        Assert.Equal(0, handler.Feedback.GetPosition().Z);
        Assert.Equal(PcbSupplyRotationState.Unrotated, handler.Rotation);
        await teaching.SetOutputOnCommand.ExecuteAsync(rotation);
        await handler.MoveXAsync(80);
        await WaitUntilAsync(() => !teaching.SetOutputOffCommand.CanExecute(rotation));
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(TeachingDirection.ZPlus));
        await handler.MoveXAsync(0);
        io.AutoResponseEnabled = false;
        await WaitUntilAsync(() => teaching.SetOutputOnCommand.CanExecute(gripper));

        var pending = teaching.SetOutputOnCommand.ExecuteAsync(gripper);
        Assert.True(io.GetOutput(gripper.Signal));
        Assert.False(pending.IsCompleted);
        await WaitUntilAsync(() => !teaching.SetOutputOffCommand.CanExecute(gripper));
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        var feedback = io.GetOutputFeedback(gripper.Signal)!;
        io.SetInput(feedback.OnInput, true);
        Assert.False(pending.IsCompleted); // Both inputs ON is not completion.
        io.SetInput(feedback.OffInput, false);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        await teaching.SetOutputOnCommand.ExecuteAsync(gripper); // ON again is allowed.

        teaching.SelectedPoint = teaching.Points.Last(point => point.MotionGroup == MotionGroup.PcbSupply);
        var beforeSelection = handler.Feedback.GetPosition();
        var releasing = teaching.SetOutputOffCommand.ExecuteAsync(gripper);
        Assert.False(releasing.IsCompleted);
        teaching.SelectNextPointCommand.Execute(null);
        await releasing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MotionGroup.PcbPlacementHandler, teaching.SelectedPoint!.MotionGroup);
        Assert.Equal(beforeSelection, handler.Feedback.GetPosition());
        Assert.False(io.GetOutput(gripper.Signal));
        Assert.True(io.GetInput(feedback.OnInput));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await WaitUntilAsync(() => !teaching.SetOutputOnCommand.CanExecute(gripper));

        var lift = teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerDown];
        var lowering = teaching.SetOutputOnCommand.ExecuteAsync(lift);
        await teaching.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await lowering;
        Assert.True(io.GetOutput(lift.Signal));
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task TeachingOutputsKeepOwnerMovementRulesAndReportFeedbackTimeout()
    {
        var settings = FlowSettings();
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedMotionGroup = MotionGroup.PcbPlacementHandler;
        var lift = teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerDown];
        await teaching.SetOutputOnCommand.ExecuteAsync(lift);
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        await WaitUntilAsync(() => !teaching.SetOutputOnCommand.CanExecute(teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerRotate]));
        await teaching.SetOutputOffCommand.ExecuteAsync(lift);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        var ipm = teaching.TeachingOutputs[OutputIo.PcbPlacementIpmDown];
        await teaching.SetOutputOnCommand.ExecuteAsync(ipm);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        Assert.True(teaching.TeachingOutputs[OutputIo.ShootBolt].HoldToRun);
        Assert.DoesNotContain(OutputIo.ShootingEscapeForward, teaching.TeachingOutputs.Keys);
        var pickup = teaching.TeachingOutputs[OutputIo.PickupHeadDown];
        await teaching.SetOutputOnCommand.ExecuteAsync(pickup);
        Assert.False(services.GetRequiredService<BoltFasteningGantry>().CanMoveHorizontal);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
        await teaching.SetOutputOffCommand.ExecuteAsync(pickup);

        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        var ngLift = teaching.TeachingOutputs[OutputIo.NgCarrierPickupDown];
        settings.Units.NgCarrierTransfer = false;
        await WaitUntilAsync(() => !teaching.SetOutputOnCommand.CanExecute(ngLift));
        settings.Units.NgCarrierTransfer = true;
        io.AutoResponseEnabled = false;
        var pending = teaching.SetOutputOnCommand.ExecuteAsync(ngLift);
        teaching.JogStopCommand.Execute(null);
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(io.GetOutput(ngLift.Signal));

        settings.Options.TimeoutMilliseconds = 50;
        await teaching.SetOutputOnCommand.ExecuteAsync(ngLift);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
    }

    [Fact]
    public async Task ManualShootingHoldsOnlyAirAndStopsOnReleaseNavigationOrStop()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        var state = services.GetRequiredService<MachineState>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.ShootingTubeBoltDetected, true);
        io.SetInput(InputIo.ShootingHeadVacuumDetected, false);
        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        var shoot = teaching.TeachingOutputs[OutputIo.ShootBolt];
        var outputs = new List<OutputIo>();
        io.OutputChanged += (output, _) => outputs.Add(output);
        Assert.True(shoot.HoldToRun);
        Assert.False(shoot.RequiresHandler);

        Action[] stopActions =
        [
            () => teaching.SetOutputOnCancelCommand.Execute(null),
            () => teaching.SelectedMotionGroup = MotionGroup.InspectionGantry,
            machine.Stop,
            teaching.Deactivate,
            () => io.SetInput(InputIo.AutoMode, false),
        ];
        foreach (var stop in stopActions)
        {
            teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
            await WaitUntilAsync(() => teaching.SetOutputOnCommand.CanExecute(shoot));
            var holding = teaching.SetOutputOnCommand.ExecuteAsync(shoot);
            Assert.True(io.GetOutput(OutputIo.ShootBolt));
            Assert.False(holding.IsCompleted);
            Assert.False(state.ManualSetupEnabled);
            Assert.False(machine.CanStart);
            stop();
            await holding.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(io.GetOutput(OutputIo.ShootBolt));
            io.SetInput(InputIo.AutoMode, true);
        }
        Assert.All(outputs, output => Assert.Equal(OutputIo.ShootBolt, output));
        Assert.Equal(MachineAlarm.None, state.Alarm);
        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => !teaching.SetOutputOnCommand.CanExecute(shoot));
    }

    [Fact]
    public async Task StationTeachingControlsOnlyItsOwnBackupPlate()
    {
        var settings = FlowSettings();
        settings.Units.NgCarrierTransfer = false;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        (MotionGroup Group, TeachingTarget Target, InputIo Up, InputIo Down, OutputIo Output)[] stations =
        [
            (MotionGroup.PcbPlacementHandler, TeachingTarget.HeatSink1PcbPlacement,
                InputIo.PcbPlacementBackupPlateUp, InputIo.PcbPlacementBackupPlateDown, OutputIo.PcbPlacementBackupPlateUp),
            (MotionGroup.BoltFastening, TeachingTarget.ShootingHeadUpperLeftLocatingPin,
                InputIo.BoltFasteningBackupPlateUp, InputIo.BoltFasteningBackupPlateDown, OutputIo.BoltFasteningBackupPlateUp),
            (MotionGroup.InspectionGantry, TeachingTarget.CarrierUpperLeftLocatingPin,
                InputIo.InspectionBackupPlateUp, InputIo.InspectionBackupPlateDown, OutputIo.InspectionBackupPlateUp),
        ];
        foreach (var (group, target, up, down, output) in stations)
        {
            teaching.SelectedMotionGroup = group;
            teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == target);
            var plate = teaching.TeachingOutputs[output];
            await WaitUntilAsync(() => !teaching.TeachCurrentPositionCommand.CanExecute(null));
            await WaitUntilAsync(() => !teaching.MoveToPointCommand.CanExecute(null));
            await WaitUntilAsync(() => teaching.SetOutputOnCommand.CanExecute(plate));
            await teaching.SetOutputOnCommand.ExecuteAsync(plate);
            Assert.True(io.GetInput(up));
            Assert.False(io.GetInput(down));
            foreach (var other in stations.Where(station => station.Group != group))
            {
                Assert.False(teaching.TeachingOutputs.ContainsKey(other.Output));
                Assert.False(io.GetInput(other.Up));
            }
            await teaching.SetOutputOffCommand.ExecuteAsync(plate);
            Assert.False(io.GetInput(up));
            Assert.True(io.GetInput(down));
        }

        await machine.HomeAsync(CancellationToken.None);
        var supply = services.GetRequiredKeyedService<IAxisMotion>(MotionGroup.PcbSupply);
        var state = services.GetRequiredService<MachineState>();
        await supply.MoveXAsync(settings.PcbSupply.BufferHandoffPosition.X, 1_000);
        teaching.SelectedMotionGroup = MotionGroup.PcbPlacementHandler;
        Assert.True(state.SupplyInBufferArea);
        await WaitUntilAsync(() => !teaching.SetOutputOnCommand.CanExecute(teaching.TeachingOutputs[OutputIo.PcbPlacementHandlerDown]));
        var placementPlate = teaching.TeachingOutputs[OutputIo.PcbPlacementBackupPlateUp];
        await WaitUntilAsync(() => teaching.SetOutputOnCommand.CanExecute(placementPlate));
        await teaching.SetOutputOnCommand.ExecuteAsync(placementPlate);
        await teaching.SetOutputOffCommand.ExecuteAsync(placementPlate);
        supply.SetServo(MotionAxis.X, false);
        Assert.False(state.ManualControlsEnabled);
        await WaitUntilAsync(() => teaching.SetOutputOnCommand.CanExecute(placementPlate));
        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => !teaching.SetOutputOnCommand.CanExecute(placementPlate));
        io.SetInput(InputIo.AutoMode, true);

        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        await WaitUntilAsync(() => !teaching.SetOutputOnCommand.CanExecute(teaching.TeachingOutputs[OutputIo.NgCarrierPickupDown]));
        settings.Options.TimeoutMilliseconds = 50;
        io.AutoResponseEnabled = false;
        await teaching.SetOutputOnCommand.ExecuteAsync(teaching.TeachingOutputs[OutputIo.InspectionBackupPlateUp]);
        Assert.Equal(MachineAlarm.MainConveyor, state.Alarm);
    }

    [Fact]
    public async Task PinTeachingEnablesCarrierScanWithoutImageReferenceTeaching()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.CarrierReference = new();
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.RecipeEditor.Name = $"PinTeaching-{Guid.NewGuid():N}";
        await WaitUntilAsync(() => !teaching.CaptureCarrierImagesCommand.CanExecute(null));
        Assert.Equal(TeachingTarget.CarrierUpperLeftLocatingPin, teaching.SelectedPoint!.Target);
        Assert.Equal(TeachingSaveBehavior.CameraCenter, teaching.SaveBehavior);
        await WaitUntilAsync(() => !teaching.TeachImagePointCommand.CanExecute(new System.Windows.Point(2, 3)));
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));

        await gantry.MoveToAsync(new() { X = 2, Y = 3 }, 1_000);
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        Assert.Equal((2, 3), (settings.CarrierReference.UpperLeftLocatingPin!.X,
            settings.CarrierReference.UpperLeftLocatingPin.Y));
        Assert.Equal(TeachingTarget.CarrierLowerRightLocatingPin, teaching.SelectedPoint!.Target);
        await WaitUntilAsync(() => !teaching.CaptureCarrierImagesCommand.CanExecute(null));

        await gantry.MoveToAsync(new() { X = 32, Y = 23 }, 1_000);
        await teaching.TeachCurrentPositionCommand.ExecuteAsync(null);
        Assert.Equal(new System.Windows.Point(2, 3), teaching.ImageOrigin);
        Assert.Equal(3, teaching.ImageMarkers.Count); // Two pins and the shared barcode at the selected PCB.
        await WaitUntilAsync(() => teaching.CaptureCarrierImagesCommand.CanExecute(null));
        teaching.AddBoltPointCommand.Execute(null);
        teaching.IsCameraLive = true;
        await WaitUntilAsync(() => teaching.CaptureCarrierImagesCommand.CanExecute(null));
        await teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);
        Assert.False(teaching.IsCameraLive);
        Assert.Null(teaching.CameraError);
        Assert.Equal(9, teaching.CarrierImages.Count);
        Assert.Equal(TeachingTarget.BoltReference, teaching.SelectedPoint!.Target);
        await WaitUntilAsync(() => teaching.TeachImagePointCommand.CanExecute(new System.Windows.Point(10, 10)));
        await WaitUntilAsync(() => !teaching.TeachCurrentPositionCommand.CanExecute(null));

        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.PcbRegion);
        await teaching.TeachImageRegionCommand.ExecuteAsync(new System.Windows.Rect(4, 5, 16, 18));
        teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
        await WaitUntilAsync(() => !teaching.TeachImageRegionCommand.CanExecute(System.Windows.Rect.Empty));
        await WaitUntilAsync(() => teaching.TeachImagePointCommand.CanExecute(new System.Windows.Point(22, 5)));
        await teaching.TeachImagePointCommand.ExecuteAsync(new System.Windows.Point(22, 5));
        teaching.SelectedPcb = HeatSinkSlot.HeatSink1;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltReference);
        teaching.MillimetersPerPixel = 0.025;
        await teaching.TeachImagePointCommand.ExecuteAsync(new System.Windows.Point(10, 10));
        var store = services.GetRequiredService<RecipeStore>();
        var saved = await store.LoadRecipeAsync(teaching.RecipeEditor.Name);
        Assert.Equal((6, 5), (saved.Pcb.BoltPoints[0].X, saved.Pcb.BoltPoints[0].Y));
        Assert.Equal(new PcbRegion(2, 2, 16, 18), saved.Pcb.GetRegion(HeatSinkSlot.HeatSink1));
        Assert.Equal(new PcbRegion(20, 2, 16, 18), saved.Pcb.GetRegion(HeatSinkSlot.HeatSink2));
        Assert.Single(saved.Pcb.BoltPoints);
        Assert.Equal(0.025, saved.CarrierImageMillimetersPerPixel);
        Assert.Equal(9, saved.CarrierImages.Count);

        await teaching.RecipeEditor.LoadCommand.ExecuteAsync(saved.Name);
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltReference);
        Assert.Equal((10, 10), (teaching.SelectedPoint.X, teaching.SelectedPoint.Y));
        teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
        Assert.Equal((28, 10), (teaching.SelectedPoint!.X, teaching.SelectedPoint.Y));
        await teaching.MoveToPointCommand.ExecuteAsync(null);
        Assert.Equal((28, 10, 0), gantry.Feedback.GetPosition());

        teaching.SelectedPcb = HeatSinkSlot.HeatSink1;
        await WaitUntilAsync(() => !teaching.TeachImagePointCommand.CanExecute(new System.Windows.Point(21, 10)));
        await teaching.TeachImagePointCommand.ExecuteAsync(new System.Windows.Point(28, 10));
        Assert.Equal(HeatSinkSlot.HeatSink2, teaching.SelectedPcb);
        Assert.Equal((6d, 5d), (teaching.SelectedPoint!.Position.Bolt!.Point.X, teaching.SelectedPoint.Position.Bolt.Point.Y));
        Assert.Equal($"{6d:F3}, {5d:F3}", teaching.SelectedPoint.PositionLabel);

        teaching.SelectedPcb = HeatSinkSlot.HeatSink1;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.DataMatrix);
        await WaitUntilAsync(() => !teaching.TeachImageRegionCommand.CanExecute(new System.Windows.Rect(18, 9, 8, 4)));
        await teaching.TeachImageRegionCommand.ExecuteAsync(new System.Windows.Rect(26, 9, 4, 4));
        Assert.Equal(HeatSinkSlot.HeatSink2, teaching.SelectedPcb);
        Assert.Equal($"{6d:F3}, {6d:F3}", teaching.SelectedPoint!.PositionLabel);
        Assert.Equal((28, 11), (teaching.SelectedPoint.X, teaching.SelectedPoint.Y));
        teaching.SelectedPcb = HeatSinkSlot.HeatSink1;
        Assert.Equal($"{6d:F3}, {6d:F3}", teaching.SelectedPoint!.PositionLabel);
        Assert.Equal((10, 11), (teaching.SelectedPoint.X, teaching.SelectedPoint.Y));
        Assert.Equal(new PcbRegion(4, 4, 4, 4),
            (await store.LoadRecipeAsync(teaching.RecipeEditor.Name)).Pcb.DataMatrix);

        await teaching.TeachImageRegionCommand.ExecuteAsync(new System.Windows.Rect(5, 7, 10, 4));
        Assert.Equal("Data Matrix region must fit inside one camera FOV.", teaching.CameraError);
        Assert.Equal((8, 6), (teaching.CameraFieldOfView!.Value.Width, teaching.CameraFieldOfView.Value.Height));

        teaching.MillimetersPerPixel = 0.05;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltReference);
        var selectedBolt = teaching.SelectedPoint;
        await teaching.CaptureInspectionCommand.ExecuteAsync(null);
        Assert.True(teaching.Preview.HasImage);
        Assert.NotNull(teaching.Preview.Result);
        await teaching.TeachImagePointCommand.ExecuteAsync(new System.Windows.Point(11, 10));
        Assert.Same(selectedBolt, teaching.SelectedPoint);
        Assert.False(teaching.Preview.HasImage);
        Assert.Null(teaching.Preview.Result);

        await teaching.CaptureInspectionCommand.ExecuteAsync(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            teaching.Preview.InspectAsync(new CancellationToken(true)));
        teaching.Preview.MinimumMaskPercent = 1;
        services.GetRequiredService<BoltTrainingSettings>().MaskThreshold = 0.6f;
        Assert.True(teaching.Preview.HasImage);
        Assert.Null(teaching.Preview.Result);
        Assert.Null(teaching.Preview.Overlay);
        await teaching.ReinspectImageCommand.ExecuteAsync(null);
        Assert.NotNull(teaching.Preview.Result);
        await teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);
        Assert.Same(selectedBolt, teaching.SelectedPoint);
        Assert.False(teaching.Preview.HasImage);
        Assert.Null(teaching.Preview.Result);

        teaching.SelectedPcb = HeatSinkSlot.HeatSink2;
        teaching.RecipeEditor.NewCommand.Execute(null);
        Assert.Equal(HeatSinkSlot.HeatSink1, teaching.SelectedPcb);
        await teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);
        Assert.Equal(TeachingTarget.PcbRegion, teaching.SelectedPoint!.Target);
        await WaitUntilAsync(() => teaching.TeachImageRegionCommand.CanExecute(System.Windows.Rect.Empty));
    }

    [Fact]
    public async Task CarrierScanKeepsSelectionChangedAfterItsImagesWereSaved()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 10, Y = 10 };
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.RecipeEditor.Name = $"SelectionAfterSave-{Guid.NewGuid():N}";
        var next = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.CarrierLowerRightLocatingPin);
        teaching.RecipeEditor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(RecipeEditor.ActiveName)) teaching.SelectedPoint = next;
        };

        await WaitUntilAsync(() => teaching.CaptureCarrierImagesCommand.CanExecute(null));
        await teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);

        Assert.Same(next, teaching.SelectedPoint);
        Assert.True(teaching.HasCarrierImages);
        var saved = await services.GetRequiredService<RecipeStore>().LoadRecipeAsync(teaching.RecipeEditor.ActiveName);
        Assert.Equal(teaching.CarrierImages.Count, saved.CarrierImages.Count);
        Assert.Null(teaching.CameraError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeachingSelectionIgnoresLateCameraCompletion(bool cameraFails)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        var capturing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var services = new ServiceCollection()
            .AddIbtmApplication(settings, new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
            .AddSingleton<ICamera>(new VirtualCamera(() =>
            {
                capturing.SetResult();
                release.Wait();
                if (cameraFails) throw new InvalidOperationException("Previous point capture failed.");
                return (0, 0, 0);
            }, () => []))
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.DataMatrix);
        await WaitUntilAsync(() => teaching.CaptureInspectionCommand.CanExecute(null));
        var next = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.CarrierUpperLeftLocatingPin);
        var recipeBefore = JsonSerializer.Serialize(teaching.RecipeEditor.Recipe);
        var settingsBefore = JsonSerializer.Serialize(settings);
        var capture = teaching.CaptureInspectionCommand.ExecuteAsync(null);
        try
        {
            await capturing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            teaching.SelectedPoint = next;
        }
        finally
        {
            release.Set();
        }
        await capture.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(next, teaching.SelectedPoint);
        Assert.False(teaching.Preview.HasImage);
        Assert.Null(teaching.Preview.Result);
        Assert.Null(teaching.CameraError);
        Assert.Equal(recipeBefore, JsonSerializer.Serialize(teaching.RecipeEditor.Recipe));
        Assert.Equal(settingsBefore, JsonSerializer.Serialize(settings));
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
    }

    [Fact]
    public async Task CarrierScanDeviceFailureStaysAtTheTeachingCommandBoundary()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        using var services = new ServiceCollection().AddSingleton<RecipeStore>()
            .AddIbtmApplication(settings)
            .AddSingleton<ICamera>(new VirtualCamera(
                () => throw new InvalidOperationException("Camera SDK capture failed."), () => []))
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.RecipeEditor.Name = $"CaptureFailure-{Guid.NewGuid():N}";
        await WaitUntilAsync(() => teaching.CaptureCarrierImagesCommand.CanExecute(null));
        await teaching.CaptureCarrierImagesCommand.ExecuteAsync(null);
        Assert.Equal("Camera SDK capture failed.", teaching.CameraError);
        Assert.False(services.GetRequiredService<MachineState>().IsRunning);
    }

    [Fact]
    public async Task FasteningBoltPositionsUseSharedSafeZWithoutDuplicateTeaching()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.BoltFastening.SafeZ = 5;
        using var services = CreateServices(settings);
        services.GetRequiredService<Recipe>().Pcb.BoltPoints =
            [new() { Number = 1, X = 10, Y = 15, Head = FasteningHead.Shooting }];
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point =>
            point.Target == TeachingTarget.BoltPosition);
        Assert.Equal(5, teaching.SelectedPoint.Z);
        Assert.True(machine.TeachingReady);
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
        await WaitUntilAsync(() => !teaching.TeachCurrentPositionCommand.CanExecute(null));
        await WaitUntilAsync(() => !teaching.AddBoltPointCommand.CanExecute(null));
        await WaitUntilAsync(() => !teaching.RemoveBoltPointCommand.CanExecute(null));
        await gantry.MoveZAsync(12);
        await teaching.MoveToPointCommand.ExecuteAsync(null);
        Assert.Equal((10, 15, 5), gantry.Feedback.GetPosition());
        settings.BoltFastening.SafeZ = 7;
        await teaching.MoveToPointCommand.ExecuteAsync(null);
        Assert.Equal((10, 15, 7), gantry.Feedback.GetPosition());
        io.SetInput(InputIo.ShootingHeadUp, false);
        io.SetInput(InputIo.ShootingHeadDown, true);
        await WaitUntilAsync(() => !teaching.MoveToPointCommand.CanExecute(null));
    }

    [Fact]
    public async Task RecipeEditsRequireIdleManualWhilePhysicalTeachingAlsoRequiresHome()
    {
        using var services = CreateServices(FlowSettings());
        services.GetRequiredService<Recipe>().Pcb.BoltPoints.Add(new BoltPoint
        {
            Number = 1, Head = FasteningHead.Shooting,
            X = 10, Y = 10,
        });
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltReference);
        await WaitUntilAsync(() => !teaching.CanEditTeaching);
        await WaitUntilAsync(() => teaching.AddBoltPointCommand.CanExecute(null));
        await WaitUntilAsync(() => teaching.RemoveBoltPointCommand.CanExecute(null));
        await WaitUntilAsync(() => !teaching.MoveToPointCommand.CanExecute(null));
        teaching.CarrierImages = [new(1, new(), InspectionPreview.CreateBitmap(
            services.GetRequiredService<VirtualCamera>().Capture(500, 0)))];
        await WaitUntilAsync(() => teaching.TeachImagePointCommand.CanExecute(new System.Windows.Point(10, 10)));

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => teaching.CanEditTeaching);
        await WaitUntilAsync(() => teaching.AddBoltPointCommand.CanExecute(null));
        await WaitUntilAsync(() => teaching.RemoveBoltPointCommand.CanExecute(null));
        teaching.RecipeEditor.Name = "";
        await WaitUntilAsync(() => !teaching.CaptureCarrierImagesCommand.CanExecute(null));
        await WaitUntilAsync(() => !teaching.TeachImagePointCommand.CanExecute(new System.Windows.Point(10, 10)));
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.PcbRegion);
        await WaitUntilAsync(() => !teaching.TeachImageRegionCommand.CanExecute(System.Windows.Rect.Empty));
        teaching.RecipeEditor.Name = "Named recipe";
        await WaitUntilAsync(() => teaching.TeachImageRegionCommand.CanExecute(System.Windows.Rect.Empty));
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltReference);
        using (services.GetRequiredService<OperationCancellation>().Link())
        {
            await WaitUntilAsync(() => !teaching.CanEditTeaching);
            await WaitUntilAsync(() => !teaching.AddBoltPointCommand.CanExecute(null));
            await WaitUntilAsync(() => !teaching.RemoveBoltPointCommand.CanExecute(null));
        }
        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => !teaching.CanEditTeaching);
        await WaitUntilAsync(() => !teaching.AddBoltPointCommand.CanExecute(null));
        await WaitUntilAsync(() => !teaching.RemoveBoltPointCommand.CanExecute(null));
        io.SetInput(InputIo.AutoMode, true);
        await WaitUntilAsync(() => teaching.CanEditTeaching);
        services.GetRequiredService<InspectionGantry>().SetServo(MotionAxis.X, false);
        await WaitUntilAsync(() => !teaching.CanEditTeaching);
        await WaitUntilAsync(() => teaching.RemoveBoltPointCommand.CanExecute(null));
        await WaitUntilAsync(() => !teaching.MoveToPointCommand.CanExecute(null));
    }

    [Fact]
    public async Task BufferSetupRequiresIdleManualControl()
    {
        var settings = FlowSettings();
        var store = new MachineStore(Path.Combine(Path.GetTempPath(), $"IBTM-buffer-teaching-{Guid.NewGuid():N}.db"));
        using var services = new ServiceCollection()
            .AddSingleton(store)
            .AddIbtmApplication(settings, new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<SupplyTeachingViewModel>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => teaching.SaveBufferSetupCommand.CanExecute(null));
        teaching.Points.Single(point => point.Target == TeachingTarget.SupplyBufferBoundary1).Teach(70, 0, 0);
        teaching.Points.Single(point => point.Target == TeachingTarget.PlacementBufferBoundary1).Teach(75, 25, 0);

        using (services.GetRequiredService<OperationCancellation>().Link())
        {
            await WaitUntilAsync(() => !teaching.SaveBufferSetupCommand.CanExecute(null));
            await teaching.SaveBufferSetupCommand.ExecuteAsync(null);
            Assert.Equal(60, settings.PcbBuffer.SupplyBoundary1);
        }
        await WaitUntilAsync(() => teaching.SaveBufferSetupCommand.CanExecute(null));

        io.SetInput(InputIo.AutoMode, false);
        await WaitUntilAsync(() => !teaching.SaveBufferSetupCommand.CanExecute(null));
        await teaching.SaveBufferSetupCommand.ExecuteAsync(null);
        Assert.Equal(60, settings.PcbBuffer.SupplyBoundary1);

        io.SetInput(InputIo.AutoMode, true);
        await WaitUntilAsync(() => teaching.SaveBufferSetupCommand.CanExecute(null));
        settings.Units.PcbSupply = false;
        await teaching.SaveBufferSetupCommand.ExecuteAsync(null);

        Assert.Null(teaching.SaveError);
        Assert.Equal(70, settings.PcbBuffer.SupplyBoundary1);
        Assert.Equal(75, settings.PcbBuffer.PlacementBoundary1.X);
        var saved = store.LoadSettings().Get<PcbBufferSettings>();
        Assert.Equal(70, saved.SupplyBoundary1);
        Assert.Equal(75, saved.PlacementBoundary1.X);
        Assert.Equal(25, saved.PlacementBoundary1.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferSetupCancelledBeforeExecutionDoesNotApply(bool closeTeaching)
    {
        var settings = FlowSettings();
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var teaching = services.GetRequiredService<SupplyTeachingViewModel>();
        var operations = services.GetRequiredService<OperationCancellation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => teaching.SaveBufferSetupCommand.CanExecute(null));
        teaching.Points.Single(point => point.Target == TeachingTarget.SupplyBufferBoundary1).Teach(70, 0, 0);

        void CancelWhenStarted()
        {
            if (!operations.HasActiveOperations) return;
            if (closeTeaching) teaching.Deactivate();
            else machine.Stop();
        }

        operations.ActivityChanged += CancelWhenStarted;
        await teaching.SaveBufferSetupCommand.ExecuteAsync(null);
        operations.ActivityChanged -= CancelWhenStarted;

        Assert.Equal(60, settings.PcbBuffer.SupplyBoundary1);
        Assert.Null(teaching.SaveError);
        Assert.False(operations.HasActiveOperations);
    }

    [Fact]
    public async Task NgTransferResumesCarryingWithoutReturningToPickup()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, true);
        io.SetInput(InputIo.AutoMode, false);
        void StopDuringTransfer(double x, double y, double z)
        {
            if (x > 40 && io.GetInput(InputIo.NgCarrierDetected))
            {
                machine.Stop();
            }
        }

        gantry.Feedback.PositionChanged += StopDuringTransfer;
        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        gantry.Feedback.PositionChanged -= StopDuringTransfer;
        var stoppedX = gantry.Feedback.GetPosition().X;
        Assert.InRange(stoppedX, 40, 149);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.True(io.GetInput(InputIo.NgCarrierGripperClosed));
        Assert.True(io.GetInput(InputIo.NgCarrierDetected));
        Assert.Equal(MachineAlarm.None, state.Alarm);

        var minimumX = stoppedX;
        gantry.Feedback.PositionChanged += (x, _, _) => minimumX = Math.Min(minimumX, x);
        var resumed = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => io.GetInput(InputIo.NgShuttleCarrierDetected)
                    && io.GetInput(InputIo.NgCarrierPickupUp)
                    && !io.GetInput(InputIo.NgCarrierDetected),
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            machine.Stop();
            await resumed;
        }

        Assert.True(minimumX >= stoppedX);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(NgTransferLiftState.Down)]
    [InlineData(NgTransferLiftState.Between)]
    [InlineData(NgTransferLiftState.Up)]
    public async Task ReleasedNgCarrierIsNotGrippedAgainOnRestart(NgTransferLiftState lift)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgCarrierTransfer);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var station = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToAsync(settings.NgCarrierTransfer.ShuttlePlacePosition, 10_000);
        io.AutoResponseEnabled = false;
        io.SetInput(InputIo.NgCarrierDetected, true);
        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        io.SetInput(InputIo.NgCarrierPickupDown, lift == NgTransferLiftState.Down);
        io.SetInput(InputIo.NgCarrierPickupUp, lift == NgTransferLiftState.Up);

        Assert.Equal(lift == NgTransferLiftState.Up
            ? InspectionStationState.Waiting
            : InspectionStationState.RaisingCarrierTransfer, station.State([]));

        using var stop = new CancellationTokenSource();
        var run = station.RunAsync([], stop.Token);
        Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));
        stop.Cancel();
        await run;

        if (lift != NgTransferLiftState.Up)
            await VerifyDryRunReleaseAsync(NgTransferState.Raising);

        // The carrier may leave the pickup sensor before the gripper reaches Open.
        io.SetInput(InputIo.NgCarrierDetected, false);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierGripperOpen, false);
        io.SetInput(InputIo.NgCarrierGripperClosed, false);
        Assert.Equal(InspectionStationState.OpeningTransferGripper, station.State([]));
        await VerifyDryRunReleaseAsync(NgTransferState.Opening);

        async Task VerifyDryRunReleaseAsync(NgTransferState expected)
        {
            var dryRun = services.GetRequiredService<NgTransferDryRun>();
            using var dryRunStop = new CancellationTokenSource();
            var dryRunTask = dryRun.RunAsync(dryRunStop.Token);
            try
            {
                Assert.Equal(NgTransferDestination.Shuttle, dryRun.Destination);
                Assert.Equal(expected, dryRun.State);
                Assert.False(io.GetOutput(OutputIo.NgCarrierGripperClose));
            }
            finally
            {
                dryRunStop.Cancel();
                await dryRunTask;
            }
        }
    }

    [Fact]
    public async Task MissingInspectionModelAllowsSetupButBlocksAutomaticStart()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Drivers.Inspection = InspectionAlgorithm.TinyUnet;
        using var services = new ServiceCollection()
            .AddSingleton<RecipeStore>()
            .AddIbtmApplication(settings, new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
            .AddSingleton(new BoltTrainingStore(Path.Combine(
                Path.GetTempPath(), $"IBTM-empty-training-{Guid.NewGuid():N}.db")))
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        services.GetRequiredService<Recipe>().Pcb.BoltPoints.Add(
            new BoltPoint { Number = 1, X = 10, Y = 10 });

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.ManualControlsEnabled);

        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.CanStart);
        await machine.StartAsync();
        Assert.Equal(MachineAlarm.Inspection, state.Alarm);
        Assert.False(state.IsRunning);

        io.SetInput(InputIo.AutoMode, true);
        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(state.ManualControlsEnabled);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public async Task InspectionAndConveyorAgreeOnBypassRoute(
        bool inspectionEnabled, bool transferEnabled, bool expectNg)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.Inspection = inspectionEnabled;
        settings.Units.NgCarrierTransfer = transferEnabled;
        using var services = CreateServices(settings);
        var io = services.GetRequiredService<VirtualIoService>();
        var work = services.GetRequiredService<InspectionWork>();
        var conveyor = services.GetRequiredService<MainConveyor>();
        var inspection = services.GetRequiredService<InspectionStation>();
        await services.GetRequiredService<MachineController>().InitializeAsync();
        io.SetInput(InputIo.InspectionCarrierPresent, true);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        io.SetInput(InputIo.MainConveyorReadyFromRear, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.InspectionBackupPlateUp, true);
        var assembly = work.Assembly(HeatSinkSlot.HeatSink1);
        assembly.RecordBoltPresence(1, true);
        assembly.CompleteInspection();
        work.Complete();

        Assert.Equal(expectNg, inspection.State([])
            == InspectionStationState.MovingTransferToCarrier);
        Assert.Equal(!expectNg, conveyor.State
            == MainConveyorState.DischargingInspectionCarrier);
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
            ? await Assert.ThrowsAsync<InvalidOperationException>(() => motion.JogXAsync(1))
            : await Assert.ThrowsAsync<InvalidOperationException>(() =>
                motion.MoveXAsync(100, 1));

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

        var moving = jog ? motion.JogXAsync(1) : motion.MoveXAsync(100, 1);

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
    public async Task AdcOperationKeepsMachineLockedUntilStopFinishes()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgConveyor),
        };
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var head = new StoppingBoltHead();
        await machine.InitializeAsync();

        var testing = machine.RunAdcProtocolAsync(
            token => head.TightenAsync(token),
            CancellationToken.None);
        Assert.True(state.IsRunning);
        machine.Stop();
        await head.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(state.IsRunning);
        Assert.False(machine.CanStart);
        Assert.False(machine.CanHome);
        Assert.False(machine.CanReset);
        await machine.StartAsync();
        Assert.False(state.AutomaticRunning);

        head.Stopped.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => testing);
        Assert.False(state.IsRunning);
        Assert.True(machine.CanStart);
    }

    [Fact]
    public async Task AutomaticAlarmWaitsForHeadStopAndKeepsFirstCause()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
        var head = new StoppingBoltHead();
        using var services = new ServiceCollection()
            .AddSingleton<RecipeStore>()
            .AddIbtmApplication(settings)
            .AddKeyedSingleton<IBoltHead>(FasteningHead.Shooting, head)
            .BuildServiceProvider();
        PrepareCarrierTeaching(settings, services.GetRequiredService<Recipe>());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
        io.SetInput(InputIo.BoltFasteningHeatSink1Present, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateUp, true);
        io.SetInput(InputIo.BoltFasteningBackupPlateDown, false);
        io.SetInput(InputIo.ShootingHeadVacuumDetected, true);
        io.SetInput(InputIo.AutoMode, false);

        var run = machine.StartAsync();
        await head.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        io.SetInput(InputIo.AirPressureLow, true);
        await head.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(run.IsCompleted);
        Assert.True(state.AutomaticRunning);
        Assert.True(state.IsRunning);
        Assert.False(machine.CanReset);
        Assert.Equal(MachineAlarm.AirPressureLow, state.Alarm);

        head.Stopped.SetException(new InvalidOperationException("Head stop failed."));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.AirPressureLow, state.Alarm);
        Assert.Null(state.AlarmMessage);
    }

    [Theory]
    [InlineData(HeatSinkLoad.Both, false)]
    [InlineData(HeatSinkLoad.None, false)]
    [InlineData(HeatSinkLoad.HeatSink1, true)]
    public async Task OneCarrierFlowsThroughTheWholeMachine(
        HeatSinkLoad heatSinkLoad,
        bool fasteningNg)
    {
        var settings = FlowSettings();
        using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<Recipe>();
        recipe.PcbSupply = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Z = 10 },
            Pcb2PickPosition = new() { X = 20, Z = 10 },
        };
        recipe.PcbPlacement = new PcbPlacementRecipe
        {
            HeatSink1PcbPlacementPosition = new()
            {
                X = 20,
                Y = 100,
                Z = 10,
            },
            HeatSink2PcbPlacementPosition = new()
            {
                X = 40,
                Y = 100,
                Z = 10,
            },
        };
        recipe.Pcb.Origins[HeatSinkSlot.HeatSink2] = new() { Y = 8 };
        recipe.Pcb.BoltPoints =
        [
            new() { Number = 1, Head = FasteningHead.Shooting, X = 12, Y = 11 },
            new() { Number = 2, Head = FasteningHead.Pickup, X = 28, Y = 11 },
        ];
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var inspection = services.GetRequiredService<InspectionWork>();
        var fastening = services.GetRequiredService<BoltFasteningWork>();
        var boltMotion = (VirtualMotionService)services
            .GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
        var fasteningCompletedAtSafeZ = false;
        fastening.Changed += () =>
        {
            if (fastening.Completed)
            {
                fasteningCompletedAtSafeZ |= boltMotion.IsAtHorizontalZ;
            }
        };
        var adc = Assert.IsType<VirtualAdcBus>(
            services.GetRequiredService<IAdcBus>());
        if (fasteningNg)
        {
            adc.SetNextFasteningResult(
                settings.Hantas.ShootingSlaveAddress,
                AdcEventStatus.FasteningNg);
        }

        var finished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedExit = false;
        HeatSinkAssembly[]? completedAssemblies = null;
        inspection.Changed += () =>
        {
            if (completedAssemblies is null && inspection.Completed)
            {
                completedAssemblies = inspection.Assemblies.ToArray();
            }
        };
        io.InputChanged += (input, value) =>
        {
            if (input == InputIo.PcbPlacementCarrierPresent && value)
            {
                io.SetInput(
                    InputIo.PcbPlacementHeatSink1Present,
                    (heatSinkLoad & HeatSinkLoad.HeatSink1) != 0);
                io.SetInput(
                    InputIo.PcbPlacementHeatSink2Present,
                    (heatSinkLoad & HeatSinkLoad.HeatSink2) != 0);
            }

            if (input == InputIo.MainConveyorExitCarrierDetected
                && value)
            {
                reachedExit = true;
            }

            var expectedNg = fasteningNg || heatSinkLoad == HeatSinkLoad.None;
            var okFinished = !expectedNg
                && reachedExit
                && !io.GetInput(
                    InputIo.MainConveyorExitCarrierDetected)
                && io.GetInput(InputIo.InspectionBackupPlateUp);
            var ngFinished = expectedNg
                && io.GetInput(InputIo.NgConveyorPosition1Occupied)
                && !io.GetInput(InputIo.NgShuttleCarrierDetected)
                && io.GetInput(InputIo.NgShuttleUp)
                && !io.GetOutput(OutputIo.NgConveyorRun);
            if (okFinished || ngFinished)
            {
                finished.TrySetResult();
                machine.Stop();
            }
        };
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await boltMotion.MoveZAsync(10, 20_000);
        io.SetInput(InputIo.AutoMode, false);

        var run = machine.StartAsync();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(state.IsRunning);
        var expectedHeatSinks = Enum.GetValues<HeatSinkSlot>()
            .Where(heatSink => heatSink switch
            {
                HeatSinkSlot.HeatSink1 =>
                    (heatSinkLoad & HeatSinkLoad.HeatSink1) != 0,
                HeatSinkSlot.HeatSink2 =>
                    (heatSinkLoad & HeatSinkLoad.HeatSink2) != 0,
                _ => false,
            })
            .ToArray();
        var assemblies = Assert.IsType<HeatSinkAssembly[]>(completedAssemblies);
        Assert.Equal(expectedHeatSinks.Length, assemblies.Length);
        foreach (var assembly in assemblies)
        {
            Assert.Contains(assembly.HeatSink, expectedHeatSinks);
            var assemblyNg = fasteningNg
                && assembly.HeatSink == expectedHeatSinks[0];
            Assert.Equal(
                !assemblyNg,
                Assert.Single(assembly.PcbBoltResults).Value.Success);
            Assert.True(Assert.Single(
                assembly.IpmSeatingResults).Value.Success);
            Assert.True(Assert.Single(
                assembly.IpmFinalResults).Value.Success);
            Assert.Equal(2, assembly.BoltPresenceResults.Count);
            Assert.All(assembly.BoltPresenceResults.Values, Assert.True);
            Assert.Equal(
                assemblyNg ? AssemblyResult.Ng : AssemblyResult.Ok,
                assembly.FasteningResult);
            Assert.Equal(AssemblyResult.Ok, assembly.InspectionResult);
            Assert.Equal(
                assemblyNg ? AssemblyResult.Ng : AssemblyResult.Ok,
                assembly.Result);
        }

        var expectedNg = fasteningNg || heatSinkLoad == HeatSinkLoad.None;
        Assert.Equal(!expectedNg, reachedExit);
        Assert.Equal(
            expectedNg,
            io.GetInput(InputIo.NgConveyorPosition1Occupied));
        Assert.True(fasteningCompletedAtSafeZ);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Station3ConfigurationRunsWithoutUpstreamHardware(bool missingBolts)
    {
        var settings = new MachineSettings
        {
            Units = new()
            {
                MainConveyor = true, Inspection = true, NgCarrierTransfer = true,
                NgShuttle = true, NgConveyor = true,
                PcbSupply = false, PcbPlacement = false, BoltFastening = false,
                PickupBoltFeeder = false, ShootingBoltFeeder = false,
            },
            Drivers = new() { Inspection = InspectionAlgorithm.Virtual },
        };
        FastHomes(settings);
        settings.InspectionGantry.Motion = FastMotion();
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 0 };
        settings.NgCarrierTransfer.CarrierPickupPosition = new() { X = 20, Y = 20 };
        settings.NgCarrierTransfer.ShuttlePlacePosition = new() { X = 100, Y = 20 };
        settings.NgCarrierTransfer.Speed = 10_000;
        using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<Recipe>();
        recipe.Pcb.BoltPoints =
        [
            new() { Number = 1, X = 10, Y = 10 },
        ];
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var inspection = services.GetRequiredService<InspectionWork>();
        IMotionFeedback[] disabledMotions =
        [
            services.GetRequiredService<PcbSupplyHandler>().Feedback,
            services.GetRequiredService<PcbPlacementHandler>().Feedback,
            services.GetRequiredService<BoltFasteningGantry>().Feedback,
        ];
        var disabledOutputs = settings.PcbSupplyHardware.Outputs.Keys
            .Concat(settings.PcbPlacementHandlerHardware.Outputs.Keys)
            .Concat(settings.BoltFasteningHardware.Outputs.Keys)
            .Concat(settings.BoltFeederHardware.Outputs.Keys).ToHashSet();
        var unexpectedOutputs = new ConcurrentBag<OutputIo>();
        var adcFrames = 0;
        services.GetRequiredService<IAdcBus>().FrameTransferred += (_, _) =>
            Interlocked.Increment(ref adcFrames);
        io.OutputChanged += (output, value) =>
        {
            if (value && disabledOutputs.Contains(output)) unexpectedOutputs.Add(output);
        };
        var arrived = 0;
        var entered = false;
        var exited = false;
        io.InputChanged += (input, value) =>
        {
            if (value)
            {
                Interlocked.Or(ref arrived, input switch
                {
                    InputIo.PcbPlacementCarrierPresent => 1,
                    InputIo.BoltFasteningCarrierPresent => 2,
                    InputIo.InspectionCarrierPresent => 4,
                    _ => 0,
                });
            }
            if (input == InputIo.MainConveyorEntryCarrierDetected && value) entered = true;
            if (input == InputIo.MainConveyorAvailableFromFront2 && value && entered)
                io.SetInput(input, false);
            if (input == InputIo.MainConveyorExitCarrierDetected && !value) exited = true;
        };
        if (missingBolts)
        {
            var camera = services.GetRequiredService<VirtualCamera>();
            camera.BoltsPresent = false;
        }

        await machine.InitializeAsync();
        Assert.True(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.MainConveyorReadyFromRear, false);
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.CanStart);
        var run = machine.StartAsync();
        try
        {
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => inspection.Completed, TimeSpan.FromSeconds(10)));
            Assert.Equal(7, arrived);
            Assert.Equal(missingBolts, inspection.HasNg);
            Assert.Equal(2, inspection.Assemblies.Count());
            Assert.All(inspection.Assemblies, assembly =>
            {
                Assert.Equal(assembly.HeatSink == HeatSinkSlot.HeatSink1 ? "PCB-1" : "PCB-2", assembly.PcbBarcode);
                Assert.Empty(assembly.PcbBoltResults);
                Assert.Empty(assembly.IpmSeatingResults);
                Assert.Empty(assembly.IpmFinalResults);
                Assert.Equal(!missingBolts, Assert.Single(assembly.BoltPresenceResults).Value);
            });
            if (missingBolts)
            {
                Assert.True(await VirtualTest.WaitUntilAsync(
                    () => io.GetInput(InputIo.NgConveyorPosition1Occupied)
                        && io.GetInput(InputIo.NgShuttleUp) && !io.GetOutput(OutputIo.NgConveyorRun),
                    TimeSpan.FromSeconds(5)));
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
        Assert.All(disabledMotions, motion => Assert.All(motion.Axes, axis =>
        {
            Assert.False(motion.GetAxisState(axis).ServoOn);
            Assert.False(motion.GetAxisState(axis).Homed);
        }));
    }

    [Fact]
    public async Task ManualJogFaultStopsTheMachineAndAllowsReset()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgCarrierTransfer),
        };
        FastHomes(settings);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);

        var fault = new InvalidOperationException("Jog feedback failed.");
        var failed = 0;
        gantry.Feedback.PositionChanged += (_, _, _) =>
        {
            if (gantry.Feedback.IsMoving && Interlocked.Exchange(ref failed, 1) == 0)
            {
                io.SetOutput(OutputIo.NgConveyorRun, true);
                throw fault;
            }
        };
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        teaching.SelectedMotionGroup = MotionGroup.InspectionGantry;
        await WaitUntilAsync(() => teaching.JogCommand.CanExecute(TeachingDirection.XPlus));
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.Contains(fault.Message, state.AlarmDetail);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.False(io.GetOutput(OutputIo.NgConveyorRun));

        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.Null(state.AlarmDetail);
        using var stopped = new CancellationTokenSource();
        var jog = gantry.JogAsync(MotionAxis.X, 10, stopped.Token);
        Assert.True(gantry.Feedback.IsMoving);
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jog.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task HomeRequiresEmptyEquipmentAndRaisedCylindersWithoutChangingOutputs()
    {
        var settings = FlowSettings();
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        await machine.InitializeAsync();
        Assert.True(machine.CanHome);
        Assert.False(state.ManualControlsEnabled);
        Assert.True(state.ManualSetupEnabled);
        io.SetInput(InputIo.AutoMode, false);
        Assert.False(state.ManualSetupEnabled);
        io.SetInput(InputIo.AutoMode, true);
        using (services.GetRequiredService<OperationCancellation>().Link())
            Assert.False(state.ManualSetupEnabled);
        Assert.True(state.ManualSetupEnabled);
        var outputsChanged = 0;
        io.OutputChanged += (_, _) => outputsChanged++;

        foreach (var input in new[]
        {
            InputIo.MainConveyorEntryCarrierDetected, InputIo.PcbPlacementCarrierPresent,
            InputIo.BoltFasteningCarrierPresent, InputIo.InspectionCarrierPresent,
            InputIo.MainConveyorExitCarrierDetected, InputIo.NgCarrierDetected,
            InputIo.NgShuttleCarrierDetected, InputIo.NgConveyorPosition1Occupied,
            InputIo.NgConveyorPosition2Occupied,
        })
        {
            await WaitUntilAsync(() => state.Display is
                { HomeBlock: HomeBlockReason.None, HomeableAxes.Count: > 0 });
            io.SetInput(input, true);
            Assert.Equal(HomeBlockReason.CarrierDetected, machine.HomeBlock);
            Assert.False(machine.CanHome);
            await WaitUntilAsync(() => state.Display is
                { HomeBlock: HomeBlockReason.CarrierDetected, HomeableAxes.Count: 0 });
            Assert.All(manual.Axes, axis => Assert.False(manual.HomeAxisCommand.CanExecute(axis)));
            await machine.HomeAsync(CancellationToken.None);
            await manual.HomeAxisCommand.ExecuteAsync(manual.Axes[3]);
            io.SetInput(input, false);
        }

        foreach (var (up, down, reason) in new[]
        {
            (InputIo.PcbPlacementHandlerUp, InputIo.PcbPlacementHandlerDown, HomeBlockReason.PlacementNotRaised),
            (InputIo.PickupHeadUp, InputIo.PickupHeadDown, HomeBlockReason.FasteningNotRaised),
            (InputIo.ShootingHeadUp, InputIo.ShootingHeadDown, HomeBlockReason.FasteningNotRaised),
            (InputIo.NgCarrierPickupUp, InputIo.NgCarrierPickupDown, HomeBlockReason.NgPickupNotRaised),
        })
        {
            io.SetInput(up, false);
            Assert.Equal(reason, machine.HomeBlock);
            Assert.False(machine.CanHome);
            await machine.HomeAsync(CancellationToken.None);
            io.SetInput(down, true);
            io.SetInput(up, true);
            Assert.Equal(reason, machine.HomeBlock);
            io.SetInput(down, false);
            Assert.True(machine.CanHome);
        }
        Assert.Equal(0, outputsChanged);

        settings.Units.PcbSupply = settings.Units.PcbPlacement = settings.Units.BoltFastening = false;
        io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        io.SetInput(InputIo.PickupHeadUp, false);
        io.SetInput(InputIo.NgShuttleUp, false);
        io.SetInput(InputIo.NgShuttleDown, true);
        Assert.True(machine.CanHome);
        services.GetRequiredService<MachineState>().RequestDisplayRefresh();
        await WaitUntilAsync(() => manual.HomeAxisCommand.CanExecute(manual.Axes[9]));
        Assert.False(manual.HomeAxisCommand.CanExecute(manual.Axes[3]));
        Assert.True(manual.HomeAxisCommand.CanExecute(manual.Axes[9]));
        io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        Assert.False(machine.CanHome);
    }

    [Fact]
    public async Task RaiseCylindersPreparesHomeWithoutMovingAxesOrOtherActuators()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        IIoService signals = io;
        await machine.InitializeAsync();
        OutputIo[] cylinders =
        [
            OutputIo.PcbPlacementHandlerDown,
            OutputIo.PickupHeadDown,
            OutputIo.ShootingHeadDown, OutputIo.NgCarrierPickupDown,
        ];
        await Task.WhenAll(cylinders.Select(output => signals.SetOutputAndWaitAsync(output, true)));
        await signals.SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
        var motions = new[]
        {
            services.GetRequiredService<PcbSupplyHandler>().Feedback,
            services.GetRequiredService<PcbPlacementHandler>().Feedback,
            services.GetRequiredService<BoltFasteningGantry>().Feedback,
            services.GetRequiredService<InspectionGantry>().Feedback,
        };
        var moved = false;
        foreach (var motion in motions) motion.MovingChanged += moving => moved |= moving;
        var outputChanges = new ConcurrentQueue<OutputIo>();
        io.OutputChanged += (output, _) => outputChanges.Enqueue(output);

        io.SetInput(InputIo.NgShuttleCarrierDetected, true);
        Assert.False(machine.CanRaiseCylinders);
        await machine.RaiseCylindersAsync(CancellationToken.None);
        Assert.Empty(outputChanges);
        io.SetInput(InputIo.NgShuttleCarrierDetected, false);
        Assert.True(machine.CanRaiseCylinders);
        Assert.False(machine.CanHome);

        var raising = machine.RaiseCylindersAsync(CancellationToken.None);
        Assert.True(state.IsRunning);
        Assert.False(state.IsHoming);
        Assert.False(state.ManualSetupEnabled);
        Assert.False(machine.CanHome);
        await raising;
        Assert.True(machine.CanHome);
        Assert.False(state.Homed);
        Assert.False(moved);
        Assert.Equal(cylinders.Order(), outputChanges.Order());
        Assert.All(cylinders, output => Assert.False(io.GetOutput(output)));
        Assert.True(io.GetOutput(OutputIo.PcbPlacementIpmDown));
        Assert.True(io.GetInput(InputIo.PcbPlacementIpmDown));
        await machine.RaiseCylindersAsync(CancellationToken.None);
        Assert.True(machine.CanHome);
    }

    [Fact]
    public async Task RaiseCylindersUsesEnabledUnitsAndKeepsOutputsOnStopOrTimeout()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgCarrierTransfer),
        };
        FastHomes(settings);
        settings.Options.TimeoutMilliseconds = 100;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.NgCarrierPickupDown, true);
        io.SetOutput(OutputIo.PcbPlacementHandlerDown, true);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        var raising = machine.RaiseCylindersAsync(CancellationToken.None);
        machine.Stop();
        await raising;
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.True(io.GetOutput(OutputIo.PcbPlacementHandlerDown));
        Assert.False(machine.CanHome);

        await machine.RaiseCylindersAsync(CancellationToken.None);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.False(state.IsRunning);
        Assert.False(state.Homed);
    }

    [Fact]
    public async Task InspectionHomeRequiresReleasedCarrierAndRaisedPickupBeforeXy()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgCarrierTransfer),
        };
        FastHomes(settings);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionGantry>();
        IIoService signals = io;
        await machine.InitializeAsync();
        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierGripperClose, true);
        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, true);
        io.SetInput(InputIo.NgCarrierDetected, true);

        Assert.False(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gantry.HomeAxisAsync(MotionAxis.X));
        Assert.True(io.GetOutput(OutputIo.NgCarrierGripperClose));
        Assert.False(state.Homed);

        io.SetInput(InputIo.NgCarrierDetected, false);
        Assert.False(machine.CanHome);
        Assert.False(gantry.CanMove);
        await machine.HomeAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gantry.HomeAxisAsync(MotionAxis.X));
        Assert.False(state.Homed);
        Assert.True(io.GetOutput(OutputIo.NgCarrierPickupDown));
        Assert.True(io.GetOutput(OutputIo.NgCarrierGripperClose));

        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierGripperClose, false);
        await signals.SetOutputAndWaitAsync(OutputIo.NgCarrierPickupDown, false);
        Assert.True(machine.CanHome);

        var unsafeMovement = false;
        gantry.Feedback.MovingChanged += moving =>
        {
            if (moving && state.IsHoming)
            {
                unsafeMovement |= !gantry.CanMove || !gantry.CanHome
                    || !io.GetInput(InputIo.NgCarrierGripperOpen);
            }
        };
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Homed);
        Assert.False(unsafeMovement);
        Assert.True(gantry.CanMove);

        io.SetInput(InputIo.NgCarrierPickupUp, false);
        io.SetInput(InputIo.NgCarrierPickupDown, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gantry.JogAsync(MotionAxis.X, 10));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 100));
        io.SetInput(InputIo.NgCarrierPickupDown, false);
        io.SetInput(InputIo.NgCarrierPickupUp, true);
        var moving = gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 10);
        await WaitUntilAsync(() => gantry.Feedback.IsMoving);
        io.SetInput(InputIo.NgCarrierPickupUp, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moving);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.NgCarrierTransfer, state.Alarm);

        io.SetInput(InputIo.NgCarrierPickupUp, true);
        await machine.ResetAsync();
        await gantry.MoveToAsync(new AxisPosition { X = 20, Y = 10 }, 1000);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public void UnitSettingsRemainLiveAfterComposition()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgConveyor),
        };
        using var services = CreateServices(settings);
        var state = services.GetRequiredService<MachineState>();

        Assert.Same(
            settings.Units,
            services.GetRequiredService<UnitSettings>());
        Assert.True(state.Homed);

        settings.Units.NgCarrierTransfer = true;
        Assert.False(state.Homed);

        settings.Units.NgCarrierTransfer = false;
        Assert.True(state.Homed);
    }

    [Theory]
    [InlineData(MotionGroup.PcbPlacementHandler, InputIo.PcbPlacementHandlerUp, InputIo.PcbPlacementHandlerDown)]
    [InlineData(MotionGroup.BoltFastening, InputIo.PickupHeadUp, InputIo.PickupHeadDown)]
    [InlineData(MotionGroup.BoltFastening, InputIo.ShootingHeadUp, InputIo.ShootingHeadDown)]
    public async Task HorizontalMotionRequiresRaisedCylindersWhileZCanRetract(
        MotionGroup group, InputIo up, InputIo down)
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.Motion.HorizontalSpeed = 10;
        settings.BoltFastening.Motion.HorizontalSpeed = 10;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        var fastening = services.GetRequiredService<BoltFasteningGantry>();
        var isPlacement = group == MotionGroup.PcbPlacementHandler;
        var feedback = isPlacement ? placement.Feedback : fastening.Feedback;
        Task MoveXY() => isPlacement
            ? placement.MoveToXYAsync(20, 20)
            : fastening.MoveToXYAsync(20, 20);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.AutoResponseEnabled = false;

        io.SetInput(up, false);
        io.SetInput(down, true);
        if (isPlacement) await placement.MoveZAsync(1);
        else await fastening.MoveZAsync(1);
        Assert.Equal(1, feedback.GetPosition().Z);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await Assert.ThrowsAsync<InvalidOperationException>(MoveXY);
        await Assert.ThrowsAsync<InvalidOperationException>(() => isPlacement
            ? placement.HomeAxisAsync(MotionAxis.X)
            : fastening.HomeAxisAsync(MotionAxis.X));
        if (isPlacement)
            await Assert.ThrowsAsync<InvalidOperationException>(() => placement.JogAsync(MotionAxis.Y, 10));

        io.SetInput(up, true);
        await Assert.ThrowsAsync<InvalidOperationException>(MoveXY);
        io.SetInput(down, false);
        var move = MoveXY();
        await WaitUntilAsync(() => feedback.IsMovingHorizontal);
        io.SetInput(up, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.False(feedback.IsMoving);
        Assert.False(feedback.IsMovingHorizontal);
        Assert.Equal(isPlacement ? MachineAlarm.PcbPlacement : MachineAlarm.BoltFastening, state.Alarm);
    }

    [Fact]
    public async Task PlacementIpmDoesNotRestrictHomeOrHorizontalTravel()
    {
        var settings = FlowSettings();
        settings.PcbPlacementHandler.Motion.HorizontalSpeed = 100;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        await machine.InitializeAsync();
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
        Assert.True(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Homed);
        Assert.True(placement.CanMoveHorizontal);
        Assert.True(io.GetInput(InputIo.PcbPlacementIpmDown));

        var move = placement.MoveToXYAsync(20, 20);
        await WaitUntilAsync(() => placement.Feedback.IsMovingHorizontal);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, false);
        await move;

        Assert.Equal((20, 20, settings.PcbPlacementHandler.BufferEntryZ), placement.Feedback.GetPosition());
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task ManualBoltPickupAndReturnPreserveOrderAndVacuumOnStopAndRetry()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        settings.BoltFastening.SafeZ = 5;
        settings.BoltFastening.PickupPosition = new() { X = 40, Y = 30, Z = 12 };
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(14);
        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltPickup);
        Assert.Equal(TeachingSaveBehavior.BoltPickup, teaching.SaveBehavior);
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));

        io.AutoResponseEnabled = false;
        io.SetOutput(OutputIo.PickupHeadVacuumPump, true);
        var vacuumChanged = false;
        var movedXyWithHeadDown = false;
        var loweredAt = new ConcurrentQueue<(double X, double Y, double Z)>();
        io.OutputChanged += (output, value) =>
        {
            if (output is OutputIo.PickupHeadVacuumPump or OutputIo.ShootingHeadVacuumPump)
                vacuumChanged = true;
            if (output == OutputIo.PickupHeadDown && value)
                loweredAt.Enqueue(gantry.Feedback.GetPosition());
        };
        gantry.Feedback.PositionChanged += (_, _, _) =>
            movedXyWithHeadDown |= gantry.Feedback.IsMovingHorizontal && !gantry.CanMoveHorizontal;

        var move = teaching.MoveToPointCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => io.GetOutput(OutputIo.PickupHeadDown));
        Assert.Equal((40, 30, 5), gantry.Feedback.GetPosition());
        Assert.False(move.IsCompleted);
        Assert.False(io.GetInput(InputIo.PickupHeadDown));
        teaching.JogStopCommand.Execute(null);
        await move.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((40, 30, 5), gantry.Feedback.GetPosition());
        Assert.False(gantry.Feedback.IsMoving);
        Assert.True(io.GetOutput(OutputIo.PickupHeadDown)); // Stop keeps pneumatic outputs.

        var retry = teaching.MoveToPointCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => io.GetOutput(OutputIo.PickupHeadDown));
        Assert.False(retry.IsCompleted);
        io.SetInput(InputIo.PickupHeadUp, false);
        io.SetInput(InputIo.PickupHeadDown, true);
        await retry.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((40, 30, 12), gantry.Feedback.GetPosition());
        Assert.Equal(BoltCylinderState.Down, gantry.PickupHeadPosition);
        Assert.Equal(BoltCylinderState.Up, gantry.ShootingHeadPosition);
        await WaitUntilAsync(() => teaching.ReturnFromPickupCommand.CanExecute(null));
        var stopAtSafeZ = true;
        gantry.Feedback.PositionChanged += (_, _, z) =>
        {
            if (stopAtSafeZ && Math.Abs(z - settings.BoltFastening.SafeZ) <= MotionService.PositionToleranceMillimeters)
            {
                stopAtSafeZ = false;
                teaching.JogStopCommand.Execute(null);
            }
        };
        await teaching.ReturnFromPickupCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(stopAtSafeZ);
        Assert.Equal((40, 30, 5), gantry.Feedback.GetPosition());
        Assert.True(io.GetOutput(OutputIo.PickupHeadDown)); // Cancellation must not advance to Head Up.
        Assert.False(gantry.Feedback.IsMoving);

        var returning = teaching.ReturnFromPickupCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => !io.GetOutput(OutputIo.PickupHeadDown));
        Assert.Equal((40, 30, 5), gantry.Feedback.GetPosition());
        Assert.False(returning.IsCompleted); // Up DO alone is not completion.
        io.SetInput(InputIo.PickupHeadDown, false);
        io.SetInput(InputIo.PickupHeadUp, true);
        await returning.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(gantry.CanMoveHorizontal);
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
        Assert.True(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
        Assert.False(vacuumChanged);
        Assert.False(movedXyWithHeadDown);
        Assert.Equal(2, loweredAt.Count);
        Assert.All(loweredAt, position => Assert.Equal((40, 30, 5), position));
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualPickupAndReturnReportCylinderTimeout(bool returning)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.BoltFastening);
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltPickup);
        if (returning) await gantry.MoveToPickupPositionAsync();
        io.AutoResponseEnabled = false;
        settings.Options.TimeoutMilliseconds = 50;

        await (returning ? teaching.ReturnFromPickupCommand : teaching.MoveToPointCommand).ExecuteAsync(null);

        Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(settings.BoltFastening.SafeZ, gantry.Feedback.GetPosition().Z);
        Assert.False(io.GetOutput(OutputIo.PickupHeadVacuumPump));
        Assert.False(io.GetOutput(OutputIo.ShootingHeadVacuumPump));
    }

    [Fact]
    public async Task FasteningTeachingAdjustsOneAxisWithHeadsDownAndPreservesPositioningRules()
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        var teaching = services.GetRequiredService<StationTeachingViewModel>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveToXYAsync(20, 20);
        await gantry.MoveZAsync(10);
        await Task.WhenAll(
            ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PickupHeadDown, true),
            ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingHeadDown, true));
        teaching.SelectedMotionGroup = MotionGroup.BoltFastening;
        teaching.SelectedPoint = teaching.FilteredPoints.Single(point => point.Target == TeachingTarget.BoltPickup);
        teaching.JogSpeed = 1;
        teaching.StepDistance = 0.1;
        await WaitUntilAsync(() => !teaching.MoveToPointCommand.CanExecute(null));
        await WaitUntilAsync(() => teaching.TeachCurrentPositionCommand.CanExecute(null));
        Assert.Equal(HomeBlockReason.FasteningNotRaised, machine.HomeBlock);
        await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
        await teaching.StepCommand.ExecuteAsync(TeachingDirection.YMinus);
        var adjusted = gantry.Feedback.GetPosition();
        Assert.Equal(20.1, adjusted.X, 6);
        Assert.Equal(19.9, adjusted.Y, 6);
        Assert.Equal(10, adjusted.Z);
        var jog = teaching.JogCommand.ExecuteAsync(TeachingDirection.XPlus);
        await WaitUntilAsync(() => gantry.Feedback.GetPosition().X > 20.1);
        Assert.Equal(MotionCommand.Adjustment, gantry.Feedback.Command);
        teaching.JogStopCommand.Execute(null);
        await jog.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
        var stopped = gantry.Feedback.GetPosition();
        await Task.Delay(30);
        Assert.Equal(stopped, gantry.Feedback.GetPosition());
        Assert.Equal(adjusted.Y, stopped.Y);
        Assert.Equal(10, stopped.Z);
        Assert.True(io.GetInput(InputIo.PickupHeadDown));
        Assert.True(io.GetInput(InputIo.ShootingHeadDown));
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gantry.MoveToXYAsync(30, 30));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gantry.HomeHorizontalAsync());

        var maximum = gantry.Feedback.GetRange(MotionAxis.X)!.Value.Maximum;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gantry.AdjustAxisAsync(MotionAxis.X, maximum + 1, 100));
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
        await gantry.AdjustAxisAsync(MotionAxis.X, maximum - 0.1, 1000);
        await gantry.JogAsync(MotionAxis.X, 10).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((maximum, stopped.Y, stopped.Z), gantry.Feedback.GetPosition());
        await WaitUntilAsync(() => !teaching.StepCommand.CanExecute(TeachingDirection.XPlus));

        await gantry.RaiseCylindersAsync();
        await WaitUntilAsync(() => teaching.MoveToPointCommand.CanExecute(null));
        MotionCommand positioning = MotionCommand.None;
        gantry.Feedback.MovingChanged += moving =>
        {
            if (moving) positioning = gantry.Feedback.Command;
        };
        await gantry.MoveToXYAsync(maximum - 1, stopped.Y);
        Assert.Equal(MotionCommand.Positioning, positioning);
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);

        var fail = true;
        gantry.Feedback.PositionChanged += (_, _, _) =>
        {
            if (!fail) return;
            fail = false;
            throw new MotionException("Injected teaching move", new IOException());
        };
        await teaching.JogCommand.ExecuteAsync(TeachingDirection.XMinus);
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FasteningAdjustmentStopsOnModeOrServoLoss(bool autoMode)
    {
        using var services = CreateServices(FlowSettings());
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(10);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.ShootingHeadDown, true);
        var jog = gantry.JogAsync(MotionAxis.X, 1);
        await WaitUntilAsync(() => gantry.Feedback.GetPosition().X > 0);
        if (autoMode) io.SetInput(InputIo.AutoMode, false);
        else gantry.SetServo(MotionAxis.X, false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jog.WaitAsync(TimeSpan.FromSeconds(2)));
        var stopped = gantry.Feedback.GetPosition();
        Assert.Equal(MotionCommand.None, gantry.Feedback.Command);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gantry.AdjustAxisAsync(MotionAxis.X, 20, 1));
        Assert.Equal(stopped, gantry.Feedback.GetPosition());
        Assert.True(io.GetInput(InputIo.ShootingHeadDown));
    }

    [Fact]
    public async Task CylinderFeedbackIsRecheckedBetweenTravelZAndXy()
    {
        var settings = FlowSettings();
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<BoltFasteningGantry>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await gantry.MoveZAsync(10);
        var before = gantry.Feedback.GetPosition();
        io.AutoResponseEnabled = false;
        gantry.Feedback.MovingChanged += moving =>
        {
            if (moving && !gantry.Feedback.IsMovingHorizontal)
                io.SetInput(InputIo.PickupHeadUp, false);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gantry.MoveToXYAsync(20, 20));

        var after = gantry.Feedback.GetPosition();
        Assert.Equal(before.X, after.X);
        Assert.Equal(before.Y, after.Y);
        Assert.Equal(settings.BoltFastening.SafeZ, after.Z);
        Assert.False(gantry.Feedback.IsMoving);
        Assert.Equal(MachineAlarm.BoltFastening, state.Alarm);
    }

    [Fact]
    public async Task EachUnitCanRunByItself()
    {
        foreach (var unit in Enum.GetValues<MachineUnit>())
        {
            var settings = new MachineSettings
            {
                Units = EnableOnly(unit),
                Drivers = new()
                {
                    Inspection = InspectionAlgorithm.Virtual,
                },
            };
            FastHomes(settings);
            using var services = CreateServices(settings);
            if (unit is MachineUnit.BoltFastening or MachineUnit.Inspection)
            {
                PrepareCarrierTeaching(
                    settings,
                    services.GetRequiredService<Recipe>());
            }

            var machine = services.GetRequiredService<MachineController>();
            var state = services.GetRequiredService<MachineState>();
            var io = services.GetRequiredService<VirtualIoService>();

            await machine.InitializeAsync();
            if (machine.CanHome)
            {
                await machine.HomeAsync(CancellationToken.None);
            }

            if (unit == MachineUnit.MainConveyor)
            {
                io.SetInput(InputIo.NgCarrierPickupUp, false);
                io.SetInput(InputIo.NgCarrierPickupDown, true);
                io.SetInput(InputIo.PcbPlacementCarrierPresent, true);
                io.SetInput(InputIo.BoltFasteningCarrierPresent, true);
                await ((IIoService)io).SetOutputAndWaitAsync(
                    OutputIo.PcbPlacementBackupPlateUp,
                    true);
                await ((IIoService)io).SetOutputAndWaitAsync(
                    OutputIo.BoltFasteningBackupPlateUp,
                    true);
                Assert.False(services.GetRequiredService<InspectionWork>().CanReceive);
            }

            io.SetInput(InputIo.AutoMode, false);
            Assert.True(machine.CanStart);

            var run = machine.StartAsync();
            await WaitUntilAsync(() => state.AutomaticRunning);
            if (unit == MachineUnit.MainConveyor)
            {
                Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
                Assert.False(io.GetInput(InputIo.InspectionCarrierPresent));
                io.SetInput(InputIo.NgCarrierPickupDown, false);
                io.SetInput(InputIo.NgCarrierPickupUp, true);
                await ((IIoService)io).WaitForInputAsync(
                    InputIo.InspectionCarrierPresent,
                    true);
                var gantry = services.GetRequiredService<InspectionGantry>();
                Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).ServoOn);
                Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).Homed);
            }

            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(MachineAlarm.None, state.Alarm);
            Assert.False(state.IsRunning);
        }
    }

    [Theory]
    [InlineData(MachineUnit.PcbSupply, MotionGroup.PcbSupply)]
    [InlineData(MachineUnit.PcbPlacement, MotionGroup.PcbPlacementHandler)]
    [InlineData(MachineUnit.BoltFastening, MotionGroup.BoltFastening)]
    [InlineData(MachineUnit.Inspection, MotionGroup.InspectionGantry)]
    [InlineData(MachineUnit.NgCarrierTransfer, MotionGroup.InspectionGantry)]
    public async Task OnlyEnabledMotionsAreInitializedReadResetAndHomed(MachineUnit unit, MotionGroup group)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(unit);
        using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        foreach (var (candidate, probe) in probes)
        {
            if (candidate == group) continue;
            // Also simulate an initialized drive that was subsequently disabled.
            probe.ReportReady = true;
            probe.FailHardwareCalls = true;
        }

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(state.Display.Available);
        Assert.False(state.Faulted);
        Assert.True(machine.CanHome);
        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Ready);
        Assert.True(state.ManualControlsEnabled);

        probes[group].Motion.SetServo(MotionAxis.X, false);
        Assert.True(machine.CanReset);
        await machine.ResetAsync();
        Assert.True(state.Ready);
        Assert.Equal(1, probes[group].ResetCalls);

        if (group == MotionGroup.BoltFastening)
        {
            var teaching = services.GetRequiredService<StationTeachingViewModel>();
            teaching.SelectedMotionGroup = group;
            teaching.StepDistance = 0.1;
            await WaitUntilAsync(() => teaching.StepCommand.CanExecute(TeachingDirection.XPlus));
            var before = probes[group].Motion.GetPosition();
            await teaching.StepCommand.ExecuteAsync(TeachingDirection.XPlus);
            Assert.Equal(before.X + 0.1, probes[group].Motion.GetPosition().X, precision: 6);
        }

        if (group is MotionGroup.PcbSupply or MotionGroup.PcbPlacementHandler)
        {
            var buffer = services.GetRequiredService<BufferStage>();
            Assert.False(buffer.CanSupplyLower);
            Assert.False(buffer.CanPlacementEnter);
        }

        foreach (var row in manual.Axes.Where(row => row.Group != group))
        {
            Assert.False(manual.ToggleServoCommand.CanExecute(row));
            Assert.False(manual.HomeAxisCommand.CanExecute(row));
            Assert.Null(row.Feedback);
            // Bypassing CanExecute still must not command a disabled drive.
            manual.ToggleServoCommand.Execute(row);
            await manual.HomeAxisCommand.ExecuteAsync(row);
        }
        Assert.All(probes.Where(item => item.Key != group),
            item => Assert.Equal(0, item.Value.HardwareCalls));
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Fact]
    public async Task DisablingAFailedMotionAllowsResetWithoutHidingAnEnabledMotionFailure()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.PcbSupply);
        using var services = CreateMotionScopeServices(settings, out var probes);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        probes[MotionGroup.PcbSupply].FailHardwareCalls = true;

        await machine.InitializeAsync();
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(state.ManualControlsEnabled);
        var failedCalls = probes[MotionGroup.PcbSupply].HardwareCalls;

        settings.Units.PcbSupply = false;
        settings.Units.PcbPlacement = true;
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        await machine.ResetAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.Equal(MachineAlarm.None, state.Alarm);
        Assert.True(state.Ready);
        Assert.True(state.ManualControlsEnabled);
        Assert.Equal(failedCalls, probes[MotionGroup.PcbSupply].HardwareCalls);

        // Re-enabling the same faulty hardware makes it mandatory again.
        settings.Units.PcbSupply = true;
        Assert.True(state.Faulted);
        Assert.False(state.ManualControlsEnabled);
        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
    }

    [Fact]
    public async Task DisabledTransferStillBlocksShuttleUntilPickupIsRaised()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.NgShuttle),
        };
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var io = services.GetRequiredService<VirtualIoService>();
        var shuttle = services.GetRequiredService<NgShuttle>();
        var gantry = services.GetRequiredService<InspectionGantry>();
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
            Assert.Equal(NgShuttleState.WaitingForCarrierPickupUp, shuttle.State);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));
            Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).ServoOn);
            Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).Homed);

            io.SetInput(InputIo.NgCarrierGripperClosed, false);
            io.SetInput(InputIo.NgCarrierGripperOpen, true);
            Assert.Equal(NgShuttleState.WaitingForCarrierPickupUp, shuttle.State);
            Assert.False(io.GetOutput(OutputIo.NgShuttleDown));

            io.SetInput(InputIo.NgCarrierPickupDown, false);
            io.SetInput(InputIo.NgCarrierPickupUp, true);
            await ((IIoService)io).WaitForInputAsync(InputIo.NgShuttleDown, true);
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).ServoOn);
            Assert.False(gantry.Feedback.GetAxisState(MotionAxis.X).Homed);
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
        using var services = CreateServices(new MachineSettings
        {
            Units = EnableOnly(MachineUnit.MainConveyor),
        });
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var operations = services.GetRequiredService<OperationCancellation>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.CanStart);

        using var manual = operations.Link();
        Assert.True(state.IsRunning);
        Assert.False(machine.CanStart);
        await machine.StartAsync();
        Assert.False(state.AutomaticRunning);

        machine.Stop();
        Assert.True(manual.IsCancellationRequested);
        Assert.True(state.IsRunning);
        Assert.False(machine.CanStart);
        manual.Dispose();
        Assert.False(state.IsRunning);
        Assert.True(machine.CanStart);
    }

    [Fact]
    public async Task StopDuringHardwareReadinessPreventsStartAndAllowsRestart()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
        var head = new WaitingBoltHead();
        using var services = new ServiceCollection()
            .AddSingleton<RecipeStore>()
            .AddIbtmApplication(settings)
            .AddKeyedSingleton<IBoltHead>(FasteningHead.Shooting, head)
            .AddKeyedSingleton<IBoltHead>(FasteningHead.Pickup, head)
            .BuildServiceProvider();
        PrepareCarrierTeaching(settings, services.GetRequiredService<Recipe>());
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
        Assert.True(state.AutomaticRunning);
        Assert.False(machine.CanStart);
        Assert.False(machine.CanHome);
        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(readinessChecks + 1, head.ReadinessChecks);

        machine.Stop();
        await starting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);

        head.ReadinessReleased.TrySetResult();
        var resumed = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);
        machine.Stop();
        await resumed.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsRunning);
        Assert.Equal(MachineAlarm.None, state.Alarm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopOrFailureDuringFirstUnitOutputPreventsLaterStarts(bool failure)
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.MainConveyor),
        };
        settings.Units.ShootingBoltFeeder = true;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        var stopped = false;
        var feederStarted = false;
        var error = new InvalidOperationException("Conveyor start failed.");
        io.OutputChanged += (output, value) =>
        {
            feederStarted |= output == OutputIo.ShootingFeederRunSignal && value;
            if (output == OutputIo.MainConveyorReadyToFront2 && value)
            {
                stopped = true;
                if (failure) throw error;
                machine.Stop();
            }
        };

        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(stopped);
        Assert.False(feederStarted);
        Assert.False(state.IsRunning);
        Assert.Equal(failure ? MachineAlarm.MainConveyor : MachineAlarm.None, state.Alarm);
        Assert.Equal(failure ? error.Message : null, state.AlarmMessage);
        Assert.False(services.GetRequiredService<OperationCancellation>().HasActiveOperations);
        Assert.False(io.GetOutput(OutputIo.ShootingFeederRunSignal));
    }

    [Fact]
    public async Task DoorTripStopsAndResetsFromLiveHardwareState()
    {
        var settings = new MachineSettings
        {
            Units = EnableOnly(MachineUnit.BoltFastening),
        };
        FastHomes(settings);
        using var services = CreateServices(settings);
        PrepareCarrierTeaching(
            settings,
            services.GetRequiredService<Recipe>());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();

        Assert.False(machine.CanStart);
        Assert.True(machine.CanHome);
        Assert.False(machine.CanReset);

        await machine.HomeAsync(CancellationToken.None);
        Assert.True(state.Homed);

        io.SetInput(InputIo.AutoMode, false);
        Assert.True(machine.CanStart);
        var firstRun = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);

        io.SetInput(InputIo.Door1Open, false);
        await firstRun.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.DoorOpen, state.Alarm);
        Assert.False(state.IsRunning);
        Assert.False(state.ServosOn);

        io.SetInput(InputIo.AutoMode, true);
        io.SetInput(InputIo.ResetButton, true);
        await WaitUntilAsync(() => !state.IsError);
        io.SetInput(InputIo.ResetButton, false);

        Assert.True(state.ServosOn);
        Assert.True(state.Homed);

        io.SetInput(InputIo.Door1Open, true);
        io.SetInput(InputIo.AutoMode, false);
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
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.MainConveyorAvailableFromFront2, false);
        await machine.StartAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.ShootingBoltFeeder, state.Alarm);
        Assert.False(io.GetOutput(OutputIo.ShootingFeederRunSignal));
        Assert.False(io.GetOutput(OutputIo.MainConveyorReadyToFront2));
        Assert.False(state.IsRunning);
        Assert.True(machine.CanReset);

        await machine.ResetAsync();
        Assert.Equal(MachineAlarm.None, state.Alarm);

        settings.BoltFeeder.ShootingTimeoutMilliseconds = 500;
        var resumed = machine.StartAsync();
        await ((IIoService)io).WaitForInputAsync(
            InputIo.ShootingFeederBoltDetected,
            true);

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
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();

        await machine.InitializeAsync();
        io.SetInput(InputIo.AutoMode, false);
        var run = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);

        io.SetConnected(false);
        await run.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.IoCommunication, state.Alarm);
        Assert.False(state.IsRunning);

        Assert.Contains("disconnected", state.AlarmDetail);

        io.SetConnected(true);
        Assert.True(machine.CanReset);
        io.SetInput(InputIo.ResetButton, true);
        await WaitUntilAsync(() => state.Alarm == MachineAlarm.None);
        io.SetInput(InputIo.ResetButton, false);

        Assert.Equal(MachineAlarm.None, state.Alarm);
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
        using var services = CreateServices(settings);
        PrepareCarrierTeaching(
            settings,
            services.GetRequiredService<Recipe>());
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var motion = (VirtualMotionService)services
            .GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);

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
        Assert.False(state.Faulted);
        Assert.True(state.ServosOn);
        var resumed = machine.StartAsync();
        await WaitUntilAsync(() => state.AutomaticRunning);
        machine.Stop();
        await resumed.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(state.IsRunning);
    }

    [Theory]
    [InlineData(InputIo.NgConveyorPosition2Occupied, true)]
    [InlineData(InputIo.PickupHeadUp, false)]
    public async Task HomeStopsWhenItsCarrierOrCylinderConditionChanges(InputIo input, bool value)
    {
        var settings = FlowSettings();
        foreach (var motionSettings in MotionSettingsOf(settings)) motionSettings.ZHome.SearchSpeed = 20;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var placement = services.GetRequiredService<PcbPlacementHandler>();
        var fastening = services.GetRequiredService<BoltFasteningGantry>();
        await machine.InitializeAsync();
        await Task.WhenAll(placement.MoveZAsync(50), fastening.MoveZAsync(50));
        var homing = machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => placement.Feedback.IsMoving && fastening.Feedback.IsMoving);
        Assert.False(state.ManualSetupEnabled);
        io.SetInput(input, value);
        await homing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(state.IsHoming);
        Assert.False(placement.Feedback.IsMoving);
        Assert.False(fastening.Feedback.IsMoving);
        Assert.False(placement.Feedback.GetAxisState(MotionAxis.Z).Homed);
        Assert.False(machine.CanHome);
    }

    [Fact]
    public async Task MotionAlarmBlocksHomeAndStopsAllHomingAxes()
    {
        var settings = FlowSettings();
        foreach (var motionSettings in MotionSettingsOf(settings)) motionSettings.ZHome.SearchSpeed = 20;
        using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var placement = (VirtualMotionService)services
            .GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler);
        var fastening = (VirtualMotionService)services
            .GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening);
        await machine.InitializeAsync();

        fastening.SetAlarm(MotionAxis.X, true);
        Assert.False(machine.CanHome);
        await machine.ResetAsync();
        Assert.True(machine.CanHome);

        await Task.WhenAll(
            placement.MoveZAsync(50, 10_000),
            fastening.MoveZAsync(50, 10_000));
        var homing = machine.HomeAsync(CancellationToken.None);
        await WaitUntilAsync(() => placement.IsMoving && fastening.IsMoving);
        fastening.SetAlarm(MotionAxis.X, true);
        await homing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(MachineAlarm.MotionUnavailable, state.Alarm);
        Assert.False(state.IsHoming);
        Assert.False(state.IsRunning);
        Assert.False(placement.IsMoving);
        Assert.False(fastening.IsMoving);
        Assert.False(placement.GetAxisState(MotionAxis.Z).Homed);
        Assert.False(fastening.GetAxisState(MotionAxis.Z).Homed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedHomeReportsCauseAndStopsOtherHomingAxes(bool exception, bool individual)
    {
        var settings = FlowSettings();
        foreach (var motionSettings in MotionSettingsOf(settings)) motionSettings.ZHome.SearchSpeed = 1;
        HomeResultMotion? homeResult = null;
        using var services = new ServiceCollection()
            .AddSingleton<RecipeStore>()
            .AddIbtmApplication(settings)
            .AddSingleton(provider =>
            {
                var motion = DispatchProxy.Create<IXyMotion, HomeResultMotion>();
                homeResult = (HomeResultMotion)motion;
                homeResult.Motion = provider.GetRequiredKeyedService<IXyMotion>(
                    MotionGroup.BoltFastening);
                return new BoltFasteningGantry(
                    provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting),
                    provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup),
                    provider.GetRequiredService<IIoService>(),
                    motion,
                    settings.BoltFastening,
                    settings.CarrierReference);
            })
            .BuildServiceProvider();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var placement = services.GetRequiredKeyedService<IXyMotion>(
            MotionGroup.PcbPlacementHandler);
        var supply = services.GetRequiredKeyedService<IAxisMotion>(
            MotionGroup.PcbSupply);
        var manual = services.GetRequiredService<MotionWindowViewModel>();
        await machine.InitializeAsync();
        await placement.MoveZAsync(50, 10_000);

        var axisRow = manual.Axes.Single(row => row.Group == MotionGroup.BoltFastening && row.Axis == MotionAxis.Z);
        var homing = individual
            ? manual.HomeAxisCommand.ExecuteAsync(axisRow)
            : machine.HomeAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => individual ? state.IsHoming : placement.IsMoving && supply.IsMoving);
            if (exception)
            {
                homeResult!.Result.SetException(new MotionException(
                    "Home", new InvalidOperationException("Home command failed.")));
            }
            else
            {
                homeResult!.Result.SetResult(false);
            }
            await homing.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(MachineAlarm.HomeFailed, state.Alarm);
            if (exception) Assert.Contains("Home command failed.", state.AlarmDetail);
            await WaitUntilAsync(() => state.Display.Alarm == MachineAlarm.HomeFailed);
            Assert.False(manual.HomeAxisCommand.CanExecute(axisRow));
            Assert.False(state.IsHoming);
            Assert.False(placement.IsMoving);
            Assert.False(supply.IsMoving);
            Assert.False(placement.GetAxisState(MotionAxis.Z).Homed);
            Assert.Equal(0, homeResult.HorizontalHomeCalls);
        }
        finally
        {
            manual.HomeAxisCommand.Cancel();
            machine.Stop();
            await homing;
        }
    }

    public class HomeResultMotion : DispatchProxy
    {
        public IXyMotion Motion { get; set; } = null!;
        public TaskCompletionSource<bool> Result { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int HorizontalHomeCalls { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IAxisMotion.HomeAsync)
                && (MotionAxis)arguments![0]! == MotionAxis.Z)
            {
                return Result.Task.WaitAsync((CancellationToken)arguments[2]!);
            }

            if (method.Name == nameof(IXyMotion.HomeHorizontalAsync))
            {
                HorizontalHomeCalls++;
            }

            return method.Invoke(Motion, arguments);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class StoppingBoltHead : IBoltHead
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopping { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public BoltHeadState State => BoltHeadState.Ready;
        public Task CheckReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SelectPresetAsync(ushort preset, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void DiscardPendingResult() { }

        public async Task<BoltResult> TightenAsync(CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            try
            {
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
        public bool WaitForReadiness { get; set; }
        public int ReadinessChecks { get; private set; }
        public TaskCompletionSource ReadinessEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadinessReleased { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public BoltHeadState State => BoltHeadState.Ready;

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

        public Task SelectPresetAsync(
            ushort preset,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BoltResult> TightenAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void DiscardPendingResult()
        {
        }
    }

    private static ServiceProvider CreateDisplayServices(out DisplayReadMotion feedback)
    {
        var motion = DispatchProxy.Create<IXyMotion, DisplayReadMotion>();
        var probe = (DisplayReadMotion)motion;
        feedback = probe;
        return new ServiceCollection()
            .AddSingleton<RecipeStore>()
            .AddIbtmApplication(FlowSettings(), new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
            .AddSingleton(provider =>
            {
                probe.Motion = provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry);
                return new InspectionGantry(motion,
                    provider.GetRequiredService<NgCarrierTransfer>(),
                    provider.GetRequiredService<OperationCancellation>(),
                    provider.GetRequiredService<InspectionGantrySettings>());
            })
            .BuildServiceProvider();
    }

    public class DisplayReadMotion : DispatchProxy
    {
        public IXyMotion Motion { get; set; } = null!;
        public Action? BeforeRead;
        public string? LastMove { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IMotionFeedback.GetAxisState)) BeforeRead?.Invoke();
            if (method.Name is nameof(IAxisMotion.MoveXAsync) or nameof(IAxisMotion.MoveYAsync)
                or nameof(IXyMotion.MoveToXYAsync)) LastMove = method.Name;
            return method.Invoke(Motion, arguments);
        }
    }

    private static ServiceProvider CreateServices(MachineSettings settings)
        => new ServiceCollection()
            .AddSingleton<RecipeStore>()
            .AddIbtmApplication(settings, new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });

    private static ServiceProvider CreateMotionScopeServices(
        MachineSettings settings, out Dictionary<MotionGroup, ScopedMotionProbe> probes)
    {
        var captured = new Dictionary<MotionGroup, ScopedMotionProbe>();
        probes = captured;
        T Wrap<T>(MotionGroup group, T motion) where T : class, IAxisMotion
        {
            var wrapper = DispatchProxy.Create<T, ScopedMotionProbe>();
            var probe = (ScopedMotionProbe)(object)wrapper;
            probe.Motion = motion;
            captured.Add(group, probe);
            return wrapper;
        }

        return new ServiceCollection()
            .AddSingleton<RecipeStore>()
            .AddIbtmApplication(settings, new Recipe { Pcb = VirtualTest.TaughtPcbLayout() })
            .AddSingleton(provider => new PcbSupplyHandler(
                Wrap(MotionGroup.PcbSupply, provider.GetRequiredKeyedService<IAxisMotion>(MotionGroup.PcbSupply)),
                provider.GetRequiredService<IIoService>(), settings.PcbSupply, settings.PcbBuffer))
            .AddSingleton(provider => new PcbPlacementHandler(
                Wrap(MotionGroup.PcbPlacementHandler, provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.PcbPlacementHandler)),
                provider.GetRequiredService<IIoService>(), settings.PcbPlacementHandler))
            .AddSingleton(provider => new BoltFasteningGantry(
                provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Shooting),
                provider.GetRequiredKeyedService<IBoltHead>(FasteningHead.Pickup),
                provider.GetRequiredService<IIoService>(),
                Wrap(MotionGroup.BoltFastening, provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.BoltFastening)),
                settings.BoltFastening, settings.CarrierReference))
            .AddSingleton(provider => new InspectionGantry(
                Wrap(MotionGroup.InspectionGantry, provider.GetRequiredKeyedService<IXyMotion>(MotionGroup.InspectionGantry)),
                provider.GetRequiredService<NgCarrierTransfer>(), provider.GetRequiredService<OperationCancellation>(),
                settings.InspectionGantry))
            .BuildServiceProvider();
    }

    public class ScopedMotionProbe : DispatchProxy
    {
        private bool _initialized;
        public IAxisMotion Motion = null!;
        public bool ReportReady;
        public bool FailHardwareCalls;
        public int HardwareCalls;
        public int ResetCalls;
        public Func<AxisState, AxisState>? OverrideState;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            var name = method!.Name;
            if (name == "get_IsReady") return ReportReady || _initialized;
            if ((!method.IsSpecialName && name != nameof(IMotionFeedback.GetRange))
                || name == "get_IsAtHorizontalZ")
            {
                Interlocked.Increment(ref HardwareCalls);
                if (name == nameof(IAxisMotion.Reset)) Interlocked.Increment(ref ResetCalls);
                if (FailHardwareCalls) throw new IOException($"Unavailable motion: {name}");
            }
            var result = method.Invoke(Motion, arguments);
            if (name == nameof(IAxisMotion.Initialize)) _initialized = true;
            if (name == nameof(IMotionFeedback.GetAxisState) && OverrideState is { } transform)
                return transform((AxisState)result!);
            return result;
        }
    }

    private static UnitSettings EnableOnly(MachineUnit unit) => new()
    {
        MainConveyor = unit == MachineUnit.MainConveyor,
        PcbSupply = unit == MachineUnit.PcbSupply,
        PcbPlacement = unit == MachineUnit.PcbPlacement,
        PickupBoltFeeder = unit == MachineUnit.PickupBoltFeeder,
        ShootingBoltFeeder = unit == MachineUnit.ShootingBoltFeeder,
        BoltFastening = unit == MachineUnit.BoltFastening,
        Inspection = unit == MachineUnit.Inspection,
        NgCarrierTransfer = unit == MachineUnit.NgCarrierTransfer,
        NgShuttle = unit == MachineUnit.NgShuttle,
        NgConveyor = unit == MachineUnit.NgConveyor,
    };

    private static HomeSettings FastHome() => new() { SearchSpeed = 10_000 };

    private static MotionSettings[] MotionSettingsOf(MachineSettings settings) =>
    [
        settings.PcbSupply.Motion, settings.PcbPlacementHandler.Motion,
        settings.BoltFastening.Motion, settings.InspectionGantry.Motion,
    ];

    private static void FastHomes(MachineSettings settings)
    {
        foreach (var motion in MotionSettingsOf(settings))
        {
            motion.HorizontalHome = FastHome();
            motion.ZHome = FastHome();
        }
    }

    private static MachineSettings FlowSettings()
    {
        var settings = new MachineSettings
        {
            Drivers = new() { Inspection = InspectionAlgorithm.Virtual },
        };
        FastHomes(settings);
        settings.PcbSupply.Motion = FastMotion();
        settings.PcbSupply.RotationZ = 0;
        settings.PcbSupply.CarrierY = 10;
        settings.PcbSupply.BufferHandoffPosition = new()
        {
            X = 80,
            Y = 30,
            Z = 10,
        };
        settings.PcbSupply.BufferClearZ = 20;
        settings.PcbBuffer.SupplyBoundary1 = 60;
        settings.PcbBuffer.SupplyBoundary2 = 100;
        settings.PcbBuffer.PlacementBoundary1 = new() { X = 60, Y = 20 };
        settings.PcbBuffer.PlacementBoundary2 = new() { X = 100, Y = 40 };
        settings.PcbPlacementHandler.Motion = FastMotion();
        settings.PcbPlacementHandler.BufferEntryZ = 0;
        settings.PcbPlacementHandler.BufferHandoffPosition = new()
        {
            X = 80,
            Y = 30,
            Z = 10,
        };
        settings.BoltFastening.Motion = FastMotion();
        settings.BoltFastening.SafeZ = 0;
        settings.BoltFastening.PickupPosition = new()
        {
            X = 100,
            Y = 50,
            Z = 10,
        };
        settings.BoltFastening.PickupHead = HeadSettings();
        settings.BoltFastening.ShootingHead = HeadSettings();
        settings.InspectionGantry.Motion = FastMotion();
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 0 };
        settings.NgCarrierTransfer.Speed = 10_000;
        settings.NgCarrierTransfer.CarrierPickupPosition = new() { X = 20, Y = 20 };
        settings.NgCarrierTransfer.ShuttlePlacePosition = new() { X = 150, Y = 20 };
        return settings;
    }

    private static MotionSettings FastMotion() => new()
    {
        HorizontalSpeed = 10_000,
        ZSpeed = 10_000,
        HorizontalHome = FastHome(),
        ZHome = FastHome(),
    };

    private static BoltHeadSettings HeadSettings() => new()
    {
        UpperLeftLocatingPin = new() { X = 0, Y = 0 },
        LowerRightLocatingPin = new() { X = 100, Y = 0 },
    };

    private static void PrepareCarrierTeaching(
        MachineSettings settings,
        Recipe recipe)
    {
        settings.CarrierReference.UpperLeftLocatingPin = new() { X = 0, Y = 0 };
        settings.CarrierReference.LowerRightLocatingPin = new() { X = 100, Y = 0 };
        settings.BoltFastening.PickupHead = HeadSettings();
        settings.BoltFastening.ShootingHead = HeadSettings();
        recipe.Pcb = VirtualTest.TaughtPcbLayout();
        recipe.Pcb.BoltPoints.Add(new BoltPoint
        {
            Number = 1,
            Head = FasteningHead.Shooting,
            X = 10,
            Y = 10,
        });
    }

    public enum MachineUnit
    {
        MainConveyor,
        PcbSupply,
        PcbPlacement,
        PickupBoltFeeder,
        ShootingBoltFeeder,
        BoltFastening,
        Inspection,
        NgCarrierTransfer,
        NgShuttle,
        NgConveyor,
    }

    [Flags]
    public enum HeatSinkLoad
    {
        None = 0,
        HeatSink1 = 1,
        HeatSink2 = 2,
        Both = HeatSink1 | HeatSink2,
    }
}
