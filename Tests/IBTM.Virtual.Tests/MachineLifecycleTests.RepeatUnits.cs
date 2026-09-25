using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.BoltFastening;
using IBTM.Inspection;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed partial class MachineLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainOnlyRepeatRequiresPreparedStation3Support(bool preparedAtStart)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        await using var services = CreateServices(settings);
        var conveyor = services.GetRequiredService<MainConveyor>();
        var inspection = services.GetRequiredService<InspectionStation>();
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        if (preparedAtStart)
        {
            io.SetInputs(
                (InputIo.InspectionHeatSink1Present, true),
                (InputIo.InspectionHeatSink2Present, true));
            await inspection.Station.SeatAsync(CancellationToken.None);
        }
        else
        {
            io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        }
        var reachedStation3 = preparedAtStart;
        io.InputChanged += (input, on) =>
        {
            if (input == InputIo.InspectionHeatSink2Present && on)
                reachedStation3 = true;
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var waitingForSupport = false;
        conveyor.StepChanged += () =>
        {
            if (!preparedAtStart && reachedStation3
                && conveyor.Step is MainConveyorState.WaitingForInspectionTransfer)
            {
                waitingForSupport = true;
                stop.Cancel();
            }
        };
        var returned = false;
        var raisedStation3 = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.InspectionBackupPlateUp && on)
                raisedStation3 = true;
            if (output == OutputIo.MainConveyorRun && !on
                && !io.GetOutput(OutputIo.MainConveyorForward)
                && io.GetInput(InputIo.MainConveyorEntryCarrierDetected))
            {
                returned = true;
                stop.Cancel();
            }
        };
        state.RepeatEnabled = true;
        try
        {
            await machine.StartAsync(stop.Token);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.True(reachedStation3);
            Assert.Equal(preparedAtStart, returned);
            Assert.Equal(!preparedAtStart, waitingForSupport);
            Assert.False(raisedStation3); // A disabled gantry cannot prepare the safe lifting position.
            Assert.Equal(preparedAtStart, io.GetInput(InputIo.MainConveyorEntryCarrierDetected));
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(MachineUnit.BoltFastening)]
    [InlineData(MachineUnit.Inspection)]
    public async Task StationRepeatWithoutMainStartsNextJobAfterCompletedWork(MachineUnit unit)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(unit);
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        ConveyorStation work = unit == MachineUnit.BoltFastening
            ? services.GetRequiredService<BoltFasteningStation>().Station : services.GetRequiredService<InspectionStation>().Station;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(unit == MachineUnit.BoltFastening
            ? InputIo.BoltFasteningHeatSink1Present : InputIo.InspectionHeatSink1Present, true);
        await work.SeatAsync(CancellationToken.None);
        var completed = 0;
        long previousJob = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        work.Changed += () =>
        {
            if (!work.Completed || work.CurrentJob.Id == previousJob)
                return;
            previousJob = work.CurrentJob.Id;
            completed++;
            Assert.NotEmpty(work.Assemblies);
            if (completed == 2)
                stop.Cancel();
        };
        state.RepeatEnabled = true;
        try
        {
            await machine.StartAsync(stop.Token);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.Equal(2, completed);
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task InspectionOnlyRepeatPicksAndReturnsWithOrWithoutMaterial(
        bool startsWithCarrierHeld, bool detected, bool ngConveyorEnabled)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        settings.Units.NgConveyor = ngConveyorEnabled;
        settings.NgCarrierTransfer.WaitingPosition = new() { X = 30, Y = 40 };
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var pickup = services.GetRequiredService<InspectionStation>();
        var gantry = services.GetRequiredService<InspectionStation>();
        var work = services.GetRequiredService<InspectionStation>();
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        PrepareCarrierTeaching(settings, recipe);
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        await work.Station.PrepareToReceiveAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, startsWithCarrierHeld);
        // Material on the disabled main route does not belong to this repeat.
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        // The held-carrier turn does not use the shuttle as a support.
        io.SetInputs((InputIo.NgShuttleUp, false), (InputIo.NgShuttleDown, false));
        if (startsWithCarrierHeld)
        {
            await work.Station.SeatAsync(CancellationToken.None);
            await services.GetRequiredService<InspectionStation>().ExecuteTransferAsync(
                NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None);
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.False(work.Station.CarrierPresent);
        }
        else
        {
            // A closed empty gripper and an ON presence sensor are not a completed pickup.
            await pickup.SetGripperOpenAsync(false);
        }
        io.SetInput(InputIo.NgCarrierDetected, detected);

        (double X, double Y, bool Holding)[] expectedDescents = startsWithCarrierHeld
            ? [(5d, 20d, true), (5d, 20d, false), (5d, 20d, true)]
            : [(5d, 20d, false), (5d, 20d, true), (5d, 20d, false), (5d, 20d, true)];

        var descents = new ConcurrentQueue<(double X, double Y, bool Holding)>();
        var mainRan = false;
        var ngConveyorRan = false;
        var shuttleMoved = false;
        var plateRaised = false;
        var releasedAtShuttle = false;
        var raisedShuttleVisits = 0;
        var returnedTwice = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgCarrierPickupDown && on)
            {
                var position = gantry.Motion.Feedback.Position;
                descents.Enqueue((position.X, position.Y,
                    pickup.Gripper == NgTransferGripperState.Closed));
            }
            if (output == OutputIo.NgCarrierGripperClose && !on
                && gantry.Motion.IsAt(settings.NgCarrierTransfer.ShuttlePlacePosition))
                releasedAtShuttle = true;
            mainRan |= output == OutputIo.MainConveyorRun && on;
            ngConveyorRan |= output == OutputIo.NgConveyorRun && on;
            shuttleMoved |= output == OutputIo.NgShuttleDown;
            plateRaised |= output == OutputIo.InspectionBackupPlateUp && on;
        };
        machine.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName != nameof(MachineController.RepeatDisplayPhase))
                return;
            if (machine.RepeatDisplayPhase == RepeatPhase.ReturnToStation3
                && gantry.Motion.IsAt(settings.NgCarrierTransfer.ShuttlePlacePosition)
                && pickup.IsRaised && pickup.Gripper == NgTransferGripperState.Closed)
                raisedShuttleVisits++;
            if (machine.RepeatDisplayPhase != RepeatPhase.Automatic
                || descents.Count != expectedDescents.Length || stop.IsCancellationRequested)
                return;
            returnedTwice = gantry.Motion.IsAt(settings.NgCarrierTransfer.WaitingPosition)
                && work.Station.CarrierPresent == startsWithCarrierHeld
                && work.Station.BackupPlate == StationCylinderState.Up && pickup.IsClear
                && pickup.Gripper == NgTransferGripperState.Open;
            stop.Cancel();
        };
        state.RepeatEnabled = true;
        try
        {
            await WaitUntilAsync(() => machine.IsStartAllowed);
            await machine.StartAsync(stop.Token);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.True(returnedTwice);
            Assert.Equal(2, raisedShuttleVisits);
            Assert.Equal(expectedDescents, descents.ToArray());
            if (!startsWithCarrierHeld)
                Assert.True(plateRaised);
            Assert.False(releasedAtShuttle);
            Assert.False(mainRan);
            Assert.False(ngConveyorRan);
            Assert.False(shuttleMoved);
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task NgTransferRepeatDoesNotCountPendingPickupAndStationPresenceAtStartup()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.Inspection);
        await using var services = CreateServices(settings);
        PrepareCarrierTeaching(settings, services.GetRequiredService<RecipeManager>().Current);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var gantry = services.GetRequiredService<InspectionStation>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        await services.GetRequiredService<InspectionStation>().Station.SeatAsync(CancellationToken.None);
        await services.GetRequiredService<InspectionStation>().ExecuteTransferAsync(
            NgTransferDestination.Shuttle, InspectionStationState.PickingCarrier, CancellationToken.None);
        await gantry.MoveToAsync(new() { X = 50, Y = 30 }, 10_000);
        io.SetInput(InputIo.InspectionHeatSink1Present, true);
        var lowered = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.NgCarrierPickupDown && on)
                lowered = true;
        };
        machine.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(MachineController.RepeatDisplayPhase)
                && machine.RepeatDisplayPhase == RepeatPhase.ReturnToStation3)
                stop.Cancel();
        };
        state.RepeatEnabled = true;
        try
        {
            await WaitUntilAsync(() => machine.IsStartAllowed);
            await machine.StartAsync(stop.Token);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.False(lowered);
            Assert.True(gantry.Motion.IsAt(settings.NgCarrierTransfer.ShuttlePlacePosition));
            Assert.True(gantry.IsRaised);
            Assert.True(gantry.IsTransferPending);
            Assert.True(io.GetInput(InputIo.NgCarrierDetected));
            Assert.True(io.GetInput(InputIo.NgCarrierGripperClosed));
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    [Fact]
    public async Task NgRepeatWithoutMainReturnsFromPosition1ToShuttle()
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.NgConveyor);
        settings.Units.NgConveyor = true;
        await using var services = CreateServices(settings);
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        io.SetInput(InputIo.NgConveyorPosition1Occupied, true);
        var reverse = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        io.OutputChanged += (output, on) =>
        {
            if (on && output == OutputIo.NgConveyorRun && io.GetOutput(OutputIo.NgConveyorReverse))
                reverse = true;
        };
        io.InputChanged += (input, on) =>
        {
            if (reverse && input == InputIo.NgShuttleUp && on && io.GetInput(InputIo.NgShuttleCarrierDetected))
                stop.Cancel();
        };
        state.RepeatEnabled = true;
        try
        {
            await machine.StartAsync(stop.Token);
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.True(reverse);
            Assert.True(io.GetInput(InputIo.NgShuttleCarrierDetected));
            Assert.True(io.GetInput(InputIo.NgShuttleUp));
            Assert.False(io.GetInput(InputIo.NgConveyorPosition1Occupied));
            Assert.False(io.GetOutput(OutputIo.MainConveyorRun));
        }
        finally
        {
            machine.Stop();
            await machine.ShutdownAsync();
        }
    }

    public enum PcbRepeatStopPoint
    {
        None,
        SupplyReturning,
        BothHolding,
        SupplyReleasing,
        PlacementHolding,
    }

    [Theory]
    [InlineData(PcbRepeatStopPoint.None)]
    [InlineData(PcbRepeatStopPoint.SupplyReturning)]
    [InlineData(PcbRepeatStopPoint.BothHolding)]
    [InlineData(PcbRepeatStopPoint.SupplyReleasing)]
    [InlineData(PcbRepeatStopPoint.PlacementHolding)]
    public async Task RepeatMainSupplyPlacementKeepsReturnedPcbsAndResumesForward(PcbRepeatStopPoint stopPoint)
    {
        var settings = FlowSettings();
        settings.Units = EnableOnly(MachineUnit.MainConveyor);
        settings.Units.PcbSupply = true;
        settings.Units.PcbPlacement = true;
        // Keep an intermediate position observable when stopping reverse travel.
        settings.PcbSupply.Motion.HorizontalSpeed = 200;
        settings.Conveyor.CarrierStopDelaySeconds = 0;
        await using var services = CreateServices(settings);
        var recipe = services.GetRequiredService<RecipeManager>().Current;
        recipe.PcbSupply.Pcb1PickPosition = new() { X = 10, Y = 10, Z = 5 };
        recipe.PcbSupply.Pcb2PickPosition = new() { X = 20, Y = 10, Z = 5 };
        recipe.PcbPlacement.HeatSink1PcbPlacementPosition = new() { X = 20, Y = 100, Z = 12 };
        recipe.PcbPlacement.HeatSink2PcbPlacementPosition = new() { X = 40, Y = 100, Z = 12 };
        var machine = services.GetRequiredService<MachineController>();
        var state = services.GetRequiredService<MachineState>();
        var io = services.GetRequiredService<VirtualIoService>();
        var supply = services.GetRequiredService<PcbSupplier>();
        var placement = services.GetRequiredService<PcbPlacer>();
        var supplier = services.GetRequiredService<PcbSupplier>();
        var placer = services.GetRequiredService<PcbPlacer>();
        var receivePosition = new AxisPosition
        {
            X = settings.PcbPlacementHandler.HandoffPosition.X,
            Y = settings.PcbPlacementHandler.HandoffPosition.Y,
            Z = settings.PcbPlacementHandler.ReceiveZ!.Value,
        };
        var handoffSteps = new ConcurrentQueue<string>();
        supplier.Trace += handoffSteps.Enqueue;
        placer.Trace += handoffSteps.Enqueue;
        var returns = 0;
        var reverseHandoffs = 0;
        var mainReturned = false;
        var enteredDisabledStation = false;
        var unsafeRelease = false;
        var descendedToSourceSlot = false;
        var loweredPlacementIpm = false;
        var placementDepartedInY = false;
        var stopped = false;
        await machine.InitializeAsync();
        await machine.HomeAsync(CancellationToken.None);
        Assert.False(supply.UpstreamCarrierAvailable);
        io.SetInput(InputIo.MainConveyorEntryCarrierDetected, true);
        io.InputChanged += (input, on) =>
        {
            if (on && input is InputIo.BoltFasteningHeatSink1Present or InputIo.InspectionHeatSink1Present)
                enteredDisabledStation = true;
        };
        io.OutputChanged += (output, on) =>
        {
            if (!on && output == OutputIo.MainConveyorRun
                && !io.GetOutput(OutputIo.MainConveyorForward)
                && io.GetInput(InputIo.MainConveyorEntryCarrierDetected))
                mainReturned = true;
            if (!on && output == OutputIo.PcbSupplyGripperClosed
                && supply.Rotation == PcbSupplyRotationState.Rotated)
                returns++;
            if (!on && output == OutputIo.PcbPlacementVacuumEjector && placement.Motion.IsAt(receivePosition))
            {
                reverseHandoffs++;
                unsafeRelease |= !supply.PcbSecured;
            }
            loweredPlacementIpm |= output == OutputIo.PcbPlacementIpmDown && on;
        };
        void StopAtHandoff()
        {
            if (stopped || stopPoint == PcbRepeatStopPoint.None)
                return;
            var reached = stopPoint switch
            {
                PcbRepeatStopPoint.SupplyReturning => reverseHandoffs == 2 && supply.PcbSecured
                    && supplier.Phase == PcbSupplyState.MovingToPickup && supply.Motion.Feedback.IsMovingHorizontal
                    && supply.Motion.Feedback.Position.X < settings.PcbSupply.HandoffPosition.X - 1
                    && supply.Motion.Feedback.Position.X > recipe.PcbSupply.Pcb2PickPosition.X + 1,
                PcbRepeatStopPoint.BothHolding => supply.PcbSecured && placement.PcbSecured
                    && placement.Motion.IsAt(receivePosition),
                PcbRepeatStopPoint.SupplyReleasing => reverseHandoffs > 0
                    && !io.GetInput(InputIo.PcbSupplyIpmFixerForward) && supply.Gripper == PcbSupplyCylinderState.Forward
                    && placement.PcbSecured && placement.Motion.IsAt(receivePosition),
                PcbRepeatStopPoint.PlacementHolding => reverseHandoffs > 0 && supply.PcbReleased
                    && placement.PcbSecured && placement.Motion.IsAt(receivePosition),
                _ => false,
            };
            if (reached)
            {
                stopped = true;
                machine.Stop();
            }
        }
        supply.Changed += StopAtHandoff;
        placement.Changed += StopAtHandoff;
        supply.Motion.Feedback.PositionChanged += (x, y, z) => StopAtHandoff();
        placement.Motion.Feedback.PositionChanged += (x, y, z) =>
        {
            if (placer.Phase is PcbPlacementState.WaitingForSupply or PcbPlacementState.WaitingForSupplyRelease
                && y > settings.PcbPlacementHandler.HandoffPosition.Y
                && y < recipe.PcbPlacement.HeatSink1PcbPlacementPosition.Y)
            {
                placementDepartedInY = true;
                Assert.Equal(settings.PcbPlacementHandler.HandoffPosition.X, x);
                Assert.Equal(settings.PcbPlacementHandler.HandoffPosition.Z, z);
                Assert.NotEqual(PcbPlacementHandoff.Clear, placer.Handoff);
            }
        };
        supplier.Trace += message =>
        {
            if (message.StartsWith("PcbSupplier: MovingToPickup ", StringComparison.Ordinal))
                Assert.Equal(recipe.PcbPlacement.HeatSink1PcbPlacementPosition.Y, placement.Motion.Feedback.Position.Y);
        };
        supply.Motion.Feedback.StateChanged += () =>
        {
            if (supply.PcbSecured && supply.Rotation == PcbSupplyRotationState.Rotated
                && !supply.Motion.IsAtZ(settings.PcbSupply.RotationZ, live: true))
                descendedToSourceSlot = true;
            StopAtHandoff();
        };
        state.RepeatEnabled = true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = machine.StartAsync(timeout.Token);
        try
        {
            if (stopPoint != PcbRepeatStopPoint.None)
            {
                await run.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(stopped,
                    $"Supply={supplier.Phase}, Placement={placer.Phase}, {state.AlarmDetail}");
                Assert.False(state.IsError, state.AlarmDetail);
                Assert.True(supply.PcbSecured || placement.PcbSecured);
                run = machine.StartAsync(timeout.Token);
            }
            Assert.True(await VirtualTest.WaitUntilAsync(
                () => mainReturned || state.IsError, TimeSpan.FromSeconds(27)),
                $"Supply={supplier.Phase}/{supplier.Handoff}/{supply.Pcb}/{supply.Rotation}/{supply.Motion.Feedback.Position}, "
                    + $"Placement={placer.Phase}/{placer.Handoff}/{placement.Pcb}/{placement.IpmLift}/{placement.Motion.Feedback.Position}, "
                    + $"Phase={machine.RepeatDisplayPhase}, {state.AlarmDetail}\n"
                    + string.Join('\n', handoffSteps));
            Assert.False(state.IsError, state.AlarmDetail);
            Assert.Equal(0, returns);
            Assert.False(descendedToSourceSlot);
            Assert.False(loweredPlacementIpm);
            Assert.Equal(stopPoint == PcbRepeatStopPoint.BothHolding ? 1 : 2, reverseHandoffs);
            Assert.False(unsafeRelease);
            Assert.True(placementDepartedInY);
            Assert.False(enteredDisabledStation);
            Assert.False(io.GetOutput(OutputIo.NgConveyorRun));
            Assert.True(mainReturned);
        }
        finally
        {
            machine.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            await machine.ShutdownAsync();
        }
    }
}
