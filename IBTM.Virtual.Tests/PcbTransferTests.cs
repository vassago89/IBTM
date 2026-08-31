using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IBTM.BoltFastening;
using IBTM.Conveyor;
using IBTM.Core;
using IBTM.Device;
using IBTM.PcbBuffer;
using IBTM.PcbPlacement;
using IBTM.PcbSupply;
using IBTM.Virtual;
using Xunit;

namespace IBTM.Virtual.Tests;

public sealed class PcbTransferTests
{
    [Fact]
    public void PlacementRecoveryKeepsOnlyCompletedHeatSinks()
    {
        var io = new VirtualIoService(
            new Dictionary<OutputIo, OutputHardware>(),
            new MachineOptions());
        var work = new PcbPlacementWork(io);

        io.SetInput(InputIo.PcbPlacementCarrierPresent, true);
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
        work.Assembly(HeatSinkSlot.HeatSink1);
        work.Complete();

        work.PrepareRecovery([HeatSinkSlot.HeatSink2]);

        var assembly = Assert.Single(work.Assemblies);
        Assert.False(work.Completed);
        Assert.Equal(HeatSinkSlot.HeatSink2, assembly.HeatSink);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    public async Task PlacementFillsOnlyPresentHeatSinks(
        bool heatSink1,
        bool heatSink2,
        int expectedPlacements)
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = MotionSettings(),
            RotationZ = 0,
            CarrierY = 30,
            BufferHandoffPosition = Position(50, 10, 8),
            BufferClearZ = 12,
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = MotionSettings(),
            BufferEntryZ = 0,
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
        var virtualIo = new VirtualIoService(
            Outputs(
                new PcbSupplyHardwareSettings(),
                new PcbPlacementHandlerHardwareSettings(),
                new ConveyorHardwareSettings()),
            new MachineOptions());
        IIoService io = virtualIo;
        var machine = new VirtualMachine(virtualIo, []);
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = Motion(
            placementSettings.Motion,
            operations);

        supplyMotion.PositionChanged += (x, y, z) =>
            machine.UpdateSupplyPosition(
                x,
                y,
                z,
                supplySettings.CarrierY,
                (supplyRecipe.Pcb1PickPosition.X,
                    supplyRecipe.Pcb1PickPosition.Z),
                (supplyRecipe.Pcb2PickPosition.X,
                    supplyRecipe.Pcb2PickPosition.Z),
                supplySettings.BufferHandoffPosition);
        placementMotion.PositionChanged += (x, y, z) =>
            machine.UpdatePlacementPosition(
                x,
                y,
                z,
                placementSettings.BufferHandoffPosition);

        var placementHandler = new PcbPlacementHandler(
            placementMotion,
            io,
            placementSettings);
        var buffer = new BufferStage(
            new PcbBufferSettings
            {
                SupplyBoundary1 = 40,
                SupplyBoundary2 = 60,
                PlacementBoundary1 = Position(40, 0, 0),
                PlacementBoundary2 = Position(60, 15, 0),
            },
            io,
            placementHandler,
            supplyMotion,
            placementMotion,
            supplySettings.BufferHandoffPosition,
            placementSettings.BufferHandoffPosition,
            () => placementSettings.BufferEntryZ);
        var supplyHandler = new PcbSupplyHandler(
            supplyMotion,
            io,
            supplySettings);
        var supply = new PcbSupplyProcess(
            supplyHandler,
            buffer);
        var placementWork = new PcbPlacementWork(io);
        var placement = new PcbPlacementProcess(
            buffer,
            placementHandler,
            placementWork);
        var placementCount = 0;
        var pressCount = 0;
        var pcbDetectedDuringPress = true;
        var placementReleased = false;
        io.InputChanged += (input, value) =>
        {
            if (input == InputIo.PcbPlacementVacuumDetected && !value)
            {
                placementReleased = true;
            }
            else if (input == InputIo.PcbPlacementHandlerUp
                     && value
                     && placementReleased)
            {
                placementReleased = false;
                Interlocked.Increment(ref placementCount);
            }
            else if (input == InputIo.PcbPlacementIpmDown
                     && value
                     && placementReleased)
            {
                Interlocked.Increment(ref pressCount);
                pcbDetectedDuringPress &= io.GetInput(
                    InputIo.PcbPlacementPcbDetected);
            }
        };

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await HomeAsync(supplyMotion);
        await HomeAsync(placementMotion);
        virtualIo.SetInput(InputIo.PcbPlacementBackupPlateDown, false);
        await ((IIoService)virtualIo).SetOutputAndWaitAsync(
            OutputIo.PcbPlacementBackupPlateUp,
            true);
        virtualIo.SetInput(InputIo.PcbPlacementCarrierPresent, true);
        virtualIo.SetInput(
            InputIo.PcbPlacementHeatSink1Present,
            heatSink1);
        virtualIo.SetInput(
            InputIo.PcbPlacementHeatSink2Present,
            heatSink2);

        using var cancellation = new CancellationTokenSource();
        var supplyRun = supply.RunAsync(supplyRecipe, cancellation.Token);
        var placementRun = placement.RunAsync(
            placementRecipe,
            cancellation.Token);

        var completed = await WaitUntilAsync(
            () => placementCount == expectedPlacements
                  && placementWork.Completed,
            TimeSpan.FromSeconds(15));

        cancellation.Cancel();
        await Task.WhenAll(supplyRun, placementRun);

        Assert.True(completed);
        Assert.Equal(expectedPlacements, placementCount);
        Assert.Equal(expectedPlacements, pressCount);
        Assert.True(pcbDetectedDuringPress);
        Assert.True(placementWork.Completed);
        Assert.Equal(
            PlacementCylinderState.Up,
            placementHandler.Ipm);
        Assert.Equal(expectedPlacements, placementWork.Assemblies.Count);
        Assert.Equal(
            heatSink1,
            placementWork.Assemblies.Any(
                assembly => assembly.HeatSink == HeatSinkSlot.HeatSink1));
        Assert.Equal(
            heatSink2,
            placementWork.Assemblies.Any(
                assembly => assembly.HeatSink == HeatSinkSlot.HeatSink2));
    }

    [Fact]
    public async Task SupplyChecksBothMissingPcbSlotsInOneSmemaCycle()
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = MotionSettings(),
            CarrierY = 30,
            BufferHandoffPosition = Position(50, 10, 8),
            BufferClearZ = 12,
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = MotionSettings(),
            BufferEntryZ = 0,
            BufferHandoffPosition = Position(50, 10, 8),
        };
        var recipe = new PcbSupplyRecipe
        {
            Pcb1PickPosition = new() { X = 10, Z = 5 },
            Pcb2PickPosition = new() { X = 20, Z = 5 },
        };
        var io = new VirtualIoService(
            Outputs(
                new PcbSupplyHardwareSettings(),
                new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = Motion(
            placementSettings.Motion,
            operations);
        var placementHandler = new PcbPlacementHandler(
            placementMotion,
            io,
            placementSettings);
        var buffer = new BufferStage(
            new PcbBufferSettings
            {
                SupplyBoundary1 = 40,
                SupplyBoundary2 = 60,
                PlacementBoundary1 = Position(40, 0, 0),
                PlacementBoundary2 = Position(60, 15, 0),
            },
            io,
            placementHandler,
            supplyMotion,
            placementMotion,
            supplySettings.BufferHandoffPosition,
            placementSettings.BufferHandoffPosition,
            () => placementSettings.BufferEntryZ);
        var supplyHandler = new PcbSupplyHandler(
            supplyMotion,
            io,
            supplySettings);
        var supply = new PcbSupplyProcess(supplyHandler, buffer);
        var pcb1Visited = false;
        var pcb2Visited = false;
        var readyOnCount = 0;
        var readyOffCount = 0;
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            pcb1Visited |= IsAt(
                x,
                y,
                z,
                recipe.Pcb1PickPosition.X,
                supplySettings.CarrierY,
                recipe.Pcb1PickPosition.Z);
            pcb2Visited |= IsAt(
                x,
                y,
                z,
                recipe.Pcb2PickPosition.X,
                supplySettings.CarrierY,
                recipe.Pcb2PickPosition.Z);
        };
        io.OutputChanged += (output, value) =>
        {
            if (output != OutputIo.PcbSupplyReadyToFront1)
            {
                return;
            }

            if (value)
            {
                Interlocked.Increment(ref readyOnCount);
            }
            else
            {
                Interlocked.Increment(ref readyOffCount);
            }
        };

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(
            HomeAsync(supplyMotion),
            HomeAsync(placementMotion));
        using var cancellation = new CancellationTokenSource();
        var run = supply.RunAsync(recipe, cancellation.Token);
        Assert.True(await WaitUntilAsync(
            () => io.GetOutput(OutputIo.PcbSupplyReadyToFront1),
            TimeSpan.FromSeconds(2)));
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);

        var checkedBoth = await WaitUntilAsync(
            () => pcb1Visited
                  && pcb2Visited
                  && io.GetOutput(OutputIo.PcbSupplyReadyToFront1),
            TimeSpan.FromSeconds(5));
        var readyOffBeforeStop = Volatile.Read(ref readyOffCount);

        cancellation.Cancel();
        await run;

        Assert.True(checkedBoth);
        Assert.False(io.GetInput(InputIo.PcbSupplyPcbDetected));
        Assert.Equal(PcbSupplyRotation.Unrotated, supplyHandler.Rotation);
        Assert.False(io.GetOutput(OutputIo.PcbSupplyNestForward));
        Assert.False(io.GetOutput(OutputIo.PcbSupplyIpmFixerForward));
        Assert.False(io.GetInput(InputIo.PcbBufferPcbPresent));
        Assert.Equal(2, readyOnCount);
        Assert.Equal(1, readyOffBeforeStop);
        Assert.False(io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
    }

    [Fact]
    public async Task PlacementResumesAtEachHandoffAndDropOffCheckpoint()
    {
        var operations = new OperationCancellation();
        var placementMotionSettings = MotionSettings();
        placementMotionSettings.ZSpeed = 100;
        var supplySettings = new PcbSupplySettings
        {
            Motion = MotionSettings(),
            RotationZ = 0,
            CarrierY = 30,
            BufferHandoffPosition = Position(50, 10, 8),
            BufferClearZ = 12,
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = placementMotionSettings,
            BufferEntryZ = 0,
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
        var virtualIo = new VirtualIoService(
            Outputs(
                new PcbSupplyHardwareSettings(),
                new PcbPlacementHandlerHardwareSettings(),
                new ConveyorHardwareSettings()),
            new MachineOptions());
        var machine = new VirtualMachine(virtualIo, []);
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = Motion(
            placementSettings.Motion,
            operations);
        supplyMotion.PositionChanged += (x, y, z) =>
            machine.UpdateSupplyPosition(
                x,
                y,
                z,
                supplySettings.CarrierY,
                (supplyRecipe.Pcb1PickPosition.X,
                    supplyRecipe.Pcb1PickPosition.Z),
                (supplyRecipe.Pcb2PickPosition.X,
                    supplyRecipe.Pcb2PickPosition.Z),
                supplySettings.BufferHandoffPosition);
        placementMotion.PositionChanged += (x, y, z) =>
            machine.UpdatePlacementPosition(
                x,
                y,
                z,
                placementSettings.BufferHandoffPosition);
        var placementHandler = new PcbPlacementHandler(
            placementMotion,
            virtualIo,
            placementSettings);
        var buffer = new BufferStage(
            new PcbBufferSettings
            {
                SupplyBoundary1 = 40,
                SupplyBoundary2 = 60,
                PlacementBoundary1 = Position(40, 0, 0),
                PlacementBoundary2 = Position(60, 15, 0),
            },
            virtualIo,
            placementHandler,
            supplyMotion,
            placementMotion,
            supplySettings.BufferHandoffPosition,
            placementSettings.BufferHandoffPosition,
            () => placementSettings.BufferEntryZ);
        var supplyHandler = new PcbSupplyHandler(
            supplyMotion,
            virtualIo,
            supplySettings);
        var supply = new PcbSupplyProcess(supplyHandler, buffer);
        var work = new PcbPlacementWork(virtualIo);
        var placement = new PcbPlacementProcess(
            buffer,
            placementHandler,
            work);
        var supplyVisitedPcb1 = false;
        var supplyVisitedPcb2 = false;
        var supplyMovedXAndYTogether = false;
        var supplyMovedXAtWrongBufferY = false;
        var supplyRotatedAwayFromHandoff = false;
        var lastSupplyPosition = supplyMotion.GetPosition();
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            var movedX = Math.Abs(x - lastSupplyPosition.X) > 0.001;
            var movedY = Math.Abs(y - lastSupplyPosition.Y) > 0.001;
            supplyMovedXAndYTogether |= movedX && movedY;
            if (movedX && supplyHandler.Pcb == PcbSupplyPcbState.Secured)
            {
                supplyMovedXAtWrongBufferY |=
                    Math.Abs(y - supplySettings.BufferHandoffPosition.Y)
                    > 0.05;
            }

            supplyVisitedPcb1 |= IsAt(
                x,
                y,
                z,
                supplyRecipe.Pcb1PickPosition.X,
                supplySettings.CarrierY,
                supplyRecipe.Pcb1PickPosition.Z);
            supplyVisitedPcb2 |= IsAt(
                x,
                y,
                z,
                supplyRecipe.Pcb2PickPosition.X,
                supplySettings.CarrierY,
                supplyRecipe.Pcb2PickPosition.Z);
            lastSupplyPosition = (x, y, z);
        };
        virtualIo.InputChanged += (input, value) =>
        {
            if (input == InputIo.PcbSupplyRotated && value)
            {
                supplyRotatedAwayFromHandoff |=
                    !supplyHandler.AtHandoffXY
                    || Math.Abs(
                        supplyMotion.GetPosition().Z
                        - supplySettings.RotationZ) > 0.05;
            }
        };

        virtualIo.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await HomeAsync(supplyMotion);
        await HomeAsync(placementMotion);
        var placementCount = 0;
        virtualIo.InputChanged += (input, value) =>
        {
            if (input == InputIo.PcbPlacementPcbDetected
                && !value
                && (placementHandler.IsAtXY(
                        placementRecipe.HeatSink1PcbPlacementPosition)
                    || placementHandler.IsAtXY(
                        placementRecipe.HeatSink2PcbPlacementPosition)))
            {
                placementCount++;
            }
        };
        await ((IIoService)virtualIo).SetOutputAndWaitAsync(
            OutputIo.PcbPlacementBackupPlateUp,
            true);
        virtualIo.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        virtualIo.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
        virtualIo.SetInput(InputIo.PcbPlacementCarrierPresent, true);

        using var supplyCancellation = new CancellationTokenSource();
        var supplyRun = supply.RunAsync(
            supplyRecipe,
            supplyCancellation.Token);
        var checkpoints = new (PcbPlacementState State, Func<bool> Stable)[]
        {
            (
                PcbPlacementState.LoweringIpm,
                () => placementHandler.AtBufferXY
                      && placementHandler.Ipm == PlacementCylinderState.Up),
            (
                PcbPlacementState.LoweringHandler,
                () => placementHandler.AtBufferXY
                      && placementHandler.AtBufferZ
                      && placementHandler.Handler
                      == PlacementCylinderState.Up),
            (
                PcbPlacementState.ApplyingVacuum,
                () => placementHandler.AtBufferXY
                      && placementHandler.Handler
                      == PlacementCylinderState.Down
                      && placementHandler.Pcb == PlacementPcbState.Detected),
            (
                PcbPlacementState.ClosingGripper,
                () => placementHandler.AtBufferXY
                      && placementHandler.VacuumDetected
                      && placementHandler.Gripper
                      == PlacementGripperState.Open),
            (
                PcbPlacementState.WaitingForSupplyExit,
                () => placementHandler.Pcb == PlacementPcbState.Secured
                      && buffer.SupplyInside),
            (
                PcbPlacementState.ReleasingVacuum,
                () => placementHandler.Handler
                      == PlacementCylinderState.Down
                      && placementHandler.VacuumDetected),
            (
                PcbPlacementState.OpeningGripper,
                () => placementHandler.Handler
                      == PlacementCylinderState.Down
                      && !placementHandler.VacuumDetected
                      && placementHandler.Gripper
                      == PlacementGripperState.Closed),
            (
                PcbPlacementState.PressingPcb,
                () => placementHandler.Handler
                      == PlacementCylinderState.Down
                      && placementHandler.Pcb == PlacementPcbState.Detected
                      && placementHandler.Gripper
                      == PlacementGripperState.Open
                      && placementHandler.Ipm == PlacementCylinderState.Up),
            (
                PcbPlacementState.RaisingIpm,
                () => work.Assemblies.Any(
                          assembly => assembly.HeatSink == HeatSinkSlot.HeatSink1)
                      && placementHandler.Ipm
                      == PlacementCylinderState.Down),
            (
                PcbPlacementState.RaisingHandler,
                () => work.Assemblies.Any(
                          assembly => assembly.HeatSink == HeatSinkSlot.HeatSink1)
                      && placementHandler.Ipm == PlacementCylinderState.Up
                      && placementHandler.Handler
                      == PlacementCylinderState.Down),
            (
                PcbPlacementState.RaisingZ,
                () => work.Assemblies.Any(
                          assembly => assembly.HeatSink == HeatSinkSlot.HeatSink1)
                      && placementHandler.Handler == PlacementCylinderState.Up
                      && !placementHandler.AtHorizontalZ),
        };
        foreach (var checkpoint in checkpoints)
        {
            using var stopped = new CancellationTokenSource();
            var stoppedRun = placement.RunAsync(
                placementRecipe,
                stopped.Token);
            var stoppedAtState = await WaitUntilAsync(
                () => placement.State(placementRecipe) == checkpoint.State
                      && checkpoint.Stable(),
                TimeSpan.FromSeconds(15));
            stopped.Cancel();
            await stoppedRun;

            Assert.True(
                stoppedAtState,
                $"Stable state not reached: {checkpoint.State}");
            Assert.False(placementMotion.IsMoving);
            Assert.False(buffer.Conflict);
        }

        using var resumed = new CancellationTokenSource();
        var placementRun = placement.RunAsync(
            placementRecipe,
            resumed.Token);
        var completed = await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(15));

        resumed.Cancel();
        supplyCancellation.Cancel();
        await Task.WhenAll(placementRun, supplyRun);

        Assert.True(completed);
        Assert.Equal(2, placementCount);
        Assert.Equal(2, work.Assemblies.Count);
        Assert.True(supplyVisitedPcb1);
        Assert.True(supplyVisitedPcb2);
        Assert.False(supplyMovedXAndYTogether);
        Assert.False(supplyMovedXAtWrongBufferY);
        Assert.False(supplyRotatedAwayFromHandoff);
        Assert.False(buffer.Conflict);
        Assert.False(supplyMotion.IsMoving);
        Assert.False(placementMotion.IsMoving);
    }

    private static MotionSettings MotionSettings() => new()
    {
        HorizontalSpeed = 2_000,
        ZSpeed = 2_000,
    };

    private static VirtualMotionService Motion(
        MotionSettings settings,
        OperationCancellation operations) =>
        new(
            settings,
            xRange: (0, 100),
            yRange: (0, 100),
            zRange: (0, 100),
            horizontalZ: () => 0,
            operationCancellation: operations);

    private static AxisPos Position(double x, double y, double z) =>
        new() { X = x, Y = y, Z = z };

    private static bool IsAt(
        double x,
        double y,
        double z,
        double targetX,
        double targetY,
        double targetZ) =>
        Math.Abs(x - targetX) <= 0.05
        && Math.Abs(y - targetY) <= 0.05
        && Math.Abs(z - targetZ) <= 0.05;

    private static IReadOnlyDictionary<OutputIo, OutputHardware> Outputs(
        params IoHardwareSettings[] settings) =>
        settings
            .SelectMany(section => section.Outputs)
            .ToDictionary();

    private static async Task HomeAsync(VirtualMotionService motion)
    {
        await motion.HomeAsync(MotionAxis.Z, 2_000);
        await motion.HomeAsync(MotionAxis.X, 2_000);
        await motion.HomeAsync(MotionAxis.Y, 2_000);
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - started >= timeout)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }
}
