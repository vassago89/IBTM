using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Virtual;
using Xunit;
using static IBTM.Virtual.Tests.VirtualTest;

namespace IBTM.Virtual.Tests;

public sealed class PcbTransferTests
{
    [Fact]
    public async Task SupplyTestAvailableWakesPickupAndNeedsAnOffEdgeForTheNextCarrier()
    {
        var settings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            BufferHandoffPosition = new() { X = 50, Y = 10 },
        };
        var placementSettings = new PcbPlacementHandlerSettings { Motion = FastMotion() };
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var motion = Motion(settings.Motion, new());
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, new(), horizontalZ: () => placementSettings.BufferHandoffPosition.Z);
        var handler = new PcbSupplyHandler(motion, io, settings,
            new PcbBufferSettings { SupplyBoundary1 = 40, SupplyBoundary2 = 60 });
        var placement = new PcbPlacementHandler(placementMotion, io, placementSettings);
        var supplier = new PcbSupplier(handler, Buffer(handler, placement, settings, placementSettings));
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Z = 5 },
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
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var run = supplier.RunAsync(recipe, stop.Token);
        try
        {
            handler.TestUpstreamCarrierAvailable = true;
            Assert.True(await WaitUntilAsync(() => completedCarriers == 1, TimeSpan.FromSeconds(2)));
            Assert.Equal((20, settings.CarrierY, settings.RotationZ), motion.GetPosition());
            Assert.False(io.GetInput(InputIo.PcbSupplyAvailableFromFront1));
            Assert.True(handler.TestUpstreamCarrierAvailable);

            // In teaching, TEST alone reports departure even if the real input stays on.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            handler.TestUpstreamCarrierAvailable = false;
            Assert.False(handler.UpstreamCarrierAvailable);

            handler.TestUpstreamCarrierAvailable = true;
            Assert.True(await WaitUntilAsync(() => completedCarriers == 2, TimeSpan.FromSeconds(2)));
            Assert.Equal((20, settings.CarrierY, settings.RotationZ), motion.GetPosition());
            Assert.True(io.GetInput(InputIo.PcbSupplyAvailableFromFront1));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.True(handler.TestUpstreamCarrierAvailable);
        Assert.False(readyWentOn);
        var newHandler = new PcbSupplyHandler(motion, io, settings, new());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffAcceptsEitherArrivalOrderAndReceivesWithCylinderOnly(bool supplyFirst)
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            BufferHandoffPosition = new() { X = 50, Y = 10 },
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
            BufferHandoffPosition = Position(50, 10, 8),
        };
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations, horizontalZ: () => placementSettings.BufferHandoffPosition.Z);
        var source = new PcbSupplyHandler(supplyMotion, io, supplySettings,
            new PcbBufferSettings { SupplyBoundary1 = 40, SupplyBoundary2 = 60 });
        var recipient = new PcbPlacementHandler(placementMotion, io, placementSettings);
        var buffer = Buffer(source, recipient, supplySettings, placementSettings);
        var placer = new PcbPlacer(buffer, recipient, new PcbPlacementWork(ConveyorStation.PcbPlacement(io)));
        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await source.SetGripperClosedAsync(true);
        await source.SetIpmFixerAsync(true);
        await source.SetRotatedAsync(true);
        Assert.True(recipient.IsAtHorizontalZ());
        await recipient.SetRotatedAsync(true);
        await recipient.SetLiftDownAsync(true);

        var changedHandoffHeight = false;
        placementMotion.PositionChanged += (_, _, z) =>
            changedHandoffHeight |= Math.Abs(z - placementSettings.BufferHandoffPosition.Z)
                > MotionService.PositionToleranceMillimeters;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (supplyFirst)
        {
            // Placement is still outside the shared area; Supply may arrive first.
            Assert.True(buffer.CanEnterSupply());
            await source.MoveToHandoffAsync(timeout.Token);
        }
        Assert.Equal(PcbPlacementState.RaisingHandler, placer.State(new(), HeatSinkSlot.HeatSink1));
        var movedWithCylinderDown = false;
        placementMotion.MovingChanged += moving =>
        {
            movedWithCylinderDown |= moving && recipient.Lift != PlacementCylinderState.Up;
        };
        for (var step = 0; step < 8 && !buffer.IsPlacementAtHandoff(); step++)
        {
            await placer.PlaceStepAsync(new(), HeatSinkSlot.HeatSink1, timeout.Token)!;
        }

        Assert.True(buffer.IsPlacementAtHandoff());
        Assert.Equal((50, 10, 8), placementMotion.GetPosition());
        Assert.Equal(PlacementCylinderState.Up, recipient.Lift);
        Assert.False(movedWithCylinderDown);
        if (!supplyFirst)
        {
            // Placement waits at receiving Z without descending its cylinder.
            Assert.Equal(PcbPlacementState.WaitingForSupply, placer.State(new(), HeatSinkSlot.HeatSink1));
            Assert.Null(placer.PlaceStepAsync(new(), HeatSinkSlot.HeatSink1, timeout.Token));
            Assert.True(buffer.CanEnterSupply());
            await source.MoveToHandoffAsync(timeout.Token);
        }

        Assert.False(buffer.HasConflict());
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementVacuumEjector)
                io.SetInput(InputIo.PcbPlacementVacuumDetected, on);
        };
        var axisMovedDuringReceipt = false;
        placementMotion.MovingChanged += moving => axisMovedDuringReceipt |= moving;
        Assert.Equal(PcbPlacementState.LoweringHandler, placer.State(new(), HeatSinkSlot.HeatSink1));
        await placer.PlaceStepAsync(new(), HeatSinkSlot.HeatSink1, timeout.Token)!;
        Assert.Equal(PlacementCylinderState.Down, recipient.Lift);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        Assert.Equal(PcbPlacementState.ApplyingVacuum, placer.State(new(), HeatSinkSlot.HeatSink1));
        await placer.PlaceStepAsync(new(), HeatSinkSlot.HeatSink1, timeout.Token)!;
        Assert.Equal(PcbPlacementState.ClosingGripper, placer.State(new(), HeatSinkSlot.HeatSink1));
        await placer.PlaceStepAsync(new(), HeatSinkSlot.HeatSink1, timeout.Token)!;
        Assert.True(buffer.IsPlacementSecuredAtHandoff());
        Assert.False(axisMovedDuringReceipt);
        Assert.False(changedHandoffHeight);
        Assert.Equal((50, 10, 8), placementMotion.GetPosition());
    }

    [Fact]
    public async Task DirectHandoffWaitsForAllThreePlacementSignalsAndSupplyExit()
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            CarrierY = 30,
            BufferHandoffPosition = new() { X = 50, Y = 10 },
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
            BufferHandoffPosition = Position(50, 10, 8),
        };
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations, horizontalZ: () => placementSettings.BufferHandoffPosition.Z);
        var source = new PcbSupplyHandler(supplyMotion, io, supplySettings,
            new PcbBufferSettings { SupplyBoundary1 = 40, SupplyBoundary2 = 60 });
        var recipient = new PcbPlacementHandler(placementMotion, io, placementSettings);
        var buffer = Buffer(source, recipient, supplySettings, placementSettings);
        var supplier = new PcbSupplier(source, buffer);
        var placer = new PcbPlacer(buffer, recipient, new PcbPlacementWork(ConveyorStation.PcbPlacement(io)));
        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, true);
        Assert.False(buffer.CanEnterPlacement());
        await supplyMotion.MoveToAsync(50, 10, supplySettings.RotationZ);
        Assert.True(buffer.CanEnterPlacement());
        await placementMotion.MoveToAsync(50, 10, 8);
        await recipient.SetLiftDownAsync(true);

        InputIo[] receipt = [InputIo.PcbPlacementPcbDetected,
            InputIo.PcbPlacementVacuumDetected, InputIo.PcbPlacementIpmGripperClosed];
        io.SetInput(InputIo.PcbPlacementIpmGripperOpen, false);
        foreach (var missing in receipt)
        {
            foreach (var signal in receipt)
                io.SetInput(signal, signal != missing);
            Assert.False(buffer.IsPlacementSecuredAtHandoff());
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var waited = false;
            void OnTrace(string message)
            {
                if (message.StartsWith("Waiting for feedback") && message.Contains("WaitingForPlacement"))
                {
                    waited = true;
                    stop.Cancel();
                }
            }
            supplier.Trace += OnTrace;
            await supplier.RunAsync(new(), stop.Token);
            supplier.Trace -= OnTrace;
            Assert.True(waited);
            Assert.True(io.GetOutput(OutputIo.PcbSupplyGripperClosed));
            Assert.True(io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
        }

        foreach (var signal in receipt)
            io.SetInput(signal, true);
        Assert.True(buffer.IsPlacementSecuredAtHandoff());
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.State(new(), HeatSinkSlot.HeatSink1));
        using var released = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var order = new System.Collections.Generic.List<OutputIo>();
        io.OutputChanged += (output, on) =>
        {
            if (!on && output is OutputIo.PcbSupplyIpmFixerForward or OutputIo.PcbSupplyGripperClosed)
            {
                Assert.True(buffer.IsPlacementSecuredAtHandoff());
                Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.State(new(), HeatSinkSlot.HeatSink1));
                order.Add(output);
                if (output == OutputIo.PcbSupplyGripperClosed)
                    io.SetInput(InputIo.PcbSupplyPcbDetected, false);
            }
        };
        void StopBeforePlacementLifts(string message)
        {
            if (message.StartsWith("PcbSupplier: WaitingForPlacementLift ", StringComparison.Ordinal))
                released.Cancel();
        }
        supplier.Trace += StopBeforePlacementLifts;
        await supplier.RunAsync(new(), released.Token);
        supplier.Trace -= StopBeforePlacementLifts;
        Assert.Equal(new[] { OutputIo.PcbSupplyIpmFixerForward, OutputIo.PcbSupplyGripperClosed }, order);
        Assert.True(source.PcbReleased);
        Assert.Equal((50, 10, supplySettings.RotationZ), supplyMotion.GetPosition());
        Assert.Equal(PcbPlacementState.RaisingHandler, placer.State(new(), HeatSinkSlot.HeatSink1));
        io.SetInput(InputIo.PcbSupplyGripperClosed, true); // Both endpoints ON is not released.
        Assert.False(source.PcbReleased);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.State(new(), HeatSinkSlot.HeatSink1));
        io.SetInput(InputIo.PcbSupplyGripperClosed, false);
        Assert.False(buffer.CanExitSupply());
        io.SetInput(InputIo.PcbPlacementHandlerUp, true); // Down still ON is not confirmed Up.
        Assert.False(buffer.CanExitSupply());
        io.SetInput(InputIo.PcbPlacementHandlerUp, false);
        await placer.PlaceStepAsync(new(), HeatSinkSlot.HeatSink1, CancellationToken.None)!;
        Assert.True(buffer.CanExitSupply());
        Assert.Equal(PcbPlacementState.WaitingForSupplyExit, placer.State(new(), HeatSinkSlot.HeatSink1));

        var exitRecipe = new PcbSupplyRecipe { Pcb1PickPosition = new() { X = 15, Z = 5 } };
        using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var diagonalExit = false;
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            Assert.Equal(supplySettings.RotationZ, z);
            Assert.Equal(PlacementCylinderState.Up, recipient.Lift);
            diagonalExit |= x is > 15 and < 50 && y is > 10 and < 30;
            if (IsAt(x, y, z, 15, 30, supplySettings.RotationZ))
                exited.Cancel();
        };
        await supplier.RunAsync(exitRecipe, exited.Token);
        Assert.True(diagonalExit);
        Assert.Equal((15, 30, supplySettings.RotationZ), supplyMotion.GetPosition());
        Assert.True(buffer.IsSupplyOutside());
        Assert.NotEqual(PcbPlacementState.WaitingForSupplyExit, placer.State(new(), HeatSinkSlot.HeatSink1));

        source.Motion.InvalidateFeedback(new System.IO.IOException("Supply feedback disconnected."));
        Assert.False(buffer.IsSupplyOutside(live: false));
    }

    [Fact]
    public async Task SupplyKeepsGripperClosedWhenPlacementLosesVacuumDuringRelease()
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(), BufferHandoffPosition = new() { X = 50, Y = 10 },
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(), BufferHandoffPosition = Position(50, 10, 8),
        };
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations, horizontalZ: () => placementSettings.BufferHandoffPosition.Z);
        var source = new PcbSupplyHandler(supplyMotion, io, supplySettings,
            new PcbBufferSettings { SupplyBoundary1 = 40, SupplyBoundary2 = 60 });
        var recipient = new PcbPlacementHandler(placementMotion, io, placementSettings);
        var buffer = Buffer(source, recipient, supplySettings, placementSettings);
        var supplier = new PcbSupplier(source, buffer);
        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        await supplyMotion.MoveToAsync(50, 10, supplySettings.RotationZ);
        await placementMotion.MoveToAsync(50, 10, 8);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        io.SetInput(InputIo.PcbPlacementVacuumDetected, true);
        io.SetInput(InputIo.PcbPlacementIpmGripperOpen, false);
        io.SetInput(InputIo.PcbPlacementIpmGripperClosed, true);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyIpmFixerForward && !on)
                io.SetInput(InputIo.PcbPlacementVacuumDetected, false);
        };

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => supplier.RunAsync(new(), stop.Token));
        Assert.Contains("before supply opened its gripper", error.Message);
        Assert.True(io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.True(buffer.IsSupplyAtHandoff());
        Assert.False(io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
    }

    [Fact]
    public void PlacementRecoveryChangesOnlyTheDisplayedSelections()
    {
        var io = new VirtualIoService(Outputs(new ConveyorHardwareSettings()), new MachineOptions());
        var work = new PcbPlacementWork(ConveyorStation.PcbPlacement(io));
        io.Initialize();
        VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        var first = work.Assembly(HeatSinkSlot.HeatSink1);
        var hidden = work.Assembly(HeatSinkSlot.HeatSink2);
        work.Complete(work.CurrentJob);
        var displayed = Enum.GetValues<HeatSinkSlot>()
            .Where(work.HeatSinkPresent)
            .Select(heatSink => (HeatSink: heatSink, Completed: true))
            .ToArray();
        Assert.Equal(HeatSinkSlot.HeatSink1, Assert.Single(displayed).HeatSink);

        io.SetInputs((InputIo.PcbPlacementHeatSink1Present, false), (InputIo.PcbPlacementHeatSink2Present, true));
        work.PrepareRecovery(displayed);

        Assert.False(work.Completed);
        Assert.Same(first, work.Assemblies.Single(item => item.HeatSink == HeatSinkSlot.HeatSink1));
        Assert.Same(hidden, work.Assemblies.Single(item => item.HeatSink == HeatSinkSlot.HeatSink2));

        work.PrepareRecovery([(HeatSinkSlot.HeatSink1, false)]);

        Assert.Same(hidden, Assert.Single(work.Assemblies));
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
            CarrierY = 30,
            BufferHandoffPosition = new() { X = 50, Y = 10 },
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
            BufferHandoffPosition = Position(50, 10, 8),
        };
        var supplyRecipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Z = 5 },
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
            placementSettings.Motion, operations, horizontalZ: () => placementSettings.BufferHandoffPosition.Z);
        supplyMotion.PositionChanged += (x, y, z) => machine.UpdateSupplyPosition(
            x,
            y,
            z,
            supplySettings.CarrierY,
            (
                supplyRecipe.Pcb1PickPosition.X,
                supplyRecipe.Pcb1PickPosition.Z),
            (
                supplyRecipe.Pcb2PickPosition.X,
                supplyRecipe.Pcb2PickPosition.Z),
            supplySettings.BufferHandoffPosition,
            supplySettings.RotationZ);
        placementMotion.PositionChanged += (x, y, z) => machine.UpdatePlacementPosition(
            x,
            y,
            z,
            placementSettings.BufferHandoffPosition,
            placementRecipe.HeatSink1PcbPlacementPosition,
            placementRecipe.HeatSink2PcbPlacementPosition);
        var placementHandler = new PcbPlacementHandler(placementMotion, io, placementSettings);
        var supplyHandler = new PcbSupplyHandler(
            supplyMotion,
            io,
            supplySettings,
            new PcbBufferSettings { SupplyBoundary1 = 40, SupplyBoundary2 = 60 });
        var buffer = Buffer(supplyHandler, placementHandler, supplySettings, placementSettings);
        var supply = new PcbSupplier(supplyHandler, buffer);
        var work = new PcbPlacementWork(ConveyorStation.PcbPlacement(io));
        var placement = new PcbPlacer(buffer, placementHandler, work);
        var returnedPositions = new System.Collections.Generic.List<(double X, double Y, double Z)>();
        supply.Trace += message =>
        {
            if (message.StartsWith("PcbSupplier: UnrotatingForPickup ", StringComparison.Ordinal))
                returnedPositions.Add(supplyMotion.GetPosition());
        };
        var bufferEntries = 0;
        var enteredBufferPrepared = true;
        var movedWithLoweredCylinder = false;
        var carriedWithIpmRaised = false;
        var wasInsideBuffer = false;
        var heatSinkChangedDuringMove = false;
        placementMotion.PositionChanged += (x, y, _) =>
        {
            movedWithLoweredCylinder |= placementMotion.IsMovingHorizontal && !placementHandler.CanMoveHorizontal;
            carriedWithIpmRaised |= placementMotion.IsMovingHorizontal
                && placementHandler.Pcb == PlacementPcbState.Secured
                && placementHandler.IpmLift != PlacementCylinderState.Down;
            var inside = x is >= 40 and <= 60 && y is >= 0 and <= 15;
            if (inside && !wasInsideBuffer)
            {
                bufferEntries++;
                enteredBufferPrepared &= placementHandler.IpmGripper == PlacementGripperState.Open
                    && placementHandler.IpmLift == PlacementCylinderState.Down;
            }

            wasInsideBuffer = inside;
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
            else if (output == OutputIo.PcbPlacementIpmGripperClose
                && placementPhase == 1
                && !value)
            {
                placementPhase = 2;
            }
            else if (output == OutputIo.PcbPlacementIpmDown && placementPhase == 2 && !value)
            {
                placementPhase = 3;
            }
            else if (output == OutputIo.PcbPlacementIpmGripperClose
                && placementPhase == 3
                && value)
            {
                placementPhase = 4;
            }
            else if (output == OutputIo.PcbPlacementIpmDown && placementPhase == 4 && value)
            {
                placementPhase = 5;
            }
        };

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        await work.Station.SeatAsync(CancellationToken.None);
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, false);
        VirtualTest.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);

        Assert.True(work.CarrierSeated);
        using var supplyCancellation = new CancellationTokenSource();
        var supplyRun = supply.RunAsync(supplyRecipe, supplyCancellation.Token);
        using var firstStop = new CancellationTokenSource();
        var firstRun = placement.RunAsync(placementRecipe, firstStop.Token);
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
        var resumedRun = placement.RunAsync(placementRecipe, resumed.Token);
        Assert.Equal(HeatSinkSlot.HeatSink2, placement.TargetHeatSink);
        var completed = await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(10));
        var prefetched = completed
            && await WaitUntilAsync(
                () => placementHandler.Pcb == PlacementPcbState.Secured
                    && placement.State(placementRecipe) == PcbPlacementState.WaitingForCarrier,
                TimeSpan.FromSeconds(10));
        var stoppedState = placement.State(placementRecipe);
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
        Assert.True(bufferEntries >= 2);
        Assert.True(enteredBufferPrepared);
        var assembly = Assert.Single(work.Assemblies);
        Assert.Equal(HeatSinkSlot.HeatSink2, assembly.HeatSink);
        Assert.Equal(PlacementCylinderState.Down, placementHandler.IpmLift);
        Assert.Equal(PlacementCylinderState.Up, placementHandler.Lift);
        Assert.True(placementHandler.IsAtHorizontalZ());
        Assert.Equal(5, placementPhase);
        Assert.False(buffer.HasConflict());
        Assert.False(supplyMotion.IsMoving);
        Assert.False(placementMotion.IsMoving);
    }

    [Trait("Category", "MachineFlow")]
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task SupplyKeepsCheckedSlotsUntilUpstreamCarrierChanges(
        bool secondPcbPresent,
        bool replaceDuringStop,
        bool stopOnPcbDetection)
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            CarrierY = 30,
            BufferHandoffPosition = new() { X = 50, Y = 10 },
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
            BufferHandoffPosition = Position(50, 10, 8),
        };
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Z = 5 },
        };
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations, horizontalZ: () => placementSettings.BufferHandoffPosition.Z);
        var placementHandler = new PcbPlacementHandler(placementMotion, io, placementSettings);
        var supplyHandler = new PcbSupplyHandler(
            supplyMotion,
            io,
            supplySettings,
            new PcbBufferSettings { SupplyBoundary1 = 40, SupplyBoundary2 = 60 });
        var buffer = Buffer(supplyHandler, placementHandler, supplySettings, placementSettings);
        var supply = new PcbSupplier(supplyHandler, buffer);
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
        using var firstStop = new CancellationTokenSource();
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            var nowAtPcb1 = IsAt(
                x,
                y,
                z,
                recipe.Pcb1PickPosition.X,
                supplySettings.CarrierY,
                recipe.Pcb1PickPosition.Z);
            if (nowAtPcb1 && !atPcb1)
                pcb1Visits++;
            atPcb1 = nowAtPcb1;
            pcb1Visited |= nowAtPcb1;
            if (stopOnPcbDetection && nowAtPcb1 && !firstStop.IsCancellationRequested)
            {
                io.SetInput(InputIo.PcbSupplyPcbDetected, true);
                firstStop.Cancel();
            }
            if (pcb1Visited && !pcb2Visited && x > 15 && !firstStop.IsCancellationRequested)
                firstStop.Cancel();
            pcb2Visited |= IsAt(
                x,
                y,
                z,
                recipe.Pcb2PickPosition.X,
                supplySettings.CarrierY,
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
            await placementMotion.MoveToAsync(50, 10, 8);
        io.SetInput(InputIo.AutoMode, false); // Production SMEMA uses the physical inputs.
        using var cancellation = new CancellationTokenSource();
        var run = supply.RunAsync(recipe, firstStop.Token);
        await WaitForOutputAsync(io, OutputIo.PcbSupplyReadyToFront1, true);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(firstStop.IsCancellationRequested);
        Assert.True(io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
        // The detected PCB was removed during manual recovery; the upstream carrier stays.
        if (stopOnPcbDetection)
            io.SetInput(InputIo.PcbSupplyPcbDetected, false);
        if (replaceDuringStop)
        {
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
        }
        run = supply.RunAsync(recipe, cancellation.Token);

        var checkedBoth = await WaitUntilAsync(
            () => pcb1Visited && pcb2Visited && !io.GetOutput(OutputIo.PcbSupplyReadyToFront1),
            TimeSpan.FromSeconds(5));
        var atRotationZ = supplyMotion.IsAtHorizontalZ;
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
        Assert.Equal(replaceDuringStop ? 2 : 1, pcb1Visits);
        Assert.True(atRotationZ);
        Assert.False(readyDroppedBeforeClear);
        Assert.True(nextCarrierAccepted);
        Assert.Equal(secondPcbPresent, io.GetInput(InputIo.PcbSupplyPcbDetected));
        Assert.Equal(secondPcbPresent, io.GetOutput(OutputIo.PcbSupplyReadyToFront1));

        if (!secondPcbPresent)
        {
            // The next carrier can arrive while automatic operation is stopped.
            var visitsBeforeNextCarrier = pcb1Visits;
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            using var nextStop = new CancellationTokenSource();
            var nextRun = supply.RunAsync(recipe, nextStop.Token);
            var restartedAtFirstSlot = await WaitUntilAsync(
                () => pcb1Visits == visitsBeforeNextCarrier + 1, TimeSpan.FromSeconds(2));
            nextStop.Cancel();
            await nextRun;
            Assert.True(restartedAtFirstSlot);
        }
    }

    [Fact]
    public async Task SupplyResumesInterruptedHandoffEntryAtTransportHeight()
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 100, ZSpeed = 20 },
            RotationZ = 3,
            BufferHandoffPosition = new() { X = 50, Y = 10 },
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
            BufferHandoffPosition = Position(50, 10, 8),
        };
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = new VirtualMotionService(
            supplySettings.Motion, operations, horizontalZ: () => supplySettings.RotationZ);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations, horizontalZ: () => placementSettings.BufferHandoffPosition.Z);
        var placementHandler = new PcbPlacementHandler(placementMotion, io, placementSettings);
        var supplyHandler = new PcbSupplyHandler(
            supplyMotion,
            io,
            supplySettings,
            new PcbBufferSettings { SupplyBoundary1 = 40, SupplyBoundary2 = 60 });
        var buffer = Buffer(supplyHandler, placementHandler, supplySettings, placementSettings);
        var supply = new PcbSupplier(supplyHandler, buffer);

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);

        await placementHandler.SetIpmLiftDownAsync(true);
        await placementMotion.MoveToAsync(50, 10, 8);
        Assert.True(buffer.CanEnterSupply());

        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interrupted = false;
        var enteredUnrotated = false;
        var changedHeightInside = false;
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            enteredUnrotated |= x >= 40 && !io.GetInput(InputIo.PcbSupplyRotated);
            changedHeightInside |= x >= 40 && Math.Abs(z - supplySettings.RotationZ) > MotionService.PositionToleranceMillimeters;
            if (!interrupted && x >= 45)
            {
                interrupted = true;
                firstStop.Cancel();
            }
        };
        await supply.RunAsync(new PcbSupplyRecipe(), firstStop.Token);
        Assert.True(interrupted);
        Assert.True(buffer.IsSupplyInside());
        Assert.False(buffer.IsSupplyAtHandoff());
        var stoppedPosition = supplyMotion.GetPosition();
        Assert.InRange(stoppedPosition.Y, 0.1, supplySettings.BufferHandoffPosition.Y - 0.1);
        Assert.False(supplyMotion.IsMoving);
        await Assert.ThrowsAsync<MotionInterlockException>(() => supplyHandler.SetRotatedAsync(false));
        Assert.Equal(stoppedPosition, supplyMotion.GetPosition());
        Assert.True(io.GetOutput(OutputIo.PcbSupplyRotate));

        using var resumed = new CancellationTokenSource();
        var run = supply.RunAsync(new PcbSupplyRecipe(), resumed.Token);
        var reachedHandoff = await WaitUntilAsync(() => buffer.IsSupplyAtHandoff(), TimeSpan.FromSeconds(5));
        resumed.Cancel();
        await run;

        Assert.True(reachedHandoff);
        Assert.False(enteredUnrotated);
        Assert.False(changedHeightInside);
        Assert.False(buffer.HasConflict());
        Assert.Equal((50, 10, supplySettings.RotationZ), supplyMotion.GetPosition());
    }

    private static BufferStage Buffer(
        PcbSupplyHandler supplyHandler,
        PcbPlacementHandler placementHandler,
        PcbSupplySettings supplySettings,
        PcbPlacementHandlerSettings placementSettings)
    {
        return new(
            new PcbBufferSettings
            {
                SupplyBoundary1 = 40,
                SupplyBoundary2 = 60,
                PlacementBoundary1 = Position(40, 0, 0),
                PlacementBoundary2 = Position(60, 15, 0),
            },
            supplyHandler,
            placementHandler,
            supplyHandler.Motion,
            placementHandler.Motion,
            supplySettings.BufferHandoffPosition,
            placementSettings.BufferHandoffPosition,
            () => supplySettings.RotationZ);
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
