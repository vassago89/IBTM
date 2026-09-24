using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Storage;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class PcbTransferTests
{
    [Fact]
    public async Task SupplyPickupTeachingUsesEachSlotsXyzAndBlocksMissingY()
    {
        var settings = new PcbSupplySettings { Motion = FastMotion(), RotationZ = 3 };
        var recipe = System.Text.Json.JsonSerializer.Deserialize<PcbSupplyRecipe>(
            """{"Pcb1PickPosition":{"X":10,"Z":5},"Pcb2PickPosition":{"X":20,"Z":8}}""")!;
        var io = new VirtualIoService(Outputs(new PcbSupplyHardwareSettings()), new MachineOptions());
        using var motion = new VirtualMotionService(settings.Motion, new());
        var handler = VirtualTest.CreateSupplier(motion, io, settings);
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 2_000);
        await handler.SetRotatedAsync(true, default);
        var picks = new[] { TeachingTarget.SupplyPcb1Pick, TeachingTarget.SupplyPcb2Pick }
            .Select(target => CreateTeachingPoint(new(target, MotionGroup.PcbSupply, TeachMode.Full),
                new() { PcbSupply = settings }, new() { PcbSupply = recipe })).ToArray();
        Assert.All(picks, point => Assert.False(point.Position.HasPosition));
        Assert.All(picks, point => Assert.False(handler.IsMoveToTeachingPositionAllowed(point.Position)));
        var before = motion.GetPosition();
        await Assert.ThrowsAsync<MotionInterlockException>(() =>
            handler.MoveToTeachingPositionAsync(picks[0].Position, picks[0].Read()));
        Assert.Equal(before, motion.GetPosition());

        picks[0].Teach(10, 30, 5);
        picks[1].Teach(20, 45, 8);
        var positions = new[] { recipe.Pcb1PickPosition, recipe.Pcb2PickPosition };
        for (var index = 0; index < picks.Length; index++)
        {
            var point = picks[index];
            Assert.True(handler.IsMoveToTeachingPositionAllowed(point.Position));
            await handler.MoveToTeachingPositionAsync(point.Position, point.Read());
            var target = positions[index];
            Assert.Equal((target.X, target.Y!.Value, target.Z), motion.GetPosition());
            Assert.True(handler.IsAtPickupXY(target));
            Assert.False(handler.IsAtPickupXY(positions[1 - index]));
        }
    }

    [Fact]
    public async Task SupplyTestAvailableWakesPickupAndNeedsAnOffEdgeForTheNextCarrier()
    {
        var settings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            RotationZ = 3,
            HandoffPosition = new() { X = 50, Y = 10 },
        };
        var placementSettings = new PcbPlacementHandlerSettings { Motion = FastMotion() };
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var motion = new VirtualMotionService(settings.Motion, new());
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, new());

        var supplier = new PcbSupplier(motion, new MotionStatus(motion),
            io,
            settings,
            new());
        var handler = supplier;
        var placer = CreatePlacer(supplier, placementMotion, io, placementSettings);
        var placement = placer;
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 20, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 30, Z = 5 },
        };
        io.Initialize();
        motion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(motion, 2_000), HomeAsync(placementMotion, 2_000));
        Assert.False(handler.TestUpstreamCarrierAvailable);
        Assert.False(handler.UpstreamCarrierAvailable);
        var completedCarriers = 0;
        supplier.Trace += message =>
        {
            if (message.StartsWith("PcbSupplier: WaitingForCarrierExit ", StringComparison.Ordinal))
                Interlocked.Increment(ref completedCarriers);
        };
        var readyWentOn = false;
        io.OutputChanged += (output, on) => readyWentOn |= output == OutputIo.PcbSupplyReadyToFront1 && on;
        motion.PositionChanged += (_, _, z) =>
        {
            if (z > settings.RotationZ)
                Assert.Equal(PcbSupplyRotationState.Rotated, handler.Rotation);
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var run = supplier.RunAsync(recipe, placer, stop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => supplier.State == PcbSupplyState.WaitingForCarrier,
                TimeSpan.FromSeconds(1)));
            Assert.False(handler.UpstreamCarrierAvailable);
            Assert.Equal(PcbSupplyRotationState.Rotated, handler.Rotation);
            handler.TestUpstreamCarrierAvailable = true;
            Assert.True(await WaitUntilAsync(() => completedCarriers == 1, TimeSpan.FromSeconds(2)));
            Assert.Equal((20, recipe.Pcb2PickPosition.Y!.Value, settings.RotationZ), motion.GetPosition());
            Assert.False(io.GetInput(InputIo.PcbSupplyAvailableFromFront1));
            Assert.True(handler.TestUpstreamCarrierAvailable);

            // In teaching, TEST alone reports departure even if the real input stays on.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            handler.TestUpstreamCarrierAvailable = false;
            Assert.False(handler.UpstreamCarrierAvailable);

            handler.TestUpstreamCarrierAvailable = true;
            Assert.True(await WaitUntilAsync(() => completedCarriers == 2, TimeSpan.FromSeconds(2)));
            Assert.Equal((20, recipe.Pcb2PickPosition.Y!.Value, settings.RotationZ), motion.GetPosition());
            Assert.True(io.GetInput(InputIo.PcbSupplyAvailableFromFront1));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.True(handler.TestUpstreamCarrierAvailable);
        Assert.False(readyWentOn);
        var newHandler = VirtualTest.CreateSupplier(motion, io, settings);
        Assert.False(newHandler.TestUpstreamCarrierAvailable);
        Assert.False(newHandler.UpstreamCarrierAvailable);
    }

    [Fact]
    public async Task VirtualSupplyCarrierLeavesAfterReadyFallsAndWaitsForTheNextReady()
    {
        var io = new VirtualIoService(Outputs(new PcbSupplyHardwareSettings()), new MachineOptions());
        _ = new VirtualMachine(io, []);
        io.SetInput(InputIo.AutoMode, false);
        io.Initialize();
        IIoService signals = io;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        io.SetOutput(OutputIo.PcbSupplyReadyToFront1, true);
        await signals.WaitForInputAsync(InputIo.PcbSupplyAvailableFromFront1, true, timeout.Token);
        Assert.True(io.GetOutput(OutputIo.PcbSupplyReadyToFront1));

        io.SetOutput(OutputIo.PcbSupplyReadyToFront1, false);
        await signals.WaitForInputAsync(InputIo.PcbSupplyAvailableFromFront1, false, timeout.Token);
        Assert.False(io.GetOutput(OutputIo.PcbSupplyReadyToFront1));

        io.SetOutput(OutputIo.PcbSupplyReadyToFront1, true);
        await signals.WaitForInputAsync(InputIo.PcbSupplyAvailableFromFront1, true, timeout.Token);
        Assert.True(io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
    }

    [Fact]
    public async Task VirtualPlacementDetectsSupplyAtReceiveZWithCylinderUp()
    {
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        var simulation = new VirtualMachine(io, []);
        io.Initialize();
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        simulation.UpdateSupplyPosition(50, 10, 7, (10, 30, 5), (20, 30, 5), Position(50, 10, 7));
        var standby = Position(50, 10, 8);

        simulation.UpdatePlacementPosition(50, 10, 8, standby, 12);
        Assert.False(io.GetInput(InputIo.PcbPlacementPcbDetected));
        simulation.UpdatePlacementPosition(50, 10, 12, standby, null);
        Assert.False(io.GetInput(InputIo.PcbPlacementPcbDetected));
        simulation.UpdatePlacementPosition(50, 10, 12, standby, 12);
        Assert.True(io.GetInput(InputIo.PcbPlacementPcbDetected));
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementHandlerDown, true);
        Assert.False(io.GetInput(InputIo.PcbPlacementPcbDetected));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffAcceptsEitherArrivalOrderAndReceivesWithZAndCylinderUp(bool supplyFirst)
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            HandoffPosition = new() { X = 50, Y = 10 },
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
            HandoffPosition = Position(50, 10, 8),
            ReceiveZ = 12,
        };
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations);

        var supplier = new PcbSupplier(supplyMotion, new MotionStatus(supplyMotion),
            io,
            supplySettings,
            new());
        var source = supplier;
        var placer = CreatePlacer(supplier, placementMotion, io, placementSettings);
        var recipient = placer;
        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await source.SetGripperClosedAsync(true);
        await source.SetIpmFixerAsync(true);
        await source.SetRotatedAsync(false);
        await recipient.MoveToHorizontalZAsync();
        Assert.True(recipient.IsAtHorizontalZ());
        await recipient.SetLiftDownAsync(true);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (supplyFirst)
        {
            // Placement is still outside the shared area; Supply may arrive first.
            await source.PrepareHandoffAsync(timeout.Token);
        }
        Assert.Equal(PcbPlacementState.MovingToHandoff, placer.State);
        var movedWithCylinderDown = false;
        placementMotion.MovingChanged += moving =>
        {
            movedWithCylinderDown |= moving && recipient.Lift != PlacementCylinderState.Up;
        };
        for (var step = 0; step < 8 && !recipient.IsAtHandoff(); step++)
        {
            await placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token)!;
        }

        Assert.True(recipient.IsAtHandoff());
        Assert.Equal((50, 10, 8), placementMotion.GetPosition());
        Assert.Equal(PlacementCylinderState.Up, recipient.Lift);
        Assert.False(movedWithCylinderDown);
        if (!supplyFirst)
        {
            // Placement waits at standby Z until Supply is holding the PCB at handoff.
            Assert.Equal(PcbPlacementState.WaitingForSupply, placer.State);
            Assert.False(await placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
            await source.PrepareHandoffAsync(timeout.Token);
        }
        var receiveZ = placementSettings.ReceiveZ;
        placementSettings.ReceiveZ = null;
        await Assert.ThrowsAsync<MotionInterlockException>(() => placer.ExecuteStepAsync(
            placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.Equal((50, 10, 8), placementMotion.GetPosition());
        Assert.True(recipient.HandlerRaised);
        placementSettings.ReceiveZ = receiveZ;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementVacuumEjector)
                io.SetInput(InputIo.PcbPlacementVacuumDetected, on);
        };
        if (supplyFirst)
        {
            // An interrupted receipt at Receive Z finishes without raising away from Supply.
            await recipient.PrepareReceiptAsync(timeout.Token);
        }
        var loweredDuringReceipt = false;
        io.OutputChanged += (output, on) => loweredDuringReceipt |= output == OutputIo.PcbPlacementHandlerDown && on;
        Assert.Equal(PcbPlacementState.ReceivingPcb, placer.State);
        var receipt = placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token);
        Assert.True(await WaitUntilAsync(() => recipient.IsAtReceivePosition(), TimeSpan.FromSeconds(2)));
        Assert.False(receipt.IsCompleted);
        Assert.True(recipient.HandlerRaised);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        Assert.True(await receipt);
        Assert.True(recipient.IsAtReceivePosition() && recipient.PcbSecured);
        Assert.False(recipient.IsAtHandoff());
        Assert.False(loweredDuringReceipt);
        Assert.False(movedWithCylinderDown);
        Assert.Equal((50, 10, placementSettings.ReceiveZ!.Value), placementMotion.GetPosition());
        Assert.Equal(PcbPlacementHandoff.Holding, placer.Handoff);
        io.SetInputs((InputIo.PcbPlacementIpmDown, false), (InputIo.PcbPlacementIpmUp, false));
        Assert.True(recipient.PcbSecured);
        Assert.Equal(PcbPlacementHandoff.Unavailable, placer.Handoff);
        io.SetInput(InputIo.PcbPlacementIpmDown, true);
        Assert.Equal(PcbPlacementHandoff.Holding, placer.Handoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectHandoffWaitsForPlacementYDepartureBeforeSupply(bool stopDuringDeparture)
    {
        var recipe = new PcbPlacementRecipe
        {
            HeatSink1PcbPlacementPosition = Position(70, 20, 10),
        };
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            RotationZ = 3,
            HandoffPosition = new() { X = 50, Y = 10, Z = 7 },
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
            HandoffPosition = Position(50, 10, 8),
            ReceiveZ = 12,
        };
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = new VirtualMotionService(
            supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations);

        var units = new UnitSettings();
        var supplier = new PcbSupplier(supplyMotion, new MotionStatus(supplyMotion),
            io,
            supplySettings,
            units);
        var source = supplier;
        var work = ConveyorStation.CreatePcbPlacement(io);
        var placer = CreatePlacer(supplier, placementMotion, io, placementSettings, recipe, work);
        var recipient = placer;
        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, false);
        Assert.False(source.IsAtHandoff() && source.PcbSecured);
        await source.PrepareHandoffAsync(CancellationToken.None);
        Assert.True(source.IsAtHandoff() && source.PcbSecured);
        await placementMotion.MoveToXYAsync(50, 10, placementSettings.Motion.HorizontalSpeed);
        await placementMotion.MoveAxisAsync(MotionAxis.Z, placementSettings.ReceiveZ!.Value, placementSettings.Motion.ZSpeed);
        await recipient.SetIpmLiftDownAsync(true);
        io.SetInputs(
            (InputIo.PcbPlacementHeatSink1Present, true),
            (InputIo.PcbPlacementBackupPlateUp, true),
            (InputIo.PcbPlacementBackupPlateDown, false),
            (InputIo.PcbPlacementStopperUp, false),
            (InputIo.PcbPlacementStopperDown, true));

        InputIo[] receipt = [InputIo.PcbPlacementPcbDetected,
            InputIo.PcbPlacementVacuumDetected];
        foreach (var missing in receipt)
        {
            foreach (var signal in receipt)
                io.SetInput(signal, signal != missing);
            Assert.False(recipient.IsAtReceivePosition() && recipient.PcbSecured);
            Assert.Equal(PcbSupplyState.HandingOff, supplier.State);
            Assert.NotEqual(PcbPlacementState.WaitingForSupplyRelease, placer.State);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var waited = false;
            void OnTrace(string message)
            {
                if (message.StartsWith("Waiting for feedback") && message.Contains(nameof(PcbSupplyState.HandingOff)))
                {
                    waited = true;
                    stop.Cancel();
                }
            }
            supplier.Trace += OnTrace;
            await supplier.RunAsync(new(), placer, stop.Token);
            supplier.Trace -= OnTrace;
            Assert.True(waited);
            Assert.True(io.GetOutput(OutputIo.PcbSupplyGripperClosed));
            Assert.True(io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
        }

        foreach (var signal in receipt)
            io.SetInput(signal, true);
        await recipient.PrepareReceiptAsync();
        Assert.True(recipient.IsAtReceivePosition() && recipient.PcbSecured);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.State);
        units.PcbPlacement = false;
        Assert.Equal(PcbPlacementState.Disabled, placer.State);
        Assert.Equal(PcbSupplyState.HandingOff, supplier.State);
        units.PcbPlacement = true;
        units.PcbSupply = false;
        Assert.Equal(PcbSupplyState.Disabled, supplier.State);
        Assert.False(await placer.ExecuteStepAsync(
            placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None));
        units.PcbSupply = true;
        using var released = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var order = new System.Collections.Generic.List<OutputIo>();
        io.OutputChanged += (output, on) =>
        {
            if (!on && output is OutputIo.PcbSupplyIpmFixerForward or OutputIo.PcbSupplyGripperClosed)
            {
                Assert.True(recipient.IsAtReceivePosition() && recipient.PcbSecured);
                Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.State);
                order.Add(output);
                if (output == OutputIo.PcbSupplyGripperClosed)
                    io.SetInput(InputIo.PcbSupplyPcbDetected, false);
            }
        };
        void StopBeforePlacementLifts(string message)
        {
            if (message.StartsWith($"PcbSupplier: {nameof(PcbSupplyState.WaitingForPlacementClear)} ", StringComparison.Ordinal))
                released.Cancel();
        }
        supplier.Trace += StopBeforePlacementLifts;
        await supplier.RunAsync(new(), placer, released.Token);
        supplier.Trace -= StopBeforePlacementLifts;
        Assert.Equal(new[] { OutputIo.PcbSupplyIpmFixerForward, OutputIo.PcbSupplyGripperClosed }, order);
        Assert.True(source.PcbReleased);
        Assert.Equal((50, 10, supplySettings.HandoffPosition.Z), supplyMotion.GetPosition());
        Assert.Equal(PcbSupplyState.WaitingForPlacementClear, supplier.State);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.State);
        io.SetInput(InputIo.PcbSupplyGripperClosed, true); // Both endpoints ON is not released.
        Assert.False(source.PcbReleased);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.State);
        io.SetInput(InputIo.PcbSupplyGripperClosed, false);
        Assert.True(recipient.HandlerRaised);
        Assert.False(recipient.IsAtHorizontalZ());
        io.SetInput(InputIo.PcbPlacementHandlerDown, true); // Both endpoints ON is not confirmed Up.
        Assert.False(recipient.HandlerRaised);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.State);
        Assert.Equal(PcbPlacementHandoff.Unavailable, placer.Handoff);
        io.SetInput(InputIo.PcbPlacementHandlerDown, false);
        using var departureStop = new CancellationTokenSource();
        placementSettings.Motion.HorizontalSpeed = 50;
        var departedInY = false;
        var interrupted = false;
        placementMotion.PositionChanged += (x, y, z) =>
        {
            if (placementMotion.IsMoving)
                Assert.NotEqual(PcbPlacementHandoff.Clear, placer.Handoff);
            if (y > 10 && y < recipe.HeatSink1PcbPlacementPosition.Y)
            {
                departedInY = true;
                Assert.Equal(50, x);
                Assert.Equal(placementSettings.HandoffPosition.Z, z);
                Assert.Equal(PcbPlacementState.PreparingPlacement, placer.State);
                Assert.True(source.IsAtHandoff());
                if (stopDuringDeparture && !interrupted)
                {
                    interrupted = true;
                    departureStop.Cancel();
                }
            }
        };
        using var returnCheck = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var waitedForZ = false;
        var returnAllowed = false;
        void ObserveReturn(string message)
        {
            if (message.StartsWith($"PcbSupplier: {nameof(PcbSupplyState.WaitingForPlacementClear)} ", StringComparison.Ordinal))
                waitedForZ = true;
            if (message.StartsWith($"PcbSupplier: {nameof(PcbSupplyState.MovingToPickup)} ", StringComparison.Ordinal))
            {
                Assert.True(recipient.IsAtHorizontalZ());
                Assert.True(recipient.HandlerRaised);
                Assert.Equal((50, recipe.HeatSink1PcbPlacementPosition.Y, placementSettings.HandoffPosition.Z),
                    placementMotion.GetPosition());
                returnAllowed = true;
                returnCheck.Cancel(); // Stop before XY so the independent Placement departure is checked below.
            }
        }
        supplier.Trace += ObserveReturn;
        var returning = supplier.RunAsync(new(), placer, returnCheck.Token);
        try
        {
            Assert.True(waitedForZ);
            Assert.False(returnAllowed);
            Assert.False(returning.IsCompleted);
            if (stopDuringDeparture)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, departureStop.Token));
                Assert.True(interrupted);
                Assert.False(returnAllowed);
                Assert.False(returning.IsCompleted);
                Assert.True(source.IsAtHandoff());
                Assert.Equal(PcbPlacementState.PreparingPlacement, placer.State);
                Assert.NotEqual(PcbPlacementHandoff.Clear, placer.Handoff);
                return;
            }
            await placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
            await returning.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(returnAllowed);
        }
        finally
        {
            returnCheck.Cancel();
            await returning;
            supplier.Trace -= ObserveReturn;
        }
        Assert.True(source.PcbReleased && recipient.HandlerRaised);
        Assert.True(departedInY);
        Assert.True(recipient.IsAtHorizontalZ());
        Assert.Equal(PcbPlacementState.PlacingPcb, placer.State);
        Assert.True(source.IsAtHandoff());
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementVacuumEjector)
                io.SetInput(InputIo.PcbPlacementVacuumDetected, on);
        };
        io.SetOutput(OutputIo.PcbPlacementVacuumEjector, true);
        await placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
        Assert.Equal((70, 20, 8), placementMotion.GetPosition());
        Assert.True(source.IsAtHandoff());

        var exitRecipe = new PcbSupplyRecipe { Pcb1PickPosition = new() { X = 15, Y = 30, Z = 5 } };
        using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var diagonalExit = false;
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            if (supplyMotion.IsMovingHorizontal)
                Assert.Equal(supplySettings.HandoffPosition.Z, z);
            Assert.Equal(PlacementCylinderState.Up, recipient.Lift);
            diagonalExit |= x is > 15 and < 50 && y is > 10 and < 30;
        };
        supplier.Trace += message =>
        {
            if (message.StartsWith("PcbSupplier: WaitingForCarrier ", StringComparison.Ordinal))
                exited.Cancel();
        };
        await supplier.RunAsync(exitRecipe, placer, exited.Token);
        Assert.True(diagonalExit);
        Assert.Equal((15, 30, supplySettings.RotationZ), supplyMotion.GetPosition());
        Assert.Equal(PcbSupplyRotationState.Rotated, source.Rotation);

        source.Motion.InvalidateFeedback(new System.IO.IOException("Supply feedback disconnected."));
        Assert.False(source.IsAtHandoff(live: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplyKeepsGripperClosedWhenPlacementLosesVacuumDuringRelease(bool repeat)
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            HandoffPosition = new() { X = 50, Y = 10 },
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
            HandoffPosition = Position(50, 10, 8),
            ReceiveZ = 12,
        };
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations);

        var supplier = new PcbSupplier(supplyMotion, new MotionStatus(supplyMotion),
            io,
            supplySettings,
            new());
        var source = supplier;
        var placer = CreatePlacer(supplier, placementMotion, io, placementSettings);
        var recipient = placer;
        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        await source.PrepareHandoffAsync(CancellationToken.None);
        await placementMotion.MoveToXYAsync(50, 10, placementSettings.Motion.HorizontalSpeed);
        await placementMotion.MoveAxisAsync(MotionAxis.Z, placementSettings.ReceiveZ!.Value, placementSettings.Motion.ZSpeed);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        io.SetInput(InputIo.PcbPlacementVacuumDetected, true);
        await recipient.PrepareReceiptAsync();
        await recipient.SetIpmLiftDownAsync(!repeat);
        Assert.Equal(PcbPlacementHandoff.Holding, placer.Handoff);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyIpmFixerForward && !on)
                io.SetInput(InputIo.PcbPlacementVacuumDetected, false);
        };

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => supplier.RunAsync(new(), placer, stop.Token, repeat));
        Assert.Contains("before supply opened its gripper", error.Message);
        Assert.True(io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.True(source.IsAtHandoff());
        Assert.False(io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
    }

    [Trait("Category", "MachineFlow")]
    [Fact]
    public async Task PlacementKeepsStartedTargetsAndReselectsAfterStop()
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            RotationZ = 0,
            HandoffPosition = new() { X = 50, Y = 10 },
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
            HandoffPosition = Position(50, 10, 8),
            ReceiveZ = 12,
        };
        var supplyRecipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 30, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 30, Z = 5 },
        };
        var placementRecipe = new PcbPlacementRecipe
        {
            HeatSink1PcbPlacementPosition = Position(70, 20, 10),
            HeatSink2PcbPlacementPosition = Position(80, 20, 10),
        };
        var io = new VirtualIoService(
            Outputs(
                new PcbSupplyHardwareSettings(),
                new PcbPlacementHandlerHardwareSettings(),
                new ConveyorHardwareSettings()),
            new MachineOptions());
        var machine = new VirtualMachine(io, []);
        io.SetInput(InputIo.AutoMode, false);
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations);
        supplyMotion.PositionChanged += (x, y, z) => machine.UpdateSupplyPosition(
            x,
            y,
            z,
            (supplyRecipe.Pcb1PickPosition.X, supplyRecipe.Pcb1PickPosition.Y, supplyRecipe.Pcb1PickPosition.Z),
            (supplyRecipe.Pcb2PickPosition.X, supplyRecipe.Pcb2PickPosition.Y, supplyRecipe.Pcb2PickPosition.Z),
            supplySettings.HandoffPosition);
        placementMotion.PositionChanged += (x, y, z) => machine.UpdatePlacementPosition(
            x,
            y,
            z,
            placementSettings.HandoffPosition,
            placementSettings.ReceiveZ,
            placementRecipe.HeatSink1PcbPlacementPosition,
            placementRecipe.HeatSink2PcbPlacementPosition);

        var supply = new PcbSupplier(supplyMotion, new MotionStatus(supplyMotion),
            io,
            supplySettings,
            new());
        var supplyHandler = supply;
        var work = ConveyorStation.CreatePcbPlacement(io);
        var placement = CreatePlacer(supply, placementMotion, io, placementSettings, placementRecipe, work);
        var placementHandler = placement;
        var returnedPositions = new System.Collections.Generic.List<(double X, double Y, double Z)>();
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementHandlerRotate)
                Assert.False(on);
            if (output == OutputIo.PcbSupplyRotate && on && supplyMotion.GetPosition().X > 0)
                returnedPositions.Add(supplyMotion.GetPosition());
        };
        var handoffEntries = 0;
        var enteredHandoffPrepared = true;
        var movedWithLoweredCylinder = false;
        var carriedWithIpmRaised = false;
        var wasAtHandoff = false;
        var heatSinkChangedDuringMove = false;
        placementMotion.PositionChanged += (x, y, _) =>
        {
            movedWithLoweredCylinder |= placementMotion.IsMovingHorizontal && !placementHandler.HandlerRaised;
            carriedWithIpmRaised |= placementMotion.IsMovingHorizontal
                && placementHandler.Pcb == PlacementPcbState.Secured
                && placementHandler.IpmLift != PlacementCylinderState.Down;
            var inside = x is >= 40 and <= 60 && y is >= 0 and <= 15;
            if (inside && !wasAtHandoff)
            {
                handoffEntries++;
                enteredHandoffPrepared &= placementHandler.IpmLift == PlacementCylinderState.Down;
            }

            wasAtHandoff = inside;
            if (!heatSinkChangedDuringMove && x > 75 && y == 20)
            {
                heatSinkChangedDuringMove = true;
                io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
                io.SetInput(InputIo.PcbPlacementHeatSink2Present, false);
            }
        };
        var placementPhase = 0;
        var vacuumApplied = false;
        io.OutputChanged += (output, value) =>
        {
            if (output == OutputIo.PcbPlacementVacuumEjector)
            {
                vacuumApplied |= value;
                if (vacuumApplied && !value)
                {
                    placementPhase = 1;
                }
            }
            else if (output == OutputIo.PcbPlacementIpmDown && placementPhase == 1 && !value)
            {
                placementPhase = 2;
            }
            else if (output == OutputIo.PcbPlacementIpmDown && placementPhase == 2 && value)
            {
                placementPhase = 3;
            }
        };

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        await work.SeatAsync(CancellationToken.None);
        await supplyHandler.SetRotatedAsync(true);
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, false);
        VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);

        Assert.True(work.CarrierSeated);
        using var supplyCancellation = new CancellationTokenSource();
        var supplyRun = supply.RunAsync(supplyRecipe, placement, supplyCancellation.Token);
        using var firstStop = new CancellationTokenSource();
        var firstRun = placement.RunAsync(firstStop.Token);
        Assert.True(
            await WaitUntilAsync(
                () => placementHandler.Pcb == PlacementPcbState.Secured,
                TimeSpan.FromSeconds(10)));
        Assert.Equal(HeatSinkSlot.HeatSink1, placement.TargetHeatSink);
        firstStop.Cancel();
        await firstRun;

        Assert.False(work.Completed);
        Assert.Empty(work.Assemblies);
        Assert.Equal(PlacementPcbState.Secured, placementHandler.Pcb);

        io.SetInputs((InputIo.PcbPlacementHeatSink1Present, false), (InputIo.PcbPlacementHeatSink2Present, true));
        using var resumed = new CancellationTokenSource();
        var resumedRun = placement.RunAsync(resumed.Token);
        Assert.Equal(HeatSinkSlot.HeatSink2, placement.TargetHeatSink);
        var completed = await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(10));
        var prefetched = completed
            && await WaitUntilAsync(
                () => placementHandler.Pcb == PlacementPcbState.Secured
                    && placement.State == PcbPlacementState.WaitingForCarrier,
                TimeSpan.FromSeconds(10));
        if (prefetched)
            Assert.True(await WaitUntilAsync(() => returnedPositions.Count >= 2, TimeSpan.FromSeconds(3)),
                $"Supply return positions: {string.Join(", ", returnedPositions)}");
        var stoppedState = placement.State;
        var stoppedTarget = placement.TargetHeatSink;

        resumed.Cancel();
        supplyCancellation.Cancel();
        await Task.WhenAll(resumedRun, supplyRun);

        Assert.True(completed, $"Placement stopped at {stoppedState}, target={stoppedTarget}, position={placementMotion.GetPosition()}.");
        Assert.True(prefetched);
        Assert.True(returnedPositions.Count >= 2);
        Assert.Equal((20, 30, supplySettings.RotationZ), returnedPositions[0]);
        Assert.Equal((10, 30, supplySettings.RotationZ), returnedPositions[1]);
        Assert.False(movedWithLoweredCylinder);
        Assert.False(carriedWithIpmRaised);
        Assert.True(heatSinkChangedDuringMove);
        Assert.True(io.GetInput(InputIo.PcbPlacementHeatSink1Present));
        Assert.False(io.GetInput(InputIo.PcbPlacementHeatSink2Present));
        Assert.True(handoffEntries >= 2);
        Assert.True(enteredHandoffPrepared);
        var assembly = Assert.Single(work.Assemblies);
        Assert.Equal(HeatSinkSlot.HeatSink2, assembly.HeatSink);
        Assert.Equal(PlacementCylinderState.Down, placementHandler.IpmLift);
        Assert.Equal(PlacementCylinderState.Up, placementHandler.Lift);
        Assert.True(placementHandler.IsAtHorizontalZ());
        Assert.Equal(3, placementPhase);
        Assert.False(supplyMotion.IsMoving);
        Assert.False(placementMotion.IsMoving);
    }

    [Trait("Category", "MachineFlow")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplyChecksBothSlotsBeforeDroppingReady(bool secondPcbPresent)
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            HandoffPosition = new() { X = 50, Y = 10 },
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
            HandoffPosition = Position(50, 10, 8),
            ReceiveZ = 12,
        };
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 30, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 30, Z = 5 },
        };
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations);

        var supply = new PcbSupplier(supplyMotion, new MotionStatus(supplyMotion),
            io,
            supplySettings,
            new());
        var supplyHandler = supply;
        var placement = CreatePlacer(supply, placementMotion, io, placementSettings);
        var placementHandler = placement;
        var pcb1Visited = false;
        var pcb2Visited = false;
        var pcb1Visits = 0;
        var atPcb1 = false;
        var readyDroppedBeforeClear = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyReadyToFront1 && !on
                && io.GetInput(InputIo.PcbSupplyAvailableFromFront1))
            {
                readyDroppedBeforeClear |= !pcb2Visited
                    || !supplyHandler.IsAtRotationZ()
                    || supplyHandler.Pcb == PcbSupplyPcbState.Detected;
            }
        };
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            var nowAtPcb1 = IsAt(
                x,
                y,
                z,
                recipe.Pcb1PickPosition.X,
                recipe.Pcb1PickPosition.Y!.Value,
                recipe.Pcb1PickPosition.Z);
            if (nowAtPcb1 && !atPcb1)
                pcb1Visits++;
            atPcb1 = nowAtPcb1;
            pcb1Visited |= nowAtPcb1;
            pcb2Visited |= IsAt(
                x,
                y,
                z,
                recipe.Pcb2PickPosition.X,
                recipe.Pcb2PickPosition.Y!.Value,
                recipe.Pcb2PickPosition.Z);
            if (pcb2Visited && secondPcbPresent)
            {
                io.SetInput(InputIo.PcbSupplyPcbDetected, true);
            }
        };

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        if (secondPcbPresent)
        {
            await placementMotion.MoveToXYAsync(50, 10, placementSettings.Motion.HorizontalSpeed);
            await placementMotion.MoveAxisAsync(MotionAxis.Z, 8, placementSettings.Motion.ZSpeed);
        }
        io.SetInput(InputIo.AutoMode, false); // Production SMEMA uses the physical inputs.
        using var cancellation = new CancellationTokenSource();
        var run = supply.RunAsync(recipe, placement, cancellation.Token);
        await WaitForOutputAsync(io, OutputIo.PcbSupplyReadyToFront1, true);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);

        var checkedBoth = await WaitUntilAsync(
            () => pcb1Visited && pcb2Visited && !io.GetOutput(OutputIo.PcbSupplyReadyToFront1),
            TimeSpan.FromSeconds(5));
        var atRotationZ = supply.IsAtRotationZ();
        var nextCarrierAccepted = true;
        if (secondPcbPresent && checkedBoth)
        {
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            nextCarrierAccepted = await WaitUntilAsync(
                () => io.GetOutput(OutputIo.PcbSupplyReadyToFront1),
                TimeSpan.FromSeconds(2));
        }

        cancellation.Cancel();
        await run;

        Assert.True(checkedBoth);
        Assert.Equal(1, pcb1Visits);
        Assert.True(atRotationZ);
        Assert.False(readyDroppedBeforeClear);
        Assert.True(nextCarrierAccepted);
        Assert.Equal(secondPcbPresent, io.GetInput(InputIo.PcbSupplyPcbDetected));
        Assert.Equal(secondPcbPresent, io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
    }

    [Fact]
    public async Task SupplyUnrotatesAtRotationZAndCancelsApproachAtHandoffZ()
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 100, ZSpeed = 20 },
            RotationZ = 3,
            HandoffPosition = new() { X = 50, Y = 10, Z = 7 },
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
            HandoffPosition = Position(50, 10, 8),
            ReceiveZ = 12,
        };
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = new VirtualMotionService(
            supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations);

        var supply = new PcbSupplier(supplyMotion, new MotionStatus(supplyMotion),
            io,
            supplySettings,
            new());
        var supplyHandler = supply;
        var placement = CreatePlacer(supply, placementMotion, io, placementSettings);
        var placementHandler = placement;

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        await supplyHandler.SetRotatedAsync(true);
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await supplyHandler.SetGripperClosedAsync(false);
        await supplyHandler.SetIpmFixerAsync(false);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);

        await placementHandler.SetIpmLiftDownAsync(true);
        await placementMotion.MoveToXYAsync(50, 10, placementSettings.Motion.HorizontalSpeed);
        await placementMotion.MoveAxisAsync(MotionAxis.Z, 8, placementSettings.Motion.ZSpeed);

        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interrupted = false;
        var enteredWithoutUnrotatedFeedback = false;
        var changedHeightInside = false;
        var unrotatedAtRotationZ = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyRotate && !on)
                unrotatedAtRotationZ = supplyHandler.IsAtRotationZ();
        };
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            enteredWithoutUnrotatedFeedback |= x >= 40
                && supplyHandler.Rotation != PcbSupplyRotationState.Unrotated;
            changedHeightInside |= x >= 40
                && Math.Abs(z - supplySettings.HandoffPosition.Z) > MotionService.PositionToleranceMillimeters;
            if (!interrupted && x >= 45)
            {
                interrupted = true;
                firstStop.Cancel();
            }
        };
        var recipe = new PcbSupplyRecipe { Pcb1PickPosition = new() { X = 10, Y = 0, Z = 5 } };
        await supply.RunAsync(recipe, placement, firstStop.Token);
        Assert.True(interrupted);
        Assert.False(supplyHandler.IsAtHandoff());
        var stoppedPosition = supplyMotion.GetPosition();
        Assert.InRange(stoppedPosition.Y, 0.1, supplySettings.HandoffPosition.Y - 0.1);
        Assert.False(supplyMotion.IsMoving);
        Assert.Equal(stoppedPosition, supplyMotion.GetPosition());
        Assert.False(io.GetOutput(OutputIo.PcbSupplyRotate));

        Assert.True(unrotatedAtRotationZ);
        Assert.False(enteredWithoutUnrotatedFeedback);
        Assert.False(changedHeightInside);
        Assert.Equal(supplySettings.HandoffPosition.Z, stoppedPosition.Z);
        Assert.True(supplyHandler.PcbSecured);
    }

    private static PcbPlacer CreatePlacer(
        PcbSupplier supply,
        IXyMotion motion,
        IIoService io,
        PcbPlacementHandlerSettings settings,
        PcbPlacementRecipe? recipe = null,
        ConveyorStation? work = null)
    {
        var recipes = new RecipeManager(OpenMachineStore(), new());
        recipes.Current.PcbPlacement = recipe ?? new();
        return new(motion, new(motion), io, settings, supply, work ?? ConveyorStation.CreatePcbPlacement(io), recipes, new());
    }

    private static MotionSettings FastMotion()
    {
        return new()
        {
            HorizontalSpeed = 2_000,
            ZSpeed = 2_000,
        };
    }

    private static AxisPosition Position(double x, double y, double z)
    {
        return new() { X = x, Y = y, Z = z };
    }

    private static bool IsAt(
        double x,
        double y,
        double z,
        double targetX,
        double targetY,
        double targetZ)
    {
        return Math.Abs(x - targetX) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(y - targetY) <= MotionService.PositionToleranceMillimeters
            && Math.Abs(z - targetZ) <= MotionService.PositionToleranceMillimeters;
    }
}
