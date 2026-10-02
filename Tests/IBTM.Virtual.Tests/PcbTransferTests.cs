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
        io.SetInputs(
            (InputIo.PcbPlacementHeatSink1Present, true),
            (InputIo.PcbPlacementBackupPlateUp, true),
            (InputIo.PcbPlacementBackupPlateDown, false));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyGripperClosed, true);
        await ((IIoService)io).SetOutputAndWaitAsync(OutputIo.PcbSupplyIpmFixerForward, true);
        await placer.MoveAxisAsync(MotionAxis.Z, placementSettings.HandoffPosition.Z);
        Assert.Equal(placementSettings.HandoffPosition.Z, placementMotion.Position.Z);
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
                Assert.True(VirtualTestSupport.IsAt(supplyMotion, supplySettings.HandoffPosition));
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
        var loweredDuringReceipt = false;
        io.OutputChanged += (output, on) => loweredDuringReceipt |= output == OutputIo.PcbPlacementHandlerDown && on;
        Assert.Equal(PcbPlacementState.ReceivingPcb, placer.Phase);
        var receipt = placer.ExecuteStepAsync(placer.GetNextStep(HeatSinkSlot.HeatSink1), HeatSinkSlot.HeatSink1, timeout.Token);
        Assert.True(await WaitUntilAsync(() => VirtualTestSupport.IsAt(placer.Motion.Feedback, Position(50, 10, 12)), TimeSpan.FromSeconds(2)));
        Assert.False(receipt.IsCompleted);
        Assert.Equal(StationCylinderState.Up, placer.Lift);
        io.SetInput(InputIo.PcbPlacementPcbDetected, true);
        Assert.True(await receipt);
        Assert.True(VirtualTestSupport.IsAt(placer.Motion.Feedback, Position(50, 10, 12)) && placer.PcbSecured);
        Assert.False(VirtualTestSupport.IsAt(placer.Motion.Feedback, placementSettings.HandoffPosition));
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
                    || Math.Abs(supply.Motion.Feedback.Position.Z - supplySettings.TravelZ) > VirtualTestSupport.PositionToleranceMillimeters
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
        var atTravelZ = Math.Abs(supply.Motion.Feedback.Position.Z - supplySettings.TravelZ) <= VirtualTestSupport.PositionToleranceMillimeters;
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
        supply.StepChanged += () =>
        {
            if (supply.Step is PcbSupplyState.PickingPcb)
                io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        };
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
                unrotatedAtHandoff = VirtualTestSupport.IsAt(supply.Motion.Feedback, supplySettings.HandoffPosition);
        };
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            rotationChangedDuringTravel |= supply.Rotation != PcbSupplyRotationState.Rotated;
            travelledAtWrongZ |= supplyMotion.IsMovingHorizontal
                && Math.Abs(z - supplySettings.TravelZ) > VirtualTestSupport.PositionToleranceMillimeters;
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
        Assert.False(VirtualTestSupport.IsAt(supply.Motion.Feedback, supplySettings.HandoffPosition));
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
        return Math.Abs(x - targetX) <= VirtualTestSupport.PositionToleranceMillimeters
            && Math.Abs(y - targetY) <= VirtualTestSupport.PositionToleranceMillimeters
            && Math.Abs(z - targetZ) <= VirtualTestSupport.PositionToleranceMillimeters;
    }
}
