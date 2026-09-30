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
using static IBTM.Virtual.Tests.VirtualTestSupport;

namespace IBTM.Virtual.Tests;

public sealed class PcbTransferTests
{
    [Fact]
    public async Task SupplyPickupTeachingStopsWhenRotationFeedbackIsLostDuringDescent()
    {
        var settings = new PcbSupplySettings { Motion = FastMotion(), TravelZ = 3 };
        settings.Motion.ZSpeed = 50;
        var io = new VirtualIoService(Outputs(new PcbSupplyHardwareSettings()), new MachineOptions());
        using var motion = new VirtualMotionService(settings.Motion, new());
        var supplier = CreateSupplier(motion, io, settings);
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 2_000);
        await supplier.SetRotatedAsync(true, default);
        var point = CreateTeachingPoint(
            new(TeachingTarget.SupplyPcb1Pick, MotionGroup.PcbSupply, TeachMode.Full),
            new() { PcbSupply = settings }, new());
        point.Teach(10, 20, 8);
        var rotationLost = false;
        motion.PositionChanged += (x, y, z) =>
        {
            if (!rotationLost && z > settings.TravelZ + 0.1)
            {
                rotationLost = true;
                io.SetInputs((InputIo.PcbSupplyRotated, false), (InputIo.PcbSupplyUnrotated, true));
            }
        };

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MotionInterlockException>(() =>
            supplier.MoveToTeachingPositionAsync(point.Position, point.MovePosition, stop.Token));

        Assert.True(rotationLost);
        Assert.False(motion.IsMoving);
        Assert.True(motion.Position.Z < 8);
    }

    [Fact]
    public async Task SupplyPickupTeachingUsesEachSlotsXyzAndBlocksMissingY()
    {
        var settings = new PcbSupplySettings { Motion = FastMotion(), TravelZ = 3 };
        var recipe = System.Text.Json.JsonSerializer.Deserialize<PcbSupplyRecipe>(
            """{"Pcb1PickPosition":{"X":10,"Z":5},"Pcb2PickPosition":{"X":20,"Z":8}}""")!;
        var io = new VirtualIoService(Outputs(new PcbSupplyHardwareSettings()), new MachineOptions());
        using var motion = new VirtualMotionService(settings.Motion, new());
        var handler = VirtualTestSupport.CreateSupplier(motion, io, settings);
        io.Initialize();
        motion.Initialize();
        await HomeAsync(motion, 2_000);
        await handler.SetRotatedAsync(true, default);
        var picks = new[] { TeachingTarget.SupplyPcb1Pick, TeachingTarget.SupplyPcb2Pick }
            .Select(target => CreateTeachingPoint(new(target, MotionGroup.PcbSupply, TeachMode.Full),
                new() { PcbSupply = settings }, new() { PcbSupply = recipe })).ToArray();
        Assert.All(picks, point => Assert.False(point.Position.HasPosition));
        Assert.All(picks, point => Assert.False(handler.IsMoveToTeachingPositionAllowed(point.Position)));
        Assert.All(picks, point => Assert.Throws<MotionInterlockException>(() => point.MovePosition));
        var before = motion.Position;
        await Assert.ThrowsAsync<MotionInterlockException>(() =>
            handler.MoveToTeachingPositionAsync(picks[0].Position, new AxisPosition()));
        Assert.Equal(before, motion.Position);

        await handler.SetRotatedAsync(false);
        var rotationCommanded = false;
        io.OutputChanged += (output, on) => rotationCommanded |= output == OutputIo.PcbSupplyRotate;
        await Assert.ThrowsAsync<MotionInterlockException>(() => handler.MoveFromHandoffAsync(recipe.Pcb1PickPosition));
        Assert.False(rotationCommanded);
        Assert.Equal(before, motion.Position);
        await handler.SetRotatedAsync(true);

        picks[0].Teach(10, 30, 5);
        picks[1].Teach(20, 45, 8);
        var positions = new[] { recipe.Pcb1PickPosition, recipe.Pcb2PickPosition };
        for (var index = 0; index < picks.Length; index++)
        {
            var point = picks[index];
            Assert.True(handler.IsMoveToTeachingPositionAllowed(point.Position));
            await handler.MoveToTeachingPositionAsync(point.Position, point.MovePosition);
            var target = positions[index];
            Assert.Equal((target.X, target.Y!.Value, target.Z), motion.Position);
        }
    }

    [Fact]
    public async Task SupplyTestAvailableWakesPickupAndNeedsAnOffEdgeForTheNextCarrier()
    {
        var supplyRecipes = new RecipeManager(OpenMachineStore(), new());
        var settings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            TravelZ = 3,
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
            supplyRecipes,
            new());
        var placer = CreatePlacer(supplier, placementMotion, io, placementSettings);
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Y = 20, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Y = 30, Z = 5 },
        };
        supplyRecipes.Current.PcbSupply = recipe;
        io.Initialize();
        motion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(motion, 2_000), HomeAsync(placementMotion, 2_000));
        Assert.False(supplier.TestUpstreamCarrierAvailable);
        Assert.False(supplier.UpstreamCarrierAvailable);
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
            if (z > settings.TravelZ)
                Assert.Equal(PcbSupplyRotationState.Rotated, supplier.Rotation);
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var run = supplier.RunAsync(placer, stop.Token);
        try
        {
            Assert.True(await WaitUntilAsync(
                () => supplier.Phase == PcbSupplyState.WaitingForCarrier,
                TimeSpan.FromSeconds(1)));
            Assert.False(supplier.UpstreamCarrierAvailable);
            Assert.Equal(PcbSupplyRotationState.Rotated, supplier.Rotation);
            supplier.TestUpstreamCarrierAvailable = true;
            Assert.True(await WaitUntilAsync(() => completedCarriers == 1, TimeSpan.FromSeconds(2)));
            Assert.Equal((20, recipe.Pcb2PickPosition.Y!.Value, settings.TravelZ), motion.Position);
            Assert.False(io.GetInput(InputIo.PcbSupplyAvailableFromFront1));
            Assert.True(supplier.TestUpstreamCarrierAvailable);

            // In teaching, TEST alone reports departure even if the real input stays on.
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            supplier.TestUpstreamCarrierAvailable = false;
            Assert.False(supplier.UpstreamCarrierAvailable);

            supplier.TestUpstreamCarrierAvailable = true;
            Assert.True(await WaitUntilAsync(() => completedCarriers == 2, TimeSpan.FromSeconds(2)));
            Assert.Equal((20, recipe.Pcb2PickPosition.Y!.Value, settings.TravelZ), motion.Position);
            Assert.True(io.GetInput(InputIo.PcbSupplyAvailableFromFront1));
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(1));
        }
        Assert.True(supplier.TestUpstreamCarrierAvailable);
        Assert.False(readyWentOn);
        var newHandler = VirtualTestSupport.CreateSupplier(motion, io, settings);
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
    public async Task PlacementWaitsForSupplyBeforeApproachingAndReceivesWithCylinderUp(bool supplyFirst)
    {
        var supplyRecipes = new RecipeManager(OpenMachineStore(), new());
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
        using var supplyMotion = new VirtualMotionService(supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations);

        var supplier = new PcbSupplier(supplyMotion, new MotionStatus(supplyMotion),
            io,
            supplySettings,
            supplyRecipes,
            new());
        var placer = CreatePlacer(supplier, placementMotion, io, placementSettings);
        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        await placer.MoveAxisAsync(MotionAxis.Z, placementSettings.HandoffPosition.Z);
        Assert.True(placer.IsAtHorizontalZ);
        await placer.SetLiftDownAsync(true);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (!supplyFirst)
        {
            var waitingPosition = placementMotion.Position;
            Assert.False(await placer.ExecuteStepAsync(
                placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
            Assert.Equal(waitingPosition, placementMotion.Position);
            Assert.Equal(PcbPlacementState.MovingToHandoff, placer.Phase);
            // Supply reaches XYZ with the pickup orientation; rotation feedback arrives later.
            await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, true);
            io.AutoResponseEnabled = false;
            var preparation = supplier.PrepareHandoffAsync(timeout.Token);
            try
            {
                Assert.True(await WaitUntilAsync(
                    () => !io.GetOutput(OutputIo.PcbSupplyRotate), TimeSpan.FromSeconds(2)));
                Assert.True(MotionServiceBase.IsHoldingPosition(supplyMotion, supplySettings.HandoffPosition));
                Assert.Equal(PcbSupplyRotationState.Rotated, supplier.Rotation);
                Assert.False(preparation.IsCompleted);
                Assert.False(await placer.ExecuteStepAsync(
                    placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
                Assert.Equal(waitingPosition, placementMotion.Position);
                io.SetInputs((InputIo.PcbSupplyRotated, false), (InputIo.PcbSupplyUnrotated, true));
                await preparation;
            }
            finally
            {
                io.AutoResponseEnabled = true;
                await preparation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
        }
        else
        {
            await supplier.PrepareHandoffAsync(timeout.Token);
        }
        Assert.Equal(PcbSupplyRotationState.Unrotated, supplier.Rotation);
        Assert.Equal(PcbSupplyHandoff.Holding, supplier.Handoff);
        Assert.Equal(PcbPlacementState.MovingToHandoff, placer.Phase);
        var movedWithCylinderDown = false;
        placementMotion.MovingChanged += moving =>
        {
            movedWithCylinderDown |= moving && placer.Lift != StationCylinderState.Up;
        };
        for (var step = 0; step < 8 && !MotionServiceBase.IsAt(placer.Motion.Feedback, placementSettings.HandoffPosition); step++)
        {
            await placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token)!;
        }

        Assert.True(MotionServiceBase.IsAt(placer.Motion.Feedback, placementSettings.HandoffPosition));
        Assert.Equal((50, 10, 8), placementMotion.Position);
        Assert.Equal(StationCylinderState.Up, placer.Lift);
        Assert.False(movedWithCylinderDown);
        Assert.Equal(PcbPlacementState.ReceivingPcb, placer.Phase);
        Assert.Equal(PcbPlacementHandoff.Unavailable, placer.Handoff);
        var receiveZ = placementSettings.ReceiveZ;
        placementSettings.ReceiveZ = null;
        await Assert.ThrowsAsync<MotionInterlockException>(() => placer.ExecuteStepAsync(
            placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token));
        Assert.Equal((50, 10, 8), placementMotion.Position);
        Assert.Equal(StationCylinderState.Up, placer.Lift);
        placementSettings.ReceiveZ = receiveZ;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementVacuumEjector)
                io.SetInput(InputIo.PcbPlacementVacuumDetected, on);
        };
        if (supplyFirst)
        {
            // An interrupted receipt at Receive Z finishes without raising away from Supply.
            await placer.PrepareReceiptAsync(timeout.Token);
        }
        var loweredDuringReceipt = false;
        io.OutputChanged += (output, on) => loweredDuringReceipt |= output == OutputIo.PcbPlacementHandlerDown && on;
        Assert.Equal(PcbPlacementState.ReceivingPcb, placer.Phase);
        var receipt = placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token);
        Assert.True(await WaitUntilAsync(() => MotionServiceBase.IsAt(placer.Motion.Feedback, Position(50, 10, 12)), TimeSpan.FromSeconds(2)));
        Assert.False(receipt.IsCompleted);
        Assert.Equal(StationCylinderState.Up, placer.Lift);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        Assert.True(await receipt);
        Assert.True(MotionServiceBase.IsAt(placer.Motion.Feedback, Position(50, 10, 12)) && placer.PcbSecured);
        Assert.False(MotionServiceBase.IsAt(placer.Motion.Feedback, placementSettings.HandoffPosition));
        Assert.False(loweredDuringReceipt);
        Assert.False(movedWithCylinderDown);
        Assert.Equal((50, 10, placementSettings.ReceiveZ!.Value), placementMotion.Position);
        Assert.Equal(PcbPlacementHandoff.Holding, placer.Handoff);
        io.SetInputs((InputIo.PcbPlacementIpmDown, false), (InputIo.PcbPlacementIpmUp, false));
        Assert.True(placer.PcbSecured);
        Assert.Equal(PcbPlacementHandoff.Unavailable, placer.Handoff);
        io.SetInput(InputIo.PcbPlacementIpmDown, true);
        Assert.Equal(PcbPlacementHandoff.Holding, placer.Handoff);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectHandoffWaitsForPlacementYDepartureBeforeSupply(bool stopDuringDeparture)
    {
        var supplyRecipes = new RecipeManager(OpenMachineStore(), new());
        var recipe = new PcbPlacementRecipe
        {
            HeatSink1PcbPlacementPosition = Position(70, 20, 10),
        };
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            TravelZ = 3,
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
            supplyRecipes,
            units);
        var work = ConveyorStation.CreatePcbPlacement(io);
        var placer = CreatePlacer(supplier, placementMotion, io, placementSettings, recipe, work, units);
        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyRotate, false);
        Assert.False(MotionServiceBase.IsAt(supplier.Motion.Feedback, supplySettings.HandoffPosition) && supplier.PcbSecured);
        await supplier.PrepareHandoffAsync(CancellationToken.None);
        Assert.True(MotionServiceBase.IsAt(supplier.Motion.Feedback, supplySettings.HandoffPosition) && supplier.PcbSecured);
        await placementMotion.MoveToXYAsync(50, 10, placementSettings.Motion.HorizontalSpeed);
        await placementMotion.MoveAxisAsync(MotionAxis.Z, placementSettings.ReceiveZ!.Value, placementSettings.Motion.ZSpeed);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
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
            Assert.False(MotionServiceBase.IsAt(placer.Motion.Feedback, Position(50, 10, 12)) && placer.PcbSecured);
            Assert.Equal(PcbSupplyState.HandingOff, supplier.Phase);
            Assert.NotEqual(PcbPlacementState.WaitingForSupplyRelease, placer.Phase);
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
            await supplier.RunAsync(placer, stop.Token);
            supplier.Trace -= OnTrace;
            Assert.True(waited);
            Assert.True(io.GetOutput(OutputIo.PcbSupplyGripperClosed));
            Assert.True(io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
        }

        foreach (var signal in receipt)
            io.SetInput(signal, true);
        await placer.PrepareReceiptAsync();
        Assert.True(MotionServiceBase.IsAt(placer.Motion.Feedback, Position(50, 10, 12)) && placer.PcbSecured);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.Phase);
        units.PcbPlacement = false;
        Assert.Equal(PcbPlacementState.Disabled, placer.GetNextStep(HeatSinkSlot.HeatSink1));
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.Phase);
        Assert.Equal(PcbSupplyState.HandingOff, supplier.Phase);
        units.PcbPlacement = true;
        units.PcbSupply = false;
        Assert.Equal(PcbSupplyState.Disabled, supplier.GetNextStep(placer));
        Assert.Equal(PcbSupplyState.HandingOff, supplier.Phase);
        Assert.False(await placer.ExecuteStepAsync(
            placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None));
        units.PcbSupply = true;
        using var released = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var order = new System.Collections.Generic.List<OutputIo>();
        io.OutputChanged += (output, on) =>
        {
            if (!on && output is OutputIo.PcbSupplyIpmFixerForward or OutputIo.PcbSupplyGripperClosed)
            {
                Assert.True(MotionServiceBase.IsAt(placer.Motion.Feedback, Position(50, 10, 12)) && placer.PcbSecured);
                Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.Phase);
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
        await supplier.RunAsync(placer, released.Token);
        supplier.Trace -= StopBeforePlacementLifts;
        Assert.Equal(new[] { OutputIo.PcbSupplyIpmFixerForward, OutputIo.PcbSupplyGripperClosed }, order);
        Assert.True(supplier.PcbReleased);
        Assert.Equal((50, 10, supplySettings.HandoffPosition.Z), supplyMotion.Position);
        Assert.Equal(PcbSupplyState.WaitingForPlacementClear, supplier.Phase);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.Phase);
        io.SetInput(InputIo.PcbSupplyGripperClosed, true); // Both endpoints ON is not released.
        Assert.False(supplier.PcbReleased);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.Phase);
        io.SetInput(InputIo.PcbSupplyGripperClosed, false);
        Assert.Equal(StationCylinderState.Up, placer.Lift);
        Assert.False(placer.IsAtHorizontalZ);
        io.SetInput(InputIo.PcbPlacementHandlerDown, true); // Both endpoints ON is not confirmed Up.
        Assert.NotEqual(StationCylinderState.Up, placer.Lift);
        Assert.Equal(PcbPlacementState.WaitingForSupplyRelease, placer.Phase);
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
                Assert.Equal(PcbPlacementState.PreparingPlacement, placer.Phase);
                Assert.True(MotionServiceBase.IsAt(supplier.Motion.Feedback, supplySettings.HandoffPosition));
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
                Assert.True(placer.IsAtHorizontalZ);
                Assert.Equal(StationCylinderState.Up, placer.Lift);
                Assert.Equal((50, recipe.HeatSink1PcbPlacementPosition.Y, placementSettings.HandoffPosition.Z),
                    placementMotion.Position);
                returnAllowed = true;
                returnCheck.Cancel(); // Stop before XY so the independent Placement departure is checked below.
            }
        }
        supplier.Trace += ObserveReturn;
        var returning = supplier.RunAsync(placer, returnCheck.Token);
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
                Assert.True(MotionServiceBase.IsAt(supplier.Motion.Feedback, supplySettings.HandoffPosition));
                Assert.Equal(PcbPlacementState.PreparingPlacement, placer.Phase);
                Assert.NotEqual(PcbPlacementHandoff.Clear, placer.Handoff);
                return;
            }
            // Run owns the peer-change subscription needed to finish the handoff wait.
            placer.StepChanged += () =>
            {
                if (placer.Step is PcbPlacementState.PlacingPcb)
                    departureStop.Cancel();
            };
            await placer.RunAsync(departureStop.Token);
            await returning.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(returnAllowed);
        }
        finally
        {
            returnCheck.Cancel();
            await returning;
            supplier.Trace -= ObserveReturn;
        }
        Assert.True(supplier.PcbReleased && placer.Lift == StationCylinderState.Up);
        Assert.True(departedInY);
        Assert.True(placer.IsAtHorizontalZ);
        Assert.Equal(PcbPlacementState.PlacingPcb, placer.GetNextStep(HeatSinkSlot.HeatSink1));
        Assert.True(MotionServiceBase.IsAt(supplier.Motion.Feedback, supplySettings.HandoffPosition));
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementVacuumEjector)
                io.SetInput(InputIo.PcbPlacementVacuumDetected, on);
        };
        io.SetOutput(OutputIo.PcbPlacementVacuumEjector, true);
        await placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, CancellationToken.None);
        Assert.Equal((70, 20, 8), placementMotion.Position);
        Assert.True(MotionServiceBase.IsAt(supplier.Motion.Feedback, supplySettings.HandoffPosition));

        var exitRecipe = new PcbSupplyRecipe { Pcb1PickPosition = new() { X = 15, Y = 30, Z = 5 } };
        supplyRecipes.Current.PcbSupply = exitRecipe;
        using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var diagonalExit = false;
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            if (supplyMotion.IsMovingHorizontal)
                Assert.Equal(supplySettings.TravelZ, z);
            Assert.Equal(StationCylinderState.Up, placer.Lift);
            diagonalExit |= x is > 15 and < 50 && y is > 10 and < 30;
        };
        supplier.Trace += message =>
        {
            if (message.StartsWith("PcbSupplier: WaitingForCarrier ", StringComparison.Ordinal))
                exited.Cancel();
        };
        await supplier.RunAsync(placer, exited.Token);
        Assert.True(diagonalExit);
        Assert.Equal((15, 30, supplySettings.TravelZ), supplyMotion.Position);
        Assert.Equal(PcbSupplyRotationState.Rotated, supplier.Rotation);

        supplier.Motion.InvalidateFeedback(new System.IO.IOException("Supply feedback disconnected."));
        Assert.False(supplier.Motion.IsFeedbackAvailable);
        Assert.Null(supplier.Motion.Position.X);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplyKeepsGripperClosedWhenPlacementLosesVacuumDuringRelease(bool repeat)
    {
        var supplyRecipes = new RecipeManager(OpenMachineStore(), new());
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
        using var supplyMotion = new VirtualMotionService(supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations);

        var supplier = new PcbSupplier(supplyMotion, new MotionStatus(supplyMotion),
            io,
            supplySettings,
            supplyRecipes,
            new());
        var placer = CreatePlacer(supplier, placementMotion, io, placementSettings);
        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        await supplier.PrepareHandoffAsync(CancellationToken.None);
        await placementMotion.MoveToXYAsync(50, 10, placementSettings.Motion.HorizontalSpeed);
        await placementMotion.MoveAxisAsync(MotionAxis.Z, placementSettings.ReceiveZ!.Value, placementSettings.Motion.ZSpeed);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        io.SetInput(InputIo.PcbPlacementVacuumDetected, true);
        await placer.PrepareReceiptAsync();
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, !repeat);
        Assert.Equal(PcbPlacementHandoff.Holding, placer.Handoff);
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyIpmFixerForward && !on)
                io.SetInput(InputIo.PcbPlacementVacuumDetected, false);
        };

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => supplier.RunAsync(placer, stop.Token, repeat));
        Assert.Contains("before supply opened its gripper", error.Message);
        Assert.True(io.GetOutput(OutputIo.PcbSupplyGripperClosed));
        Assert.True(MotionServiceBase.IsAt(supplier.Motion.Feedback, supplySettings.HandoffPosition));
        Assert.False(io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
    }

    [Trait("Category", "MachineFlow")]
    [Fact]
    public async Task PlacementReselectsAfterStopAndCompletesSupplyHandoffs()
    {
        var supplyRecipes = new RecipeManager(OpenMachineStore(), new());
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            TravelZ = 0,
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
        supplyRecipes.Current.PcbSupply = supplyRecipe;
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
        using var supplyMotion = new VirtualMotionService(supplySettings.Motion, operations);
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
            supplyRecipes,
            new());
        var work = ConveyorStation.CreatePcbPlacement(io);
        var placement = CreatePlacer(supply, placementMotion, io, placementSettings, placementRecipe, work);
        var returnedPositions = new System.Collections.Generic.List<(double X, double Y, double Z)>();
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbPlacementHandlerRotate)
                Assert.False(on);
            if (output == OutputIo.PcbSupplyRotate)
                Assert.True(MotionServiceBase.IsHoldingPosition(supplyMotion, supplySettings.HandoffPosition));
        };
        supply.StepChanged += () =>
        {
            if (supply.Step is PcbSupplyState.WaitingForCarrier or PcbSupplyState.WaitingForCarrierExit)
                returnedPositions.Add(supplyMotion.Position);
        };
        var handoffEntries = 0;
        var enteredHandoffPrepared = true;
        var movedWithLoweredCylinder = false;
        var carriedWithIpmRaised = false;
        var wasAtHandoff = false;
        placementMotion.PositionChanged += (x, y, _) =>
        {
            movedWithLoweredCylinder |= placementMotion.IsMovingHorizontal
                && placement.Lift != StationCylinderState.Up;
            carriedWithIpmRaised |= placementMotion.IsMovingHorizontal
                && placement.Pcb == PlacementPcbState.Secured
                && placement.IpmLift != StationCylinderState.Down;
            var inside = x is >= 40 and <= 60 && y is >= 0 and <= 15;
            if (inside && !wasAtHandoff)
            {
                handoffEntries++;
                enteredHandoffPrepared &= placement.IpmLift == StationCylinderState.Down;
            }

            wasAtHandoff = inside;
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
        await supply.MoveToTeachingPositionAsync(
            new(TeachingTarget.SupplyHandoff, MotionGroup.PcbSupply, TeachMode.Full), supplySettings.HandoffPosition);
        await supply.SetRotatedAsync(true);
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, false);
        VirtualTestSupport.SetCarrier(io, InputIo.PcbPlacementHeatSink1Present, true);

        Assert.True(work.CarrierSeated);
        using var supplyCancellation = new CancellationTokenSource();
        var supplyRun = supply.RunAsync(placement, supplyCancellation.Token);
        using var firstStop = new CancellationTokenSource();
        var firstRun = placement.RunAsync(firstStop.Token);
        Assert.True(
            await WaitUntilAsync(
                () => placement.Pcb == PlacementPcbState.Secured,
                TimeSpan.FromSeconds(10)),
            $"Supply={supply.Phase}, Placement={placement.Phase}, supply={supplyRun.Exception}, placement={firstRun.Exception}");
        Assert.Equal(HeatSinkSlot.HeatSink1, placement.TargetHeatSink);
        firstStop.Cancel();
        await firstRun;

        Assert.False(work.Completed);
        Assert.Empty(work.Assemblies);
        Assert.Equal(PlacementPcbState.Secured, placement.Pcb);

        io.SetInputs((InputIo.PcbPlacementHeatSink1Present, false), (InputIo.PcbPlacementHeatSink2Present, true));
        using var resumed = new CancellationTokenSource();
        var resumedRun = placement.RunAsync(resumed.Token);
        Assert.Equal(HeatSinkSlot.HeatSink2, placement.TargetHeatSink);
        var completed = await WaitUntilAsync(() => work.Completed, TimeSpan.FromSeconds(10));
        var prefetched = completed
            && await WaitUntilAsync(
                () => placement.Pcb == PlacementPcbState.Secured
                    && placement.Phase == PcbPlacementState.WaitingForCarrier,
                TimeSpan.FromSeconds(10));
        if (prefetched)
            Assert.True(await WaitUntilAsync(() => returnedPositions.Count >= 3, TimeSpan.FromSeconds(3)),
                $"Supply return positions: {string.Join(", ", returnedPositions)}");
        var stoppedState = placement.Phase;
        var stoppedTarget = placement.TargetHeatSink;

        resumed.Cancel();
        supplyCancellation.Cancel();
        await Task.WhenAll(resumedRun, supplyRun);

        Assert.True(completed, $"Placement stopped at {stoppedState}, target={stoppedTarget}, position={placementMotion.Position}.");
        Assert.True(prefetched);
        Assert.True(returnedPositions.Count >= 3);
        Assert.Equal((20, 30, supplySettings.TravelZ), returnedPositions[1]);
        Assert.Equal((10, 30, supplySettings.TravelZ), returnedPositions[2]);
        Assert.False(movedWithLoweredCylinder);
        Assert.False(carriedWithIpmRaised);
        Assert.False(io.GetInput(InputIo.PcbPlacementHeatSink1Present));
        Assert.True(io.GetInput(InputIo.PcbPlacementHeatSink2Present));
        Assert.True(handoffEntries >= 2);
        Assert.True(enteredHandoffPrepared);
        var assembly = Assert.Single(work.Assemblies);
        Assert.Equal(HeatSinkSlot.HeatSink2, assembly.HeatSink);
        Assert.Equal(StationCylinderState.Down, placement.IpmLift);
        Assert.Equal(StationCylinderState.Up, placement.Lift);
        Assert.True(placement.IsAtHorizontalZ);
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
        var supplyRecipes = new RecipeManager(OpenMachineStore(), new());
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
        supplyRecipes.Current.PcbSupply = recipe;
        var io = new VirtualIoService(
            Outputs(new PcbSupplyHardwareSettings(), new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = new VirtualMotionService(supplySettings.Motion, operations);
        using var placementMotion = new VirtualMotionService(
            placementSettings.Motion, operations);

        var supply = new PcbSupplier(supplyMotion, new MotionStatus(supplyMotion),
            io,
            supplySettings,
            supplyRecipes,
            new());
        var placement = CreatePlacer(supply, placementMotion, io, placementSettings);
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
                    || !MotionServiceBase.IsAtZ(supply.Motion.Feedback, supplySettings.TravelZ)
                    || supply.Pcb == PcbSupplyPcbState.Detected;
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
        var run = supply.RunAsync(placement, cancellation.Token);
        await WaitForOutputAsync(io, OutputIo.PcbSupplyReadyToFront1, true);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);

        var checkedBoth = await WaitUntilAsync(
            () => pcb1Visited && pcb2Visited && !io.GetOutput(OutputIo.PcbSupplyReadyToFront1),
            TimeSpan.FromSeconds(5));
        var atTravelZ = MotionServiceBase.IsAtZ(supply.Motion.Feedback, supplySettings.TravelZ);
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
        Assert.True(atTravelZ);
        Assert.False(readyDroppedBeforeClear);
        Assert.True(nextCarrierAccepted);
        Assert.Equal(secondPcbPresent, io.GetInput(InputIo.PcbSupplyPcbDetected));
        Assert.Equal(secondPcbPresent, io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
    }

    [Fact]
    public async Task SupplyCancelsHandoffApproachAtTravelZBeforeRotating()
    {
        var supplyRecipes = new RecipeManager(OpenMachineStore(), new());
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 100, ZSpeed = 20 },
            TravelZ = 3,
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
            supplyRecipes,
            new());
        var placement = CreatePlacer(supply, placementMotion, io, placementSettings);

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        io.SetInputs((InputIo.PcbSupplyRotated, true), (InputIo.PcbSupplyUnrotated, false));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, false);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, false);
        io.SetInput(InputIo.AutoMode, false);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);

        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbPlacementIpmDown, true);
        await placementMotion.MoveToXYAsync(50, 10, placementSettings.Motion.HorizontalSpeed);
        await placementMotion.MoveAxisAsync(MotionAxis.Z, 8, placementSettings.Motion.ZSpeed);

        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interrupted = false;
        var rotationChangedDuringTravel = false;
        var travelledAtWrongZ = false;
        var unrotatedAtHandoff = false;
        io.OutputChanged += (output, on) =>
        {
            if (output == OutputIo.PcbSupplyRotate && !on)
                unrotatedAtHandoff = MotionServiceBase.IsHoldingPosition(supply.Motion.Feedback, supplySettings.HandoffPosition);
        };
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            rotationChangedDuringTravel |= supply.Rotation != PcbSupplyRotationState.Rotated;
            travelledAtWrongZ |= supplyMotion.IsMovingHorizontal
                && Math.Abs(z - supplySettings.TravelZ) > MotionServiceBase.PositionToleranceMillimeters;
            if (!interrupted && x >= 45)
            {
                interrupted = true;
                firstStop.Cancel();
            }
        };
        var recipe = new PcbSupplyRecipe { Pcb1PickPosition = new() { X = 10, Y = 0, Z = 5 } };
        supplyRecipes.Current.PcbSupply = recipe;
        await supply.RunAsync(placement, firstStop.Token);
        Assert.True(interrupted);
        Assert.False(MotionServiceBase.IsAt(supply.Motion.Feedback, supplySettings.HandoffPosition));
        var stoppedPosition = supplyMotion.Position;
        Assert.InRange(stoppedPosition.Y, 0.1, supplySettings.HandoffPosition.Y - 0.1);
        Assert.False(supplyMotion.IsMoving);
        Assert.Equal(stoppedPosition, supplyMotion.Position);
        Assert.Equal(PcbSupplyRotationState.Rotated, supply.Rotation);

        Assert.False(unrotatedAtHandoff);
        Assert.False(rotationChangedDuringTravel);
        Assert.False(travelledAtWrongZ);
        Assert.Equal(supplySettings.TravelZ, stoppedPosition.Z);
        Assert.True(supply.PcbSecured);
    }

    private static PcbPlacer CreatePlacer(
        PcbSupplier supply,
        IXyMotion motion,
        IIoService io,
        PcbPlacementHandlerSettings settings,
        PcbPlacementRecipe? recipe = null,
        ConveyorStation? work = null,
        UnitSettings? units = null)
    {
        var recipes = new RecipeManager(OpenMachineStore(), new());
        recipes.Current.PcbPlacement = recipe ?? new();
        return new(motion, new(motion), io, settings, supply, work ?? ConveyorStation.CreatePcbPlacement(io), recipes, units ?? new());
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
        return Math.Abs(x - targetX) <= MotionServiceBase.PositionToleranceMillimeters
            && Math.Abs(y - targetY) <= MotionServiceBase.PositionToleranceMillimeters
            && Math.Abs(z - targetZ) <= MotionServiceBase.PositionToleranceMillimeters;
    }
}
