using System;
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
    public async Task PlacementResumesWhileHoldingPcbAndFillsDetectedHeatSink()
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
        var supply = new PcbSupplyProcess(
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
        var placement = new PcbPlacementProcess(
            buffer,
            placementHandler,
            work);

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(
            HomeAsync(supplyMotion),
            HomeAsync(placementMotion));
        await ((IIoService)io).SetOutputAndWaitAsync(
            OutputIo.PcbPlacementBackupPlateUp,
            true);
        io.SetInput(InputIo.PcbPlacementHeatSink1Present, false);
        io.SetInput(InputIo.PcbPlacementHeatSink2Present, true);
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
        firstStop.Cancel();
        await firstRun;

        Assert.False(work.Completed);
        Assert.Equal(PlacementPcbState.Secured, placementHandler.Pcb);

        using var resumed = new CancellationTokenSource();
        var resumedRun = placement.RunAsync(
            placementRecipe,
            resumed.Token);
        var completed = await WaitUntilAsync(
            () => work.Completed,
            TimeSpan.FromSeconds(10));

        resumed.Cancel();
        supplyCancellation.Cancel();
        await Task.WhenAll(resumedRun, supplyRun);

        Assert.True(completed);
        var assembly = Assert.Single(work.Assemblies);
        Assert.Equal(HeatSinkSlot.HeatSink2, assembly.HeatSink);
        Assert.False(buffer.Conflict);
        Assert.False(supplyMotion.IsMoving);
        Assert.False(placementMotion.IsMoving);
    }

    [Fact]
    public async Task SupplyChecksBothMissingPcbSlotsInOneSmemaCycle()
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
        var supply = new PcbSupplyProcess(
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
        };

        io.Initialize();
        supplyMotion.Initialize();
        placementMotion.Initialize();
        await Task.WhenAll(
            HomeAsync(supplyMotion),
            HomeAsync(placementMotion));
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
        cancellation.Cancel();
        await run;

        Assert.True(checkedBoth);
        Assert.False(io.GetInput(InputIo.PcbBufferPcbPresent));
        Assert.False(io.GetOutput(OutputIo.PcbSupplyReadyToFront1));
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

    private static async Task HomeAsync(VirtualMotionService motion)
    {
        await motion.HomeAsync(MotionAxis.Z, 2_000);
        await motion.HomeAsync(MotionAxis.X, 2_000);
        await motion.HomeAsync(MotionAxis.Y, 2_000);
    }

}
