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
    public void PlacementRecoveryChangesOnlyTheDisplayedSelections()
    {
        var io = new VirtualIoService(
            Outputs(new ConveyorHardwareSettings()), new MachineOptions());
        var work = new PcbPlacementWork(ConveyorStation.PcbPlacement(io));
        io.Initialize();
        io.SetInput(InputIo.PcbPlacementCarrierPresent, true);
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        var first = work.Assembly(HeatSinkSlot.HeatSink1);
        var hidden = work.Assembly(HeatSinkSlot.HeatSink2);
        work.Complete();
        var displayed = Enum.GetValues<HeatSinkSlot>()
            .Where(work.HeatSinkPresent)
            .Select(heatSink => (HeatSink: heatSink, Completed: true))
            .ToArray();
        Assert.Equal(HeatSinkSlot.HeatSink1, Assert.Single(displayed).HeatSink);

        io.SetInput(InputIo.PcbPlacementHeatSink1Present, false);
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
        work.PrepareRecovery(displayed);

        Assert.False(work.Completed);
        Assert.Same(first, work.Assemblies.Single(item => item.HeatSink == HeatSinkSlot.HeatSink1));
        Assert.Same(hidden, work.Assemblies.Single(item => item.HeatSink == HeatSinkSlot.HeatSink2));

        work.PrepareRecovery([(HeatSinkSlot.HeatSink1, false)]);

        Assert.Same(hidden, Assert.Single(work.Assemblies));
    }

    [Fact]
    public async Task PlacementKeepsStartedTargetsAndReselectsAfterStop()
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            RotationZ = 0,
            CarrierY = 30,
            BufferHandoffPosition = Position(50, 10, 8),
            BufferClearZ = 12,
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
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
        var io = new VirtualIoService(
            Outputs(
                new PcbSupplyHardwareSettings(),
                new PcbPlacementHandlerHardwareSettings(),
                new ConveyorHardwareSettings()),
            new MachineOptions());
        var machine = new VirtualMachine(io, []);
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
        var buffer = Buffer(
            io,
            placementHandler,
            supplyMotion,
            placementMotion,
            supplySettings,
            placementSettings);
        var supply = new PcbSupplier(
            new PcbSupplyHandler(
                supplyMotion,
                io,
                supplySettings,
                new PcbBufferSettings
                {
                    SupplyBoundary1 = 40,
                    SupplyBoundary2 = 60,
                }),
            buffer);
        var work = new PcbPlacementWork(
            ConveyorStation.PcbPlacement(io));
        var placement = new PcbPlacer(
            buffer,
            placementHandler,
            work);
        var bufferEntries = 0;
        var enteredBufferPrepared = true;
        var wasInsideBuffer = false;
        var heatSinkChangedDuringMove = false;
        placementMotion.PositionChanged += (x, y, _) =>
        {
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
            else if (output == OutputIo.PcbPlacementIpmGripperClose
                     && placementPhase == 2
                     && value)
            {
                placementPhase = 3;
            }
            else if (output == OutputIo.PcbPlacementIpmDown
                     && placementPhase == 3
                     && value)
            {
                placementPhase = 4;
            }
        };

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(
            HomeAsync(supplyMotion, 2_000),
            HomeAsync(placementMotion, 2_000));
        await ((IIoService)io).SetOutputAndWaitAsync(
            OutputIo.PcbPlacementBackupPlateUp,
            true);
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, true);
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, false);
        io.SetInput(InputIo.PcbPlacementCarrierPresent, true);

        using var supplyCancellation = new CancellationTokenSource();
        var supplyRun = supply.RunAsync(
            supplyRecipe,
            supplyCancellation.Token);
        using var firstStop = new CancellationTokenSource();
        var firstRun = placement.RunAsync(
            placementRecipe,
            firstStop.Token);
        Assert.True(await WaitUntilAsync(
            () => placementHandler.Pcb == PlacementPcbState.Secured,
            TimeSpan.FromSeconds(10)));
        Assert.Equal(HeatSinkSlot.HeatSink1, placement.TargetHeatSink);
        firstStop.Cancel();
        await firstRun;

        Assert.False(work.Completed);
        Assert.Empty(work.Assemblies);
        Assert.Equal(PlacementPcbState.Secured, placementHandler.Pcb);

        io.SetInput(InputIo.PcbPlacementHeatSink1Present, false);
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
        using var resumed = new CancellationTokenSource();
        var resumedRun = placement.RunAsync(
            placementRecipe,
            resumed.Token);
        Assert.Equal(HeatSinkSlot.HeatSink2, placement.TargetHeatSink);
        var completed = await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(10));
        var prefetched = completed && await WaitUntilAsync(
            () => placementHandler.Pcb == PlacementPcbState.Secured
                  && placement.State(placementRecipe)
                      == PcbPlacementState.WaitingForCarrier,
            TimeSpan.FromSeconds(10));

        resumed.Cancel();
        supplyCancellation.Cancel();
        await Task.WhenAll(resumedRun, supplyRun);

        Assert.True(completed);
        Assert.True(prefetched);
        Assert.True(heatSinkChangedDuringMove);
        Assert.True(io.GetInput(InputIo.PcbPlacementHeatSink1Present));
        Assert.False(io.GetInput(InputIo.PcbPlacementHeatSink2Present));
        Assert.True(bufferEntries >= 2);
        Assert.True(enteredBufferPrepared);
        var assembly = Assert.Single(work.Assemblies);
        Assert.Equal(HeatSinkSlot.HeatSink2, assembly.HeatSink);
        Assert.Equal(PlacementCylinderState.Up, placementHandler.IpmLift);
        Assert.Equal(PlacementCylinderState.Up, placementHandler.Lift);
        Assert.True(placementHandler.AtHorizontalZ);
        Assert.Equal(4, placementPhase);
        Assert.False(buffer.Conflict);
        Assert.False(supplyMotion.IsMoving);
        Assert.False(placementMotion.IsMoving);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplyReleasesUpstreamAfterCheckingBothSlots(
        bool secondPcbPresent)
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = FastMotion(),
            CarrierY = 30,
            BufferHandoffPosition = Position(50, 10, 8),
            BufferClearZ = 12,
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
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
        var buffer = Buffer(
            io,
            placementHandler,
            supplyMotion,
            placementMotion,
            supplySettings,
            placementSettings);
        var supply = new PcbSupplier(
            new PcbSupplyHandler(
                supplyMotion,
                io,
                supplySettings,
                new PcbBufferSettings
                {
                    SupplyBoundary1 = 40,
                    SupplyBoundary2 = 60,
                }),
            buffer);
        var pcb1Visited = false;
        var pcb2Visited = false;
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
            if (pcb2Visited && secondPcbPresent)
            {
                io.SetInput(InputIo.PcbSupplyPcbDetected, true);
            }
        };

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(
            HomeAsync(supplyMotion, 2_000),
            HomeAsync(placementMotion, 2_000));
        io.SetInput(InputIo.PcbBufferPcbPresent, secondPcbPresent);
        using var cancellation = new CancellationTokenSource();
        var run = supply.RunAsync(recipe, cancellation.Token);
        await WaitForOutputAsync(
            io,
            OutputIo.PcbSupplyReadyToFront1,
            true);
        io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);

        var checkedBoth = await WaitUntilAsync(
            () => pcb1Visited
                  && pcb2Visited
                  && io.GetOutput(OutputIo.PcbSupplyReadyToFront1),
            TimeSpan.FromSeconds(5));
        var atRotationZ = supplyMotion.IsAtHorizontalZ;
        var nextCarrierAccepted = true;
        if (secondPcbPresent && checkedBoth)
        {
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, false);
            io.SetInput(InputIo.PcbSupplyAvailableFromFront1, true);
            nextCarrierAccepted = await WaitUntilAsync(
                () => !io.GetOutput(OutputIo.PcbSupplyReadyToFront1),
                TimeSpan.FromSeconds(2));
        }

        cancellation.Cancel();
        await run;

        Assert.True(checkedBoth);
        Assert.True(atRotationZ);
        Assert.True(nextCarrierAccepted);
        Assert.Equal(secondPcbPresent,
            io.GetInput(InputIo.PcbSupplyPcbDetected));
        Assert.Equal(secondPcbPresent,
            io.GetInput(InputIo.PcbBufferPcbPresent));
        Assert.False(io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupplyResumesInterruptedBufferEntry(bool descending)
    {
        var operations = new OperationCancellation();
        var supplySettings = new PcbSupplySettings
        {
            Motion = new() { HorizontalSpeed = 100, ZSpeed = 20 },
            BufferHandoffPosition = Position(50, 10, 8),
            BufferClearZ = 12,
        };
        var placementSettings = new PcbPlacementHandlerSettings
        {
            Motion = FastMotion(),
            BufferHandoffPosition = Position(50, 10, 8),
        };
        var io = new VirtualIoService(
            Outputs(
                new PcbSupplyHardwareSettings(),
                new PcbPlacementHandlerHardwareSettings()),
            new MachineOptions());
        using var supplyMotion = Motion(supplySettings.Motion, operations);
        using var placementMotion = Motion(placementSettings.Motion, operations);
        var placementHandler = new PcbPlacementHandler(
            placementMotion, io, placementSettings);
        var buffer = Buffer(
            io, placementHandler, supplyMotion, placementMotion,
            supplySettings, placementSettings);
        var supplyHandler = new PcbSupplyHandler(supplyMotion, io, supplySettings,
            new PcbBufferSettings
            {
                SupplyBoundary1 = 40,
                SupplyBoundary2 = 60,
            });
        var supply = new PcbSupplier(supplyHandler, buffer);

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(HomeAsync(supplyMotion, 2_000), HomeAsync(placementMotion, 2_000));
        io.SetInput(InputIo.PcbSupplyPcbDetected, true);
        await ((IIoService)io).SetOutputAndWaitAsync(
            OutputIo.PcbSupplyNestForward, true);
        await ((IIoService)io).SetOutputAndWaitAsync(
            OutputIo.PcbSupplyIpmFixerForward, true);

        using var firstStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var interrupted = false;
        var enteredUnrotated = false;
        supplyMotion.PositionChanged += (x, y, z) =>
        {
            enteredUnrotated |= x >= 40
                && !io.GetInput(InputIo.PcbSupplyRotated);
            if (IsAt(x, y, z, 50, 10, 8))
            {
                io.SetInput(InputIo.PcbBufferPcbPresent, true);
            }

            if (!interrupted && (descending ? z >= 4 : x >= 45))
            {
                interrupted = true;
                if (descending)
                {
                    io.SetInput(InputIo.PcbBufferPcbPresent, true);
                }
                firstStop.Cancel();
            }
        };
        await supply.RunAsync(new PcbSupplyRecipe(), firstStop.Token);
        Assert.True(interrupted);
        Assert.True(buffer.SupplyInside);
        Assert.False(buffer.SupplyAtHandoff);
        Assert.Equal(descending, buffer.PcbPresent);
        var stoppedPosition = supplyMotion.GetPosition();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            supplyHandler.SetRotatedAsync(false));
        Assert.Equal(stoppedPosition, supplyMotion.GetPosition());
        Assert.True(io.GetOutput(OutputIo.PcbSupplyRotate));

        using var resumed = new CancellationTokenSource();
        var run = supply.RunAsync(new PcbSupplyRecipe(), resumed.Token);
        var reachedHandoff = await WaitUntilAsync(
            () => buffer.SupplyAtHandoff,
            TimeSpan.FromSeconds(5));
        resumed.Cancel();
        await run;

        Assert.True(reachedHandoff);
        Assert.False(enteredUnrotated);
        Assert.False(buffer.Conflict);
        Assert.Equal((50, 10, 8), supplyMotion.GetPosition());
    }

    private static BufferStage Buffer(
        IIoService io,
        PcbPlacementHandler placementHandler,
        IAxisMotion supplyMotion,
        IXyMotion placementMotion,
        PcbSupplySettings supplySettings,
        PcbPlacementHandlerSettings placementSettings) => new(
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

    private static MotionSettings FastMotion() => new()
    {
        HorizontalSpeed = 2_000,
        ZSpeed = 2_000,
    };

    private static AxisPosition Position(double x, double y, double z) =>
        new() { X = x, Y = y, Z = z };

    private static bool IsAt(
        double x,
        double y,
        double z,
        double targetX,
        double targetY,
        double targetZ) =>
        Math.Abs(x - targetX) <= MotionService.PositionToleranceMillimeters
        && Math.Abs(y - targetY) <= MotionService.PositionToleranceMillimeters
        && Math.Abs(z - targetZ) <= MotionService.PositionToleranceMillimeters;

}
